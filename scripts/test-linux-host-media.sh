#!/bin/sh
# Owned laboratory only: creates a private keyring and virtual audio sink. Never use a physical desktop.
set -eu
test "${LUME_OWNED_LINUX_LAB:-}" = 1 || { echo 'An owned Linux laboratory is required.' >&2; exit 1; }
test "$(id -u)" != 0 || { echo 'Run as the unprivileged fixture owner.' >&2; exit 1; }
test -x "${LUME_TEST_HOST_BINARY:?}" && test -x "${LUME_TEST_DESKTOP_TESTS:?}"
if [ "${1:-}" != '--inside' ]; then
  for tool in dbus-run-session gnome-keyring-daemon pulseaudio pactl secret-tool; do command -v "$tool" >/dev/null || exit 1; done
  base="$(mktemp -d "${TMPDIR:-/tmp}/lume-host-test.XXXXXX")"
  export XDG_DATA_HOME="$base/data" XDG_CONFIG_HOME="$base/config" XDG_RUNTIME_DIR="$base/runtime"
  mkdir -m 700 "$XDG_DATA_HOME" "$XDG_CONFIG_HOME" "$XDG_RUNTIME_DIR"
  exec dbus-run-session -- sh "$0" --inside
fi
printf '%s' 'owned-fixture-keyring-only' | gnome-keyring-daemon --unlock --components=secrets >/dev/null
gnome-keyring-daemon --start --components=secrets >/dev/null
pulseaudio --daemonize=yes -n --exit-idle-time=-1 --load='module-native-protocol-unix' --load='module-null-sink sink_name=lume_test rate=48000 channels=2'
trap 'pulseaudio --kill >/dev/null 2>&1 || true' EXIT
pactl set-default-sink lume_test
pactl set-default-source lume_test.monitor
export PULSE_SINK=lume_test PULSE_SOURCE=lume_test.monitor LUME_TEST_AUDIO_NULL=1
"$LUME_TEST_DESKTOP_TESTS" protected_keyring_startup_daemon_and_disable --ignored --nocapture --test-threads=1
"$LUME_TEST_DESKTOP_TESTS" null_sink_capture_playback_and_cleanup --ignored --nocapture --test-threads=1
echo 'PASS Isolated Linux keyring, sign-in configuration, real daemon, duplicate exclusion, disable, virtual PCM playback/capture and child cleanup.'
