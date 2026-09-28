//! Bounded PDF snapshots for explicit local print adapters. No command execution.
use anyhow::{Context, Result, ensure};
use std::{
    fs::{self, File, OpenOptions},
    io::{Read, Seek, SeekFrom, Write},
    path::{Path, PathBuf},
};
pub const MAX_PDF: u64 = 128 * 1024 * 1024;
pub struct PdfSnapshot {
    pub path: PathBuf,
    directory: PathBuf,
}
impl Drop for PdfSnapshot {
    fn drop(&mut self) {
        let _ = fs::remove_file(&self.path);
        let _ = fs::remove_dir(&self.directory);
    }
}
pub fn snapshot(source: &Path) -> Result<PdfSnapshot> {
    ensure!(
        source.is_absolute()
            && source
                .extension()
                .is_some_and(|s| s.eq_ignore_ascii_case("pdf")),
        "Choose a downloaded PDF"
    );
    super::files::local_path(source)?;
    ensure!(
        fs::symlink_metadata(source)?.is_file(),
        "Choose a regular PDF file"
    );
    let mut input = File::open(source)?;
    let size = input.metadata()?.len();
    ensure!(
        input.metadata()?.is_file() && (8..=MAX_PDF).contains(&size),
        "PDF exceeds the 128 MiB print limit"
    );
    let mut magic = [0; 5];
    input.read_exact(&mut magic)?;
    ensure!(&magic == b"%PDF-", "This file is not a PDF");
    input.seek(SeekFrom::Start(0))?;
    let directory = std::env::temp_dir().join(format!("lume-print-{}", super::paired::token(16)?));
    #[allow(unused_mut)]
    let mut builder = fs::DirBuilder::new();
    #[cfg(unix)]
    {
        use std::os::unix::fs::DirBuilderExt;
        builder.mode(0o700);
    }
    builder.create(&directory)?;
    let result = PdfSnapshot {
        path: directory.join("document.pdf"),
        directory,
    };
    let mut options = OpenOptions::new();
    options.write(true).create_new(true);
    #[cfg(unix)]
    {
        use std::os::unix::fs::OpenOptionsExt;
        options.mode(0o600);
    }
    let mut output = options.open(&result.path)?;
    let mut left = size;
    let mut buffer = [0; 65536];
    while left > 0 {
        let n = left.min(buffer.len() as u64) as usize;
        input
            .read_exact(&mut buffer[..n])
            .context("The PDF changed while preparing")?;
        output.write_all(&buffer[..n])?;
        left -= n as u64;
    }
    ensure!(
        input.metadata()?.len() == size,
        "The PDF changed while preparing"
    );
    output.sync_all()?;
    Ok(result)
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn snapshot_is_private_independent_and_rejects_non_pdf() {
        let dir = std::env::temp_dir().join(format!(
            "lume-print-test-{}",
            crate::paired::token(12).unwrap()
        ));
        fs::create_dir(&dir).unwrap();
        let pdf = dir.join("sample.pdf");
        fs::write(&pdf, b"%PDF-1.4\nfixture\n%%EOF\n").unwrap();
        let snap = snapshot(&pdf).unwrap();
        let copy = snap.path.clone();
        assert_eq!(fs::read(&copy).unwrap(), fs::read(&pdf).unwrap());
        fs::write(&pdf, b"not a PDF").unwrap();
        assert!(snapshot(&pdf).is_err());
        assert!(fs::read(&copy).unwrap().starts_with(b"%PDF-"));
        let exe = dir.join("unsafe.exe");
        fs::write(&exe, b"%PDF-1.4\n").unwrap();
        assert!(snapshot(&exe).is_err());
        drop(snap);
        assert!(!copy.exists());
        fs::remove_file(exe).unwrap();
        fs::remove_file(pdf).unwrap();
        fs::remove_dir(dir).unwrap();
    }
}
