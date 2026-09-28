//! Desktop vault: Argon2id + AES-256-GCM, unlocked once per application run.
use crate::paired::SavedComputer;
use anyhow::{Context, Result, ensure};
use argon2::{Algorithm, Argon2, Params, Version};
use ring::{
    aead,
    rand::{SecureRandom, SystemRandom},
};
use std::{
    fs::{self, OpenOptions},
    io::{Read, Write},
    path::{Path, PathBuf},
};
use zeroize::Zeroizing;

const HEADER: &[u8] = b"LUMEVLT1";
const MAX_BYTES: usize = 128 * 1024;
pub struct Vault {
    path: PathBuf,
    salt: [u8; 16],
    key: Zeroizing<[u8; 32]>,
    pub computers: Vec<SavedComputer>,
}
impl Vault {
    pub fn unlock(path: &Path, password: &str) -> Result<Self> {
        ensure!(path.is_absolute(), "Vault path must be absolute");
        ensure!(
            password.chars().count() >= 12 && password.len() <= 1024,
            "Use a vault passphrase of 12–1024 bytes"
        );
        let mut salt = [0; 16];
        let bytes = match fs::File::open(path) {
            Ok(file) => {
                ensure!(
                    file.metadata()?.len() <= MAX_BYTES as u64,
                    "Invalid vault size"
                );
                let mut v = Vec::new();
                file.take((MAX_BYTES + 1) as u64).read_to_end(&mut v)?;
                Some(v)
            }
            Err(e) if e.kind() == std::io::ErrorKind::NotFound => None,
            Err(e) => return Err(e.into()),
        };
        if let Some(bytes) = &bytes {
            ensure!(
                bytes.len() >= 52 && bytes.len() <= MAX_BYTES && bytes.starts_with(HEADER),
                "Invalid vault"
            );
            salt.copy_from_slice(&bytes[8..24]);
        } else {
            SystemRandom::new()
                .fill(&mut salt)
                .map_err(|_| anyhow::anyhow!("Secure randomness unavailable"))?;
        }
        let mut key = Zeroizing::new([0; 32]);
        Argon2::new(
            Algorithm::Argon2id,
            Version::V0x13,
            Params::new(19456, 2, 1, Some(32))
                .map_err(|_| anyhow::anyhow!("Invalid vault parameters"))?,
        )
        .hash_password_into(password.as_bytes(), &salt, key.as_mut())
        .map_err(|_| anyhow::anyhow!("Unable to unlock vault"))?;
        let mut vault = Self {
            path: path.into(),
            salt,
            key,
            computers: Vec::new(),
        };
        if let Some(bytes) = bytes {
            let mut encrypted = Zeroizing::new(bytes[36..].to_vec());
            let cipher = aead::LessSafeKey::new(
                aead::UnboundKey::new(&aead::AES_256_GCM, vault.key.as_slice())
                    .map_err(|_| anyhow::anyhow!("Invalid vault key"))?,
            );
            let plain = cipher
                .open_in_place(
                    aead::Nonce::try_assume_unique_for_key(&bytes[24..36])
                        .map_err(|_| anyhow::anyhow!("Invalid vault nonce"))?,
                    aead::Aad::from(&bytes[..24]),
                    &mut encrypted,
                )
                .map_err(|_| anyhow::anyhow!("Wrong passphrase or damaged vault"))?;
            vault.computers = serde_json::from_slice(plain)?;
            ensure!(vault.computers.len() <= 64, "Too many saved computers");
            let mut ids = std::collections::HashSet::new();
            for c in &vault.computers {
                c.validate()?;
                ensure!(ids.insert(c.id.clone()), "Duplicate saved computer");
            }
        } else {
            vault.persist()?;
        }
        Ok(vault)
    }
    pub fn save(&mut self, computer: SavedComputer) -> Result<()> {
        computer.validate()?;
        let previous = self.computers.clone();
        self.computers.retain(|c| c.id != computer.id);
        ensure!(
            self.computers.len() < 64,
            "Remove an unused saved computer first"
        );
        self.computers.push(computer);
        if let Err(e) = self.persist() {
            self.computers = previous;
            return Err(e);
        }
        Ok(())
    }
    pub fn remove(&mut self, id: &str) -> Result<()> {
        let previous = self.computers.clone();
        self.computers.retain(|c| c.id != id);
        if let Err(e) = self.persist() {
            self.computers = previous;
            return Err(e);
        }
        Ok(())
    }
    fn persist(&self) -> Result<()> {
        let parent = self.path.parent().context("Invalid vault directory")?;
        fs::create_dir_all(parent)?;
        let mut nonce = [0; 12];
        SystemRandom::new()
            .fill(&mut nonce)
            .map_err(|_| anyhow::anyhow!("Secure randomness unavailable"))?;
        let mut bytes = HEADER.to_vec();
        bytes.extend(self.salt);
        let mut encrypted = Zeroizing::new(serde_json::to_vec(&self.computers)?);
        let cipher = aead::LessSafeKey::new(
            aead::UnboundKey::new(&aead::AES_256_GCM, self.key.as_slice())
                .map_err(|_| anyhow::anyhow!("Invalid vault key"))?,
        );
        cipher
            .seal_in_place_append_tag(
                aead::Nonce::assume_unique_for_key(nonce),
                aead::Aad::from(bytes.as_slice()),
                &mut *encrypted,
            )
            .map_err(|_| anyhow::anyhow!("Unable to protect saved computers"))?;
        bytes.extend(nonce);
        bytes.extend_from_slice(&encrypted);
        ensure!(bytes.len() <= MAX_BYTES, "Vault exceeds its bound");
        let temp = parent.join(format!(".lume-{}.tmp", crate::paired::token(16)?));
        let result = (|| -> Result<()> {
            let mut options = OpenOptions::new();
            options.write(true).create_new(true);
            #[cfg(unix)]
            {
                use std::os::unix::fs::OpenOptionsExt;
                options.mode(0o600);
            }
            let mut file = options.open(&temp)?;
            file.write_all(&bytes)?;
            file.sync_all()?;
            drop(file);
            fs::rename(&temp, &self.path)?;
            Ok(())
        })();
        if result.is_err() {
            let _ = fs::remove_file(&temp);
        }
        result
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn vault_round_trip_wrong_password_tamper_and_remove() {
        let dir = std::env::temp_dir().join(format!(
            "lume-vault-test-{}",
            crate::paired::token(12).unwrap()
        ));
        fs::create_dir(&dir).unwrap();
        let path = dir.join("computers.vault");
        let password = "isolated test passphrase";
        let secret = crate::paired::token(32).unwrap();
        let mut vault = Vault::unlock(&path, password).unwrap();
        vault
            .save(SavedComputer {
                host: format!("lume-{}", "a".repeat(32)),
                id: "b".repeat(32),
                name: "Fixture".into(),
                key: secret.clone(),
            })
            .unwrap();
        drop(vault);
        let bytes = fs::read(&path).unwrap();
        assert!(!bytes.windows(secret.len()).any(|w| w == secret.as_bytes()));
        assert!(Vault::unlock(&path, "incorrect test passphrase").is_err());
        let mut vault = Vault::unlock(&path, password).unwrap();
        assert_eq!(vault.computers.len(), 1);
        vault.remove(&"b".repeat(32)).unwrap();
        drop(vault);
        assert!(Vault::unlock(&path, password).unwrap().computers.is_empty());
        let mut bytes = fs::read(&path).unwrap();
        *bytes.last_mut().unwrap() ^= 1;
        fs::write(&path, bytes).unwrap();
        assert!(Vault::unlock(&path, password).is_err());
        fs::remove_file(path).unwrap();
        fs::remove_dir(dir).unwrap();
    }
}
