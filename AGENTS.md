# Lume Remote

Build a small, owner-controlled Windows remote desktop app. Project files and UI text are English. Do not add telemetry, subscriptions, commercial-use detection, session-duration caps or automatic firewall changes.

Permanent access requires explicit local opt-in, protected per-computer credentials, immediate disable/revocation, and visible management. Guest invitations require local approval. Windows service installation and updates use normal administrator/UAC consent and protected paths. Never bypass Windows authentication.

Security boundaries: exact SHA-256 TLS pinning, fresh session secrets, authorization before capture/input, bounded parsers and queues, explicit clipboard actions, and immediate disconnection. Do not log invitations, keys, SDP or private desktop content. Test approval and synthetic capture belong only in the separate test executable.

Keep network liveness, decoding and acknowledgements independent of the UI. Closing the dashboard hides it in the notification area; viewer sessions are independent. Disconnect must cancel recovery. Protocol changes must be negotiated and preserve documented legacy behaviour.

Use the Windows .NET Framework compiler and system libraries, with no NuGet/runtime dependency. Rebuild pinned native dependencies through the supplied scripts. Validate authorization, malformed packets, cleanup, codec fallback, recovery and native UI. Distinguish local, hardware, WAN, reboot, secure-desktop and physical-wake evidence. Never claim source FPS or comparative performance from a target setting.

## Repair handoff: unresolved manual WAN connection (2026-09-30)

The owner stopped this repair round and requested a GitHub push plus this handoff
for another agent. Do not describe the reported Internet connection as fixed.
Work is on `codex/audit-remediation-20260930`, draft PR #6, against audit baseline
`d20f787655cf216ad3eeee10bc9063148b4b7b60`. Read
`docs/AUDIT-REMEDIATION.md` and `docs/NATIVE-PEER-PATCHES.md` before continuing.
The original ten audit fixes and their earlier passing CI results are separate
from the additional native patches and the unresolved real-WAN test.

### Actual failure and reproduction

- Two Windows PCs are on different networks, without a shared VPN or an owned
  TURN relay. The owner opens Guest access, starts sharing on the remote PC,
  sends its private invitation, then applies the viewer's returned reply there.
- Two fresh stock-candidate attempts remained at `Negotiating a direct P2P
  route...` and failed before local approval. The owner reported:
  `P2P connection failed or closed. Some networks need a TURN relay. Stop sharing and create fresh codes if this attempt expired.`
- The first offer/reply parsed and passed the application's signature check.
  Both contained server-reflexive candidates; neither contained relay candidates.
  This proves neither ICE connectivity nor that TURN is necessary. The current
  error merges native failed/closed states and does not identify the failing phase.
- An isolated corrected viewer was then tried against the unchanged remote
  stock host. The owner applied reply #3 at about 254 seconds after viewer
  creation and still saw `Negotiating`. The source gives the viewer only
  180 seconds after displaying its reply. The local P2P window still displayed
  `Reply copied. Paste it on the sharing PC, then wait here.` after that budget;
  a later OS query reported zero UDP endpoints for this preview process.
  This stale UI state and the effective timeout/cleanup path remain unexplained.
  No approval, remote desktop, WAN clipboard, file transfer or idle acceptance
  was observed. Do not treat reply #3 as a usable fresh code.
- Firewall status is not established: the rule query was denied and no rules
  were changed. The owner did not confirm whether this candidate prompted for
  firewall permission. Do not attribute the failure to firewall, NAT, load or
  timing without measuring the relevant layer.

### Confirmed defects and implemented source changes

`src/PeerForms.cs` calls `CreateAnswer` before the reply can travel back to the
host. `src/PeerTransport.cs` then waits separately for channel readiness.
The application's 180-second viewer wait did not extend either native timer:

1. Pinned libjuice arms an ICE PAC deadline of 39,500 ms after remote-offer
   application/gathering. Owned UDP proxies withholding traffic until a reply
   at 45 seconds reproduced a stock viewer becoming terminal around 40 seconds.
2. If ICE connects before the host receives the reply, the active viewer starts
   DTLS early. With working early ICE and DTLS forwarding but a reply withheld
   until 150 seconds, the stock viewer became terminal at 123,877 ms.

Two versioned patches now set PAC to 600,000 ms and configure the libdatachannel
MbedTLS backend with `mbedtls_ssl_conf_handshake_timeout(&mConf, 1000, 600000)`.
The MbedTLS maximum is a retransmission interval, not a ten-minute total deadline.
The app still waits 180 seconds for the viewer and 90 seconds after host reply
application. No application wait extension or countdown was implemented in
this round. The host's failed-wait catch does not itself guarantee peer disposal.
These are connection setup budgets, not session-duration limits.

Public ABI, DTLS roles, consent freshness, STUN retry parameters, invitation
format, exact TLS pinning and local authorization remain unchanged. A longer
PAC alone does not extend every individual ICE check's retries; the controlled
tests model specific late inbound checks, not every NAT topology.

All four platform builders use `scripts/prepare-peer-source.py`, a stdlib-only
helper which validates exact clean upstream revisions and submodules, copies
tracked files into an isolated overlay, applies the hash-guarded patches and
records all file hashes. Never patch the owner's existing upstream checkout.
The Windows helper handles long paths and canonicalizes patched files to LF
after Git application; initial long-path/CRLF failures were resolved before
the successful preparation. Package source allowlists include both patches,
their manifest and helpers. Notices explicitly disclose modified MPL files.

### Evidence and remaining validation

- Experimental corrected Windows DLL:
  `1c5d118bf2b03093ab2c9bd924f7dcc908ce51a41cace19fd0a259521e581acb`.
  The stock library, installed host and original candidate were preserved.
- Owned proxy compatibility: stock host + corrected viewer passed immediate,
  2/45/90-second blocked-route replies, and 150-second early-route replies.
  Exact eight-byte streams in both directions and cleanup passed. This is
  loopback evidence, not public-Internet evidence.
- Public regression `tests/LumeTests.exe --p2p-signaling-delay`: 3 passed,
  0 failed (immediate 0.3 s, blocked45 45.2 s, early150 150.1 s), with loaded
  module identity, duplex bytes, zero proxy errors and joined resources.
  Windows CI now includes it; new hosted results must be checked separately.
- Ordinary Windows `--safe` with the prototype: 115 passed, 0 failed; no
  optional local-relay fixture, physical input, capture or clipboard access.
  The earlier audit's 116-check run included the extra relay fixture.
- A separate pending-DTLS cancellation probe failed its 3-second guard while
  waiting for a task containing `WaitReady(2000)` to finish. It did not prove
  that the readiness deadline fired. Its finally cleanup passed: viewer
  disposal 1 ms, host 0 ms, proxy threads and all four tasks joined. Do not
  report this whole probe as passing or as proof of a disposal deadlock.
- Source overlay preparation verified 8,839 files and four guards passed:
  correct provenance/output, altered-overlay rejection without overwrite,
  corrupt-patch rejection and unknown-pin rejection. PowerShell parsing and
  shell syntax passed. The final integrated builder DLL was NOT compiled.
  Android/Linux/Apple patched native builds were NOT executed in this round.
  macOS/iOS remain uncompiled and untested because no Apple device is available.
- Rust prototype run: bridge 2 passed; core 61 passed/1 recursive-folder fixture
  timeout. Standalone retries also hit that fixture's 30-second drive budget.
  It drives workers directly and does not use the native peer library. An
  unchanged standalone run without datachannel.dll passed in 33.82 s. An
  ignored instrumented copy with a larger test-only drive budget passed all
  real assertions in 117.95 s (upload 84.134 s, download 31.931 s, 412 successful
  publications, no failures). Instrumented output may add backpressure.
  This supports a timing-sensitive fixture; it does not establish the cause
  of every delay. No Rust production or test source was changed. Review the
  fixture budget independently; do not extend production transfer timeouts
  merely to make a test pass.

Local receipts, helpers and prototype artifacts are intentionally ignored under
`verification/remediation-2026-09-30/`: `p2p-delay`, `public-signaling-delay`,
`p2p-cancellation`, `p2p-fixed-native`, `p2p-integrated-native`,
`p2p-fixed-regression-win`, `folder-diagnosis`, and `wan-manual-attempts.json`.
They are not available from a fresh GitHub clone; committed fixtures and build
instructions must remain sufficient to reproduce the relevant checks.
Private invitations/replies are in the owner's Downloads TXT files, not Git.
Never print their contents or filenames containing codes. Never enable raw
native logging: it can expose SDP, credentials and addresses.

### Recommended repair sequence for the next agent

1. Inspect current Git/CI and preserve every running installed host and remote
   session. Test in a new folder; never silently replace the installed host.
2. Rebuild through the integrated patched-source builders and run Safe, native
   delayed-signaling, bounded cancellation and appropriate interoperability
   checks. Keep the new CI status distinct from the older passing checkpoints.
3. Trace readiness-task start, monotonic deadlines, native callbacks and UI
   completion with phase/count/time-only diagnostics. Repair expired-reply UI
   and cleanup; consider a clearly displayed, bounded manual-exchange budget
   that allows realistic copying, without changing session limits or auth.
4. Use fresh invitation/reply codes for the next WAN attempt. Distinguish ICE
   route discovery, DTLS/SCTP channel opening, pinned application TLS and host
   approval. The generic current error cannot justify a relay diagnosis.
5. Only after real connection succeeds, verify clipboard, files both ways and
   at least 35 minutes idle on those networks. If direct traversal remains
   blocked, document measured constraints and design an explicit authenticated
   relay fallback; do not invent a deployed public relay or promise universal
   P2P reachability.

### Continuation (second agent, 2026-09-30)

- Hosted Windows CI on `c0fda8c` built the integrated patched `datachannel.dll`
  through `build-all.ps1`, then passed `verify.ps1 -Safe` and
  `--p2p-signaling-delay`. Linux, Android build and Android emulator jobs passed.
  This is hosted-runner evidence, not WAN evidence.
- Root cause addressed at the application level: in manual guest P2P the
  controlling PC starts ICE when it creates the reply, while the sharing PC starts
  only after a human copies the reply back. `src/GuestRendezvous.cs` now returns the
  reply automatically through the existing public broker. The broker ID and route
  are HMAC-derived from the invitation secret (no invitation format change); the
  reply is sealed with `SignalCrypto` under that secret, verified with
  `VerifyReply`, and only the first matching reply is accepted and acknowledged.
  Older peers, broker outages and `LUME_MANUAL_GUEST_REPLY=1` keep the manual path.
- `PeerTransport.Phase` and phase-specific failures distinguish no route (ICE),
  route without secure link (DTLS), and data channel. The viewer waits up to 10
  minutes with a visible countdown; native disposal runs off the UI thread.
- Tests: `GuestRendezvousIdentities` (offline, main suite) and
  `GuestRendezvousPublic` (`--signal`, public broker; a non-blocking CI step).
- Still unproven: a real two-network guest connection. Next WAN attempt: fresh
  codes, note whether the viewer says the reply was received automatically, and
  the phase shown at failure. Then clipboard, files both ways and 35 minutes idle.
- Review fixes on top of the audit branch: the dashboard no longer cancels
  Windows shutdown/logoff unless a recording is still being saved; the Rust
  receiver rejects a repeated offer and ignores a repeated finish while a file is
  being published (regression test fails without the fix); the Android document
  notice expires after 10 seconds; the control-pipe wait event is no longer
  disposed while its I/O callback can still set it; Disable revokes through
  host.dat when the disable-state lock is held (the owner can read that
  directory) and reports success once revocation is published. The unused
  in-place `scripts/apply-peer-patches.ps1` was removed.
- Next step for the owner's local agent: follow `docs/TWO-PC-TEST-PLAN.md`
  (two home PCs over WinRM; LAN flow first, then one PC on a different network).
