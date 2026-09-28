# Portable parity implementation

The 0.7 archives remain immutable checkpoints. This work extends the shared Rust
viewer, desktop UI, Android JNI app and iOS source without changing installed
Windows hosts or publishing a release.

## Implemented in 0.8

- Pair using the existing one-time Windows code, protect the returned credential
  locally, and reconnect through encrypted signaling without exchanging replies.
- Browse remote folders; upload and download files with bounded workers, explicit
  destinations, progress, cancellation, SHA-256 validation and no overwrite.
- Use the existing Windows packet layouts. File capability and host authorization
  remain mandatory; no unsolicited local file writes or downloads.
- Keep network/ACK handling independent of filesystem and UI operations.
- Verify Rust tests, real Windows/Rust protocol fixtures, Android compilation and
  Linux native execution separately. Apple SDK/runtime acceptance remains open.

Current results are in VALIDATION.md and the v08 evidence files. Saved access
interoperability, files in both directions, the scoped Linux host and private-X11
UI/input/clipboard checks passed. Android compiled; mobile storage/lifecycle and
Apple build/device acceptance have not run. This is progress toward the requested
full parity, not a declaration that the entire option has been completed.

## Regression boundaries

Signaling authentication, credential retention, path traversal, symlink handling,
partial-file cleanup, cancellation during slow storage, stale responses, offline
hosts, broker deadlines, multiple independent viewers and legacy image negotiation
must retain explicit bounds. No credentials, invitations or paths from a user's
actual files belong in diagnostic output.

## Remaining full parity

Portable host permanent service, portable monitor selection, folder-job recovery,
audio/voice, recording/printing, Wayland portals, mobile lifecycle/device testing
and Apple compilation are separate acceptance requirements. This document does
not mark them complete or authorize publication.
