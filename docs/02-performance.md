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
- 历史变更按尾部替换、追加、删除通知，保留既有消息与高度缓存；流式消息落盘仍原位替换正文为 Markdown。
- 分支面板首次打开时才构建完整树。会话文件已打开时直接选中标签，不再先重复读取 JSONL；启动目录复用已有环境。
- 窄屏输入框由 96 DIP 缩为 56 DIP，保留草稿和选区。追随最新消息使用滚动偏移，避免将高于视口的整条回复反复滚入视图。

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
- 虚拟化回收后恢复两级折叠和工具明细状态；释放资源时也处理已经折叠、没有出现在可见树里的内容。
- `NativeHoverTests` 在独立 Xvfb 中驱动实际鼠标，检查延迟、顶部和底部提示、移出与滚动关闭，以及执行过程开合。
- 会话统计与余额配置使用原生 `Border`、`DockPanel`、`ScrollViewer` 分配剩余高度，不在 `MeasureContent` 中修改 `MaxHeight` 等布局属性。
- 余额请求在启动时捕获 UI Dispatcher，返回结果经该 Dispatcher 更新控件；表单和统计行重建时显式释放旧控件。
- 会话阅读区统一抑制文本、折叠栏及按钮获得焦点时触发的外层滚动。手动开合执行过程、过程组、工具和思考时暂停底部追随，在布局收敛后恢复被点击标题的屏幕锚点。
- 差异正文使用一份只读 `MultiLineTextBox` 与 `ITextClassifier`，可跨增删行拖选、复制；行号置于不可命中的独立文本列。普通滚轮由会话处理，Shift+滚轮浏览长行。
- 图片预览按图像纵横比居中布局，图片之外的留白命中遮罩并关闭；图片内部点击不关闭。
- Markdown 文件链接按会话目录解析相对路径，支持绝对路径、`file://`、`~/`、URL 编码以及 `:行:列` / `#L行C列`。文件交给编辑器、目录交给默认程序，错误在状态栏显示；拒绝非 HTTP/HTTPS/file 协议和远程文件共享。

## 验证与复现

```sh
xvfb-run -a dotnet test PiWeft.slnx
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
