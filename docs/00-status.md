# PiWeft 状态

PiWeft 把 [pi](https://github.com/earendil-works/pi) 的核心移植为 C# / .NET 11：NativeAOT CLI `pi` 与 MewUI 桌面工作台 `piweft-desktop`。会话文件与 pi、PiDeck 互通，但不是完整移植。

构建/打包见 [README](../README.md)；兼容与差距见 [01-parity](01-parity.md)；性能见 [02-performance](02-performance.md)；codemode 见 [03-codemode](03-codemode.md)；Markdown 见 [04-markdown](04-markdown.md)。

## 架构

| 项目 | 职责 |
|---|---|
| `PiWeft.Ai` | 消息、JSON、模型目录（含虚拟模型）、费用、认证、provider 流式适配、生图/分类、Codex WebSocket |
| `PiWeft.Agent` | 代理循环、串行/并行工具、steering、follow-up、事件与中断 |
| `PiWeft.CodingAgent` | 会话、内置工具、资源发现、设置、压缩、分支摘要、重试、统计、安全门 |
| `PiWeft.JavaScript` | Jint 5 沙箱、codemode 源码解析与 TypeScript 声明 |
| `PiWeft.Imaging` | 图片管线：按模型 `inputLimits.images.resize` 应用 EXIF 方向、Lanczos3 缩放、PNG/JPEG 字节预算选择（Sdcb.SimdCodecs 分格式编解码） |
| `PiWeft.Mcp` | stdio / streamable HTTP、分页、工具、资源、提示、通知与 OAuth |
| `PiWeft.Cli` | 参数与环境装配、text / print / JSON / RPC、认证、HTML 导出、宿主日志（脱敏、崩溃记录、诊断包） |
| `PiWeft.Markdown` | 增量 Markdown 文档模型、块级 diff、分块布局与精确高度回收 |
| `PiWeft.Desktop` | 多项目、多会话工作台，进程内驱动 `CodingAgentSession` |
| `tools/` | AOT 冒烟、MCP fixture、基准、截图脚本、图标获取 |

- .NET 11、`LangVersion=preview`（C# 15）、Runtime Async、制表符缩进；文档、界面文案与注释以中文为主。
- Runtime Async 由 `Directory.Build.props` 的 `Features=runtime-async=on` 全局开启：async 方法不再生成状态机，热执行栈只剩真实帧，CLI 的 NativeAOT 产物体积 -2.3%。少数写法（自定义 `AsyncMethodBuilder`、async 方法里的 `ref`/`unsafe` 布局）不受支持；SDK 11.0.100 不接 `UseRuntimeAsync` 开关，单个项目要退出就在它的 csproj 里覆写 `<Features></Features>`。
- 种类固定的抽象记录标 `closed`（内容块、助手事件、会话条目、分类问题/答案、codemode 输出项）：可行类固定在程序集内，消费方的 `switch` 不写兜底分支也能通过编译，新增种类时会直接编译报错而不是静默走默认分支。
- 会话读取管线用 C# 15 union 代替 `object`：`SessionLine` 只有头部与条目两个 case，`List<object>` 变成 `List<SessionLine>`，少了裸转型。union 是 8 字节 struct，与引用等宽，读入不装箱；`OfType<T>()` 在 union 序列上恒为空（CA2021 会拦），过滤一律用模式匹配。逐行解析走 `Utf8JsonReader`（`SessionJsonReader`），不建 `JsonNode` 树，大文本块直接接管 UTF-8 字节；写回用 `Utf8JsonWriter`，延迟存储的正文直接写 UTF-8 字节（`JsonUtf8String`），不经过 UTF-16。
- `SessionManager` 的线程契约：`_stateGate` 保护条目表、索引、标签与叶子。所有写路径（含 `Append*` 的 id 生成与父链接）都在闸内；对外读属性要么取闸（`Entries`/`Header`/`Labels`/`EntryCount`/`Revision`），要么只做一次引用读（`LeafId`）。`Labels` 返回冻结快照，拿不到线上字典。并发用例见 `SessionManagerConcurrencyTests`：多写线程追加后条目链必须是一条线，读线程枚举标签/条目不得抛异常。
- JSON 使用 `System.Text.Json` 源生成与手写转换器；不使用 Newtonsoft.Json 或供应商 SDK，保留 trim/AOT 分析器。
- 核心库默认无第三方运行时包，例外两处：codemode 的 Jint 5 预览版（`Directory.Packages.props` + `Jint-preview` 源），以及 `PiWeft.Imaging` 的 Sdcb.SimdCodecs 分格式编解码包（纯托管、`net10.0` 单目标、`IsAotCompatible`，只引 Png/Jpeg/Gif/Webp/Bmp/Abstractions，不引会拖上 TIFF/J2K/JBIG2 的门面包）。桌面只用 `external/MewUI` 源码子模块与 `PiWeft.Markdown`（Markdig）。MewUI 的 X11 平台层自带 `Tmds.DBus.Protocol`（AT-SPI2 用）；本项目不开无障碍，裁剪后不进产物。
- 模型目录以内嵌 `src/PiWeft.Ai/Resources/models.json` 为基线（43 个 provider），`<agentDir>/models-store.json` 缓存远程目录。设置页「模型目录」下载 pi-ai npm 整包 tarball 生成最新目录写入 `<agentDir>/model-catalog.json`（GitHub main 单文件兜底），tarball 过 registry SHA-512、包内逐文件 SHA-256，覆盖层解析结果在进程内缓存；保留上一个覆盖版，桌面启动时每天自动检查一次，CLI 与桌面读取同一份覆盖层。内嵌基线含 GPT-6.2 Astra/Sol（OpenAI、Azure、Codex），`openai` / `azure` / `openai-codex` 的默认模型是 `gpt-6.2-sol`；两者拒绝 `none`/`minimal`，思考档从 `low` 起。虚拟模型支持静态 `"virtual":{"model":...}` 与动态 `"virtual":{"route":"..."}`，状态写成 `pi.virtual-model-state` 条目。
- 不实现 TypeScript 扩展（JS 扩展宿主已实现）、mermaid、KaTeX、托盘、桌面通知、应用自更新、崩溃上报、内嵌浏览器；终端用独立 PTY Dock，不嵌第三方终端组件。codemode 默认不启用。

## 兼容基线

- CLI 产物/程序集名保持 `pi`；`--version` 报告兼容的 pi 版本 **1.6.0**（PiDeck 拿它做功能门槛与更新比较），RPC 契约版本 1.3.0 只出现在 `/changelog` 与 `--bug` 报告里。
- 保留 `PI_CODING_AGENT_DIR`、`PI_LOG_LEVEL`、`~/.pi/agent`、项目 `.pi`、配置文件名与 v3 JSONL 会话格式。损坏的会话头 `cwd` 回退到当前目录。
- provider 行为与 1.6.0 对齐：Gemini 版本判定不再要求 ID 以 `gemini` 开头（`vendor-gemini-4-argon` 同样拿到 strict 工具、工具调用 id 与函数响应图片），`compat.supportsStrictMode` 可覆盖；Codex 的系统提示词作为首条 `developer` 输入项发送（GPT-6.2 上不再每轮击穿提示词缓存），`supportsMidConvoEffort` 的模型（Codex、OpenAI Responses、Azure）用 `configuration_update` 切换思考档，`max_output_tokens` 最低 16；Copilot 的 Responses 与 Claude 默认发严格工具；分类器用量把 `input_tokens_details` 的缓存读写拆出 `input`。
- 助手诊断：`AssistantMessage.diagnostics` 原样读写并随会话落盘。OpenAI 丢弃 reasoning、Anthropic 丢弃 thinking 块时各写一条 `*_input_transformations`；`settings.showCacheMissNotices` 打开后桌面与 CLI 提示丢弃计数与最后一条请求的明显缓存未命中（>20k token 或 >$0.1），文案与 pi 一致，默认关闭。
- 自动重试的瞬态错误表补到 pi 1.6.0：Cloudflare 525、网关 `provider is temporarily unavailable`、`api_error` 事件、OpenRouter 空响应与提前结束、`admission ceiling`、`repeat the request later` 等。
- bash/powershell 尾部截断在超长行后跟空行时保留行尾（pi #12355），状态行报输出第一行的行号。
- 日志：`<agentDir>/logs/piweft-<app>-<pid>-<日期>-<序号>.log`，一行一条，按天与 4 MB 滚动，保留 14 天、最多 31 个文件。`PI_LOG_LEVEL` 或设置页「日志 → 日志级别」控制级别，改动立即生效；设置页可筛选、搜索、分页、复制、导出诊断包与清空。异常记录在写入前、诊断包在导出前都过脱敏：家目录 → `~`、agent 目录 → `<agent>`、工作目录 → `<workspace>`、凭据 → `***`。异常记录保留类型、脱敏后的消息、内部异常链与栈。
- 崩溃记录：`<agentDir>/crashes.json` 保留最近 5 条、7 天内的记录，下次启动提示一次（桌面 toast，CLI stderr）。
- 系统提示词只在运行时构造，不写入会话；章节拼进一条消息后不再保留 `Sections`，provider 取初始系统文本时不会重复追加。未知字段原样保留：会话头、条目顶层、已知角色的未建模字段（`Extra`）、未知角色整体（`CustomMessage.Raw`）、未知条目（`UnknownSessionEntry`）；`usage` / `usage.cost` 的未知字段同样保留。新增已知角色字段时须同步 `MessageJsonConverter` 与 `SessionJson.KnownKeys`。
- `find` 的路径型 pattern 锚定搜索目录（`src/*.ts` 不再匹配 `pkg/src/a.ts`，要任意深度写 `**/src/*.ts`），开头的 `./` 会剥掉，绝对 pattern 按绝对路径匹配；`read` 的「模型不支持图片」只写进 details，不进结果正文。
- 默认工具与 pi 一致：`read`/`bash`/`edit`/`write`，另保留 `todo`、`ask_question` 与 `tool_search`（MCP 工具默认是 codemode exposure，靠它发现）；Windows 默认含 `powershell`。`grep`/`find`/`ls`/`codemode` 需显式开启。`--tools` 支持整体替换、`+name`/`-name` 与 `--exclude-tools` 通配。
- `write`/`edit` 就地写入（与 pi 的 `fs.writeFile` 同语义）：保持 inode、硬链接、权限位与已打开句柄；会话/设置/导入这些需要原子性的落盘仍走同目录临时文件 + rename。
- 图片进上下文前按模型 `inputLimits.images.resize` 处理，`read` 工具与所有用户附件共用同一条管线。限制内原样保留（动画 GIF/WebP 不动）；超限先应用 EXIF 方向，按 Lanczos3 缩到 `maxWidth/maxHeight`，再在 PNG 与 JPEG 质量阶梯（`jpegQuality`/85/70/55/40）里取能进 base64 字节预算的最小者，仍超就按 0.75 递减到 1×1。输出带尺寸映射括注；无法解码的图只留一句括注，坏数据不进上下文。`settings.images.autoResize` 关掉后三条入口都只做格式归一（AVIF/HEIC 转 PNG），尺寸与编码原样发送，也不补尺寸括注。
- 工具结果的图片（MCP、扩展、截图工具）也在进入历史前按同一套限制处理，括注作为额外文本块追加；与 `read` 的区别是处理失败时保留原图块（工具已经产出它，删掉等于改变工具行为）。`settings.images.blockImages` 在发送路径与请求投影两层生效：发送时识别到的图直接换成括注，历史里已有的图（含扩展注入的 `CustomMessage`）在请求投影时过滤，与 pi 在 `convertToLlm` 里的兜底同义。
- 附件归一化在进上下文时才做：`PrepareInput` 保持同步，计划模式标记与模板展开要立即生效；`PromptAsync` 与 steering/follow-up 出队时在线程池上跑解码/缩放（`AgentOptions.NormalizeQueuedMessage`），排队期间换模型也会按新限制重新处理。`read` 工具同样把读文件与图片处理交给线程池。
- bash / powershell 子进程注入 `PI_SESSION_ID`、`PI_SESSION_FILE`、`PI_PROVIDER`、`PI_MODEL`、`PI_REASONING_LEVEL`，启动时设置 `AI_AGENT=pi`、`PI_CODING_AGENT=true`；`settings.shellPath` / `shellCommandPrefix` 控制 shell 与前缀。
- bash 路径、输出与收尾：命令输出进入上下文/会话前剥掉 ANSI 与 NUL/控制字符（工具保留 CR，与 pi 的精确输出一致），完整输出临时文件为 0600；shell 先退出、后台进程仍占管道时 timeout 仍然生效，并在状态行报告 shell 的真实退出码。每次调用把一整条命令装进容器：Linux 用 systemd user scope（cgroup，命令自己 `setsid` 也跑不掉），Windows 用 `KILL_ON_JOB_CLOSE` 的 Job 对象，其余 Unix 用 `setsid` 进程组。命令正常结束也杀掉容器里的残留（`dotnet build` 的 MSBuild 节点与 VBCSCompiler 因此不会活过工具调用），取消/超时按同一容器处置，没有容器时退回整树 kill。用户 `!` 命令的流式事件是 `bash_execution_update.delta` 文本块并去掉 CR，与 pi 的 bash-executor 一致。
- 工具参数校验覆盖 `$ref`/`$defs`、`patternProperties`、`prefixItems` 与单值转数组（pi 1.5 的 pi-schema 子集）；不支持 TypeBox 的 `Refine`/codec 这类超出 JSON Schema 的扩展。`ask_question`/`todo` 的根 schema 不设 `additionalProperties:false`，与 PiDeck 一样容忍旧式顶层字段与未知字段。
- 生效 settings：`shellPath`、`shellCommandPrefix`、`sessionDir`、`defaultTools`、`cacheWarming`（默认 `idle`）、`showCacheMissNotices`（默认关）、`thinkingBudgets`、`images.autoResize`、`images.blockImages`、`steeringMode`、`followUpMode`、`modelThinkingLevels`、`enabledModels`、`branchSummary`、`compaction`（含 `modelOverrides`）、`retry`、`transport`、`httpIdleTimeoutMs`、`httpProxy`、`extensions`、`skills`、`prompts`、`themes`、`packages`、`enableSkillCommands`、`defaultProjectTrust`、`sessionShareDestination`、`codemode`、`security`。其余键保留不生效。
- 启动迁移与 pi 的 `migrations.ts` 对齐：`oauth.json` / `settings.apiKeys` 并入 `auth.json`；根目录 `*.jsonl` 搬入 `sessions/<cwd>/`；`commands/` → `prompts/`；v1/v2 会话升级到 v3 并原子回写。`--export` 只读。
- 编辑重发不新建会话：`TruncateForResend` 在原 JSONL 里把放弃的提问及后继写成 `deleted` 墓碑，叶子回到父条目。墓碑不进投影/分支树，重试与删除仍走 `context_edit`。编辑助手回复走另一条路：追加 `context_edit` 换掉投影里的正文，原条目留在文件里，不截断后续回合也不重跑。
- 模型回退与模式无匹配要报出来：会话记录的模型已删除时回退到宿主默认，同时写一条 `Could not restore model ...` 警告（CLI stderr、桌面 toast）；`--models` / `settings.enabledModels` 里没有匹配的模式报 `No models match pattern`，不拦启动。
- 手动继续生成与自动重试分开：上一轮是瞬态失败、输出被截断，或换模型/供应商后可能恢复的失败（配额、余额、凭据、模型不存在、上下文超限）后，草稿为空且没有排队的用户消息时，发送键变「继续生成」。它不新增提问，先追加一条空的隐藏 `piweft-continue` custom message 再续跑；重放时空正文被 provider 丢掉，所以模型看到的是同一个提问或被截断的半截回答。请求参数、内容过滤、证书校验失败不接（重跑不会变好，交给标题栏「重试」或修配置）。主动中断总能继续：正文与思考一起重放接着写；连思考都没有、只剩半截工具调用时按重提跑一轮，不新增用户提问；工具批次已落盘时模型接着处理结果。配额/余额这类不自动重试，但可以换模型后点继续，省掉手打一条「继续」。排队的注入上下文（子代理回报、关联会话消息）不拦继续，随本次请求一起送出。继续失败后仍保留继续资格。
- `--system-prompt` 替代 preamble，`--append-system-prompt` 追加，`--no-context-files` 停用 AGENTS.md / CLAUDE.md；`--export` 导出 HTML/JSONL，`--safe` 脱敏；`--models` 限定轮换范围。HTML 导出的正文按 Markdown 渲染（表格、列表、代码块），原始 HTML 与脚本式链接不执行；工具输出仍走 ANSI 转内联样式。
- 退出收尾：CLI 接 SIGTERM/SIGHUP，第一次走优雅关闭，清理期间再收到才强退；退出前等 OAuth 刷新写完 `auth.json`，避免供应商已轮换 refresh token 而本地没保存。
- `pi install/list/remove/update` 管理 npm/git/本地包，`--local` 写项目 `.pi/settings.json`；`settings.npmCommand` 可覆盖 npm。`pi update` 提示用发行页更新 NativeAOT 本体，`pi update --extensions` 更新包并报告 npm 版本或 git commit 变化。llama.cpp router 默认 `http://127.0.0.1:9931`。
- `pi mcp list|add|remove|login|logout` 管理服务器与 OAuth；配置写 `<agentDir>/mcp.json`，令牌写 `<agentDir>/mcp-auth.json`（与 pi 同一份文件、同一套键与状态形状，旧的 `mcp-oauth.json` 首次读取时迁移）。写入保留其它键、注释与尾逗号。
- CLI `@file` 展开为 `<file>` 文本块，图片作为内容块；print/JSON 模式合入重定向 stdin；RPC 拒绝 `@file`。
- 技能命令 `/skill:name` 与提示词模板在 `CodingAgentSession` 内统一展开。交互命令含 `/model`、`/thinking`、`/compact`、`/plan`、`/reload`、`/copy`、`/todo`、`/import`、`/bug`、`/new`、`/resume`、`/session`、`/name`、`/tree`、`/fork`、`/clone`、`/export`、`/scoped-models`、`/mcp`、`/trust`、`/settings`、`/login`、`/logout`、`/hotkeys`、`/changelog`、`/help`、`/share`、`/llama`。`!command` 输出并入对话，`!!command` 只执行。
- `-e/--extension` 加载显式扩展文件/目录/`builtin:<name>`/`npm:<pkg>[@ver]`/`git:<repo>@<ref>`；后两者物化到 `<agentDir>/extension-packages/`（无精确版本时每次启动重装或跟分支），离线只认缓存，失败进诊断。`-ne/--no-extensions` 只关闭约定发现。未知 `--flag` 交给扩展 `pi.registerFlag`，无人注册时退出 2。`settings.extensions` 的 `+path`/`-path`/`!glob` 过滤扩展；`-builtin:mcp`、`-builtin:codemode`、`-builtin:tool-search`、`-builtin:llama.cpp`、`-builtin:subagent` 关闭对应原生能力。桌面设置页「扩展」提供这些开关。
- 网络：`httpIdleTimeoutMs` 默认 300 秒，按读取续期；只切断停滞流，不限制长流。严格区分用户中止（`aborted`，不重试）与空闲超时（`error`，可重试）。TLS/SSL 握手失败与 Windows 的连接被强制关闭按传输瞬态重试，证书校验失败不重试。错误文案带内层原因（证书 / 握手），`PI_DEBUG_ERRORS=1` 给完整堆栈。`httpProxy` 只读全局设置。`--offline` / `PI_OFFLINE=1` 跳过自动网络操作。
- 项目信任：`<agentDir>/trust.json` 从 cwd 逐级向上取第一个决策；`-a/--approve`、`-na/--no-approve` 覆盖；默认 `ask`。不受信时不合并项目设置、不连项目 MCP、不读项目模型/技能/提示词。RPC/print 无法弹框时按不受信处理。
- 凭据按显式选项、存储凭据、环境变量异步解析；OAuth 刷新有闸门且不跟随请求取消。可恢复错误按 `settings.retry` 退避；供应商 `Retry-After` 优先；要求等待超过 15 分钟的配额冷却不自动重试，直接把错误交回界面。失败尝试保留在文件里，通过 `context_edit` 排除出上下文。
- 请求过大（`prefill_cap_refused`、413、payload too large、上下文超限）进入 overflow recovery：排除失败尝试后压缩；摘要请求本身过大时按 token 递归对半拆分，先摘要前半段再把它作为 `previousSummary` 摘要后半段。压缩/上下文编辑之后的最后一次 usage 不再充当上下文估算锚点，避免压缩后仍显示 600K。
- 溢出识别不只靠错误文本：`input + cacheRead` 超过窗口的静默溢出、`length` 结束且输出为 0、以及 `context window exceeded` 一类错误都算。`length` 结束但输出没到模型上限时做一次「排除半截回复 + 压缩 + 重试」。失败的尝试连同配对的工具结果一起用 `context_edit` 排除。
- 自动压缩失败只发 `compaction_end` 错误，不拦用户提问，也不中断正在跑的工具回合；摘要请求只重试瞬态错误，且不写缓存前缀。摘要末尾附 `<read-files>` / `<modified-files>`（跨压缩累积），压缩检查点保存合并后的系统提示词。
- PiDeck 私有名字（`pi-deck-*`、`__PI_DECK_*`、`[PI_DECK_*]`）集中声明在 `Compatibility/PiDeckProtocol.cs`，形状由 `PiDeckProtocolTests` 固定；纯内部名字用 `piweft-` 前缀。
- PiDeck 宿主通道：`PIDECK_SECURITY_CONFIG`（策略快照接管档位/路径/危险命令）、`PIDECK_MODEL_TRACE_URL`（模型请求快照）、`PIDECK_AUTO_SESSION_TITLE`（首轮自动命名，先 `setStatus("pideck:auto-title")` 再改名）都已接线；关联会话的关系与提议落在 `<agentDir>/linked-sessions.json`，重启后仍可见（子会话按 `stopped` 恢复）。
- PiDeck 0.12 互通：`/goal pause|resume|clear`、`/plan [off]`、`/reload` 在 RPC prompt 路径返回 `disposition:"handled"`，不写用户消息；`get_commands` 列出扩展命令与虚拟 `reload`（来源路径以 `pi-deck-runtime.ts` 结尾）；ask 支持 `skipped` 与部分关闭（`closed`）；关联会话结果同时给 camelCase 与 snake_case，`start_session` 支持 `proposal_id`。
- 子代理是会话原生能力（`delegate` / `wait_subagents` / `list_subagents` / `read_subagent` / `send_to_subagent` / `stop_subagent`）：异步委派给带角色的子会话，最终回复作为消息回到父会话，父会话空闲时直接起一轮。角色来自 `.pi/agents/**/*.md`、`<agentDir>/agents/**/*.md` 与内置 `scout` / `reviewer` / `oracle` / `worker`，`advertise` 决定是否进 `delegate` 的模型可见目录，完整目录用 `list_subagents`；有宿主的会话另提供 `/scout` / `/parallel-review` / `/review-loop` 内置提示词模板；深度上限 1，并发上限 4，每个用户回合最多启动 32 个；`delegate` 可给 `label`，`wait_subagents` 可 `any: true` 只等第一个完成；子代理默认不开标签（`settings.subagents.openTabs` 可回到旧行为）：卡片行按需把活会话挂成标签，无标签时提问显示在父标签并标注来源，回合结束后的下一拍回收会话对象（仍可冷恢复）；侧栏仍把子会话挂在父会话下；输入区子代理条可展开看任务、进度、结果与用量，可打开或停止；运行中的耗时自己走秒，子代理提问时卡片与父标签显示「等待回答」并弹 toast；`context: fork` 截到最后一个完整回合；计划模式与工具白名单收窄会传给子会话；`delegate` 支持 `output_schema`（JSON Schema 子集，校验失败标 failed）；`send_to_subagent` 支持 `follow_up`/`steer`/`auto`，子代理停在 `ask_parent` 上时该消息直接当答案投递；子代理可以用 `ask_parent` 问父会话（`wait_subagents` 提前返回 `reason: question`，20 秒没人答转给用户的问询卡，5 分钟超时，单轮最多 3 次），提问与等待都不计入运行时限；连续 6 次回报拉起父会话后停投；单次运行默认 30 分钟，到期前先 steer 收尾、到点硬停并把已写出的正文一起投递；等用户回答时暂停计时；`delegate` 可给 `verify`（宿主自己跑的命令，退出码与输出进结果，失败即标 failed）、`max_tokens` 与 `timeout_ms`；`settings.subagents` 可改并发、每回合启动数、运行时限、token 上限与验证超时；结果没送到就退出时，重启后按 `deliverable` 标记补投一次；fork 历史超过 100k 字符只保留最近一段；关系条目落盘（含任务全文、模型、思考级别与会话文件），重启后恢复列表并能冷恢复继续对话；结果在父会话运行中以 steer 投递（当前工具批次结束、下一次模型请求前），空闲直接起一轮；`wait_subagents` 与投递按轮二选一；工具行按字段展示；回报按助手排版渲染，不算用户提问（`UserMessage.IsUserAuthored`，分叉、重试、标题、导出都不认它）；`customType`/`display` 与人 pi 同形状落盘，重启后仍是回报。`-builtin:subagent` 关闭。详见 [05-subagent](05-subagent.md)。
- 关联会话是桌面原生能力（`start_session` / `send_to_session` / `list_linked_sessions`）：子会话是同一项目的完整后台会话，独立标签；每轮结束把最后一条回复前 400 字回报父会话，空闲时直接起一轮、忙时 steer 到下一次模型请求前；`send_to_session` 的 `mode` 可取 `auto` / `steer` / `follow_up`。连续 6 次回报自动续跑后停止，用户发消息清零。`offer` 出提议卡等用户选「新会话 / 在此做 / 不要」（最长 10 分钟）；`own_initiative` 只登记卡片、工具立即返回。输入区上方的子会话条列出待决定提议与已开子会话。关系与提议落在 `<agentDir>/linked-sessions.json`，子会话按 `stopped` 恢复。纯 CLI 不注册这三个工具。
- 计划模式与目标模式是会话原生行为（不依赖扩展）：计划模式声明 `submit_plan(steps[])`，结构化步骤直接成为计划卡与评审内容，文本 `Plan:` 解析保留给旧宿主；系统提示写明格式，模型没按格式写时提示一次。只读边界按 `ToolPlanAccess` 白名单：MCP 只放行 `annotations.readOnlyHint`，扩展工具默认拒绝，`start_session` / `send_to_session` 不进计划模式，子代理不注册 `submit_plan`；bash 仍过命令判据，`find -delete`、`git branch <name>`、`git log --output`、`sort -o`、`curl -o`、`env <cmd>` 等写参数已拦。桌面计划卡把「开始执行 / 结束执行」固定在滚动区外，执行态步骤行可点勾选；执行期间待办卡让位；关闭评审只收起卡片，草案与只读状态保留，退出走 chip 或 `/plan off`；长计划可展开全文或一键复制；评审里提交修改意见时旧草案保留，新计划到达才覆盖。目标模式按 `__PI_DECK_GOAL_MODE__` 标记续轮，`GOAL_COMPLETE` / `GOAL_BLOCKED:` 或轮次上限收尾；中止落 paused，请求失败与输出截断落 blocked 并带原因码，排队中的续轮起跑前复查状态。轮次上限时目标条换成「+10 轮」文字按钮，token 预算阻塞时恢复从当前用量重新起算；请求失败与输出截断连续 3 轮同因才阻塞；暂停中改目标（`/goal <新文本>` 或目标条编辑）保留轮次与总预算；计划与目标互斥切换时给一条暂停提示。目标完成/阻塞写一条 `piweft-goal-ended` 时间线记录，带轮次与 token 收据；状态分别写 `pi-deck-plan-mode` 与 `pi-deck-goal-mode` 自定义条目，跨分支恢复（计划只续执行态；目标只恢复为暂停，目标全文只在变化时落盘）。桌面目标条编辑行是多行输入框，拖入文件插入 @ 引用并按主输入框的 chip 渲染；目标条可编辑、暂停、恢复（空闲时立即补一轮，轮次上限先加预算）与清除；模式占位提示随计划/目标切换；流式期间切模式排队到本轮结束，chip 标「本轮后」；桌面发送路径接管 `/plan`、`/goal`、`/reload`，补全栏也列出这三条命令。
- bash/powershell 调用前先过 shell 守卫：删除命令的字面量目标复制进系统回收站（Linux XDG Trash、Windows 回收站、macOS 废纸篓，`PIDECK_TRASH_GUARD=off` 关闭），Windows Git Bash 的 `> nul` 改写成 `/dev/null`；`settings.httpProxy` 注入的代理只给模型请求，agent shell 不继承。
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
- OAuth：ChatGPT/Codex、GitHub Copilot、Anthropic、OpenRouter、xAI、Kimi、Meta、Radius；其它走 API Key。`pi auth login|logout <provider>` 与旧的 `pi login|logout <provider>` 都可用，`--api-key`/`--no-browser` 按 pi 语义解析。API Key 字段来自与桌面端共用的内置供应商目录（Cloudflare/Vertex/Bedrock 多字段）。`pi auth check` 返回 `ready`/`not_ready`/`invalid`，退出码 0/1/2。
- `auth.json` 支持明文与 `${VAR}`，命令型凭据以 `!` 开头（10 秒超时、64 KiB、失败回落环境变量），以及 `api_key.env` 的供应商级字段（Cloudflare 账户/网关、Vertex 项目/区域/ADC、AWS 访问密钥）。写入原子，Unix 0600；损坏文件拒绝覆盖。
- OpenCode / OpenCode Go 请求自动带 `x-opencode-session` 与 `x-opencode-client: piweft`；显式同名头优先。
- Anthropic message thread：会话有 id 且端点支持时，后续请求只发新增消息；前缀变化或服务器拒绝时退回完整请求并重开 thread，端点不认识 `thread` 字段时本会话关闭。`compat.supportsMessageThreads` 可覆盖默认（baseUrl 指向 `api.anthropic.com` 默认开，自定义 provider id 也算）。
- MCP OAuth 客户端身份支持 `auto`/`dcr`/`cimd`：`cimd` 用 pi 的 Client ID Metadata Document（`https://pi.dev/oauth/client.json`，授权服务器不带 `iss` 时按服务器派生路径），`auto` 在服务器支持公开客户端文档且未配 `clientName` 时优先用它。
- MCP 支持 stdio / streamable HTTP 握手、工具/资源/模板/提示分页、通知与 OAuth（授权码 + PKCE、动态注册、回环回调、401 刷新重放、客户端凭据）。stdio 失败会杀子进程并释放连接。服务器在后台并行连接（上限 8），首轮只为 `direct` 工具等 10 秒、RPC 等全部连完，其余服务器按需等待；408/429/5xx 重试两次（`Retry-After` 优先），`notifications/message` 写入 `<agentDir>/mcp.log`（5 MB 滚动）。

## 桌面能力

桌面复用 CLI 的环境装配与同一会话文件，不启动 RPC 子进程。包含多项目/多会话标签、会话树与有界正文搜索、置顶/命名/删除到 `.trash`/恢复（父会话归档时先关标签再收走关联子会话）、外部会话导入（Claude Code、Codex CLI）、文件浏览与编辑、原生 todo/ask_question/计划模式、快捷消息（插入草稿与直发）、资源与主题热重载、模型连接测试/远程目录、Git 全流程（状态、暂存、提交、分支、AI 摘要、图、比较、远端同步、stash）、检查点、轨迹、终端 Dock、使用统计（应用用量的区间趋势、模型占比、活动热力图与连续天数）、供应商余额、深浅主题、正文行距三档。

同一工具与参数反复调用时，第 3/5/8 次在工具结果末尾补一句提醒（用户发新消息清零）。

详见 [01-parity](01-parity.md) 与 [DESIGN](../DESIGN.md)。

## 已知限制

- 扩展宿主执行 JavaScript，支持 CJS `module.exports = function (pi) {...}` 与 ESM `export default function (pi) {...}`；ESM 的相对 import 与裸模块 `typebox`、`@earendil-works/pi-coding-agent`、`@earendil-works/pi-ai` 由宿主模块表提供（旧 `@sinclair/*`、`@mariozechner/*` 同名映射），模块加载根是发现目录。异步处理器可用，Promise 由 `UnwrapIfPromise` 驱动到结束；TypeScript 仍需构建期转译；无自定义 renderer。当前派发 `session_start`、`session_info_changed`、`session_shutdown`、`tool_call`、`tool_result`、`before_agent_start`、`agent_start`、`agent_end`、`turn_start`、`turn_end`、`message_start`、`message_end`、`tool_execution_start`、`tool_execution_end`、`model_select`、`thinking_level_select`，其它事件注册时即报诊断。`/reload` 先给旧宿主 `session_shutdown`，重建后重发 `session_start`；工具表每次从内置基线重建，旧扩展的工具不会留在表里，codemode 描述跟着重绑。生命周期事件带 `reason`（`startup`/`reload`/`new`/`resume`/`fork`/`quit`）与 `previousSessionFile`：RPC 的 `new_session` 先给旧会话 `session_shutdown("new")` 再构造新会话（`session_start("new")` + 上一个会话文件），`switch_session`/`fork`/`clone` 在同一实例上派发 `shutdown` + `start`。`--mode json` 也给扩展一个宿主：`session_start` 里的 `notify` 写进事件流，对话框按取消返回。`message_update`、`tool_execution_update` 与 provider/context 类事件尚未派发。扩展工具支持 `promptSnippet`/`promptGuidelines`/`outputSchema`/`exposure`（含 `model-only`）/`namespace`/`executionMode`，尚未接 `reachesTools`/`prepareLoadout`/`ctx.executeTool`。
- `before_agent_start` 应用返回的 `systemPrompt`、就地修改的 `systemPromptOptions.sections`，以及返回的 `message`（作为隐藏 custom message 跟在用户消息后进入本轮并落盘）。
- 仍缺的 1.5 运行时项：OpenAI `requiresThinkingAsText`、`zaiToolStream`；MCP 跨进程刷新锁。
- 安全门 `ask` 在桌面与交互式 CLI 上弹三选一：本次允许 / 本会话允许 / 拒绝；本会话允许按工具名记住，不跨会话也不落盘。没有审批宿主时退化为拒绝；默认档 `off`；CLI 无切换档位命令。
- 扩展诊断要能看见：加载失败与处理器抛错进 `ExtensionHost.Diagnostics`，运行中实时发 `extension_error` 事件（RPC），加载期的那批由 `TakeUnreportedExtensionDiagnostics` 在客户端连上后补报一次；桌面会话打开时收集到设置页「扩展」分区，并弹一条 toast。
- 与 pi 1.6.0 的剩余差距：Codex `websocket-cached` 不复用连接（60 分钟上限已按 close 原因重试）；`openai-responses` / Azure 的系统提示词仍发顶层 `instructions`，pi 发首条 `developer` 输入项（本端口只有一条合并后的 system，差异落在缓存前缀与工具变更通知）。
- CLI 仍是按行 REPL：无完整 TUI、多行编辑、`$EDITOR`、键位绑定与终端图片。
- 并行工具里同一文件的 `edit`/`write` 已串行化；`nestedCalls` / `parentToolCallId` 尚未产出，只按未知字段保留。
- 生图/分类网关已接入 codemode（含 `openai-decisions`、`typesafe-system-one`、`cloudflare-workers-ai-system-one`），但没有一级入口；虚拟模型路由已实现，扩展宿主动态注册未接；Codex `websocket-cached` 未复用连接。虚拟模型的图片限制按外层模型条目的 `inputLimits` 取，未使用路由目标模型的限制。
- 桌面历史仍全量在内存（分支/fork/搜索/todo/`context_edit` 依赖全量）；完整 VT100 边界、终端多标签、快照回放未覆盖。
- `read`/`grep` 流式扫描：只按 `\n` 切行（`read` 保留 CR，`grep` 剥行尾 CR），`read` 只保留会显示的行、`grep` 只保留能进字节预算的行；取消在解码块之间生效。`grep` 识别 UTF-16 BOM，并像 rg 一样搜索二进制文件里的文本行。
- `grep`/`find` 仍是进程内实现，不依赖外部二进制：glob 已覆盖 `{a,b}` 分支、`[!a]` 取反、`**` 与 fd 的 smart-case、路径型 pattern 任意深度匹配。ignore 按工具分开：`grep` 用 `.ignore`/`.rgignore`，`find` 用 `.ignore`/`.fdignore` 与 `~/.config/fd/ignore`；`.gitignore` 只在仓库内生效（`find` 的 `--no-require-git` 语义除外），进入嵌套仓库时丢掉祖先仓库的本地 VCS 规则，非 VCS 规则与全局 git 忽略保留。剩余差异是正则方言（.NET `NonBacktracking` 而非 Rust regex）、遍历顺序（进程内为确定性排序，fd/rg 未定义），以及超长单行的内存（按行缓冲，不按整文件）。
- `grep`/`find` 不跟随符号链接与目录联接；Windows 上只跳过真正的链接；OneDrive 等云同步占位符也是 reparse point，但属于普通目录，照常遍历。
- 图片管线只处理 pi 支持的五种内联格式（PNG/JPEG/GIF/WebP/BMP）；AVIF/HEIC/TIFF 等需要先转成 PNG。`inputLimits.images.maxPerMessage` 对用户消息、`maxPerRequest` 对请求投影强制，超出部分以文本括注代替；工具结果只受 `maxPerRequest` 约束。缩放与预乘语义和 Photon 不完全逐字节一致：本实现预乘 alpha，透明边缘更干净。
- `constrainedSampling` 已声明在 `read`/`bash`/`edit`/`write` 并在 provider 层落地 strict JSON schema（Anthropic 含额度超限的会话级降级，OpenAI Completions/Responses、Google VALIDATED、Bedrock、Mistral）。grammar 变体（codemode 的 lark 约束）尚未实现。
- 子代理不支持外部 CLI 角色；没有 worktree 隔离，跨文件并发写仍要父会话复核。
- Windows/macOS 原生窗口、发布包、真实供应商联网与 PiDeck 完整 e2e 仍需实机验证。Windows 的 Git Bash 发现顺序、旧版 WSL 的 `-s` + stdin、PowerShell 7 优先与 UTF-8 前置、`taskkill /F /T`、`/c/` `/mnt/c/` 路径换算已按 pi 审计对齐，但缺少实机回归。

## 验证

```sh
dotnet test PiWeft.slnx
dotnet run --project tools/PiWeft.SessionBench
dotnet publish tools/PiWeft.AotSmoke -c Release -r linux-x64
dotnet publish src/PiWeft.Cli -c Release -r linux-x64
dotnet publish src/PiWeft.Desktop -c Release -r linux-x64
```

无头 GUI：`xvfb-run -a dotnet test PiWeft.slnx`。视觉验收脚本见 [01-parity](01-parity.md)。provider 使用打桩服务；真实数据互操作测试依赖本机会话/技能数据。
