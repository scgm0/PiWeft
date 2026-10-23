# 性能与基准

本文合并会话统计口径、热路径优化、桌面渲染与增量投影的实测结果。功能对齐见 [pi / PiDeck 兼容](01-parity.md)。

## 会话统计

输入区下方为紧凑统计行：当前分支轮次、首 token 延迟、最近一次模型回复耗时、生成速度、最新缓存命中率、累计输入/输出。点击上下文百分比或查看图标打开窗口级统计面板：

- 当前上下文 token / 模型窗口，包含超过窗口上限的真实百分比。
- 累计输入、输出、缓存读取、缓存写入、缓存总量和费用。
- 最新命中率、会话平均命中率、累计 token 加权命中率与有效消息样本数。
- 当前分支轮次、全文件用户/助手消息数、工具执行与失败次数。
- 最近一次模型回复的首 token 延迟、总耗时、生成速度与端到端速度。
- 模型回复累计耗时、工具累计执行耗时和平均首 token 延迟。
- 手动压缩上下文；运行时禁用，压缩期间可从输入区中断，关闭标签也会取消。
- 供应商余额区位于会话统计弹窗。保留刷新、查询时间、配置入口与错误状态；成功结果短时缓存，切换供应商或项目清空旧值，隐藏弹窗取消请求并拒绝迟到结果。

设置页使用同一份统计。明细控件原位更新，深浅主题同步，窄窗口内滚动；切换标签或关闭会话时移除浮层。面板遮罩覆盖整个窗口，不以右侧会话区为定位边界。

## 统计口径

- 缓存命中率为 `cacheRead / (input + cacheRead + cacheWrite)`。
- 平均命中率是有效助手消息命中率的算术平均；加权命中率是累计缓存读取除以累计 prompt token。样本是实际助手消息，不是 UI 刷新次数；无有效 prompt token 时不添加样本。
- 累计 token 与费用沿用 `UsageTotals` / `UsageBreakdown` 口径，包含全文件助手消息和 `usage` 条目。压缩或导航不会抹去历史费用，不重复叠加工具自身用量或压缩条目已记录过的用量。
- 当前分支轮次按原始用户消息计数，压缩摘要和分支摘要不增加轮次。
- 首 token 延迟取正文首个非空 delta；无正文时回退到思考首 delta。
- 生成速度是输出 token 除以首个非空正文/思考 delta 到回复结束的时间；端到端速度除以整次模型请求耗时。不把工具执行或重试退避时间计入单次模型回复。
- 新回复新增可选 JSON 字段 `firstTokenMs` 和 `streamingDurationMs`，与 `durationMs` 一起持久化。使用单调时钟计时；旧会话缺失指标显示 `-`，不从整次耗时推测首 token 或思考耗时。
- 人民币金额按 PiDeck 的固定参考汇率 `1 USD = 7.2 CNY` 估算，不是实时汇率或供应商余额。
- 工具累计耗时为各次执行时间之和，并行执行会重叠，不代表会话墙钟时间。

## 会话与投影热路径

- `SessionManager.Revision` 在追加、导航、上下文编辑、压缩、派生、新建、重载时使派生缓存失效。
- 条目快照和上下文消息投影在未变更时复用；只读容器避免消费者修改缓存列表。
- 统计只消费追加的新条目；重置、派生或重载后重新计算。命中率与首 token 均值用累计值与计数器，不保存不断增长的样本数组。
- 上下文估算按投影引用缓存，CLI 与桌面端共用；用 `Lock` 同步快照及版本，防止后台追加与 UI 读取交错导致过期缓存。
- 聊天快照共享不可变历史前缀，流式帧只分配小尾部；重复读取同一帧复用快照。分支/压缩后重新投影。
- 继续利用 MewUI 的 40 ms `DispatcherTimer` 合并更新、历史控件复用与流式结束后才解析 Markdown 的现有设计。未变更的统计行不重新格式化或替换控件。

### 查询基准

```bash
dotnet run --project tools/PiWeft.SessionBench/PiWeft.SessionBench.csproj
```

同一机器、.NET SDK 10.0.401 / Runtime 10.0.12，Debug 构建，5000 对用户/助手消息（10000 条目），预热后执行 1000 次 `UsageTotals + UsageBreakdown + ContextTokens`：

| 实现 | 耗时 | 当前线程分配量 | 校验和 |
|---|---:|---:|---:|
| 修改前程序集 | 6677.54 ms | 1,770,384,000 bytes | 5657030200 |
| 修改后 | 0.46 ms | 0 bytes | 5657030200 |

这是未变更会话的查询热路径结果，不代表启动、网络推理、完整 UI 渲染或整进程 RSS 提升相同比例。首次加载和变更后的投影仍需要遍历会话；不宣称长会话已实现全量消息虚拟化。

## 桌面渲染

主题原位换色、可变高度消息虚拟化、延迟构建分支树，以及轻量侧栏、消息卡片、空会话页与输入区均已实现。实现见 `ConversationTimeline.cs`、`SessionTab.cs`、`AppStyles.cs` 和 `MainWindow.cs`。

### 主题切换

消息卡片和工具背景由主题样式解析，思考标题使用主题回调。切换深浅主题保留 Markdown 实例、消息选区、工具展开内容、输入草稿与光标，不再重建全部消息或解析整段 Markdown；分支树和 Git 不因换肤而重读数据。框架文本角色与图标仍刷新颜色，但消息子树只包含视口附近的控件。一次 Debug 原生窗口演练测得切换调用约 **42.7 ms**；这是局部观测，不是所有硬件或标签数量的保证。

### 会话打开与内存

- 消息使用 MewUI 0.22.1 的可变高度 `ItemsControl`，只为可见范围与少量预加载行创建正文、Markdown、图片和工具控件。
- 滚出视口后释放消息内容，回收池只保留小型容器；再次浏览时恢复轻量的工具/思考展开状态。
- 模板的 unbind 发生在同一控件重新绑定前，因此在布局结束后清理真正已脱离视口的内容，保留流式更新期间的选区与控件。
- 历史变更按尾部替换、追加、删除通知，保留既有消息与高度缓存；流式尾部只在可见宿主上原位更新，不发送 Replace 通知，因此虚拟化高度缓存不会回落成 120 DIP 估算值，滚动条与视口不会被短暂压短后停在真实底部之前。流式消息落盘时仍原位替换正文为 Markdown。
- Replace 通知按“真正需要重建的连续行段”拆分，而不是从第一处差异一直覆盖到末尾；一轮结束、单行元数据变化不会再让其后的已测高度全部作废。执行过程里的工具/思考行仍然原位更新，不进入 Replace 段；组内虚拟化步骤列表的流式思考/正文也原位更新（包括流式行落盘后 EntryId 变化、但时间戳不变的收尾），只重测该行，组体 extent 与滚动进度不会先缩后涨。
- 分支面板首次打开时才构建完整树。会话文件已打开时直接选中标签，不再先重复读取 JSONL；启动目录复用已有环境。
- 打开会话走后台线程，UI 线程只建标签页。45 MiB / 26,985 行（26,984 条目）的真实会话实测（x64 Debug / Xvfb）：

| 阶段 | 是否 UI 线程 | 耗时 |
|---|---|---:|
| `SessionManager.Open` 逐行解析 | 后台 | 0.84–0.88 s（冷启动 1.39–1.94 s） |
| 建会话 + `ChatViewModel.Attach` 聊天记录投影 | 后台 | 0.27–0.36 s |
| `ExecutionProcess.Project`（1741 行） | 后台（随投影） | 0.03–0.07 s |
| `SessionTab` 构造 + 首次布局 | UI | 0.35–0.73 s（冷启动）/ 约 0.04 s（热） |

  解析同时改成 `Utf8LineReader` + `SessionJson.ParseLine(ReadOnlySpan<byte>)`：45 MiB 不再把每一行先解码成 `string`（整份文件规模的分配），快约 10–15%，更重要的是能按字节位置汇报进度。`SessionManager.Open` 因此新增可选的 `IProgress<SessionReadProgress>` 与 `CancellationToken`（每 256 行或 4 MiB 汇报一次，逐行检查取消）；旧的同步重载保持不变，CLI 与守护进程调用点零改动。
  UI 侧的加载提示**就是准备阶段会话面板自身的内容**（`SessionLoadingPanel`：欢迎面板 StackPanel 里的一段，加载中与「开始一次会话」文案互斥切换），不是窗口级遮罩也不是浮层卡片，更没有中止按钮。
  **加载中旧会话必须整体让位**（历史缺陷：在主区已有一个会话时打开另一个，加载段落直接浮在旧会话正文之上——`UpdateStatus` 只把准备阶段面板设为可见，却没把 `_tabControl`、右侧面板与欢迎页状态行收起，两层内容同时在场）。现在 `SessionLoadingPanel.Showing` 期间：`_tabControl`、`_panelHost`、`_status` 全部收起，主区只剩加载提示；`SessionLoadingPanelTests` 之外另有 `SessionLoadingSmokeTests` 常驻演练（`--ui-smoke loading`）断言 `tabVisible=False welcomeVisible=True prompt=True panels=False` 与收尾的 `tabs=2 welcomeVisible=False prompt=False tabVisible=True`。：解析阶段显示文件名 + 百分比 + 已读行数（`Strings.OpeningSessionProgress`），随后「正在准备会话…」；文件长度未知时退回不定进度，只汇报行数。段落按在途操作计数收放，打开与改名叠在一起也不会提前收起；加载中准备阶段面板强制可见（`UpdateStatus`），所以从已有标签页再开第二个会话时提示也落在同一处。**加载失败就地显示在同一段落里**：红色标题「打开会话失败」+ 原始异常信息 + 「关闭」，不再挤到顶部横幅，错误态一直留到用户关闭或开始下一次操作（错误态会收起进度轨道，免得看着像分割线）。同一份文件在解析期间重复点击只跑一份；窗口关闭（`_sessionAbort` + `_sessionHostClosed`，不再往已关的窗口塞标签页）或失败时会释放已经建好的会话与投影（会话持有 JSONL 写入句柄）。改名的 `session_info` 追加同样需要读完整份会话，因此也移到后台，只在文件大于 2 MiB 时才占用这段提示，避免小会话闪一下。
  因为启动时自动打开的会话不再保证「立刻就有标签页」，`--theme-smoke`、`--ui-smoke`、`--smoke-tab` 改为显式等待在途打开结束（`WaitForSessionOpensAsync`），而不是靠固定延时猜；加载提示会阻止关窗，退出前也先排空在途打开。
- 窄屏输入框由 96 DIP 缩为 56 DIP，保留草稿和选区。追随最新消息使用滚动偏移，避免将高于视口的整条回复反复滚入视图；滚动前先从滚动内容同步最新 extent，并把正在进行的动画目标改指向末尾，避免虚拟化高度变化时停在旧底部。
- 跟随意图与布局解耦（对齐 PiDeck 的状态机）：滚轮/触控板/键盘/滚动条才是读者输入，行高变化引起的锚点校正不改变跟随态；近底带内的上滚先累计读者自己的位移（25 DIP 逃逸），1–2 px 的触控板抖动不会退出跟随。Arrow/PageUp/PageDown/Home/End 在焦点控件未处理时滚动会话，嵌套视口到边界后再交还祖先。
- 平滑滚动输入按帧合并：同一帧的 wheel/trackpad 增量先累加，再一次性写入偏移，避免高回报率设备一帧触发多次重排。动画中途再次输入时用 Hermite 曲线从当前速度续接，不再每次从零速重开 ease；键盘自动重复（`KeyEventArgs.IsRepeat`）直接落位，长按方向键不再被压成单次曲线的第一小段。滚轮落在滚动条上仍由视口按 100 DIP 平滑滚动，不再走 `ScrollBar` 的 24 DIP 立即跳变；终端滚轮把分数刻度累积成整行（一格 3 行），触控板不会每个事件都跳 3 行。跟底滑行不再使用固定 260ms 上限，而是按剩余距离提高指数衰减率：近距离保持柔和尾巴，几万 DIP 的「回到最新」在几百毫秒内收敛到阈值，避免最后一帧突然跳几十 DIP。以上仍是应用层优化，滚动帧依旧会触发 MewUI 的 arrange；真正的 placement-only 滚动需要框架改动。
- 执行过程组体的跟底由**几何**决定（`SmoothScroll.AutoFollowAtEnd`）：滚动条停在底部（4 DIP 容差）就跟随最新步骤，只有读者明确上滚（滚轮/键盘向上、拖动滚动条、恢复历史位置）才离开，回到末尾自动重新锁底。布局测高、栈→虚拟化 presenter 切换、展开/折叠步骤引起的 extent 与偏移变化都不再永久关闭跟底——此前这些路径会静默清掉跟随状态，表现为「置底也不跟」。展开步骤时用 `StopFollowingGrowth` 只清本次跟随，并把预览行在同一帧的 `SyncBodyMetrics` 里钉回原位，不留到下一次无关更新。钉锚同时记录**步骤索引**而不只是行宿主：虚拟化器会在流式帧里 Replace 该行并换掉宿主，只认引用会让锚点提前失效（表现为偶发的「展开步骤行时组体进度没固定在预览行」）；渲染阶段的收尾最多补两次，且一旦发现偏移不是自己写的（读者滚轮/键盘/滚动条已接管）立刻放弃钉锚，绝不和读者抢滚动。工具步骤的开合一律钉住预览行——即使滚动条正好停在末尾（几何上属于跟随态），展开的正文也不该把阅读位置顶走；流式思考/正文的开合仍交给跟底通道。流式行重测期间保留布局前的跟底意图，避免被虚拟化 anchor 校正误判成读者上滚，只有滚轮/键盘/滚动条才取消。展开流式思考仅在读者本就位于末尾时才继续贴底。
- 会话内搜索进入时取消跟底并定位第一条命中，清空后按“搜索前的行 + 行内深度”恢复阅读位置（长回复中间也不会跳回行首）；过程组里的工具名与输出也参与匹配。
- 正文与展开的思考改用 `PiWeft.Markdown`：纯追加时只重新解析最后一个顶层块（链接引用定义回退全量），再按**块模型哈希**做前缀/后缀 diff，未变块复用元素与文本布局；段落按已闭合行分块，只重排开放尾块；代码块用 `ReplaceRange` 追加。定稿只切换 `IsStreaming`，不再做纯文本→Markdown 的替换，也没有“结束那一下重建整棵树”的卡顿。架构见 [PiWeft.Markdown](04-markdown.md)。
- 工具输出、过程步骤和终端等纯文本仍用 `StreamingTextView`（`MultiLineTextBox` + 自有 `EditableTextDocument`）：纯追加走 `ReplaceRange` 的区间增量编辑，只重排受影响的行；撤销历史对只读展示无意义，`UndoSizeLimit = 0` 让每帧不产生编辑记录。前缀被缩短或改写（重发、改写历史）才退回整篇替换。实测（x64 Debug / Xvfb，520 DIP 宽，含 measure + arrange，逐帧追加一段）：

| 文本长度 | `Text=` 全量替换 | `StreamingTextView` 增量追加 |
|---:|---:|---:|
| 16 K | 72.3 ms（中位 51.0） | 0.87 ms（中位 0.90） |
| 32 K | 42.5 ms（中位 42.3） | 0.78 ms（中位 0.51） |
| 64 K | 77.2 ms（中位 74.1） | 0.97 ms（中位 0.60） |

  参照：`TextBlock.Text` 全量替换 8 K/16 K/32 K/64 K 分别约 22/23/37/54 ms；固定高度 `MultiLineTextBox`（行虚拟化）约 1.3–2.7 ms。差异几乎全部来自是否重建整篇文档，而不是控件类型，因此 32 K 以上也能逐帧更新，不需要再按长度降频。

### PiWeft.Markdown 增量渲染

`src/PiWeft.Markdown` 的测试基准（x64 Debug / Xvfb / MewVG X11，520 DIP 宽，见 `tests/PiWeft.Desktop.Tests/MarkdownViewTests.cs`）：

| 场景 | 中位 | p90 | 每帧分配 |
|---|---:|---:|---:|
| 32 K、180 个块，尾部新增一段（只解析尾块） | 约 0.7–2 ms | 约 1–4 ms | ~0.1 MiB |
| 24 K 单段落逐段追加（开放尾块分块） | 约 4–7 ms | 约 8–15 ms | ~0.5 MiB |
| 24 K 单段落定稿（只切 `IsStreaming`） | 0.1 ms | — | — |
| 200 项列表尾部追加（行高缓存 + 尾项原位更新） | 约 6–12 ms | 约 10–25 ms | — |
| 32 K 单段落一次性全量布局（改造前基线） | ~79 ms | ~119 ms | ~4.4 MiB |

32 K 单段落一次性全量布局仍是历史消息首次进入时的成本；流式路径从第一帧起就按开放尾块增长，不会走到它。分块布局与整段布局的行数、行高、baseline 和总高度在测试中逐行比对一致；块回收只保留外层视口附近的元素，总高度保持精确，因此外层可变高度虚拟化的行高缓存不会因回收而失效。

流式渲染不再替换段落/列表/表格元素：尾块原位更新，新增列表项/表格行/引用段落直接追加到现有容器；视口内及上下各 2 个块保持已附着，视口外再保留 ±12 个已测块，避免边界抖动反复 attach/detach。这些是消除“增量更新时文本块一直闪烁”的关键。
- 虚拟化行的高度只在 arrange 阶段重测。MewUI 0.22.1 里 `InvalidateMeasure` 命中「祖先已 dirty」分支时不会同时置 arrange dirty，行高变化会被跳过（表现为展开最后一步/展开思考后组体滚不到新内容；该缺陷在 0.22.1 与当前 main 上都能复现，已在框架仓库补了回归测试与修复）。ExecutionProcessView 与时间线在行内更新后显式 `InvalidateArrange`（含视口内容），布局落定后重新发布 extent，并把正在跟随增长的组体重新贴到底部；升级到含修复的 MewUI 后这些显式失效依然安全。会话搜索弹窗的行是带 hover 提示的按钮，不做虚拟化，因此结果按 200 行封顶并在状态行说明截断（`SessionSearchTruncated`），避免一次检索构造上千个控件。

同一 1000 轮、2000 条消息 fixture 的局部冷启动观测（包含 `SessionTab` 构造及 960×720 控件布局，不包括 JSONL 解析、模型环境初始化或网络请求）：

| 指标 | 优化前 | 优化后 |
|---|---:|---:|
| 同时构造的 Markdown 视图 | 1000 | 3 |
| 当前线程累计托管分配 | 253,602,008 bytes | 5,347,040 bytes |
| 构造与布局耗时 | 4445.66 ms | 767.17 ms |

累计分配不是存活堆大小，也不是整进程 RSS。会话核心仍保留 JSONL 历史及模型上下文，尚未实现历史文件分页；优化限制的是昂贵的 UI 控件、Markdown 与图片布局数量。

Linux NativeAOT 发布版、Xvfb/Mesa、1280×820 的实际会话演练中：60 条工具输出 fixture 的 RSS 约 208.4 MiB，1000 轮 / 2001 条消息 fixture 的 RSS 约 211.2 MiB。二者内容不同，这组数字只记录优化后的实测占用，不构成优化前后的 RSS 比较。

## 增量投影与会话缓存

长会话的瓶颈主要不在 Markdown 控件，而在每次消息结束时重新映射整段历史：

- `SessionManager.GetBranch()` 用不可变列表缓存当前分支快照；普通追加只做结构共享的 `Add`，不再每次重建 O(n) 路径。
- `BuildProjectedMessages()` 在分支只追加消息时复用上一次投影，只把新增条目接到尾部；上下文编辑、压缩、分支摘要和自定义消息仍走完整重建。`CodingAgentSession.Project()` 按 revision 缓存带系统提示词的投影。
- `ChatViewModel.Rebuild()` 只重算最后一个 user 轮次；新增 user 轮次时直接保留旧行，上下文编辑/分支切换才全量重建。`_entryIds` 也按分支尾部增量维护。
- `ExecutionProcess.Project()` 的流式路径改用 `ExecutionProcessProjector`：历史轮次的折叠结果保持不变，只重投影当前轮。`Summary()`/`GroupSummary()` 由多次 LINQ 扫描改为单遍计数。
- Provider 的 `OnProviderStreamEvent` 回调改为只在有订阅者时才解析 `JsonNode`，避免为每个 SSE delta 白做一次 DOM 解析。
- 会话 JSONL 追加改用 .NET 10 的 `File.AppendAllBytes(ReadOnlySpan<byte>)` 和可复用的 `ArrayBufferWriter<byte>`，不再为每条消息生成 UTF-16 字符串再编码回 UTF-8。
- 会话目录扫描改用 `Utf8LineReader` + `Utf8JsonReader`：直接按 UTF-8 行读文件，只取 type/cwd/name/role 与首条用户预览，跳过工具输出正文。
- Provider SSE 每个流复用一个 UTF-8 缓冲；不再为每个事件 `span.ToArray()`。微基准约 192 B/事件降到 72 B/事件，解析耗时约快 15–20%。
- 终端重绘复用 `Run` 对象，避免每个变化行、每个颜色段都分配新的 inline run；`MessageRows.Update` 利用投影器给出的稳定前缀从当前轮开始比较。
- `CodingAgentSession.Project()` 用只读拼接视图代替每次复制完整消息列表。

本机 Release 局部观测（单次，不是所有硬件的保证）：

| 场景 | 优化前 | 优化后 |
|---|---:|---:|
| 10,000 条条目追加 2 条后投影 | 4.13 ms / 1.26 MB | 0.016 ms / 9 KB |
| 1,000 轮会话追加一条 user 后 `ChatViewModel` 重建 | 2.53 ms / 1.25 MB | 0.25–0.6 ms / 36 KB |
| 2,000 行流式 `ExecutionProcess.Project` | 1.15 ms / 233 KB | 0.07–0.12 ms（含测试字符串分配） |
| 40 个会话 / 2,000 行目录冷扫描 | 约 39–67 ms / 34.4 MB | 约 19–24 ms / 0.26 MB |
| 单个 SSE 事件解析 | 192 B/事件 | 72 B/事件 |

`.NET 10` NativeAOT 发布对照：默认 `piweft-desktop` 62,399,168 bytes；`OptimizationPreference=Speed` 为 64,028,176 bytes（+2.6%），适合确认 AOT 代码生成为瓶颈后再开启。`PredefinedCulturesOnly=true` 在当前依赖下没有减小体积，仍保留中文/本地化能力。

**下一批候选**：`SessionManager.ReadFileEntries` 仍是 `File.ReadLines` + `JsonNode` 全量解析；目录扫描已经用 `Utf8JsonReader`，但打开超大历史文件仍会创建完整消息对象。Provider 侧每个 SSE 事件仍构造一次 `JsonDocument`（字节数组分配已消除）；终端在高频全屏重绘时仍会为新增行创建 `Run`（只在缓存未命中时）。

## Tooltip 与执行过程

- Tooltip 保留 350 ms 延迟，通过 `Popup.ShowAt` 展示，内容测量、原生表面与边界翻转交给 MewUI。锚点直接使用窗口绝对 `Bounds`，不累加父级坐标，不把提示内容放入 `OverlayLayer`。
- 每轮一个执行过程汇总，历史默认折叠；思考和连续工具调用组成次级折叠组，中间回复按原顺序穿插，末尾正文与图片保持常显。流式轮自动展开最新过程组，尊重手动开合状态。
- 工具调用与结果通过调用 ID 合并，只计数一次，同时保留输入参数与输出。工具正文与 diff 仍首次展开时才创建。
- 过程组内 24 条以内步骤使用内容驱动的栈：展开/收起步骤会同步更新组体高度（最高 300 DIP），滚轮落在组体空白与竖线区域也先归组体；超过 24 条切换为虚拟化列表并固定限高，组体不再随单行展开跳高。流式最新组自动展开时跟住末尾，手动展开从顶部开始；工具步骤展开用预览行锚点停住，流式思考展开则让组体与会话跟随内容增长。虚拟化回收后仍恢复两级折叠和工具明细状态；释放资源时也处理已经折叠、没有出现在可见树里的内容。
- `NativeHoverTests` 在独立 Xvfb 中驱动实际鼠标，检查延迟、顶部和底部提示、移出与滚动关闭，以及执行过程开合。
- 会话统计与余额配置使用原生 `Border`、`DockPanel`、`ScrollViewer` 分配剩余高度，不在 `MeasureContent` 中修改 `MaxHeight` 等布局属性。
- 余额请求在启动时捕获 UI Dispatcher，返回结果经该 Dispatcher 更新控件；表单和统计行重建时显式释放旧控件。
- `PiWeft.Markdown` 的块回收以「外层视口 + 自身 Bounds」算可见范围；行被虚拟化器摘下再挂回时强制重测量，避免沿用回收前的缓存测量而留下空白正文（要点一次滚动才恢复的成因）。
- 会话阅读区统一抑制文本、折叠栏及按钮获得焦点时触发的滚动；焦点守卫会记录并恢复**所有祖先** `ScrollViewer`（执行过程组体、代码横向视口与会话本身）。手动开合执行过程、过程组时暂停底部追随，在布局收敛后恢复被点击标题的屏幕锚点；组内步骤折叠只暂停跟随、不建立外层锚点，避免改变外部会话进度；流式思考展开则主动跟到末尾。只读正文/代码禁用输入法组合；可编辑引用输入框在组合期间临时撤掉 chip 投影并在提交后恢复，避免组合装饰范围按源文偏移越界。
- 差异正文使用一份只读 `MultiLineTextBox` 与 `ITextClassifier`，可跨增删行拖选、复制；行号置于不可命中的独立文本列。普通滚轮由会话处理，Shift+滚轮浏览长行。
- 图片预览按图像纵横比居中布局，图片之外的留白命中遮罩并关闭；图片内部点击不关闭。
- Markdown 文件链接按会话目录解析相对路径，支持绝对路径、`file://`、`~/`、URL 编码以及 `:行:列` / `#L行C列`。文件交给编辑器、目录交给默认程序，错误在状态栏显示；拒绝非 HTTP/HTTPS/file 协议和远程文件共享。

## 用诊断数据做的一轮优化（45 MiB / 26,985 行真实会话）

打开同一个会话，优化前后各测三次（x64 Debug / Xvfb，冷启进程）：

| 指标 | 优化前 | 优化后 |
|---|---:|---:|
| 进程启动 → 会话就绪 | 3.1–3.9 s | 2.56–2.67 s |
| 这一轮里 UI 线程被占住 | 环境构建 519 ms + 侧栏扫描 190–582 ms + 标签页构造 422 ms + 状态条快照 193 ms | 标签页构造 240–251 ms + 侧栏建列表 15–18 ms |
| 侧栏刷新（流式期每条消息一次） | 重扫整份 JSONL：190–582 ms | 续扫新增字节：0.3 ms |
| 上下文 token 估算 | 每次投影变化整段重扫 ~150 ms（UI 线程） | 按消息引用复用前缀，追加一条 < 1 ms |
| 会话 JSONL 解析（后台） | 单段 1511 ms | 3 段并行 605–690 ms |
| RSS / 托管堆峰值 | 490 MiB / 200 MiB | 487 MiB / 200 MiB（未动数据结构，持平） |

改了什么：

- **启动不再串行读两遍。** `MainWindow` 构造的后半段拆成 `BootstrapAsync`：环境构建、模型清单、侧栏摘要扫描全部在后台跑；同时用 `SessionCatalog.MostRecentPath()`（只 stat 目录，不读内容）先一步把「启动会自动打开的那份」交给后台解析，与侧栏扫描并行，解析结果直接给打开流程接手（`session.open.read` 记 `prefetched=true`），不再重读几十 MB。无置顶会话时才预解析，置顶时不猜错。
- **侧栏摘要改成续扫，不再整份重读。** 会话文件是只追加的，缓存里除了长度/时间戳还记住上次扫到的行边界与首行指纹（FNV-1a）。文件变长时只喂新增字节；首行指纹不匹配（同名文件被整份替换）才回退整扫。以前流式期每次 `RefreshSessions` 都会在 UI 线程重扫整份几十 MB。
- **目录行不建 JSON 树。** `FastScanLine` 只读行首窗口里的 `type`/`role` 两个键就判定这行对目录有没有影响，兼容 pi 的 `{"id":…,"type":"message"…}` 与外部导出的 `{"type": "message", …}`（冒号带空格）；正文一律不碰。头部、`session_info`、需要预览的首条用户消息、断行、超长前缀、键序异常都退回完整解析，`SessionCatalogFastScanTests` 逐形状断言快速路径与完整解析**逐字段一致**（含正文里写着 `"type":"message"` 的坑）。
- **`Utf8LineReader` 零拷贝快路径。** 整行本来就在读缓冲里时直接返回该缓冲的切片，不再抄进 `_lineBuffer`——那等于给几十 MB 多走一遍内存。返回跨度的有效期仍是「到下一次调用为止」，两个消费者都当场用完。读+分行 47 MB 从 ~400 ms 降到 11.5 ms（暖缓存，隔离基准）。
- **会话 JSONL 分段并行解析。** `SessionManager.ReadFileEntries` 按字节切成 2–3 段（`ChunkCount` 依核数与文件大小封顶，小文件与 ≤2 核不切），每段从下一个整行开始、读到下一段边界为止，结果按段序拼接，顺序与单线程逐行完全一致；进度按已完成字节合并汇报，仍然单调。`SessionParallelReadTests` 用**自己写的单段读**当基线比对 id 序列（>8 MiB 才生效，防止基准退化），并覆盖「一条 6 MB 的巨型行横跨段界」。
- **上下文估算增量化。** `CodingAgentSession.ContextTokens` 按消息引用复用已估算的前缀，只估算新增/改写的尾部；分支切换、上下文编辑都能正确回退重算（`ContextEstimateIncrementalTests` 三个场景与参考实现逐位一致）。这条把「每条消息都要重扫 27k 条历史」的 O(n²) 苗头掐掉。
- **状态条快照挪到后台。** 真实用量、上下文估算、轮数原先在标签页首帧的 UI 线程算（193 ms），现在随 `session.open.prepare` 一起算好交给 `SessionTab`；流式期后续 revision 变化命中增量估算，`render.stats` 只剩个位数毫秒。

- **不把几 MB 的行缓冲塞进数组池。** `Utf8LineReader` 的行缓冲会跟着最大的一行长膨胀（几 MB 的正文很常见），原来无条件 `ArrayPool.Return` 等于让进程永久留着这块；现在超过 256 KB 就交给 GC，池里只留正常尺寸的分桶。解析一次的瞬时分配约 259 MB（与会话大小同阶，与并行度无关，实测过），这部分要靠数据结构分页才能压下来。
- 顺带把三个从未指向问题的细粒度计时（`render.entries`/`render.state`/`render.follow`/`render.chrome`）从诊断里去掉，面板只留有信息量的行。

下一步的已知对象：`session.open.tab` 的 240 ms 里首帧约 100 ms（`timeline.realize` 55 ms + `timeline.project` 26 ms），其余是标签页控件树本身；以及 27k 条会话条目常驻带来的 200 MiB 托管堆——那需要动数据结构（分页读历史或惰性 extras），不在本轮范围。

## 第二轮：首屏、实时帧与控件树

在同一台机器上用诊断数据继续压榨，测量方式见文末「性能诊断」一节。

### 启动与首屏

| 阶段 | 耗时（x64 Debug / Xvfb，3 次中位） | 是否阻塞首屏 |
|---|---:|---|
| 主题解析 + 内嵌字体注册 + 内置文案 | 109 ms | 是 |
| `MainWindow` 构造（整套控件树） | 53 ms | 是 |
| 窗口创建 → 首帧绘制（X11 + MewVG GL 上下文、字体图集） | ~880 ms | 是（框架/驱动侧） |
| 首屏可见（`startup.first-paint`） | **1.05 s** | — |
| 环境 + 侧栏摘要 + 首个会话解析（后台，与首屏并行） | 0.8–1.0 s | 否 |
| 会话就绪（`session.open.*` 全部完成） | **2.28–2.42 s** | 否 |

关键是这两条已经**解耦**：首屏只等控件树，不等任何 IO。首帧之后主区显示准备阶段面板与加载段落，后台把环境、侧栏与首个会话补齐；`session.open.read` 在预解析命中时记 0 ms（侧栏扫描已经读过同一份文件）。剩下的 ~880 ms 在窗口/GL 上下文初始化里，属于框架与驱动，不在应用代码内。

### 实时对话的每帧成本

1000 轮历史、400 个 token 帧、每 40 ms 合并一次渲染（x64 Debug / Xvfb，`BenchProductionFrameStages` 同款量法）：

| 帧内阶段 | 优化前 | 优化后 |
|---|---:|---:|
| `chat.Entries()`（快照） | 0.03 ms / 2 KB | 0.02 ms / 2 KB |
| 执行过程投影（增量只重投最后一轮） | 0.18 ms / 33 KB | 0.16 ms / 32 KB |
| `SetEntries`（行更新 + 原位重绑） | 2.96 ms / 214 KB | 3.14 ms / 217 KB |
| 布局（整窗 measure + arrange） | 10.97 ms / 751 KB | 10.20 ms / 632 KB |
| 整帧渲染合计（`Render` 平均值） | 4.37 ms | 3.75 ms |

这一轮砍掉的是每帧的**无谓分配与重复计算**：

- **右侧定位轴不再每帧整表重建。** `SyncOutline` 以前为上千行新建两个列表、再 `SequenceEqual` 整表比较（1000 轮历史约 60 KB/帧）。现在利用投影器已经保证的「稳定前缀引用不变」，只在尾部增删用户行；token 帧通常一行都不用扫。
- **`SameEntry` 去掉 `Zip(...).All(...)`**：过程组逐成员比较改成按下标循环，零枚举器分配。这个方法在每次行绑定、每次投影比较里都会调用。
- **`ExecutionProcessView.Nodes` 复用节点与成员缓冲**：原来每帧新建 `List<Node>` + 每组一个数组 + 插值键，现在节点对象按索引复用、成员数组在数量不变时原地覆盖。
- **发送/复制按钮的可用性不再每帧整表扫描**（两次 `all.Any(...)`，流式期每 40 ms 各扫两千行），按投影行数缓存。
- **状态条与消息头只在输入变化时重建字符串**：`_status.Text`、助手消息头（时间/角色/耗时）以前每帧重新格式化 + 时区转换；折叠预览以前每帧从头扫一遍思考正文，现在只在 4 KB 窗口内找首行，取到即定格。
- **助手消息头的 `DurationMs`/时间戳/流式标志**做成键比较，命中就直接跳过。

### 常驻内存

打开 45 MiB / 26,985 行的会话后，托管堆峰值 200 MiB、RSS 约 490 MiB，**这一轮没有实质下降**，原因值得记下来：

- 47 MB 的 UTF-8 正文解码成 UTF-16 `string` 后就约 94 MB，加上 27k 条 `SessionMessageEntry`/`Message`/`ContentBlock` 的对象头，就是 150–200 MiB 的主体。`ChatEntry` 行本身很便宜（实测每个 136 字节，空集合用 `Array.Empty<T>()`，不多带数组）。
- 想再压下去只能改数据结构：按文件偏移惰性解码正文、或对历史做分页。这会影响「整段历史随时可发给模型」的语义，属于架构级改动，本轮不做。
- 唯一拿掉的是**进程级常驻大块**：`Utf8LineReader` 的行缓冲会跟着最大的一行长膨胀（几 MB 正文很常见），原来无条件还给 `ArrayPool` 等于让进程永久留着；现在超过 256 KB 交给 GC。

## 第三轮：侧栏按需加载（默认收起）

AOT 实机数据（`timings-20261022-150604.jsonl`，78 份会话 / 304 MB）暴露了一处串行依赖：`environment.create` 950 ms、`sidebar.list` **897 ms**，而「首个会话就绪」要等两者都完成。拆开之后又发现，**启动时根本不必扫侧栏**。

### 改动

- **侧栏默认收起**（`DesktopPreference.SidebarCollapsed` 默认 `true`）。收起状态下启动完全不读会话摘要：首个会话用「只 stat 目录」得到的路径直接开，列表等展开侧栏或打开搜索时才加载（`EnsureSessionsLoadedAsync`，重复调用共用同一个在途任务）。旧偏好里显式写过 `false` 的用户保持展开。
- **侧栏摘要与首个会话解耦**：`_catalog.List()` 不再和环境构建、预解析打包在同一个 `Task.Run` 里。三者各自并行，会话不再陪侧栏扫完整个目录。
- **摘要扫描按文件并行**（`ScanParallelism`，留一个核给 UI）：66 份 / 285 MB 从 2043 ms 降到 1128 ms。
- **只扫「会显示出来的那些」**：文件数超过 `limit`（侧栏 200 条）时，先只 stat 排序、再对头部做摘要扫描。300 份 / 275 MB 的场景下要读的字节数从 275 MB 降到 2.3 MB（**35 ms**）。
- **搜索不受影响**：搜索弹窗仍在需要时触发加载，列表未就绪时显示「正在读取会话列表…」而不是「暂无会话」；数据到位后 `Reload()` 用当前关键字重跑过滤。
- 明确动作（改名、删除、归档、置顶、新建会话、手动刷新、分支）走 `RefreshSessions(force: true)`，不会因为「还没加载过」被静默跳过；流式结束这类后台刷新则不再主动付扫描成本。

### 实测（300 份会话 / 275 MB，x64 Debug / Xvfb）

| 指标 | 侧栏展开 | 侧栏收起（默认） |
|---|---:|---:|
| `sidebar.list` | 30–67 ms（200 份 / 2.3 MB） | **不发生** |
| 首屏 | ~0.7 s | ~0.7 s |
| 首个会话就绪 | ~1.1 s | **~1.1 s** |

跨进程复用摘要索引也试过（`<agentDir>/sessions/.piweft-catalog.json`，命中后侧栏 13.5 ms），但它引入一份额外的磁盘状态、还要处理多实例并发写，收益只是「侧栏秒开」而不是「能用」——按评估结论**不采用**，改为上面这套无状态方案。

## 第四轮：侧栏项目的展开状态

侧栏「项目」分段原来每次重建都把全部项目展开（`foreach (node) _sessionTree.Expand(node)`），会话多的机器上一打开就是几十个项目、上百条会话铺满屏幕。

- **默认折叠**：只有两类项目会展开 —— 用户上次展开过的（`DesktopPreference.ExpandedProjects`，按工作目录记录），以及**当前选中会话所在的项目**（否则高亮项被折在看不见的地方，用户会以为没选中）。
- **记录展开状态**：`TreeView.Expanding` / `Collapsing` 只在用户显式操作时触发（程序化重建时的同值调用会被框架短路），因此在事件里记一笔并落盘即可，不会把默认折叠状态写进偏好。全部折起来时直接删掉 `expandedProjects` 键。
- **选中判定按路径而不是会话 id**：外部工具生成的会话，头部 id 可能与文件名里的 uuid 不一致，用 id 会漏判、把选中项折起来。
- 判据抽成 `MainWindow.ShouldExpandProject(cwd, containsSelected, expandedProjects)`，单测覆盖四种组合。

实测（5 份会话 / 2 个项目）：启动后两个项目都是折叠态；点开 `other` 后 `desktop.json` 出现 `"expandedProjects":["/tmp/sidebar/other"]`；重启后 `other` 保持展开、`cwd` 保持折叠。

## 性能诊断（设置 → 维护 → 性能诊断）

对齐 PiDeck 的同名开关：默认关闭，关闭时每个调用点只做一次布尔判断（连秒表都不分配）；打开后无需重启即可开始记录，再关即停。

采样分三档，成本依次递增，越靠热路径越便宜（下列单次成本是这台机器 x64 .NET 10 的实测值）：

| 档 | 调用点做什么 | 单次成本 | 分配 |
|---|---|---:|---:|
| 帧档 | `Diagnostics.ObserveFrame`：写两个 2048 槽环形缓冲 + 几个 `Interlocked` | ~66 ns（含 GC 读数 ~120 ns） | 0 B |
| 计数档 | `Diagnostics.Count` / `Diagnostics.Slow`：桶内 `Interlocked.Add`，只有超过阈值才落明细 | 30–50 ns | 0 B |
| 明细档 | `Diagnostics.Record`：`DateTime.Now` + 锁 + 聚合 + JSON 序列化 + 入队 | ~2.1–2.7 µs | ~870 B |

- 帧档：接 `Window.FrameRendered`（MewUI 每帧渲染后触发）与 `Window.LastFrameStats`（框架本来就每帧无条件填好，所以绘制量是白拿的）。产出帧间隔的 p50/p95/最大、FPS（按典型帧中位数换算，不让一两帧建树把算术平均压成个位数）、掉帧（≥20 ms）/长帧（≥50 ms）计数、每帧分配字节、GC 暂停占比、输入→绘制延迟，以及 draw calls / 剔除 / 文本 / 形状 / 图片 / 裁剪计数。输入取样点挂在窗口级 `PreviewKeyDown`/`MouseDown`、会话滚动容器，以及阅读守卫接管的滚轮上（下游控件通常已经把事件处理掉，冒泡不到窗口根）。两帧之间超过 400 ms 不算帧样本而记成**停顿**：渲染循环是按请求跑的，空闲后的第一次重绘只说明「刚才没人要求重绘」，混进样本会让 FPS 与 GC 占比全部失真。分位数只在读面板/落盘时从环形缓冲算，热路径只写一个槽位；长帧另有限流 1 秒一条的 `Long frame` 警告日志。
- 计数档：给「每帧都会发生」的路径用 —— 行构建/回收（`row.build` / `row.release`）、流式期原位更新的行数（`row.inplace`）、`render.stats` / `timeline.realize` / `ui.render` 的重建轮次。设置页在耗时表前面显示这些计数，它们才是「重建 vs 复用」的直接对照量；改造前每帧两条明细记录约等于每秒上百 KB 垃圾，现在同一位置只是 `Interlocked.Add`。
- 明细档：只留给低频粗路径与慢于阈值的样本，逐条写 JSONL。
- 采样：1 秒一次的线程池定时器读工作集 / 私有内存 / 托管堆 / 碎片 / 累计分配 / GC 次数，加上 CPU 占比、提交内存、pinned 对象、待终结对象、线程池线程数与排队工作项，并从池线程向 UI 线程投递一个探针，量它被接手前排了多久 —— 这就是 PiDeck 里 `setImmediate` 往返的等价物，≥ 50 ms 直接落一条警告日志（「谁把 UI 线程占住了」）。UI 队列延迟只在运行时可测，无头测试里为 0。
- 逐秒曲线：每秒写一行 `name="runtime.window"`（`frames/fps/p50/p95/max/dropped/long/stall/alloc-per-frame/gc + heap/loh/gen0/gen2/finq/pinned/rss + draw/cull/text/input/lag/cpu/pending/counters`）。**空闲也写**：判断「堆是不是一直在长」恰恰要看没有重绘的那段时间。设置页的「压一次堆」走 `GC.GetTotalMemory(forceFullCollection)`，直接给出回收前后的差值 —— 堆掉不回基线才是泄漏。
- 落盘：每条耗时一行 JSONL 写进 `<agentDir>/diagnostics/timings-<本地时间戳>.jsonl`，同时带上当时的 RSS / 托管堆，把「这一步慢」和「当时涨到多少」对在一起。250 ms 一批 flush，关闭时先把缓冲写完再释放句柄；单条 ≥ 200 ms 额外记 `Slow path` 警告。
- FPS 的读法：FPS 由**帧间隔中位数**换算，而 MewUI 的渲染循环是「按请求重绘」。启动那几秒是背靠背的布局帧（间隔 7 ms → 报出 138），稳定后只剩真实重绘的间隔（含 vsync 等待 → 75 左右），不是掉速。面板因此同时给出「节奏」行（连续/按请求 · VSync · 目标帧率）与每秒帧数，`stall` 计数把 >400 ms 的空档从样本里剔除。
- 聚合：内存里按名称累计次数 / 总耗时 / 最大 / 最近，设置页按顺序展示「概览指标块 → 帧时间条 → 渲染循环 → 内存与 GC → 事件计数 → 耗时聚合 → 最近明细」；指标块用两列两行避免文字被裁，长备注只进悬浮提示；重新开启会清零聚合并换一份新文件，避免上一轮的慢路径混进这一轮对照。「清空统计」用于同一轮里分段对照。
- 免代码的更狠一档：Debug 构建里 MewUI 自带 DevTools（`<MewUIDevTools>` 在 Debug 默认为 true），`Ctrl+Shift+P` 是帧档叠层（FPS/帧分解/draw calls/每帧分配/GC 次数）、`Ctrl+Alt+Shift+P` 是逐帧 profiler、`Ctrl+Shift+T` 视觉树、`Ctrl+Shift+I` 元素拾取；trimmed 与 NativeAOT 发布会自动把它折叠掉（发布产物里 `Aprillz.MewUI.DevTools.Enabled=false`），所以设置页那份自持面板仍然必要。CPU 采样、GC 堆转储、ETW/perf 这类重活一律交给外部工具（`dotnet-trace`、`dotnet-gcdump`、`perf`），不在应用里自建。
- 计时点：`startup.theme-and-fonts`、`startup.window-build`、`startup.first-paint`（首屏时延，`Application.Run` 的 startup 回调里挂一个 Render 优先级工作项来量）、`environment.create`（启动构建环境）、`mcp.connect`、`sidebar.refresh`（侧栏全量刷新）、`session.open.read`（解析整份 JSONL，带 bytes/lines/entries）、`session.open.prepare`（建会话 + 聊天记录投影，带 entries/messages）、`chat.project`（整段投影）、`session.open.tab`（标签页构造）、`ui.render`（计数每次，明细只记 ≥ 8 ms 的帧，带 rows/visible）、`markdown.finalize`（思考定稿重建文档树，带 chars）。

45 MiB / 26,985 行真实会话在诊断页实测到的一轮（x64 Debug / Xvfb，值随冷热波动）：

| 计时点 | 耗时 | 说明 |
|---|---:|---|
| `session.open.read` | 1192.7 ms | 后台线程，不占 UI |
| `session.open.tab` | 375.6 ms | UI 线程，标签页构造 |
| `session.open.prepare` | 312.0 ms | 后台线程 |
| `ui.render` | 259.5 ms | UI 线程，首帧（含虚拟化建树） |
| `chat.project` | 249.1 ms | 后台线程（随 prepare） |
| `sidebar.refresh` | 200.5 ms | **UI 线程**，侧栏全量刷新：下一步的优化对象 |
| `environment.create` | 86.2 ms | UI 线程，启动一次 |
| `mcp.connect` | 4.3 ms | 后台 |

  同一轮 UI 队列延迟最大 41.1 ms、工作集 494 MiB、托管堆 282 MiB —— 这类数字就是用来判断「该把哪段挪出 UI 线程」的，而不是靠肉眼猜。

同一份 11 MB 会话在打开并滚动时的逐秒实录（x64 Debug / Xvfb，`runtime.window`）：

```text
07:52:53 frames=5  fps=26.7 p50=19.6  p95=89.3  max=89.3   dropped=1 long=1 stall=0/0.0ms
         alloc/frame=926560B gc=0.03% draw=6 cull=0 text=0 counters=[row.build:14,row.release:11,...]
07:53:06 frames=1  fps=10.6 p50=29.9  p95=376.7 max=376.7  dropped=2 long=3 stall=24/517.7ms
```

以及一轮 3 分半「反复上下滚各 12 格」的压力采样（每 8 秒取一点）：

| 时刻 | 托管堆 | 大对象堆 | 工作集 | gen0 / gen2 | 待终结 |
|---|---:|---:|---:|---|---:|
| 15:30:36 | 104 MiB | 52 MiB | 368 MiB | 15 / 3 | 42 |
| 15:31:15 | 106 MiB | 52 MiB | 421 MiB | 24 / 3 | 1884 |
| 15:32:20 | 107 MiB | 52 MiB | 429 MiB | 41 / 3 | 837 |
| 15:33:25 | 109 MiB | 52 MiB | 433 MiB | 58 / 3 | 519 |
| 15:34:17 | 111 MiB | 52 MiB | 438 MiB | 70 / 3 | 663 |

读法：托管堆 3 分半只涨 7 MiB、大对象堆完全不动、gen2 始终是 3（还没到期收）、待终结队列涨到 1884 又落回 519 —— 是**回收滞后 + 终结器排队**，不是泄漏；工作集涨的那几十 MiB 是原生侧（Skia 字形/位图缓存与保留场景），要判泄漏就点「压一次堆」看剩余，或者把同一操作重复几轮看剩余是不是一路抬高。这类结论靠每帧明细看不出来，靠帧档 + 计数档才成立，而它占掉的墙钟时间不到 0.05%。

## 验证与复现

```sh
xvfb-run -a dotnet test PiWeft.slnx
xvfb-run -a dotnet test tests/PiWeft.CodingAgent.Tests --filter FullyQualifiedName~SessionOpenProgressTests
xvfb-run -a dotnet test tests/PiWeft.Desktop.Tests --filter FullyQualifiedName~DiagnosticsTests
# 诊断分区截图验收（需要 DISPLAY）：偏好里先写 {"diagnostics":true}
PIWEFT_SCREENSHOTS=/tmp/piweft-diagnostics xvfb-run -a dotnet test tests/PiWeft.Desktop.Tests --filter FullyQualifiedName~SessionLoadingSmokeTests
xvfb-run -a dotnet test tests/PiWeft.Desktop.Tests --filter FullyQualifiedName~SessionLoadingPanelTests
xvfb-run -a dotnet test tests/PiWeft.Desktop.Tests --filter FullyQualifiedName~SessionLoadingSmokeTests   # 需要 DISPLAY
xvfb-run -a dotnet test tests/PiWeft.Desktop.Tests --filter FullyQualifiedName~SessionTabChatHandoffTests
xvfb-run -a dotnet test tests/PiWeft.Desktop.Tests --filter FullyQualifiedName~ConversationPerformanceTests --logger 'console;verbosity=detailed'
dotnet publish src/PiWeft.Desktop -c Release -r linux-x64 -o artifacts/desktop-optimized-linux-x64
PI_WORKSPACE_SMOKE_APP="$PWD/artifacts/desktop-optimized-linux-x64/piweft-desktop" \
  xvfb-run -a -s '-screen 0 1600x1000x24' python3 tools/desktop-workspace-smoke.py 0 4 9 10 11
PI_WORKSPACE_SMOKE_APP="$PWD/artifacts/desktop-optimized-linux-x64/piweft-desktop" \
  PI_WORKSPACE_SMOKE_TURNS=1000 \
  xvfb-run -a -s '-screen 0 1600x1000x24' python3 tools/desktop-workspace-smoke.py 0
```

原生鼠标/键盘演练：

```sh
PIWEFT_NATIVE_TESTS=1 PIWEFT_SCREENSHOTS=/tmp/piweft-ui-validation \
  xvfb-run -a -s '-screen 0 1440x1000x24' \
  dotnet test tests/PiWeft.Desktop.Tests --filter FullyQualifiedName~NativeHoverTests

PIWEFT_NATIVE_TESTS=1 PIWEFT_SCREENSHOTS=/tmp/piweft-interaction-validation \
  xvfb-run -a -s '-screen 0 1440x1000x24' \
  dotnet test tests/PiWeft.Desktop.Tests --filter FullyQualifiedName~NativeMessageInteractionTests
# Set PIWEFT_INTERACTION_APP to a NativeAOT executable to exercise the published app.
```

截图演练均使用独立目录，无真实会话或凭据。脚本除窗口尺寸与进程退出检查外，还断言最新回复实际落在消息视口内。`PI_WORKSPACE_SMOKE_TURNS` 可设 0–5000，0 使用原工具 fixture。
