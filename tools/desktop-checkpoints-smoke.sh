#!/usr/bin/env bash
# 检查点面板的视觉验收：造一个带两个提交与未提交改动的夹具仓库，写入两条 pi 检查点引用，
# 打开右侧「检查点」分段按深浅主题各截一张图。夹具自成一体，不读也不改用户的 agent 目录与仓库。
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
output="$(mktemp -d /tmp/piweft-desktop-checkpoints-XXXXXX)"
app_pid=""
cleanup() {
  if [[ -n "$app_pid" ]] && kill -0 "$app_pid" 2>/dev/null; then
    kill "$app_pid"
    wait "$app_pid" || true
  fi
}
trap cleanup EXIT

for theme in dark light; do
  work="$output/$theme/project"
  mkdir -p "$work" "$output/$theme/agent" "$output/$theme/home/.local/share" "$output/$theme/home/.config"
  git -C "$work" init -q -b main
  git -C "$work" config user.email smoke@example.com
  git -C "$work" config user.name "PiWeft Smoke"
  printf 'first\n' >"$work/app.cs"
  printf '### fixture\n' >"$work/README.md"
  git -C "$work" add --all
  git -C "$work" commit -qm "initial commit"
  first="$(git -C "$work" rev-parse HEAD)"

  printf 'namespace Demo;\n\npublic static class Program\n{\n}\n' >"$work/app.cs"
  git -C "$work" add --all
  git -C "$work" commit -qm "shape the app"
  second="$(git -C "$work" rev-parse HEAD)"

  # 复现「340 个文件 / 5 万多行」的真实快照：先提交一批生成物，再在快照之后改动它们。
  # 完整补丁不能进面板（旧实现展开就卡），这里用来验证默认只加载 `--stat` 概览。
  mkdir -p "$work/gen"
  for i in $(seq 1 340); do
    {
      printf 'file %s\n' "$i"
      for j in $(seq 1 170); do printf 'line %s\n' "$j"; done
    } >"$work/gen/file-$i.txt"
  done
  git -C "$work" add -- gen
  git -C "$work" commit -qm "generated files"
  bulk="$(git -C "$work" rev-parse HEAD)"

  # 检查点引用：一条对准第一次提交、一条对准含 340 个生成物的提交。
  git -C "$work" update-ref "refs/pi-checkpoints/20260101T000000250Z-aaaaaaaa" "$first"
  git -C "$work" update-ref "refs/pi-checkpoints/20260101T000030750Z-bbbbbbbb" "$bulk"

  for i in $(seq 1 340); do
    for j in $(seq 1 170); do printf 'extra %s\n' "$j"; done >>"$work/gen/file-$i.txt"
  done
  printf 'notes\n' >"$work/notes.md"

  # 面板跟着会话的工作目录走：把草稿会话指到夹具仓库上。
  jq --arg workspace "$work" '. + {chatWorkspace: $workspace}' \
    "$root/tests/PiWeft.Desktop.Tests/Fixtures/desktop-$theme.json" >"$output/$theme/agent/desktop.json"
  HOME="$output/$theme/home" PI_CODING_AGENT_DIR="$output/$theme/agent" dotnet "$root/src/PiWeft.Desktop/bin/Debug/net11.0/piweft-desktop.dll" \
    --cwd "$work" --ui-smoke checkpoints-diff >"$output/$theme/stdout.log" 2>"$output/$theme/stderr.log" &
  app_pid=$!
  sleep 5
  window="$(xdotool search --onlyvisible --name '^PiWeft Desktop — ' | head -1)"
  import -window "$window" "$output/$theme.png"
  wait "$app_pid" || true
  app_pid=""
  if ! rg -q 'checkpoints rows=[1-9]' "$output/$theme/stdout.log"; then
    printf '检查点面板没有渲染出任何行: %s\n' "$output/$theme/stdout.log"
    exit 1
  fi
  if ! rg -q 'diff-visible=True' "$output/$theme/stdout.log"; then
    printf '检查点变更概览没有展开: %s\n' "$output/$theme/stdout.log"
    exit 1
  fi
  lines="$(rg -o 'diff-lines=[0-9]+' "$output/$theme/stdout.log" | head -1 | cut -d= -f2)"
  if [[ -z "$lines" ]] || (( lines < 300 || lines > 2000 )); then
    printf '变更概览行数不在预期范围（应为 340 个文件、≤2000 行）: %s\n' "${lines:-<none>}"
    exit 1
  fi
  if rg -q '异常|失败|failed|Exception' "$output/$theme/stderr.log"; then
    printf '检查点面板 smoke 报错: %s\n' "$output/$theme/stderr.log"
    exit 1
  fi
done

printf 'Screenshots and logs: %s\n' "$output"
