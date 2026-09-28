//! Paired single-file checkpoints. Keys never enter journals or public state.
use super::*;
use hmac::{Hmac, Mac};
use std::io::{Seek, SeekFrom};
use zeroize::Zeroizing;

pub(super) struct Journal {
    path: PathBuf,
    digest: [u8; 32],
    keep: bool,
}

pub(super) fn key(value: &str) -> Result<Zeroizing<Vec<u8>>> {
    use base64::Engine;
    let bytes = Zeroizing::new(base64::engine::general_purpose::URL_SAFE_NO_PAD.decode(value)?);
    ensure!(bytes.len() == 32, "Invalid paired transfer key");
    Ok(bytes)
}

impl Incoming {
    pub(super) fn resume(
        folder: &Path,
        name: &str,
        length: u64,
        digest: [u8; 32],
        key: &[u8],
        mut progress: impl FnMut() -> Result<()>,
    ) -> Result<Self> {
        local_path(folder)?;
        safe_name(name)?;
        ensure!(
            folder.is_dir() && key.len() == 32,
            "Invalid resume destination"
        );
        let canonical = fs::canonicalize(folder)?;
        let mut mac = Hmac::<Sha256>::new_from_slice(key)?;
        mac.update(b"Lume portable paired resume v1\0");
        mac.update(
            canonical
                .to_str()
                .context("Invalid destination encoding")?
                .as_bytes(),
        );
        mac.update(&[0]);
        mac.update(name.as_bytes());
        let mut header = b"LUMEPR1\0".to_vec();
        header.extend(length.to_le_bytes());
        header.extend(digest);
        mac.update(&header);
        let tag = mac.finalize().into_bytes();
        header.extend(tag);
        let identity: String = tag.iter().map(|b| format!("{b:02x}")).collect();
        let temp = folder.join(format!(".lume-resume-{identity}.part"));
        let path = folder.join(format!(".lume-resume-{identity}.state"));
        let mut options = OpenOptions::new();
        options.read(true).write(true).create_new(true);
        #[cfg(unix)]
        {
            use std::os::unix::fs::OpenOptionsExt;
            options.mode(0o600);
        }
        let (mut file, created) = match options.open(&temp) {
            Ok(file) => (file, true),
            Err(e) if e.kind() == std::io::ErrorKind::AlreadyExists => {
                local_path(&temp)?;
                (
                    OpenOptions::new().read(true).write(true).open(&temp)?,
                    false,
                )
            }
            Err(e) => return Err(e.into()),
        };
        ensure!(file.metadata()?.is_file(), "Invalid partial file");
        file.try_lock()
            .context("This transfer is already open in another session")?;
        #[cfg(unix)]
        {
            use std::os::unix::fs::MetadataExt;
            ensure!(
                file.metadata()?.nlink() == 1,
                "Linked partial files are not supported"
            );
        }
        // A missing/invalid journal never authorizes reuse or deletion of someone else's data.
        if created {
            let result = (|| -> Result<()> {
                let mut state = options.open(&path)?;
                state.write_all(&header)?;
                state.sync_all()?;
                Ok(())
            })();
            if let Err(e) = result {
                drop(file);
                let _ = fs::remove_file(&temp);
                return Err(e);
            }
        } else {
            local_path(&path)?;
            let mut state = File::open(&path)?;
            ensure!(
                state.metadata()?.is_file() && state.metadata()?.len() == 80,
                "Invalid resume journal"
            );
            let mut stored = [0; 80];
            state.read_exact(&mut stored)?;
            ensure!(
                crate::wire::equal(&header, &stored),
                "Resume journal identity changed"
            );
        }
        let position = file.metadata()?.len();
        ensure!(position <= length, "Partial file exceeds its expected size");
        let mut hash = Sha256::new();
        let mut remaining = position;
        let mut buffer = [0; CHUNK];
        while remaining > 0 {
            progress()?;
            let n = buffer.len().min(remaining as usize);
            file.read_exact(&mut buffer[..n])?;
            hash.update(&buffer[..n]);
            remaining -= n as u64;
        }
        file.seek(SeekFrom::Start(position))?;
        Ok(Self {
            file: Some(file),
            temp,
            folder: folder.into(),
            name: name.into(),
            length,
            position,
            hash,
            journal: Some(Journal {
                path,
                digest,
                keep: false,
            }),
        })
    }
    pub(super) fn preserve(&mut self) {
        if let Some(journal) = &mut self.journal {
            if self.file.as_ref().is_some_and(|f| f.sync_all().is_ok()) {
                journal.keep = true;
            }
        }
    }
    pub(super) fn check_identity(&self, digest: &[u8]) -> Result<()> {
        if let Some(journal) = &self.journal {
            ensure!(
                crate::wire::equal(&journal.digest, digest),
                "Source identity changed"
            );
        }
        Ok(())
    }
    pub(super) fn cleanup(&self) {
        if self.journal.as_ref().is_some_and(|j| j.keep) {
            return;
        }
        let _ = fs::remove_file(&self.temp);
        if let Some(journal) = &self.journal {
            let _ = fs::remove_file(&journal.path);
        }
    }
}

impl Worker {
    pub(super) fn preparing(&self, id: &str, last: &mut Instant) -> Result<()> {
        ensure!(
            !self.stop.load(Ordering::Acquire) && !self.cancel.load(Ordering::Acquire),
            "Transfer preparation cancelled"
        );
        if last.elapsed() >= Duration::from_secs(2) {
            self.send(packet(16, id))?;
            *last = Instant::now();
        }
        Ok(())
    }
    pub(super) fn identity(&self, file: &mut File, id: &str, length: u64) -> Result<[u8; 32]> {
        let mut hash = Sha256::new();
        let mut buffer = [0; CHUNK];
        let mut left = length;
        let mut last = Instant::now();
        while left > 0 {
            self.preparing(id, &mut last)?;
            let n = buffer.len().min(left as usize);
            file.read_exact(&mut buffer[..n])?;
            hash.update(&buffer[..n]);
            left -= n as u64;
        }
        ensure!(
            file.metadata()?.len() == length,
            "Source changed during preparation"
        );
        file.seek(SeekFrom::Start(0))?;
        Ok(hash.finalize().into())
    }
    pub(super) fn incoming(
        &self,
        folder: &Path,
        name: &str,
        length: u64,
        digest: Option<[u8; 32]>,
        id: &str,
    ) -> Result<Incoming> {
        if let Some(digest) = digest {
            let key = self
                .resume_key
                .as_ref()
                .context("Resume was not negotiated")?;
            let mut last = Instant::now();
            Incoming::resume(folder, name, length, digest, key, || {
                self.preparing(id, &mut last)
            })
        } else {
            Incoming::create(folder, name, length)
        }
    }
    pub(super) fn resume_ready(&mut self, id: &str, offset: u64, prefix: &[u8]) -> Result<()> {
        let Some(mut transfer) = self.transfer.take() else {
            return Ok(());
        };
        let result = (|| -> Result<()> {
            let Transfer::Send {
                id: expected,
                file,
                length,
                sent,
                ack,
                ready,
                hash,
                resumable,
                activity,
                ..
            } = &mut transfer
            else {
                bail!("Unexpected resume receipt");
            };
            ensure!(
                expected == id && *resumable && !*ready && offset <= *length,
                "Unexpected resume offset"
            );
            let mut left = offset;
            let mut buffer = [0; CHUNK];
            let mut last = Instant::now();
            while left > 0 {
                self.preparing(id, &mut last)?;
                let n = buffer.len().min(left as usize);
                file.read_exact(&mut buffer[..n])?;
                hash.update(&buffer[..n]);
                left -= n as u64;
            }
            ensure!(
                crate::wire::equal(&hash.clone().finalize(), prefix),
                "Saved prefix changed. Retry to start a fresh verified transfer"
            );
            *sent = offset;
            *ack = offset;
            *ready = true;
            *activity = Instant::now();
            let mut s = self.state.lock().unwrap();
            s.resumed_bytes = offset;
            s.bytes = offset;
            Ok(())
        })();
        self.transfer = Some(transfer);
        if let Err(e) = result {
            self.fail(&e.to_string())?;
        }
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    fn root() -> PathBuf {
        let root = std::env::temp_dir().join(format!("lume-resume-test-{}", identifier().unwrap()));
        fs::create_dir(&root).unwrap();
        root
    }
    fn pair(root: &Path) -> (Worker, Receiver<Event>, Worker, Receiver<Event>) {
        let (mut viewer, vr) = super::super::tests::worker(None);
        let (mut host, hr) = super::super::tests::worker(Some(root.into()));
        viewer.resume_key = Some(Zeroizing::new(vec![19; 32]));
        host.resume_key = Some(Zeroizing::new(vec![19; 32]));
        (viewer, vr, host, hr)
    }
    fn relay(from: &Receiver<Event>, to: &mut Worker) -> u64 {
        let mut bytes = 0;
        while let Ok(event) = from.try_recv() {
            match event {
                Event::Send(p) => {
                    if p.0.get(1) == Some(&6) {
                        bytes += p.0.len() as u64 - 50;
                    }
                    to.handle(p).unwrap();
                }
                Event::Failed(e) => panic!("{e}"),
            }
        }
        bytes
    }
    fn step(v: &mut Worker, vr: &Receiver<Event>, h: &mut Worker, hr: &Receiver<Event>) -> u64 {
        let mut bytes = relay(vr, h) + relay(hr, v);
        v.pump().unwrap();
        h.pump().unwrap();
        bytes += relay(vr, h) + relay(hr, v);
        bytes
    }
    fn preserve(worker: &mut Worker) -> u64 {
        if let Some(Transfer::Receive {
            incoming: Some(file),
            ..
        }) = &mut worker.transfer
        {
            file.preserve();
            file.position
        } else {
            panic!("Expected partial download");
        }
    }
    #[test]
    fn interrupted_upload_and_download_send_only_the_verified_suffix() {
        for upload in [true, false] {
            let root = root();
            let local = root.join("local");
            let host_root = root.join("host");
            fs::create_dir(&local).unwrap();
            fs::create_dir(&host_root).unwrap();
            let bytes = (0..CHUNK * 32 + 13)
                .map(|v| (v % 251) as u8)
                .collect::<Vec<_>>();
            let source = if upload { &local } else { &host_root };
            fs::write(source.join("large.bin"), &bytes).unwrap();
            let command = || {
                if upload {
                    FileCommand::Upload {
                        local: local.join("large.bin"),
                        folder: "R:\\".into(),
                        name: "large.bin".into(),
                    }
                } else {
                    FileCommand::Download {
                        remote: "R:\\large.bin".into(),
                        folder: local.clone(),
                        name: "large.bin".into(),
                    }
                }
            };
            let (mut v, vr, mut h, hr) = pair(&host_root);
            v.command(command()).unwrap();
            step(&mut v, &vr, &mut h, &hr);
            step(&mut v, &vr, &mut h, &hr);
            let offset = preserve(if upload { &mut h } else { &mut v });
            assert!(offset > 0 && offset < bytes.len() as u64);
            drop((v, h, vr, hr));
            let (mut v, vr, mut h, hr) = pair(&host_root);
            v.command(command()).unwrap();
            let mut sent = 0;
            for _ in 0..50 {
                sent += step(&mut v, &vr, &mut h, &hr);
                if v.transfer.is_none() && h.transfer.is_none() {
                    break;
                }
            }
            assert!(v.transfer.is_none() && h.transfer.is_none());
            assert_eq!(sent, bytes.len() as u64 - offset);
            let destination = if upload { &host_root } else { &local };
            assert_eq!(fs::read(destination.join("large.bin")).unwrap(), bytes);
            assert_eq!(fs::read_dir(destination).unwrap().count(), 1);
            drop((v, h));
            fs::remove_dir_all(root).unwrap();
        }
    }
    #[test]
    fn checkpoint_identity_lock_and_digest_are_enforced() {
        let root = root();
        let digest: [u8; 32] = Sha256::digest(b"abcdef").into();
        let mut first =
            Incoming::resume(&root, "sample.bin", 6, digest, &[1; 32], || Ok(())).unwrap();
        first.append(0, b"abc").unwrap();
        assert!(Incoming::resume(&root, "sample.bin", 6, digest, &[1; 32], || Ok(())).is_err());
        first.preserve();
        drop(first);
        let wrong = Incoming::resume(&root, "sample.bin", 6, digest, &[2; 32], || Ok(())).unwrap();
        assert_eq!(wrong.position, 0);
        drop(wrong);
        let mut resumed =
            Incoming::resume(&root, "sample.bin", 6, digest, &[1; 32], || Ok(())).unwrap();
        assert_eq!(resumed.position, 3);
        resumed.append(3, b"XXX").unwrap();
        assert!(resumed.complete(&Sha256::digest(b"abcXXX")).is_err());
        drop(resumed);
        assert_eq!(fs::read_dir(&root).unwrap().count(), 0);
        fs::remove_dir(root).unwrap();
    }
    #[test]
    fn corrupt_prefix_is_cancelled_and_retry_starts_clean() {
        let root = root();
        let local = root.join("local");
        let host_root = root.join("host");
        fs::create_dir(&local).unwrap();
        fs::create_dir(&host_root).unwrap();
        fs::write(local.join("sample.bin"), vec![17; CHUNK * 24]).unwrap();
        let command = || FileCommand::Upload {
            local: local.join("sample.bin"),
            folder: "R:\\".into(),
            name: "sample.bin".into(),
        };
        let (mut v, vr, mut h, hr) = pair(&host_root);
        v.command(command()).unwrap();
        step(&mut v, &vr, &mut h, &hr);
        preserve(&mut h);
        drop((v, h, vr, hr));
        let partial = fs::read_dir(&host_root)
            .unwrap()
            .filter_map(Result::ok)
            .map(|v| v.path())
            .find(|p| p.extension().is_some_and(|s| s == "part"))
            .unwrap();
        OpenOptions::new()
            .write(true)
            .open(&partial)
            .unwrap()
            .write_all(b"corrupt")
            .unwrap();
        let (mut v, vr, mut h, hr) = pair(&host_root);
        v.command(command()).unwrap();
        for _ in 0..4 {
            step(&mut v, &vr, &mut h, &hr);
        }
        assert!(v.transfer.is_none() && h.transfer.is_none());
        assert!(v.state.lock().unwrap().status.contains("prefix changed"));
        assert_eq!(fs::read_dir(&host_root).unwrap().count(), 0);
        v.command(command()).unwrap();
        for _ in 0..50 {
            step(&mut v, &vr, &mut h, &hr);
            if v.transfer.is_none() && h.transfer.is_none() {
                break;
            }
        }
        assert_eq!(
            fs::read(host_root.join("sample.bin")).unwrap(),
            vec![17; CHUNK * 24]
        );
        drop((v, h));
        fs::remove_dir_all(root).unwrap();
    }
    #[test]
    fn cancel_removes_partial_and_legacy_ack_cannot_skip_prefix() {
        let root = root();
        fs::write(root.join("source.bin"), b"abcdef").unwrap();
        let (mut v, vr, mut h, hr) = pair(&root);
        v.command(FileCommand::Upload {
            local: root.join("source.bin"),
            folder: "R:\\".into(),
            name: "copy.bin".into(),
        })
        .unwrap();
        relay(&vr, &mut h);
        let id = v.transfer.as_ref().unwrap().id().to_owned();
        assert!(v.handle(packet(8, &id).long(0)).is_err());
        v.command(FileCommand::Cancel).unwrap();
        relay(&vr, &mut h);
        assert!(h.transfer.is_none());
        assert_eq!(fs::read_dir(&root).unwrap().count(), 1);
        drop((v, h, hr));
        fs::remove_dir_all(root).unwrap();
    }
}
