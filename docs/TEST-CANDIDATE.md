# Audit remediation candidate: two-PC WAN check

This is a development candidate for the 2026-09-30 audit fixes, not a new stable
release. The application version remains 0.12.1; identify the candidate by its
archive SHA-256 and source commit in the accompanying evidence receipt.

## Run alongside the existing version

1. Extract the entire candidate ZIP into a new folder on each Windows PC.
2. Leave the existing application and installed host running. Open `START.bat`
   from the new folder on both PCs.
3. Use **Guest access** for this test. On the sharing PC, select the Internet P2P
   route and start sharing. Connect from the other PC, return its reply if
   requested, and approve the request on the sharing PC.

Both dashboards use the existing saved-computer preferences. This guest test
does not require pairing, installing/updating the host, or disabling permanent
access. Keep the original connection as the fallback while testing.

## Acceptance checks

Use two different Internet connections and disposable test files.

- Record time to connect and the route/status shown by the candidate.
- While moving a window, select 360p/10 FPS, then Source and a higher target FPS.
  Record delivered updates/s and RTT. A target rate is not a measured rate;
  unchanged screens intentionally send fewer updates.
- Send and receive clipboard text several times. Confirm both directions and
  that a second action immediately after completion works.
- Transfer a test file in both directions, then a folder containing an empty
  folder. Compare SHA-256, and confirm an existing destination file is preserved.
- Start a recording, stop it, then immediately close its viewer. Repeat by
  closing the viewer while recording and by using **Exit** from the tray. Reopen
  the resulting MP4 and seek near its end.
- Hide/close only the dashboard; confirm the viewer keeps working. Open a second
  guest session and confirm both viewers remain independent.
- Leave the candidate session idle for at least 35 minutes, with the dashboard
  hidden. Move a window again and confirm the session is still responsive.
- Disconnect and reconnect the candidate. Keep any interruption, error message
  and elapsed time in the result, even if a subsequent retry succeeds.

Report PASS/FAIL for each check, plus the two Windows versions, candidate archive
hash and any exact error text. Do not publish invitations, replies, pairing codes,
private clipboard text or desktop recordings. Real WAN, disk and device behaviour
must be reported separately from automated synthetic fixtures.
