# PiWeft

PiWeft 是 C# / .NET 10 的编码智能体，含 NativeAOT CLI `pi` 与 MewUI 桌面工作台 `piweft-desktop`。会话文件与 pi 兼容。

- [项目状态](docs/00-status.md) · [pi / PiDeck 兼容](docs/01-parity.md) · [性能](docs/02-performance.md) · [codemode](docs/03-codemode.md) · [Markdown](docs/04-markdown.md) · [视觉契约](DESIGN.md)
- 沿用 pi 的参数、RPC JSONL 协议与环境变量：`PI_CODING_AGENT_DIR`、`PI_LOG_LEVEL`、`~/.pi/agent`、`.pi`、配置文件名和 v3 JSONL 会话格式不变。

## 构建与运行

```sh
git submodule update --init --recursive
dotnet restore PiWeft.slnx
dotnet build PiWeft.slnx
dotnet run --project src/PiWeft.Desktop -- --cwd /path/to/project
dotnet run --project src/PiWeft.Cli -- --help
dotnet test PiWeft.slnx
```

Linux 桌面需要 X11/XWayland、OpenGL/EGL 与 fontconfig（Ubuntu 包：`libx11-6 libxcursor1 libxrandr2 libxi6 libgl1 libegl1 libfontconfig1`）。无头测试用 `xvfb-run -a dotnet test PiWeft.slnx`。

MewUI 只从 `external/MewUI` 源码子模块构建。更新上游：

```sh
./scripts/mewui-source.sh          # Windows: ./scripts/mewui-source.ps1
git add external/MewUI
```

更新后跑全量桌面测试、NativeAOT 发布与 `--ui-smoke stream`。`external/Directory.*.props` 隔离 PiWeft 的包管理与分析器设置。

## 打包

NativeAOT 需在目标平台构建（Windows: VS C++；macOS: Xcode CLT；Ubuntu: `clang zlib1g-dev`）。

```powershell
./scripts/package.ps1 -Rid linux-x64 -Version 0.1.0
```

RID：`win-x64`、`win-arm64`（仅本地）、`linux-x64`、`linux-arm64`、`osx-x64`、`osx-arm64`。输出 `artifacts/packages/`：两个可执行文件、字体/图标许可证、ZIP（Windows）或 TAR.GZ（Linux/macOS，macOS 含 ad-hoc 签名的 `.app`）及 SHA-256。重复构建请换 `-Output`。未做 Windows/Apple 签名与公证。

## CI

| 平台 | 架构 | 运行器 |
| --- | --- | --- |
| Windows | x64 | windows-2022 |
| Linux | x64 / ARM64 | ubuntu-24.04 / ubuntu-24.04-arm |
| macOS | x64 / ARM64 | macos-15-intel / macos-15 |
