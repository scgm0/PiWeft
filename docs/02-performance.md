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
| NativeAOT 桌面体积 | 62.4 MB；`OptimizationPreference=Speed` 64.0 MB（+2.6%） |

内存读法：工作集包含字体映射、GPU/驱动共享表面与保留合成；判断泄漏看趋势与 `runtime.window` 的 `rtext/rgeom/ratlas/rpersist/rscratch/rdecoded` 台账，再用 `dotnet-counters`/`dotnet-gcdump` 与 `smaps_rollup` 对照。打开含大量截图的会话会一次性抬高工作集，不属于逐帧增长。

## 已落地的热路径

- 侧栏摘要持久化到 `<agentDir>/session-summaries.json`，键为长度 + mtime + 首行指纹；追加时用尾巴指纹续扫，原地重写则整扫。缓存损坏退化为全扫；删除会话的条目下次构建时清除。
- 会话解析用 `Utf8LineReader` + `SessionJson.ParseLine(ReadOnlySpan<byte>)`；大文件按 2–3 段并行，顺序与单线程一致；进度按字节汇报。`Utf8LineReader` 对超大行缓冲（>256 KiB）交给 GC，不归还共享池。
- 会话 JSONL 追加用 `File.AppendAllBytes` + 可复用 `ArrayBufferWriter<byte>`；SSE 每流复用 UTF-8 缓冲。
- 分支快照用不可变列表结构共享；`BuildProjectedMessages()` 在纯追加时复用前缀；`ChatViewModel.Rebuild()` 只重算最后一个用户轮次；过程投影只重投当前轮；统计只消费新增条目。
- 流式正文/思考改为追加式 `StreamedText` 快照，长度与行相等判定 O(1)，整篇物化只在复制/导出发生。
- Markdown 解析按尾块增量，块级模型哈希做前后缀 diff；段落按已闭合行分块，只重排开放尾块；代码块 `ReplaceRange`；定稿只切 `IsStreaming`。
- 虚拟化只建视口附近控件；离屏释放正文/Markdown/图片，保留展开状态；分支树延迟构建；Replace 只覆盖真正变化的连续行段。
- 平滑滚动按帧合并输入、Hermite 续接、设备像素提交；虚拟化高度校正时平移动画目标，避免「滚很多却回退」。
- 过程组跟底由几何决定；展开工具行用步骤索引锚定预览行，虚拟化换宿主也不失效；流式思考只续跟、不反复锁底。
- 图片/文件面板、Git 状态/图/分支、检查点列表放后台线程，generation 丢弃过期结果；面板按节独立宿主，选行只换 diff。
- 诊断分帧档（~66 ns/0 B）、计数档（30–50 ns/0 B）、明细档（2.1–2.7 µs/~870 B）；空闲也写 `runtime.window`，便于判断堆趋势。
- 滚动/首次打开复杂会话的剩余长帧主要来自框架的文本重录、交换缓冲与 GL 上下文初始化，应用侧不再收窄。

## 为什么不做历史分页

- 桌面打开会话必须拿到全部条目（分支、fork/编辑、搜索、todo、`context_edit` 重放、压缩边界）。
- 字节偏移索引与全量解析成本几乎相等；唯一收益「同文件未变更再打开更快」需要一个只靠索引工作的读取路径，正是被砍掉的分页。
- 条数不是内存上界：199 条短消息 + 1 条 20 MB 工具输出仍是 20 MB；按字节预算切页会破坏「一页 = N 轮」语义。
- 常态打开中位 ~5 ms、p90 80 ms；唯一 1.7 s 的样本是 45 MiB 外部 transcript 导入产物。
- 若将来接入，触发条件应按字节阈值（如 32 MiB）隔离异常，而不是全局替换。

## 诊断面板

设置 → 维护 → 性能诊断，默认关闭；关闭时调用点只做一次布尔判断。打开后每秒刷新实时区（概览、帧时间、渲染循环、内存/GC、计数、聚合、最近明细），静态部分不动；面板摘下后脉冲停止。

- 帧档接 `Window.FrameRendered` / `LastFrameStats`，产出 p50/p95/最大、FPS（中位帧间隔）、掉帧/长帧、每帧分配、GC 暂停占比、输入→绘制延迟与 draw/cull/text/shape/image/clip 计数；>400 ms 的间隔记为停顿，不混入帧样本。
- 计数档给每帧路径（`row.build`/`row.release`/`row.inplace`/`render.stats`/`timeline.realize`/`ui.render`）；明细档只给低频或超阈值样本，逐条写 JSONL。
- 每秒采样工作集/私有内存/托管堆/碎片/累计分配/GC 次数/CPU/提交内存/pinned/待终结/线程池，并从池线程向 UI 投递探针测排队延迟。
- 落盘 `<agentDir>/diagnostics/timings-<时间戳>.jsonl`，250 ms 一批 flush；单条 ≥200 ms 记 `Slow path`。
- FPS 按帧间隔中位数换算，渲染循环是按请求重绘；面板同时显示「连续/按请求 · VSync · 目标帧率」与每秒帧数。
- Debug 下 MewUI DevTools（`Ctrl+Shift+P` 帧叠层、`Ctrl+Alt+Shift+P` profiler、`Ctrl+Shift+T` 视觉树、`Ctrl+Shift+I` 拾取）可用；AOT 发布会折叠。CPU 采样、堆转储、ETW/perf 交给外部工具。

## 复现

```sh
dotnet run --project tools/PiWeft.SessionBench
xvfb-run -a dotnet test PiWeft.slnx
xvfb-run -a dotnet test tests/PiWeft.Desktop.Tests --filter FullyQualifiedName~ConversationPerformanceTests --logger 'console;verbosity=detailed'
PI_WORKSPACE_SMOKE_APP="$PWD/artifacts/desktop-optimized-linux-x64/piweft-desktop" \
  xvfb-run -a -s '-screen 0 1600x1000x24' python3 tools/desktop-workspace-smoke.py 0 4 9 10 11
```

`--ui-smoke stream` 每秒打印工作集/托管堆/渲染缓存，是流式内存曲线的固定入口；`PI_WORKSPACE_SMOKE_TURNS=0` 使用原工具 fixture。
