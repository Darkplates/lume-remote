use crate::wire::{Invitation, equal};
use anyhow::Result;
use base64::{Engine, engine::general_purpose::URL_SAFE_NO_PAD};
use rustls::{
    ClientConfig, DigitallySignedStruct, Error, ServerConfig, SignatureScheme,
    client::danger::{HandshakeSignatureValid, ServerCertVerified, ServerCertVerifier},
    pki_types::{CertificateDer, PrivateKeyDer, PrivatePkcs8KeyDer, ServerName, UnixTime},
};
use sha2::{Digest, Sha256};
use std::sync::Arc;

#[derive(Debug)]
struct PinnedCertificate {
    pin: [u8; 32],
    provider: rustls::crypto::CryptoProvider,
}
impl ServerCertVerifier for PinnedCertificate {
    fn verify_server_cert(
        &self,
        cert: &CertificateDer<'_>,
        _: &[CertificateDer<'_>],
        _: &ServerName<'_>,
        _: &[u8],
        _: UnixTime,
    ) -> std::result::Result<ServerCertVerified, Error> {
        if !equal(&Sha256::digest(cert.as_ref()), &self.pin) {
            return Err(Error::General(
                "The host certificate does not match this invitation".into(),
            ));
        }
        Ok(ServerCertVerified::assertion())
    }
    fn verify_tls12_signature(
        &self,
        message: &[u8],
        cert: &CertificateDer<'_>,
        signature: &DigitallySignedStruct,
    ) -> std::result::Result<HandshakeSignatureValid, Error> {
        rustls::crypto::verify_tls12_signature(
            message,
            cert,
            signature,
            &self.provider.signature_verification_algorithms,
        )
    }
    fn verify_tls13_signature(
        &self,
        message: &[u8],
        cert: &CertificateDer<'_>,
        signature: &DigitallySignedStruct,
    ) -> std::result::Result<HandshakeSignatureValid, Error> {
        rustls::crypto::verify_tls13_signature(
            message,
            cert,
            signature,
            &self.provider.signature_verification_algorithms,
        )
    }
    fn supported_verify_schemes(&self) -> Vec<SignatureScheme> {
        self.provider
            .signature_verification_algorithms
            .supported_schemes()
    }
}
pub fn client(pin: [u8; 32]) -> Result<Arc<ClientConfig>> {
    let provider = rustls::crypto::ring::default_provider();
    Ok(Arc::new(
        ClientConfig::builder_with_provider(Arc::new(provider.clone()))
            .with_safe_default_protocol_versions()?
            .dangerous()
            .with_custom_certificate_verifier(Arc::new(PinnedCertificate { pin, provider }))
            .with_no_client_auth(),
    ))
}
pub struct Identity {
    pub config: Arc<ServerConfig>,
    pub invite: Invitation,
}
pub fn identity(host: String, port: u16) -> Result<Identity> {
    let key = rcgen::KeyPair::generate()?;
    let mut params = rcgen::CertificateParams::new(vec!["lume-remote".into()])?;
    params
        .distinguished_name
        .push(rcgen::DnType::CommonName, "Lume Remote Session v4");
    let cert = params.self_signed(&key)?;
    let provider = rustls::crypto::ring::default_provider();
    let mut secret = [0u8; 32];
    provider
        .secure_random
        .fill(&mut secret)
        .map_err(|_| anyhow::anyhow!("Secure random generation failed"))?;
    let config = ServerConfig::builder_with_provider(Arc::new(provider))
        .with_safe_default_protocol_versions()?
        .with_no_client_auth()
        .with_single_cert(
            vec![cert.der().clone()],
            PrivateKeyDer::Pkcs8(PrivatePkcs8KeyDer::from(key.serialize_der())),
        )?;
    let pin = Sha256::digest(cert.der()).into();
    Ok(Identity {
        config: Arc::new(config),
        invite: Invitation {
            host,
            port,
            room: String::new(),
            secret: URL_SAFE_NO_PAD.encode(secret),
            pin,
        },
    })
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn exact_pin() {
        let id = identity("localhost".into(), 1).unwrap();
        let other = identity("localhost".into(), 1).unwrap();
        assert_ne!(id.invite.pin, other.invite.pin);
        assert_ne!(id.invite.secret, other.invite.secret);
        assert_eq!(id.invite.secret.len(), 43);
    }
}
