#!/usr/bin/env bash
# 设置 → 日志的视觉验收：预置一份带各级别与多行栈的日志文件，深浅主题各截一张整屏图。
# 运行：xvfb-run -a -s '-screen 0 1600x1000x24' bash tools/desktop-logs-smoke.sh
# HOME 与 PI_CODING_AGENT_DIR 都指向临时目录，不碰真实的 agent 数据。
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
output="$(mktemp -d /tmp/piweft-desktop-logs-XXXXXX)"
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
  mkdir -p "$agent/logs" "$work" "$home/.local/share" "$home/.config"
  cp "$root/tests/PiWeft.Desktop.Tests/Fixtures/desktop-$theme.json" "$agent/desktop.json"

  # 每个级别各一行，末尾再来一条带续行的错误：检查颜色区分与单行收敛。
  cat >"$agent/logs/piweft-desktop-1-20261117-000.log" <<LOG
2026-11-17 05:18:52.385 [INF] PiWeft.Cli.AppLog | Logging started: application=desktop, process=1, runtime=11.0.0, level=Information
2026-11-17 05:18:52.387 [INF] PiWeft.Desktop.MainWindow | Startup directory: $work
2026-11-17 05:18:52.837 [INF] PiWeft.Desktop.MainWindow | Desktop ready: models=45, tools=12
2026-11-17 05:18:52.845 [WRN] PiWeft.Desktop.Diagnostics | Slow path: startup.first-paint 262.4 ms
2026-11-17 05:18:53.001 [ERR] PiWeft.Desktop.SessionTab | Open session: System.IO.IOException: disk full, stack=  at Foo()
   at Bar()
2026-11-17 05:18:53.002 [INF] PiWeft.Desktop.Mcp | MCP connections ready: servers=0, errors=0
2026-11-17 05:18:54.100 [DBG] PiWeft.Desktop.ChatViewModel | token usage refreshed
LOG

  HOME="$home" PI_CODING_AGENT_DIR="$agent" dotnet "$root/src/PiWeft.Desktop/bin/Debug/net11.0/PiWeft.dll" \
    --cwd "$work" --ui-smoke logs-settings >"$output/$theme/stdout.log" 2>"$output/$theme/stderr.log" &
  app_pid=$!
  # 会话打开 1.5 s 后派发演练，再等一次查询回来；6 s 时列表已经排布完。
  sleep 6
  import -window root "$output/$theme.png"
  wait "$app_pid" || true
  app_pid=""
done

echo "截图：$output"
