# 子代理

`delegate` 把一个自包含任务交给带角色的子会话，最终回复作为消息回到父会话。纯 C# 实现，不依赖 JS、Node 或外部 CLI。

## 为什么是异步

同步调用把父回合卡在工具调用里。子级要用户拍板时没有出口，用户中断父回合又要额外决定子会话怎么收尾。异步把启动和等待拆开：

1. `delegate` 立即返回子代理名字。
2. 每轮结束，最终回复作为一条用户消息回到父会话。父会话空闲时直接起一轮，忙时排 follow-up。
3. 下一步依赖结果时，用 `wait_subagents` 显式等；取走的结果不再重复投递（同一段正文只进一次上下文）。工具结果的 `Details` 里每个子代理带 `returned`（正文已随本次调用返回）与 `result`，桌面据此补一张只读回报卡：看得见，不写会话，不多起一轮。

这和三家参考实现的取舍一致：deepseek 的 `delivery: 'caller'` 就是「调用方等结果，不发父级通知」；pi-subagents 的完成通知只带 2 KB 以内预览，与 `bg_wait` 的完整返回并存，靠 run key 去重；PiDeck 的关联会话没有等待概念，每轮都回报。这里取前者的语义，再用只读卡补上后两者的可见性。

结果回父会话有两条路径，差别只在「要不要把结果交给模型继续做」：

| 路径 | 写会话 | 进上下文 | 起一轮请求 | 时间线 |
|---|---|---|---|---|
| 没人等，子代理跑完 | 是（`piweft-subagent-result` user 消息） | 是 | 父会话空闲就起一轮 | 回报步骤（默认一行） |
| `wait_subagents` 取走 | 否，只在工具结果的 `Details` 里 | 只经工具结果进一次 | 否 | 只读回报步骤 |

第一条是异步委派的默认语义：结果到了就让父会话接着做，否则模型根本不知道子代理说了什么。第二条是「同一轮里就要用它」时的捷径，代价是不再另发消息。

投递时机是父会话这一轮结束：子代理在父会话跑一轮时完成，结果先攒着，等 `agent_end` 再投。这样父会话阻塞在 `wait_subagents` 上时，不会有第二条路径把同一段正文再排成 follow-up。

`pi` 的 pi-subagents 默认也是异步，deepseek 的委派工具只提供异步。这条路线不是简化版。

## 工具

| 工具 | 用途 |
|---|---|
| `delegate` | `{ agent, task, name?, context? }`，启动子代理，立即返回 |
| `wait_subagents` | `{ names?, timeout_ms? }`，等到结束一轮或超时 |
| `list_subagents` | 本会话的子代理与可用角色 |
| `send_to_subagent` | `{ id, message }`，追加一轮，不等回复 |
| `stop_subagent` | `{ id }`，停当前回合，子会话仍可读 |

工具只在宿主支持子会话且深度未到上限时注册。纯 CLI、`--tools none`、子代理会话本身都拿不到这些工具。

开启 codemode 后，脚本可以 `await tools.delegate(...)` 批量启动，再用 `await tools.wait_subagents(...)` 收集结果。Jint 已经在依赖里，不需要第二个 JS 运行时。

## 上下文

- `fresh`（默认）：子会话只看得到任务本身。
- `fork`：把本会话当前分支投影里最后一条不含工具调用的助手消息及其之前的内容写进子会话。当前回合的助手消息一定带 `delegate` 调用，所以截到这里就避开了半截回合。没有已完成回合时直接拒绝，提示改用 `fresh`。

fork 的子会话仍用角色的系统提示词与委派边界，不会复用父会话的请求前缀。

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
---

You review HTTP handlers. Report findings with file and line references.
```

- `name`：小写字母开头，只含小写字母、数字与连字符，最长 32，不能是 `lead`。省略时用文件名。
- `tools`：子会话工具白名单。支持逗号列表与 `- item` 块列表。省略时沿用父会话可用的工具。
- `excludeTools`：在白名单之上再排除。
- 计划模式下 `edit`/`write` 即使在白名单里也会被拿掉；父会话用 `--tools` 收窄过的工具，子会话不会绕过。
- `model`：`provider/id`，或唯一 id。解析失败回落父会话模型。
- `thinking`：子会话思考级别，省略时沿用父会话当前级别。
- 正文：追加在子会话系统提示词后面，再后面是委派边界。

内置三个角色：`scout`（只读侦察）、`reviewer`（只读审查）、`worker`（实现，带 `edit`/`write`/`bash`）。

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
- 子代理条的状态：启动中 / 运行中 / **已完成** / 失败 / 已停止。`已完成` 表示这一轮做完了、还能继续发消息。运行中额外显示当前工具（`运行中 · read src/Auth.cs`），同一个值也进 `list_subagents`。
- `wait_subagents` 阻塞期间每 5 秒把「在等谁、在干什么」推一次，界面不会只剩一行「等了十分钟」。

## 生命周期

- 深度上限 1。子代理会话不注册 `delegate` 系列工具，也不带关联会话工具，递归停在第一层。
- 并发上限 4，超了直接拒绝，不排队。
- 每个用户回合最多启动 32 个；子代理回报触发的父会话轮次不重置预算，用户自己发消息才清零。
- 子会话在桌面上开一个后台标签，不抢当前标签；有 UI 宿主，所以子代理可以自己用 `ask_question` 问用户。
- 子会话不加载扩展，工具面只有角色白名单。
- 子会话头写 `parentSession`，父会话写 `piweft-subagent` 自定义条目（`start` / `started` / `idle` / `failed` / `stopped`，结束条目带 400 字摘要）。重启后按条目恢复列表；恢复项没有活会话（`list_subagents` 里 `restored: true`），`send_to_subagent` 会提示会话未打开，桌面仍可按 `session_id` 打开子会话转录。
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
