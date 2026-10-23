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

### 3. 段内分块布局

`MarkdownParagraphView` 把段落切成若干 `Chunk`：

- 每个 chunk 是一个独立的 `ITextLayout`，在**已闭合行边界**结束；
- 最后一个 chunk 是开放行，追加时只重排它；
- 开放 chunk 超过 2 K 字符或 64 行时，在最后一个已闭合行处切出新的冻结 chunk；
- 尾部改写（关闭 `**`、反引号、链接等）从第一个变化偏移所在的 chunk 开始重排，前面的冻结 chunk 保留；
- 冻结 chunk 用 `TextLayoutCachePolicy.Owner` 缓存，绘制时按 `chunk.Top` 堆叠；
- 选区、链接命中、行内代码背景按 chunk 求交，全局偏移 = `chunk.Start + localOffset`。

分块布局与整段布局的行数、行高、baseline 和总高度在测试中逐行比对一致。

### 4. 代码块

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

### 5. 精确高度块回收

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

### 6. 选区、链接与主题

- 选区是文档级的 `(TextUnit, Offset)` 范围；`SelectedText` 从文档模型构建，因此包含未 realize 的块。
- 列表、表格、引用内部的每个段落各自持有 `TextUnit`；容器被复用时按位置重新分配文本单元，因此这些嵌套文本与正文一样参与鼠标拖选、双击选词、全选和 PRIMARY 发布。
- 源文变化时如果新旧文本共享足够前缀（纯追加、尾部改写、任务切换），保留选区并从新模型重新计算 `SelectedText`；只有整篇替换才清空选区。
- 复制/全选通过 `StandardCommands.Copy` / `StandardCommands.SelectAll` 注册，右键菜单提供同样入口。
- 链接由段落视图命中，`LinkRequested` 交给宿主；`#anchor` 在控件内滚动到目标块。
- 控件本身和链接段落都可以获得焦点。宿主通过 `FocusableElementCreated` 给段落挂焦点守卫（桌面端见
  `TextFocusScrollGuard`）；守卫在焦点进入阅读控件时记录**所有祖先 ScrollViewer**，并撤回这次焦点事务
  引起的位移。否则一个比执行过程组体还高的 `MarkdownView` 被聚焦时，MewUI 的 focus-into-view 会把组体
  和会话滚到视图顶部/底部，看起来像进度跳变；虚拟化行还可能被暂时释放，直到滚动后才重建。
- 主题换色走 MewUI 的 `WithTheme`；字号/DPI 变化会重建元素，因为代码块和列表标记把字体烘焙进了子元素。
- 代码块背景通过 `MarkdownTheme.CodeBackgroundSelector` 跟随 UI 主题。

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
- `MarkdownIncrementalParserTests` 对每 5 个字符的流式前缀做增量解析与全量解析的逐块哈希/纯文本比对；`MarkdownEdgeCaseTests` 覆盖空文档、CRLF、标题/表头加粗、嵌套列表、任务切换、代码改写、跨容器选区等边界。
- 未实现 CommonMark 之外的 HTML 格式化、脚注、定义列表和图片解码；HTML 按字面文本显示，图片显示 alt 文本。
- 未闭合的强调/链接在闭合前按字面显示（与 Markdig 行为一致），没有做 Streamdown 式的尾块语法修补。
- 列表/表格/引用支持尾项原位更新和行/子块追加；删除或在中间插入项时仍整块重建一次。
- 块回收首次进入超大历史消息时仍需测量全部块；这是精确高度的代价，后续可用分批测量或估算模式优化。

## 使用

```csharp
var view = new MarkdownView
{
    FontSize = AppTheme.FontSizeChat,
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
view.SelectionChanged += () => PublishPrimary(view.SelectedText);

view.Source = text;
view.IsStreaming = true;  // 流式
// ...
view.IsStreaming = false; // 定稿：不重建，只切状态
```
