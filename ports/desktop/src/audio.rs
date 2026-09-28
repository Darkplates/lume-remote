//! Owned, bounded PCM processes. No shell, global audio settings, or device work on the UI thread.
use anyhow::{Result, ensure};
use lume_core::media::{Backend, HostEvent, MediaState};
use std::{
    io::{BufRead, BufReader, Read, Write},
    process::{Child, Command, Stdio},
    sync::{
        Arc, Mutex,
        atomic::{AtomicBool, Ordering},
        mpsc::{self, Receiver, SyncSender},
    },
    thread,
    time::Duration,
};

pub fn available() -> bool {
    #[cfg(target_os = "linux")]
    {
        return std::path::Path::new("/usr/bin/parec").is_file()
            && std::path::Path::new("/usr/bin/pacat").is_file();
    }
    #[cfg(target_os = "macos")]
    {
        return helper().is_ok_and(|p| p.is_file());
    }
    #[allow(unreachable_code)]
    false
}
#[cfg(target_os = "macos")]
fn helper() -> Result<std::path::PathBuf> {
    Ok(std::env::current_exe()?
        .parent()
        .ok_or_else(|| anyhow::anyhow!("App path unavailable"))?
        .join("lume-audio"))
}
fn command(mode: &str) -> Result<Command> {
    #[cfg(target_os = "linux")]
    {
        let mut c = Command::new(if mode == "play" {
            "/usr/bin/pacat"
        } else {
            "/usr/bin/parec"
        });
        c.args([
            "--raw",
            "--format=s16le",
            "--rate=48000",
            "--channels=2",
            "--latency-msec=40",
            "--client-name=Lume",
        ]);
        if mode == "system" {
            c.arg("--device=@DEFAULT_MONITOR@");
        }
        return Ok(c);
    }
    #[cfg(target_os = "macos")]
    {
        let mut c = Command::new(helper()?);
        c.arg(mode);
        return Ok(c);
    }
    #[allow(unreachable_code)]
    {
        let _ = mode;
        anyhow::bail!("Use the native Windows app for audio")
    }
}
struct Capture {
    child: Child,
    rx: Receiver<Vec<u8>>,
    worker: Option<thread::JoinHandle<()>>,
}
impl Capture {
    fn start(mode: &str) -> Result<Self> {
        let mut child = command(mode)?
            .stdin(Stdio::null())
            .stdout(Stdio::piped())
            .stderr(Stdio::null())
            .spawn()?;
        let mut output = child.stdout.take().unwrap();
        let (tx, rx) = mpsc::sync_channel(4);
        let worker = thread::spawn(move || {
            loop {
                let mut pcm = vec![0; 3840];
                if output.read_exact(&mut pcm).is_err() {
                    break;
                }
                match tx.try_send(pcm) {
                    Ok(()) | Err(mpsc::TrySendError::Full(_)) => {}
                    Err(_) => break,
                }
            }
        });
        Ok(Self {
            child,
            rx,
            worker: Some(worker),
        })
    }
    fn poll(&mut self) -> Result<Option<Vec<u8>>> {
        match self.rx.try_recv() {
            Ok(p) => Ok(Some(p)),
            Err(mpsc::TryRecvError::Empty) => Ok(None),
            Err(_) => anyhow::bail!("Audio capture ended"),
        }
    }
}
impl Drop for Capture {
    fn drop(&mut self) {
        let _ = self.child.kill();
        let _ = self.child.wait();
        if let Some(w) = self.worker.take() {
            let _ = w.join();
        }
    }
}
struct Playback {
    child: Child,
    tx: Option<SyncSender<Vec<u8>>>,
    worker: Option<thread::JoinHandle<()>>,
}
impl Playback {
    fn start() -> Result<Self> {
        let mut child = command("play")?
            .stdin(Stdio::piped())
            .stdout(Stdio::null())
            .stderr(Stdio::null())
            .spawn()?;
        let mut input = child.stdin.take().unwrap();
        let (tx, rx) = mpsc::sync_channel::<Vec<u8>>(4);
        let worker = thread::spawn(move || {
            while let Ok(pcm) = rx.recv() {
                if input.write_all(&pcm).is_err() {
                    break;
                }
            }
        });
        Ok(Self {
            child,
            tx: Some(tx),
            worker: Some(worker),
        })
    }
    fn push(&mut self, bytes: Vec<u8>) -> Result<()> {
        ensure!(self.child.try_wait()?.is_none(), "Audio playback ended");
        match self.tx.as_ref().unwrap().try_send(bytes) {
            Ok(()) | Err(mpsc::TrySendError::Full(_)) => Ok(()),
            Err(_) => anyhow::bail!("Audio playback ended"),
        }
    }
}
impl Drop for Playback {
    fn drop(&mut self) {
        self.tx.take();
        let _ = self.child.kill();
        let _ = self.child.wait();
        if let Some(w) = self.worker.take() {
            let _ = w.join();
        }
    }
}
struct Consent {
    child: Child,
    rx: Receiver<bool>,
    worker: Option<thread::JoinHandle<()>>,
}
impl Consent {
    fn start() -> Result<Self> {
        let mut child = Command::new(std::env::current_exe()?)
            .arg("--microphone-consent")
            .stdin(Stdio::null())
            .stdout(Stdio::piped())
            .stderr(Stdio::null())
            .spawn()?;
        let output = child.stdout.take().unwrap();
        let (tx, rx) = mpsc::sync_channel(2);
        let worker = thread::spawn(move || {
            let mut reader = BufReader::new(output).take(64);
            let mut line = String::new();
            while reader.read_line(&mut line).is_ok_and(|n| n > 0) {
                if tx.try_send(line.trim() == "allow").is_err() {
                    break;
                }
                line.clear();
            }
        });
        Ok(Self {
            child,
            rx,
            worker: Some(worker),
        })
    }
}
impl Drop for Consent {
    fn drop(&mut self) {
        let _ = self.child.kill();
        let _ = self.child.wait();
        if let Some(w) = self.worker.take() {
            let _ = w.join();
        }
    }
}
#[derive(Default)]
pub struct HostAudio {
    system: Option<Capture>,
    microphone: Option<Capture>,
    speaker: Option<Playback>,
    consent: Option<Consent>,
}
impl Backend for HostAudio {
    fn audio(&mut self, on: bool) -> Result<()> {
        self.system = None;
        if on {
            self.system = Some(Capture::start("system")?);
        }
        Ok(())
    }
    fn voice(&mut self, on: bool) -> Result<()> {
        self.microphone = None;
        self.speaker = None;
        self.consent = None;
        if on {
            self.consent = Some(Consent::start()?);
        }
        Ok(())
    }
    fn play(&mut self, pcm: &[u8]) -> Result<()> {
        if let Some(s) = self.speaker.as_mut() {
            s.push(pcm.into())?;
        }
        Ok(())
    }
    fn poll(&mut self) -> Result<Vec<HostEvent>> {
        let mut events = Vec::new();
        let consent = self
            .consent
            .as_mut()
            .map(|c| match c.rx.try_recv() {
                Ok(true) => 1,
                Ok(false) | Err(mpsc::TryRecvError::Disconnected) => 2,
                Err(_) => {
                    if c.child.try_wait().ok().flatten().is_some() {
                        2
                    } else {
                        0
                    }
                }
            })
            .unwrap_or(0);
        if consent == 1 && self.microphone.is_none() {
            match (Capture::start("microphone"), Playback::start()) {
                (Ok(m), Ok(p)) => {
                    self.microphone = Some(m);
                    self.speaker = Some(p);
                    events.push(HostEvent::Consent(true));
                }
                _ => {
                    self.voice(false)?;
                    events.push(HostEvent::Consent(false));
                }
            }
        } else if consent == 2 {
            self.voice(false)?;
            events.push(HostEvent::VoiceEnded);
        }
        if let Some(c) = self.system.as_mut() {
            for _ in 0..4 {
                if let Some(p) = c.poll()? {
                    events.push(HostEvent::Audio(p));
                } else {
                    break;
                }
            }
        }
        if let Some(c) = self.microphone.as_mut() {
            for _ in 0..4 {
                if let Some(p) = c.poll()? {
                    events.push(HostEvent::Voice(p));
                } else {
                    break;
                }
            }
        }
        Ok(events)
    }
}

pub struct Player {
    stop: Arc<AtomicBool>,
    worker: Option<thread::JoinHandle<()>>,
}
impl Player {
    pub fn new(state: Arc<Mutex<MediaState>>) -> Self {
        let stop = Arc::new(AtomicBool::new(false));
        let cancelled = stop.clone();
        let worker = thread::spawn(move || {
            let (mut audio, mut voice, mut mic): (
                Option<Playback>,
                Option<Playback>,
                Option<Capture>,
            ) = (None, None, None);
            let (mut audio_generation, mut voice_generation) = (0, 0);
            while !cancelled.load(Ordering::Acquire) {
                let (a, v, ag, vg) = {
                    let s = state.lock().unwrap();
                    (s.audio, s.voice, s.audio_generation, s.voice_generation)
                };
                if !a || ag != audio_generation {
                    audio = None;
                }
                if !v || vg != voice_generation {
                    voice = None;
                    mic = None;
                }
                audio_generation = ag;
                voice_generation = vg;
                let work = (|| -> Result<()> {
                    if a && audio.is_none() {
                        audio = Some(Playback::start()?);
                    }
                    if v && voice.is_none() {
                        voice = Some(Playback::start()?);
                        mic = Some(Capture::start("microphone")?);
                    }
                    for (kind, device) in [(20, &mut audio), (21, &mut voice)] {
                        if let Some(device) = device {
                            for _ in 0..4 {
                                let p = state.lock().unwrap().take(kind);
                                if let Some(p) = p {
                                    device.push(p.bytes)?;
                                } else {
                                    break;
                                }
                            }
                        }
                    }
                    if let Some(c) = mic.as_mut() {
                        for _ in 0..4 {
                            if let Some(p) = c.poll()? {
                                let _ = state.lock().unwrap().microphone(vg, &p);
                            } else {
                                break;
                            }
                        }
                    }
                    Ok(())
                })();
                if work.is_err() {
                    let mut s = state.lock().unwrap();
                    s.reset();
                    s.stop_requested = true;
                    s.status="Audio device unavailable. Check local permissions and audio service, then retry.".into();
                }
                thread::sleep(Duration::from_millis(10));
            }
        });
        Self {
            stop,
            worker: Some(worker),
        }
    }
}
impl Drop for Player {
    fn drop(&mut self) {
        self.stop.store(true, Ordering::Release);
        if let Some(w) = self.worker.take() {
            let _ = w.join();
        }
    }
}

pub fn consent_window() -> eframe::Result {
    struct Dialog {
        allowed: bool,
    }
    impl eframe::App for Dialog {
        fn update(&mut self, ctx: &eframe::egui::Context, _: &mut eframe::Frame) {
            use eframe::egui;
            egui::CentralPanel::default().show(ctx,|ui|{ui.heading(if self.allowed{"Microphone shared with your remote session"}else{"Allow a voice call?"});
            ui.label("Your remote controller can hear your microphone after you allow it. Closing this window stops the microphone.");
            if !self.allowed&&ui.button("Allow microphone for this call").clicked(){self.allowed=true;println!("allow");let _=std::io::stdout().flush();}
            if ui.button(if self.allowed{"Stop microphone"}else{"Decline"}).clicked(){ctx.send_viewport_cmd(egui::ViewportCommand::Close);}
        });
            ctx.request_repaint_after(Duration::from_millis(200));
        }
    }
    eframe::run_native(
        "Lume · microphone consent",
        eframe::NativeOptions {
            viewport: eframe::egui::ViewportBuilder::default()
                .with_inner_size([480., 180.])
                .with_always_on_top(),
            ..Default::default()
        },
        Box::new(|_| Ok(Box::new(Dialog { allowed: false }))),
    )
}

#[cfg(all(test, target_os = "linux"))]
mod tests {
    use super::*;
    #[test]
    #[ignore = "Requires an isolated PulseAudio null sink and LUME_TEST_AUDIO_NULL=1"]
    fn null_sink_capture_playback_and_cleanup() {
        assert_eq!(std::env::var("LUME_TEST_AUDIO_NULL").as_deref(), Ok("1"));
        assert_eq!(std::env::var("PULSE_SINK").as_deref(), Ok("lume_test"));
        let mut capture = Capture::start("system").unwrap();
        let mut speaker = Playback::start().unwrap();
        let begin = std::time::Instant::now();
        let mut nonzero = 0;
        let mut samples = 0;
        while begin.elapsed() < Duration::from_secs(8) && nonzero < 5 {
            let mut pcm = Vec::new();
            for i in 0..960 {
                let n = (12000.
                    * ((samples + i) as f64 * 440. * std::f64::consts::TAU / 48000.).sin())
                    as i16;
                pcm.extend(n.to_le_bytes());
                pcm.extend(n.to_le_bytes());
            }
            samples += 960;
            speaker.push(pcm).unwrap();
            for _ in 0..4 {
                if let Some(p) = capture.poll().unwrap() {
                    lume_core::media::validate(&p).unwrap();
                    if p.iter().any(|v| *v != 0) {
                        nonzero += 1;
                    }
                }
            }
            thread::sleep(Duration::from_millis(20));
        }
        assert!(
            nonzero >= 5,
            "No synthetic system audio reached the virtual monitor"
        );
        let end = std::time::Instant::now();
        drop(capture);
        drop(speaker);
        assert!(
            end.elapsed() < Duration::from_secs(2),
            "Audio children did not join"
        );
    }
}
