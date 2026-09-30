# Two-PC guest P2P test plan

For the agent running the owner's two home Windows PCs over WinRM. Branch
`claude/charming-planck-8zr2ox` (commit `09052be` or newer). Goal: produce honest
evidence for the guest P2P changes in `src/GuestRendezvous.cs`, `src/PeerForms.cs`
and `src/PeerTransport.cs`. Nothing here may claim a WAN fix unless a WAN run
actually connected.

## Rules

- Preserve every installed Lume host, its settings and running sessions. Build and
  run only in new folders (for example `C:\AI\lume-wan-test-v2`). Never run Enable
  access, UPDATE-HOST.bat or Settings → Update installed host.
- No firewall, router, NAT or network-adapter changes by script. Do not enable raw
  native logging. Never print, log or commit invitations, replies, keys, SDP,
  candidate addresses or screenshots containing codes.
- WinRM runs in a non-interactive session: it cannot show or click Lume windows.
  Launch the GUI in the signed-in desktop with a one-shot scheduled task
  (`/IT`, run as the signed-in user), or ask the owner to open it. Local approval
  (Allow/Decline) is always clicked by the owner. Do not automate it.
- Stop only processes started by this test (track PIDs); never kill Lume by name.
- Keep evidence in the ignored `verification/two-pc-<date>/` folder, secret-free.

## Steps

1. **Inventory both PCs:** Windows build, network adapters (no addresses in the
   report), running Lume processes and install paths, free disk. Confirm the
   installed host (if any) is untouched.
2. **Build once** on PC A: `scripts\build-all.ps1 -Tests`, `scripts\verify.ps1 -Safe`,
   then `scripts\package.ps1`. Copy the ZIP to PC B over WinRM
   (`Copy-Item -ToSession`) and compare SHA-256 on both sides. Extract to a new
   folder on each PC.
3. **Headless checks on both PCs** (no UI, no codes printed):
   `tests\LumeTests.exe --signal` (public broker and automatic guest reply) and
   `tests\LumeTests.exe --p2p` (native WebRTC and pinned TLS on loopback).
   Record pass/fail counts per PC.
4. **LAN run (same home network).** This checks the flow, not NAT traversal.
   - PC A (sharing): Guest access → Connection route: P2P Internet → Start sharing →
     Copy P2P invitation. Move the invitation to PC B without logging it (for
     example a temporary file deleted immediately after use, or the owner pastes it).
   - PC B (controlling): paste into Private invitation → Connect to computer.
   - Record, without codes:
     - whether PC B shows "The sharing PC received the reply automatically";
     - whether PC A applied it without a paste;
     - the phase lines shown;
     - seconds from Connect to the approval dialog;
     - the owner's Allow click;
     - the route line in PC A's status bar (expected "Direct P2P / host candidates").
   - Repeat once with `LUME_MANUAL_GUEST_REPLY=1` set for both processes, to confirm
     that the manual copy-back still works.
5. **Different-network run (the real bug).** Put PC B on a different network, for
   example the owner's phone hotspot with Ethernet unplugged and home Wi-Fi off.
   WinRM will be unavailable during this run, so prepare everything first and let
   the owner click through step 4 by hand. The owner notes three things:
   - the automatic-reply line;
   - the last phase line and any error text;
   - the route line (expected "Direct P2P / NAT traversal").

   After reconnecting, collect only the secret-free session logs from
   `%LOCALAPPDATA%\LumeRemote\Diagnostics` on both PCs. Mobile carriers often
   use NAT that blocks direct P2P. A clear "No direct network route was found"
   there is a measured constraint, not a Lume bug. Record it as such and, if
   possible, retry from another network (another home Wi-Fi).
6. **If a different-network run connects:**
   - clipboard text in both directions;
   - one file each way, comparing SHA-256;
   - stay idle for at least 35 minutes, then confirm input still works;
   - Disconnect, and confirm the "… is connected" pill disappears on PC A.
7. **Clean up:** close the test apps, and delete temporary code files and the test
   folders the owner does not want to keep. Leave the installed host as found.

## Report

Write a short English report with separate sections for:

- **Headless:** results for each PC.
- **LAN:** the automatic and manual runs.
- **Different network:** which network was used, the result and the phase.
- **Not tested.**

State plainly whether a real different-network connection was observed. If fixes are
needed, commit them on a new branch with tests and let CI run. Do not merge.
