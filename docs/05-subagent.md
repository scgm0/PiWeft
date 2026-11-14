# 子代理

`delegate` 把一个自包含任务交给带角色的子会话，最终回复作为消息回到父会话。纯 C# 实现，不依赖 JS、Node 或外部 CLI。

## 为什么是异步

同步调用把父回合卡在工具调用里。子级要用户拍板时没有出口，用户中断父回合又要额外决定子会话怎么收尾。异步把启动和等待拆开：

1. `delegate` 立即返回子代理名字。
2. 每轮结束，最终回复作为一条用户消息回到父会话。父会话空闲时直接起一轮，忙时 steer（当前工具批次结束、下一次模型请求前）。
3. 下一步依赖结果时，用 `wait_subagents` 显式等；取走的结果不再重复投递（同一段正文只进一次上下文）。工具结果的 `Details` 里每个子代理带 `returned`（正文已随本次调用返回）与 `result`，桌面据此补一张只读回报卡：看得见，不写会话，不多起一轮。

这和三家参考实现的取舍一致：deepseek 的 `delivery: 'caller'` 就是「调用方等结果，不发父级通知」；pi-subagents 的完成通知只带 2 KB 以内预览，与 `bg_wait` 的完整返回并存，靠 run key 去重；PiDeck 的关联会话没有等待概念，每轮都回报。这里取前者的语义，再用只读卡补上后两者的可见性。

结果回父会话有两条路径，差别只在「要不要把结果交给模型继续做」：

| 路径 | 写会话 | 进上下文 | 起一轮请求 | 时间线 |
|---|---|---|---|---|
| 没人等，子代理跑完 | 是（`piweft-subagent-result` user 消息） | 是 | 父会话空闲就起一轮 | 回报步骤（默认一行） |
| `wait_subagents` 取走 | 否，只在工具结果的 `Details` 里 | 只经工具结果进一次 | 否 | 只读回报步骤 |

第一条是异步委派的默认语义：结果到了就让父会话接着做，否则模型根本不知道子代理说了什么。第二条是「同一轮里就要用它」时的捷径，代价是不再另发消息。

投递时机：父会话空闲就直接起一轮；运行中则以 steer 排到当前工具批次结束、下一次模型请求之前，不再等 `agent_end`。父会话阻塞在 `wait_subagents` 上时，投递与 wait 在 `_gate` 下按 `DeliveredTurn` 二选一：谁先取走这一轮谁标记，另一条路径只报状态，同一段正文不会同时进工具结果和 steer。

`pi` 的 pi-subagents 默认也是异步，deepseek 的委派工具只提供异步。这条路线不是简化版。

## 工具

| 工具 | 用途 |
|---|---|
| `delegate` | `{ agent, task, label?, name?, context?, output_schema? }`，启动子代理，立即返回；`label` 是卡片上的一句话，省略时取任务首行 |
| `wait_subagents` | `{ names?, timeout_ms?, any? }`，等到结束一轮或超时；`any: true` 第一个结束就返回，其余仍按未投递处理 |
| `list_subagents` | 本会话的子代理与可用角色 |
| `read_subagent` | `{ id, max_chars? }`，读子会话最近的助手正文；截断或重启后取全文 |
| `send_to_subagent` | `{ id, message, mode? }`，追加消息，不等回复；`mode` 为 `follow_up`（默认）、`steer` 或 `auto` |
| `stop_subagent` | `{ id }`，停当前回合，子会话仍可读 |

工具只在宿主支持子会话且深度未到上限时注册。纯 CLI、`--tools none`、子代理会话本身都拿不到这些工具。`read_subagent` 从活会话的投影或会话文件读最近 40 段助手正文，默认 12k、上限 48k。

开启 codemode 后，脚本可以 `await tools.delegate(...)` 批量启动，再用 `await tools.wait_subagents(...)` 收集结果。Jint 已经在依赖里，不需要第二个 JS 运行时。

`wait_subagents` 默认等全部目标，`any: true` 改成第一个结束就返回：返回的正文只带已完成的目标，其余保持未投递，下一次 wait 还能取。已经结束的目标不会让 `any` 继续等还在跑的兄弟。

子代理每完成一条助手消息，首行摘要记进运行快照的 `progress`；工具结束会清掉 `activity`。卡片、`list_subagents` 的 `Details` 与 wait 心跳都读这两个字段，长回合里能看到子代理写到了哪一句。

## 上下文

- `fresh`（默认）：子会话只看得到任务本身。
- `fork`：把本会话当前分支投影里最后一条不含工具调用的助手消息及其之前的内容写进子会话。当前回合的助手消息一定带 `delegate` 调用，所以截到这里就避开了半截回合。历史超过 100k 字符时只保留最近一段，并以一条「历史已截断」的用户消息开头；没有已完成回合时直接拒绝，提示改用 `fresh`。

fork 的子会话仍用角色的系统提示词与委派边界，不会复用父会话的请求前缀。

父会话在计划模式下，子会话建好就进入计划模式：`edit`/`write` 从工具面拿掉，`bash` 仍保留但只允许只读命令。计划模式是创建时的快照，冷恢复沿用 `planMode` 条目。

`wait_subagents` 的单次正文预算 24k。装不下的结果标成 `returned: false` 留在未投递状态，下一次 `wait_subagents` 还能取；不再出现「正文被丢掉但仍报已投递」。工具结果的 `Details` 里每行带 `session_file`，截断时正文末尾也给这个路径。

## 结构化结果

`delegate` 的 `output_schema` 要求子代理最终回复返回一个符合 schema 的 JSON 值。schema 只接受一个子集，遇到不认识的 keyword 直接拒绝，不会静默放宽：

- 根节点必须是 `object`。
- 支持 `type`（`object`/`array`/`string`/`number`/`integer`/`boolean`/`null`）、`properties`、`required`、`items`、`enum`、`additionalProperties`（布尔或 schema）、`description`/`title`/`default`/`examples`/`$schema`。
- 不支持 `pattern`、`anyOf`、`oneOf`、`allOf`、`$ref` 等；定义阶段就会报错。
- 默认与 JSON Schema 一致：未写 `additionalProperties` 时允许多余字段，写 `false` 才拒绝。

子代理的系统提示词里会附上 schema。结束时从最终回复提取 JSON：整段是 JSON 就直接用，否则取最后一个 ` ```json ` 围栏块，再退回首尾大括号之间的内容。校验失败把这一轮标成 `failed`，正文后追加错误原因；校验通过的结果同时出现在：

- 投递消息里的 `structured_output:` 段；
- `wait_subagents` 的 `Details[].structured_output` 与结果正文；
- `list_subagents` 的 `Details[].structured_output`；
- `read_subagent` 读回的正文。

schema 只在 `start` 条目落盘一次，`structuredOutput` 随 `idle`/`failed` 条目落盘；重启后列表与 `wait` 仍能看到结构化结果。

## 系统提示词

- 父会话不读单独的「子代理说明文件」。六个工具各带 `Snippet` 与 `Guidelines`，只在工具真正注册时才进 `<tools>` 与 `<rules>`；`delegate` 的工具描述只列 `advertise: true` 的角色，最多 24 个、每个描述 200 字，其余用 `list_subagents` 查。内置角色默认广告；新角色默认不广告，覆盖同名内置角色时沿用内置值，写 `advertise: false` 可以隐藏。`-builtin:subagent`、没有宿主、子代理深度到上限时，这些文字一起消失，不会留下过期的委派指令。
- `delegate` 的 snippet 与 guidelines 直接说明使用时机：直接工作为默认，独立侦察/审查/实现切片才委派；scout 用于不熟悉代码，reviewer 用于非平凡改动，oracle 用于风险决策，worker 用于自包含实现。只有工具可见时才出现。
- 子会话的系统提示词是「基础提示词 + 角色正文 + 委派边界」。边界写明父会话与子代理名字、能否看到历史、结论写进最终回复、不要开新子代理；角色白名单里被父会话拒绝的工具也追加在这里。`output_schema` 附在边界末尾。
- `fresh` 的边界说「看不到父会话的历史，任务写在第一条用户消息里」；`fork` 说「继承了到上一个完整回合为止的历史，本次任务写在最后一条用户消息里」；冷恢复的继续会话说「之前的对话在上下文里，新消息写在后续用户消息里」。三种措辞不能共用，否则模型会误判历史范围。
- 不把运行中的子代理列表塞进系统提示词。它每次变化都会改前缀，破坏 provider 的缓存；要看谁在跑，用 `list_subagents` 或 `wait_subagents`。pi-subagents 的 `active_subagents` 与 `advertised_subagents` 是可选章节，deepseek 的 `tool:subagent` 是固定章节；这里取前者「按需查」的语义，避免每轮变提示词。

## 提示词模板

有子代理宿主的会话额外提供三个内置模板，纯 CLI 不出现：

- `/scout`：在规划或实现前并行启动 scout，收集入口、数据流、约束与风险。
- `/parallel-review`：并行启动 fresh-context reviewer，按正确性、测试、简洁性等角度审查当前改动。
- `/review-loop`：在 worker 与 reviewer 之间循环，最多 3 轮，只修被接受的 P0/P1。

模板本身只是普通 `PromptTemplate`，用户或项目的 `prompts/<name>.md` 同名覆盖；`--no-prompt-templates` 仍然优先，不会把内置模板塞回去。`PromptTemplates.Discover` 不带内置项，`CodingAgentSession` 只在 `Subagents != null` 时合并。

## 角色文件

发现顺序：内置、`<agentDir>/agents/**/*.md`、项目的 `.pi/agents/**/*.md`。同名后发现的覆盖先发现的。

```markdown
---
name: api-reviewer
description: Review HTTP handlers for input validation and error mapping
tools: read, grep, find, ls
excludeTools: write
model: anthropic/claude-sonnet-4
thinking: high
advertise: true
---

You review HTTP handlers. Report findings with file and line references.
```

- `name`：小写字母开头，只含小写字母、数字与连字符，最长 32，不能是 `lead`。省略时用文件名。
- `tools`：子会话工具白名单。支持逗号列表与 `- item` 块列表，也接受 MCP 工具名。省略时沿用父会话可用的工具。
- `excludeTools`：在白名单之上再排除。
- 白名单只做收窄。父会话用 `--tools` 给了纯名字列表时，白名单不能再加回列表外的工具；父会话没显式收窄时，白名单可以补 `read`/`grep`/`find`/`ls` 这类只读发现工具，内置 `scout` 依赖这一点。写工具、shell 和已停用的内置扩展不能这样加回来。
- 被拒的工具写进子会话系统提示词，模型不会按白名单猜；`--exclude-tools`、`NoBuiltinTools` 与计划模式同样先生效。
- 计划模式下 `edit`/`write` 即使在白名单里也会被拿掉；`bash` 留下，但子会话本身也在计划模式，会拦只读命令以外的调用。
- `model`：`provider/id`，或唯一 id。解析失败回落父会话模型。
- `thinking`：子会话思考级别，省略时沿用父会话当前级别。
- `advertise`：是否把角色名与描述放进 `delegate` 的工具描述。内置角色为 `true`；新角色省略为 `false`，覆盖同名内置角色时沿用内置值。完整目录始终用 `list_subagents` 查。
- 正文：追加在子会话系统提示词后面，再后面是委派边界。

内置四个角色：`scout`（只读侦察）、`reviewer`（只读审查）、`oracle`（第二意见，挑战假设但不改文件）、`worker`（实现，带 `edit`/`write`/`bash`）。

## 在界面里长什么样

- 工具行收起时给一句人话：`委派 scout · 找出登录流程的入口。`、`等待子代理 code-review, widget-wiring`。
- 展开后按字段分栏：角色、名字、上下文、任务全文、等待目标、超时；结果区是给模型的那段文本，不是 JSON。
- 代码块底色改成 `CardBackgroundAlt`（比页面高一档），行内代码取两者的中间色：原来深色主题里 `#0f0f0f` 的块贴在 `#141414` 的页面上几乎看不出边界。
- 工具结果的正文是可读文本，结构化数据在 `Details` 里。`JsonNode.ToJsonString()` 默认把中文写成 `\uXXXX`，这里统一用 `PiJson.NodeOptions`。
- 回报消息会触发新的一次请求，但不另起「执行过程」组：从用户视角它仍是同一问一答里的又一步，折进当前组，避免时间线上并排两个组。`子代理回报「scout-1」` 加首句预览（跳过 `session_id:` 那层信封），点开才是 Markdown 全文。组头摘要把它算成单独一类（「收到子代理回报」），不会被「执行了命令」概括掉；组头计数也单列一项。
- 展开后的回报按助手排版渲染：Markdown、代码块复制、图片预览、链接、PRIMARY 选择都和其它消息共用一套接线。超过 1600 字给固定视口自己滚，长审阅不会把时间线顶走。
- 回报不是用户提问：`UserMessage.IsUserAuthored` = `CustomType is null`。分叉点（`get_fork_messages`、桌面分叉列表）、重试取的最后一条提问、会话标题、HTML 导出都按这个过滤，否则机器文本会冒充用户发言。
- `customType` 与 `display` 跟 pi 同一形状落盘（`{"role":"user","customType":"…"}`，隐藏注入才写 `"display":false`）。漏写会让重启后的回报变回普通提问：分叉、重试、标题与导出全跟着错。
- 修落盘之前的旧会话没有类型标记：桌面按正文前缀（`子代理「`、`子会话「`、`来自父会话「`）认回来，只影响显示，不改文件，也不让它们从分叉点里消失。
- 排队卡里回报没有「撤回输入框」与「丢弃」：把机器文本撤进草稿没有意义。
- 子代理条的状态：启动中 / 运行中 / **已完成** / 失败 / 已停止。`已完成` 表示这一轮做完了、还能继续发消息。运行中额外显示当前工具（`运行中 · read src/Auth.cs`），工具返回后清掉；进度摘要显示在展开区，`list_subagents` 与 wait 心跳也带上。完成的行再带输入 + 输出 token 与总耗时（含工具与等待，不是模型回复耗时）；`list_subagents` 的 `Details` 里是 `tokens` / `cost` / `elapsed_ms`。旧条目里的 `tokens` 曾按 `TotalTokens`（含 cacheRead）记录，恢复时会按子会话文件重算成与标签页「输入 / 输出 token」一致的口径。
- 每行可展开：角色、模型、思考级别、任务全文、进度、最近回复与会话文件都在卡里。运行中的耗时自己走秒，不靠工具活动刷新。行按「运行中在前、其余按结束时间倒序」排，刚结束的一轮不用翻到列表底部。卡片最多画 20 行，更早的运行留在侧栏与 `list_subagents` 里，长会话不会把整棵 UI 拖重。
- 子代理在自己的标签里 `ask_question` 时，卡片状态改成「等待回答」，父会话标签带圆点，窗口弹一次 toast。回答后自动恢复。
- `wait_subagents` 阻塞期间每 5 秒把「在等谁、在干什么」推一次，界面不会只剩一行「等了十分钟」。

## 生命周期

- 深度上限 1。子代理会话不注册 `delegate` 系列工具，也不带关联会话工具，递归停在第一层。
- 并发上限 4，超了直接拒绝，不排队。给空闲子代理发消息也算开一轮，同样受这个上限约束；首轮还在启动时 `send_to_subagent` 先排队，不会和首轮抢子会话。
- 每个用户回合最多启动 32 个；子代理回报触发的父会话轮次不重置预算，用户自己发消息才清零。
- 连续 6 次回报拉起父会话后停止自动投递，用户发消息清零。停投的正文仍在 `Result` 与子会话文件里，`wait_subagents` 还能取。
- 单次运行默认 30 分钟超时。到点把状态落到 `failed` 并投递一条带 `timed out` 的结果，迟到的 `agent_end` 不会把失败改回 idle；再发消息就是新的一次运行，重新计时。
- `send_to_subagent` 的 `steer` 把消息放进当前运行的 steering 队列，下一个工具边界生效；空闲时用 `follow_up`，`auto` 在运行中选 steer、否则退回 follow_up。
- 子会话在桌面上开一个后台标签，不抢当前标签；侧栏里挂在父会话下面，不占顶层会话行，也不占侧栏 200 条的顶层名额。当前标签是子会话时，父行会自动展开。有 UI 宿主，所以子代理可以自己用 `ask_question` 问用户。
- 子会话不加载扩展，工具面只有角色白名单。
- 子会话只弱引用父会话：父标签关掉、子标签还开着时，父会话的整份消息不会被吊住。父标签关闭会停掉正在跑的子代理，避免结果无处投递还继续烧 token；子会话标签与文件保留，仍可读取或继续。
- 子会话头写 `parentSession`，父会话写 `piweft-subagent` 自定义条目（`start` / `started` / `idle` / `failed` / `stopped`，结束条目带 400 字摘要）。重启后按条目恢复列表（`list_subagents` 里 `restored: true`）；恢复项没有活会话，但 `send_to_subagent` 会按会话文件冷恢复，重新订阅并继续对话。计划模式下建的关系把 `planMode` 一起落盘，恢复后仍只读。任务全文只在 `start` 条目写一次，模型与思考级别跟 `started` 一起落盘，`startedAt`/`finishedAt` 随状态条目落盘；卡片展开与总耗时在重启后仍有内容。
- 同一时刻结束的多个子代理合并成一条消息，避免一次拉起多轮父会话。
- `stop_subagent` 只停当前回合；子会话还在，可以继续读，也可以再 `send_to_subagent`。关掉子会话标签也同理：状态落到 stopped，会话文件还在，发消息就会冷恢复。停过之后再发消息，新一轮的结论照常回报（`Stopping` 只属于被停的那一轮）。
- 建子会话是慢活：这期间被停掉，首轮不会再起，工具调用收到「stopped before its first turn」。

## 性能实现

- 角色目录每个会话建一次，`FrozenDictionary` 查找；子代理复用父会话的目录与资源快照，不重扫技能与模板目录。
- `System.Threading.Lock` 保护 hub 状态，事件回调返回 `ValueTask`。
- `wait_subagents` 用 .NET 9 的 `Task.WhenEach`：完成一个就推进一次，另有一条 5 秒心跳只负责「还在跑」的进度；截止时间用 `CancellationTokenSource.CancelAfter` 表达，超时只停止等待，子代理继续跑。
- 桌面子代理卡按结构比较：只有行数、名字、会话 id、状态变了才重建行，活动变化只改那一行右侧的文字，hover 提示与卡内焦点不丢。
- 结果投递有 200ms 合并窗口，N 个子代理同时结束只拉起一轮父会话；投递走 `Task.Run`，不捕获 UI 上下文。
- 结果截断用 span，不切数组；正文超过 12k 字符只保留头部并指向子会话文件。工具结果 `Details` 里给界面卡片留的正文压到 4k，避免同一段正文在会话文件里存两份。
- 全部代码无反射、无动态、无第三方依赖，NativeAOT 安全。

## 限制

- 不支持外部 CLI 角色（claude-code / codex / cursor）。
- 没有 worktree 隔离。同进程的 `FileMutationQueue` 只保证同一文件的写串行，跨文件冲突仍要父会话复核。
- 子代理在自己的标签里提问。父会话阻塞在 `wait_subagents` 时也能在子标签作答，因为标签与回合是独立的。
- `wait_subagents` 默认等 10 分钟；超时后结果里直接给出下一步（结束回合等消息），免得模型再等一轮。
- 子代理条上的历史项标「可继续」：给它发消息就会从会话文件重新打开；点行打开转录。
