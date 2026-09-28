//! Windows-compatible encrypted rendezvous. The broker never receives desktop keys or SDP.
use crate::{peer::Peer, signal::Offer, wire};
use aes::cipher::{BlockDecryptMut, BlockEncryptMut, KeyIvInit, block_padding::Pkcs7};
use anyhow::{Context, Result, bail, ensure};
use base64::{Engine, engine::general_purpose::URL_SAFE_NO_PAD};
use hmac::{Hmac, Mac};
use serde::{Deserialize, Serialize};
use sha2::Sha256;
use std::{
    net::{TcpStream, ToSocketAddrs},
    path::Path,
    sync::atomic::{AtomicBool, Ordering},
    thread,
    time::{Duration, Instant, SystemTime, UNIX_EPOCH},
};
use tungstenite::{Message, WebSocket, stream::MaybeTlsStream};
use zeroize::{Zeroize, ZeroizeOnDrop, Zeroizing};

const ENDPOINT: &str = "wss://0.peerjs.com/peerjs";
const MAX_SIGNAL: usize = 196608;
type Hmac256 = Hmac<Sha256>;

pub fn token(n: usize) -> Result<String> {
    let mut bytes = Zeroizing::new(vec![0; n]);
    rustls::crypto::ring::default_provider()
        .secure_random
        .fill(&mut bytes)
        .map_err(|_| anyhow::anyhow!("Secure randomness unavailable"))?;
    Ok(URL_SAFE_NO_PAD.encode(&*bytes))
}
pub(crate) fn id() -> Result<String> {
    let bytes = URL_SAFE_NO_PAD.decode(token(16)?)?;
    Ok(bytes.iter().map(|b| format!("{b:02x}")).collect())
}
pub fn now() -> i64 {
    621355968000000000
        + (SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap_or_default()
            .as_nanos()
            / 100) as i64
}
fn name(value: &str) -> Result<()> {
    ensure!(
        !value.trim().is_empty()
            && value.chars().count() <= 80
            && !value.chars().any(char::is_control),
        "Choose a name of 1–80 printable characters"
    );
    Ok(())
}
fn host(value: &str) -> Result<()> {
    wire::hex(
        value.strip_prefix("lume-").context("Invalid saved host")?,
        16,
    )?;
    Ok(())
}
fn key(value: &str) -> Result<Zeroizing<Vec<u8>>> {
    ensure!(value.len() == 43, "Invalid computer key");
    let bytes = Zeroizing::new(URL_SAFE_NO_PAD.decode(value)?);
    ensure!(bytes.len() == 32, "Invalid computer key");
    Ok(bytes)
}
#[derive(Clone, Serialize, Deserialize, Zeroize, ZeroizeOnDrop)]
#[serde(deny_unknown_fields)]
pub struct SavedComputer {
    pub host: String,
    pub id: String,
    pub name: String,
    pub key: String,
}
impl SavedComputer {
    pub fn validate(&self) -> Result<()> {
        host(&self.host)?;
        wire::hex(&self.id, 16)?;
        name(&self.name)?;
        key(&self.key)?;
        Ok(())
    }
    /// Internal application handoff; never display, log, or copy this value.
    pub fn connection(&self) -> Result<String> {
        self.validate()?;
        Ok(format!(
            "lume-saved://{}",
            URL_SAFE_NO_PAD.encode(Zeroizing::new(serde_json::to_vec(self)?).as_slice())
        ))
    }
    pub fn parse(value: &str) -> Result<Self> {
        ensure!(value.len() <= 4096, "Saved credential exceeds its bound");
        let plain = Zeroizing::new(
            URL_SAFE_NO_PAD.decode(
                value
                    .strip_prefix("lume-saved://")
                    .context("Invalid saved computer")?,
            )?,
        );
        let result: Self = serde_json::from_slice(&plain)?;
        result.validate()?;
        Ok(result)
    }
}
#[derive(Clone, Serialize, Deserialize, Zeroize, ZeroizeOnDrop)]
pub struct PairingCode {
    pub host: String,
    pub id: String,
    pub key: String,
    pub name: String,
    pub expires: i64,
    #[serde(default)]
    pub wake: bool,
    #[serde(default)]
    pub mac: Option<String>,
}
impl PairingCode {
    pub fn parse(value: &str) -> Result<Self> {
        ensure!(value.len() <= 4096, "Pairing code exceeds its bound");
        let plain = Zeroizing::new(
            URL_SAFE_NO_PAD.decode(
                value
                    .trim()
                    .strip_prefix("lume-pair://")
                    .context("Paste a one-time desktop pairing code")?,
            )?,
        );
        let result: Self = serde_json::from_slice(&plain)?;
        host(&result.host)?;
        wire::hex(&result.id, 16)?;
        key(&result.key)?;
        name(&result.name)?;
        ensure!(!result.wake, "Use a desktop pairing code");
        ensure!(
            result.expires >= now() && result.expires <= now() + 36000000000,
            "Pairing code expired. Create a new one on the sharing computer"
        );
        Ok(result)
    }
}
#[derive(Clone, Serialize, Deserialize)]
pub struct Envelope {
    pub v: i32,
    pub route: String,
    pub request: String,
    pub stage: String,
    pub r#box: String,
}
#[derive(Default, Serialize, Deserialize, Zeroize, ZeroizeOnDrop)]
pub struct Body {
    pub created: i64,
    pub name: Option<String>,
    pub key: Option<String>,
    pub code: Option<String>,
    pub message: Option<String>,
    pub mac: Option<String>,
}
fn derive(secret: &str, purpose: &str) -> Result<Zeroizing<[u8; 32]>> {
    let mut mac = Hmac256::new_from_slice(&key(secret)?)?;
    mac.update(format!("Lume signaling v1 / {purpose}").as_bytes());
    Ok(Zeroizing::new(mac.finalize().into_bytes().into()))
}
fn header(source: &str, destination: &str, envelope: &Envelope) -> Result<String> {
    host(source)?;
    host(destination)?;
    wire::hex(&envelope.route, 16)?;
    wire::hex(&envelope.request, 16)?;
    ensure!(
        envelope.v == 1
            && !envelope.stage.is_empty()
            && envelope.stage.len() <= 20
            && envelope.stage.bytes().all(|b| b.is_ascii_lowercase()),
        "Invalid signaling stage"
    );
    Ok(format!(
        "1\n{source}\n{destination}\n{}\n{}\n{}\n",
        envelope.route, envelope.request, envelope.stage
    ))
}
pub fn seal(
    secret: &str,
    source: &str,
    destination: &str,
    route: &str,
    request: &str,
    stage: &str,
    mut body: Body,
) -> Result<Envelope> {
    let mut env = Envelope {
        v: 1,
        route: route.into(),
        request: request.into(),
        stage: stage.into(),
        r#box: String::new(),
    };
    let header = header(source, destination, &env)?;
    body.created = now();
    let plain = Zeroizing::new(serde_json::to_vec(&body)?);
    ensure!(plain.len() <= 98304, "Signaling payload exceeds its bound");
    let iv = URL_SAFE_NO_PAD.decode(token(16)?)?;
    let encrypted = cbc::Encryptor::<aes::Aes256>::new_from_slices(
        derive(secret, "encryption")?.as_slice(),
        &iv,
    )
    .map_err(|_| anyhow::anyhow!("Invalid encryption key"))?
    .encrypt_padded_vec_mut::<Pkcs7>(&plain);
    let mut packed = iv;
    packed.extend(encrypted);
    let mut mac = Hmac256::new_from_slice(derive(secret, "authentication")?.as_slice())?;
    mac.update(header.as_bytes());
    mac.update(&packed);
    packed.extend(mac.finalize().into_bytes());
    env.r#box = URL_SAFE_NO_PAD.encode(packed);
    Ok(env)
}
pub fn open(secret: &str, source: &str, destination: &str, env: &Envelope) -> Result<Body> {
    let header = header(source, destination, env)?;
    ensure!(
        env.r#box.len() <= 140000,
        "Signaling payload exceeds its bound"
    );
    let packed = URL_SAFE_NO_PAD.decode(&env.r#box)?;
    ensure!(
        packed.len() >= 64 && (packed.len() - 48).is_multiple_of(16),
        "Invalid signaling envelope"
    );
    let (encrypted, tag) = packed.split_at(packed.len() - 32);
    let mut mac = Hmac256::new_from_slice(derive(secret, "authentication")?.as_slice())?;
    mac.update(header.as_bytes());
    mac.update(encrypted);
    mac.verify_slice(tag)
        .map_err(|_| anyhow::anyhow!("Signaling authentication failed"))?;
    // Authenticate header and ciphertext before attempting CBC decryption.
    let plain = Zeroizing::new(
        cbc::Decryptor::<aes::Aes256>::new_from_slices(
            derive(secret, "encryption")?.as_slice(),
            &encrypted[..16],
        )
        .map_err(|_| anyhow::anyhow!("Invalid encryption key"))?
        .decrypt_padded_vec_mut::<Pkcs7>(&encrypted[16..])
        .map_err(|_| anyhow::anyhow!("Signaling authentication failed"))?,
    );
    ensure!(plain.len() <= 98304, "Signaling payload exceeds its bound");
    let body: Body = serde_json::from_slice(&plain)?;
    ensure!(
        (body.created as i128 - now() as i128).abs() <= 3000000000,
        "Signaling request expired. Check both clocks"
    );
    Ok(body)
}
pub(crate) struct Broker {
    socket: WebSocket<MaybeTlsStream<TcpStream>>,
    pub(crate) id: String,
    ping: Instant,
}
impl Broker {
    fn connect(stop: &AtomicBool) -> Result<Self> {
        let id = format!("lume-{}", id()?);
        Self::connect_as(id, &token(32)?, stop)
    }
    pub(crate) fn connect_as(id: String, token: &str, stop: &AtomicBool) -> Result<Self> {
        host(&id)?;
        key(token)?;
        let request = format!("{ENDPOINT}?key=peerjs&id={id}&token={token}");
        let mut socket = None;
        for address in ("0.peerjs.com", 443).to_socket_addrs()?.take(8) {
            ensure!(!stop.load(Ordering::Acquire), "Connection cancelled");
            if let Ok(tcp) = TcpStream::connect_timeout(&address, Duration::from_secs(5)) {
                socket = Some(tcp);
                break;
            }
        }
        let tcp = socket.ok_or(crate::recovery::Unavailable(
            "The signaling service is unavailable",
        ))?;
        tcp.set_read_timeout(Some(Duration::from_secs(12)))?;
        tcp.set_write_timeout(Some(Duration::from_secs(12)))?;
        let config = tungstenite::protocol::WebSocketConfig::default()
            .max_message_size(Some(MAX_SIGNAL))
            .max_frame_size(Some(MAX_SIGNAL))
            .write_buffer_size(0)
            .max_write_buffer_size(2 * MAX_SIGNAL);
        let (mut socket, _) =
            tungstenite::client_tls_with_config(request.as_str(), tcp, Some(config), None)?;
        match socket.get_mut() {
            MaybeTlsStream::Plain(tcp) => tcp.set_nonblocking(true)?,
            MaybeTlsStream::Rustls(tls) => tls.sock.set_nonblocking(true)?,
            _ => bail!("Unsupported signaling transport"),
        }
        let mut broker = Self {
            socket,
            id,
            ping: Instant::now(),
        };
        let deadline = Instant::now();
        loop {
            ensure!(!stop.load(Ordering::Acquire), "Connection cancelled");
            ensure!(
                deadline.elapsed() < Duration::from_secs(15),
                "The signaling service did not answer"
            );
            if let Some(packet) = broker.read()? {
                ensure!(
                    packet["type"] == "OPEN",
                    "The signaling service rejected the connection"
                );
                return Ok(broker);
            }
            thread::sleep(Duration::from_millis(10));
        }
    }
    pub(crate) fn read(&mut self) -> Result<Option<serde_json::Value>> {
        if self.ping.elapsed() > Duration::from_secs(5) {
            self.send_json(serde_json::json!({"type":"HEARTBEAT"}))?;
            self.ping = Instant::now();
        }
        match self.socket.read() {
            Ok(Message::Text(text)) => Ok(Some(serde_json::from_str(&text)?)),
            Ok(Message::Ping(_) | Message::Pong(_)) => Ok(None),
            Ok(Message::Close(_)) => {
                return Err(
                    crate::recovery::Unavailable("The signaling service disconnected").into(),
                );
            }
            Ok(_) => bail!("Invalid signaling message"),
            Err(tungstenite::Error::Io(e)) if e.kind() == std::io::ErrorKind::WouldBlock => {
                Ok(None)
            }
            Err(tungstenite::Error::ConnectionClosed | tungstenite::Error::AlreadyClosed) => {
                return Err(
                    crate::recovery::Unavailable("The signaling service disconnected").into(),
                );
            }
            Err(tungstenite::Error::Io(e)) => Err(e.into()),
            Err(e) => Err(e.into()),
        }
    }
    fn send_json(&mut self, packet: serde_json::Value) -> Result<()> {
        let text = serde_json::to_string(&packet)?;
        ensure!(
            text.len() <= MAX_SIGNAL,
            "Signaling message exceeds its bound"
        );
        match self.socket.send(Message::Text(text.into())) {
            Ok(()) => Ok(()),
            Err(tungstenite::Error::Io(e)) if e.kind() == std::io::ErrorKind::WouldBlock => Ok(()),
            Err(tungstenite::Error::ConnectionClosed | tungstenite::Error::AlreadyClosed) => {
                return Err(
                    crate::recovery::Unavailable("The signaling service disconnected").into(),
                );
            }
            Err(tungstenite::Error::Io(e)) => Err(e.into()),
            Err(e) => Err(e.into()),
        }
    }
    pub(crate) fn send(&mut self, destination: &str, env: Envelope) -> Result<()> {
        let answer = matches!(env.stage.as_str(), "answer" | "paired" | "woke");
        self.send_json(serde_json::json!({"type":if answer {"ANSWER"} else {"OFFER"},"dst":destination,
            "payload":{"type":"data","connectionId":format!("dc_{}",env.request),"label":"lume-encrypted","serialization":"raw","browser":"Lume","reliable":true,
            "sdp":{"type":if answer {"answer"} else {"offer"},"sdp":"v=0\r\no=- 0 0 IN IP4 127.0.0.1\r\ns=Lume encrypted signaling\r\nt=0 0\r\nm=application 9 UDP/DTLS/SCTP webrtc-datachannel\r\nc=IN IP4 0.0.0.0\r\na=mid:0\r\na=sctp-port:5000\r\n"},"metadata":env}}))
    }
    fn reply(
        &mut self,
        source: &str,
        route: &str,
        request: &str,
        stop: &AtomicBool,
        timeout: u64,
    ) -> Result<Envelope> {
        let start = Instant::now();
        let mut ignored = 0;
        loop {
            ensure!(!stop.load(Ordering::Acquire), "Connection cancelled");
            if start.elapsed() >= Duration::from_secs(timeout) {
                return Err(crate::recovery::Unavailable(
                    "The saved computer did not answer. It may be asleep or offline",
                )
                .into());
            }
            if let Some(packet) = self.read()? {
                if packet["type"] == "EXPIRE" {
                    return Err(crate::recovery::Unavailable(
                        "The saved computer is offline or permanent access is disabled",
                    )
                    .into());
                }
                if packet["src"] == source {
                    if let Ok(env) =
                        serde_json::from_value::<Envelope>(packet["payload"]["metadata"].clone())
                    {
                        if env.route == route && env.request == request {
                            return Ok(env);
                        }
                    }
                }
                ignored += 1;
                ensure!(ignored <= 128, "Too many unexpected signaling messages");
            }
            thread::sleep(Duration::from_millis(10));
        }
    }
}
pub fn pair(code: PairingCode, stop: &AtomicBool) -> Result<SavedComputer> {
    ensure!(code.expires >= now(), "Pairing code expired");
    let mut broker = Broker::connect(stop)?;
    let request = id()?;
    let new_key = token(32)?;
    let envelope = seal(
        &code.key,
        &broker.id,
        &code.host,
        &code.id,
        &request,
        "pair",
        Body {
            name: Some("Lume portable viewer".into()),
            key: Some(new_key.clone()),
            created: 0,
            code: None,
            message: None,
            mac: None,
        },
    )?;
    broker.send(&code.host, envelope)?;
    let reply = broker.reply(&code.host, &code.id, &request, stop, 25)?;
    if reply.stage == "error" {
        let _ = open(&code.key, &code.host, &broker.id, &reply)?;
        bail!("Pairing was rejected. Create a fresh code on the host");
    }
    ensure!(reply.stage == "paired", "Unexpected pairing response");
    let body = open(&new_key, &code.host, &broker.id, &reply)?;
    let saved = SavedComputer {
        host: code.host.clone(),
        id: code.id.clone(),
        key: new_key,
        name: body.name.clone().context("Missing computer name")?,
    };
    saved.validate()?;
    Ok(saved)
}
pub fn connect(
    saved: &SavedComputer,
    library: &Path,
    stop: &AtomicBool,
) -> Result<(wire::Invitation, Peer)> {
    saved.validate()?;
    let mut broker = Broker::connect(stop)?;
    let request = id()?;
    let envelope = seal(
        &saved.key,
        &broker.id,
        &saved.host,
        &saved.id,
        &request,
        "connect",
        Body::default(),
    )?;
    broker.send(&saved.host, envelope)?;
    let reply = broker.reply(&saved.host, &saved.id, &request, stop, 40)?;
    let body = open(&saved.key, &saved.host, &broker.id, &reply)?;
    if reply.stage == "error"
        && matches!(
            body.message.as_deref(),
            Some("busy" | "This PC already has a session or connection attempt.")
        )
    {
        return Err(crate::recovery::Unavailable("The host is finishing another session").into());
    }
    ensure!(
        reply.stage == "offer",
        "The host rejected the connection. Check that this computer is still paired"
    );
    let offer = Offer::parse(body.code.as_deref().context("Missing connection offer")?)?;
    let peer = Peer::new(library, true)?;
    let answer = peer.answer_cancellable(&offer.sdp, Some(stop))?;
    broker.send(
        &saved.host,
        seal(
            &saved.key,
            &broker.id,
            &saved.host,
            &saved.id,
            &request,
            "answer",
            Body {
                code: Some(offer.reply(&answer)?),
                created: 0,
                name: None,
                key: None,
                message: None,
                mac: None,
            },
        )?,
    )?;
    // Drain buffered signaling writes before closing the broker.
    let flush_start = Instant::now();
    loop {
        ensure!(!stop.load(Ordering::Acquire), "Connection cancelled");
        match broker.socket.flush() {
            Ok(()) => break,
            Err(tungstenite::Error::Io(e)) if e.kind() == std::io::ErrorKind::WouldBlock => {}
            Err(_) => bail!("Unable to send connection answer"),
        }
        ensure!(
            flush_start.elapsed() < Duration::from_secs(12),
            "Connection answer timed out"
        );
        thread::sleep(Duration::from_millis(5));
    }
    peer.ready(Some(stop))?;
    Ok((offer.invitation, peer))
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn encrypted_signaling_rejects_tampering_routing_and_wrong_key() {
        let k = token(32).unwrap();
        let a = format!("lume-{}", id().unwrap());
        let b = format!("lume-{}", id().unwrap());
        let env = seal(
            &k,
            &a,
            &b,
            &id().unwrap(),
            &id().unwrap(),
            "connect",
            Body {
                name: Some("Owned fixture".into()),
                created: 0,
                key: None,
                code: None,
                message: None,
                mac: None,
            },
        )
        .unwrap();
        assert_eq!(
            open(&k, &a, &b, &env).unwrap().name.as_deref(),
            Some("Owned fixture")
        );
        assert!(open(&k, &b, &a, &env).is_err());
        assert!(open(&token(32).unwrap(), &a, &b, &env).is_err());
        let mut changed = env.clone();
        changed.stage = "pair".into();
        assert!(open(&k, &a, &b, &changed).is_err());
        let mut bytes = URL_SAFE_NO_PAD.decode(&env.r#box).unwrap();
        bytes[18] ^= 1;
        changed = env;
        changed.r#box = URL_SAFE_NO_PAD.encode(bytes);
        assert!(open(&k, &a, &b, &changed).is_err());
    }
    #[test]
    fn saved_codes_validate_before_any_network_access() {
        let saved = SavedComputer {
            host: format!("lume-{}", id().unwrap()),
            id: id().unwrap(),
            key: token(32).unwrap(),
            name: "Owned computer".into(),
        };
        let encoded = saved.connection().unwrap();
        assert_eq!(SavedComputer::parse(&encoded).unwrap().id, saved.id);
        assert!(SavedComputer::parse("lume-saved://AAAA").is_err());
        let mut bad = saved.clone();
        bad.host.push('\n');
        assert!(bad.validate().is_err());
        let value = serde_json::json!({"host":saved.host,"id":saved.id,"key":saved.key,"name":saved.name,"expires":now()-1,"wake":false});
        assert!(
            PairingCode::parse(&format!(
                "lume-pair://{}",
                URL_SAFE_NO_PAD.encode(value.to_string())
            ))
            .is_err()
        );
    }
}
