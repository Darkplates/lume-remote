#!/bin/bash
# Read-only prerequisite validation. Never installs packages or changes Xcode settings.
set -euo pipefail
mode="${1:-all}"
case "$mode" in all|macos|ios) ;; *) echo 'Usage: apple-preflight.sh [all|macos|ios]' >&2; exit 2;; esac
if [ "$(uname -s)" != Darwin ]; then
  echo 'Apple compilation requires a Mac with the full Xcode application. This host is not macOS.' >&2
  exit 2
fi
missing=0
for tool in xcrun xcodebuild swift rustup cargo cmake ninja xcodegen git python3 codesign; do
  if ! command -v "$tool" >/dev/null; then printf 'Missing prerequisite: %s\n' "$tool" >&2; missing=1; fi
done
test "$missing" = 0 || { echo 'Install the listed tools, then run BUILD-APPLE.command again.' >&2; exit 2; }
xcodebuild -version
xcrun --sdk macosx --show-sdk-path
if [ "$mode" != macos ]; then
  xcrun --sdk iphoneos --show-sdk-path
  xcrun --sdk iphonesimulator --show-sdk-path
fi
xcrun --find clang
xcrun --find swiftc
rustup show active-toolchain
cargo --version
swift --version
cmake --version
ninja --version
xcodegen --version
printf 'PASS Read-only Apple prerequisites (%s). Compilation and device acceptance remain separate.\n' "$mode"
