//! Owned synthetic host with an explicitly isolated shared directory.
use anyhow::{Result, ensure};
use image::{Rgba, RgbaImage};
use lume_core::session::{Desktop, Host};
use std::{
    fs,
    path::PathBuf,
    sync::Arc,
    thread,
    time::{Duration, Instant},
};
struct Synthetic {
    root: PathBuf,
}
impl Desktop for Synthetic {
    fn size(&self) -> (u32, u32, u32) {
        (64, 48, 60)
    }
    fn capture(&mut self) -> Result<RgbaImage> {
        Ok(RgbaImage::from_pixel(64, 48, Rgba([70, 120, 190, 255])))
    }
    fn input(&mut self, _: u8, _: i32, _: i32) -> Result<()> {
        Ok(())
    }
    fn release(&mut self) {}
    fn shared_folder(&self) -> Option<PathBuf> {
        Some(self.root.clone())
    }
}
fn main() -> Result<()> {
    let args: Vec<_> = std::env::args().collect();
    ensure!(
        args.len() >= 3,
        "Specify private invitation and new isolated root"
    );
    let invite = PathBuf::from(&args[1]);
    let root = PathBuf::from(&args[2]);
    ensure!(
        !invite.exists() && !root.exists() && root.is_absolute(),
        "Fixture destinations must be new and absolute"
    );
    fs::create_dir(&root)?;
    fs::write(
        root.join("payload.bin"),
        (0..1048699usize)
            .map(|i| (i * 31 + 7) as u8)
            .collect::<Vec<_>>(),
    )?;
    let shared = root.clone();
    let bind = args.get(3).map(String::as_str).unwrap_or("127.0.0.1:0");
    let mut host = Host::listen(
        bind,
        "127.0.0.1",
        true,
        Arc::new(move || {
            Ok(Box::new(Synthetic {
                root: shared.clone(),
            }))
        }),
    )?;
    fs::write(&invite, &host.code)?;
    let start = Instant::now();
    while !PathBuf::from(format!("{}.stop", invite.display())).exists()
        && start.elapsed() < Duration::from_secs(180)
    {
        if let Ok(request) = host.requests.try_recv() {
            request.answer.send(true)?;
        }
        thread::sleep(Duration::from_millis(10));
    }
    host.close_and_wait();
    fs::remove_file(invite)?;
    println!("PASS Synthetic portable host stopped; no actual desktop capture or input.");
    Ok(())
}
