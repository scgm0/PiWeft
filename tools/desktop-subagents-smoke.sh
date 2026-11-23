#!/usr/bin/env bash
# 子代理卡的视觉验收：运行中（当前工具）/ 失败可继续（原因预览）/ 等待回答 / 验证通过（预算与证据）四态行。
# --ui-smoke subagents 直接把卡片摆成这组快照，不建子会话，截图不受回合节奏影响。
# 每个主题三张：默认、悬停第一行、展开失败行并滚到底（任务、失败原因与会话文件）。
#
#   xvfb-run -a -s "-screen 0 1280x900x24" bash tools/desktop-subagents-smoke.sh
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
output="$(mktemp -d /tmp/piweft-desktop-subagents-XXXXXX)"
app_pid=""
cleanup() {
  if [[ -n "$app_pid" ]] && kill -0 "$app_pid" 2>/dev/null; then
    kill "$app_pid"
    wait "$app_pid" || true
  fi
}
trap cleanup EXIT

for theme in dark light; do
  # 先记下已经存在的窗口：同一台机器上还开着别的 PiWeft 时，按标题取第一条会截到错误的窗口。
  before="$(xdotool search --onlyvisible --name 'PiWeft Desktop' 2>/dev/null | sort || true)"
  work="$output/$theme/project"
  mkdir -p "$work" "$output/$theme/agent" "$output/$theme/home/.local/share" "$output/$theme/home/.config"
  jq --arg workspace "$work" '. + {chatWorkspace: $workspace}' \
    "$root/tests/PiWeft.Desktop.Tests/Fixtures/desktop-$theme.json" >"$output/$theme/agent/desktop.json"

  HOME="$output/$theme/home" PI_CODING_AGENT_DIR="$output/$theme/agent" dotnet "$root/src/PiWeft.Desktop/bin/Debug/net11.0/PiWeft.dll" \
    --cwd "$work" --ui-smoke subagents >"$output/$theme/stdout.log" 2>"$output/$theme/stderr.log" &
  app_pid=$!
  sleep 6
  # 认本次新开的那一张：窗口不写 _NET_WM_PID，按名字取第一条会截到别的实例。
  window=""
  for candidate in $(xdotool search --onlyvisible --name 'PiWeft Desktop' 2>/dev/null || true); do
    if ! grep -qx "$candidate" <<<"$before"; then
      window="$candidate"
      break
    fi
  done
  if [[ -z "$window" ]]; then
    printf 'subagents smoke 找不到本次打开的窗口\n' >&2
    exit 1
  fi
  eval "$(xdotool getwindowgeometry --shell "$window")"
  # 截图前先点一下空对话区：刚映射的窗口可能还没有真正的重绘。
  xdotool mousemove --window "$window" 240 140
  sleep 1
  xdotool click --window "$window" 1
  sleep 2
  import -window "$window" "$output/$theme.png"
  # 悬停第一行：确认 hover 底色与行内边距。
  xdotool mousemove --window "$window" $((WIDTH / 2)) $((HEIGHT * 30 / 100))
  sleep 1
  import -window "$window" "$output/$theme-hover.png"
  # 展开第三行（失败行），再在卡内滚到底：任务、失败原因、会话文件都入镜。
  xdotool mousemove --window "$window" $((WIDTH / 2)) $((HEIGHT * 48 / 100))
  sleep 0.5
  xdotool click --window "$window" 1
  sleep 1
  xdotool mousemove --window "$window" $((WIDTH / 2)) $((HEIGHT * 40 / 100))
  for _ in 1 2 3 4 5 6; do
    xdotool click --window "$window" 5
    sleep 0.15
  done
  sleep 1
  import -window "$window" "$output/$theme-expanded.png"
  wait "$app_pid" || true
  app_pid=""
  if rg -q '异常|失败|failed|Exception' "$output/$theme/stderr.log"; then
    printf 'subagents smoke 报错: %s\n' "$output/$theme/stderr.log"
    exit 1
  fi
done

printf 'Screenshots and logs: %s\n' "$output"
