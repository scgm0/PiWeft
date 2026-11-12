# PiWeft.Markdown

`src/PiWeft.Markdown` 是桌面会话使用的增量 Markdown 富文本控件。原则：解析可以全量，呈现树与文本布局必须增量。功能对齐见 [01-parity](01-parity.md)，性能数字见 [02-performance](02-performance.md)。

## 文档模型

`MarkdownParser` 用 Markdig 解析并映射为不可变 `MarkdownDocument`：

- 顶层 `MarkdownBlock`：段落、标题、代码、分隔线、引用、列表、表格。
- 内联 `MarkdownRun`：文本、样式位、链接、任务状态。
- 每个块带模型哈希（文本、样式、URL、锚点、子块），用于识别呈现相等的块。
- `MarkdownTextUnit` 按渲染顺序收集可选中文本，选区/复制/`SelectedText` 都从这里构建。

哈希基于映射后的模型而非源文切片，因此引用链接、脚注编号、setext 标题等“源文没变但解析变了”不会错误复用。

## 块级 diff 与流式更新

源文变化时：解析（纯追加且无链接引用定义时只重解析最后一个顶层块）→ 对新旧顶层块哈希做最长公共前后缀 diff → 复用前后缀元素与文本布局 → 只重建中间变化块 → 尾块追加/改写时原位更新。

段落/标题、代码、引用、列表、表格都支持尾块原位更新：容器结构不变时只更新最后一个子块/行/单元格；新增列表项、引用段落或表格行直接追加；删除或中间插入才整块重建。

`Append(delta)` 把源文累积在内部 `StringBuilder`，帧间用引用相等证明纯追加，`MarkdownDocument.Source` 惰性物化。`Source` 与 `Append` 可混用，切换时回退全量解析。

### 流式尾块修补

`IsStreaming` 时，`MarkdownStreamRepair` 在解析尾块前把 EOF 处未闭合的行内语法临时闭合：`**`/`*`/`_`、反引号、`](` URL、`~~`/`==`/`++`。修补只追加不可见闭合符，不产生可见文本、不新建块。

- 围栏/缩进代码不修（未闭合时余下内容本来按代码显示）。
- HTML 标记不修（跨行注释的吞噬语义无法与全量解析保持一致）。
- 扫描是保守启发式：行内代码只在本行找闭合，空行/块起始清空栈，反斜杠与 flanking 规则参与判定；猜错由定稿纠正。
- 定稿时按同一 `tailStart` 做一次无修补重解析（O(tail)），最终模型总是全量解析结果；修补正确时哈希不变、元素零重建。
- 尾解析前先丢掉 `SourceStart >= tailStart` 的旧块，不只丢最后一块。表格行停在半个 `|` 上时，Markdig 会在表格前多映射一个没有 run 的空段落（与表格同起点）；映射阶段直接丢弃这种段落，否则每帧保留一个，列表与表格之间会越叠越空。

## 扩展点

`MarkdownOptions.ConfigurePipeline` 接收 Markdig 管线 builder（可启用数学、脚注等）；每份 options 缓存一条管线，含委托的实例按引用比较。未映射的叶子块成为 `MarkdownBlockKind.Custom`，`CustomKind` 保存节点类型名，`MarkdownRenderers.RegisterBlock(type, factory)` 提供呈现；返回 null 或未注册时退回字面文本。`MarkdownRenderContext` 提供视图、主题、模型与 `RenderChildren()`。自定义元素实现 `IMarkdownCustomView` 可参与尾块原位更新。

内建 HTML 只有行内白名单（`UseHtmlFormatting`，默认开）：`<br>` 转硬换行；`b/strong/i/em/u/s/strike/del/mark/ins/code/tt` 映射样式位；`sub/sup` 缩小字号；`span/font` 透明且不解释属性；完整行内注释隐藏。未知标签与块级 HTML 保持字面文本，链接/图片/脚本无法进入渲染。

## 图片

图片走宿主 `IMarkdownImageResolver` 异步加载；没有 resolver 时不碰文件系统/网络，只显示 alt 文本。视图按段落缓存结果（含失败），段落销毁取消在途请求。图片 run 仍是行内 run（选区/复制看到 alt），解码后在段落文本下方按原始宽高缩放绘制（不超过段落宽度，不放大）；到达时清空顶层块高度缓存并重测。流式期间按 URL 复用已解码源。点击经 `ImageRequested` 交给宿主。桌面 `MarkdownImageLoader` 支持 data URI、`file:` 与 http(s)（10 秒超时、8 MiB 上限、LRU 48）；data URI 从源文原始字符串解析，避免 `Uri` 规范化损坏 base64。

## GFM Alerts 与链接

Markdig 1.x 不识别 `> [!NOTE]`：解析 QuoteBlock 时识别 `NOTE`/`TIP`/`IMPORTANT`/`WARNING`/`CAUTION`，打上 `Alert` 并从首段剥掉标记（独占一行时移除首段）。视图渲染彩色左缘 + 同色淡底 + 加粗标签；标签是装饰，不进文本单元。颜色/文案经 `MarkdownTheme.AlertColorSelector` / `AlertLabel` 注入，缺省回退 Accent 与英文标签。

`YamlFrontMatterBlock`（继承 CodeBlock）在启用 YAML 时整块跳过；`EmojiInline`（继承 CodeInline）取 `ToString()` 字符。`MarkdownOptions.LinkSchemes` 把协议白名单下沉到控件库：白名单外绝对协议渲染为纯文本，图片不进入 resolver；锚点与相对 URL 恒通过。

## 高度缓存与分块布局

`MarkdownView` 维护进程级 LRU（4096 条）块高度缓存，键为「块模型哈希 + 字体/主题/DPI + 精确宽度」；命中高度与重测逐像素一致。含图片的块与流式尾块不入缓存。宿主必须全视图共用同一份 Options 与 Theme 实例，否则缓存永不命中。

块的排布矩形就是它那一轮的测量高度，绘制不再越过它：块在 arrange 之后才长高（滚动中补实现新块、外层漏测）时，超出已排布高度的段落这一帧不画，`MarkdownView` 再把每个块裁到自己的矩形内；下一次测量把行高补齐后正常绘制。少一帧正文，好过正文叠到下一块或下一行上。

`MarkdownParagraphView` 把段落切成 `Chunk`：

- 每个 chunk 是独立 `ITextLayout`，在已闭合行边界结束；最后一个是开放行。
- 开放 chunk 超过 2 K 字符或 64 行时，在最后一个已闭合行处切出冻结 chunk。
- 尾部改写从第一个变化偏移所在 chunk 重排，前面冻结 chunk 保留。
- 冻结 chunk 用 `TextLayoutCachePolicy.Owner` 缓存，绘制按 `chunk.Top` 堆叠。
- 选区、链接命中、行内代码背景按 chunk 求交，全局偏移 = `chunk.Start + localOffset`。
- 堆叠时除最后一个 chunk 外补一份行间距，否则流式段落会比一次成型矮一截。

分块与整段布局的行数、行高、baseline、总高度在测试中逐行一致。

### 宽度往返与排布一致

块行高必须等于内部实际文本高度。两条路径曾破坏不变量：

1. 高度缓存命中时跳过重测：宽度变化后回到旧宽度会给出错误高度。现在只对没有元素的槽位使用缓存，已附着块一律重测。
2. 渲染期在别的宽度补版面：只在渲染宽度与测量宽度一致时补，且补出的文本高度超过父级高度时这一帧不画。

`MarkdownLayoutConsistencyTests` 用宽度往返与 40 帧流式追加守住「版面宽度 = 排布宽度」「父级高度 ≥ 排版高度」。

### 流式尾块不要用 transient

尾块每帧换文本，不要标 `TextDrawOptions.Transient = true`：MewVG 的临时文本槽位只在窗口帧 `EndFrame` 回收，离屏帧会话不会释放，60 fps 下约 6 MiB/s 增长。尾块走普通文本缓存，受 16 MiB LRU 约束，滚动/定稿自然淘汰。

## 代码块、回收、选区

- `MarkdownCodeBlockView`：只读 `MultiLineTextBox` + 水平 `ScrollViewer`；单卡片 + 34 DIP 顶栏；追加走 `ReplaceRange`，语法着色按行缓存；关闭围栏只切状态。自定义视图实现 `IMarkdownCodeView` 可原位更新。行内代码画带内边距/圆角/细边框的 chip，字号按 `InlineCodeFontScale`（默认 0.92）。
- 回收只针对顶层块元素：首次测量全部块得到精确高度；视口内及上下各 2 块保持附着，视口外保留 ±12 个 detached 缓存；离屏释放元素但保留模型与高度；有焦点的块即使离屏也保留；虚拟化行摘下再挂回时强制重测，避免沿用回收前测量留下空白。
- 选区是文档级 `(TextUnit, Offset)`；`SelectedText` 从模型构建，包含未 realize 块。源文变化且共享足够前缀时保留选区并重算文本；整篇替换才清空。
- 链接由段落命中，`LinkRequested` 交给宿主；`#anchor` 在控件内滚动。Tab/Shift+Tab 在段落内链接间移动，越界冒泡到视图后跨块导航；目标块未 realize 时先滚动再在布局完成后挂焦点。Enter/Space 激活。
- `FocusableElementCreated` 供宿主挂焦点守卫（桌面 `TextFocusScrollGuard`），撤回 focus-into-view 引起的祖先滚动位移。
- 换肤走 `WithTheme`；字号/DPI 变化重建元素。代码背景经 `CodeBackgroundSelector`。

## 行距

正文行距按字号倍率：`LineHeightScale` 默认 1.6（段落/列表/引用/表格），`HeadingLineHeightScale` 默认 1.3（标题）。最终落到 MewUI 的 `LineSpacing`：先量字体自然行高（HarmonyOS Sans SC ≈1.19 em），再补 `max(0, 字号 × 倍率 − 自然行高)`。

- 随字号缩放，不压字；倍率小于自然行高时补 0；置 0/负值关闭。
- 只加在行与行之间，块间距仍由 `BlockSpacing` 描述；代码块不受影响。
- 选区底色由 `MarkdownParagraphView` 自绘，每行矩形上下各扩半个行间距，避免逐行横纹。
- 单行列表条目额外补一个行距（`ListItemLeading()`），标记与内容一起下移，仍与第一行对齐；嵌套列表同路径；松散列表条目间距不变。

## 使用

```csharp
var view = new MarkdownView
{
    FontSize = AppTheme.FontSizeChat,
    Options = new MarkdownOptions
    {
        ConfigurePipeline = builder => builder.UseMathematics(),
        LinkSchemes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "http", "https" },
    },
    MarkdownTheme = new MarkdownTheme
    {
        CodeFontFamily = AppTheme.MonoFont,
        CodeFontSize = AppTheme.FontSizeCaption,
        CodeBackgroundSelector = theme => AppTheme.For(theme).CodeBackground,
        InlineCodeBackgroundSelector = theme => AppTheme.For(theme).CardBackgroundAlt,
        InlineCodeBorderSelector = theme => AppTheme.For(theme).Border,
        CardCornerRadius = AppTheme.RadiusXl,
        CodeCopyIcon = size => AppIcons.Icon("copy", AppStyles.RoleSubtle, size),
        CodeCopiedIcon = size => AppIcons.Icon("check", AppStyles.RoleSuccess, size),
    },
    ImageResolver = imageLoader, // 不设置则永不读盘/联网
};

view.LinkRequested += args => Open(args.Url);
view.FocusableElementCreated = paragraph => focus.Track(paragraph);
view.ImageRequested += args => { if (args.Image is { } source) Preview(source); };
view.SelectionChanged += () => PublishPrimary(view.SelectedText);
view.Renderers = new MarkdownRenderers()
    .RegisterBlock("MathBlock", (block, _) => new MathView().Source(block.Text));

view.Source = text;
view.IsStreaming = true;
view.Append(delta);
// ...
view.IsStreaming = false;
```

## 限制

- 解析在 UI 线程同步执行；32 K 量级足够快，数百 KB 单条消息可考虑后台解析 + revision 合并。
- 纯追加只解析尾块；链接引用定义会使该帧回退全量解析。
- 尾块修补是保守启发式；围栏/缩进代码和 HTML 不修，猜错由定稿帧纠正。
- 列表/表格/引用只在删除或中间插入时整块重建。
- 块回收首次进入超大历史仍需测量全部块；后续可分批测量或估算。
- 块级 HTML、脚注正式视图未实现；脚注/定义列表可经 `ConfigurePipeline` 启用，脚注定义按普通段落渲染。
