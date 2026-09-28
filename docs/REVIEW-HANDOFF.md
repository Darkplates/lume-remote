# Review handoff

The requested Opus review has not run. The companion output
LumeRemote-Opus-review.txt contains the complete reusable review prompt.

Read AGENTS.md, PARITY-CONTRACT.md, FEATURES.md, release-gates.json,
PROTOCOL-V4.md, SECURITY.md and VALIDATION.md first. Compare against the
immutable 0.7 source archive for the 0.8 changes, and the 0.4 archive for the
earlier feature expansion, rather than an absent Git baseline commit.

Prioritize protocol downgrade/layout safety; owner-token filesystem permissions;
display generation/input races; tools cancellation/worker isolation; clipboard
conflicts and reconnect; WASAPI privacy/device failure; MP4 start/stop/disk-full/
close races; overlay lifetime; fixed power-action authorization; packaging.

For 0.7, also review ports/core TLS/codec downgrade and approval gates; P2P callback
retirement on failed/cancelled handshakes; bounded C ABI/JNI buffers; Android service
and bitmap lifecycle; Apple build/link settings and permission boundaries; and
portable capability claims. Windows/Rust fixtures and cross-compilation do not
prove Linux, macOS, Android or iOS execution. The 0.8 Linux VM has separate native
and file-interoperability evidence in VALIDATION.md. Portable permanent-host
services, recovery, media and Wayland control remain missing. Review third-party/portable
inventory gaps before distributing any portable binary.

For 0.8, prioritize authenticated broker envelopes/freshness, credential retention
and protected desktop/Android/iOS stores; bounded file workers, unsolicited-write
rejection, scoped roots/symlinks, cancellation and no-overwrite publication;
mobile document-picker session routing and staging cleanup. File-worker unit tests
and Linux/Windows fixtures do not establish mobile storage/lifecycle acceptance.

Do not repeat the previously cancelled UAC update or interrupt the installed 0.3
session without fresh owner authorization. Do not publish with required gates open.
Synthetic tests do not replace native hardware/WAN/other-OS acceptance.
