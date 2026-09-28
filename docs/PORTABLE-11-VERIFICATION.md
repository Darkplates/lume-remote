# Portable 0.11 verification

Date: 2026-09-28. Local development checkpoint. Public-release acceptance and full
feature parity remain incomplete. The four 0.10 release artifacts retain their
recorded SHA-256 hashes. The installed Windows 0.3 host is untouched.

## Changes

- Portable viewers send and receive recursive folders over the existing negotiated
  file protocol. The worker preserves empty directories, creates a new root on each
  attempt, verifies each file, reports progress and supports cancellation. Completed
  items remain after cancellation; the active partial file is removed. Enumeration
  is bounded by depth and page size. Forged directory receipts, link traversal and
  nested directory collisions are rejected. Portable resumable jobs remain absent.
- Android uses the system document-tree picker and private streaming staging/export.
  iOS source uses a security-scoped folder picker and bounded streaming copies.
  No broad mobile storage permission or new dependency was added. Mobile storage
  provider behaviour has not been executed on a device.
- Portable MKV recording accepts Source or a custom 1-1000 FPS target. A one-slot
  frame notification replaces the fixed 33 ms sleep; encoding stays off the network
  thread and uses the latest image. Video timestamps are sampled before encoding.
  The worker finalizes on disconnect, display generation or resolution change.
  Microphones are excluded. Target FPS is not a throughput guarantee.

## Observed checks

| Layer | Result | Evidence boundary |
| --- | --- | --- |
| Windows application | 93 safe checks passed | Synthetic/local protocol, relay and native P2P; physical input and installed service excluded |
| Shared Rust on Windows | 42 core + 2 C ABI tests passed | Includes three native P2P tests run separately from the default suite |
| Folder interoperability | Both Windows/Rust directions passed | Actual authenticated synthetic sessions, nested/empty folders, Unicode, collisions, independent destination hashes and continued video |
| Source-target recording | 180 synthetic notified images independently decoded | 16x16 fixture, 126 distinct decoded colours, strictly increasing timestamps through 2.905 s; not real-time 180 FPS evidence |
| Stereo recording regression | 54 video frames and stereo PCM independently decoded | Synthetic TLS consent/decline/generation/toggle fixture; 37 video colours, 55,680 samples per channel and distinct stereo tones |
| Shared Rust on Alpine | 44 core + 2 C ABI tests passed | Owned QEMU TCG VM, including Unix filesystem checks and native P2P |
| Linux X11 | 2 native checks passed | Final optimized executable in owned Xvfb: dashboard rendering, capture, input release and Unicode clipboard |
| Linux host and audio | 1 lifecycle + 1 audio check passed | Private keyring, graphical-sign-in configuration, actual daemon, singleton/disable and PulseAudio null sink |
| Android build | ARM64/x86-64 native libraries, app and separate instrumentation APK compiled | Instrumentation was built, not executed |
| Android lint/package | 0 errors, 13 warnings; signature v2, CRCs, 16 KiB alignment and 8 native library identities passed | Static/container evidence; physical/emulator execution remains absent |
| Android copy helper | 3 JVM checks passed on the product helper | Byte preservation across chunks, cancellation before any read/write, and cancellation after the first completed chunk; no Android provider execution |
| Apple preparation | Shell syntax and expected non-Mac preflight rejection passed | No Apple SDK compilation, application execution or binary supplied |
| Dependency notices | 869 crate and 13 Rust distribution notice files match recorded hashes | Five known upstream notice gaps remain; hash agreement is not legal approval |

The Linux core suite took 143.97 s under QEMU TCG. Test duration is not application
FPS or a hardware benchmark. The final Linux executable inspected in Xvfb has
SHA-256 `5129b25160bd13d0676adc6e437e5d284c6a98953ef3f880aab3db8e8f01000f`.

## Validation corrections

Candidate failures remain in the local verification directory. A Rust fixture was
adjusted after a new private state field prevented its struct literal from compiling.
The first stereo fixture invocation supplied a relative path and was correctly
rejected; the absolute-path invocation passed. The first frame decoder wrapper hit
its metadata startup deadline during concurrent builds; a bounded direct FFmpeg
decoder passed on the same artifact. The initial Apple preflight assertion expected
exit 1; the script documents exit 2 for non-Mac hosts, and the corrected assertion
passed. These fixture/tool corrections are not evidence of Apple or device execution.

## Independent review

The requested AGY CLI review completed using `gemini-3.8-flash-high` with
`--effort high`. It inspected 39 unique current/baseline files and identified two P3
findings: missing single-file export cancellation on Android and an enabled desktop
folder upload action on legacy hosts. Both were confirmed in source and corrected.
The same model completed a focused re-review and marked both resolved, with no new
confirmed issue in those corrections. See [review and disposition](AGY-11-REVIEW.md).

An initial headless attempt returned no review because a terminal operation needed
interactive permission. That attempt is not counted as successful. The completed
run used built-in file-reading tools with existing permissions unchanged; nine file
lookup errors were recovered. Review evidence does not certify absence of defects
or satisfy the broader publication/security/device gates. No Opus review ran.

## Remaining boundaries

No Apple device is available. macOS and iOS/iPadOS have not been compiled or tested.
This owner-accepted preparation exception remains prominent in README and release
gates. Android device execution, physical multi-monitor/high-refresh GPU, audio
hardware, WAN, secure desktop, SMB, printers and wake/reboot remain unverified.
The Linux binary targets Alpine/musl x86-64 and has only isolated X11 VM evidence.

Portable resume, annotations, printing, mobile host roles, Wayland control and MP4
codec parity remain open. No measured CPU/RAM savings or superiority over commercial
remote-desktop software is claimed. No subscription, commercial-use detector,
telemetry or session-duration quota was introduced. No repository was published,
installed host updated or personal message sent.
