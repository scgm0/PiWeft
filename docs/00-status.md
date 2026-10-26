# PiWeft 状态

PiWeft 将 [pi](https://github.com/earendil-works/pi) 的核心移植为 C# / .NET 10，提供 NativeAOT CLI `pi` 和 MewUI 桌面工作台 `piweft-desktop`。会话文件与 pi、PiDeck 互通，但不等于完整移植 pi 或 PiDeck 的所有功能。

构建、运行与打包见 [README](../README.md)。兼容边界与剩余差距见 [pi / PiDeck 兼容](01-parity.md)，统计与渲染性能见 [性能与基准](02-performance.md)，codemode/Jint 见 [codemode 与 Jint](03-codemode.md)，增量 Markdown 控件见 [PiWeft.Markdown](04-markdown.md)。

## 架构与约束

| 项目 | 职责 |
|---|---|
| `src/PiWeft.Ai` | 消息、JSON、模型目录（含虚拟模型）、费用、认证、provider 流式适配、生图/分类 API、Codex WebSocket |
| `src/PiWeft.Agent` | 代理循环、串行/并行工具、steering、follow-up、事件与中断 |
| `src/PiWeft.CodingAgent` | 会话、内置工具、资源发现（技能/提示词/主题/包）、设置、压缩、分支摘要、重试与统计 |
| `src/PiWeft.JavaScript` | Jint 5 沙箱、codemode 源码解析与 TypeScript 声明渲染 |
| `src/PiWeft.Mcp` | stdio / streamable HTTP、分页、工具、资源、提示与通知 |
| `src/PiWeft.Cli` | 参数与环境装配、text / print / JSON / RPC、认证、HTML 导出、宿主日志 |
| `src/PiWeft.Markdown` | 增量 Markdown 文档模型、块级 diff、分块段落布局与精确高度块回收 |
| `src/PiWeft.Desktop` | 多项目、多会话工作台，进程内驱动 `CodingAgentSession` |
| `tools/` | NativeAOT 冒烟、MCP fixture、会话/桌面基准、桌面截图脚本、Lucide 图标获取 |

- 使用 .NET 10、`LangVersion=preview` 和制表符缩进；中文为文档、界面文案和注释的第一方语言。
- JSON 使用 `System.Text.Json` 源生成与手写转换器，不依赖 Newtonsoft.Json 或供应商 SDK。保留 trim/AOT 分析器。
- 核心库无第三方运行时包，唯一例外是 codemode 所需的 Jint 5 预览版（版本固定在 `Directory.Packages.props`，包来自 `nuget.config` 里的 `Jint-preview` 源）；宿主使用 ZLogger，桌面使用 MewUI 0.22.1 的 Core/SVG 扩展和自有 `PiWeft.Markdown`（Markdig）；`Aprillz.MewUI.Markdown` 只保留在测试中作参考实现。
- 模型目录以内嵌 `src/PiWeft.Ai/Resources/models.json` 为基线，`<agentDir>/models-store.json` 缓存 pi.dev 远程目录（`pi update --models` 刷新），自定义端点由 `models.json` 注册。模型条目可写 `"virtual":{"model":"provider/id","thinkingLevel":"high","thinkingLevels":["off","high"]}` 静态路由，或 `"virtual":{"route":"return { model: ctx.modelRegistry.find(...) }","thinkingLevels":[...]}` 动态 JS 路由；每次请求由 route 选出物理模型，助手消息仍记录实际物理模型，路由状态写成 `pi.virtual-model-state` 自定义条目。
- 不实现 JS 扩展宿主、mermaid、KaTeX、托盘、桌面通知、自动更新、崩溃上报或内嵌浏览器；终端以独立的 PTY 命令 Dock 提供，不内嵌第三方终端组件。codemode 已实现为内置工具，默认不启用。

## pi 与 PiDeck 兼容基线

- CLI 产物与程序集名保持 `pi`；`--version` 报告 RPC 契约版本 **1.3.0**，不是桌面构建号。
- 保留 `PI_CODING_AGENT_DIR`、`PI_LOG_LEVEL`、`~/.pi/agent`、项目 `.pi`、配置文件名及 version 3 JSONL 会话格式。打开外部会话时，头部 `cwd` 为空或路径损坏会回退到当前工作目录，避免整窗启动失败。
- 系统提示词只在运行时构造，不能作为历史消息写入会话。工具热更新保留提示章节边界；未知会话字段原样保留：会话头、条目顶层字段、以及四种已知角色（system/user/assistant/toolResult）消息各自的未建模字段都会原样写回（`Message.Extra` / `SessionEntry.Extra` / `SessionHeader.Extra`），未知消息角色整体进 `CustomMessage.Raw`，未知条目类型由 `UnknownSessionEntry` 整行输出。新增上游字段（pi 的 `nestedCalls`、`hermesExtras` 就是实例）不需要改转换器也不会在 `fork`/导入落盘时丢失；新增字段落到已建模角色上时，必须同步 `MessageJsonConverter` 与 `SessionJson.KnownKeys` 的字段名单，否则该字段会被当作未知字段重复输出。内置工具的短 snippet 与 guidelines 在所有入口（CLI/RPC/桌面）按名称重新挂回。
- 默认工具集与 pi 一致：`read`/`bash`/`edit`/`write`，另保留 PiDeck 依赖的原生 `todo` 与 `ask_question`；Windows 上默认目录还包含 `powershell`。`grep`/`find`/`ls` 与 `codemode` 需要 `--tools`、`settings.defaultTools` 显式开启。`--tools` 的纯名字列表整体替换默认集、`+name`/`-name` 在默认集上增减、`--exclude-tools` 支持通配，模式始终针对完整内置目录展开。
- bash 工具向子进程注入 pi 的会话环境变量（`PI_SESSION_ID`、`PI_SESSION_FILE`、`PI_PROVIDER`、`PI_MODEL`、`PI_REASONING_LEVEL`，执行前先清除继承值），进程启动时设置 `AI_AGENT=pi` 与 `PI_CODING_AGENT=true`；`settings.shellPath`、`settings.shellCommandPrefix` 分别控制 bash 的 shell 与命令前缀。`!`/`!!` 内联命令（交互模式）不走注入。
- 生效的 settings 键：`shellPath`、`shellCommandPrefix`、`sessionDir`（`--session-dir` 优先）、`defaultTools`、`cacheWarming`、`steeringMode`、`followUpMode`、`modelThinkingLevels`、`enabledModels`、`branchSummary`、`sessionShareDestination`、`codemode`、`security`。其余 pi 键原样保留、不生效。`security` 是本端口新增的键（pi 没有），语义与 PiDeck 的 `security-policy.json` 一致，详见[工具调用安全门](01-parity.md#工具调用安全门)。
- `--system-prompt` 取代默认 preamble，`--append-system-prompt` 追加为 `addendum` 章节，`--no-context-files` 停用 AGENTS.md / CLAUDE.md 发现；三者都接受字面文本或文件路径。`--export <session> [output]` 导出 HTML/JSONL 后退出（只读）；`--models <patterns>` 限定模型轮换范围。
- `pi install <source>`、`pi list`、`pi remove <source>`、`pi update [source]` 管理 pi 包：npm 装到 `<agentDir>/npm/node_modules`、git 克隆到 `<agentDir>/git`、本地路径只登记不复制；`--local/-l` 写项目 `.pi/settings.json`。`settings.npmCommand` 可覆盖 npm 可执行文件。
- `pi mcp list|add|remove|login|logout` 管理 MCP 服务器与 OAuth 令牌：配置写 `<agentDir>/mcp.json`（`--local` 改项目 `.pi/mcp.json`），令牌写 `<agentDir>/mcp-oauth.json`；写入保留其它键、注释与尾逗号，解析失败时拒绝覆盖。
- CLI `@file` 参数展开为 `<file>` 文本块，图片作为内容块随首条提示发送；print/JSON 模式把重定向 stdin 合入首条提示（交互模式不读管道 stdin），RPC 模式明确拒绝 `@file`。
- 技能命令 `/skill:name` 与提示词模板在 `CodingAgentSession` 内统一展开，CLI、RPC 与桌面共享同一语义。交互模式实现 pi 的内置斜杠命令面：`/model`、`/thinking`、`/compact`、`/plan`、`/reload`、`/copy`、`/import`、`/bug`、`/new`、`/resume`、`/session`、`/name`、`/tree`、`/fork`、`/clone`、`/export`、`/scoped-models`、`/mcp`、`/trust`、`/settings`、`/login`、`/logout`、`/hotkeys`、`/changelog`、`/help`；`/share`、`/llama` 与 `/bug` 也已实现。`!command` 把输出并入对话，`!!command` 只执行不入模型上下文。
- 为兼容而接受但不生效的选项（`-e`、`--tui-mode` 等）会在 stderr 提示一次「accepted for compatibility but not implemented」。`pi config` 列出包来源与可编辑资源键；`pi update --self/--extensions/--models/--all/--force` 按 pi 语义解析。`pi mcp add` 支持 pi 形状（`-- <command> [args]`、`--bearer-token-env-var`、`--oauth-client-id/secret`、`--exposure`、`--description`），`pi mcp login --timeout` 限定整个登录过程。
- 项目信任：`<agentDir>/trust.json` 与 pi 同构（规范化路径 → true/false），从工作目录逐级向上取第一个决策；`-a/--approve`、`-na/--no-approve` 覆盖；全局 `settings.defaultProjectTrust` 为兜底，默认 `ask`。不受信时不合并项目设置、不连接项目 `.pi/mcp.json`、不读取项目级 `.pi/models.json`，也不加载项目级技能与提示词模板。交互模式与桌面在启动时询问「信任（记住）/仅本次/不信任（记住）」；RPC 与 `--print` 无法弹框，`ask` 按不受信处理。
- 凭据按显式选项、存储凭据、环境变量解析；每次请求按目标供应商选择凭据，压缩与分支摘要共用该路径。请求超时只覆盖等待响应头，不切断正常长流。可恢复错误按 `settings.retry` 分类并指数退避；失败尝试保留在文件里，通过 `context_edit` 从模型上下文排除。
- PiDeck 传入的 `--no-themes`、`--offline`、`--no-extensions`、`-e <path>` 等兼容参数保留；接受参数不代表实现 JS 扩展。主题由桌面按 pi 主题 JSON 解析。

## 内置 codemode

内置 `codemode` 工具让模型写 JavaScript，在 Jint 5 沙箱里调用其它工具。默认不启用（与 pi 的 inactive 注册一致）：`--tools +codemode` 或 `settings.defaultTools: ["+codemode"]` 选中；存在 MCP `codemode` exposure 工具时按 pi 行为自动启用。

脚本面、工具发现、exposure 分流、声明预算、嵌套调用管线、沙箱限制与 AOT 差异见 [codemode 与 Jint](03-codemode.md)。

## RPC

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
| 扩展 UI | `ask_question` 发出 `extension_ui_request`（`method: "input"`，PiDeck 批量信封），`extension_ui_response` 唤醒等待中的提问；pi 的 `select`/`confirm`/`editor`/`notify` 与 fire-and-forget 的 `setStatus`/`setWidget`/`setTitle`/`set_editor_text` 已按 pi 顶层字段实现（RPC、控制台与桌面），仍未提供扩展 renderer |
| 资源 | `get_resources` 返回已解析的技能/提示词/主题/包与诊断；`reload_resources` 重新解析并刷新系统提示词里的技能章节 |

`get_state` 在 pi 字段之外补充 `cwd`、`tools`、`entryCount`、`errorMessage` 和 `cacheWarming`；消息计数不含运行时系统提示词。`get_session_stats` 拆分输入、输出、缓存读取和写入，并给出上下文窗口占比。JSON 写入深度为 65536，以容纳深层会话树。

事件族与 pi 对齐（JSON 与 RPC 共用）：每次会话级运行结束发 `agent_settled`（`aborted` 取自最后一次助手消息的中止状态）；`queue_update` 在 steering/follow-up 队列变化时发全量快照；`thinking_level_changed`、`session_info_changed`、`entry_appended`、`compaction_start`/`compaction_end` 齐备；`tool_execution_update`/`tool_execution_end` 携带 `partialResult`/`result` 与 `durationMs`。

让 PiDeck 使用本端口：将 `customPiPath` 指向发布的 `pi` 二进制，或执行 `dotnet .../pi.dll` 的包装脚本。真实 pi 字段对照和 PiDeck 握手由现有测试验证；PiDeck 完整 UI e2e 仍需单独运行。

## Provider 与认证

实现以下 API：`anthropic-messages`、`openai-completions`、`openai-responses`、`openai-codex-responses`、`azure-openai-responses`、`google-generative-ai`、`google-vertex`、`bedrock-converse-stream`、`mistral-conversations`、`pi-messages`。

`pi login` 支持 ChatGPT Codex、OpenAI ChatGPT、GitHub Copilot、Anthropic、OpenRouter、xAI、Kimi、Meta、Radius 的 OAuth；其它 Provider 走 API key 登录。`logout` 可移除任意 Provider 的凭据并保留其它条目；运行时按需刷新过期 OAuth token。`pi auth check` 输出 `ready` / `not_ready` / `invalid`，退出码为 0 / 1 / 2；`invalid` 表示凭据存在但不可用。`print-api-key` / `print-bearer-token` 只输出相应凭据；`--min-expiry` 仅适用于后者。

`auth.json` 的 API Key 支持明文和 `${VAR}` 模板；命令型凭据（`!` 开头）在取值时执行命令取 stdout（10 秒超时、64 KiB 上限，不缓存），失败时回落到环境变量。写入是原子的，Unix 下仅用户可读写；文件无法解析时拒绝覆盖，避免抹掉其它供应商的凭据。

## MCP

stdio 与 streamable HTTP 均支持握手、工具/资源/模板/提示分页及调用。stdio 子进程的 PATH 按 PiDeck 的顺序组装（env PATH → 登录 shell PATH → 包管理器/版本管理器目录 → 平台兜底），命令解析为绝对路径；登录 shell 探测异步执行并按进程缓存。

资源工具过滤 `ui://` / MCP App 内容，保留图片块；其它二进制保存到临时文件，模型侧文本限制为 20 KB 并提供完整文件路径。工具只返回 `structuredContent` 时，模型侧回退为 JSON 文本。HTTP 通知流里带 id 的服务端请求会反向 POST 应答；stdio 列表变化通知刷新工具并调用 `SetTools`，HTTP 通过独立 SSE 通知流接收通知，服务器返回 400/404/405/406/204 时静默降级。

OAuth 支持授权码 + PKCE、动态注册、回环回调、401 刷新重放与客户端凭据授权（`clientId + clientSecret`）。令牌单独保存在 `<agentDir>/mcp-oauth.json`，与 provider 凭据的 `auth.json` 分开。stdio 握手失败会杀掉已拉起的子进程并释放自有 `HttpClient`。

## 桌面能力

桌面直接复用 CLI 的环境装配与相同会话文件，不启动 RPC 子进程。

- 多项目、多会话工作台：会话树、元数据与有界正文搜索、置顶、命名、删除到 `.trash`、恢复、并行标签与右键操作。启动目录兜底：从二进制目录启动沿用最近项目；macOS 由 Finder/launchd 启动且 cwd 为文件系统根时退回最近项目或用户主目录。
- 文件浏览器与内置编辑器（`Ctrl+Shift+E`）：按目录导航、跳过 `.git`/依赖/构建产物、带 Git 状态标记；目录列表按视口虚拟化（最多 2000 条）。文本读写有大小与二进制判定，保存用时间戳检测外部修改。助手每轮修改汇总显示在回答下方。
- 原生 `todo` / `ask_question` / 计划模式：todo 按会话分支持久化为 PiDeck v3 自定义条目（`customType: "pi-deck-todo"`），切换分支自动恢复；`ask_question` 的线协议与 PiDeck 批量信封一致；计划模式停用 `edit`/`write`、附加 `plan_mode` 章节并用只读 shell 白名单拦截破坏性命令。
- 资源与主题：按 pi 约定发现技能/提示词模板/主题/pi 包，优先级为项目 settings 声明(0) > 项目自动发现(1) > 用户 settings 声明(2) > 用户目录自动发现(3) > 包(4)，同名资源先发现者胜出。主题目录文件监视热重载（防抖 400 ms，项目主题只在受信任时监视）；`/reload` 与 RPC `reload_resources` 重新解析而不重建会话。
- 模型连接测试与远程模型目录：1-token 探测（RPC `test_model`）与按 API 拉取 `GET /models`（RPC `fetch_remote_models`），入口在配置页与输入框工具栏。
- Git 状态/diff、暂存/取消暂存、提交、分支切换/新建、检查点与回滚、远端同步（fetch / pull `--ff-only` / push，首次推送自动建立上游跟踪）、stash 收起与应用/丢弃（面板按未暂存/已暂存分节，带批量暂存与提交禁用态）、命令 Dock（PTY 终端）、跨会话用量统计与余额查询、实时会话统计、深浅主题与内嵌字体/图标。所有 git 子进程钉住 `LC_ALL=C`，失败按输出特征分类成可操作的中文提示、原始输出留在详情里；状态读取用 `--porcelain=v1 -z`，中文 / 空格 / emoji / 换行路径都按原始 UTF-8 解析。
- 供应商/模型、API Key、Agent 设置、MCP 和 JSON 配置管理；全局/项目作用域、未知字段保留、原子写入和冲突检查。设置是窗口级模态弹窗，未保存修改受保护。
- 按工作目录单实例；后台有界日志；`--self-check`、`--theme-smoke` 及截图脚本用于验证。

桌面功能对照、交互约束、Linux PRIMARY、图片剪贴板与配置安全边界见 [pi / PiDeck 兼容](01-parity.md)。

## 已知限制

- 未实现完整 JS/TS 扩展宿主与扩展 renderer；fire-and-forget UI 方法已就绪，但目前没有进程内 JS 扩展调用方。
- 工具调用安全门只有「放行 / 拒绝」，没有「先问用户再执行」的审批窗口。PiDeck 的 `ask` 动作在这里退化为拒绝，因此默认危险命令表只收不可逆的破坏性命令，日常动作（`rm -rf node_modules`、`npm install`、`git commit`）一律放行。安全门默认等级是 `off`，不显式配置就完全放行。
- CLI 仍是按行 REPL，没有完整 TUI、多行编辑、`$EDITOR`、键位绑定与终端图片。
- 生图/分类网关目前只有 OpenRouter 与 OpenAI Decisions 两种方言，尚未接到 CLI/TUI/桌面入口；虚拟模型已支持 `models.json` 动态 JS route 与状态条目，但扩展宿主动态注册仍未接入；Codex `websocket-cached` 尚未复用连接。cache warming 已有 `cache_warming_decision` 回调；`cimd`、完整 MCP `callbackUrl` 路径与 pi 的 `mcp-auth.json` 共享格式仍未覆盖。
- `Radius` 登录目前走设备码（未做浏览器选择菜单）；OpenAI ChatGPT 回调固定端口 1455；GitHub Copilot Enterprise 只支持域名输入；`/llama` 下载为轮询等待，无进度条/量化体积选择。
- 桌面 JSONL 历史仍全量保存在内存，尚未实现分页；完整 VT100 边界行为、终端多标签与快照回放未覆盖。
- Windows/macOS 原生窗口、发布包、真实供应商联网与 PiDeck 完整 e2e 仍需实机验证。

## 验证入口

```sh
dotnet test PiWeft.slnx
dotnet run --project tools/PiWeft.SessionBench
dotnet publish tools/PiWeft.AotSmoke -c Release -r linux-x64
dotnet publish src/PiWeft.Cli -c Release -r linux-x64
dotnet publish src/PiWeft.Desktop -c Release -r linux-x64
```

GUI 环境可用 `xvfb-run -a dotnet test PiWeft.slnx`。2026-10-26 全量测试：非桌面 898 项（Agent 23 / Ai 192 / JavaScript 56 / Mcp 86 / Cli 126 / CodingAgent 415）、Desktop 921 项，共 1819 项，全部通过；连续多轮并行全量运行无随机失败。`RealSessionFidelityTests` 直接读本机 `~/.pi/agent/sessions` 的真实 pi 会话（当前约 7.8 万行 / 90 个文件）逐行比对重序列化结果，它变红即表示会话格式出现漂移，应当作阻断项而不是已知噪音。`dotnet publish src/PiWeft.Cli -c Release -r linux-x64` 的 NativeAOT 冒烟通过（`AOT SMOKE OK`）。

测试覆盖 provider 请求/流事件、工具与队列、会话无损读写、压缩/重试、认证、RPC、MCP、桌面逻辑和控件。真实数据互操作测试受本机会话/技能数据是否存在影响；provider 使用打桩服务，不能替代真实供应商联网验证。
