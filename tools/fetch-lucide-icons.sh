#!/usr/bin/env bash
#
# 从 Lucide 官方仓库下载图标到桌面端的内嵌图标集。
#
#   tools/fetch-lucide-icons.sh file-text package palette
#   tools/fetch-lucide-icons.sh --list          # 列出已有图标名
#
# 下载的 SVG 会被 csproj 里的 <EmbeddedResource Include="Resources/icons/*.svg" />
# 自动打进程序集，不需要改工程文件。
#
# 脚本会校验 AppIcons 依赖的两个属性：
#   - stroke="currentColor"：加载时替换成当前主题颜色，所以图标能跟随深浅主题换色；
#   - stroke-width="2"：加载时调成 2.25，避免 24px 画布缩到 16px 显示时笔画过细。
#
# 许可：Lucide 采用 ISC。其中一部分图标派生自 Feather（MIT），
# 名单在 src/PiWeft.Desktop/Resources/icons/LICENSE-Lucide.txt 里；
# 新增名字若属于该名单，无需改动（名单已覆盖）。
#
# 新名字要不要加进那份名单，看**几何是否沿用 Feather**，而不是看两者是否都有同名图标
# （Lucide 的图标名基本是 Feather 的超集，只看名字会漏判也会误判）。对比两边的图元数据：
#
#   curl -s https://raw.githubusercontent.com/feathericons/feather/main/icons/<名>.svg | grep -oE '<(path|circle|rect|line)[^>]*/>'
#   curl -s https://raw.githubusercontent.com/lucide-icons/lucide/main/icons/<名>.svg      | grep -oE '<(path|circle|rect|line)[^>]*/>'
#
# 图元一致或仅属性顺序不同（lock、search）→ 派生自 Feather，名字要进名单；
# 路径被重画（shield 在 Feather 是 `M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10z`，
# Lucide 是另一套带圆角的 24 网格路径）→ 只是同名重制，按 ISC 处理，不进名单。
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
icons_dir="$repo_root/src/PiWeft.Desktop/Resources/icons"
base_url="${LUCIDE_ICON_BASE_URL:-https://raw.githubusercontent.com/lucide-icons/lucide/main/icons}"

usage() {
	cat <<'EOF'
用法：tools/fetch-lucide-icons.sh <图标名> [更多图标名…]
      tools/fetch-lucide-icons.sh --list

图标名用 Lucide 的官方名字（小写、连字符分隔），例如 file-text、package、palette。
可用 LUCIDE_ICON_BASE_URL 覆盖下载源（默认取 lucide-icons/lucide 的 main 分支）。
EOF
}

if [ $# -eq 0 ]; then
	usage >&2
	exit 2
fi

if [ "$1" = "-h" ] || [ "$1" = "--help" ]; then
	usage
	exit 0
fi

if [ "$1" = "--list" ]; then
	find "$icons_dir" -maxdepth 1 -name '*.svg' -printf '%f\n' | sed 's/\.svg$//' | sort
	exit 0
fi

for name in "$@"; do
	if ! printf '%s' "$name" | grep -Eq '^[a-z0-9]+(-[a-z0-9]+)*$'; then
		echo "pi: 无效的图标名「$name」——只允许小写字母、数字与连字符" >&2
		exit 2
	fi

	target="$icons_dir/$name.svg"
	temporary="$(mktemp)"
	trap 'rm -f "$temporary"' EXIT

	if ! curl -fsSL "$base_url/$name.svg" -o "$temporary"; then
		echo "pi: 下载失败「$name」——检查图标名是否正确，或网络是否可用" >&2
		exit 1
	fi

	if ! grep -q 'stroke="currentColor"' "$temporary"; then
		echo "pi: 警告「$name.svg」没有 stroke=\"currentColor\"，图标可能不跟随主题换色" >&2
	fi

	if ! grep -q 'stroke-width="2"' "$temporary"; then
		echo "pi: 警告「$name.svg」没有 stroke-width=\"2\"，笔画粗细会与其它图标不一致" >&2
	fi

	mv "$temporary" "$target"
	trap - EXIT
	echo "已写入 $target"
done

echo
echo "提示：图标采用 Lucide 的 ISC 许可；派生自 Feather（MIT）的名字已列在 LICENSE-Lucide.txt。"
