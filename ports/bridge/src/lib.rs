//! Small C ABI shared by Swift and the Android JNI adapter.
//! Handles are never reused. No invitation, image or clipboard data is logged.
use anyhow::{Result, ensure};
use lume_core::{
    session::{Command, Viewer},
    wire::Quality,
};
use serde::Deserialize;
use std::{
    cell::RefCell,
    collections::HashMap,
    panic::{AssertUnwindSafe, catch_unwind},
    sync::{
        Arc, Mutex, OnceLock,
        atomic::{AtomicU64, Ordering},
    },
};

type Session = Arc<Mutex<Viewer>>;
fn sessions() -> &'static Mutex<HashMap<u64, Session>> {
    static SESSIONS: OnceLock<Mutex<HashMap<u64, Session>>> = OnceLock::new();
    SESSIONS.get_or_init(|| Mutex::new(HashMap::new()))
}
fn session(handle: u64) -> Result<Session> {
    sessions()
        .lock()
        .map_err(|_| anyhow::anyhow!("Session registry unavailable"))?
        .get(&handle)
        .cloned()
        .ok_or_else(|| anyhow::anyhow!("Session is closed"))
}
thread_local! { static ERROR: RefCell<String> = const { RefCell::new(String::new()) }; }
fn protect<T: Default>(action: impl FnOnce() -> Result<T>) -> T {
    match catch_unwind(AssertUnwindSafe(action)) {
        Ok(Ok(value)) => value,
        error => {
            let text = match error {
                Ok(Err(e)) => e.to_string(),
                _ => "Native action failed".into(),
            };
            ERROR.with(|e| *e.borrow_mut() = text);
            T::default()
        }
    }
}
unsafe fn text<'a>(data: *const u8, size: usize, max: usize) -> Result<&'a str> {
    ensure!(
        size <= max && (size == 0 || !data.is_null()),
        "Invalid text buffer"
    );
    if size == 0 {
        return Ok("");
    }
    Ok(std::str::from_utf8(unsafe {
        std::slice::from_raw_parts(data, size)
    })?)
}
unsafe fn copy(bytes: &[u8], destination: *mut u8, capacity: usize) -> Result<usize> {
    ensure!(
        capacity <= 144 * 1024 * 1024 && (capacity == 0 || !destination.is_null()),
        "Invalid output buffer"
    );
    if capacity >= bytes.len() && !bytes.is_empty() {
        unsafe {
            std::ptr::copy_nonoverlapping(bytes.as_ptr(), destination, bytes.len());
        }
    }
    Ok(bytes.len())
}
/// Returns the UTF-8 byte count; copies only when the supplied buffer is large enough.
/// # Safety
/// Non-null buffers must reference capacity writable bytes and must not overlap.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn lume_error(destination: *mut u8, capacity: usize) -> usize {
    protect(|| ERROR.with(|e| unsafe { copy(e.borrow().as_bytes(), destination, capacity) }))
}
/// Open a viewer. The absolute native library path is used only for P2P invitations.
/// # Safety
/// Input buffers must remain readable for their declared lengths during this call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn lume_open(
    invite: *const u8,
    size: usize,
    library: *const u8,
    library_size: usize,
) -> u64 {
    protect(|| {
        let invite = unsafe { text(invite, size, 65536) }?;
        let library = unsafe { text(library, library_size, 4096) }?;
        let mut registry = sessions()
            .lock()
            .map_err(|_| anyhow::anyhow!("Session registry unavailable"))?;
        ensure!(
            registry.len() < 16,
            "Close a session before opening another"
        );
        static NEXT: AtomicU64 = AtomicU64::new(1);
        let id = NEXT
            .fetch_update(Ordering::Relaxed, Ordering::Relaxed, |v| v.checked_add(1))
            .map_err(|_| anyhow::anyhow!("Session identifiers exhausted"))?;
        let path = std::path::PathBuf::from(library);
        if invite.trim().starts_with("lume-p2p://") || invite.trim().starts_with("lume-saved://") {
            ensure!(
                path.is_absolute() && path.is_file(),
                "The packaged P2P library is unavailable"
            );
        }
        let viewer = Viewer::open(invite.trim(), &path, Quality::default())?;
        registry.insert(id, Arc::new(Mutex::new(viewer)));
        Ok(id)
    })
}
/// Cancel immediately; call lume_close on a background thread to finish teardown.
#[unsafe(no_mangle)]
pub extern "C" fn lume_cancel(handle: u64) -> bool {
    protect(|| {
        session(handle)?
            .lock()
            .map_err(|_| anyhow::anyhow!("Session unavailable"))?
            .disconnect();
        Ok(true)
    })
}
/// Joins network work. Idempotent. Must not run on a UI thread.
#[unsafe(no_mangle)]
pub extern "C" fn lume_close(handle: u64) -> bool {
    protect(|| {
        let value = sessions()
            .lock()
            .map_err(|_| anyhow::anyhow!("Session registry unavailable"))?
            .remove(&handle);
        if let Some(value) = value {
            value
                .lock()
                .map_err(|_| anyhow::anyhow!("Session unavailable"))?
                .close_and_wait();
        }
        Ok(true)
    })
}
/// UTF-8 JSON snapshot. Does not consume clipboard content; acknowledge it explicitly.
/// # Safety
/// destination must be writable for capacity bytes when capacity is non-zero.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn lume_state(handle: u64, destination: *mut u8, capacity: usize) -> usize {
    protect(|| {
        let session = session(handle)?;
        let viewer = session
            .lock()
            .map_err(|_| anyhow::anyhow!("Session unavailable"))?;
        let s = viewer
            .state
            .lock()
            .map_err(|_| anyhow::anyhow!("Session state unavailable"))?;
        let bytes = serde_json::to_vec(
            &serde_json::json!({"connected":s.connected,"status":s.status,"reconnecting":s.reconnecting,"reconnect_attempts":s.reconnect_attempts,
            "peer":s.peer,"control":s.control,"input_ready":s.connected && s.control && s.frame.is_some() && !s.input_suspended,
            "monitors":s.monitors,"monitor_pending":s.monitor_pending,"monitor_status":s.monitor_status,
            "capabilities":s.capabilities,"width":s.width,"height":s.height,
            "refresh":s.refresh,"epoch":s.epoch,"sequence":s.sequence,"reply":s.reply,"clipboard":s.clipboard,
            "frames":s.frames,"chats":s.chats,"media":*s.media.lock().unwrap(),"recording":*viewer.recording.status.lock().unwrap(),"pair_ready":s.paired.is_some(),"files_allowed":s.files_allowed,"files":*s.files.lock().unwrap()}),
        )?;
        unsafe { copy(&bytes, destination, capacity) }
    })
}
/// Private pairing result for encrypted local storage. Never include it in status/logs.
/// Compact snapshot for audio workers: no images, files, clipboard, or credentials.
/// # Safety
/// destination must be writable for capacity bytes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn lume_media(handle: u64, destination: *mut u8, capacity: usize) -> usize {
    protect(|| {
        let value = session(handle)?;
        let viewer = value
            .lock()
            .map_err(|_| anyhow::anyhow!("Session unavailable"))?;
        let state = viewer.state.lock().unwrap();
        let bytes = serde_json::to_vec(
            &serde_json::json!({"connected":state.connected,"media":*state.media.lock().unwrap()}),
        )?;
        unsafe { copy(&bytes, destination, capacity) }
    })
}
/// Private pairing result for encrypted local storage. Never include it in status/logs.
/// # Safety
/// destination must be writable for capacity bytes; data remains until ClearPairing.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn lume_pairing(handle: u64, destination: *mut u8, capacity: usize) -> usize {
    protect(|| {
        let session = session(handle)?;
        let viewer = session
            .lock()
            .map_err(|_| anyhow::anyhow!("Session unavailable"))?;
        let state = viewer
            .state
            .lock()
            .map_err(|_| anyhow::anyhow!("Session unavailable"))?;
        let paired = state
            .paired
            .as_ref()
            .ok_or_else(|| anyhow::anyhow!("No pairing result"))?;
        let bytes = zeroize::Zeroizing::new(serde_json::to_vec(
            &serde_json::json!({"id":paired.id,"name":paired.name,"connection":paired.connection()?}),
        )?);
        unsafe { copy(&bytes, destination, capacity) }
    })
}
#[repr(C)]
#[derive(Default, Clone, Copy)]
pub struct FrameInfo {
    pub width: u32,
    pub height: u32,
    pub epoch: i32,
    pub sequence: i32,
}
/// Copies one complete RGBA8 frame and matching metadata. Returns required bytes.
/// A zero-size destination can query size. If the frame changes size before retry,
/// the returned requirement/metadata describe the new frame and no partial copy occurs.
/// # Safety
/// info must point to a writable FrameInfo; destination must be writable for capacity bytes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn lume_frame(
    handle: u64,
    last: i32,
    info: *mut FrameInfo,
    destination: *mut u8,
    capacity: usize,
) -> usize {
    protect(|| {
        ensure!(!info.is_null(), "Frame metadata buffer is required");
        let session = session(handle)?;
        let viewer = session
            .lock()
            .map_err(|_| anyhow::anyhow!("Session unavailable"))?;
        let s = viewer
            .state
            .lock()
            .map_err(|_| anyhow::anyhow!("Session state unavailable"))?;
        if last == s.sequence || s.frame.is_none() {
            return Ok(0);
        }
        let frame = s.frame.as_ref().unwrap().clone();
        let metadata = FrameInfo {
            width: frame.width(),
            height: frame.height(),
            epoch: s.epoch,
            sequence: s.sequence,
        };
        drop(s);
        drop(viewer);
        unsafe {
            info.write(metadata);
            copy(frame.as_raw(), destination, capacity)
        }
    })
}
/// # Safety
/// Output pointers must be writable, with capacity at least 19200 bytes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn lume_audio(
    handle: u64,
    kind: u8,
    generation: *mut i32,
    destination: *mut u8,
    capacity: usize,
) -> usize {
    protect(|| {
        ensure!(
            matches!(kind, 20 | 21)
                && !generation.is_null()
                && !destination.is_null()
                && capacity >= lume_core::media::MAX_PCM,
            "Invalid PCM output buffer"
        );
        let session = session(handle)?;
        let viewer = session
            .lock()
            .map_err(|_| anyhow::anyhow!("Session unavailable"))?;
        let state = viewer
            .state
            .lock()
            .map_err(|_| anyhow::anyhow!("Session unavailable"))?;
        let Some(pcm) = state.media.lock().unwrap().take(kind) else {
            return Ok(0);
        };
        unsafe {
            generation.write(pcm.generation);
            copy(&pcm.bytes, destination, capacity)
        }
    })
}
/// # Safety
/// Input must be readable for length bytes, at most 19200.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn lume_microphone(
    handle: u64,
    generation: i32,
    data: *const u8,
    length: usize,
) -> bool {
    protect(|| {
        ensure!(
            !data.is_null() && (4..=lume_core::media::MAX_PCM).contains(&length),
            "Invalid PCM input buffer"
        );
        let session = session(handle)?;
        let viewer = session
            .lock()
            .map_err(|_| anyhow::anyhow!("Session unavailable"))?;
        let state = viewer
            .state
            .lock()
            .map_err(|_| anyhow::anyhow!("Session unavailable"))?;
        state.media.lock().unwrap().microphone(generation, unsafe {
            std::slice::from_raw_parts(data, length)
        })?;
        Ok(true)
    })
}
#[derive(Deserialize)]
#[serde(tag = "type", rename_all = "snake_case", deny_unknown_fields)]
enum Action {
    Annotation {
        epoch: i32,
        points: Vec<[i32; 2]>,
    },
    Record {
        path: String,
        #[serde(default)]
        fps: u32,
    },
    StopRecording,
    Audio {
        enabled: bool,
    },
    Voice {
        enabled: bool,
    },
    ClearPairing,
    ListFiles {
        path: String,
        page: i32,
    },
    UploadFile {
        local: String,
        folder: String,
        name: String,
    },
    DownloadFile {
        remote: String,
        folder: String,
        name: String,
    },
    UploadFolder {
        local: String,
        folder: String,
        name: String,
    },
    DownloadFolder {
        remote: String,
        folder: String,
        name: String,
    },
    CancelFile,
    Quality {
        height: i32,
        fps: i32,
        jpeg: i32,
        lossless: bool,
    },
    Input {
        action: u8,
        a: i32,
        b: i32,
        epoch: i32,
    },
    Release,
    Clipboard {
        text: String,
    },
    ReadClipboard,
    ListMonitors,
    SelectMonitor {
        id: String,
    },
    ClearClipboard {
        text: String,
    },
    Chat {
        text: String,
    },
}
/// Enqueue an action. Malformed/local oversized actions leave the connection alive.
/// # Safety
/// data must be readable for size bytes during the call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn lume_action(handle: u64, data: *const u8, size: usize) -> bool {
    protect(|| {
        let value: Action = serde_json::from_str(unsafe { text(data, size, 300000) }?)?;
        let session = session(handle)?;
        let viewer = session
            .lock()
            .map_err(|_| anyhow::anyhow!("Session unavailable"))?;
        let command = match value {
            Action::Annotation { epoch, points } => Command::Annotation(epoch, points),
            Action::Record { path, fps } => {
                viewer.recording.start_with_fps(
                    viewer.state.clone(),
                    std::path::Path::new(&path),
                    fps,
                )?;
                return Ok(true);
            }
            Action::StopRecording => {
                viewer.recording.stop();
                return Ok(true);
            }
            Action::Audio { enabled } => Command::Audio(enabled),
            Action::Voice { enabled } => Command::Voice(enabled),
            Action::ClearPairing => {
                viewer
                    .state
                    .lock()
                    .map_err(|_| anyhow::anyhow!("Session unavailable"))?
                    .paired = None;
                return Ok(true);
            }
            Action::ListFiles { path, page } => {
                Command::Files(lume_core::files::FileCommand::List { path, page })
            }
            Action::UploadFile {
                local,
                folder,
                name,
            } => Command::Files(lume_core::files::FileCommand::Upload {
                local: local.into(),
                folder,
                name,
            }),
            Action::DownloadFile {
                remote,
                folder,
                name,
            } => Command::Files(lume_core::files::FileCommand::Download {
                remote,
                folder: folder.into(),
                name,
            }),
            Action::CancelFile => Command::Files(lume_core::files::FileCommand::Cancel),
            Action::UploadFolder {
                local,
                folder,
                name,
            } => Command::Files(lume_core::files::FileCommand::UploadFolder {
                local: local.into(),
                folder,
                name,
            }),
            Action::DownloadFolder {
                remote,
                folder,
                name,
            } => Command::Files(lume_core::files::FileCommand::DownloadFolder {
                remote,
                folder: folder.into(),
                name,
            }),
            Action::Quality {
                height,
                fps,
                jpeg,
                lossless,
            } => Command::Quality(Quality {
                height,
                fps,
                jpeg,
                lossless,
            }),
            Action::Input {
                action,
                a,
                b,
                epoch,
            } => Command::Input(action, a, b, epoch),
            Action::Release => Command::Release,
            Action::Clipboard { text } => Command::Clipboard(text),
            Action::ReadClipboard => Command::ReadClipboard,
            Action::ListMonitors => Command::ListMonitors,
            Action::SelectMonitor { id } => Command::SelectMonitor(id),
            Action::Chat { text } => Command::Chat(text),
            Action::ClearClipboard { text } => {
                let mut s = viewer
                    .state
                    .lock()
                    .map_err(|_| anyhow::anyhow!("Session state unavailable"))?;
                if s.clipboard.as_deref() == Some(&text) {
                    s.clipboard = None;
                }
                return Ok(true);
            }
        };
        viewer.command(command)?;
        Ok(true)
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use image::{Rgba, RgbaImage};
    use lume_core::session::{Desktop, Host};
    use std::time::{Duration, Instant};
    struct Fixture;
    impl Desktop for Fixture {
        fn size(&self) -> (u32, u32, u32) {
            (64, 48, 60)
        }
        fn capture(&mut self) -> Result<RgbaImage> {
            Ok(RgbaImage::from_pixel(64, 48, Rgba([70, 120, 190, 255])))
        }
        fn input(&mut self, _: u8, _: i32, _: i32) -> Result<()> {
            anyhow::bail!("View only")
        }
        fn release(&mut self) {}
    }
    #[test]
    fn ffi_rejects_buffers_and_stale_handles() {
        unsafe {
            assert_eq!(lume_open(std::ptr::null(), 1, std::ptr::null(), 0), 0);
            assert!(lume_error(std::ptr::null_mut(), 0) > 0);
            assert!(!lume_action(u64::MAX, b"{}".as_ptr(), 2));
            assert_eq!(lume_state(u64::MAX, std::ptr::null_mut(), 0), 0);
            let mut generation = 0;
            let mut pcm = [123u8; 19200];
            assert_eq!(
                lume_audio(u64::MAX, 20, &mut generation, pcm.as_mut_ptr(), pcm.len()),
                0
            );
            assert_eq!(pcm, [123; 19200]);
            assert!(!lume_microphone(u64::MAX, 1, std::ptr::null(), 4));
            assert_eq!(lume_media(u64::MAX, std::ptr::null_mut(), 0), 0);
            assert!(lume_close(u64::MAX));
        }
    }
    #[test]
    fn ffi_real_tls_consent_frame_copy_and_close() {
        unsafe {
            let host = Host::listen(
                "127.0.0.1:0",
                "127.0.0.1",
                false,
                Arc::new(|| Ok(Box::new(Fixture))),
            )
            .unwrap();
            let code = host.invitation.encode();
            let handle = lume_open(code.as_ptr(), code.len(), std::ptr::null(), 0);
            assert_ne!(handle, 0);
            let request = host.requests.recv_timeout(Duration::from_secs(8)).unwrap();
            let mut info = FrameInfo::default();
            assert_eq!(lume_frame(handle, 0, &mut info, std::ptr::null_mut(), 0), 0);
            request.answer.send(true).unwrap();
            let timer = Instant::now();
            let mut bytes = vec![0u8; 64 * 48 * 4];
            loop {
                assert!(timer.elapsed() < Duration::from_secs(8));
                if lume_frame(handle, 0, &mut info, bytes.as_mut_ptr(), bytes.len()) > 0 {
                    break;
                }
                std::thread::sleep(Duration::from_millis(10));
            }
            assert_eq!((info.width, info.height), (64, 48));
            assert!(info.sequence > 0);
            let previous = info.sequence;
            let next = lume_frame(handle, previous, &mut info, bytes.as_mut_ptr(), bytes.len());
            // A negotiation frame may arrive between calls on a slower machine.
            assert!(next == 0 || (next == bytes.len() && info.sequence > previous));
            let mut too_small = [123u8; 4];
            assert_eq!(
                lume_frame(handle, 0, &mut info, too_small.as_mut_ptr(), 4),
                64 * 48 * 4
            );
            assert_eq!(too_small, [123; 4]);
            let mut json = vec![0u8; 8192];
            let n = lume_state(handle, json.as_mut_ptr(), json.len());
            let state: serde_json::Value = serde_json::from_slice(&json[..n]).unwrap();
            assert_eq!(state["connected"], true);
            assert_eq!(state["control"], false);
            let n = lume_media(handle, json.as_mut_ptr(), json.len());
            let compact: serde_json::Value = serde_json::from_slice(&json[..n]).unwrap();
            assert_eq!(compact["connected"], true);
            assert_eq!(compact["media"]["voice"], false);
            assert!(compact.get("clipboard").is_none());
            assert!(!lume_microphone(handle, 1, [0u8; 3840].as_ptr(), 3840));
            assert!(lume_cancel(handle));
            assert!(lume_close(handle));
            assert_eq!(lume_state(handle, json.as_mut_ptr(), json.len()), 0);
        }
    }
}
