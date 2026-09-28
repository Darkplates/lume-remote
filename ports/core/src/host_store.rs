//! Owner-only authenticated host settings. The encryption key comes from the OS store.
use crate::paired::{self, PairingCode, SavedComputer};
use anyhow::{Context, Result, ensure};
use base64::{Engine, engine::general_purpose::URL_SAFE_NO_PAD};
use ring::{
    aead,
    rand::{SecureRandom, SystemRandom},
};
use serde::{Deserialize, Serialize};
use std::{
    fs::{self, File, OpenOptions},
    io::{Read, Write},
    path::{Path, PathBuf},
};
use zeroize::{Zeroize, ZeroizeOnDrop, Zeroizing};
const HEADER: &[u8] = b"LUMEHOST1";
const MAX: usize = 256 * 1024;

#[derive(Clone, Serialize, Deserialize, Zeroize, ZeroizeOnDrop)]
#[serde(deny_unknown_fields)]
pub struct HostConfig {
    pub host: String,
    pub broker_token: String,
    pub name: String,
    pub enabled: bool,
    pub control: bool,
    pub folder: Option<String>,
    pub pending: Option<PairingCode>,
    pub controllers: Vec<SavedComputer>,
}
impl HostConfig {
    fn new() -> Result<Self> {
        Ok(Self {
            host: format!("lume-{}", paired::id()?),
            broker_token: paired::token(32)?,
            name: "My computer".into(),
            enabled: false,
            control: false,
            folder: None,
            pending: None,
            controllers: Vec::new(),
        })
    }
    pub fn validate(&self) -> Result<()> {
        SavedComputer {
            host: self.host.clone(),
            id: "0".repeat(32),
            name: self.name.clone(),
            key: self.broker_token.clone(),
        }
        .validate()?;
        ensure!(
            self.controllers.len() <= 32,
            "Remove an unused paired computer first"
        );
        let mut ids = std::collections::HashSet::new();
        for c in &self.controllers {
            c.validate()?;
            ensure!(
                c.host == self.host && ids.insert(&c.id),
                "Invalid paired computer"
            );
        }
        if let Some(p) = &self.pending {
            SavedComputer {
                host: p.host.clone(),
                id: p.id.clone(),
                name: p.name.clone(),
                key: p.key.clone(),
            }
            .validate()?;
            ensure!(
                p.host == self.host && !p.wake && p.expires > 0,
                "Invalid pending pairing"
            );
        }
        if let Some(folder) = &self.folder {
            ensure!(
                self.control && Path::new(folder).is_absolute() && folder.len() <= 4096,
                "Choose an absolute shared folder and enable control"
            );
        }
        Ok(())
    }
    pub fn pairing(&mut self) -> Result<String> {
        ensure!(self.enabled, "Enable permanent access first");
        let p = PairingCode {
            host: self.host.clone(),
            id: paired::id()?,
            key: paired::token(32)?,
            name: self.name.clone(),
            expires: paired::now() + 6000000000,
            wake: false,
            mac: None,
        };
        let code = format!(
            "lume-pair://{}",
            URL_SAFE_NO_PAD.encode(Zeroizing::new(serde_json::to_vec(&p)?).as_slice())
        );
        self.pending = Some(p);
        Ok(code)
    }
    pub fn authorized(&self, controller: &SavedComputer) -> bool {
        self.enabled
            && self.controllers.iter().any(|c| {
                c.id == controller.id
                    && crate::wire::equal(c.key.as_bytes(), controller.key.as_bytes())
            })
    }
}

pub struct HostStore {
    pub path: PathBuf,
    key: Zeroizing<[u8; 32]>,
}
impl HostStore {
    pub fn open(path: PathBuf, key: [u8; 32]) -> Result<Self> {
        ensure!(path.is_absolute(), "Host settings path must be absolute");
        let parent = path.parent().context("Invalid host directory")?;
        if !parent.exists() {
            #[cfg(unix)]
            {
                use std::os::unix::fs::DirBuilderExt;
                fs::DirBuilder::new()
                    .recursive(true)
                    .mode(0o700)
                    .create(parent)?;
            }
            #[cfg(not(unix))]
            fs::create_dir_all(parent)?;
        }
        ensure!(
            !fs::symlink_metadata(parent)?.file_type().is_symlink(),
            "Host directory must not be a symbolic link"
        );
        #[cfg(unix)]
        {
            use std::os::unix::fs::MetadataExt;
            unsafe extern "C" {
                fn geteuid() -> u32;
            }
            let meta = fs::metadata(parent)?;
            ensure!(
                meta.uid() == unsafe { geteuid() } && meta.mode() & 0o077 == 0,
                "Host directory must belong only to this user (0700)"
            );
        }
        let store = Self {
            path,
            key: Zeroizing::new(key),
        };
        let _lock = store.lock()?;
        if !store.path.exists() {
            store.save(&HostConfig::new()?)?;
        }
        store.load()?;
        Ok(store)
    }
    fn options() -> OpenOptions {
        let mut options = OpenOptions::new();
        options.read(true).write(true);
        #[cfg(unix)]
        {
            use std::os::unix::fs::OpenOptionsExt;
            options.mode(0o600);
        }
        options
    }
    pub fn lock_named(&self, name: &str) -> Result<File> {
        ensure!(
            matches!(name, "host.lock" | "daemon.lock"),
            "Invalid host lock"
        );
        let path = self.path.with_file_name(name);
        if path.exists() {
            ensure!(
                !fs::symlink_metadata(&path)?.file_type().is_symlink(),
                "Unsafe host lock"
            );
        }
        let file = Self::options().create(true).truncate(false).open(path)?;
        file.try_lock()
            .map_err(|_| anyhow::anyhow!("Host settings or daemon are already in use"))?;
        Ok(file)
    }
    fn lock(&self) -> Result<File> {
        self.lock_named("host.lock")
    }
    pub fn load(&self) -> Result<HostConfig> {
        ensure!(
            !fs::symlink_metadata(&self.path)?.file_type().is_symlink(),
            "Unsafe host settings file"
        );
        let file = File::open(&self.path)?;
        ensure!(
            file.metadata()?.len() <= MAX as u64,
            "Host settings exceed their bound"
        );
        let mut bytes = Vec::new();
        file.take((MAX + 1) as u64).read_to_end(&mut bytes)?;
        ensure!(
            bytes.len() >= HEADER.len() + 28 && bytes.len() <= MAX && bytes.starts_with(HEADER),
            "Invalid protected host settings"
        );
        let cipher = self.cipher()?;
        let n = HEADER.len();
        let nonce = aead::Nonce::try_assume_unique_for_key(&bytes[n..n + 12])
            .map_err(|_| anyhow::anyhow!("Invalid nonce"))?;
        let mut data = Zeroizing::new(bytes[n + 12..].to_vec());
        let plain = cipher
            .open_in_place(nonce, aead::Aad::from(HEADER), &mut data)
            .map_err(|_| {
                anyhow::anyhow!("Host settings cannot be authenticated with this OS key")
            })?;
        let config: HostConfig = serde_json::from_slice(plain)?;
        config.validate()?;
        Ok(config)
    }
    pub fn change<T>(&self, change: impl FnOnce(&mut HostConfig) -> Result<T>) -> Result<T> {
        let _lock = self.lock()?;
        let mut config = self.load()?;
        let result = change(&mut config)?;
        config.validate()?;
        self.save(&config)?;
        Ok(result)
    }
    fn cipher(&self) -> Result<aead::LessSafeKey> {
        Ok(aead::LessSafeKey::new(
            aead::UnboundKey::new(&aead::AES_256_GCM, self.key.as_slice())
                .map_err(|_| anyhow::anyhow!("Invalid OS key"))?,
        ))
    }
    fn save(&self, config: &HostConfig) -> Result<()> {
        config.validate()?;
        let mut nonce = [0; 12];
        SystemRandom::new()
            .fill(&mut nonce)
            .map_err(|_| anyhow::anyhow!("Secure randomness unavailable"))?;
        let mut data = Zeroizing::new(serde_json::to_vec(config)?);
        self.cipher()?
            .seal_in_place_append_tag(
                aead::Nonce::assume_unique_for_key(nonce),
                aead::Aad::from(HEADER),
                &mut *data,
            )
            .map_err(|_| anyhow::anyhow!("Cannot protect host settings"))?;
        ensure!(
            data.len() + HEADER.len() + 12 <= MAX,
            "Host settings exceed their bound"
        );
        let temp = self
            .path
            .with_file_name(format!(".host-{}.tmp", paired::token(12)?));
        let result = (|| -> Result<()> {
            let mut file = Self::options().create_new(true).open(&temp)?;
            file.write_all(HEADER)?;
            file.write_all(&nonce)?;
            file.write_all(&data)?;
            file.sync_all()?;
            drop(file);
            fs::rename(&temp, &self.path)?;
            #[cfg(unix)]
            File::open(self.path.parent().unwrap())?.sync_all()?;
            Ok(())
        })();
        if result.is_err() {
            let _ = fs::remove_file(temp);
        }
        result
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn protected_host_pairing_revocation_and_tampering() {
        let dir = std::env::temp_dir().join(format!("lume-host-{}", paired::token(12).unwrap()));
        let path = dir.join("host.encrypted");
        let store = HostStore::open(path.clone(), [71; 32]).unwrap();
        let code = store
            .change(|c| {
                c.enabled = true;
                c.control = true;
                c.pairing()
            })
            .unwrap();
        let pending = PairingCode::parse(&code).unwrap();
        assert!(!String::from_utf8_lossy(&fs::read(&path).unwrap()).contains(&pending.key));
        assert!(HostStore::open(path.clone(), [72; 32]).is_err());
        let controller = SavedComputer {
            host: pending.host.clone(),
            id: pending.id.clone(),
            key: paired::token(32).unwrap(),
            name: "Fixture".into(),
        };
        store
            .change(|c| {
                c.controllers.push(controller.clone());
                c.pending = None;
                Ok(())
            })
            .unwrap();
        assert!(store.load().unwrap().authorized(&controller));
        store
            .change(|c| {
                c.controllers.clear();
                Ok(())
            })
            .unwrap();
        assert!(!store.load().unwrap().authorized(&controller));
        let lock = store.lock().unwrap();
        assert!(
            store
                .change(|c| {
                    c.enabled = false;
                    Ok(())
                })
                .is_err()
        );
        drop(lock);
        let mut data = fs::read(&path).unwrap();
        *data.last_mut().unwrap() ^= 1;
        fs::write(&path, data).unwrap();
        assert!(store.load().is_err());
        fs::remove_file(path).unwrap();
        fs::remove_file(dir.join("host.lock")).unwrap();
        fs::remove_dir(dir).unwrap();
    }
}
