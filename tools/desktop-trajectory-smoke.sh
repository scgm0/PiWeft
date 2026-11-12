#!/usr/bin/env bash
# 轨迹页的视觉验收：造一个有 6 轮、连续工具、压缩与扩展过程的 pi 会话，
# 打开右侧「轨迹」分段按深浅主题各截一张图。夹具自成一体，不读也不改用户的 agent 目录。
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
output="$(mktemp -d /tmp/piweft-desktop-trajectory-XXXXXX)"
# 默认截初始态；SMOKE_VIEW=trajectory 时额外选中一条工具记录，检查检查器与选中行。
view="${SMOKE_VIEW:-trajectory-initial}"
app_pid=""
cleanup() {
  if [[ -n "$app_pid" ]] && kill -0 "$app_pid" 2>/dev/null; then
    kill "$app_pid"
    wait "$app_pid" || true
  fi
}
trap cleanup EXIT

for theme in dark light; do
  project="$output/$theme/project"
  agent="$output/$theme/agent"
  mkdir -p "$project" "$agent/sessions" "$output/$theme/home"

  # 工作目录 → 会话目录名：去掉开头的斜杠，把 / 换成 -，前后各包一层 --（与 SessionPaths.EncodeCwd 一致）。
  encoded="--$(printf '%s' "${project#/}" | tr '/' '-')--"
  session_dir="$agent/sessions/$encoded"
  mkdir -p "$session_dir"

  cp "$root/tests/PiWeft.Desktop.Tests/Fixtures/desktop-$theme.json" "$agent/desktop.json"
  cp "$root/tests/PiWeft.Desktop.Tests/Fixtures/config-models.json" "$agent/models.json"

  python3 - "$session_dir/fixture.jsonl" "$project" <<'PY'
import json
import sys
from datetime import datetime, timezone

path, cwd = sys.argv[1], sys.argv[2]
base = 1767225600000  # 2026-01-01T00:00:00Z
seq = 0
lines = []
parent = None
counter = 0


def iso(ms):
    return datetime.fromtimestamp(ms / 1000, timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.%f")[:-3] + "Z"


def entry(kind, at, **fields):
    global parent, seq, counter
    seq += 1
    counter += 1
    node = {"type": kind, "id": f"e{seq:08x}", "parentId": parent, "timestamp": iso(at)}
    node.update(fields)
    parent = node["id"]
    lines.append(node)
    return node["id"]


def user(at, text):
    return entry("message", at, message={
        "role": "user",
        "content": [{"type": "text", "text": text}],
        "timestamp": at,
    })


def assistant(at, thinking, text, calls, usage, stop="toolUse"):
    content = []
    if thinking:
        content.append({"type": "thinking", "thinking": thinking})
    if text:
        content.append({"type": "text", "text": text})
    for call_id, command in calls:
        content.append({"type": "toolCall", "id": call_id, "name": "bash", "arguments": {"command": command}})
    duration = 2400.0
    entry("message", at + int(duration), message={
        "role": "assistant",
        "content": content,
        "api": "openai-completions",
        "provider": "fixture",
        "model": "fixture-model",
        "usage": usage,
        "stopReason": stop,
        "timestamp": at,
        "durationMs": duration,
    })
    return at + int(duration)


def tool_result(at, call_id, text, duration=180.0):
    entry("message", at + int(duration), message={
        "role": "toolResult",
        "toolCallId": call_id,
        "toolName": "bash",
        "content": [{"type": "text", "text": text}],
        "timestamp": at,
        "durationMs": duration,
    })
    return at + int(duration)


lines.append({
    "type": "session",
    "version": 3,
    "id": "fixture-trajectory-session",
    "timestamp": iso(base),
    "cwd": cwd,
})
entry("model_change", base - 8000, provider="fixture", modelId="fixture-model")
entry("thinking_level_change", base - 7000, thinkingLevel="medium")

prompts = [
    "请实现轨迹页面",
    "下一步推荐什么？",
    "补丁列表滚动没效果",
    "把检查器里的耗时也显示出来",
    "看一下这轮的工具调用顺序",
    "最后再跑一遍测试",
]

for turn, prompt in enumerate(prompts):
    at = base + turn * 600_000
    user(at, prompt)
    usage = {"input": 1200 + turn * 300, "output": 180 + turn * 40, "cacheRead": 400, "cacheWrite": 0,
             "reasoning": 24, "totalTokens": 1800 + turn * 300}
    if turn == 2:
        calls = [(f"c{turn}-{index}", "sed -n 1,80p src/App.cs" if index % 2 == 0 else "grep -rn Trajectory src")
                 for index in range(10)]
        at = assistant(at + 100, "先连续查十次，观察工具组折叠。", "连续工具会压成一行。", calls, usage)
        for index, (call_id, _) in enumerate(calls):
            at = tool_result(at + 120 + index * 10, call_id, f"result {index}\nline two\nline three")
    else:
        at = assistant(at + 100, f"第 {turn + 1} 轮先看现状。",
                       f"这一轮处理：{prompt}", [(f"c{turn}-a", "ls -la"), (f"c{turn}-b", "dotnet build")], usage)
        at = tool_result(at + 200, f"c{turn}-a", "src\nbuild.sh\nREADME.md", 420.0)
        at = tool_result(at + 300, f"c{turn}-b", "Build succeeded.\n0 warnings, 0 errors.", 860.0)
        at = assistant(at + 400, "结果对得上，给出结论。", f"第 {turn + 1} 轮完成。", [],
                       {"input": 900, "output": 120, "cacheRead": 300, "cacheWrite": 0, "reasoning": 12,
                        "totalTokens": 1320}, stop="stop")

    if turn == 3:
        entry("compaction", at + 2000, summary="上下文压缩为要点，保留当前轨迹实现。", firstKeptEntryId="e00000003",
              tokensBefore=181200)
    if turn == 4:
        entry("custom", at + 2000, customType="piweft.fixture", data={"note": "轨迹夹具"})

with open(path, "w", encoding="utf-8") as handle:
    for line in lines:
        handle.write(json.dumps(line, ensure_ascii=False) + "\n")
PY

  HOME="$output/$theme/home" PI_CODING_AGENT_DIR="$agent" dotnet "$root/src/PiWeft.Desktop/bin/Debug/net11.0/piweft-desktop.dll" \
    --cwd "$project" --ui-smoke "$view" >"$output/$theme/stdout.log" 2>"$output/$theme/stderr.log" &
  app_pid=$!
  sleep 5
  window="$(xdotool search --onlyvisible --name '^PiWeft Desktop — ' | head -1)"
  import -window "$window" "$output/$theme.png"
  kill "$app_pid" 2>/dev/null || true
  wait "$app_pid" || true
  app_pid=""
  if ! rg -q 'trajectory turns=6 records=[0-9]+ folded=5 window=3' "$output/$theme/stdout.log"; then
    printf '轨迹页没有建出预期的轮次/窗口: %s\n' "$output/$theme/stdout.log"
    exit 1
  fi
  if rg -q '异常|失败|failed|Exception' "$output/$theme/stderr.log"; then
    printf '轨迹页 smoke 报错: %s\n' "$output/$theme/stderr.log"
    exit 1
  fi
done

printf 'Screenshots and logs: %s\n' "$output"
