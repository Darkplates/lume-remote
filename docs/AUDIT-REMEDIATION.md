# Audit remediation - 2026-09-30

Baseline: `d20f787655cf216ad3eeee10bc9063148b4b7b60`.

This change addresses nine P2 defects and one P3 documentation entry-point
issue identified by the 2026-09-30 repository audit. Execution results below
separate verified fixtures from pending platform and user acceptance.

## Owner access and settings compatibility

Disable now publishes a separate DPAPI-protected `host-disabled.dat` generation
in the ACL-protected Host directory. Reads override stale host settings while
the marker exists. A short protected state lock serializes publication and
explicit enable; a delayed writer cannot erase a newer disable. Only a fresh
explicit enable removes the generation it observed. Host preferences and the
remote session protocol retain their existing schemas.

The current worker creates a fresh control-pipe endpoint before publishing it
in DPAPI-protected `host-control.dat`, then reuses the same pipe handle across
requests. The directory ACL, server-owner check, client-SID authentication and
request bounds remain in force. Cancellation closes a stalled listener.
Current dashboards fall back to the legacy deterministic endpoint when an old
worker has no discovery record. Older dashboards do not understand new worker
discovery: update the dashboard and worker together when applying these fixes.
Neither user confirmation nor the existing Windows installer/UAC process is
bypassed. The running installed host is not changed by test builds.

The owner pipe grants ReadWrite/Synchronize without CreateNewInstance to the
owner; see the [Microsoft PipeAccessRights contract](https://learn.microsoft.com/en-us/dotnet/api/system.io.pipes.pipeaccessrights?view=netframework-4.8.1).

## Other corrections

- Clipboard workers release their guard before completing successful or failed
  tasks; genuine concurrent operations remain rejected.
- Recording finalization survives viewer disposal and application exit waits
  for outstanding work. A bounded wait keeps the application open if saving
  has not completed, rather than abandoning the writer.
- Portable annotations map validated viewer generations to the remote epoch.
- Portable file publication runs as one bounded pending job with completion
  acknowledgements, cancellation and stale-result isolation.
- POSIX upload/open paths validate regular files using nonblocking/no-follow
  semantics and descriptor identity. The existing locked libc package supplies
  target-specific constants; no registry package is upgraded.
- Android preserves pending document operations across Activity recreation and
  delayed service binding, and handles negative AudioTrack write results.
- The validation landing page now indexes current evidence before the dated
  historical checkpoints.

| Audit ID | Correction | Main regression evidence |
| --- | --- | --- |
| WR-01 | Release clipboard guard before task completion | Sequential success/failure and concurrency tests |
| WR-02 | Track recording writers independently of viewer lifetime | Stop/close, pending close, timeout, fault, cancellation and disposed-viewer exit |
| SE-01 | Protected disable generation and serialized publication | Delayed settings writer and in-flight enable cannot undo a later disable |
| SE-02 | Fresh protected discovery and persistent pipe listener | Multiple authenticated requests reuse the endpoint, then remove discovery on stop |
| PF-01 | Translate valid local generation into the host epoch | Pinned-TLS peer covers monitor changes, saved recovery and stale queued commands; actual Windows handler accepts empty clears after monitor changes |
| PF-02 | One asynchronous publication job, periodic completion ACK | Both receiver roles survive a real 31-second publication delay; cancel/deadline/full-outbox checks |
| PF-03 | Nonblocking/no-follow regular-file open and descriptor identity | POSIX FIFO, symlink and file-substitution checks |
| RM-01 | Persist one pending document operation with session identity | Five operation types, deferred result and stale-session checks; Android Activity/Bundle instrumentation passed on an owned emulator |
| RM-02 | Handle negative and partial AudioTrack writes | Bounded PCM, partial/zero/negative writes and same-generation failure gate |
| RM-03 | Index current validation before historical checkpoints | Current documentation links reviewed |

## Execution evidence

| Layer | Current integration evidence | Boundary |
| --- | --- | --- |
| Windows Safe suite | 116 passed, 0 failed | Synthetic capture, injected clipboard, local owned UI/protocol fixtures; actual desktop capture/input skipped |
| Targeted Windows regressions | Files/clipboard 10, media 12, UI 5, protected-host controls 9 passed | Includes 5,001 sequential clipboard jobs, pending recording close/exit and stale settings writers |
| Recording output | Both synthetic MP4 fixtures independently decoded by FFmpeg 7.1 | VFR output decoded with passthrough and demux timebase; no physical desktop/microphone recording |
| Rust on Windows | 62 core + 2 bridge tests passed, including all native peer tests | Includes real 31-second publication delays for both receiver roles; synthetic data |
| Android JVM | Six regression groups passed | Pending operation and PCM policy logic; no Android framework or physical audio execution |
| Android package | Fresh ARM64/x86-64 Rust/JNI, main/test APK build, lint, signature v2 and ZIP/ELF alignment passed; 0 errors / 13 existing warnings | All eight native input hashes verified; debug development APK |
| Android framework runtime | Isolated remediation instrumentation passed on API 35 x86-64 KVM emulator | [Run for code commit 56fdb3d](https://github.com/Darkplates/lume-remote/actions/runs/36653592499); real Activity recreation and Bundle/Parcel, deferred binding/export, stale-session rejection; no external SAF provider or physical audio |
| Linux/POSIX CI | 66 core + 2 bridge tests and 2 native X11/UI tests passed on Ubuntu 24.04 | [Run for code commit 56fdb3d](https://github.com/Darkplates/lume-remote/actions/runs/36653592504); FIFO/substitution/native peer tests included; keyring test filtered out |
| Windows/Rust interoperability | Six fixture invocations passed | Both viewer/host directions, source/JPEG switching, pairing/revocation, files/folders and monitors; synthetic capture and isolated credentials/files |
| Actual Windows annotation gate | Three positive receipts observed for host epochs 1/2/3 after monitor changes; stale local generations rejected | Empty clears only, synthetic monitors, injected input sink, owned TLS observer; no overlay, real input or clipboard |
| Long idle soak | 1,800.3 seconds completed, exit 0, no timeout; 7,792 heartbeat bytes | Current-source copied executable and native libraries; static synthetic local P2P/TLS, minimized viewer and no UI message pump; separate from WAN |
| Two-PC WAN | User confirmed second-PC availability; pending candidate test | Use the [candidate checklist](TEST-CANDIDATE.md) with the existing access kept available |

The Windows and Android hosted jobs passed for production-code commit
`56fdb3d6ce837dd290b93c0154b0f0b544f60c15`. The Android CI job executed isolated
Activity/Bundle instrumentation on a fresh KVM-backed emulator and returned
`PASS Android isolated remediation contract`. The runtime input APK SHA-256 was
`d8a1f825894f8bec67bc61cd4463a88f814e9e8349b93e25d19060515b160884`; the test APK was
`5e73deffcd0878247d3f873592685a67ff177a060a0f955dad64dddb4116a2b7`.
The locally built debug APK has its separate hash below; different debug signing
and build environments do not make those packages byte-identical. A software
emulator on this Windows host did not boot successfully; that attempt is not a pass.

Commit `25067148a0e6f0f8488aa852692cf9c83225e896` adds the actual Windows annotation
gate fixture, its hosted invocation and the PowerShell 5 harness correction. Those
fixtures passed locally on Windows PowerShell 5 and PowerShell 7. Production source
and the candidate application remain unchanged; that commit's fresh hosted rerun
must be assessed by its own result.

The initial local access-files harness run under Windows PowerShell 5 decoded a
Unicode fixture filename using the local ANSI code page. The test script now
constructs that character explicitly; the exact PowerShell 5 rerun passed. This
was a harness failure, not a failed transfer or a product encoding correction.

The additional Windows annotation observer confirms the actual production handler,
not only a simulated peer's epoch comparison. Nonempty drawing and saved-session
recovery against that Windows handler remain untested here. The Rust pinned-TLS
peer regression covers both behaviours at the protocol/generation layer.

The same current-source core/bridge binaries also passed on the isolated Alpine
overlay: 66 + 2 tests, no failures or ignored tests. An extra local cross-build of
the desktop was cancelled to release its compiler lock and memory for the Windows
interop probe. It is not a pass; the separate Ubuntu CI desktop/X11 checks above
did pass. Keyring, Linux host media/null-audio and physical Wayland execution were
not repeated in this remediation round.

The local Android development APK is 11,758,088 bytes, SHA-256
`70973f26f39f6383074c0af1e0038e6999ad185d5662fd905b754ecdc68201fd`.
It is debug-signed, not a production/store signing claim. All 40 native/Java source
and configuration hashes stayed unchanged during its build. External SAF providers
and physical AudioTrack devices still require their own acceptance checks.

The verified Windows WAN candidate is
`LumeRemote-0.12.1-audit-56fdb3d-win64.zip`, SHA-256
`50e5374df440b07fc8fd82f04965cc617bb76de2644514d681afd4be333c6f51`.
Its 1,169 files and manifest passed verification and fresh extraction. The source
archive contains the Android JVM fixture and also passed fresh extraction, those
six JVM groups, and Windows test compilation. Later integration-only fixtures do
not change the candidate application code.

The portable publication worker allows one pending publication. Cancellation and
close stop waiting for it; an already blocked filesystem syscall may continue in
that one background thread until the operating system returns. This does not
prove performance or cancellation on every SMB/exFAT/provider/device combination.

Local synthetic, virtual-device, physical-device and WAN acceptance remain
distinct. macOS/iOS remain uncompiled and untested: no Apple device is available,
and the explicitly accepted preparation-only exception is unchanged.
