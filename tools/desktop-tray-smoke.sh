#!/usr/bin/env bash
# 托盘驻留验收（Run under xvfb-run）：关闭主窗口后进程继续、后台流式任务继续、
# 托盘在 StatusNotifier watcher 上注册、第二次启动能把隐藏的窗口唤回。
#
# 需要 session bus 上有 org.kde.StatusNotifierWatcher（KDE / Xfce 自带；
# GNOME 装 AppIndicator 扩展后才有）。没有 watcher 时跳过：此时应用按设计直接退出，
# 与「隐藏后失联」无关。
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
dll="$root/src/PiWeft.Desktop/bin/Debug/net11.0/piweft-desktop.dll"
output="$(mktemp -d /tmp/piweft-tray-XXXXXX)"
app_pid=""
cleanup() {
  if [[ -n "$app_pid" ]] && kill -0 "$app_pid" 2>/dev/null; then
    kill "$app_pid"
    wait "$app_pid" || true
  fi
}
trap cleanup EXIT

if ! command -v gdbus >/dev/null || ! command -v xdotool >/dev/null; then
  printf 'SKIP: gdbus / xdotool missing\n'
  exit 0
fi

has_watcher="$(gdbus call --session --dest org.freedesktop.DBus --object-path /org/freedesktop/DBus \
  --method org.freedesktop.DBus.NameHasOwner org.kde.StatusNotifierWatcher 2>/dev/null || true)"
if [[ "$has_watcher" != *true* ]]; then
  printf 'SKIP: no StatusNotifier watcher on the session bus\n'
  exit 0
fi
has_host="$(gdbus call --session --dest org.kde.StatusNotifierWatcher --object-path /StatusNotifierWatcher \
  --method org.freedesktop.DBus.Properties.Get org.kde.StatusNotifierWatcher IsStatusNotifierHostRegistered 2>/dev/null || true)"
if [[ "$has_host" != *true* ]]; then
  printf 'SKIP: watcher has no registered host\n'
  exit 0
fi

mkdir -p "$output/agent" "$output/project"
export PI_CODING_AGENT_DIR="$output/agent"
dotnet "$dll" --cwd "$output/project" --ui-smoke tray >"$output/stdout.log" 2>"$output/stderr.log" &
app_pid=$!

wait_phase() {
  local phase="$1"
  for _ in $(seq 1 240); do
    grep -q "tray-phase=$phase" "$output/stdout.log" 2>/dev/null && return 0
    kill -0 "$app_pid" 2>/dev/null || break
    sleep 0.25
  done
  printf 'FAIL: never reached tray-phase=%s\n' "$phase"
  exit 1
}

visible_window() {
  xdotool search --onlyvisible --name '^PiWeft' 2>/dev/null | head -1 || true
}

wait_phase shown
if grep -q 'tray supported=False' "$output/stdout.log"; then
  printf 'SKIP: the running app reports no usable tray\n'
  kill "$app_pid" 2>/dev/null || true
  wait "$app_pid" 2>/dev/null || true
  app_pid=""
  exit 0
fi
[[ -n "$(visible_window)" ]] || { printf 'FAIL: window not visible at startup\n'; exit 1; }

tray_item="$(gdbus call --session --dest org.kde.StatusNotifierWatcher --object-path /StatusNotifierWatcher \
  --method org.freedesktop.DBus.Properties.Get org.kde.StatusNotifierWatcher RegisteredStatusNotifierItems 2>/dev/null || true)"
if [[ "$tray_item" != *"StatusNotifierItem-$app_pid-"* ]]; then
  printf 'FAIL: tray item for pid %s is not registered\n' "$app_pid"
  exit 1
fi

wait_phase hidden
sleep 1
[[ -z "$(visible_window)" ]] || { printf 'FAIL: window still visible after close\n'; exit 1; }
before="$(grep -c 'stream t=' "$output/stdout.log" || true)"
sleep 3
after="$(grep -c 'stream t=' "$output/stdout.log" || true)"
if (( after <= before )); then
  printf 'FAIL: stream stopped while hidden (%s -> %s lines)\n' "$before" "$after"
  exit 1
fi

# 第二次启动只发激活信号，隐藏的窗口应自己回到前台。
dotnet "$dll" --cwd "$output/project" >"$output/second.log" 2>&1 || true
sleep 1
[[ -n "$(visible_window)" ]] || { printf 'FAIL: second launch did not reveal the window\n'; exit 1; }

wait_phase revealed
[[ -n "$(visible_window)" ]] || { printf 'FAIL: window not visible after reveal\n'; exit 1; }
menus="$(grep -o 'tray-menu=[^ ]*' "$output/stdout.log" | cut -d= -f2 | tr '\n' ' ')"
if [[ "$menus" != "pi.tray.hide pi.tray.show pi.tray.hide " ]]; then
  printf 'FAIL: tray menu did not follow window state: %s\n' "$menus"
  exit 1
fi
for _ in $(seq 1 120); do
  kill -0 "$app_pid" 2>/dev/null || break
  sleep 0.25
done
if kill -0 "$app_pid" 2>/dev/null; then
  printf 'FAIL: tray smoke did not exit\n'
  exit 1
fi
wait "$app_pid" || true
app_pid=""

if grep -qi 'exception' "$output/stderr.log"; then
  printf 'FAIL: stderr reports an exception: %s\n' "$output/stderr.log"
  exit 1
fi
printf 'tray smoke ok: hidden window stays alive, stream continues, tray registered, reveal works\n'
printf 'Logs: %s\n' "$output"
