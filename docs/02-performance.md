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
| 32 K 尾部追加 | 中位 0.7–2 ms，p90 1–4 ms，~0.1 MiB/帧 |
| 24 K 单段落逐段追加 | 中位 4–7 ms，p90 8–15 ms，~0.5 MiB/帧 |
| 32 K 单段落一次性全量布局 | ~79 ms（历史消息首次进入的一次性成本） |
| 纯文本追加 32 K | 全量替换 42.5 ms → 增量追加 0.78 ms |
| 1000 轮会话冷启动局部 | Markdown 视图 1000 → 3；累计分配 253.6 MB → 5.3 MB；4445 ms → 767 ms |
| 10,000 条追加 2 条后投影 | 4.13 ms / 1.26 MB → 0.016 ms / 9 KB |
| 1000 轮追加一条 user 后重建 | 2.53 ms / 1.25 MB → 0.25–0.6 ms / 36 KB |
| 2,000 行流式过程投影 | 1.15 ms / 233 KB → 0.07–0.12 ms |
| 40 会话侧栏冷扫描 | 39–67 ms / 34.4 MB → 19–24 ms / 0.26 MB |
| SSE 事件解析 | 192 B/事件 → 72 B/事件，快 15–20% |
| NativeAOT 桌面体积（linux-x64，当前提交） | 75.8 MiB；开启 `EventSourceSupport` 77.4 MiB（+1.6 MiB / +2.1%，换来 `dotnet-counters`/`dotnet-trace` 可附着）。旧记录 62.4 MB / 64.0 MB 来自更早构建，需按当前提交重测 |

内存读法：工作集包含字体映射、GPU/驱动共享表面与保留合成；判断泄漏看趋势与 `runtime.window` 的 `rtext/rgeom/ratlas/rpersist/rscratch/rdecoded/rbitmap/rvector/rencoded` 台账，再用 `dotnet-counters`/`dotnet-trace` 与 `smaps_rollup` 对照。发布版已打开 NativeAOT 的 `EventSourceSupport`（实测 +1.6 MiB），`dotnet-counters collect` 已验证可附着；但 NativeAOT 仍不支持 `dotnet-gcdump` 的堆分析，托管堆趋势以自带窗口值为准。打开含大量截图的会话会一次性抬高工作集，不属于逐帧增长。

## 已落地的热路径

- 侧栏摘要持久化到 `<agentDir>/session-summaries.json`，键为长度 + mtime + 首行指纹；追加时用尾巴指纹续扫，原地重写则整扫。缓存损坏退化为全扫；删除会话的条目下次构建时清除。
- 会话解析用 `Utf8LineReader` + `SessionJson.ParseLine(ReadOnlySpan<byte>)`；大文件按 2–3 段并行，顺序与单线程一致；进度按字节汇报。`Utf8LineReader` 对超大行缓冲（>256 KiB）交给 GC，不归还共享池。
- 会话 JSONL 追加用 `File.AppendAllBytes` + 可复用 `ArrayBufferWriter<byte>`；SSE 每流复用 UTF-8 缓冲。
- 分支快照用不可变列表结构共享；`BuildProjectedMessages()` 在纯追加时复用前缀；`ChatViewModel.Rebuild()` 只重算最后一个用户轮次；过程投影只重投当前轮；统计只消费新增条目。
- 流式正文/思考改为追加式 `StreamedText` 快照，长度与行相等判定 O(1)，整篇物化只在复制/导出发生。
- Markdown 解析按尾块增量，块级模型哈希做前后缀 diff；段落按已闭合行分块，只重排开放尾块；代码块 `ReplaceRange`；定稿只切 `IsStreaming`。
- 虚拟化只建视口附近控件；离屏释放正文/Markdown/图片，保留展开状态；分支树延迟构建；Replace 只覆盖变化的连续行段。
- 平滑滚动按帧合并输入、Hermite 续接、设备像素提交；虚拟化高度校正时平移动画目标，避免「滚很多却回退」。
- 会话时间线与执行过程组体共用 PiWeft 自己的虚拟化面板（`VirtualRowsPanel`）：每行独立估算高度、维护前缀和与阅读锚点，不再依赖 MewUI presenter 的全局运行平均；前缀和只在行数/高度/宽度变化时重算（滚动通知里的阅读点捕获是 O(log n)），高度缓存与快照缓冲按几何增长复用，行回收遍历零分配。结构变化（插入/删除/过程组重排）按顶部行身份补偿坐标系平移（跟底时让位给追末尾）；extent 收缩把 reader 夹到新末尾后，再用行内偏移映射回原处。
- 过程组跟底由几何决定；展开工具行用步骤索引锚定预览行，虚拟化换宿主也不失效；流式思考只续跟、不反复锁底。
- 图片/文件面板、Git 状态/图/分支、检查点列表放后台线程，generation 丢弃过期结果；面板按节独立宿主，选行只换 diff。
- 图片管线（`PiWeft.Imaging`）按「解码一次、重采样一次、编码候选最小者」组织：限制内的图只做解码校验后原样返回（1920×1080 ≈ 15 ms）；超限图应用 EXIF 方向后 Lanczos3 重采样，PNG 与 JPEG 质量阶梯里取进预算的最小者，仍超再按 0.75 递减。重采样器是分离式两遍：水平方向「源驱动散射」（每个源像素只预乘一次，按反向权重写目标行）+ 垂直方向 `Vector<float>` 累加，分带相互独立并行；`ArrayPool` 复用行缓冲、`ref` 局部写避开边界检查、不透明图走无预乘快路径。12 MP（4032×3024 → 2000×1500）端到端从 ~2.4 s 降到 ~0.62 s（其中重采样 1.9 s → 0.33 s，8 核），2560×1440 ≈ 0.25 s，1920×1080 只做解码校验 ≈ 14 ms。方向变换按 `uint`（RGBA 四字节）整体搬，并给 8 个方向各写一条专用循环。附件归一化在线程池上跑，`read` 工具也把读文件与解码移出 UI 线程。
- 诊断分帧档（约百纳秒/0 B）、计数档（30–50 ns/0 B）、明细档（约 2 µs/~870 B）；`Slow` 的 detail 延迟到超过阈值才格式化，流式追加只计数+采慢样本。空闲也写 `runtime.window`，便于判断堆趋势。
- 滚动/首次打开复杂会话的剩余长帧主要来自框架的文本重录、交换缓冲与 GL 上下文初始化，应用侧不再收窄。

## 为什么不做历史分页

- 桌面打开会话必须拿到全部条目（分支、fork/编辑、搜索、todo、`context_edit` 重放、压缩边界）。
- 字节偏移索引与全量解析成本几乎相等；唯一收益「同文件未变更再打开更快」需要一个只靠索引工作的读取路径，正是被砍掉的分页。
- 条数不是内存上界：199 条短消息 + 1 条 20 MB 工具输出仍是 20 MB；按字节预算切页会破坏「一页 = N 轮」语义。
- 常态打开中位 ~5 ms、p90 80 ms；唯一 1.7 s 的样本是 45 MiB 外部 transcript 导入产物。
- 若将来接入，触发条件应按字节阈值（如 32 MiB）隔离异常，而不是全局替换。

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

## 复现

```sh
dotnet run --project tools/PiWeft.SessionBench
xvfb-run -a dotnet test PiWeft.slnx
xvfb-run -a dotnet test tests/PiWeft.Desktop.Tests --filter FullyQualifiedName~ConversationPerformanceTests --logger 'console;verbosity=detailed'
PI_WORKSPACE_SMOKE_APP="$PWD/artifacts/desktop-optimized-linux-x64/piweft-desktop" \
  xvfb-run -a -s '-screen 0 1600x1000x24' python3 tools/desktop-workspace-smoke.py 0 4 9 10 11
```

`--ui-smoke stream` 每秒打印工作集/托管堆/渲染缓存，是流式内存曲线的固定入口；`PI_WORKSPACE_SMOKE_TURNS=0` 使用原工具 fixture。

## 外部 CPU 采样

自带诊断是应用级 span：能看到「打开会话 884 ms」「这一帧 41 ms」「工具执行 8 s」，看不到「CPU 时间花在哪个函数/栈」。后者交给外部采样器，三者互补：

- `ultra profile --pid <pid> --duration 30`（xoofx/ultra，采样 profiler，输出 Firefox Profiler 格式）。Linux 需要 root 或 `kernel.perf_event_paranoid ≤ 0`；Windows 需要管理员（或 `--elevate`）；macOS 仅 Apple Silicon 且只能 launch。它的 trace 与本项目导出的 `profile-*.json` 在同一个查看器（profiler.firefox.com）里打开，可以对照「自带 span 慢在哪一段」和「CPU 花在哪个栈」。
- NativeAOT 下 Linux 的 perf 采样能看到 native 栈，但 AOT 代码没有 JIT map，.NET 方法名解析有限；这时它更适合定位渲染/原生库热点。
- `dotnet-trace collect -p <pid> --profile dotnet-sampled-thread-time` 是另一条不依赖 Ultra 的路径；发布版已开 `EventSourceSupport`，可以直接附着。
- 这些都不作为运行依赖、不打包进发布物；给出 PID 与权限即可复现。
