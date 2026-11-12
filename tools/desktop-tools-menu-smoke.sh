#!/usr/bin/env bash
# 「添加工具」下拉的视觉验收：预置一份带增量条目、被扩展挡下的工具的 settings.json，
# 打开设置 → 配置管理 → Agent 设置并摆出下拉，深浅主题各截一张整屏图。
# 运行：xvfb-run -a -s '-screen 0 1920x1080x24' bash tools/desktop-tools-menu-smoke.sh
# 弹层是独立的原生窗口，截整屏（root）而不是应用窗口；HOME 与 PI_CODING_AGENT_DIR 都指向临时目录。
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
output="$(mktemp -d /tmp/piweft-desktop-tools-menu-XXXXXX)"
app_pid=""
cleanup() {
  if [[ -n "$app_pid" ]] && kill -0 "$app_pid" 2>/dev/null; then
    kill "$app_pid"
    wait "$app_pid" || true
  fi
}
trap cleanup EXIT

for theme in dark light; do
  agent="$output/$theme/agent"
  work="$output/$theme/project"
  home="$output/$theme/home"
  mkdir -p "$agent" "$work" "$home/.local/share" "$home/.config"
  cp "$root/tests/PiWeft.Desktop.Tests/Fixtures/desktop-$theme.json" "$agent/desktop.json"

  # -bash：项目层显式移除的条目标记；tool_search 被内置开关挡下：应压暗并提示原因。
  cat >"$agent/settings.json" <<'JSON'
{
  "defaultTools": ["read", "-bash", "edit", "write", "grep", "find", "ls", "todo", "ask_question", "tool_search", "codemode"],
  "extensions": ["-builtin:tool-search"]
}
JSON

  HOME="$home" PI_CODING_AGENT_DIR="$agent" dotnet "$root/src/PiWeft.Desktop/bin/Debug/net11.0/piweft-desktop.dll" \
    --cwd "$work" --ui-smoke config-tools >"$output/$theme/stdout.log" 2>"$output/$theme/stderr.log" &
  app_pid=$!
  # 会话打开 1.5 s 后派发演练，再等两帧排布才摆出下拉；5 s 时弹层已经稳定。
  sleep 5
  import -window root "$output/$theme.png"
  wait "$app_pid" || true
  app_pid=""
  if rg -q '异常|失败|failed|Exception' "$output/$theme/stderr.log"; then
    printf '工具下拉 smoke 报错: %s\n' "$output/$theme/stderr.log"
    exit 1
  fi
  if ! rg -q 'ui smoke 已打开工具下拉' "$output/$theme/stdout.log"; then
    printf '工具下拉没有被打开：%s\n' "$output/$theme/stdout.log"
    exit 1
  fi
done

printf 'Screenshots and logs: %s\n' "$output"
