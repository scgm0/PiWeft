#!/usr/bin/env bash
# Git 面板的视觉验收：造一个有远端、有已暂存/未暂存/未跟踪改动、有合并冲突、有 stash
# 与一个隔离副本的夹具仓库。深浅主题各截九张：面板全景、diff 浮层、提交详情、
# 新建副本表单、比较节的「工作区」范围、审查层筛选（.gitattributes 覆盖）、
# 无暂存时的一键提交入口，以及外部提交前后各一张。
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

# 夹具的 git 不读用户的全局配置：开着 commit.gpgsign / core.hooksPath 的机器上
# 第一次 commit 就会中断。应用进程继承同一套环境，行为一致。
export GIT_CONFIG_GLOBAL=/dev/null
export GIT_CONFIG_NOSYSTEM=1

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
  # 仓内审查层：把 src/Staged.cs 标成测试层，截图核对 .gitattributes 覆盖内置规则。
  printf 'src/Staged.cs piweft-review-layer=test\n' >"$work/.gitattributes"
  git -C "$work" add --all
  git -C "$work" commit -qm "initial commit"
  git -C "$work" push -q -u origin main

  # 先用一处无关改动造出 stash 条目（stash 会把当时所有改动都收走，
  # 所以必须在大面积改动之前做，否则夹具里就只剩干净工作区）。
  printf 'scratch\n' >"$work/scratch.txt"
  git -C "$work" stash push -q -u -m "smoke stash"

  # 提交图要有分叉与合并才测得出泳道：一条 side 分支 + 一次 --no-ff 合并。
  git -C "$work" switch -q -c side
  printf 'side\n' >"$work/side.txt"
  git -C "$work" add -- side.txt
  git -C "$work" commit -qm "side work"
  git -C "$work" switch -q main
  git -C "$work" merge -q --no-ff --no-edit side

  # 合并冲突要占一节：side 分支与 main 各改同一个文件，合不进去就停在冲突态。
  printf 'main line\n' >"$work/conflict.md"
  git -C "$work" add -- conflict.md
  git -C "$work" commit -qm "main side of conflict"
  git -C "$work" switch -q -c feature HEAD~1
  printf 'feature line\n' >"$work/conflict.md"
  git -C "$work" add -- conflict.md
  git -C "$work" commit -qm "feature side of conflict"
  git -C "$work" switch -q main
  git -C "$work" merge feature >/dev/null 2>&1 || true

  # 隔离副本：子代理之外的并行工作目录，面板「更多」区要列出来。
  git -C "$work" worktree add -q "$output/$theme/worktree" -b smoke-copy HEAD

  # 会话目录里的第二个仓库：分支栏的仓库下拉与切换靠它才看得见。
  mkdir -p "$work/packages/lib"
  git -C "$work/packages/lib" init -q -b lib-main
  git -C "$work/packages/lib" config user.email smoke@example.com
  git -C "$work/packages/lib" config user.name "PiWeft Smoke"
  printf 'namespace Lib;\n\npublic static class Values {}\n' >"$work/packages/lib/Values.cs"
  git -C "$work/packages/lib" add --all
  git -C "$work/packages/lib" commit -qm "lib base"

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
  # 面板布局预先展开「更多」区：stash 与隔离副本都在那一段。
  jq --arg workspace "$work" --arg panes "图=200:1|比较=160:0|更多=250:1|更改=1" \
    '. + {chatWorkspace: $workspace, gitPanes: {($workspace): $panes}, windowHeight: 1000}' \
    "$root/tests/PiWeft.Desktop.Tests/Fixtures/desktop-$theme.json" >"$output/$theme/agent/desktop.json"

  shoot() {
    local view="$1" name="$2"
    HOME="$output/$theme/home" PI_CODING_AGENT_DIR="$output/$theme/agent" \
      dotnet "$root/src/PiWeft.Desktop/bin/Debug/net11.0/PiWeft.dll" \
      --cwd "$work" --ui-smoke "$view" >"$output/$theme/$name.log" 2>"$output/$theme/$name.err.log" &
    app_pid=$!
    sleep 5
    window="$(xdotool search --onlyvisible --name '^PiWeft Desktop — ' | head -1)"
    if [[ -z "$window" ]]; then
      printf 'Git 面板 smoke 找不到窗口（%s）\n' "$name"
      cat "$output/$theme/$name.err.log" >&2 || true
      exit 1
    fi
    import -window "$window" "$output/$theme/$name.png"
    wait "$app_pid" || true
    app_pid=""
    if rg -q '异常|失败|failed|Exception' "$output/$theme/$name.err.log"; then
      printf 'Git 面板 smoke 报错: %s\n' "$output/$theme/$name.err.log"
      exit 1
    fi
  }

  # 面板全景不带 diff 浮层：行内动作、冲突节、更多区才看得见。
  shoot git-panel panel
  # diff 浮层：选中第一条改动。
  shoot git diff
  # 新建隔离副本的内嵌编辑器（名称、基线、提示与按钮）。
  shoot git-worktree worktree
  # 展开一条提交：作者行右侧的复制哈希与提交操作菜单入口。
  shoot git-commit commit
  # 比较节的「工作区」范围：下拉里的新目标与含未提交/未跟踪的结果行。
  shoot git-compare compare
  # 审查层筛选：.gitattributes 声明的那一条只在「测试」层出现。
  shoot git-compare-lens lens
  # 取消暂存后露出「全部暂存并提交」入口（冲突还在，只用于截图核对排版）。
  git -C "$work" restore --staged -- src/Staged.cs || true
  shoot git-panel smart-commit

  # 自动刷新：面板开着时从外部提交，两秒轮询应把改动列表刷成干净。
  HOME="$output/$theme/home" PI_CODING_AGENT_DIR="$output/$theme/agent" \
    dotnet "$root/src/PiWeft.Desktop/bin/Debug/net11.0/PiWeft.dll" \
    --cwd "$work" --ui-smoke git-panel >"$output/$theme/live.log" 2>"$output/$theme/live.err.log" &
  app_pid=$!
  sleep 5
  window="$(xdotool search --onlyvisible --name '^PiWeft Desktop — ' | head -1)"
  if [[ -z "$window" ]]; then
    printf 'Git 面板 smoke 找不到窗口（live）\n'
    exit 1
  fi
  import -window "$window" "$output/$theme/live-before.png"
  # 外部提交必须真的成功：失败时前后两张图一样，脚本不能照样退出 0。
  if ! git -C "$work" add --all || ! git -C "$work" commit -qm "external commit"; then
    printf 'Git 面板 smoke 外部提交失败\n'
    git -C "$work" status --porcelain >&2 || true
    exit 1
  fi
  if [[ -n "$(git -C "$work" status --porcelain)" ]]; then
    printf 'Git 面板 smoke 外部提交后工作区仍不干净\n'
    git -C "$work" status --porcelain >&2
    exit 1
  fi
  git -C "$work" worktree prune >/dev/null 2>&1 || true
  sleep 3
  import -window "$window" "$output/$theme/live-after.png"
  wait "$app_pid" || true
  app_pid=""
  if rg -q '异常|失败|failed|Exception' "$output/$theme/live.err.log"; then
    printf 'Git 面板 smoke 报错: %s\n' "$output/$theme/live.err.log"
    exit 1
  fi
done

printf 'Screenshots and logs: %s\n' "$output"
