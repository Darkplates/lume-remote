# Protocol 4 additions

TLS pin verification remains mandatory. The self-signed certificate name is exactly
Lume Remote Session v4. New clients negotiate 4 for this authenticated marker, 3 for
the old v3 marker, otherwise 2. The host continues accepting versions 1-3 without
changing their layouts. An old v3 client falls back to version 2 on the new marker.

The existing length-prefixed, little-endian protocol is retained. UTF-8 strings
have signed 32-bit byte lengths with operation-specific bounds. The version 4
Accepted message appends a 64-bit SessionCapabilities mask after the version 3
allow-files flag. Capabilities are conditional on authorization and available code.

## Display generation

Version 4 Frame and Input payloads insert a signed 32-bit display generation before
the old payload. Generation starts at 1. A monitor change drains outstanding frame
ACKs, releases held input, replaces capture/input bounds and increments generation.
Old-generation input is ignored. A queued viewer input keeps its original generation;
it is never relabelled when sent. The UI waits for presentation of a matching frame.
Source dimensions and refresh are returned with the monitor-selection receipt.

## Session tools

- Kind 18: request id (positive int64), tool enum (byte), tool-specific payload.
- Kind 19: request id, success bool, bounded error text, payload length, payload.
  Embedded payloads begin with the Kind 18 marker for reuse of the bounded parser.
- At most four locally pending requests, eight queued incoming operations, a
  75-second response deadline and a maximum request size of 262,220 payload bytes.
- Tool enums: clipboard read 1, monitor list 2, monitor select 3, chat 4, power 5,
  system audio 6, annotations 7, clipboard write 8, voice 9, portable images 10.
- Chat is bidirectional, at most 8 KiB per message and 100 displayed messages.
- Clipboard text is at most 256 KiB. Paired opt-in sync uses the read/write receipts;
  guest reads/writes remain subject to their local permission flow.
- Annotations carry generation plus zero (clear) or 2-128 normalized points.
- Power is the fixed Lock/Restart/ShutDown enum, paired control only. Receipt means
  request accepted; native outcome is not remotely proven by the receipt.

Malformed syntax terminates the session. Unsupported, declined or ordinary failed
operations return a tool error while frames/ACKs/heartbeat continue. Tool queues
run independently of network reads and use dedicated workers, not blocked pool slots.

## Audio

Kind 20 carries the requested audio generation, a bounded block length and a
losslessly DEFLATE-compressed block. Its first int32 is the expanded byte count.
Samples are stereo PCM16 at 48 kHz, at most 19,200 bytes (100 ms), with exact expanded
length validation. Playback buffers at most four blocks and discards older audio
under overload. Audio remains disabled until requested.

Voice capability 1024 adds tool 9 (enabled bool, generation int32), Kind 21
(generation plus the same bounded PCM block layout) and Kind 22 (call ended,
generation). Each call requires explicit microphone permission at both endpoints.
The host consent window remains visible with Stop microphone until teardown.
Stale generations cannot stop or inject into a later call. The 60-second consent
deadline is not a call-duration limit.

## Folders

File operation 11 adds atomic directory creation to the negotiated file channel.
It carries parent path, validated single-component name and collision policy, and
returns the created path through the existing Result message. Directory walks use
the existing paged listings; ordinary files retain their offset/length/hash checks.
Existing trees are never merged implicitly.

## Paired file resume and configured roots

Capabilities FileResume=256 and NetworkFolders=512 preserve the old file layouts.
File operations 12 GetResume, 13 PutResume and 14 OfferResume add a fixed 32-byte
full-file SHA-256 identity to offers. Operation 15 ResumeReady returns an int64
offset and 32-byte SHA-256 of the retained prefix. Sender verifies that prefix
before sending the suffix; receiver verifies full length and identity before rename.
Operation 16 Preparing refreshes activity during full-file hashing. Operation 17
Roots requests the extended local-drive/configured-share list. Old peers continue
using ordinary List/Get/Put/Offer/Ready; unauthenticated resume is not available.

Resumable partials and their HMAC-authenticated journals belong to a particular
paired controller and canonical destination. Network loss preserves them; explicit
cancellation or failed verification removes the active partial. Retrying the same
file/destination resumes; there is no persistent automatic batch/folder queue.
Only the owner can configure UNC roots. Owner impersonation and ordinary Windows
permissions apply; arbitrary UNC/device paths remain rejected.

## Portable images (0.7 development)

Capability 2048 advertises tool 10, whose body is one boolean. Success has an empty
tool-reply body after its Kind 18 marker. Enabling it selects codec 3 (PNG) for
lossless frames, with the existing frame/block geometry and byte-length envelope.
PNG dimensions must match the bounded block dimensions before decompression.
Capability 4096 additionally asks viewers to enable portable images for a host
that does not emit Windows XPRESS. A portable viewer requests JPEG until the tool
receipt, then applies the requested source/custom settings. Unsupported in-flight
XPRESS/H.264 packets are structurally validated and acknowledged during fallback.
The old quality packet layout and Windows-to-Windows defaults remain unchanged.

Portable TLS clients send the DNS-valid SNI `lume-remote`. Identity is still the
exact SHA-256 certificate pin in the private invitation, not a public-CA hostname
claim. No certificate validation is bypassed to support another platform.

## Portable saved access and files (0.8 development)

Portable pairing preserves the existing Windows broker identifiers, stages and
AES-256-CBC/HMAC-SHA256 envelope. The authentication tag is verified before decrypt;
source, destination, route, request and stage are bound to it. Successful pairing
does not create capture/input. A saved connection negotiates a fresh invitation
and pinned TLS session. Credential revocation remains controlled by the host.

The portable file channel reuses Kind 17 and existing v3/v4 FileOp layouts. Its
worker has bounded queues, a 512 KiB window and 64 KiB chunks. Paged listing,
upload, download, cancel and SHA-256 completion interoperate with Windows. A
portable host maps only its explicitly approved folder to virtual R:\ and
advertises folder creation in v4. It does not advertise resume or network roots.
Portable viewer UIs currently transfer individual files; Windows viewers can
use the host's negotiated folder support. See PORTABLE-ACCESS.md for storage
requirements, consent and the current recovery limitations.

## Portable folder jobs (0.11 development)

Portable viewers now use the existing capability 16 / operation 11 together with
paged List/Get/Put. A new top-level directory uses the unique-name policy; nested
directories must not merge. A creation receipt is checked against its exact parent
and permitted collision suffix before it becomes the next upload destination.
Enumeration is depth-first with a maximum depth of 64 and at most one 200-entry
remote page per level. Empty directories are retained. Duplicate remote names
that collide with an already received item stop the job without replacing it.
Per-file SHA-256 and offset checks remain unchanged. Cancellation retires bounded
request identifiers so in-flight packets cannot target the next transfer. Completed
items remain; explicit cancellation and disconnect remove the active partial.
These jobs are session-only. Resume and persistent batch journals are not added.

## Portable paired resume (0.12 development)

Authenticated saved connections now enable the existing capability 256 and file
operations 12–16. Guest invitations do not grant resumable state. Each local
checkpoint is HMAC-bound to the paired key, canonical destination, filename, length
and SHA-256; it is exclusively locked while active. Portable local journal layout
is independent of the Windows local journal format; the wire operations are shared.
Folder jobs retain ordinary operations and unique-root retry behaviour. Viewer tool
7 implements the existing annotation packet with at most four pending receipts.
