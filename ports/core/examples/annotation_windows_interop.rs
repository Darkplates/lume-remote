//! Observe real Windows clear receipts after display changes, without an overlay.
use anyhow::{Result, ensure};
use lume_core::{
    session::{Command, Viewer},
    wire::{Invitation, Quality},
};
use std::{
    fs,
    path::Path,
    thread,
    time::{Duration, Instant},
};

fn wait(viewer: &Viewer, check: impl Fn(&lume_core::session::ViewState) -> bool) -> Result<()> {
    let start = Instant::now();
    loop {
        let state = viewer.state.lock().unwrap();
        if check(&state) {
            return Ok(());
        }
        ensure!(
            state.connected && start.elapsed() < Duration::from_secs(15),
            "Windows annotation fixture did not advance: {}",
            state.status
        );
        drop(state);
        thread::sleep(Duration::from_millis(10));
    }
}

fn clear(viewer: &Viewer, receipt: &str, expected: &[i32]) -> Result<()> {
    let generation = viewer.state.lock().unwrap().epoch;
    viewer.command(Command::Annotation(generation, vec![]))?;
    let start = Instant::now();
    loop {
        let lines = fs::read_to_string(receipt)?;
        let rows: Vec<_> = lines.lines().collect();
        if rows.len() >= expected.len() {
            ensure!(
                rows.len() == expected.len(),
                "Unexpected annotation receipt count"
            );
            for (row, epoch) in rows.iter().zip(expected) {
                ensure!(
                    *row == format!("{epoch}\taccepted"),
                    "Windows rejected or relabelled a clear receipt"
                );
            }
            ensure!(
                viewer.state.lock().unwrap().status == "Connected",
                "The viewer retained an annotation error"
            );
            return Ok(());
        }
        ensure!(
            start.elapsed() < Duration::from_secs(15),
            "The actual Windows gate did not return its clear receipt"
        );
        thread::sleep(Duration::from_millis(10));
    }
}

fn main() -> Result<()> {
    let path = std::env::args()
        .nth(1)
        .ok_or_else(|| anyhow::anyhow!("Specify an owned annotation invitation file"))?;
    ensure!(
        fs::metadata(&path)?.len() <= 65536,
        "Invitation file exceeds its bound"
    );
    let invite = Invitation::parse(&fs::read_to_string(&path)?)?;
    let mut viewer = Viewer::connect(
        invite,
        Quality {
            height: 0,
            fps: 0,
            jpeg: 100,
            lossless: true,
        },
    );
    let start = Instant::now();
    while !viewer.state.lock().unwrap().connected {
        ensure!(
            start.elapsed() < Duration::from_secs(15),
            "The owned Windows host did not accept the viewer"
        );
        thread::sleep(Duration::from_millis(10));
    }
    wait(&viewer, |s| s.frame.is_some() && !s.input_suspended)?;
    {
        let state = viewer.state.lock().unwrap();
        ensure!(
            state.control && state.capabilities & lume_core::annotations::CAPABILITY != 0,
            "Annotations were not negotiated"
        );
    }
    let receipts = format!("{path}.receipts");
    ensure!(
        Path::new(&receipts).is_file(),
        "The owned receipt observer is missing"
    );
    clear(&viewer, &receipts, &[1])?;
    let old = viewer.state.lock().unwrap().epoch;
    viewer.command(Command::SelectMonitor("two".into()))?;
    wait(&viewer, |s| {
        !s.input_suspended
            && !s.monitor_pending
            && s.frame
                .as_ref()
                .is_some_and(|frame| frame.dimensions() == (800, 450))
    })?;
    ensure!(
        viewer.state.lock().unwrap().epoch > 2,
        "The fixture did not diverge from the Windows wire epoch"
    );
    ensure!(
        viewer.command(Command::Annotation(old, vec![])).is_err(),
        "The old local generation was accepted"
    );
    clear(&viewer, &receipts, &[1, 2])?;
    let old = viewer.state.lock().unwrap().epoch;
    viewer.command(Command::SelectMonitor("one".into()))?;
    wait(&viewer, |s| {
        !s.input_suspended
            && !s.monitor_pending
            && s.frame
                .as_ref()
                .is_some_and(|frame| frame.dimensions() == (640, 360))
    })?;
    ensure!(
        viewer.state.lock().unwrap().epoch > 3,
        "The restored display did not retain a distinct local generation"
    );
    ensure!(
        viewer.command(Command::Annotation(old, vec![])).is_err(),
        "The previous display generation was accepted"
    );
    clear(&viewer, &receipts, &[1, 2, 3])?;
    viewer.close_and_wait();
    println!(
        "PASS Rust viewer / actual Windows annotation gate: observed clear receipts for remote epochs 1/2/3 after monitor changes; stale local generations rejected. No overlay/input/clipboard."
    );
    Ok(())
}
