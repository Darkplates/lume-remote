//! Isolated Windows fixture only; no actual desktop input or user files.
use anyhow::{Result, ensure};
use lume_core::{
    files::FileCommand,
    session::{Command, Viewer},
    wire::Quality,
};
use std::{
    fs,
    path::{Path, PathBuf},
    thread,
    time::{Duration, Instant},
};
fn wait(
    viewer: &Viewer,
    description: &str,
    condition: impl Fn(&lume_core::session::ViewState) -> bool,
) -> Result<()> {
    let start = Instant::now();
    loop {
        let state = viewer.state.lock().unwrap();
        if condition(&state) {
            return Ok(());
        }
        ensure!(!viewer.is_finished(), "{description}: {}", state.status);
        ensure!(
            start.elapsed() < Duration::from_secs(60),
            "{description} timed out: {}",
            state.files.lock().unwrap().status
        );
        drop(state);
        thread::sleep(Duration::from_millis(10));
    }
}
fn download(viewer: &Viewer, root: &str, local: &Path, name: &str) -> Result<PathBuf> {
    viewer.command(Command::Files(FileCommand::Download {
        remote: format!("{root}\\{name}"),
        folder: local.into(),
        name: name.into(),
    }))?;
    wait(viewer, "Download", |s| {
        s.files.lock().unwrap().completed.is_some()
    })?;
    let value = viewer
        .state
        .lock()
        .unwrap()
        .files
        .lock()
        .unwrap()
        .completed
        .clone()
        .unwrap();
    Ok(value.into())
}
fn files(path: &str, library: &Path, local: &Path) -> Result<()> {
    ensure!(!local.exists(), "Local test destination must be new");
    fs::create_dir(local)?;
    let root = fs::read_to_string(format!("{path}.root"))?;
    let viewer = Viewer::open(&fs::read_to_string(path)?, library, Quality::default())?;
    wait(&viewer, "Connection", |s| s.connected && s.frames > 0)?;
    ensure!(
        viewer.state.lock().unwrap().files_allowed,
        "Missing file permission"
    );
    viewer.command(Command::Files(FileCommand::List {
        path: root.clone(),
        page: 0,
    }))?;
    wait(&viewer, "First page", |s| {
        let f = s.files.lock().unwrap();
        f.path == root && f.entries.len() == 200 && f.more
    })?;
    viewer.command(Command::Files(FileCommand::List {
        path: root.clone(),
        page: 1,
    }))?;
    wait(&viewer, "Second page", |s| {
        let f = s.files.lock().unwrap();
        f.page == 1 && !f.more && f.entries.len() == 7
    })?;
    println!("PASS Windows paged directory listing from the portable viewer.");
    fs::write(local.join("payload.bin"), b"preserve existing")?;
    let result = download(&viewer, &root, local, "payload.bin")?;
    let expected: Vec<u8> = (0..1048699usize).map(|i| (i * 31 + 7) as u8).collect();
    ensure!(fs::read(&result)? == expected, "Downloaded bytes differ");
    ensure!(
        fs::read(local.join("payload.bin"))? == b"preserve existing",
        "Existing local file changed"
    );
    viewer.state.lock().unwrap().files.lock().unwrap().completed = None;
    let empty = download(&viewer, &root, local, "empty.txt")?;
    ensure!(fs::metadata(empty)?.len() == 0, "Empty file changed");
    println!("PASS Download, empty file, SHA-256 and non-overwrite collision handling.");
    let source = local.join("upload.bin");
    fs::write(&source, &expected)?;
    for attempt in 0..2 {
        viewer.command(Command::Files(FileCommand::Upload {
            local: source.clone(),
            folder: root.clone(),
            name: "upload.bin".into(),
        }))?;
        wait(&viewer, "Upload start", |s| s.files.lock().unwrap().active)?;
        wait(&viewer, "Upload finish", |s| {
            let f = s.files.lock().unwrap();
            !f.active && f.status.starts_with("Complete")
        })?;
        println!(
            "PASS Verified upload {} while video remains connected.",
            attempt + 1
        );
    }
    let before = viewer.state.lock().unwrap().frames;
    let tree = local.join("folder-job");
    fs::create_dir_all(tree.join("nested/empty"))?;
    fs::write(tree.join("nested/café.bin"), &expected)?;
    fs::write(tree.join("zero.txt"), [])?;
    for _ in 0..2 {
        let operation = viewer.state.lock().unwrap().files.lock().unwrap().operation;
        viewer.command(Command::Files(FileCommand::UploadFolder {
            local: tree.clone(),
            folder: root.clone(),
            name: "folder-job".into(),
        }))?;
        wait(&viewer, "Folder upload", |s| {
            let f = s.files.lock().unwrap();
            f.operation > operation && !f.active && f.status.starts_with("Folder complete")
        })?;
    }
    let operation = viewer.state.lock().unwrap().files.lock().unwrap().operation;
    viewer.command(Command::Files(FileCommand::DownloadFolder {
        remote: format!("{root}\\folder-job"),
        folder: local.into(),
        name: "folder-job".into(),
    }))?;
    wait(&viewer, "Folder download", |s| {
        let f = s.files.lock().unwrap();
        f.operation > operation && !f.active && f.completed_directory
    })?;
    let saved = PathBuf::from(
        viewer
            .state
            .lock()
            .unwrap()
            .files
            .lock()
            .unwrap()
            .completed
            .clone()
            .unwrap(),
    );
    ensure!(
        saved != tree && saved.join("nested/empty").is_dir(),
        "Folder collision or empty directory lost"
    );
    ensure!(
        fs::read(saved.join("nested/café.bin"))? == expected
            && fs::metadata(saved.join("zero.txt"))?.len() == 0,
        "Folder bytes changed"
    );
    println!(
        "PASS Portable recursive upload/download with Windows: nested/empty folders, Unicode, root collisions and per-file SHA-256."
    );
    wait(&viewer, "Video after files", |s| s.frames > before)?;
    let mut viewer = viewer;
    viewer.close_and_wait();
    println!("PASS File worker joins and video continues after transfers.");
    Ok(())
}
fn paired(path: &str, library: &Path) -> Result<()> {
    let mut pairing = Viewer::open(&fs::read_to_string(path)?, library, Quality::default())?;
    wait(&pairing, "Pairing", |s| s.paired.is_some())?;
    let saved = pairing.state.lock().unwrap().paired.take().unwrap();
    pairing.close_and_wait();
    fs::write(format!("{path}.paired"), "ok")?;
    let started = Instant::now();
    while !Path::new(&format!("{path}.confirmed")).exists() {
        ensure!(
            started.elapsed() < Duration::from_secs(10),
            "Host did not confirm pairing"
        );
        thread::sleep(Duration::from_millis(20));
    }
    println!("PASS Portable pairing with Windows encrypted signaling and consumed one-time code.");
    for attempt in 0..2 {
        let mut viewer = Viewer::open(&saved.connection()?, library, Quality::default())?;
        wait(&viewer, "Saved connection", |s| {
            s.connected && s.frames >= 2
        })?;
        if attempt == 0 {
            ensure!(
                viewer.state.lock().unwrap().files.lock().unwrap().resumable,
                "Paired resume was not negotiated"
            );
            let local = PathBuf::from(format!("{path}.local"));
            fs::create_dir(&local)?;
            let root = fs::read_to_string(format!("{path}.root"))?;
            let bytes: Vec<u8> = (0..1048701usize).map(|i| (i % 251) as u8).collect();
            let source = local.join("resume.bin");
            fs::write(&source, &bytes)?;
            let operation = viewer.state.lock().unwrap().files.lock().unwrap().operation;
            viewer.command(Command::Files(FileCommand::Upload {
                local: source,
                folder: root.clone(),
                name: "resume.bin".into(),
            }))?;
            wait(&viewer, "Paired upload", |s| {
                let f = s.files.lock().unwrap();
                f.operation > operation && !f.active && f.status.starts_with("Complete")
            })?;
            let received = download(&viewer, &root, &local, "resume.bin")?;
            ensure!(fs::read(received)? == bytes, "Paired file bytes changed");
            let epoch = viewer.state.lock().unwrap().epoch;
            viewer.command(Command::Annotation(epoch, vec![]))?;
            ensure!(
                viewer
                    .command(Command::Annotation(epoch + 1, vec![]))
                    .is_err(),
                "Stale annotation accepted"
            );
            println!(
                "PASS Windows/Rust paired resume operations in both directions; annotation clear and stale generation denial."
            );
        }
        if attempt == 1 {
            fs::write(format!("{path}.revoke"), "revoke")?;
            let start = Instant::now();
            while viewer.state.lock().unwrap().connected {
                ensure!(
                    start.elapsed() < Duration::from_secs(15),
                    "Revocation did not disconnect the session"
                );
                thread::sleep(Duration::from_millis(20));
            }
        }
        viewer.close_and_wait();
        thread::sleep(Duration::from_secs(1));
    }
    let mut rejected = Viewer::open(&saved.connection()?, library, Quality::default())?;
    let start = Instant::now();
    while !rejected.is_finished() && rejected.state.lock().unwrap().reconnect_attempts == 0 {
        ensure!(
            !rejected.state.lock().unwrap().connected,
            "Revoked computer reconnected"
        );
        ensure!(
            start.elapsed() < Duration::from_secs(55),
            "Revoked attempt did not return an unavailable result"
        );
        thread::sleep(Duration::from_millis(50));
    }
    rejected.close_and_wait();
    println!("PASS Two automatic P2P connections; live revocation and rejected reuse.");
    Ok(())
}
fn main() -> Result<()> {
    let args: Vec<_> = std::env::args().collect();
    ensure!(
        args.len() >= 4,
        "Specify mode, private invitation file and native library"
    );
    if args[1] == "paired" {
        paired(&args[2], Path::new(&args[3]))
    } else {
        ensure!(args.len() == 5, "Specify a new local test directory");
        files(&args[2], Path::new(&args[3]), Path::new(&args[4]))
    }
}
