# Lume Remote two-PC guest P2P test report

Test date: 2026-09-30
Evidence publication: 2026-10-01
Status: Immediate LAN flows pass. A late-manual-reply route failure is reproduced.
A real different-network/WAN connection has NOT been observed.

## Source and build

Branch: claude/charming-planck-8zr2ox
Commit: 3b22f3e66ede5354fc3ccea1b9ac043ad3c62855
Fresh checkout; both handoffs and the two-PC plan read. Integrated application,
native libraries and tests built through build-all.ps1 -Tests. Package verified:
1,180 files and manifest; transferred copies matched SHA-256. Hosted Windows and
portable workflows for this exact commit succeeded.
Loaded native SHA-256 in the instrumented real-PC fixtures:
23abe43e9c9ed8378021b49c6cac652273a78be0a27affc2d2f9c0873902ddb8

## Headless

- Safe on PC A: 118 passed, 0 failed; physical fixtures skipped.
- PC A --signal: 3 passed, 0 failed; 21.52 seconds; exit 0.
- PC B --signal: 3 passed, 0 failed; 21.45 seconds; exit 0.
- PC A --p2p: 7 passed, 0 failed; 0.75 seconds; exit 0.
- PC B --p2p: 7 passed, 0 failed; 0.74 seconds; exit 0.
- PC A --p2p-signaling-delay: 3 passed, 0 failed; exit 0. Immediate, 45-second
blocked-route and 150-second early-route cases verified duplex bytes and joined
cleanup. They use owned loopback UDP proxies, not the two-PC network.
Public signaling checks exercised encrypted replies and acknowledgement/rejection.
These passing results do not establish WAN access or every late-signaling path.

## LAN

Both PCs: Windows 11 Pro build 26200. Their active Ethernet IPv4 interfaces were
checked privately and share the same subnet. Wi-Fi was disconnected.

Production GUI, automatic: PASS. The owner reported success after local Allow;
PC A showed an active desktop stream and PC B logged a viewer-connected event.
No manual reply copy-back was performed. The transient acknowledgement, exact
route and time to approval were not captured.

Production GUI, manual: FAILED before approval. The reply parsed and its signature
was accepted, then the host timed out finding a route. Invitation staging to reply
collection took 9 minutes 32 seconds. Viewer creation time/final error were not
recorded; the exact cause of that attempt remains unknown.

Autonomous tests used both actual PCs, production C# protocol code, the integrated
native library, real P2P, exact pinned TLS and fresh private codes. A SEPARATE test
executable supplied synthetic frames, fixture approval, an injected keyboard sink
and an in-memory clipboard. Production consent was not automated. WinRM exchanged
codes privately. Startup tasks and private temporary files were removed.

automatic-v2: PASS; host 20.00s; viewer 18.13s; decoded frames 80.
manual-v1: PASS; host 20.74s; viewer 18.93s; decoded frames 80.
manual-delay45-v2: PASS; host 59.66s; viewer 57.72s; decoded frames 31.
manual-delay150-v1: FAIL; host 245.48s; viewer 241.67s; decoded frames not reached.
manual-delay150-v2: FAIL; host 245.55s; viewer 241.55s; decoded frames not reached.
manual-fresh-answer150-v1: PASS; host 160.72s; viewer 159.04s; decoded frames 30.

The immediate automatic/manual cases passed 15 seconds of connected observation;
the successful delayed cases passed 5 seconds. Each successful case checked 640x360
decoded/acknowledged frames, one approval before source creation/capture, exactly
two injected keyboard events, clipboard read/write in both directions, and one
65,536-byte file each way with SHA-256 content verification. Viewer receivers joined.
The host route label was Direct P2P / host candidates; the viewer label was Direct
P2P / NAT traversal. Candidate-type labels do not turn these shared-subnet runs
into WAN evidence.

Holding the answer for 150 seconds AFTER creation failed twice. Both hosts verified
the reply. Both endpoints exhausted a 90-second readiness wait after delivery.
The instrumented repeat never observed a selected ICE route, secure link or open
channel. Approval/source/capture/input counts remained zero. The peers were still
in the checking phase when readiness timed out; native terminal failure was not
observed. The fixture viewer uses 90 seconds after delivery, rather than the GUI's
full ten-minute setup budget; the GUI host also waits 90 seconds after a reply.

Control: waiting 150 seconds BEFORE creating a fresh answer passed on both PCs.
The offer survived that age; the comparison associates the failure with waiting
after answer creation. It does not identify the exact native/firewall mechanism
or implement a production fix. Read-only firewall queries found enabled profiles
and no executable-specific rules for the test fixture; this does not prove drops
or causation. No firewall settings were changed.
Aborted launcher/probe development attempts are excluded from application totals.

## Different network

NOT RUN. No separate mobile/tethered connection was active or accessible to the
agent. A saved phone-like hotspot profile was absent from the available Wi-Fi
list, and no phone tethering adapter was present. Network adapters, router, NAT
and firewall settings were preserved. Public broker traffic is not a WAN data path.
The original WAN issue remains unvalidated; no WAN fix is claimed.

## Not tested

Successful physical manual GUI approval/desktop acceptance; actual Windows input;
real Windows clipboard UI; WAN connection/clipboard/files; 35-minute WAN idle
acceptance; physical disconnect-pill cleanup; another external-network retry.
Short synthetic observations do not meet the WAN idle requirement. Frame counts
are not comparative/source-FPS claims.

## Preservation and changes

Installed-binary/settings hashes and service states match the baseline on both PCs.
The existing PC A host service remains running. Only recorded test PIDs with exact
executable-path checks were stopped. Original PC A PIDs 5848, 9260 and 24492 remain
running; original PC A PID 11876 and PC B PID 9184 disappeared earlier and were not
stopped by the agent. Test GUI windows and CLI processes are closed; temporary
private-code files and one-shot tasks are absent. Test copies/evidence are retained.
Only new test copies, separate test code, automation and evidence were created.
No production source change or installed-host enable/update during the test run.
The tracked checkout was clean when testing ended. Raw native logging remained disabled.

## Evidence

Ignored verification/two-pc-2026-09-30/ retains local build/Safe/package receipts,
baseline/preservation checks, headless results, real-PC fixture JSON, phase traces,
module/source hashes and reproducible test automation source.
No invitations, replies, keys, SDP, candidate addresses or desktop content are
included in this report. The delayed manual failure remains unresolved.

Selected sanitized JSON, native-delay output and cleanup receipts are published
under docs/evidence/two-pc-2026-09-30/. See TWO-PC-AUTOMATION.md for reproduction.
