# Changelog

## Unreleased (review fixes, not yet validated on Windows hardware)

These changes were compile-checked (C#) and unit-tested (Rust core/bridge, relay)
on Linux only. Windows UI, native video and two-PC acceptance remain pending.

- Local permission prompts (voice, system audio, clipboard) pause remote presses
  and pointer input so a guest with control cannot answer them. Voice consent
  focuses Decline; incoming chat no longer takes keyboard focus.
- Paired and portable signaling ignore malformed unauthenticated envelopes instead
  of ending the attempt and automatic recovery.
- Portable hosts bound buffered data before authentication and refuse FIFOs in
  shared folders; the hex validator rejects sign characters.
- The relay caps connections per source address and handles Python 3.10 timeouts.
- JPEG/PNG regions are drawn 1:1 when host and viewer use different display
  scaling; H.264 decoding accepts macroblock-padded widths such as 1366.
- Stalled paired transfers keep their resumable partial; finished recordings are
  never deleted on a name collision; a tool backlog answers busy instead of
  ending the session.
- UI: confirm Forget and Remove Windows service, Cancel/Esc in the code prompt,
  unverified-name wording in connection requests, accurate guest summary, real
  version in the footer, Exit full screen label, no clipped footer text.
- Display scaling: every form now declares its 96-DPI design size before adding
  controls, so buttons, rows, padding and dialogs scale with the text at 125-200%
  instead of clipping it. Sizes applied later use `Theme.Px`. The process stays
  system-DPI-aware; capture and input coordinates are unchanged.
- Dark theme: dark title bars, scroll bars, drop-down lists, number boxes and a
  themed Files list header where Windows supports them. High contrast keeps the
  system colours and native controls. `CHECK-UI-SCALE.bat` renders every form
  and runs the UI checks at the current display scale; screenshots at 100, 125,
  150 and 200 percent are still required before calling scaling fixed.

## 0.12.0 development

- Fix consecutive Windows file transfers by ordering the completion receipt before
  waking the next request, and releasing the sender slot when its terminal receipt
  arrives. Add a controlled receipt-ordering regression and a 32-file folder case.
- Fix public CI's complete Windows SDK selection and Linux GBM link dependency.
- Add an offline interface walkthrough, a physical demo script, a Windows process
  resource collector and a tested connection/resource report generator. No physical
  WAN or competitor benchmark is inferred from synthetic checks.

- Add paired portable single-file resume with authenticated checkpoints, prefix
  verification, exclusive file locks and cancellation cleanup.
- Add portable viewer drawing/clear controls for the Windows host annotation tool,
  with bounded strokes and display-generation checks.
- Add explicit Linux CUPS and Android system PDF printing; prepare macOS PDFKit
  and iOS print-panel source. Apple remains uncompiled/untested.
- Improve dependency notice provenance and document the public source preview,
  outstanding platform roles, physical acceptance and production release gates.


## 0.11.0 development

- Portable recursive folder jobs, including nested/empty directories, bounded
  depth-first enumeration, collision-safe roots, integrity and cancellation.
- Android document-tree staging/export and iOS folder-picker preparation.
- Portable recording source/custom FPS targets and one-slot frame notifications.
- Independent AGY Gemini 3.8 Flash High review and focused re-review completed.
  Correct Android export cancellation and gate the desktop folder action for legacy hosts.

Full parity, physical/device acceptance and public release remain incomplete.
macOS/iOS have not been compiled or tested because no Apple device is available.

## 0.10.0 portable displays and Apple preparation (unreleased)

- Add authenticated display enumeration and live selection to portable hosts,
  desktop viewers and Android/iOS interfaces using existing Windows protocol tools.
  Release held keys/buttons and require the correct display image generation before
  resuming input. Preserve the session when a requested monitor disappears.
- Replace fixed desktop/mobile presentation intervals with bounded desktop wakeups,
  Android Choreographer and iOS CADisplayLink. Network/ACK processing stays independent
  of UI scheduling, and mobile input uses the image actually displayed. Device FPS
  and high-refresh hardware acceptance remain unverified.
- Add Apple read-only preflight, selectable macOS/iOS builds, universal simulator
  packaging, artifact/signature/privacy checks, per-run reports and a Finder launcher.
  No Apple device is available; macOS and iOS have not been compiled or tested.
  The owner explicitly accepts preparation with this visible limitation.

## 0.9.0 portable host and media development (unreleased)

- Recover saved portable sessions with fresh authentication, cancellable backoff,
  retained quality and fresh input/frame generations. Do not replay old actions.
- Add explicitly enabled Linux graphical-sign-in hosts and macOS LaunchAgent source,
  authenticated one-time pairing, live revocation and encrypted host settings with
  OS key protection. Closing the dashboard does not stop the separate host process.
- Add the existing Windows PCM protocol, bounded device queues, system playback,
  per-call microphone consent and microphone shutdown across connection changes.
- Add local Matroska MJPEG/stereo-PCM recording, with independent decode evidence.
  Recording is optional, targets at most 30 sampled images per second, and excludes
  both microphones. Keep completed mobile recordings for explicit OS export.
- Add Android AudioTrack/AudioRecord adapters and iOS AVAudioEngine source. Mobile
  audio and recording stop on backgrounding; remote-session recovery remains active
  only while the OS allows execution. Android device testing remains unavailable.
- Add a macOS 13+ AVFoundation/ScreenCaptureKit helper and updated universal build
  recipe. Apple compilation, permissions, signing and hardware remain unverified.

## 0.8.0 portable access development (unreleased)

- Pair portable viewers with existing Windows hosts using one-time codes and the
  existing authenticated encrypted signaling protocol; reconnect without copying replies.
- Protect desktop credentials with an Argon2id/AES-GCM vault, Android credentials
  with Keystore and iOS credentials with Keychain. Removal is local; host revocation
  remains authoritative.
- Add paged remote browsing, upload/download, progress, cancellation, SHA-256 and
  collision preservation through a bounded filesystem worker. Android/iOS use
  document pickers and private staging instead of broad storage permissions.
- Allow a portable desktop host to expose one explicitly chosen directory as a
  virtual R: drive after local approval, including folder creation for Windows clients.
- Add real Windows/Rust fixtures for pairing, reconnect, revocation and files in
  both directions. Keep physical/mobile/Apple acceptance separate.


## 0.7.0 portable development (unreleased)

- Add negotiated exact-pixel PNG frames while preserving legacy Windows codecs.
- Use a DNS-valid TLS SNI for interoperability with the pinned Rust TLS host.
- Add a Rust transport/image core and native Linux/macOS desktop source, with guest
  approval, primary-monitor capture/input, explicit clipboard and separate viewers.
- Add Android and iOS viewer source plus a bounded C ABI/JNI adapter. Android keeps
  sessions in a visible foreground service and releases frame memory on disconnect.
- Join network workers on shutdown and keep native P2P callback contexts valid
  through cancellation, including partial TLS handshakes.
- Add Windows/Rust TCP/P2P interoperability fixtures and isolated native tests.
- Build the Android ARM64/x86-64 APK with verified dependency and Rust runtime
  notices; Android execution remains unverified.
- Build an optimized Alpine x86-64 package and exercise its fresh extraction,
  native UI, X11 capture/input/release and Unicode clipboard in an isolated VM.
- Portable feature parity, Apple builds, physical mobile devices and fresh WAN
  commissioning remain separate acceptance gates. No public release is claimed.

## 0.6.0 Windows development checkpoint (unreleased)

- Resume paired single-file transfers after network interruption with authenticated journals, prefix verification and full-file identity.
- Add explicit owner-configured network-share roots without exposing arbitrary UNC paths or storing network passwords.
- Add consented two-way voice, visible host microphone controls, generation isolation and bounded audio queues.
- Add AAC system audio and selectable/source FPS to MP4 recording.
- Encode video explicitly before MP4 muxing to prevent the sink writer silently reducing a 180-frame sequence to 60.
- Add remote PDF download, native preview, printer selection and confirmed local spool submission.
- Extend regression for interruption/cancellation/corruption, old settings, voice consent and PDF parsing/rendering.
- Preserve the installed 0.3 host. Physical hardware/WAN and other-platform acceptance remain separate.

## 0.5.0 development checkpoint (unreleased)

- Negotiate protocol 4 with bounded asynchronous tools and version 2/3 wire compatibility.
- Add clipboard pull and explicit paired text sync with conflict/echo handling.
- Add recursive folder copies both ways, preserving empty folders and existing destinations.
- Add live monitor selection and display-generation validation on frames and input.
- Add bounded session chat and temporary click-through annotations; suppress control while drawing.
- Add explicitly enabled WASAPI system audio with lossless bounded PCM blocks; keep microphone off.
- Add video-only MP4 recording through Media Foundation, fixed-canvas scaling, REC and normal-close finalization.
- Add confirmed paired lock/restart/shutdown without a general remote shell or forced application termination.
- Use dedicated blocking-session workers to keep the shared thread pool available.
- Add release gates and a review handoff. Cross-platform implementations, transfer resume and printing remain incomplete.

## 0.4.0 preview

- Add bidirectional file transfer with remote browsing, batch selection, progress, cancellation, bounded streaming and SHA-256 verification.
- Preserve existing destination files and restrict installed-host file access to the signed-in owner token.
- Fix paired clipboard delivery and nonfatal repeated/busy clipboard actions; display actual host receipts.
- Fix UI-dependent heartbeats that could let idle sessions time out.
- Decode and acknowledge frames outside the UI, with a bounded presentation queue.
- Recover paired sessions after transient transport failures; cancel on Disconnect.
- Preserve local failure categories in rotating, credential-free event logs.
- Hide the dashboard in the notification area when its X button is pressed.
- Keep concurrent viewers independent, with one window per remote PC.
- Simplify the dashboard, session HUD and quality presets; add an original app icon.
- Add optional Media Foundation H.264, actual hardware detection and lossless fallback.
- Negotiate protocol 3 through the pinned certificate; retain protocol 2 image compatibility.
- Provide explicit host updates that preserve pairings and access state.
- Add Windows CI, clean packaging and documented validation boundaries.

## 0.3.0

Saved computers, revocable permanent access, a Windows host service, live quality selection and wake-only helpers.

## 0.2.0

Native P2P negotiation and manual invitation/reply exchange.

## 0.1.0

Locally approved remote desktop over direct TCP or a self-hosted relay.
