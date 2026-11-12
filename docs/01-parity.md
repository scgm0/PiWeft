# pi / PiDeck 兼容与差距

PiWeft 复用 pi 兼容核心；桌面用 MewUI + NativeAOT，PiDeck 用 Electron + React。本文记录桌面功能、配置安全边界与剩余差距。当前实现见 [状态](00-status.md)，视觉契约见 [DESIGN.md](../DESIGN.md)。

对照基准（只读）：pi `v1.5.2+22`（`b30d54b8a`）、PiDeck 0.9.0；pi_agent_rust / pi-go 仅作方向参考。

## 视觉与主题

配色源于 PiDeck `foundation.css`，集中在 `AppPalette`；尺寸与字号在 `AppTheme`；完整规则见 DESIGN.md。

| 维度 | 深色 / 浅色 |
|---|---|
| 背景 | app `#121212` / `#ffffff`；侧栏 `#171717` / `#ffffff`；弱化底 `#262626` / `#f4f4f5` |
| 文本 | primary `#ecece7` / `#202124`；secondary `#b8b8b2` / `#4b5563` |
| 边框 | `#2e2e2e` / `#dfdfdf` |
| 强调 | `#fafafa` / `#18181b` |
| 字号 / 圆角 | 12–18；4/6/8/10/12 |
| 控件高 | 28 / 32 / 36 |

换肤调用 `Application.SetTheme`，不重建界面：命名样式按主题解析；`TextBlock` 用 `Tag` 角色刷新前景；图标缓存按颜色刷新（弱引用）；消息卡片、工具、思考、diff 原位换色，保留 Markdown 实例、选区、展开状态与草稿。MewUI 只从 `external/MewUI` 源码子模块构建。Linux 用 X11 + MewVG，Windows 用 Win32 + Direct2D，macOS 用 MacOS + MewVG。字体：HarmonyOS Sans SC Regular（正文）、Maple Mono NF CN（代码/终端）；Lucide SVG 内嵌。

## 桌面功能对照

| 能力 | 已实现 | 尚未覆盖 |
|---|---|---|
| 项目/会话 | cwd 分组、添加/移除项目（空目录也保留）、环境隔离、并行标签、置顶、命名、归档/恢复、最近项目 | 项目级置顶、标签拖拽/分屏、批量操作 |
| 搜索 | 元数据 + 有界正文搜索、会话内过滤 | 正则、命中高亮 |
| 消息 | Markdown、高亮、复制、图片放大、思考折叠 | 代码行号、跨行块注释、更多语言 |
| 工具 | 折叠摘要、状态/耗时、按工具分栏的参数与输出、统一/并排 diff、大文本固定视口虚拟化；shell 侧删除前备份进回收站、Windows `> nul` 改写、模型专用代理不传给 agent 命令 | 完整文件交互 |
| 历史 | 编辑重发、重试、继续生成、fork、分支树、跳转、HTML 导出 | 多选批量、分支比较 |
| 输入/模型 | 斜杠命令、命令面板、改键、快捷消息（插入/直发）、模型筛选/搜索/刷新、记住上次使用、每模型思考档 | 模型收藏 |
| 附件 | 路径引用、图片文件/字节、粘贴、复制预览；进上下文前按模型限制应用 EXIF 方向并缩放/转码 | 更多媒体类型、回复转图片 |
| 用量 | 会话统计、上下文、费用、缓存、压缩；供应商余额/套餐；跨会话热力图 | 火山 AK/SK、Sub2API 登录态、热力图下钻 |
| 设置 | 模型/供应商、供应商认证台（40+ 内置、OAuth / API Key 双方法、搜索、状态来源）、Agent、MCP、JSON、作用域、快捷消息、草稿保护、安全门、导入 | 技能/提示词管理、商店、安全门图形编辑与审批 |
| MCP | 启动连接、状态、stdio 热刷新、手动重连 | 自动重连、跨项目运行时管理 |
| Git | 状态、目录树、文件差异浮层（与轮末 chip 同一块）、暂存、提交、分支、AI 摘要、图、比较、fetch/pull/push、stash | 冲突界面、rebase/cherry-pick/reset、多远端 |
| 文件 | 目录导航、Git 标记、内置编辑器、修改汇总 | 多文件标签、语法高亮编辑、正则搜索 |
| 终端 | MiniPty、shell、scrollback、选择、SGR/备用屏、缩放重排、字素对齐、字格光标、OSC 4/8/10/11/12/52、鼠标、bracketed paste、IME、PRIMARY | 完整 VT100 边界、终端内搜索、多标签、快照回放 |
| 关联会话 | 子会话工具（开 / 发消息 / 列表）、提议卡片（offer / own_initiative）、回合回报、自动续跑额度 6、子会话条、以后台标签打开 | 关系与提议落盘 |
| 主题/窗口 | 深浅/系统、侧栏收起与宽度恢复、尺寸/位置恢复、单实例 | 自定义强调色、多显示器可见性 |

内嵌浏览器、自动更新、托盘、系统通知不在范围内。

## 交互与约束

- 正文/输入居中，阅读宽度 760/1060/1360（默认 1060）；字号 14–22（默认 16），修改立即生效且保留草稿/选区。
- 输入框占位提示自绘：正文为空且 `ITextCompositionClient.IsComposing == false` 时显示；IME 组合期间让位，组合结束后按提交正文决定是否恢复。
- 消息时间线可变高度虚拟化，只建视口附近控件；离屏释放正文/Markdown/图片，保留工具/思考展开状态；分支树首次打开才构建。
- 折叠工具首次展开才建原文/图片/diff；步骤展开后重测行高并重发组体 extent。过程组限高 300 DIP；≤24 条用内容驱动栈，超过切虚拟化列表。
- 会话先落盘、视图模型还没收到 `message_end` 时，同一条助手消息会同时有落盘行与流式行：投影按时间戳只留流式那一份，正文不会既作为中间回复放进过程组、又作为助手消息行出现。
- 行被移除或替换时整视口重画一次：离场元素里溢出的绘制不属于任何场景节点，部分重绘与滚动都不会带走，会留下贴在旧屏幕位置、滚动不变的残影。流式原位更新不触发这次重画。
- 过程组组头跟着最新一步换词：最新一步是思考时给「正在思考」与 sparkles 图标，不沿用上一次工具的「正在运行命令」；其余按最近一次工具调用给「正在…」并附参数详情。
- 组体滚动条置底时跟最新步骤（几何判定，读者上滚才暂停，回到底部恢复）；工具步骤展开用预览行锚点停住，流式思考展开则跟随末尾。
- 组内展开项标题吸顶，只对「已展开且高于视口」的步骤生效；从吸顶标题收起时先把本项开头对齐到视口顶部。
- 消息区右缘定位轴只取真实用户消息；悬浮/键盘聚焦显示摘要与时间，点击/Enter 跳转；长会话先压缩间距再抽稀，保留首尾；窄于 540 DIP 或分支视图隐藏。
- 会话搜索先匹配标题/首条摘要，220 ms 后扫描正文；每个文件最多 512 KiB，结果最多 200 行。会话内过滤同时匹配正文、思考、工具名与过程步骤。
- 流式正文与思考共用增量 Markdown：只解析尾块、按模型哈希 diff、原位更新尾块；40 ms 合并刷新。向上浏览立即停跟底，回车/回到底部恢复。思考段以 `thinking_end` 或正文块开始为终点；provider 不补 `thinking_end` 时，正文一开始过程组就停止「正在思考…」的秒表。
- 跟随状态只由滚轮/触控板/键盘/滚动条改变；近底 1–2 px 抖动累计到 25 DIP 才逃逸。嵌套视口到边界后把滚轮交还祖先。
- 代码块不嵌套纵向视口：普通滚轮滚会话，Shift+滚轮/横向触控板滚代码；复制按钮是宿主注入的 Lucide 图标。
- 并排 diff 是两列可选中、可横向滚动的整篇文档：列宽均分宿主，长行在列内横向滚动且两列偏移同步到同一绝对值，行号列固定在左缘；大补丁正文限高、两侧与行号列共用纵向偏移。
- 右侧 Git 面板不再内嵌 diff 卡片：点一行改动直接把补丁交给文件差异浮层（工作区/暂存可「在编辑器中打开」，提交与分支比较只读）。切走会话、收起面板或切到别的分段时浮层一起收起。
- 用户消息把 `<skill>` 块折叠为 chip；`@"path"` 显示为行内文件 chip（复制/编辑/发送保留原文）；正文中的 `/skill:名称` 也画成 chip。代码块、行内代码与 URL 不参与识别。
- 轮末修改按文件聚合：chip 显示文件名、本轮改写次数与增删行数；只占一行，放不下的收进 +N 展开。左键打开本轮该文件的补丁浮层（统一/并排，Esc 关闭），补丁取自工具结果。
- 文件可拖入会话区；目录/文件插入引用，图片后台读取为附件，不自动发送。图片在发送或排队消息出队时（线程池上）按当前模型的 `inputLimits.images.resize` 处理：限制内原样保留，超限才缩放/重编码，失败的那张以一句括注代替。
- 命令面板每次打开取一份命令快照，最多 12 项；模型选择用虚拟化供应商树，搜索覆盖全部模型。
- 快捷消息入口在输入区底栏（Ctrl+Shift+M），弹层贴在按钮上方打开，搜索 + 每页 8 条：点条目把原文换行追加到草稿尾，行尾按钮直发且不动草稿与附件。清单存 `desktop.json` 的 `quickMessages`，最多 30 条、单条 200 字；设置「快捷消息」分区一行一条编辑，与出厂清单相同时不写键，显式清空写空数组。运行中、有待发附件或编辑重发未决时直发置灰。
- 可用模型要求 API 已实现且能解析凭据；不执行命令型凭据、不发网络探测。
- 命令/模型/图片面板是窗口级覆盖层，同一窗口只开一个；Esc 关闭，切换标签清理。
- 计划模式在回合结束时从 `Plan:` / `计划：` 下的编号行取步骤，推 `pi-deck-plan-todos` 草案，再弹 `[PI_DECK_PLAN_NEXT]` 选单：执行会恢复写权限并立刻起一轮，`[DONE:n]` 逐条勾进度；先不执行保持只读，关闭选单退出计划模式。步骤状态写 `pi-deck-plan-mode` 条目，只在执行态随分支恢复。
- 目标模式带 `__PI_DECK_GOAL_MODE__` 标记进入，续轮指令以隐藏 follow-up 交给模型（首行 `[GOAL MODE ACTIVE] Round: n/m`，目标全文只在系统提示 section 里）；`GOAL_COMPLETE` / `GOAL_BLOCKED: 原因` / 轮次上限（默认 32）结束，中止与出错不推进轮次。暂停/阻塞时带标记的正文只当本轮指令，目标保持不变。状态写 `pi-deck-goal-mode` 条目，目标全文只在变化时落盘、重启只恢复为暂停；`/goal resume` 与目标条的恢复在空闲时真的补一轮。
- 关联会话由 `start_session` / `send_to_session` / `list_linked_sessions` 驱动（名字与 PiDeck 扩展一致）：子会话是同一项目的完整后台会话，独立标签、不影响当前标签。每轮结束把最后一条回复前 400 字回报父会话；父会话空闲时直接起一轮，忙时排 follow-up。连续 6 次回报自动续跑后停止，用户在父会话发消息清零额度；关系只在进程内，重启后子会话仍在但不再联动。纯 CLI 没有宿主，不注册这三个工具。
- 提议卡片分两种：`offer` 默认，`start_session` 等用户点「新会话 / 在此做 / 不要」，最长 10 分钟，超时按没回答返回；`own_initiative` 只登记卡片、工具立即返回，用户点「在此做」会把简报作为用户消息发回父会话。桌面子会话条列出待决定提议与已开子会话，子会话行点击切到后台标签。
- `ask_question` 是会话内联卡片（非遮罩）：题卡标签 + 进度 + 整行选项 + 自定义输入 + review 确认页；卡片打开时输入区折叠，页签标题加 `●`。文本题与自定义输入支持拖入本地路径，按主输入框的 `@` 引用插入；切题与多选重绘保留未提交草稿。调用展开后按题号列出题目，答完的题在题干下贴选中的选项文案（自定义输入贴原文），取消或没答完时原样贴结果。
- 等待态常驻输入卡上方，阶段优先级退避 → 压缩 → 工具 → 正文 → 思考 → 预热/空窗；预热期三句轮播，回合内空窗只说「正在处理」；静默 2 秒补「本轮已 N 秒」。时间线尾部的等待占位行只在等首 token 时出现，条件是最后一行是用户消息且没有任何一行在流；它报时的时候状态条不再重复同一个数字。
- 提示音：完成/出错/等待输入三类，默认完成与出错开、音量 0.6；会话完成 8 秒、全局 1.5 秒冷却；出错按边沿、等待输入按批去重；「仅失焦提醒」在前台静默。
- 跨项目会话使用各自 cwd；运行时发送键变中断，保留草稿并禁用模型切换与历史修改。Split Send 菜单支持 steer / follow-up / 中断，排队卡可撤回或丢弃。
- 继续生成：上一轮失败或输出被截断后，草稿为空时发送键变「继续生成」。点它不新增提问，先落一条空的隐藏 custom message（`piweft-continue`）当新回合的准入标记；时间线上只留一行「已请求继续生成」，失败那轮保持独立，半截回答不会被下一轮挤进过程组。主动中断不提供继续（那条助手消息不进重放，继续等于重提，交给标题栏的「重试」）。有草稿、有待发附件或编辑重发未决时不接管主按钮；空草稿回车仍不动作。
- Markdown 链接只放行 http/https；编辑器优先 `$VISUAL` / `$EDITOR`；HTML 导出无脚本且自包含。

### 历史思考耗时

桌面以 `recordInitialThinkingLevel: false` 打开会话，不追加条目或改 mtime。标准 pi 会话没有独立思考计时字段，`durationMs` 是整次回复耗时，不能据此推测思考耗时；`firstTokenMs` / `streamingDurationMs` 口径见 [性能](02-performance.md)。

## Linux PRIMARY 与图片剪贴板

- Linux 下选中的正文、Markdown、代码/工具输出和输入框文本自动发布到 PRIMARY；中键在可点击位置插入，不自动发送，可撤销。普通 Ctrl+C/V 走 CLIPBOARD；密码框不发布。
- PRIMARY 默认由进程内 X11 实现持有（独立 Display + 事件线程，`libX11.so.6`，`poll` + `eventfd`，大文本按 ICCCM INCR 分块）。失败时按 `xclip` → `xsel` 回退；无 DISPLAY 才用 `wl-copy`/`wl-paste`。
- 图片粘贴：剪贴板有图片就作为附件，否则交回文本粘贴；右键「粘贴」走同一命令；预览可「复制图片」。
- 解码统一用 MewUI `ImageDecoders` / `ImageSource.CopyPixels`；Windows DIB↔PNG 为纯托管最小实现。

## 外部会话导入

`pi import` 与桌面「导入外部会话」共用 `src/PiWeft.CodingAgent/Import/`。

| 来源 | 根目录 | 形态 |
|---|---|---|
| Claude Code | `~/.claude/projects/<slug>/<sessionId>.jsonl` | user/assistant + content 块 |
| Codex CLI | `~/.codex/sessions/<Y>/<M>/<D>/rollout-*.jsonl` | session_meta + response_item，兼容旧 event_msg |

- 列举只读头部（每文件 ≤256 KiB，单来源 ≤500 条），导入时才逐行全量读取。
- `tool_result` 还原为 pi `toolResult`；一个 `tool_use` 收尾一条助手消息，保证调用/结果相邻；跳过 `isSidechain` / `isMeta`；保留模型、用量、时间戳。
- 同目录临时文件 + rename 原子落盘；链尾追加 `piweft-import` 标记（不能 `parentId: null`，否则叶子断链）。
- 重复导入产生新会话，不覆盖；只读源库，不写回；无 SQLite 依赖（不支持 OpenCode/ZCode/Cursor/Kimi）。

## 扩展宿主

`src/PiWeft.CodingAgent/Extensions/`。一个扩展 = 一个 Jint 引擎 + 专属线程；注册与回调都投递到该线程。

- 发现 `<agentDir>/extensions` 与受信项目的 `.pi/extensions`（`.js/.mjs/.cjs`、`.ts/.mts/.cts/.tsx`，排除 `.d.ts`）；显式 `-e` 与 `settings.extensions` 不受信任门控；同一路径只加载一次。
- `settings.extensions` 的 `!glob`/`+path`/`-path` 过滤发现结果（`!` 只实现 `*`/`**`/`?`，不支持字符类）：全局按「排除 → 包含 → 排除」、项目最后一条匹配覆盖，与 pi 的 package-manager 一致。停用的扩展仍出现在设置页，只是不进宿主。
- 内置扩展是原生实现；`-builtin:mcp`/`-builtin:llama.cpp`/`-builtin:codemode`/`-builtin:tool-search`/`-builtin:subagent` 分别关掉 MCP 连接、`/llama`、codemode、`tool_search` 与子代理委派。设置页开关写 `settings.json`，改动在新建会话或重启后生效。
- 桌面设置页另列「桌面内置扩展」：只列已移植的 PiDeck `pi-deck-*`（去 `.ts` 后缀），随应用提供、不写 `settings.extensions`，开关只读。
- `tool_search` 与 codemode 一样是目录里的内置工具，但默认启用（pi 的 `builtin:tool-search` 是 `defaultActive: false`）：MCP 工具默认 codemode exposure，不默认开启就没有发现入口。`-tool_search` 与 `-builtin:tool-search` 都能关掉它。
- 「Agent 设置」的工具清单是整份目录：被 `settings.extensions` 挡住的内置扩展仍列在「内置扩展工具」里，名字压暗并提示原因；已写在 `defaultTools` 里的同样压暗，计数只算真会加载的工具。
- 注册面：`pi.on`、`pi.registerTool`、`pi.registerCommand`、`pi.registerFlag`/`getFlag`、`pi.ui.*`。未知 `--flag` 交给注册者，无注册者时报错退出 2。
- 事件：实际派发 `session_start`、`session_info_changed`、`session_shutdown`、`tool_call`、`tool_result`、`before_agent_start`、`agent_start`、`agent_end`、`turn_start`、`turn_end`、`message_start`、`message_end`、`tool_execution_start`、`tool_execution_end`、`model_select`、`thinking_level_select`；`message_update`、`tool_execution_update`、provider/context 类事件尚未派发。
- 事件处理器可返回对象；返回值合并进 payload，与就地改写等价。
- 沙箱无 `fs`/`net`/`require`/动态 import；出口只有显式 `__pi*` 宿主函数，桥一律 `ClrFunction`（AOT 无警告）。
- 扩展的 `tool_call` 钩子排在安全门之前，但放行的调用仍过档位检查；内置工具优先于同名扩展工具。
- `/reload` 重建宿主；注入的宿主生命周期归注入方；并表从内置基线重建。单个扩展抛错只记自身诊断；卡死扩展 10 秒切断。
- 限制：只执行 JavaScript（TS 需转译）；处理器必须同步，返回 Promise 会明确报告并丢弃。

## Git 远端同步与 stash

`GitWriter` 覆盖 fetch/pull/push/stash，全部用 `ArgumentList` 传参；以 `-` 开头的名字拒绝，stash 引用只收 `stash@{n}`。本地命令 30 秒、网络 120 秒超时，stdout/stderr 并发读。

- pull 只用 `--ff-only`：没有冲突界面，不能快进就让用户去终端。
- 首次推送自动建跟踪：只有一个远端用它；多个远端有 `origin` 用 `origin`；都没有则不猜。分离头指针拒绝。
- 无远端/无上游时按钮变灰并说明原因。
- 网络操作设 `GIT_TERMINAL_PROMPT=0`，避免等待密码。
- 所有 git 子进程钉住 `LC_ALL=C`/`LANG`/`LANGUAGE`，分类表按英文特征串匹配；原始输出留在 `GitResult.Detail`。
- 未跟踪文件 diff 不走 `git diff --no-index`（Windows 无 `/dev/null`），自行合成整份新增的 unified diff。
- 状态读取用 `--porcelain=v1 -z`（原始 UTF-8、无引号）；改名条目按 `R`/`C` 多消费原路径字段。中文/空格/emoji/换行/改名路径有回归测试。
- 分离头指针归一为空分支名并置 `Detached`，不出现在分支下拉。
- 面板三段：分支栏、「更改」区（提交框 + AI 摘要 + 改动树 + diff）、折叠「更多」（stash、提交摘要设置、无远端提示）。改动按「合并冲突/已暂存/未暂存」分节；目录链合并为一行，可整树暂存/取消暂存。
- AI 提交摘要读暂存区 diff（≤8000 字符），默认跟随会话模型/思考档，可在设置固定；模板用 `{diff}` 占位，空则回退内置 Gitmoji 模板。输出剥掉围栏/前缀/引号；失败按原因内联提示，原始错误进展开条；进度按 90 秒上限爬升。
- 网络操作后台执行，完成后回 UI 线程；暂存与 `git init` 仍同步。推送/拉取徽标显示 ahead/behind，只在该方向有差异时出现。
- 面板按节独立宿主，选行只换 diff、折叠只重画改动树、展开提交只重画图；行的显示内容不参与命中，避免悬停叶子频繁切换。
- 四节可折叠、可拖高（6 DIP 命中，正文最小 60 DIP，三节合计 ≤ 面板六成），状态按仓库写入 `desktop.json` 的 `gitPanes`。拖动量按「按下时的窗口坐标 + 当时高度」换算绝对值，不能用增量累加。
- 源代码管理图：`git log --all --topo-order -n40`（排除检查点与 stash），最多 8 条泳道；引用筛选、刷新、加载更多各自失效缓存；提交详情按 hash 缓存。
- 比较更改：`git diff base...target` + `rev-list --left-right --count`，只读本地仓库；点击文件把三点 diff 送底部面板。
- 冲突与分叉不假装能处理：`--ff-only` 失败、push 被拒、stash 冲突都只给「去终端解决」的指引。

## 配置管理与安全边界

| PiDeck | 行为 | 本端口 |
|---|---|---|
| `ModelsTab` | 供应商/模型编辑、复制、导入导出、保留高级字段 | `ConfigurationView.Forms.cs`、`ConfigurationModels.cs` |
| `AuthTab` | API Key 与 OAuth 分开 | 同一供应商合并两种方法；API Key 表单按供应商生成字段，覆盖 OAuth 前确认 |
| `McpTab` | 全局/项目、enabled、stdio/HTTP，保存不启动 | `ConfigurationView.Mcp.cs` |
| `piConfigFileStore` | revision、未知字段、原子替换、外部修改冲突 | `ConfigurationStore.cs` |
| `ConfigManager` | 配置领域与视图分离 | `ProviderProbe`、`RemoteModelCatalog` |

- 设置分系统设置/配置管理；自定义供应商支持新增、编辑端点/协议、复制、删除、导入 JSON、脱敏导出；模型字段补丁保留 `headers`/`cost`/`compat`。
- 认证：内置供应商目录（42 个）同时展示 OAuth 与 API Key；API Key 支持遮罩输入、命令型凭据、`${VAR}` 模板、环境变量来源和供应商专属字段（Cloudflare 账户/网关、Vertex 项目/区域/ADC、Bedrock AWS 密钥）。保存 API Key 会替换同名 OAuth，必须在界面确认；删除会清掉 auth.json 条目但不动环境变量。Agent 设置含默认供应商/模型/思考档、自动压缩/重试；未修改的继承值不写项目覆盖。
- MCP：全局/项目配置、stdio/HTTP、参数数组、启停/删除，保留 env/headers；保存不自动重连。
- 源文件（模型、认证、设置、MCP、`usage-probes.json`）可显示、编辑和修复损坏文件；草稿独立于主题刷新，切页/重载/关闭均保护未保存修改。
- 文件读写限制在 agent 目录与固定项目 `.pi` 路径；认证不写项目作用域；读写 ≤2 MiB；只接受对象根节点并校验关键字段。
- 写入用同目录临时文件 + flush + 原子替换；Unix 新配置 0600；桌面写入有排他锁并检测 pi `.lock` 与 revision；不宣称与非协作编辑器事务互斥。
- 导出移除 API Key、认证字段、headers/env/OAuth 与 URL 查询/userinfo；不自动导出 auth.json；日志不输出配置内容或异常原文。
- 模型目录/Agent 设置只作用于新会话；已有会话不运行时替换。MCP 保存后需手动重连。

### 供应商认证

桌面复用 `PiWeft.Ai.Auth` 的 OAuth（与 `pi login` 同一套流程和 `auth.json`）：Anthropic（授权码）、OpenAI ChatGPT（回环）、Codex（设备码）、GitHub Copilot（设备码 + 企业域名）、xAI、Kimi、Meta、Radius（设备码）、OpenRouter（浏览器换取 Key）。面板实时输出日志，提取授权 URL/设备码，支持输入授权码/回调 URL、取消和重试；凭据由 `OAuthStore`/`CodexAuth` 原子写入全局 `auth.json`（Unix 0600），界面不显示 token。登录/退出后重建模型环境并推给所有标签页。

API Key 目录在 `ProviderAuthCatalog`，与 `pi login <provider> [oauth|api-key]` 共用同一份供应商事实：标准供应商一个 `key`，Cloudflare / Vertex / Bedrock 使用额外 `env` 字段或环境变量 marker。这些字段参与请求：Cloudflare 占位符由凭据 env 展开、网关令牌走 `cf-aig-authorization`，Vertex API Key 走 express 端点、ADC 读取凭据文件与项目/区域，Bedrock AWS 密钥参与 SigV4 签名。认证页把 OAuth 与 API Key 合并成一行，显示已保存 / 环境变量 / 命令型 / 外部凭据 / 未配置与过期状态；内置目录覆盖 `models.json` 全部 42 个供应商，自定义 auth.json 条目也能编辑或删除。API Key 面板不回显已保存密钥，留空表示保留；覆盖 OAuth 前必须确认。

### 供应商请求头与会话亲和

- OpenCode / OpenCode Go：所有请求补 `x-opencode-session`（当前会话 ID）与 `x-opencode-client: piweft`；显式同名头优先，缺失会被网关 400。
- 会话亲和：按模型 `compat.sendSessionAffinityHeaders` / `sessionAffinityFormat` 发送 `x-session-id`、`session_id`（Completions）或 `session-id`（Responses）、`x-client-request-id`、`x-session-affinity` 的子集；OpenRouter 默认 `x-session-id`。
- GitHub Copilot：静态目录头之外，补 `X-Initiator`、`Openai-Intent: conversation-edits`，含图片补 `Copilot-Vision-Request: true`；Anthropic 代理用 Bearer + `anthropic-dangerous-direct-browser-access`。
- Claude OAuth：Bearer + `claude-cli/2.1.322` UA、`x-app: cli`、浏览器直连标记、`claude-code-20250219,oauth-2025-04-20` beta，并在 system 首块注入 Claude Code 身份提示。自适应思考、`supportsTemperature`、错误工具结果图片后置、会话级严格工具降级与 message thread 续聊已接入。
- provider 级 `headers`/`authHeader` 同时作用于内置、远程缓存与自定义模型，模型同名头优先；`authHeader: true` 追加 `Authorization: Bearer <key>`。
- OpenRouter `HTTP-Referer`/`X-Title` 与 TokenDance `X-App-URL` 不是功能必需；不伪造公共 app URL。

### 用量查询

配置在 `~/.pi/agent/usage-probes.json`（与 PiDeck 同构）：`providers` 由弹窗维护，`probes` 供手写，运行时按 `baseUrl` 合并。旧 `usage-queries.json` 只读兼容。默认关闭，需显式启用。超时预算覆盖整次探测（预检 + 所有候选 URL），失败也会沿用配置的自动查询间隔。

内置候选：OpenCode Go、DeepSeek、OpenRouter、Kimi、Moonshot、SiliconFlow、智谱 GLM、Codex、xAI、Command Code、TokenDance、MiniMax、通用 `/usage`。OpenCode Go 走 origin 下的 `/zen/go/v1/usage`；OpenCode Zen 的用量在 Console 预算接口，需要 workspace 凭据，暂不内置。结果统一为 `periods` / `balance` / `credits`。展示为进度条（≥90% 红、≥70% 橙）与剩余量；失败显示尝试过的 URL、HTTP 状态/超时与脱敏摘要。安全边界：不跟 3xx；仅 HTTPS 或本机 HTTP；内置候选按 host（可带路径前缀）匹配，用户 probes 仍按 `baseUrlContains` 子串匹配；跨域只允许内置官方地址；模型自定义头只回发供应商域；响应 ≤1 MiB；明细抹掉 Key 并截断 240 字符。

### 工具调用安全门

配置为 `SecurityConfig`（总开关 + 默认档 + 档位表 + `sessionLevels`），入口 `ToolSecurity.Evaluate(tool, args, cwd)` 返回 `Allow`/`Ask`/`Deny`，挂在 `GuardedBeforeToolCallAsync`（直接调用、codemode 嵌套、MCP 工具唯一汇合点）。

| level | 行为 |
|---|---|
| `off`（默认） | 全部放行，不改变既有行为 |
| `standard` | 敏感文件读写拒绝；危险 shell 模式拒绝；目录不限 |
| `strict` | 在 standard 上增加工作目录边界：写入目录外拒绝，读取降级放行 |

- `ask` 有宿主就弹确认，没有审批人（RPC/print/守护进程）就拒绝；确认前先过路径边界与危险命令检查。
- 允许按工具名在本会话记住（不跨会话、不落盘）；会话覆盖写全局 `settings.security.sessionLevels`，不写项目设置。
- 只读全局设置；`level` 写错或缺失按 `off`。敏感路径解析符号链接真实路径；目录边界同样解析真实路径，`../` 与软链绕不过；Windows/macOS 大小写不敏感，Linux 敏感。空 `denyDirs` 条目丢弃（空目录会被当成 `/`）。
- 危险命令表只收不可逆动作（删除根/家目录、`mkfs`、写块设备、fork bomb、关机、提权、Windows `format`/`rd /s /q`）；日常 `cp`/`mv`/`npm install`/`git commit` 在 standard 仍放行。
- 不覆盖 MCP 与自定义工具（无可判定路径），bash 只按命令文本匹配。
- 入口：设置 → 运行 → 安全门（总开关、默认档、档位编辑、自定义档、会话覆盖）和输入框 shield 按钮；改动即时写盘并推给会话。

## PiDeck 私有名字与互通契约

`pi-deck-*`、`__PI_DECK_*`、`[PI_DECK_*]` 是 PiDeck 内置扩展的私有标识，不是 pi 的协议。把 `customPiPath` 指向本端口时，PiDeck 仍会 `--extension` 注入这些 `.ts`，而本端口只跑 JS，功能由原生实现接管；PiDeck 的渲染层与历史读取器只认这些名字，因此它们是互通契约，集中声明在 `src/PiWeft.CodingAgent/Compatibility/PiDeckProtocol.cs`。

| 名字 | 用途 | PiDeck 读取点 |
|---|---|---|
| `__piDeckBatchAsk` / `__piDeckBatchAsk__` | `ask_question` 批量信封与哨兵 placeholder | `feishu/AskCard.ts`、`pi/extensionUiGate.ts` |
| `pi-deck-todo`（custom 条目） | v3 待办快照：`version/activePlan/previousPlan/nextPlanId/nextTodoId` | `pi/SessionHistoryReader.ts`、`ipc/sessionIpc.ts`、`pi/AgentManager.ts` |
| `pi-deck-todo`（widget key） | 待办 widget，首行 `[[pid:todo-plan:<leafId>:<planId>]]` | `components/session/SessionTodoStrip.tsx`、`agentTodoParser.ts` |
| `pi-deck-todo-brief` | 压缩后补注的不可见消息 | todo 扩展的补注判定 |
| `pi-deck-plan-mode`（custom 条目） | `enabled/todos[].step,text,completed/executing` | plan 扩展 `session_start` 恢复执行态 |
| `pi-deck-plan-todos`（widget key） | 计划草案/进度 widget | `SessionTodoStrip.tsx`、`ComposerComponents.tsx` |
| `[PI_DECK_PLAN_NEXT]` | 计划确认选单标题前缀 | `renderer/src/utils/askUi.ts` |
| `pi-deck-plan-execute` / `pi-deck-plan-complete` | 执行指令与完成消息（可见 custom message） | 无按名过滤；沿用只为让 PiDeck 会话历史保持同一形态 |
| `pi-deck-goal-mode`（custom 条目） | `enabled/phase/objective/roundsStarted/maxGoalRounds/blockReason`；objective 只在变化时写 | goal 扩展 `session_start` |
| `pi-deck-goal`（widget key） | 目标进度 widget：首行 `phase · n/m`，第二行目标，阻塞原因第三行；完成不出现 | `components/session/SessionGoalStrip.tsx`、`parsePiGoalWidget` |
| `__PI_DECK_PLAN_MODE__` / `__PI_DECK_GOAL_MODE__` | composer 注入的模式标记，正文即指令/目标 | `shared/composerModeMarkers.ts` |

- 形状比名字更容易漂移：PiDeck 的待办快照从 v2 变到 v3。名字对、字段错的表现是「静默不显示」，`tests/PiWeft.CodingAgent.Tests/PiDeckProtocolTests.cs` 逐条断言字面量与字段名。
- 纯内部名字不借前缀：目标续轮消息角色是 `piweft-goal-continue`，继续生成的空标记是 `piweft-continue`，都以 `display:false` 的 custom message 落进会话。
- 升级 PiDeck 时检查：`resources/extensions/pi-deck-*.ts` 的条目/widget 形状、`src/renderer` 的 key 表、`src/main` 的 `customType` 过滤。改名或改形状要同步 `PiDeckProtocol` 与形状测试。

## 启动迁移、CLI 与网络

### 启动迁移

| 迁移 | 行为 | 失败 |
|---|---|---|
| 凭据 | `oauth.json` 与 `settings.apiKeys` 并入 `auth.json`；`oauth.json` 改名 `.migrated`，settings 删除 `apiKeys` | 只警告，不删来源、不写 auth |
| 会话落点 | agent 根 `*.jsonl` 按头部 cwd 移入 `sessions/<编码 cwd>/`；重名保留两份 | 单文件警告 |
| 资源目录 | `commands/` → `prompts/`；项目级仅受信时执行 | 警告 |
| 会话格式 | v1 补 id/parentId、`firstKeptEntryIndex`→id；v2 `hookMessage`→`custom`；原子回写 | v3 逐字节不变；`--export` 不落盘 |
| settings 旧键 | `queueMode`→`steeringMode`、`websockets`→`transport`、`retry.maxDelayMs`→`retry.provider.maxRetryDelayMs` | 无法识别按未设置 |
| 扩展路径 | 用户级相对 `agentDir`，项目级相对 `<cwd>/.pi`，`-e` 相对 cwd | 原样保留，加载期报不存在 |

`AgentMigrations.Run` 由 CLI/桌面启动时调用，幂等。

### 命令行与网络

- `--offline` / `PI_OFFLINE=1` 跳过模型目录刷新、包更新和启动目录拉取；显式登录、安装与对话照常。
- `pi update` 在 NativeAOT 构建里提示用发行页更新本体；`pi update --extensions` 更新包并报告 npm 版本或 git commit 变化；`--all` 同时提示本体与更新包。
- 未知长选项按 `--flag=value` / `--flag value` / 布尔 `--flag` 收集，交给扩展；无人注册报错退出 2。短选项立即报错。
- `-e/--extension`、`-ne/--no-extensions`、`--theme`、`--skill`、`--prompt-template` 已接线；`--use-theme`、`--tui-mode`、`--autocomplete-max-visible` 只提示一次。
- `settings.httpProxy` 只读全局并写入 `HTTP_PROXY`/`HTTPS_PROXY`；`PI_SHARE_VIEWER_URL` 覆盖 `/share` 查看器。
- 显式超时只覆盖等响应头；`httpIdleTimeoutMs` 兜底响应体/错误体，按读取续期，一个流一个定时器。错误体另限 1 MiB。用户中止 → `aborted`，空闲超时 → `error` 且可重试；`SseParser` 的取消必须转异常，避免把半截流当正常结束。
- `settings.transport` 目前只影响 Codex responses；写错收敛到 `auto`。OpenCode 会话头覆盖压缩、摘要、缓存预热、探测与 Git 摘要。
- `/share` 只导出当前分支并在链尾追加 `pi.share`（系统提示 + 工具 schema）；优先 `gh gist create`，回退 `GITHUB_TOKEN`/`GH_TOKEN`；Radius 上传 ndjson。
- `edit`/`write` 按规范化路径条带锁串行化；原子写入与会话头部补写跟随符号链接写目标，不替换链接本身。

## 虚拟模型 route

```json
{
  "providers": {
    "router": {
      "models": [{
        "id": "auto", "name": "Auto",
        "virtual": {
          "thinkingLevels": ["off", "high"],
          "route": "const t = ctx.modelRegistry.find('p', request.reason === 'retry' ? 'b' : 'a'); return { model: t, thinkingLevel: 'low', state: { reason: request.reason } };",
          "timeoutMs": 5000
        }
      }]
    }
  }
}
```

- `request`：`model`、`thinkingLevel`、`reason`（`user`/`continuation`/`retry`/`direct`）、`previous`、`failed`、`state`、`messages`。
- `ctx.modelRegistry`：`find`、`findOfType`、`getModelsOfType`、`getAvailableOfType`、`models`；只能路由到物理模型。
- 返回 `{ model: { provider, id }, thinkingLevel?, state? }`；`route` 可为源码字符串或 `{ "code": "...", "timeoutMs": 5000 }`。
- `state` 写成 `pi.virtual-model-state` 条目，按分支恢复；`direct` 请求不读写状态。
- 路由失败、目标非物理模型或超时 → 本次请求报错，不落回 `pi-virtual`。

## 剩余差距

| 编号 | 能力 | 状态 |
|---|---|---|
| PI-01 | JS/TS 扩展 | JS 可用（发现、`-e npm:`/`git:`、专属线程、工具/命令、会话/回合/消息/工具执行/模型等 16 个事件、flag、reload）；ESM 与异步处理器、流式 update 与 provider/context 事件、TypeScript 不支持 |
| PI-02 | 扩展 UI | 对话框与 setStatus/setWidget/setTitle/set_editor_text 已实现；扩展工具支持 `promptSnippet`/`promptGuidelines`/`outputSchema`/`exposure`/`namespace`/`executionMode`；无自定义 renderer、无 `ctx.executeTool` 扩展入口 |
| PI-03 | 包管理 | 安装/列表/移除/更新已实现，npm/git 更新报告版本或 commit 变化；无自更新执行、依赖冲突治理与资源筛选界面 |
| PI-04 | 完整 TUI | 行式 REPL；缺多行编辑、`$EDITOR`、TUI 图片粘贴、补全、选择器、终端图片与完整键位 |
| PI-05 | codemode / tool_search | 已实现（含 worker 排队、`PI_CODEMODE_MAX_WORKERS`、检索 `$ref`、prelude lockdown）；余项只有顶层 `arguments` 等轻微差异 |
| PI-06 | MCP 高级运行时 | OAuth/PKCE/客户端凭据、`callbackUrl`、`clientRegistration` 的 `auto`/`dcr`/`cimd`、与 pi 共享 `mcp-auth.json`、401 重放、HTTP 通知、瞬时故障重试、`mcp.log`、后台并行连接与首轮 direct 等待已覆盖；缺跨进程刷新锁 |
| PI-07 | 认证 | OAuth + API Key 全覆盖；桌面与 `pi login` 共用供应商目录，支持 Cloudflare/Vertex/Bedrock 多字段与凭据 env；缺 AWS 配置文件/实例元数据链、OIDC/SSO、多账号 |
| PI-08 | 动态模型目录 | 远程刷新、离线缓存、账号目录、条件请求、6 小时节流已实现；缺账号能力同步与增量合并 |
| PI-09 | 高级模型运行时 | 虚拟模型、cache warming、samplingParams、Codex WebSocket、生图/分类已实现；缺扩展注册 route、连接复用、生图入口 |
| PI-10 | 资源/主题 | 发现、解析、热重载已实现；缺资源编辑界面与包筛选可视化 |
| PI-11 | 项目信任 | 已实现 |
| PI-12 | 行内 shell 与环境变量 | 已实现 |
| PI-13 | powershell 工具 | 已实现：全平台都在工具目录里、默认不启用，非 Windows 执行时返回 pi 原文 `The powershell tool is only available on Windows.` |
| PI-14 | CLI 键位 | 未实现 `keybindings.json` |
| PI-15 | 斜杠命令 | `/copy`、`/import`、`/share`、`/bug`、`/llama`、`/settings` 已实现 |
| PI-16 | 子工具账本 | `nestedCalls` / `parentToolCallId` 尚未产出 |
| PI-17 | 分类器方言 | `openai-decisions`、`typesafe-system-one`、`cloudflare-workers-ai-system-one` 已实现；缺其它供应商的私有分类协议 |
| PI-18 | 网络细项 | Codex `websocket-cached` 复用、zstd/continuation；AWS ambient 链 |
| D-01 | 问询/待办/计划 | 已实现，含计划步骤跟踪（`Plan:` 解析、`pi-deck-plan-todos` 进度、`[DONE:n]`、执行确认选单）与目标模式（自动续轮、`GOAL_COMPLETE`/`GOAL_BLOCKED`、轮次上限、桌面目标条）；缺独立后台 Ask 工作流 |
| D-02 | 文件工作台 | 导航、编辑、汇总、diff 已实现；缺多文件标签、草稿本、跨行块注释、内嵌浏览器 |
| D-03 | 内置终端 | MiniPty 与主要 VT 行为；缺 Windows/macOS 实机回归、完整 VT100、多标签、快照回放 |
| D-04 | Git 工作流 | 暂存/提交/分支/图/比较/远端/stash 已实现；缺冲突界面、rebase/cherry-pick/reset、多远端 |
| D-05 | 检查点 | 快照/概览/恢复已实现；缺与回合关联、恢复后清理未跟踪文件、分页、按范围回退 |
| D-06 | 配置深度 | 连接测试、远程目录、价格、订阅 OAuth 已实现；缺供应商迁移、收藏/排序、专用目录、多账号 |
| D-07 | 用量 | `usage-probes.json`、三种形态、多窗口、自动查询、失败明细已实现；缺火山 AK/SK、Sub2API、热力图下钻 |
| D-08 | 技能/插件生态 | 资源发现与资源页；缺商店、中文提示词库、包安装可视化、独立宿主插件 |
| D-09 | 多后端/导入 | Claude Code / Codex CLI 导入；缺 DSH/ACP 与 SQLite 格式 |
| D-10 | 多模态 | 图片输入/预览；缺视觉桥、生图入口、语音/视频 |
| D-11 | 远程/自动化 | 缺局域网 Web、实时推送、隧道、飞书、定时任务 |
| D-12 | 桌面集成 | 缺托盘、系统通知、自动更新、快捷浮窗、桌面宠物；诊断包与环境体检未对齐 |
| D-13 | 交互/长历史 | 缺项目级置顶/排序、拖拽/分屏、完整消息编辑删除、Mermaid/KaTeX；历史分页经实测不做 |
| D-14 | 工具审批 | 安全门已实现；缺 PiDeck 的独立会话存储位置与 CLI 档位切换 |
| D-15 | 关联会话 | 子会话工具、提议卡片（offer / own_initiative）、回合回报、自动续跑额度 6、子会话条、后台标签已实现；缺关系与提议落盘 |

## 验证

```sh
xvfb-run -a dotnet test PiWeft.slnx --no-restore -v minimal
dotnet publish src/PiWeft.Desktop/PiWeft.Desktop.csproj -c Release -r linux-x64 --no-restore -v minimal
xvfb-run -a -s '-screen 0 1440x900x24' bash tools/desktop-parity-smoke.sh
xvfb-run -a -s '-screen 0 1280x820x24' bash tools/desktop-ask-smoke.sh
xvfb-run -a -s '-screen 0 1280x820x24' bash tools/desktop-waiting-smoke.sh
xvfb-run -a -s '-screen 0 1440x900x24' bash tools/desktop-checkpoints-smoke.sh
```

截图脚本使用独立 fixture agent 目录，不读取真实密钥或会话。修改进程环境变量的测试必须加入 `process-environment` 串行集合。
