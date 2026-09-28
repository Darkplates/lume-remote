//! Synthetic cross-language display tools. Never capture a physical screen.
use anyhow::{Result, ensure};
use image::{Rgba, RgbaImage};
use lume_core::{
    monitors::Monitor,
    session::{Command, Desktop, Host, Viewer},
    wire::{Invitation, Quality},
};
use std::{
    path::Path,
    sync::Arc,
    time::{Duration, Instant},
};
struct Displays(bool);
impl Desktop for Displays {
    fn size(&self) -> (u32, u32, u32) {
        if self.0 {
            (800, 450, 144)
        } else {
            (640, 360, 60)
        }
    }
    fn capture(&mut self) -> Result<RgbaImage> {
        let (w, h, _) = self.size();
        Ok(RgbaImage::from_pixel(
            w,
            h,
            Rgba(if self.0 {
                [0, 128, 128, 255]
            } else {
                [0, 0, 128, 255]
            }),
        ))
    }
    fn input(&mut self, _: u8, _: i32, _: i32) -> Result<()> {
        anyhow::bail!("Synthetic fixture is view-only")
    }
    fn release(&mut self) {}
    fn supports_monitors(&self) -> bool {
        true
    }
    fn monitors(&mut self) -> Result<Vec<Monitor>> {
        Ok(vec![
            Monitor {
                id: "one".into(),
                name: "One".into(),
                x: 0,
                y: 0,
                width: 640,
                height: 360,
                refresh: 60,
                selected: !self.0,
            },
            Monitor {
                id: "two".into(),
                name: "Two".into(),
                x: -800,
                y: -20,
                width: 800,
                height: 450,
                refresh: 144,
                selected: self.0,
            },
        ])
    }
    fn select_monitor(&mut self, id: &str) -> Result<()> {
        ensure!(id == "one" || id == "two", "Display disconnected");
        self.0 = id == "two";
        Ok(())
    }
}
fn wait(viewer: &Viewer, check: impl Fn(&lume_core::session::ViewState) -> bool) -> Result<()> {
    let start = Instant::now();
    loop {
        let state = viewer.state.lock().unwrap();
        if check(&state) {
            return Ok(());
        }
        ensure!(
            !state.status.starts_with("Connection ended")
                && start.elapsed() < Duration::from_secs(15),
            "Display interoperability failed: {}",
            state.status
        );
        drop(state);
        std::thread::sleep(Duration::from_millis(5));
    }
}
fn main() -> Result<()> {
    let args: Vec<_> = std::env::args().collect();
    ensure!(
        args.len() == 3,
        "Usage: monitor_interop host|viewer fresh-invitation-path"
    );
    let path = &args[2];
    if args[1] == "host" {
        ensure!(!Path::new(path).exists(), "Output already exists");
        let mut host = Host::listen(
            "127.0.0.1:0",
            "127.0.0.1",
            false,
            Arc::new(|| Ok(Box::new(Displays(false)))),
        )?;
        std::fs::write(path, &host.code)?;
        let result = (|| -> Result<()> {
            host.requests
                .recv_timeout(Duration::from_secs(30))?
                .answer
                .send(true)?;
            let start = Instant::now();
            while !Path::new(&(path.clone() + ".stop")).exists() {
                ensure!(
                    start.elapsed() < Duration::from_secs(60),
                    "Fixture stop timed out"
                );
                std::thread::sleep(Duration::from_millis(25));
            }
            Ok(())
        })();
        host.close_and_wait();
        std::fs::remove_file(path)?;
        result?;
    } else {
        ensure!(args[1] == "viewer", "Unknown mode");
        ensure!(
            std::fs::metadata(path)?.len() <= 65536,
            "Invitation too large"
        );
        let invite = Invitation::parse(&std::fs::read_to_string(path)?)?;
        let mut viewer = Viewer::connect(
            invite,
            Quality {
                height: 0,
                fps: 0,
                jpeg: 100,
                lossless: true,
            },
        );
        wait(&viewer, |s| s.frame.is_some())?;
        viewer.command(Command::ListMonitors)?;
        wait(&viewer, |s| s.monitors.len() == 2)?;
        {
            let state = viewer.state.lock().unwrap();
            ensure!(
                !state.control && state.monitors[1].x == -800,
                "Metadata/permission changed"
            );
        }
        viewer.command(Command::SelectMonitor("two".into()))?;
        wait(&viewer, |s| {
            !s.input_suspended
                && s.refresh == 144
                && s.frame.as_ref().is_some_and(|f| {
                    f.dimensions() == (800, 450) && f.get_pixel(20, 20).0 == [0, 128, 128, 255]
                })
        })?;
        viewer.command(Command::SelectMonitor("missing".into()))?;
        wait(&viewer, |s| {
            s.monitor_status.contains("Select a display again")
        })?;
        viewer.command(Command::SelectMonitor("one".into()))?;
        wait(&viewer, |s| {
            !s.input_suspended
                && s.refresh == 60
                && s.frame.as_ref().is_some_and(|f| {
                    f.dimensions() == (640, 360) && f.get_pixel(20, 20).0 == [0, 0, 128, 255]
                })
        })?;
        viewer.close_and_wait();
        println!(
            "PASS Rust viewer / Windows host: display list, negative origin, source pixels, epoch changes, missing-display recovery. Synthetic only."
        );
    }
    Ok(())
}
