#!/usr/bin/env bash
# 订阅登录流程验收：本地假 Codex 授权服务 + --ui-smoke auth-login，
# 覆盖设备码展示、令牌交换、auth.json 写入与登录成功态。
# Run with: xvfb-run -a -s '-screen 0 1600x1000x24' bash tools/desktop-auth-login-smoke.sh
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
output="$(mktemp -d /tmp/pi-auth-login-smoke-XXXXXX)"
app="${PI_AUTH_SMOKE_APP:-$root/src/PiWeft.Desktop/bin/Debug/net10.0/piweft-desktop.dll}"
app_pid=""
server_pid=""
cleanup() {
  if [[ -n "$app_pid" ]] && kill -0 "$app_pid" 2>/dev/null; then kill "$app_pid"; wait "$app_pid" || true; fi
  if [[ -n "$server_pid" ]] && kill -0 "$server_pid" 2>/dev/null; then kill "$server_pid"; wait "$server_pid" || true; fi
}
trap cleanup EXIT

port_file="$output/port"
python3 - "$port_file" <<'PY' &
import http.server, json, sys
class Handler(http.server.BaseHTTPRequestHandler):
    def do_POST(self):
        length = int(self.headers.get("Content-Length", "0"))
        if length:
            self.rfile.read(length)
        if self.path.endswith("/api/accounts/deviceauth/usercode"):
            payload = {"device_auth_id": "demo-device", "user_code": "DEMO-1234", "interval": 1}
        elif self.path.endswith("/api/accounts/deviceauth/token"):
            payload = {"authorization_code": "demo-code", "code_verifier": "demo-verifier"}
        elif self.path.endswith("/oauth/token"):
            payload = {"access_token": "demo.access.token", "refresh_token": "demo-refresh", "expires_in": 3600}
        else:
            self.send_response(404); self.end_headers(); return
        body = json.dumps(payload).encode()
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)
    def log_message(self, *args):
        pass
server = http.server.HTTPServer(("127.0.0.1", 0), Handler)
with open(sys.argv[1], "w") as handle:
    handle.write(str(server.server_port))
server.serve_forever()
PY
server_pid=$!
for _ in {1..100}; do [[ -s "$port_file" ]] && break; sleep 0.05; done
port="$(cat "$port_file")"

for theme in dark light; do
  directory="$output/$theme"
  mkdir -p "$directory/agent" "$directory/project"
  cp "$root/tests/PiWeft.Desktop.Tests/Fixtures/config-models.json" "$directory/agent/models.json"
  cp "$root/tests/PiWeft.Desktop.Tests/Fixtures/desktop-$theme.json" "$directory/agent/desktop.json"
  printf '{"defaultProvider":"local-openai","defaultModel":"local-code"}' >"$directory/agent/settings.json"
  if [[ "$app" == *.dll ]]; then command=(dotnet "$app"); else command=("$app"); fi
  PI_CODING_AGENT_DIR="$directory/agent" PI_CODEX_AUTH_BASE_URL="http://127.0.0.1:$port" \
    "${command[@]}" --cwd "$directory/project" --ui-smoke auth-login \
    >"$directory/stdout.log" 2>"$directory/stderr.log" &
  app_pid=$!
  window=""
  for _ in {1..100}; do
    window="$(xdotool search --onlyvisible --name '^PiWeft Desktop — ' 2>/dev/null | head -1 || true)"
    if [[ -n "$window" ]] && rg -q 'ui smoke 已打开 auth-login' "$directory/stdout.log"; then break; fi
    kill -0 "$app_pid" 2>/dev/null || break
    sleep 0.2
  done
  if [[ -z "$window" ]]; then
    printf 'Auth login window did not open; logs: %s\n' "$directory"
    exit 1
  fi
  sleep 6
  import -window "$window" "$directory/auth-login.png"
  dimensions="$(identify -format '%wx%h' "$directory/auth-login.png")"
  expected="1280x820"
  if [[ "$theme" == "light" ]]; then expected="1000x720"; fi
  if [[ "$dimensions" != "$expected" ]]; then
    printf 'Screenshot was clipped (%s, expected %s); use a 1600x1000 Xvfb screen.\n' "$dimensions" "$expected"
    exit 1
  fi
  wait "$app_pid" || true
  app_pid=""
  if ! rg -q '"openai-codex"' "$directory/agent/auth.json" || ! rg -q 'demo.access.token' "$directory/agent/auth.json"; then
    printf 'Login did not write Codex credentials; logs: %s\n' "$directory"
    exit 1
  fi
  if rg -q '异常|失败|failed|Exception' "$directory/stderr.log"; then
    printf 'UI smoke reported an error: %s\n' "$directory/stderr.log"
    exit 1
  fi
done
printf 'Screenshots and logs: %s\n' "$output"
