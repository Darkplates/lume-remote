# P2P connectivity

Lume uses a reliable, ordered native WebRTC data channel. ICE gathers local candidates and uses Cloudflare STUN for public-address discovery. The desktop's pinned TLS session then runs inside DTLS/SCTP. STUN carries discovery metadata, not desktop traffic.

Saved computers exchange authenticated encrypted envelopes through `wss://0.peerjs.com/peerjs`. The real SDP, pair keys and session credentials are encrypted. The broker still observes connection metadata and can deny availability. Its terms and availability are external dependencies.

Guest P2P can instead exchange an invitation and authenticated reply manually. Treat both as private capabilities and send them only to the intended person. Local approval remains required before capture or input.

Some NAT/firewall combinations cannot establish a direct route. This preview does not configure TURN credentials or promise universal traversal. Direct LAN/VPN and a separately hosted TCP relay remain available.

Native dependency revisions, licenses and rebuild instructions are in [THIRD-PARTY-NOTICES](../THIRD-PARTY-NOTICES.txt). A local host-candidate integration test, public STUN discovery, public signaling and a session between two physical networks are different validation layers.