#!/usr/bin/env bash
# 用量查询界面验收：本地假网关返回两种形态（余额 / 三档百分比），
# 分别截图「配置弹窗」与「统计面板底部摘要」，深浅主题各一组。
# Run with: xvfb-run -a -s '-screen 0 1600x1000x24' bash tools/desktop-usage-smoke.sh
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
output="$(mktemp -d /tmp/pi-usage-smoke-XXXXXX)"
app="${PI_USAGE_SMOKE_APP:-$root/src/PiWeft.Desktop/bin/Debug/net10.0/piweft-desktop.dll}"
app_pid=""
server_pid=""
cleanup() {
  if [[ -n "$app_pid" ]] && kill -0 "$app_pid" 2>/dev/null; then kill "$app_pid"; wait "$app_pid" || true; fi
  if [[ -n "$server_pid" ]] && kill -0 "$server_pid" 2>/dev/null; then kill "$server_pid"; wait "$server_pid" || true; fi
}
trap cleanup EXIT

# 假网关：/usage 同时能返回余额与三档窗口；由探针 parse 决定展示哪种形态。
port_file="$output/port"
python3 - "$port_file" <<'PY' &
import http.server, json, sys
class Handler(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        payload = {
            "balance": 42.5,
            "unit": "CNY",
            "usage": {
                "rolling": {"percent": 12},
                "weekly": {"percent": 75, "resetsAt": "2030-01-01T00:00:00Z"},
                "monthly": {"percent": 96},
            },
        }
        if self.path.startswith("/periods"):
            # 三档形态：去掉 balance，让 built-in 通用候选结构不符、落到用户探针。
            payload = {"usage": payload["usage"]}
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
  for target in balance statistics; do
    directory="$output/$theme-$target"
    mkdir -p "$directory/agent" "$directory/project"
    cp "$root/tests/PiWeft.Desktop.Tests/Fixtures/config-models.json" "$directory/agent/models.json"
    cp "$root/tests/PiWeft.Desktop.Tests/Fixtures/desktop-$theme.json" "$directory/agent/desktop.json"
    printf '{"defaultProvider":"local-openai","defaultModel":"local-code"}' >"$directory/agent/settings.json"
    if [[ "$target" == "balance" ]]; then
      # 弹窗 + 余额形态：显示金额行，同时验证模板字段与状态行。
      cat >"$directory/agent/usage-probes.json" <<JSON
{"providers":{"local-openai":{"enabled":true,"template":"general","baseUrl":"http://127.0.0.1:$port","apiKey":"test","timeoutSecs":5,"intervalMinutes":5}}}
JSON
    else
      # 统计面板 + 三档形态：显示滚动/周/月进度条与档位色。
      cat >"$directory/agent/usage-probes.json" <<JSON
{"providers":{"local-openai":{"enabled":true,"template":"","baseUrl":"http://127.0.0.1:$port/periods","apiKey":"test","timeoutSecs":5,"intervalMinutes":5}},
 "probes":[{"name":"local periods","match":{"baseUrlContains":["127.0.0.1"]},"request":{"path":"/usage"},"parse":{"kind":"periods"}}]}
JSON
    fi
    if [[ "$app" == *.dll ]]; then command=(dotnet "$app"); else command=("$app"); fi
    PI_CODING_AGENT_DIR="$directory/agent" "${command[@]}" --cwd "$directory/project" --ui-smoke "$target" \
      >"$directory/stdout.log" 2>"$directory/stderr.log" &
    app_pid=$!
    window=""
    for _ in {1..100}; do
      window="$(xdotool search --onlyvisible --name '^PiWeft Desktop — ' 2>/dev/null | head -1 || true)"
      if [[ -n "$window" ]] && rg -q "ui smoke 已打开 $target" "$directory/stdout.log"; then break; fi
      kill -0 "$app_pid" 2>/dev/null || break
      sleep 0.2
    done
    if [[ -z "$window" ]]; then
      printf 'Usage window did not open (%s); logs: %s\n' "$target" "$directory"
      exit 1
    fi
    # 等一次真实查询落盘：摘要/弹窗状态行从「正在查询」变成结果。
    sleep 2.5
    xdotool getwindowgeometry --shell "$window" >"$directory/geometry.log"
    import -window "$window" "$directory/$target.png"
    dimensions="$(identify -format '%wx%h' "$directory/$target.png")"
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
done
printf 'Screenshots and logs: %s\n' "$output"
