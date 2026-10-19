# PiWeft 状态

PiWeft 将 [pi](https://github.com/earendil-works/pi) 的核心移植为 C# / .NET 10，提供 NativeAOT CLI `pi` 和 MewUI 桌面工作台 `piweft-desktop`。会话文件与 pi、PiDeck 互通，但不等于完整移植 pi 或 PiDeck 的所有功能。

构建、运行与打包见 [README](../README.md)。

## 架构与约束

| 项目 | 职责 |
|---|---|
| `src/PiWeft.Ai` | 消息、JSON、模型目录（含虚拟模型）、费用、认证、provider 流式适配、生图/分类 API、Codex WebSocket |
| `src/PiWeft.Agent` | 代理循环、串行/并行工具、steering、follow-up、事件与中断 |
| `src/PiWeft.CodingAgent` | 会话、内置工具、资源发现（技能/提示词/主题/包）、设置、压缩、分支摘要、重试与统计 |
| `src/PiWeft.Mcp` | stdio / streamable HTTP、分页、工具、资源、提示与通知 |
| `src/PiWeft.Cli` | 参数与环境装配、text / print / JSON / RPC、认证、HTML 导出、宿主日志 |
| `src/PiWeft.Desktop` | 多项目、多会话工作台，进程内驱动 `CodingAgentSession` |
| `tools/` | NativeAOT 冒烟、MCP fixture、会话/桌面基准、桌面截图脚本、Lucide 图标获取 |

- 使用 .NET 10、`LangVersion=preview` 和制表符缩进；中文为文档、界面文案和注释的第一方语言。
- JSON 使用 `System.Text.Json` 源生成与手写转换器，不依赖 Newtonsoft.Json 或供应商 SDK。保留 trim/AOT 分析器。
- 核心库无第三方运行时包；宿主使用 ZLogger，桌面使用 MewUI 0.22.1、Markdown 和 SVG 扩展。
- 模型目录以内嵌 `src/PiWeft.Ai/Resources/models.json` 为基线，`<agentDir>/models-store.json` 缓存 pi.dev 远程目录（`pi update --models` 刷新），自定义端点由 `models.json` 注册。`models.json` 的模型条目可写 `"virtual":{"model":"provider/id","thinkingLevel":"high","thinkingLevels":["off","high"]}`，注册为不直接访问 Provider 的虚拟目录项；每次请求由静态目标路由，助手消息仍记录实际物理模型。
- 不实现 JS 扩展宿主、codemode、mermaid、KaTeX、托盘、桌面通知、自动更新、崩溃上报或内嵌浏览器；终端以独立的 PTY 命令 Dock 提供，不内嵌第三方终端组件。

## pi 与 PiDeck 兼容

- CLI 产物与程序集名保持 `pi`；`--version` 报告 RPC 契约版本 **1.3.0**，不是桌面构建号。
- 保留 `PI_CODING_AGENT_DIR`、`PI_LOG_LEVEL`、`~/.pi/agent`、项目 `.pi`、配置文件名及 version 3 JSONL 会话格式。
- 系统提示词只在运行时构造，不能作为历史消息写入会话。工具热更新保留提示章节边界；未知会话字段原样保留。内置工具的短 snippet 与 guidelines 在所有入口（CLI/RPC/桌面）按名称重新挂回，与 pi/PiDeck 提示词一致。
- 默认工具集与 pi 一致：`read`/`bash`/`edit`/`write`，另保留 PiDeck 依赖的原生 `todo` 与 `ask_question`；pi 默认关闭的 `grep`/`find`/`ls` 需要 `--tools`、`settings.defaultTools` 显式开启。`--tools` 的纯名字列表整体替换默认集、`+name`/`-name` 在默认集上增减、`--exclude-tools` 支持通配，模式始终针对完整内置目录展开。
- bash 工具向子进程注入 pi 的会话环境变量（`PI_SESSION_ID`、`PI_SESSION_FILE`、`PI_PROVIDER`、`PI_MODEL`、`PI_REASONING_LEVEL`，执行前先清除继承值），进程启动时设置 `AI_AGENT=pi` 与 `PI_CODING_AGENT=true`；`settings.shellPath`、`settings.shellCommandPrefix` 分别控制 bash 的 shell 与命令前缀。`!`/`!!` 内联命令（交互模式）不走注入，与 pi 一致。
- 生效的 settings 键新增：`shellPath`、`shellCommandPrefix`、`sessionDir`（`--session-dir` 优先）、`defaultTools`、`cacheWarming`、`steeringMode`、`followUpMode`、`modelThinkingLevels`、`enabledModels`、`branchSummary`、`sessionShareDestination`。其余 pi 键继续原样保留、不生效。
- `--system-prompt` 取代默认 preamble，`--append-system-prompt` 追加为 `addendum` 章节，`--no-context-files` 停用 AGENTS.md / CLAUDE.md 发现；三者都接受字面文本或文件路径。
- `--export <session> [output]` 导出 HTML 后退出（只读，不改会话文件）；`--models <patterns>` 限定 Ctrl+P 模型轮换范围。
- `pi install <source>`、`pi list`、`pi remove <source>`、`pi update [source]` 管理 pi 包：npm 装到 `<agentDir>/npm/node_modules`、git 克隆到 `<agentDir>/git`、本地路径只登记不复制；`--local/-l` 写项目 `.pi/settings.json`。npm 可执行文件可用 `settings.npmCommand` 覆盖。
- `pi mcp list|add|remove|login|logout` 管理 MCP 服务器与它们的 OAuth 令牌：`list` 会连接服务器并报告 state/tools/error，启用服务器失败时退出 1；配置写 `<agentDir>/mcp.json`（`--local` 改项目 `.pi/mcp.json`），令牌写 `<agentDir>/mcp-oauth.json`；`login` 走授权码 + PKCE 的回环流程，没有 `clientId` 时按需动态注册。写入是保留式的：保留文件里的其它键、注释与尾逗号，解析失败时拒绝覆盖并报错。
- CLI `@file` 参数展开为 `<file>` 文本块，图片作为内容块随首条提示发送；print / JSON 模式还会把重定向的 stdin 合入首条提示（交互模式不读管道 stdin）。RPC 模式明确拒绝 `@file`。
- 技能命令 `/skill:name` 与提示词模板在 `CodingAgentSession` 内统一展开，CLI、RPC 与桌面共享同一语义；RPC 空闲 `prompt` 保留 `images` 内容块。交互模式实现 pi 的内置斜杠命令面：`/model`、`/thinking`、`/compact`、`/plan`、`/reload`、`/copy`、`/import`、`/bug`、`/new`、`/resume`、`/session`、`/name`、`/tree`、`/fork`、`/clone`、`/export`、`/scoped-models`、`/mcp`、`/trust`、`/settings`、`/login`、`/logout`、`/hotkeys`、`/changelog`、`/help`；`/share`（Radius artifact 或私有 GitHub gist）、`/llama`（router 管理、HF 搜索/下载、自动注册）与 `/bug`（脱敏诊断 zip）已实现。`!command` 把输出并入对话，`!!command` 只执行不入模型上下文。
- 为兼容而接受但不生效的选项（`-e`、`--tui-mode` 等）会在 stderr 提示一次「accepted for compatibility but not implemented」，不再静默降级；`pi config` 列出包来源与可编辑资源键，`pi update --self/--self 别名`、`--extensions`、`--models`、`--all`、`--force` 按 pi 语义解析（自更新交由系统包管理器），`pi mcp add` 支持 pi 形状（`-- <command> [args]`、`--bearer-token-env-var`、`--oauth-client-id/secret`、`--exposure`、`--description`），`pi mcp login --timeout` 限定整个登录过程。
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

`get_state` 在 pi 字段之外补充 `cwd`、`tools`、`entryCount`、`errorMessage` 和 `cacheWarming`（state/reason/nextWarmAt/decision）；消息计数不含运行时系统提示词。
`get_session_stats` 拆分输入、输出、缓存读取和写入，并给出上下文窗口占比。JSON 写入深度为 65536，以容纳深层会话树。

事件族与 pi 对齐（JSON 与 RPC 共用）：每次会话级运行结束发 `agent_settled`（`aborted` 取自最后一次助手消息的中止状态），RPC 的每个 `prompt` 运行结束时也发；`queue_update` 在 steering/follow-up 队列变化（入队、清空、被代理消费）时发全量快照；`thinking_level_changed`、`session_info_changed`、`entry_appended`、`compaction_start`/`compaction_end`（含 `reason`/`result`/`aborted`/`willRetry`/`errorMessage`）齐备；`tool_execution_update`/`tool_execution_end` 携带 `partialResult`/`result`（content + details）与 `durationMs`。

让 PiDeck 使用本端口：将 `customPiPath` 指向发布的 `pi` 二进制，或执行 `dotnet .../pi.dll` 的包装脚本。
真实 pi 字段对照和 PiDeck 握手由现有测试验证；PiDeck 完整 UI e2e 仍需单独运行，不能由握手测试推断全部高级能力已覆盖。

### Provider 与认证

实现以下 API：`anthropic-messages`、`openai-completions`、`openai-responses`、`openai-codex-responses`、
`azure-openai-responses`、`google-generative-ai`、`google-vertex`、`bedrock-converse-stream`、
`mistral-conversations`、`pi-messages`。

`pi login` 支持 ChatGPT Codex、OpenAI ChatGPT、GitHub Copilot、Anthropic、OpenRouter、xAI、Kimi、Meta、Radius 的 OAuth；其它 Provider 走 API key 登录。`logout` 可移除任意 Provider 的凭据并保留其它条目。
`pi auth check` 输出 `ready` / `not_ready` / `invalid`，退出码为 0 / 1 / 2（参数异常为 2）；`invalid` 表示凭据存在但不可用（命令型凭据执行失败、OAuth 刷新失败）。支持供应商、模型、JSON、凭据检查和禁用刷新选项。
`print-api-key` / `print-bearer-token` 只输出相应凭据；`--min-expiry` 仅适用于后者。
`auth.json` 的 API Key 支持明文和 `${VAR}` 模板；命令型凭据（`!` 开头）在取值时执行命令取 stdout（10 秒超时、64 KiB 上限，与 pi 一致不缓存），失败时回落到环境变量。写入是原子的，Unix 下仅用户可读写；文件无法解析时拒绝覆盖，避免抹掉其它供应商的凭据。

### MCP

stdio 与 streamable HTTP 均支持握手、工具/资源/模板/提示分页及调用。
stdio 服务器的子进程 PATH 按 PiDeck 的顺序组装（env PATH → 登录 shell PATH → 包管理器/版本管理器目录 → 平台兜底），命令解析为绝对路径：图形外壳启动（macOS Finder）只有系统 PATH 时仍能找到 Homebrew/nvm 里的 `node`，解析失败给出含搜索路径的错误而不是裸 ENOENT。登录 shell 探测异步执行并按进程缓存，超时或失败只退化到 env PATH。
资源工具过滤 `ui://` / MCP App 内容，保留图片块；其它二进制保存到临时文件，模型侧文本限制为 20 KB 并提供完整文件路径。工具只返回 `structuredContent` 时，模型侧回退为 JSON 文本。
HTTP 传输下通知流里带 id 的服务端请求（如 `ping`）会反向 POST 应答：`ping` 回空结果，其余方法回 -32601；应答失败静默丢弃，不影响通知流。服务器的 `oauth` 对象（兼容旧的 `auth: oauth` + 顶层字段）配置 `clientId + clientSecret` 时走客户端凭据授权（RFC 6749 §4.4）：无需浏览器与动态注册，`pi mcp login` 直接取令牌，运行时到期自动重新授权；仅配置 `clientId` 时仍走授权码 + PKCE 回环流程，没有 `clientId` 时按需动态注册，回环回调容忍浏览器的杂散连接。令牌单独保存在 `<agentDir>/mcp-oauth.json`，与 provider 凭据的 `auth.json` 分开；请求 401 时刷新一次并重放同一请求。stdio 列表变化通知刷新工具并调用 `SetTools`；HTTP 通过独立 SSE 通知流接收同一路径的通知，服务器返回 400 / 404 / 405 / 406 / 204 时静默降级为请求-响应模式。通知流对坏帧只跳过、流被关闭或失败后指数退避重连（上限 30 秒），收到 401 时主动刷新令牌；握手失败会杀掉已拉起的 stdio 子进程并释放自有 `HttpClient`。


### 2026-10-19 本轮补全

- **正确性**：Provider 请求前的消息变换层补齐（跨模型 thinking 丢弃、失败回合跳过、孤儿工具调用补结果、工具调用 id 归一、非视觉模型图片降级）；工具参数 JSON 修复与 `argumentsError`；工具回合中途自动压缩、上下文溢出后的 compact+retry、摘要请求重试事件；`/import` 改为替换并 resume；RPC `abort` 等待空闲，补齐 `toolcall_start`/`tool_execution_update`/`agent_end.willRetry`/`cycle_model.isScoped` 字段；compaction/branch summary 的 `details` 改为 pi 的 `{readFiles, modifiedFiles}`；MCP 工具无 `content` 时回退到 `structuredContent` JSON。
- **配置兼容**：`packages` 对象形式与旧版 `skills` 对象不再导致整份 settings 失效；`defaultTools` 支持项目 `+ / -` 继承与显式空列表；`models.json` 支持 provider 级 `modelOverrides`、`baseUrl`、`compat`、`promptCache`、`samplingParams`、部分价格覆盖和环境/命令型值；`--no-tools` 同时禁用 MCP；`--skill`/`--prompt-template`/`--theme` 实际生效。
- **模型目录**：新增 `<agentDir>/models-store.json` 远程目录缓存，`pi update --models` 刷新 pi.dev 目录；启动时加载缓存；实现账号级 OpenRouter `/models/user`、Radius `/v1/config`、GitHub Copilot `/models` 目录；新增 `defaultModelPerProvider` 与未知模型 fallback；修正 `kimi-coding` 到 1.3.0 的 `openai-completions` + `/coding/v1`。
- **高级运行时**：实现虚拟模型 `pi-virtual`（`models.json` 的 `virtual.model` 静态路由、目录项、会话级路由、物理响应记录）；`cacheWarming`（off/streaming/idle）按阶段停止/续保，带经济学阈值、状态与 RPC 展示；模型 `promptCache` 生命周期 + 1-token 重放；模型级 `samplingParams`；Codex Responses 增加 WebSocket 传输（`websocket`/`websocket-cached`/`auto`，auto 握手失败回退 SSE）；新增 OpenRouter 生图与 OpenAI Decisions 分类网关。
- **内置工具**：新增 `powershell`（Windows/装有 pwsh 的平台）；自动加载 `SYSTEM.md` / `APPEND_SYSTEM.md`，上下文文件补齐 `AGENTS.override.md` 与大小写变体。
- **命令与导出**：`/share` 支持 Radius artifact 与私有 GitHub gist（auto 优先 Radius）；`/llama` 支持 router 列表/load/unload、Hugging Face 搜索/详情/下载，并把已加载模型自动注册为 `llama.cpp/*`；`/bug` 生成脱敏 zip（环境、配置、错误日志、可选会话）；`/export` 支持 `.jsonl`。
- **认证**：`pi login` 已覆盖 OpenAI ChatGPT（动态客户端 + PKCE）、GitHub Copilot（设备码 + Copilot token）、OpenRouter（PKCE 换永久 key）、Meta（设备码 + API key mint）、Radius（gateway 设备码）、Anthropic、xAI、Kimi 与 Codex；其余 Provider 仍支持 API key 登录。`logout` 可移除任意 Provider 的凭据；运行时按需刷新过期 OAuth token。
- **已知限制**：Radius 登录目前走设备码（未做浏览器选择菜单）；OpenAI ChatGPT 回调固定端口 1455；GitHub Copilot Enterprise 只支持域名输入；`/llama` 下载为轮询等待，无进度条/量化体积选择；虚拟模型只支持 `models.json` 静态目标，尚未接入 JS 扩展的动态 `route()` 与路由状态条目；Codex `websocket-cached` 尚未复用连接/`previous_response_id`；生图/分类网关目前只有 OpenRouter 与 OpenAI Decisions 两种方言，且尚未接到 CLI/TUI/桌面入口；cache warming 未实现扩展决策事件；`cimd`、完整 MCP `callbackUrl` 路径与 pi 的 `mcp-auth.json` 共享格式仍未覆盖。

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
- 可搜索模型选择器、中文思考级别、斜杠命令、命令面板、可自定义快捷键、文件拖入与图片附件，以及系统剪贴板的图片粘贴与图片复制。
- 供应商/模型、API Key、Agent 设置、MCP 和 JSON 配置管理；全局/项目作用域、未知字段保留、原子写入和冲突检查。
- 余额/积分查询支持内置端点及自定义解析、New API、Cookie、独立令牌，入口在会话统计弹窗（刷新、查询时间、配置入口与错误状态），不包含完整订阅窗口查询。
- 跨会话用量统计：单遍扫描会话 JSONL，按日期、供应商+模型、项目聚合令牌与费用，并提供 26 周×7 天热力图；RPC `get_usage_stats` 与桌面「用量统计」标签（命令面板入口）共用。
- 模型连接测试：输入框工具栏的「测试连接」按钮发一次 1-token 探测，验证 Base URL、凭据、模型 ID 与自定义头，并显示延迟；RPC 提供 `test_model`。
- 远程模型目录：配置页每个供应商的「拉取远程模型」按钮按 API 探测 `GET /models`（OpenAI 兼容 / Anthropic `/v1/models` / Google `/models`），把缺失的模型以保守默认值并入配置草稿；RPC 提供 `fetch_remote_models`。
- 实时会话统计、首 token 延迟、速度、费用、缓存命中率与手动压缩；统计口径与性能基准见专项文档。
- 深色、浅色、跟随系统；主题切换不重建整棵界面。内嵌 HarmonyOS Sans SC、Maple Mono NF CN 和 Lucide 图标，许可随包。
- 资源与主题：按 pi 约定发现技能、提示词模板与主题，来源覆盖用户目录、项目 `.pi`/`.agents`、settings 显式声明的路径与已安装 pi 包。优先级按 rank 排序：项目 settings 声明（0）> 项目自动发现（1）> 用户 settings 声明（2）> 用户目录自动发现（3）> 包（4）；同名资源保留先发现者，因此项目级总能覆盖用户级。坏资源不阻断会话：主题名类型错误、颜色值非法、`SKILL.md` 不可读都只记一条诊断后跳过。「资源」页列出技能、模板、主题与 pi 包的来源、路径与解析诊断，可一键重载；外观页可选择自定义 pi 主题（含包提供的主题）并即时换肤。主题目录有文件监视热重载（防抖 400ms）：激活主题的文件被修改后即时换肤，文件临时解析失败时保留当前外观；项目主题只在受信任时监视。`/reload` 与 RPC `reload_resources` 重新解析资源而不重建会话。
- Git 状态/diff、暂存与取消暂存、提交、分支切换/新建，编辑器与浏览器跳转，自包含 HTML 导出，窗口尺寸/位置与最近项目/模型持久化。
- 工作区检查点与回滚：快照写入 `refs/pi-checkpoints/<id>` 低层引用（不产生分支或提交），Git 面板可手动创建/恢复；发送提示前每仓库每分钟自动打一个（仅在有改动时），默认保留 50 个。
- 命令 Dock（`Ctrl+Shift+D`）：MiniPty 在项目目录里启动交互式 shell（真伪终端），键盘与 IME 输入直接写回 PTY。终端仿真对齐 xterm.js 的常用部分：2000 行 scrollback + 滚动条、选择（拖选/双击选词/三击选行/Alt 区块）、缩放重排（软换行重切）、SGR 颜色与宽字符（斜体/下划线/删除线/暗淡/隐藏）、滚动区域（DECSTBM）、G0/G1 字符集、备用屏（47/1047/1049）、自动换行/原点模式/应用光标键、鼠标上报（1000/1002/1003、1006、1015、1016）、bracketed paste（多行先确认）、DECSCUSR 光标形态与闪烁、OSC 4/10/11/12 调色、DECSCNM/DECALN/DECRQM、制表位、IRM、OSC 8 超链接、OSC 52 剪贴板、焦点上报、DSR/DA。剪贴板：`Ctrl+C` 有选区复制、无选区中断，`Ctrl+V` 粘贴；Linux 选区同步 PRIMARY（拖选即复制、中键粘贴）；右键菜单含复制/粘贴/全选/清屏/重启；Shift+滚轮本地翻历史。输入法在三个平台走同一组 MewUI 接口（Linux 用 IBus/fcitx5/XIM，Windows 用 IMM32，macOS 用 NSTextInputClient）：组合文字以浮层显示在光标格、不写入 PTY，候选窗经 `GetCharRectInWindow` 定位，提交文本以 UTF-8 写入 PTY。头部 Shell 按钮支持自动（`$SHELL` → bash → sh）、检测到的 shell 与自定义路径，选择写入 `desktop.json` 并重启当前 PTY。macOS 走同一 POSIX 分支（MiniPty 的 `posix_spawn` helper），复制/粘贴用 Cmd+C/V、Ctrl+C 仍是中断；helper 由 MiniPty 包分发（JIT 与 dylib 同目录，AOT 时复制到发布根目录）。未覆盖：终端多标签、快照回放与完整 VT100 边界行为。
- 按工作目录单实例；后台有界日志；`--self-check`、`--theme-smoke` 及截图脚本用于验证。

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

2026-10-19 本轮验证：Ai/Agent/CodingAgent/MCP/CLI 共 752 项测试通过，Linux x64 NativeAOT 冒烟通过（`AOT SMOKE OK`）；Desktop 436/437 通过，剩余 1 项为 X11 PrimarySelection 环境相关失败。真实供应商联网与 Windows/macOS 实机仍未验证。

GUI 环境可用 `xvfb-run`。真实二进制对照测试要求 PATH 中有 TypeScript `pi`；没有安装时测试失败，不静默跳过。
测试覆盖 provider 请求/流事件、工具与队列、会话无损读写、压缩/重试、认证、RPC、MCP、桌面逻辑和控件。
真实数据互操作测试还受本机会话/技能数据是否存在影响；provider 使用打桩服务，不能替代真实供应商联网验证。

Linux 有 NativeAOT 与 GUI 验证路径。Windows/macOS 使用条件平台包与原生打包 runner，但原生窗口、shell/路径语义及发布包仍需实机回归。
