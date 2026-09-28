//! The existing version-4 Windows display tools, with bounded metadata.
use crate::{
    frame,
    wire::{Packet, Reader},
};
use anyhow::{Result, ensure};
use serde::Serialize;
use std::collections::HashSet;

pub const CAPABILITY: u64 = 2;

#[derive(Clone, Debug, Serialize)]
pub struct Monitor {
    pub id: String,
    pub name: String,
    pub x: i32,
    pub y: i32,
    pub width: u32,
    pub height: u32,
    pub refresh: u32,
    pub selected: bool,
}

impl Monitor {
    pub fn validate(&self) -> Result<()> {
        ensure!(
            !self.id.is_empty() && self.id.len() <= 256 && !self.id.chars().any(char::is_control),
            "Invalid display identifier"
        );
        ensure!(
            !self.name.is_empty() && self.name.len() <= 256,
            "Invalid display name"
        );
        ensure!(
            (1..=32768).contains(&self.width)
                && (1..=32768).contains(&self.height)
                && (1..=1000).contains(&self.refresh),
            "Invalid display geometry"
        );
        ensure!(
            self.x.checked_add(self.width as i32 - 1).is_some()
                && self.y.checked_add(self.height as i32 - 1).is_some(),
            "Display coordinates overflow"
        );
        Ok(())
    }
}

pub fn validate_list(list: &[Monitor]) -> Result<()> {
    ensure!((1..=32).contains(&list.len()), "Invalid display count");
    let mut ids = HashSet::new();
    let mut selected = 0;
    for monitor in list {
        monitor.validate()?;
        ensure!(ids.insert(&monitor.id), "Duplicate display identifier");
        selected += usize::from(monitor.selected);
    }
    ensure!(selected <= 1, "Multiple current displays");
    Ok(())
}

pub fn encode_list(list: &[Monitor]) -> Result<Packet> {
    validate_list(list)?;
    let mut packet = Packet::new(18).int(list.len() as i32);
    for m in list {
        packet = packet
            .text(&m.id)
            .text(&m.name)
            .int(m.x)
            .int(m.y)
            .int(m.width as i32)
            .int(m.height as i32)
            .int(m.refresh as i32)
            .byte(m.selected as u8);
    }
    Ok(packet)
}

pub fn decode_list(body: &[u8]) -> Result<Vec<Monitor>> {
    let mut reader = Reader::new(body);
    ensure!(reader.byte()? == 18, "Invalid display reply");
    let count = reader.int()?;
    ensure!((1..=32).contains(&count), "Invalid display count");
    let mut result = Vec::with_capacity(count as usize);
    for _ in 0..count {
        result.push(Monitor {
            id: reader.text(256)?,
            name: reader.text(256)?,
            x: reader.int()?,
            y: reader.int()?,
            width: reader.int()? as u32,
            height: reader.int()? as u32,
            refresh: reader.int()? as u32,
            selected: reader.boolean()?,
        });
    }
    reader.end()?;
    validate_list(&result)?;
    Ok(result)
}

pub fn decode_selection(body: &[u8]) -> Result<(i32, u32, u32, u32)> {
    let mut reader = Reader::new(body);
    ensure!(reader.byte()? == 18, "Invalid display selection reply");
    let epoch = reader.int()?;
    let (width, height) = frame::dimensions(reader.int()?, reader.int()?)?;
    let refresh = reader.int()?;
    reader.end()?;
    ensure!(
        epoch >= 2 && (1..=1000).contains(&refresh),
        "Invalid selected display generation"
    );
    Ok((epoch, width, height, refresh as u32))
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::session::{Command, Desktop, Host, Viewer};
    use crate::wire::Quality;
    use image::{Rgba, RgbaImage};
    use std::{
        sync::{
            Arc, Mutex,
            atomic::{AtomicBool, AtomicUsize, Ordering},
        },
        time::{Duration, Instant},
    };

    #[derive(Default)]
    struct Log {
        held: bool,
        events: Vec<(usize, i32)>,
        selected: usize,
    }
    struct Displays {
        log: Arc<Mutex<Log>>,
        gate: Arc<AtomicBool>,
        captures: Arc<AtomicUsize>,
    }
    impl Desktop for Displays {
        fn size(&self) -> (u32, u32, u32) {
            if self.log.lock().unwrap().selected == 0 {
                (64, 48, 60)
            } else {
                (80, 60, 180)
            }
        }
        fn capture(&mut self) -> Result<RgbaImage> {
            self.captures.fetch_add(1, Ordering::Relaxed);
            let (w, h, _) = self.size();
            let colour = if w == 64 {
                [20, 80, 140, 255]
            } else {
                [100, 60, 220, 255]
            };
            Ok(RgbaImage::from_pixel(w, h, Rgba(colour)))
        }
        fn input(&mut self, _: u8, key: i32, _: i32) -> Result<()> {
            let mut log = self.log.lock().unwrap();
            let selected = log.selected;
            log.held = true;
            log.events.push((selected, key));
            Ok(())
        }
        fn release(&mut self) {
            self.log.lock().unwrap().held = false;
        }
        fn supports_monitors(&self) -> bool {
            true
        }
        fn monitors(&mut self) -> Result<Vec<Monitor>> {
            let selected = self.log.lock().unwrap().selected;
            Ok((0..2)
                .map(|i| Monitor {
                    id: i.to_string(),
                    name: format!("Display {}", i + 1),
                    x: if i == 0 { 0 } else { -80 },
                    y: -12,
                    width: if i == 0 { 64 } else { 80 },
                    height: if i == 0 { 48 } else { 60 },
                    refresh: if i == 0 { 60 } else { 180 },
                    selected: i == selected,
                })
                .collect())
        }
        fn select_monitor(&mut self, id: &str) -> Result<()> {
            ensure!(id == "0" || id == "1", "Display disappeared");
            let started = Instant::now();
            while !self.gate.load(Ordering::Acquire) {
                ensure!(
                    started.elapsed() < Duration::from_secs(5),
                    "Test selection gate timed out"
                );
                std::thread::sleep(Duration::from_millis(2));
            }
            self.log.lock().unwrap().selected = usize::from(id == "1");
            Ok(())
        }
    }
    fn wait(check: impl Fn() -> bool) {
        let started = Instant::now();
        while !check() {
            assert!(
                started.elapsed() < Duration::from_secs(8),
                "Display condition timed out"
            );
            std::thread::sleep(Duration::from_millis(5));
        }
    }
    struct Unblock(Arc<AtomicBool>);
    impl Drop for Unblock {
        fn drop(&mut self) {
            self.0.store(true, Ordering::Release);
        }
    }
    #[test]
    fn tls_display_switch_releases_keys_and_rejects_old_input() {
        let log = Arc::new(Mutex::new(Log::default()));
        let gate = Arc::new(AtomicBool::new(false));
        let captures = Arc::new(AtomicUsize::new(0));
        let (l, g, c) = (log.clone(), gate.clone(), captures.clone());
        let mut host = Host::listen(
            "127.0.0.1:0",
            "127.0.0.1",
            true,
            Arc::new(move || {
                Ok(Box::new(Displays {
                    log: l.clone(),
                    gate: g.clone(),
                    captures: c.clone(),
                }))
            }),
        )
        .unwrap();
        let mut viewer = Viewer::connect(
            host.invitation.clone(),
            Quality {
                height: 0,
                fps: 0,
                lossless: true,
                ..Default::default()
            },
        );
        let _unblock = Unblock(gate.clone());
        let wakeups = Arc::new(AtomicUsize::new(0));
        let unlocked = Arc::new(AtomicBool::new(true));
        let (wake_count, wake_unlocked, weak_state) = (
            wakeups.clone(),
            unlocked.clone(),
            Arc::downgrade(&viewer.state),
        );
        viewer.set_frame_wakeup(move || {
            wake_count.fetch_add(1, Ordering::Relaxed);
            if let Some(state) = weak_state.upgrade() {
                let mut acquired = false;
                for _ in 0..100 {
                    if state.try_lock().is_ok() {
                        acquired = true;
                        break;
                    }
                    std::thread::sleep(Duration::from_millis(1));
                }
                if !acquired {
                    wake_unlocked.store(false, Ordering::Release);
                }
            }
        });
        let approval = host.requests.recv_timeout(Duration::from_secs(8)).unwrap();
        assert_eq!(captures.load(Ordering::Acquire), 0);
        assert!(viewer.command(Command::ListMonitors).is_err());
        approval.answer.send(true).unwrap();
        wait(|| viewer.state.lock().unwrap().frame.is_some());
        viewer.command(Command::ListMonitors).unwrap();
        wait(|| viewer.state.lock().unwrap().monitors.len() == 2);
        let old_epoch = {
            let state = viewer.state.lock().unwrap();
            assert_eq!((state.monitors[1].x, state.monitors[1].refresh), (-80, 180));
            state.epoch
        };
        viewer.command(Command::Input(4, 65, 0, old_epoch)).unwrap();
        wait(|| log.lock().unwrap().held);
        viewer.command(Command::SelectMonitor("1".into())).unwrap();
        wait(|| {
            let s = viewer.state.lock().unwrap();
            s.input_suspended && s.frame.is_none()
        });
        wait(|| !log.lock().unwrap().held);
        assert!(viewer.command(Command::Input(4, 66, 0, old_epoch)).is_err());
        gate.store(true, Ordering::Release);
        wait(|| {
            let s = viewer.state.lock().unwrap();
            !s.input_suspended
                && s.width == 80
                && s.refresh == 180
                && s.frame
                    .as_ref()
                    .is_some_and(|f| f.get_pixel(4, 4).0 == [100, 60, 220, 255])
        });
        let current_epoch = viewer.state.lock().unwrap().epoch;
        assert!(current_epoch > old_epoch);
        viewer.command(Command::Input(4, 67, 0, old_epoch)).unwrap();
        viewer
            .command(Command::Input(4, 68, 0, current_epoch))
            .unwrap();
        wait(|| log.lock().unwrap().events.len() >= 2);
        assert_eq!(log.lock().unwrap().events, vec![(0, 65), (1, 68)]);
        viewer
            .command(Command::SelectMonitor("missing".into()))
            .unwrap();
        wait(|| {
            viewer
                .state
                .lock()
                .unwrap()
                .monitor_status
                .contains("Select a display again")
        });
        {
            let s = viewer.state.lock().unwrap();
            assert!(s.connected && s.input_suspended);
        }
        assert_eq!(log.lock().unwrap().selected, 1);
        viewer.command(Command::SelectMonitor("0".into())).unwrap();
        wait(|| {
            let s = viewer.state.lock().unwrap();
            !s.input_suspended && s.width == 64 && s.frame.is_some()
        });
        assert!(viewer.state.lock().unwrap().connected);
        viewer.close_and_wait();
        host.close_and_wait();
        assert!(wakeups.load(Ordering::Acquire) >= 2);
        assert!(
            unlocked.load(Ordering::Acquire),
            "Presentation callback held the session lock"
        );
    }
    #[test]
    fn display_metadata_rejects_ambiguous_or_unbounded_layouts() {
        let m = Monitor {
            id: "left".into(),
            name: "Display 2".into(),
            x: -1920,
            y: -120,
            width: 1920,
            height: 1080,
            refresh: 180,
            selected: true,
        };
        let valid = encode_list(&[m.clone()]).unwrap();
        let decoded = decode_list(&valid.0).unwrap();
        assert_eq!((decoded[0].x, decoded[0].refresh), (-1920, 180));
        assert!(decode_list(&valid.0[..valid.0.len() - 1]).is_err());
        assert!(encode_list(&[m.clone(), m.clone()]).is_err());
        let mut bad = m;
        bad.x = i32::MAX;
        assert!(encode_list(&[bad]).is_err());
        assert!(decode_list(&Packet::new(18).int(33).0).is_err());
        assert!(decode_selection(&Packet::new(18).int(1).int(320).int(180).int(60).0).is_err());
    }
}
