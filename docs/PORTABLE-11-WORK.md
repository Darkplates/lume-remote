# Portable folder jobs and recording follow-up

The owner selected further portable parity work, followed by an independent review
through AGY CLI with `gemini-3.8-flash-high` and `--effort high`. The review runs
after implementation and validation. Review findings must be checked against the
source, corrected where applicable, and retested before the final handoff.

This increment implements recursive portable viewer folder upload/download with
empty directories, bounded depth-first enumeration, collision-safe new roots,
per-file integrity, progress and cancellation. It uses the existing negotiated
Windows folder protocol. It does not introduce a new wire version, implicitly
merge existing trees, or claim persistent folder-job resume.

Portable recording gains an explicit source/custom FPS target and frame-driven
wakeups. Encoding and disk work remain off the network thread with bounded pending
work. Targets are not achieved FPS measurements; mobile execution and Apple build
acceptance remain separate.

Affected systems: portable file worker, desktop/mobile file controls and storage
pickers, shared recording state, C ABI actions and portable UI. Regression risks:
path traversal and links, stale cancellation packets, misleading partial success,
UI/network stalls, mobile provider permissions, recording teardown and timestamps.

Validation: recursive/empty folders and collisions in both directions; malicious
directory receipts and paginated entries; cancellation and follow-up transfer;
authenticated Windows/Rust interoperability; full core/bridge tests; available
native and Android builds; independent media decode; source/package verification.

The four 0.10 artifacts are immutable checkpoints. The installed Windows 0.3 host
stays untouched. No repository publication or personal message is authorized here.
macOS and iOS remain explicitly uncompiled/untested because no Apple device is
available, as already accepted by the owner. Remaining parity stays in FEATURES.md.
