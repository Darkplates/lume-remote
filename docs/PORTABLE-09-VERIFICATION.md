# Portable 0.9 verification

Date: 2026-09-28. This is a development checkpoint, not a public release candidate.
The owner selected permanent Linux access/recovery and portable multimedia/Apple
implementation. The immutable 0.8 packages remain available for comparison.

## Implemented scope

- Saved portable sessions recover after transport loss using fresh authenticated
  session credentials, preserved quality and cancellable backoff. Stale input,
  clipboard, file and media actions are discarded. Malformed/authentication/codec
  errors terminate the session; a decoder EOF is not a transport EOF.
- Explicitly enabled permanent desktop access uses encrypted host settings and
  an OS keyring key. One-time pairing, individual revocation, input/folder consent
  and a separate graphical-session host are implemented. Linux uses Secret Service
  and XDG autostart; macOS source uses Keychain and LaunchAgent.
- Portable system audio, voice consent at both endpoints, bounded playback/capture
  workers and local MKV recording are implemented. Recordings contain sampled
  images and enabled system audio, at most 30 image samples per second, excluding
  microphones. Recording stops on disconnect/recovery or resolution change.
- Android integrates service/permission handling, playback/capture and persistent
  recording export. iOS integrates corresponding SwiftUI/AVAudioEngine source.
  macOS has a new ScreenCaptureKit/AVFoundation helper and now targets macOS 13+.

See [access boundaries](PORTABLE-ACCESS.md) and [media boundaries](PORTABLE-MEDIA.md).

## Evidence

| Layer | Observed outcome | What it establishes |
| --- | --- | --- |
| Windows managed application | 92 passed, 0 failed | Safe protocol, authorization, codec, file and native P2P regression checks |
| Shared Rust on Windows | 34 core + 2 C ABI tests passed; 0 ignored | Includes native P2P, fresh recovery/TLS/frame state, malformed media, storage and teardown |
| Shared Rust on Alpine Linux VM | 35 core + 2 C ABI tests passed; 0 ignored | Same shared behavior under musl, plus Unix filesystem/symlink coverage |
| Permanent host fixture | Pairing, encrypted persistence, no capture while pairing, host restart/recovery and live revoke passed | Synthetic host, public rendezvous, same-machine endpoints |
| Windows viewer to portable host | Pairing, two P2P connections, live revoke and rejected key reuse passed | Actual product protocol interoperability with synthetic desktop content |
| Portable viewer to Windows host | Pairing, two P2P connections, live revoke and rejected reuse passed | Reverse product direction with isolated credentials |
| Portable viewer/Windows files | Browsing, download, empty file, SHA-256, collision preservation, two uploads and worker join passed | Isolated real filesystem operations while synthetic video continues |
| Native Linux X11 | Two isolated UI/capture/input/Unicode clipboard checks passed | Software-rendered owned Xvfb display, not a physical desktop |
| Linux host lifecycle | Protected keyring persistence, autostart configuration, real daemon, singleton and disable passed | Owned VM user context, not pre-login or physical reboot acceptance |
| Linux audio adapter | Virtual PCM capture/playback and process cleanup passed | Actual PulseAudio client processes against an owned null sink |
| Windows/Rust audio wire | All 19,200 PCM bytes decoded identically in both directions | Wire compatibility independent of audio hardware |
| Portable media/recording | Synthetic TLS consent, denial, stale generation and rapid toggles passed | Authorized media state transitions and bounded synthetic flow |
| Independent MKV decode | 43 frames, 32 distinct colours, 320 x 180; 50,880 samples/channel at 48 kHz | FFmpeg independently decodes changing video and distinct stereo tones; physical audio is untested |
| Android | ARM64/x86-64 native and Java/test APK compilation; lint has 0 errors, 11 warnings | Build/static evidence only; see versioned APK signature/alignment report |
| Apple build script | Shell syntax check passed | Does not establish Swift, Rust/Apple linking or SDK compilation |

The Linux core run took 143.57 seconds under QEMU TCG; the C ABI run took 1.81
seconds. These timings are test execution measurements, not desktop performance
benchmarks. Native X11/host/media acceptance and artifact checks are recorded
separately in `docs/evidence/v09-*.txt` and the outer versioned verification file.

The earlier Linux recovery test had an assertion race: a late frame from the old
connection could satisfy its sequence check, then new acceptance arrived before
the first new image. The test now waits for a present image of the newer epoch.
Production input already required a present current image. The final full Linux
suite passed after that correction. A separate production correction distinguishes
decoder failures from transport EOF; its new regression passed on both platforms.

## Packaging and integrity

The final outer `LumeRemote-0.9.0-dev-verification.txt` lists archive byte sizes,
SHA-256 values and exact final packaging outcomes. ZIPs contain per-file manifests;
the Alpine archive contains a per-file manifest and preserves executable modes.
Packaging uses an explicit source allowlist and excludes private host settings,
saved-computer credentials, session logs, build caches and synthetic private keys.
The Android package includes ARM64/x86-64 libraries and dependency notices; the
separate instrumentation APK is not embedded in the application.

Notice inventory: 491 of 496 locked registry crates have collected notice files.
Five entries require upstream review. There are 869 collected crate notice files
and 13 Rust distribution notice files. This is not an independent legal/security
review or a claim that every locked dependency is linked into every platform.

## Unverified and incomplete

- macOS and iOS have not been compiled with Apple SDKs or run. The owner accepted
  unavailable macOS execution; that does not validate compilation or permissions.
- Android has not executed on a working emulator or physical device. Two earlier
  emulator versions failed before boot/ADB. No instrumentation result is claimed.
- No physical microphone, speaker, desktop capture, injected input, printer, SMB
  share, lock/login/reboot/wake or different-network pair of PCs was tested here.
- The Linux binary specifically targets Alpine/musl x86-64. Other distributions,
  hardware GPU behavior and Wayland control remain outside the evidence.
- Permanent portable hosting starts after graphical sign-in. It is not a root
  service, pre-login access, unconditional wake or host process-crash supervisor.
- Portable viewer folder jobs/resume, monitor switching and mobile host roles
  remain incomplete. MKV recording is not Windows MP4/source-FPS parity.
- Desktop/mobile image presentation intervals do not establish 180 FPS delivery.
  No measured resource or performance superiority over another product is claimed.
- Independent review, full dependency notice review, publisher signing, managed
  relay infrastructure and signed automatic updates remain open release work.

The installed Windows 0.3 host was not updated or interrupted. No public repository
was created or pushed. The required release-gate checker remains blocked with exit
code 2; an accepted platform-testing exception does not clear unrelated gates.

## Reproduction

Use the platform prerequisites and commands in [ports/README.md](../ports/README.md).
For the shared tests, set `LUME_TEST_DATACHANNEL` to the matching native library:

```sh
cargo test --manifest-path ports/Cargo.toml -p lume-core -p lume-bridge --locked -- --include-ignored
```

Use Windows PowerShell 5.1 for `scripts/build.ps1 -Tests` and
`scripts/verify.ps1 -Safe`. Product fixtures require a new isolated scratch folder
for every run. Linux native tests must use the owned Xvfb test script; host/media
tests require the explicit owned-laboratory flag and their private DBus/keyring/null
sink environment. Never substitute the user's physical desktop or audio device.

The synthetic recording can be independently decoded with
`scripts/verify-portable-media.py <fixture.mkv> --report <report.json>` and the test
dependency `imageio-ffmpeg`. It is not part of the application runtime.
