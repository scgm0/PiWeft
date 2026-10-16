# PiWeft 状态

PiWeft 将 [pi](https://github.com/earendil-works/pi) 的核心移植为 C# / .NET 10，提供 NativeAOT CLI `pi` 和 MewUI 桌面工作台 `piweft-desktop`。会话文件与 pi、PiDeck 互通，但不等于完整移植 pi 或 PiDeck 的所有功能。

构建、运行与打包见 [README](../README.md)。

## 架构与约束

| 项目 | 职责 |
|---|---|
| `src/PiWeft.Ai` | 消息、JSON、模型目录、费用、认证、provider 流式适配 |
| `src/PiWeft.Agent` | 代理循环、串行/并行工具、steering、follow-up、事件与中断 |
| `src/PiWeft.CodingAgent` | 会话、内置工具、资源发现（技能/提示词/主题/包）、设置、压缩、分支摘要、重试与统计 |
| `src/PiWeft.Mcp` | stdio / streamable HTTP、分页、工具、资源、提示与通知 |
| `src/PiWeft.Cli` | 参数与环境装配、text / print / JSON / RPC、认证、HTML 导出、宿主日志 |
| `src/PiWeft.Desktop` | 多项目、多会话工作台，进程内驱动 `CodingAgentSession` |
| `tools/` | NativeAOT 冒烟、MCP fixture、会话/桌面基准、桌面截图脚本、Lucide 图标获取 |

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
- `pi install <source>`、`pi list`、`pi remove <source>`、`pi update [source]` 管理 pi 包：npm 装到 `<agentDir>/npm/node_modules`、git 克隆到 `<agentDir>/git`、本地路径只登记不复制；`--local/-l` 写项目 `.pi/settings.json`。npm 可执行文件可用 `settings.npmCommand` 覆盖。
- `pi mcp list|add|remove|login|logout` 管理 MCP 服务器与它们的 OAuth 令牌：配置写 `<agentDir>/mcp.json`（`--local` 改项目 `.pi/mcp.json`），令牌写 `<agentDir>/mcp-oauth.json`；`login` 走授权码 + PKCE 的回环流程，没有 `clientId` 时按需动态注册。写入是保留式的：保留文件里的其它键、注释与尾逗号，解析失败时拒绝覆盖并报错。
- CLI `@file` 参数展开为 `<file>` 文本块，图片作为内容块随首条提示发送；print / JSON 模式还会把重定向的 stdin 合入首条提示（交互模式不读管道 stdin）。RPC 模式明确拒绝 `@file`。
- 技能命令 `/skill:name` 与提示词模板在 `CodingAgentSession` 内统一展开，CLI、RPC 与桌面共享同一语义；RPC 空闲 `prompt` 保留 `images` 内容块。交互模式另有 pi 的 `/copy`（最近助手回复进系统剪贴板）、`/import <路径>`（把另一会话当前分支的消息并入本会话并重建上下文）与 `/bug`（反馈入口 + 会话路径）；pi 的 `/share`、`/llama`、`/settings` 未实现。
- CLI 启动记录思考级别元数据，与 pi 一致。桌面只读浏览不改文件字节或 mtime，开始对话或主动改级别时再记录。
- 凭据按显式选项、存储凭据、环境变量解析；每次请求按目标供应商选择凭据，压缩与分支摘要共用该路径。
- 请求超时只覆盖等待响应头，不切断正常长流。输出上限截断在 print 模式输出部分文本并以 1 退出。
- 可恢复错误按 `settings.retry` 分类并指数退避；失败尝试保留在文件里，通过 `context_edit` 从模型上下文排除，不重复提交用户消息。
- PiDeck 传入的 `--no-themes`、`--offline`、`--no-extensions`、`-e <path>` 等兼容参数保留。接受参数不代表实现了 JS 扩展；主题由桌面按 pi 主题 JSON 解析。
- 项目信任：`<agentDir>/trust.json` 与 pi 同构（规范化路径 → true/false），从工作目录逐级向上取第一个决策；`-a/--approve`、`-na/--no-approve` 覆盖；全局 `settings.defaultProjectTrust` 为兜底，**默认 `ask`（与 pi 一致）**。为 false 时不合并项目设置、不连接项目 `.pi/mcp.json`、不读取项目级 `.pi/models.json`（它能改写端点与请求头），也不加载项目级技能与提示词模板（用户级目录仍加载，上下文文件不受影响）。当默认值为 `ask` 且存在受保护项目资源时，CLI 交互模式与桌面在启动时弹卡片询问「信任（记住）/仅本次会话信任/不信任（记住）」，选择写入 `trust.json`；RPC 与 `--print` 无法弹框，`ask` 按不受信处理（失败关闭）。

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
| 扩展 UI | `ask_question` 发出 `extension_ui_request`（`method: "input"`，PiDeck 批量信封），`extension_ui_response` 唤醒等待中的提问；pi 的对话框方法 `select`/`confirm`/`editor` 与 fire-and-forget 的 `notify` 已按 pi 线协议实现（含 agent 侧 `timeout` 自动解析），`setStatus`/`setWidget`/`setTitle`/`set_editor_text` 尚未发出 |
| 资源 | `get_resources` 返回已解析的技能/提示词/主题/包与诊断；`reload_resources` 重新解析并刷新系统提示词里的技能章节 |

`get_state` 在 pi 字段之外补充 `cwd`、`tools`、`entryCount` 和 `errorMessage`；消息计数不含运行时系统提示词。
`get_session_stats` 拆分输入、输出、缓存读取和写入，并给出上下文窗口占比。JSON 写入深度为 65536，以容纳深层会话树。

让 PiDeck 使用本端口：将 `customPiPath` 指向发布的 `pi` 二进制，或执行 `dotnet .../pi.dll` 的包装脚本。
真实 pi 字段对照和 PiDeck 握手由现有测试验证；PiDeck 完整 UI e2e 仍需单独运行，不能由握手测试推断全部高级能力已覆盖。

### Provider 与认证

实现以下 API：`anthropic-messages`、`openai-completions`、`openai-responses`、`openai-codex-responses`、
`azure-openai-responses`、`google-generative-ai`、`google-vertex`、`bedrock-converse-stream`、
`mistral-conversations`、`pi-messages`。

`pi login` 支持 ChatGPT Codex 设备码授权与令牌刷新；`logout` 保留其它供应商条目。
`pi auth check` 输出 `ready` / `not_ready` / `invalid`，退出码为 0 / 1 / 2（参数异常为 2）；`invalid` 表示凭据存在但不可用（命令型凭据执行失败、OAuth 刷新失败）。支持供应商、模型、JSON、凭据检查和禁用刷新选项。
`print-api-key` / `print-bearer-token` 只输出相应凭据；`--min-expiry` 仅适用于后者。
`auth.json` 的 API Key 支持明文和 `${VAR}` 模板；命令型凭据（`!` 开头）在取值时执行命令取 stdout（10 秒超时、64 KiB 上限，与 pi 一致不缓存），失败时回落到环境变量。写入是原子的，Unix 下仅用户可读写；文件无法解析时拒绝覆盖，避免抹掉其它供应商的凭据。

### MCP

stdio 与 streamable HTTP 均支持握手、工具/资源/模板/提示分页及调用。
stdio 服务器的子进程 PATH 按 PiDeck 的顺序组装（env PATH → 登录 shell PATH → 包管理器/版本管理器目录 → 平台兜底），命令解析为绝对路径：图形外壳启动（macOS Finder）只有系统 PATH 时仍能找到 Homebrew/nvm 里的 `node`，解析失败给出含搜索路径的错误而不是裸 ENOENT。登录 shell 探测异步执行并按进程缓存，超时或失败只退化到 env PATH。
资源工具过滤 `ui://` / MCP App 内容，保留图片块；其它二进制保存到临时文件，模型侧文本限制为 20 KB 并提供完整文件路径。
HTTP 传输下通知流里带 id 的服务端请求（如 `ping`）会反向 POST 应答：`ping` 回空结果，其余方法回 -32601；应答失败静默丢弃，不影响通知流。声明 `auth: oauth` 的服务器配置 `clientId + clientSecret` 时走客户端凭据授权（RFC 6749 §4.4）：无需浏览器与动态注册，`pi mcp login` 直接取令牌，运行时到期自动重新授权；仅配置 `clientId` 时仍走授权码 + PKCE 回环流程，没有 `clientId` 时按需动态注册，回环回调容忍浏览器的杂散连接。令牌单独保存在 `<agentDir>/mcp-oauth.json`，与 provider 凭据的 `auth.json` 分开；请求 401 时刷新一次并重放同一请求。stdio 列表变化通知刷新工具并调用 `SetTools`；HTTP 通过独立 SSE 通知流接收同一路径的通知，服务器返回 400 / 404 / 405 / 406 / 204 时静默降级为请求-响应模式。通知流对坏帧只跳过、流被关闭或失败后指数退避重连（上限 30 秒），收到 401 时主动刷新令牌；握手失败会杀掉已拉起的 stdio 子进程并释放自有 `HttpClient`。

## 桌面能力

桌面直接复用 CLI 的环境装配与相同会话文件，不启动 RPC 子进程。

- 多项目、多会话工作台：会话树、元数据与有界正文搜索、置顶/命名/删除到 `.trash`/恢复、并行标签；文件浏览器与内置编辑器（`Ctrl+Shift+E`）；助手的每轮修改汇总。
- 原生 `todo` / `ask_question` / 计划模式：todo 按会话分支持久化为 PiDeck v3 自定义条目（`customType: "pi-deck-todo"`），切换分支自动恢复；`ask_question` 的线协议与 PiDeck 批量信封一致；计划模式（输入框 Plan 开关或 `/plan`）停用 `edit`/`write`、附加 `plan_mode` 章节并用只读 shell 白名单拦截破坏性命令。
- 资源与主题：按 pi 约定发现技能/提示词模板/主题/pi 包，优先级为 项目 settings 声明(0) > 项目自动发现(1) > 用户 settings 声明(2) > 用户目录自动发现(3) > 包(4)，同名资源先发现者胜出，项目级总能覆盖用户级；坏资源只记一条诊断后跳过，不阻断会话。主题目录文件监视热重载（防抖 400 ms，项目主题只在受信任时监视）；`/reload` 与 RPC `reload_resources` 重新解析而不重建会话。
- 模型连接测试与远程模型目录：1-token 探测（RPC `test_model`）与按 API 拉取 `GET /models`（RPC `fetch_remote_models`），入口在配置页与输入框工具栏。
- Git 状态/diff/写操作、检查点与回滚、命令 Dock、跨会话用量统计与余额查询、实时会话统计、深浅主题与内嵌字体/图标。

界面功能对照、交互细节与性能约束见 [桌面对齐与限制](02-desktop-parity.md)，渲染基准见 [桌面渲染性能](05-desktop-rendering-performance.md)，统计口径见 [会话统计与性能](04-session-statistics-performance.md)。

详细约束集中在以下文档，不再重复维护逐轮开发记录：

- [桌面对齐与限制](02-desktop-parity.md)
- [配置管理与安全边界](03-pideck-configuration-parity.md)
- [会话统计与性能](04-session-statistics-performance.md)
- [桌面渲染性能](05-desktop-rendering-performance.md)
- [功能缺口与 Jint 评估](06-feature-gap-and-jint-assessment.md)

## 验证入口

```sh
dotnet test PiWeft.slnx
dotnet run --project tools/PiWeft.SessionBench
dotnet publish tools/PiWeft.AotSmoke -c Release -r linux-x64
dotnet publish src/PiWeft.Cli -c Release -r linux-x64
dotnet publish src/PiWeft.Desktop -c Release -r linux-x64
```

GUI 环境可用 `xvfb-run`。真实二进制对照测试要求 PATH 中有 TypeScript `pi`；没有安装时测试失败，不静默跳过。
测试基线：`dotnet test PiWeft.slnx` 共 1007 项，全部通过（Linux 桌面端需 `xvfb-run`；2026-10-16 复核）。专项文档引用覆盖率时以本文为准。
测试覆盖 provider 请求/流事件、工具与队列、会话无损读写、压缩/重试、认证、RPC、MCP、桌面逻辑和控件。
真实数据互操作测试还受本机会话/技能数据是否存在影响；provider 使用打桩服务，不能替代真实供应商联网验证。

Linux 有 NativeAOT 与 GUI 验证路径。Windows/macOS 使用条件平台包与原生打包 runner，但原生窗口、shell/路径语义及发布包仍需实机回归。
