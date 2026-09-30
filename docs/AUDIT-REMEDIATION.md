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

## Execution evidence

| Layer | Current integration evidence | Boundary |
| --- | --- | --- |
| Windows Safe suite | 116 passed, 0 failed | Synthetic capture, injected clipboard, local owned UI/protocol fixtures; actual desktop capture/input skipped |
| Targeted Windows regressions | Files/clipboard 10, media 12, UI 5, protected-host controls 9 passed | Includes 5,001 sequential clipboard jobs, pending recording close/exit and stale settings writers |
| Recording output | Both synthetic MP4 fixtures independently decoded by FFmpeg 7.1 | VFR output decoded with passthrough and demux timebase; no physical desktop/microphone recording |
| Rust on Windows | 62 core + 2 bridge tests passed, including all native peer tests | Includes real 31-second publication delays for both receiver roles; synthetic data |
| Android JVM | Six regression groups passed | Pending operation and PCM policy logic; no Android framework or physical audio execution |
| Android package | Main/test APK compilation and lint passed, 0 errors / 13 existing warnings | Java fixes built; current Rust/JNI rebuild and virtual-device instrumentation pending |
| Linux/POSIX and cross-language fixtures | Pending current-source execution | Isolated Alpine overlay and owned displays only |
| Long idle soak | Pending | Local minimized/blocked UI fixture is separate from WAN |
| Two-PC WAN | User confirmed second-PC availability; pending candidate test | Use the [candidate checklist](TEST-CANDIDATE.md) with the existing access kept available |

The Android CI job executes isolated Activity/Bundle instrumentation on a fresh
KVM-backed emulator and checks the exact success marker. A software emulator on
this Windows host did not boot successfully; that attempt is not a pass.

The portable publication worker allows one pending publication. Cancellation and
close stop waiting for it; an already blocked filesystem syscall may continue in
that one background thread until the operating system returns. This does not
prove performance or cancellation on every SMB/exFAT/provider/device combination.

Local synthetic, virtual-device, physical-device and WAN acceptance remain
distinct. macOS/iOS remain uncompiled and untested: no Apple device is available,
and the explicitly accepted preparation-only exception is unchanged.
