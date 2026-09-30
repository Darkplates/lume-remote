//! Streaming folder jobs over the existing, negotiated file operations.
use super::*;

const MAX_DEPTH: usize = 64;
pub(super) struct FolderJob {
    name: String,
    root: Option<String>,
    direction: Direction,
    pending: Option<Pending>,
    items: u64,
    bytes: u64,
}
enum Direction {
    Upload(Vec<UploadDirectory>),
    Download(Vec<DownloadDirectory>),
}
struct UploadDirectory {
    entries: fs::ReadDir,
    remote: String,
}
struct DownloadDirectory {
    remote: String,
    local: PathBuf,
    page: i32,
    loaded: bool,
    more: bool,
    entries: VecDeque<Entry>,
}
enum Pending {
    Create {
        id: String,
        local: PathBuf,
        parent: String,
        name: String,
        root: bool,
        started: Instant,
    },
    List {
        id: String,
        started: Instant,
    },
}
impl FolderJob {
    pub(super) fn pending_id(&self) -> Option<&str> {
        match &self.pending {
            Some(Pending::Create { id, .. } | Pending::List { id, .. }) => Some(id),
            None => None,
        }
    }
    fn waiting(&self) -> Result<bool> {
        if let Some(Pending::Create { started, .. } | Pending::List { started, .. }) = &self.pending
        {
            ensure!(
                started.elapsed() < Duration::from_secs(30),
                "The remote folder did not answer"
            );
            return Ok(true);
        }
        Ok(false)
    }
}
fn child(parent: &str, name: &str) -> Result<String> {
    safe_name(name)?;
    ensure!(!parent.is_empty(), "Choose a remote folder");
    let path = format!(
        "{}{}{}",
        parent.trim_end_matches(['\\', '/']),
        if parent.contains('\\') { '\\' } else { '/' },
        name
    );
    remote_path(&path)?;
    Ok(path)
}
fn created_path(parent: &str, name: &str, root: bool, response: &str) -> Result<()> {
    let at = response
        .rfind(['\\', '/'])
        .context("Invalid created folder receipt")?;
    let leaf = &response[at + 1..];
    safe_name(leaf)?;
    ensure!(
        child(parent, leaf)? == response,
        "Created folder escaped its requested parent"
    );
    if leaf != name {
        ensure!(root, "A nested folder was renamed unexpectedly");
        let suffix = leaf
            .strip_prefix(name)
            .and_then(|s| s.strip_prefix(" ("))
            .and_then(|s| s.strip_suffix(')'))
            .context("Invalid collision-safe folder name")?;
        let n: usize = suffix.parse()?;
        ensure!(
            (1..10000).contains(&n) && suffix == n.to_string(),
            "Invalid folder collision suffix"
        );
    }
    Ok(())
}
fn create_local(parent: &Path, name: &str, unique: bool) -> Result<PathBuf> {
    local_path(parent)?;
    safe_name(name)?;
    ensure!(parent.is_dir(), "Choose a local folder");
    for n in 0..if unique { 10000 } else { 1 } {
        let leaf = if n == 0 {
            name.to_owned()
        } else {
            format!("{name} ({n})")
        };
        safe_name(&leaf)?;
        let path = parent.join(leaf);
        #[allow(unused_mut)]
        let mut builder = fs::DirBuilder::new();
        #[cfg(unix)]
        {
            use std::os::unix::fs::DirBuilderExt;
            builder.mode(0o700);
        }
        match builder.create(&path) {
            Ok(()) => return Ok(path),
            Err(e) if e.kind() == std::io::ErrorKind::AlreadyExists => {}
            Err(e) => return Err(e.into()),
        }
    }
    bail!("A file or folder already uses that name")
}

#[cfg(test)]
mod tests {
    use super::*;
    fn root() -> PathBuf {
        let root = std::env::temp_dir().join(format!("lume-folder-job-{}", identifier().unwrap()));
        fs::create_dir(&root).unwrap();
        root
    }
    fn drive(viewer: &mut Worker, vr: &Receiver<Event>, host: &mut Worker, hr: &Receiver<Event>) {
        let started = Instant::now();
        while started.elapsed() < Duration::from_secs(30) {
            viewer.pump().unwrap();
            if let Err(e) = viewer.pump_job() {
                viewer.fail(&format!("Folder stopped: {e}")).unwrap();
            }
            host.pump().unwrap();
            for _ in 0..16 {
                match vr.try_recv() {
                    Ok(Event::Send(p)) => host.handle(p).unwrap(),
                    Ok(Event::Failed(e)) => panic!("{e}"),
                    Err(_) => break,
                }
            }
            for _ in 0..16 {
                match hr.try_recv() {
                    Ok(Event::Send(p)) => viewer.handle(p).unwrap(),
                    Ok(Event::Failed(e)) => panic!("{e}"),
                    Err(_) => break,
                }
            }
            if viewer.job.is_none() && viewer.transfer.is_none() {
                return;
            }
            if viewer.completion.is_some() || host.completion.is_some() {
                thread::sleep(Duration::from_millis(1));
            }
        }
        panic!("Folder job failed to terminate");
    }
    #[test]
    fn recursive_folders_roundtrip_pages_empty_directories_and_collisions() {
        let root = root();
        let local = root.join("tree");
        let shared = root.join("shared");
        let destination = root.join("download");
        fs::create_dir_all(local.join("nested/empty")).unwrap();
        fs::create_dir(&shared).unwrap();
        fs::create_dir(&destination).unwrap();
        fs::create_dir(shared.join("tree")).unwrap();
        fs::write(shared.join("tree/keep.txt"), b"existing").unwrap();
        for n in 0..205 {
            fs::write(local.join(format!("file-{n:03}.txt")), format!("value-{n}")).unwrap();
        }
        fs::write(local.join("nested/café.bin"), vec![57; CHUNK * 10 + 7]).unwrap();
        let (mut viewer, vr) = super::super::tests::worker(None);
        let (mut host, hr) = super::super::tests::worker(Some(shared.clone()));
        viewer
            .command(FileCommand::UploadFolder {
                local: local.clone(),
                folder: "R:\\".into(),
                name: "tree".into(),
            })
            .unwrap();
        drive(&mut viewer, &vr, &mut host, &hr);
        assert!(
            viewer
                .state
                .lock()
                .unwrap()
                .status
                .starts_with("Folder complete")
        );
        assert_eq!(fs::read(shared.join("tree/keep.txt")).unwrap(), b"existing");
        assert!(shared.join("tree (1)/nested/empty").is_dir());
        fs::create_dir(destination.join("tree (1)")).unwrap();
        viewer
            .command(FileCommand::DownloadFolder {
                remote: "R:\\tree (1)".into(),
                folder: destination.clone(),
                name: "tree (1)".into(),
            })
            .unwrap();
        drive(&mut viewer, &vr, &mut host, &hr);
        let state = viewer.state.lock().unwrap().clone();
        assert!(
            state.status.starts_with("Folder complete"),
            "{}",
            state.status
        );
        assert!(state.completed_directory);
        assert_eq!(state.items_done, 209);
        let saved = PathBuf::from(state.completed.unwrap());
        assert_eq!(saved, destination.join("tree (1) (1)"));
        assert!(saved.join("nested/empty").is_dir());
        for n in 0..205 {
            assert_eq!(
                fs::read(saved.join(format!("file-{n:03}.txt"))).unwrap(),
                format!("value-{n}").as_bytes()
            );
        }
        assert_eq!(
            fs::read(saved.join("nested/café.bin")).unwrap(),
            vec![57; CHUNK * 10 + 7]
        );
        drop((viewer, host));
        fs::remove_dir_all(root).unwrap();
    }
    #[test]
    fn forged_directory_receipts_never_redirect_uploads() {
        let root = root();
        let (mut viewer, _vr) = super::super::tests::worker(None);
        viewer
            .command(FileCommand::UploadFolder {
                local: root.clone(),
                folder: "R:\\safe".into(),
                name: "tree".into(),
            })
            .unwrap();
        let id = viewer
            .job
            .as_ref()
            .unwrap()
            .pending_id()
            .unwrap()
            .to_owned();
        viewer
            .handle(packet(10, &id).byte(1).text("R:\\elsewhere\\tree"))
            .unwrap();
        assert!(viewer.job.is_none());
        assert!(viewer.state.lock().unwrap().status.contains("escaped"));
        for bad in [
            "R:\\safe\\..",
            "R:\\safe\\other",
            "R:\\safe\\tree (0)",
            "R:\\safe\\tree (01)",
            "R:\\safe\\tree (1)\\child",
        ] {
            assert!(created_path("R:\\safe", "tree", true, bad).is_err());
        }
        assert!(created_path("R:\\safe", "tree", true, "R:\\safe\\tree (12)").is_ok());
        assert!(created_path("R:\\safe", "tree", false, "R:\\safe\\tree (12)").is_err());
        drop(viewer);
        fs::remove_dir(root).unwrap();
    }
    #[test]
    fn cancel_and_late_folder_reply_leave_followup_transfer_usable() {
        let root = root();
        fs::write(root.join("next.txt"), b"next").unwrap();
        let (mut viewer, _vr) = super::super::tests::worker(None);
        viewer
            .command(FileCommand::UploadFolder {
                local: root.clone(),
                folder: "R:\\".into(),
                name: "tree".into(),
            })
            .unwrap();
        let retired = viewer
            .job
            .as_ref()
            .unwrap()
            .pending_id()
            .unwrap()
            .to_owned();
        viewer.command(FileCommand::Cancel).unwrap();
        viewer
            .command(FileCommand::Upload {
                local: root.join("next.txt"),
                folder: "R:\\".into(),
                name: "next.txt".into(),
            })
            .unwrap();
        let current = viewer.transfer.as_ref().unwrap().id().to_owned();
        viewer
            .handle(packet(10, &retired).byte(1).text("R:\\tree"))
            .unwrap();
        assert_eq!(viewer.transfer.as_ref().unwrap().id(), current);
        assert!(viewer.job.is_none());
        viewer.command(FileCommand::Cancel).unwrap();
        viewer
            .command(FileCommand::Download {
                remote: "R:\\new.txt".into(),
                folder: root.clone(),
                name: "new.txt".into(),
            })
            .unwrap();
        viewer
            .handle(packet(6, &current).long(0).int(4).bytes(b"late"))
            .unwrap();
        assert!(viewer.transfer.is_some());
        drop(viewer);
        fs::remove_dir_all(root).unwrap();
    }
    #[test]
    fn folder_commands_require_capability_and_keep_existing_destinations() {
        let root = root();
        fs::create_dir(root.join("keep")).unwrap();
        fs::write(root.join("keep/original"), b"original").unwrap();
        assert!(create_local(&root, "keep", false).is_err());
        let (mut viewer, _vr) = super::super::tests::worker(None);
        viewer.folders = false;
        assert!(
            viewer
                .command(FileCommand::DownloadFolder {
                    remote: "R:\\keep".into(),
                    folder: root.clone(),
                    name: "keep".into()
                })
                .is_err()
        );
        assert_eq!(fs::read(root.join("keep/original")).unwrap(), b"original");
        assert_eq!(fs::read_dir(&root).unwrap().count(), 1);
        drop(viewer);
        fs::remove_dir_all(root).unwrap();
    }
    #[cfg(unix)]
    #[test]
    fn folder_upload_rejects_links_and_special_files() {
        let root = root();
        let local = root.join("tree");
        let shared = root.join("shared");
        fs::create_dir(&local).unwrap();
        fs::create_dir(&shared).unwrap();
        std::os::unix::fs::symlink("/", local.join("escape")).unwrap();
        let (mut viewer, vr) = super::super::tests::worker(None);
        let (mut host, hr) = super::super::tests::worker(Some(shared.clone()));
        viewer
            .command(FileCommand::UploadFolder {
                local,
                folder: "R:\\".into(),
                name: "tree".into(),
            })
            .unwrap();
        drive(&mut viewer, &vr, &mut host, &hr);
        assert!(
            viewer
                .state
                .lock()
                .unwrap()
                .status
                .starts_with("Folder stopped")
        );
        assert_eq!(fs::read_dir(shared.join("tree")).unwrap().count(), 0);
        drop((viewer, host));
        fs::remove_dir_all(root).unwrap();
    }
}
impl Worker {
    fn job_command(&mut self, command: FileCommand) -> Result<()> {
        let key = self.resume_key.take();
        let result = self.command(command);
        self.resume_key = key;
        result
    }

    pub(super) fn retire(&mut self, id: String) {
        if self.retired.len() == 32 {
            self.retired.pop_front();
        }
        self.retired.push_back(id);
    }
    fn check_job_start(&self) -> Result<()> {
        ensure!(
            self.server_root.is_none() && self.folders,
            "This host does not support folder transfers"
        );
        ensure!(
            self.transfer.is_none() && self.job.is_none(),
            "Finish or cancel the current transfer first"
        );
        Ok(())
    }
    fn show_job(&self, job: &FolderJob) {
        let mut s = self.state.lock().unwrap();
        s.active = true;
        s.folder_job = true;
        s.items_done = job.items;
        s.bytes_done = job.bytes;
        s.completed = None;
        s.completed_directory = false;
        s.status = format!("Copying {} — {} items complete", job.name, job.items);
        if self.transfer.is_none() {
            s.name = job.name.clone();
            s.direction = if matches!(job.direction, Direction::Upload(_)) {
                "Sending folder"
            } else {
                "Receiving folder"
            }
            .into();
            s.bytes = 0;
            s.total = 0;
        }
    }
    pub(super) fn start_upload_job(
        &mut self,
        local: PathBuf,
        folder: String,
        name: String,
    ) -> Result<()> {
        self.check_job_start()?;
        local_path(&local)?;
        ensure!(local.is_dir(), "Choose a local folder");
        // Check enumeration before asking the other computer to create anything.
        let _ = fs::read_dir(&local)?;
        let id = identifier()?;
        self.send(packet(11, &id).text(&folder).text(&name).byte(1))?;
        let job = FolderJob {
            name: name.clone(),
            root: None,
            direction: Direction::Upload(Vec::new()),
            items: 0,
            bytes: 0,
            pending: Some(Pending::Create {
                id,
                local,
                parent: folder,
                name,
                root: true,
                started: Instant::now(),
            }),
        };
        self.show_job(&job);
        self.job = Some(job);
        Ok(())
    }
    pub(super) fn start_download_job(
        &mut self,
        remote: String,
        folder: PathBuf,
        name: String,
    ) -> Result<()> {
        self.check_job_start()?;
        let local = create_local(&folder, &name, true)?;
        let job = FolderJob {
            name,
            root: Some(local.to_string_lossy().into()),
            items: 1,
            bytes: 0,
            pending: None,
            direction: Direction::Download(vec![DownloadDirectory {
                remote,
                local,
                page: 0,
                loaded: false,
                more: false,
                entries: VecDeque::new(),
            }]),
        };
        self.show_job(&job);
        self.job = Some(job);
        Ok(())
    }
    pub(super) fn pump_job(&mut self) -> Result<()> {
        if self.transfer.is_some() {
            return Ok(());
        }
        let Some(mut job) = self.job.take() else {
            return Ok(());
        };
        let result = (|| -> Result<bool> {
            if job.waiting()? {
                return Ok(false);
            }
            match &mut job.direction {
                Direction::Upload(stack) => {
                    let depth = stack.len();
                    let Some(top) = stack.last_mut() else {
                        return Ok(true);
                    };
                    let Some(entry) = top.entries.next() else {
                        stack.pop();
                        return Ok(false);
                    };
                    let entry = entry?;
                    let local = entry.path();
                    local_path(&local)?;
                    let name = entry
                        .file_name()
                        .into_string()
                        .map_err(|_| anyhow::anyhow!("A file name is not valid Unicode"))?;
                    safe_name(&name)?;
                    ensure!(
                        !name.starts_with(".lume-"),
                        "Internal Lume transfer files cannot be copied"
                    );
                    let kind = entry.file_type()?;
                    if kind.is_dir() {
                        ensure!(
                            depth < MAX_DEPTH,
                            "Folder nesting exceeds the safe traversal depth"
                        );
                        let _ = fs::read_dir(&local)?;
                        let id = identifier()?;
                        self.send(packet(11, &id).text(&top.remote).text(&name).byte(0))?;
                        job.pending = Some(Pending::Create {
                            id,
                            local,
                            parent: top.remote.clone(),
                            name,
                            root: false,
                            started: Instant::now(),
                        });
                    } else {
                        ensure!(
                            kind.is_file(),
                            "Only ordinary files and folders can be copied"
                        );
                        self.job_command(FileCommand::Upload {
                            local,
                            folder: top.remote.clone(),
                            name,
                        })?;
                    }
                }
                Direction::Download(stack) => {
                    let depth = stack.len();
                    let Some(top) = stack.last_mut() else {
                        return Ok(true);
                    };
                    if !top.loaded {
                        let id = identifier()?;
                        self.send(packet(1, &id).text(&top.remote).int(top.page))?;
                        job.pending = Some(Pending::List {
                            id,
                            started: Instant::now(),
                        });
                    } else if let Some(entry) = top.entries.pop_front() {
                        let remote = child(&top.remote, &entry.name)?;
                        if entry.directory {
                            ensure!(
                                depth < MAX_DEPTH,
                                "Folder nesting exceeds the safe traversal depth"
                            );
                            let local = create_local(&top.local, &entry.name, false)?;
                            stack.push(DownloadDirectory {
                                remote,
                                local,
                                page: 0,
                                loaded: false,
                                more: false,
                                entries: VecDeque::new(),
                            });
                            job.items = job
                                .items
                                .checked_add(1)
                                .context("Folder item count overflow")?;
                        } else {
                            // Repeated/mutating pages must not silently duplicate or replace an item.
                            match fs::symlink_metadata(top.local.join(&entry.name)) {
                                Err(e) if e.kind() == std::io::ErrorKind::NotFound => {}
                                Err(e) => return Err(e.into()),
                                Ok(_) => bail!(
                                    "The remote listing repeated a name or the destination changed"
                                ),
                            }
                            self.job_command(FileCommand::Download {
                                remote,
                                folder: top.local.clone(),
                                name: entry.name,
                            })?;
                        }
                    } else if top.more {
                        ensure!(top.page < 10000, "Remote folder page limit exceeded");
                        top.page += 1;
                        top.loaded = false;
                    } else {
                        stack.pop();
                    }
                }
            }
            Ok(false)
        })();
        match result {
            Ok(true) => {
                let mut s = self.state.lock().unwrap();
                s.active = false;
                s.folder_job = true;
                s.items_done = job.items;
                s.bytes_done = job.bytes;
                s.bytes = job.bytes;
                s.total = job.bytes;
                s.status = format!(
                    "Folder complete — {} items; files SHA-256 verified",
                    job.items
                );
                if matches!(job.direction, Direction::Download(_)) {
                    s.completed = job.root;
                    s.completed_directory = true;
                }
            }
            other => {
                self.show_job(&job);
                self.job = Some(job);
                other?;
            }
        }
        Ok(())
    }
    pub(super) fn job_list(
        &mut self,
        id: &str,
        path: &str,
        page: i32,
        more: bool,
        entries: &[Entry],
    ) -> Result<bool> {
        let Some(job) = self.job.as_mut().filter(|j| j.pending_id() == Some(id)) else {
            return Ok(false);
        };
        ensure!(
            matches!(job.pending, Some(Pending::List { .. })),
            "Unexpected folder listing receipt"
        );
        let Direction::Download(stack) = &mut job.direction else {
            bail!("Unexpected folder job listing")
        };
        let top = stack.last_mut().context("No requested folder")?;
        ensure!(
            top.remote == path && top.page == page && (!more || !entries.is_empty()),
            "Unexpected folder job page"
        );
        top.entries = entries.iter().cloned().collect();
        top.loaded = true;
        top.more = more;
        job.pending = None;
        Ok(true)
    }
    pub(super) fn job_result(&mut self, id: &str, ok: bool, message: &str) -> Result<bool> {
        if self.job.as_ref().and_then(FolderJob::pending_id) != Some(id) {
            return Ok(false);
        }
        if !ok {
            self.fail(&format!(
                "Folder stopped: {message}. Completed items kept; retry creates a new folder."
            ))?;
            return Ok(true);
        }
        let mut job = self.job.take().unwrap();
        let result = (|| -> Result<()> {
            let Some(Pending::Create {
                local,
                parent,
                name,
                root,
                ..
            }) = job.pending.take()
            else {
                bail!("Unexpected folder success receipt")
            };
            created_path(&parent, &name, root, message)?;
            local_path(&local)?;
            let Direction::Upload(stack) = &mut job.direction else {
                bail!("Unexpected created folder")
            };
            stack.push(UploadDirectory {
                entries: fs::read_dir(local)?,
                remote: message.into(),
            });
            job.items = job
                .items
                .checked_add(1)
                .context("Folder item count overflow")?;
            if root {
                job.root = Some(message.into());
            }
            Ok(())
        })();
        self.show_job(&job);
        self.job = Some(job);
        if let Err(e) = result {
            self.fail(&format!("Folder stopped: {e}. Completed items kept."))?;
        }
        Ok(true)
    }
    pub(super) fn job_file_done(&mut self, length: u64) -> Result<()> {
        if let Some(job) = self.job.as_mut() {
            job.items = job
                .items
                .checked_add(1)
                .context("Folder item count overflow")?;
            job.bytes = job
                .bytes
                .checked_add(length)
                .context("Folder size overflow")?;
            let mut s = self.state.lock().unwrap();
            s.active = true;
            s.folder_job = true;
            s.items_done = job.items;
            s.bytes_done = job.bytes;
            s.completed = None;
            s.completed_directory = false;
        }
        Ok(())
    }
}
