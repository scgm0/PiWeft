# 性能与基准

本文只保留当前性能结论、口径与复现入口；功能对齐见 [01-parity](01-parity.md)。所有数字是本机局部观测（x64 Debug / Xvfb / MewVG X11），不是所有硬件的保证。

## 会话统计口径

- 缓存命中率 = `cacheRead / (input + cacheRead + cacheWrite)`；平均命中率按有效助手消息算术平均，加权命中率按累计 token 计算。
- 当前分支轮次按原始用户消息计数；压缩摘要与分支摘要不计轮次。
- 首 token 延迟取正文首个非空 delta，无正文时回退思考；生成速度 = 输出 token / 首个 delta 到回复结束；端到端速度除以整次请求耗时。
- 新回复持久化 `firstTokenMs` / `streamingDurationMs` / `durationMs`；旧会话缺失显示 `-`，不推测。
- 会话均速按各回复活跃区间的并集计算（重叠只计一次）；缺时间戳的旧记录不入样本。
- 工具累计耗时是各次执行之和，并行会重叠，不代表墙钟时间；人民币按固定 `1 USD = 7.2 CNY` 估算。
- 上下文三段（系统/工具/对话）按 `系统 + 工具 + (总占用 − 系统 − 工具)` 展示，估算超出时优先系统，进度条不越界。

## 关键实测

| 场景 | 结果 |
|---|---|
| 92 会话 / 331 MiB 侧栏启动 | 冷启动 **1046 ms** → 摘要热启动 **52 ms** |
| 摘要文件体积 | 38.5 KiB（预览 200 字符、条目上限 4096） |
| 11 MiB / 1 万条会话续扫 | 每轮 0.2–1.4 ms；失效整扫 63–83 ms |
| 45 MiB / 26,985 行会话打开 | 解析 0.84–0.88 s；建投影 0.27–0.36 s；标签页构造 0.35–0.73 s（冷）/ ~0.04 s（热） |
| 常规会话打开合计 | 中位 ~5 ms、p90 80 ms、18 MiB 262 ms、45 MiB 1.7 s |
| 堆占用 | 约为文件 3×（45.3 MiB → 137–172 MiB） |
| 流式每帧分配（时间线 + Markdown） | ~0.7 KiB；`Entries()` 重建约 1.3 KB/帧（修复前 394 KB/帧） |
| 流式每帧分配（`--ui-smoke stream`，诊断 `alloc/frame` 逐秒采样） | 文档最大时（末尾）299 KB/帧 → 174 KB/帧；全程均值 143 KB → 111 KB，中位 174 KB → 156 KB（同一脚本、Release、80 s，语料无上限地往同一条回复追加） |
| 32 K 尾部追加 | 中位 0.7–2 ms，p90 1–4 ms，~0.1 MiB/帧 |
| 24 K 单段落逐段追加 | 中位 4–7 ms，p90 8–15 ms，~0.5 MiB/帧 |
| 32 K 单段落一次性全量布局 | ~79 ms（历史消息首次进入的一次性成本） |
| 纯文本追加 32 K | 全量替换 42.5 ms → 增量追加 0.78 ms |
| 1000 轮会话冷启动局部 | Markdown 视图 1000 → 3；累计分配 253.6 MB → 5.3 MB；4445 ms → 767 ms |
| 10,000 条追加 2 条后投影 | 4.13 ms / 1.26 MB → 0.016 ms / 9 KB |
| 1000 轮追加一条 user 后重建 | 2.53 ms / 1.25 MB → 0.25–0.6 ms / 36 KB |
| 2,000 行流式过程投影 | 1.15 ms / 233 KB → 0.07–0.12 ms |
| 40 会话侧栏冷扫 | 39–67 ms / 34.4 MB → 19–24 ms / 0.26 MB |
| 使用统计 343 会话 / 867 MiB | 全扫 0.72–1.0 s → 按文件并行 0.28–0.42 s，新进程首扫 2.4 → 0.50 s；冷页缓存（`fadvise DONTNEED` 后）4.1 → 2.8 s，其中 867 MiB / 400 MB/s 的读盘下限约 2.2 s；分配 21.4 MiB；无变化刷新 2.2 ms；20.7 MiB 活跃会话追加一条 0.12–0.22 ms / 43.6 KiB；报告 + 文件聚合常驻 1.97 MiB |
| 本仓 14,208 文件遍历（rg 语义，8 线程） | 1.3 s → 0.09–0.10 s，分配 10 MiB |
| `find` 全量列举 / `*.cs`（limit 1e6） | 0.76 s → 0.06–0.07 s / 0.043 s（fd 0.10–0.12 s / 0.09 s） |
| 本仓 279 MiB `grep` 无命中 | 5.6 s → 0.09–0.11 s（rg 0.045 s） |
| `grep` 命中若干条（额度内） | 1.6 s → 6–10 ms（rg 0.05 s：我们命中够了就停遍历） |
| 23 MB 单文件 `grep` | 3.5 ms（mmap，6.6 GB/s；rg 3.7 ms） |
| `write` 1 MB / 10 MB | 分块编码写入后 0.78 ms / 8.9 ms（pi 1.33 ms / 11.9 ms）；100 B–2 KB 均 0.04 ms（pi 0.07–0.17 ms） |
| CLI NativeAOT 产物（linux-x64，Release） | 当前提交 31,671,672 B。runtime-async 的 A/B 在同一棵树上量：关 32,060,744 B → 开 31,321,368 B（-739,376 B，**-2.3%**） |
| `SessionManager.Open` 46 MB / 26,984 行（只读+建索引，9 轮中位） | `List<object>` 管线 402 ms / 249.2 MiB → `List<SessionLine>` union 管线 403 ms / 249.2 MiB（各 5 次采样，无差异） |
| SSE 事件解析 | 192 B/事件 → 72 B/事件，快 15–20% |
| NativeAOT 桌面体积（linux-x64，Release，当前提交） | 82.9 MiB（86,926,248 B），配置含 `EventSourceSupport`（换来 `dotnet-counters`/`dotnet-trace` 可附着）与显式 `Aprillz.MewUI.Automation.Enabled=false`。旧记录 82.5 / 82.1 / 80.6 / 80.0 / 75.8 / 77.4 MiB 与 62.4 / 64.0 MB 均来自更早构建 |
| 桌面托盘驻留的产物体积代价 | 同一份代码只加托盘入口：86,539,488 B → 86,706,856 B，+167,368 B（+0.19%）。托盘后端此前已由 X11 宿主带进产物，增量是 `TrayIcon` 实例、菜单与设置项 |
| MewUI 子模块更新（c2cd7ef → 2f6943d）的产物体积代价 | 86,926,312 B → 86,926,248 B，-64 B。6 个提交落在窗口后端（X11 撤销后重新映射、macOS orderOut 的状态记录）与 Direct2D 的裁剪读取、DWrite 字族回退，Linux 的 MewVG 产物没有新增符号；同期删掉的 `X11WindowMapping` 也在这一份差额里 |
| MewUI 子模块更新（45428e1 → c2cd7ef）的产物体积代价 | 同一份代码只换子模块：86,067,872 B → 86,539,488 B，+471,616 B（+0.55%）。11 个提交新增 Linux 托盘（StatusNotifierItem）与应用标识；托盘后端挂在 `X11PlatformHost` 上，产物里保留了 `Tmds.DBus.Protocol` 与 `TrayIcon` 符号，AT-SPI2 关闭已不足以把 DBus 栈裁掉 |
| MewUI 子模块更新（553f28d → 45428e1）的产物体积代价 | 同一份代码只换子模块：85,719,760 B → 86,067,872 B，+348,112 B（+0.41%）。新增的 system backdrop、托盘、多源绑定与自动化代码在不引用时不进产物；增量来自 retained 渲染（组合层缓存、backdrop pass）与 MewVG 0.11.1 |
| MewUI 子模块更新（2b93028 → 553f28d）的产物体积代价 | 同一份代码只换子模块：84,459,584 B → 84,475,952 B，+16,368 B（+0.02%）。新增的 X11 AT-SPI2 代码与 `Tmds.DBus.Protocol` 依赖在 automation 关掉后被裁掉 |
| `Aprillz.MewUI.Automation.Enabled` 不写 vs 写 false | 84,830,160 B → 84,475,952 B，省 354,208 B：不写这一项时裁剪器拿不到 feature 值，ILLink 里 `featurevalue="false"` 的替换不生效，整条 automation 树留在产物里 |
| 第三方包升级的产物体积代价（linux-x64，Release，NativeAOT） | ZLogger 2.6.2 / Markdig 1.6.0 / MiniPty 1.6.0 / Test.Sdk 18.12.0 / Jint 2428 / Sdcb.SimdCodecs 2.0.0：CLI +337,712 B（+1.08%），桌面 +433,544 B（+0.52%）；其中 Jint 单独占 +41,200 B |

内存读法：工作集包含字体映射、GPU/驱动共享表面与保留合成；判断泄漏看趋势与 `runtime.window` 的 `rtext/rgeom/ratlas/rpersist/rscratch/rdecoded/rbitmap/rvector/rencoded` 台账，再用 `dotnet-counters`/`dotnet-trace` 与 `smaps_rollup` 对照。发布版已打开 NativeAOT 的 `EventSourceSupport`（实测 +1.6 MiB），`dotnet-counters collect` 已验证可附着；但 NativeAOT 仍不支持 `dotnet-gcdump` 的堆分析，托管堆趋势以自带窗口值为准。打开含大量截图的会话会一次性抬高工作集，不属于逐帧增长。

## 已落地的热路径

- 大段工具结果正文延迟物化：`TextContent` 超过 8 KiB 只留 UTF-8，`Text` 按需解码；桌面 `DeferredText` 在摘要与行高估算时只取前缀，展开才解全文。30.2 MiB / 600 条 50 KiB 工具结果的 v3 会话，打开后稳态堆 33.5 MiB（1.11× 文件，同形状此前 2.10×），打开中位 78 ms。`dotnet run --project tools/PiWeft.SessionBench -c Release -- memory` 复现。
- `Truncate.Head`/`Tail` 改成按 span 扫行：不再 `Split` 出整份行列表，`Tail` 也从 `List.Insert(0)` 改为反向切片。3.79 MiB / 12 万行的 shell 尾部截断从 23–32 ms、12.3 MiB 分配降到 1.3–2.5 ms、100 KiB（同一进程 20 次均值，Release）。
- 侧栏摘要持久化到 `<agentDir>/session-summaries.json`，键为长度 + mtime + 首行指纹；追加时用尾巴指纹续扫，原地重写则整扫。缓存损坏退化为全扫；删除会话的条目下次构建时清除。读写走 System.Text.Json 源生成（141 条 83 KB 的缓存：装载 1.08 MB 分配 → 0.38 MB，2.3 ms → 1.4 ms），落盘在后台线程单飞，关窗时同步补一次。
- 会话搜索的正文扫描按 1 MB 分块扫全文，块间留关键字长度减一的重叠；比较在 UTF-8 字节上做：没有 ASCII 字母的关键字（中日韩）走向量化 `IndexOf`，带大小写的逐个 rune 折成大写，先用 `SearchValues<byte>` 筛候选首字节再比。只有命中行前后 8 KB 才解码，摘要仍从 JSON 里取字符串值。总读盘预算 512 MB，按列表顺序预先摊好每份读多少字节（额度不够的那份读尾部，再后面的整份跳过），命中范围不随线程调度变。实测 141 份会话 / 470 MB：命中 20–25 ms / 0.3 MB 分配，未命中 59 ms（无大小写、精确路径）到 80–173 ms（带大小写的折叠路径，要逐候选解 rune）/ 0.02–0.07 MB；此前的「只读尾部 512 KB」是 110–136 ms / 118 MB，覆盖范围反而只有全文的七分之一。
- 使用统计按会话文件增量：`UsageStats.ScanState` 缓存每个文件的按日/模型聚合，只重读长度或写入时间变化的文件；追加时校验文件头 256 字节与断点前 64 字节后从断点续读；文件重写、末尾没换行或读失败就整扫，读失败时保留旧数据并把报告标成截断。价格只在对齐报告时应用一次，改模型价格不必重读文件。单行超过 256 KiB 时不复制进行缓冲，按空行交回，断点仍落在行边界。
- 全量扫描按文件并行（上限 8 线程，大文件先开始），增量场景（≤2 个文件）仍走串行：每个文件的聚合互相独立，合并顺序不影响结果。单线程口径下 20.3 万行里 9.3 万行带 `usage`，`Utf8JsonReader` 走一遍要 650 ms（约 10 µs/行，与纯 token 前进等价，正文 token 在 `usage` 之前所以省不掉），整条链路 850 ms 中 I/O 只占 160 ms；并行后稳态 280–420 ms，新进程首扫 0.50 s。实测过的两个方案都不划算：启动预热把热路径先跑 300 次只省 10 ms；手写跳过 `message.content` 只快 1.4 倍（含 `SearchValues` 向量化估计 2 倍），却要把 JSON 逐字节走一遍。
- 会话解析用 `Utf8LineReader` + `SessionJson.ParseLine`；大文件按 2–3 段并行，顺序与单线程一致；进度按字节汇报。`Utf8LineReader` 对超大行缓冲（>256 KiB）交给 GC，不归还共享池。
- 会话行改用 `Utf8JsonReader` 解析（`SessionJsonReader`），不再逐行建 `JsonNode` 树，也不走 `GetString()`：大文本块未转义时直接接管 `ValueSpan`，含转义（工具输出几乎都带换行）时用 `SessionJsonUnescape` 解到池化缓冲，最后只复制实际长度。30.2 MiB / 600 条 50 KiB 工具结果的会话，打开分配 125.84 → 31.78 MiB、中位 173 → 84 ms、稳态堆 33.52 → 30.28 MiB（1.00× 文件），gen2 从 1–4 次降到 0–1 次。`dotnet run --project tools/PiWeft.SessionBench -c Release -- memory` 复现；`-- parse` 对比逐行解析（`SessionJson` 14.4 ms / 31.4 MB vs `JsonDocument` + `GetString` 28.7 ms / 61.6 MB）。`SessionJsonReaderTests` 把 reader 路径与 JsonNode 路径的序列化结果逐字节对比，覆盖转义、代理对、未知字段与未知角色。
- 写回也走 UTF-8：延迟存储的正文直接 `WriteRawValue` 写原始字节（`JsonUtf8String`），转义用 `SearchValues<byte>` 找下一个待转义字节、中间整段复制，不再解码成 UTF-16 再编码。600 条 50 KiB 工具结果写回 253.4 → 165.9 ms、分配 62.5 MB → 0.1 MB（`SessionBench -- write`）。
- 请求体不再复制：`JsonPayload.Write` 直接把 `ArrayBufferWriter.WrittenMemory` 交给 `ReadOnlyMemoryContent`，省掉 `ToArray` 那一份与请求体等量的分配（长会话每轮几十 MB）。Bedrock 的 SigV4 与 Codex 的 WebSocket 也接受 `ReadOnlyMemory<byte>`，签名哈希与帧发送都直接取 span。
- 流式正文与思考用 `TextContent.FromStreaming` / `ThinkingContent.FromStreaming`：provider 每个 delta 只往 `StringBuilder` 追加，快照共享同一个实例，文本第一次被读时物化并缓存。此前每 delta 一次 `ToString()`，一条 50 KiB 回复在 2000 个 delta 下是约 50 MB 的 O(n²) 分配。`ThinkingContent` 的相等性按正文比较，不比较 buffer 引用。
- RPC / JSON 的事件序列化复用线程静态 `ArrayBufferWriter` + `Utf8JsonWriter`（`Reset`），不再为每个流式事件新建 `MemoryStream` 与 writer。
- 会话 JSONL 追加用 `File.AppendAllBytes` + 可复用 `ArrayBufferWriter<byte>`；SSE 每流复用 UTF-8 缓冲。
- 分支快照用不可变列表结构共享；`BuildProjectedMessages()` 在纯追加时复用前缀；`ChatViewModel.Rebuild()` 只重算最后一个用户轮次；过程投影只重投当前轮；统计只消费新增条目。
- 继续生成的资格查询走「投影倒扫 + 错误文本定性」：定性结果按助手消息对象缓存（`ConditionalWeakTable`），错误文本可以带整段供应商响应体。带 146 字错误的 2000 条会话，每次查询 17.7 µs → 0.24 µs、0 分配（Release，100 万次）；界面每个按键与每帧都会问一次。
- 流式正文/思考改为追加式 `StreamedText` 快照，长度与行相等判定 O(1)，整篇物化只在复制/导出发生。
- Markdown 解析按尾块增量，块级模型哈希做前后缀 diff；段落按已闭合行分块，只重排开放尾块；代码块 `ReplaceRange`；定稿只切 `IsStreaming`。
- 增量解析的文档簿记不再按整篇重建：文本单元下标直接记在 `MarkdownBlock.TextUnitIndex`（原来每帧为整份文档重建一张 块→下标 字典），锚点表在尾块没有标题/锚点、被替掉的旧块也没有锚点时按引用共享上一版（原来每帧复制两份）。800 块、每帧都带一个新标题（最坏形状）的逐帧追加从 151 KB 降到 100 KB；无标题的纯段落递增约 20 B/块（100→400 块时 12–18 KB/帧）。
- 段落渲染的 paint span 按「run 版本 + 块边界 + 链接色/标记色」缓存：流式期逐帧重绘可见块时，不再为每个块新建 `List` 与 `ToArray()`。
- `MarkdownView` 的前缀和数组按几何增长复用，重算循环走 `CollectionsMarshal.AsSpan`；尾块检测 `ContainsLinkReferenceDefinition` 先用一次向量化 `IndexOf("]:")` 排掉没有链接定义的帧（每帧都会调用）。
- 流式每帧的账单还有一条固定项：开放尾块每帧都要重新解析与重排，单段落追加的成本与段落长度成正比（文档里的 0.5 MiB/帧就是这一项），不属于簿记开销。
- 虚拟化只建视口附近控件；离屏释放正文/Markdown/图片，保留展开状态；分支树延迟构建；Replace 只覆盖变化的连续行段。
- 标题栏页签只在条目增删/重排时重建按钮；标题与选中项在原控件上更新，不再为每次状态刷新拼整条标题签名或换掉按钮，焦点与悬停提示保持。页签超出宽度时横向滚动，滚轮映射到横向轴，选中项在布局后用 `Bounds` 对齐。
- 平滑滚动按帧合并输入、Hermite 续接、设备像素提交；虚拟化高度校正时平移动画目标，避免「滚很多却回退」。
- 会话时间线与执行过程组体共用 PiWeft 自己的虚拟化面板（`VirtualRowsPanel`）：每行独立估算高度、维护前缀和与阅读锚点，不再依赖 MewUI presenter 的全局运行平均；前缀和只在行数/高度/宽度变化时重算（滚动通知里的阅读点捕获是 O(log n)），高度缓存与快照缓冲按几何增长复用，行回收遍历零分配。结构变化（插入/删除/过程组重排）按顶部行身份补偿坐标系平移（跟底时让位给追末尾）；extent 收缩把 reader 夹到新末尾后，再用行内偏移映射回原处。
- 行内容在自己的 arrange 里长高（Markdown 滚动中补实现新块）时，`VirtualRowsPanel` 同一帧重测这些行再摆一遍：否则下一行停在旧位置，被长出来的正文压住。变高的行在阅读锚点上方时，同一段差额补进偏移，浏览中的读者不会看到正文往上跳。
- 块级 `HasImage`（子树里有没有图片 run）在哈希阶段随子块一起算好：跨实例高度缓存不必在每次块测量时递归子树查图片。
- 过程组跟底由几何决定；展开工具行用步骤索引锚定预览行，虚拟化换宿主也不失效；流式思考只续跟、不反复锁底。组体内容在测量里长高时同帧补跟底（订阅内容尺寸，而不是等 Render 阶段那次），新露出的一行不会先贴在视口底边被切掉一截。
- 过程组只在步骤内容真的变化时作废组体排列：未变帧不再每 40 ms 重排一次行表（41 步虚拟化组体：3.6 KB / 0.20 ms → 0 B / 0.035 ms）。≤24 步的栈组体只追加/释放差异行，不再整块重建，展开步骤里的焦点与选区不会因为下一步到了就丢。节点成员按区间直接写进复用数组，每帧少一趟成员缓冲的二次搬运。
- 链接解析（`MessageLinkTarget`）改用源生成正则，控制字符用 span 范围查找：每次渲染链接少两次全局 Regex 缓存查表，也不再分配 `CharEnumerator` 与委托。
- 工具展开参数用 `JsonDocument`/`JsonElement` 读取，不为只读字段建 `JsonNode` 树；行数用 `MemoryExtensions.Count`，参数/结果换行裁剪走 `ReadOnlySpan<char>`。过程组表头只在参数变化（或参数缺失时正文长度变化）时重解析，流式输出增长不再反复拆同一段 JSON。
- 工具参数值、write 内容、read 结果与 diff 的长文本走 `MultiLineTextBox` 的虚拟化布局：超过 32 KB 或 400 行时限制为 240 DIP 视口，`SizeToDocument` 不再把整份文档逐行物化；并排 diff 的正文与行号列共享纵向偏移。
- 图片/文件面板、Git 状态/图/分支、检查点列表放后台线程，generation 丢弃过期结果；面板按节独立宿主，选行只换 diff。
- 图片管线（`PiWeft.Imaging`）按「解码一次、重采样一次、编码候选最小者」组织：限制内的图只做解码校验后原样返回（1920×1080 ≈ 15 ms）；超限图应用 EXIF 方向后 Lanczos3 重采样，PNG 与 JPEG 质量阶梯里取进预算的最小者，仍超再按 0.75 递减。重采样器是分离式两遍：水平方向「源驱动散射」（每个源像素只预乘一次，按反向权重写目标行）+ 垂直方向 `Vector<float>` 累加，分带相互独立并行；`ArrayPool` 复用行缓冲、`ref` 局部写避开边界检查、不透明图走无预乘快路径。12 MP（4032×3024 → 2000×1500）端到端从 ~2.4 s 降到 ~0.62 s（其中重采样 1.9 s → 0.33 s，8 核），2560×1440 ≈ 0.25 s，1920×1080 只做解码校验 ≈ 14 ms。方向变换按 `uint`（RGBA 四字节）整体搬，并给 8 个方向各写一条专用循环。附件归一化在线程池上跑，`read` 工具也把读文件与解码移出 UI 线程。
- 诊断分帧档（约百纳秒/0 B）、计数档（30–50 ns/0 B）、明细档（约 2 µs/~870 B）；`Slow` 的 detail 延迟到超过阈值才格式化，流式追加只计数+采慢样本。空闲也写 `runtime.window`，便于判断堆趋势。
- 滚动/首次打开复杂会话的剩余长帧主要来自框架的文本重录、交换缓冲与 GL 上下文初始化，应用侧不再收窄。
- `grep`/`find` 的 ignore 匹配不再用正则：每条规则编译成 `/` 分段 + 段内 `*`、`?`、`[...]` 的 glob。纯名字与 `*.ext` 进字典；名字通配按首字符分桶（`[Bb]in`、`[Dd]ebug*/`），`*` 开头的再按末字符分一次；路径规则按首段分桶，`**/x/...` 按 x 或 x 的首字符分桶，`**/x` 直接退化成名字规则。路径按段切一次、所有路径模式复用切好的段（旧实现每次试 `**` 都重新 `IndexOf` 切一遍）。
- 命中后的求值不再全表扫描：正向命中且没有否定规则命中时，最后命中的必然是正向规则，直接返回「忽略」；只有真的存在否定命中才按序逐条求值。vendored 的 196 条规则层因此从 11 µs/条目降到 5 µs，80 条规则层从 2.4 µs 降到 0.85 µs（单线程测量）。
- 遍历改成流式：一个目录一个工作项，`ConcurrentBag`（线程本地 LIFO + 窃取，对应 `ignore` crate 的 crossbeam-deque）分发，`ProcessorCount` 个 worker 消费；第一个子目录就地继续、其余入队。条目在 worker 线程上直接交给访问者，非空结果入 `BlockingCollection`，消费方边遍历边取，不再有「整棵节点树 + 按序装配」那一层。**输出顺序因此是完成顺序**（rg/fd 也不保证顺序），换来每趟少一次树构建与一次全量装配。
- 规则链写时复制：没有 ignore 文件的目录（绝大多数）直接共享父节点的层列表，只有真的压栈才复制。目录列举列表、路径缓冲按 worker 复用；`grep` 的 `FileScan` 里显示路径、行表、前导上下文队列全部惰性分配（无命中的文件不产生这些分配，也不进结果队列）。每趟遍历分配 12 → 10 MiB。路径缓冲每个 worker 复用（对应 `ignore` 的 `PathBuf` push/pop），只有留下来的条目才 `new string`。单趟 `FileSystemEnumerable` 拿名字与 d_type（不再 `GetFiles` + `GetDirectories` 两趟），相对路径按「当前目录前缀 + 名字」切片，规则基准目录只做一次前缀比较。子模块边界按整栈快照还原（此前进入嵌套仓库会永久丢掉祖先规则，本仓多列出 7,213 个 `bin`/`obj` 文件）。输出与 `rg --files --hidden` 逐行相同。
- `find` 的 glob 匹配与 `grep` 的扫描都在 worker 线程上做（访问者返回匹配结果或扫描结果），消费方只收集/合并。`grep` 的合并就地做：每到达一个文件结果就按上限与尾随上下文裁决，不再攒一份 `List<FileScan>` 再遍历。命中额度用完时遍历停止下探（`rg` 命中够数也会杀掉搜索），因此**撞到上限时的结果集不保证顺序一致**，与 rg 相同。
- `grep` 读大文件走 mmap（≥1 MiB）：映射出来的字节直接做 SIMD 查找，不复制、不用池化缓冲（`MemoryMappedFile` 只能通过指针取 span，`PiWeft.CodingAgent` 为此打开 `AllowUnsafeBlocks`）。映射失败或非普通文件退回分块读。匹配分两条路。字面量（`literal`，或 pattern 里没有正则元字符）走字节匹配：`SearchValues<byte>`/`IndexOf` 直接在字节上找，命中后才解码那一行；无命中时整份文件只做一次 SIMD 查找。正则走字符路径，但先用 pattern 里最长的字面量片段做块级预筛，整块不含该片段就不解码。小文件按 256 KiB 分块读（切点对齐换行符），避免为 20 MB 以上的文件租 LOH 数组。上限与尾随上下文在合并阶段裁决。
- `write` 不再整份 `Encoding.GetBytes`：改成 16 KiB 字符分块 + `Encoder.Convert` 写进池化缓冲（峰值分配从「内容大小」降到 ~48 KiB），`FileStream` 关掉内部缓冲并声明顺序写。10 MB 写入 16 ms → 8.9 ms。有测试逐字节比对一次性编码结果（含分块边界上的代理对与孤立代理项）。
- `edit` 的行级 diff 从全表 LCS（2 万行两侧 1.6 GB、23 s）换成「公共前后缀 + 两侧唯一行锚点 + 最长递增子序列」，内存与行数线性；2 万行单处替换 24.7 s → 24 ms，patch 只含改动附近的 hunk。
- JSON Schema 的 `$ref` 解析按 schema 根缓存（`ConditionalWeakTable` + `ConcurrentDictionary`），同一工具每次调用的参数校验不再重走 pointer。
- `auth.json` 只读缓存按路径 + mtime + 长度判新鲜，写入路径（`WriteRoot`、`OAuthStore.Write`、`CodexAuth.Save`）显式失效。模型选择器每次打开都问一遍 `ReadApiKeys`，此前每次都是读盘 + 解析。
- 模型目录的 `List` / `ChatModels` / `ImageModels` / `ClassifierModels` / `FindById` 缓存成不可变快照，`Add`/`Remove` 推进版本号后清空；并发读不会把旧快照写回。桌面模型选择器与 `ResolveModel` 不再每次分配新列表。
- 系统提示文本（`GetText(SystemMessage)` 的章节拼接）按实例缓存，用 `ConditionalWeakTable` 而不是 record 字段：`with` 出来的检查点快照不会继承旧文本。每请求省一次几十 KB 的拼接。
- `Transcript.Normalize` 把章节拼进 `Content` 后不再保留 `Sections`：此前 provider 的 `GetInitialSystemText` 会再追加一遍，系统提示整段发两次。
- 纯追加的 `CustomMessageEntry`（子代理回报、关联会话消息、todo brief）进入增量投影，不再每条整段重建投影。
- 重复工具调用签名直接遍历 `JsonNode` 算 FNV，字符串只取前 512 字符加长度：`write` 带 MB 级正文时不再为 64 位签名生成整串。
- 会话事件订阅分两类：外部订阅（UI、RPC 输出）异常隔离并记 `Trace`，内部落盘订阅仍传播异常；一个渲染回调抛错不再让整轮会话失败。

## 为什么不做历史分页

- 桌面打开会话必须拿到全部条目（分支、fork/编辑、搜索、todo、`context_edit` 重放、压缩边界）。
- 字节偏移索引与全量解析成本几乎相等；唯一收益「同文件未变更再打开更快」需要一个只靠索引工作的读取路径，正是被砍掉的分页。
- 条数不是内存上界：199 条短消息 + 1 条 20 MB 工具输出仍是 20 MB；按字节预算切页会破坏「一页 = N 轮」语义。
- 常态打开中位 ~5 ms、p90 80 ms；唯一 1.7 s 的样本是 45 MiB 外部 transcript 导入产物。
- 若将来接入，触发条件应按字节阈值（如 32 MiB）隔离异常，而不是全局替换。

## 为什么行模型不用 union

轨迹面板/时间线的行模型（`LedgerRow`、`TrajectoryLedgerItem`）保持 `class`，不换成 C# 15 的 struct union：

- MewUI 的条目管道是 `object` 的：`IItemsView.GetItem(int)` 返回 `object?`，`IDataTemplate.Bind` 的公共入口接收 `object?`。行对象进列表时就装箱了，换成 struct 只会多箱几次，每次 rebind 都算一次。
- 自注册 `MewProperty<LedgerRow>` 也不行：`PropertyValueStore.Entry` 的 `Value`/`RawValue` 都是 `object?`，任何 `MewProperty<T>` 的 `T` 都会装箱。
- `Tag` 换强类型字段只能省一次属性查找与一次转型，与分配无关；行控件已改用 `LedgerRowView.Row`（`TrajectoryView.cs`）就是这个微清理，不再扩。
- 真要零分配，前提是把 MewUI 的条目管道泛型化（`IItemsView<T>`、模板只走强类型），那是改上游子模块。而账本按 `turnWindow` 分窗、行数几百、每秒重建一次，单行 32 B，收益 ~10 KB/s，不值。
- 这一块真正值得做的是**行视图复用与增量 diff**（重建行表时复用未变的行），不涉及 MewUI 也不涉及 union。

## MewUI 2f6943d 升级记录

- MewUI 子模块 c2cd7ef → 2f6943d（6 个提交）。不改代码就能构建；桌面 1422 项测试通过，全量其余项目通过。
- 已删 `X11WindowMapping`。X11 后端的 `Hide` 改成 `XWithdrawWindow`，`PresentSurface` 对撤销过的窗口重新 `XMapWindow`，并把尺寸提示标成 `USPosition`，让 WM 用窗口自己记住的位置。托盘唤回、二次启动唤回都走这条路径，不再需要应用侧另开 X 连接补一次映射。`tools/desktop-tray-smoke.sh` 通过：关窗即隐藏、后台流式继续、二次启动把隐藏窗口唤回可见。
- 已核对不改代码：`FrameworkElement` 的 arrange 用 `UnclippedDesiredSize` 补回被测量约束裁掉的部分，只在 arrange 槽比测量约束宽时生效；本项目自绘面板都传显式 arrange 矩形，`MarqueeLine` 的 `_lineWidth` 也来自无限宽测量，语义不变。Direct2D 的 `ITargetReadScopeContext`、圆角裁剪下的 backdrop、DWrite 字族回退只让彩色字族吃 emoji 区间，都只走 Windows 后端。macOS 的 `PresentSurface` / `IsHidden` 与 X11 是同一处改动。

## MewUI c2cd7ef 升级记录

- MewUI 子模块 45428e1 → c2cd7ef（11 个提交）。升级后不改项目代码能构建；桌面 1315 项测试通过，全量其余项目通过。
- 已采用：`Application.Create().WithApplicationId(...)`。桌面把 `com.mystivaid.piweft` 交给平台，与 `scripts/package.ps1` 的 `CFBundleIdentifier` 一致；Windows 得到 AppUserModelID，Linux 的 `WM_CLASS` res_class 用它。xprop 实测 `WM_CLASS(STRING) = "dotnet", "com.mystivaid.piweft"`：res_name 是启动进程名，AOT 原生启动时是 `PiWeft`。
- 已核对：X11 `MewVGX11GraphicsFactory` 的 display 关闭通知、macOS MewVG `Clear` 的裁剪修正都落在本项目的调用路径之外；项目没有 `TrayIcon`。
- 已量：AOT 体积见上表，+471,616 B。产物里能看到 `Tmds.DBus.Protocol` 与 `TrayIcon` 符号。

## MewUI 45428e1 升级记录

- MewUI 子模块 553f28d → 45428e1（75 个提交）。升级后不改项目代码就能构建，桌面测试 1237 个全部通过。
- 已采用：`Application.ShutdownRequested`。系统注销/关机时先标位，`MainWindow.OnClosing` 跳过设置页的未保存确认：系统退出不等人，一个确认框会拖住整个会话结束。
- 已核对：SIGTERM 的清理由 MewUI 兜住。X11 后端 `Dispose` 先 `RaiseClosed` 再销毁原生窗口，窗口 `Closed` 里的收尾（中止在途轮次、终端子进程、摘要落盘、窗口几何）照常执行，原生窗口此时仍可读 `Position`；升级前进程被运行时直接终止，这些都不会执行。
- 已修：桌面测试偶发 `VisualTree.Visit` 越界（`SettingsUsageSectionReusesTheSamePanel`，全量约 3 次 1 次）。根因是 `UsageStatsPanel` 在没有 dispatcher 的测试里直接从扫描线程完成 UI 更新，与测试线程的树操作撞上 MewUI 的进程级静态遍历栈。`SettingsView` 现在保存分区后台任务（`WaitForSectionWorkAsync`），测试等它落地再动树；另补程序集级 `CollectionBehavior(DisableTestParallelization = true)` 兜底。连跑 3 次全量 1237 项无失败。
- 未采纳的新能力：`Window.SystemBackdrop` 要求 `AllowsTransparency`，本项目窗口是不透明自绘框，Linux 的 MewVG 后端也不提供材质；`BackdropFilter` 是毛玻璃视觉，DESIGN.md 不用；`TrayIcon` 没有常驻托盘需求；`MultiBinding` 面向多来源复合绑定，本项目 7 处绑定都是单来源；`IGraphicsContext.GlobalAlpha` 改为相对框架 alpha，项目没有直接使用它。
- 已核对仍要保留的项目侧规避：`SvgImageSource.Tint` 只替换 fill，Lucide 是 stroke 图标，仍需加载时预替换 `currentColor`；TextBox 的 Placeholder 聚焦即隐藏，选择器与搜索框仍自绘提示；`ThemeManager` 的 seed 仍禁止在 `Application` 运行后修改。

## 本轮审计记录（2026-11-22）

口径：`dotnet build -p:AnalysisMode=All` 加人工读码；再用 `dotnet-trace`（`dotnet-sampled-thread-time`）与 `dotnet-gcdump` 对照运行时。

- 已修：`UsageStatsPanel` 的完成回调在无 dispatcher 时直接改树（测试竞态根因）。详见上方升级记录。
- 已修：7 处写配置的 `new JsonSerializerOptions { WriteIndented = true, Encoder = ... }` 改为共享 `PiJson.IndentedNodeOptions`。`JsonSerializerOptions` 构造要初始化转换器与缓存，每次写盘重建是白花；`ProjectTrust` 顺带从默认编码器换成可读转义，中文路径不再写成 `\uXXXX`。
- 已补：`ShellRunner` 最后一个 `DllImport` 换 `LibraryImport`（AOT 源生成，与其余 53 处一致）；`PumpAsync` 的局部锁换成 `Lock`，顺带消除 `CS9216` 装箱警告。
- 已补：`ProcessPrimarySelection.Dispose` 取消后释放 `_shutdown`；两处 `TaskCompletionSource` 加 `RunContinuationsAsynchronously`，continuation 不再内联在 dispatcher 回调里。
- 已删：`SessionTab.ComposerAttachments`（无调用方）。
- 采样结论：`--ui-smoke stream` 下应用侧函数只占主线程 0.3%，`SessionTab.Render` 之外全是 MewUI 的 Measure/Arrange（7.5% / 4.3%）与 MewVG 呈现；宿主的 `WaitForFrameBudget` + `WaitForWorkOrEvents` 占 34%。应用侧优化空间已经不在渲染路径。
- 堆对比：30 秒流式前后 20.6 → 20.9 MiB（+326 KiB），增长是字形/布局缓存与图标缓存；`HoverHints`、`TextFocusScrollGuard` 的按键都是 `ConditionalWeakTable`，无泄漏。
- 已核对仍不修：`CodingAgentSession._runAbort/_retryAbort` 由 `using` 释放（CA2213 误报）；`McpClient._output` 归 `McpProcess` 所有；`McpConnection._refreshGate` 等 `SemaphoreSlim` 无 `AvailableWaitHandle`，不 Dispose 不泄露；`ShellRunner` 每 25 ms 的 `Task.Delay` 是轮询空闲检测，换 `PeriodicTimer` 不减少分配。

## 本轮审计记录（MewUI 553f28d 之后）

只列有代码证据的结论。口径：`dotnet build -p:AnalysisMode=All` 加人工读码，再用 `--ui-smoke stream` + 诊断 `alloc/frame` 对照。

- 已修：流式增量解析的文档簿记每帧按整篇重建（文本单元下标字典、锚点表复制）。见上方「已落地的热路径」前四条。
- 已核对无问题：会话追加在 `_stateGate` 内写文件并在失败时回滚；`Utf8LineReader` 的零拷贝快路径与池化上限；`grep`/`find` 的字节扫描、`SearchValues` 预筛、mmap 阈值与上限即停；SSE 复用字节缓冲、每事件只留一份 `JsonDocument`；`Agent._listeners` 写时复制 + `volatile`；`CodingAgentSession` 的 run CTS 由 `using` 持有；诊断帧档/计数档常态零分配（有测试守着）。
- 分析器全量模式共 7571 条，逐类看过：`CA2000` 的 `StringContent`/`FormUrlEncodedContent` 是 GC 压力不是句柄泄漏（冷路径）；`CA2213` 命中的 UI 控件由可视树释放；`CA2012` 的 `List<ValueTask>` 是「收集后各 await 一次」的合法用法；`CA1508` 在 `union` 模式匹配上误报（`SessionLine` 的 `is` 分支被当成必真）。没有发现功能 bug。
- 没有采纳：`MewUITouch`/`MewUIEnvironmentDebugLogging` 等开关的裁剪收益未测，改行为不值；条目管道泛型化是改上游，理由见上一节。

## 本轮审计记录（2026-11-23）

口径：`dotnet build -p:AnalysisMode=All` 与人工读码；运行时用 `--ui-smoke tray` / `tray-idle` 的隐藏阶段读 `/proc/<pid>/stat` 的 utime+stime（4 秒窗口），AOT 发布版复测。

- 已修：隐藏到托盘后 CPU 99% 的根因是思考预览的 `AnimationClock`（`StreamPump` 逐字泵、`MarqueeLine` 扫光）只判断节点可见/在视口，不知道窗口已隐藏；MewUI 宿主因此持续渲染。`StreamPump.HiddenByWindow` 标记挂到 `SessionTab` 根，`SetActive`（后台标签）与 `SetWindowVisible`（隐藏/最小化）统一更新；排队中的 `_renderTimer.Start` 与 `Render()` 里的 `_busyTimer` 再判一次 `_windowVisible`，隐藏瞬间的在途 delta 不会把帧泵点着。`--ui-smoke tray` 隐藏阶段 3.97 s/4 s → 0.20 s/4 s（AOT 0.17 s），流式输出继续；`tray-idle` 隐藏 0.24 s/4 s，说明不是 MewVG 空转。
- 已修：单实例第二实例退出时无条件删 socket 文件（`Dispose` 不看是否持有监听 socket），下一次启动会把它当残留，同一项目能开出第二个实例。现在只有持有监听 socket 的首实例删除；测试覆盖第二实例退出后文件仍在。
- 已修：单实例监听循环把 `ReceiveAsync`/`onActivate` 放在 try 外，一次客户端 RST 或 dispatcher 已停就让激活通道永久失效。整轮包 try/catch，取消/释放退出，其它异常记日志后继续；测试用 `LingerState(0)` 造 RST。
- 已修：`desktop.json` 直接覆盖写，进程在写盘中途被杀会留下半截 JSON，下次 Load 回退默认值（窗口几何、项目、置顶、快捷消息一起丢）。改成同目录 `.tmp` + `File.Move(overwrite)`；测试断言无残留且可解析。
- 已修：设置页关窗确认只捕 `InvalidOperationException`，其它异常让窗口带着未保存草稿关闭且 `_exitRequested` 不复位。现在任何异常都按取消处理并记日志。
- 已核对未修：`LiveActivityClock`/`LiveDurationClock` 在隐藏时仍每 100 ms/1 s 改文本（剩余约 5% CPU 的一部分）；`AgentLoop` 单条消息内的工具并发无上限；`CloseTabAsync` 仍是 fire-and-forget。三项收益小或改动面大，暂留。

## 诊断面板

设置 → 维护 → 性能诊断，默认关闭；关闭时调用点只做一次布尔判断。打开后实时区每秒原地更新文本与帧条，不重建控件树；面板摘下或暂停刷新后脉冲停止。按钮提供暂停/继续、刷新、压堆、清空、打开目录与导出 Chrome Trace。

- 帧档用「应用侧重绘请求 → `FrameRendered`」量本帧成本，不用两次呈现之间的空闲间隔：`SessionTab.Render`、流式打字机、平滑滚动、输入都会标记请求，请求之前的空闲被切掉。MewUI 源码不需要改动；覆盖不到的框架内部重绘（悬停、框架动画）间隔 <50 ms 时按帧成本算，≥50 ms 时按空闲处理。`≥400 ms` 的停顿单独计数，不混入帧成本。
- 掉帧阈值取目标帧率；未设置时用连续循环的帧间隔中位数，且只接受 4–40 ms、吸附到 144/120/90/60/30 Hz 的常见刷新率，偏离太远回退 60 fps。不再固定 20 ms；长帧仍是 ≥50 ms 的独立阈值。
- 产出窗口 p50/p95/max、掉帧/长帧/停顿、每帧分配、GC 暂停占比、输入→绘制、draw/cull/text/shape/image/clip；`runtime.window` 同时写窗口值（`frames`/`window.fps`/`p50`/`p95`/`dropped`/`long`/`stall`/`alloc/frame`/`gc`）和 `total.*` 累计值，文件里既有逐秒曲线也能看整个采集期峰值。
- 输入采样：键盘用 `Window.PreviewKeyDown`；指针/滚轮除窗口冒泡点外，还会话视口、阅读守卫、输入区、发送键、标签页、会话行、侧栏滚动各记一次，覆盖主要交互；更深的按钮点击可能收不到样本，这是不改框架前提下的已知限制。一帧内保留最早的待处理输入，记最坏等待。
- 计数档给每帧路径（`row.build`/`row.release`/`row.inplace`/`render.stats`/`timeline.realize`/`ui.render`）；`Slow` 的 detail 延迟到超过阈值才格式化，流式 Markdown 追加只计数 + 慢样本，不再每帧写明细。
- 每秒采样工作集/私有内存/托管堆/LOH/POH/碎片/累计分配/GC 次数/CPU/提交内存/pinned/待终结/线程池；CPU 用 `TimeSpan.TicksPerMillisecond` 换算（`Process.TotalProcessorTime` 是 100 ns tick，不是 Stopwatch tick）。
- 落盘 `<agentDir>/diagnostics/timings-<时间戳>-<pid>-<序号>.jsonl`：首行 `diagnostics.meta` 带 schema 版本/运行时/OS/架构/核数，每条记录带 `kind/cat/tid/v`。16 MiB 轮转、保留最近 12 份/7 天；队列有上限，写盘跟不上时丢弃并计数，不阻塞 UI。
- 明细档只给低频或超阈值样本；单条 ≥200 ms 记 `Slow path`。两种导出：`trace-*.json`（Chrome Trace Event Format）可直接打开 Perfetto / Chrome DevTools，Firefox Profiler 也能导入为 marker；`profile-*.json`（Gecko profile v37，已用 Firefox Profiler 的真实导入器验证）在 profiler.firefox.com 打开，span 是 marker、逐秒指标是 counter 轨道，与 Ultra 的 trace 用同一个查看器。counter 的 `count` 按「值 × 间隔秒数」编码，让 line-rate 画出来等于指标值。speedscope 读的是 Chrome DevTools CPU profile，不是 Chrome Trace Event Format，不能直接打开。
- 诊断不改 `external/MewUI` 源码：帧起点来自应用侧请求，指针采样靠已有事件与各交互宿主的取样点；`scripts/mewui-source.sh` 可随时把子模块重置回上游。
- Debug 下 MewUI DevTools（`Ctrl+Shift+P` 帧叠层、`Ctrl+Alt+Shift+P` profiler、`Ctrl+Shift+T` 视觉树、`Ctrl+Shift+I` 拾取）可用；AOT 发布会折叠。CPU 采样、堆转储、ETW/perf 仍交给外部工具；发布版已开 EventPipe，`dotnet-gcdump` 的堆分析在 NativeAOT 下不可用。

- 计划/目标解析按行切片：`MarkdownScan.Lines` 用 `ref struct` 枚举 `ReadOnlySpan<char>`，长回复不再 `Split('\n')` 为每行分配字符串；编号行、`[DONE:n]` 与终态标记手工扫描，只有真正成为步骤的文本转成 string。
- 工具守卫与 codemode 嵌套调用按 `FrozenDictionary` 查工具名，不再每次线性扫工具表；计划步骤统计一次循环，widget 行用固定数组，不再每次 Linq 委托与 List 扩容。
## 复现

```sh
dotnet run --project tools/PiWeft.SessionBench
xvfb-run -a dotnet test PiWeft.slnx
xvfb-run -a dotnet test tests/PiWeft.Desktop.Tests --filter FullyQualifiedName~ConversationPerformanceTests --logger 'console;verbosity=detailed'
PI_WORKSPACE_SMOKE_APP="$PWD/artifacts/desktop-optimized-linux-x64/PiWeft" \
  xvfb-run -a -s '-screen 0 1600x1000x24' python3 tools/desktop-workspace-smoke.py 0 4 9 10 11
```

`--ui-smoke stream` 每秒打印工作集/托管堆/渲染缓存，是流式内存曲线的固定入口；`PI_WORKSPACE_SMOKE_TURNS=0` 使用原工具 fixture。

## 外部 CPU 采样

自带诊断是应用级 span：能看到「打开会话 884 ms」「这一帧 41 ms」「工具执行 8 s」，看不到「CPU 时间花在哪个函数/栈」。后者交给外部采样器，三者互补：

- `ultra profile --pid <pid> --duration 30`（xoofx/ultra，采样 profiler，输出 Firefox Profiler 格式）。Linux 需要 root 或 `kernel.perf_event_paranoid ≤ 0`；Windows 需要管理员（或 `--elevate`）；macOS 仅 Apple Silicon 且只能 launch。它的 trace 与本项目导出的 `profile-*.json` 在同一个查看器（profiler.firefox.com）里打开，可以对照「自带 span 慢在哪一段」和「CPU 花在哪个栈」。
- NativeAOT 下 Linux 的 perf 采样能看到 native 栈，但 AOT 代码没有 JIT map，.NET 方法名解析有限；这时它更适合定位渲染/原生库热点。
- `dotnet-trace collect -p <pid> --profile dotnet-sampled-thread-time` 是另一条不依赖 Ultra 的路径；发布版已开 `EventSourceSupport`，可以直接附着。
- 这些都不作为运行依赖、不打包进发布物；给出 PID 与权限即可复现。
