#!/usr/bin/env python3
"""Isolated screenshot and Linux idle-resource probe. Run under xvfb-run.

Usage: xvfb-run -a -s '-screen 0 1600x1000x24' python3 tools/desktop-workspace-smoke.py
Set PI_WORKSPACE_SMOKE_APP to a published NativeAOT executable to measure that build.
Set PI_WORKSPACE_SMOKE_TURNS=1000 to exercise a long history instead of the tool fixture.
"""
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import time

ROOT = Path(__file__).resolve().parents[1]
OUTPUT = Path(tempfile.mkdtemp(prefix="piweft-workspace-"))
APP = Path(os.environ.get("PI_WORKSPACE_SMOKE_APP", ROOT / "src/PiWeft.Desktop/bin/Debug/net10.0/piweft-desktop.dll"))
CASES = [("chat", "dark", 1280, 820), ("appearance", "dark", 1280, 820),
         ("agent", "light", 1280, 820), ("mcp", "dark", 1280, 820),
         ("chat", "dark", 720, 520), ("sidebar", "dark", 1280, 820),
         ("statistics", "dark", 1280, 820), ("statistics-details", "light", 1280, 820),
         ("chat", "dark", 1920, 1080), ("chat", "light", 1280, 820),
         ("empty", "dark", 1280, 820), ("empty", "light", 1280, 820)]
SELECTED_CASES = set(map(int, sys.argv[1:])) if sys.argv[1:] else set(range(len(CASES)))


def cpu_ticks(pid):
    # comm is parenthesized and may include spaces; fields after it start at state.
    fields = Path(f"/proc/{pid}/stat").read_text().rsplit(")", 1)[1].split()
    return int(fields[11]) + int(fields[12])


def fixture(directory, theme, width, height, empty=False):
    agent, project = directory / "agent", directory / "project"
    agent.mkdir(parents=True)
    project.mkdir()
    shutil.copy(ROOT / "tests/PiWeft.Desktop.Tests/Fixtures/config-models.json", agent / "models.json")
    (agent / "desktop.json").write_text(json.dumps(dict(theme=theme, windowWidth=width, windowHeight=height)))
    (agent / "settings.json").write_text(json.dumps(dict(defaultProvider="local-openai", defaultModel="local-code")))
    sessions = agent / "sessions" / "fixture"
    sessions.mkdir(parents=True)
    stamp = "2026-01-01T00:00:00.000Z"
    entries = [dict(type="session", version=3, id="fixture-workspace", timestamp=stamp, cwd=str(project))]
    parent = None
    messages = [dict(role="user", content="参考 PiDeck 优化工作区，检查设置与工具输出。", timestamp=1767225600000),
                dict(role="assistant", api="openai-completions", provider="local-openai", model="local-code", stopReason="stop",
                     content=[dict(type="text", text="已完成布局检查。\n\n- 项目侧栏支持收起\n- 设置按分类展示\n- 工具输出按需展开\n\n```csharp\nvar workspace = new Workspace();\n```")], timestamp=1767225600000)]
    # Enough collapsed output to expose eager construction without using real sessions or network.
    messages += [dict(role="toolResult", toolCallId=f"call-{i}", toolName="read", isError=False,
                      content=[dict(type="text", text=f"文件 {i}\n" + "fixture output\n" * 600)], timestamp=1767225600000) for i in range(60)]
    messages.append(dict(role="assistant", api="openai-completions", provider="local-openai", model="local-code", stopReason="stop",
                         content=[dict(type="text", text="工作区已就绪。工具结果保留在上方，展开后可以选择并复制原文。\n\n设置页按分类展示，输入区会适应窗口宽度。")],
                         timestamp=1767225600000, usage=dict(input=20000, output=128, cacheRead=16000, cacheWrite=0, totalTokens=36128, cost=dict(total=0.012))))
    turns = int(os.environ.get("PI_WORKSPACE_SMOKE_TURNS", "0"))
    if not 0 <= turns <= 5000:
        raise ValueError("PI_WORKSPACE_SMOKE_TURNS must be between 0 and 5000")
    if turns:
        tail = messages[-1]
        messages = []
        for turn in range(turns):
            messages.extend([
                dict(role="user", content=f"历史问题 {turn}", timestamp=1767225600000),
                dict(role="assistant", api="openai-completions", provider="local-openai", model="local-code", stopReason="stop",
                     content=[dict(type="text", text=f"历史回复 {turn}。\n\n```csharp\nvar value = {turn};\n```")], timestamp=1767225600000),
            ])
        messages.append(tail)
    if empty:
        messages = []
    for index, message in enumerate(messages):
        identity = f"entry{index:04}"
        entries.append(dict(type="message", id=identity, parentId=parent, timestamp=stamp, message=message))
        parent = identity
    (sessions / "2026-01-01_fixture-workspace.jsonl").write_text("\n".join(json.dumps(e, ensure_ascii=False) for e in entries) + "\n")
    return agent, project


results = []
for index, (view, theme, width, height) in enumerate(CASES):
    if index not in SELECTED_CASES:
        continue
    directory = OUTPUT / f"{index}-{view}-{theme}"
    agent, project = fixture(directory, theme, width, height, empty=view == "empty")
    smoke_view = "chat" if view == "empty" else view
    command = ["dotnet", str(APP)] if APP.suffix == ".dll" else [str(APP)]
    with (directory / "stdout.log").open("w") as stdout, (directory / "stderr.log").open("w") as stderr:
        process = subprocess.Popen(command + ["--cwd", str(project), "--ui-smoke", smoke_view],
                                   env=dict(os.environ, PI_CODING_AGENT_DIR=str(agent)), stdout=stdout, stderr=stderr)
        try:
            deadline = time.monotonic() + 15
            while f"ui smoke 已打开 {smoke_view}" not in (directory / "stdout.log").read_text():
                if process.poll() is not None or time.monotonic() > deadline:
                    raise RuntimeError(f"View did not open: {directory}")
                time.sleep(0.1)
            time.sleep(1.5)
            window = subprocess.check_output(["xdotool", "search", "--onlyvisible", "--name", f"^PiWeft Desktop — {project}$"], text=True).splitlines()[0]
            screenshot = directory / "screenshot.png"
            subprocess.run(["import", "-window", window, str(screenshot)], check=True)
            dimensions = subprocess.check_output(["identify", "-format", "%wx%h", str(screenshot)], text=True)
            if dimensions != f"{width}x{height}":
                raise RuntimeError(f"Screenshot clipped: {dimensions}, expected {width}x{height}")
            start, ticks = time.monotonic(), cpu_ticks(process.pid)
            time.sleep(2)
            elapsed = time.monotonic() - start
            cpu = (cpu_ticks(process.pid) - ticks) / os.sysconf("SC_CLK_TCK") / elapsed * 100
            status = Path(f"/proc/{process.pid}/status").read_text().splitlines()
            rss = int(next(line.split()[1] for line in status if line.startswith("VmRSS:"))) / 1024
            if process.wait(timeout=15) != 0:
                raise RuntimeError(f"Application failed: {directory}")
            if smoke_view == "chat" and "timeline latest visible: True" not in (directory / "stdout.log").read_text():
                raise RuntimeError(f"Latest reply is outside the conversation viewport: {directory}")
            errors = (directory / "stderr.log").read_text()
            if any(word in errors for word in ("Exception", "异常", "failed")):
                raise RuntimeError(f"UI error: {directory}")
            result = dict(view=view, theme=theme, size=dimensions, rssMiB=round(rss, 1), idleCpuPercent=round(cpu, 2), screenshot=str(screenshot))
            results.append(result)
            print(json.dumps(result, ensure_ascii=False), flush=True)
        finally:
            if process.poll() is None:
                process.terminate()
                process.wait(timeout=10)
(OUTPUT / "results.json").write_text(json.dumps(dict(app=str(APP), scenarios=results), indent=2))
print(f"Screenshots, logs and measurements: {OUTPUT}")
