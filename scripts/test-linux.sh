#!/bin/sh
# Never point native input/capture tests at the user's display.
set -eu
project="$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)"
test "$(uname -s)" = Linux || { echo 'Run this test on Linux.' >&2; exit 1; }
for command in Xvfb xvfb-run xauth; do
  command -v "$command" >/dev/null || { printf 'Missing test tool: %s\n' "$command" >&2; exit 1; }
done
unset WAYLAND_DISPLAY
export XDG_SESSION_TYPE=x11
export LIBGL_ALWAYS_SOFTWARE=1
if [ -n "${LUME_X11_TEST_EXECUTABLE:-}" ]; then
  case "$LUME_X11_TEST_EXECUTABLE" in /*) ;; *) echo 'The prebuilt test executable must have an absolute path.' >&2; exit 1;; esac
  test -x "$LUME_X11_TEST_EXECUTABLE" || { echo 'The prebuilt test executable is missing or not executable.' >&2; exit 1; }
  exec xvfb-run --auto-servernum --server-args='-screen 0 1024x768x24 -nolisten tcp' \
    env LUME_OWNED_X11=1 "$LUME_X11_TEST_EXECUTABLE" --ignored --skip audio::tests --nocapture --test-threads=1
fi
command -v cargo >/dev/null || { echo 'Missing test tool: cargo' >&2; exit 1; }
case "$(rustc -vV | sed -n 's/^host: //p')" in
  *-linux-musl) export RUSTFLAGS="${RUSTFLAGS:+$RUSTFLAGS }-C target-feature=-crt-static -C link-self-contained=no" ;;
esac
exec xvfb-run --auto-servernum --server-args='-screen 0 1024x768x24 -nolisten tcp' \
  env LUME_OWNED_X11=1 cargo test --manifest-path "$project/ports/Cargo.toml" \
  -p lume-desktop --test x11_native --locked -j 2 -- --ignored --skip audio::tests --nocapture --test-threads=1
