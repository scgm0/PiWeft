# PiWeft

PiWeft 是一个基于 C# / .NET 10 的编码智能体（coding agent），带有 NativeAOT CLI（`pi`）和 MewUI 桌面工作区（`piweft-desktop`）。会话文件与 pi 保持兼容。当前实现见[项目状态](docs/00-status.md)，兼容边界与差距见[pi / PiDeck 兼容](docs/01-parity.md)，性能基准见[性能与基准](docs/02-performance.md)，codemode/Jint 见[codemode 与 Jint](docs/03-codemode.md)，增量 Markdown 控件见[PiWeft.Markdown](docs/04-markdown.md)。

CLI 项目为 `PiWeft.Cli`，其构建输出为 `pi` / `pi.exe`（带有作为托管程序集的 `pi.dll`）。`pi` 制品保留其参数、RPC JSONL 协议以及 pi 兼容版本。环境变量 `PI_CODING_AGENT_DIR` 和 `PI_LOG_LEVEL`、`~/.pi/agent` 和项目 `.pi` 目录、配置文件名以及会话格式保持不变。

## 构建与运行

安装 .NET 10 SDK，然后从仓库根目录运行：

```sh
dotnet restore PiWeft.slnx
dotnet build PiWeft.slnx
dotnet run --project src/PiWeft.Desktop -- --cwd /path/to/project
dotnet run --project src/PiWeft.Cli -- --help
dotnet test PiWeft.slnx
```

Linux 桌面渲染需要 X11 会话（或 XWayland）、OpenGL/EGL 以及 fontconfig。在 Ubuntu 上，请安装 `libx11-6 libxcursor1 libxrandr2 libxi6 libgl1 libegl1 libfontconfig1`。对于无头 GUI 测试，请安装 `xvfb` 并使用 `xvfb-run -a dotnet test PiWeft.slnx`。

## 打包

NativeAOT 发布需要本地构建工具：Windows 上的 Visual Studio C++、macOS 上的 Xcode 命令行工具以及 Ubuntu 上的 `clang` 和 `zlib1g-dev`。请在目标操作系统和架构上进行构建；NativeAOT 不支持任意跨操作系统构建。

使用 PowerShell 7：

```powershell
./scripts/package.ps1 -Rid linux-x64 -Version 0.1.0
```

视情况使用 `win-x64`、`linux-x64`、`linux-arm64`、`osx-x64` 或 `osx-arm64`。该脚本还接受用于配合适当 ARM64 工具链进行本地构建的 `win-arm64`，但它不包含在 CI 中。当使用相同版本/RID 重复构建时，请选择一个新的 `-Output` 目录。该脚本会发布两个可执行文件，并在 `artifacts/packages/` 下创建一个归档文件以及一个 SHA-256 校验和，其中包含字体/图标许可证。Windows 使用 ZIP；Linux/macOS 使用 TAR.GZ。macOS 包含一个带有自签名（ad-hoc signing）的 `PiWeft Desktop.app` 捆绑包。未配置 Windows 签名和 Apple Developer ID 签名/公证；可能会出现操作系统安全警告。

## GitHub Actions

| 平台 | 架构 | 运行器 |
| --- | --- | --- |
| Windows | x64 | windows-2022 |
| Linux | x64 | ubuntu-24.04 |
| Linux | ARM64 | ubuntu-24.04-arm |
| macOS | x64 | macos-15-intel |
| macOS | ARM64 | macos-15 |
