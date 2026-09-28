//! Separate synthetic fixture. Never links test approval/capture into the desktop app.
use anyhow::{Result, ensure};
use lume_core::{
    host_store::HostStore,
    paired,
    permanent::PermanentHost,
    session::{Desktop, Viewer},
    wire::Quality,
};
use std::{
    path::PathBuf,
    sync::{
        Arc,
        atomic::{AtomicUsize, Ordering},
    },
    thread,
    time::{Duration, Instant},
};
struct Synthetic(Arc<AtomicUsize>, u8);
impl Desktop for Synthetic {
    fn size(&self) -> (u32, u32, u32) {
        (64, 48, 60)
    }
    fn capture(&mut self) -> Result<image::RgbaImage> {
        self.0.fetch_add(1, Ordering::Relaxed);
        Ok(image::RgbaImage::from_pixel(
            64,
            48,
            image::Rgba([self.1, 120, 190, 255]),
        ))
    }
    fn input(&mut self, _: u8, _: i32, _: i32) -> Result<()> {
        Ok(())
    }
    fn release(&mut self) {}
}
fn wait(mut check: impl FnMut() -> bool) -> Result<()> {
    let start = Instant::now();
    while !check() {
        ensure!(
            start.elapsed() < Duration::from_secs(100),
            "Permanent-host fixture timed out"
        );
        thread::sleep(Duration::from_millis(25));
    }
    Ok(())
}
fn main() -> Result<()> {
    let args: Vec<_> = std::env::args().collect();
    ensure!(
        args.len() == 3 || (args.len() == 4 && args[3] == "--windows-viewer"),
        "Specify native library and a new scratch directory"
    );
    let library = PathBuf::from(&args[1]);
    let root = PathBuf::from(&args[2]);
    ensure!(!root.exists(), "Preserve existing test data");
    let store = Arc::new(HostStore::open(root.join("host.encrypted"), [83; 32])?);
    let code = store.change(|c| {
        c.enabled = true;
        c.control = true;
        c.name = "Synthetic portable host".into();
        c.pairing()
    })?;
    let count = Arc::new(AtomicUsize::new(0));
    let captures = count.clone();
    let mut host = PermanentHost::start(
        store.clone(),
        library.clone(),
        Arc::new(move |_| Ok(Box::new(Synthetic(captures.clone(), 70)))),
    )?;
    wait(|| host.status.lock().unwrap().contains("ready"))?;
    if args.len() == 4 {
        let path = root.join("private.pair");
        std::fs::write(&path, &code)?;
        let result = (|| -> Result<()> {
            wait(|| path.with_extension("paired").exists())?;
            ensure!(
                count.load(Ordering::Acquire) == 0
                    && store.load()?.pending.is_none()
                    && store.load()?.controllers.len() == 1,
                "Windows pairing captured prematurely or was not persisted"
            );
            std::fs::write(path.with_extension("confirmed"), "ok")?;
            wait(|| path.with_extension("revoke").exists())?;
            store.change(|c| {
                c.controllers.clear();
                Ok(())
            })?;
            wait(|| path.with_extension("stop").exists())?;
            ensure!(
                count.load(Ordering::Acquire) > 0,
                "Windows never received a desktop"
            );
            println!(
                "PASS Windows paired with portable host; durable one-time code, approved connections and revocation."
            );
            Ok(())
        })();
        store.change(|c| {
            c.enabled = false;
            Ok(())
        })?;
        host.close_and_wait();
        let _ = std::fs::remove_file(path);
        return result;
    }
    let stop = std::sync::atomic::AtomicBool::new(false);
    let saved = paired::pair(paired::PairingCode::parse(&code)?, &stop)?;
    ensure!(
        count.load(Ordering::Acquire) == 0,
        "Pairing captured before any desktop connection"
    );
    let reopened = HostStore::open(root.join("host.encrypted"), [83; 32])?;
    ensure!(
        reopened.load()?.authorized(&saved) && reopened.load()?.pending.is_none(),
        "Pairing was not durably consumed"
    );
    println!(
        "PASS Portable permanent host: authenticated one-time pairing, encrypted persistence, no capture while pairing."
    );
    let mut viewer = Viewer::open(
        &saved.connection()?,
        &library,
        Quality {
            lossless: true,
            ..Default::default()
        },
    )?;
    wait(|| {
        viewer
            .state
            .lock()
            .unwrap()
            .frame
            .as_ref()
            .is_some_and(|f| f.get_pixel(0, 0).0[0] == 70)
    })?;
    let old_epoch = viewer.state.lock().unwrap().epoch;
    store.change(|c| {
        c.enabled = false;
        Ok(())
    })?;
    wait(|| viewer.state.lock().unwrap().reconnecting)?;
    host.close_and_wait();
    store.change(|c| {
        c.enabled = true;
        Ok(())
    })?;
    let captures = count.clone();
    let mut second = PermanentHost::start(
        store.clone(),
        library,
        Arc::new(move |_| Ok(Box::new(Synthetic(captures.clone(), 95)))),
    )?;
    wait(|| {
        viewer
            .state
            .lock()
            .unwrap()
            .frame
            .as_ref()
            .is_some_and(|f| f.get_pixel(0, 0).0[0] == 95)
    })?;
    ensure!(
        viewer.state.lock().unwrap().epoch > old_epoch,
        "Stale frame generation after recovery"
    );
    println!(
        "PASS Same viewer recovered automatically after host restart with a fresh session and preserved Source quality."
    );
    store.change(|c| {
        c.controllers.clear();
        Ok(())
    })?;
    wait(|| !viewer.state.lock().unwrap().connected)?;
    let before = count.load(Ordering::Acquire);
    thread::sleep(Duration::from_millis(500));
    ensure!(
        count.load(Ordering::Acquire) == before,
        "Revoked controller still captures"
    );
    viewer.close_and_wait();
    second.close_and_wait();
    println!(
        "PASS Live revocation stopped capture; explicit disconnect cancelled recovery; all workers joined."
    );
    Ok(())
}
