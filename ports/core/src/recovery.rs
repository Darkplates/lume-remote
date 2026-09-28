//! Retry only transport availability failures, never protocol/authentication errors.
use std::{fmt, io, time::Duration};

#[derive(Debug)]
pub struct Unavailable(pub &'static str);
impl fmt::Display for Unavailable {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(self.0)
    }
}
impl std::error::Error for Unavailable {}
#[derive(Debug)]
pub struct ProtocolFailure(anyhow::Error);
impl fmt::Display for ProtocolFailure {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "Invalid remote payload: {}", self.0)
    }
}
impl std::error::Error for ProtocolFailure {
    fn source(&self) -> Option<&(dyn std::error::Error + 'static)> {
        Some(self.0.as_ref())
    }
}
pub fn protocol(error: anyhow::Error) -> anyhow::Error {
    ProtocolFailure(error).into()
}
pub fn retryable(error: &anyhow::Error) -> bool {
    // Image/audio decoders can report UnexpectedEof for a complete but malformed
    // payload. That is not an interrupted transport and must never trigger retry.
    if error.chain().any(|cause| cause.is::<ProtocolFailure>()) {
        return false;
    }
    error.chain().any(|cause| {
        cause.is::<Unavailable>()
            || cause.downcast_ref::<io::Error>().is_some_and(|e| {
                matches!(
                    e.kind(),
                    io::ErrorKind::TimedOut
                        | io::ErrorKind::ConnectionRefused
                        | io::ErrorKind::ConnectionReset
                        | io::ErrorKind::ConnectionAborted
                        | io::ErrorKind::BrokenPipe
                        | io::ErrorKind::UnexpectedEof
                        | io::ErrorKind::NotConnected
                        | io::ErrorKind::NetworkUnreachable
                        | io::ErrorKind::HostUnreachable
                )
            })
    })
}
pub fn delay(attempt: u32) -> Duration {
    Duration::from_millis((1000u64 << attempt.saturating_sub(1).min(5)).min(30000))
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn codec_eof_is_terminal_while_transport_eof_retries() {
        assert!(retryable(
            &io::Error::from(io::ErrorKind::UnexpectedEof).into()
        ));
        assert!(!retryable(&protocol(
            io::Error::from(io::ErrorKind::UnexpectedEof).into()
        )));
        let mut packet = crate::wire::Packet::new(4)
            .int(1)
            .int(1)
            .int(64)
            .int(48)
            .int(1)
            .int(0)
            .int(0)
            .int(64)
            .int(48)
            .byte(3)
            .int(8);
        packet.0.extend([137, 80, 78, 71, 13, 10, 26, 10]);
        let error = crate::frame::Decoder::default()
            .apply(&packet, 4)
            .map_err(protocol)
            .unwrap_err();
        assert!(!retryable(&error));
    }
}
