# 桌面渲染性能

主题原位换色、可变高度消息虚拟化、延迟构建分支树，以及 PiDeck 风格的轻量侧栏、消息卡片、空会话页与输入区均已实现。实现见 `ConversationTimeline.cs`、`SessionTab.cs`、`AppStyles.cs` 和 `MainWindow.cs`。

## 主题切换

消息卡片和工具背景由主题样式解析，思考标题使用主题回调。切换深浅主题保留 Markdown 实例、消息选区、工具展开内容、输入草稿与光标，不再重建全部消息或解析整段 Markdown；分支树和 Git 不再因为换肤而重读数据。

框架文本角色与图标仍刷新颜色，但消息子树只包含视口附近的控件。真实窗口主题演练增加了用户消息、Markdown/代码、思考和工具输出 fixture，验证换色后的控件身份与选择状态。一次 Debug 原生窗口演练测得切换调用约 **42.7 ms**；这是局部观测，不是所有硬件或所有标签数量的保证。

## 会话打开与内存

- 消息使用 MewUI 0.22.1 的可变高度 `ItemsControl`，只为可见范围与少量预加载行创建正文、Markdown、图片和工具控件。
- 滚出视口后释放消息内容，回收池只保留小型容器；再次浏览时恢复轻量的工具/思考展开状态。
- 模板的 unbind 也发生在同一控件重新绑定前，因此在布局结束后清理真正已脱离视口的内容，保留流式更新期间的选区与控件。
- 历史变更按尾部替换、追加、删除通知，保留既有消息与高度缓存；流式消息落盘仍原位替换正文为 Markdown。
- 分支面板首次打开时才构建完整树。会话文件已打开时直接选中标签，不再先重复读取 JSONL；启动目录复用已有环境，配置重载后也复用新环境。
- 窄屏输入框由 96 DIP 缩为 56 DIP，保留草稿和选区。追随最新消息使用滚动偏移，避免将高于视口的整条回复反复滚入视图。

以下是相同 1000 轮、2000 条消息 fixture 的局部冷启动观测，均包含 `SessionTab` 构造及 960×720 控件布局，不包括 JSONL 文件解析、模型环境初始化或网络请求。优化后的驱动额外执行布局收敛轮次。单次耗时包含 JIT 与文本后端初始化，不应视为稳定平均值。

| 指标 | 优化前 | 优化后 |
|---|---:|---:|
| 同时构造的 Markdown 视图 | 1000 | 3 |
| 当前线程累计托管分配 | 253,602,008 bytes | 5,347,040 bytes |
| 构造与布局耗时 | 4445.66 ms | 767.17 ms |

**累计分配不是存活堆大小，也不是整进程 RSS。** 会话核心仍保留 JSONL 历史及模型上下文，尚未实现历史文件分页；优化限制的是昂贵的 UI 控件、Markdown 与图片布局数量。

Linux NativeAOT 发布版、Xvfb/Mesa、1280×820 的实际会话演练中：60 条工具输出 fixture 的 RSS 约 208.4 MiB，1000 轮 / 2001 条消息 fixture 的 RSS 约 211.2 MiB。二者内容不同，这组数字只记录优化后的实测占用，不构成优化前后的 RSS 比较；字体和图形后端也计入进程占用。

## Tooltip 与执行过程

- Tooltip 保留 350 ms 延迟，通过 `Popup.ShowAt` 展示，内容测量、原生表面与边界翻转交给 MewUI。锚点直接使用窗口绝对 `Bounds`，不累加父级坐标，不把提示内容放入 `OverlayLayer`。
- 每轮一个执行过程汇总，历史默认折叠；思考和连续工具调用组成次级折叠组，中间回复按原顺序穿插，末尾正文与图片保持常显。流式轮自动展开最新过程组，尊重手动开合状态。
- 工具调用与结果通过调用 ID 合并，只计数一次，同时保留输入参数与输出。工具正文与 diff 仍首次展开时才创建。
- 虚拟化回收后恢复两级折叠和工具明细状态；释放资源时也处理已经折叠、没有出现在可见树里的内容。
- `NativeHoverTests` 在独立 Xvfb 中驱动实际鼠标，检查延迟、顶部和底部提示、移出与滚动关闭，以及执行过程开合；启用方式见下方。
- 会话统计与余额配置使用原生 `Border`、`DockPanel`、`ScrollViewer` 分配剩余高度，不在 `MeasureContent` 中修改 `MaxHeight` 等布局属性。专项测试检查测量不产生属性副作用，窄窗口中滚动区与固定操作区不重叠。
- 余额请求在启动时捕获 UI Dispatcher，返回结果经该 Dispatcher 更新控件；表单和统计行重建时显式释放旧控件。

```sh
PIWEFT_NATIVE_TESTS=1 PIWEFT_SCREENSHOTS=/tmp/piweft-ui-validation \
  xvfb-run -a -s '-screen 0 1440x1000x24' \
  dotnet test tests/PiWeft.Desktop.Tests --filter FullyQualifiedName~NativeHoverTests
```

- 会话阅读区统一抑制文本、折叠栏及按钮获得焦点时触发的外层滚动，保留焦点、选区和键盘操作。MewUI 0.22.1 没有公开关闭焦点滚动的开关，应用在焦点事务中同步恢复偏移，不反射访问框架内部接口。
- 手动开合执行过程、过程组、工具和思考时暂停底部追随，在布局收敛后恢复被点击标题的屏幕锚点；不会把“展开了长输出”误判成需要滚到最终回答。恢复与追随回调使用 `DispatcherPriority.Render`，低于 `Layout`、在绘制前执行；不能使用默认 `Normal` 提前清除锚点。
- 差异正文使用一份只读 `MultiLineTextBox` 与 `ITextClassifier`，可跨增删行拖选、复制；行号置于不可命中的独立文本列，不混入复制内容。普通滚轮由会话处理，Shift+滚轮浏览长行。
- 图片预览按图像纵横比居中布局，图片之外的留白命中遮罩并关闭；图片内部点击不关闭。
- Markdown 文件链接按会话目录解析相对路径，支持绝对路径、`file://`、`~/`、URL 编码以及 `:行:列` / `#L行C列`。文件交给编辑器、目录交给默认程序，错误在状态栏显示；拒绝非 HTTP/HTTPS/file 协议和远程文件共享。
- `NativeMessageInteractionTests` 使用真实鼠标验证长差异的开合锚点、跨行选区、图片留白关闭和路径链接启动隔离的模拟编辑器，不访问真实配置或供应商账户。

```sh
PIWEFT_NATIVE_TESTS=1 PIWEFT_SCREENSHOTS=/tmp/piweft-interaction-validation \
  xvfb-run -a -s '-screen 0 1440x1000x24' \
  dotnet test tests/PiWeft.Desktop.Tests --filter FullyQualifiedName~NativeMessageInteractionTests
# Set PIWEFT_INTERACTION_APP to a NativeAOT executable to exercise the published app.
```

## 增量投影与会话缓存（2026-10-19）

长会话的瓶颈主要不在 Markdown 控件，而在每次消息结束时重新映射整段历史：

- `SessionManager.GetBranch()` 用不可变列表缓存当前分支快照；普通追加只做结构共享的 `Add`，不再每次重建 O(n) 路径。
- `BuildProjectedMessages()` 在分支只追加消息时复用上一次投影，只把新增条目接到尾部；上下文编辑、压缩、分支摘要和自定义消息仍走完整重建。`CodingAgentSession.Project()` 按 revision 缓存带系统提示词的投影，避免每个消费方重复复制列表。
- `ChatViewModel.Rebuild()` 只重算最后一个 user 轮次；新增 user 轮次时直接保留旧行，上下文编辑/分支切换才全量重建。`_entryIds` 也按分支尾部增量维护。
- `ExecutionProcess.Project()` 的流式路径改用 `ExecutionProcessProjector`：历史轮次的折叠结果保持不变，只重投影当前轮。`Summary()`/`GroupSummary()` 由多次 LINQ 扫描改为单遍计数。
- Provider 的 `OnProviderStreamEvent` 回调改为只在有订阅者时才解析 `JsonNode`；此前 `?.Invoke(JsonNode.Parse(...))` 会为每个 SSE delta 白做一次 DOM 解析，即使回调为 null。
- 会话 JSONL 追加改用 .NET 10 的 `File.AppendAllBytes(ReadOnlySpan<byte>)` 和可复用的 `ArrayBufferWriter<byte>`，不再为每条消息生成 UTF-16 字符串再编码回 UTF-8。
- 会话目录扫描改用 `Utf8LineReader` + `Utf8JsonReader`：直接按 UTF-8 行读文件，只取 type/cwd/name/role 与首条用户预览，跳过工具输出正文，不再为每行创建字符串和 `JsonDocument`。
- Provider SSE 每个流复用一个 UTF-8 缓冲；不再为每个事件 `span.ToArray()`。微基准约 192 B/事件降到 72 B/事件，解析耗时约快 15–20%。
- 终端重绘复用 `Run` 对象，避免每个变化行、每个颜色段都分配新的 inline run；`MessageRows.Update` 利用投影器给出的稳定前缀从当前轮开始比较；`CodingAgentSession.Project()` 用只读拼接视图代替每次复制完整消息列表。

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

## 验证与复现

全项目 **1139 项测试通过**，其中桌面端 **437 项**（2026-10-19 复核）；Linux NativeAOT 发布无警告。Debug 与 Linux NativeAOT 发布版都通过原生鼠标/键盘演练，覆盖三级开合位置稳定、差异跨行选择、图片留白关闭和本地链接启动模拟编辑器。Windows/macOS 原生界面未在本环境验证。

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

截图演练均使用独立目录，无真实会话或凭据。脚本除窗口尺寸与进程退出检查外，还断言最新回复实际落在消息视口内。案例 9 为浅色会话，10/11 为深色/浅色空会话；`PI_WORKSPACE_SMOKE_TURNS` 可设 0–5000，0 使用原工具 fixture。
