//! Bounded Windows-compatible PCM transport. Microphones require platform consent.
use crate::wire::{Packet, Reader};
use anyhow::{Result, ensure};
use flate2::{Compression, read::DeflateDecoder, write::DeflateEncoder};
use serde::Serialize;
use std::{
    collections::{HashMap, VecDeque},
    io::{Read, Write},
    sync::{Arc, Mutex},
    time::{Duration, Instant},
};
pub const MAX_PCM: usize = 19200;
pub const AUDIO: u64 = 32;
pub const VOICE: u64 = 1024;
#[derive(Clone)]
pub struct Pcm {
    pub received: Instant,
    pub kind: u8,
    pub generation: i32,
    pub bytes: Vec<u8>,
}
pub fn validate(bytes: &[u8]) -> Result<()> {
    ensure!(
        (4..=MAX_PCM).contains(&bytes.len()) && bytes.len() % 4 == 0,
        "Invalid stereo 48 kHz PCM16 block"
    );
    Ok(())
}
pub fn packet(kind: u8, generation: i32, bytes: &[u8]) -> Result<Packet> {
    ensure!(
        matches!(kind, 20 | 21) && generation > 0,
        "Invalid audio channel or generation"
    );
    validate(bytes)?;
    let mut encoder = DeflateEncoder::new(Vec::new(), Compression::fast());
    encoder.write_all(bytes)?;
    let compressed = encoder.finish()?;
    ensure!(
        compressed.len() <= MAX_PCM + 252,
        "Audio packet exceeds its bound"
    );
    let mut p = Packet::new(kind)
        .int(generation)
        .int((compressed.len() + 4) as i32)
        .int(bytes.len() as i32);
    p.0.extend(compressed);
    Ok(p)
}
pub fn decode(packet: &Packet) -> Result<Pcm> {
    ensure!(
        matches!(packet.0.first(), Some(20 | 21)),
        "Invalid audio channel"
    );
    let mut r = Reader::new(&packet.0[1..]);
    let generation = r.int()?;
    let size = r.int()?;
    ensure!(
        generation > 0 && (5..=(MAX_PCM + 256) as i32).contains(&size),
        "Invalid audio envelope"
    );
    let block = r.take(size as usize)?;
    r.end()?;
    let declared = i32::from_le_bytes(block[..4].try_into()?);
    ensure!(
        (4..=MAX_PCM as i32).contains(&declared) && declared % 4 == 0,
        "Invalid PCM length"
    );
    let mut decoder = DeflateDecoder::new(&block[4..]);
    let mut bytes = Vec::with_capacity(declared as usize);
    (&mut decoder)
        .take((declared + 1) as u64)
        .read_to_end(&mut bytes)?;
    ensure!(
        bytes.len() == declared as usize && decoder.total_in() as usize == block.len() - 4,
        "Audio expansion or payload length mismatch"
    );
    Ok(Pcm {
        received: Instant::now(),
        kind: packet.0[0],
        generation,
        bytes,
    })
}
#[derive(Default, Serialize)]
pub struct MediaState {
    pub audio: bool,
    pub voice: bool,
    pub audio_pending: bool,
    pub voice_pending: bool,
    pub audio_generation: i32,
    pub voice_generation: i32,
    pub status: String,
    pub dropped: u64,
    #[serde(skip)]
    audio_queue: VecDeque<Pcm>,
    #[serde(skip)]
    voice_queue: VecDeque<Pcm>,
    #[serde(skip)]
    microphone: VecDeque<Pcm>,
    #[serde(skip)]
    pub(crate) record_audio: Option<std::sync::mpsc::SyncSender<Pcm>>,
    #[serde(skip)]
    pub stop_requested: bool,
}
impl MediaState {
    pub fn reset(&mut self) {
        self.audio = false;
        self.voice = false;
        self.audio_pending = false;
        self.voice_pending = false;
        self.audio_generation = self.audio_generation.checked_add(1).unwrap_or(1);
        self.voice_generation = self.voice_generation.checked_add(1).unwrap_or(1);
        self.audio_queue.clear();
        self.voice_queue.clear();
        self.microphone.clear();
    }
    pub fn take(&mut self, kind: u8) -> Option<Pcm> {
        if kind == 20 && self.audio {
            self.audio_queue.pop_front()
        } else if kind == 21 && self.voice {
            self.voice_queue.pop_front()
        } else {
            None
        }
    }
    pub fn microphone(&mut self, generation: i32, bytes: &[u8]) -> Result<()> {
        validate(bytes)?;
        ensure!(
            self.voice && generation == self.voice_generation,
            "Microphone is not enabled for this call"
        );
        if self.microphone.len() >= 4 {
            self.microphone.pop_front();
            self.dropped += 1;
        }
        self.microphone.push_back(Pcm {
            received: Instant::now(),
            kind: 21,
            generation,
            bytes: bytes.into(),
        });
        Ok(())
    }
    pub fn outgoing(&mut self) -> Option<Pcm> {
        self.microphone
            .pop_front()
            .filter(|p| self.voice && p.generation == self.voice_generation)
    }
    fn push(&mut self, p: Pcm) {
        if p.kind == 20 && self.audio && p.generation == self.audio_generation {
            if let Some(tx) = &self.record_audio {
                let _ = tx.try_send(p.clone());
            }
        }
        let q = if p.kind == 20 {
            if !self.audio || p.generation != self.audio_generation {
                return;
            }
            &mut self.audio_queue
        } else {
            if !self.voice || p.generation != self.voice_generation {
                return;
            }
            &mut self.voice_queue
        };
        if q.len() >= 4 {
            q.pop_front();
            self.dropped += 1;
        }
        q.push_back(p);
    }
}
pub struct ViewerMedia {
    pub state: Arc<Mutex<MediaState>>,
    pending: HashMap<i64, (u8, i32, bool, Instant)>,
}
impl ViewerMedia {
    pub fn new(state: Arc<Mutex<MediaState>>) -> Self {
        Self {
            state,
            pending: HashMap::new(),
        }
    }
    pub fn request(&mut self, kind: u8, enabled: bool, id: i64) -> Result<Packet> {
        ensure!(matches!(kind, 20 | 21), "Invalid media request");
        self.pending.retain(|_, v| v.0 != kind);
        let mut s = self.state.lock().unwrap();
        let generation = if kind == 20 {
            s.audio = false;
            s.audio_pending = enabled;
            s.audio_queue.clear();
            s.audio_generation = s.audio_generation.checked_add(1).unwrap_or(1);
            s.audio_generation
        } else {
            s.voice = false;
            s.voice_pending = enabled;
            s.voice_queue.clear();
            s.microphone.clear();
            s.voice_generation = s.voice_generation.checked_add(1).unwrap_or(1);
            s.voice_generation
        };
        s.status = if enabled && kind == 21 {
            "Waiting for host microphone consent…".into()
        } else {
            String::new()
        };
        self.pending
            .insert(id, (kind, generation, enabled, Instant::now()));
        Ok(Packet::new(18)
            .long(id)
            .byte(if kind == 20 { 6 } else { 9 })
            .byte(enabled as u8)
            .int(generation))
    }
    pub fn reply(&mut self, id: i64, ok: bool, error: &str, body: &[u8]) -> Result<bool> {
        let Some((kind, generation, enabled, _)) = self.pending.remove(&id) else {
            return Ok(false);
        };
        ensure!(body == [18], "Invalid media reply");
        let mut s = self.state.lock().unwrap();
        if kind == 20 && generation == s.audio_generation {
            s.audio = ok && enabled;
            s.audio_pending = false;
        } else if kind == 21 && generation == s.voice_generation {
            s.voice = ok && enabled;
            s.voice_pending = false;
        } else {
            return Ok(true);
        }
        s.status = if ok { String::new() } else { error.into() };
        Ok(true)
    }
    pub fn receive(&mut self, p: &Packet) -> Result<()> {
        let pcm = decode(p)?;
        self.state.lock().unwrap().push(pcm);
        Ok(())
    }
    pub fn ended(&mut self, generation: i32) -> Result<()> {
        ensure!(generation > 0, "Invalid voice generation");
        let mut s = self.state.lock().unwrap();
        if generation == s.voice_generation {
            s.voice = false;
            s.voice_pending = false;
            s.voice_queue.clear();
            s.microphone.clear();
            s.status = "Voice call ended. Microphone off.".into();
        }
        Ok(())
    }
    pub fn expire(&mut self) -> Vec<Packet> {
        let expired: Vec<_> = self
            .pending
            .iter()
            .filter(|(_, v)| v.3.elapsed() > Duration::from_secs(75))
            .map(|(id, _)| *id)
            .collect();
        let mut result = Vec::new();
        for id in expired {
            if let Some((kind, generation, _, _)) = self.pending.remove(&id) {
                let mut s = self.state.lock().unwrap();
                if kind == 20 && s.audio_generation == generation {
                    s.audio = false;
                    s.audio_pending = false;
                }
                if kind == 21 && s.voice_generation == generation {
                    s.voice = false;
                    s.voice_pending = false;
                    s.microphone.clear();
                }
                s.status = "Media request timed out. You can retry.".into();
                result.push(
                    Packet::new(18)
                        .long(id)
                        .byte(if kind == 20 { 6 } else { 9 })
                        .byte(0)
                        .int(generation),
                );
            }
        }
        result
    }
}
impl Drop for ViewerMedia {
    fn drop(&mut self) {
        self.state.lock().unwrap().reset();
    }
}

pub trait Capture: Send {
    fn poll(&mut self) -> Result<Option<Vec<u8>>>;
}

pub enum HostEvent {
    Consent(bool),
    Audio(Vec<u8>),
    Voice(Vec<u8>),
    VoiceEnded,
}
/// Platform work must be bounded and non-blocking. Capture starts only after consent.
pub trait Backend: Send {
    fn audio(&mut self, enabled: bool) -> Result<()>;
    fn voice(&mut self, enabled: bool) -> Result<()>;
    fn poll(&mut self) -> Result<Vec<HostEvent>>;
    fn play(&mut self, pcm: &[u8]) -> Result<()>;
}
pub struct HostMedia {
    ended: Option<i32>,
    backend: Option<Box<dyn Backend>>,
    audio: Option<i32>,
    voice: Option<i32>,
    pending: Option<(i64, i32, Instant)>,
}
fn reply(id: i64, ok: bool, error: &str) -> Packet {
    Packet::new(19)
        .long(id)
        .byte(ok as u8)
        .text(error)
        .int(1)
        .byte(18)
}
impl HostMedia {
    pub fn new(backend: Option<Box<dyn Backend>>) -> Self {
        Self {
            ended: None,
            backend,
            audio: None,
            voice: None,
            pending: None,
        }
    }
    pub fn capabilities(&self) -> u64 {
        if self.backend.is_some() {
            AUDIO | VOICE
        } else {
            0
        }
    }
    pub fn request(
        &mut self,
        id: i64,
        tool: u8,
        enabled: bool,
        generation: i32,
    ) -> Result<Vec<Packet>> {
        ensure!(
            matches!(tool, 6 | 9) && generation > 0,
            "Invalid media request"
        );
        let Some(b) = self.backend.as_mut() else {
            return Ok(vec![reply(id, false, "Media is unavailable on this host")]);
        };
        if tool == 6 {
            self.audio = None;
            let result = b.audio(enabled);
            if result.is_ok() && enabled {
                self.audio = Some(generation);
            }
            return Ok(vec![reply(
                id,
                result.is_ok(),
                if result.is_ok() {
                    ""
                } else {
                    "System audio is unavailable. Check the local audio service or permission."
                },
            )]);
        }
        let mut out = Vec::new();
        if let Some((old, _, _)) = self.pending.take() {
            out.push(reply(old, false, "Voice request cancelled"));
        }
        self.voice = None;
        b.voice(false)?;
        if !enabled {
            out.push(reply(id, true, ""));
        } else if b.voice(true).is_ok() {
            self.pending = Some((id, generation, Instant::now()));
        } else {
            out.push(reply(
                id,
                false,
                "Voice is unavailable. Check local microphone permission.",
            ));
        }
        Ok(out)
    }
    pub fn receive(&mut self, p: &Packet) -> Result<()> {
        let pcm = decode(p)?;
        ensure!(
            pcm.kind == 21,
            "Only microphone audio is accepted by a host"
        );
        if self.voice == Some(pcm.generation) {
            if let Some(b) = self.backend.as_mut() {
                if b.play(&pcm.bytes).is_err() {
                    let _ = b.voice(false);
                    self.ended = self.voice.take();
                }
            }
        }
        Ok(())
    }
    pub fn poll(&mut self) -> Result<Vec<Packet>> {
        let mut out = Vec::new();
        if let Some(g) = self.ended.take() {
            out.push(Packet::new(22).int(g));
        }
        let Some(b) = self.backend.as_mut() else {
            return Ok(out);
        };
        if self
            .pending
            .is_some_and(|p| p.2.elapsed() > Duration::from_secs(60))
        {
            let (id, _, _) = self.pending.take().unwrap();
            b.voice(false)?;
            out.push(reply(id, false, "Local microphone consent timed out"));
        }
        let events = match b.poll() {
            Ok(e) => e,
            Err(_) => {
                let _ = b.audio(false);
                let _ = b.voice(false);
                self.audio = None;
                if let Some((id, _, _)) = self.pending.take() {
                    out.push(reply(id, false, "Audio device is unavailable"));
                }
                if let Some(g) = self.voice.take() {
                    out.push(Packet::new(22).int(g));
                }
                return Ok(out);
            }
        };
        ensure!(
            events.len() <= 12,
            "Media backend exceeded its event budget"
        );
        for event in events {
            match event {
                HostEvent::Consent(allowed) => {
                    if let Some((id, g, _)) = self.pending.take() {
                        if allowed {
                            self.voice = Some(g);
                        }
                        out.push(reply(
                            id,
                            allowed,
                            if allowed {
                                ""
                            } else {
                                "The host declined microphone access"
                            },
                        ));
                    }
                }
                HostEvent::Audio(pcm) => {
                    if let Some(g) = self.audio {
                        out.push(packet(20, g, &pcm)?);
                    }
                }
                HostEvent::Voice(pcm) => {
                    if let Some(g) = self.voice {
                        out.push(packet(21, g, &pcm)?);
                    }
                }
                HostEvent::VoiceEnded => {
                    if let Some((id, _, _)) = self.pending.take() {
                        out.push(reply(id, false, "Voice request closed"));
                    }
                    if let Some(g) = self.voice.take() {
                        out.push(Packet::new(22).int(g));
                    }
                }
            }
        }
        Ok(out)
    }
}
impl Drop for HostMedia {
    fn drop(&mut self) {
        if let Some(b) = self.backend.as_mut() {
            let _ = b.audio(false);
            let _ = b.voice(false);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn pcm_round_trip_and_strict_expansion() {
        let bytes: Vec<u8> = (0..MAX_PCM).map(|n| (n % 251) as u8).collect();
        let p = packet(20, 4, &bytes).unwrap();
        assert_eq!(decode(&p).unwrap().bytes, bytes);
        let mut malformed = p;
        malformed.0[9..13].copy_from_slice(&4i32.to_le_bytes());
        assert!(decode(&malformed).is_err());
        assert!(packet(21, 0, &[0; 4]).is_err());
    }
    #[test]
    fn permission_generation_and_bounded_playback() {
        let state = Arc::new(Mutex::new(MediaState::default()));
        let mut m = ViewerMedia::new(state.clone());
        assert!(state.lock().unwrap().microphone(1, &[0; 4]).is_err());
        m.request(21, true, 1).unwrap();
        m.receive(&packet(21, 1, &[0; 4]).unwrap()).unwrap();
        assert!(state.lock().unwrap().take(21).is_none());
        m.reply(1, true, "", &[18]).unwrap();
        for _ in 0..12 {
            m.receive(&packet(21, 1, &[0; 4]).unwrap()).unwrap();
        }
        assert_eq!(state.lock().unwrap().voice_queue.len(), 4);
        m.request(21, false, 2).unwrap();
        m.ended(1).unwrap();
        assert!(!state.lock().unwrap().voice);
        assert!(state.lock().unwrap().microphone(1, &[0; 4]).is_err());
        drop(m);
        assert!(state.lock().unwrap().take(21).is_none());
    }
}
