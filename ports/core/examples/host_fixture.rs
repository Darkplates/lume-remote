// Explicit synthetic integration helper. Excluded from the production app.
use image::{Rgba, RgbaImage};
use lume_core::session::{Desktop, Host};
use std::{
    path::Path,
    sync::Arc,
    time::{Duration, Instant},
};
struct Synthetic;
impl Desktop for Synthetic {
    fn size(&self) -> (u32, u32, u32) {
        (64, 48, 60)
    }
    fn capture(&mut self) -> anyhow::Result<RgbaImage> {
        Ok(RgbaImage::from_pixel(64, 48, Rgba([70, 120, 190, 255])))
    }
    fn input(&mut self, _: u8, _: i32, _: i32) -> anyhow::Result<()> {
        anyhow::bail!("Input must not be invoked in this view-only fixture")
    }
    fn release(&mut self) {}
}
fn main() -> anyhow::Result<()> {
    let path = std::env::args()
        .nth(1)
        .ok_or_else(|| anyhow::anyhow!("Specify a fresh fixture output path"))?;
    anyhow::ensure!(!Path::new(&path).exists(), "Output already exists");
    let host = Host::listen(
        "127.0.0.1:0",
        "127.0.0.1",
        false,
        Arc::new(|| Ok(Box::new(Synthetic))),
    )?;
    std::fs::write(&path, &host.code)?;
    let result = (|| -> anyhow::Result<()> {
        let started = Instant::now();
        let request = loop {
            match host.requests.recv_timeout(Duration::from_millis(50)) {
                Ok(request) => break request,
                Err(_) => {
                    let status = host.status.lock().unwrap().clone();
                    anyhow::ensure!(
                        !status.starts_with("Session ended")
                            && started.elapsed() < Duration::from_secs(20),
                        "Fixture handshake: {status}"
                    );
                    if Path::new(&(path.clone() + ".stop")).exists() {
                        return Ok(());
                    }
                }
            }
        };
        request.answer.send(true)?;
        let time = Instant::now();
        while time.elapsed() < Duration::from_secs(40)
            && !Path::new(&(path.clone() + ".stop")).exists()
        {
            std::thread::sleep(Duration::from_millis(50));
        }
        Ok(())
    })();
    std::fs::remove_file(path)?;
    result
}
