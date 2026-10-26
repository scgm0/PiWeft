#!/usr/bin/env bash
# 导入外部会话弹窗的视觉验收：造一个带 Claude Code 与 Codex CLI 夹具会话的家目录，
# 打开弹窗截图。HOME 与 PI_CODING_AGENT_DIR 都被指向临时目录，不读也不改用户的真实数据。
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
output="$(mktemp -d /tmp/piweft-desktop-import-XXXXXX)"
app_pid=""
cleanup() {
  if [[ -n "$app_pid" ]] && kill -0 "$app_pid" 2>/dev/null; then
    kill "$app_pid"
    wait "$app_pid" || true
  fi
}
trap cleanup EXIT

home="$output/home"
work="$output/project"
mkdir -p "$home/.local/share" "$home/.config" "$home/.claude/projects/-tmp-proj" "$home/.codex/sessions/2026/10/21" "$work" "$output/agent"
cwd_json="$(printf '%s' "$work" | sed 's/\\/\\\\/g; s/"/\\"/g')"

cat >"$home/.claude/projects/-tmp-proj/s1.jsonl" <<JSON
{"type":"user","sessionId":"s1","cwd":"$cwd_json","timestamp":"2026-10-20T10:00:00.000Z","message":{"role":"user","content":[{"type":"text","text":"构建为什么失败"}]}}
{"type":"assistant","sessionId":"s1","cwd":"$cwd_json","timestamp":"2026-10-20T10:00:05.000Z","message":{"role":"assistant","content":[{"type":"text","text":"缺分号"}]}}
JSON

cat >"$home/.codex/sessions/2026/10/21/rollout-1.jsonl" <<JSON
{"timestamp":"2026-10-21T09:00:00.000Z","type":"session_meta","payload":{"id":"c1","cwd":"$cwd_json","model":"gpt-5"}}
{"timestamp":"2026-10-21T09:00:01.000Z","type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"把 README 标题改掉"}]}}
JSON

jq --arg workspace "$work" '. + {chatWorkspace: $workspace}' \
  "$root/tests/PiWeft.Desktop.Tests/Fixtures/desktop-dark.json" >"$output/agent/desktop.json"

HOME="$home" PI_CODING_AGENT_DIR="$output/agent" dotnet "$root/src/PiWeft.Desktop/bin/Debug/net10.0/piweft-desktop.dll" \
  --cwd "$work" --ui-smoke import >"$output/stdout.log" 2>"$output/stderr.log" &
app_pid=$!
sleep 5
window="$(xdotool search --onlyvisible --name '^PiWeft Desktop — ' | head -1)"
import -window "$window" "$output/import.png"
wait "$app_pid" || true
app_pid=""
if rg -q '异常|失败|failed|Exception' "$output/stderr.log"; then
  printf '导入弹窗 smoke 报错: %s\n' "$output/stderr.log"
  exit 1
fi
printf 'Screenshot and logs: %s\n' "$output"
