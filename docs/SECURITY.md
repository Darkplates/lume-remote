# Security boundaries

Lume Remote is an unsigned preview, not an independently audited remote-access product.

## Authorization and transport

Guest capture/input starts only after local approval, with a 60-second decline deadline. The control dialog explicitly includes file browsing and transfer. View-only sessions cannot inject input, send clipboard text or use files. Each host accepts one viewer; distinct remote PCs can have independent viewer windows.

Permanent access requires normal Windows administrator setup and explicit pairing. One-time codes expire and are consumed; each controller receives a separate protected key. Disabling access or revoking a controller terminates its paired session. Saved settings use Windows DPAPI, plus ACLs limited to the granting owner, SYSTEM and Administrators. Only administrators can modify the installed executable.

Sessions use fresh secrets and TLS 1.2 or newer with exact SHA-256 certificate pinning. The pin also authenticates the protocol capability marker. No trusted root certificate is installed. WebRTC transports the pinned TLS stream over its encrypted data channel; the optional TCP relay only forwards opaque bytes.

Automatic signaling encrypts/authenticates SDP and session credentials before sending through the public broker. A guest's P2P reply also returns through it: the broker identity and route are derived from the private invitation secret by HMAC, the reply is encrypted and authenticated with that secret, and the sharing PC accepts only the first reply that matches its invitation signature. Older viewers, or `LUME_MANUAL_GUEST_REPLY=1`, keep the manual copy-back.

Short-code pairing uses the eight digits only as a public rendezvous. The joining PC commits to an ephemeral P-256 key (SHA-256 of key and nonce) before it sees the sharing PC's key, then reveals it; both derive a six-digit comparison number and a session key from the ECDH secret and the transcript. The sharing PC sends its existing one-time pairing code, encrypted under that key, only after its owner confirms that both numbers match, and the joining PC continues only after its owner confirms too. A relay that substitutes keys succeeds with probability about one in a million per attempt, and each code accepts one joining PC. Guest invitation links keep the invitation in the URL fragment; the `lume-open:` handler validates it and always asks before connecting. The broker can observe random IDs, network addresses, timing and ciphertext sizes. It can deny service. Saved-key signaling envelopes do not provide a forward-secrecy guarantee. STUN observes address-discovery metadata. No automatic TURN fallback is bundled.

### Guest link page

Guest links point to `https://darkplates.github.io/lume-remote/open.html`, the static `docs/open.html` served by GitHub Pages from this repository. Browsers do not send the `#` fragment that holds the invitation to the server, but the page's own script reads it, so that page and its hosting are a trust root for guest links: anyone who can change `docs/open.html` on the published branch, or the Pages configuration, could serve a script that reads invitations. The page carries a strict Content-Security-Policy that allows only its own hashed inline script and blocks script-initiated connections (fetch, XHR, WebSocket) and external resources; it does not restrict where links navigate. Link previews, mail scanners and chat services may rewrite, open or log the full URL including the fragment, so send links only over channels you trust, and paste the invitation instead when in doubt. Maintainers should protect the publishing branch (required reviews, no force pushes) because a change there changes the page every guest link opens.

## Clipboard and files

Clipboard text is transferred by explicit actions or explicitly enabled paired-session text sync. Sync polls only during that opt-in session and is disabled after reconnect. Simultaneous different edits pause sync without overwriting either clipboard. Paired access uses the existing control authorization; guests still receive a local clipboard preview. Work runs on STA. Duplicate requests and Windows clipboard contention return nonfatal notices. A successful receipt means the host copy completed. Text is bounded to 256 KiB per action; larger content can be sent as a file.

File transfer uses the same authenticated TLS connection and a separately negotiated capability. There is one active transfer per session, a bounded 32-message disk queue, 64 KiB chunks and at most 512 KiB awaiting acknowledgement. Listings are paged at 200 entries. File lengths and offsets use 64-bit integers; there is no commercial file-size or duration quota.

Ordinary received files use random `.part` names. Negotiated paired resume uses deterministic HMAC-derived `.lume-resume-*.part` names and authenticated `.state` journals in the selected folder; pairing keys are never written to those journals. Exact offsets, total length and SHA-256 must match before the final rename. A collision creates a numbered name rather than overwriting an existing file. Files are never executed or opened automatically. The API offers listing and copying, not remote shell, delete, permission modification or command execution.

A SYSTEM host worker must obtain the active signed-in console user's token and verify that its SID matches the granting owner. File operations impersonate that user, retaining Windows file permission checks. A different user or signed-out console cannot use the file channel. Portable sessions use their current process rights.

The file browser accepts local fixed/removable volumes and at most 32 owner-configured UNC roots. Network operations use the owner token without storing a network password. Paths outside that explicit allowlist, device paths, alternate data streams, traversal components, reserved Windows names and reparse points are rejected. The current implementation conservatively bounds full paths to 240 characters. It does not promise a sandbox against an already compromised local owner who changes filesystem objects concurrently; Windows ACLs and owner-token impersonation are the privilege boundary.

Explicit cancellation and failed verification delete the active partial when permissions/disk allow. Paired resumable transfers retain partials on transport loss, including the disconnected file-window cleanup. Retrying the same source/destination verifies the authenticated metadata, rehashes the prefix and transfers only the suffix. Wrong pair keys cannot reuse journals; a changed/corrupt prefix fails without closing the desktop. Partials are hidden from the app browser, are not complete files and can remain until retry or local cleanup. They are not encrypted at rest by Lume; destination Windows ACLs apply. Batch/folder jobs do not resume automatically. Directory creation is atomic; a fresh unique root avoids merging into an existing tree.

## Version 4 session tools

The pinned certificate marker negotiates version 4. Accepted capabilities are explicit; older layouts are unchanged. Requests/replies, chat history, audio blocks, annotation points and work queues are bounded. Short callbacks retain access to the thread pool; blocking queues and session loops use dedicated workers.

Display switches drain outstanding frames, release held input and advance a generation. Generation-tagged queued input is discarded on both ends after a display change. Viewer input remains disabled until the matching display image is presented.

Remote clipboard pulls require control permission and guest consent. Paired clipboard/audio/power callbacks check the signed-in owner. System audio starts only on explicit request; guests approve locally. WASAPI captures the default system-output mix, which can include other applications or user sessions. System audio does not include microphone capture. The separate voice call requires local permission at both ends on every call, including paired access. The host keeps a visible microphone control. Decline, timeout, disconnect or Stop releases the permission/capture; stale call generations are ignored. Queues discard old blocks under overload to bound latency. [Microsoft loopback documentation](https://learn.microsoft.com/en-us/windows/win32/coreaudio/loopback-recording).

MP4 recording is an explicit local action with a visible indicator, selectable FPS target and optional AAC system audio. It does not automatically record voice microphone channels. It writes a unique temporary file and renames after container finalization. Existing files are preserved. Normal close waits for finalization; a process/OS crash may leave an unfinished recording. [Microsoft sink-writer documentation](https://learn.microsoft.com/en-us/windows/win32/medfound/tutorial--using-the-sink-writer-to-encode-video).

Power requests are a fixed enum available only to paired control sessions, confirmed in the viewer. There is no arbitrary command/argument channel. Restart/shutdown use the Windows system executable without /f; Windows can wait for applications. A receipt confirms acceptance of the request, not an observed reboot. Tests inject harmless callbacks and never invoke native power actions.

Annotations are visible, expire after 30 seconds and close on teardown. They do not intercept local clicks. Native overlay and audio behaviour still require physical-device acceptance.

## Process and resource limits

The optional service runs as LocalSystem to attach to the active console desktop. This is a powerful authorization; pair only computers you control and trust. Windows sign-in and secure attention are not bypassed. Portable guest control retains normal integrity restrictions. Lock, UAC and reboot behaviour still require separate acceptance testing.

Network reads/writes, handshakes, frame acknowledgement windows, native codec input, dimensions, decompression and queues are bounded. Malformed or unauthorized protocol traffic closes a session; ordinary file/clipboard operational failures do not. A stalled or dead network can disconnect; paired viewers recover with fresh authorization and credentials. There is no commercial session timer.

Connection logs (`connections.log`) retain fixed failure categories, exception types and HRESULTs, without exception messages, private desktop content, file paths, clipboard text, credentials, SDP, addresses or peer names. Each connection log keeps at most one 256 KiB current file and one previous file. Two status files are different: the service's `status.txt` (in its protected machine directory) and `setup.log` (written by `scripts/permanent-access.ps1` next to the extracted package) are overwritten with the latest status, which after a failure is the exception message text (for setup, also any rollback failure message). Such messages can include local file paths or system error details; neither file records invitations, keys, SDP or desktop content.

## Remaining checks

No independent security audit, publisher signing, automatic update trust chain or competitor superiority has been established. A package hash detects corruption, not an untrusted distributor. File transfer and H.264 require 0.4 on both endpoints; image-mode compatibility with 0.3 is tested locally.

Two physical PCs on different networks, all keyboard layouts/DPI/GPU combinations, long sessions under real workloads, service updates under injected failures, locked desktops and physical wake remain distinct from local synthetic tests. See [validation](VALIDATION.md) and [feature status](FEATURES.md).
