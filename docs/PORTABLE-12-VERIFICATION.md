# Portable 0.12 verification

Date: 2026-09-28. Public development source preview, not a stable binary release or
full cross-platform parity. The installed Windows 0.3 host is unchanged, and the
0.11 archives are preserved. Apple hardware is unavailable: macOS and iOS/iPadOS
remain uncompiled and untested.

## Changes

- Paired portable single-file transfers negotiate the existing resume capability.
  Journals bind the paired credential, canonical destination, filename, length and
  full SHA-256. Retried transfers rehash the retained prefix before sending only the
  suffix, then verify the complete identity before collision-safe publication.
  File locks reject concurrent reuse. Explicit cancellation and integrity failure
  remove the active partial. Guest and legacy operations retain their behavior.
  Folder jobs remain non-resumable. Mobile session caches survive automatic
  reconnection but are removed on explicit session close.
- Portable viewer drawing releases held input, suppresses keyboard/mouse injection,
  bounds each stroke to 128 points and uses the displayed generation. Windows hosts
  show the existing temporary overlay. Linux/macOS hosts do not advertise overlays.
- Completed PDF downloads can be copied into private bounded snapshots, then sent
  to a local printing adapter after explicit selection. Linux uses CUPS; Android
  uses the system panel and bounded selected-page rasterization; Apple print-panel
  source is prepared. This is PDF forwarding, not a virtual printer driver.
- Notice provenance covers all 496 locked registry packages. 493 use exact-package
  or exact-commit notices, two use explicitly identified later-upstream supplements,
  and one Apple-only dispatch notice gap remains. All 874 recorded notice or
  declaration files match their hashes. See [provenance](LICENSE-PROVENANCE.md).
- Public source checks, issue/PR templates, build workflows, distribution guidance
  and a [measured launch plan](LAUNCH-PLAN.md) are prepared.

## Observed checks

| Layer | Result | Boundary |
| --- | --- | --- |
| Windows application | 93 safe checks passed | Local/synthetic protocol, relay and native P2P; no installed-host or physical-WAN acceptance |
| Shared Rust on Windows | 48 core + 2 C ABI tests passed | Includes three native P2P tests, authenticated resume, annotations and PDF snapshot checks |
| Paired Windows/Rust interoperability | Passed | One-time pairing, actual encrypted P2P resume operations in both directions, annotation clear, stale-generation denial, two connections and live revocation; synthetic capture |
| Legacy files and portable host | Passed | Recursive/empty folders, Unicode, collisions, SHA-256, video continuity and both Windows/Rust host directions |
| Portable permanent host | Passed | Windows viewer pairing, two P2P connections, live revoke and rejected key reuse; isolated credentials and synthetic capture |
| Shared Rust on Alpine | 50 core + 2 C ABI checks passed | Actual owned QEMU TCG execution, including Unix filesystem cases and native P2P |
| Linux native desktop | 2 X11 + 1 host/keyring + 1 virtual PCM check passed | Optimized app, isolated software-rendered display, owned input/clipboard, real daemon and virtual audio |
| Linux print destination parser | 1 check passed | Rejects option-shaped or shell-like names; no CUPS spool or physical printer acceptance |
| Android build | ARM64/x86-64 native libraries, app and separate instrumentation APK compiled | Instrumentation built, not executed |
| Android package/lint | 0 errors, 13 warnings; signature, CRCs, 16 KiB alignment and eight native input identities passed | Static/container validation; no physical or emulator execution |
| Android stream helper | 3 JVM checks passed | Actual shared copy helper, byte preservation and cancellation; no storage-provider/device execution |
| Apple preparation | Shell syntax passed | No Apple SDK compilation, execution or binary provided |

The interrupted-transfer tests create new file workers after interruption and count
actual chunk payloads, asserting that the second attempt sends only the missing
suffix. They also exercise wrong keys, corrupt prefixes, concurrent locks, final
identity changes and forged legacy acknowledgements. Product interoperability uses
actual Windows and Rust peers but does not simulate every router or storage device.

## Review and remaining acceptance

The earlier AGY Gemini 3.8 Flash High review covered 0.11; it is not represented as
an independent review of these new 0.12 changes. The current work was checked through
source inspection and the stated automated/runtime layers. No security certification
or absence-of-defects claim is made.

Physical WAN and long sessions, installed service/login/reboot/wake, real microphone
and display hardware, mobile devices, storage providers and printers remain open.
Linux native evidence is an isolated X11 software-rendered VM, not a physical
Wayland desktop. Apple remains explicitly uncompiled and untested. No performance
comparison establishes superiority over commercial remote-desktop products.

The source has no commercial-use check, subscription, telemetry or session-duration
quota. Network, operating-system and infrastructure failures can still interrupt a
session. Publishing source does not mark the production gates as passed.

Sanitized observed logs: [Windows](evidence/v12-windows.txt), [shared core](evidence/v12-rust-windows.txt), [interoperability](evidence/v12-interop.txt), [Linux core](evidence/v12-linux-core.txt), [notices](evidence/v12-notices.txt).

The final optimized Alpine executable rendered the corrected 0.12 label in the
owned Xvfb VM. Its copied and local SHA-256 match: `47d8f1b65e78c2e0c080e00fcff3eae9df46e868599f2cdfe9273c4b7328fe85`.
[Native and package log](evidence/v12-linux-native.txt). Test runtime is not an FPS
benchmark. The first candidate displayed an old 0.11 label; that label was corrected
and the rebuilt final executable was tested again.

[Android build/package evidence](evidence/v12-android.txt) and [stream helper](evidence/v12-android-copy.txt). The first Android compilation rejected PdfDocument in a try-with-resources block because that API is not AutoCloseable. The adapter now closes the document explicitly in finally and finishes each page in finally; the subsequent full build and lint passed.

The first successful Android lint also identified two drawing allocations. Paint
and Path are now retained by the view and reused on each draw; the final build
and lint pass has no DrawAllocation warning. Remaining warnings concern existing
translation resources, backup-rule compatibility and Gradle version availability.
