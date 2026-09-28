# Lume Remote

Build a small, owner-controlled Windows remote desktop app. Project files and UI text are English. Do not add telemetry, subscriptions, commercial-use detection, session-duration caps or automatic firewall changes.

Permanent access requires explicit local opt-in, protected per-computer credentials, immediate disable/revocation, and visible management. Guest invitations require local approval. Windows service installation and updates use normal administrator/UAC consent and protected paths. Never bypass Windows authentication.

Security boundaries: exact SHA-256 TLS pinning, fresh session secrets, authorization before capture/input, bounded parsers and queues, explicit clipboard actions, and immediate disconnection. Do not log invitations, keys, SDP or private desktop content. Test approval and synthetic capture belong only in the separate test executable.

Keep network liveness, decoding and acknowledgements independent of the UI. Closing the dashboard hides it in the notification area; viewer sessions are independent. Disconnect must cancel recovery. Protocol changes must be negotiated and preserve documented legacy behaviour.

Use the Windows .NET Framework compiler and system libraries, with no NuGet/runtime dependency. Rebuild pinned native dependencies through the supplied scripts. Validate authorization, malformed packets, cleanup, codec fallback, recovery and native UI. Distinguish local, hardware, WAN, reboot, secure-desktop and physical-wake evidence. Never claim source FPS or comparative performance from a target setting.