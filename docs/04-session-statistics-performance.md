# 会话统计与性能

统计口径参考 `/home/scgm/TestBuild/PiDeck` 的 `ComposerStatsLine.tsx`、`SurfaceComponents.tsx`、
`cacheHitStats.ts` 和 `messagePerfTracker.ts`。不新增运行时依赖，保持 .NET 10 / MewUI 0.22.1 / NativeAOT。

## 展示

输入区下方为紧凑统计行：当前分支轮次、首 token 延迟、最近一次模型回复耗时、生成速度、最新缓存命中率、累计输入/输出。
点击上下文百分比或查看图标打开窗口级统计面板：

- 当前上下文 token / 模型窗口，包含超过窗口上限的真实百分比。
- 累计输入、输出、缓存读取、缓存写入、缓存总量和费用。
- 最新命中率、会话平均命中率、累计 token 加权命中率与有效消息样本数。
- 当前分支轮次、全文件用户/助手消息数、工具执行与失败次数。
- 最近一次模型回复的首 token 延迟、总耗时、生成速度与端到端速度。
- 模型回复累计耗时、工具累计执行耗时和平均首 token 延迟。
- 手动压缩上下文；运行时禁用，压缩期间可从输入区中断，关闭标签也会取消。
- 供应商余额区位于会话统计弹窗。保留刷新、查询时间、配置入口与错误状态；配置关闭后回到统计弹窗。成功结果短时缓存，切换供应商或项目清空旧值，隐藏弹窗取消请求并拒绝迟到结果。

设置页使用同一份统计。明细控件原位更新，深浅主题同步，窄窗口内滚动；切换标签或关闭会话时移除浮层。
面板遮罩覆盖整个窗口，不以右侧会话区为定位边界。

## 口径

- 缓存命中率为 `cacheRead / (input + cacheRead + cacheWrite)`。
- 平均命中率是有效助手消息命中率的算术平均；加权命中率是累计缓存读取除以累计 prompt token。
  样本是实际助手消息，不是 UI 刷新次数。无有效 prompt token 时不添加样本。
- 累计 token 与费用沿用既有 `UsageTotals` / `UsageBreakdown` 口径，包含全文件助手消息和 `usage` 条目。
  压缩或导航不会抹去历史费用，不重复叠加工具自身用量或压缩条目已记录过的用量。
- 当前分支轮次按原始用户消息计数，压缩摘要和分支摘要不增加轮次。
- 首 token 延迟取正文首个非空 delta；无正文时回退到思考首 delta。
- 生成速度是输出 token 除以首个非空正文/思考 delta 到回复结束的时间；端到端速度除以整次模型请求耗时。
  不把工具执行或重试退避时间计入单次模型回复。
- 新回复新增可选 JSON 字段 `firstTokenMs` 和 `streamingDurationMs`，与 `durationMs` 一起持久化。
  使用单调时钟计时；旧会话缺失指标显示 `-`，不从整次耗时推测首 token 或思考耗时。
- 人民币金额按 PiDeck 的固定参考汇率 `1 USD = 7.2 CNY` 估算，不是实时汇率或供应商余额。
- 工具累计耗时为各次执行时间之和，并行执行会重叠，不代表会话墙钟时间。

## 热路径优化

- `SessionManager.Revision` 在追加、导航、上下文编辑、压缩、派生、新建、重载时使派生缓存失效。
- 条目快照和上下文消息投影在未变更时复用；只读容器避免消费者修改缓存列表。
- 统计只消费追加的新条目；重置、派生或重载后重新计算。命中率与首 token 均值用累计值与计数器，
  不保存不断增长的样本数组。
- 上下文估算按投影引用缓存，CLI 与桌面端共用；用 .NET 的 `Lock` / `EnterScope()` 同步快照及版本，
  防止后台追加与 UI 读取交错导致过期缓存。
- 聊天快照共享不可变历史前缀，流式帧只分配小尾部；重复读取同一帧复用快照。
  分支/压缩后重新投影，避免静态导航后聊天行停留在旧分支。
- 继续利用 MewUI 的 40ms `DispatcherTimer` 合并更新、历史控件复用与流式结束后才解析 Markdown 的现有设计。
  未变更的统计行不重新格式化或替换控件，历史行比较优先按引用判断。

### 可复现基准

```bash
dotnet run --project tools/PiWeft.SessionBench/PiWeft.SessionBench.csproj
```

同一机器、.NET SDK 10.0.401 / Runtime 10.0.12，Debug 构建，5000 对用户/助手消息（10000 条目），
预热后执行 1000 次 `UsageTotals + UsageBreakdown + ContextTokens`：

| 实现 | 耗时 | 当前线程分配量 | 校验和 |
|---|---:|---:|---:|
| 修改前程序集 | 6677.54 ms | 1,770,384,000 bytes | 5657030200 |
| 修改后 | 0.46 ms | 0 bytes | 5657030200 |

这是未变更会话的查询热路径结果，不代表启动、网络推理、完整 UI 渲染或整进程 RSS 提升相同比例。
首次加载和变更后的投影仍需要遍历会话；不宣称长会话已实现全量消息虚拟化。

## 验证

- `dotnet test PiWeft.slnx`：非桌面 835 项通过（2026-10-19 复核；Desktop 437 项，X11 PrimarySelection 偶发失败、单独运行稳定），覆盖计时 JSON 往返、空/旧数据、错误与取消终态、
  算术与加权统计、追加、导航、编辑、压缩、派生、新建、切换会话、并发查询和零分配查询。
- `dotnet publish src/PiWeft.Desktop/PiWeft.Desktop.csproj -c Release -r linux-x64`：NativeAOT 发布成功，无新增警告。
- 独立临时 agent 目录下，调试版和 NativeAOT 版均通过深浅主题 1280x820、窄窗口 720x520 截图演练；
  不读取或修改用户真实配置和会话，也不访问供应商网络。

```bash
xvfb-run -a -s '-screen 0 1600x1000x24' bash tools/desktop-statistics-smoke.sh
PI_STATISTICS_SMOKE_APP="$PWD/src/PiWeft.Desktop/bin/Release/net10.0/linux-x64/publish/piweft-desktop" \
  xvfb-run -a -s '-screen 0 1600x1000x24' bash tools/desktop-statistics-smoke.sh
```

截图脚本会检查实际图片尺寸，避免本机 `xvfb-run` 默认 640x480 虚拟屏幕导致窗口被裁切却误报通过。
初始窗口尺寸在创建原生窗口前设置，位置恢复仍在加载后进行。

发布入口：`src/PiWeft.Desktop/bin/Release/net10.0/linux-x64/publish/piweft-desktop`。
不涉及系统/工具 prompt token 的独立估算、实时汇率或 PiDeck 的全部高级功能；供应商余额查询见上文。
