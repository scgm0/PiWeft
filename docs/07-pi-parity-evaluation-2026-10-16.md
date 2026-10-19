# PiWeft 与 pi 功能差距评估（2026-10-16 快照）

> **2026-10-19 收尾修订**：本轮实现状态已同步到本文各表。新增 OpenAI ChatGPT/GitHub Copilot/OpenRouter/Meta/Radius OAuth、账号级 OpenRouter/Radius/Copilot 目录、cache warming、`/share` Radius 上传、`/llama` HF 搜索/下载与 Provider 自动注册；随后补齐虚拟模型、Codex WebSocket、OpenRouter 生图与 OpenAI Decisions 分类网关。未覆盖项保留在 PI-01/02/05（扩展宿主/TUI）以及 PI-09 的扩展动态路由/连接复用/入口接线、D-* 与验证欠账中。桌面 X11 PrimarySelection 的 1 个测试在本机环境仍偶发失败，与本次功能改动无关。
>
> **2026-10-19 第二轮更新**：已落地消息变换层、工具 JSON 修复、上下文溢出恢复、摘要重试、settings/packages 对象兼容、models.json provider 级覆盖、动态目录缓存、默认模型选择与未知模型 fallback、powershell、SYSTEM.md/APPEND_SYSTEM.md、JSONL 导出、/share、/llama、/bug 诊断包，以及 Anthropic/xAI/Kimi 与随后补上的 OpenAI ChatGPT/GitHub Copilot/OpenRouter/Meta/Radius OAuth。仍未实现：完整 JS 扩展宿主、codemode/tool_search、完整 TUI。
>
> **2026-10-19 更新**：基准已前进到 pi 1.3.0。本轮（F/C 类修正）落地——NEW-01（`PI_*` 环境变量注入 + `AI_AGENT`/`PI_CODING_AGENT` 进程标记）、事件协议补齐（`agent_settled`、`queue_update`、`session_info_changed`、`thinking_level_changed`、`entry_appended`、`compaction_*`）、默认工具集对齐 pi、settings 功能性键（`shellPath`/`shellCommandPrefix`/`sessionDir`/`defaultTools`）、CLI 斜杠命令面（含 `!`/`!!`）、`pi config` 与 `pi update --self/--extensions/--models/--all`、`pi mcp add` 的 pi 形状选项、提示词模板空参数、摘要输出上限、模型歧义优先凭据与 `--model :thinking` 后缀、兼容参数降级警告、RPC 契约版本 1.3.0。仍未实现：NEW-03（powershell 工具）、NEW-04（CLI keybindings.json）、NEW-05（编辑器增强）、`/share`（上传）与 `/llama`、PI-01/02/04/05/08/09（其中 NEW-03、`/share`、`/llama` 已在第二轮补上）。NEW-07 撤销：`--min-release-age` 不是 pi 的用户面参数。
>
> **2026-10-16 晚间更新**：本文的以下条目已在本轮补全中落地——`/copy` `/import` `/bug`（NEW-06 子集）、PI-02 的对话框方法面（select/confirm/editor/notify）、PI-06 的客户端凭据授权与 HTTP 反向应答、PI-07 的命令型凭据执行与 `auth check` invalid、PI-10 的主题热重载、PI-11 的默认值改为 `ask`。详见 [00-status.md](00-status.md) 与 [06 第 3、4 节](06-feature-gap-and-jint-assessment.md) 的最新状态。`defaultProjectTrust` 默认值已从 `always` 收紧为 `ask`：依赖旧默认（项目设置静默生效）的部署需在全局 settings 显式写回 `"defaultProjectTrust":"always"`。

评估对象：PiWeft 工作树源码（2026-10-19：非桌面 752 项、Desktop 436/437 通过）。
基准：本地 `/home/scgm/TestBuild/pi`（当前 `v1.3.0+30`，`482c1381a`，TypeScript 上游）；
参照：`/home/scgm/TestBuild/pi_agent_rust`（Rust 移植，v0.8.0）与 `/home/scgm/TestBuild/pi-go`（Go 移植），仅用于交叉验证缺口真实性与补全策略，不作为必备契约。

本文是**一次性评估快照**，不替代 [06-feature-gap-and-jint-assessment.md](06-feature-gap-and-jint-assessment.md) 的编号追踪；缺口沿用其稳定编号（`PI-*`/`D-*`/`X-*`），新发现项以 `NEW-*` 暂编，待确认后并入正式编号。

## 1. 总体结论

1. **协议与会话层已高度对齐**：version 3 JSONL、fork/clone/分支树、压缩、重试、RPC 命令面、MCP（含 OAuth 2.1 + exposure 过滤）、技能/模板/主题/包、项目信任、计划模式、HTML 导出均已实现且有回归测试。Rust/Go 两版在多项上反而落后于 PiWeft（如 Go 版自评 P0 缺失的 steering 排队、上下文文件发现、每模型压缩预算、prompt 模板，PiWeft 均已具备）。
2. **剩余核心差距集中在两块**：JS 扩展生态（PI-01/02/05）与完整终端 TUI（PI-04）。高级模型运行时的核心协议（虚拟模型、cache warming、Codex WebSocket、生图/分类）已落地，余项是扩展动态路由、连接复用和 CLI/TUI/桌面入口接线。
3. **三条移植路线已经分化**，PiWeft 需要先做产品定位决策再补扩展生态：Rust 版内嵌 QuickJS 直接跑 TS 扩展（兼容优先），Go 版彻底放弃进程内扩展、改用 hooks + MCP + skills（重写优先），PiWeft 目前在 Jint 路线（兼容优先）上已做完可行性论证但 S2 未启动。

## 2. 已具备能力（确认无需重复开发）

与 pi 逐项核对后确认已对齐的能力，详见 [06 第 2 节](06-feature-gap-and-jint-assessment.md)，此处只列本次新确认的对照点：

- **工具面**：pi 的 8 个内置工具中，`read/bash/edit/write/grep/find/ls` 全部实现，并新增 `powershell`；pi 经扩展注册的 `todo`/`ask_question` PiWeft 已原生实现且线协议与 PiDeck 一致。pi 默认关闭的 `codemode`/`tool_search` 未实现（PI-05）。
- **Provider 协议**：10 类流式 API 已覆盖 pi 30+ 提供商所依赖的全部主流协议族（Anthropic/OpenAI 系/Google 系/Bedrock/Mistral/pi-messages）；差距在认证方式与目录服务，不在协议本身。
- **RPC 面**：对照 pi `docs/rpc-commands.md`，PiWeft 的命令组覆盖对话/队列/会话/导航/模型/计划/管理/MCP/统计/资源，明显超过 Go 版的 RPC 面。`extension_ui` 的 `select`/`confirm`/`input`/`editor`/`notify` 已实现；缺的是扩展宿主与 `setStatus`/`setWidget` 等 fire-and-forget 方法（PI-02）。
- **Go 版自评的 P0 缺失**（steering/follow-up 排队、上下文文件发现、prompt 模板、每模型压缩预算）在 PiWeft 全部已实现——这些不是缺口。

## 3. 相对 pi 的确认缺口（沿用 06 编号，状态复核）

| 编号 | 能力 | 本次复核结论 |
| --- | --- | --- |
| PI-01 | JS/TS 扩展宿主 | 仍未开始。Rust 版证明该路线可行：内嵌 QuickJS-ng 直接加载 pi 的 `.ts/.js` 扩展，Node/Bun shim 覆盖 13 个核心模块，223 个真实扩展语料 91.9% 通过。PiWeft 的 Jint 5 探针（06 第 5-6 节）结论与之互证：解释器不是瓶颈，宿主契约才是 |
| PI-02 | 扩展 UI 协议 | 仅 `input` 方法。pi 的 renderer 注册、自定义组件、经 RPC 转发的完整 `extension_ui` 记录族不成立 |
| PI-04 | 完整终端 TUI | 仍为按行 REPL。Rust/Go 均重投了大量工程（Rust 自研 FrankenTUI，Go 用 Bubble Tea v2 + 补全 + 模糊评分）；这是 CLI 用户的第一体验，建议列为扩展生态之外的最高优先级 |
| PI-05 | codemode / tool_search | 未实现。Rust 版经验：tool_search（BM25 延迟发现）与 exposure 分级是工具数超过上下文预算时的完整方案，PiWeft 的 `McpExposure` 已就位但 `codemode/deferred` 暂按 `direct` 降级 |
| PI-06 | MCP 高级运行时 | 已覆盖 OAuth 授权码 + PKCE、客户端凭据、`oauth` 对象形状、callbackPort/clientName/authServerMetadataUrl、HTTP 通知流与服务端反向应答；余项是网关模式令牌交换变体、桌面 MCP 授权入口、与 pi 完全一致的令牌文件共享格式 |
| PI-07 | 认证覆盖 | 已支持 Codex、OpenAI ChatGPT、GitHub Copilot、Anthropic、OpenRouter、xAI、Kimi、Meta、Radius 的 OAuth，以及所有 Provider 的 API key 登录；`!command` 命令型凭据与运行时刷新可用。余项是 Bedrock profile/ECS/IRSA 完整 ambient 链、Radius 浏览器/设备码选择菜单、GitHub Enterprise 之外的细分登录方式 |
| PI-08 | 动态模型目录 | 已实现 pi.dev 目录刷新、`<agentDir>/models-store.json` 离线缓存、账号级 OpenRouter/Radius/GitHub Copilot 目录。余项是 ETag/刷新节流与更细的账号能力同步 |
| PI-09 | 高级模型运行时 | 已实现虚拟模型 `pi-virtual`（静态 `models.json` 路由）、cache warming 经济学阈值与状态、模型级 `samplingParams`、Codex WebSocket（auto 回退 SSE）、OpenRouter 生图与 OpenAI Decisions 分类网关。余项是扩展动态 `route()`/路由状态、`websocket-cached` 连接复用、Codex zstd 与完整 `store`/continuation 语义，以及生图/分类的 CLI/TUI/桌面入口 |
| PI-10 | 资源配置与主题 | 主题 JSON 解析、主题文件监视热重载与资源页已实现；余项是独立资源编辑界面与包资源筛选可视化 |

## 4. 新发现的未编号缺口（本次对照 pi 源码逐一核实）

| 暂编 | 能力 | pi 行为 | PiWeft 现状 |
| --- | --- | --- | --- |
| NEW-01 | 环境变量协议 | bash/powershell 工具注入 `AI_AGENT`、`PI_SESSION_ID`、`PI_SESSION_FILE`、`PI_PROVIDER`、`PI_MODEL`、`PI_REASONING_LEVEL`，扩展和脚本可感知会话上下文 | 已实现（`PI_*` 注入 + `AI_AGENT`/`PI_CODING_AGENT` 进程标记） |
| NEW-02 | `!`/`!!` 内联 shell | 输入框 `!` 前缀直接执行 shell，扩展可经 `user_bash` 事件拦截接管 | 已实现（`!` 进上下文、`!!` 不进上下文） |
| NEW-03 | `powershell` 内置工具 | Windows 上独立的 powershell 工具 | 已实现（Windows 与装有 pwsh 的平台） |
| NEW-04 | CLI 键位绑定 | `keybindings.json` 命名动作映射 + `/hotkeys` | 未实现（桌面有独立快捷键体系，互不相通） |
| NEW-05 | 编辑器增强 | 多行编辑、`$EDITOR` 外部编辑、剪贴板图片粘贴进 TUI | REPL 均无（并入 PI-04 范围即可） |
| NEW-06 | pi 专属斜杠命令 | `/copy`、`/import`、`/share`、`/bug`、`/llama`、`/settings` | `/copy` `/import` `/settings` 已实现；`/share` 支持 Radius artifact 与私有 GitHub gist；`/bug` 生成脱敏 zip；`/llama` 支持 router 管理、HF 搜索/详情/下载与 Provider 自动注册（无进度条与量化体积 UI） |
| NEW-07 | 未知字段容忍面 | `--min-release-age` 等供应链参数 | 无对应场景，可忽略 |

NEW-01/02/03 与 NEW-06 的主要命令已落地；剩余 NEW-04/05 属 TUI 范围。

## 5. Rust/Go 参照带来的方法论输入

1. **一致性语料回归（Rust）**：Rust 版维护 223 个真实扩展的一致性矩阵（`docs/extension-conformance-matrix.json`）和 `dropin-*` 机器可读 CLI/事件/配置差异清单。PiWeft 若启动 PI-01，建议直接采纳此方法：先建支持矩阵（工具/命令/事件/状态/UI/Node 模块/TypeBox 各标支持级别），用固定语料做回归，而不是逐扩展手工验证。
2. **工具分层暴露（Rust）**：essential（常驻 schema）/ discoverable（按需提升）/ gated（settings 门控）三层 + 默认启用集合，可控制大工具面下的上下文膨胀，与 pi 的 exposure 分级殊途同归；PI-05 设计时可一并考虑。
3. **无扩展宿主的替代路线（Go）**：Go 版证明 skills + MCP + shell hooks 可以覆盖大部分扩展需求，代价是失去进程内插件 API。若 PiWeft 的产品定位是「轻量原生工作台」，这条路线可让 PI-01/02/05 整体降级为「明确排除」，把资源转投 PI-04 与桌面深化。
4. **有意的哲学分歧要文档化**：Rust 版对 print 模式默认不自动批准、Go 版对宽松 schema 均明示与上游相反的选择及理由。PiWeft 的 `defaultProjectTrust` 已与 pi 一样默认 `ask`，不再保留旧默认。
5. **验证欠账优先于新功能口径**：三个仓库都把「真实供应商联网测试」「多平台实机回归」列为未完成。PiWeft 的 Windows/macOS 原生窗口与打包、PiDeck 完整 e2e、真实供应商联网三项（06 第 1.1 节）在扩展生态决策前应先补，否则兼容声明缺少实证口径。

## 6. 建议补全顺序（供决策，非排期）

| 档 | 内容 | 理由 |
| --- | --- | --- |
| P0 | PI-04 完整 TUI；S2 Jint 试点（PI-05 基础）；高级运行时入口接线（生图/分类、虚拟模型动态路由）；验证欠账（多平台实机 + 真实供应商联网） | TUI 是 CLI 第一体验；Jint 可行性已论证、依赖 S1 已完成；认证、账号目录、缓存保温、虚拟模型与 WebSocket 核心已落地，剩余是扩展宿主和入口；无实证口径一切兼容声明都悬空 |
| P1 | PI-01 扩展宿主（S3，前提是范围决策通过）；PI-05 完整（tool_search/exposure 生效）；PI-09 的扩展动态路由/连接复用/入口；NEW-04 键位；PI-06 余项 | 扩展生态是「替代 pi + 扩展工作流」的分水岭，但成本在宿主契约，需先批准范围变更；其余为独立可验收的能力增强 |
| P2 | PI-02 完整 extension_ui；X-01 子代理、X-03 LSP/AST、X-04 记忆、X-07 搜索/浏览器；D 系列深化（D-04 推拉、D-13 长历史分页等） | 属于超越 pi 基线的方向，两个参照仓库也都是后置；按产品需求单独取舍 |

**关键决策点**（与 06 第 7.3 节一致，本次补充）：在「Rust 路线（尽最大努力兼容 TS 扩展）」与「Go 路线（明确放弃，hooks + MCP + skills）」之间二选一，是 PI-01/02/05 全部下游工作的前置条件；建议以 PiDeck 现有扩展（`pi-deck-ask-question`/`pi-deck-todo`/`pi-deck-plan-mode`——PiWeft 已原生覆盖）之外是否有真实扩展依赖为判据。
