#!/bin/bash
# Build on a Mac with Xcode, Rust, CMake, Ninja and XcodeGen available.
set -euo pipefail
project="$(cd "$(dirname "$0")/.." && pwd)"
mode="${1:-all}"
if [ "$mode" = --preflight ]; then exec bash "$project/scripts/apple-preflight.sh" all; fi
bash "$project/scripts/apple-preflight.sh" "$mode"
source_root="${LUME_PEER_SOURCE_ROOT:-$project/build/peer-source}"
build_root="${LUME_APPLE_BUILD_ROOT:-$project/build/apple}"
report_root="$project/verification/apple-$(date -u +%Y%m%dT%H%M%SZ)-$$"
mkdir -p "$source_root" "$build_root" "$report_root"
exec > >(tee "$report_root/build.txt") 2>&1
trap 'printf "Apple build failed at line %s; no acceptance result was issued.\n" "$LINENO" >&2' ERR
fetch() {
  local name="$1" url="$2" tag="$3" commit="$4"
  if [ ! -e "$source_root/$name" ]; then git clone --depth 1 --branch "$tag" --recurse-submodules --shallow-submodules "$url" "$source_root/$name"; fi
  test "$(git -C "$source_root/$name" rev-parse HEAD)" = "$commit" || { echo 'Pinned dependency revision mismatch' >&2; exit 1; }
  test -z "$(git -C "$source_root/$name" status --porcelain --untracked-files=no)" || { echo 'Dependency has local changes; preserving it' >&2; exit 1; }
  if git -C "$source_root/$name" submodule status --recursive | grep -q '^[+-U]'; then echo 'Pinned submodule is unavailable' >&2; exit 1; fi
}
fetch libdatachannel https://github.com/paullouisageneau/libdatachannel.git v0.24.6 6b1e2e620f1e37f0eafeee702eaea0043cb305fd
fetch mbedtls https://github.com/Mbed-TLS/mbedtls.git mbedtls-3.6.7 068ff080b369adfac81509f9b57b2afabaf82dc5
if [ "$mode" != macos ]; then
rustup target add aarch64-apple-ios aarch64-apple-ios-sim x86_64-apple-ios
for target in aarch64-apple-ios aarch64-apple-ios-sim x86_64-apple-ios; do
  cargo build --manifest-path "$project/ports/Cargo.toml" -p lume-bridge --release --target "$target" --locked -j 2
done
mkdir -p "$build_root/simulator"
xcrun lipo -create "$project/ports/target/aarch64-apple-ios-sim/release/liblume_bridge.a" "$project/ports/target/x86_64-apple-ios/release/liblume_bridge.a" -output "$build_root/simulator/liblume_bridge.a"
for platform in iphoneos iphonesimulator; do
  archs=arm64; supported=iPhoneOS
  if [ "$platform" = iphonesimulator ]; then archs='arm64;x86_64'; supported=iPhoneSimulator; fi
  native="$build_root/$platform"
  cmake -S "$project/native/peer" -B "$native" -G Ninja -DCMAKE_SYSTEM_NAME=iOS "-DCMAKE_OSX_SYSROOT=$platform" "-DCMAKE_OSX_ARCHITECTURES=$archs" -DCMAKE_OSX_DEPLOYMENT_TARGET=16.0 -DCMAKE_BUILD_TYPE=Release -DCMAKE_POLICY_VERSION_MINIMUM=3.5 "-DLUME_PEER_SOURCE_ROOT=$source_root"
  cmake --build "$native" --target datachannel --parallel 2
  framework="$native/LumePeer.framework"
  mkdir -p "$framework/Headers" "$framework/Modules"
  cp "$native/libdatachannel/libdatachannel.dylib" "$framework/LumePeer"
  cp "$source_root/libdatachannel/include/rtc/rtc.h" "$framework/Headers/rtc.h"
  cp "$source_root/libdatachannel/include/rtc/version.h" "$framework/Headers/version.h"
  xcrun install_name_tool -id '@rpath/LumePeer.framework/LumePeer' "$framework/LumePeer"
  printf 'framework module LumePeer { umbrella header "rtc.h" export * }\n' > "$framework/Modules/module.modulemap"
  cat > "$framework/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?><!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict><key>CFBundleName</key><string>LumePeer</string><key>CFBundleExecutable</key><string>LumePeer</string><key>CFBundleIdentifier</key><string>com.lume.peer</string><key>CFBundlePackageType</key><string>FMWK</string><key>CFBundleVersion</key><string>12</string><key>CFBundleShortVersionString</key><string>0.12.0</string><key>MinimumOSVersion</key><string>16.0</string><key>CFBundleSupportedPlatforms</key><array><string>$supported</string></array></dict></plist>
EOF
done
staging="$(mktemp -d "$build_root/frameworks.XXXXXX")"
xcodebuild -create-xcframework -library "$project/ports/target/aarch64-apple-ios/release/liblume_bridge.a" -headers "$project/ports/bridge/include" -library "$build_root/simulator/liblume_bridge.a" -headers "$project/ports/bridge/include" -output "$staging/LumeCore.xcframework"
xcodebuild -create-xcframework -framework "$build_root/iphoneos/LumePeer.framework" -framework "$build_root/iphonesimulator/LumePeer.framework" -output "$staging/LumePeer.xcframework"
destination="$project/ports/ios/Frameworks"
if [ -e "$destination" ]; then mv "$destination" "$build_root/Frameworks.previous.$(date +%Y%m%d-%H%M%S)"; fi
mv "$staging" "$destination"
(cd "$project/ports/ios" && xcodegen generate)
xcodebuild -project "$project/ports/ios/Lume.xcodeproj" -scheme Lume -configuration Debug -sdk iphonesimulator -destination 'generic/platform=iOS Simulator' -derivedDataPath "$build_root/DerivedData" CODE_SIGNING_ALLOWED=NO ONLY_ACTIVE_ARCH=NO 'ARCHS=arm64 x86_64' ENABLE_DEBUG_DYLIB=NO build
fi
if [ "$mode" != ios ]; then
rustup target add aarch64-apple-darwin x86_64-apple-darwin
for target in aarch64-apple-darwin x86_64-apple-darwin; do
  MACOSX_DEPLOYMENT_TARGET=13.0 cargo build --manifest-path "$project/ports/Cargo.toml" -p lume-desktop --release --target "$target" --locked -j 2
done
native="$build_root/macos"
cmake -S "$project/native/peer" -B "$native" -G Ninja '-DCMAKE_OSX_ARCHITECTURES=arm64;x86_64' -DCMAKE_OSX_DEPLOYMENT_TARGET=13.0 -DCMAKE_BUILD_TYPE=Release -DCMAKE_POLICY_VERSION_MINIMUM=3.5 "-DLUME_PEER_SOURCE_ROOT=$source_root"
cmake --build "$native" --target datachannel --parallel 2
LUME_TEST_DATACHANNEL="$native/libdatachannel/libdatachannel.dylib" cargo test --manifest-path "$project/ports/Cargo.toml" -p lume-core -p lume-bridge --locked -j 2 -- --include-ignored | tee "$report_root/core-tests.txt"
app="$build_root/Lume.app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
mkdir -p "$app/Contents/Resources/licenses"
cp -R "$project/third-party/." "$app/Contents/Resources/licenses/"
xcrun lipo -create "$project/ports/target/aarch64-apple-darwin/release/lume-desktop" "$project/ports/target/x86_64-apple-darwin/release/lume-desktop" -output "$app/Contents/MacOS/Lume"
cp "$native/libdatachannel/libdatachannel.dylib" "$app/Contents/MacOS/libdatachannel.dylib"
for arch in arm64 x86_64; do
  xcrun swiftc -swift-version 5 -O -target "$arch-apple-macosx13.0" -framework AppKit -framework PDFKit "$project/ports/macos/PrintHelper.swift" -o "$build_root/lume-print-$arch"
  xcrun swiftc -swift-version 5 -O -target "$arch-apple-macosx13.0" -framework AVFoundation -framework ScreenCaptureKit -framework CoreMedia "$project/ports/macos/AudioHelper.swift" -o "$build_root/lume-audio-$arch"
done
xcrun lipo -create "$build_root/lume-print-arm64" "$build_root/lume-print-x86_64" -output "$app/Contents/MacOS/lume-print"
xcrun lipo -create "$build_root/lume-audio-arm64" "$build_root/lume-audio-x86_64" -output "$app/Contents/MacOS/lume-audio"
cat > "$app/Contents/Info.plist" <<'EOF'
<?xml version="1.0" encoding="UTF-8"?><!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd"><plist version="1.0"><dict><key>CFBundleName</key><string>Lume</string><key>CFBundleExecutable</key><string>Lume</string><key>CFBundleIdentifier</key><string>com.lume.remote</string><key>CFBundlePackageType</key><string>APPL</string><key>CFBundleShortVersionString</key><string>0.12.0</string><key>CFBundleVersion</key><string>12</string><key>LSMinimumSystemVersion</key><string>13.0</string><key>NSMicrophoneUsageDescription</key><string>Share your microphone only after you approve a voice call.</string><key>NSAudioCaptureUsageDescription</key><string>Share system audio with your authorized remote session when sound is enabled.</string><key>NSHighResolutionCapable</key><true/><key>NSLocalNetworkUsageDescription</key><string>Connect to computers you own or have permission to use.</string></dict></plist>
EOF
codesign --force --deep --sign - "$app"
fi
python3 "$project/scripts/verify-apple.py" "$build_root" --mode "$mode" --output "$report_root/artifacts.json"
printf 'PASS Apple development build (%s). Physical devices, signing/notarization and native desktop acceptance require separate checks.\n' "$mode"
printf 'Verification report: %s\n' "$report_root"
