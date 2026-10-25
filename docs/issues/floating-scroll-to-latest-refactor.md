# 「移动到最新」悬浮按钮重构：三条成因（半圆残缺、位置偏移、不跟随扩展 UI）

症状：圆钮有时只剩下半个（平切边穿过圆心）；长期偏离正文列水平中心；待办卡/终端坞等把正文区压矮后，圆钮停在旧位置不跟随。

## 成因一：浮层位置靠外部同步 margin

### A. 浮层位置靠外部同步 margin

旧实现把外壳挂到 `window.OverlayLayer`，而 OverlayLayer 只按**客户区矩形**排布子级
（`overlay.Arrange(new Rect(0, 0, clientSize.Width, clientSize.Height))`），于是正文区的位置要靠
`UpdateFloatingShellMargins()` 换算成 margin 存起来，触发点只有两处：挂层时与 `_mainArea.SizeChanged`。

后果：

- `View.Bounds.Bottom`（标签页内容底部 805）≠ 客户区底部（820），换算出的 margin 让圆钮水平中心落在 647，
  而正文列中心是 640 —— 实测偏 6.5 DIP；
- 正文区**移动但不改变尺寸**（分栏另一侧伸缩、上方卡片展开收起、标签页切换）时不触发同步，
  margin 停在旧值，圆钮就压到不该压的地方，看起来像缺了一半。

## 成因二：宿主用裸 `FrameworkElement` 手工挂子级 —— 稳定复现「半圆」

我这轮重构第一版把宿主写成了 `FrameworkElement + AttachChild + 在 ArrangeContent 里按绝对矩形摆子级`，
结果 **488 帧连拍里每一帧圆钮都是上半缺失的半圆**（`import` + 像素剖面检测），
平切边正好穿过圆心；挂 OverlayLayer 还是留在 `_mainArea` 里都一样。

也就是说：子级不是由 `Panel` 摆放、而是被非面板宿主按绝对矩形摆时，保留合成的裁剪按面板槽位算，
子级超出槽位的部分被裁掉 —— 就是用户看到的半圆。
改成 `FloatingAnchor : Panel` 后同一场景 179/179 帧圆钮完整（详见下面验证数据）。

这条对读 `docs/issues/scroll-up-blank-realize-window-lag.md` 时写的「按钮升入装饰层」注释也是补全：
那里的「偶发只剩图标」属于同一类分层残缺。

## 成因三：挂在 `OverlayLayer` 上的浮层不会被重新 arrange

`OverlayLayer.Layout(clientSize)` 每轮都用**同一个客户区矩形**去 `Arrange` 浮层，而 `Element.Arrange` 开头就是

```csharp
if (!IsArrangeDirty && Bounds == arrangedRect) return;   // 位置尺寸都没变 → 跳过
```

所以正文区变矮/移动时（待办卡展开、终端坞打开、搜索条出现、分栏拖动），浮层自己的矩形没变 → 它的
`ArrangeContent` 根本不跑 → 按旧矩形算出的位置被一直沿用。旧代码靠 `_mainArea.SizeChanged` 改 margin 才“看起来”跟随；
我第一版把 margin 同步删掉之后，这个洞就裸露出来：**圆钮完全不跟随**。

修法：在正文区里放一个铺满它的驱动器 `FloatingTracker`（无背景、`IsHitTestVisible=false`、`DesiredSize` 为 0，
不参与也不影响布局），正文区任何移动/缩放都会走到它的 `ArrangeContent`，由它 `InvalidateArrange()` 浮层。
位置数值仍然只在 `FloatingAnchor.ArrangeContent` 里按锚点实时 Bounds 计算，不再有第二处换算。

A/B 实测（同一演练，正把下终端坞与搜索条交替开关，正文区底边在 338/602 之间来回）：

| 配置 | 结果 |
|---|---|
| 断开 `FloatingTracker.Arranged` | 155 tick 里 **78 个位置陈旧**（圆钮停在 bottom=605，正文区底边已是 338），且抽帧出现「左/右两瓣纯背景」的缺瓣签名 |
| 接上（当前实现） | 67~155 tick **全部** `btnBottom == mainBottom + 3`，缺瓣帧 0 |

## 重构后的实现

```
window.OverlayLayer
  └─ FloatingAnchor : Panel            // 每轮 arrange 读 Anchor(_mainArea) 的实时 Bounds 摆位
       └─ ShadowDecorator             // 框架自带：在自己的画布里画阴影 + 排布唯一子级
            └─ Button _scrollToLatest // 36 DIP 圆钮 + 图标
```

要点：

1. **位置由布局算**：`FloatingAnchor.ArrangeContent` 每轮按 `_mainArea.Bounds` 算水平居中与
   「下缘越过 3 DIP」，不再有 margin 换算、`SizeChanged` 同步、以及「挂载早于首次布局」的特例。
2. **阴影用框架的 `ShadowDecorator`**（`CornerRadius 18 / BlurRadius 7 / OffsetY 2`），
   删掉自绘的 `FloatingShadow.cs`：圆钮与投影不再是两块互相独立的录制。
3. **隐藏只切一个节点的 opacity**（0 或 1，本按钮没有 opacity 过渡），
   不会进入 `0<opacity<1` 的透明合成分支；显示时仍强制一次 `InvalidateMeasure`。
4. **命中只转发子级**：`FloatingAnchor.OnHitTest` 从最上层子级往下找，自身绝不作为命中结果返回
   （`Panel` 默认会把自己当命中目标，铺满正文区的宿主会吞掉滚轮与悬停）。
5. 仍保留挂层/摘层（在 `FloatingTracker` 上做视觉根跟踪）：标签页切换时只留一个浮层
   （`ConversationPerformanceTests.SwitchingSessionTabsLeavesOnlyOneScrollToLatestOverlay` 守着）。
6. `--ui-smoke latest-churn` 现在抖动的是**真会挪动正文区**的动作（待办卡 + 搜索条 + 底部终端坞），
   `PIWEFT_SWEEP_PROBE=1` 时逐 tick 打印 `btn=(x,y+w+h) mainBottom=… hosted=… op=…`。
   原实现只切待办卡，在没有计划的会话里正文区纹丝不动，所以“跟随”缺陷根本测不出来。

## 验证

常驻回归脚本（已加进仓库）：

```sh
dotnet build src/PiWeft.Desktop -c Debug
xvfb-run -a -s '-screen 0 1920x1080x24' bash tools/desktop-floating-button-check.sh [会话.jsonl]
# ticks=67 正文区底边取值=2 种 / 位置陈旧 tick=0 / 缺瓣帧=0 → RESULT PASS
# 把 SessionTab 里 FloatingTracker 的 Arranged 那行注释掉 → RESULT FAIL（78 个陈旧 tick + 缺瓣帧）
```

真实窗口连拍（`--ui-smoke latest-churn` + 鼠标悬停进出 + 在正文上滚滚轮，41 帧逐帧检测）：

- 圆钮剖面覆盖率 top/mid/bot = 1.84/1.01/0.90（无任何扇区缺失，判定阈值是最小覆盖 < 0.6）；
- 圆钮中心 x = 639.5（正文列中心 640，旧实现 647）；下缘 = 正文区底边 + 3 ✓；
- 在正文区滚轮：帧间正文变化 90108 px → 浮层不吞事件 ✓；悬停圆钮时滚轮不影响正文 ✓；
- `DISPLAY= dotnet test tests/PiWeft.Desktop.Tests` → **864 / 864 通过**。

测试补了不变量（`EmptySessionLayoutTests.ScrollToLatestIsAFloatingBottomCenterButton`）：
宿主必须是 `Panel`、居中于正文区、下缘越过 3 DIP、隐藏时不命中、正文区上方不命中、换尺寸后位置跟着走。

## 以后再出现这类问题

先跑 `tools/desktop-floating-button-check.sh`：它同时判「位置是否跟随正文区」与「圆钮是否缺瓣」，
并把每 tick 的几何与截图留在临时目录。PASS 却仍看到异常，就是新成因，按上面 A/B 的采法重新定位
（临时打印几何 + 像素剖面），不要在没抓到证据前先改渲染路径。
