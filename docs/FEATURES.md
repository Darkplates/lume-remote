# Feature status - 0.12.1 development preview

Public development source is authorized. Stable binary release and full parity remain **not ready**.
Implemented and validated are separate states. The owner accepts Apple preparation
without compilation/testing because no Apple devices are available. This explicit
exception does not establish that the untested macOS/iOS source builds or runs.

| Capability | Implementation | Evidence / remaining boundary |
| --- | --- | --- |
| Windows view/control, pairing, revoke | Implemented | Local native TLS/P2P; current two-PC WAN acceptance pending |
| Short-code pairing (Windows) | Implemented | 8-digit code as a public-broker rendezvous, committed P-256 key exchange and a 6-digit number confirmed on both PCs before the one-time pairing code is sent encrypted; the full code still works. Offline handshake/substitution checks run in every suite; the public-broker round trip runs only with `--signal`. Two-PC and WAN acceptance pending |
| Guest invitation links (Windows) | Implemented | Link to the static `docs/open.html` page on GitHub Pages, which hands the fragment-held invitation to the per-user `lume-open:` handler; Lume always asks before connecting. Link round-trip and rejection checks run in every suite; the page and its hosting are a trust root (see SECURITY.md). Browser and real-PC acceptance pending |
| Automatic guest reply (Windows P2P) | Implemented, with manual fallback | Reply sealed under the invitation secret and returned through the public broker; only the first matching reply is accepted. Offline identity checks in every suite; public-broker delivery only with `--signal`. Reported connected on two PCs on one LAN; two-network acceptance pending |
| Background host, reconnect, tray, multiple remote PCs | Implemented | Local recovery/multiple-session tests; one incoming viewer per host |
| Source/custom resolution/FPS; lossless/JPEG/H.264 | Implemented | Codec/quality tests; targets are not measured throughput |
| Explicit clipboard send and receive | Implemented | STA, consent wait, error recovery and authenticated receipts |
| Paired text clipboard sync | Implemented, opt-in | Change/echo/conflict/disconnect tests; OS clipboard coexistence pending |
| Files and recursive folders, both directions | Implemented | SHA-256, collisions, cancel, nested and empty-folder checks |
| Paired single-file resume | Implemented | Actual upload/download interruption, authenticated state, prefix/corruption/cancel checks; no automatic folder-job resume |
| Owner-configured network shares | Implemented | Allowlist and root discovery tests; actual SMB endpoint not tested |
| Live monitor selection | Implemented | Synthetic geometry/negative origin and stale-input rejection; physical multi-monitor tests pending |
| Session chat | Implemented | Native local chat delivery; bounded session-only history |
| Visible annotations | Implemented | Coordinates/parser/render tests; physical remote overlay commissioning pending |
| System audio | Implemented, opt-in | Exact PCM/compression bounds and denial tests; hardware/WAN playback untested |
| Microphone / two-way voice | Implemented, consented | Both users opt in, host visible Stop control; denial/cancellation tested, real devices untested |
| MP4 video with optional AAC system audio | Implemented | Independent stereo tone/video decoding; fixed canvas, target FPS, normal-close finalization |
| Recording source/custom FPS | Implemented target | Synthetic 180 distinct images in one second independently decoded; no real-time 180 FPS claim |
| Paired lock/restart/shutdown | Implemented | Safe callbacks only; native power/lock/reboot commissioning pending |
| Unattended wake | Conditional implementation | Requires a wake path and compatible physical hardware; unverified |
| Remote PDF printing | Implemented forwarding | Windows PDF rendering and real Microsoft Print to PDF spool passed; physical printer untested; no virtual Lume driver |
| Portable TLS/P2P and PNG source images | Implemented | Windows/Rust TLS in both directions, native P2P and exact synthetic pixels passed |
| Linux native host/viewer | Partial implementation | 0.12 Alpine x86-64: 50 core + 2 C ABI, 1 printer-name parser, 2 native UI/X11, 1 protected keyring/daemon and 1 virtual audio check passed in an isolated VM with the final optimized executable. The 0.8 checkpoint retains actual Linux-host/Windows file/folder evidence; other distributions, physical desktops and Wayland control remain unverified/unavailable |
| macOS native host/viewer | Partial implementation | Native adapters/build recipe exist; Apple build/execution unverified; macOS has not been tested |
| Android viewer | Partial implementation | 0.12 APK and separate test APK built for ARM64/x86-64; signature, native input identity and alignment verified; printing/annotations compiled; no physical-device/emulator/WAN acceptance |
| iOS/iPadOS viewer | Partial implementation | SwiftUI source, C ABI and XCFramework recipe; Apple build/execution unverified |
| Portable saved access, recovery and files | Implemented | One-time pairing, fresh P2P reconnect, live revocation, bounded paged file viewer and scoped desktop file host. Recovery/TLS-generation/cancellation fixtures passed. Mobile UI/storage execution remains unverified. |
| Portable permanent desktop host | Implemented user-session process | Linux keyring, sign-in configuration, actual daemon, singleton and disable passed in an owned VM. macOS LaunchAgent/Keychain source uncompiled. No pre-login service, wake or process-crash supervisor. |
| Portable sound / consented voice | Implemented adapters | Windows/Rust PCM interoperability, synthetic consent/decline/generation tests and actual Linux null-sink playback/capture passed. Physical devices, mobile execution and Apple compilation unverified. |
| Portable recording | Implemented MKV with source/custom FPS targets | 180 synthetic notified frames independently decoded with increasing timestamps; target is not achieved device/network FPS. Current stereo regression independently decoded: 54 frames and 55,680 PCM samples per channel. Excludes microphones; stops on recovery/display/resolution change. Mobile execution and MP4 codec parity remain open. |
| Portable viewer folder jobs | Implemented | Recursive/empty folders, Unicode, paginated listing, collision-safe roots, forged receipt rejection and cancel/follow-up checks passed in the shared core. Android document-tree staging/export and iOS folder-picker source are prepared; actual mobile storage providers remain unverified. Retry creates a new root; folder-job resume remains missing. |
| Portable display selection | Implemented | Authenticated synthetic list/switch, negative origins, held-key release, stale-image/input rejection and missing-display recovery passed. Windows/Rust interoperability passed in both directions. Physical multi-monitor and mobile UI execution remain separate. |
| Portable paired single-file resume | Implemented | Authenticated checkpoints, file locks, verified suffix retry, wrong-key isolation, corrupt-prefix cancellation and legacy fallback. Mobile cache survives automatic reconnection but is removed on explicit session close. |
| Portable viewer annotations | Implemented | Draw/clear controls and generation checks; Windows host overlay only. Linux/macOS hosts do not advertise this feature. Physical pen/touch/overlay acceptance remains pending. |
| Portable PDF printing | Platform adapters | Linux CUPS selection, Android system panel with bounded raster rendering, macOS PDFKit/iOS print-panel source. Physical printers/mobile execution and Apple compilation remain unverified. |
| Portable mobile hosts and remaining parity | Missing | No Android/iOS host; portable host annotation overlays, secure desktop and MP4 codec parity remain open. Viewer annotations require a Windows host; PDF adapters have explicit format and platform limits. Physical high-refresh acceptance is separate from the implemented recording target. |
| Portable high-refresh presentation | Implemented scheduling | Desktop frame wakeups on a coalescing worker; Android Choreographer; iOS CADisplayLink source. A deliberately stalled presentation test preserves frames/ACKs. Actual device FPS, power use and high-refresh hardware acceptance remain unverified. |
| TURN fleet, signed/automatic updates | Pending | External service/signing infrastructure is not bundled |
| AI-model code review (not an independent audit) | Scoped static review of 0.11 changes | AGY CLI Gemini 3.8 Flash High inspected 0.11 changes; two P3 findings were corrected and re-reviewed. This was an automated model review, not an independent security audit; none has been performed. See AGY-11-REVIEW.md. |

No commercial duration quota, subscription, telemetry or commercial-use detector is
implemented. Timeouts, queue bounds and physical network/power failures still exist.
No measured superiority or full commercial-product parity is claimed.
