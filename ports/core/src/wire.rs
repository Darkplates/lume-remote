use anyhow::{Result, ensure};
use base64::{Engine, engine::general_purpose::URL_SAFE_NO_PAD};
use std::{
    fmt,
    io::{Read, Write},
    net::IpAddr,
};

pub const MAX_PACKET: usize = 144 * 1024 * 1024;
pub const PORTABLE_IMAGES: u64 = 2048;

#[derive(Clone)]
pub struct Invitation {
    pub host: String,
    pub port: u16,
    pub room: String,
    pub secret: String,
    pub pin: [u8; 32],
}
impl fmt::Debug for Invitation {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str("Invitation(<private>)")
    }
}
impl Invitation {
    pub fn parse(text: &str) -> Result<Self> {
        ensure!(text.len() <= 4096, "Invitation exceeds its bound");
        let raw = URL_SAFE_NO_PAD.decode(
            text.trim()
                .strip_prefix("lume://")
                .ok_or_else(|| anyhow::anyhow!("Paste a complete Lume invitation"))?,
        )?;
        let text = std::str::from_utf8(&raw)?;
        let p: Vec<_> = text.split('|').collect();
        ensure!(p.len() == 6 && p[0] == "1", "Unsupported invitation");
        let port: u16 = p[2].parse()?;
        ensure!(port != 0 && valid_host(p[1]), "Invalid network address");
        ensure!(
            p[3].is_empty() || hex(p[3], 16).is_ok(),
            "Invalid relay room"
        );
        ensure!(
            p[4].len() == 43 && URL_SAFE_NO_PAD.decode(p[4])?.len() == 32,
            "Invalid invitation key"
        );
        let pin: [u8; 32] = hex(p[5], 32)?
            .try_into()
            .map_err(|_| anyhow::anyhow!("Invalid pin"))?;
        Ok(Self {
            host: p[1].into(),
            port,
            room: p[3].into(),
            secret: p[4].into(),
            pin,
        })
    }
    pub fn encode(&self) -> String {
        let pin: String = self.pin.iter().map(|b| format!("{b:02X}")).collect();
        format!(
            "lume://{}",
            URL_SAFE_NO_PAD.encode(format!(
                "1|{}|{}|{}|{}|{}",
                self.host, self.port, self.room, self.secret, pin
            ))
        )
    }
}
fn valid_host(host: &str) -> bool {
    if host.len() > 253 || host.is_empty() {
        return false;
    }
    if let Ok(ip) = host.parse::<IpAddr>() {
        return !ip.is_unspecified();
    }
    host.split('.').all(|s| {
        !s.is_empty()
            && s.len() <= 63
            && !s.starts_with('-')
            && !s.ends_with('-')
            && s.bytes().all(|b| b.is_ascii_alphanumeric() || b == b'-')
    })
}
pub fn hex(value: &str, size: usize) -> Result<Vec<u8>> {
    ensure!(
        value.len() == size * 2 && value.is_ascii(),
        "Invalid hexadecimal value"
    );
    (0..size)
        .map(|i| Ok(u8::from_str_radix(&value[i * 2..i * 2 + 2], 16)?))
        .collect()
}
pub fn equal(a: &[u8], b: &[u8]) -> bool {
    let mut difference = a.len() ^ b.len();
    for i in 0..a.len().max(b.len()) {
        difference |= (a.get(i).copied().unwrap_or(0) ^ b.get(i).copied().unwrap_or(0)) as usize;
    }
    difference == 0
}

pub struct Reader<'a> {
    bytes: &'a [u8],
    offset: usize,
}
impl<'a> Reader<'a> {
    pub fn new(bytes: &'a [u8]) -> Self {
        Self { bytes, offset: 0 }
    }
    pub fn take(&mut self, n: usize) -> Result<&'a [u8]> {
        ensure!(n <= self.bytes.len() - self.offset, "Truncated packet");
        let p = &self.bytes[self.offset..self.offset + n];
        self.offset += n;
        Ok(p)
    }
    pub fn byte(&mut self) -> Result<u8> {
        Ok(self.take(1)?[0])
    }
    pub fn boolean(&mut self) -> Result<bool> {
        let b = self.byte()?;
        ensure!(b <= 1, "Invalid boolean");
        Ok(b != 0)
    }
    pub fn int(&mut self) -> Result<i32> {
        Ok(i32::from_le_bytes(self.take(4)?.try_into()?))
    }
    pub fn long(&mut self) -> Result<i64> {
        Ok(i64::from_le_bytes(self.take(8)?.try_into()?))
    }
    pub fn ulong(&mut self) -> Result<u64> {
        Ok(u64::from_le_bytes(self.take(8)?.try_into()?))
    }
    pub fn text(&mut self, maximum: usize) -> Result<String> {
        let n = self.int()?;
        ensure!(n >= 0 && n as usize <= maximum, "Invalid string length");
        Ok(std::str::from_utf8(self.take(n as usize)?)?.to_owned())
    }
    pub fn end(&self) -> Result<()> {
        ensure!(self.offset == self.bytes.len(), "Unexpected trailing bytes");
        Ok(())
    }
    pub fn remaining(&self) -> usize {
        self.bytes.len() - self.offset
    }
}
#[derive(Clone)]
pub struct Packet(pub Vec<u8>);
impl Packet {
    pub fn new(kind: u8) -> Self {
        Self(vec![kind])
    }
    pub fn byte(mut self, v: u8) -> Self {
        self.0.push(v);
        self
    }
    pub fn int(mut self, v: i32) -> Self {
        self.0.extend(v.to_le_bytes());
        self
    }
    pub fn long(mut self, v: i64) -> Self {
        self.0.extend(v.to_le_bytes());
        self
    }
    pub fn ulong(mut self, v: u64) -> Self {
        self.0.extend(v.to_le_bytes());
        self
    }
    pub fn text(mut self, v: &str) -> Self {
        self.0.extend((v.len() as i32).to_le_bytes());
        self.0.extend(v.as_bytes());
        self
    }
    pub fn bytes(mut self, v: &[u8]) -> Self {
        self.0.extend(v);
        self
    }
    pub fn framed(&self) -> Result<Vec<u8>> {
        ensure!(
            !self.0.is_empty() && self.0.len() <= MAX_PACKET,
            "Invalid packet size"
        );
        let mut v = Vec::with_capacity(self.0.len() + 4);
        v.extend((self.0.len() as i32).to_le_bytes());
        v.extend(&self.0);
        Ok(v)
    }
}
#[derive(Default)]
pub struct Framer {
    bytes: Vec<u8>,
    consumed: usize,
}
impl Framer {
    pub fn push(&mut self, bytes: &[u8]) -> Result<()> {
        ensure!(
            self.bytes.len() - self.consumed + bytes.len() <= MAX_PACKET + 65540,
            "Receive buffer exceeded"
        );
        if self.consumed > 0 {
            self.bytes.drain(..self.consumed);
            self.consumed = 0;
        }
        self.bytes.extend_from_slice(bytes);
        Ok(())
    }
    pub fn next_packet(&mut self) -> Result<Option<Packet>> {
        let a = &self.bytes[self.consumed..];
        if a.len() < 4 {
            return Ok(None);
        }
        let n = i32::from_le_bytes(a[..4].try_into()?);
        ensure!(n > 0 && n as usize <= MAX_PACKET, "Invalid packet size");
        if a.len() < n as usize + 4 {
            return Ok(None);
        }
        let result = Packet(a[4..4 + n as usize].to_vec());
        self.consumed += n as usize + 4;
        Ok(Some(result))
    }
}
pub fn read_packet(stream: &mut impl Read, maximum: usize) -> Result<Packet> {
    let mut head = [0; 4];
    stream.read_exact(&mut head)?;
    let n = i32::from_le_bytes(head);
    ensure!(
        n > 0 && n as usize <= maximum.min(MAX_PACKET),
        "Invalid packet size"
    );
    let mut v = vec![0; n as usize];
    stream.read_exact(&mut v)?;
    Ok(Packet(v))
}
pub fn write_packet(stream: &mut impl Write, packet: Packet) -> Result<()> {
    stream.write_all(&packet.framed()?)?;
    stream.flush()?;
    Ok(())
}

#[derive(Clone, Copy, Debug)]
pub struct Quality {
    pub height: i32,
    pub fps: i32,
    pub jpeg: i32,
    pub lossless: bool,
}
impl Default for Quality {
    fn default() -> Self {
        Self {
            height: 1080,
            fps: 60,
            jpeg: 85,
            lossless: false,
        }
    }
}
impl Quality {
    pub fn validate(&self) -> Result<()> {
        ensure!(
            (self.height == 0 || (120..=16384).contains(&self.height))
                && (-1..=1000).contains(&self.fps)
                && (10..=100).contains(&self.jpeg),
            "Invalid quality target"
        );
        Ok(())
    }
    pub fn read(r: &mut Reader, version: i32) -> Result<Self> {
        let q = Self {
            height: r.int()?,
            fps: r.int()?,
            jpeg: r.int()?,
            lossless: r.boolean()?,
        };
        if version >= 3 {
            let video = r.boolean()?;
            let bitrate = r.int()?;
            ensure!(
                !video && (250..=100000).contains(&bitrate),
                "This portable host currently accepts image modes"
            );
        }
        q.validate()?;
        Ok(q)
    }
    pub fn write(&self, p: Packet, version: i32) -> Packet {
        let p = p
            .int(self.height)
            .int(self.fps)
            .int(self.jpeg)
            .byte(self.lossless as u8);
        if version >= 3 { p.byte(0).int(8000) } else { p }
    }
    pub fn target_fps(&self, hz: u32) -> u32 {
        if self.fps <= 0 {
            hz.clamp(1, 1000)
        } else {
            self.fps as u32
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn fragmented_packets() {
        let p = Packet::new(12).text("test").framed().unwrap();
        let mut f = Framer::default();
        for b in &p[..p.len() - 1] {
            f.push(&[*b]).unwrap();
            assert!(f.next_packet().unwrap().is_none())
        }
        f.push(&p[p.len() - 1..]).unwrap();
        let p = f.next_packet().unwrap().unwrap();
        let mut r = Reader::new(&p.0[1..]);
        assert_eq!(r.text(4).unwrap(), "test");
        r.end().unwrap();
    }
    #[test]
    fn malformed_and_bounded() {
        for n in [-1, 0, i32::MAX] {
            let mut f = Framer::default();
            f.push(&n.to_le_bytes()).unwrap();
            assert!(f.next_packet().is_err())
        }
        assert!(Reader::new(&[2]).boolean().is_err());
        assert!(Reader::new(&[255; 4]).text(100).is_err());
        assert!(!equal(b"key", b"ke"));
    }
    #[test]
    fn invitations_and_secrets() {
        let invite = Invitation {
            host: "localhost".into(),
            port: 4567,
            room: String::new(),
            secret: URL_SAFE_NO_PAD.encode([5; 32]),
            pin: [9; 32],
        };
        let parsed = Invitation::parse(&invite.encode()).unwrap();
        assert_eq!(parsed.pin, invite.pin);
        assert!(!format!("{invite:?}").contains(&invite.secret));
        assert!(Invitation::parse("lume://bad").is_err());
        for host in ["0.0.0.0", "::", "bad/host", "-host", "a..b"] {
            assert!(!valid_host(host));
        }
    }
    #[test]
    fn quality_targets() {
        for fps in [10, 180, 0, -1, 1000] {
            let q = Quality {
                fps,
                ..Default::default()
            };
            q.validate().unwrap();
        }
        assert!(
            Quality {
                fps: 1001,
                ..Default::default()
            }
            .validate()
            .is_err()
        );
    }
}
