#!/usr/bin/env bash
# 使用统计 / 应用用量界面验收：本地生成跨天、跨模型会话，深浅主题各截顶部与滚动后的明细。
# Run with: xvfb-run -a -s '-screen 0 1600x1000x24' bash tools/desktop-usage-stats-smoke.sh
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
output="$(mktemp -d /tmp/pi-usage-stats-smoke-XXXXXX)"
app="${PI_USAGE_STATS_SMOKE_APP:-$root/src/PiWeft.Desktop/bin/Debug/net11.0/piweft-desktop.dll}"
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
const compact = variant === 'compact';
const narrow = compact || variant === 'light';
fs.writeFileSync(path.join(agent, 'desktop.json'), JSON.stringify({
  theme: variant === 'light' ? 'light' : 'dark',
  windowWidth: compact ? 720 : narrow ? 1000 : 1280,
  windowHeight: compact ? 520 : narrow ? 720 : 820,
}));
fs.writeFileSync(path.join(agent, 'settings.json'), JSON.stringify({
  defaultProvider: 'local-openai',
  defaultModel: 'local-code',
}));

const now = new Date();
now.setUTCHours(2, 0, 0, 0);
const days = [0, 1, 2, 3, 4, 6, 7, 9, 12, 14, 18, 22, 27, 31, 36, 42];
const models = [
  ['local-openai', 'local-code', 1.0],
  ['local-anthropic', 'local-reasoner', 0.6],
  ['local-openai', 'local-chat', 0.4],
];
const project = '--' + cwd.replace(/^\//, '').replaceAll('/', '-') + '--';
const sessionDir = path.join(agent, 'sessions', project);
fs.mkdirSync(sessionDir, { recursive: true });

for (const [index, daysAgo] of days.entries()) {
  const start = new Date(now.getTime() - daysAgo * 86_400_000);
  const id = `019b76da-a800-7000-8000-${String(index).padStart(12, '0')}`;
  const lines = [{
    type: 'session', version: 3, id,
    timestamp: start.toISOString(), cwd,
  }];
  const used = [models[index % 3]];
  if (index % 4 === 0) used.push(models[(index + 1) % 3]);
  let time = start.getTime();
  for (let turn = 0; turn < 1 + (index % 3); turn++) {
    const userId = `u${String(index).padStart(2, '0')}${String(turn).padStart(2, '0')}`;
    time += 2 * 60_000;
    lines.push({
      type: 'message', id: userId, parentId: null,
      timestamp: new Date(time).toISOString(),
      message: { role: 'user', content: `Turn ${turn} of session ${index}` },
    });
    for (const [position, [provider, model, scale]] of used.entries()) {
      time += (4 + turn) * 60_000;
      const input = Math.round((900 + index * 130 + turn * 70) * scale);
      const output = Math.round((120 + index * 17 + turn * 11) * scale);
      const cacheRead = Math.round((4000 + index * 900 + turn * 250) * scale);
      const cacheWrite = Math.round((300 + index * 40) * scale);
      const cost = Number((input * 0.000002 + output * 0.000008 + cacheRead * 0.0000004 + cacheWrite * 0.000001).toFixed(4));
      lines.push({
        type: 'message', id: `a${String(index).padStart(2, '0')}${String(turn).padStart(2, '0')}${position}`,
        parentId: userId,
        timestamp: new Date(time).toISOString(),
        message: {
          role: 'assistant', provider, model,
          content: [{ type: 'text', text: 'Done.' }],
          usage: { input, output, cacheRead, cacheWrite, cost: { total: cost } },
          durationMs: (4 + turn) * 60_000,
          stopReason: 'stop',
        },
      });
    }
    time += 60_000;
  }
  const name = start.toISOString().replaceAll(':', '-').replace('.000', '-000');
  fs.writeFileSync(path.join(sessionDir, `${name}_${id}.jsonl`),
    lines.map(line => JSON.stringify(line)).join('\n') + '\n');
}
JS

  if [[ "$app" == *.dll ]]; then command=(dotnet "$app"); else command=("$app"); fi
  PI_CODING_AGENT_DIR="$directory/agent" "${command[@]}" --cwd "$directory/project" --ui-smoke usage-stats \
    >"$directory/stdout.log" 2>"$directory/stderr.log" &
  app_pid=$!
  window=""
  for _ in {1..120}; do
    window="$(xdotool search --onlyvisible --name '^PiWeft Desktop — ' 2>/dev/null | head -1 || true)"
    if [[ -n "$window" ]] && rg -q 'ui smoke 已打开 usage-stats' "$directory/stdout.log"; then break; fi
    kill -0 "$app_pid" 2>/dev/null || break
    sleep 0.25
  done
  if [[ -z "$window" ]]; then
    printf 'Usage stats window did not open; logs: %s\n' "$directory"
    exit 1
  fi

  sleep 3.5
  import -window "$window" "$directory/top.png"
  if [[ "$variant" == "compact" ]]; then pointer="400 380"; elif [[ "$variant" == "light" ]]; then pointer="520 540"; else pointer="760 620"; fi
  # shellcheck disable=SC2086
  xdotool mousemove --window "$window" $pointer
  for _ in {1..6}; do xdotool click 5; sleep 0.12; done
  sleep 0.8
  import -window "$window" "$directory/details.png"

  dimensions="$(identify -format '%wx%h' "$directory/top.png")"
  expected="1280x820"
  if [[ "$variant" == "light" ]]; then expected="1000x720"; fi
  if [[ "$variant" == "compact" ]]; then expected="720x520"; fi
  if [[ "$dimensions" != "$expected" ]]; then
    printf 'Screenshot was clipped (%s, expected %s); use a 1600x1000 Xvfb screen.\n' "$dimensions" "$expected"
    exit 1
  fi

  wait "$app_pid" || true
  app_pid=""
  if rg -q '异常|失败|failed|Exception' "$directory/stderr.log"; then
    printf 'UI smoke reported an error: %s\n' "$directory/stderr.log"
    exit 1
  fi
done
printf 'Screenshots and logs: %s\n' "$output"
