#!/usr/bin/env bash
# Run under xvfb-run. Fixtures never read or modify the user's agent directory.
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
output="$(mktemp -d /tmp/piweft-desktop-parity-XXXXXX)"
app_pid=""
cleanup() {
  if [[ -n "$app_pid" ]] && kill -0 "$app_pid" 2>/dev/null; then
    kill "$app_pid"
    wait "$app_pid" || true
  fi
}
trap cleanup EXIT
for theme in dark light; do
  mkdir -p "$output/$theme/agent" "$output/$theme/project"
  cp "$root/tests/PiWeft.Desktop.Tests/Fixtures/config-models.json" "$output/$theme/agent/models.json"
  cp "$root/tests/PiWeft.Desktop.Tests/Fixtures/desktop-$theme.json" "$output/$theme/agent/desktop.json"
  PI_CODING_AGENT_DIR="$output/$theme/agent" dotnet "$root/src/PiWeft.Desktop/bin/Debug/net10.0/piweft-desktop.dll" \
    --cwd "$output/$theme/project" --ui-smoke config >"$output/$theme/stdout.log" 2>"$output/$theme/stderr.log" &
  app_pid=$!
  sleep 4
  window="$(xdotool search --onlyvisible --name '^PiWeft Desktop — ' | head -1)"
  import -window "$window" "$output/$theme.png"
  wait "$app_pid"
  app_pid=""
  if rg -q '异常|失败|failed|Exception' "$output/$theme/stderr.log"; then
    printf 'UI smoke reported an error: %s\n' "$output/$theme/stderr.log"
    exit 1
  fi
done
printf 'Screenshots and logs: %s\n' "$output"
