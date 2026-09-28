# Contributing

Keep the app native, small and understandable. Describe the problem, the resulting behaviour, and the evidence for a change.

## Development

Use Windows x64 with .NET Framework 4.8, Git, Python and Visual Studio 2022 C++ Build Tools. Run `scripts/build-all.ps1 -Tests`, then `scripts/verify.ps1 -Safe`. Dependency source revisions are pinned and verified by `scripts/build-peer.ps1`. Build output stays under `build/`; local test reports stay under `verification/`.

`--safe` avoids global keyboard/mouse injection. The dedicated native input fixture intentionally sends Windows input to an owned test window; run the full fixture only on a machine you can spare. Use `tests/LumeTests.exe --native-capture` for the owned-window capture check, `--connections` for recovery, `--ui` for tray/multiple viewers, and `--idle-soak 2100` for a 35-minute synthetic P2P session.

Tests compile into a separate executable. Do not add production switches that bypass approval, pairing, certificate pinning or input authorization.

## Changes

Preserve protocol compatibility or document a negotiated version change. Keep transport, decoding and heartbeats independent from the UI message loop. Every new queue and parser needs a defensible bound. Disconnect must cancel recovery, release input and preserve revocation.

Do not commit settings, private invitations, keys, SDP, machine identifiers, local connection reports or captured desktops. Use synthetic data in screenshots and tests. UI screenshots in this repository are renders of the actual WinForms app with synthetic names.

Describe the validation layer: local unit/integration, native UI, actual GPU, public signaling, WAN, or second-PC acceptance. Avoid converting a target FPS or a synthetic codec measurement into a claim of real-world throughput.

## Reporting problems

Include the version, Windows version, selected quality, whether both peers were updated, and whether the issue happens while content moves or remains still. A sanitized event log is stored in the user's LocalAppData LumeRemote/Diagnostics folder; the installed host records events in ProgramData/LumeRemote/Host. Review anything you share. Never post pairing codes or private desktop screenshots.