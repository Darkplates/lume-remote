//! Explicit owner opt-in, OS-protected credentials, and a separate sign-in host process.
use anyhow::{Context, Result, ensure};
use lume_core::{host_store::HostStore, permanent::PermanentHost};
use std::{
    fs,
    io::{Read, Write},
    path::{Path, PathBuf},
    process::{Command, Stdio},
    sync::Arc,
    time::{Duration, Instant},
};
use zeroize::Zeroizing;

pub fn supported() -> bool {
    cfg!(any(target_os = "linux", target_os = "macos"))
}
fn home() -> Result<PathBuf> {
    let path = PathBuf::from(std::env::var_os("HOME").context("Home directory unavailable")?);
    ensure!(path.is_absolute(), "Home directory must be absolute");
    Ok(path)
}
pub fn directory() -> Result<PathBuf> {
    let base = if cfg!(target_os = "macos") {
        home()?.join("Library/Application Support")
    } else {
        std::env::var_os("XDG_DATA_HOME")
            .map(PathBuf::from)
            .filter(|p| p.is_absolute())
            .unwrap_or(home()?.join(".local/share"))
    };
    Ok(base.join("lume/host"))
}
pub fn open(create: bool) -> Result<Arc<HostStore>> {
    ensure!(
        supported(),
        "Use the native Windows application for permanent access"
    );
    let path = directory()?.join("host.encrypted");
    ensure!(
        create || path.is_file(),
        "Permanent access is not configured"
    );
    let key = os_key(create && !path.exists())?;
    Ok(Arc::new(HostStore::open(path, *key)?))
}
#[cfg(target_os = "linux")]
fn os_key(create: bool) -> Result<Zeroizing<[u8; 32]>> {
    let tool = Path::new("/usr/bin/secret-tool");
    ensure!(
        tool.is_file(),
        "Install libsecret's secret-tool and unlock your desktop keyring before enabling access"
    );
    let attributes = [
        "application",
        "com.lume.remote",
        "component",
        "permanent-host",
    ];
    let mut lookup = Command::new(tool);
    lookup.arg("lookup").args(attributes);
    let (ok, text) = bounded(&mut lookup, None)?;
    if ok && !text.trim().is_empty() {
        let bytes = Zeroizing::new(lume_core::wire::hex(text.trim(), 32)?);
        let mut key = Zeroizing::new([0; 32]);
        key.copy_from_slice(&bytes);
        return Ok(key);
    }
    ensure!(
        create,
        "Unlock the desktop keyring containing this host's key; no plaintext fallback is used"
    );
    let token = Zeroizing::new(lume_core::paired::token(32)?);
    use base64::Engine;
    let bytes =
        Zeroizing::new(base64::engine::general_purpose::URL_SAFE_NO_PAD.decode(token.as_bytes())?);
    let encoded = Zeroizing::new(bytes.iter().map(|b| format!("{b:02x}")).collect::<String>());
    let mut save = Command::new(tool);
    save.args(["store", "--label=Lume permanent access"])
        .args(attributes);
    ensure!(
        bounded(&mut save, Some(encoded.as_bytes()))?.0,
        "The desktop keyring did not save the host key"
    );
    let mut key = Zeroizing::new([0; 32]);
    key.copy_from_slice(&bytes);
    Ok(key)
}
#[cfg(target_os = "macos")]
fn os_key(create: bool) -> Result<Zeroizing<[u8; 32]>> {
    use std::ffi::c_void;
    #[link(name = "Security", kind = "framework")]
    unsafe extern "C" {
        fn SecKeychainFindGenericPassword(
            keychain: *const c_void,
            service_len: u32,
            service: *const u8,
            account_len: u32,
            account: *const u8,
            length: *mut u32,
            data: *mut *mut c_void,
            item: *mut *mut c_void,
        ) -> i32;
        fn SecKeychainAddGenericPassword(
            keychain: *const c_void,
            service_len: u32,
            service: *const u8,
            account_len: u32,
            account: *const u8,
            length: u32,
            data: *const c_void,
            item: *mut *mut c_void,
        ) -> i32;
        fn SecKeychainItemFreeContent(attributes: *mut c_void, data: *mut c_void) -> i32;
    }
    let service = b"com.lume.remote";
    let account = b"permanent-host";
    let (mut length, mut data) = (0, std::ptr::null_mut());
    let status = unsafe {
        SecKeychainFindGenericPassword(
            std::ptr::null(),
            service.len() as u32,
            service.as_ptr(),
            account.len() as u32,
            account.as_ptr(),
            &mut length,
            &mut data,
            std::ptr::null_mut(),
        )
    };
    if status == 0 {
        let mut key = Zeroizing::new([0; 32]);
        let valid = length == 32 && !data.is_null();
        if valid {
            key.copy_from_slice(unsafe { std::slice::from_raw_parts(data.cast::<u8>(), 32) });
        }
        unsafe {
            SecKeychainItemFreeContent(std::ptr::null_mut(), data);
        }
        ensure!(valid, "Invalid host Keychain item");
        return Ok(key);
    }
    ensure!(
        status == -25300 && create,
        "Unlock and allow access to the host's macOS Keychain item"
    );
    use base64::Engine;
    let token = Zeroizing::new(lume_core::paired::token(32)?);
    let bytes =
        Zeroizing::new(base64::engine::general_purpose::URL_SAFE_NO_PAD.decode(token.as_bytes())?);
    let status = unsafe {
        SecKeychainAddGenericPassword(
            std::ptr::null(),
            service.len() as u32,
            service.as_ptr(),
            account.len() as u32,
            account.as_ptr(),
            32,
            bytes.as_ptr().cast(),
            std::ptr::null_mut(),
        )
    };
    ensure!(status == 0, "The Keychain did not save the host key");
    let mut key = Zeroizing::new([0; 32]);
    key.copy_from_slice(&bytes);
    Ok(key)
}
#[cfg(not(any(target_os = "linux", target_os = "macos")))]
fn os_key(_: bool) -> Result<Zeroizing<[u8; 32]>> {
    anyhow::bail!("Use the native Windows application for permanent access")
}

#[cfg(target_os = "linux")]
fn bounded(command: &mut Command, input: Option<&[u8]>) -> Result<(bool, Zeroizing<String>)> {
    let mut child = command
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::null())
        .spawn()?;
    if let Some(mut stdin) = child.stdin.take() {
        if let Some(bytes) = input {
            stdin.write_all(bytes)?;
        }
    }
    let start = Instant::now();
    let status = loop {
        if let Some(status) = child.try_wait()? {
            break status;
        }
        if start.elapsed() > Duration::from_secs(45) {
            let _ = child.kill();
            let _ = child.wait();
            anyhow::bail!("Unlock the desktop keyring and try again");
        }
        std::thread::sleep(Duration::from_millis(25));
    };
    let mut output = Zeroizing::new(String::new());
    child
        .stdout
        .take()
        .context("Missing keyring response")?
        .take(257)
        .read_to_string(&mut output)?;
    ensure!(output.len() <= 256, "Invalid keyring response");
    Ok((status.success(), output))
}
fn executable() -> Result<PathBuf> {
    let exe = std::env::current_exe()?;
    let text = exe
        .to_str()
        .context("Application path must be valid Unicode")?;
    ensure!(
        !text.chars().any(char::is_control),
        "Unsupported application path"
    );
    Ok(exe)
}
fn startup_path() -> Result<PathBuf> {
    if cfg!(target_os = "macos") {
        Ok(home()?.join("Library/LaunchAgents/com.lume.remote.host.plist"))
    } else {
        let config = std::env::var_os("XDG_CONFIG_HOME")
            .map(PathBuf::from)
            .filter(|p| p.is_absolute())
            .unwrap_or(home()?.join(".config"));
        Ok(config.join("autostart/com.lume.remote.host.desktop"))
    }
}
fn startup_text(exe: &str) -> String {
    if cfg!(target_os = "macos") {
        let exe = exe
            .replace('&', "&amp;")
            .replace('<', "&lt;")
            .replace('>', "&gt;")
            .replace('"', "&quot;")
            .replace('\'', "&apos;");
        format!(
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<!-- Lume managed permanent host -->\n<plist version=\"1.0\"><dict><key>Label</key><string>com.lume.remote.host</string><key>ProgramArguments</key><array><string>{exe}</string><string>--host-daemon</string></array><key>RunAtLoad</key><true/><key>ProcessType</key><string>Interactive</string><key>LimitLoadToSessionType</key><string>Aqua</string></dict></plist>\n"
        )
    } else {
        let exe = exe
            .replace('\\', "\\\\\\\\")
            .replace('"', "\\\\\"")
            .replace('`', "\\\\`")
            .replace('$', "\\\\$")
            .replace('%', "%%");
        format!(
            "# Lume managed permanent host\n[Desktop Entry]\nType=Application\nName=Lume permanent access\nComment=Access enabled by this computer's owner\nExec=\"{exe}\" --host-daemon\nTerminal=false\nX-GNOME-Autostart-enabled=true\n"
        )
    }
}
pub fn startup(enable: bool) -> Result<()> {
    let path = startup_path()?;
    if path.exists() {
        ensure!(
            !fs::symlink_metadata(&path)?.file_type().is_symlink()
                && fs::read_to_string(&path)?.contains("Lume managed permanent host"),
            "Existing startup configuration is not managed by Lume"
        );
    }
    if enable {
        fs::create_dir_all(path.parent().unwrap())?;
        let text = startup_text(executable()?.to_str().unwrap());
        let temp = path.with_extension(format!("{}.tmp", lume_core::paired::token(8)?));
        let mut options = fs::OpenOptions::new();
        options.write(true).create_new(true);
        #[cfg(unix)]
        {
            use std::os::unix::fs::OpenOptionsExt;
            options.mode(0o600);
        }
        let mut file = options.open(&temp)?;
        file.write_all(text.as_bytes())?;
        file.sync_all()?;
        drop(file);
        fs::rename(temp, path)?;
    } else if path.exists() {
        fs::remove_file(path)?;
    }
    Ok(())
}
pub fn spawn() -> Result<()> {
    let mut child = Command::new(executable()?)
        .arg("--host-daemon")
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .spawn()?;
    std::thread::spawn(move || {
        let _ = child.wait();
    });
    Ok(())
}
pub fn heartbeat() -> String {
    let result = (|| -> Result<String> {
        let path = directory()?.join("status.txt");
        let meta = fs::symlink_metadata(&path)?;
        ensure!(
            !meta.file_type().is_symlink()
                && meta.len() <= 512
                && meta.modified()?.elapsed()?.as_secs() < 20,
            "Host offline"
        );
        Ok(fs::read_to_string(path)?)
    })();
    result.unwrap_or_else(|_| "Host is not running".into())
}
pub fn daemon() -> Result<()> {
    ensure!(supported(), "This host process is for Linux/macOS");
    #[cfg(unix)]
    {
        unsafe extern "C" {
            fn geteuid() -> u32;
        }
        ensure!(
            unsafe { geteuid() } != 0,
            "Run the host as the desktop owner, not root"
        );
    }
    let store = open(false)?;
    let _guard = store.lock_named("daemon.lock")?;
    let config = store.load()?;
    if !config.enabled {
        return Ok(());
    }
    let mut host = PermanentHost::start(
        store.clone(),
        crate::peer_library()?,
        Arc::new(|config| {
            crate::native::NativeDesktop::open_shared(
                config.control,
                config.folder.as_ref().map(PathBuf::from),
            )
        }),
    )?;
    let status_path = store.path.with_file_name("status.txt");
    while !host.is_finished() {
        let mut options = fs::OpenOptions::new();
        options.write(true).create(true).truncate(true);
        #[cfg(unix)]
        {
            use std::os::unix::fs::OpenOptionsExt;
            options.mode(0o600);
        }
        if !status_path.exists() || !fs::symlink_metadata(&status_path)?.file_type().is_symlink() {
            if let Ok(mut file) = options.open(&status_path) {
                let _ = file.write_all(host.status.lock().unwrap().as_bytes());
            }
        }
        std::thread::sleep(Duration::from_secs(2));
    }
    host.close_and_wait();
    let _ = fs::remove_file(status_path);
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn startup_keeps_arguments_data_not_shell_code() {
        let text = startup_text("/home/owner/My App/Lume");
        assert!(text.contains("--host-daemon"));
        assert!(!text.contains("sh -c"));
        #[cfg(target_os = "linux")]
        assert!(text.contains("Exec=\"/home/owner/My App/Lume\" --host-daemon"));
    }
    #[cfg(target_os = "linux")]
    #[test]
    #[ignore = "Requires an isolated DBus/keyring and LUME_TEST_HOST_BINARY, never the physical desktop"]
    fn protected_keyring_startup_daemon_and_disable() {
        let binary = std::env::var("LUME_TEST_HOST_BINARY").expect("Owned Linux fixture binary");
        for key in ["XDG_DATA_HOME", "XDG_CONFIG_HOME"] {
            let p = std::env::var(key).unwrap();
            assert!(p.contains("lume-host-test") && Path::new(&p).is_absolute());
        }
        let store = open(true).unwrap();
        let host = store.load().unwrap().host.clone();
        drop(store);
        let store = open(false).unwrap();
        assert_eq!(store.load().unwrap().host, host);
        startup(true).unwrap();
        let path = startup_path().unwrap();
        assert!(fs::read_to_string(&path).unwrap().contains("--host-daemon"));
        startup(true).unwrap();
        // Validate the real runtime with a fresh test entry containing its actual executable.
        fs::write(&path, startup_text(&binary)).unwrap();
        store
            .change(|c| {
                c.enabled = true;
                Ok(())
            })
            .unwrap();
        struct Owned(Child);
        impl Drop for Owned {
            fn drop(&mut self) {
                let _ = self.0.kill();
                let _ = self.0.wait();
            }
        }
        use std::process::Child;
        let mut child = Owned(
            Command::new(&binary)
                .arg("--host-daemon")
                .stdin(Stdio::null())
                .stdout(Stdio::null())
                .stderr(Stdio::null())
                .spawn()
                .unwrap(),
        );
        let begin = Instant::now();
        while heartbeat() == "Host is not running" {
            assert!(child.0.try_wait().unwrap().is_none(), "Daemon exited");
            assert!(
                begin.elapsed() < Duration::from_secs(25),
                "No daemon heartbeat"
            );
            std::thread::sleep(Duration::from_millis(100));
        }
        let second = Command::new(&binary).arg("--host-daemon").status().unwrap();
        assert!(!second.success(), "Duplicate daemon was allowed");
        store
            .change(|c| {
                c.enabled = false;
                Ok(())
            })
            .unwrap();
        let begin = Instant::now();
        while child.0.try_wait().unwrap().is_none() {
            assert!(
                begin.elapsed() < Duration::from_secs(20),
                "Disabled host remained alive"
            );
            std::thread::sleep(Duration::from_millis(100));
        }
        startup(false).unwrap();
        assert!(!path.exists());
        assert!(!store.path.with_file_name("status.txt").exists());
        use std::os::unix::fs::PermissionsExt;
        assert_eq!(
            fs::metadata(&store.path).unwrap().permissions().mode() & 0o777,
            0o600
        );
    }
}
