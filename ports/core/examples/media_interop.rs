//! Synthetic desktop/audio only. No physical capture, input, speaker or microphone.
use anyhow::{Result, ensure};
use lume_core::{
    media::{Backend, HostEvent},
    session::{Command, Desktop, Host, Viewer},
    wire::Quality,
};
use std::{
    path::Path,
    sync::{
        Arc,
        atomic::{AtomicUsize, Ordering},
    },
    thread,
    time::{Duration, Instant},
};
struct DesktopFixture {
    consent: Arc<AtomicUsize>,
    played: Arc<AtomicUsize>,
    frame: u8,
}
impl Desktop for DesktopFixture {
    fn size(&self) -> (u32, u32, u32) {
        (320, 180, 60)
    }
    fn capture(&mut self) -> Result<image::RgbaImage> {
        self.frame = self.frame.wrapping_add(1);
        Ok(image::RgbaImage::from_pixel(
            320,
            180,
            image::Rgba([self.frame, 128, 30, 255]),
        ))
    }
    fn input(&mut self, _: u8, _: i32, _: i32) -> Result<()> {
        Ok(())
    }
    fn release(&mut self) {}
    fn media(&self) -> Option<Box<dyn Backend>> {
        Some(Box::new(AudioFixture {
            consent: self.consent.clone(),
            played: self.played.clone(),
            audio: false,
            voice: false,
            pending: false,
            last: Instant::now(),
            samples: 0,
        }))
    }
}
struct AudioFixture {
    consent: Arc<AtomicUsize>,
    played: Arc<AtomicUsize>,
    audio: bool,
    voice: bool,
    pending: bool,
    last: Instant,
    samples: usize,
}
impl Backend for AudioFixture {
    fn audio(&mut self, on: bool) -> Result<()> {
        self.audio = on;
        Ok(())
    }
    fn voice(&mut self, on: bool) -> Result<()> {
        self.voice = false;
        self.pending = on;
        Ok(())
    }
    fn play(&mut self, bytes: &[u8]) -> Result<()> {
        ensure!(
            self.voice,
            "Audio reached a microphone session before consent"
        );
        lume_core::media::validate(bytes)?;
        self.played.fetch_add(1, Ordering::SeqCst);
        Ok(())
    }
    fn poll(&mut self) -> Result<Vec<HostEvent>> {
        let mut out = Vec::new();
        if self.pending {
            let value = self.consent.load(Ordering::SeqCst);
            if value > 0 {
                self.pending = false;
                self.voice = value == 1;
                out.push(HostEvent::Consent(self.voice));
            }
        }
        if self.last.elapsed() > Duration::from_millis(20) {
            self.last = Instant::now();
            let mut pcm = Vec::new();
            for i in 0..960 {
                for hz in [440., 880.] {
                    let n = (12000.
                        * ((self.samples + i) as f64 * hz * std::f64::consts::TAU / 48000.).sin())
                        as i16;
                    pcm.extend(n.to_le_bytes());
                }
            }
            self.samples += 960;
            if self.audio {
                out.push(HostEvent::Audio(pcm.clone()));
            }
            if self.voice {
                out.push(HostEvent::Voice(pcm));
            }
        }
        Ok(out)
    }
}
fn wait(mut check: impl FnMut() -> bool, message: &str) -> Result<()> {
    let begin = Instant::now();
    while !check() {
        ensure!(begin.elapsed() < Duration::from_secs(12), "{message}");
        thread::sleep(Duration::from_millis(10));
    }
    Ok(())
}
fn main() -> Result<()> {
    let args: Vec<_> = std::env::args().collect();
    ensure!(args.len() == 2, "Specify a new absolute .mkv output");
    let path = Path::new(&args[1]);
    ensure!(!path.exists(), "Preserve previous evidence");
    let consent = Arc::new(AtomicUsize::new(0));
    let played = Arc::new(AtomicUsize::new(0));
    let c = consent.clone();
    let p = played.clone();
    // System audio is offered only to owner-paired sessions, so this evidence tool
    // runs the host as a paired fixture.
    let mut host = Host::listen_paired_fixture(
        "127.0.0.1:0",
        "127.0.0.1",
        true,
        Arc::new(move || {
            Ok(Box::new(DesktopFixture {
                consent: c.clone(),
                played: p.clone(),
                frame: 0,
            }))
        }),
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
    )?;
    let mut viewer = Viewer::connect(host.invitation.clone(), Quality::default());
    host.requests
        .recv_timeout(Duration::from_secs(10))?
        .answer
        .send(true)?;
    wait(
        || viewer.state.lock().unwrap().frames > 0,
        "No authorized frame",
    )?;
    let media = viewer.state.lock().unwrap().media.clone();
    viewer.command(Command::Audio(true))?;
    wait(|| media.lock().unwrap().audio, "Audio was not enabled")?;
    wait(
        || media.lock().unwrap().take(20).is_some(),
        "No system audio",
    )?;
    viewer.recording.start(viewer.state.clone(), path)?;
    viewer.command(Command::Voice(true))?;
    wait(|| media.lock().unwrap().voice_pending, "No voice request")?;
    ensure!(
        media.lock().unwrap().microphone(1, &[0; 3840]).is_err(),
        "Microphone accepted before consent"
    );
    ensure!(
        played.load(Ordering::SeqCst) == 0,
        "Premature voice playback"
    );
    consent.store(1, Ordering::SeqCst);
    wait(|| media.lock().unwrap().voice, "Voice approval not applied")?;
    let generation = media.lock().unwrap().voice_generation;
    media.lock().unwrap().microphone(generation, &[0; 3840])?;
    wait(
        || played.load(Ordering::SeqCst) > 0,
        "Host did not receive approved voice",
    )?;
    wait(
        || media.lock().unwrap().take(21).is_some(),
        "Host microphone audio was not received",
    )?;
    viewer.command(Command::Voice(false))?;
    wait(|| !media.lock().unwrap().voice, "Microphone did not stop")?;
    ensure!(
        media
            .lock()
            .unwrap()
            .microphone(generation, &[0; 3840])
            .is_err(),
        "Stale microphone generation accepted"
    );
    consent.store(2, Ordering::SeqCst);
    viewer.command(Command::Voice(true))?;
    wait(
        || media.lock().unwrap().status.contains("declined"),
        "Declined voice did not settle",
    )?;
    ensure!(!media.lock().unwrap().voice, "Denied voice was enabled");
    for i in 0..30 {
        viewer.command(Command::Audio(i % 2 == 0))?;
    }
    viewer.command(Command::Audio(true))?;
    wait(
        || media.lock().unwrap().audio,
        "Rapid media changes broke the session",
    )?;
    thread::sleep(Duration::from_secs(3));
    viewer.recording.stop();
    wait(
        || !viewer.recording.status.lock().unwrap().active,
        "Recorder did not finalize",
    )?;
    let r = viewer.recording.status.lock().unwrap();
    ensure!(
        r.frames >= 20 && r.audio_blocks >= 30,
        "Recording has insufficient content"
    );
    println!(
        "PASS TLS media: local host consent, explicit playback, denied/stale microphone, rapid toggles; MKV {} frames / {} stereo blocks.",
        r.frames, r.audio_blocks
    );
    drop(r);
    ensure!(
        viewer.state.lock().unwrap().connected,
        "Media actions disconnected the viewer"
    );
    viewer.close_and_wait();
    host.close_and_wait();
    Ok(())
}
