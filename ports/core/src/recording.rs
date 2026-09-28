//! Local recording: bounded worker, Matroska MJPEG + optional system PCM. Never records microphones.
use crate::{media::Pcm, session::ViewState};
use anyhow::{Result, ensure};
use serde::Serialize;
use std::{
    fs::{File, OpenOptions},
    io::{Seek, SeekFrom, Write},
    path::Path,
    sync::{
        Arc, Mutex,
        atomic::{AtomicBool, Ordering},
        mpsc,
    },
    thread,
    time::{Duration, Instant},
};
fn length(value: usize) -> Vec<u8> {
    for n in 1..=8 {
        if (value as u64) < (1u64 << (7 * n)) - 1 {
            let mut b = (value as u64).to_be_bytes()[8 - n..].to_vec();
            b[0] |= 1 << (8 - n);
            return b;
        }
    }
    unreachable!()
}
fn element(id: &[u8], payload: &[u8]) -> Vec<u8> {
    let mut out = id.to_vec();
    out.extend(length(payload.len()));
    out.extend(payload);
    out
}
fn uint(id: &[u8], value: u64) -> Vec<u8> {
    let bytes = value.to_be_bytes();
    let first = bytes.iter().position(|v| *v != 0).unwrap_or(7);
    element(id, &bytes[first..])
}
struct Muxer {
    file: File,
    duration: u64,
    segment: u64,
    cues: Vec<(u64, u64)>,
    last_cue: u64,
}
impl Muxer {
    fn new(mut file: File, width: u32, height: u32) -> Result<Self> {
        let mut header = uint(&[0x42, 0x86], 1);
        header.extend(uint(&[0x42, 0xf7], 1));
        header.extend(uint(&[0x42, 0xf2], 4));
        header.extend(uint(&[0x42, 0xf3], 8));
        header.extend(element(&[0x42, 0x82], b"matroska"));
        header.extend(uint(&[0x42, 0x87], 4));
        header.extend(uint(&[0x42, 0x85], 2));
        file.write_all(&element(&[0x1a, 0x45, 0xdf, 0xa3], &header))?;
        file.write_all(&[0x18, 0x53, 0x80, 0x67, 1, 255, 255, 255, 255, 255, 255, 255])?;
        let segment = file.stream_position()?;
        let mut info = uint(&[0x2a, 0xd7, 0xb1], 1_000_000);
        info.extend(element(&[0x4d, 0x80], b"Lume"));
        info.extend(element(&[0x57, 0x41], b"Lume portable"));
        info.extend(element(&[0x44, 0x89], &0f64.to_be_bytes()));
        let info = element(&[0x15, 0x49, 0xa9, 0x66], &info);
        let duration = file.stream_position()? + info.len() as u64 - 8;
        file.write_all(&info)?;
        let mut video = uint(&[0xd7], 1);
        video.extend(uint(&[0x73, 0xc5], 1));
        video.extend(uint(&[0x83], 1));
        video.extend(element(&[0x86], b"V_MJPEG"));
        let mut dimensions = uint(&[0xb0], width as u64);
        dimensions.extend(uint(&[0xba], height as u64));
        video.extend(element(&[0xe0], &dimensions));
        let mut audio = uint(&[0xd7], 2);
        audio.extend(uint(&[0x73, 0xc5], 2));
        audio.extend(uint(&[0x83], 2));
        audio.extend(element(&[0x86], b"A_PCM/INT/LIT"));
        let mut format = element(&[0xb5], &48000f64.to_be_bytes());
        format.extend(uint(&[0x9f], 2));
        format.extend(uint(&[0x62, 0x64], 16));
        audio.extend(element(&[0xe1], &format));
        let mut tracks = element(&[0xae], &video);
        tracks.extend(element(&[0xae], &audio));
        file.write_all(&element(&[0x16, 0x54, 0xae, 0x6b], &tracks))?;
        Ok(Self {
            file,
            duration,
            segment,
            cues: Vec::new(),
            last_cue: 0,
        })
    }
    fn block(&mut self, track: u8, time: u64, bytes: &[u8]) -> Result<()> {
        let position = self.file.stream_position()? - self.segment;
        let mut block = vec![0x80 | track, 0, 0, 0x80];
        block.extend(bytes);
        let mut cluster = uint(&[0xe7], time);
        cluster.extend(element(&[0xa3], &block));
        self.file
            .write_all(&element(&[0x1f, 0x43, 0xb6, 0x75], &cluster))?;
        // One cue per minute bounds the index to < 32 KiB per day; every image is independently decodable.
        if track == 1 && (self.cues.is_empty() || time >= self.last_cue + 60_000) {
            self.cues.push((time, position));
            self.last_cue = time;
        }
        Ok(())
    }
    fn finish(mut self, millis: u64) -> Result<()> {
        let mut cues = Vec::new();
        for (time, position) in self.cues {
            let mut cue = uint(&[0xb3], time);
            let mut target = uint(&[0xf7], 1);
            target.extend(uint(&[0xf1], position));
            cue.extend(element(&[0xb7], &target));
            cues.extend(element(&[0xbb], &cue));
        }
        self.file
            .write_all(&element(&[0x1c, 0x53, 0xbb, 0x6b], &cues))?;
        self.file.seek(SeekFrom::Start(self.duration))?;
        self.file.write_all(&(millis as f64).to_be_bytes())?;
        self.file.sync_all()?;
        Ok(())
    }
}
#[derive(Default, Serialize)]
pub struct Status {
    pub active: bool,
    pub path: String,
    pub frames: u64,
    pub target_fps: u32,
    pub skipped_frames: u64,
    pub audio_blocks: u64,
    pub dropped_audio: u64,
    pub message: String,
}
pub struct Control {
    pub status: Arc<Mutex<Status>>,
    stop: Arc<AtomicBool>,
    worker: Mutex<Option<thread::JoinHandle<()>>>,
}
impl Default for Control {
    fn default() -> Self {
        Self {
            status: Arc::new(Mutex::new(Status::default())),
            stop: Arc::new(AtomicBool::new(false)),
            worker: Mutex::new(None),
        }
    }
}
impl Control {
    pub fn start(&self, state: Arc<Mutex<ViewState>>, path: &Path) -> Result<()> {
        self.start_with_fps(state, path, 0)
    }
    pub fn start_with_fps(
        &self,
        state: Arc<Mutex<ViewState>>,
        path: &Path,
        fps: u32,
    ) -> Result<()> {
        ensure!(fps <= 1000, "Recording FPS must be 0 (source) or 1–1000");
        let mut worker = self.worker.lock().unwrap();
        ensure!(
            !self.status.lock().unwrap().active,
            "Recording is already active"
        );
        if let Some(w) = worker.take() {
            let _ = w.join();
        }
        ensure!(
            path.is_absolute()
                && path
                    .extension()
                    .is_some_and(|s| s.eq_ignore_ascii_case("mkv")),
            "Choose an absolute .mkv destination"
        );
        let (width, height, epoch, media, target_fps) = {
            let s = state.lock().unwrap();
            ensure!(s.connected, "Connect before recording");
            let f = s
                .frame
                .as_ref()
                .ok_or_else(|| anyhow::anyhow!("Wait for the first image"))?;
            (
                f.width(),
                f.height(),
                s.epoch,
                s.media.clone(),
                if fps == 0 {
                    s.refresh.clamp(1, 1000)
                } else {
                    fps
                },
            )
        };
        let mut options = OpenOptions::new();
        options.write(true).create_new(true);
        #[cfg(unix)]
        {
            use std::os::unix::fs::OpenOptionsExt;
            options.mode(0o600);
        }
        let mux = Muxer::new(options.open(path)?, width, height)?;
        let (tx, rx) = mpsc::sync_channel::<Pcm>(8);
        let (wake, changed) = mpsc::sync_channel(1);
        state.lock().unwrap().record_wakeup = Some(wake);
        media.lock().unwrap().record_audio = Some(tx);
        *self.status.lock().unwrap() = Status {
            active: true,
            target_fps,
            path: path.to_string_lossy().into(),
            message: "Recording image and enabled system audio. Microphones are excluded.".into(),
            ..Default::default()
        };
        self.stop.store(false, Ordering::Release);
        let stop = self.stop.clone();
        let status = self.status.clone();
        *worker = Some(thread::spawn(move || {
            let begin = Instant::now();
            let mut mux = mux;
            let mut sequence = 0;
            let period = Duration::from_nanos(1_000_000_000 / target_fps as u64);
            let mut next_frame = begin;
            let mut audio_clock = 0f64;
            let mut reason = "Recording saved.";
            let result = (|| -> Result<()> {
                while !stop.load(Ordering::Acquire) {
                    let (connected, e, seq, frame) = {
                        let s = state.lock().unwrap();
                        (s.connected, s.epoch, s.sequence, s.frame.clone())
                    };
                    if !connected || e != epoch {
                        reason =
                            "Recording saved. A connection or display change ended this recording.";
                        break;
                    }
                    let sampled = Instant::now();
                    if seq != sequence && sampled >= next_frame {
                        if let Some(frame) = frame {
                            if frame.dimensions() != (width, height) {
                                reason = "Recording saved. Resolution changed.";
                                break;
                            }
                            let mut jpeg = Vec::new();
                            image::codecs::jpeg::JpegEncoder::new_with_quality(&mut jpeg, 90)
                                .encode_image(&*frame)?;
                            mux.block(1, sampled.duration_since(begin).as_millis() as u64, &jpeg)?;
                            if sequence > 0 && seq > sequence {
                                status.lock().unwrap().skipped_frames +=
                                    (seq - sequence - 1) as u64;
                            }
                            sequence = seq;
                            next_frame = sampled + period;
                            status.lock().unwrap().frames += 1;
                        }
                    }
                    for _ in 0..8 {
                        match rx.try_recv() {
                            Ok(p) => {
                                let arrival = p
                                    .received
                                    .checked_duration_since(begin)
                                    .unwrap_or_default()
                                    .as_secs_f64()
                                    * 1000.;
                                let duration = p.bytes.len() as f64 / 192.;
                                if audio_clock == 0. || arrival > audio_clock + 150. {
                                    audio_clock = (arrival - duration).max(0.);
                                }
                                mux.block(2, audio_clock as u64, &p.bytes)?;
                                audio_clock += duration;
                                status.lock().unwrap().audio_blocks += 1;
                            }
                            Err(_) => break,
                        }
                    }
                    // One pending wakeup, latest image only. Slow encoding never blocks ACKs.
                    let wait = if seq != sequence {
                        next_frame
                            .saturating_duration_since(Instant::now())
                            .min(Duration::from_millis(20))
                    } else {
                        Duration::from_millis(20)
                    };
                    let _ = changed.recv_timeout(wait);
                }
                Ok(())
            })();
            media.lock().unwrap().record_audio = None;
            state.lock().unwrap().record_wakeup = None;
            let saved = mux.finish(begin.elapsed().as_millis() as u64);
            let mut s = status.lock().unwrap();
            s.active = false;
            s.message = if result.is_ok() && saved.is_ok() {
                reason.into()
            } else {
                "Recording stopped because the file could not be written completely. Check free disk space.".into()
            };
        }));
        Ok(())
    }
    pub fn stop(&self) {
        self.stop.store(true, Ordering::Release);
    }
}
impl Drop for Control {
    fn drop(&mut self) {
        self.stop();
        if let Some(w) = self.worker.lock().unwrap().take() {
            let _ = w.join();
        }
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn source_fps_recording_distinct_frames_epoch_stop_and_no_overwrite() {
        let path = std::env::var_os("LUME_RECORDING_TEST_OUTPUT")
            .map(std::path::PathBuf::from)
            .unwrap_or_else(|| {
                std::env::temp_dir().join(format!(
                    "lume-source-fps-{}.mkv",
                    crate::paired::token(12).unwrap()
                ))
            });
        assert!(!path.exists(), "Preserve prior recording evidence");
        let mut initial = ViewState::default();
        initial.connected = true;
        initial.epoch = 1;
        initial.refresh = 180;
        initial.sequence = 1;
        initial.frame = Some(Arc::new(image::RgbaImage::from_pixel(
            16,
            16,
            image::Rgba([0, 128, 40, 255]),
        )));
        let state = Arc::new(Mutex::new(initial));
        let recorder = Control::default();
        assert!(recorder.start_with_fps(state.clone(), &path, 1001).is_err());
        assert!(!path.exists());
        recorder.start(state.clone(), &path).unwrap();
        assert_eq!(recorder.status.lock().unwrap().target_fps, 180);
        for n in 1..=180 {
            {
                let mut s = state.lock().unwrap();
                s.sequence = n;
                s.frame = Some(Arc::new(image::RgbaImage::from_pixel(
                    16,
                    16,
                    image::Rgba([n as u8, 128, 40, 255]),
                )));
                let _ = s.record_wakeup.as_ref().unwrap().try_send(());
            }
            let until = Instant::now() + Duration::from_secs(3);
            while recorder.status.lock().unwrap().frames < n as u64 {
                assert!(
                    Instant::now() < until,
                    "Recording did not consume a notified frame"
                );
                thread::sleep(Duration::from_millis(1));
            }
        }
        state.lock().unwrap().epoch = 2;
        let until = Instant::now() + Duration::from_secs(3);
        while recorder.status.lock().unwrap().active {
            assert!(Instant::now() < until);
            thread::sleep(Duration::from_millis(2));
        }
        assert_eq!(recorder.status.lock().unwrap().frames, 180);
        assert!(
            recorder
                .status
                .lock()
                .unwrap()
                .message
                .contains("display change")
        );
        assert!(state.lock().unwrap().record_wakeup.is_none());
        let bytes = std::fs::read(&path).unwrap();
        assert!(recorder.start_with_fps(state, &path, 10).is_err());
        assert_eq!(std::fs::read(&path).unwrap(), bytes);
        drop(recorder);
        if std::env::var_os("LUME_RECORDING_TEST_OUTPUT").is_none() {
            std::fs::remove_file(path).unwrap();
        }
    }
    #[test]
    fn matroska_sizes_and_preservation() {
        for n in [0, 1, 126, 127, 128, 16382, 16383, 32768] {
            let b = length(n);
            assert!(b.len() <= 8);
            let mut value = (b[0] & ((1 << (8 - b.len())) - 1)) as usize;
            for v in b.iter().skip(1) {
                value = (value << 8) | *v as usize;
            }
            assert_eq!(value, n);
        }
        let path = std::env::temp_dir().join(format!(
            "lume-mkv-{}.mkv",
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .unwrap()
                .as_nanos()
        ));
        let f = OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(&path)
            .unwrap();
        let mut m = Muxer::new(f, 2, 2).unwrap();
        m.block(1, 0, b"fixture").unwrap();
        m.block(2, 20, &[0; 3840]).unwrap();
        m.finish(1250).unwrap();
        let bytes = std::fs::read(&path).unwrap();
        assert_eq!(&bytes[..4], &[0x1a, 0x45, 0xdf, 0xa3]);
        assert!(bytes.windows(7).any(|s| s == b"V_MJPEG"));
        assert!(bytes.windows(8).any(|s| s == 1250f64.to_be_bytes()));
        std::fs::remove_file(path).unwrap();
    }
}
