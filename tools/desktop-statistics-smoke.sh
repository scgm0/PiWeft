#!/usr/bin/env bash
# Run with: xvfb-run -a -s '-screen 0 1600x1000x24' bash tools/desktop-statistics-smoke.sh
# All sessions/configuration are isolated from the user's agent directory.
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
output="$(mktemp -d /tmp/pi-statistics-smoke-XXXXXX)"
app="${PI_STATISTICS_SMOKE_APP:-$root/src/PiWeft.Desktop/bin/Debug/net10.0/piweft-desktop.dll}"
app_pid=""
cleanup() {
  if [[ -n "$app_pid" ]] && kill -0 "$app_pid" 2>/dev/null; then
    kill "$app_pid"
    wait "$app_pid" || true
  fi
}
trap cleanup EXIT
for variant in dark light compact; do
  directory="$output/$variant"
  mkdir -p "$directory/agent" "$directory/project"
  cp "$root/tests/PiWeft.Desktop.Tests/Fixtures/config-models.json" "$directory/agent/models.json"
  node - "$directory" "$variant" <<'JS'
const fs = require('node:fs');
const path = require('node:path');
const [directory, variant] = process.argv.slice(2);
const cwd = path.join(directory, 'project');
const agent = path.join(directory, 'agent');
fs.writeFileSync(path.join(agent, 'desktop.json'), JSON.stringify({ theme: variant === 'light' ? 'light' : 'dark', windowWidth: variant === 'compact' ? 720 : 1280, windowHeight: variant === 'compact' ? 520 : 820 }));
fs.writeFileSync(path.join(agent, 'settings.json'), JSON.stringify({ defaultProvider: 'local-openai', defaultModel: 'local-code' }));
const timestamp = '2026-01-01T00:00:00.000Z';
const id = '019b76da-a800-7000-8000-000000000001';
const sessionDir = path.join(agent, 'sessions', '--' + cwd.replace(/^\//, '').replaceAll('/', '-') + '--');
fs.mkdirSync(sessionDir, { recursive: true });
const entries = [
  { type: 'session', version: 3, id, timestamp, cwd },
  { type: 'message', id: 'user0001', parentId: null, timestamp, message: { role: 'user', content: 'Inspect the session statistics.', timestamp: 1767225600000 } },
  { type: 'message', id: 'reply001', parentId: 'user0001', timestamp, message: { role: 'assistant', content: [{ type: 'text', text: 'Session statistics are ready.\n\n- Context and cumulative usage\n- Latest and average cache hit rate\n- Reply timing and throughput' }], api: 'openai-completions', provider: 'local-openai', model: 'local-code', timestamp: 1767225600000, stopReason: 'stop', durationMs: 8800, firstTokenMs: 2800, streamingDurationMs: 6000, usage: { input: 2000, output: 270, cacheRead: 16000, cacheWrite: 2000, totalTokens: 20270, cost: { total: 0.123 } } } },
];
fs.writeFileSync(path.join(sessionDir, '2026-01-01T00-00-00-000Z_' + id + '.jsonl'), entries.map(entry => JSON.stringify(entry)).join('\n') + '\n');
JS
  if [[ "$app" == *.dll ]]; then
    command=(dotnet "$app")
  else
    command=("$app")
  fi
  PI_CODING_AGENT_DIR="$directory/agent" "${command[@]}" --cwd "$directory/project" --ui-smoke statistics >"$directory/stdout.log" 2>"$directory/stderr.log" &
  app_pid=$!
  window=""
  for attempt in {1..100}; do
    window="$(xdotool search --onlyvisible --name '^PiWeft Desktop — ' 2>/dev/null | head -1 || true)"
    if [[ -n "$window" ]] && rg -q 'ui smoke 已打开 statistics' "$directory/stdout.log"; then break; fi
    if ! kill -0 "$app_pid" 2>/dev/null; then break; fi
    sleep 0.2
  done
  if [[ -z "$window" ]] || ! rg -q 'ui smoke 已打开 statistics' "$directory/stdout.log"; then
    printf 'Statistics window did not open; logs: %s\n' "$directory"
    exit 1
  fi
  xdotool getwindowgeometry --shell "$window" >"$directory/geometry.log"
  xdotool search --onlyvisible --name '^PiWeft Desktop — ' >"$directory/window-ids.log"
  sleep 0.5
  import -window "$window" "$output/$variant.png"
  dimensions="$(identify -format '%wx%h' "$output/$variant.png")"
  expected="1280x820"
  if [[ "$variant" == compact ]]; then expected="720x520"; fi
  if [[ "$dimensions" != "$expected" ]]; then
    printf 'Screenshot was clipped (%s, expected %s); use a 1600x1000 Xvfb screen.\n' "$dimensions" "$expected"
    exit 1
  fi
  wait "$app_pid"
  app_pid=""
  if rg -q '异常|失败|failed|Exception' "$directory/stderr.log"; then
    printf 'UI smoke reported an error: %s\n' "$directory/stderr.log"
    exit 1
  fi
done
printf 'Screenshots and logs: %s\n' "$output"
