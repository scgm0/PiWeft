# PiWeft.Markdown

`src/PiWeft.Markdown` 是桌面会话使用的增量 Markdown 富文本控件。目标不是把 MewUI 的
`MarkdownPresenter` 改得更快，而是把“增量”放在正确的层：**解析可以全量，呈现树和文本布局必须增量**。

## 设计

### 1. 文档模型

`MarkdownParser` 用 Markdig 解析源文，映射成不可变的 `MarkdownDocument`：

- 顶层 `MarkdownBlock`：段落、标题、代码、分隔线、引用、列表、表格。
- 内联 `MarkdownRun`：文本、样式位、链接、任务状态。
- 每个块带一个 **模型哈希**（文本、样式、URL、锚点、子块），用于跨 revision 识别“呈现上相等”的块。
- `MarkdownTextUnit` 按渲染顺序收集可选中文本，选区、复制和 `SelectedText` 都从这里构建。

哈希包含映射后的模型而不是源文切片，因此引用式链接、脚注编号、setext 标题等“源文没变但解析结果变了”的
情况不会被错误复用。

### 2. 块级 diff 与元素复用

`MarkdownView` 在源文变化时：

1. 解析：默认全量；当新源文是旧源文的纯追加且没有链接引用定义时，只重新解析最后一个顶层块
   （按 `SourceStart` 切出尾部、映射后把 source offset 平移回原坐标）。单块文档（整段列表/代码/段落）
   没有可复用的前缀，仍走全量。
2. 对新旧顶层块的模型哈希做最长公共前缀/后缀 diff；
3. 前缀和后缀块复用原来的元素与文本布局；
4. 只重建中间变化的块；
5. 如果变化只是最后一个块的尾部追加/尾部改写，则**原位更新**该元素，不重建。

流式追加的典型帧因此只包含“解析 + diff + 一个尾块的布局”，与历史长度无关。

段落/标题、代码块、引用、列表和表格都支持**尾块原位更新**：容器结构不变时，只更新最后一个子块
（或最后一行的最后一个单元格），容器元素和前面已冻结的子块保留。新增列表项、引用段落或表格行时
直接向现有容器追加子元素，已有项不重建；只有删除/在中间插入项时才整块重建一次。这消除了逐 token
重建导致的闪烁。

### 3. 流式尾块修补与 Append 输入

流式期间（`IsStreaming = true`）解析尾块前，`MarkdownStreamRepair` 先扫描尾串，把 EOF 处仍未闭合的
行内语法临时闭合：未闭合的 `**`/`*`/`_`、反引号代码段、`](` URL 和 `~~`/`==`/`++` 配对，在帧末追加
等长的**不可见闭合符**（`**`、`` ` ``、`)`）。修补只追加标记字符、不产生可见文本、不能新建块，因此：

- 渲染上，"hello **wor" 在流式期间直接显示为粗体 "wor"，闭合瞬间不再从字面星号翻转为粗体；
- 围栏与缩进代码块不修：未闭合时本来就把余下内容当代码显示，追加闭合栅栏是无效操作；
- HTML 标记（含注释）不修：Markdig 不让段落内的行内注释中断块结构，尾块局部扫描无法与全量解析的
  注释吞噬语义保持一致，差分恒等式（流式解析 ≡ 全量解析修补后的源文）会在跨行未闭合注释上破裂；
- 扫描是保守启发式：行内代码段只在本行找闭合、空行与块起始清空未闭合栈、反斜杠转义与左右
  flanking 规则参与判定。修补结果可能猜错（作者本意是字面星号时），定稿帧整体纠正。

修补与定稿通过 `MarkdownDocument.TailRepaired` 衔接：流式帧打了修补，`IsStreaming` 切回 false 时
按同一 `tailStart` 再做一次**无修补**的尾块解析（O(tail)）；最终模型总是全量解析的精确结果，
且修补猜对时哈希不变、元素零重建。

`Append(delta)` 是流式输入的首选入口：源文累积在控件内部的 `StringBuilder` 里，帧与帧之间用
builder 引用相等证明“纯追加”，跳过整串前缀比较；`MarkdownDocument.Source` 变为惰性物化，流式帧
不产生整串字符串拷贝。`Source` 赋值与 `Append` 可混用，切换时自动回退全量解析。桌面端
`AssistantMessageView.FeedMarkdown` 已接入：流式帧只喂增量，编辑重发/收缩/换行绑定自动回退整体
赋值，同一文本重复到达的状态帧不再触发任何更新。

### 4. 扩展点：ConfigurePipeline 与自定义块渲染器

`MarkdownOptions.ConfigurePipeline` 接收 Markdig 管线 builder，可启用 `UseMathematics()`、
`UseFootnotes()` 等扩展（每份 options 实例缓存一条管线，弱引用回收；含委托的实例按引用比较）。
没有内建映射的叶子块映射为 `MarkdownBlockKind.Custom`，`CustomKind` 携带 Markdig 节点类型名，
`MarkdownRenderers.RegisterBlock(类型名, 工厂)` 提供呈现；工厂返回 null 或未注册时退回字面文本
（有子块则渲染子块）。`MarkdownRenderContext` 携带视图、主题、模型，`RenderChildren()` 把子块交给
内建管线。自定义元素实现 `IMarkdownCustomView` 即可参与尾块原位更新（流式中的数学块每帧收到新模型），
否则按模型哈希变化整块重建；未注册的自定义块按段落渲染字面文本。注意 `MathBlock` 继承自
`CodeBlock`，映射顺序上必须在通用代码分支之前拦截；Markdig 未知行内节点的 `ToString()` 是类型名，
因此行内回退一律取源文 span。

内建 HTML 处理只有一层**行内白名单**（`UseHtmlFormatting`，默认开）：`<br>` 转硬换行，
`b/strong/i/em/u/s/strike/del/mark/ins/code/tt` 映射到样式位，`sub/sup` 缩小字号（文本引擎没有
基线偏移），`span/font` 透明、属性一律不解释，完整行内注释节点直接隐藏。未知标签和块级 HTML 保持
字面文本，链接、图片、脚本无法通过该通道进入渲染。

### 5. 图片与 resolver

图片走宿主提供的 `IMarkdownImageResolver` 异步加载：没有 resolver 时不碰文件系统与网络，图片只显示
alt 文本。resolver 自持解码、策略（协议白名单、字节上限）与缓存；视图按段落缓存结果（含失败——
坏 URL 不会在流式帧里反复请求），段落销毁即取消在途请求。渲染上，图片 run 仍是行内 run（选区/复制
继续看到 alt 文本），解码完成后在段落文本下方按原始宽高缩放绘制（不超过段落宽度，不放大），到达
瞬间置空对应顶层块的缓存高度并触发重测——嵌套段落（列表/表格/引用）沿视觉树找到宿主块。流式期间
run 逐帧重映射，图片元素与已解码源按 URL 复用。点击已解析图片经 `ImageRequested`（带原始 URL、
解析后 URL 与 `ImageSource`）交给宿主；桌面端接与附件同一套全尺寸预览。桌面 `MarkdownImageLoader`
支持 data URI（复用 `ChatImages` 去重与复制字节）、本地 file URI 与 http(s)（10 s 超时、8 MiB 上限，
禁用之外协议），LRU 缓存 48 条；data URI 一律从源文原始字符串解析，`Uri` 规范化会损坏 base64。

### 6. GFM Alerts、front matter 与链接白名单

Markdig 1.x 不识别 GitHub alerts（`> [!NOTE]`），解析器在映射 QuoteBlock 时自行识别：首段文本以
`[!NOTE]`/`[!TIP]`/`[!IMPORTANT]`/`[!WARNING]`/`[!CAUTION]`（大小写不敏感）开头即打上
`MarkdownBlock.Alert` 并把标记（含其后空白，可跨 run）从首段剥掉；标记独占一行时首段整体移除。
视图侧渲染彩色左缘 + 同色淡底 + 加粗标签行（标签是装饰，不进文本单元；容器复用与原位更新按标签行
偏移对位，标记增删或更换类种时整块重建），颜色与文案经
`MarkdownTheme.AlertColorSelector` / `AlertLabel` 注入，未注入时回退 Accent 与英文首字母大写。
`YamlFrontMatterBlock`（继承自 CodeBlock，须在通用代码分支前拦截）在宿主启用 YAML 扩展时整块跳过；
`EmojiInline`（继承自 CodeInline，同理先行匹配）用 `ToString()` 的字符而不是源文 span。
`MarkdownOptions.LinkSchemes` 把链接协议白名单下沉到控件库：设置后，白名单之外的绝对协议渲染为
纯文本、图片 run 不会进入 resolver（锚点与相对 URL 恒通过）；null 保持旧行为，宿主的打开侧校验
仍然是最后一道。

### 7. 跨实例块高度缓存

首次进入超大历史消息必须测量全部块（精确高度的代价）；重进同一消息不需要。`MarkdownView` 维护一个
进程级 LRU（4096 条）高度缓存，键为「块模型哈希 + 字体/主题/DPI + 精确宽度」——键命中的高度与重新
测量逐像素一致，因此离屏块不再创建元素即可拿到高度，重进成本从 O(全部块) 降为 O(视口)。含图片的
块不入缓存：其高度依赖异步解析，模型哈希不覆盖。流式期间（`IsStreaming`）一概不入缓存：尾块每帧
都是新哈希，会把历史条目挤出 LRU。主题记录带委托字段按引用比较——**宿主必须全视图共享同一份
Options 与 Theme 实例**（桌面端为 `AssistantMessageView.SharedMarkdownOptions/SharedMarkdownTheme`），
每视图新建一份会让跨实例缓存永不命中。

### 8. 段内分块布局

`MarkdownParagraphView` 把段落切成若干 `Chunk`：

- 每个 chunk 是一个独立的 `ITextLayout`，在**已闭合行边界**结束；
- 最后一个 chunk 是开放行，追加时只重排它；
- 开放 chunk 超过 2 K 字符或 64 行时，在最后一个已闭合行处切出新的冻结 chunk；
- 尾部改写（关闭 `**`、反引号、链接等）从第一个变化偏移所在的 chunk 开始重排，前面的冻结 chunk 保留；
- 冻结 chunk 用 `TextLayoutCachePolicy.Owner` 缓存，绘制时按 `chunk.Top` 堆叠；
- 选区、链接命中、行内代码背景按 chunk 求交，全局偏移 = `chunk.Start + localOffset`；
- chunk 边界就是行边界，而行间距只存在于“行与行之间”：因此堆叠时除最后一个 chunk 外都要补上
  一份行间距，否则流式拼接出来的段落会比一次成型的目标矮一截（每丢一个边界少一行间距）。

分块布局与整段布局的行数、行高、baseline 和总高度在测试中逐行比对一致。

### 9. 代码块

`MarkdownCodeBlockView` 使用只读 `MultiLineTextBox` + 水平 `ScrollViewer`：

- 代码块使用「单卡片 + 紧凑顶栏」的外观：1 px 主题边框、`CardCornerRadius` 圆角、
  34 DIP 顶栏和独立的代码表面，避免盒中盒；
- 代码顶栏左侧是语言 pill，右侧是一枚**图标动作**（28×24 扁平按钮、圆角 5：静止 62% 不透明、悬停恢复全强度，
  提示文案走 `CodeCopyTooltip`）。图标由宿主经 `MarkdownTheme.CodeCopyIcon` / `CodeCopiedIcon` 注入 —— 桌面端就是
  `tools/fetch-lucide-icons.sh` 那套内嵌 Lucide SVG（`copy`，复制成功后短暂换成 `check`）；
  **控件库自己不画图标**，也不引 SVG 依赖，宿主没给图标时退回本地化文本标签。
  动作内容一律在按钮里两向居中：宿主的图标常常只设了纵向居中，固定宽度下水平方向默认 `Stretch`，
  图标就会贴在按钮左侧（看着就是「图标没居中」）；
  代码体左右各 12 DIP 内边距；
- 追加走 `ReplaceRange`，语法着色按行缓存；
- 关闭围栏只切换状态，不重建控件；
- 自定义代码视图实现 `IMarkdownCodeView` 即可参与原位更新；否则该块在变化时重建。

行内代码不再画字形矩形底，而是在文字范围外绘制带内边距、圆角和细边框的 chip；
布局文本在代码 run 两侧插入 thin space，保证 chip 与相邻字形之间有**真实的外部间距**，而不是只把背景画宽；
字号按 `InlineCodeFontScale`（默认 0.92）缩放，颜色通过 `InlineCodeBackgroundSelector`
和 `InlineCodeBorderSelector` 跟随浅色/深色主题（默认取主题的按钮面与控件边框）。

### 10. 精确高度块回收

`MarkdownView` 只回收**顶层块元素**，不内嵌滚动视口：

- 首次布局测量全部块，得到精确高度和 prefix；
- 视口内以及上下各 2 个块保持已附着；视口外再保留 ±12 个已测块作为 detached 缓存，避免边界抖动反复 attach/detach；
- 列表使用固定的标记列 + 行高缓存面板；流式追加只重测尾项，不再因为 `Grid` 的星号列多轮测量而重排整张列表；
- 表格同样是一张单卡片：表头有独立浅色底，数据行斑马纹，行之间只有水平分隔线，最后一行的下边框由外卡片承担；列宽按内容长度取平方根加权，避免短的数字列占掉长描述列的空间；表格正文使用 `TableFontSizeScale`（默认 0.9）缩小一档；
- 只保留与外层 `ScrollViewer` 视口相交（上下各留一段 overscan）的块元素与文本布局；
- 离屏块释放元素，但保留模型和精确高度；
- 总高度始终精确，外层可变高度虚拟化的行高缓存不会因回收而失效；
- 有键盘焦点的块即使离屏也保留；
- 虚拟化行被摘下再挂回时（`OnParentChanged` / `OnVisualRootChanged`），控件强制重测量并重排可见块。否则回收期间被 detach 的块会沿用**回收前的缓存测量**，正文一片空白，直到读者再滚一下才恢复；
- 流式尾部在视口外时只更新模型，不创建/布局元素。

这与 `MarkdownViewer` 的估算高度虚拟化不同：后者拥有自己的视口，可以用估算值并修正滚动偏移；
消息行内部的回收必须保持行高稳定，否则会和外层虚拟化的 anchor 逻辑互相拉扯。

### 11. 选区、链接与主题

- 选区是文档级的 `(TextUnit, Offset)` 范围；`SelectedText` 从文档模型构建，因此包含未 realize 的块。
- 列表、表格、引用内部的每个段落各自持有 `TextUnit`；容器被复用时按位置重新分配文本单元，因此这些嵌套文本与正文一样参与鼠标拖选、双击选词、全选和 PRIMARY 发布。
- 源文变化时如果新旧文本共享足够前缀（纯追加、尾部改写、任务切换），保留选区并从新模型重新计算 `SelectedText`；只有整篇替换才清空选区。
- 复制/全选通过 `StandardCommands.Copy` / `StandardCommands.SelectAll` 注册，右键菜单提供同样入口。
- 链接由段落视图命中，`LinkRequested` 交给宿主；`#anchor` 在控件内滚动到目标块。
- 键盘链接遍历：Tab/Shift+Tab 先在段落内的链接间移动，段落边缘冒泡到视图后跨块移动到下一个（或上一个）
  含链接的文本单元；目标块未 realize 时先按前向顶对齐/反向底对齐滚动，Background 优先级的 dispatcher
  回调在布局完成后补挂焦点。Enter/Space 激活当前链接。文档两端交还默认焦点导航，不影响 Tab 移出控件。
- 控件本身和链接段落都可以获得焦点。宿主通过 `FocusableElementCreated` 给段落挂焦点守卫（桌面端见
  `TextFocusScrollGuard`）；守卫在焦点进入阅读控件时记录**所有祖先 ScrollViewer**，并撤回这次焦点事务
  引起的位移。否则一个比执行过程组体还高的 `MarkdownView` 被聚焦时，MewUI 的 focus-into-view 会把组体
  和会话滚到视图顶部/底部，看起来像进度跳变；虚拟化行还可能被暂时释放，直到滚动后才重建。
- 主题换色走 MewUI 的 `WithTheme`；字号/DPI 变化会重建元素，因为代码块和列表标记把字体烘焙进了子元素。
- 代码块背景通过 `MarkdownTheme.CodeBackgroundSelector` 跟随 UI 主题。

### 12. 行距（默认视觉节奏）

正文行距以**字号倍率**给出，对齐 PiDeck 的 `--line-height-chat`（1.6）与 `.markdown-body` 标题（1.3）：

| 属性 | 默认 | 作用 |
|---|---:|---|
| `MarkdownTheme.LineHeightScale` | 1.6 | 段落 / 列表 / 引用 / 表格单元的行高倍率 |
| `MarkdownTheme.HeadingLineHeightScale` | 1.3 | 标题行高倍率（字号大，需要更紧的行距） |

倍率最终落到 MewUI 段落样式的 `LineSpacing`（附加行间距，DIP）上：控件先用一个只探索性布局（按
字族/字号/字重/斜体/DPI 缓存，进程内一次）量出字体**自然行高**（HarmonyOS Sans SC 实测 ≈ 1.19 em），
再按 `max(0, 字号 × 倍率 − 自然行高)` 补差。这样定下的几条性质都是有意为之：

- **随字号走**：用户把会话字号从 14 调到 22，行距同步放大，不会出现“字大了、行挤了”；
- **永不压字**：倍率小于字体自然行高（比如某些 CJK 字体本来就 1.5+）时补差为 0，不会让上下行咬在
  一起，也不会裁掉字形；置 0 或负值即关闭行距，退回字体自然行高；
- **块间距仍是块间距**：行间距只加在行与行之间，块的上下缘贴着文字，`BlockSpacing` 继续描述块之间
  的真实空白，不会因行高变大而把段落间距一起撑开；
- **不影响代码块**：代码体是只读 `MultiLineTextBox`，MewUI 0.22.1 没有给它行高开关；
- **选区跟着行框走，不跟着字形带走**：MewUI 的行范围矩形只有一行的自然高度，行间距拉开后
  逐行选区会变成一条条横纹。所以选区底色由 `MarkdownParagraphView` 自己铺（不再走 `TextPaintSpan`），
  每行矩形上下各扩半个行间距：行与行之间无缝相接，单行选区正好一个行框高（16 px → 25.6 DIP）。
  块边缘只会伸进 `BlockSpacing` 一半（≤ 3.3 DIP < 8 DIP），不会盖到相邻块的文字。
  行内代码 chip、`==标记==` 底色与链接下划线的绘制顺序不变（chip → 选区 → 文本绘制）。
  测试见 `SelectionOfAWrappedParagraphHasNoGapsBetweenLines`（逐行 `Bottom == 下一行 Y`）。

**列表条目单独补一份**。行距只存在于“行与行之间”，所以**单行条目**自己拿不到任何行距：不补的话，
字号 16 时正文一行走 25.6 DIP，列表一行只走 19 DIP，列表反而比正文挤；条目里的行内代码 chip
（字高 + 上下内边距 + 1 px 描边，约等于一整行高）会跟相邻条目/chip 直接贴上——这就是“单行代码挤在
一起”的观感。因此 `MarkdownView` 把同一口径的 `ListItemLeading()` 交给 `MarkdownListPanel.AddRow`，
行高等于「子元素高 + 一个行间距」，上下各滞半个行距，标记（弹头 / 序号 / 任务复选框）与内容一起下移，
仍与第一行对齐。于是：

- 单行条目 = 1 个行框（= 正文行距），多行条目 = N 个行框，不会按行重复加；
- 嵌套列表的条目走同一条路径，各层行距与正文一致；
- 松散列表（loose）条目之间的 `BlockSpacing` 与 4 DIP 收尾间距不变。

宿主想改节奏就在共享的主题实例上 `with`：`SharedMarkdownTheme with { LineHeightScale = 1.45 }`。
行距倍率属于 `MarkdownTheme`，而主题记录进了高度缓存的 `MeasureKey`，改值会自动使全部已测高度作废。
测试见 `MarkdownLineSpacingTests`（正文/标题倍率、字号跟随、不压字、关闭退回、换主题重排，
以及列表条目行距、标记与行框对齐）。

## 实测

环境：x64 Debug / Xvfb / MewVG X11，520 DIP 宽，测试见
`tests/PiWeft.Desktop.Tests/MarkdownViewTests.cs`。

| 场景 | 中位 | p90 | 每帧分配 |
|---|---:|---:|---:|
| 32 K、180 个块，尾部新增一段（只解析尾块） | 约 0.7–2 ms | 约 1–4 ms | ~0.1 MiB |
| 24 K 单段落逐段追加（开放尾块分块） | 约 4–7 ms | 约 8–15 ms | ~0.5 MiB |
| 24 K 单段落定稿（只切 `IsStreaming`） | 0.1 ms | — | — |
| 200 项列表尾部追加（行高缓存 + 尾项原位更新） | 约 6–12 ms | 约 10–25 ms | — |
| 32 K 单段落一次性全量布局（改造前基线） | ~79 ms | ~119 ms | ~4.4 MiB |

32 K 单段落一次性全量布局的 79 ms 仍是历史消息首次进入时的成本；流式路径不会走到它，因为段落从第一帧起
就按开放尾块增长。单段落/单列表/单代码块是“整篇只有一个顶层块”的情况，尾部就是全篇，解析成本仍随长度增长；
30 K 单段落实测约 6 ms。列表/表格/引用在结构变化（删除/中间插入）时才整块重建。

## 限制与后续

- 解析在 UI 线程同步执行；32 K 量级足够快。若单条消息达到数百 KB，可加入后台解析 + revision 合并。
- 纯追加时只解析最后一个顶层块；链接引用定义会让该帧回退全量解析（避免早先链接解析结果失效）。
- `MarkdownIncrementalParserTests` 对每 5 个字符的流式前缀做增量解析与全量解析的逐块哈希/纯文本比对；`MarkdownStreamRepairTests` 对流式前缀做三元差分（流式解析 ≡ 全量解析(源文 + 修补后缀)，且除尾块外 ≡ 无修补全量解析）；`MarkdownEdgeCaseTests` 覆盖空文档、CRLF、标题/表头加粗、嵌套列表、任务切换、代码改写、跨容器选区等边界。
- 行内 HTML 只有白名单格式化（见扩展点一节）；块级 HTML、脚注编号/回链的正式视图未实现：脚注/定义列表可经 `ConfigurePipeline` 启用，脚注定义由 FootnoteGroup 以普通段落渲染，polished 视图走 `MarkdownRenderers`。图片按 resolver 结果绘制在文本下方（不与文字同排混排），空 alt 的图片 run 在文本行里仍是 U+FFFC 占位字形；点击在语义上是图片激活，不参与链接 Tab 遍历。
- 流式尾块修补是保守启发式（`MarkdownStreamRepair`）：只修强调/代码段/链接 URL 等行内配对语法；围栏、缩进代码和 HTML 标记不修。修补与作者本意不符时由定稿帧的无修补重解析纠正。修补过的链接 run 的 SourceLength 可能短暂越过真实源文末尾，宿主应只把它用于诊断定位。
- 列表/表格/引用支持尾项原位更新和行/子块追加；删除或在中间插入项时仍整块重建一次。
- 后台解析 + revision 合并仍缓行：32 K 量级同步解析足够快，流式热路径的风险大于收益；数百 KB 单条消息出现时再做。
- 块回收首次进入超大历史消息时仍需测量全部块；这是精确高度的代价，后续可用分批测量或估算模式优化。

## 使用

```csharp
var view = new MarkdownView
{
    FontSize = AppTheme.FontSizeChat,
    Options = new MarkdownOptions
    {
        ConfigurePipeline = builder => builder.UseMathematics(),
    },
    MarkdownTheme = new MarkdownTheme
    {
        CodeFontFamily = AppTheme.MonoFont,
        CodeFontSize = AppTheme.FontSizeCaption,
        CodeBackgroundSelector = theme => AppTheme.For(theme).CodeBackground,
        InlineCodeBackgroundSelector = theme => AppTheme.For(theme).CardBackgroundAlt,
        InlineCodeBorderSelector = theme => AppTheme.For(theme).Border,
        CardCornerRadius = AppTheme.RadiusXl,
        // 图标动作走内嵌 Lucide SVG；控件库不自己画图标。
        CodeCopyIcon = size => AppIcons.Icon("copy", AppStyles.RoleSubtle, size),
        CodeCopiedIcon = size => AppIcons.Icon("check", AppStyles.RoleSuccess, size),
    },
};

view.LinkRequested += args => Open(args.Url);
// 链接段落会成为独立焦点目标；桌面端用它把所有祖先滚动视口的焦点位移撤回。
view.FocusableElementCreated = paragraph => focus.Track(paragraph);
view.CodeTextCreated = (text, language) =>
{
    text.Extensions.Classifiers.Add(new KeywordClassifier(language));
    text.Tag = AppStyles.RolePrimary;
};
view.ImageResolver = imageLoader;   // 不设置则永不读盘/联网，图片只显示 alt 文本
view.ImageRequested += args => { if (args.Image is { } source) Preview(source); };
// GFM alerts 的语义色与中文标签（缺省回退 Accent 与 "Note" 式英文标签）。
view.MarkdownTheme = view.MarkdownTheme with
{
    AlertColorSelector = (theme, kind) => kind switch
    {
        "NOTE" => AppTheme.Info,
        "CAUTION" => AppTheme.Error,
        _ => null,
    },
    AlertLabel = kind => kind switch { "NOTE" => Strings.AlertNote, _ => null },
};
// 链接协议白名单（第二道闸门；宿主的打开侧校验仍是最后一道）。
view.Options = view.Options with { LinkSchemes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "http", "https" } };
view.SelectionChanged += () => PublishPrimary(view.SelectedText);
// 自定义块（例如经 ConfigurePipeline 启用的数学块）：按 Markdig 节点类型名注册。
view.Renderers = new MarkdownRenderers()
    .RegisterBlock("MathBlock", (block, _) => new MathView().Source(block.Text));

view.Source = text;
view.IsStreaming = true;  // 流式
view.Append(delta);       // 流式帧首选：只喂增量，尾块未闭合语法会被临时修补
// ...
view.IsStreaming = false; // 定稿：切状态；修补过的尾块做一次 O(tail) 无修补重解析
```
