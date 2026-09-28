# Idle connection recovery

## Reproduced failure

The previous viewer sent session pings from a Windows Forms timer and decoded/acknowledged frames on the UI thread. Both transports had a 30-second read timeout, and the host bounded missing frame acknowledgements. A stalled UI could therefore look like a dead peer.

An isolated 0.3.0 static-desktop fixture lost its connection after its UI message loop was deliberately blocked for 40 seconds. This reproduces a failure mechanism; it does not retrospectively identify the exact cause of an earlier user-reported disconnect, because that build retained only its current status.

There is no 30-minute commercial/session limit in the implementation.

## Current behaviour

A dedicated background heartbeat sends existing protocol pings every three seconds after authenticated acceptance. It is independent of UI timers, input queues and image changes. Receive/decode/ACK work also runs outside the UI. Unchanged pixels are not resent to manufacture a higher FPS number.

Network read and write bounds remain in place to detect dead peers. A paired viewer that loses its transport negotiates a fresh connection in the same window with delays of 1, 2, 5, 10, 20 and then 30 seconds between attempts. Attempts continue while that session window remains open. Authentication/protocol errors stop automatic retries. Local removal of the saved pairing stops recovery; host revocation is always rechecked before capture.

Disconnect and window close cancel retries. Guest sessions keep their approval workflow. Windows resume causes a paired viewer to request a fresh route. Neither process lifetime nor a session window is a guarantee that the remote computer or network will remain awake.

## Diagnostics

The host no longer relies on the overwritten status file as its only failure evidence. Event logs retain UTC timestamps, fixed event categories, exception types and HRESULTs. They exclude exception messages, addresses, peer names, invitations, keys and SDP. Each log rotates at 256 KiB and retains one previous file.

- Viewer: LocalAppData/LumeRemote/Diagnostics/connections.log
- Installed host: ProgramData/LumeRemote/Host/connections.log

See [validation](VALIDATION.md) for the idle soak and recovery tests. Two-network acceptance is separate from local P2P tests.