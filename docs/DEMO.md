# Demo kit

Open `docs/demo.html` from an extracted checkout for a five-step interface preview.
It works offline, uses no external script, and sends no measurements. The PNGs come
from real Windows forms rendered by the isolated UI fixture, with synthetic saved
computers, files and desktop content. They are **not** a recorded physical WAN
session, connection timing or delivered-FPS evidence.

Regenerate those assets from a built checkout:

```powershell
.\tests\LumeTests.exe --ui-preview verification/demo-current
```

Inspect the output before replacing public images. Keep the sample labels and
synthetic fixture visible. The existing source-preview images remain the published
interface examples; the preview identifies their scope on every step.

## Record the first physical demo

Use two owned Windows PCs on different networks and an empty test desktop. Show
exact versions/commit and route. Record an uninterrupted master before editing a
shorter public clip. Pair privately first: the public video must never display a
usable invitation or credential. Do not blur credentials as the only safeguard;
revoke any invitation shown in a recording before distributing it.

| Approximate segment | Show | Evidence to retain |
| --- | --- | --- |
| 0–10 s | Open Lume and connect to the saved PC | Uncut click-to-visible-image section, with timer methodology |
| 10–20 s | Move a test window and type into a scratch document | Actual input response, not just a video stream |
| 20–30 s | Select Source, then Save data | Source dimensions and settings; do not label selected FPS as delivered FPS |
| 30–40 s | Explicit clipboard text and a small file transfer | Public sample text and matching source/destination SHA-256 |
| 40–50 s | Close dashboard while the viewer remains open | Session still usable; reopen via notification area |
| 50–60 s | Disconnect and show the repository | Preview label, platform status, where to report a bug |

This is a script, not a promise that every step takes the displayed time. Report
failures, route changes and edits. Keep an uncut version linked alongside any short
clip. A separate repeated benchmark is needed for comparisons with other products.

For a long-session demonstration, retain 60-minute idle/minimized evidence and the
successful input/transfer at the end. A short clip cannot demonstrate that no
timeout or disconnection occurs during a longer run.
