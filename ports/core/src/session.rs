use crate::{
    frame::{self, Decoder},
    tls,
    wire::{self, Framer, Invitation, PORTABLE_IMAGES, Packet, Quality, Reader},
};
use anyhow::{Context, Result, bail, ensure};
use image::RgbaImage;
use rustls::{ClientConnection, Connection, ServerConnection, pki_types::ServerName};
use std::{
    io::{self, Read, Write},
    net::{Shutdown, TcpListener, TcpStream, ToSocketAddrs},
    sync::{
        Arc, Mutex,
        atomic::{AtomicBool, Ordering},
        mpsc::{self, Receiver, SyncSender},
    },
    thread,
    time::{Duration, Instant},
};

pub enum Stream {
    Tcp(TcpStream),
    Peer(crate::peer::Peer),
}
impl Read for Stream {
    fn read(&mut self, out: &mut [u8]) -> io::Result<usize> {
        match self {
            Self::Tcp(s) => s.read(out),
            Self::Peer(s) => s.read(out),
        }
    }
}
impl Write for Stream {
    fn write(&mut self, bytes: &[u8]) -> io::Result<usize> {
        match self {
            Self::Tcp(s) => s.write(bytes),
            Self::Peer(s) => s.write(bytes),
        }
    }
    fn flush(&mut self) -> io::Result<()> {
        match self {
            Self::Tcp(s) => s.flush(),
            Self::Peer(s) => s.flush(),
        }
    }
}
impl Stream {
    fn blocking(&self, value: bool) -> io::Result<()> {
        match self {
            Self::Tcp(s) => s.set_nonblocking(!value),
            Self::Peer(s) => {
                s.set_nonblocking(!value);
                Ok(())
            }
        }
    }
    fn prepare(&self) -> io::Result<()> {
        self.blocking(true)?;
        if let Self::Tcp(s) = self {
            s.set_nodelay(true)?;
            s.set_read_timeout(Some(Duration::from_secs(12)))?;
            s.set_write_timeout(Some(Duration::from_secs(12)))?;
        }
        Ok(())
    }
    fn close(&self) {
        match self {
            Self::Tcp(s) => {
                let _ = s.shutdown(Shutdown::Both);
            }
            Self::Peer(s) => s.close(),
        }
    }
}
pub struct Network {
    pub tls: Connection,
    pub socket: Stream,
    framer: Framer,
    last: Instant,
    queued: usize,
}
impl Network {
    fn new(mut tls: Connection, mut socket: Stream, stop: &AtomicBool) -> Result<Self> {
        // Windows accepted sockets inherit the listener's nonblocking mode.
        socket.prepare()?;
        socket.blocking(false)?;
        tls.set_buffer_limit(None);
        let started = Instant::now();
        while tls.is_handshaking() {
            ensure!(!stop.load(Ordering::Acquire), "Connection cancelled");
            ensure!(
                started.elapsed() < Duration::from_secs(12),
                "TLS handshake timed out"
            );
            match tls.complete_io(&mut socket) {
                Ok(_) => {}
                Err(e) if e.kind() == io::ErrorKind::WouldBlock => {
                    thread::sleep(Duration::from_millis(2))
                }
                Err(e) => return Err(e.into()),
            }
        }
        socket.blocking(false)?;
        Ok(Self {
            tls,
            socket,
            framer: Framer::default(),
            last: Instant::now(),
            queued: 0,
        })
    }
    pub fn send(&mut self, p: Packet) -> Result<()> {
        let bytes = p.framed()?;
        ensure!(
            self.queued + bytes.len() <= wire::MAX_PACKET + 1048576,
            "Send buffer exceeded"
        );
        self.tls.writer().write_all(&bytes)?;
        self.queued += bytes.len();
        Ok(())
    }
    pub fn tick(&mut self) -> Result<Vec<Packet>> {
        while self.tls.wants_write() {
            match self.tls.write_tls(&mut self.socket) {
                Ok(0) => {
                    return Err(
                        io::Error::new(io::ErrorKind::UnexpectedEof, "Connection closed").into(),
                    );
                }
                Ok(_) => {}
                Err(e) if e.kind() == io::ErrorKind::WouldBlock => break,
                Err(e) => return Err(e.into()),
            }
        }
        if !self.tls.wants_write() {
            self.queued = 0;
        }
        match self.tls.read_tls(&mut self.socket) {
            Ok(0) => {
                return Err(
                    io::Error::new(io::ErrorKind::UnexpectedEof, "Connection closed").into(),
                );
            }
            Ok(_) => {
                self.tls.process_new_packets()?;
            }
            Err(e) if e.kind() == io::ErrorKind::WouldBlock => {}
            Err(e) => return Err(e.into()),
        }
        let mut buffer = [0u8; 65536];
        let mut packets = Vec::new();
        loop {
            match self.tls.reader().read(&mut buffer) {
                Ok(0) => break,
                Ok(n) => {
                    self.framer.push(&buffer[..n])?;
                    while let Some(p) = self.framer.next_packet()? {
                        self.last = Instant::now();
                        packets.push(p);
                        ensure!(packets.len() <= 128, "Too many queued messages");
                    }
                }
                Err(e) if e.kind() == io::ErrorKind::WouldBlock => break,
                Err(e) => return Err(e.into()),
            }
        }
        Ok(packets)
    }
    fn pending(&self) -> bool {
        self.tls.wants_write()
    }
}
impl Drop for Network {
    fn drop(&mut self) {
        self.socket.close();
    }
}

#[derive(Default)]
pub struct ViewState {
    frame_wakeup: Option<SyncSender<()>>,
    pub(crate) record_wakeup: Option<SyncSender<()>>,
    pub media: Arc<Mutex<crate::media::MediaState>>,
    pub connected: bool,
    pub reconnecting: bool,
    pub reconnect_attempts: u32,
    pub status: String,
    pub peer: String,
    pub control: bool,
    pub input_suspended: bool,
    pub monitors: Vec<crate::monitors::Monitor>,
    pub monitor_pending: bool,
    pub monitor_status: String,
    pub capabilities: u64,
    pub width: u32,
    pub height: u32,
    pub refresh: u32,
    pub epoch: i32,
    pub sequence: i32,
    pub frame: Option<Arc<RgbaImage>>,
    pub clipboard: Option<String>,
    pub chats: Vec<String>,
    pub frames: u64,
    pub reply: Option<String>,
    pub paired: Option<crate::paired::SavedComputer>,
    pub files_allowed: bool,
    pub files: Arc<Mutex<crate::files::FileState>>,
}
pub enum Command {
    Annotation(i32, Vec<[i32; 2]>),
    Audio(bool),
    Voice(bool),
    Quality(Quality),
    Input(u8, i32, i32, i32),
    Release,
    Clipboard(String),
    ReadClipboard,
    ListMonitors,
    SelectMonitor(String),
    Chat(String),
    Files(crate::files::FileCommand),
    Disconnect,
}
pub struct Viewer {
    pub recording: crate::recording::Control,
    pub state: Arc<Mutex<ViewState>>,
    commands: SyncSender<Command>,
    stop: Arc<AtomicBool>,
    worker: Option<thread::JoinHandle<()>>,
    presentation: Option<thread::JoinHandle<()>>,
}
#[derive(Clone)]
enum Endpoint {
    Direct(Invitation),
    Peer(crate::signal::Offer, std::path::PathBuf),
    Saved(crate::paired::SavedComputer, std::path::PathBuf),
    Pair(crate::paired::PairingCode),
    #[cfg(test)]
    TestSaved(Arc<Mutex<Invitation>>),
}
impl Viewer {
    /// Coalesce presentation notifications on a separate worker. UI scheduling
    /// cannot block network liveness/ACKs. Callbacks must finish and not retain state.
    pub fn set_frame_wakeup(&mut self, wakeup: impl Fn() + Send + 'static) {
        self.state.lock().unwrap().frame_wakeup = None;
        if let Some(worker) = self.presentation.take() {
            let _ = worker.join();
        }
        let (sender, receiver) = mpsc::sync_channel(1);
        self.state.lock().unwrap().frame_wakeup = Some(sender);
        self.presentation = Some(thread::spawn(move || {
            while receiver.recv().is_ok() {
                wakeup();
            }
        }));
    }
    pub fn open(text: &str, library: &std::path::Path, quality: Quality) -> Result<Self> {
        if text.starts_with("lume-pair://") {
            Ok(Self::start(
                Endpoint::Pair(crate::paired::PairingCode::parse(text)?),
                quality,
            ))
        } else if text.starts_with("lume-saved://") {
            Ok(Self::start(
                Endpoint::Saved(crate::paired::SavedComputer::parse(text)?, library.into()),
                quality,
            ))
        } else if text.starts_with("lume-p2p://") {
            Ok(Self::connect_peer(
                crate::signal::Offer::parse(text)?,
                library.into(),
                quality,
            ))
        } else {
            Ok(Self::connect(Invitation::parse(text)?, quality))
        }
    }
    pub fn connect(invite: Invitation, quality: Quality) -> Self {
        Self::start(Endpoint::Direct(invite), quality)
    }
    pub fn connect_peer(
        offer: crate::signal::Offer,
        library: std::path::PathBuf,
        quality: Quality,
    ) -> Self {
        Self::start(Endpoint::Peer(offer, library), quality)
    }
    fn start(endpoint: Endpoint, quality: Quality) -> Self {
        let state = Arc::new(Mutex::new(ViewState {
            status: "Connecting…".into(),
            ..Default::default()
        }));
        let stop = Arc::new(AtomicBool::new(false));
        let (tx, rx) = mpsc::sync_channel(128);
        let mut result = Self {
            recording: Default::default(),
            state: state.clone(),
            commands: tx,
            stop: stop.clone(),
            worker: None,
            presentation: None,
        };
        result.worker = Some(thread::spawn(move || {
            let done = viewer_run(endpoint, quality, &state, &stop, rx);
            let mut s = state.lock().unwrap();
            s.connected = false;
            s.reconnecting = false;
            s.control = false;
            s.clipboard = None;
            s.reply = None;
            s.status = match done {
                Ok(()) if s.paired.is_some() => "Paired. Save this computer to reconnect.".into(),
                Ok(()) => "Disconnected".into(),
                Err(e) => format!("Connection ended: {e}"),
            };
        }));
        result
    }
    pub fn command(&self, command: Command) -> Result<()> {
        if matches!(command, Command::Disconnect) {
            self.disconnect();
            return Ok(());
        }
        match &command {
            Command::Annotation(epoch, points) => {
                crate::annotations::validate(*epoch, points)?;
                let s = self.state.lock().unwrap();
                ensure!(
                    s.connected
                        && s.control
                        && s.capabilities & crate::annotations::CAPABILITY != 0
                        && s.frame.is_some()
                        && !s.input_suspended
                        && s.epoch == *epoch,
                    "Annotations require the current display and host permission"
                );
            }
            Command::Audio(_) | Command::Voice(_) => {
                let s = self.state.lock().unwrap();
                let cap = if matches!(&command, Command::Audio(_)) {
                    crate::media::AUDIO
                } else {
                    crate::media::VOICE
                };
                ensure!(
                    s.connected && s.capabilities & cap != 0,
                    "This host has not enabled that media feature"
                );
            }
            Command::Quality(q) => q.validate()?,
            Command::Input(action, a, b, _) => {
                validate_input(*action, *a, *b)?;
                let s = self.state.lock().unwrap();
                ensure!(
                    s.connected && s.control && s.frame.is_some() && !s.input_suspended,
                    "Wait for the current desktop before sending input"
                );
            }
            Command::Clipboard(text) => {
                ensure!(text.len() <= 262144, "Clipboard text exceeds 256 KiB");
                ensure!(
                    self.state.lock().unwrap().connected,
                    "Wait for reconnection before sending clipboard text"
                );
            }
            Command::Chat(text) => {
                ensure!(text.len() <= 8192, "Chat message exceeds 8 KiB");
                ensure!(
                    self.state.lock().unwrap().connected,
                    "Wait for reconnection before sending a message"
                );
            }
            Command::ReadClipboard => ensure!(
                self.state.lock().unwrap().connected,
                "Wait for reconnection before reading clipboard text"
            ),
            Command::ListMonitors | Command::SelectMonitor(_) => {
                let state = self.state.lock().unwrap();
                ensure!(
                    state.connected && state.capabilities & crate::monitors::CAPABILITY != 0,
                    "This host does not provide display selection"
                );
                ensure!(
                    !state.monitor_pending,
                    "Wait for the current display request"
                );
                if let Command::SelectMonitor(id) = &command {
                    ensure!(
                        !id.is_empty() && id.len() <= 256 && !id.chars().any(char::is_control),
                        "Invalid display identifier"
                    );
                    if let Some(monitor) = state.monitors.iter().find(|m| &m.id == id) {
                        frame::dimensions(monitor.width as i32, monitor.height as i32)?;
                    }
                }
            }
            Command::Files(action) => {
                action.validate()?;
                let state = self.state.lock().unwrap();
                ensure!(
                    state.connected && state.files_allowed,
                    "The host has not enabled file access for this session"
                );
                if matches!(
                    action,
                    crate::files::FileCommand::UploadFolder { .. }
                        | crate::files::FileCommand::DownloadFolder { .. }
                ) {
                    ensure!(
                        state.capabilities & 16 != 0,
                        "This host does not support folder transfers"
                    );
                }
            }
            _ => {}
        }
        self.commands
            .try_send(command)
            .map_err(|_| anyhow::anyhow!("Session action queue is busy"))
    }
    pub fn disconnect(&self) {
        self.stop.store(true, Ordering::Release);
    }
    pub fn is_finished(&self) -> bool {
        self.worker
            .as_ref()
            .is_none_or(|worker| worker.is_finished())
    }
    pub fn close_and_wait(&mut self) {
        self.disconnect();
        if let Some(worker) = self.worker.take() {
            let _ = worker.join();
        }
        self.state.lock().unwrap().frame_wakeup = None;
        if let Some(worker) = self.presentation.take() {
            let _ = worker.join();
        }
    }
}
impl Drop for Viewer {
    fn drop(&mut self) {
        self.close_and_wait();
    }
}
fn tcp(invite: &Invitation) -> Result<TcpStream> {
    let mut last = None;
    for address in (invite.host.as_str(), invite.port)
        .to_socket_addrs()?
        .take(8)
    {
        match TcpStream::connect_timeout(&address, Duration::from_secs(8)) {
            Ok(mut stream) => {
                if !invite.room.is_empty() {
                    stream.set_read_timeout(Some(Duration::from_secs(12)))?;
                    stream.write_all(format!("LUME1 V {}\n", invite.room).as_bytes())?;
                    let mut response = [0];
                    stream.read_exact(&mut response)?;
                    ensure!(response[0] == 1, "Relay has no available host");
                }
                return Ok(stream);
            }
            Err(e) => last = Some(e),
        }
    }
    Err(last
        .map(Into::into)
        .unwrap_or_else(|| anyhow::anyhow!("No usable host address")))
}
fn viewer_run(
    endpoint: Endpoint,
    mut quality: Quality,
    state: &Arc<Mutex<ViewState>>,
    stop: &AtomicBool,
    commands: Receiver<Command>,
) -> Result<()> {
    let recover = matches!(&endpoint, Endpoint::Saved(..));
    #[cfg(test)]
    let recover = recover || matches!(&endpoint, Endpoint::TestSaved(..));
    let mut attempt = 0u32;
    loop {
        let result = viewer_attempt(endpoint.clone(), &mut quality, state, stop, &commands);
        let connected = {
            let mut s = state.lock().unwrap();
            let connected = s.connected;
            s.connected = false;
            s.control = false;
            s.files_allowed = false;
            s.capabilities = 0;
            s.clipboard = None;
            s.reply = None;
            s.frame = None;
            s.input_suspended = false;
            s.monitors.clear();
            s.monitor_pending = false;
            s.monitor_status.clear();
            connected
        };
        if stop.load(Ordering::Acquire) {
            return Ok(());
        }
        let Err(error) = result else {
            return Ok(());
        };
        if !recover || !crate::recovery::retryable(&error) {
            return Err(error);
        }
        attempt = if connected {
            1
        } else {
            attempt.saturating_add(1)
        };
        let delay = crate::recovery::delay(attempt);
        {
            let mut s = state.lock().unwrap();
            s.reconnecting = true;
            s.reconnect_attempts = s.reconnect_attempts.saturating_add(1);
            s.status = format!(
                "Connection lost. Reconnecting in {} seconds…",
                delay.as_secs()
            );
        }
        // Never replay input, clipboard, chat or file actions on a fresh connection.
        let started = Instant::now();
        loop {
            while let Ok(command) = commands.try_recv() {
                if let Command::Quality(q) = command {
                    quality = q;
                }
            }
            if stop.load(Ordering::Acquire) {
                return Ok(());
            }
            if started.elapsed() >= delay {
                break;
            }
            thread::sleep(Duration::from_millis(20));
        }
    }
}
fn viewer_attempt(
    endpoint: Endpoint,
    quality: &mut Quality,
    state: &Arc<Mutex<ViewState>>,
    stop: &AtomicBool,
    commands: &Receiver<Command>,
) -> Result<()> {
    quality.validate()?;
    let resume_key = match &endpoint {
        Endpoint::Saved(saved, _) => Some(zeroize::Zeroizing::new(saved.key.clone())),
        _ => None,
    };
    let (invite, stream) = match endpoint {
        Endpoint::Pair(code) => {
            state.lock().unwrap().status = "Pairing with the sharing computer…".into();
            let paired = crate::paired::pair(code, stop)?;
            state.lock().unwrap().paired = Some(paired);
            return Ok(());
        }
        Endpoint::Saved(saved, library) => {
            state.lock().unwrap().status = "Finding your saved computer…".into();
            let (invite, peer) = crate::paired::connect(&saved, &library, stop)?;
            (invite, Stream::Peer(peer))
        }
        Endpoint::Direct(invite) => {
            let socket = tcp(&invite)?;
            (invite, Stream::Tcp(socket))
        }
        #[cfg(test)]
        Endpoint::TestSaved(current) => {
            let invite = current.lock().unwrap().clone();
            let socket = tcp(&invite)?;
            (invite, Stream::Tcp(socket))
        }
        Endpoint::Peer(offer, library) => {
            let peer = crate::peer::Peer::new(&library, true)?;
            let answer = peer.answer_cancellable(&offer.sdp, Some(stop))?;
            {
                let mut s = state.lock().unwrap();
                s.reply = Some(offer.reply(&answer)?);
                s.status = "Copy the reply to the sharing computer".into();
            }
            peer.ready(Some(stop))?;
            state.lock().unwrap().reply = None;
            (offer.invitation, Stream::Peer(peer))
        }
    };
    let client = ClientConnection::new(
        tls::client(invite.pin)?,
        ServerName::try_from("lume-remote")?,
    )?;
    let mut net = Network::new(Connection::Client(client), stream, stop)?;
    let cert = net
        .tls
        .peer_certificates()
        .and_then(|v| v.first())
        .context("No host certificate")?;
    let version = if cert
        .windows(b"Lume Remote Session v4".len())
        .any(|w| w == b"Lume Remote Session v4")
    {
        4
    } else if cert
        .windows(b"Lume Remote Session v3".len())
        .any(|w| w == b"Lume Remote Session v3")
    {
        3
    } else {
        2
    };
    net.send(
        Packet::new(1)
            .int(version)
            .text(&invite.secret)
            .text("Lume portable viewer"),
    )?;
    state.lock().unwrap().status = "Waiting for local host approval…".into();
    let mut accepted = false;
    let mut remote_epoch = 0;
    let mut portable = false;
    let mut can_control = false;
    let mut decoder = Decoder::default();
    let mut ping = Instant::now();
    let mut request = 1i64;
    let mut clipboard_request = 0;
    let mut clipboard_started = Instant::now();
    let mut enable_request = 0;
    let mut monitor_list_request = 0;
    let mut monitor_selection_request = 0;
    let mut monitor_started = Instant::now();
    let mut monitor_baseline_epoch = 0;
    let mut monitor_target_epoch = 0;
    let mut monitor_target = String::new();
    let mut files: Option<crate::files::FileClient> = None;
    let mut annotations = std::collections::HashMap::<i64, Instant>::new();
    let mut media = crate::media::ViewerMedia::new(state.lock().unwrap().media.clone());
    while !stop.load(Ordering::Acquire) {
        for p in net.tick()? {
            let mut r = Reader::new(&p.0[1..]);
            match p.0[0] {
                2 => {
                    ensure!(!accepted, "Duplicate acceptance");
                    can_control = r.boolean()?;
                    let peer = r.text(128)?;
                    let (w, h) = frame::dimensions(r.int()?, r.int()?)?;
                    let mut hz = 60;
                    if version >= 2 {
                        ensure!(r.int()? == version, "Protocol mismatch");
                        hz = r.int()?;
                        ensure!((1..=1000).contains(&hz), "Invalid refresh rate");
                    }
                    let files_allowed = version >= 3 && r.boolean()?;
                    let caps = if version >= 4 { r.ulong()? } else { 0 };
                    r.end()?;
                    accepted = true;
                    {
                        let mut s = state.lock().unwrap();
                        s.connected = true;
                        s.reconnecting = false;
                        s.peer = peer;
                        s.control = can_control;
                        s.input_suspended = false;
                        s.width = w;
                        s.height = h;
                        s.refresh = hz as u32;
                        s.capabilities = caps;
                        s.files_allowed = files_allowed;
                        if files_allowed {
                            files = Some(crate::files::FileClient::viewer_resuming(
                                s.files.clone(),
                                caps & 512 != 0,
                                caps & 16 != 0,
                                if caps & 256 != 0 {
                                    resume_key.as_deref().map(String::as_str)
                                } else {
                                    None
                                },
                            )?);
                        }
                        s.status = "Connected".into();
                    }
                    let mut initial = *quality;
                    initial.lossless = false;
                    net.send(initial.write(Packet::new(14), version))?;
                    if caps & PORTABLE_IMAGES != 0 {
                        enable_request = request;
                        request += 1;
                        net.send(Packet::new(18).long(enable_request).byte(10).byte(1))?;
                    }
                }
                3 => {
                    bail!("{}", r.text(2048)?)
                }
                4 => {
                    ensure!(accepted, "Frame before authorization");
                    if let Some(frame) = decoder
                        .apply(&p, version)
                        .map_err(crate::recovery::protocol)?
                    {
                        let mut s = state.lock().unwrap();
                        // A Windows capture worker can send the new display before
                        // its tool reply. Present it, but wait for both before input.
                        if monitor_selection_request != 0 && decoder.epoch <= monitor_baseline_epoch
                        {
                            net.send(Packet::new(5).int(decoder.sequence))?;
                            continue;
                        }
                        s.width = frame.width();
                        s.height = frame.height();
                        if remote_epoch != decoder.epoch {
                            s.epoch = s.epoch.checked_add(1).unwrap_or(1);
                            remote_epoch = decoder.epoch;
                        }
                        s.sequence = s.sequence.checked_add(1).unwrap_or(1);
                        s.frames += 1;
                        s.frame = Some(Arc::new(frame));
                        if let Some(wakeup) = &s.record_wakeup {
                            let _ = wakeup.try_send(());
                        }
                        if monitor_target_epoch == decoder.epoch && monitor_target_epoch > 0 {
                            s.input_suspended = false;
                            s.monitor_status.clear();
                        }
                    }
                    net.send(Packet::new(5).int(decoder.sequence))?;
                    let wakeup = state.lock().unwrap().frame_wakeup.clone();
                    if let Some(wakeup) = wakeup {
                        let _ = wakeup.try_send(());
                    }
                }
                9 => {
                    let nonce = r.long()?;
                    r.end()?;
                    net.send(Packet::new(10).long(nonce))?;
                }
                10 => {
                    r.long()?;
                    r.end()?;
                }
                11 => return Ok(()),
                12 => {
                    let text = r.text(4096)?;
                    r.end()?;
                    state.lock().unwrap().status = text;
                }
                13 => {
                    ensure!(r.remaining() <= 32, "Invalid cursor notice");
                }
                15 => {
                    let _ = Quality::read(&mut r, version)?;
                    frame::dimensions(r.int()?, r.int()?)?;
                    r.int()?;
                    r.end()?;
                }
                16 => {
                    ensure!(r.remaining() <= 4096, "Invalid metrics envelope");
                }
                17 => {
                    ensure!(accepted, "File packet before authorization");
                    files
                        .as_ref()
                        .context("File access was not negotiated")?
                        .handle(p)?;
                }
                18 => {
                    let id = r.long()?;
                    let tool = r.byte()?;
                    ensure!(id > 0, "Invalid request id");
                    let (ok, error) = if tool == 4 {
                        let text = r.text(8192)?;
                        r.end()?;
                        let mut s = state.lock().unwrap();
                        s.chats.push(text);
                        if s.chats.len() > 100 {
                            s.chats.remove(0);
                        }
                        (true, "")
                    } else {
                        (false, "This action is unavailable on this viewer")
                    };
                    net.send(
                        Packet::new(19)
                            .long(id)
                            .byte(ok as u8)
                            .text(error)
                            .int(1)
                            .byte(18),
                    )?;
                }
                19 => {
                    let id = r.long()?;
                    let ok = r.boolean()?;
                    let error = r.text(1024)?;
                    let n = r.int()?;
                    ensure!(
                        id > 0 && (1..=262200).contains(&n),
                        "Invalid reply size or identifier"
                    );
                    let body = r.take(n as usize)?;
                    r.end()?;
                    ensure!(body[0] == 18, "Invalid tool reply body");
                    if annotations.remove(&id).is_some() {
                        ensure!(body.len() == 1, "Unexpected annotation receipt");
                        if !ok {
                            state.lock().unwrap().status = error;
                        }
                    } else if media.reply(id, ok, &error, body)? {
                    } else if id == enable_request {
                        enable_request = 0;
                        ensure!(body.len() == 1, "Unexpected portable negotiation payload");
                        if ok {
                            portable = true;
                            net.send(quality.write(Packet::new(14), version))?;
                        } else {
                            state.lock().unwrap().status = error;
                        }
                    } else if id == monitor_list_request {
                        monitor_list_request = 0;
                        let mut s = state.lock().unwrap();
                        s.monitor_pending = false;
                        if ok {
                            s.monitors = crate::monitors::decode_list(body)
                                .map_err(crate::recovery::protocol)?;
                            s.monitor_status.clear();
                        } else {
                            s.monitor_status = error;
                        }
                    } else if id == monitor_selection_request {
                        monitor_selection_request = 0;
                        let mut s = state.lock().unwrap();
                        s.monitor_pending = false;
                        if ok {
                            let (epoch, width, height, hz) =
                                crate::monitors::decode_selection(body)
                                    .map_err(crate::recovery::protocol)?;
                            ensure!(
                                epoch > monitor_baseline_epoch,
                                "Display generation did not advance"
                            );
                            monitor_target_epoch = epoch;
                            s.refresh = hz;
                            if s.frame.is_none() {
                                s.width = width;
                                s.height = height;
                            }
                            for monitor in &mut s.monitors {
                                monitor.selected = monitor.id == monitor_target;
                            }
                            s.input_suspended = decoder.epoch != epoch || s.frame.is_none();
                            s.monitor_status = if s.input_suspended {
                                "Waiting for the selected display…".into()
                            } else {
                                String::new()
                            };
                        } else {
                            s.monitor_status =
                                format!("{error} Select a display again to resume input.");
                        }
                    } else if id == clipboard_request {
                        clipboard_request = 0;
                        if ok {
                            let mut b = Reader::new(body);
                            ensure!(b.byte()? == 18, "Invalid clipboard reply");
                            let text = b.text(262144)?;
                            b.end()?;
                            state.lock().unwrap().clipboard = Some(text);
                        } else {
                            state.lock().unwrap().status = error;
                        }
                    } else if !ok {
                        state.lock().unwrap().status = error;
                    }
                }
                20 | 21 => {
                    ensure!(
                        accepted
                            && state.lock().unwrap().capabilities
                                & if p.0[0] == 20 {
                                    crate::media::AUDIO
                                } else {
                                    crate::media::VOICE
                                }
                                != 0,
                        "Media was not negotiated"
                    );
                    media.receive(&p).map_err(crate::recovery::protocol)?;
                }
                22 => {
                    ensure!(
                        accepted && state.lock().unwrap().capabilities & crate::media::VOICE != 0,
                        "Voice was not negotiated"
                    );
                    let generation = r.int()?;
                    r.end()?;
                    media.ended(generation)?;
                }
                _ => bail!("Unexpected session message"),
            }
        }
        for _ in 0..32 {
            let Ok(command) = commands.try_recv() else {
                break;
            };
            if !accepted {
                if let Command::Quality(q) = command {
                    *quality = q;
                }
                continue;
            }
            match command {
                Command::Audio(enabled) | Command::Voice(enabled) => {
                    let kind = if matches!(command, Command::Audio(_)) {
                        20
                    } else {
                        21
                    };
                    match media.request(kind, enabled, request) {
                        Ok(packet) => {
                            request += 1;
                            net.send(packet)?;
                        }
                        Err(error) => media.state.lock().unwrap().status = error.to_string(),
                    }
                }
                Command::Disconnect => return Ok(()),
                Command::Release => net.send(Packet::new(7))?,
                Command::Quality(q) => {
                    q.validate()?;
                    *quality = q;
                    let mut active = q;
                    if !portable {
                        active.lossless = false;
                    }
                    net.send(active.write(Packet::new(14), version))?;
                }
                Command::Input(action, a, b, epoch) => {
                    let ready = {
                        let s = state.lock().unwrap();
                        !s.input_suspended && s.frame.is_some() && epoch == s.epoch
                    };
                    if can_control && ready {
                        validate_input(action, a, b)?;
                        let p = if version >= 4 {
                            Packet::new(6).int(decoder.epoch)
                        } else {
                            Packet::new(6)
                        };
                        net.send(p.byte(action).int(a).int(b))?;
                    }
                }
                Command::Clipboard(text) => {
                    ensure!(text.len() <= 262144, "Clipboard text exceeds 256 KiB");
                    if can_control {
                        net.send(Packet::new(8).text(&text))?;
                    }
                }
                Command::ReadClipboard => {
                    if can_control
                        && state.lock().unwrap().capabilities & 1 != 0
                        && clipboard_request == 0
                    {
                        clipboard_request = request;
                        clipboard_started = Instant::now();
                        request += 1;
                        net.send(Packet::new(18).long(clipboard_request).byte(1))?;
                    }
                }
                Command::ListMonitors | Command::SelectMonitor(_) => {
                    if monitor_list_request != 0 || monitor_selection_request != 0 {
                        continue;
                    }
                    monitor_started = Instant::now();
                    let mut s = state.lock().unwrap();
                    s.monitor_pending = true;
                    s.monitor_status.clear();
                    match command {
                        Command::ListMonitors => {
                            monitor_list_request = request;
                            net.send(Packet::new(18).long(request).byte(2))?;
                        }
                        Command::SelectMonitor(id) => {
                            monitor_baseline_epoch = decoder.epoch;
                            monitor_target_epoch = 0;
                            monitor_target = id;
                            monitor_selection_request = request;
                            s.input_suspended = true;
                            s.frame = None;
                            s.epoch = s.epoch.checked_add(1).unwrap_or(1);
                            net.send(Packet::new(7))?;
                            net.send(Packet::new(18).long(request).byte(3).text(&monitor_target))?;
                        }
                        _ => unreachable!(),
                    }
                    request += 1;
                }
                Command::Annotation(epoch, points) => {
                    let s = state.lock().unwrap();
                    if s.connected
                        && s.control
                        && !s.input_suspended
                        && s.epoch == epoch
                        && s.capabilities & crate::annotations::CAPABILITY != 0
                    {
                        if annotations.len() >= 4 {
                            continue;
                        }
                        annotations.insert(request, Instant::now());
                        net.send(Packet::new(7))?;
                        net.send(crate::annotations::packet(request, epoch, &points)?)?;
                        request += 1;
                    }
                }
                Command::Chat(text) => {
                    ensure!(text.len() <= 8192, "Chat message exceeds 8 KiB");
                    if state.lock().unwrap().capabilities & 4 != 0 {
                        net.send(Packet::new(18).long(request).byte(4).text(&text))?;
                        request += 1;
                    }
                }
                Command::Files(action) => {
                    if let Some(files) = &files {
                        if let Err(e) = files.command(action) {
                            files.state.lock().unwrap().status = e.to_string();
                        }
                    }
                }
            }
        }
        if let Some(files) = &files {
            for _ in 0..4 {
                if net.queued >= 512 * 1024 {
                    break;
                }
                match files.events.try_recv() {
                    Ok(crate::files::Event::Send(p)) => net.send(p)?,
                    Ok(crate::files::Event::Failed(error)) => bail!("{error}"),
                    Err(_) => break,
                }
            }
        }
        if accepted {
            let stop_media = {
                let mut s = media.state.lock().unwrap();
                std::mem::take(&mut s.stop_requested)
            };
            if stop_media {
                for kind in [20, 21] {
                    request += 1;
                    net.send(media.request(kind, false, request)?)?;
                }
            }
            for packet in media.expire() {
                net.send(packet)?;
            }
            for _ in 0..4 {
                if net.queued >= 512 * 1024 {
                    break;
                }
                let block = media.state.lock().unwrap().outgoing();
                if let Some(block) = block {
                    net.send(crate::media::packet(21, block.generation, &block.bytes)?)?;
                } else {
                    break;
                }
            }
        }
        let pending_annotations = annotations.len();
        annotations.retain(|_, started| started.elapsed() < Duration::from_secs(75));
        if annotations.len() != pending_annotations {
            state.lock().unwrap().status = "Annotation timed out. You can retry.".into();
        }
        if clipboard_request != 0 && clipboard_started.elapsed() >= Duration::from_secs(75) {
            clipboard_request = 0;
            state.lock().unwrap().status = "Clipboard action timed out. You can retry.".into();
        }
        if (monitor_list_request != 0 || monitor_selection_request != 0)
            && monitor_started.elapsed() >= Duration::from_secs(75)
        {
            monitor_list_request = 0;
            monitor_selection_request = 0;
            let mut s = state.lock().unwrap();
            s.monitor_pending = false;
            s.monitor_status =
                "Display request timed out. Refresh the list and choose a display again.".into();
        }
        if net.last.elapsed() >= Duration::from_secs(if accepted { 30 } else { 75 }) {
            return Err(crate::recovery::Unavailable("The connection stopped responding").into());
        }
        if accepted && ping.elapsed() > Duration::from_secs(5) {
            net.send(Packet::new(9).long(request))?;
            ping = Instant::now();
        }
        thread::sleep(Duration::from_millis(2));
    }
    Ok(())
}
pub fn validate_input(action: u8, a: i32, b: i32) -> Result<()> {
    let valid = match action {
        0 => (0..=65535).contains(&a) && (0..=65535).contains(&b),
        1 | 2 => (0..=2).contains(&a) && b == 0,
        3 => (-1200..=1200).contains(&a) && b == 0,
        4 | 5 => (1..=254).contains(&a) && (0..=1).contains(&b),
        _ => false,
    };
    ensure!(valid, "Invalid input event");
    Ok(())
}

pub trait Desktop {
    fn supports_monitors(&self) -> bool {
        false
    }
    fn monitors(&mut self) -> Result<Vec<crate::monitors::Monitor>> {
        bail!("Display selection is unavailable")
    }
    /// Failure must leave the current display geometry/capture source unchanged.
    fn select_monitor(&mut self, _id: &str) -> Result<()> {
        bail!("Display selection is unavailable")
    }
    fn media(&self) -> Option<Box<dyn crate::media::Backend>> {
        None
    }
    /// Explicitly selected local folder, exposed as a virtual drive on the wire.
    fn shared_folder(&self) -> Option<std::path::PathBuf> {
        None
    }
    fn size(&self) -> (u32, u32, u32);
    fn capture(&mut self) -> Result<RgbaImage>;
    fn input(&mut self, action: u8, a: i32, b: i32) -> Result<()>;
    fn release(&mut self);
    fn clipboard_available(&self) -> bool {
        false
    }
    fn clipboard_read(&mut self) -> Result<String> {
        bail!("Clipboard access is unavailable")
    }
    fn clipboard_write(&mut self, _text: String) -> Result<()> {
        bail!("Clipboard access is unavailable")
    }
}
pub struct HostRequest {
    pub name: String,
    pub control: bool,
    pub answer: SyncSender<bool>,
}
pub type Factory = Arc<dyn Fn() -> Result<Box<dyn Desktop>> + Send + Sync>;
pub struct Host {
    pub invitation: Invitation,
    pub code: String,
    pub requests: Receiver<HostRequest>,
    pub status: Arc<Mutex<String>>,
    stop: Arc<AtomicBool>,
    reply: Option<SyncSender<String>>,
    worker: Option<thread::JoinHandle<()>>,
}
impl Host {
    pub fn listen(bind: &str, advertised: &str, control: bool, factory: Factory) -> Result<Self> {
        let listener = TcpListener::bind(bind)?;
        listener.set_nonblocking(true)?;
        let identity = tls::identity(advertised.into(), listener.local_addr()?.port())?;
        let invitation = identity.invite.clone();
        let (requests_tx, requests) = mpsc::sync_channel(1);
        let status = Arc::new(Mutex::new("Waiting for a connection".into()));
        let stop = Arc::new(AtomicBool::new(false));
        let mut handle = Self {
            code: invitation.encode(),
            reply: None,
            worker: None,
            invitation,
            requests,
            status: status.clone(),
            stop: stop.clone(),
        };
        handle.worker = Some(thread::spawn(move || {
            while !stop.load(Ordering::Acquire) {
                match listener.accept() {
                    Ok((socket, _)) => {
                        let result = host_session(
                            Stream::Tcp(socket),
                            &identity,
                            control,
                            &factory,
                            &requests_tx,
                            &stop,
                            &status,
                            None,
                        );
                        *status.lock().unwrap() = match result {
                            Ok(()) => "Waiting for a connection".into(),
                            Err(e) => format!("Session ended: {e}"),
                        };
                    }
                    Err(e) if e.kind() == io::ErrorKind::WouldBlock => {
                        thread::sleep(Duration::from_millis(50))
                    }
                    Err(e) => {
                        *status.lock().unwrap() = format!("Listener failed: {e}");
                        break;
                    }
                }
            }
        }));
        Ok(handle)
    }
    pub fn peer(
        library: &std::path::Path,
        control: bool,
        factory: Factory,
        stun: bool,
    ) -> Result<Self> {
        Self::peer_cancellable(
            library,
            control,
            factory,
            stun,
            Arc::new(AtomicBool::new(false)),
        )
    }
    pub fn peer_cancellable(
        library: &std::path::Path,
        control: bool,
        factory: Factory,
        stun: bool,
        stop: Arc<AtomicBool>,
    ) -> Result<Self> {
        Self::peer_resuming(library, control, factory, stun, stop, None)
    }
    pub(crate) fn peer_resuming(
        library: &std::path::Path,
        control: bool,
        factory: Factory,
        stun: bool,
        stop: Arc<AtomicBool>,
        resume_key: Option<zeroize::Zeroizing<String>>,
    ) -> Result<Self> {
        let identity = tls::identity("127.0.0.1".into(), 1)?;
        let invitation = identity.invite.clone();
        let peer = crate::peer::Peer::new(library, stun)?;
        let offer =
            crate::signal::Offer::new(invitation.clone(), peer.offer_cancellable(Some(&stop))?)?;
        let code = offer.encode()?;
        let (requests_tx, requests) = mpsc::sync_channel(1);
        let (reply_tx, reply_rx) = mpsc::sync_channel::<String>(1);
        let status = Arc::new(Mutex::new(
            "Copy the invitation, then paste the other computer's reply".into(),
        ));
        let mut host = Self {
            invitation,
            code,
            requests,
            status: status.clone(),
            stop: stop.clone(),
            reply: Some(reply_tx),
            worker: None,
        };
        host.worker = Some(thread::spawn(move || {
            let result = (|| -> Result<()> {
                let answer = loop {
                    ensure!(!stop.load(Ordering::Acquire), "Sharing stopped");
                    match reply_rx.recv_timeout(Duration::from_millis(100)) {
                        Ok(text) => match offer.verify_reply(&text) {
                            Ok(answer) => break answer,
                            Err(_) => {
                                *status.lock().unwrap() =
                                    "Reply rejected; paste the reply for this invitation".into()
                            }
                        },
                        Err(mpsc::RecvTimeoutError::Timeout) => {}
                        Err(_) => return Ok(()),
                    }
                };
                peer.accept(&answer)?;
                peer.ready(Some(&stop))?;
                host_session(
                    Stream::Peer(peer),
                    &identity,
                    control,
                    &factory,
                    &requests_tx,
                    &stop,
                    &status,
                    resume_key.as_deref().map(String::as_str),
                )
            })();
            *status.lock().unwrap() = match result {
                Ok(()) => "Session ended. Start sharing again for a fresh invitation".into(),
                Err(e) => format!("Session ended: {e}"),
            };
        }));
        Ok(host)
    }
    pub fn is_peer(&self) -> bool {
        self.reply.is_some()
    }
    pub fn accept_reply(&self, text: String) -> Result<()> {
        ensure!(text.len() <= 65536, "Reply exceeds its bound");
        self.reply
            .as_ref()
            .context("This is a direct session")?
            .try_send(text)
            .map_err(|_| anyhow::anyhow!("Wait for the current reply to be processed"))
    }
    pub fn stop(&self) {
        self.stop.store(true, Ordering::Release);
    }
    pub fn is_finished(&self) -> bool {
        self.worker
            .as_ref()
            .is_none_or(|worker| worker.is_finished())
    }
    pub fn close_and_wait(&mut self) {
        self.stop();
        if let Some(worker) = self.worker.take() {
            let _ = worker.join();
        }
    }
}
impl Drop for Host {
    fn drop(&mut self) {
        self.close_and_wait();
    }
}
struct OwnerDesktop(Box<dyn Desktop>);
impl Drop for OwnerDesktop {
    fn drop(&mut self) {
        self.0.release();
    }
}
fn host_session(
    socket: Stream,
    identity: &tls::Identity,
    control: bool,
    factory: &Factory,
    requests: &SyncSender<HostRequest>,
    stop: &AtomicBool,
    status: &Mutex<String>,
    resume_key: Option<&str>,
) -> Result<()> {
    let mut net = Network::new(
        Connection::Server(ServerConnection::new(identity.config.clone())?),
        socket,
        stop,
    )?;
    let start = Instant::now();
    let (version, name) = loop {
        ensure!(!stop.load(Ordering::Acquire), "Sharing stopped");
        ensure!(
            start.elapsed() < Duration::from_secs(12),
            "Authentication timed out"
        );
        let packets = net.tick()?;
        if let Some(p) = packets.first() {
            ensure!(packets.len() == 1 && p.0[0] == 1, "Authentication required");
            let mut r = Reader::new(&p.0[1..]);
            let version = r.int()?;
            let secret = r.text(128)?;
            let name = r.text(128)?;
            r.end()?;
            ensure!(
                (2..=4).contains(&version)
                    && wire::equal(secret.as_bytes(), identity.invite.secret.as_bytes()),
                "Invitation rejected"
            );
            ensure!(
                !name.is_empty() && !name.chars().any(char::is_control),
                "Invalid peer name"
            );
            break (version, name);
        }
        thread::sleep(Duration::from_millis(5));
    };
    let (tx, rx) = mpsc::sync_channel(1);
    requests
        .try_send(HostRequest {
            name: name.clone(),
            control,
            answer: tx,
        })
        .map_err(|_| anyhow::anyhow!("The local approval window is busy"))?;
    *status.lock().unwrap() = format!("Approval requested by {name}");
    let deadline = Instant::now();
    loop {
        ensure!(!stop.load(Ordering::Acquire), "Sharing stopped");
        ensure!(
            deadline.elapsed() < Duration::from_secs(60),
            "Approval timed out"
        );
        match rx.recv_timeout(Duration::from_millis(50)) {
            Ok(true) => break,
            Ok(false) | Err(mpsc::RecvTimeoutError::Disconnected) => {
                bail!("The host declined the request")
            }
            Err(mpsc::RecvTimeoutError::Timeout) => {}
        }
    }
    let mut source = OwnerDesktop(factory()?);
    let mut media =
        crate::media::HostMedia::new(if version >= 4 { source.0.media() } else { None });
    let files = if control && version >= 3 {
        source
            .0
            .shared_folder()
            .map(|root| {
                crate::files::FileClient::serve_resuming(
                    root,
                    if version >= 4 { resume_key } else { None },
                )
            })
            .transpose()?
    } else {
        None
    };
    let (mut w, mut h, mut hz) = source.0.size();
    frame::dimensions(w as i32, h as i32)?;
    ensure!((1..=1000).contains(&hz), "Invalid display refresh");
    let mut accepted = Packet::new(2)
        .byte(control as u8)
        .text("Lume portable host")
        .int(w as i32)
        .int(h as i32)
        .int(version)
        .int(hz as i32);
    if version >= 3 {
        accepted = accepted.byte(files.is_some() as u8)
    }
    let clipboard_allowed = control && source.0.clipboard_available();
    let monitors_allowed = source.0.supports_monitors();
    if version >= 4 {
        accepted = accepted.ulong(
            PORTABLE_IMAGES
                | 4096
                | media.capabilities()
                | if monitors_allowed {
                    crate::monitors::CAPABILITY
                } else {
                    0
                }
                | if clipboard_allowed { 1 | 128 } else { 0 }
                | if files.is_some() {
                    16 | if resume_key.is_some() { 256 } else { 0 }
                } else {
                    0
                },
        )
    }
    net.send(accepted)?;
    *status.lock().unwrap() = format!("Sharing with {name} — Stop sharing ends access");
    let mut quality = Quality::default();
    let mut portable = false;
    let mut seq = 0;
    let mut epoch = 1i32;
    let mut monitor_change: Option<(i64, String)> = None;
    let mut pending = std::collections::VecDeque::new();
    let mut next = Instant::now();
    let mut last_frame: Option<RgbaImage> = None;
    let mut force = true;
    net.last = Instant::now();
    while !stop.load(Ordering::Acquire) {
        for p in net.tick()? {
            let mut r = Reader::new(&p.0[1..]);
            match p.0[0] {
                5 => {
                    let ack = r.int()?;
                    r.end()?;
                    ensure!(
                        pending.pop_front() == Some(ack),
                        "Unexpected frame acknowledgement"
                    );
                }
                6 => {
                    let input_epoch = if version >= 4 { r.int()? } else { 1 };
                    let action = r.byte()?;
                    let a = r.int()?;
                    let b = r.int()?;
                    r.end()?;
                    validate_input(action, a, b)?;
                    if control && input_epoch == epoch && monitor_change.is_none() {
                        source.0.input(action, a, b)?;
                    }
                }
                7 => {
                    r.end()?;
                    source.0.release();
                }
                8 => {
                    let text = r.text(262144)?;
                    r.end()?;
                    let result = if clipboard_allowed {
                        source.0.clipboard_write(text)
                    } else {
                        Err(anyhow::anyhow!("Clipboard access is not enabled"))
                    };
                    if result.is_err() {
                        net.send(
                            Packet::new(12).text("Clipboard text could not be applied. Try again."),
                        )?;
                    }
                }
                9 => {
                    let n = r.long()?;
                    r.end()?;
                    net.send(Packet::new(10).long(n))?;
                }
                10 => {
                    r.long()?;
                    r.end()?;
                }
                11 => return Ok(()),
                14 => {
                    match Quality::read(&mut r, version) {
                        Ok(q) => {
                            r.end()?;
                            quality = q;
                            force = true;
                        }
                        Err(_) => {
                            net.send(Packet::new(12).text("Choose Source or JPEG; this portable host has no H.264 encoder yet"))?;
                        }
                    }
                }
                18 if version >= 4 => {
                    ensure!(p.0.len() <= 262220, "Tool request exceeds its bound");
                    let id = r.long()?;
                    let tool = r.byte()?;
                    ensure!(id > 0, "Invalid tool request");
                    if tool == 1 || tool == 8 {
                        let text = if tool == 8 {
                            Some(r.text(262144)?)
                        } else {
                            None
                        };
                        r.end()?;
                        let action = (|| -> Result<Packet> {
                            ensure!(clipboard_allowed, "Clipboard access is not enabled");
                            if let Some(text) = text {
                                source.0.clipboard_write(text)?;
                                Ok(Packet::new(18))
                            } else {
                                let text = source.0.clipboard_read()?;
                                ensure!(text.len() <= 262144, "Clipboard exceeds 256 KiB");
                                Ok(Packet::new(18).text(&text))
                            }
                        })();
                        let (ok, error, body) = match action {
                            Ok(body) => (true, "", body),
                            Err(_) => (
                                false,
                                "Clipboard is unavailable or busy. You can retry.",
                                Packet::new(18),
                            ),
                        };
                        let mut reply = Packet::new(19)
                            .long(id)
                            .byte(ok as u8)
                            .text(error)
                            .int(body.0.len() as i32);
                        reply.0.extend(body.0);
                        net.send(reply)?;
                    } else if tool == 2 {
                        r.end()?;
                        let result = (|| {
                            ensure!(monitors_allowed, "Display selection is unavailable");
                            crate::monitors::encode_list(&source.0.monitors()?)
                        })();
                        net.send(display_reply(id, result))?;
                    } else if tool == 3 {
                        let display = r.text(256)?;
                        r.end()?;
                        if !monitors_allowed
                            || monitor_change.is_some()
                            || display.is_empty()
                            || display.chars().any(char::is_control)
                        {
                            net.send(display_reply(
                                id,
                                Err(anyhow::anyhow!(
                                    "Display selection is unavailable or another request is pending"
                                )),
                            ))?;
                        } else {
                            source.0.release();
                            monitor_change = Some((id, display));
                        }
                    } else if tool == 6 || tool == 9 {
                        let enabled = r.boolean()?;
                        let generation = r.int()?;
                        r.end()?;
                        for reply in media.request(id, tool, enabled, generation)? {
                            net.send(reply)?;
                        }
                    } else if tool == 10 {
                        portable = r.boolean()?;
                        r.end()?;
                        force = true;
                        net.send(Packet::new(19).long(id).byte(1).text("").int(1).byte(18))?;
                    } else {
                        net.send(
                            Packet::new(19)
                                .long(id)
                                .byte(0)
                                .text("This action is unavailable on this host")
                                .int(1)
                                .byte(18),
                        )?;
                    }
                }
                21 if version >= 4 => {
                    media.receive(&p)?;
                }
                17 => {
                    files
                        .as_ref()
                        .context("File access was not negotiated")?
                        .handle(p)?;
                }
                _ => bail!("Unexpected host message"),
            }
        }
        if pending.is_empty() {
            if let Some((id, display)) = monitor_change.take() {
                let result = (|| -> Result<Packet> {
                    let changed_epoch = epoch
                        .checked_add(1)
                        .context("Display generation exhausted")?;
                    source.0.release();
                    source.0.select_monitor(&display)?;
                    let size = source.0.size();
                    frame::dimensions(size.0 as i32, size.1 as i32)?;
                    ensure!((1..=1000).contains(&size.2), "Invalid display refresh");
                    (w, h, hz) = size;
                    epoch = changed_epoch;
                    last_frame = None;
                    Ok(Packet::new(18)
                        .int(epoch)
                        .int(w as i32)
                        .int(h as i32)
                        .int(hz as i32))
                })();
                force = true;
                next = Instant::now();
                net.send(display_reply(id, result))?;
            }
        }
        ensure!(
            net.last.elapsed() < Duration::from_secs(30),
            "Viewer stopped responding"
        );
        for packet in media.poll()? {
            if !matches!(packet.0[0], 20 | 21) || net.queued < 256 * 1024 {
                net.send(packet)?;
            }
        }
        if let Some(files) = &files {
            for _ in 0..4 {
                if net.queued >= 512 * 1024 {
                    break;
                }
                match files.events.try_recv() {
                    Ok(crate::files::Event::Send(p)) => net.send(p)?,
                    Ok(crate::files::Event::Failed(error)) => bail!("{error}"),
                    Err(_) => break,
                }
            }
        }
        if monitor_change.is_none() && Instant::now() >= next && pending.len() < 2 && !net.pending()
        {
            let original = source.0.capture()?;
            ensure!(
                original.dimensions() == (w, h),
                "Display changed; start a new sharing session"
            );
            let resized = if quality.height > 0 && quality.height < h as i32 {
                let height = quality.height as u32;
                let width = ((w as u64 * height as u64 / h as u64).max(1)) as u32;
                image::imageops::resize(
                    &original,
                    width,
                    height,
                    image::imageops::FilterType::Triangle,
                )
            } else {
                original
            };
            if force || last_frame.as_ref() != Some(&resized) {
                let lossless = portable && quality.lossless;
                let actual = Quality {
                    lossless,
                    ..quality
                };
                if force {
                    net.send(
                        actual
                            .write(Packet::new(15), version)
                            .int(resized.width() as i32)
                            .int(resized.height() as i32)
                            .int(hz as i32),
                    )?;
                }
                seq += 1;
                net.send(frame::encode(
                    &resized,
                    seq,
                    epoch,
                    version,
                    lossless,
                    quality.jpeg as u8,
                )?)?;
                pending.push_back(seq);
                last_frame = Some(resized);
                force = false;
            }
            next = Instant::now() + Duration::from_secs_f64(1.0 / quality.target_fps(hz) as f64);
        }
        thread::sleep(Duration::from_millis(2));
    }
    Ok(())
}

fn display_reply(id: i64, result: Result<Packet>) -> Packet {
    let (ok, error, body) = match result {
        Ok(body) => (true, String::new(), body),
        Err(error) => (
            false,
            error.to_string().chars().take(200).collect(),
            Packet::new(18),
        ),
    };
    let mut packet = Packet::new(19)
        .long(id)
        .byte(ok as u8)
        .text(&error)
        .int(body.0.len() as i32);
    packet.0.extend(body.0);
    packet
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::atomic::AtomicUsize;
    #[test]
    fn stalled_presentation_does_not_block_frames_or_acknowledgements() {
        let (mut host, _) = fixture();
        let mut viewer = Viewer::connect(host.invitation.clone(), Quality::default());
        let entered = Arc::new(AtomicBool::new(false));
        let released = Arc::new(AtomicBool::new(false));
        struct Release(Arc<AtomicBool>);
        impl Drop for Release {
            fn drop(&mut self) {
                self.0.store(true, Ordering::Release);
            }
        }
        let _release = Release(released.clone());
        let (callback_entered, callback_release) = (entered.clone(), released.clone());
        viewer.set_frame_wakeup(move || {
            callback_entered.store(true, Ordering::Release);
            while !callback_release.load(Ordering::Acquire) {
                thread::sleep(Duration::from_millis(2));
            }
        });
        host.requests
            .recv_timeout(Duration::from_secs(8))
            .unwrap()
            .answer
            .send(true)
            .unwrap();
        wait(|| entered.load(Ordering::Acquire));
        let first = viewer.state.lock().unwrap().frames;
        for jpeg in [65, 95, 65] {
            let before = viewer.state.lock().unwrap().frames;
            viewer
                .command(Command::Quality(Quality {
                    jpeg,
                    ..Default::default()
                }))
                .unwrap();
            wait(|| viewer.state.lock().unwrap().frames > before);
        }
        assert!(viewer.state.lock().unwrap().frames >= first + 3);
        released.store(true, Ordering::Release);
        viewer.close_and_wait();
        host.close_and_wait();
    }
    #[test]
    fn saved_recovery_uses_fresh_identity_and_frame_generation() {
        let (mut first, _) = fixture();
        let destination = Arc::new(Mutex::new(first.invitation.clone()));
        let mut viewer = Viewer::start(
            Endpoint::TestSaved(destination.clone()),
            Quality {
                lossless: true,
                ..Default::default()
            },
        );
        first
            .requests
            .recv_timeout(Duration::from_secs(8))
            .unwrap()
            .answer
            .send(true)
            .unwrap();
        wait(|| viewer.state.lock().unwrap().frames > 0);
        let (old_epoch, old_sequence) = {
            let s = viewer.state.lock().unwrap();
            (s.epoch, s.sequence)
        };
        let (mut second, _) = fixture();
        assert_ne!(first.invitation.pin, second.invitation.pin);
        *destination.lock().unwrap() = second.invitation.clone();
        first.close_and_wait();
        wait(|| viewer.state.lock().unwrap().reconnecting);
        assert!(
            viewer
                .command(Command::Clipboard("Do not replay".into()))
                .is_err()
        );
        second
            .requests
            .recv_timeout(Duration::from_secs(8))
            .unwrap()
            .answer
            .send(true)
            .unwrap();
        wait(|| {
            let s = viewer.state.lock().unwrap();
            // Acceptance can precede the first new frame. Earlier in-flight frames
            // may have increased the presentation sequence before disconnection.
            s.connected && s.frame.is_some() && s.epoch > old_epoch && s.sequence > old_sequence
        });
        {
            let s = viewer.state.lock().unwrap();
            assert!(s.epoch > old_epoch);
            assert!(!s.reconnecting);
            assert_eq!(s.reconnect_attempts, 1);
        }
        viewer.close_and_wait();
        second.close_and_wait();
    }
    #[test]
    fn saved_recovery_cancel_and_bad_pin_are_terminal() {
        let (mut host, count) = fixture();
        let mut invite = host.invitation.clone();
        invite.pin[0] ^= 1;
        let mut rejected = Viewer::start(
            Endpoint::TestSaved(Arc::new(Mutex::new(invite))),
            Quality::default(),
        );
        wait(|| rejected.is_finished());
        assert_eq!(rejected.state.lock().unwrap().reconnect_attempts, 0);
        assert_eq!(count.load(Ordering::Acquire), 0);
        rejected.close_and_wait();
        host.close_and_wait();
        let mut offline = Viewer::start(
            Endpoint::TestSaved(Arc::new(Mutex::new(host.invitation.clone()))),
            Quality::default(),
        );
        wait(|| offline.state.lock().unwrap().reconnecting);
        let started = Instant::now();
        offline.command(Command::Disconnect).unwrap();
        offline.close_and_wait();
        assert!(started.elapsed() < Duration::from_secs(1));
    }
    #[test]
    #[ignore = "requires pinned libdatachannel via LUME_TEST_DATACHANNEL"]
    fn native_p2p_tls_images() {
        let library = std::path::PathBuf::from(std::env::var("LUME_TEST_DATACHANNEL").unwrap());
        let count = Arc::new(AtomicUsize::new(0));
        let captured = count.clone();
        let mut host = Host::peer(
            &library,
            false,
            Arc::new(move || {
                captured.fetch_add(1, Ordering::Relaxed);
                Ok(Box::new(Synthetic))
            }),
            false,
        )
        .unwrap();
        let offer = crate::signal::Offer::parse(&host.code).unwrap();
        let mut viewer = Viewer::connect_peer(
            offer,
            library,
            Quality {
                height: 0,
                lossless: true,
                ..Default::default()
            },
        );
        wait(|| viewer.state.lock().unwrap().reply.is_some());
        host.accept_reply(viewer.state.lock().unwrap().reply.clone().unwrap())
            .unwrap();
        let request = host.requests.recv_timeout(Duration::from_secs(8)).unwrap();
        assert_eq!(count.load(Ordering::Relaxed), 0);
        request.answer.send(true).unwrap();
        wait(|| {
            viewer
                .state
                .lock()
                .unwrap()
                .frame
                .as_ref()
                .is_some_and(|image| image.get_pixel(10, 10).0 == [70, 120, 190, 255])
        });
        assert!(viewer.state.lock().unwrap().connected);
        viewer.close_and_wait();
        host.close_and_wait();
    }
    struct Synthetic;
    impl Desktop for Synthetic {
        fn size(&self) -> (u32, u32, u32) {
            (64, 48, 60)
        }
        fn capture(&mut self) -> Result<RgbaImage> {
            Ok(RgbaImage::from_pixel(
                64,
                48,
                image::Rgba([70, 120, 190, 255]),
            ))
        }
        fn input(&mut self, _: u8, _: i32, _: i32) -> Result<()> {
            Ok(())
        }
        fn release(&mut self) {}
    }
    fn wait(check: impl Fn() -> bool) {
        let t = Instant::now();
        while !check() {
            assert!(t.elapsed() < Duration::from_secs(8), "Timed out");
            thread::sleep(Duration::from_millis(10));
        }
    }
    fn fixture() -> (Host, Arc<AtomicUsize>) {
        let captures = Arc::new(AtomicUsize::new(0));
        let c = captures.clone();
        let h = Host::listen(
            "127.0.0.1:0",
            "127.0.0.1",
            false,
            Arc::new(move || {
                c.fetch_add(1, Ordering::Relaxed);
                Ok(Box::new(Synthetic))
            }),
        )
        .unwrap();
        (h, captures)
    }
    #[test]
    fn tls_consent_exact_frames_and_disconnect() {
        let (host, count) = fixture();
        let viewer = Viewer::connect(
            host.invitation.clone(),
            Quality {
                height: 0,
                lossless: true,
                ..Default::default()
            },
        );
        let request = host
            .requests
            .recv_timeout(Duration::from_secs(8))
            .unwrap_or_else(|e| {
                panic!(
                    "{e:?}: client={} host={}",
                    viewer.state.lock().unwrap().status,
                    host.status.lock().unwrap()
                )
            });
        assert_eq!(count.load(Ordering::Relaxed), 0);
        request.answer.send(true).unwrap();
        // JPEG frames can arrive while the lossless capability is negotiated.
        // Observe the requested pixels, not a timing-dependent frame count.
        wait(|| {
            let s = viewer.state.lock().unwrap();
            s.frames >= 2
                && s.frame
                    .as_ref()
                    .is_some_and(|frame| frame.get_pixel(10, 10).0 == [70, 120, 190, 255])
        });
        let control = viewer.state.lock().unwrap().control;
        assert!(!control);
        viewer.disconnect();
        wait(|| !viewer.state.lock().unwrap().connected);
    }
    #[test]
    fn pin_failure_never_reaches_approval() {
        let (host, count) = fixture();
        let mut invite = host.invitation.clone();
        invite.pin[0] ^= 1;
        let v = Viewer::connect(invite, Quality::default());
        wait(|| {
            v.state
                .lock()
                .unwrap()
                .status
                .starts_with("Connection ended")
        });
        assert!(host.requests.try_recv().is_err());
        assert_eq!(count.load(Ordering::Relaxed), 0);
    }
    #[test]
    fn secret_failure_and_decline_never_capture() {
        let (host, count) = fixture();
        let mut invite = host.invitation.clone();
        invite.secret = invite.secret.chars().rev().collect();
        let v = Viewer::connect(invite, Quality::default());
        wait(|| {
            v.state
                .lock()
                .unwrap()
                .status
                .starts_with("Connection ended")
        });
        assert!(host.requests.try_recv().is_err());
        let good = Viewer::connect(host.invitation.clone(), Quality::default());
        host.requests
            .recv_timeout(Duration::from_secs(8))
            .unwrap()
            .answer
            .send(false)
            .unwrap();
        wait(|| {
            good.state
                .lock()
                .unwrap()
                .status
                .starts_with("Connection ended")
        });
        assert_eq!(count.load(Ordering::Relaxed), 0);
    }
    #[test]
    fn input_bounds() {
        assert!(validate_input(0, 65535, 0).is_ok());
        assert!(validate_input(0, 65536, 0).is_err());
        assert!(validate_input(4, 255, 0).is_err());
        assert!(validate_input(8, 0, 0).is_err());
    }
    struct ClipboardFixture(Arc<Mutex<String>>, Arc<AtomicUsize>);
    impl Desktop for ClipboardFixture {
        fn size(&self) -> (u32, u32, u32) {
            Synthetic.size()
        }
        fn capture(&mut self) -> Result<RgbaImage> {
            Synthetic.capture()
        }
        fn input(&mut self, _: u8, _: i32, _: i32) -> Result<()> {
            Ok(())
        }
        fn release(&mut self) {}
        fn clipboard_available(&self) -> bool {
            true
        }
        fn clipboard_read(&mut self) -> Result<String> {
            Ok(self.0.lock().unwrap().clone())
        }
        fn clipboard_write(&mut self, text: String) -> Result<()> {
            ensure!(
                self.1.fetch_add(1, Ordering::Relaxed) > 0,
                "Synthetic busy clipboard"
            );
            *self.0.lock().unwrap() = text;
            Ok(())
        }
    }
    #[test]
    fn clipboard_busy_retry_and_oversize_keep_video_connected() {
        let text = Arc::new(Mutex::new("host text".to_string()));
        let writes = Arc::new(AtomicUsize::new(0));
        let (a, b) = (text.clone(), writes.clone());
        let host = Host::listen(
            "127.0.0.1:0",
            "127.0.0.1",
            true,
            Arc::new(move || Ok(Box::new(ClipboardFixture(a.clone(), b.clone())))),
        )
        .unwrap();
        let viewer = Viewer::connect(host.invitation.clone(), Quality::default());
        host.requests
            .recv_timeout(Duration::from_secs(8))
            .unwrap()
            .answer
            .send(true)
            .unwrap();
        wait(|| viewer.state.lock().unwrap().connected);
        assert!(
            viewer
                .command(Command::Clipboard("x".repeat(262145)))
                .is_err()
        );
        viewer
            .command(Command::Clipboard("first attempt".into()))
            .unwrap();
        wait(|| viewer.state.lock().unwrap().status.contains("could not"));
        assert_eq!(&*text.lock().unwrap(), "host text");
        viewer
            .command(Command::Clipboard("second attempt".into()))
            .unwrap();
        wait(|| writes.load(Ordering::Relaxed) >= 2);
        viewer.command(Command::ReadClipboard).unwrap();
        wait(|| viewer.state.lock().unwrap().clipboard.as_deref() == Some("second attempt"));
        assert!(viewer.state.lock().unwrap().connected);
        let previous = viewer.state.lock().unwrap().frames;
        viewer
            .command(Command::Quality(Quality {
                lossless: true,
                ..Default::default()
            }))
            .unwrap();
        wait(|| viewer.state.lock().unwrap().frames > previous);
    }
    #[test]
    fn cancelling_a_partial_tls_handshake_joins_promptly() {
        let (mut host, count) = fixture();
        let _silent = TcpStream::connect(("127.0.0.1", host.invitation.port)).unwrap();
        thread::sleep(Duration::from_millis(80));
        let started = Instant::now();
        host.close_and_wait();
        assert!(started.elapsed() < Duration::from_secs(2));
        assert_eq!(count.load(Ordering::Relaxed), 0);
    }
}
