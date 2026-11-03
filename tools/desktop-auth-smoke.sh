#!/usr/bin/env bash
# 认证页验收：用假凭据展示 OAuth 登录状态卡（已登录/已过期）、auth.json 条目与 API Key 表单。
# Run with: xvfb-run -a -s '-screen 0 1600x1000x24' bash tools/desktop-auth-smoke.sh
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
output="$(mktemp -d /tmp/pi-auth-smoke-XXXXXX)"
app="${PI_AUTH_SMOKE_APP:-$root/src/PiWeft.Desktop/bin/Debug/net10.0/piweft-desktop.dll}"
app_pid=""
cleanup() {
  if [[ -n "$app_pid" ]] && kill -0 "$app_pid" 2>/dev/null; then kill "$app_pid"; wait "$app_pid" || true; fi
}
trap cleanup EXIT

for theme in dark light; do
  directory="$output/$theme"
  mkdir -p "$directory/agent" "$directory/project"
  cp "$root/tests/PiWeft.Desktop.Tests/Fixtures/config-models.json" "$directory/agent/models.json"
  cp "$root/tests/PiWeft.Desktop.Tests/Fixtures/desktop-$theme.json" "$directory/agent/desktop.json"
  printf '{"defaultProvider":"local-openai","defaultModel":"local-code"}' >"$directory/agent/settings.json"
  cat >"$directory/agent/auth.json" <<'JSON'
{
  "anthropic": {
    "type": "oauth",
    "access": "sk-ant-oat-demo-access",
    "refresh": "demo-refresh",
    "expires": 1924992000,
    "accountId": "demo@anthropic.example"
  },
  "openai-codex": {
    "type": "oauth",
    "access": "demo.access.token",
    "refresh": "demo-refresh",
    "expires": 1924992000
  },
  "github-copilot": {
    "type": "oauth",
    "access": "gho_demo",
    "refresh": "ghr_demo",
    "expires": 1924992000,
    "enterpriseUrl": "github.com"
  },
  "xai": {
    "type": "oauth",
    "access": "xai-demo",
    "refresh": "demo-refresh",
    "expires": 1700000000
  },
  "deepseek": {
    "type": "api_key",
    "key": "sk-demo-key"
  }
}
JSON
  if [[ "$app" == *.dll ]]; then command=(dotnet "$app"); else command=("$app"); fi
  PI_CODING_AGENT_DIR="$directory/agent" "${command[@]}" --cwd "$directory/project" --ui-smoke auth \
    >"$directory/stdout.log" 2>"$directory/stderr.log" &
  app_pid=$!
  window=""
  for _ in {1..100}; do
    window="$(xdotool search --onlyvisible --name '^PiWeft Desktop — ' 2>/dev/null | head -1 || true)"
    if [[ -n "$window" ]] && rg -q 'ui smoke 已打开 auth' "$directory/stdout.log"; then break; fi
    kill -0 "$app_pid" 2>/dev/null || break
    sleep 0.2
  done
  if [[ -z "$window" ]]; then
    printf 'Auth window did not open; logs: %s\n' "$directory"
    exit 1
  fi
  sleep 2.5
  import -window "$window" "$directory/auth.png"
  dimensions="$(identify -format '%wx%h' "$directory/auth.png")"
  expected="1280x820"
  if [[ "$theme" == "light" ]]; then expected="1000x720"; fi
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
