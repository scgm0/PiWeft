# pi / PiDeck 兼容与差距

PiWeft 复用 pi 兼容核心，桌面端用 MewUI + NativeAOT，PiDeck 用 Electron + React。本文记录桌面功能、配置安全边界和仍然存在的差距；当前实现状态见 [状态](00-status.md)。

对照基准（只读参考）：

| 对象 | 版本/路径 | 用途 |
|---|---|---|
| pi | 本地只读检出，`v1.3.0+30`（`482c1381a`） | 核心协议、CLI、扩展与 codemode |
| PiDeck | 本地只读检出，0.9.0 | 桌面工作流、配置与视觉基准 |
| pi_agent_rust / pi-go | 本地同级仓库 | 子代理、IDE、记忆与工具增强参考，不是必备契约 |

## 桌面视觉与主题

配色来自 PiDeck 的 `src/renderer/src/styles/foundation.css`，集中在 `AppPalette`；尺寸与字号集中在 `AppTheme`。完整视觉契约见 [DESIGN.md](../DESIGN.md)。

| 维度 | 取值（深色 / 浅色） |
|---|---|
| 背景 | app `#121212` / `#ffffff`，侧栏 `#171717` / `#ffffff`，弱化底 `#262626` / `#f4f4f5` |
| 文本 | primary `#ecece7` / `#202124`，secondary `#b8b8b2` / `#4b5563` |
| 边框 | default `#2e2e2e` / `#dfdfdf` |
| 强调色 | 近白 `#fafafa` / 近黑 `#18181b` |
| 代码块 | 随深浅主题配色；工具输出使用 `#111418` 深底与 `#d1d5db` 浅字 |
| 字号 | 辅助 12、caption 13、control 14、正文/输入默认 16（可选 14/16/18/20/22）、title 17、brand 18 |
| 圆角 / 间距 | 4/6/8/10/12；4/8/12/16/24 |
| 控件高度 | 28/32/36 |

主题切换调用 `Application.SetTheme`，不重建整棵界面或会话：

1. 长期存在的容器与按钮使用 `AppStyles` 命名样式，由 setter 按主题解析颜色。
2. `TextBlock` 不支持 `StyleName`，通过 `Tag` 角色与 `ApplyTextColors` 刷新前景色。
3. 图标缓存包含颜色，`AppIcons.RefreshAll` 更新存活控件，弱引用避免持有旧消息。
4. 消息卡片、工具、思考标题和 diff 原位换色，保留 Markdown 实例、选区、展开状态与配置草稿；分支树和 Git 不因主题变化重新加载。

MewUI 0.22.1 全部来自 NuGet；Linux 使用 X11 + MewVG，Windows 使用 Win32 + Direct2D，macOS 使用 MacOS + MewVG。正文默认 HarmonyOS Sans SC Regular，代码与终端使用 Maple Mono NF CN Regular（Nerd Font）；Lucide SVG 内嵌，许可随包。HarmonyOS Sans 的镜像许可与官方发布说明存在差异，分发前需核实。

## 桌面功能对照

| 能力 | 已实现 | 尚未覆盖 |
|---|---|---|
| 项目与会话 | 按 cwd 分组、跨项目环境隔离、并行标签、置顶、命名、归档/恢复、最近项目 | 项目级置顶、标签拖拽/分屏、批量操作 |
| 搜索 | 元数据与后台有界正文搜索、会话内筛选 | 正则、命中高亮 |
| 消息 | Markdown、语法高亮、代码复制、图片与点击放大、思考折叠、用户/助手复制 | 代码行号、跨行块注释、更多语言 |
| 工具 | 折叠摘要、状态/耗时、参数/输出、带行号的统一 patch、并排 diff | 完整文件交互 |
| 历史操作 | 编辑重发、重试、fork、分支树、直接/摘要跳转、HTML 导出 | 多选批量操作、分支比较 |
| 输入与模型 | 斜杠命令、命令面板、可改快捷键、可用模型筛选、供应商分组/折叠、搜索/刷新、最近模型优先、中文思考级别 | 模型收藏 |
| 附件 | 路径引用、图片文件/原生字节、剪贴板图片粘贴、复制预览图片、仅图片发送 | 更多媒体类型、把回复整体复制为图片 |
| 用量 | 会话统计、上下文、费用、缓存与计时、手动压缩；余额/积分查询；跨会话统计与 26 周热力图 | 完整供应商订阅窗口、热力图下钻 |
| 设置 | 模型/供应商、API Key、Agent、MCP、JSON、作用域与草稿保护、工具调用安全门（`settings.security`）、外部会话导入 | 桌面 OAuth、技能/提示词管理、商店、安全门的图形化编辑与交互式审批 |
| MCP | 启动连接、状态、stdio 工具热刷新、设置面板手动重连 | 自动重连、跨项目运行时管理 |
| Git | 状态、暂存/未暂存目录树、diff、在编辑器中打开、暂存/取消暂存（含目录级）、提交（Ctrl+Enter）、分支切换/新建、AI 提交摘要（默认跟随会话，可在设置中固定模型/思考级别）、源代码管理图（泳道、引用筛选、提交详情/文件 diff、加载更多）、比较更改（三点比较、ahead/behind、文件 diff）、fetch/pull（`--ff-only`）/push（首次自动建跟踪）、stash 收起/应用/丢弃、失败分类提示 | 冲突解决界面、rebase/cherry-pick/reset/drop 等改写历史的操作、多远端选择；检查点不由 Git 面板管理（会话仍自动快照） |
| 文件 | 目录导航、Git 标记、内置文本编辑器与冲突检测保存、回答级修改汇总 | 多文件标签、语法高亮编辑、正则搜索 |
| 终端 | MiniPty 伪终端、交互式 shell、Shell 选择、scrollback、选择、SGR/滚动区域/字符集/备用屏、缩放重排、字素簇按格对齐（组合符号/ZWJ emoji/国旗/肤色/Hangul，含跨读块拼接）、光标按字格几何绘制（DECSCUSR 块/竖线/下划线、失焦空心框，边贴设备像素、横向跟该行真实排布）、DECSCUSR/DECSCNM/DECALN/DECRQM、OSC 4/10/11/12、OSC 8/52、鼠标上报、bracketed paste、IME、Ctrl+C/V、PRIMARY、右键菜单 | 完整 VT100 边界行为、终端内搜索、终端多标签、快照回放 |
| 主题与窗口 | 深浅/系统主题预览、侧栏收起与宽度恢复、尺寸与零/负位置恢复、单实例 | 自定义强调色、多显示器可见性/最大化恢复 |

内嵌浏览器、自动更新、托盘、通知等不在实现范围内（命令 Dock 是基于 MiniPty 的伪终端，但不是完整终端仿真器）。

## 桌面交互与约束

- 正文与输入区居中并限制阅读宽度；默认 1060 DIP，可在外观页选择 760/1060/1360。正文与输入字号默认 16，可选 14–22；修改立即应用、记住偏好，不替换输入框、不清空草稿或选区。
- 消息时间线使用可变高度虚拟化，只构建视口附近控件；离开视口释放正文、Markdown 和图片，再次浏览恢复工具/思考展开状态。分支树延迟到首次打开时构建。时间线整份打开会话（实测中位 ~5 ms、p90 80 ms，故未做首屏分页，理由见[性能与基准](02-performance.md#为什么没有做字节偏移索引--历史分页)）。局部更新只让真正变化的行段失效，已测行高与滚动范围不会因单行变化整体回退成估算值。
- 文件浏览面板同样使用固定高度虚拟化列表（目录最多 2000 条），只为视口建行；进入子目录、刷新与返回都从列表顶部开始。模型选择的搜索结果重建后回到第一行。
- 侧栏使用 14 DIP 标题和固定时间列；标题超宽省略。侧栏可收起，宽度与状态写入 `desktop.json`，搜索快捷键自动展开。
- 顶部状态只显示供应商、模型、思考档和忙碌状态；用量摘要统一放在输入区下方。统计浮层默认「概览」，明细按快照增量更新；缺失计时显示 `-`。
- 设置采用系统/配置分区与「工作区 / 维护」分类，导航独立滚动。配置编辑器只在首次进入配置管理时构造，归档只在访问归档页时扫描。
- 折叠工具只创建摘要，首次展开才创建原文、图片和 diff；移出视口后释放内容，仅记住展开状态。步骤行展开后即使位于视口下沿之外也会重测行高并重新发布组体 extent，组体不会停在展开前的可滚范围。长输出不会预先构造文本布局。执行过程用左侧计数 pill + 紧凑步骤展示工具/思考/中间回复：连续竖线与步骤图标分列，互不遮挡；少量步骤时组体高度由内容驱动（最高 300 DIP），展开/收起步骤会带动组体增高或回落；超过 24 条时改用虚拟化列表并保持 300 DIP 限高。流式最新组自动展开时跟住最新步骤，手动展开的组从顶部开始；虚拟化步骤里的流式思考/正文只原位更新，不会每帧重绑导致行高缓存回退、组体进度跳变。思考步骤用轮次+序号、工具步骤用调用 id 作稳定键，流式消息落盘后组与步骤不会因源 EntryId 变化而重建。组体滚动条置底时自动跟随最新步骤（跟底由几何判定：切换栈/虚拟化、行高重测、展开折叠都不会永久关闭，只有读者上滚才暂停，回到末尾自动恢复）；流式思考与正文共用 `PiWeft.Markdown` 增量视图：解析后按块模型哈希 diff，只重建尾块；段落按已闭合行分块，只重排开放尾块；代码块用 `ReplaceRange` 追加。思考结束只切换流式状态，不重建、不自动折叠已展开的内容。展开工具步骤时视口停在它的预览行（预览锚点按步骤索引解析，虚拟化器换掉行宿主也不会失效；即使滚动条停在末尾也照钉），组内步骤折叠只暂停外层跟随、不移动外层会话进度；展开流式思考时组体与会话一起跟到思考末尾；点击思考正文只把焦点交给阅读控件，焦点守卫会恢复所有祖先滚动视口，不会让 MewUI 的 focus-into-view 把组体滚到 `MarkdownView` 的顶部/底部、跳变组体进度，也不会让虚拟化行暂时释放后留下空白。滚轮是标准嵌套滚动：内层这个方向还滚得动就滚内层（会话不动），到顶/到底或内容装得下时把滚轮交还外层会话继续滚。工具行显示状态、耗时与等宽摘要；底部显示收起与总耗时。运行中不预先显示成功，停止后未返回结果的工具显示「已中断」。
- 消息区右缘提供可跳转的定位刻度轴（仅真实显示的用户消息参与）：悬浮或键盘聚焦显示问题摘要与日期时间，点击/Enter 跳转，上下键与 Home/End 在刻度间导航，滚轮继续滚动正文。当前项按实际可见行跟随，不受原问题被虚拟化回收影响；末尾高亮最后一问。长会话的刻度间距由 14 DIP 缩至 4 DIP，仍超限时均匀抽稀并保留首尾；消息区窄于 540 DIP、少于两问或切换分支视图时隐藏。
- 会话搜索先即时匹配标题和首条消息摘要；正文扫描等待 220 ms，新输入与关闭窗口取消旧请求。每个候选文件最多读取 512 KiB，超出预算的正文不参与搜索。结果列表有界（200 行）并在状态行说明截断，方向键与 Enter 只在已显示的结果内移动。
- 会话内过滤（标题行搜索框）进入时取消跟底、定位第一条命中；清空后恢复搜索前的阅读行与行内深度。过滤同时匹配正文、思考、工具名和执行过程里的步骤，过程组不再是搜索盲区。
- 流式期间正文和展开的思考都使用同一个增量 Markdown 视图：全量解析后按块模型哈希 diff，只重建变化的尾块；段落按已闭合行分块，只重排开放尾块；代码块追加不重建。定稿只切换 `IsStreaming`，不再做纯文本→Markdown 的替换。40 ms 合并刷新，复用未变化历史控件与折叠状态。向上浏览时立即停止追随底部，手动回到末尾会重新进入追随；跟随动画进行中遇到内容增长会继续指向末尾。「移动到最新」是消息区底部居中的悬浮圆钮，带发丝轮廓和轻阴影，只在向上浏览时自动出现，回到底部自动隐藏；点击使用平滑动画。
- 跟随态只由滚轮/触控板/键盘/滚动条这类显式输入改变：近底带内的 1–2 px 抖动先累计再判断逃逸（25 DIP），虚拟化行高变化引起的布局滚动不取消跟随。键盘在焦点控件未处理时用方向键/Page/Home/End 滚动会话，嵌套组体到边界后沿祖先链交接。
- 代码块不嵌套纵向视口：普通滚轮移动会话列表，横向触控板和 Shift+滚轮浏览代码；顶栏的复制按钮是一枚 Lucide 图标动作（`copy`，复制成功后短暂换成 `check`），静止压暗、悬停恢复全强度；图标由宿主注入，Markdown 控件库自己不画图标。差异正文使用一份只读 `MultiLineTextBox` 与 `ITextClassifier`，可跨行拖选复制，行号不混入复制内容。
- 用户消息把技能命令展开的 `<skill>` 块折叠成 chip，正文、时间和操作分层展示；长文默认折叠 8 行。图片以紧凑缩略图排在气泡上方，点击仍打开全尺寸预览。
- 扩展注入的内部上下文（会话文件里 `custom_message` 且 `display: false`，如目标模式 / 计划模式的提示）仍参与模型上下文、也仍是一轮的边界，但和 PiDeck 一样不在时间线里占用户气泡；`display: true` 的扩展通知照常显示。
- 用户消息和草稿里的 `@"path"` / `@path` 通过文本投影和内联对象显示为行内文件 chip：图标 + `@短文件名`（目录带尾斜杠和文件夹图标），完整路径仍保留在文档中，悬浮在 chip 上时贴着 chip 显示完整路径（相对引用按会话目录补成绝对路径），历史消息里点击打开。草稿里方向键按整枚跳过，Backspace/Delete 删除整枚，撤销可恢复；复制、编辑和发送始终使用原始 `@"path"`。代码块、行内代码和 URL 里的 `@` 不参与识别。
- 同一套投影也把正文里的 `/skill:名称` 画成行内技能 chip（sparkles + 技能名，Thinking 紫，对齐 PiDeck）：悬浮提示技能文件位置（解析不到时回退 `/skill:名称`），技能 chip 不可点击打开；原文 `/skill:名称` 保留，草稿里同样是整枚原子。代码块、行内代码和 URL 里的 `/skill:` 保持纯文本。
- 文件可拖入整个会话区；文件和目录插入草稿引用，图片后台有界读取为附件，不自动发送。剪贴板图片走同一条 `AddImage` 路径。
- 命令面板每次打开只取一份命令快照，最多显示 12 项；模型选择独立使用虚拟化供应商树，搜索覆盖全部模型，不截断为前 12 项。
- 可用模型要求 API 已实现且能解析当前运行环境凭据；不执行命令型凭据、不发网络探测。`--list-models` 保留完整目录语义，RPC `get_available_models` 与桌面一致。
- 命令/模型/图片面板使用窗口级覆盖层，同一窗口只打开一个；遮罩不穿透对话，Esc 可关闭，切换标签或销毁视图会清理浮层。
- 用户/助手原文、Markdown、代码和工具输出支持部分选择与复制；用户消息、思考、工具原文和流式正文使用自适应高度只读文本控件。
- 跨项目会话使用自己的 cwd 环境，启动项目 MCP 不注入其它项目标签。运行时发送按钮变为中断，保留草稿并禁用模型切换及历史修改动作；中断会结束流式请求、工具批次、自动压缩与重试退避，把已生成的部分回复以 `aborted` 消息落盘，随后可立即再次发送。发送键右侧的 Split Send 菜单可把草稿加入当前回合（steer）或排到下一轮（follow-up），附件随消息入队。
- Markdown 链接只放行 http/https；编辑器优先 `$VISUAL` / `$EDITOR`；HTML 导出无脚本且自包含。

### 历史思考耗时与只读浏览

桌面通过 `recordInitialThinkingLevel: false` 打开会话，不追加条目或修改 mtime；真正开始对话时再记录实际级别。标准 pi 会话没有独立思考计时字段，`durationMs` 是整次回复耗时，不能据此推测思考耗时。`firstTokenMs` / `streamingDurationMs` 的口径见 [性能与基准](02-performance.md)。

## Linux PRIMARY 与图片剪贴板

Linux 上选中的原文、Markdown、代码和工具输出，以及普通输入框文本，自动发布到 PRIMARY；鼠标中键在可点击位置插入 PRIMARY。普通 Ctrl+C/Ctrl+V 的 CLIPBOARD 不受影响，密码框不发布文本，中键粘贴不自动发送且可撤销。
当前 MewUI Linux 宿主是 X11/XWayland。PRIMARY 默认由进程内原生实现持有：`X11PrimarySelection` 用独立 Display 连接与专用事件线程直接实现 X11 selection owner 协议，只依赖 `libX11.so.6`。事件线程用 `poll` 等待 X 连接与 `eventfd`，空闲时不轮询；超过单个请求块的文本按 ICCCM INCR 分块传输。`XOpenDisplay` 或资源创建失败时按 `xclip`、`xsel` 顺序回退外部程序；没有 DISPLAY 时才使用 `wl-copy`/`wl-paste`。纯 Wayland 且无 XWayland 时只能走回退路径。

```sh
xvfb-run -a -s '-screen 0 1600x1000x24' python3 tools/desktop-primary-smoke.py
```

`PrimarySelectionTests.IncrementalTransferCarriesTextLargerThanOneRequest` 的「外部 xclip owner → 本端口接收」那一段要求宿主的 X server 自己能服务大块 INCR：同一份 xclip 二进制在 Xvfb 下正常，在真实桌面会话里可能收下 2 MB 后任何客户端都读不回来（连 xclip→xclip 往返都是 0 字节）。测试因此先用**同一段文本**做一次 xclip→xclip 往返探测，走不通就跳过——这是宿主环境限制，不应报成产品回归。单次 `ReadAsync` 的读超时是 2 秒，所以任何「重试到拿到为止」的测试都必须自己带总预算，否则每次超时叠起来会把一条断言拖成一分钟。

图片剪贴板按 Ctrl+V/Cmd+V 时，如果剪贴板里有图片就按附件处理；没有图片时按键原样交给框架做文本粘贴。输入框默认右键菜单的「粘贴」替换为同一条图片优先命令，因此纯图片剪贴板时菜单项可用，与快捷键一致；只有文本时仍回退到文本框的默认文本粘贴。图片预览浮层提供「复制图片」，把原始编码字节写回系统剪贴板。MewUI 的 `IClipboardService` 只有文本，桌面侧新增 `IClipboardImages`（`HasImage` / `ReadAsync` / `TrySetImageAsync`），测试注入假实现即可覆盖交互。

- Linux：`xclip` 读写 PNG/JPEG/WebP/GIF；写入后保留 xclip 进程作为 owner，`xsel` 不支持图片类型。
- Windows：读写原生剪贴板；读取优先 `PNG`，其次 `CF_DIBV5`/`CF_DIB`，也处理 `CF_HDROP`；写入同时放 `CF_DIB`、`CF_DIBV5` 与编码格式。
- macOS：`NSPasteboard` 的 `public.png`/`public.jpeg`；`public.tiff` 经 `NSBitmapImageRep` 转 PNG。

解码统一使用 MewUI 的 `ImageDecoders` / `ImageSource.CopyPixels`。Windows 的 DIB↔PNG 互转自带最小实现（`DibCodec`、`PngCodec`），都是纯托管代码并用框架解码器交叉验证。

## 外部会话导入

`pi import`（CLI）与桌面「导入外部会话」弹窗共用 `src/PiWeft.CodingAgent/Import/` 的同一套管线：
发现 → 转换 → 写成 pi v3 JSONL。

| 来源 | 根目录 | 形态 |
|---|---|---|
| Claude Code | `~/.claude/projects/<slug>/<sessionId>.jsonl` | `type: user/assistant` + `message.content` 块（text / thinking / tool_use / tool_result） |
| Codex CLI | `~/.codex/sessions/<YYYY>/<MM>/<DD>/rollout-*.jsonl` | `session_meta` + `response_item`（message / reasoning / function_call / function_call_output），兼容旧版 `event_msg/user_message` |

- **列举有界、只读头部**：摘要只需要 `sessionId`/`cwd`/首条用户消息，都在文件前部；每个文件最多读 256 KiB（按最后一个换行截断，避免半行），单来源最多 500 条。真实整读只在导入时发生，且逐行流式。
- **转换保真**：`tool_result` 必须还原成 pi 的 `toolResult` 而不是用户文本（否则模型看到工具的原始 JSON，toolCall ↔ toolResult 也断了配对）；一个 `tool_use` 收尾一条助手消息，保证调用与结果相邻可配对；`isSidechain`（子代理旁支）与 `isMeta`（宿主机元信息）跳过；模型、用量（含 cache read/write）与时间戳按源 transcript 保留，Codex 只有 call/output 时间戳时补一个派生 `durationMs`。
- **落盘**：同目录 `.importing` 临时文件 + rename 原子替换；导入标记 `piweft-import` 记来源路径与 `converterVersion`。
- **标记必须挂在链尾**：自定义条目若 `parentId: null` 就会成为文件最后一行 = 分支叶子，父链断在那里，打开后整个对话都投影不出来（这条有回归测试）。
- **重复导入不覆盖**：每次产出新的会话 id 与文件名，所以在清单里点两次会得到两份产物 —— 这是刻意的，用户可能想对比两次转换的结果；要清理就直接删会话。
- **不做的事**：只读源文件，绝不改动原工具的会话库；不写回 `~/.claude` / `~/.codex`；没有 SQLite 依赖，因此 OpenCode / ZCode / Cursor / Kimi 这类以数据库存放会话的来源暂不支持。
- 视觉验收：`xvfb-run -a bash tools/desktop-import-smoke.sh`（夹具家目录 + 弹窗截图，不碰真实数据）。
- 测试：`tests/PiWeft.CodingAgent.Tests/SessionImportTests.cs`（发现/转换/落盘 12 项，含产物必须能被 `SessionManager` 正常打开）、`tests/PiWeft.Cli.Tests/ImportCommandTests.cs`（命令面 8 项）、`tests/PiWeft.Desktop.Tests/SessionImportDialogTests.cs`（弹窗 5 项）。

## 进程内扩展宿主（PI-01 / PI-02）

`src/PiWeft.CodingAgent/Extensions/`。一个扩展 = 一个 Jint 引擎 + 一条专属线程，
注册期与所有回调都投递到那条线程上执行（Jint 引擎不是线程安全的，而工具与事件来自主机线程）。

- **发现**：`<agentDir>/extensions` 与（仅在项目受信任时）`<cwd>/.pi/extensions`，认 `.js/.mjs/.cjs` 与 `.ts/.mts/.cts/.tsx`；
  `.d.ts` 与目录里的杂文件不算扩展。
- **注册面**：`pi.on(event, handler)`、`pi.registerTool({name,label,description,parameters,execute})`、
  `pi.registerCommand(name, {description, handler})`（也收 `(name, description, handler)` 的位置写法）、`pi.ui.*`。
- **可派发事件**：`tool_call`、`tool_result`、`before_agent_start`、`agent_end`。
  监听别的事件在**注册时就报诊断**并出现在 `UnsupportedEvents` —— 悄悄收下却永不触发是最难查的失效模式。
- **沙箱**：扩展没有 `fs` / `net` / `require` / 动态 import。唯一出口是显式注册的 `__pi*` 宿主函数，
  `pi` 对象由这些函数在 JS 侧组装（与 `CodeModePrelude` 同一手法）。桥一律 `ClrFunction`，
  因此 NativeAOT/裁剪零告警（AOT 冒烟里有一 stage 专门跑扩展宿主）。
- **安全边界**：扩展的 `tool_call` 钩子排在安全门**之前**（扩展可以主动拦调用），
  但它放行的调用仍要过档位检查 —— 扩展不能变成绕过安全门的口子。内置工具优先于扩展同名工具。
- **可见性**：资源页新增「扩展」一节，逐条列出来源与**未生效原因**（TypeScript 需转译、语法错误等）。
  只发现不执行；执行属于会话建立时。
- **`/reload` 重建扩展宿主**（RPC `reload_resources` 与桌面的重载按钮都走它）：改完扩展文件还要重启应用
  才生效等于没有热更新。只有宿主自己加载的才重建 —— **注入进来的**（`options.Extensions`）生命周期归注入方，
  会话擅自 Dispose 别人的宿主是越界。并表始终从内置基线出发，否则 reload 会把扩展工具越加越多。
- **失败隔离**：一个扩展抛错只记在它自己的诊断里，其余扩展与会话照常；
  卡死的扩展被 10 秒超时切断并报告，不拖住会话。
- **两条明确限制**：① 只执行 JavaScript（TS 需要构建期转译，本端口不内置转译器）；
  ② 处理器必须同步 —— 返回 Promise 会被**明确报告**并丢弃返回值（异步要等事件循环泵接进宿主）。
  这两条都在界面上可见，不会静默降级。

## Git 远端同步与 stash

`GitWriter` 覆盖 fetch / pull / push / stash 收起·应用·丢弃，全部用 `ArgumentList` 传参（不拼 shell），
名字以 `-` 开头一律拒绝以免被当成 git 选项，stash 引用只接受 `stash@{n}` 形态。本地命令 30 秒、网络命令
120 秒超时，stdout/stderr 并发读（顺序读会让子进程缓冲写满后挂死，超时也救不回来）。

几条刻意的取舍：

- **pull 只用 `--ff-only`**。这里没有冲突解决界面，自动 merge 或 rebase 会把仓库推进到一个用户无法从界面
  收拾的状态（半完成的合并、未解决的 `<<<<<<<` 标记）。不能快进时宁可报错并让用户去终端决定合并还是变基。
- **首次推送自动建跟踪**：没有上游时执行 `push --set-upstream <远端> <当前分支>`。远端不能写死 `origin` —— 不少仓库的远端叫 `upstream` / `github`，写死会让一次本该成功的推送报成「'origin' does not appear to be a git repository」。选择规则：只有一个远端就用它；有多个且其中有 `origin` 就用 `origin`（约定俗成的目标）；有多个且都没有 `origin` **不猜**，提示用户自己 `--set-upstream-to`（推错远端比不推难查得多）。`git push` 在无上游时的
  报错是新手最容易卡住的地方，而按钮的语义就是「把当前分支推上去」。分离头指针下拒绝（会造出用户没预期的分支名）。
- **没有远端 / 没有上游时按钮变灰**，并说明原因；面板不靠「点下去看报错」来教学。
- **网络操作关掉终端提示**（`GIT_TERMINAL_PROMPT=0`）：把「等用户输入密码」从挂满 120 秒超时变成立刻返回
  可分类的认证错误。凭据管理器与 askpass 不受影响。
- **所有 git 子进程钉住 `LC_ALL=C`/`LANG`/`LANGUAGE`**：git 的消息是本地化的，分类表按英文特征串写，
  不钉住语言就会静默全部失配 —— 中文环境下 `git stash push` 说「没有要保存的本地修改」，
  `git pull --ff-only` 失败时首行是「来自 /tmp/remote」而不是 `fatal: Not possible to fast-forward`。
  钉住之后用户看到的是翻译过的中文提示，原始英文输出留在 `GitResult.Detail` 里（面板末尾展示）。
  这条约束有源码契约测试兜底，漏掉任何一处 git 子进程都会红。
- **未跟踪文件的 diff 不经过 git**：以前用 `git diff --no-index /dev/null <path>`，而 `/dev/null` 只在 POSIX 上存在（Git for Windows 是否认这个字面量取决于版本），Windows 上会直接失败、面板显示「没有可显示的 diff」。现在自己合成一份「整份新增」的 unified diff（只读界面要显示的前若干行，二进制给一句说明），跨平台一致且少起一个进程。
- **状态读取必须走 `--porcelain=v1 -z`**：非 `-z` 时 git 会对非 ASCII 路径做八进制转义
  （`"\344\270\255..."`）、给带空格的路径加引号，而解析按固定列取子串 —— 中文 / emoji 文件名会变成
  一串不存在的路径，于是「点暂存没反应」「点开文件说没有」。`-z` 输出原始 UTF-8 且不带引号，是 git 唯一
  保证机器可读的形态；改名条目在 `-z` 下把**原路径放在紧随其后的独立字段**（顺序与文本形态的
  `old -> new` 相反），解析时按 `R`/`C` 状态多消费一个字段。真实仓库上的中文 / 空格 / emoji / 换行 / 改名
  路径由 `tests/PiWeft.Desktop.Tests/GitStatusPathTests.cs` 钉住。
- **分离头指针不表现成一个分支**：`## HEAD (no branch)` 归一成空分支名（界面回落到 `HEAD`）并置 `Detached`，
  否则它会出现在分支下拉里还能被「切换」，推送按钮也照样可点。
- **面板按 PiDeck 的 SCM 三段重排**：顶部是紧凑分支栏（分支下拉 + 远端同步图标），下面是「更改」区
  （多行提交框 + Ctrl+Enter、AI 摘要、暂存/未暂存目录树）与折叠的「更多」（stash、提交摘要默认配置、
  无远端提示）；检查点不再占用 Git 面板，会话仍按回合自动快照。改动按「未暂存 / 已暂存 / 合并冲突」
  分节，同一文件两边都有改动时两边各出现一次（`MM` 这类状态只按索引列归类会让人以为已经提交干净了）。
  目录树把单子目录链合并成 `a.b.c` 一行，目录行可折叠、可对整棵子树暂存/取消暂存；文件行显示
  `A/M/D/R/U/?` 状态字母与行内动作。没有已暂存改动时提交主按钮禁用并给出提示。
- **AI 提交摘要**：读取暂存区 diff（最多 8000 字符）交给当前会话模型；默认沿用会话的模型与思考级别，
  也可在「设置 → Git 提交摘要」里固定模型/级别。虚拟模型先按目录路由到物理模型；会话正在运行时按钮禁用。
  模型输出会剥掉代码围栏、前缀和引号，限制为可直接提交的短消息。暂存区为空或 git 读取失败分别给出
  不同提示。视觉验收脚本：`xvfb-run -a bash tools/desktop-git-smoke.sh` 会造一个有远端、有 stash、
  三种改动状态的夹具仓库，按深浅主题各截一张图。
- **面板不被网络操作冻住**：fetch/pull/push、切分支、stash 收起/应用/丢弃与提交都放到后台线程，
  完成后经 Dispatcher 回到 UI 线程刷新；期间按钮进入忙碌态，网络慢链路不会冻结整个窗口。
  暂存/取消暂存与 `git init` 是本地快操作，仍同步执行。AI 摘要同样后台生成（60 秒上限），
  面板显示不确定进度条；暂存区 diff 用有界读取，超大 lockfile 不会整份读进内存。
- **源代码管理图**：`git log --all --topo-order -n40`（排除 `refs/pi-checkpoints/*` 与 `refs/stash`）
  读取最近提交，按与 PiDeck 相同的 swimlane 算法画输入/输出 lane（泳道列自绘，最多 8 条可见 lane）；
  引用筛选把范围收敛到单个分支，刷新/筛选/加载更多各自失效缓存。点击提交行展开完整消息、
  作者/时间/短哈希、增删统计与文件列表，
  点文件把该提交相对第一父提交的 diff 送到底部 diff 面板；提交详情按 hash 缓存，避免重复起 git。
  迁移到新的分支/提交/推送/拉取后 `_graphDirty` 使图缓存失效。
- **比较更改**：选择基准/目标分支后跑三点比较（`git diff base...target` + `git rev-list --left-right --count`），
  列出状态字母、ahead/behind 与文件；点文件把 `base...target` 的 diff 送到底部面板。比较只读取本地仓库，
  不执行 merge/rebase；同一分支或不足两个分支时直接给提示。图形/比较的 git 输出有界，路径放在 `--` 之后，
  hash/ref 先做形态校验。
- **冲突与分叉不假装能处理**：`--ff-only` 失败、push 被拒、stash 应用冲突都只给出「去终端解决」的指引。
  在这条链路上宁可少做，也不要把仓库留在半完成状态。

## 配置管理与安全边界

| PiDeck 源码 | 关键行为 | 本端口落点 |
|---|---|---|
| `config/ModelsTab.tsx` | 供应商与模型编辑、复制、导入导出，保留高级字段 | `ConfigurationView.Forms.cs`、`ConfigurationModels.cs` |
| `config/AuthTab.tsx` | 认证供应商清单，API Key 与 OAuth 分开 | API Key 管理；OAuth 只展示类型，禁止被表单覆盖 |
| `config/McpTab.tsx` | 全局/项目作用域、enabled、stdio/HTTP；保存不启动服务器 | `ConfigurationView.Mcp.cs`；明确下次启动生效 |
| `main/config/piConfigFileStore.ts` | revision、未知字段保留、同目录原子替换、外部修改冲突 | `ConfigurationStore.cs` |
| `main/config/ConfigManager.ts` | 配置领域与视图分离；模型拉取与探测按 API 分流 | `ProviderProbe`、`RemoteModelCatalog` |

- 分类设置页：系统设置与配置管理分开；保留环境、会话用量、归档、快捷键、日志和关于。
- 自定义供应商：新增、编辑端点和协议、复制、删除、导入 JSON、脱敏导出。模型可设置 ID、名称、上下文、输出上限、推理及图片输入；字段补丁保留 `headers`、`cost`、`compat` 等未编辑数据。
- 认证：API Key 遮罩输入、保存、删除；保存后清空敏感输入；已有 OAuth 不允许被 API Key 覆盖。
- Agent 设置：默认供应商/模型/思考级别、自动压缩和自动重试。未修改的继承值不制造项目覆盖。
- MCP：全局/项目配置，stdio/HTTP 编辑、参数数组、启停和删除；保留 env/headers 等高级字段。
- 源文件：模型、认证、设置和 MCP JSON，可显式显示、编辑和修复损坏文件。配置草稿独立于主题刷新；切页、重载、关闭设置弹窗和窗口均保护未保存修改。
- 文件读写限制在所选 agent 目录和固定的项目 `.pi` 路径，认证不允许写到项目作用域。读取和写入最多 2 MiB；只接受对象根节点，校验关键字段类型、模型 ID 和 MCP 名称。
- 写入使用同目录临时文件、刷新到磁盘后原子替换；Unix 新配置仅用户可读写。桌面写入之间使用专属排他锁，并检测 pi 的 `.lock` 标记与内容 revision；不宣称与非协作编辑器拥有事务互斥。
- 导出移除 API Key、认证相关字段、全部 headers/env/OAuth 和 URL 的查询、userinfo；不会自动导出 auth.json。源文件可能含密钥，默认不显示；配置操作不向日志输出异常原文或配置内容。
- 模型目录/Agent 设置更新作用于新会话；已有会话不进行运行时替换。MCP 保存不自动重连，设置面板提供手动「重连 MCP」。

### 工具调用安全门

配置容器是 `SecurityConfig`（总开关 + 默认档 + 档位表 + `sessionLevels`），单个档位的策略是 `ToolSecurity`。
判定入口 `ToolSecurity.Evaluate(tool, args, cwd)` 返回 `Allow` / `Ask` / `Deny` 三态，
挂在 `CodingAgentSession.GuardedBeforeToolCallAsync`（直接调用、codemode 嵌套调用、MCP 工具的唯一汇合点）。

三态的语义，与 PiDeck 的差异都写在类型注释里：

- **`ask` 有宿主就弹确认，没有宿主就拒绝**（RPC / print / 守护进程直连属于这一类）。
  无审批人在场却放行，等于这道门只在有人看着时才关上 —— 最坏的一种失效模式。
- `ask` 之前**先过路径边界与危险命令检查**：不可逆操作不该靠一次回车放行，所以「要问一下」不会把
  「本来就该拒绝」提升成「问一句」。
- 确认对话框里选了允许，按**工具名**在本会话内记住（`_securityApprovedTools`），不跨会话、不落盘。
- 会话级覆盖存在同一份全局设置里（`security.sessionLevels`，键是会话文件路径），
  而不是项目 `.pi/settings.json`：项目设置能改自己的安全档位等于让一个仓库关掉本机防护。
- 界面入口两处：设置 → 运行 → **安全门**（总开关、默认档、档位编辑、`新建自定义档位`/删除、按会话覆盖列表），
  以及会话输入框左群的 **shield** 按钮（跟随档位）。分区改动即时写盘并推给所有已打开的会话
  （`SettingsView.SecurityChanged` → `MainWindow.ApplySecurityToSessions`），不留「草稿 + 统一保存」的脏状态。
- 视觉验收：`xvfb-run -a -s '-screen 0 1440x900x24' bash tools/desktop-security-smoke.sh`
  （预置非默认配置 + 自定义档 + 会话覆盖，按深浅主题各截一张，并断言分区真的被选中）。
- 一个踩过的实现约束：`ToolSecurity` 的模式表（敏感路径、危险命令）必须声明在档位快照
  （`Off`/`Standard`/`Strict`/`Builtins`）**之前** —— 静态属性按文本顺序初始化，反了就会让
  `Standard.DenyBashPatterns` 静默变成 null，表现为「危险命令表失效」而不是编译错误
  （这个坑在本仓库出现过两次，`StaticPatternTablesAreLoaded` 是它的守卫）。


PiWeft 新增 `settings.security`（pi 没有这个键），语义对齐 PiDeck 的 `security/policy.ts` + 内置扩展 `pi-deck-security-gate.ts`，实现在 `src/PiWeft.CodingAgent/Security/ToolSecurity.cs`，挂在**所有工具调用的唯一汇合点** `GuardedBeforeToolCallAsync`（计划模式检查之后、宿主钩子之前）。直接调用、codemode 里的嵌套调用、MCP 工具都走这条路 —— 嵌套调用不是绕过口。

| `level` | 行为 |
|---|---|
| `off`（默认） | 完全放行。引入安全门不改变任何既有行为，也不破坏 pi 兼容语义 |
| `standard` | 敏感文件读写均拒绝；匹配危险模式的 shell 命令拒绝；目录不限制 |
| `strict` | 在 `standard` 之上加工作目录边界：**写入**目录外一律拒绝，**读**降级放行（与 PiDeck 一致） |

```json
{
  "security": {
    "level": "strict",
    "pathPolicy": "custom",
    "customAllowDirs": ["~/work/shared"],
    "denyDirs": ["~/work/secrets"],
    "protectSensitivePaths": true,
    "denyBashPatterns": ["\\brm\\s+-[a-z]*[rf]"]
  }
}
```

- **只读全局设置**：项目里的 `.pi/settings.json` 可以随仓库分发，能改自己的安全等级等于让仓库关掉用户的防护。`level` 写错（如 `strcit`）或缺失一律按 `off` 处理 —— 写错的配置不该反向把用户的工作流卡死。
- **敏感路径**与 PiDeck 的 `DEFAULT_SENSITIVE_PATH_PATTERNS` 逐条一致：`.env` 家族、`.git` 元数据、`id_rsa`/`id_ed25519`/`id_ecdsa`、包管理凭据、`.pem`/`.key`/`.p12`。按**解析过符号链接的真实路径**匹配，所以 `notes.txt` 软链到 `.env` 也会命中。
- **目录边界同样解析真实路径**：不存在的尾部按「已存在的最近祖先是真实路径」拼回，`../` 与软链目录都绕不过去。Windows/macOS 大小写不敏感，Linux 敏感。
- **`denyDirs` 没有绕过出口**：读写都拒绝，与 PiDeck 一致。空行与空串在配置边界被丢掉 —— 空目录会被当成「根为 `/`」从而匹配一切。
- **危险命令表刻意只收不可逆动作**：删除根/家目录、`mkfs`、直接写块设备、fork bomb、关机、提权式改根权限、Windows 的 `format`/`rd /s /q` 根目录。`cp`/`mv`/`npm install`/`git commit` 这类在 PiDeck 里是「问一下」，本端口没有审批窗口，拒绝它们会直接毁掉正常工作流。
- **不是全覆盖**：MCP 与自定义工具没有可判定的路径语义，一律放行（与 PiDeck 只覆盖 `SECURITY_TOOL_LABELS` 里那几个工具一致）。bash 只能按命令文本匹配，不能约束它实际碰到的路径。
- 配置入口：桌面设置 → 配置管理 → 源文件 → `settings.json`（与其它 pi 原生配置同一处编辑）。
- 测试：`tests/PiWeft.CodingAgent.Tests/ToolSecurityTests.cs`（纯策略 35 项）+ `ToolSecuritySessionTests.cs`（接线 5 项：`off` 不干预、`strict` 拦住越界写、`standard` 拦敏感读与危险命令、codemode 嵌套调用同门、设置驱动）。

## 虚拟模型 route 与状态

`models.json` 的虚拟模型既支持静态目标，也支持动态 JS 路由：

```json
{
  "providers": {
    "router": {
      "models": [
        {
          "id": "auto",
          "name": "Auto",
          "virtual": {
            "thinkingLevels": ["off", "high"],
            "route": "const target = ctx.modelRegistry.find('p', request.reason === 'retry' ? 'b' : 'a'); return { model: target, thinkingLevel: 'low', state: { reason: request.reason } };",
            "timeoutMs": 5000
          }
        }
      ]
    }
  }
}
```

- `request` 含 `model`、`thinkingLevel`、`reason`（`user`/`continuation`/`retry`/`direct`）、`previous`、`failed`、`state` 与 `messages`。
- `ctx.modelRegistry` 提供 `find`、`findOfType`、`getModelsOfType`、`getAvailableOfType` 与 `models`；只能路由到物理模型。
- 返回 `{ model: { provider, id }, thinkingLevel?, state? }`；`route` 可以是源码字符串或 `{ "code": "...", "timeoutMs": 5000 }`。
- `state` 写成 `pi.virtual-model-state` 自定义条目（`{ provider, modelId, state }`），按会话分支恢复；`direct` 请求（压缩、分支摘要、连接探测）不读写状态。
- 路由失败、目标不是物理模型或脚本超时会让本次请求以错误结束，不会落到 `pi-virtual`。

## 剩余差距

稳定编号沿用历史评估（`PI-*` 为 pi 能力，`D-*` 为桌面工作台，`X-*` 为参照方向），只列仍未完成的事项。

| 编号 | 能力 | 状态与剩余差距 |
|---|---|---|
| PI-01 | JS/TS 扩展宿主 | **JavaScript 扩展已可用**：按 pi 约定发现（全局 `<agentDir>/extensions` + 受信任项目的 `.pi/extensions`），每扩展一个 Jint 引擎跑在专属线程上，注册工具/命令/事件钩子，工具调用前派发 `tool_call`；`/reload` 与关闭会话时释放。**TypeScript 仍不支持**（本端口无转译器），但会逐条说明原因而不是静默不加载 |
| PI-02 | 扩展 UI 协议 | 对话框与 fire-and-forget 方法（`setStatus`/`setWidget`/`setTitle`/`set_editor_text`）均已按 pi 顶层字段实现，RPC/控制台/桌面有对应落点；**扩展宿主已接上**：`pi.ui.notify/confirm/input/select/setStatus/setWidget/setTitle` 可由扩展调用（没有宿主界面时交互方法按取消处理并记诊断）。仍未覆盖：自定义 renderer、`setEditorText` 的扩展侧入口 |
| PI-03 | Pi 包管理 | 已实现 settings `packages` 来源解析、npm/git/本地安装与 `pi install/list/remove/update`；未覆盖 npm 依赖冲突治理、包图库与资源筛选界面 |
| PI-04 | 完整终端 TUI | 仍为按行 REPL；缺少多行编辑、`$EDITOR`、剪贴板图片粘贴进 TUI、终端流式工具展示、补全、选择器、终端图片和完整快捷键 |
| PI-05 | codemode / tool_search | 已实现；余项是 pi prelude 的 lockdown（见 [codemode 与 Jint](03-codemode.md)） |
| PI-06 | MCP 高级运行时 | 已覆盖 OAuth 授权码 + PKCE、客户端凭据、`oauth` 对象、`callbackPort`/`clientName`/`authServerMetadataUrl`、401 刷新重放、HTTP 通知流与反向应答；**授权流程抽成 `McpLogin` 由 CLI 与桌面共用**（桌面配置页每个 HTTP 服务器一行显示 未授权/已授权/令牌已过期，带授权·重新授权·清除凭据）；未覆盖 `cimd`、完整 `callbackUrl` 路径、与 pi 共享 `mcp-auth.json` 令牌格式 |
| PI-07 | 认证覆盖 | 已支持 Codex、OpenAI ChatGPT、GitHub Copilot、Anthropic、OpenRouter、xAI、Kimi、Meta、Radius OAuth 与全 Provider API key；余项是 AWS profile/ECS/IRSA 完整 ambient 链与部分企业登录细分 |
| PI-08 | 动态模型目录 | 已实现 pi.dev 目录刷新、离线缓存、账号级 OpenRouter/Radius/GitHub Copilot 目录、**条件请求（If-None-Match / If-Modified-Since，304 不重传不覆盖）与 6 小时刷新节流**（`pi update --models` 用 `force` 绕过）；余项是更细的账号能力同步与目录增量合并 |
| PI-09 | 高级模型运行时 | 已实现虚拟模型（静态目标 + `models.json` 动态 JS route + 路由状态条目）、cache warming（含 `cache_warming_decision` 扩展回调）、模型级 `samplingParams`、Codex WebSocket、生图/分类网关；余项是扩展宿主动态注册 route、`websocket-cached` 连接复用、Codex zstd 与完整 continuation，以及生图/分类入口接线 |
| PI-10 | 资源配置与主题 | 资源发现、主题 JSON 解析、主题目录热重载与资源页已实现；余项是独立资源编辑界面与包资源筛选可视化 |
| PI-11 | 项目信任 | 已实现 `trust.json`、父目录查找、显式覆盖、默认 `ask`、项目资源门控与启动询问 |
| PI-12 | 行内 shell 与会话环境变量 | 已实现 `!`/`!!` 与 bash（Windows 上还有 powershell）的 `PI_*` 注入 |
| PI-13 | Windows powershell 工具 | 已实现（Windows） |
| PI-14 | CLI 键位绑定 | 未实现 `keybindings.json` 与 `/hotkeys`；桌面快捷键体系独立 |
| PI-15 | pi 专属斜杠命令 | `/copy`、`/import`、`/share`、`/bug`、`/llama`、`/settings` 已实现 |
| D-01 | 问询、待办、计划 | 已实现原生 todo、ask_question、计划模式；未覆盖独立后台 Ask 工作流 |
| D-02 | 文件工作台 | 已实现目录导航、文本编辑、修改汇总、统一/并排 diff；未覆盖多文件标签、草稿本、跨行块注释、内嵌浏览器 |
| D-03 | 内置终端 | 已实现 MiniPty 与主要 VT 行为；未覆盖 macOS/Windows 实机回归、完整 VT100 边界、多标签、快照回放 |
| D-04 | 完整 Git 工作流 | 已实现暂存/提交/分支、目录树、AI 提交摘要、源代码管理图、比较更改、远端同步（fetch/pull/push，含首次推送自动建跟踪）与 stash；检查点仍由会话自动快照提供；未覆盖冲突解决界面、rebase/cherry-pick/reset/drop 等改写历史的操作、多远端选择 |
| D-05 | 文件检查点/回滚 | 已实现 `refs/pi-checkpoints/<id>` 快照与回滚；未覆盖快照与助手回合关联、清理后新增未跟踪文件、检查点分页与 diff 预览 |
| D-06 | 配置深度 | 已实现连接测试、远程模型拉取、价格表单；未覆盖供应商迁移、收藏/隐藏/排序、Azure/Vertex/Bedrock 专用目录接口、桌面 OAuth |
| D-07 | 用量系统 | 已实现余额/积分查询、跨会话统计与热力图；未覆盖订阅窗口/重置时间、火山 AK/SK 等专用解析、热力图下钻 |
| D-08 | 技能/插件生态 | 已实现资源发现与资源页；缺少商店、中文提示词库、包安装可视化入口和 PiDeck 独立宿主插件系统 |
| D-09 | 多后端/导入 | 已实现 Claude Code 与 Codex CLI 的会话导入（`pi import` + 桌面弹窗）；缺少 DSH、ACP 外部 Agent 后端，以及 OpenCode / ZCode / Cursor / Kimi 等其余格式（这些源是 SQLite 库，本端口没有 SQLite 依赖） |
| D-10 | 多模态工作流 | 图片输入/预览已有；缺少视觉桥、独立生图入口、语音转写/视频生成工作流 |
| D-11 | 远程/自动化 | 缺少局域网 Web、实时推送、外网访问令牌/隧道、飞书和定时任务 |
| D-12 | 桌面集成 | 缺少托盘、系统通知/历史、自动更新、快捷任务浮窗、桌面宠物；代理、进程监控和诊断包也未完整对齐 |
| D-13 | 交互/长历史 | 缺少项目级置顶/排序、标签拖拽/分屏、完整消息编辑删除、Mermaid/KaTeX。历史分页**经实测后决定不做**（中位会话打开约 5 ms、p90 80 ms，且条数切页不构成内存上界），全部历史仍整份在内存；侧栏摘要改为磁盘持久化（启动 1046 ms → 52 ms） |
| D-14 | 工具调用审批 | 已实现完整安全门：`allow`/`ask`/`deny` 工具动作矩阵（ask 走宿主确认对话框，没有审批人时按拒绝处理）、敏感文件、目录边界、危险命令表、禁地；设置 → 运行 → 安全门 提供总开关、默认档、档位编辑与自定义档增删；会话输入框的「安全」按钮按会话覆盖档位（`sessionLevels`）。未覆盖：PiDeck 的「按会话」独立存储位置（本项目存在全局 settings 的 security 段）与 CLI 侧的档位切换命令 |
| X-01 | 原生子代理、并行/链式编排、worktree 隔离 | 高价值但涉及执行所有权、取消、凭据和补丁合并 |
| X-02 | ACP、IDE 集成 | 与 D-09 有关联；服务端协议与桌面接入外部后端应分别规划 |
| X-03 | LSP/AST/DAP | 语义检索、诊断、结构化编辑、调试；不是基础工具的直接兼容要求 |
| X-04 | 长期记忆/检索 | 跨会话存储、索引、embedding 和隐私；不是会话压缩的替代 |
| X-05 | 后台任务/PTY 服务监督 | 需要生命周期、日志预算、取消和孤儿进程治理 |
| X-06 | 技能安全审计、能力/路径约束 | 文件工具路径限制不等于 Shell/进程的 OS 沙箱 |
| X-07 | Web 搜索、浏览器/桌面自动化、增强编辑/媒体工具 | 按产品需求选择，可以通过 MCP 或宿主工具提供 |

## 验证

```sh
xvfb-run -a dotnet test PiWeft.slnx --no-restore -v minimal
dotnet publish src/PiWeft.Desktop/PiWeft.Desktop.csproj -c Release -r linux-x64 --no-restore -v minimal

xvfb-run -a -s '-screen 0 1440x900x24' bash tools/desktop-parity-smoke.sh
PI_CODING_AGENT_DIR=$(mktemp -d) xvfb-run -a \
  src/PiWeft.Desktop/bin/Release/net10.0/linux-x64/publish/piweft-desktop --theme-smoke --cwd /tmp
xvfb-run -a -s '-screen 0 1600x1000x24' bash tools/desktop-statistics-smoke.sh
```

截图脚本使用独立 fixture agent 目录，不读取真实密钥或会话。修改进程环境变量的测试必须加入 `process-environment` 串行集合。MewUI 命名样式按目标控件类型校验；包版 Key 枚举没有标点键，不能根据上游 main 源码假设 API 可用。Windows/macOS 原生 GUI、发布包以及 PiDeck 完整 e2e 仍需独立验证。
