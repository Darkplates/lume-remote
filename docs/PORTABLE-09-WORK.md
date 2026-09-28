# Portable 0.9 work

The owner selected permanent Linux access/recovery and multimedia/Apple work.
The verified 0.8 archives are the immutable comparison point. Do not update the
installed Windows host, publish, or perform physical power actions in this work.

## Implementation and acceptance

1. Saved-session recovery: fresh authenticated invitations, cancellable backoff,
   preserved quality, no replay of input/clipboard/file commands, and terminal
   handling of malformed/authentication failures. Verify actual reconnect,
   changed TLS identity, state/frame generations and cancellation.
2. Owner-enabled Linux graphical-session host: protected credentials, durable
   pairing/revoke, bounded broker work, one active controller, explicit shared
   folder and visible management. Keep guest consent separate. Test service
   configuration and native execution inside the owned Linux laboratory.
3. Multimedia: shared bounded PCM protocol, explicit system-audio playback and
   voice consent, platform playback/capture adapters, recording with validated
   output, and no microphone activation before local permission. Test synthetic
   data independently of real devices.
4. Apple: integrate the same recovery/media/host mechanisms where the OS permits;
   improve build/validation automation. Apple SDK compilation and OS permissions
   require an actual Apple environment and must remain explicit if unavailable.

Security/regression points: broker authentication and replay, secrets at rest,
revocation during capture, stale input across reconnect, queues/cancellation,
audio generations and permission denial, mobile lifecycle, and build packaging.
Dependencies, protocol capabilities and platform support claims must be documented.

## Implemented checkpoint

All four source workstreams above are implemented. The permanent host remains a
graphical-session user process, not a pre-login service or wake mechanism. macOS
now targets 13+ for system-audio capture. Mobile host roles remain outside this
checkpoint. Portable recording uses MJPEG/PCM MKV at up to 30 sampled images per
second; it does not claim parity with the Windows MP4/source-FPS recorder.

Evidence includes Windows-safe regression, shared core/C ABI/native P2P checks,
real authenticated product interoperability in both directions, fresh-session
recovery and live revocation, native Linux Xvfb/keyring/daemon/null-sink checks,
Windows/Rust PCM cross-decoding and independent FFmpeg MKV decoding. Android Java
and ARM64/x86-64 native builds have compiled, with APK signature/alignment checks.

The Linux recovery test originally checked the new frame generation immediately
after the acceptance packet, before the first new image. That test race was
reproduced and corrected to wait for a non-empty image of the new generation;
input was already gated on the presence of that image in production code.

A final production correction distinguishes malformed media from transport loss:
decoder errors, including a truncated image reported as unexpected EOF, terminate
the session instead of entering saved-session recovery. A regression test covers
both truncated PNG decoding and a genuine transport EOF.

The final versioned verification report records exact outcomes and artifact hashes.
Apple SDK compilation/execution, physical/mobile audio and devices, separate-PC
WAN/reboot/wake, other Linux distributions and independent review remain unverified.
No publication, installed-host update or physical power action is included.

Final Windows shared tests passed (34 core + 2 C ABI), and final Linux shared tests
passed (35 core + 2 C ABI). The optimized Linux app also passed the two X11 checks
and separate protected-host/audio-adapter tests. Its VM hashes matched the packaged
inputs. The owned VM was shut down normally after validation, preserving its disk.
