#!/usr/bin/env python3
"""Run under xvfb-run; verify real X11 PRIMARY ownership, independent CLIPBOARD and native middle paste."""
import base64
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import tempfile
import time

ROOT = Path(__file__).resolve().parents[1]
OUTPUT = Path(tempfile.mkdtemp(prefix="piweft-primary-"))
APP = Path(os.environ.get("PI_WORKSPACE_SMOKE_APP", ROOT / "src/PiWeft.Desktop/bin/Debug/net10.0/piweft-desktop.dll"))
agent, project = OUTPUT / "agent", OUTPUT / "project"
agent.mkdir()
project.mkdir()
shutil.copy(ROOT / "tests/PiWeft.Desktop.Tests/Fixtures/config-models.json", agent / "models.json")
(agent / "desktop.json").write_text(json.dumps(dict(theme="dark", windowWidth=1280, windowHeight=820)))
(agent / "settings.json").write_text(json.dumps(dict(defaultProvider="local-openai", defaultModel="local-code")))
sessions = agent / "sessions" / "fixture"
sessions.mkdir(parents=True)
entries = [dict(type="session", version=3, id="primary-test", timestamp="2026-01-01T00:00:00Z", cwd=str(project)),
           dict(type="message", id="reply001", parentId=None, timestamp="2026-01-01T00:00:00Z",
                message=dict(role="assistant", api="openai-completions", provider="local-openai", model="local-code",
                             content=[dict(type="text", text="PRIMARY 中文 selection\n\n**Markdown** also exports plain selected text.")],
                             timestamp=1767225600000, stopReason="stop"))]
(sessions / "2026-01-01_primary-test.jsonl").write_text("\n".join(json.dumps(entry) for entry in entries) + "\n")


def clip_read(selection):
    return subprocess.check_output(["xclip", "-selection", selection, "-out"], timeout=3).decode()


def clip_owner(selection, text):
    owner = subprocess.Popen(["xclip", "-quiet", "-selection", selection, "-in"], stdin=subprocess.PIPE,
                             stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    owner.stdin.write(text.encode())
    owner.stdin.close()
    return owner


clipboard = clip_owner("clipboard", "independent clipboard")
external = None
command = ["dotnet", str(APP)] if APP.suffix == ".dll" else [str(APP)]
with (OUTPUT / "stdout.log").open("w") as stdout, (OUTPUT / "stderr.log").open("w") as stderr:
    process = subprocess.Popen(command + ["--cwd", str(project), "--ui-smoke", "primary"],
                               env=dict(os.environ, PI_CODING_AGENT_DIR=str(agent)), stdout=stdout, stderr=stderr)
    try:
        deadline = time.monotonic() + 15
        while "primary-point:" not in (OUTPUT / "stdout.log").read_text():
            if process.poll() is not None or time.monotonic() > deadline:
                raise RuntimeError(f"Primary smoke did not prepare: {OUTPUT}")
            time.sleep(0.1)
        log = (OUTPUT / "stdout.log").read_text()
        selected = base64.b64decode(re.search(r"primary-source:(\S+)", log)[1]).decode()
        time.sleep(0.4)
        assert clip_read("primary") == selected, "Markdown selection not exported to PRIMARY"
        assert clip_read("clipboard") == "independent clipboard", "Selection overwrote CLIPBOARD"
        external_text = "外部 PRIMARY\nsecond line"
        external = clip_owner("primary", external_text)
        time.sleep(0.15)
        window = subprocess.check_output(["xdotool", "search", "--onlyvisible", "--name", f"^PiWeft Desktop — {project}$"], text=True).splitlines()[0]
        x, y = re.search(r"primary-point:(\d+),(\d+)", log).groups()
        subprocess.run(["xdotool", "mousemove", "--window", window, x, y, "click", "2"], check=True)
        time.sleep(0.4)
        subprocess.run(["import", "-window", window, str(OUTPUT / "primary-paste.png")], check=True)
        assert process.wait(timeout=15) == 0
        log = (OUTPUT / "stdout.log").read_text()
        draft = base64.b64decode(re.search(r"primary-draft:(\S+)", log)[1]).decode()
        assert draft == "before " + external_text + "after", repr(draft)
        assert clip_read("clipboard") == "independent clipboard"
        print(f"PRIMARY interoperability and actual middle paste passed: {OUTPUT}")
    finally:
        for child in (process, external, clipboard):
            if child is not None and child.poll() is None:
                child.terminate()
                child.wait(timeout=5)
