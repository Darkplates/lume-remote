# Architecture

## Components

The native Windows Forms app targets C# 5 and .NET Framework 4.8. It uses Windows Schannel for pinned TLS, a C++ DXGI capture bridge, a Windows Media Foundation video bridge, and pinned libdatachannel/ICE/SCTP dependencies. A Python TCP relay is optional. No browser engine, account service, app telemetry or subscription check is included.

- `Session.cs`: TLS authentication, local authorization, framing, capture pacing and receive loops.
- `SessionHeartbeat.cs`: background liveness independent of the UI.
- `PeerTransport.cs`: ordered native WebRTC data channel exposed as a bounded stream.
- `PairedAccess.cs`, `SignalBroker.cs`, `TrustedStore.cs`: encrypted rendezvous, revocable pairings and protected local state.
- `PairedReconnect.cs`: cancellable recovery of saved sessions with capped backoff.
- `Capture.cs`, `AdaptiveFrameEncoder.cs`, `native/capture.cpp`: image capture, change detection and encoding selection.
- `VideoCodec.cs`, `H264Bounds.cs`, `native/video.cpp`: bounded H.264 bitstreams and Media Foundation encoding/decoding.
- `ViewerForm.cs`, `LatestFrameQueue.cs`: worker-thread decoding, ordered acknowledgements and one pending presentation bitmap.
- `HostServiceWindows.cs`: opt-in privileged service and console worker.

## Authentication and recovery

Each host session creates a fresh certificate and 256-bit session secret. A guest invitation or authenticated pairing envelope binds the exact certificate pin. TLS must pass the pin, protocol and cipher checks before the session secret is sent.

Screen and input resources are created only after local guest consent or current paired authorization. A host accepts one active viewer. Different hosts can be viewed concurrently in independent windows.

Saved reconnects use fresh encrypted rendezvous and fresh TLS credentials. They never reuse a failed session's input queue. Local cancellation, key removal, host disable and revocation remain effective. Guest invitations do not gain unattended recovery.

## Display pipeline

DXGI capture uses reusable textures and a bitmap, with GDI fallback. After a lost duplication (UAC, lock, mode change) GDI is used while DXGI is retried with backoff (1 s doubling to 8 s); the selected display's bounds are rechecked at most once per second and a changed resolution or position recreates capture and input bounds without a new monitor epoch. The host status shows the active capture backend. Unchanged frames and pointer-only updates skip unnecessary image work. Image mode hashes tiles and sends merged dirty regions as lossless XPRESS-HUFF or JPEG. Cursor position is separate. An idle desktop reduces capture checks.

H.264 is optional. BGRA is converted to NV12 on the CPU; Media Foundation prefers an available hardware encoder and otherwise tries the Windows software encoder. The active backend is reported, not inferred from the presence of a GPU. The current decoder and output conversion use CPU work. H.264 is lossy 4:2:0. Unsupported dimensions or encoder settings fall back visibly to lossless image mode. A synchronous encoder that needs more input keeps receiving the latest frame, even if unchanged, until it produces the buffered access unit. If the viewer's Windows decoder fails, the viewer keeps the last image, still acknowledges each frame, requests lossless images, shows a notice and saves that quality instead of the failing video setting; malformed envelopes and sequence headers still end the session.

Every received delta/video packet is decoded in order and acknowledged after decoding. Completed presentation images can replace older pending images without breaking delta references. The UI displays only the latest completed image. In-flight frames and input queues remain bounded. A missing acknowledgement is a transport failure, not a commercial duration limit.

## Wire compatibility

Packets retain a 4-byte little-endian length, a one-byte kind, and typed payload. Strings carry bounded UTF-8 lengths. TLS pins authenticate the protocol capability marker in the certificate. The viewer reads the pinned certificate's common name: `Lume Remote Session v4` selects v4, `Lume Remote Session v3` selects v3, and anything else selects v2. It sends that version in its authentication packet, and the host must echo the same version in its acceptance or the viewer closes the session. Hosts accept versions 1 to 4 and keep the older layouts.

V3 adds video/bitrate fields to quality packets, an accepted-session file-capability boolean, `StreamMetrics=16`, `Files=17`, and frame codec 2. V2 retains its exact image-mode layout and does not receive v3-only packets.

A frame contains sequence, image dimensions, region count, and bounded regions. Codecs 0 and 1 are JPEG and XPRESS-HUFF. Codec 2 is one full-image region containing a version byte, a reset flag, and an Annex B H.264 access unit. Sequence headers are validated before native decoding; a reset requires an IDR. New native video contexts are created and disposed on their owning worker thread.

## Windows lifetime

Closing the dashboard hides it in the notification area. Viewer windows remain independent. Explicit tray exit closes all viewers; Windows shutdown still ends the app normally. An enabled host service has a separate lifecycle. Closing a viewer stops its heartbeat, transport and retry loop, and releases the host's injected input.

The optional relay forwards opaque TLS bytes with backpressure. It does not terminate desktop TLS. Public signaling coordinates encrypted envelopes and is not a desktop relay.

## File and clipboard actions

File messages multiplex folder listing, upload/download offers, chunks, acknowledgements, finish hashes, results and cancellation on the authenticated TLS stream. Each endpoint allows one active transfer, streams 64 KiB chunks with a 512 KiB acknowledged window, and bounds its disk queue to 32 messages. Folder responses contain at most 200 entries per page. File lengths and offsets are 64-bit. A 30-second lack of progress aborts the transfer, not the desktop; there is no total transfer-duration quota.

The receiver creates a random `.part` file in the selected folder. It validates exact offsets, total bytes and SHA-256 before atomically moving to a final name. Collisions receive a numbered suffix; existing files are never overwritten. Cancellation removes the active temporary copy when disk access remains available. Paired resume preserves an authenticated partial/journal on network loss; retrying the same file/destination verifies its prefix and final identity. A process crash or loss of the owner token can leave a `.part` for manual cleanup. Automatic batch/folder-job resume is not implemented.

The installed SYSTEM worker obtains the active console user's token with WTSQueryUserToken and requires its SID to match the owner who enabled permanent access. File operations impersonate that owner, retaining Windows ACL enforcement. Paths must be local fixed/removable volumes or explicitly configured UNC roots. Device paths, unconfigured shares, alternate streams, traversal, reserved names and reparse points are refused. Guest control explicitly discloses file access in its local consent dialog. View-only and older protocol sessions cannot open the file channel.

Clipboard text is explicit, bounded and executed on a dedicated STA thread. Paired requests copy using the existing authorization. Guest requests resolve only after the local preview is accepted or dismissed. Busy/duplicate actions return notices instead of terminating the connection. The viewer displays a successful receipt only after the host completes the operation.

## API references

[Desktop Duplication](https://learn.microsoft.com/en-us/windows/win32/direct3ddxgi/desktop-dup-api), [H.264 encoder](https://learn.microsoft.com/en-us/windows/win32/medfound/h-264-video-encoder), [asynchronous Media Foundation transforms](https://learn.microsoft.com/en-us/windows/win32/medfound/asynchronous-mfts).

[Owner token](https://learn.microsoft.com/en-us/windows/win32/api/wtsapi32/nf-wtsapi32-wtsqueryusertoken), [STA clipboard API](https://learn.microsoft.com/en-us/dotnet/api/system.windows.forms.clipboard.settext).

## Windows 0.6 media and printing

The microphone path is separate from system loopback. Both users authorize each
voice call, host consent remains visible and a generation isolates stale callbacks.
Capture, network send and playback use separate bounded queues; native handles stay
on their owning worker threads.

Recording explicitly drives the low-latency H.264 transform and remuxes encoded
packets with actual timestamps. It avoids sink-writer RGB conversion thinning frames
to a driver's nominal rate. AAC system audio uses a bounded, gap-aware PCM timeline.
Requested recording FPS is a target, not measured hardware throughput.

PDF forwarding first verifies the transferred file, then uses Windows.Data.Pdf for
bounded native preview/rendering and PrintDocument for explicit local spool selection.
No document shell verb, arbitrary program, virtual printer driver or bundled PDF
parser is invoked. Non-PDF input is rejected.

References: [MPEG-4 sink](https://learn.microsoft.com/en-us/windows/win32/medfound/mpeg-4-file-sink),
[AAC encoder](https://learn.microsoft.com/en-us/windows/win32/medfound/aac-encoder),
[Windows PDF renderer](https://learn.microsoft.com/en-us/uwp/api/windows.data.pdf.pdfdocument).
