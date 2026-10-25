#!/usr/bin/env bash
# 「移动到最新」悬浮按钮回归：让正文区真的移动/变矮（待办卡、搜索条、底部终端坞），
# 逐 tick 断言圆钮下缘 = 正文区底边 + 3，并抽帧确认圆钮没有被切成半个。
# 用法：xvfb-run -a -s '-screen 0 1920x1080x24' bash tools/desktop-floating-button-check.sh [会话.jsonl]
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
session="${1:-$(ls -t "$HOME/.pi/agent/sessions/--home-scgm-RiderProjects-PiWeft--/"*.jsonl 2>/dev/null | head -1)}"
[[ -n "${session:-}" ]] || { echo "没有可用会话，参数传入一个 .jsonl"; exit 2; }
output="$(mktemp -d /tmp/piweft-floating-check-XXXXXX)"
dll="$root/src/PiWeft.Desktop/bin/Debug/net10.0/piweft-desktop.dll"
[[ -f "$dll" ]] || { echo "先构建桌面端：dotnet build src/PiWeft.Desktop"; exit 2; }

agent="$output/agent"; mkdir -p "$agent/sessions/--home-scgm-RiderProjects-PiWeft--" "$output/frames"
cp "$root/tests/PiWeft.Desktop.Tests/Fixtures/config-models.json" "$agent/models.json"
cp "$root/tests/PiWeft.Desktop.Tests/Fixtures/desktop-dark.json" "$agent/desktop.json" 2>/dev/null || true
cp "$session" "$agent/sessions/--home-scgm-RiderProjects-PiWeft--/"

PIWEFT_SWEEP_PROBE=1 PI_CODING_AGENT_DIR="$agent" dotnet "$dll" --cwd "$root" --ui-smoke latest-churn \
  >"$output/stdout.log" 2>"$output/stderr.log" &
app=$!
cleanup() { kill "$app" 2>/dev/null || true; wait "$app" 2>/dev/null || true; }
trap cleanup EXIT

for _ in $(seq 1 60); do grep -q "churn-ready" "$output/stdout.log" && break; sleep 0.5; done
window="$(xdotool search --onlyvisible --name '^PiWeft Desktop' | head -1)"
[[ -n "${window:-}" ]] || { echo "没找到窗口，看 $output/stderr.log"; exit 2; }
for i in $(seq 0 39); do import -window "$window" "$output/frames/f-$(printf '%03d' "$i").png" 2>/dev/null || true; sleep 0.12; done
kill "$app" 2>/dev/null || true; wait "$app" 2>/dev/null || true; trap - EXIT

python3 - "$output" <<'PY'
import re, sys, pathlib
from PIL import Image
import numpy as np

out = pathlib.Path(sys.argv[1])
overlap = 3
rows = []
for line in (out / "stdout.log").read_text().splitlines():
    m = re.match(r"^churn (\d+) btn=\((-?\d+),(-?\d+)\+(-?\d+)x(-?\d+)\) mainBottom=(-?\d+) hosted=(\w+) op=([\d.]+)", line)
    if m:
        x, y, w, h = map(int, m.group(2, 3, 4, 5))
        rows.append(dict(tick=int(m.group(1)), x=x, y=y, w=w, h=h, bottom=y + h,
                         mainBottom=int(m.group(6)), hosted=m.group(7), op=float(m.group(8))))
if not rows:
    print("演练没有输出几何行（检查 PIWEFT_SWEEP_PROBE=1 与 --ui-smoke latest-churn）")
    sys.exit(2)

stale = [r for r in rows if r["bottom"] != r["mainBottom"] + overlap]
moving = len({r["mainBottom"] for r in rows})
print(f"ticks={len(rows)} 正文区底边取值={moving} 种（要 >1 才说明正文区真的在动）")
print(f"位置陈旧（圆钮没跟随正文区）的 tick={len(stale)}")
for r in stale[:5]:
    print(f"   tick{r['tick']} btnBottom={r['bottom']} 期望={r['mainBottom'] + overlap}")

# 抽帧检查圆钮是否被切成半个：按几何行给出的中心量四个扇区的环带亮度
bad = []
frames = sorted((out / "frames").glob("*.png"))
for p in frames:
    g = np.asarray(Image.open(p).convert("L")).astype(float)
    # 用最近一次几何的中心附近找高亮图标，再量圆环四向
    hint = max(rows, key=lambda r: r["tick"])
    cx, cy = hint["x"] + hint["w"] / 2, hint["y"] + hint["h"] / 2
    box = g[int(cy) - 30:int(cy) + 30, int(cx) - 30:int(cx) + 30]
    if box.size == 0 or (box > 150).sum() < 12:
        continue
    ys, xs = np.nonzero(box > 150)
    cx, cy = float(xs.mean()) + cx - 30, float(ys.mean()) + cy - 30
    def arc(angle):
        x = int(cx + 17 * np.cos(np.radians(angle))); y = int(cy - 17 * np.sin(np.radians(angle)))
        return float(g[max(0, y - 1):y + 2, max(0, x - 1):x + 2].mean())
    quadrants = {"上": np.mean([arc(a) for a in (70, 90, 110)]), "下": np.mean([arc(a) for a in (250, 270, 290)]),
                 "左": arc(180), "右": arc(0)}
    lo, hi = min(quadrants.values()), max(quadrants.values())
    if hi > 45 and lo < 30:  # 三向有环、一向是纯背景 → 缺了一瓣
        bad.append((p.name, {k: round(v, 1) for k, v in quadrants.items()}))
print(f"缺瓣帧={len(bad)}")
for item in bad[:5]:
    print("   ", item)

failed = bool(stale) or moving < 2 or bool(bad)
print("RESULT", "FAIL" if failed else "PASS", " 细节目录：", out)
sys.exit(1 if failed else 0)
PY
