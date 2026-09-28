# Portable media (0.9 development)

Open Sound to request the host's system audio. Open Voice and explicitly allow
your microphone for that call. The host must accept in its own microphone window;
that window stays visible and closing it stops the microphone. Decline, timeout,
disconnection and a new connection generation invalidate old microphone packets.
Recovery never silently re-enables sound or a microphone. Use headphones: acoustic
echo cancellation is device-dependent and is not proven on the current hardware.

Linux uses the installed PulseAudio client tools (`parec` / `pacat`), including
PipeWire's PulseAudio compatibility service where supplied by the distribution.
It does not change global sound-server settings. The app owns and joins its child
processes. macOS 13+ source builds a small helper using AVFoundation and
ScreenCaptureKit; the OS still requires microphone and screen/audio permission.
Android uses AudioTrack/AudioRecord, runtime microphone permission and a visible
foreground service. iOS source uses AVAudioEngine/AVAudioSession. Mobile audio
stops when Lume leaves the foreground. No background microphone mode is enabled.

The shared wire format is the existing Windows PCM16 little-endian stereo format
at 48 kHz, raw DEFLATE compressed, with a positive stream generation. Each expanded
block is at most 19,200 bytes. Device queues hold at most four blocks and discard
overflow instead of growing without bound. Audio processing is independent of
image presentation. Format parameters are interoperability requirements, not a
claim about hardware fidelity, echo cancellation, latency or CPU consumption.

Record saves a new MKV file with MJPEG images (quality 90) and enabled system audio.
The recording worker follows coalesced frame notifications and accepts a source
refresh target (0) or custom 1–1000 FPS. One pending notification and the latest
image bound encoding work; slow encoding skips images and reports that count.
Variable timestamps reflect sampled frames, not the time an encode finishes.
Both microphones are excluded. Recording stops and finalizes when the
connection, display generation or resolution changes; it does not span recovery.
No recording duration cap is imposed. Available storage still limits recording.
MJPEG can use substantially more storage than Windows H.264 MP4. The portable
recorder does not provide the Windows MP4/H.264 codec controls. Target FPS is not
measured capture, network or recording throughput.

Desktop recordings go to Downloads with unique names. Android/iOS Recordings lists
keep completed files independently of connection cleanup and offer explicit OS
export/sharing. Those mobile export flows need execution on actual devices.
Files are created exclusively: an existing destination is never replaced.

Validation layers: synthetic TLS exercises consent/decline, stale generations,
rapid toggles and recording. Windows and Rust independently decode each other's
PCM blocks. FFmpeg independently decodes the MKV video and stereo tones. An owned
Linux VM exercises the actual PulseAudio client processes through a null sink.
These tests do not establish physical microphones, speakers, Android/iOS runtime,
macOS permissions, WAN latency or production performance.

Implementation references: [Matroska codec mappings](https://www.matroska.org/technical/codec_specs.html),
[Matroska elements](https://www.matroska.org/technical/elements.html),
[Apple sample-rate conversion](https://developer.apple.com/documentation/technotes/tn3136-avaudioconverter-performing-sample-rate-conversions).
