#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")"
printf 'Lume development build: macOS and iOS simulator\n'
status=0
bash scripts/build-apple.sh all || status=$?
if [ "$status" -ne 0 ]; then
  printf '\nBuild stopped (exit %s). Fix the error above before retrying.\n' "$status"
fi
printf '\nPress Return to close. '
read -r _answer || true
exit "$status"
