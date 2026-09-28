# Portable platform development

This workspace adds a Rust transport/image core, a native Linux/macOS desktop UI,
and Android/iOS viewer applications. It does not replace the .NET Windows app.
The 0.6 through 0.11 archives remain immutable checkpoints. Portable feature parity
has **not** been achieved. The repository is a public development source preview.

Use the source archive for these projects. The Windows executable archive includes
this guide but does not include the Rust/mobile project source or portable binaries.

## Implemented roles

| Platform | Viewer | Host | Current boundary |
| --- | --- | --- | --- |
| Windows native .NET | Full current Windows feature set | Owner-managed service and guest sharing | Physical/WAN commissioning still required |
| Linux desktop | TLS/P2P, JPEG/PNG, quality, input, clipboard, files, saved recovery, sound/voice and MKV | Approved guests and owner-enabled sign-in host; X11 input | OS keyring required for permanent host; virtual audio/daemon tested in an owned VM; physical desktop and Wayland control unverified/unavailable |
| macOS desktop | Same portable viewer source | Guest approval and owner-enabled LaunchAgent source | macOS 13+ for system audio; Keychain, screen/microphone and Accessibility permission required; Apple build/execution unverified |
| Android | Multi-session viewer, touch/keys, quality, clipboard, saved recovery, files, sound/voice and MKV | Not implemented | Device execution unverified; microphone requires runtime permission and per-call action; media stops when backgrounded |
| iOS/iPadOS | SwiftUI source: multi-session, touch, quality, clipboard, saved recovery, files and media | Not implemented | Apple build/device execution unverified; background execution is OS-controlled |
| Browser | Not implemented | Not implemented | No claim of browser compatibility |

The portable host captures only after guest approval or authenticated access from
an explicitly paired controller. Native input and clipboard access are created
only for an authorized control session. Guest invitations are
private credentials. Guest P2P requires copying the reply back to the sharing computer. A Windows
one-time pairing code instead saves an individual credential and reuses the
existing encrypted broker for a saved-entry connection. Saved sessions automatically
recover after transport failure with fresh authentication; Disconnect cancels them.
Linux/macOS permanent access runs as a separate graphical-sign-in process. See
`../docs/PORTABLE-ACCESS.md` and `../docs/PORTABLE-MEDIA.md` for scope and validation.

Portable source mode negotiates capability 2048 / tool 10, using exact PNG pixels.
JPEG remains compatible with older hosts. Windows-to-Windows XPRESS/H.264 paths
are retained. The portable decoder does not implement XPRESS or H.264; it requests
JPEG while switching modes, acknowledges obsolete in-flight frames, then uses PNG
when both endpoints support it. Source resolution/FPS are targets, not performance
guarantees. Detailed layout is in `../docs/PROTOCOL-V4.md`.

Portable display selection uses the existing Windows tools and display generations.
Changing displays releases held input; commands from an old displayed image are
rejected. A disconnected display leaves input paused until a valid display is selected.
Desktop repaint requests now follow frames through a separate coalescing worker;
Android uses Choreographer and iOS uses CADisplayLink. Mobile frame work allows at
most one pending copy/presentation. Status polling remains slow and independent.
These changes remove the fixed 16/33 ms presentation intervals; they do not prove
180 FPS or any specific device/network performance. The Windows .NET viewer has
a separate rendering implementation.

**No Apple device is available. macOS and iOS/iPadOS have not been compiled or
tested.** The owner accepts this explicit preparation-only status. A future Mac
can run `bash scripts/build-apple.sh --preflight` to check prerequisites without
changing system configuration, then `bash scripts/build-apple.sh all` (or `macos`
or `ios`). `BUILD-APPLE.command` runs the same build from Finder after executable
permission is granted. Logs and artifact checks are written to `verification/`.
The Mac build includes core/C ABI/native P2P tests; it does not silently capture
the owner's desktop or grant OS permissions. Device and permission-flow testing
remain separate from bundle/architecture/signature validation.

## Build

Dependencies are pinned in `Cargo.lock` and the native build scripts. Rust 1.98.1
was used on the Windows development machine. Keep the matching native library next
to `lume-desktop`: `datachannel.dll`, `libdatachannel.so`, or `libdatachannel.dylib`.
Do not copy the Windows DLL onto Linux or Apple systems.

### Linux

Install Rust/Cargo, Git, CMake, Ninja, a C/C++ compiler, Clang/libclang, pkg-config,
X11/XRandR/XCB/XTest/Xcursor/XInput/Xinerama, xkbcommon, Wayland, PipeWire and Mesa
development packages, including EGL/GBM headers and linker libraries. Minimal X11
systems also need the corresponding runtime libraries and X keyboard definitions.
PDF printing needs the local CUPS client (`lpstat`, `lp`) and a configured printer.
Permanent access additionally needs `secret-tool` (libsecret) and an unlocked
Secret Service desktop keyring. Audio uses `parec` and `pacat` with a running
PulseAudio-compatible server. No global sound-server configuration is changed.
Run `sh scripts/build-linux.sh` from the repository root. It fetches the pinned native
sources into the project's build directory and produces a native development
folder. The result targets the distribution/libc used for building: an Alpine
musl build is not a universal Linux binary or an AppImage.
On a native musl toolchain the scripts select dynamic CRT linking so the app can
load its P2P and display libraries. Those libraries cannot load from a fully static
musl executable.

Run `sh scripts/test-linux.sh` from the repository root for native UI,
capture/input/clipboard acceptance. It starts a private Xvfb display with local
authentication, uses software rendering and clears the inherited Wayland selection.
For a cross-compiled test executable, set `LUME_X11_TEST_EXECUTABLE` and
`LUME_DESKTOP_TEST_BINARY` to their absolute paths on the Linux test machine.
Never set the test's `LUME_OWNED_X11` flag against a person's real desktop. Native P2P tests require
`LUME_TEST_DATACHANNEL` set to the absolute packaged library path.

### Android

Use JDK 17 or later, Android SDK 36, NDK 28.2.13676358, Rust Android targets,
CMake/Ninja, and the pinned peer dependency checkout. From the repository root on Windows:

```powershell
rustup target add aarch64-linux-android x86_64-linux-android
.\scripts\build-android.ps1 -Sdk <sdk> -CMake <cmake.exe> -Ninja <ninja.exe> -PeerSource <peer-sources>
cd ports\android
.\gradlew.bat :app:assembleDebug :app:assembleDebugAndroidTest :app:lintDebug
```

The debug APK is a development build, not a store release. The two ABIs are ARM64
and x86-64. No advertising/analytics SDK is included. A visible foreground service
keeps user-started sessions running after the activity closes, with Disconnect all
in the notification. Explicit disconnect releases native work and frame memory.
Android can still stop applications under OS resource/power policies.

`scripts/test-android.ps1` installs both APKs only on an explicitly named local
emulator and connects to separate synthetic Windows test processes. Test approval
is not built into the production APK. Physical touchscreen, vendor power policies,
and public-Internet conditions require separate acceptance.

### Apple

On a Mac with Xcode, Rust, Git, CMake, Ninja and XcodeGen, run
`bash scripts/build-apple.sh` from the repository root. It builds the Rust and WebRTC libraries, assembles
device/simulator XCFrameworks, generates the Xcode project, builds an unsigned iOS
simulator app and packages an ad-hoc-signed universal macOS app. Device distribution
requires the owner's signing identity and provisioning. No signing credentials are
stored here. The script and Swift source have not been executed on an Apple host.

macOS requests screen-capture permission through the OS. Input also depends on
Accessibility permission. iOS does not claim a background host, screen control of
other apps, or permanent sockets while suspended. Clipboard writes stay local to
the device; private reply codes have an expiration when placed on the pasteboard.

## Tests and trust boundaries

0.11 portable viewers can send and receive whole folders when the host advertises
folder support. Each job creates a new collision-safe root, retains empty folders,
verifies each file, and reports completed items and bytes. Enumeration is streamed
with at most 64 nested directories and one bounded remote page per level. Linked
and special files are rejected; directory listings are not filesystem snapshots.
Cancellation or interruption keeps completed items and removes the active partial.
Retrying a folder creates a new root. Since 0.12, paired individual files can resume after interruption; mobile cache is removed when explicitly closing its session. See `../docs/PORTABLE-COLLABORATION.md` for resume, viewer annotations and PDF printing.

Android folder access uses the system document-tree picker. It stages the chosen
tree in private cache, then uploads it, and explicitly exports downloaded folders
to a newly created document folder. Staging requires additional free device space.
Completed export items remain after cancellation; private copies are removed at
disconnect. Providers and device execution still need real Android acceptance.
iOS source uses the system folder picker and security-scoped access, with bounded
staging and cancellation. No Apple build or device execution has been performed.

Portable MKV recording now accepts 0 for source refresh or a custom 1–1000 FPS
target. Desktop: Quality → Recording FPS, then Record. Android/iOS: Record opens
the FPS choice. One pending frame wakeup and the latest image prevent encoding
backlog from blocking the network. JPEG encoding may skip received images when
the machine is slow. The status counts skipped images. Actual FPS is not implied
by the setting, and portable MP4 recording remains unavailable.

Core tests exercise approval/denial, exact pixels, pinned TLS, malformed framing,
clipboard retry, native P2P, cancellation and joined teardown. The C ABI tests
exercise stale handles, bounded buffers and matching frame metadata. Windows/Rust
interoperability fixtures exercise real TLS and native P2P with synthetic frames.
These layers are useful evidence, but they are not physical-device/WAN acceptance.

Portable UI and media work is independent of the heartbeat and acknowledgement
loop. Queues are bounded. There is no duration quota, subscription or commercial-use
detector. Guest hosts accept one viewer at a time; viewer applications can connect
to multiple different hosts. The C ABI's 16 concurrent handle bound protects mobile
memory and is a resource limit, not a paid tier.

See `../docs/FEATURES.md` and `../docs/release-gates.json` for current evidence and
remaining release gates. The 0.11 AGY static review and correction re-review are recorded in
`../docs/AGY-11-REVIEW.md`; broader security acceptance and the historical Opus review remain open.

Linux build prerequisite: GBM development headers/link library (`libgbm-dev` on Ubuntu/Debian, `mesa-dev` on Alpine), in addition to the windowing/PipeWire dependencies. The build preflight checks `pkg-config --exists gbm`.
