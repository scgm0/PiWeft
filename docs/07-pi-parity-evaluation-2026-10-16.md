# PiWeft 与 pi 功能差距评估（2026-10-16 快照）

> **2026-10-16 晚间更新**：本文的以下条目已在本轮补全中落地——`/copy` `/import` `/bug`（NEW-06 子集）、PI-02 的对话框方法面（select/confirm/editor/notify）、PI-06 的客户端凭据授权与 HTTP 反向应答、PI-07 的命令型凭据执行与 `auth check` invalid、PI-10 的主题热重载、PI-11 的默认值改为 `ask`。详见 [00-status.md](00-status.md) 与 [06 第 3、4 节](06-feature-gap-and-jint-assessment.md) 的最新状态。仍未实现：NEW-01（环境变量协议）、NEW-03（powershell 工具）、NEW-04（CLI keybindings）、NEW-05（编辑器增强）、`/share` `/llama` `/settings`、PI-08/PI-09。`defaultProjectTrust` 默认值已从 `always` 收紧为 `ask`：依赖旧默认（项目设置静默生效）的部署需在全局 settings 显式写回 `"defaultProjectTrust":"always"`。

评估对象：PiWeft 工作树源码（993 项测试通过基线）。
基准：本地 `/home/scgm/TestBuild/pi`（coding-agent 1.2.0，TypeScript 上游）；
参照：`/home/scgm/TestBuild/pi_agent_rust`（Rust 移植，v0.8.0）与 `/home/scgm/TestBuild/pi-go`（Go 移植），仅用于交叉验证缺口真实性与补全策略，不作为必备契约。

本文是**一次性评估快照**，不替代 [06-feature-gap-and-jint-assessment.md](06-feature-gap-and-jint-assessment.md) 的编号追踪；缺口沿用其稳定编号（`PI-*`/`D-*`/`X-*`），新发现项以 `NEW-*` 暂编，待确认后并入正式编号。

## 1. 总体结论

1. **协议与会话层已高度对齐**：version 3 JSONL、fork/clone/分支树、压缩、重试、RPC 命令面、MCP（含 OAuth 2.1 + exposure 过滤）、技能/模板/主题/包、项目信任、计划模式、HTML 导出均已实现且有回归测试。Rust/Go 两版在多项上反而落后于 PiWeft（如 Go 版自评 P0 缺失的 steering 排队、上下文文件发现、每模型压缩预算、prompt 模板，PiWeft 均已具备）。
2. **剩余核心差距集中在四块**：JS 扩展生态（PI-01/02/05）、完整终端 TUI（PI-04）、认证与模型目录广度（PI-07/08/09）、若干 pi 特有交互细节（本文第 4 节 NEW 项）。
3. **三条移植路线已经分化**，PiWeft 需要先做产品定位决策再补扩展生态：Rust 版内嵌 QuickJS 直接跑 TS 扩展（兼容优先），Go 版彻底放弃进程内扩展、改用 hooks + MCP + skills（重写优先），PiWeft 目前在 Jint 路线（兼容优先）上已做完可行性论证但 S2 未启动。

## 2. 已具备能力（确认无需重复开发）

与 pi 逐项核对后确认已对齐的能力，详见 [06 第 2 节](06-feature-gap-and-jint-assessment.md)，此处只列本次新确认的对照点：

- **工具面**：pi 的 8 个内置工具中，`read/bash/edit/write/grep/find/ls` 全部实现；pi 经扩展注册的 `todo`/`ask_question` PiWeft 已原生实现且线协议与 PiDeck 一致。pi 默认关闭的 `codemode`/`tool_search` 未实现（PI-05）。
- **Provider 协议**：10 类流式 API 已覆盖 pi 30+ 提供商所依赖的全部主流协议族（Anthropic/OpenAI 系/Google 系/Bedrock/Mistral/pi-messages）；差距在认证方式与目录服务，不在协议本身。
- **RPC 面**：对照 pi `docs/rpc-commands.md`，PiWeft 的命令组覆盖对话/队列/会话/导航/模型/计划/管理/MCP/统计/资源，明显超过 Go 版的 RPC 面；唯一结构性缺口是 `extension_ui` 只发出 `input` 一种方法（PI-02）。
- **Go 版自评的 P0 缺失**（steering/follow-up 排队、上下文文件发现、prompt 模板、每模型压缩预算）在 PiWeft 全部已实现——这些不是缺口。

## 3. 相对 pi 的确认缺口（沿用 06 编号，状态复核）

| 编号 | 能力 | 本次复核结论 |
| --- | --- | --- |
| PI-01 | JS/TS 扩展宿主 | 仍未开始。Rust 版证明该路线可行：内嵌 QuickJS-ng 直接加载 pi 的 `.ts/.js` 扩展，Node/Bun shim 覆盖 13 个核心模块，223 个真实扩展语料 91.9% 通过。PiWeft 的 Jint 5 探针（06 第 5-6 节）结论与之互证：解释器不是瓶颈，宿主契约才是 |
| PI-02 | 扩展 UI 协议 | 仅 `input` 方法。pi 的 renderer 注册、自定义组件、经 RPC 转发的完整 `extension_ui` 记录族不成立 |
| PI-04 | 完整终端 TUI | 仍为按行 REPL。Rust/Go 均重投了大量工程（Rust 自研 FrankenTUI，Go 用 Bubble Tea v2 + 补全 + 模糊评分）；这是 CLI 用户的第一体验，建议列为扩展生态之外的最高优先级 |
| PI-05 | codemode / tool_search | 未实现。Rust 版经验：tool_search（BM25 延迟发现）与 exposure 分级是工具数超过上下文预算时的完整方案，PiWeft 的 `McpExposure` 已就位但 `codemode/deferred` 暂按 `direct` 降级 |
| PI-06 | MCP 高级运行时 | 余项：OAuth 客户端凭据/网关模式、桌面 MCP 授权入口；另发现 HTTP 传输的服务端反向请求（如 ping）应答未实现（`McpHttpClient.cs:438`） |
| PI-07 | 认证覆盖 | OAuth 仅 Codex；pi 支持浏览器/设备流的 Anthropic/OpenAI/Google 登录、`!command` 命令型凭据、Bedrock/Vertex ambient 凭据链。命令型凭据目前明确不执行 |
| PI-08 | 动态模型目录 | pi 有内置目录 + pi.dev 在线刷新（`pi update --models`）+ 离线缓存；PiWeft 固定内置目录 + 手动按 API 拉取。账号级目录与能力同步缺失 |
| PI-09 | 高级模型运行时 | 虚拟路由模型、分类器/生图模型、缓存保温（`cacheWarming` streaming/idle）、Codex WebSocket 均缺。缓存保温对高频对话的成本影响直接，建议单独评估 |
| PI-10 | 资源配置与主题 | 主题 JSON 解析已完整；余项为主题文件自动监视热重载、独立资源管理界面 |

## 4. 新发现的未编号缺口（本次对照 pi 源码逐一核实）

| 暂编 | 能力 | pi 行为 | PiWeft 现状 |
| --- | --- | --- | --- |
| NEW-01 | 环境变量协议 | bash/powershell 工具注入 `AI_AGENT`、`PI_SESSION_ID`、`PI_SESSION_FILE`、`PI_PROVIDER`、`PI_MODEL`、`PI_REASONING_LEVEL`，扩展和脚本可感知会话上下文 | 未实现（全源码无这些标识符） |
| NEW-02 | `!`/`!!` 内联 shell | 输入框 `!` 前缀直接执行 shell，扩展可经 `user_bash` 事件拦截接管 | 未实现 |
| NEW-03 | `powershell` 内置工具 | Windows 上独立的 powershell 工具 | 无；Windows 支持本就在验证欠账内，若承诺 Windows CLI 应补 |
| NEW-04 | CLI 键位绑定 | `keybindings.json` 命名动作映射 + `/hotkeys` | 未实现（桌面有独立快捷键体系，互不相通） |
| NEW-05 | 编辑器增强 | 多行编辑、`$EDITOR` 外部编辑、剪贴板图片粘贴进 TUI | REPL 均无（并入 PI-04 范围即可） |
| NEW-06 | pi 专属斜杠命令 | `/copy`（复制最近回复）、`/import`（导入 JSONL）、`/share`（上传 Radius/GitHub gist）、`/bug`、`/llama`（内置 llama.cpp 本地模型）、`/settings` | 均未实现；`/share` 与 `/copy` 成本低价值高，`/llama` 可并入 PI-08 决策 |
| NEW-07 | 未知字段容忍面 | `--min-release-age` 等供应链参数 | 无对应场景，可忽略 |

其中 NEW-01/02/06 前三项都是小体量、可独立验收的兼容项，建议在下一轮 S0 类兼容回归中一并处置。

## 5. Rust/Go 参照带来的方法论输入

1. **一致性语料回归（Rust）**：Rust 版维护 223 个真实扩展的一致性矩阵（`docs/extension-conformance-matrix.json`）和 `dropin-*` 机器可读 CLI/事件/配置差异清单。PiWeft 若启动 PI-01，建议直接采纳此方法：先建支持矩阵（工具/命令/事件/状态/UI/Node 模块/TypeBox 各标支持级别），用固定语料做回归，而不是逐扩展手工验证。
2. **工具分层暴露（Rust）**：essential（常驻 schema）/ discoverable（按需提升）/ gated（settings 门控）三层 + 默认启用集合，可控制大工具面下的上下文膨胀，与 pi 的 exposure 分级殊途同归；PI-05 设计时可一并考虑。
3. **无扩展宿主的替代路线（Go）**：Go 版证明 skills + MCP + shell hooks 可以覆盖大部分扩展需求，代价是失去进程内插件 API。若 PiWeft 的产品定位是「轻量原生工作台」，这条路线可让 PI-01/02/05 整体降级为「明确排除」，把资源转投 PI-04 与桌面深化。
4. **有意的哲学分歧要文档化**：Rust 版对 print 模式默认不自动批准、Go 版对宽松 schema 均明示与上游相反的选择及理由。PiWeft 的 `defaultProjectTrust` 默认 `always`（pi 默认 `ask`）属同类分歧，已在 00-status.md 声明，保持该纪律即可。
5. **验证欠账优先于新功能口径**：三个仓库都把「真实供应商联网测试」「多平台实机回归」列为未完成。PiWeft 的 Windows/macOS 原生窗口与打包、PiDeck 完整 e2e、真实供应商联网三项（06 第 1.1 节）在扩展生态决策前应先补，否则兼容声明缺少实证口径。

## 6. 建议补全顺序（供决策，非排期）

| 档 | 内容 | 理由 |
| --- | --- | --- |
| P0 | PI-04 完整 TUI；S2 Jint 试点（PI-05 基础）；PI-07 认证面扩展（至少 Anthropic/Google OAuth + `!command` 凭据）；验证欠账（多平台实机 + 真实供应商联网） | TUI 是 CLI 第一体验；Jint 可行性已论证、依赖 S1 已完成；认证广度决定「能否替代 pi 日常使用」；无实证口径一切兼容声明都悬空 |
| P1 | PI-01 扩展宿主（S3，前提是范围决策通过）；PI-05 完整（tool_search/exposure 生效）；PI-08 目录刷新；PI-09 缓存保温与虚拟模型；NEW-01/02/06 兼容小项；PI-06 余项 | 扩展生态是「替代 pi + 扩展工作流」的分水岭，但成本在宿主契约，需先批准范围变更；其余为独立可验收的能力增强 |
| P2 | PI-02 完整 extension_ui；X-01 子代理、X-03 LSP/AST、X-04 记忆、X-07 搜索/浏览器；D 系列深化（D-04 推拉、D-13 长历史分页等） | 属于超越 pi 基线的方向，两个参照仓库也都是后置；按产品需求单独取舍 |

**关键决策点**（与 06 第 7.3 节一致，本次补充）：在「Rust 路线（尽最大努力兼容 TS 扩展）」与「Go 路线（明确放弃，hooks + MCP + skills）」之间二选一，是 PI-01/02/05 全部下游工作的前置条件；建议以 PiDeck 现有扩展（`pi-deck-ask-question`/`pi-deck-todo`/`pi-deck-plan-mode`——PiWeft 已原生覆盖）之外是否有真实扩展依赖为判据。
