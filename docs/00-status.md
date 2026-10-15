# PiWeft 状态

PiWeft 将 [pi](https://github.com/earendil-works/pi) 的核心移植为 C# / .NET 10，提供 NativeAOT CLI `pi` 和 MewUI 桌面工作台 `piweft-desktop`。会话文件与 pi、PiDeck 互通，但不等于完整移植 pi 或 PiDeck 的所有功能。

构建、运行与打包见 [README](../README.md)；[英文摘要](00-status.en.md) 保留同一兼容边界。

## 架构与约束

| 项目 | 职责 |
|---|---|
| `src/PiWeft.Ai` | 消息、JSON、模型目录、费用、认证、provider 流式适配 |
| `src/PiWeft.Agent` | 代理循环、串行/并行工具、steering、follow-up、事件与中断 |
| `src/PiWeft.CodingAgent` | 会话、内置工具、技能、提示词、设置、压缩、分支摘要、重试与统计 |
| `src/PiWeft.Mcp` | stdio / streamable HTTP、分页、工具、资源、提示与通知 |
| `src/PiWeft.Cli` | 参数与环境装配、text / print / JSON / RPC、认证、HTML 导出、宿主日志 |
| `src/PiWeft.Desktop` | 多项目、多会话工作台，进程内驱动 `CodingAgentSession` |
| `tools/` | NativeAOT 冒烟、MCP fixture、会话查询基准、桌面截图脚本 |

- 使用 .NET 10、`LangVersion=preview` 和制表符缩进；中文为文档、界面文案和注释的第一方语言。
- JSON 使用 `System.Text.Json` 源生成与手写转换器，不依赖 Newtonsoft.Json 或供应商 SDK。保留 trim/AOT 分析器。
- 核心库无第三方运行时包；宿主使用 ZLogger，桌面使用 MewUI 0.22.1、Markdown 和 SVG 扩展。
- 模型目录固定在 `src/PiWeft.Ai/Resources/models.json`，自定义端点由 `models.json` 注册。
- 不实现 JS 扩展宿主、codemode、mermaid、KaTeX、托盘、桌面通知、自动更新、崩溃上报、内嵌浏览器或终端。

## pi 与 PiDeck 兼容

- CLI 产物与程序集名保持 `pi`；`--version` 报告 RPC 契约版本 **1.2.0**，不是桌面构建号。
- 保留 `PI_CODING_AGENT_DIR`、`PI_LOG_LEVEL`、`~/.pi/agent`、项目 `.pi`、配置文件名及 version 3 JSONL 会话格式。
- 系统提示词只在运行时构造，不能作为历史消息写入会话。工具热更新保留提示章节边界；未知会话字段原样保留。内置工具的短 snippet 与 guidelines 在所有入口（CLI/RPC/桌面）按名称重新挂回，与 pi/PiDeck 提示词一致。
- `--system-prompt` 取代默认 preamble，`--append-system-prompt` 追加为 `addendum` 章节，`--no-context-files` 停用 AGENTS.md / CLAUDE.md 发现；三者都接受字面文本或文件路径。
- `--export <session> [output]` 导出 HTML 后退出（只读，不改会话文件）；`--models <patterns>` 限定 Ctrl+P 模型轮换范围。
- CLI `@file` 参数展开为 `<file>` 文本块，图片作为内容块随首条提示发送；print / JSON / 交互模式还会把重定向的 stdin 合入首条提示。RPC 模式明确拒绝 `@file`。
- 技能命令 `/skill:name` 与提示词模板在 `CodingAgentSession` 内统一展开，CLI、RPC 与桌面共享同一语义；RPC 空闲 `prompt` 保留 `images` 内容块。
- CLI 启动记录思考级别元数据，与 pi 一致。桌面只读浏览不改文件字节或 mtime，开始对话或主动改级别时再记录。
- 凭据按显式选项、存储凭据、环境变量解析；每次请求按目标供应商选择凭据，压缩与分支摘要共用该路径。
- 请求超时只覆盖等待响应头，不切断正常长流。输出上限截断在 print 模式输出部分文本并以 1 退出。
- 可恢复错误按 `settings.retry` 分类并指数退避；失败尝试保留在文件里，通过 `context_edit` 从模型上下文排除，不重复提交用户消息。
- PiDeck 传入的 `--no-themes`、`--offline`、`--no-extensions`、`-e <path>` 等兼容参数保留。接受参数不代表实现了主题或扩展。
- 项目信任：`<agentDir>/trust.json` 与 pi 同构（规范化路径 → true/false），从工作目录逐级向上取第一个决策；`-a/--approve`、`-na/--no-approve` 覆盖；全局 `settings.defaultProjectTrust` 为兜底。为 false 时不合并项目设置、不连接项目 `.pi/mcp.json`，也不加载项目级技能与提示词模板（用户级目录仍加载，上下文文件不受影响）。本端口默认 `always`（pi 默认 `ask`），以免破坏既有行为；PiDeck 传的 `--no-approve` 会按预期收紧。当默认值为 `ask` 且存在受保护项目资源时，CLI 交互模式与桌面在启动时弹卡片询问「信任（记住）/仅本次会话信任/不信任（记住）」，选择写入 `trust.json`；RPC 与 `--print` 无法弹框，`ask` 按不受信处理（失败关闭）。

### RPC

| 命令组 | 支持范围 |
|---|---|
| 对话与队列 | `prompt`（图片、`streamingBehavior`）、`steer`、`follow_up`、`abort`、`clear_queue`、队列模式 |
| 会话与查询 | `new_session`、`get_state`、`get_messages`、`get_session_stats`、`get_entries`（`since` 游标）、`get_tree`、`get_last_assistant_text`、命名 |
| 导航与派生 | `get_fork_messages`、`fork`、`clone`、`switch_session` |
| 模型与思考 | 获取、设置及轮换模型/思考级别；保留完整模型声明字段 |
| 计划模式与信任 | `set_plan_mode` 开关只读计划模式；`get_state` 回报 `planMode` 与 `projectTrusted` |
| 管理 | `compact`、自动压缩、自动重试、中断重试、`bash`、`abort_bash`、`get_commands`、`export_html` |
| 连接与目录 | `test_model` 用当前 provider 的完整请求路径发 1-token 探测，返回 `ok`/`latencyMs`/`usage`/`error`；`fetch_remote_models` 按 API 拉取远程模型 id 列表 |
| MCP 运行时 | `list_mcp` 回报每个配置服务器的连接状态/工具数/错误；`reconnect_mcp` 释放旧连接后按当前 `mcp.json` 重建并热替换当前会话的工具集 |
| 用量统计 | `get_usage_stats` 单遍扫描会话 JSONL，返回总数与按日期/模型/项目的聚合及 `heatmap`（`weeks`/`since`/`project` 可选） |
| 扩展 UI | `ask_question` 发出 `extension_ui_request`（`method: "input"`，PiDeck 批量信封），`extension_ui_response` 唤醒等待中的提问；其它扩展 UI 方法不发出 |

`get_state` 在 pi 字段之外补充 `cwd`、`tools`、`entryCount` 和 `errorMessage`；消息计数不含运行时系统提示词。
`get_session_stats` 拆分输入、输出、缓存读取和写入，并给出上下文窗口占比。JSON 写入深度为 65536，以容纳深层会话树。

让 PiDeck 使用本端口：将 `customPiPath` 指向发布的 `pi` 二进制，或执行 `dotnet .../pi.dll` 的包装脚本。
真实 pi 字段对照和 PiDeck 握手由现有测试验证；PiDeck 完整 UI e2e 仍需单独运行，不能由握手测试推断全部高级能力已覆盖。

### Provider 与认证

实现以下 API：`anthropic-messages`、`openai-completions`、`openai-responses`、`openai-codex-responses`、
`azure-openai-responses`、`google-generative-ai`、`google-vertex`、`bedrock-converse-stream`、
`mistral-conversations`、`pi-messages`。

`pi login` 支持 ChatGPT Codex 设备码授权与令牌刷新；`logout` 保留其它供应商条目。
`pi auth check` 输出 `ready` / `not_ready` / `invalid`，退出码为 0 / 1 / 2；支持供应商、模型、JSON、凭据检查和禁用刷新选项。
`print-api-key` / `print-bearer-token` 只输出相应凭据；`--min-expiry` 仅适用于后者。
`auth.json` 的 API Key 支持明文和 `${VAR}` 模板；命令型凭据（`!` 开头）不执行并明确提示。

### MCP

stdio 与 streamable HTTP 均支持握手、工具/资源/模板/提示分页及调用。
stdio 服务器的子进程 PATH 按 PiDeck 的顺序组装（env PATH → 登录 shell PATH → 包管理器/版本管理器目录 → 平台兜底），命令解析为绝对路径：图形外壳启动（macOS Finder）只有系统 PATH 时仍能找到 Homebrew/nvm 里的 `node`，解析失败给出含搜索路径的错误而不是裸 ENOENT。登录 shell 探测异步执行并按进程缓存，超时或失败只退化到 env PATH。
资源工具过滤 `ui://` / MCP App 内容，保留图片块；其它二进制保存到临时文件，模型侧文本限制为 20 KB 并提供完整文件路径。
stdio 列表变化通知刷新工具并调用 `SetTools`；HTTP 尚未打开独立 SSE 通知流，因此不支持该通知路径。

## 桌面能力

桌面直接复用 CLI 的环境装配与相同会话文件，不启动 RPC 子进程。

- 多项目会话树、元数据与有界正文搜索、置顶、命名、删除到 `.trash`、恢复、并行标签与右键操作。
- 启动目录兜底：从二进制目录启动沿用最近项目；macOS 由 Finder/launchd 启动且 cwd 为文件系统根时退回最近项目或用户主目录，避免把 `/` 当成项目。终端启动与显式 `--cwd` 不做推断。
- 文件浏览器与内置编辑器（`Ctrl+Shift+E`）：按目录导航、跳过 `.git`/依赖/构建产物、带 Git 状态标记；文本读写有大小与二进制判定，保存用时间戳检测外部修改。
- 助手中的每轮修改汇总：自上一条用户消息以来成功的 `edit`/`write` 文件列表显示在回答下方。
- 原生 `todo` 工具与输入区上方的待办条：计划按会话分支持久化为 PiDeck v3 自定义条目（`customType: "pi-deck-todo"`），切换分支时自动恢复；计划全貌只随变更的 toolResult 进入历史，不逐轮注入。
- 原生 `ask_question` 工具：通过 RPC `extension_ui_request` / CLI 交互提示 / 桌面窗口级问询卡片向用户提问；线协议与 PiDeck 批量信封一致，因此 PiDeck 的桌面端也能渲染（select / multi_select / confirm / input / editor）。
- 原生计划模式：输入框 Plan 开关（或 `/plan`、RPC `set_plan_mode`）启用后停用 `edit`/`write` 并附加 `plan_mode` 提示词章节；只读 shell 白名单拦截破坏性命令；PiDeck 的隐藏标记会在下一条无标记消息时自动退出。
- Markdown、语法高亮、代码复制、独立思考折叠区、工具状态/耗时/patch、编辑重发、重试、分支树和摘要跳转。
- 可搜索模型选择器、中文思考级别、斜杠命令、命令面板、可自定义快捷键、文件拖入与图片附件。
- 供应商/模型、API Key、Agent 设置、MCP 和 JSON 配置管理；全局/项目作用域、未知字段保留、原子写入和冲突检查。
- 余额/积分查询支持内置端点及自定义解析、New API、Cookie、独立令牌，入口在会话统计弹窗（刷新、查询时间、配置入口与错误状态），不包含完整订阅窗口查询。
- 跨会话用量统计：单遍扫描会话 JSONL，按日期、供应商+模型、项目聚合令牌与费用，并提供 26 周×7 天热力图；RPC `get_usage_stats` 与桌面「用量统计」标签（命令面板入口）共用。
- 模型连接测试：输入框工具栏的「测试连接」按钮发一次 1-token 探测，验证 Base URL、凭据、模型 ID 与自定义头，并显示延迟；RPC 提供 `test_model`。
- 远程模型目录：配置页每个供应商的「拉取远程模型」按钮按 API 探测 `GET /models`（OpenAI 兼容 / Anthropic `/v1/models` / Google `/models`），把缺失的模型以保守默认值并入配置草稿；RPC 提供 `fetch_remote_models`。
- 实时会话统计、首 token 延迟、速度、费用、缓存命中率与手动压缩；统计口径与性能基准见专项文档。
- 深色、浅色、跟随系统；主题切换不重建整棵界面。内嵌 HarmonyOS Sans SC、Maple Mono CN 和 Lucide 图标，许可随包。
- Git 状态/diff、暂存与取消暂存、提交、分支切换/新建，编辑器与浏览器跳转，自包含 HTML 导出，窗口尺寸/位置与最近项目/模型持久化。
- 工作区检查点与回滚：快照写入 `refs/pi-checkpoints/<id>` 低层引用（不产生分支或提交），Git 面板可手动创建/恢复；发送提示前每仓库每分钟自动打一个（仅在有改动时），默认保留 50 个。
- 命令 Dock（`Ctrl+Shift+D`）：在项目目录里运行单条 shell 命令并流式显示输出，支持取消与有界输出。**不是完整 PTY**：无交互式提示、全屏 TUI 或常驻会话。
- 按工作目录单实例；后台有界日志；`--self-check`、`--theme-smoke` 及截图脚本用于验证。

详细约束集中在以下文档，不再重复维护逐轮开发记录：

- [桌面对齐与限制](02-desktop-parity.md)
- [配置管理与安全边界](03-pideck-configuration-parity.md)
- [会话统计与性能](04-session-statistics-performance.md)

## 验证入口

```sh
dotnet test PiWeft.slnx
dotnet run --project tools/PiWeft.SessionBench
dotnet publish tools/PiWeft.AotSmoke -c Release -r linux-x64
dotnet publish src/PiWeft.Cli -c Release -r linux-x64
dotnet publish src/PiWeft.Desktop -c Release -r linux-x64
```

GUI 环境可用 `xvfb-run`。真实二进制对照测试要求 PATH 中有 TypeScript `pi`；没有安装时测试失败，不静默跳过。
测试覆盖 provider 请求/流事件、工具与队列、会话无损读写、压缩/重试、认证、RPC、MCP、桌面逻辑和控件。
真实数据互操作测试还受本机会话/技能数据是否存在影响；provider 使用打桩服务，不能替代真实供应商联网验证。

Linux 有 NativeAOT 与 GUI 验证路径。Windows/macOS 使用条件平台包与原生打包 runner，但原生窗口、shell/路径语义及发布包仍需实机回归。
