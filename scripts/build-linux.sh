#!/bin/sh
# Native build; run on the Linux distribution that will run the resulting app.
set -eu
project="$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)"
source_root="${LUME_PEER_SOURCE_ROOT:-$project/build/peer-source}"
build_root="${LUME_LINUX_BUILD_ROOT:-$project/build/linux}"
profile="${LUME_BUILD_PROFILE:-release}"
case "$profile" in debug|release) ;; *) echo 'LUME_BUILD_PROFILE must be debug or release' >&2; exit 1;; esac
for command in cargo rustc git cmake ninja pkg-config; do
  command -v "$command" >/dev/null || { printf 'Missing build tool: %s\n' "$command" >&2; exit 1; }
done
# libdatachannel and the native windowing backends use dlopen. A fully static
# musl executable cannot load those libraries, even when they are present.
case "$(rustc -vV | sed -n 's/^host: //p')" in
  *-linux-musl) export RUSTFLAGS="${RUSTFLAGS:+$RUSTFLAGS }-C target-feature=-crt-static -C link-self-contained=no" ;;
esac
mkdir -p "$source_root" "$build_root"
fetch() {
  name="$1"; url="$2"; tag="$3"; commit="$4"
  if [ ! -e "$source_root/$name" ]; then git clone --depth 1 --branch "$tag" --recurse-submodules --shallow-submodules "$url" "$source_root/$name"; fi
  test "$(git -C "$source_root/$name" rev-parse HEAD)" = "$commit" || { echo 'Pinned dependency mismatch' >&2; exit 1; }
  test -z "$(git -C "$source_root/$name" status --porcelain --untracked-files=no)" || { echo 'Dependency has local changes; preserving it' >&2; exit 1; }
  if git -C "$source_root/$name" submodule status --recursive | grep -q '^[+-U]'; then echo 'Pinned submodule unavailable' >&2; exit 1; fi
}
fetch libdatachannel https://github.com/paullouisageneau/libdatachannel.git v0.24.6 6b1e2e620f1e37f0eafeee702eaea0043cb305fd
fetch mbedtls https://github.com/Mbed-TLS/mbedtls.git mbedtls-3.6.7 068ff080b369adfac81509f9b57b2afabaf82dc5
cmake -S "$project/native/peer" -B "$build_root/peer" -G Ninja -DCMAKE_BUILD_TYPE=Release -DCMAKE_POLICY_VERSION_MINIMUM=3.5 "-DLUME_PEER_SOURCE_ROOT=$source_root"
cmake --build "$build_root/peer" --target datachannel --parallel 2
export LUME_TEST_DATACHANNEL="$build_root/peer/libdatachannel/libdatachannel.so"
cargo test --manifest-path "$project/ports/Cargo.toml" -p lume-core -p lume-bridge --locked -j 2 -- --include-ignored
if [ "$profile" = release ]; then
  cargo build --manifest-path "$project/ports/Cargo.toml" -p lume-desktop --locked -j 2 --release
else
  cargo build --manifest-path "$project/ports/Cargo.toml" -p lume-desktop --locked -j 2
fi
package="$build_root/Lume-linux-development"
mkdir -p "$package"
cp "$project/ports/target/$profile/lume-desktop" "$package/lume-desktop"
cp "$LUME_TEST_DATACHANNEL" "$package/libdatachannel.so"
cp "$project/ports/README.md" "$package/README.md"
cp "$project/LICENSE.txt" "$project/THIRD-PARTY-NOTICES.txt" "$package/"
cp -R "$project/third-party" "$package/"
printf 'Built: %s\nNative desktop, display permissions and WAN acceptance remain separate checks.\n' "$package"
