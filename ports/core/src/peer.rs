//! Native libdatachannel stream. Pinned TLS remains inside the data channel.
use anyhow::{Result, ensure};
use libloading::Library;
use std::{
    collections::{HashMap, VecDeque},
    ffi::{CStr, CString, c_char, c_void},
    io::{self, Read, Write},
    path::Path,
    sync::{
        Arc, Condvar, Mutex, OnceLock,
        atomic::{AtomicBool, AtomicI32, AtomicUsize, Ordering},
    },
    time::{Duration, Instant},
};

type StateCallback = unsafe extern "C" fn(i32, i32, *mut c_void);
type SimpleCallback = unsafe extern "C" fn(i32, *mut c_void);
type MessageCallback = unsafe extern "C" fn(i32, *const c_char, i32, *mut c_void);
type ErrorCallback = unsafe extern "C" fn(i32, *const c_char, *mut c_void);
#[repr(C)]
#[derive(Default)]
struct Config {
    servers: *const *const c_char,
    count: i32,
    proxy: *const c_char,
    bind: *const c_char,
    certificate: i32,
    policy: i32,
    tcp: bool,
    mux: bool,
    manual: bool,
    media: bool,
    begin: u16,
    end: u16,
    mtu: i32,
    message_size: i32,
}
struct Rtc {
    create: unsafe extern "C" fn(*const Config) -> i32,
    delete_peer: unsafe extern "C" fn(i32) -> i32,
    delete_channel: unsafe extern "C" fn(i32) -> i32,
    pointer: unsafe extern "C" fn(i32, *mut c_void),
    state: unsafe extern "C" fn(i32, StateCallback) -> i32,
    gathering: unsafe extern "C" fn(i32, StateCallback) -> i32,
    channel: unsafe extern "C" fn(i32, StateCallback) -> i32,
    open: unsafe extern "C" fn(i32, SimpleCallback) -> i32,
    closed: unsafe extern "C" fn(i32, SimpleCallback) -> i32,
    error: unsafe extern "C" fn(i32, ErrorCallback) -> i32,
    message: unsafe extern "C" fn(i32, MessageCallback) -> i32,
    local: unsafe extern "C" fn(i32, *const c_char) -> i32,
    remote: unsafe extern "C" fn(i32, *const c_char, *const c_char) -> i32,
    description: unsafe extern "C" fn(i32, *mut c_char, i32) -> i32,
    create_channel: unsafe extern "C" fn(i32, *const c_char) -> i32,
    is_open: unsafe extern "C" fn(i32) -> bool,
    buffered: unsafe extern "C" fn(i32) -> i32,
    send: unsafe extern "C" fn(i32, *const c_char, i32) -> i32,
    _library: Library,
}
fn loaded_modules() -> &'static Mutex<Vec<(std::path::PathBuf, Arc<Rtc>)>> {
    static LOADED: OnceLock<Mutex<Vec<(std::path::PathBuf, Arc<Rtc>)>>> = OnceLock::new();
    LOADED.get_or_init(|| Mutex::new(Vec::new()))
}
impl Rtc {
    unsafe fn load(path: &Path) -> Result<Arc<Self>> {
        ensure!(
            path.is_absolute(),
            "Use an absolute path to the packaged WebRTC library"
        );
        // libdatachannel owns process-global workers. Keep its module loaded for
        // the process lifetime, as the Windows P/Invoke application does.
        let canonical = path.canonicalize()?;
        let mut loaded = loaded_modules().lock().unwrap_or_else(|e| e.into_inner());
        if let Some((_, rtc)) = loaded.iter().find(|(p, _)| p == &canonical) {
            return Ok(rtc.clone());
        }
        ensure!(
            loaded.is_empty(),
            "A different WebRTC library is already loaded"
        );
        let lib = unsafe { Library::new(&canonical) }?;
        macro_rules! sym {
            ($name:literal) => {
                unsafe { *lib.get(concat!($name, "\0").as_bytes())? }
            };
        }
        let init: unsafe extern "C" fn(i32, *const c_void) = sym!("rtcInitLogger");
        unsafe {
            init(0, std::ptr::null());
        }
        let rtc = Arc::new(Self {
            create: sym!("rtcCreatePeerConnection"),
            delete_peer: sym!("rtcDeletePeerConnection"),
            delete_channel: sym!("rtcDeleteDataChannel"),
            pointer: sym!("rtcSetUserPointer"),
            state: sym!("rtcSetStateChangeCallback"),
            gathering: sym!("rtcSetGatheringStateChangeCallback"),
            channel: sym!("rtcSetDataChannelCallback"),
            open: sym!("rtcSetOpenCallback"),
            closed: sym!("rtcSetClosedCallback"),
            error: sym!("rtcSetErrorCallback"),
            message: sym!("rtcSetMessageCallback"),
            local: sym!("rtcSetLocalDescription"),
            remote: sym!("rtcSetRemoteDescription"),
            description: sym!("rtcGetLocalDescription"),
            create_channel: sym!("rtcCreateDataChannel"),
            is_open: sym!("rtcIsOpen"),
            buffered: sym!("rtcGetBufferedAmount"),
            send: sym!("rtcSendMessage"),
            _library: lib,
        });
        loaded.push((canonical, rtc.clone()));
        Ok(rtc)
    }
}
#[derive(Default)]
struct State {
    gathered: bool,
    open: bool,
    ended: bool,
    chunks: VecDeque<Vec<u8>>,
    offset: usize,
    bytes: usize,
}
struct Inner {
    rtc: Arc<Rtc>,
    state: Mutex<State>,
    changed: Condvar,
    channel: AtomicI32,
    native_gate: Mutex<()>,
}
impl Inner {
    fn end(&self) {
        self.state.lock().unwrap_or_else(|e| e.into_inner()).ended = true;
        self.changed.notify_all();
    }
    fn attach(&self, id: i32, ptr: *mut c_void) -> Result<()> {
        let _native = self.native_gate.lock().unwrap_or_else(|e| e.into_inner());
        if self.state.lock().unwrap_or_else(|e| e.into_inner()).ended {
            unsafe {
                (self.rtc.delete_channel)(id);
            }
            return Ok(());
        }
        if self
            .channel
            .compare_exchange(-1, id, Ordering::AcqRel, Ordering::Acquire)
            .is_err()
        {
            unsafe {
                (self.rtc.delete_channel)(id);
            }
            return Ok(());
        }
        unsafe {
            (self.rtc.pointer)(id, ptr);
            check((self.rtc.open)(id, on_open))?;
            check((self.rtc.closed)(id, on_closed))?;
            check((self.rtc.error)(id, on_error))?;
            check((self.rtc.message)(id, on_message))?;
            if (self.rtc.is_open)(id) {
                self.state.lock().unwrap_or_else(|e| e.into_inner()).open = true;
                self.changed.notify_all();
            }
        }
        Ok(())
    }
}
fn contexts() -> &'static Mutex<HashMap<usize, Arc<Inner>>> {
    static CONTEXTS: OnceLock<Mutex<HashMap<usize, Arc<Inner>>>> = OnceLock::new();
    CONTEXTS.get_or_init(|| Mutex::new(HashMap::new()))
}
fn inner(p: *mut c_void) -> Option<Arc<Inner>> {
    // The C API may have copied its user pointer before handle deletion. Use a
    // never-reused integer token, not a raw pointer to freed Rust memory.
    contexts()
        .lock()
        .unwrap_or_else(|e| e.into_inner())
        .get(&(p as usize))
        .cloned()
}
unsafe extern "C" fn on_state(_: i32, state: i32, p: *mut c_void) {
    if let Some(i) = inner(p) {
        if state == 4 || state == 5 {
            i.end();
        }
    }
}
unsafe extern "C" fn on_gather(_: i32, state: i32, p: *mut c_void) {
    if let Some(i) = inner(p) {
        if state == 2 {
            i.state.lock().unwrap_or_else(|e| e.into_inner()).gathered = true;
            i.changed.notify_all();
        }
    }
}
unsafe extern "C" fn on_channel(_: i32, id: i32, p: *mut c_void) {
    if let Some(i) = inner(p) {
        if i.attach(id, p).is_err() {
            i.end();
        }
    } else {
        // The C API may deliver a channel after its peer's teardown. It still
        // owns a native handle even though the Rust context has been retired.
        let rtc = loaded_modules()
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .first()
            .map(|(_, rtc)| rtc.clone());
        if let Some(rtc) = rtc {
            unsafe {
                (rtc.delete_channel)(id);
            }
        }
    }
}
unsafe extern "C" fn on_open(_: i32, p: *mut c_void) {
    if let Some(i) = inner(p) {
        i.state.lock().unwrap_or_else(|e| e.into_inner()).open = true;
        i.changed.notify_all();
    }
}
unsafe extern "C" fn on_closed(_: i32, p: *mut c_void) {
    if let Some(i) = inner(p) {
        i.end();
    }
}
unsafe extern "C" fn on_error(_: i32, _: *const c_char, p: *mut c_void) {
    if let Some(i) = inner(p) {
        i.end();
    }
}
unsafe extern "C" fn on_message(_: i32, bytes: *const c_char, n: i32, p: *mut c_void) {
    if let Some(i) = inner(p) {
        if bytes.is_null() || !(1..=16384).contains(&n) {
            i.end();
            return;
        }
        let mut state = i.state.lock().unwrap_or_else(|e| e.into_inner());
        if state.ended {
            return;
        }
        if state.bytes + n as usize > 4 * 1024 * 1024 {
            state.ended = true;
            i.changed.notify_all();
            return;
        }
        state.chunks.push_back(
            unsafe { std::slice::from_raw_parts(bytes.cast::<u8>(), n as usize) }.to_vec(),
        );
        state.bytes += n as usize;
        i.changed.notify_all();
    }
}
fn check(code: i32) -> Result<i32> {
    ensure!(code >= 0, "Native WebRTC operation failed ({code})");
    Ok(code)
}
fn io_error(kind: io::ErrorKind, text: &str) -> io::Error {
    io::Error::new(kind, text)
}

pub struct Peer {
    pc: i32,
    inner: Arc<Inner>,
    callback_reference: usize,
    nonblocking: AtomicBool,
}
// The C library serializes its handles; callbacks access only mutexes/atomics.
// A callback acquires its own Arc from the registry before using its context.
unsafe impl Send for Peer {}
impl Peer {
    pub fn new(library: &Path, stun: bool) -> Result<Self> {
        let rtc = unsafe { Rtc::load(library) }?;
        let server = CString::new("stun:stun.cloudflare.com:3478")?;
        let servers = [server.as_ptr()];
        let config = Config {
            servers: if stun {
                servers.as_ptr()
            } else {
                std::ptr::null()
            },
            count: if stun { 1 } else { 0 },
            manual: true,
            message_size: 16384,
            ..Default::default()
        };
        static NEXT_CONTEXT: AtomicUsize = AtomicUsize::new(1);
        let token = NEXT_CONTEXT
            .fetch_update(Ordering::Relaxed, Ordering::Relaxed, |v| v.checked_add(1))
            .map_err(|_| anyhow::anyhow!("WebRTC context identifiers exhausted"))?;
        let pc = check(unsafe { (rtc.create)(&config) })?;
        let inner = Arc::new(Inner {
            rtc,
            state: Mutex::new(State::default()),
            changed: Condvar::new(),
            channel: AtomicI32::new(-1),
            native_gate: Mutex::new(()),
        });
        contexts()
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .insert(token, inner.clone());
        let peer = Self {
            pc,
            inner,
            callback_reference: token,
            nonblocking: AtomicBool::new(false),
        };
        unsafe {
            (peer.inner.rtc.pointer)(pc, token as *mut c_void);
            check((peer.inner.rtc.state)(pc, on_state))?;
            check((peer.inner.rtc.gathering)(pc, on_gather))?;
            check((peer.inner.rtc.channel)(pc, on_channel))?;
        }
        Ok(peer)
    }
    pub fn offer(&self) -> Result<String> {
        self.offer_cancellable(None)
    }
    pub fn offer_cancellable(&self, stop: Option<&AtomicBool>) -> Result<String> {
        unsafe {
            let dc = check((self.inner.rtc.create_channel)(
                self.pc,
                c"lume-tls".as_ptr(),
            ))?;
            self.inner
                .attach(dc, self.callback_reference as *mut c_void)?;
            check((self.inner.rtc.local)(self.pc, c"offer".as_ptr()))?;
        }
        self.gather(stop)
    }
    pub fn answer(&self, offer: &str) -> Result<String> {
        self.answer_cancellable(offer, None)
    }
    pub fn answer_cancellable(&self, offer: &str, stop: Option<&AtomicBool>) -> Result<String> {
        validate_sdp(offer)?;
        let offer = CString::new(offer)?;
        unsafe {
            check((self.inner.rtc.remote)(
                self.pc,
                offer.as_ptr(),
                c"offer".as_ptr(),
            ))?;
            check((self.inner.rtc.local)(self.pc, c"answer".as_ptr()))?;
        }
        self.gather(stop)
    }
    pub fn accept(&self, answer: &str) -> Result<()> {
        validate_sdp(answer)?;
        let answer = CString::new(answer)?;
        unsafe {
            check((self.inner.rtc.remote)(
                self.pc,
                answer.as_ptr(),
                c"answer".as_ptr(),
            ))?;
        }
        Ok(())
    }
    fn wait(
        &self,
        what: impl Fn(&State) -> bool,
        timeout: Duration,
        stop: Option<&AtomicBool>,
    ) -> Result<()> {
        let start = Instant::now();
        let mut s = self.inner.state.lock().unwrap_or_else(|e| e.into_inner());
        while !what(&s) {
            if s.ended {
                return Err(crate::recovery::Unavailable("P2P connection closed").into());
            }
            ensure!(
                !stop.is_some_and(|s| s.load(Ordering::Acquire)),
                "Connection cancelled"
            );
            if start.elapsed() >= timeout {
                return Err(crate::recovery::Unavailable("P2P address discovery/connection timed out; these routers may need a TURN relay").into());
            }
            s = self
                .inner
                .changed
                .wait_timeout(s, Duration::from_millis(50))
                .unwrap_or_else(|e| e.into_inner())
                .0;
        }
        Ok(())
    }
    fn gather(&self, stop: Option<&AtomicBool>) -> Result<String> {
        self.wait(|s| s.gathered, Duration::from_secs(25), stop)?;
        let mut out = vec![0 as c_char; 32768];
        check(unsafe {
            (self.inner.rtc.description)(self.pc, out.as_mut_ptr(), out.len() as i32)
        })?;
        let text = unsafe { CStr::from_ptr(out.as_ptr()) }.to_str()?.to_owned();
        validate_sdp(&text)?;
        Ok(text)
    }
    pub fn ready(&self, stop: Option<&AtomicBool>) -> Result<()> {
        self.wait(|s| s.open, Duration::from_secs(35), stop)
    }
    pub fn set_nonblocking(&self, value: bool) {
        self.nonblocking.store(value, Ordering::Release);
    }
    pub fn close(&self) {
        self.inner.end();
    }
}
impl Read for Peer {
    fn read(&mut self, out: &mut [u8]) -> io::Result<usize> {
        if out.is_empty() {
            return Ok(0);
        }
        let start = Instant::now();
        let mut s = self.inner.state.lock().unwrap_or_else(|e| e.into_inner());
        loop {
            if s.ended {
                return Err(io_error(
                    io::ErrorKind::ConnectionAborted,
                    "P2P connection closed",
                ));
            }
            if let Some(chunk) = s.chunks.front() {
                let n = out.len().min(chunk.len() - s.offset);
                out[..n].copy_from_slice(&chunk[s.offset..s.offset + n]);
                let complete = s.offset + n == chunk.len();
                s.offset += n;
                s.bytes -= n;
                if complete {
                    s.chunks.pop_front();
                    s.offset = 0;
                }
                return Ok(n);
            }
            if self.nonblocking.load(Ordering::Acquire) {
                return Err(io_error(io::ErrorKind::WouldBlock, "No P2P data available"));
            }
            if start.elapsed() > Duration::from_secs(12) {
                return Err(io_error(io::ErrorKind::TimedOut, "P2P read timed out"));
            }
            s = self
                .inner
                .changed
                .wait_timeout(s, Duration::from_millis(50))
                .unwrap_or_else(|e| e.into_inner())
                .0;
        }
    }
}
impl Write for Peer {
    fn write(&mut self, bytes: &[u8]) -> io::Result<usize> {
        if bytes.is_empty() {
            return Ok(0);
        }
        let start = Instant::now();
        loop {
            {
                let s = self.inner.state.lock().unwrap_or_else(|e| e.into_inner());
                if s.ended || !s.open {
                    return Err(io_error(
                        io::ErrorKind::ConnectionAborted,
                        "P2P data channel is not open",
                    ));
                }
            }
            let id = self.inner.channel.load(Ordering::Acquire);
            let buffered = unsafe { (self.inner.rtc.buffered)(id) };
            if buffered < 0 {
                return Err(io_error(
                    io::ErrorKind::ConnectionAborted,
                    "P2P send queue closed",
                ));
            }
            if buffered <= 262144 {
                let n = bytes.len().min(16384);
                if unsafe { (self.inner.rtc.send)(id, bytes.as_ptr().cast(), n as i32) } < 0 {
                    return Err(io_error(
                        io::ErrorKind::ConnectionAborted,
                        "P2P send failed",
                    ));
                }
                return Ok(n);
            }
            if self.nonblocking.load(Ordering::Acquire) {
                return Err(io_error(
                    io::ErrorKind::WouldBlock,
                    "P2P send queue is full",
                ));
            }
            if start.elapsed() > Duration::from_secs(12) {
                return Err(io_error(io::ErrorKind::TimedOut, "P2P send timed out"));
            }
            std::thread::sleep(Duration::from_millis(2));
        }
    }
    fn flush(&mut self) -> io::Result<()> {
        Ok(())
    }
}
impl Drop for Peer {
    fn drop(&mut self) {
        self.inner.end();
        let dc = {
            let _native = self
                .inner
                .native_gate
                .lock()
                .unwrap_or_else(|e| e.into_inner());
            self.inner.channel.swap(-1, Ordering::AcqRel)
        };
        unsafe {
            if dc >= 0 {
                (self.inner.rtc.delete_channel)(dc);
            }
            (self.inner.rtc.delete_peer)(self.pc);
        }
        contexts()
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .remove(&self.callback_reference);
    }
}

pub fn validate_sdp(sdp: &str) -> Result<()> {
    ensure!(
        (64..=30000).contains(&sdp.len())
            && sdp.starts_with("v=0\r\n")
            && sdp.contains("m=application ")
            && sdp.contains("a=fingerprint:sha-256 ")
            && sdp.contains("a=ice-ufrag:")
            && sdp.contains("a=ice-pwd:")
            && sdp
                .bytes()
                .all(|b| (32..=126).contains(&b) || b == b'\r' || b == b'\n' || b == b'\t'),
        "Invalid P2P description"
    );
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    #[ignore = "requires the pinned native library via LUME_TEST_DATACHANNEL"]
    fn native_peer_stream() {
        let path = std::path::PathBuf::from(std::env::var("LUME_TEST_DATACHANNEL").unwrap());
        let mut a = Peer::new(&path, false).unwrap();
        let mut b = Peer::new(&path, false).unwrap();
        let offer = a.offer().unwrap();
        let answer = b.answer(&offer).unwrap();
        a.accept(&answer).unwrap();
        a.ready(None).unwrap();
        b.ready(None).unwrap();
        a.write_all(b"native stream").unwrap();
        let mut received = [0u8; 13];
        b.read_exact(&mut received).unwrap();
        assert_eq!(&received, b"native stream");
        b.set_nonblocking(true);
        assert_eq!(
            b.read(&mut received).unwrap_err().kind(),
            io::ErrorKind::WouldBlock
        );
    }
    #[test]
    #[ignore = "requires the pinned native library via LUME_TEST_DATACHANNEL"]
    fn cancelled_and_unopened_peers_retire_callback_contexts() {
        let path = std::path::PathBuf::from(std::env::var("LUME_TEST_DATACHANNEL").unwrap());
        for cancelled in [false, true] {
            let peer = Peer::new(&path, false).unwrap();
            let token = peer.callback_reference;
            let weak = Arc::downgrade(&peer.inner);
            if cancelled {
                peer.close();
            }
            drop(peer);
            assert!(!contexts().lock().unwrap().contains_key(&token));
            let start = Instant::now();
            while weak.upgrade().is_some() {
                assert!(
                    start.elapsed() < Duration::from_secs(5),
                    "Retired P2P callback still owns its context"
                );
                std::thread::sleep(Duration::from_millis(10));
            }
            unsafe {
                on_state(0, 4, token as *mut c_void);
            }
        }
    }
}
