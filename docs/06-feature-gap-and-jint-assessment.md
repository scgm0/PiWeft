# 功能缺口与 Jint 技术评估

评估日期：2026-10-14（Jint 探针）；2026-10-16 对照源码与全量 993 项测试复核，更新实现状态、清理已完成的记录并修复排查出的缺陷。状态：规划输入，尚未批准为实施路线。

本文汇总 PiWeft 与 pi / PiDeck 的功能对照，以及 Jint 5 实现 codemode、JS 扩展的可行性和性能探针结果。用于后续确定产品范围、拆分任务、建立依赖与验收标准，不代表相关功能已经实现，也不修改既有产品范围决策。

## 1. 结论与使用方式

- 基础编码 Agent、会话和轻量桌面工作流已经具备；既有入口的兼容缺口（C-01 至 C-06、PI-11）与高频工作台闭环（D-01 至 D-07、PI-06）已基本处置，主要差距在扩展生态、工作台深度与高级模型运行时。
- 会话文件和 RPC 互通不等于完整替代 pi + PiDeck，尤其不能据握手成功推断 JS 扩展工作流可用。
- 剩余优先事项：完整 TUI、多后端、远程服务等应先确定是否属于产品目标，再决定 JS 扩展/codemode 的范围变更。
- Jint 适合先实现受限 codemode，再实现有限的 JS 扩展 API。完整 pi 扩展兼容的主要成本在宿主契约，而非解释器速度。
- 性能要分别看首次初始化、热执行、数据转换、托管分配、原生体积和真实工作流，不能只引用一个微基准。

缺口使用稳定编号：`C-*` 为兼容行为，`PI-*` 为 pi 能力，`D-*` 为桌面工作台，`X-*` 为 pi_agent_rust/pi-go 补充方向。编号便于拆分任务，不是完成度统计。阶段与优先级均为建议，负责人、工作量和时间安排留待正式计划确定。

### 1.1 对照基准

| 对象 | 基准与用途 |
| --- | --- |
| PiWeft | 当前工作目录源码及现有状态文档；评估时源码处于未跟踪工作树，未以已提交版本作为快照 |
| pi | 本地 `/home/scgm/TestBuild/pi`，coding-agent 包版本 1.2.0；作为核心协议、CLI、扩展及 codemode 基准 |
| PiDeck | 本地 `/home/scgm/TestBuild/PiDeck`，包版本 0.9.0；作为桌面工作流基准 |
| pi_agent_rust / pi-go | 本地同级仓库，作为子代理、IDE、记忆和工具增强参考，不作为原版 pi 的必备契约 |
| Jint | 当时正式版 4.17.0；5.0.0 开发线固定提交 `8537426f92ee847ccb09af59b1db60079e3a8028` |

功能分析以源码静态核对为主。Jint 探针实际执行了 JIT 与 Linux x64 NativeAOT。2026-10-16 复核重新运行了全量测试（993 项通过）并对照源码更新第 3、4 节状态；真实供应商联网测试与完整 PiDeck UI e2e 两个口径始终未进行，开始实施前仍应重新确认版本、代码路径和缺口状态。

### 1.2 与现有文档的关系

- [项目状态](00-status.md)：当前实现和公开兼容边界。
- [桌面对齐与限制](02-desktop-parity.md)：桌面细节与验证。
- [配置管理差距](03-pideck-configuration-parity.md)：配置领域的对照。
- [统计与性能](04-session-statistics-performance.md)、[渲染性能](05-desktop-rendering-performance.md)：已有性能口径。

现有文档明确排除 JS 扩展宿主、codemode、Mermaid、KaTeX、托盘、通知、自动更新、崩溃上报、内嵌浏览器和终端。本文将其记录为产品范围差异或候选方向；采用 Jint 等方案需要另行批准范围变更。

## 2. 已有能力基线

以下能力已有实现，应作为回归保护对象，而不是重新开发的任务：

| 领域 | 已有能力 | 不能据此推断 |
| --- | --- | --- |
| 模型调用 | 十类 Provider API、流式事件、思考级别、用量和费用、Codex 设备码登录/刷新 | 所有 Provider OAuth、动态账号模型目录、WebSocket 传输都可用 |
| Agent | 七个基础文件/Shell/搜索工具、串行/并行工具、校验、前后钩子、steering/follow-up、中断、原生 todo/ask_question/计划模式 | 已有 codemode、嵌套工具管线或子代理编排 |
| 会话 | JSONL 兼容、恢复、fork/clone、分支树/摘要导航、压缩、重试、统计 | 文件回滚、完整历史分页或跨 Agent 原生格式导入 |
| MCP | stdio/streamable HTTP、工具/资源/模板/提示的基础能力、stdio 工具列表热刷新、exposure 过滤、运行时重连 | OAuth、HTTP 独立通知流、CLI 管理子命令 |
| 桌面 | 多项目/并行标签、搜索、会话置顶/命名/归档、Markdown、思考/工具过程、图片附件/预览、编辑重发、文件浏览器与编辑器、Git 写操作、检查点、PTY 命令 Dock、跨会话用量统计 | 完整消息编辑删除、完整 VT 仿真与 scrollback、后台 Ask 工作流 |
| 配置 | 模型/供应商/API Key/Agent/MCP/JSON、作用域、未知字段保留、原子写入和冲突检测、连接测试、远程模型拉取、价格表单 | 完整 pi 配置语义、OAuth 登录 |
| 用量与系统 | 会话与跨会话用量、余额/积分、自定义查询、New API/Cookie、Git 状态/diff 与写操作、检查点、HTML 导出、主题、日志、单实例 | 订阅窗口、热力图下钻、推送/拉取、托盘或更新系统 |

供应商支持应按协议、认证、目录和实际联网结果分别判断，不能用 API 数量直接推算支持率。C# 已有自定义 Tool/Provider 接口，但不等于能运行 pi 的 JS/TS 插件。

## 3. 兼容行为缺口与处置记录

这些项目不是单纯增加新功能，而是已有参数、菜单或协议入口没有完整接通。全部条目已处置并有回归测试；剩余事项并入第 4 节缺口清单继续跟踪。项目代码信任只决定资源加载，不是文件或 Shell 操作沙箱。

| 编号 | 发现 | 代码依据 | 状态 | 回归 |
| --- | --- | --- | --- |
| C-01 | `--system-prompt`、`--no-context-files` 已解析，但环境装配未传递，会话仍无条件加载上下文文件 | [CliArguments](../src/PiWeft.Cli/CliArguments.cs)、[CreateSession](../src/PiWeft.Cli/CliEnvironment.cs)、[会话初始化](../src/PiWeft.CodingAgent/CodingAgentSession.cs) | 已实现：`--system-prompt` 取代 preamble，`--append-system-prompt` 成 `addendum`，`--no-context-files` 停用项目上下文；三者支持文本或文件路径 | `PromptInputTests`、`CliRunnerTests.CreateSession_*` |
| C-02 | CLI `@file` 没有 pi 的附件展开语义；print 路径没有把管道 stdin 合入首条提示 | [参数解析](../src/PiWeft.Cli/CliArguments.cs)、[执行路径](../src/PiWeft.Cli/CliRunner.cs) | 已实现：`@file` 展开为 `<file>` 文本块/图片内容块，print/JSON 合并重定向 stdin（交互模式不读管道 stdin）；RPC 明确拒绝 | `CliArgumentsTests.Parse_CollectsFileArgumentsSeparately`、`CliRunnerTests.PrintMode_ExpandsFileArgumentsIntoThePrompt`、`RpcMode_RejectsFileArguments` |
| C-03 | 桌面补全列出模板和 `/skill:name`，普通发送未接模板展开；显式技能加载未见对应完整处理 | [SessionTab](../src/PiWeft.Desktop/SessionTab.cs)、[ChatViewModel](../src/PiWeft.Desktop/ChatViewModel.cs)、[PromptTemplates](../src/PiWeft.CodingAgent/Prompts/PromptTemplates.cs) | 已实现：`CodingAgentSession.ExpandInput` 统一展开 `/skill:name` 与提示词模板，CLI/RPC/桌面共享 | `PromptInputTests`、`PromptAsync_ExpandsSkillAndTemplateBeforeSending` |
| C-04 | RPC 空闲 `prompt` 路径只发送展开后的文本，没有使用 `images`；忙碌排队路径有图片转换 | [HandlePromptAsync / TextMessage](../src/PiWeft.Cli/CliRunner.cs) | 已实现：空闲 `prompt` 与 steer/follow-up 均保留 `images` 内容块；消息重载补齐自动压缩与重试 | `CliRunnerTests.RpcMode_PreservesImagesOnIdlePrompt` |
| C-05 | `--export`、`--models`、显式资源路径、主题/TUI/信任等部分兼容参数只是消费并忽略 | [兼容参数分支](../src/PiWeft.Cli/CliArguments.cs) | 部分实现：`--export` 与 `--models` 已落地；扩展/主题/TUI 类参数显式标注为「接受但不实现」，帮助文本同步说明 | `CliArgumentsTests`、`CliRunnerTests` |
| C-06 | 文档/帮助仍有过度或滞后声明，例如 RPC 图片支持、斜杠命令完整度、帮助中的 MCP 范围，以及 `pi auth check` 的 `invalid` 状态 | [状态文档](00-status.md)、[HelpText](../src/PiWeft.Cli/CliArguments.cs) | 已实现：HelpText 移除错误的 MCP 范围声明，并列明接受的兼容参数；状态文档同步。`auth check` 已补齐 pi 的三态输出 `ready`/`not_ready`/`invalid`（退出码 0/1/2）：`invalid` 表示凭据存在但不可用（命令型凭据执行失败、OAuth 刷新失败） | 能力清单随改动维护；`AuthCommandProcessTests.AuthCheckReportsInvalidWhenTheCommandCredentialFails` |

提示词保真：CLI/桌面传入已过滤的工具列表时会丢失内置工具的短 snippet 与 guidelines，现按名称重新挂回（`todo` 的 snippet/guidelines 与 PiDeck 原文一致）；回归见 `SystemPromptTests`、`RealPromptFidelityTests`（取全部真实会话的并集）。

`--approve/--no-approve` 的语义已随 PI-11 落地（见 4.1），不再单独列为兼容缺口。

## 4. 功能缺口清单

### 4.1 相对 pi

| 编号 | 能力 | 当前状态与差距 |
| --- | --- | --- |
| PI-01 | JS/TS 扩展宿主 | 没有现有 pi 扩展的加载、注册、生命周期、动态能力、重载与清理机制 |
| PI-02 | 扩展 UI 协议 | 部分实现：原生 `ask_question` 经 RPC `extension_ui_request`（`method: "input"`，PiDeck 批量信封）发出，`extension_ui_response` 唤醒等待中的提问；CLI 交互提示与桌面卡片同路径。pi 的对话框方法 `select`/`confirm`/`editor` 与 fire-and-forget 的 `notify` 已按 pi 线协议实现（RPC 转发、控制台与桌面三宿主、agent 侧 `timeout` 自动解析），为 S3 扩展宿主就位；尚未有进程内调用方，`setStatus`/`setWidget`/`setTitle`/`set_editor_text` 不发出，pi 扩展的 renderer / 自定义 TUI 协议不成立 |
| PI-03 | Pi 包管理 | 已实现：settings 的 `packages` 来源解析与包资源发现（`package.json` 的 `pi` 清单、约定目录、glob 收窄、按名去重）；`pi install/list/remove/update` 支持 npm 安装（`--prefix` + `--legacy-peer-deps`）、git 克隆（含 `@ref`）、本地路径登记与目录清理，来源原子写回 settings。未覆盖：npm 依赖冲突治理、包图库与资源筛选诊断界面 |
| PI-04 | 完整终端 TUI | 当前为按行 REPL，缺少多行编辑、终端流式工具展示、补全、选择器、终端图片和完整快捷键 |
| PI-05 | codemode / tool_search | 缺少脚本编排、结果过滤、延迟工具发现和脚本状态；并行 Agent 工具执行不是 codemode |
| PI-06 | MCP 高级运行时 | 部分实现：`McpExposure` 实现 pi 同名 `exposure`/`toolExposure`（`codemode-deferred` 归一、精确名优先于通配符、逐工具覆盖服务器默认；`hidden` 工具不声明，整台 `hidden` 且无逐工具放行的服务器不连接）；因无 codemode，`codemode`/`deferred` 暂按 `direct`。运行时重连：RPC `list_mcp`/`reconnect_mcp` 与桌面「重连 MCP」共用释放-重建-热替换路径。已覆盖：OAuth 2.1 授权码 + PKCE（RFC 9728/8414/7591 的发现与动态注册、回环回调、401 自动刷新并重放、令牌存 `<agentDir>/mcp-oauth.json`）、**客户端凭据授权**（`clientId + clientSecret` 走 RFC 6749 §4.4，`pi mcp login` 与运行时到期重新授权均无需浏览器）、**HTTP 通知流上的服务端请求反向应答**（`ping` 回空结果，其余 -32601）、HTTP 独立 SSE 通知流（服务器返回 404/405 时静默降级）、CLI `mcp list/add/remove/login/logout`。未覆盖：OAuth 网关模式的令牌交换变体、桌面界面的 MCP 授权入口 |
| PI-07 | 认证覆盖 | 部分实现：命令型凭据（`!` 开头）已在请求时执行（10 秒超时、64 KiB 输出上限、不缓存，失败回落环境变量，与 pi 一致）；`auth check` 三态 `ready`/`not_ready`/`invalid` 齐备。OAuth 仍只支持 Codex 设备码；AWS 环境签名路径不等于完整 profile/ECS/IRSA 链 |
| PI-08 | 动态模型目录 | 固定内置目录加本地覆盖；配置页可按 API 拉取远程模型 id 并入草稿（`RemoteModelCatalog`），但缺少账号级目录/能力同步与联网更新；模型选择器刷新仍只重查本地认证 |
| PI-09 | 高级模型运行时 | 缺少虚拟路由、分类器/生图模型执行、缓存保温、Codex WebSocket、模型级按思考档位采样配置 |
| PI-10 | 资源配置与主题 | 部分实现：技能/模板/主题按用户、项目、包作用域统一发现（含 settings 的 `skills`/`prompts`/`themes` 与诊断）；pi 主题 JSON 解析完整（hex / oklch / okhsl / ANSI 索引 / 变量引用 / 终端默认，含色域映射），桌面外观页可选择并即时换肤；**主题目录文件监视热重载已实现**（防抖 400ms、坏文件保留旧外观、项目主题受信任门控，`ThemeWatcher`）。`/reload` 与 RPC `reload_resources` 重新解析资源而不重建会话。未覆盖：独立资源管理界面 |
| PI-11 | 项目信任 | 已实现：`<agentDir>/trust.json`（规范化路径 → true/false）、父目录查找、原子写入；显式 `-a/--na` 优先，其次 trust.json，最后**全局** `settings.defaultProjectTrust`（默认 `ask`，与 pi 一致；项目设置不能自我授权）；不受信时不合并项目设置、不连项目 `.pi/mcp.json`、不加载项目级技能/提示词模板；CLI 交互模式与桌面启动时询问（信任/仅本次/不信任），无 UI 时 `ask` 按不受信处理。测试：`ProjectTrustTests`、`CliEnvironmentTests.Create_GatesProjectSettingsWhenUntrusted`、`McpConfigTests.Load_WithoutProjectTrustIgnoresProjectConfig` |

依据：[项目状态](00-status.md)、[Provider 装配](../src/PiWeft.Cli/CliEnvironment.cs)、[模型目录](../src/PiWeft.Ai/Models/ModelRegistry.cs)、[MCP 配置](../src/PiWeft.Mcp/McpConfig.cs)。

### 4.2 相对 PiDeck

| 编号 | 能力 | 当前状态与差距 |
| --- | --- | --- |
| D-01 | 问询、待办、计划 | 已实现：原生 `todo`（PiDeck v3 `customType: "pi-deck-todo"` 自定义条目、按会话分支恢复、计划全貌只随变更 toolResult 进上下文）、原生 `ask_question`（select/multi_select/confirm/input/editor；RPC/CLI 交互/桌面卡片三宿主，headless 明确报告不可用）、计划模式（停用 `edit`/`write`、`plan_mode` 提示章节、只读 shell 白名单、识别 PiDeck 标记自动退出）。测试：`TodoToolTests`（11 项）、`AskQuestionTests`（5 项）、`PlanModeTests`（14 项）、`TodoStripTests`、`AskQuestionPanelTests`（2 项）。未覆盖：独立后台 Ask 工作流 |
| D-02 | 文件工作台 | 部分实现：`ProjectFiles` 目录列举（跳过 `.git`/依赖/构建产物、2000 条上限）与文本读写（1MiB 上限、NUL 二进制判定、路径不越出根目录、时间戳冲突检测）；`FileBrowserView` 目录导航与内置编辑器（`Ctrl+Shift+E`）；回答级修改汇总；统一/并排 diff 切换。测试：`ProjectFilesTests`（7 项）、`FileBrowserViewTests`（2 项）、`SideBySideDiffTests`/`DiffViewRenderTests`（6 项）、`ChatViewModelTests.AssistantRowSummarizesFilesChangedThisTurn`。未覆盖：多文件标签、草稿本、跨行块注释、内嵌浏览器 |
| D-03 | 内置终端 | 已实现：`TerminalDock` 用 MiniPty 1.3.0 在项目目录启动真伪终端（Linux/macOS `forkpty`/`posix_spawn`，Windows ConPTY）；`TerminalScreen` 做 VT 解析（scrollback、备用屏 47/1047/1049、滚动区域、G0/G1 字符集、SGR 全属性与宽字符、自动换行/原点模式/应用光标键、制表位、IRM、DECSCUSR（含闪烁）/DECSCNM/DECALN/DECRQM、OSC 4/10/11/12 调色、OSC 8 超链接、OSC 52、焦点上报、DSR/DA），清行/清屏、鼠标 1000/1002/1003 + 1006/1015/1016；窗口缩放按软换行重排，40ms 合并重绘并带滚动条。键盘/IME 经队列串行写回，拖选/双击/三击/Alt 区块选择、Shift+滚轮翻历史、bracketed paste（多行先确认），尺寸变化同步 `TIOCSWINSZ`；复制粘贴用 `Ctrl+C`/`Ctrl+V`，Linux 选区同步 PRIMARY（中键粘贴），右键菜单含复制/粘贴/全选/清屏/重启；IME 预编辑经 `ITextCompositionClient` 浮层显示在光标格、候选窗按光标格定位，提交文本经 `ITextInputClient` 写回 PTY（依赖 MewUI X11 的 IBus/fcitx5/XIM 后端）；等宽字体 Maple Mono NF CN；头部 Shell 按钮切换自动/检测到的/自定义路径，写入 `desktop.json` 并重启 PTY；macOS 用 MiniPty 的 `posix_spawn` helper（JIT 时随 native assets 与 dylib 同目录，AOT 由包 targets 复制到发布根目录），复制粘贴走 Cmd+C/V；`Ctrl+Shift+D` 与命令面板入口。测试：`TerminalScreenTests`（49 项）、`TerminalDockTests`（8 项）、`TerminalShellTests`（6 项）、`AppFontTests`（1 项）。未覆盖：macOS/Windows 实机回归、完整 VT100 边界行为、终端多标签、快照回放 |
| D-04 | 完整 Git 工作流 | 部分实现（写操作已落地）：Git 面板分支切换/新建、逐文件暂存/取消暂存、提交信息与提交；`GitWriter` 用 `ArgumentList` 传参、拒绝以 `-` 开头的名称、30s 超时、4KB 输出上限。测试：`GitWriteTests`（真实临时仓库，4 项）、`GitViewTests`（2 项）。未覆盖：推送/拉取、历史图谱、冲突解决界面 |
| D-05 | 文件检查点/回滚 | 部分实现：`Checkpoints` 用临时索引 + `commit-tree` 把工作区（含未跟踪文件、尊从 .gitignore）写成 `refs/pi-checkpoints/<id>`，不产生分支或提交；`git restore --worktree` 回滚；默认保留 50 个并按时间清理；发送提示前每仓库每分钟自动快照（干净工作区不消耗限频窗口）。测试：`CheckpointTests`（5 项）、`GitViewTests.CreatingACheckpointAddsARestoreRow`。未覆盖：快照与助手回合关联、清理后新增的未跟踪文件、检查点分页与 diff 预览 |
| D-06 | 配置深度 | 部分实现：`ProviderProbe` 复用完整 provider 请求路径做 1-token 探测（桌面「测试连接」与 RPC `test_model` 共用）；`RemoteModelCatalog` 按 API 拉取模型 id（OpenAI 兼容 `GET /models`、Anthropic `/v1/models`、Google `/models`，2MiB 与 15s 上限），配置页「拉取远程模型」把缺失项以默认参数并入草稿，RPC `fetch_remote_models`；编辑模型表单含输入/输出/缓存读/缓存写四个价格框，非负且有限才写入 `cost`。测试：`ProviderProbeTests`（5 项）、`RemoteModelCatalogTests`（5 项）、`ConfigurationStoreTests.AddMissingModelsKeepsExistingEntriesAndDefaultsNewOnes`/`ModelCostIsWrittenOnlyWhenProvided`、`CliRunnerTests.RpcMode_TestsModelConnection`/`RpcMode_FetchRemoteModelsReportsUnsupportedApi`。未覆盖：供应商迁移、收藏/隐藏/排序、Azure/Vertex/Bedrock 专用目录接口、桌面 OAuth |
| D-07 | 用量系统 | 部分实现：余额/积分查询（内置供应商 + 通用/New API 探针，入口在会话统计弹窗，输入工具栏不再单独展示）；`UsageStats` 单遍扫描会话 JSONL，按日期、供应商+模型、项目三个维度聚合令牌与费用（只解析含 `usage` 的行，单行超 256KiB 跳过，文件数默认 2000），`UsageStats.Heatmap` 默认 26 周×7 天 4 档热度；RPC `get_usage_stats` 与桌面「用量统计」标签。测试：`UsageStatsTests`（6 项）、`UsageStatsViewTests`（7 项）、`CliRunnerTests.RpcMode_ReportsCrossSessionUsageStats`。未覆盖：订阅窗口/重置时间、火山 AK/SK 等专用解析、热力图点击下钻 |
| D-08 | 技能/插件生态 | 部分实现：技能/提示词模板/主题统一发现，桌面「资源」页列出技能、模板、主题与 pi 包的来源、路径与解析诊断并可一键重载，外观页可选择自定义主题；缺少商店、中文提示词库、包安装的可视化入口和 PiDeck 独立宿主插件系统 |
| D-09 | 多后端/导入 | 缺少 DSH、ACP 外部 Agent 后端，以及 Codex/Claude 等原生会话转换导入 |
| D-10 | 多模态工作流 | 图片输入/预览已有；缺少视觉桥、独立生图，以及参考仓库中的语音转写/视频生成工作流 |
| D-11 | 远程/自动化 | 缺少局域网 Web、实时推送、外网访问令牌/隧道、飞书和定时任务 |
| D-12 | 桌面集成 | 缺少托盘、系统通知/历史、自动更新、快捷任务浮窗、桌面宠物；代理、进程监控和诊断包也未完整对齐 |
| D-13 | 交互/长历史 | 项目级置顶/排序、标签拖拽/分屏、完整消息编辑删除、Mermaid/KaTeX 等未覆盖；UI 已虚拟化，JSONL 历史仍全量保存在内存（图片剪贴板粘贴与复制已覆盖） |

依据：[桌面对齐清单](02-desktop-parity.md)、[配置差距](03-pideck-configuration-parity.md)。PiDeck 的 `resources/extensions/pi-deck-ask-question.ts`、`pi-deck-todo.ts`、`pi-deck-plan-mode.ts` 依赖其扩展与 UI 宿主，切换到 PiWeft CLI 后不会自动可用；同等能力已由原生工具覆盖（D-01），但 PiDeck 自己的宿主插件体系不等于 pi Agent 扩展体系。

### 4.3 Rust/Go 补充方向

| 编号 | 方向 | 定位 |
| --- | --- | --- |
| X-01 | 原生子代理、并行/链式编排、worktree 隔离 | 高价值但涉及执行所有权、取消、凭据和补丁合并 |
| X-02 | ACP、IDE 集成 | 与 D-09 有关联；服务端协议与桌面接入外部后端应分别规划 |
| X-03 | LSP/AST/DAP | 语义检索、诊断、结构化编辑、调试；不是基础七工具的直接兼容要求 |
| X-04 | 长期记忆/检索 | 跨会话存储、索引、embedding 和隐私；不是会话压缩的替代 |
| X-05 | 后台任务/PTY 服务监督 | 需要生命周期、日志预算、取消和孤儿进程治理 |
| X-06 | 技能安全审计、能力/路径约束 | 用于增强安全；文件工具路径限制不等于 Shell/进程的 OS 沙箱 |
| X-07 | Web 搜索、浏览器/桌面自动化、增强编辑/媒体工具 | 按产品需求选择，可以通过 MCP 或宿主工具提供 |

原版 pi 的子代理和计划模式主要通过扩展提供，不能把 Rust/Go 的所有新增能力计为原版 pi 的必备缺口。

## 5. Jint 5 可行性与实施边界

### 5.1 版本与验证范围

评估时 GitHub/NuGet 正式版为 **4.17.0**。`main` 声明 5.0.0 开发线，预览源列出至 `5.0.0-preview-2249`。本次因预览包下载缓慢，使用固定源码提交构建，不能将结果标成正式版 5.0.0 或 preview-2249 的测试结果。

源码唯一构建调整是将 Jint 项目的目标框架限制为 `net10.0`，没有改变解释器运行时源码。正式采用时必须固定包版本/来源，审查许可证与依赖，并复跑目标版本；Jint 主分支 API 仍可能发生破坏性变化。

| 目标 | 判断 | Jint 之外的工作 |
| --- | --- | --- |
| codemode 基础执行 | 可行，匹配度高 | async 包装、工具桥接、输出、限制、取消、结果语义 |
| 接近 pi 的完整 codemode | 可行但不是仅引入解释器 | 暴露/发现、嵌套调用、分支 store、图片/分类器 API、协议输入适配 |
| 自有 JS 扩展 | 可行 | 工具/命令/事件/会话/UI、调度、重载、清理 |
| 原样运行 pi TS 扩展 | 不能开箱即用 | TS 转译、TypeBox/pi 模块、Node 行为与宿主契约 |
| 全面兼容 npm 扩展生态 | 工作量大，需独立决策 | 包解析、依赖、Node 能力、Provider、自定义 TUI、兼容测试 |

### 5.2 标准 JS 与 Node/TypeScript 的界线

- Jint 支持 ES modules、Promise、async 和 `EvaluateAsync/InvokeAsync`，可通过 `Tasks.RegisterPromise` 将宿主异步工作桥接为 JS Promise。
- 自动把 CLR Task/ValueTask 转 Promise 的互操作仍属实验性路径，初期不应依赖它；显式 Promise 桥接更便于控制线程与取消。
- Jint 不直接执行 TypeScript。扩展应在安装/构建时预转译或预打包并缓存，避免每次加载在解释器内运行完整 TS 编译器。
- Jint 5 的 Node 风格解析和 `path/url/querystring` 是可选能力；不是完整 Node。`fs/child_process/http/crypto/os/buffer` 等仍需要选择性宿主实现或明确不支持。
- TypeBox、pi 包导出、事件顺序、上下文转换、Provider 流和 TUI renderer 都是兼容层工作，Jint 不会替宿主完成。

### 5.3 建议的宿主结构

候选独立模块名为 `PiWeft.JavaScript`，尚未创建。应避免把解释器逻辑散入 Provider、CLI 或 UI。

| 部分 | 建议 |
| --- | --- |
| codemode | 每次执行新建受限引擎，包装 async 函数，允许 `tools`、`text/image/console`、描述/搜索和分支 store；结束后释放 |
| 工具调度 | 提取/复用 C# 调度入口，执行参数校验、前后钩子、许可、中断、阶段事件和用量汇总；禁止直接调用 `AgentTool.Execute` 绕过管线 |
| 嵌套调用 | 建立 parent/child 调用关系和有界记录；子结果不逐条进入模型上下文，只有脚本输出进入；失败、错误结果、未 await 调用都有确定语义 |
| 状态 | `store/load` 是受限 JSON，成功才提交；按会话分支恢复，不复用跨用户/跨信任域的 JS 对象 |
| 扩展 | 长期引擎保留注册回调；按扩展/会话隔离并串行调度；事件契约、重载和清理另行设计 |
| UI | 用结构化选择/确认/输入/编辑协议桥接 RPC 和 MewUI；不能直接执行 pi-tui 组件获得原生桌面控件 |
| 数据 | 显式 `ClrFunction`、JS 值及受限 JSON/DTO 转换，不暴露整个会话对象、HttpClient、任意 CLR 命名空间 |
| 加载 | 按需初始化；共享受限且有容量预算的 prepared AST，不以跨信任域引擎池作为隔离 |

现有 [AgentTool 类型](../src/PiWeft.Agent/AgentTypes.cs) 和 [工具执行链](../src/PiWeft.Agent/AgentLoop.cs) 是复用基础，但完整嵌套调用入口、输出 schema/暴露策略及父子事件契约仍需补齐。Jint 不会直接补齐 PI-06、PI-09 的 C# 宿主能力。

### 5.4 安全与 NativeAOT

- `ForUntrustedCode` 是起点，不是 OS 沙箱。默认禁用 CLR/反射、外部模块加载、字符串编译等，并设置有限资源预算。
- 它默认禁用 `eval/Function`，而 pi codemode 允许二者在同一 VM 内生成代码；这是应公开说明或另行设计的兼容差异。
- Jint 内存限制统计执行期间托管分配量，不是 QuickJS 独立 VM 堆、存活堆或进程 RSS 上限。输出复制、宿主数据和并发也要限制。
- 取消/超时是协作式，不能强行中断任意宿主回调或不返回的 BCL 操作；宿主工具必须接收自己的取消 token。
- `BeginOperation` 可将累计预算覆盖多次入口、异步执行和结果转换；不要在异步操作结束前释放作用域。Promise 默认时限也应按真实 I/O 调整，不能直接套用短演示值。
- prepared 脚本的解析预算应在准备阶段应用，执行时不会重新检查源码/AST。结果使用受限转换，不能对不可信对象直接做无限制 `ToObject/ToString`。
- 一个 Engine 只能有一个操作所有者。并行 I/O 不等于多线程操作 JS 对象；完成结果应通过队列/手动 Promise 安全回到引擎。
- 全局快照恢复不能撤销所有原型、对象图、模块及 CLR 状态变更，不是隔离边界；取消后的迟到回调要有退役/世代检查。
- 硬 CPU/内存/生命周期边界需受限可销毁进程或容器，并以能力 RPC 调用工具。仅把解释器放后台线程不能提供这一边界。
- NativeAOT 已验证所用 API 子集，但宽泛反射、运行时值类型泛型构造、裁剪后的成员访问仍有风险；应使用显式投影并独立审计警告。

## 6. Jint 性能探针结果

### 6.1 环境、方法与口径

- CPU：Intel Core i5-9300H；系统：Linux x64。
- SDK：10.0.401；运行时：.NET 10.0.12；workstation GC；Release。
- Stopwatch 微探针；每项预热 20 次，采样 30-5000 次；机器没有做性能隔离，不是 BenchmarkDotNet 或完整应用测量。
- 使用显式 `ClrFunction/JsObject/Tasks.RegisterPromise`，没有启用宽泛 CLR 对象投影或实验性自动 Task 转换。
- 受限负载使用 `ForUntrustedCode`。预算：200 万语句、64,000,000 bytes 分配、数组 100,000、入口/累计操作 10 秒、Promise 5 秒；其他维度沿用当时默认有限值。这是探针配置，不是建议生产默认值。
- 通常复用 prepared 脚本；近似 codemode 项则每次创建引擎并重新准备脚本，还覆盖 `BeginOperation` 和受限结果转换。

### 6.2 最终运行均值

| 工作负载 | JIT | NativeAOT |
| --- | ---: | ---: |
| 进程内首次创建引擎、解析、求值 | 275.26 ms | 174.56 ms |
| 热进程新建空引擎并释放 | 13.00 us | 7.37 us |
| 热进程新建受限引擎并释放 | 13.35 us | 5.84 us |
| 新引擎解析并执行极小脚本 | 17.43 us | 6.28 us |
| 热引擎执行 prepared 极小脚本 | 0.82 us | 0.40 us |
| 简单受限扩展 hook | 4.03 us | 0.91 us |
| 1000 次显式同步宿主桥接 | 0.79 ms | 0.52 ms |
| 20 次立即完成的 Promise 桥接 | 0.147 ms | 0.065 ms |
| 20 个各等待 50 ms 的宿主调用并发 | 55.34 ms | 50.87 ms |
| 47,391 字符/1000 条 JSON 的受限解析筛选映射 | 2.74 ms | 1.17 ms |
| 每次新引擎的近似 codemode 流程 | 1.68 ms | 0.89 ms |
| 近似 codemode 流程 p95 | 2.71 ms | 1.33 ms |
| 10 万次 JS 数值循环，无受限配置 | 7.61 ms | 11.61 ms |
| 同一 JS 循环，受限配置 | 12.02 ms | 15.29 ms |
| 对应 C# 数值循环 | 0.057 ms | 0.050 ms |

其他运行的首次求值为 JIT 290-324 ms、AOT 142 ms；冷路径不是稳定统计分位数。不同负载的 JIT/AOT 优劣不能外推。

近似 codemode 包含创建受限引擎、解析短 async 脚本、以 JSON 参数调用模拟宿主函数、通过 Promise 获取约 47 KB JSON、筛选 1000 条记录、序列化摘要和受限转换。不包含真实文件/网络/模型 I/O、完整许可和 Agent 调度。hook 使用已在引擎内的小对象，不包含复制完整会话；直接桥接不包含全套 JSON/参数校验。因此不能把微秒数当成完整工具调用开销。

实际探针通过：模块导入/命令注册/回调、async/Promise.all、跨线程手动 Promise 完成、无限循环取消、受限环境中 `process/require/fetch/importNamespace` 不可见、`eval` 被禁用。

### 6.3 内存、体积和警告

| 指标 | 实测 | 限制 |
| --- | ---: | --- |
| 保留 100 个执行过小型工具/JSON 负载的受限引擎 | AOT 增加 7,861,880 bytes；JIT 7,881,408 bytes，约 77 KiB/引擎 | 是小负载托管存活增量，不是 RSS、复杂扩展图或保证上限 |
| 10 万次 JS 循环临时分配 | 约 2.87 MB/次 | CPU 脚本也会增加 GC 压力 |
| JSON 筛选临时分配 | 约 198 KB/次 | 大数据和重复复制会放大成本 |
| 近似 codemode AOT 临时分配 | 约 181 KB/次 | 不包含真实工具的宿主内存 |
| NativeAOT 探针可执行文件 | 19,160,720 bytes，约 18.27 MiB | 独立探针，不是 PiWeft 发布包 |
| 空控制台 AOT 基线 | 1,270,840 bytes，约 1.21 MiB | 与现有应用的共享代码不同 |
| 探针与基线之差 | 约 17.06 MiB | 不是接入 PiWeft 后的精确体积增量 |
| AOT 发布 | 成功并运行全部探针 | 有 Jint `IL2104`/`IL3053` 汇总警告，不是无警告依赖 |

Jint 官方比较页当时的性能表仍以 4.16.0 为基准，不能当成 Jint 5 的实测，也不能据此断言 Jint 5 总体比 QuickJS/V8 更快。本次没有同机 QuickJS 对照。

### 6.4 对本项目的实际影响

- 小型编排、轻量 hook、结果过滤通常是毫秒级成本，相对秒级 I/O 占比较小；不承诺固定百分比。
- 首次使用约百毫秒初始化值得做懒加载；未启用功能时不创建 VM，但原生链接体积成本仍可能存在。
- 大循环、大 JSON、高频 token hook 可能显著增加 CPU、复制和 GC，应将重计算留给 C#，合并/限频非必要事件。
- 实际扩展数量、会话并发、上下文大小、工具数据量和引擎生命周期决定内存，不能用空引擎分配估计整个工作台。
- 接入后应测真实 CLI/桌面发布包的启动、首次调用、稳态、并发、RSS、分配、GC 和包体积，而不是只复跑极小脚本。

原始实验位于 `/tmp/piweft-jint-eval-D9k8ZX/`，含 `REPORT.md`、`bench/Program.cs`、`jit-results-final.txt`、`aot-results-final.txt` 和构建日志。临时目录不保证长期存在；本文已保留主要数据、版本、配置和口径。正式试点应将可复现探针迁入仓库工具/测试，再据此制定门槛。

## 7. 建议分阶段补全

以下阶段不是已批准排期。产品功能线与 JS 技术试点可在独立模块中并行推进，但共享输入/工具/UI 契约应先确定。

| 阶段 | 范围 | 状态（2026-10-15） | 前置条件 | 退出标准 |
| --- | --- | --- | --- | --- |
| S0 兼容回归 | C-01 至 C-06 | 已完成，见第 3 节 | 对照目标 pi 行为 | 失败用例先行；CLI/RPC/桌面实际输入一致；公开能力说明与实现吻合 |
| S1 安全和运行时基础 | PI-11、共享输入处理、工具调度和结构化 UI；D-01 可先原生实现 | 已完成：信任、统一输入展开、原生 Ask/todo/plan 均已落地 | S0，明确许可与项目资源加载边界 | 不可信项目资源不会静默执行；嵌套工具不绕过钩子；Ask/中断/关闭不会挂起 |
| S2 Jint 试点/codemode MVP | PI-05 的基础部分 | 未开始；范围变更未批准 | S1 工具契约，批准 JS 范围变更 | 固定 Jint 版本；有限能力；错误/取消/并发/输出预算；Linux AOT 实跑，报告警告和实际包增量 |
| S3 有限扩展宿主 | PI-01/PI-02，按需 PI-10 和 D-01 | 未开始 | S2 的投影/调度与 UI 基础 | 明确支持 API 清单；指定扩展样本通过；重载/清理/分支恢复；未支持导入有可理解错误 |
| S4 高频工作台闭环 | D-02 文件树/编辑器、D-04、D-06、PI-06、PI-08、D-07 基础统计 | 大部分已完成，剩余见第 4 节各条目「未覆盖」 | 各领域协议/认证/文件安全设计 | 连接探测不误报；Git 写操作与冲突处理；配置作用域和凭据隔离；MCP 保存/重连行为明确 |
| S5 产品扩展 | PI-03/PI-04/PI-09、D-03/D-05/D-08 至 D-13、X-* | 未开始 | 范围和成本决策 | 分领域单独验收，不捆绑为一次“全功能对齐” |

S4 可以与 S2/S3 并行，不要求文件树或模型探测等待完整 JS 宿主。D-01 也可以直接用 C# 实现；“获得问询/待办能力”和“兼容现有 JS 扩展”是不同目标。

### 7.1 codemode 分层验收

1. 基础执行：async/return、工具成功/失败、`Promise.all/allSettled`、输出顺序、禁止递归 codemode。
2. 工具契约：参数校验、许可钩子、父子 ID、阶段事件、图片、structuredContent、用量汇总；无 await 调用结束时取消。
3. 资源治理：源码/AST/递归/语句/分配/输出上限、不可结算 Promise、脚本取消、宿主超时、迟到完成和进程隔离策略。
4. 会话治理：成功才提交 store；恢复/fork/树导航各见本分支状态；失败保留已产生输出，不宣称已执行写工具能自动回滚。
5. 性能：真实代表脚本、生产限制配置、冷/热路径、1/多会话并发；记录机器、版本、分配/RSS/GC、p50/p95 和包体积。
6. 高级能力另行验收：延迟发现/暴露、声明预算、分类器/生图 API、原始脚本输入与 JSON 包装的协议差异。

### 7.2 扩展兼容验收

- 先建立支持矩阵：工具、命令、事件、状态、标准 UI、Node 模块、TypeBox、Provider、renderer 各自标明支持/部分/不支持。
- 首批选择小工具、生命周期钩子、待办/计划和标准 Ask 扩展；涉及 Node 文件/进程、Provider 流、自定义 TUI 的样本另分级。
- 在指定 pi/PiDeck 版本上验证真实交互，不用注册成功或 RPC 握手替代功能 e2e。
- 验证事件顺序、工具阻止、并行调用、重入、异步清理、重载后的旧回调失效及跨会话隔离。
- TS 转译/依赖缓存也要限容量和校验来源；不能因支持导入而默认授权所有模块。

### 7.3 制定正式计划前的决策

| 决策 | 必须明确的内容 |
| --- | --- |
| 产品定位 | 轻量原生工作台、pi 协议替代，还是完整 pi + PiDeck 功能替代 |
| 扩展目标 | 自有 JS API、指定 pi 扩展子集，还是 npm 生态兼容；哪些 API 明确拒绝 |
| Jint 版本 | 等待正式版、固定 preview，或维护源码构建；升级和安全修复策略 |
| 隔离等级 | 可信扩展/不可信 codemode 的能力、进程、文件、网络和凭据边界 |
| NativeAOT 门槛 | 警告审计和可接受体积增量；不要全局静默忽略分析器警告 |
| 性能预算 | 使用目标硬件和真实脚本建立首次初始化、p95、并发、RSS、分配和包体积门槛 |
| 桌面范围 | 终端/浏览器/托盘/更新/通知等是否推翻当前排除决策 |
| 验证平台 | Linux、Windows、macOS 原生运行与打包；Jint 探针目前只证实 Linux x64 |

## 8. 资料与维护

- [pi](https://github.com/earendil-works/pi)、[PiDeck](https://github.com/ayuayue/PiDeck)、本地 Rust/Go 参考仓库。
- [Jint 仓库](https://github.com/sebastienros/jint)。以下 Jint 文档固定到实测提交，避免将后续主分支变化当成原结论：
- [异步](https://github.com/sebastienros/jint/blob/8537426f92ee847ccb09af59b1db60079e3a8028/docs/guide/async.md)、[线程安全](https://github.com/sebastienros/jint/blob/8537426f92ee847ccb09af59b1db60079e3a8028/docs/guide/thread-safety.md)。
- [不可信代码](https://github.com/sebastienros/jint/blob/8537426f92ee847ccb09af59b1db60079e3a8028/docs/guide/untrusted-code.md)、[限制与结果转换](https://github.com/sebastienros/jint/blob/8537426f92ee847ccb09af59b1db60079e3a8028/docs/guide/constraints.md)。
- [Node 兼容](https://github.com/sebastienros/jint/blob/8537426f92ee847ccb09af59b1db60079e3a8028/docs/guide/node-compatibility.md)、[v5 迁移/AOT](https://github.com/sebastienros/jint/blob/8537426f92ee847ccb09af59b1db60079e3a8028/docs/guide/migrating-to-v5.md)。
- [性能方法](https://github.com/sebastienros/jint/blob/8537426f92ee847ccb09af59b1db60079e3a8028/docs/guide/performance.md)、[官方引擎比较及版本口径](https://github.com/sebastienros/jint/blob/8537426f92ee847ccb09af59b1db60079e3a8028/docs/guide/engine-comparison.md)。

功能完成后，应更新当前状态文档、给对应编号关联测试/实现并重新标注状态；不要把本文的日期快照当成永久缺口清单。正式计划应从“范围决策 + S0 回归 + S2 可复现试点”开始，不以未经定义的完成百分比或工期承诺代替这些步骤。
