// Synthetic Windows test-host integration; never capture a real desktop here.
use lume_core::{
    session::{Command, Viewer},
    wire::{Invitation, Quality},
};
use std::{
    thread,
    time::{Duration, Instant},
};
fn main() -> anyhow::Result<()> {
    let path = std::env::args()
        .nth(1)
        .ok_or_else(|| anyhow::anyhow!("Pass the test-only invitation file"))?;
    anyhow::ensure!(
        std::fs::metadata(&path)?.len() <= 65536,
        "Invitation file exceeds its bound"
    );
    let text = std::fs::read_to_string(&path)?;
    let quality = Quality {
        height: 0,
        lossless: true,
        ..Default::default()
    };
    let viewer = if text.starts_with("lume-p2p://") {
        let library = std::path::PathBuf::from(
            std::env::args()
                .nth(2)
                .ok_or_else(|| anyhow::anyhow!("Specify the pinned native library path"))?,
        );
        Viewer::connect_peer(lume_core::signal::Offer::parse(&text)?, library, quality)
    } else {
        Viewer::connect(Invitation::parse(&text)?, quality)
    };
    let mut reply_written = false;
    let started = Instant::now();
    loop {
        let s = viewer.state.lock().unwrap();
        if !reply_written {
            if let Some(reply) = &s.reply {
                let tmp = path.clone() + ".reply.tmp";
                std::fs::write(&tmp, reply)?;
                std::fs::rename(tmp, path.clone() + ".reply")?;
                reply_written = true;
            }
        }
        let exact = s.frame.as_ref().is_some_and(|frame| {
            frame.dimensions() == (640, 360) && frame.get_pixel(20, 20).0 == [12, 30, 60, 255]
        });
        if s.frames >= 2 && exact {
            anyhow::ensure!(
                s.capabilities & 2048 != 0,
                "Portable image negotiation unavailable"
            );
            println!(
                "PASS Rust viewer received exact Windows source pixels: {}x{}, generation {}. Synthetic capture only.",
                s.width, s.height, s.epoch
            );
            break;
        }
        anyhow::ensure!(
            !s.status.starts_with("Connection ended")
                && started.elapsed() < Duration::from_secs(15),
            "Interop failed: {}",
            s.status
        );
        drop(s);
        thread::sleep(Duration::from_millis(10));
    }
    let old = viewer.state.lock().unwrap().frames;
    viewer.command(Command::Quality(Quality {
        height: 180,
        fps: 10,
        jpeg: 65,
        lossless: false,
    }))?;
    loop {
        let s = viewer.state.lock().unwrap();
        if s.frames > old
            && s.frame
                .as_ref()
                .is_some_and(|f| f.dimensions() == (320, 180))
        {
            println!("PASS Live JPEG quality switch delivered the requested 320x180 frame.");
            break;
        }
        anyhow::ensure!(
            started.elapsed() < Duration::from_secs(20),
            "Quality switch timed out: {}",
            s.status
        );
        drop(s);
        thread::sleep(Duration::from_millis(10));
    }
    viewer.command(Command::Quality(Quality {
        height: 0,
        fps: 0,
        jpeg: 100,
        lossless: true,
    }))?;
    let restore_started = Instant::now();
    loop {
        let s = viewer.state.lock().unwrap();
        if s.frame.as_ref().is_some_and(|f| {
            f.dimensions() == (640, 360) && f.get_pixel(20, 20).0 == [12, 30, 60, 255]
        }) {
            println!(
                "PASS Restoring Source recovered original dimensions and exact pixels without reconnecting."
            );
            break;
        }
        anyhow::ensure!(
            restore_started.elapsed() < Duration::from_secs(15),
            "Source restore timed out: {}",
            s.status
        );
        drop(s);
        thread::sleep(Duration::from_millis(10));
    }
    viewer.disconnect();
    Ok(())
}
