# Public development launch

## Position and first audience

Start with Windows users connecting to their own home PCs: developers, creators and
people supporting their own machines. The first promise is a simple saved-computer
workflow with transparent source and no application session timer. Do not try to
replace enterprise support, device management and every mobile role in one launch.

Open source alone is not a unique advantage: RustDesk already exists. TeamViewer and
AnyDesk also offer personal-use options, so "the only free alternative" is false.
Win a specific workflow with lower friction and measured reliability. Publish useful
comparisons rather than insults, unverified speed claims or promises of unlimited
hosted bandwidth.

Reference points checked on 2026-09-28:
- [TeamViewer personal-use scope](https://www.teamviewer.com/en/global/support/knowledge-base/teamviewer-classic/licensing/personal-use/for-personal-use/)
- [AnyDesk personal/professional scope](https://anydesk.com/en/pricing)
- [RustDesk open-source project](https://github.com/rustdesk/rustdesk)

## Concrete launch assets

- [Interface preview and physical demo script](DEMO.md); open `demo.html` locally.
- [Measurement protocol and runnable collection/report tools](BENCHMARKS.md).
- [Prepared public post, introduction and private-message draft](LAUNCH-COPY.txt).
- [Platform acceptance](FEATURES.md), [security reporting](../SECURITY.md), and
  [machine-readable stable release gates](release-gates.json).

The interface preview is available now. A physical WAN demo and a controlled
competitor benchmark still require the actual endpoint pair. There are no fabricated
results standing in for them. The source/Windows prerelease is for contributors and
careful testing, not a declaration that all production gates have passed.

## Small beta, then a wider launch

1. Keep the default branch reproducibly buildable. Provide a complete ZIP, hashes,
   producing commit, setup steps, known limitations and a clear way to report issues.
2. Ask for ten opt-in Windows testers with different network combinations. Each
   should try pairing, reconnecting, 60 minutes idle/minimized, recovery, clipboard,
   a verified file transfer and revocation. Count failed attempts, not just successes.
3. Fix recurring failures before adding more features. Priorities are host/service
   lifecycle, NAT/relay reachability, truthful failure messages, consecutive file
   transfers, resource use on both endpoints and reliable recovery.
4. Record the physical one-minute demo and repeated comparison. Publish raw evidence
   with exact configurations. Let independent testers reproduce the results.
5. Share a short demonstration and a specific contributor request. Ask for Mac build
   help, real Android validation, security review and distribution/signing help.
   Review each community's posting rules before posting there.

These are proposed acceptance targets, not measured outcomes: ten testers complete
the scenario; every disconnection has an actionable report; no unresolved
authorization or data-loss bug; a fresh install can connect without developer help.
Track voluntary issue reports and follow-up feedback. Do not add telemetry to count
users. If the small beta cannot complete the workflow, delay broad promotion.

## Where a durable advantage could come from

| Priority | Deliverable | Evidence needed |
| --- | --- | --- |
| Trust | Signed distribution, explicit permissions, revocation, clear security reporting | Independent review and actual installed-host tests |
| Reliability | Predictable cross-network connection and recovery | Multiple physical NAT cases, idle/long sessions and relay failure tests |
| Simplicity | Pair once, saved PCs, quiet dashboard, easy update/rollback | First-time users complete the flow unaided |
| Efficiency | Good motion/text at a small host and viewer resource budget | Matched-quality, repeated measurements on real hardware |
| Ownership | Documented self-hosting and transparent running costs | Reproducible setup on an independent deployment |

A managed fallback relay has bandwidth and operational costs. Keeping the client
free does not make those costs disappear. Choose a sustainable, optional funding
model only with an explicit decision; do not silently add quotas, commercial-use
detection or subscription checks. Lume currently relies on public signaling/STUN and
does not ship a managed TURN fleet or an automatic signed updater.

macOS and iOS stay labelled uncompiled/untested because no Apple devices are
available. Contributions may close those gates; preparing source does not close them.

Publish a useful, reproducible project before claiming product parity. The first
public checkpoint is a development release, with platform roles and acceptance
boundaries visible in the README. No claim of outperforming competing products has
been established by the current evidence.

## First demonstration

Record a short demonstration on two owned physical Windows PCs on different
networks: one-time pairing, reconnecting from a saved computer, changing quality,
clipboard text, a verified file transfer and session disconnect. Hide credentials
and private desktop content. Include the commit, versions, CPU/GPU, display, network
type and whether the host was already running. A single fast connection is a useful
observation, not a comparative benchmark.

## Reproducible comparison

Use current licensed versions of each compared product on the same machines and
network. Separate cold launch from warm reconnect. Run at least ten repetitions
and publish raw timings, median and p95, idle/moving-image CPU and memory, visual
quality, transferred bytes and failures. Keep resolution, codec/quality target and
test content comparable. Report unsuitable NAT/TURN conditions and power failures.
Do not equate a selected FPS target with measured delivered frames.

## Invite contribution

Ask for a small number of specific contributions: Mac build results, physical
Android/iOS checks, Wayland portal integration, TURN deployment recipes, independent
security review and signed distribution. Provide reproduction steps and acceptance
criteria. Welcome bug reports without requiring users to expose pairing secrets.
Do not post claims that unfinished platform implementations are production ready.

## Sustainable operation

The source has no subscription, commercial-use detection or session timer. Hosted
signaling, relay bandwidth, signing and support still have costs. Document the
self-hosted path, dependencies on public services, and service failure behaviour.
Choose any future funding transparently without changing the published guarantees
silently. Reliability, ownership and clear costs are the competitive case.

## Before a stable binary release

Close the required production gates in release-gates.json: physical WAN and
long-session checks, installed-host lifecycle and security review, real devices and
printers, plus distribution trust. Apple remains explicitly uncompiled/untested
until someone with suitable hardware supplies reproducible evidence. Public source
availability and production readiness are separate statuses.

## 0.12.1 launch checklist

Nothing is posted automatically. Tick each item in order; stop at the first failure.

1. Two PCs you own, on different networks, both running the 0.12.1 ZIP from the
   release page (check the SHA-256 against the `.sha256` file).
2. On PC A: Enable access, Pair another PC. On PC B: Add a computer, paste the code.
3. Connect from PC B. Confirm the "... is connected" pill appears on PC A and its
   Disconnect button ends the session.
4. Clipboard text both ways, one file each way (compare SHA-256), a 60-minute idle
   session, reconnect after restarting PC A, then Revoke from PC A's settings.
5. Guest access: start sharing on PC A, connect from PC B, check the approval dialog.
6. Record the result (routes, versions, any failed attempt) as an issue or in
   `docs/evidence/`, without pairing codes or private screen content.
7. Only then: pin the release, add the promo video to the README/release, and post
   the drafts in [LAUNCH-COPY.txt](LAUNCH-COPY.txt), one channel at a time, answering
   replies the same day.
