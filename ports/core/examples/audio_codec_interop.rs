use anyhow::{Result, ensure};
use std::io::Write;
fn main() -> Result<()> {
    let args: Vec<_> = std::env::args().collect();
    ensure!(
        args.len() == 3,
        "Specify Windows input and a new portable output"
    );
    let data = std::fs::read(&args[1])?;
    ensure!(data.len() <= 19465, "Oversized fixture");
    let decoded = lume_core::media::decode(&lume_core::wire::Packet(data))?;
    let expected: Vec<u8> = (0..19200).map(|n| (n % 251) as u8).collect();
    ensure!(
        decoded.bytes == expected && decoded.generation == 13,
        "Windows PCM did not round trip"
    );
    let packet = lume_core::media::packet(21, 13, &expected)?;
    std::fs::OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(&args[2])?
        .write_all(&packet.0)?;
    println!("PASS Rust independently decoded all Windows PCM bytes.");
    Ok(())
}
