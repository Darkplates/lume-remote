# Validation index

This page separates current evidence from immutable historical checkpoints.
Passing synthetic or hosted checks does not establish physical-device, WAN,
installed-service, publisher-signing or independent-security acceptance.

## Current source and preview evidence

- The current feature/implementation matrix is [FEATURES.md](FEATURES.md).
- The latest versioned portable verification is
  [PORTABLE-12-VERIFICATION.md](PORTABLE-12-VERIFICATION.md); previous versioned
  verification pages retain their own dates and source boundaries.
- Required acceptance limits and the explicitly accepted Apple preparation-only
  exception are recorded in [release-gates.json](release-gates.json).
- Source commit `d20f787655cf216ad3eeee10bc9063148b4b7b60` passed hosted
  [Windows checks](https://github.com/Darkplates/lume-remote/actions/runs/36646739989)
  and [Linux checks](https://github.com/Darkplates/lume-remote/actions/runs/36646739922).
  These are results for that exact commit, not every subsequent working tree.
- The 2026-09-30 local audit of that commit found 107 safe Windows checks passed
  and one intermittent clipboard-ordering failure, despite passing hosted CI.
  Rust core/bridge passed 55 + 2 checks. Remediation is described in
  [AUDIT-REMEDIATION.md](AUDIT-REMEDIATION.md), with its own execution boundaries.
- Notice inventory: 496 registry packages, with 493 collected, two explicitly
  classified later-upstream notices and one upstream-review exception. See
  [LICENSE-PROVENANCE.md](LICENSE-PROVENANCE.md); this is not legal certification.
- The published `v0.12.1-preview.1` ZIP represents commit
  `19da8c1cb51372a1c7e40829130359f7163bdb97`, not the later audited source.
  Compare the producing commit before applying source verification to a binary.

## Historical 0.8 checkpoint

The remainder preserves dated evidence and missing features as they stood at
each checkpoint. Historical counts and claims are not current acceptance status.

# Validation - 0.8.0 portable development checkpoint

Date: 2026-09-28. Public release remains blocked. The installed Windows 0.3
service is unchanged. Older archives, including 0.7, remain immutable checkpoints.

## Evidence at the 0.8 checkpoint

- Windows: 92 safe application checks passed. Rust core and C ABI passed 28
  checks, including native P2P and the encrypted vault's wrong-password/tamper
  cases. The portable desktop source also passed the Windows build check.
- A separate Windows host and Rust viewer paired through the existing public
  rendezvous service, consumed the one-time code without capture, connected twice
  over P2P, revoked a live session and rejected the revoked credential. This fixture
  ran twice. Both peers were on this machine; it does not prove two-network WAN.
- Windows-host file checks passed paged listings (200 + 7 entries), empty files,
  a 1 MiB + 123 byte payload, uploads/downloads, SHA-256, collision preservation
  and continued video. Reverse checks passed the scoped portable host, traversal
  denial, folder creation/download and independent destination hashes.
- Three Windows/Rust image fixtures passed direct TLS in both directions and
  native P2P, exact Source pixels, lower-resolution JPEG and live Source restore.
- In the Alpine 3.24.2 x86-64 VM, 29 core/C ABI checks passed, including the native
  P2P library and Unix symlink-escape case. Two separate private-Xvfb checks passed
  native window rendering, X11 capture, owned input/release and Unicode clipboard.
  The 0.8 dashboard was inspected at original screenshot resolution.
- The real Linux file host and Windows viewer passed scoped-root discovery,
  traversal refusal, file/folder transfers, collision preservation and an
  independent SHA-256 check of the Linux filesystem. An initial SSH forwarding
  attempt was refused by the laboratory's SSH policy. The passing fixture used a
  loopback-only QEMU port forward; SSH and Windows security settings were unchanged.
- Android ARM64/x86-64 native libraries, Java app and separate instrumentation APK
  compiled. Lint completed with zero errors and nine warnings. APK signature,
  ZIP/16 KiB native alignment, both ABIs and absence of test code were verified.
  The APK contains 869 crate notice files and 13 Rust distribution notice files,
  each checked against its recorded hash.

Filtered reports are in `docs/evidence/v08-*.txt`. The final output report records
archive hashes, fresh-extraction checks and the exact packaged-binary acceptance.

## Remaining acceptance and implementation

No Android instrumented/device test ran: two emulator versions previously failed
before boot on this host. Keystore, document-picker, service lifecycle and physical
mobile/WAN behaviour therefore remain unverified. macOS/iOS sources and build
recipes have not been compiled or executed on an Apple host. The owner waived
macOS execution testing, not implementation or build correctness.

Portable clients now implement protected saved Windows-host access and single-file
tools. Portable desktop hosts can explicitly share one folder after approval.
They still lack a permanent-host service, automatic recovery after network loss,
viewer folder jobs/resume, monitor selection, portable media/voice/recording/printing,
Wayland control and mobile host roles. Mobile staging must be exported before
disconnect. Atomic portable downloads require filesystem hard-link support.
Desktop/mobile polling still limits high-refresh presentation; no 180 FPS or
competitor CPU/RAM/performance superiority is claimed.

The Linux build is optimized Alpine/musl x86-64, tested in an isolated software-
rendered VM, not a universal Linux binary. Physical desktops, other distributions,
GPU behaviour and two-PC WAN remain separate. The current synthetic fixtures do
not commission real microphones/speakers, SMB, printers, installed-host ACLs,
lock/reboot/secure-desktop or physical wake.

The notice collector recovered files for 491 of 496 locked registry packages.
The same five target-specific inventory gaps remain; the collection is not an
independent licensing audit. CI recipes have not run on GitHub. No Opus/security
review, publisher signing, signed updater or managed TURN fleet is established.
The release-gate check intentionally exits 2 with ten required gates still open.

## Historical 0.7 evidence

The following describes the immutable 0.7 checkpoint, not the current feature set.

# Validation - 0.7.0 portable development checkpoint

Date: 2026-09-28. These are development builds. Public release remains blocked.
The installed Windows 0.3 service is unchanged. The immutable 0.6 archives remain
available as a comparison checkpoint.

## Current Windows and transport evidence

- The final Windows .NET build passed all 92 safe application checks.
- The Rust core and C ABI passed 20 checks, including native P2P. A new callback
  retirement regression covers cancellation and dropping an unopened peer.
- Three independent Windows/Rust fixtures passed: Rust viewer to Windows host
  over direct TLS, the same over native P2P, and Windows viewer to Rust host.
  Source PNG pixels, live quality switches and process teardown were checked.
- Both TLS stacks now use a valid DNS-form SNI value. Authentication continues
  to require the exact pinned certificate and the invitation secret.
- ARM64 and x86-64 Android Rust, WebRTC and JNI libraries compiled. ELF LOAD
  segments were checked for 16 KiB alignment. The Java app and separate test
  APK compiled; test-only approval remains in the Windows fixture executable.
- APK revision r2 adds the matching Rust standard-library notices. Eleven code,
  native library, manifest and resource members are byte-identical to the first
  0.7 APK. Its signature, ZIP CRC/alignment, 823 crate notices and 13 Rust
  distribution copyright/license files were verified against their recorded hashes.

## Platform acceptance boundaries

The Android emulator exited with 0xc0000005 before boot/ADB on this Windows host
with no usable hardware acceleration in the test setup. Two official emulator versions were
tried. No Android instrumented test, physical-device test or Android WAN session
has run. The APK's signature/alignment and compilation are separate evidence.

Linux x86-64 executables ran inside a dedicated Alpine 3.24.2 QEMU guest. All
20 core/C ABI checks passed, including the pinned native P2P library built in
that guest. Two Xvfb checks passed: native window rendering and actual X11 capture,
owned keyboard/mouse events, held-input release and bidirectional Unicode clipboard.
The rendered desktop was inspected. Rust executables were cross-compiled with
Rust 1.98.1, Zig 0.15.2 and a hash-checked sysroot exported from that guest.
This software-rendered VM proves those behaviours, not physical GPU performance,
Wayland acceptance or compatibility with other Linux distributions.
The packaged Linux binary uses the optimized, stripped release profile. Its
SHA-256 matches the application executed inside the guest. The supplied
`scripts/test-linux.sh` runner isolates X11 input and clipboard in a private Xvfb
server; it can run either native Cargo tests or an explicitly supplied test binary.
The final archive was freshly extracted in Linux: all 857 manifest hashes matched,
and its executable launcher passed both native tests. UI acceptance requires the
header and both action sections on consecutive captures; the complete window was
visually inspected. An initially incomplete tool preview was checked against the
identical PNG files and opaque pixel data, then viewed at original resolution;
it was not a native rendering defect.

A Windows host sent synthetic source frames to the real Linux viewer executable;
live JPEG resizing and restoring exact Source pixels passed on two consecutive
confirmation runs. An earlier run timed out during approval under the test
workload; its root cause remains unproven. No physical WAN result is inferred.

macOS/iOS sources and build recipes have not been compiled or executed on an
Apple host. The owner waived macOS execution testing, not implementation.
Portable pairing/service, file/media tools, Wayland control, mobile host roles
and a browser client remain missing. The Windows feature set is not portable
feature parity. Apple signing/notarization and publisher signing are absent.
The portable UI currently polls every 16 ms on desktop and 33 ms on mobile;
180 FPS presentation is not implemented on these clients.

Filtered reports are in docs/evidence/v07-*.txt. CI recipes exist but have not
run on GitHub. No Opus or independent security review has run. Native devices,
real audio/printing, installed-host ACLs, wake/reboot and two-PC WAN acceptance
remain outstanding. No comparison against competitor resource use is established.

The source collector recovered notice files for 469 of 474 locked registry
packages, including exact upstream-commit provenance and the bundled font notices.
Five lockfile entries still require review; none occurs in the Android bridge's
target dependency tree. This collection is not an independent licensing audit.

## Historical 0.6 evidence

The following describes the immutable Windows 0.6 checkpoint.

# Validation - 0.6.0 Windows development checkpoint

Date: 2026-09-28. Windows 10 x64, .NET Framework 4.8. Public release remains blocked.
The installed 0.3 service is preserved; these tests use the separate development copy.

## Current evidence

- Final regression: 91 application checks and 7 relay checks passed after the last
  native code change. A 45-second static/minimized P2P plus pinned-TLS soak passed
  without a UI message pump.
- Seven resume/share tests cover real upload and download interruptions, authenticated
  state, wrong-pair isolation, corruption, explicit cancellation, old settings and
  owner-approved root discovery. No physical SMB endpoint was contacted.
- Three print checks validate the actual Windows PDF renderer, geometry/colours,
  invalid inputs, cancellation and hash-verified remote download before rendering.
- A separate real spool test submitted the synthetic PDF to Microsoft Print to PDF,
  then reopened/rendered its output. No physical printer or paper was used.
- Voice tests exercise host consent wait/denial/disconnect and unauthorized requests
  while desktop frames continue. They never open a real microphone.
- MP4 checks cover H.264/AAC tracks, preservation, normal-exit finalization and
  bounded stereo alignment. Independent FFmpeg decoding found both expected stereo
  tones (440/880 Hz), with 53 video frames over 1.64 seconds in the final fixture.
- The recorder's earlier sink-writer pipeline silently reduced a synthetic 180-frame
  sequence to 60 frames. Explicit encoding plus packet passthrough fixed that defect.
  Both the MP4 sample table and independent decoder now count 180 distinct images
  over one second. This offline fixture does not prove 180 FPS capture or networking.

Filtered reports: docs/evidence/v06-*.txt. Reproduce media decoding with
`python scripts/verify-media.py` after `tests/LumeTests.exe --media`; its
imageio-ffmpeg dependency is test-only and is not shipped with the app.
The native PDF build additionally requires the Windows SDK merged Windows.winmd.

## Remaining acceptance

Two physical PCs on different networks; real SMB permissions/connectivity; actual
microphone/speaker selection and device changes; physical printing; installed-host
owner-token/ACL behaviour; clipboard coexistence; mixed-monitor DPI/hotplug; native
lock/login/UAC/restart/shutdown; physical wake; publisher signing and independent
security review. No Opus review has run. macOS execution is explicitly untested.
Cross-platform implementation/build status is tracked separately in FEATURES.md
and release-gates.json. A source/build result never substitutes for native OS use.

File resume covers a paired single-file retry into the same destination. Automatic
folder-job resume and a virtual printer driver are not implemented; printing forwards
PDFs through an explicit local preview and printer selection.

## Historical 0.5 evidence

The following baseline is retained for provenance; its claims describe the old build.

# Validation - 0.5.0 development checkpoint

Date: 2026-09-28. Windows 10 x64, .NET Framework 4.8. Public release remains blocked.
The installed 0.3 host was preserved; this evidence concerns the separate checkout.

## Current local evidence

- The full safe suite passed 77 application checks and 7 relay checks. It includes
  the retained authorization, P2P/TLS, quality, codec, file, recovery and tray checks.
- New tools tests cover consent waits without blocking frames, clipboard size/STA/
  failures, v3 compatibility, monitor metadata and recursive empty/nested folders.
- Collaboration checks cover opt-in clipboard changes/echo/conflict/teardown,
  native local chat delivery, audio denial, fixed power callbacks, annotation
  bounds and stale monitor input at both endpoints.
- A constrained four-worker thread pool still completed file/list operations.
  Blocking session loops have dedicated workers; fixtures also cancel directly
  after an acknowledged file chunk instead of relying on timing-sensitive polling.
- Media checks round-trip bounded stereo PCM16 without sample changes and reject
  invalid/decompression-bomb blocks. Native audio capture/playback was not invoked.
- MP4 checks produced a real H.264 container, preserved existing files and verified
  finalization when the dashboard exits with a recording active.
- Independent FFmpeg decoding of the synthetic recording returned 34 frames over
  1.13 seconds at 640 x 360. Top/bottom colour samples verified orientation.
  FFmpeg is a test dependency already present on this machine, not an app dependency.
- The final 45-second minimized/static native P2P/TLS soak passed without a UI pump.
- Native UI previews were rendered with synthetic content and inspected. Hardware
  multi-monitor overlays and end-user interaction remain separate checks.

Filtered text reports are in docs/evidence/v05-*.txt. Public previews contain
synthetic content; private working reports and temporary paths are excluded.

## Not established

Two physical PCs on different networks; actual OS clipboard coexistence; audio
devices/device changes; physical multi-monitor hotplug/DPI; new installed-host owner
ACL execution; Windows lock/login/UAC/restart/shutdown; physical wake; performance
superiority; signed distribution and independent security review.
Power tests inject callbacks only: no real lock, restart or shutdown was executed.
No Opus review has run.

Linux, macOS and mobile implementations are absent, not merely untested binaries.
macOS execution is explicitly untested as agreed with the owner. Network shares,
transfer resume, microphone/voice, recording audio and remote printing are absent.
The release gate script reports these missing requirements without publishing.

## Historical 0.4 evidence

The following dated baseline is retained for comparison. Its long soak and hardware
results do not automatically establish acceptance of every 0.5 addition.

### 0.4.0 preview baseline

Date: 2026-09-28. Windows 10 x64, .NET Framework 4.8. The development checkout is separate from the installed 0.3 host. Results below must not be read as acceptance on a second physical PC.

## Automated local checks

- 61 application checks passed with `scripts/verify.ps1 -Safe`, plus 7 Python relay checks. The safe suite does not inject global keyboard/mouse input.
- Eight file/clipboard checks cover STA clipboard execution, repeated and busy clipboard operations, path/owner restrictions, hash/offset/length validation, 8 MiB binary and empty-file transfers both ways over native P2P with pinned TLS, filename collision preservation, cancellation cleanup, authorization and the real file browser UI.
- Two simultaneous local P2P viewers keep receiving after the dashboard is closed to the tray. Closing one viewer does not close the other. Explicit application exit cleans both.
- Saved-viewer recovery negotiates fresh credentials and reconnects in the same window after a synthetic transport failure. Cancellation and authentication/protocol rejection are tested separately.
- Eleven video checks exercise Windows H.264, bounded SPS parsing, live codec transitions, version 2 compatibility, timing metrics and image-mode fallback. The ordinary quality suite checks source dimensions, exact lossless pixels, 360p/10 FPS and 180 FPS parameters.
- A separate real DXGI fixture reproduced an owned test-window pixel. Actual public signaling and one-time pairing/reconnect/revocation probes passed earlier in this development run; these are native clients on this machine, not a two-router acceptance run.

Public, filtered evidence is under [evidence](evidence/). Private working reports and any local absolute paths remain outside the release.

## Idle regression

An isolated copy of the previous 0.3 viewer lost a synthetic static-desktop session when only its UI message loop was deliberately blocked for 40 seconds. The old UI timer also owned session pings. This reproduces a mechanism; it does not prove the historical cause of the user's earlier disconnect, which had no retained failure log.

The new heartbeat/recovery candidate completed a 2,100-second (35-minute) static desktop test over native local P2P plus pinned TLS, with the viewer minimized and no UI message pump. It kept its connection and sent independent heartbeat traffic. That long soak used a copied candidate before the later file/clipboard/UI additions. A 45-second regression beyond the read timeout is also run against the final source. These are local liveness checks, not a WAN uptime guarantee.

## Synthetic codec sample

The same machine has an Intel Iris Pro 5200 and a 60 Hz 1920x1080 display. This sample used synthetic 1080p frames, with 5 warm-up frames and 45 measured frames. Capture and network transfer were excluded. Encoder/decoder work was sequential in the harness; source FPS is not being claimed.

| Backend | Encode | Decode | Codec-only throughput | Normalized process CPU |
| --- | --- | --- | --- | --- |
| Intel Quick Sync hardware encoder | 20.78 ms | 21.01 ms | 23.9 FPS | 9.0% |
| Windows software H.264 | 15.89 ms | 17.57 ms | 29.9 FPS | 14.5% |

Hardware encoding reduced CPU in this sample but was not faster end-to-end in the sequential harness. The current decoder and BGRA/NV12 conversions still use CPU work. This is not a comparison with another remote-desktop product.

## Build and distribution

The managed app, DXGI bridge, Media Foundation bridge and pinned native WebRTC dependency were built locally. The native build uses the static C++ runtime. The package script uses an explicit publication allowlist, includes dependency notices and generates a SHA-256 manifest. The verifier checks every archive member and refuses duplicate, unsafe or unmanifested paths. Fresh extraction and tests are separate from hash verification.

Windows CI is configured but has not run on GitHub. UI images use sample PC names, synthetic desktop pixels and temporary test files with a display-only sample path; they contain no user's private desktop or file listing.

## Not established by these results

The 0.4 privileged service update, actual owner-token file operations in its SYSTEM worker, clipboard interaction with other applications, second-PC installation, two-physical-PC WAN transfers, long real workloads, unattended reboot/lock/login/UAC behaviour, physical wake, interrupted-file resume, folder recursion, all GPU/DPI/keyboard layouts, sustained 180+ FPS, signing, independent security audit and competitor superiority remain separate acceptance gates. See [feature status](FEATURES.md).

Earlier user-reported successful cross-network access and the observed installed 0.3 host are historical evidence, not automatic acceptance of this build. Local package creation is not public publication.

## Local installation attempt

The normal administrator/UAC update was requested from the verified candidate on 2026-09-28. Windows returned ERROR_CANCELLED before starting the installer. The observed installed version remained 0.3.0.0 with its user session active; no service update or installed-file acceptance test was run. A future update must use the normal administrator prompt again at the owner's request. This does not change the local 0.4 build/package test results.
