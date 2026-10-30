#!/usr/bin/env bash
# 首轮等待的视觉验收：状态条常驻（图标 + 呼吸点 + 预热短语 + 本轮已 N 秒 + 功能提示）
# 与时间线尾部的等待占位行。--ui-smoke waiting 直接把视图摆成「已发出、首 token 未到」，
# 不需要真实代理（真实等待时长不可控，截图对不上）。
#
# 与 ask smoke 一样，截图前先点一下空对话区：刚映射的窗口可能还没有真正的重绘。
#
#   xvfb-run -a -s "-screen 0 1280x820x24" bash tools/desktop-waiting-smoke.sh
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
output="$(mktemp -d /tmp/piweft-desktop-waiting-XXXXXX)"
app_pid=""
cleanup() {
  if [[ -n "$app_pid" ]] && kill -0 "$app_pid" 2>/dev/null; then
    kill "$app_pid"
    wait "$app_pid" || true
  fi
}
trap cleanup EXIT

for theme in dark light; do
  work="$output/$theme/project"
  mkdir -p "$work" "$output/$theme/agent" "$output/$theme/home/.local/share" "$output/$theme/home/.config"
  jq --arg workspace "$work" '. + {chatWorkspace: $workspace}' \
    "$root/tests/PiWeft.Desktop.Tests/Fixtures/desktop-$theme.json" >"$output/$theme/agent/desktop.json"

  HOME="$output/$theme/home" PI_CODING_AGENT_DIR="$output/$theme/agent" dotnet "$root/src/PiWeft.Desktop/bin/Debug/net10.0/piweft-desktop.dll" \
    --cwd "$work" --ui-smoke waiting >"$output/$theme/stdout.log" 2>"$output/$theme/stderr.log" &
  app_pid=$!
  # 心跳 500ms 一跳：「本轮已 N 秒」要到 2 秒后才出现，睡 5 秒能截到带时长的样子。
  sleep 5
  window="$(xdotool search --onlyvisible --name '^PiWeft Desktop — ' | head -1)"
  xdotool mousemove --window "$window" 240 140
  sleep 1
  xdotool click --window "$window" 1
  sleep 1
  import -window "$window" "$output/$theme.png"
  wait "$app_pid" || true
  app_pid=""
  if rg -q '异常|失败|failed|Exception' "$output/$theme/stderr.log"; then
    printf 'waiting smoke 报错: %s\n' "$output/$theme/stderr.log"
    exit 1
  fi
done

printf 'Screenshots and logs: %s\n' "$output"
