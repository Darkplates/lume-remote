use crate::{
    peer::validate_sdp,
    wire::{Invitation, Packet, Reader},
};
use anyhow::{Result, ensure};
use base64::{Engine, engine::general_purpose::URL_SAFE_NO_PAD};
use flate2::{Compression, read::GzDecoder, write::GzEncoder};
use hmac::{Hmac, Mac};
use sha2::Sha256;
use std::io::{Read, Write};
#[derive(Clone)]
pub struct Offer {
    pub id: String,
    pub invitation: Invitation,
    pub sdp: String,
}
impl Offer {
    pub fn new(invitation: Invitation, sdp: String) -> Result<Self> {
        validate_sdp(&sdp)?;
        let mut id = [0u8; 16];
        rustls::crypto::ring::default_provider()
            .secure_random
            .fill(&mut id)
            .map_err(|_| anyhow::anyhow!("Random generator failed"))?;
        Ok(Self {
            id: URL_SAFE_NO_PAD.encode(id),
            invitation,
            sdp,
        })
    }
    pub fn encode(&self) -> Result<String> {
        pack(
            "lume-p2p://",
            Packet::new(90)
                .int(1)
                .text(&self.id)
                .text(&self.invitation.encode())
                .text(&self.sdp),
        )
    }
    pub fn parse(text: &str) -> Result<Self> {
        let packet = unpack(text, "lume-p2p://")?;
        let mut r = Reader::new(&packet);
        ensure!(r.byte()? == 90 && r.int()? == 1, "Unsupported P2P offer");
        let id = r.text(64)?;
        ensure!(
            id.len() == 22 && URL_SAFE_NO_PAD.decode(&id)?.len() == 16,
            "Invalid P2P identifier"
        );
        let invitation = Invitation::parse(&r.text(4096)?)?;
        ensure!(invitation.room.is_empty(), "P2P offer contains a relay");
        let sdp = r.text(30000)?;
        validate_sdp(&sdp)?;
        r.end()?;
        Ok(Self {
            id,
            invitation,
            sdp,
        })
    }
    pub fn reply(&self, sdp: &str) -> Result<String> {
        validate_sdp(sdp)?;
        let key = URL_SAFE_NO_PAD.decode(&self.invitation.secret)?;
        let mut mac = Hmac::<Sha256>::new_from_slice(&key)?;
        mac.update(format!("{}\n{sdp}", self.id).as_bytes());
        pack(
            "lume-reply://",
            Packet::new(91)
                .int(1)
                .text(&self.id)
                .text(&URL_SAFE_NO_PAD.encode(mac.finalize().into_bytes()))
                .text(sdp),
        )
    }
    pub fn verify_reply(&self, text: &str) -> Result<String> {
        let packet = unpack(text, "lume-reply://")?;
        let mut r = Reader::new(&packet);
        ensure!(r.byte()? == 91 && r.int()? == 1, "Unsupported P2P reply");
        ensure!(
            r.text(64)? == self.id,
            "Reply belongs to another invitation"
        );
        let signature = r.text(128)?;
        ensure!(signature.len() == 43, "Invalid reply signature");
        let sdp = r.text(30000)?;
        validate_sdp(&sdp)?;
        r.end()?;
        let key = URL_SAFE_NO_PAD.decode(&self.invitation.secret)?;
        let mut mac = Hmac::<Sha256>::new_from_slice(&key)?;
        mac.update(format!("{}\n{sdp}", self.id).as_bytes());
        mac.verify_slice(&URL_SAFE_NO_PAD.decode(signature)?)?;
        Ok(sdp)
    }
}
fn pack(prefix: &str, packet: Packet) -> Result<String> {
    let mut gzip = GzEncoder::new(Vec::new(), Compression::fast());
    gzip.write_all(&packet.0)?;
    let bytes = gzip.finish()?;
    Ok(format!("{prefix}{}", URL_SAFE_NO_PAD.encode(bytes)))
}
fn unpack(text: &str, prefix: &str) -> Result<Vec<u8>> {
    ensure!(text.len() <= 65536, "P2P code exceeds its bound");
    let text = text
        .trim()
        .strip_prefix(prefix)
        .ok_or_else(|| anyhow::anyhow!("Paste the complete P2P code"))?;
    let bytes = URL_SAFE_NO_PAD.decode(text)?;
    let mut unpacked = Vec::new();
    GzDecoder::new(bytes.as_slice())
        .take(65537)
        .read_to_end(&mut unpacked)?;
    ensure!(
        (5..=65536).contains(&unpacked.len()),
        "Invalid expanded P2P code"
    );
    Ok(unpacked)
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn signed_exchange() {
        let id = crate::tls::identity("127.0.0.1".into(), 1).unwrap();
        let sdp="v=0\r\nm=application 9 UDP/DTLS/SCTP webrtc-datachannel\r\na=fingerprint:sha-256 00\r\na=ice-ufrag:test\r\na=ice-pwd:test\r\n".to_string();
        let offer = Offer::new(id.invite, sdp.clone()).unwrap();
        let parsed = Offer::parse(&offer.encode().unwrap()).unwrap();
        let reply = parsed.reply(&sdp).unwrap();
        assert_eq!(offer.verify_reply(&reply).unwrap(), sdp);
        let wrong = Offer::new(
            crate::tls::identity("127.0.0.1".into(), 1).unwrap().invite,
            sdp,
        )
        .unwrap();
        assert!(wrong.verify_reply(&reply).is_err());
        assert!(Offer::parse(&"x".repeat(65537)).is_err());
    }
}
