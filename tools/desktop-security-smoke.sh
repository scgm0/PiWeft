#!/usr/bin/env bash
# 安全门设置面板的视觉验收：预置一份非默认的安全配置，打开设置 → 安全门并按深浅主题各截一张图。
# HOME 与 PI_CODING_AGENT_DIR 都指向临时目录，不读也不改用户的真实设置与凭据。
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
output="$(mktemp -d /tmp/piweft-desktop-security-XXXXXX)"
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

  # 预置：默认档 strict + 一条会话级覆盖 + 一个自定义档，让面板一打开就有可看的内容
  # （包括「跟随默认 / 本会话覆盖」的差异与自定义档的删除入口）。
  cat >"$agent/settings.json" <<'JSON'
{
  "theme": "dark",
  "security": {
    "level": "strict",
    "levels": [
      { "id": "release", "name": "发布分支", "description": "只读为主，禁地无条件拒绝", "builtin": false, "protectSensitivePaths": true, "pathPolicy": "workspace", "denyDirs": ["secrets"], "toolActions": { "bash": "ask", "edit": "deny" } }
    ],
    "sessionLevels": {
      "/tmp/demo/2026-01-01T00-00-00-000Z_deadbeef.jsonl": "release"
    }
  }
}
JSON

  HOME="$home" PI_CODING_AGENT_DIR="$agent" dotnet "$root/src/PiWeft.Desktop/bin/Debug/net10.0/piweft-desktop.dll" \
    --cwd "$work" --ui-smoke security >"$output/$theme/stdout.log" 2>"$output/$theme/stderr.log" &
  app_pid=$!
  # --ui-smoke 目标在会话打开后再等 1.5 s 才派发，界面驻留 7 s：4 s 时窗口已经切到安全门分区。
  sleep 4
  window="$(xdotool search --onlyvisible --name '^PiWeft Desktop — ' | head -1)"
  import -window "$window" "$output/$theme.png"
  wait "$app_pid" || true
  app_pid=""
  if rg -q '异常|失败|failed|Exception' "$output/$theme/stderr.log"; then
    printf '安全门面板 smoke 报错: %s\n' "$output/$theme/stderr.log"
    exit 1
  fi
  # 断言分区真的被选中：否则截图会静默退化成概览页，看了也白看。
  if ! rg -q 'ui smoke 已打开 security' "$output/$theme/stdout.log"; then
    printf '安全门分区没有被打开：%s\n' "$output/$theme/stdout.log"
    exit 1
  fi
done

printf 'Screenshots and logs: %s\n' "$output"
