# Public development launch

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
