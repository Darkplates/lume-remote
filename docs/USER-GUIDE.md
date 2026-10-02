# Lume Remote user guide

Details moved from the README. For the overview, see [README](../README.md).

## Windows and sessions

Closing the main window hides it in the Windows notification area. Remote windows stay open. Double-click the tray icon to bring the dashboard back. You can control several different PCs at once, each in its own window. **Disconnect** closes only that session. **Exit Lume and disconnect viewers**, in the tray menu, closes all viewers; an enabled host service continues running.

## Quality

| Preset | Resolution and frame-rate target | Encoding |
| --- | --- | --- |
| Source | Original resolution and display refresh | Lossless, exact pixels |
| Smooth video | Up to 1080p, 60 FPS target | H.264, hardware when available |
| Save data | Up to 360p, 10 FPS target | JPEG |
| Custom settings | Independent resolution, FPS and compression | Selectable |

H.264 uses lossy 4:2:0 colour sampling. Choose Source for exact text and colours. Hardware detection is reported in **More → Connection details**. The current decoder and pixel conversions use CPU work. Selecting 180 FPS does not establish that either PC or the network can deliver it. Unchanged screens send fewer images.

## Updating an existing installation

Extract into a new folder and close the old viewer on the controlling PC. Open the new copy there.

If the target already has permanent access enabled, run **UPDATE-HOST.bat** from the new folder, or use **Settings → Update installed host**. Windows requests administrator permission; the host restarts and the old installed dashboard closes. Pairings and the enabled/disabled access setting are preserved. A backup of replaced installation files is retained. If the old dashboard is hidden, exit it from its tray menu before updating.

Use the new viewer for automatic recovery. Both endpoints need 0.6 for resume, network-folder discovery and voice. The earlier tools/folders need 0.5. Versions 2 and 3 retain their original layouts; an older viewer can fall back to protocol 2 on a newer host. Image-mode protocol compatibility with 0.3 is tested locally.

## Files and clipboard

In a connected session, choose **Files**, open a remote drive and folder, then **Send files** or **Receive selected**. You can select files or folders, use **Send folder**, drag local items into the remote list, and cancel a batch. The desktop remains connected. Files use the same authenticated, encrypted connection; no cloud upload is involved.

Choose **More → Send clipboard text** to copy local text to the remote PC. A paired computer uses the permission already granted; a guest's text still requires local acceptance. Use **Get remote clipboard text** for the opposite direction. **Sync clipboard text** is an explicit paired-session opt-in; it stops at disconnect and starts disabled after reconnection.

File access follows the signed-in owner's Windows permissions. It supports ordinary files on local fixed/removable volumes and explicitly configured network roots, without an application file-size quota; free space, Windows permissions, 240-character paths and throughput still apply. Linked folders remain denied. Folder copies preserve empty directories and nested content. Cancellation keeps completed items and deletes the active partial. A network interruption preserves paired single-file resume state: retry the same file into the same destination to verify and reuse its prefix. This is not automatic batch/folder resume; retrying a folder creates a new unique root. Guest transfers do not retain resumable state. See [security details](SECURITY.md).

## Connectivity and scope

Saved access uses encrypted messages through the public PeerJS signaling service, and Cloudflare STUN for address discovery. The desktop travels through the peer connection. These external services have their own availability and policies. Some network combinations require a reachable relay; no automatic TURN service or free hosted relay bandwidth is supplied. Manual P2P signaling, LAN/VPN connections and a self-hosted TCP relay are available.

### Pairing your own PCs

1. On the PC you want to reach, choose **Enable access**, then **Pair another PC**. It shows an
   eight-digit code, for example `4829 1307`.
2. On your other PC, choose **Add a computer** and type those digits.
3. Both PCs then show the same six-digit number. Check that it matches and choose
   **They match** on both. If the numbers differ, stop: nothing is shared.

The digits only help the two PCs find each other through the broker. The matching number
proves that nobody in between swapped keys. Afterwards the PCs exchange the existing one-time
pairing code encrypted. The full code can still be copied and pasted instead, for example
without Internet access to the broker.

### Guest invitation links

**Copy link** creates an https link to a static page (`open.html` on GitHub Pages). The page
reads the invitation from the part after `#`, which browsers never send to any server,
removes it from the address bar and opens Lume through the per-user `lume-open:` protocol.
Lume registers that protocol for the current Windows user when the dashboard starts. A link
never connects by itself: Lume asks first, and the sharing PC still approves. Without Lume
installed, the page explains how to get it and offers **Copy invitation**. Pasting the link
or the invitation into **Private invitation** also works.

### Guest access over the Internet (P2P)

1. On the PC you share, choose **Guest access → Start sharing** and send the invitation privately.
2. On the other PC, paste the invitation and choose **Connect to computer**. Lume sends its reply back to the sharing PC automatically through the encrypted broker, so nobody has to copy it back in a hurry.
3. If the P2P window says automatic delivery did not reach the sharing PC (for example an older Lume version there), copy the reply and paste it on the sharing PC.
4. Approve the request on the sharing PC. For P2P, the approval shows a check code; ask your guest to read the code shown on their screen and decline if it differs.

The windows show which step the connection is in: checking network routes, route found, securing the connection. If it fails, the message says which step failed. "No direct network route was found" means the routers did not let the two PCs reach each other; a shared VPN or your own relay avoids this. Codes work once: after a failure, stop sharing and start again for fresh codes.

Set the environment variable `LUME_MANUAL_GUEST_REPLY=1` on both PCs to keep the reply exchange fully manual, without contacting the broker.

An always-on helper in the remote network or suitable router support is needed for Internet wake. The sleeping PC also needs compatible hardware, firmware, standby power and network configuration. Physical wake and secure-desktop behaviour are separate acceptance checks.

This is an unsigned Windows development build. System audio, consented voice, AAC recording and PDF print forwarding are implemented; physical audio devices, real SMB shares and a physical printer still need commissioning. PDF printing uses the Windows renderer and a local printer dialog; it is not a virtual Lume printer driver for arbitrary remote applications. Export other formats to PDF first. An automatic updater is not included. Portable implementations are partial and have separate acceptance gates; macOS is explicitly untested. Feature parity or performance superiority over other remote desktop products has not been established. The owner authorized this public development source checkpoint; stable binary release criteria remain open. See [release contract](PARITY-CONTRACT.md), [validation](VALIDATION.md), [feature status](FEATURES.md) and [security](SECURITY.md).
