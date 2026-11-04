# external 源码依赖

`external/MewUI/` 是 git submodule，是 PiWeft 唯一的 MewUI 构建来源（不再使用 NuGet 包）。

```bash
# 首次检出
git submodule update --init --recursive

# 更新到 upstream main（或 MEWUI_REF 指定的分支/标签）
scripts/mewui-source.sh
git add external/MewUI

dotnet build PiWeft.slnx
```

`external/Directory.Build.props` 与 `external/Directory.Packages.props` 只负责
隔离 PiWeft 根目录的 `TargetFramework`、分析器和中央包管理设置，不要删除。

源码是唯一支持的 MewUI 构建方式；发布流程请使用支持递归 submodule 的 checkout（CI 已配置）。
