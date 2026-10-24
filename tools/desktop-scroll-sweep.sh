#!/usr/bin/env bash
# 真实渲染复现「长消息部分情况下渲染消失」：
#   fixture 会话（用户报告的那条消息）→ 应用分步滚动 → 逐帧截图 → 像素检测空白带。
# 在 xvfb-run 下运行：xvfb-run -a -s '-screen 0 1920x1080x24' bash tools/desktop-scroll-sweep.sh <session.jsonl>
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
session="${1:?usage: desktop-scroll-sweep.sh <session.jsonl>}"
output="$(mktemp -d /tmp/piweft-scroll-sweep-XXXXXX)"
app_pid=""
cleanup() {
  if [[ -n "$app_pid" ]] && kill -0 "$app_pid" 2>/dev/null; then
    kill "$app_pid" 2>/dev/null || true
    wait "$app_pid" 2>/dev/null || true
  fi
}
trap cleanup EXIT

agent="$output/agent"
project="$output/project"
mkdir -p "$agent" "$project" "$output/frames"
cp "$root/tests/PiWeft.Desktop.Tests/Fixtures/config-models.json" "$agent/models.json"
if [[ -f "$root/tests/PiWeft.Desktop.Tests/Fixtures/desktop-dark.json" ]]; then
  cp "$root/tests/PiWeft.Desktop.Tests/Fixtures/desktop-dark.json" "$agent/desktop.json"
fi
mkdir -p "$agent/sessions/--home-scgm-RiderProjects-PiWeft--"
cp "$session" "$agent/sessions/--home-scgm-RiderProjects-PiWeft--/"

PI_CODING_AGENT_DIR="$agent" PIWEFT_MARKDOWN_HEIGHT_CACHE="${HEIGHT_CACHE:-1}" dotnet "$root/src/PiWeft.Desktop/bin/Debug/net10.0/piweft-desktop.dll" \
  --cwd "/home/scgm/RiderProjects/PiWeft" --ui-smoke scroll-sweep \
  >"$output/stdout.log" 2>"$output/stderr.log" &
app_pid=$!

# 等应用起窗口。
for _ in $(seq 1 40); do
  window="$(xdotool search --onlyvisible --name '^PiWeft Desktop' 2>/dev/null | head -1 || true)"
  if [[ -n "$window" ]]; then break; fi
  sleep 0.5
done
if [[ -z "${window:-}" ]]; then
  echo "no window found; stderr:"
  tail -20 "$output/stderr.log" || true
  exit 1
fi
sleep 4

# 高密度连拍：50 ms 一帧 × 50 帧（2.5 s），覆盖跳变瞬间的每一个呈现帧。
frames=0
for _ in $(seq 1 50); do
  import -window "$window" "$output/frames/frame-$(printf '%03d' "$frames").png" 2>/dev/null || true
  frames=$((frames + 1))
  sleep 0.05
done

kill "$app_pid" 2>/dev/null || true
wait "$app_pid" 2>/dev/null || true
app_pid=""

python3 "$root/tools/detect-blank-bands.py" "$output/frames" | tee "$output/blank-report.txt"
echo "output: $output"
