#!/usr/bin/env bash
# Git 面板的视觉验收：造一个有远端、有已暂存/未暂存/未跟踪改动、有 stash 的夹具仓库，
# 打开 Git 面板按深浅主题各截一张图。夹具自成一体，不读也不改用户的 agent 目录与仓库。
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
output="$(mktemp -d /tmp/piweft-desktop-git-XXXXXX)"
app_pid=""
cleanup() {
  if [[ -n "$app_pid" ]] && kill -0 "$app_pid" 2>/dev/null; then
    kill "$app_pid"
    wait "$app_pid" || true
  fi
}
trap cleanup EXIT

# 夹具仓库：一个裸远端 + 一个带各类改动的克隆。
for theme in dark light; do
  work="$output/$theme/project"
  remote="$output/$theme/remote.git"
  mkdir -p "$work" "$output/$theme/agent" "$output/$theme/home/.local/share" "$output/$theme/home/.config"
  git init -q --bare -b main "$remote"
  git -C "$work" init -q -b main
  git -C "$work" config user.email smoke@example.com
  git -C "$work" config user.name "PiWeft Smoke"
  git -C "$work" remote add origin "$remote"
  printf 'first\n' >"$work/app.cs"
  printf '### fixture\n' >"$work/README.md"
  git -C "$work" add --all
  git -C "$work" commit -qm "initial commit"
  git -C "$work" push -q -u origin main

  # 先用一处无关改动造出 stash 条目（stash 会把当时所有改动都收走，
  # 所以必须在大面积改动之前做，否则夹具里就只剩干净工作区）。
  printf 'scratch\n' >"$work/scratch.txt"
  git -C "$work" stash push -q -u -m "smoke stash"

  # 再铺开三种状态：已暂存（新增）、未暂存（修改，也是 diff 区要展示的那份）、未跟踪。
  mkdir -p "$work/src"
  printf 'namespace Demo;\n\npublic static class Staged\n{\n}\n' >"$work/src/Staged.cs"
  git -C "$work" add -- src/Staged.cs
  printf 'first\nsecond line\nthird line\n' >"$work/app.cs"
  printf 'notes\n' >"$work/untracked.md"

  # 远端要先有一个本地没有的提交，拉取才不是灰的（ahead/behind 也要有东西可看）。
  other="$output/$theme/other"
  git clone -q "$remote" "$other"
  git -C "$other" config user.email smoke@example.com
  git -C "$other" config user.name "PiWeft Smoke"
  printf 'remote side\n' >"$other/remote-only.txt"
  git -C "$other" add --all
  git -C "$other" commit -qm "remote commit"
  git -C "$other" push -q
  git -C "$work" fetch -q

  # 让会话的工作目录就是夹具仓库：面板内容跟着会话的 cwd 走，不跟着 --cwd 走
  # （草稿会话落在「对话」分段的聊天目录里，所以要把它指到仓库上）。
  jq --arg workspace "$work" '. + {chatWorkspace: $workspace}' \
    "$root/tests/PiWeft.Desktop.Tests/Fixtures/desktop-$theme.json" >"$output/$theme/agent/desktop.json"
  HOME="$output/$theme/home" PI_CODING_AGENT_DIR="$output/$theme/agent" dotnet "$root/src/PiWeft.Desktop/bin/Debug/net10.0/piweft-desktop.dll" \
    --cwd "$work" --ui-smoke git >"$output/$theme/stdout.log" 2>"$output/$theme/stderr.log" &
  app_pid=$!
  sleep 5
  window="$(xdotool search --onlyvisible --name '^PiWeft Desktop — ' | head -1)"
  import -window "$window" "$output/$theme.png"
  wait "$app_pid" || true
  app_pid=""
  if rg -q '异常|失败|failed|Exception' "$output/$theme/stderr.log"; then
    printf 'Git 面板 smoke 报错: %s\n' "$output/$theme/stderr.log"
    exit 1
  fi
done

printf 'Screenshots and logs: %s\n' "$output"
