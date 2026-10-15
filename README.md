# PiWeft

PiWeft is a C# / .NET 10 coding agent with a NativeAOT CLI (`pi`) and a MewUI desktop workspace (`piweft-desktop`). Session files remain compatible with pi and PiDeck. See [project status](docs/00-status.md) for the implemented API surface and limitations.

The CLI project is `PiWeft.Cli`, and its build output is `pi` / `pi.exe` (with `pi.dll` as the managed assembly). The `pi` artifact keeps its arguments, the RPC JSONL protocol and the pi compatibility version. The `PI_CODING_AGENT_DIR` and `PI_LOG_LEVEL` environment variables, `~/.pi/agent` and project `.pi` directories, configuration filenames and session format remain unchanged.

## Build and Run

Install the .NET 10 SDK, then run from the repository root:

```sh
dotnet restore PiWeft.slnx
dotnet build PiWeft.slnx
dotnet run --project src/PiWeft.Desktop -- --cwd /path/to/project
dotnet run --project src/PiWeft.Cli -- --help
dotnet test PiWeft.slnx
```

Linux desktop rendering requires an X11 session (or XWayland), OpenGL/EGL, and fontconfig. On Ubuntu, install `libx11-6 libxcursor1 libxrandr2 libxi6 libgl1 libegl1 libfontconfig1`. For headless GUI tests, install `xvfb` and use `xvfb-run -a dotnet test PiWeft.slnx`.

## Models and Image Preview

The model picker groups available chat models by provider, marks the current model, supports full-catalog search and refresh, and virtualizes rows instead of creating a control for every model. Availability means the API is implemented and local credentials can be resolved from `models.json`, `auth.json`, environment variables, or the supported Codex OAuth / AWS / Vertex paths. It does not make network requests to prove account access. Unconfigured providers and unavailable startup fallback models are hidden; `pi --list-models` remains a full catalog query. Model endpoint changes still apply to new sessions; refresh in an existing session rechecks authentication against its startup environment.

Click an image in user or assistant messages, expanded tool results, or an attachment filename to view it in a window-sized overlay. Escape, the close button, and the backdrop close the preview. Attachment removal has its own button. Thumbnails and previews share image sources through a weak cache, and closing a preview clears its image reference.

## Attachments and Balance Queries

Drop files, directories, local file URLs, or textual paths into the composer. Non-image files and directories become quoted path references at the caret. Images become removable attachments and are sent as image content blocks, including image-only messages. PNG, JPEG, GIF, WebP, and BMP files are supported; native PNG/JPEG/GIF/WebP byte payloads are also accepted when offered by the source application. Image attachment limits are 10 MiB per image, 20 MiB total, and 10 images per draft. Text-only models keep the draft and prompt for a vision-capable model instead of sending images.

The session statistics panel below the composer hosts **余额查询** for the current provider, with refresh, last query time, a configuration entry and error state. It automatically recognizes DeepSeek, OpenRouter per-key credits, Moonshot, and SiliconFlow, with a generic `/usage` fallback. Unsupported endpoints can be configured in the same panel using a request path or URL, numeric field paths such as `balance_infos[0].total_balance`, a currency field, total/used fields, and a divisor. **New API** supplies `/api/user/self` and a quota divisor of 500000; enter the account access token and user ID. Cookie authentication and a separate query token are supported.

Query configuration is saved atomically to `<agentDir>/usage-queries.json`, independently of model inference settings. API keys are reused from `models.json`, `auth.json`, or the provider environment variable. Cookies and query tokens remain local, so do not commit this file. Queries use a 15-second timeout and a 1 MiB response limit. Redirects are disabled. HTTPS is required except for loopback HTTP; cross-origin query URLs require their own explicit credentials and never inherit model headers. Only parsed numbers and short units are displayed, not raw response bodies. This is a balance/credits implementation, not the complete PiDeck subscription-window query system.

## Reading and Linux Selection

The appearance page provides live 14–22 px chat/composer text and compact/comfortable/wide reading columns. Statistics are consolidated below the composer; the panel opens with a readable overview and keeps all counters on its details tab.

The message timeline virtualizes variable-height rows: only the viewport creates Markdown, text and image controls, and recycled rows release their content while retaining expansion state. Branch history is built on demand. Theme switching recolors messages in place and preserves Markdown selections, tool expansion and drafts. The sidebar and empty-session surface use a lighter PiDeck-style layout; narrow windows reduce composer height to keep replies readable. See [desktop rendering performance](docs/05-desktop-rendering-performance.md) for measurements and validation.

On Linux, selecting normal text publishes the independent PRIMARY selection. Middle-click in an editable text box pastes at the clicked position without sending; Ctrl+C/Ctrl+V still use CLIPBOARD. Password fields never publish their contents. X11/XWayland requires `xclip` (preferred) or `xsel` on PATH. The clipboard helper is loaded on demand, with one owned selection process, bounded reads and no polling. Missing helpers leave normal clipboard behavior available. See [desktop behavior and validation](docs/02-desktop-parity.md) for limits and the isolated PRIMARY smoke test.

## Packaging

NativeAOT publishing requires native build tools: Visual Studio C++ on Windows, Xcode command-line tools on macOS, and `clang` plus `zlib1g-dev` on Ubuntu. Build on the target OS and architecture; NativeAOT does not support arbitrary cross-OS builds.

With PowerShell 7:

```powershell
./scripts/package.ps1 -Rid linux-x64 -Version 0.1.0
```

Use `win-x64`, `linux-x64`, `linux-arm64`, `osx-x64`, or `osx-arm64` as appropriate. The script also accepts `win-arm64` for a local build with a suitable ARM64 toolchain, but it is not included in CI. Choose a fresh `-Output` directory when repeating a build with the same version/RID. The script publishes both executables and creates an archive plus a SHA-256 checksum under `artifacts/packages/`, including font/icon licenses. Windows uses ZIP; Linux/macOS use TAR.GZ. macOS includes a `PiWeft Desktop.app` bundle with ad-hoc signing. Windows signing and Apple Developer ID signing/notarization are not configured; operating-system security warnings may occur.

## GitHub Actions

[`.github/workflows/build.yml`](.github/workflows/build.yml) runs tests on Ubuntu with Xvfb, then builds NativeAOT packages on five native runners:

| Platform | Architecture | Runner |
| --- | --- | --- |
| Windows | x64 | windows-2022 |
| Linux | x64 | ubuntu-24.04 |
| Linux | ARM64 | ubuntu-24.04-arm |
| macOS | x64 | macos-15-intel |
| macOS | ARM64 | macos-15 |

Pushes to `main`/`master`, pull requests, and manual dispatch build downloadable artifacts. A `v<version>` tag also creates a **draft** GitHub release with the packages and checksums. The workflow never publishes a public release automatically. ARM runner availability may depend on repository visibility and your GitHub plan.
