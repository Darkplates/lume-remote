# Portable saved access and files (0.9 development)

## Saved computers

Enable permanent access on the host, create a one-time desktop pairing
code, and paste it into the portable viewer's connection box. Linux/macOS desktop
first asks you to create or unlock a local vault. Android saves the pairing after
the host accepts it. iOS has a Save paired computer action. A saved computer then
connects directly from its list entry without the guest offer/reply exchange.

The desktop vault uses Argon2id (19 MiB, two passes, one lane) and AES-256-GCM with
fresh salt/nonce material. Unlock once per app run with your passphrase; no
plaintext credential fallback exists. Losing the passphrase requires pairing
again. Android uses a non-exportable Android Keystore AES-GCM key and an app-private
atomic file. iOS source uses a device-only Keychain item. App backup remains disabled.
The apps never put saved credentials into status snapshots, logs or clipboard.

Forget removes a credential from this device. Revoke it on the Windows host to
invalidate all copies and disconnect a live session. Linux/macOS host management
also provides per-controller revocation. A cancelled pairing may have
been accepted before its receipt arrived; remove unused entries on the host and
create a new one-time code if needed.
After a transport loss a saved portable session retries with fresh signaling/TLS
credentials, backing off from one to thirty seconds. Disconnect cancels recovery.
Authentication/protocol failures are terminal. Input, clipboard and file commands
are not replayed; sound, voice and recording do not resume automatically.

## Permanent desktop host

On Linux/macOS, open Permanent access, choose the computer name, control permission
and optional shared folder, then Enable access at sign-in. The owner can create a
one-time ten-minute pairing code, refresh the controller list, revoke a controller,
or disable access. One controller at a time is supported. Guest approval remains
separate and guest invitations cannot bypass it.

The host is a separate user process and remains available when the dashboard closes.
Linux installs a user XDG autostart entry; macOS source installs a user Aqua
LaunchAgent. It starts after graphical sign-in, not before login, and does not wake
an off computer. The OS must permit capture and input. Linux control currently
requires X11; Wayland control is not implemented. Sign-out, sleep and OS restrictions
can stop availability. There is no system/root daemon or process-crash supervisor.

Host settings are AES-256-GCM encrypted with a random master key saved in Linux
Secret Service via `secret-tool`, or macOS Keychain. Install libsecret and an unlocked
desktop keyring on Linux. There is no plaintext fallback. Files use owner-only
permissions and serialization locks, atomic replacement and bounded reads. The
daemon holds a separate singleton lock. Revocation is checked independently of
the broker and UI while capture is active. Network interruption retries registration
without tearing down an otherwise working P2P session.

Signaling preserves the existing Windows AES-256-CBC/HMAC-SHA256 envelope, verifies
the tag before decryption, binds both identities/request/stage, and bounds message
size and age. The existing PeerServer endpoint is a rendezvous dependency, not a
Lume-owned uptime or free relay guarantee. Session TLS still verifies the exact
certificate pin and fresh invitation key from the authenticated offer.

## File workflow

Open Files in an authorized session. Select a remote drive/folder. Upload a local
file/folder or download an entry; each transfer exposes progress and Cancel. Desktop
accepts a local path or a dropped item and an explicit download folder. Android
and iOS use the OS document/folder picker, private staging, and Save download to
export a completed copy. Export before disconnecting: mobile staging is temporary.
No broad device-storage permission is requested.

Network reads, video acknowledgements and heartbeat do not wait for file I/O.
The file worker bounds its queues, uses 64 KiB chunks and a 512 KiB send window,
checks offsets/lengths/SHA-256, rejects unsolicited writes, and preserves existing
files by choosing a collision suffix. Downloads are published only after validation.
The current atomic publication uses filesystem hard links; choose an internal
filesystem if a removable filesystem does not support them. Explicit cancellation
and ordinary disconnect remove the incomplete download. There is no portable
interrupted-transfer resume or persistent folder queue yet.

The portable desktop host can explicitly share one directory when control is
enabled. The local approval panel names that directory. Only after approval does
the host create its file worker. Windows-compatible viewers see a virtual `R:\`
root mapped to the selected directory. Other drives, traversal, symlinks and linked
ancestors are refused. Nothing maps `R:\` to the host's actual filesystem root.
Windows clients can browse, create collision-safe folders and transfer folder trees
through the existing negotiated file protocol. Portable viewer UIs now copy folders
recursively into new roots, including empty folders, without implicit merging.
Completed items survive cancellation; retry starts a new root. Names must be portable Windows-compatible components;
unsupported Unix names and internal partials are omitted from listings.

## Acceptance

Use `scripts/test-portable-access.ps1` for synthetic Windows-host file checks or
`-Pairing` for an isolated one-time code, two P2P reconnects and live revocation.
The pairing fixture uses the existing public rendezvous service for its own test
identities; it does not connect to the installed host. Use
`scripts/test-portable-file-host.ps1` for a Windows viewer and synthetic Rust host.
All scratch destinations must be new. Real keyboard/mouse injection is disabled
in the file fixtures. Private invitation files are removed during teardown.

Unit tests, Windows interoperability, Linux execution, Android compilation and
Apple compilation/device execution are different evidence layers. This version
does not claim complete platform or commercial-product parity. See the versioned
verification report and release gates for exactly what ran.

## 0.12 paired single-file resume

The 0.12 core adds negotiated paired resume; the earlier limitations above are superseded only for individual files. Folder batches remain session-only. See [portable collaboration](PORTABLE-COLLABORATION.md) for authenticated journals, cancellation, mobile cache lifetime and platform limits.
