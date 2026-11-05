# PiWeft 状态

PiWeft 把 [pi](https://github.com/earendil-works/pi) 的核心移植为 C# / .NET 10：NativeAOT CLI `pi` 与 MewUI 桌面工作台 `piweft-desktop`。会话文件与 pi、PiDeck 互通，但不是完整移植。

构建/打包见 [README](../README.md)；兼容与差距见 [01-parity](01-parity.md)；性能见 [02-performance](02-performance.md)；codemode 见 [03-codemode](03-codemode.md)；Markdown 见 [04-markdown](04-markdown.md)。

## 架构

| 项目 | 职责 |
|---|---|
| `PiWeft.Ai` | 消息、JSON、模型目录（含虚拟模型）、费用、认证、provider 流式适配、生图/分类、Codex WebSocket |
| `PiWeft.Agent` | 代理循环、串行/并行工具、steering、follow-up、事件与中断 |
| `PiWeft.CodingAgent` | 会话、内置工具、资源发现、设置、压缩、分支摘要、重试、统计、安全门 |
| `PiWeft.JavaScript` | Jint 5 沙箱、codemode 源码解析与 TypeScript 声明 |
| `PiWeft.Mcp` | stdio / streamable HTTP、分页、工具、资源、提示、通知与 OAuth |
| `PiWeft.Cli` | 参数与环境装配、text / print / JSON / RPC、认证、HTML 导出、宿主日志 |
| `PiWeft.Markdown` | 增量 Markdown 文档模型、块级 diff、分块布局与精确高度回收 |
| `PiWeft.Desktop` | 多项目、多会话工作台，进程内驱动 `CodingAgentSession` |
| `tools/` | AOT 冒烟、MCP fixture、基准、截图脚本、图标获取 |

- .NET 10、`LangVersion=preview`、制表符缩进；文档、界面文案与注释以中文为主。
- JSON 使用 `System.Text.Json` 源生成与手写转换器；不使用 Newtonsoft.Json 或供应商 SDK，保留 trim/AOT 分析器。
- 核心库无第三方运行时包，例外是 codemode 的 Jint 5 预览版（`Directory.Packages.props` + `Jint-preview` 源）。桌面只用 `external/MewUI` 源码子模块与 `PiWeft.Markdown`（Markdig）。
- 模型目录以内嵌 `src/PiWeft.Ai/Resources/models.json` 为基线，`<agentDir>/models-store.json` 缓存远程目录。虚拟模型支持静态 `"virtual":{"model":...}` 与动态 `"virtual":{"route":"..."}`，状态写成 `pi.virtual-model-state` 条目。
- 不实现 TypeScript 扩展（JS 扩展宿主已实现）、mermaid、KaTeX、托盘、桌面通知、自动更新、崩溃上报、内嵌浏览器；终端用独立 PTY Dock，不嵌第三方终端组件。codemode 默认不启用。

## 兼容基线

- CLI 产物/程序集名保持 `pi`；`--version` 报告 RPC 契约版本 **1.3.0**。
- 保留 `PI_CODING_AGENT_DIR`、`PI_LOG_LEVEL`、`~/.pi/agent`、项目 `.pi`、配置文件名与 v3 JSONL 会话格式。损坏的会话头 `cwd` 回退到当前目录。
- 系统提示词只在运行时构造，不写入会话。未知字段原样保留：会话头、条目顶层、已知角色的未建模字段（`Extra`）、未知角色整体（`CustomMessage.Raw`）、未知条目（`UnknownSessionEntry`）；`usage` / `usage.cost` 的未知字段同样保留。新增已知角色字段时须同步 `MessageJsonConverter` 与 `SessionJson.KnownKeys`。
- 默认工具与 pi 一致：`read`/`bash`/`edit`/`write`，另保留 `todo` 与 `ask_question`；Windows 默认含 `powershell`。`grep`/`find`/`ls`/`codemode` 需显式开启。`--tools` 支持整体替换、`+name`/`-name` 与 `--exclude-tools` 通配。
- bash / powershell 子进程注入 `PI_SESSION_ID`、`PI_SESSION_FILE`、`PI_PROVIDER`、`PI_MODEL`、`PI_REASONING_LEVEL`，启动时设置 `AI_AGENT=pi`、`PI_CODING_AGENT=true`；`settings.shellPath` / `shellCommandPrefix` 控制 shell 与前缀。
- bash 路径、输出与收尾：命令输出进入上下文/会话前剥掉 ANSI 与 NUL/控制字符（工具保留 CR，与 pi 的精确输出一致），完整输出临时文件为 0600；shell 先退出、后台进程仍占管道时 timeout 仍然生效，并在状态行报告 shell 的真实退出码。Unix 用 `setsid` 建立独立进程组，超时/取消整组 `SIGKILL`（无 `setsid` 时退回整树 kill）；Windows 用 System32 的 `taskkill /F /T`。用户 `!` 命令的流式事件是 `bash_execution_update.delta` 文本块并去掉 CR，与 pi 的 bash-executor 一致。
- 生效 settings：`shellPath`、`shellCommandPrefix`、`sessionDir`、`defaultTools`、`cacheWarming`、`steeringMode`、`followUpMode`、`modelThinkingLevels`、`enabledModels`、`branchSummary`、`compaction`、`retry`、`transport`、`httpIdleTimeoutMs`、`httpProxy`、`extensions`、`skills`、`prompts`、`themes`、`packages`、`enableSkillCommands`、`defaultProjectTrust`、`sessionShareDestination`、`codemode`、`security`。其余键保留不生效。
- 启动迁移与 pi 的 `migrations.ts` 对齐：`oauth.json` / `settings.apiKeys` 并入 `auth.json`；根目录 `*.jsonl` 搬入 `sessions/<cwd>/`；`commands/` → `prompts/`；v1/v2 会话升级到 v3 并原子回写。`--export` 只读。
- 编辑重发不新建会话：`TruncateForResend` 在原 JSONL 里把放弃的提问及后继写成 `deleted` 墓碑，叶子回到父条目。墓碑不进投影/分支树，重试与删除仍走 `context_edit`。
- `--system-prompt` 替代 preamble，`--append-system-prompt` 追加，`--no-context-files` 停用 AGENTS.md / CLAUDE.md；`--export` 导出 HTML/JSONL；`--models` 限定轮换范围。
- `pi install/list/remove/update` 管理 npm/git/本地包，`--local` 写项目 `.pi/settings.json`；`settings.npmCommand` 可覆盖 npm。
- `pi mcp list|add|remove|login|logout` 管理服务器与 OAuth；配置写 `<agentDir>/mcp.json`，令牌写 `mcp-oauth.json`；写入保留其它键、注释与尾逗号。
- CLI `@file` 展开为 `<file>` 文本块，图片作为内容块；print/JSON 模式合入重定向 stdin；RPC 拒绝 `@file`。
- 技能命令 `/skill:name` 与提示词模板在 `CodingAgentSession` 内统一展开。交互命令含 `/model`、`/thinking`、`/compact`、`/plan`、`/reload`、`/copy`、`/todo`、`/import`、`/bug`、`/new`、`/resume`、`/session`、`/name`、`/tree`、`/fork`、`/clone`、`/export`、`/scoped-models`、`/mcp`、`/trust`、`/settings`、`/login`、`/logout`、`/hotkeys`、`/changelog`、`/help`、`/share`、`/llama`。`!command` 输出并入对话，`!!command` 只执行。
- `-e/--extension` 加载显式扩展文件/目录/`builtin:<name>`；`-ne/--no-extensions` 只关闭约定发现。未知 `--flag` 交给扩展 `pi.registerFlag`，无人注册时退出 2。
- 网络：`httpIdleTimeoutMs` 默认 300 秒，按读取续期；只切断停滞流，不限制长流。严格区分用户中止（`aborted`，不重试）与空闲超时（`error`，可重试）。`httpProxy` 只读全局设置。`--offline` / `PI_OFFLINE=1` 跳过自动网络操作。
- 项目信任：`<agentDir>/trust.json` 从 cwd 逐级向上取第一个决策；`-a/--approve`、`-na/--no-approve` 覆盖；默认 `ask`。不受信时不合并项目设置、不连项目 MCP、不读项目模型/技能/提示词。RPC/print 无法弹框时按不受信处理。
- 凭据按显式选项、存储凭据、环境变量异步解析；OAuth 刷新有闸门且不跟随请求取消。可恢复错误按 `settings.retry` 退避；供应商 `Retry-After` 优先；要求等待超过 15 分钟的配额冷却不自动重试，直接把错误交回界面。失败尝试保留在文件里，通过 `context_edit` 排除出上下文。
- 请求过大（`prefill_cap_refused`、413、payload too large、上下文超限）进入 overflow recovery：排除失败尝试后压缩；摘要请求本身过大时按 token 递归对半拆分，先摘要前半段再把它作为 `previousSummary` 摘要后半段。压缩/上下文编辑之后的最后一次 usage 不再充当上下文估算锚点，避免压缩后仍显示 600K。
- 提示音与 PiDeck 同义：完成/出错/等待输入三类事件，各有开关与预设 WAV，默认完成与出错开；会话完成 8 秒、全局 1.5 秒冷却；「仅失焦提醒」在前台静默。
- 等待态由 `RespondingStatusPolicy` 统一判定（退避 → 压缩 → 工具 → 正文 → 思考 → 预热/空窗）；预热期三句轮播，回合内空窗只说「正在处理」；时间线尾部另有等待占位行（不进模型上下文/会话文件）。
- PiDeck 的 `--no-themes`、`--offline`、`--no-extensions`、`-e <path>` 已接线；`--use-theme`、`--tui-mode`、`--autocomplete-max-visible` 接受但不实现。

## RPC

| 命令组 | 范围 |
|---|---|
| 对话/队列 | `prompt`、`steer`、`follow_up`、`abort`、`clear_queue`、队列模式 |
| 会话/查询 | `new_session`、`get_state`、`get_messages`、`get_session_stats`、`get_entries`、`get_tree`、`get_last_assistant_text`、命名 |
| 导航/派生 | `get_fork_messages`、`fork`、`clone`、`switch_session` |
| 模型/思考 | 获取、设置、轮换；保留完整模型声明 |
| 计划/信任 | `set_plan_mode`；`get_state` 回报 `planMode` / `projectTrusted` |
| 管理 | `compact`、自动压缩/重试、`bash`、`abort_bash`、`get_commands`、`export_html` |
| 连接/目录 | `test_model`（1-token 探测）、`fetch_remote_models` |
| MCP | `list_mcp`、`reconnect_mcp` |
| 统计 | `get_usage_stats`（按日期/模型/项目与热力图） |
| 扩展 UI | `ask_question` / `extension_ui_response`；`select`/`confirm`/`editor`/`notify` 与 `setStatus`/`setWidget`/`setTitle`/`set_editor_text` |
| 资源 | `get_resources`、`reload_resources` |

`get_state` 额外返回 `cwd`、`tools`、`entryCount`、`errorMessage`、`cacheWarming`。事件与 pi 对齐：`agent_settled`、`queue_update`、`thinking_level_changed`、`session_info_changed`、`entry_appended`、`compaction_start/end`、`tool_execution_update/end`。让 PiDeck 用本端口：把 `customPiPath` 指向 `pi` 二进制或包装脚本。

## Provider、认证与 MCP

- API：`anthropic-messages`、`openai-completions`、`openai-responses`、`openai-codex-responses`、`azure-openai-responses`、`google-generative-ai`、`google-vertex`、`bedrock-converse-stream`、`mistral-conversations`、`pi-messages`。
- OAuth：ChatGPT/Codex、GitHub Copilot、Anthropic、OpenRouter、xAI、Kimi、Meta、Radius；其它走 API Key。`pi login <provider> [oauth|api-key]` 可选登录方式，API Key 字段来自与桌面端共用的内置供应商目录（Cloudflare/Vertex/Bedrock 多字段）。`pi auth check` 返回 `ready`/`not_ready`/`invalid`，退出码 0/1/2。
- `auth.json` 支持明文与 `${VAR}`，命令型凭据以 `!` 开头（10 秒超时、64 KiB、失败回落环境变量），以及 `api_key.env` 的供应商级字段（Cloudflare 账户/网关、Vertex 项目/区域/ADC、AWS 访问密钥）。写入原子，Unix 0600；损坏文件拒绝覆盖。
- OpenCode / OpenCode Go 请求自动带 `x-opencode-session` 与 `x-opencode-client: piweft`；显式同名头优先。
- MCP 支持 stdio / streamable HTTP 握手、工具/资源/模板/提示分页、通知与 OAuth（授权码 + PKCE、动态注册、回环回调、401 刷新重放、客户端凭据）。stdio 失败会杀子进程并释放连接。

## 桌面能力

桌面复用 CLI 的环境装配与同一会话文件，不启动 RPC 子进程。包含多项目/多会话标签、会话树与有界正文搜索、置顶/命名/删除到 `.trash`/恢复、外部会话导入（Claude Code、Codex CLI）、文件浏览与编辑、原生 todo/ask_question/计划模式、资源与主题热重载、模型连接测试/远程目录、Git 全流程（状态、暂存、提交、分支、AI 摘要、图、比较、远端同步、stash）、检查点、轨迹、终端 Dock、用量统计与供应商余额、深浅主题。

详见 [01-parity](01-parity.md) 与 [DESIGN](../DESIGN.md)。

## 已知限制

- 扩展宿主只支持 JavaScript；TypeScript 需构建期转译；处理器必须同步；无自定义 renderer。
- 安全门 `ask` 在没有审批宿主时退化为拒绝；默认档 `off`；CLI 无切换档位命令。
- CLI 仍是按行 REPL：无完整 TUI、多行编辑、`$EDITOR`、键位绑定与终端图片。
- 并行工具里同一文件的 `edit`/`write` 已串行化；`nestedCalls` / `parentToolCallId` 尚未产出，只按未知字段保留。
- 生图/分类网关已接入 codemode，但没有一级入口；虚拟模型路由已实现，扩展宿主动态注册未接；Codex `websocket-cached` 未复用连接。
- 桌面历史仍全量在内存（分支/fork/搜索/todo/`context_edit` 依赖全量）；完整 VT100 边界、终端多标签、快照回放未覆盖。
- `grep`/`find` 仍是进程内实现：glob、父目录 ignore、limit/字节上限已对齐，但正则使用 .NET `NonBacktracking` 而非 ripgrep/fd；不依赖外部二进制。
- `read` 图片按 magic bytes 识别并给出非 vision 提示，但未按模型限制缩放/转码。
- 内置工具尚未声明 `constrainedSampling`，provider 层也未实现 strict JSON schema / grammar。
- Windows/macOS 原生窗口、发布包、真实供应商联网与 PiDeck 完整 e2e 仍需实机验证。

## 验证

```sh
dotnet test PiWeft.slnx
dotnet run --project tools/PiWeft.SessionBench
dotnet publish tools/PiWeft.AotSmoke -c Release -r linux-x64
dotnet publish src/PiWeft.Cli -c Release -r linux-x64
dotnet publish src/PiWeft.Desktop -c Release -r linux-x64
```

无头 GUI：`xvfb-run -a dotnet test PiWeft.slnx`。视觉验收脚本见 [01-parity](01-parity.md)。provider 使用打桩服务；真实数据互操作测试依赖本机会话/技能数据。
