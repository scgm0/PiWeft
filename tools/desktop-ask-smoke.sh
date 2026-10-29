#!/usr/bin/env bash
# ask_question 内联卡片的视觉验收：在会话底部展开一张批量问询卡
# （select / multi_select / editor，带题卡标签、进度与「下一题」），深浅主题各截一张图。
# 不需要真实代理：--ui-smoke ask 直接调用会话标签的问询卡渲染。
#
# 截图前先在空对话区移一下鼠标并点一下：刚映射的窗口可能还没有真正的重绘，
# 直接 import 会拿到一张纯黑图（深色主题尤其明显）。落点在卡片上方，不会改动作答状态。
#
# 建议给 Xvfb 一个和窗口一致的屏（否则窗口被裁到 640×480）：
#   xvfb-run -a -s "-screen 0 1280x820x24" bash tools/desktop-ask-smoke.sh
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
output="$(mktemp -d /tmp/piweft-desktop-ask-XXXXXX)"
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
    --cwd "$work" --ui-smoke ask >"$output/$theme/stdout.log" 2>"$output/$theme/stderr.log" &
  app_pid=$!
  sleep 5
  window="$(xdotool search --onlyvisible --name '^PiWeft Desktop — ' | head -1)"
  xdotool mousemove --window "$window" 240 150
  sleep 1
  xdotool click --window "$window" 1
  sleep 1
  import -window "$window" "$output/$theme.png"
  wait "$app_pid" || true
  app_pid=""
  if rg -q '异常|失败|failed|Exception' "$output/$theme/stderr.log"; then
    printf 'ask 卡片 smoke 报错: %s\n' "$output/$theme/stderr.log"
    exit 1
  fi
done

printf 'Screenshots and logs: %s\n' "$output"
