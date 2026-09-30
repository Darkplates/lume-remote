//! Bounded, off-network file worker for the existing Windows file protocol.
use crate::wire::{Packet, Reader};
use anyhow::{Context, Result, bail, ensure};
use serde::Serialize;
use sha2::{Digest, Sha256};
use std::{
    collections::{HashMap, VecDeque},
    fs::{self, File, OpenOptions},
    io::{Read, Write},
    path::{Path, PathBuf},
    sync::{
        Arc, Mutex,
        atomic::{AtomicBool, Ordering},
        mpsc::{self, Receiver, SyncSender},
    },
    thread,
    time::{Duration, Instant},
};

const CHUNK: usize = 65536;
const WINDOW: u64 = 8 * CHUNK as u64;
const COMPLETION_HEARTBEAT: Duration = Duration::from_secs(5);
const COMPLETION_DEADLINE: Duration = Duration::from_secs(10 * 60);
#[path = "file_jobs.rs"]
mod jobs;
use jobs::FolderJob;
#[path = "file_resume.rs"]
mod resume;
#[derive(Clone, Default, Serialize)]
pub struct FileState {
    pub operation: u64,
    pub resumable: bool,
    pub resumed_bytes: u64,
    pub path: String,
    pub page: i32,
    pub more: bool,
    pub entries: Vec<Entry>,
    pub listing: bool,
    pub active: bool,
    pub name: String,
    pub direction: String,
    pub bytes: u64,
    pub total: u64,
    pub status: String,
    pub completed: Option<String>,
    pub completed_directory: bool,
    pub folder_job: bool,
    pub items_done: u64,
    pub bytes_done: u64,
}
#[derive(Clone, Serialize)]
pub struct Entry {
    pub name: String,
    pub directory: bool,
    pub length: u64,
}
pub enum FileCommand {
    List {
        path: String,
        page: i32,
    },
    Upload {
        local: PathBuf,
        folder: String,
        name: String,
    },
    Download {
        remote: String,
        folder: PathBuf,
        name: String,
    },
    UploadFolder {
        local: PathBuf,
        folder: String,
        name: String,
    },
    DownloadFolder {
        remote: String,
        folder: PathBuf,
        name: String,
    },
    Cancel,
}
impl FileCommand {
    pub fn validate(&self) -> Result<()> {
        match self {
            Self::List { path, page } => {
                remote_path(path)?;
                ensure!((0..=10000).contains(page), "Invalid folder page");
            }
            Self::Upload {
                local,
                folder,
                name,
            }
            | Self::UploadFolder {
                local,
                folder,
                name,
            } => {
                ensure!(local.is_absolute(), "Choose an absolute local file path");
                remote_path(folder)?;
                safe_name(name)?;
            }
            Self::Download {
                remote,
                folder,
                name,
            }
            | Self::DownloadFolder {
                remote,
                folder,
                name,
            } => {
                ensure!(folder.is_absolute(), "Choose an absolute download folder");
                remote_path(remote)?;
                safe_name(name)?;
                ensure!(
                    remote.rsplit(['\\', '/']).next() == Some(name.as_str()),
                    "Download name does not match the requested file"
                );
            }
            Self::Cancel => {}
        }
        Ok(())
    }
}
pub fn safe_name(name: &str) -> Result<()> {
    ensure!(
        !name.is_empty()
            && name.len() <= 255
            && name != "."
            && name != ".."
            && !name.ends_with([' ', '.'])
            && !name
                .chars()
                .any(|c| c.is_control() || "<>:\"/\\|?*".contains(c)),
        "Choose a normal file name without paths or reserved characters"
    );
    let base = name.split('.').next().unwrap().trim_end().to_uppercase();
    let numbered = (base.starts_with("COM") || base.starts_with("LPT"))
        && base.chars().count() == 4
        && base
            .chars()
            .nth(3)
            .is_some_and(|c| "123456789¹²³".contains(c));
    ensure!(
        !matches!(
            base.as_str(),
            "CON" | "PRN" | "AUX" | "NUL" | "CLOCK$" | "CONIN$" | "CONOUT$"
        ) && !numbered,
        "That file name is reserved"
    );
    Ok(())
}
fn remote_path(path: &str) -> Result<()> {
    ensure!(
        path.len() <= 4096 && !path.chars().any(char::is_control),
        "Invalid remote path"
    );
    Ok(())
}
pub(crate) fn local_path(path: &Path) -> Result<()> {
    ensure!(path.is_absolute(), "Choose an absolute local path");
    for part in path.components() {
        ensure!(
            !matches!(part, std::path::Component::ParentDir),
            "Parent traversal is not allowed"
        );
    }
    for part in path.ancestors() {
        let metadata = fs::symlink_metadata(part)?;
        ensure!(
            !metadata.file_type().is_symlink(),
            "Linked files and folders are not supported"
        );
        #[cfg(windows)]
        {
            use std::os::windows::fs::MetadataExt;
            ensure!(
                metadata.file_attributes() & 0x400 == 0,
                "Linked files and folders are not supported"
            );
        }
    }
    Ok(())
}
/// Refuse special files before open, and close the check/open race on POSIX:
/// nonblocking prevents a substituted FIFO from waiting for a writer; no-follow
/// prevents a substituted final symlink, and metadata validates the descriptor.
fn open_regular(path: &Path) -> Result<File> {
    local_path(path)?;
    let expected = fs::symlink_metadata(path)?;
    ensure!(expected.is_file(), "Choose a regular file");
    open_regular_descriptor(path, &expected)
}
fn open_regular_descriptor(path: &Path, expected: &fs::Metadata) -> Result<File> {
    #[cfg(not(unix))]
    let _ = expected;
    let mut options = OpenOptions::new();
    options.read(true);
    #[cfg(unix)]
    {
        use std::os::unix::fs::OpenOptionsExt;
        options.custom_flags(libc::O_NONBLOCK | libc::O_NOFOLLOW);
    }
    let file = options.open(path)?;
    let metadata = file.metadata()?;
    ensure!(
        metadata.is_file() && metadata.len() <= i64::MAX as u64,
        "Choose a regular file"
    );
    #[cfg(unix)]
    {
        use std::os::unix::fs::MetadataExt;
        ensure!(
            metadata.dev() == expected.dev() && metadata.ino() == expected.ino(),
            "The source file changed before it could be opened"
        );
    }
    Ok(file)
}
fn identifier() -> Result<String> {
    let token = crate::paired::token(16)?;
    use base64::Engine;
    let bytes = base64::engine::general_purpose::URL_SAFE_NO_PAD.decode(token)?;
    Ok(bytes.iter().map(|b| format!("{b:02x}")).collect())
}
fn packet(op: u8, id: &str) -> Packet {
    Packet::new(17).byte(op).text(id)
}
enum Work {
    Command(FileCommand),
    Packet(Packet),
}
pub enum Event {
    Send(Packet),
    Failed(String),
}
pub struct FileClient {
    tx: SyncSender<Work>,
    pub events: Receiver<Event>,
    pub state: Arc<Mutex<FileState>>,
    stop: Arc<AtomicBool>,
    cancel: Arc<AtomicBool>,
    worker: Option<thread::JoinHandle<()>>,
}
impl FileClient {
    pub fn new(state: Arc<Mutex<FileState>>, network_roots: bool) -> Self {
        Self::viewer(state, network_roots, false)
    }
    pub fn viewer(state: Arc<Mutex<FileState>>, network_roots: bool, folders: bool) -> Self {
        Self::start(state, network_roots, folders, None, None)
    }
    pub fn viewer_resuming(
        state: Arc<Mutex<FileState>>,
        network_roots: bool,
        folders: bool,
        key: Option<&str>,
    ) -> Result<Self> {
        Ok(Self::start(
            state,
            network_roots,
            folders,
            None,
            key.map(resume::key).transpose()?,
        ))
    }
    pub fn serve(root: PathBuf) -> Result<Self> {
        Self::serve_resuming(root, None)
    }
    pub fn serve_resuming(root: PathBuf, key: Option<&str>) -> Result<Self> {
        local_path(&root)?;
        ensure!(root.is_dir(), "Choose an existing shared folder");
        Ok(Self::start(
            Default::default(),
            false,
            false,
            Some(root),
            key.map(resume::key).transpose()?,
        ))
    }
    fn start(
        state: Arc<Mutex<FileState>>,
        network_roots: bool,
        folders: bool,
        server_root: Option<PathBuf>,
        resume_key: Option<zeroize::Zeroizing<Vec<u8>>>,
    ) -> Self {
        let (tx, rx) = mpsc::sync_channel(32);
        let (out, events) = mpsc::sync_channel(16);
        let stop = Arc::new(AtomicBool::new(false));
        let worker_stop = stop.clone();
        let cancel = Arc::new(AtomicBool::new(false));
        let worker_cancel = cancel.clone();
        state.lock().unwrap().resumable = resume_key.is_some();
        let shared = state.clone();
        let worker = thread::spawn(move || {
            let mut worker = Worker {
                out,
                stop: worker_stop,
                cancel: worker_cancel,
                resume_key,
                state: shared,
                transfer: None,
                completion: None,
                lists: HashMap::new(),
                network_roots,
                server_root,
                folders,
                job: None,
                retired: VecDeque::new(),
            };
            let result = worker.run(rx);
            if worker.stop.load(Ordering::Acquire) {
                if let Some(Transfer::Receive {
                    incoming: Some(file),
                    ..
                }) = &mut worker.transfer
                {
                    file.preserve();
                }
            }
            let mut s = worker.state.lock().unwrap();
            if worker.stop.load(Ordering::Acquire) {
                if s.active {
                    s.status = if s.resumable && !s.folder_job {
                        "Connection ended. Retry the same file and destination to resume."
                    } else {
                        "Session ended. Completed items kept; incomplete file removed."
                    }
                    .into();
                }
                s.active = false;
                s.listing = false;
                return;
            }
            s.active = false;
            s.listing = false;
            if let Err(e) = result {
                s.status = format!("File protocol error: {e}");
                drop(s);
                let _ = worker.emit(Event::Failed(
                    "The remote file channel sent an invalid response".into(),
                ));
            }
        });
        Self {
            tx,
            events,
            state,
            stop,
            cancel,
            worker: Some(worker),
        }
    }
    pub fn command(&self, command: FileCommand) -> Result<()> {
        command.validate()?;
        let transfer = !matches!(command, FileCommand::List { .. } | FileCommand::Cancel);
        let mut state = self.state.lock().unwrap();
        if transfer {
            ensure!(!state.active, "Finish or cancel the current transfer first");
        }
        if matches!(command, FileCommand::Cancel) {
            self.cancel.store(true, Ordering::Release);
        } else if transfer {
            self.cancel.store(false, Ordering::Release);
        }
        self.tx
            .try_send(Work::Command(command))
            .map_err(|_| anyhow::anyhow!("The file worker is busy"))?;
        if transfer {
            state.operation = state.operation.saturating_add(1);
            state.active = true;
            state.completed = None;
            state.resumed_bytes = 0;
            state.completed_directory = false;
        }
        Ok(())
    }
    pub fn handle(&self, p: Packet) -> Result<()> {
        ensure!(p.0.len() <= 262144, "File packet exceeds its bound");
        self.tx
            .try_send(Work::Packet(p))
            .map_err(|_| anyhow::anyhow!("Too many pending file packets"))
    }
}
impl Drop for FileClient {
    fn drop(&mut self) {
        self.stop.store(true, Ordering::Release);
        if let Some(worker) = self.worker.take() {
            let _ = worker.join();
        }
    }
}
struct PendingList {
    path: String,
    page: i32,
    started: Instant,
}
enum Transfer {
    Send {
        id: String,
        name: String,
        file: File,
        length: u64,
        sent: u64,
        ack: u64,
        ready: bool,
        finished: bool,
        resumable: bool,
        hash: Sha256,
        activity: Instant,
    },
    Receive {
        id: String,
        name: String,
        folder: PathBuf,
        incoming: Option<Incoming>,
        resumable: bool,
        activity: Instant,
    },
}
impl Transfer {
    fn id(&self) -> &str {
        match self {
            Self::Send { id, .. } | Self::Receive { id, .. } => id,
        }
    }
    fn activity(&self) -> Instant {
        match self {
            Self::Send { activity, .. } | Self::Receive { activity, .. } => *activity,
        }
    }
}
struct Incoming {
    file: Option<File>,
    temp: PathBuf,
    folder: PathBuf,
    name: String,
    length: u64,
    position: u64,
    hash: Sha256,
    journal: Option<resume::Journal>,
}
impl Incoming {
    fn create(folder: &Path, name: &str, length: u64) -> Result<Self> {
        local_path(folder)?;
        safe_name(name)?;
        ensure!(folder.is_dir(), "The download folder is unavailable");
        let temp = folder.join(format!(".lume-{}.partial", identifier()?));
        let mut options = OpenOptions::new();
        options.create_new(true).write(true);
        #[cfg(unix)]
        {
            use std::os::unix::fs::OpenOptionsExt;
            options.mode(0o600);
        }
        let file = options.open(&temp)?;
        Ok(Self {
            file: Some(file),
            temp,
            folder: folder.into(),
            name: name.into(),
            length,
            position: 0,
            hash: Sha256::new(),
            journal: None,
        })
    }
    fn append(&mut self, offset: u64, bytes: &[u8]) -> Result<()> {
        ensure!(
            offset == self.position
                && !bytes.is_empty()
                && bytes.len() <= CHUNK
                && bytes.len() as u64 <= self.length - self.position,
            "Invalid file chunk offset or size"
        );
        self.file
            .as_mut()
            .context("Download is closed")?
            .write_all(bytes)?;
        self.hash.update(bytes);
        self.position += bytes.len() as u64;
        Ok(())
    }
    #[cfg(test)]
    fn complete(&mut self, digest: &[u8]) -> Result<PathBuf> {
        self.complete_with(digest, &|| Ok(()))
    }
    fn complete_with(&mut self, digest: &[u8], check: &dyn Fn() -> Result<()>) -> Result<PathBuf> {
        check()?;
        self.check_identity(digest)?;
        ensure!(
            self.position == self.length
                && crate::wire::equal(&self.hash.clone().finalize(), digest),
            "File length or SHA-256 did not match"
        );
        self.file
            .as_mut()
            .context("Download is closed")?
            .sync_all()?;
        check()?;
        self.file.take();
        local_path(&self.folder)?;
        publish_with(
            &self.temp,
            &self.folder,
            &self.name,
            |from, to| fs::hard_link(from, to),
            check,
        )
    }
}
/// Publishes a verified temporary file under the first free name, never replacing
/// an existing file or link. A hard link publishes atomically; file systems that
/// cannot link (FAT/exFAT, some SMB shares) use an exclusive-create copy instead.
#[cfg(test)]
fn publish(
    temp: &Path,
    folder: &Path,
    name: &str,
    link: impl Fn(&Path, &Path) -> std::io::Result<()>,
) -> Result<PathBuf> {
    publish_with(temp, folder, name, link, &|| Ok(()))
}
fn publish_with(
    temp: &Path,
    folder: &Path,
    name: &str,
    link: impl Fn(&Path, &Path) -> std::io::Result<()>,
    check: &dyn Fn() -> Result<()>,
) -> Result<PathBuf> {
    use std::io::ErrorKind;
    let mut linkable = true;
    for n in 0..10000 {
        check()?;
        let candidate = if n == 0 {
            name.to_owned()
        } else {
            let p = Path::new(name);
            let stem = p
                .file_stem()
                .context("Invalid file name")?
                .to_string_lossy();
            match p.extension() {
                Some(ext) => format!("{stem} ({n}).{}", ext.to_string_lossy()),
                None => format!("{stem} ({n})"),
            }
        };
        safe_name(&candidate)?;
        let target = folder.join(candidate);
        if linkable {
            match link(temp, &target) {
                Ok(()) => {
                    fs::remove_file(temp)?;
                    return Ok(target);
                }
                Err(e) if e.kind() == ErrorKind::AlreadyExists => continue,
                Err(e) if e.kind() == ErrorKind::NotFound => return Err(e.into()),
                // Unsupported, PermissionDenied (EPERM on vfat/exFAT), EOPNOTSUPP,
                // ERROR_INVALID_FUNCTION and similar. The copy below is equally
                // non-destructive and reports its own error if the cause is general.
                Err(_) => linkable = false,
            }
        }
        match copy_new(temp, &target, check) {
            Ok(()) => {
                fs::remove_file(temp)?;
                return Ok(target);
            }
            Err(e) if e.kind() == ErrorKind::AlreadyExists => {}
            Err(e) => return Err(e.into()),
        }
    }
    bail!("Choose a different destination file name")
}
/// Copies `temp` into a newly created `target`; fails if `target` exists.
fn copy_new(temp: &Path, target: &Path, check: &dyn Fn() -> Result<()>) -> std::io::Result<()> {
    let mut options = OpenOptions::new();
    options.write(true).create_new(true);
    #[cfg(unix)]
    {
        use std::os::unix::fs::OpenOptionsExt;
        options.mode(0o600);
    }
    let mut out = options.open(target)?;
    let result = (|| {
        let mut input = File::open(temp)?;
        let expected = input.metadata()?.len();
        let mut copied = 0;
        let mut buffer = [0; CHUNK];
        loop {
            check().map_err(std::io::Error::other)?;
            let size = input.read(&mut buffer)?;
            if size == 0 {
                break;
            }
            out.write_all(&buffer[..size])?;
            copied += size as u64;
        }
        if copied != expected {
            return Err(std::io::Error::other("Published copy is incomplete"));
        }
        out.sync_all()?;
        check().map_err(std::io::Error::other)
    })();
    if result.is_err() {
        // Only the file created exclusively above is removed.
        drop(out);
        let _ = fs::remove_file(target);
    }
    result
}
impl Drop for Incoming {
    fn drop(&mut self) {
        self.file.take();
        self.cleanup();
    }
}
struct Worker {
    out: SyncSender<Event>,
    stop: Arc<AtomicBool>,
    state: Arc<Mutex<FileState>>,
    transfer: Option<Transfer>,
    completion: Option<Completion>,
    lists: HashMap<String, PendingList>,
    network_roots: bool,
    cancel: Arc<AtomicBool>,
    resume_key: Option<zeroize::Zeroizing<Vec<u8>>>,
    server_root: Option<PathBuf>,
    folders: bool,
    job: Option<FolderJob>,
    retired: VecDeque<String>,
}
/// At most one disk publication runs per worker. It never owns the network or
/// waits on the UI, and closing the worker does not join a blocked disk syscall.
struct Completion {
    id: String,
    length: u64,
    started: Instant,
    acknowledged: Instant,
    cancel: Arc<AtomicBool>,
    result: Receiver<(Incoming, Result<PathBuf>)>,
}
impl Worker {
    fn start_completion(&mut self, id: &str, file: Incoming, digest: &[u8]) -> Result<()> {
        self.start_completion_with(id, file, digest, |file, digest, check| {
            file.complete_with(digest, check)
        })
    }
    fn start_completion_with(
        &mut self,
        id: &str,
        mut file: Incoming,
        digest: &[u8],
        complete: impl FnOnce(&mut Incoming, &[u8], &dyn Fn() -> Result<()>) -> Result<PathBuf>
        + Send
        + 'static,
    ) -> Result<()> {
        ensure!(
            self.completion.is_none(),
            "Wait for the previous file publication to finish"
        );
        // Reject invalid finishes before starting liveness or expensive disk work.
        file.check_identity(digest)?;
        ensure!(
            file.position == file.length
                && crate::wire::equal(&file.hash.clone().finalize(), digest),
            "File length or SHA-256 did not match"
        );
        let digest = digest.to_vec();
        let length = file.length;
        let cancel = Arc::new(AtomicBool::new(false));
        let (completion_cancel, stop, local_cancel) =
            (cancel.clone(), self.stop.clone(), self.cancel.clone());
        let started = Instant::now();
        let (out, result) = mpsc::sync_channel(1);
        thread::Builder::new()
            .name("lume-file-publication".into())
            .spawn(move || {
                let check = || {
                    ensure!(
                        !stop.load(Ordering::Acquire)
                            && !local_cancel.load(Ordering::Acquire)
                            && !completion_cancel.load(Ordering::Acquire),
                        "File publication cancelled"
                    );
                    ensure!(
                        started.elapsed() < COMPLETION_DEADLINE,
                        "File publication timed out"
                    );
                    Ok(())
                };
                let published = complete(&mut file, &digest, &check);
                if published.is_err()
                    && stop.load(Ordering::Acquire)
                    && !local_cancel.load(Ordering::Acquire)
                    && !completion_cancel.load(Ordering::Acquire)
                {
                    file.preserve();
                }
                // Dropping a receiver never blocks this thread, including on close.
                let _ = out.send((file, published));
            })?;
        self.completion = Some(Completion {
            id: id.into(),
            length,
            started,
            acknowledged: started,
            cancel,
            result,
        });
        Ok(())
    }
    fn pump_completion(&mut self) -> Result<()> {
        let Some(completion) = &self.completion else {
            return Ok(());
        };
        let outcome = match completion.result.try_recv() {
            Ok(outcome) => Some(outcome),
            Err(mpsc::TryRecvError::Empty) => None,
            Err(mpsc::TryRecvError::Disconnected) => {
                self.completion.take();
                return self.fail("File publication worker stopped");
            }
        };
        if let Some((file, result)) = outcome {
            let completion = self.completion.take().unwrap();
            if !self
                .transfer
                .as_ref()
                .is_some_and(|t| t.id() == completion.id)
            {
                return Ok(()); // Cancelled transfers never publish a late success receipt.
            }
            if completion.cancel.load(Ordering::Acquire) || self.cancel.load(Ordering::Acquire) {
                return self.fail("Transfer cancelled. Completed items kept.");
            }
            match result {
                Ok(path) => {
                    let response = if self.server_root.is_some() {
                        self.virtual_path(&path)?
                    } else {
                        path.to_string_lossy().into()
                    };
                    self.send(packet(10, &completion.id).byte(1).text(&response))?;
                    self.transfer.take();
                    self.progress(&file.name, "Receiving", file.length, file.length, false);
                    self.state.lock().unwrap().completed = Some(path.to_string_lossy().into());
                    self.job_file_done(file.length)?;
                }
                Err(e) => {
                    self.send(
                        packet(10, &completion.id)
                            .byte(0)
                            .text("File verification or publication failed"),
                    )?;
                    self.fail(&format!("Download failed: {e}"))?;
                }
            }
        } else if completion.started.elapsed() >= COMPLETION_DEADLINE {
            if !completion.cancel.load(Ordering::Acquire) {
                self.fail("File publication timed out. Completed items kept.")?;
            }
        } else if !completion.cancel.load(Ordering::Acquire)
            && !self.cancel.load(Ordering::Acquire)
            && completion.acknowledged.elapsed() >= COMPLETION_HEARTBEAT
        {
            // Every existing sender accepts a repeated final Ack as liveness.
            // A full outbox must not block cancellation; retry on the next tick.
            let acknowledgement = packet(8, &completion.id).long(completion.length as i64);
            match self.out.try_send(Event::Send(acknowledgement)) {
                Ok(()) => {
                    let now = Instant::now();
                    self.completion.as_mut().unwrap().acknowledged = now;
                    if let Some(Transfer::Receive { activity, .. }) = &mut self.transfer {
                        *activity = now;
                    }
                }
                Err(mpsc::TrySendError::Full(_)) => {}
                Err(_) => bail!("File channel closed"),
            }
        }
        Ok(())
    }
    fn resolve(&self, path: &str) -> Result<PathBuf> {
        let root = self.server_root.as_ref().context("This is a viewer")?;
        let tail = path
            .strip_prefix("R:\\")
            .context("Choose a folder inside the shared drive")?;
        ensure!(
            path.len() <= 4096 && !tail.contains('/'),
            "Invalid shared path"
        );
        let mut full = root.clone();
        for part in tail.split('\\') {
            if !part.is_empty() {
                safe_name(part)?;
                full.push(part);
            }
        }
        local_path(&full)?;
        Ok(full)
    }
    fn virtual_path(&self, path: &Path) -> Result<String> {
        let relative = path.strip_prefix(self.server_root.as_ref().context("This is a viewer")?)?;
        Ok(format!(
            "R:\\{}",
            relative
                .components()
                .map(|c| c.as_os_str().to_string_lossy())
                .collect::<Vec<_>>()
                .join("\\")
        ))
    }
    fn request(&mut self, op: u8, id: &str, r: &mut Reader<'_>) -> Result<()> {
        // Syntax errors terminate the channel; ordinary access/I/O failures get receipts.
        match op {
            1 => {
                let path = r.text(4096)?;
                let page = r.int()?;
                r.end()?;
                ensure!((0..=10000).contains(&page), "Invalid folder page");
                let result = (|| -> Result<Packet> {
                    if path.is_empty() {
                        ensure!(page == 0, "Invalid root page");
                        return Ok(packet(2, id)
                            .text("")
                            .int(0)
                            .byte(0)
                            .int(1)
                            .text("R:\\")
                            .byte(1)
                            .long(0));
                    }
                    let local = self.resolve(&path)?;
                    let mut entries = Vec::new();
                    let mut skipped = 0;
                    let mut more = false;
                    for item in fs::read_dir(local)? {
                        ensure!(!self.stop.load(Ordering::Acquire), "File worker stopped");
                        let Ok(item) = item else { continue };
                        let Some(name) = item.file_name().to_str().map(str::to_owned) else {
                            continue;
                        };
                        if name.starts_with(".lume-") || safe_name(&name).is_err() {
                            continue;
                        }
                        let Ok(kind) = item.file_type() else { continue };
                        if !kind.is_file() && !kind.is_dir() {
                            continue;
                        }
                        if local_path(&item.path()).is_err() {
                            continue;
                        }
                        if skipped < page as usize * 200 {
                            skipped += 1;
                            continue;
                        }
                        if entries.len() == 200 {
                            more = true;
                            break;
                        }
                        let length = if kind.is_file() {
                            let Ok(metadata) = item.metadata() else {
                                continue;
                            };
                            metadata.len()
                        } else {
                            0
                        };
                        if length > i64::MAX as u64 {
                            continue;
                        }
                        entries.push((name, kind.is_dir(), length));
                    }
                    let mut p = packet(2, id)
                        .text(&path)
                        .int(page)
                        .byte(more as u8)
                        .int(entries.len() as i32);
                    for (name, directory, length) in entries {
                        p = p.text(&name).byte(directory as u8).long(length as i64);
                    }
                    Ok(p)
                })();
                match result {
                    Ok(p) => self.send(p)?,
                    Err(_) => self.send(
                        packet(10, id)
                            .byte(0)
                            .text("This shared folder is unavailable"),
                    )?,
                }
            }
            3 | 12 => {
                ensure!(
                    op != 12 || self.resume_key.is_some(),
                    "Resume was not negotiated"
                );
                let path = r.text(4096)?;
                r.end()?;
                let result = (|| -> Result<Transfer> {
                    ensure!(
                        self.transfer.is_none() && self.completion.is_none(),
                        "Another transfer is active"
                    );
                    let local = self.resolve(&path)?;
                    let name = local
                        .file_name()
                        .context("Choose a file")?
                        .to_str()
                        .context("Invalid file name")?
                        .to_owned();
                    safe_name(&name)?;
                    let mut file = open_regular(&local)?;
                    let metadata = file.metadata()?;
                    ensure!(
                        metadata.is_file() && metadata.len() <= i64::MAX as u64,
                        "Choose a regular file"
                    );
                    let length = metadata.len();
                    let mut offer = packet(if op == 12 { 14 } else { 5 }, id)
                        .text(&name)
                        .long(length as i64);
                    if op == 12 {
                        offer = offer.bytes(&self.identity(&mut file, id, length)?);
                    }
                    self.send(offer)?;
                    Ok(Transfer::Send {
                        id: id.into(),
                        name,
                        file,
                        length,
                        sent: 0,
                        ack: 0,
                        ready: false,
                        finished: false,
                        resumable: op == 12,
                        hash: Sha256::new(),
                        activity: Instant::now(),
                    })
                })();
                match result {
                    Ok(t) => self.transfer = Some(t),
                    Err(_) => self.send(
                        packet(10, id)
                            .byte(0)
                            .text("This shared file is unavailable or a transfer is active"),
                    )?,
                }
            }
            4 | 13 => {
                ensure!(
                    op != 13 || self.resume_key.is_some(),
                    "Resume was not negotiated"
                );
                let folder = r.text(4096)?;
                let name = r.text(1024)?;
                safe_name(&name)?;
                let length = r.long()?;
                ensure!(length >= 0, "Invalid file length");
                let digest = if op == 13 {
                    Some(r.take(32)?.try_into().unwrap())
                } else {
                    None
                };
                r.end()?;
                let result = (|| -> Result<Transfer> {
                    ensure!(
                        self.transfer.is_none() && self.completion.is_none(),
                        "Another transfer is active"
                    );
                    let folder = self.resolve(&folder)?;
                    let incoming = self.incoming(&folder, &name, length as u64, digest, id)?;
                    self.send(if op == 13 {
                        packet(15, id)
                            .long(incoming.position as i64)
                            .bytes(&incoming.hash.clone().finalize())
                    } else {
                        packet(8, id).long(0)
                    })?;
                    Ok(Transfer::Receive {
                        id: id.into(),
                        name,
                        folder,
                        incoming: Some(incoming),
                        resumable: op == 13,
                        activity: Instant::now(),
                    })
                })();
                match result {
                    Ok(t) => self.transfer = Some(t),
                    Err(_) => self.send(
                        packet(10, id)
                            .byte(0)
                            .text("Cannot receive this file in the shared folder"),
                    )?,
                }
            }
            11 => {
                let parent = r.text(4096)?;
                let name = r.text(1024)?;
                safe_name(&name)?;
                let unique = r.boolean()?;
                r.end()?;
                let result = (|| -> Result<String> {
                    let parent = self.resolve(&parent)?;
                    ensure!(parent.is_dir(), "Choose a shared folder");
                    for n in 0..if unique { 10000 } else { 1 } {
                        let name = if n == 0 {
                            name.clone()
                        } else {
                            format!("{name} ({n})")
                        };
                        safe_name(&name)?;
                        let path = parent.join(name);
                        match fs::create_dir(&path) {
                            Ok(()) => return self.virtual_path(&path),
                            Err(e) if e.kind() == std::io::ErrorKind::AlreadyExists => {}
                            Err(e) => return Err(e.into()),
                        }
                    }
                    bail!("A folder with that name already exists")
                })();
                match result {
                    Ok(path) => self.send(packet(10, id).byte(1).text(&path))?,
                    Err(_) => self.send(
                        packet(10, id)
                            .byte(0)
                            .text("Cannot create that folder without replacing existing data"),
                    )?,
                }
            }
            _ => bail!("Unsupported file request"),
        }
        Ok(())
    }
    fn emit(&self, mut event: Event) -> Result<()> {
        loop {
            ensure!(!self.stop.load(Ordering::Acquire), "File worker stopped");
            match self.out.try_send(event) {
                Ok(()) => return Ok(()),
                Err(mpsc::TrySendError::Full(e)) => event = e,
                Err(_) => bail!("File channel closed"),
            };
            thread::sleep(Duration::from_millis(5));
        }
    }
    fn send(&self, p: Packet) -> Result<()> {
        self.emit(Event::Send(p))
    }
    fn fail(&mut self, message: &str) -> Result<()> {
        if let Some(completion) = &self.completion {
            completion.cancel.store(true, Ordering::Release);
        }
        if let Some(t) = self.transfer.take() {
            self.retire(t.id().into());
            self.send(packet(9, t.id()))?;
        }
        if let Some(job) = self.job.take() {
            if let Some(id) = job.pending_id() {
                self.retire(id.to_owned());
            }
        }
        let mut s = self.state.lock().unwrap();
        s.active = false;
        s.status = message.into();
        s.completed = None;
        s.completed_directory = false;
        Ok(())
    }
    fn run(&mut self, rx: Receiver<Work>) -> Result<()> {
        while !self.stop.load(Ordering::Acquire) {
            match rx.recv_timeout(Duration::from_millis(10)) {
                Ok(Work::Command(command)) => {
                    if let Err(e) = self.command(command) {
                        let mut s = self.state.lock().unwrap();
                        s.status = e.to_string();
                        s.active = self.transfer.is_some() || self.job.is_some();
                    }
                }
                Ok(Work::Packet(p)) => self.handle(p)?,
                Err(mpsc::RecvTimeoutError::Disconnected) => break,
                Err(mpsc::RecvTimeoutError::Timeout) => {}
            }
            if let Err(e) = self.pump() {
                self.fail(&format!("Transfer failed: {e}"))?;
            }
            if let Err(e) = self.pump_job() {
                self.fail(&format!(
                    "Folder stopped: {e}. Completed items kept; retry creates a new folder."
                ))?;
            }
            if self
                .transfer
                .as_ref()
                .is_some_and(|t| t.activity().elapsed() > Duration::from_secs(30))
            {
                self.fail("Transfer stalled. You can retry.")?;
            }
            let before = self.lists.len();
            self.lists
                .retain(|_, p| p.started.elapsed() < Duration::from_secs(30));
            if self.lists.len() != before {
                let mut s = self.state.lock().unwrap();
                s.listing = !self.lists.is_empty();
                s.status = "The remote folder did not answer. You can retry.".into();
            }
        }
        Ok(())
    }
    fn command(&mut self, command: FileCommand) -> Result<()> {
        command.validate()?;
        ensure!(
            matches!(command, FileCommand::List { .. } | FileCommand::Cancel)
                || self.completion.is_none(),
            "Wait for the previous file publication to finish"
        );
        match command {
            FileCommand::Cancel => self.fail("Transfer cancelled")?,
            FileCommand::List { path, page } => {
                ensure!(self.lists.is_empty(), "Wait for the current folder to load");
                let id = identifier()?;
                self.send(
                    packet(
                        if self.network_roots && path.is_empty() {
                            17
                        } else {
                            1
                        },
                        &id,
                    )
                    .text(&path)
                    .int(page),
                )?;
                self.lists.insert(
                    id,
                    PendingList {
                        path,
                        page,
                        started: Instant::now(),
                    },
                );
                self.state.lock().unwrap().listing = true;
            }
            FileCommand::Upload {
                local,
                folder,
                name,
            } => {
                ensure!(
                    self.transfer.is_none() && self.job.is_none() && self.completion.is_none(),
                    "Finish or cancel the current transfer first"
                );
                let mut file = open_regular(&local)?;
                let metadata = file.metadata()?;
                ensure!(
                    metadata.is_file() && metadata.len() <= i64::MAX as u64,
                    "Choose a regular file"
                );
                let id = identifier()?;
                let length = metadata.len();
                let resumable = self.resume_key.is_some();
                let mut offer = packet(if resumable { 13 } else { 4 }, &id)
                    .text(&folder)
                    .text(&name)
                    .long(length as i64);
                if resumable {
                    offer = offer.bytes(&self.identity(&mut file, &id, length)?);
                }
                self.send(offer)?;
                self.transfer = Some(Transfer::Send {
                    id,
                    name: name.clone(),
                    file,
                    length,
                    sent: 0,
                    ack: 0,
                    ready: false,
                    finished: false,
                    resumable,
                    hash: Sha256::new(),
                    activity: Instant::now(),
                });
                self.progress(&name, "Sending", 0, length, true);
            }
            FileCommand::Download {
                remote,
                folder,
                name,
            } => {
                ensure!(
                    self.transfer.is_none() && self.job.is_none() && self.completion.is_none(),
                    "Finish or cancel the current transfer first"
                );
                local_path(&folder)?;
                ensure!(folder.is_dir(), "Choose a download folder");
                let id = identifier()?;
                let resumable = self.resume_key.is_some();
                self.send(packet(if resumable { 12 } else { 3 }, &id).text(&remote))?;
                self.transfer = Some(Transfer::Receive {
                    id,
                    name: name.clone(),
                    folder,
                    incoming: None,
                    resumable,
                    activity: Instant::now(),
                });
                self.progress(&name, "Receiving", 0, 0, true);
            }
            FileCommand::UploadFolder {
                local,
                folder,
                name,
            } => {
                self.start_upload_job(local, folder, name)?;
            }
            FileCommand::DownloadFolder {
                remote,
                folder,
                name,
            } => {
                self.start_download_job(remote, folder, name)?;
            }
        }
        Ok(())
    }
    fn progress(&self, name: &str, direction: &str, bytes: u64, total: u64, active: bool) {
        let mut s = self.state.lock().unwrap();
        s.name = name.into();
        s.direction = direction.into();
        s.bytes = bytes;
        s.total = total;
        s.active = active || self.job.is_some();
        s.status = if active {
            "Transferring…"
        } else {
            "Complete — SHA-256 verified"
        }
        .into();
        s.completed = None;
        s.completed_directory = false;
        if self.job.is_none() {
            s.folder_job = false;
            s.items_done = 0;
            s.bytes_done = 0;
        }
    }
    fn pump(&mut self) -> Result<()> {
        self.pump_completion()?;
        for _ in 0..8 {
            let p = match &mut self.transfer {
                Some(Transfer::Send {
                    id,
                    file,
                    length,
                    sent,
                    ack,
                    ready,
                    finished,
                    hash,
                    ..
                }) if *ready && !*finished => {
                    if *sent == *length && *ack == *length {
                        *finished = true;
                        Some(packet(7, id).bytes(&hash.clone().finalize()))
                    } else if *sent < *length && *sent - *ack < WINDOW {
                        let mut bytes = vec![0; CHUNK.min((*length - *sent) as usize)];
                        file.read_exact(&mut bytes)
                            .context("The source file changed while sending")?;
                        hash.update(&bytes);
                        let p = packet(6, id)
                            .long(*sent as i64)
                            .int(bytes.len() as i32)
                            .bytes(&bytes);
                        *sent += bytes.len() as u64;
                        Some(p)
                    } else {
                        None
                    }
                }
                _ => None,
            };
            if let Some(p) = p {
                self.send(p)?;
            } else {
                break;
            }
        }
        Ok(())
    }
    fn handle(&mut self, p: Packet) -> Result<()> {
        ensure!(
            p.0.first() == Some(&17) && p.0.len() <= 262144,
            "Invalid file packet"
        );
        let mut r = Reader::new(&p.0[1..]);
        let op = r.byte()?;
        let id = r.text(32)?;
        crate::wire::hex(&id, 16)?;
        // Replies/chunks already in flight after a local cancel cannot affect a new job.
        if self.retired.contains(&id) {
            return Ok(());
        }
        if matches!(op, 1 | 3 | 4 | 11 | 12 | 13) {
            ensure!(self.server_root.is_some(), "Unrequested file action");
            return self.request(op, &id, &mut r);
        }
        match op {
            2 => {
                ensure!(self.server_root.is_none(), "Unexpected folder response");
                let path = r.text(4096)?;
                remote_path(&path)?;
                let page = r.int()?;
                let more = r.boolean()?;
                let count = r.int()?;
                ensure!(
                    (0..=10000).contains(&page) && (0..=200).contains(&count),
                    "Invalid folder listing"
                );
                let mut entries = Vec::new();
                for _ in 0..count {
                    let name = r.text(1024)?;
                    let directory = r.boolean()?;
                    let length = r.long()?;
                    ensure!(length >= 0, "Invalid file size");
                    if path.is_empty() {
                        ensure!(directory, "Invalid root listing");
                        remote_path(&name)?;
                    } else {
                        safe_name(&name)?;
                    }
                    entries.push(Entry {
                        name,
                        directory,
                        length: length as u64,
                    });
                }
                r.end()?;
                if self.job_list(&id, &path, page, more, &entries)? {
                    return Ok(());
                }
                if let Some(pending) = self.lists.remove(&id) {
                    ensure!(
                        path == pending.path && page == pending.page,
                        "Unexpected folder response"
                    );
                    let mut s = self.state.lock().unwrap();
                    s.path = path;
                    s.page = page;
                    s.more = more;
                    s.entries = entries;
                    s.listing = !self.lists.is_empty();
                    if !s.active {
                        s.status.clear();
                    }
                }
            }
            5 | 14 => {
                ensure!(self.server_root.is_none(), "Unrequested file offer");
                ensure!(
                    op != 14 || self.resume_key.is_some(),
                    "Resume was not negotiated"
                );
                let offered = r.text(1024)?;
                safe_name(&offered)?;
                let length = r.long()?;
                ensure!(length >= 0, "Invalid file length");
                let digest = if op == 14 {
                    Some(r.take(32)?.try_into().unwrap())
                } else {
                    None
                };
                r.end()?;
                if let Some(Transfer::Receive {
                    id: expected,
                    name,
                    folder,
                    incoming,
                    resumable,
                    ..
                }) = &self.transfer
                {
                    ensure!(
                        expected == &id
                            && name == &offered
                            && incoming.is_none()
                            && self.completion.is_none()
                            && *resumable == (op == 14),
                        "Unexpected file offer"
                    );
                    match self.incoming(folder, name, length as u64, digest, &id) {
                        Ok(file) => {
                            let receipt = if op == 14 {
                                packet(15, &id)
                                    .long(file.position as i64)
                                    .bytes(&file.hash.clone().finalize())
                            } else {
                                packet(8, &id).long(0)
                            };
                            let mut state = self.state.lock().unwrap();
                            state.total = length as u64;
                            state.bytes = file.position;
                            state.resumed_bytes = file.position;
                            drop(state);
                            if let Some(Transfer::Receive {
                                incoming, activity, ..
                            }) = &mut self.transfer
                            {
                                *incoming = Some(file);
                                *activity = Instant::now();
                            }
                            self.send(receipt)?;
                        }
                        Err(e) => self.fail(&format!("Cannot create download: {e}"))?,
                    }
                } else {
                    self.send(packet(9, &id))?;
                }
            }
            15 => {
                ensure!(self.resume_key.is_some(), "Resume was not negotiated");
                let offset = r.long()?;
                let prefix = r.take(32)?;
                r.end()?;
                ensure!(offset >= 0, "Invalid resume offset");
                self.resume_ready(&id, offset as u64, prefix)?;
            }
            16 => {
                ensure!(self.resume_key.is_some(), "Resume was not negotiated");
                r.end()?;
                match &mut self.transfer {
                    Some(
                        Transfer::Send {
                            id: expected,
                            activity,
                            ..
                        }
                        | Transfer::Receive {
                            id: expected,
                            activity,
                            ..
                        },
                    ) if expected == &id => *activity = Instant::now(),
                    _ => {}
                }
            }
            6 => {
                let offset = r.long()?;
                let size = r.int()?;
                ensure!(
                    offset >= 0 && (1..=CHUNK as i32).contains(&size),
                    "Invalid file chunk"
                );
                let bytes = r.take(size as usize)?;
                r.end()?;
                if let Some(Transfer::Receive {
                    id: expected,
                    incoming,
                    activity,
                    ..
                }) = &mut self.transfer
                {
                    ensure!(expected == &id, "Unexpected file chunk");
                    let file = incoming.as_mut().context("File was not accepted")?;
                    ensure!(
                        offset as u64 == file.position
                            && size as u64 <= file.length - file.position,
                        "Invalid file chunk offset"
                    );
                    match file.append(offset as u64, bytes) {
                        Ok(()) => {
                            *activity = Instant::now();
                            let position = file.position;
                            self.state.lock().unwrap().bytes = position;
                            self.send(packet(8, &id).long(position as i64))?;
                        }
                        Err(e) => self.fail(&format!("Cannot write download: {e}"))?,
                    }
                }
            }
            7 => {
                let digest = r.take(32)?;
                r.end()?;
                // A repeated finish while the file is being verified or published is ignored.
                if self.transfer.as_ref().is_some_and(|t| t.id() == id) && self.completion.is_none()
                {
                    let Some(Transfer::Receive {
                        incoming, activity, ..
                    }) = &mut self.transfer
                    else {
                        bail!("Unexpected transfer finish")
                    };
                    let file = incoming.take().context("Unexpected transfer finish")?;
                    *activity = Instant::now();
                    match self.start_completion(&id, file, digest) {
                        Ok(()) => {}
                        Err(e) => {
                            self.send(
                                packet(10, &id)
                                    .byte(0)
                                    .text("File verification or publication failed"),
                            )?;
                            self.fail(&format!("Download failed: {e}"))?;
                        }
                    }
                }
            }
            8 => {
                let received = r.long()?;
                r.end()?;
                ensure!(received >= 0, "Invalid file acknowledgement");
                if let Some(Transfer::Send {
                    id: expected,
                    ack,
                    sent,
                    ready,
                    resumable,
                    activity,
                    ..
                }) = &mut self.transfer
                {
                    if expected == &id {
                        ensure!(!*resumable || *ready, "Resume prefix was not verified");
                        ensure!(
                            received as u64 >= *ack && received as u64 <= *sent,
                            "Invalid file acknowledgement"
                        );
                        *ready = true;
                        *ack = received as u64;
                        *activity = Instant::now();
                        self.state.lock().unwrap().bytes = *ack;
                    }
                }
            }
            9 => {
                r.end()?;
                if self.transfer.as_ref().is_some_and(|t| t.id() == id) {
                    self.fail("The other computer cancelled the transfer. Completed items kept.")?;
                }
            }
            10 => {
                let ok = r.boolean()?;
                let message = r.text(2048)?;
                r.end()?;
                if self.job_result(&id, ok, &message)? {
                    return Ok(());
                } else if self.lists.remove(&id).is_some() {
                    let mut s = self.state.lock().unwrap();
                    s.listing = !self.lists.is_empty();
                    s.status = message;
                } else if self.transfer.as_ref().is_some_and(|t| t.id() == id) {
                    let mut completed_length = None;
                    if ok {
                        match &self.transfer {
                            Some(Transfer::Send {
                                name,
                                length,
                                ack,
                                finished,
                                ..
                            }) => {
                                ensure!(*finished && ack == length, "Premature file completion");
                                self.progress(name, "Sending", *length, *length, false);
                                completed_length = Some(*length);
                            }
                            _ => bail!("Unexpected file completion"),
                        }
                    } else {
                        self.fail(&format!(
                            "Transfer failed: {message}. Completed items kept."
                        ))?;
                    }
                    self.transfer.take();
                    if let Some(length) = completed_length {
                        self.job_file_done(length)?;
                    }
                }
            }
            _ => bail!("Unrequested or unsupported file operation"),
        }
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    pub(super) fn worker(root: Option<PathBuf>) -> (Worker, Receiver<Event>) {
        let (out, rx) = mpsc::sync_channel(64);
        (
            Worker {
                out,
                stop: Arc::new(AtomicBool::new(false)),
                state: Default::default(),
                transfer: None,
                completion: None,
                lists: HashMap::new(),
                network_roots: false,
                cancel: Arc::new(AtomicBool::new(false)),
                resume_key: None,
                server_root: root,
                folders: true,
                job: None,
                retired: VecDeque::new(),
            },
            rx,
        )
    }
    fn fixture_root() -> PathBuf {
        let root =
            std::env::temp_dir().join(format!("lume-completion-test-{}", identifier().unwrap()));
        fs::create_dir(&root).unwrap();
        root
    }
    fn completing(worker: &mut Worker, folder: &Path) -> (String, Incoming) {
        let id = identifier().unwrap();
        let mut file = Incoming::create(folder, "verified.txt", 4).unwrap();
        file.append(0, b"data").unwrap();
        worker.transfer = Some(Transfer::Receive {
            id: id.clone(),
            name: file.name.clone(),
            folder: folder.into(),
            incoming: None,
            resumable: false,
            activity: Instant::now(),
        });
        (id, file)
    }
    fn wait_completion(worker: &mut Worker) {
        let started = Instant::now();
        while worker.completion.is_some() {
            worker.pump().unwrap();
            assert!(
                started.elapsed() < Duration::from_secs(3),
                "Publication did not finish"
            );
            thread::sleep(Duration::from_millis(1));
        }
    }
    #[test]
    fn publication_rejects_a_repeated_offer_and_ignores_a_repeated_finish() {
        let root = fixture_root();
        let (mut receiver, _replies) = worker(None);
        let (id, file) = completing(&mut receiver, &root);
        let name = file.name.clone();
        let (release, blocked) = mpsc::channel::<()>();
        receiver
            .start_completion_with(
                &id,
                file,
                &Sha256::digest(b"data"),
                move |file, digest, check| {
                    blocked.recv_timeout(Duration::from_secs(10))?;
                    file.complete_with(digest, check)
                },
            )
            .unwrap();
        receiver
            .handle(packet(7, &id).bytes(&Sha256::digest(b"data")))
            .unwrap();
        assert!(receiver.completion.is_some());
        assert!(receiver.handle(packet(5, &id).text(&name).long(4)).is_err());
        release.send(()).unwrap();
        wait_completion(&mut receiver);
        assert_eq!(fs::read(root.join("verified.txt")).unwrap(), b"data");
        fs::remove_dir_all(&root).ok();
    }
    #[test]
    fn completion_heartbeats_outlive_the_legacy_sender_timeout_in_both_roles() {
        // Both roles use real worker/packet handling and the production five-second
        // interval. The only fake is a controlled delay before the real flush/link.
        thread::scope(|scope| {
            for serving in [false, true] {
                scope.spawn(move || {
                    let root = fixture_root();
                    let (mut receiver, replies) = worker(serving.then(|| root.clone()));
                    let (id, file) = completing(&mut receiver, &root);
                    let (mut sender, _) = worker(None);
                    sender.transfer = Some(Transfer::Send {
                        id: id.clone(),
                        name: "verified.txt".into(),
                        file: File::open(&file.temp).unwrap(),
                        length: 4,
                        sent: 4,
                        ack: 4,
                        ready: true,
                        finished: true,
                        resumable: false,
                        hash: file.hash.clone(),
                        activity: Instant::now(),
                    });
                    let (release, blocked) = mpsc::channel();
                    receiver
                        .start_completion_with(
                            &id,
                            file,
                            &Sha256::digest(b"data"),
                            move |file, digest, check| {
                                blocked.recv_timeout(Duration::from_secs(40))?;
                                file.complete_with(digest, check)
                            },
                        )
                        .unwrap();
                    let started = Instant::now();
                    let mut acknowledgements = 0;
                    while started.elapsed() < Duration::from_secs(31) {
                        receiver.pump().unwrap();
                        while let Ok(Event::Send(p)) = replies.try_recv() {
                            assert_eq!(p.0.get(1), Some(&8));
                            acknowledgements += 1;
                            sender.handle(p).unwrap();
                        }
                        assert!(
                            sender.transfer.as_ref().unwrap().activity().elapsed()
                                < Duration::from_secs(30),
                            "Legacy sender would have stalled"
                        );
                        thread::sleep(Duration::from_millis(10));
                    }
                    assert!(acknowledgements >= 6);
                    assert!(!root.join("verified.txt").exists());
                    release.send(()).unwrap();
                    wait_completion(&mut receiver);
                    while let Ok(Event::Send(p)) = replies.try_recv() {
                        sender.handle(p).unwrap();
                    }
                    assert!(sender.transfer.is_none());
                    assert_eq!(fs::read(root.join("verified.txt")).unwrap(), b"data");
                    assert_eq!(fs::read_dir(&root).unwrap().count(), 1);
                    assert!(receiver.state.lock().unwrap().completed.is_some());
                    drop((sender, receiver));
                    fs::remove_dir_all(root).unwrap();
                });
            }
        });
    }
    #[test]
    fn cancel_and_deadline_stop_completion_liveness_without_starting_another_publication() {
        for timeout in [false, true] {
            let root = fixture_root();
            let (mut worker, replies) = worker(None);
            let (id, file) = completing(&mut worker, &root);
            let (release, blocked) = mpsc::channel();
            worker
                .start_completion_with(
                    &id,
                    file,
                    &Sha256::digest(b"data"),
                    move |file, digest, check| {
                        blocked.recv_timeout(Duration::from_secs(5))?;
                        file.complete_with(digest, check)
                    },
                )
                .unwrap();
            if timeout {
                worker.completion.as_mut().unwrap().started = Instant::now() - COMPLETION_DEADLINE;
                worker.pump().unwrap();
                assert!(worker.state.lock().unwrap().status.contains("timed out"));
            } else {
                worker.command(FileCommand::Cancel).unwrap();
            }
            assert!(worker.transfer.is_none());
            assert!(
                worker
                    .completion
                    .as_ref()
                    .unwrap()
                    .cancel
                    .load(Ordering::Acquire)
            );
            assert!(
                worker
                    .command(FileCommand::Download {
                        remote: "R:\\verified.txt".into(),
                        folder: root.clone(),
                        name: "verified.txt".into(),
                    })
                    .is_err()
            );
            while replies.try_recv().is_ok() {}
            worker.completion.as_mut().unwrap().acknowledged =
                Instant::now() - COMPLETION_HEARTBEAT;
            worker.pump().unwrap();
            assert!(
                replies.try_recv().is_err(),
                "Cancellation must stop heartbeat packets"
            );
            release.send(()).unwrap();
            wait_completion(&mut worker);
            assert_eq!(fs::read_dir(&root).unwrap().count(), 0);
            assert!(
                replies.try_recv().is_err(),
                "Cancellation must not send a late success"
            );
            fs::remove_dir(root).unwrap();
        }
    }
    #[test]
    fn closing_a_worker_does_not_join_a_blocked_disk_publication() {
        let root = fixture_root();
        let (mut worker, events) = worker(None);
        let (id, file) = completing(&mut worker, &root);
        let (release, blocked) = mpsc::channel();
        worker
            .start_completion_with(
                &id,
                file,
                &Sha256::digest(b"data"),
                move |file, digest, check| {
                    blocked.recv_timeout(Duration::from_secs(5))?;
                    file.complete_with(digest, check)
                },
            )
            .unwrap();
        let (tx, rx) = mpsc::sync_channel(32);
        let stop = worker.stop.clone();
        let client = FileClient {
            tx,
            events,
            state: worker.state.clone(),
            stop,
            cancel: worker.cancel.clone(),
            worker: Some(thread::spawn(move || worker.run(rx).unwrap())),
        };
        let started = Instant::now();
        drop(client);
        assert!(started.elapsed() < Duration::from_secs(1));
        release.send(()).unwrap();
        let started = Instant::now();
        while fs::read_dir(&root).unwrap().count() != 0 {
            assert!(
                started.elapsed() < Duration::from_secs(3),
                "Cancelled partial was retained"
            );
            thread::sleep(Duration::from_millis(1));
        }
        assert!(!root.join("verified.txt").exists());
        fs::remove_dir(root).unwrap();
    }
    #[test]
    fn cancellation_before_the_worker_consumes_a_result_does_not_send_late_success() {
        let root = fixture_root();
        let (mut worker, replies) = worker(None);
        let (id, file) = completing(&mut worker, &root);
        let (ready, published) = mpsc::channel();
        worker
            .start_completion_with(
                &id,
                file,
                &Sha256::digest(b"data"),
                move |file, digest, check| {
                    let path = file.complete_with(digest, check)?;
                    ready.send(())?;
                    Ok(path)
                },
            )
            .unwrap();
        published.recv_timeout(Duration::from_secs(3)).unwrap();
        // FileClient::command(Cancel) sets this before the queued command is
        // processed. A completion result may become ready in the same interval.
        worker.cancel.store(true, Ordering::Release);
        wait_completion(&mut worker);
        assert!(worker.transfer.is_none());
        assert!(worker.state.lock().unwrap().status.contains("cancelled"));
        assert!(worker.state.lock().unwrap().completed.is_none());
        while let Ok(Event::Send(packet)) = replies.try_recv() {
            assert_eq!(
                packet.0.get(1),
                Some(&9),
                "A cancelled operation sent a late result"
            );
        }
        // Cancellation after atomic publication keeps that completed item, as
        // cancellation does for earlier completed files in a folder operation.
        assert_eq!(fs::read(root.join("verified.txt")).unwrap(), b"data");
        assert_eq!(fs::read_dir(&root).unwrap().count(), 1);
        fs::remove_dir_all(root).unwrap();
    }
    #[test]
    fn completion_heartbeat_does_not_wait_for_a_full_outbox() {
        let root = fixture_root();
        let (mut worker, replies) = worker(None);
        let (id, file) = completing(&mut worker, &root);
        let (release, blocked) = mpsc::channel();
        worker
            .start_completion_with(
                &id,
                file,
                &Sha256::digest(b"data"),
                move |file, digest, check| {
                    blocked.recv_timeout(Duration::from_secs(5))?;
                    file.complete_with(digest, check)
                },
            )
            .unwrap();
        while worker
            .out
            .try_send(Event::Send(packet(8, &id).long(4)))
            .is_ok()
        {}
        worker.completion.as_mut().unwrap().acknowledged = Instant::now() - COMPLETION_HEARTBEAT;
        let started = Instant::now();
        worker.pump().unwrap();
        assert!(started.elapsed() < Duration::from_millis(100));
        while replies.try_recv().is_ok() {}
        worker.command(FileCommand::Cancel).unwrap();
        release.send(()).unwrap();
        wait_completion(&mut worker);
        fs::remove_dir(root).unwrap();
    }
    #[test]
    fn cancelling_copy_publication_removes_only_its_exclusive_output() {
        let root = fixture_root();
        let temp = root.join(".lume-copy.partial");
        fs::write(&temp, vec![1; CHUNK * 4]).unwrap();
        fs::write(root.join("report.bin"), b"existing").unwrap();
        let checks = std::cell::Cell::new(0);
        let result = publish_with(
            &temp,
            &root,
            "report.bin",
            |_, _| Err(std::io::Error::from(std::io::ErrorKind::Unsupported)),
            &|| {
                checks.set(checks.get() + 1);
                ensure!(checks.get() < 5, "Cancelled test copy");
                Ok(())
            },
        );
        assert!(result.is_err());
        assert_eq!(fs::read(root.join("report.bin")).unwrap(), b"existing");
        assert!(!root.join("report (1).bin").exists());
        assert!(temp.exists());
        fs::remove_dir_all(root).unwrap();
    }
    #[cfg(unix)]
    #[test]
    fn upload_fifo_without_a_writer_is_rejected_and_closes_promptly() {
        use std::os::unix::ffi::OsStrExt;
        let root = fixture_root();
        let fifo = root.join("no-writer.fifo");
        let name = std::ffi::CString::new(fifo.as_os_str().as_bytes()).unwrap();
        assert_eq!(unsafe { libc::mkfifo(name.as_ptr(), 0o600) }, 0);
        let state = Arc::new(Mutex::new(FileState::default()));
        let client = FileClient::new(state.clone(), false);
        client
            .command(FileCommand::Upload {
                local: fifo.clone(),
                folder: "R:\\".into(),
                name: "fifo.txt".into(),
            })
            .unwrap();
        let started = Instant::now();
        while state.lock().unwrap().active && started.elapsed() < Duration::from_secs(2) {
            thread::sleep(Duration::from_millis(1));
        }
        let rejected = !state.lock().unwrap().active;
        if !rejected {
            // Rescue an unfixed implementation before asserting, so CI never hangs.
            use std::os::unix::fs::OpenOptionsExt;
            let _rescue = OpenOptions::new()
                .read(true)
                .write(true)
                .custom_flags(libc::O_NONBLOCK)
                .open(&fifo)
                .unwrap();
        }
        let closed = Instant::now();
        drop(client);
        assert!(
            rejected,
            "FIFO selection blocked before regular-file validation"
        );
        assert!(closed.elapsed() < Duration::from_secs(1));
        assert!(state.lock().unwrap().status.contains("regular file"));
        fs::remove_file(fifo).unwrap();
        fs::remove_dir(root).unwrap();
    }
    #[cfg(unix)]
    #[test]
    fn descriptor_open_rejects_fifo_symlink_and_file_substitution_after_the_precheck() {
        use std::os::unix::ffi::OsStrExt;
        let root = fixture_root();
        let source = root.join("source.bin");
        fs::write(&source, b"original").unwrap();
        let expected = fs::symlink_metadata(&source).unwrap();
        // Keep the original inode alive while replacing its name.
        fs::rename(&source, root.join("original.bin")).unwrap();
        let name = std::ffi::CString::new(source.as_os_str().as_bytes()).unwrap();
        assert_eq!(unsafe { libc::mkfifo(name.as_ptr(), 0o600) }, 0);
        let (out, result) = mpsc::channel();
        let (path, metadata) = (source.clone(), expected.clone());
        let opened = thread::spawn(move || {
            out.send(open_regular_descriptor(&path, &metadata).is_err())
                .unwrap()
        });
        let rejected = result.recv_timeout(Duration::from_secs(2));
        if rejected.is_err() {
            use std::os::unix::fs::OpenOptionsExt;
            let _rescue = OpenOptions::new()
                .read(true)
                .write(true)
                .custom_flags(libc::O_NONBLOCK)
                .open(&source)
                .unwrap();
        }
        opened.join().unwrap();
        assert!(rejected.unwrap(), "Substituted FIFO was accepted");
        fs::remove_file(&source).unwrap();
        std::os::unix::fs::symlink(root.join("original.bin"), &source).unwrap();
        assert!(open_regular_descriptor(&source, &expected).is_err());
        fs::remove_file(&source).unwrap();
        fs::write(&source, b"substituted regular file").unwrap();
        assert!(open_regular_descriptor(&source, &expected).is_err());
        assert!(open_regular(&source).is_ok());
        let socket = root.join("socket");
        let listener = std::os::unix::net::UnixListener::bind(&socket).unwrap();
        assert!(open_regular(&socket).is_err());
        drop(listener);
        fs::remove_dir_all(root).unwrap();
    }
    #[test]
    fn publish_falls_back_to_exclusive_copy_without_overwriting() {
        use std::io::{Error, ErrorKind};
        let root =
            std::env::temp_dir().join(format!("lume-publish-test-{}", identifier().unwrap()));
        fs::create_dir(&root).unwrap();
        fs::write(root.join("report.txt"), b"existing").unwrap();
        for (n, kind) in [
            ErrorKind::Unsupported,
            ErrorKind::PermissionDenied,
            ErrorKind::Other,
        ]
        .into_iter()
        .enumerate()
        {
            let temp = root.join(format!(".lume-{n}.partial"));
            fs::write(&temp, format!("verified {n}")).unwrap();
            let links = std::cell::Cell::new(0);
            let published = publish(&temp, &root, "report.txt", |_, _| {
                links.set(links.get() + 1);
                Err(Error::from(kind))
            })
            .unwrap();
            // The first failure selects the copy path; the name is never replaced.
            assert_eq!(links.get(), 1);
            assert_eq!(published, root.join(format!("report ({}).txt", n + 1)));
            assert_eq!(
                fs::read(&published).unwrap(),
                format!("verified {n}").as_bytes()
            );
            assert!(!temp.exists());
        }
        assert_eq!(fs::read(root.join("report.txt")).unwrap(), b"existing");
        // A missing temporary file is an error, never an empty published file.
        assert!(
            publish(&root.join("missing"), &root, "gone.txt", |_, _| Err(
                Error::from(ErrorKind::Unsupported)
            ))
            .is_err()
        );
        assert!(!root.join("gone.txt").exists());
        // The atomic hard-link path still publishes without replacing.
        let temp = root.join(".lume-link.partial");
        fs::write(&temp, b"linked").unwrap();
        let linked = publish(&temp, &root, "report.txt", |a, b| fs::hard_link(a, b)).unwrap();
        assert_eq!(linked, root.join("report (4).txt"));
        assert_eq!(fs::read(linked).unwrap(), b"linked");
        assert!(!temp.exists());
        fs::remove_dir_all(root).unwrap();
    }
    #[test]
    fn scoped_host_denies_traversal_and_unsolicited_viewer_writes() {
        let root = std::env::temp_dir().join(format!("lume-scope-test-{}", identifier().unwrap()));
        fs::create_dir(&root).unwrap();
        let (mut host, _) = worker(Some(root.clone()));
        assert_eq!(host.resolve("R:\\").unwrap(), root);
        for path in ["C:\\", "R:\\..\\", "R:\\../", "R:\\x:stream", "/etc"] {
            assert!(host.resolve(path).is_err());
        }
        let id = identifier().unwrap();
        let mut reader = Reader::new(&[]);
        assert!(host.request(99, &id, &mut reader).is_err());
        let (mut viewer, _) = worker(None);
        assert!(
            viewer
                .handle(
                    packet(4, &id)
                        .text(&root.to_string_lossy())
                        .text("unrequested.txt")
                        .long(1)
                )
                .is_err()
        );
        assert_eq!(fs::read_dir(&root).unwrap().count(), 0);
        fs::remove_dir(root).unwrap();
    }
    #[test]
    fn incoming_offset_and_premature_sender_completion_are_rejected() {
        let root =
            std::env::temp_dir().join(format!("lume-invalid-file-test-{}", identifier().unwrap()));
        fs::create_dir(&root).unwrap();
        let local = root.join("send.bin");
        fs::write(&local, b"abc").unwrap();
        let (mut worker, _rx) = worker(None);
        worker
            .command(FileCommand::Upload {
                local: local.clone(),
                folder: "R:\\".into(),
                name: "send.bin".into(),
            })
            .unwrap();
        let id = worker.transfer.as_ref().unwrap().id().to_owned();
        assert!(worker.handle(packet(8, &id).long(1)).is_err());
        assert!(
            worker
                .handle(packet(10, &id).byte(1).text("R:\\send.bin"))
                .is_err()
        );
        worker.fail("cancel").unwrap();
        worker
            .command(FileCommand::Download {
                remote: "R:\\receive.bin".into(),
                folder: root.clone(),
                name: "receive.bin".into(),
            })
            .unwrap();
        let id = worker.transfer.as_ref().unwrap().id().to_owned();
        worker
            .handle(packet(5, &id).text("receive.bin").long(3))
            .unwrap();
        assert!(
            worker
                .handle(packet(6, &id).long(1).int(3).bytes(b"abc"))
                .is_err()
        );
        drop(worker);
        assert_eq!(fs::read_dir(&root).unwrap().count(), 1);
        fs::remove_file(local).unwrap();
        fs::remove_dir(root).unwrap();
    }
    #[cfg(unix)]
    #[test]
    fn shared_symlink_cannot_escape_selected_root() {
        let root = std::env::temp_dir().join(format!("lume-link-test-{}", identifier().unwrap()));
        fs::create_dir(&root).unwrap();
        std::os::unix::fs::symlink("/", root.join("escape")).unwrap();
        let (host, _) = worker(Some(root.clone()));
        assert!(host.resolve("R:\\escape\\etc").is_err());
        fs::remove_file(root.join("escape")).unwrap();
        fs::remove_dir(root).unwrap();
    }
    #[test]
    fn incoming_hash_collision_and_cleanup() {
        let dir = std::env::temp_dir().join(format!("lume-file-test-{}", identifier().unwrap()));
        fs::create_dir(&dir).unwrap();
        fs::write(dir.join("sample.txt"), b"keep").unwrap();
        let mut incoming = Incoming::create(&dir, "sample.txt", 4).unwrap();
        incoming.append(0, b"data").unwrap();
        let saved = incoming.complete(&Sha256::digest(b"data")).unwrap();
        assert_ne!(saved, dir.join("sample.txt"));
        assert_eq!(fs::read(dir.join("sample.txt")).unwrap(), b"keep");
        assert_eq!(fs::read(&saved).unwrap(), b"data");
        drop(incoming);
        let mut broken = Incoming::create(&dir, "bad.txt", 4).unwrap();
        assert!(broken.append(1, b"data").is_err());
        broken.append(0, b"data").unwrap();
        assert!(broken.complete(&[0; 32]).is_err());
        drop(broken);
        assert_eq!(fs::read_dir(&dir).unwrap().count(), 2);
        fs::remove_file(saved).unwrap();
        fs::remove_file(dir.join("sample.txt")).unwrap();
        fs::remove_dir(dir).unwrap();
    }
    #[test]
    fn names_and_explicit_destinations_reject_traversal() {
        for name in ["../a", "a/b", "CON.txt", "COM¹.txt", "x:", "a.", "a\\b"] {
            assert!(safe_name(name).is_err(), "{name}");
        }
        assert!(safe_name("notes café.txt").is_ok());
        assert!(
            FileCommand::Download {
                remote: "C:\\a.txt".into(),
                folder: std::env::temp_dir(),
                name: "b.txt".into()
            }
            .validate()
            .is_err()
        );
    }
    #[test]
    fn cancel_worker_with_full_outbox_does_not_hang() {
        let client = FileClient::new(Default::default(), false);
        let id = identifier().unwrap();
        for _ in 0..24 {
            client
                .handle(packet(5, &id).text("unrequested.txt").long(1))
                .unwrap();
        }
        thread::sleep(Duration::from_millis(100));
        let started = Instant::now();
        drop(client);
        assert!(started.elapsed() < Duration::from_secs(2));
    }
}
