using System.Diagnostics;
using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Aprillz.MewUI.Markdown;

namespace PiWeft.Desktop;

/// <summary>助手消息的持久视图：思考独立折叠，流式只更新文本，结束后渲染 Markdown。</summary>
internal sealed class AssistantMessageView : IDisposable
{
	private readonly TextBlock _header = new();
	private readonly DockPanel _headerRow;
	private readonly TextBlock _thinkingHeader = new();
	private readonly TextBlock _thinkingPreview = new();
	private readonly Image _thinkingIcon;
	private MarkdownPresenter? _thinkingMarkdown;
	// 流式思考先用轻量纯文本视图逐帧更新；定稿后再换成 Markdown。
	// MarkdownPresenter 每次源文变化都会重建整棵文档控件树，长思考逐帧重解析
	// 会把 UI 线程占满（实测 8 KB 约 40 ms/帧）；纯文本只需文本布局。
	private StreamingTextView? _thinkingText;
	// 流式正文只在真正有流式文本时才建：过程组里的紧凑思考行是虚拟化的，
	// 每行都预建一个 MultiLineTextBox 会白白占住文本视图与布局资源。
	private StreamingTextView? _streamingText;
	private readonly Expander _thinking;
	private readonly Border _body = new();
	private readonly TextBlock _changes = new();
	private readonly Button _copy;
	private readonly TextFocusScrollGuard _focus;
	private readonly Action<string> _openLink;
	private ChatEntry? _entry;
	// 展开中的思考是否正在跟随内容增长：只在流式思考期间保持，避免结束后把阅读位置拉走。
	private bool _followingThinking;

	public AssistantMessageView(ChatEntry entry, Action<string> copy, TextFocusScrollGuard focus, Action<ImageSource>? preview = null,
		Action<string>? openLink = null)
	{
		_focus = focus;
		_openLink = openLink ?? (url => Shell.OpenMessageLink(url, Environment.CurrentDirectory));
		_header.FontSize(AppTheme.FontSizeMicro).FontFamily(AppTheme.MonoFont).SubtleText();
		_thinkingHeader.FontSize(AppTheme.FontSizeCaption).FontWeight(FontWeight.Medium)
			.WithTheme((theme, text) => text.Foreground = AppTheme.For(theme).Thinking);
		_thinkingPreview.FontSize(AppTheme.FontSizeMicro).FontFamily(AppTheme.MonoFont).SubtleText()
			.TextTrimming(TextTrimming.CharacterEllipsis).VerticalAlignment(VerticalAlignment.Center);

		_thinkingIcon = AppIcons.Icon("sparkles", AppStyles.RoleThinking, 12);
		// Expander 自带折叠箭头，不在标题内再画一个。
		var thinkingTitle = new Grid().Columns("Auto,Auto,*").HorizontalAlignment(HorizontalAlignment.Stretch)
			.Children(
				_thinkingIcon.Column(0),
				_thinkingHeader.Column(1).Margin(new Thickness(5, 0, 0, 0)),
				_thinkingPreview.Column(2).Margin(new Thickness(6, 0, 0, 0)));
		_thinking = new Expander()
			.Header(thinkingTitle)
			.IsExpanded(false);
		_thinking.ExpandedChanged += expanded =>
		{
			_thinkingPreview.IsVisible = !expanded && _thinkingPreview.Text.Length > 0;
			if (!expanded)
			{
				_followingThinking = false;
				_focus.StopGrowth(_thinking);
				return;
			}
			EnsureThinkingBody();
			// 用户展开：记住这次阅读意图。若思考正在流式输出就立即贴住末尾；
			// 尚未开始时由后续 Update 在首个 thinking delta 到来后接管。
			// 从回收缓存恢复状态时不算一次用户展开，不重新贴底。
			if (!_focus.SuppressGrowthFollow)
			{
				_followingThinking = true;
				if (_entry?.IsThinking == true) _focus.FollowGrowth(_thinking);
			}
		};

		// 思考行内部由组体/跟随增长处理；不要用外层锚点把会话滚动位置带走。
		_focus.TrackFold(_thinking, preserveAnchor: false);
		_copy = AppIcons.Button("copy", Strings.CopyMessage, () => copy(_entry?.Text ?? string.Empty), size: 14);
		_headerRow = new DockPanel().LastChildFill().Children(_copy.DockRight(), _header);
		var content = new StackPanel().Spacing(AppTheme.Space2).Children(
			_headerRow,
			_thinking,
			_body,
			_changes.FontSize(AppTheme.FontSizeMicro).SubtleText().TextWrapping(TextWrapping.Wrap).IsVisible(false));

		foreach (var image in entry.Images)
		{
			content.Children(SessionTab.ImageView(image, preview));
		}

		View = new Border()
			.Padding(entry.Compact ? new Thickness(0, 0, 0, 0) : new Thickness(AppTheme.Space3, AppTheme.Space2, AppTheme.Space3, AppTheme.Space2))
			.MaxWidth(AppTheme.MessageMaxWidth)
			.HorizontalAlignment(HorizontalAlignment.Stretch)
			.Child(content);
		View.Tag = this;
		Update(entry);
	}

	public Border View { get; }

	public void Dispose()
	{
		// Streaming text leaves the visual tree when Markdown takes over, but still owns text resources.
		if (_thinkingText is not null)
		{
			UiLifetime.Release(_thinkingText.View);
			_thinkingText = null;
		}
		if (_streamingText is not null)
		{
			UiLifetime.Release(_streamingText.View);
			_streamingText = null;
		}
		if (_thinkingMarkdown is not null)
		{
			UiLifetime.Release(_thinkingMarkdown);
			_thinkingMarkdown = null;
		}
	}

	public void Update(ChatEntry entry)
	{
		// 头部文本的输入只有这三项：流式期每帧都重新格式化并转换时区，是纯粹的字符串垃圾。
		var headerKey = (entry.Timestamp, entry.IsStreaming, entry.DurationMs);
		if (headerKey != _headerKey)
		{
			_headerKey = headerKey;
			var time = entry.Timestamp > 0
				? DateTimeOffset.FromUnixTimeMilliseconds(entry.Timestamp).ToLocalTime().ToString("HH:mm")
				: string.Empty;
			var role = entry.IsStreaming ? Strings.RoleAssistantStreaming : Strings.RoleAssistant;
			var timing = !entry.IsStreaming && entry.DurationMs is { } duration
				? $"  ·  {Strings.ResponseDuration(duration)}"
				: string.Empty;
			_header.Text(time.Length > 0 ? $"{role}  ·  {time}{timing}" : $"{role}{timing}");
		}
		_headerRow.IsVisible(!entry.Compact);
		_thinking.IsVisible(entry.Thinking.Length > 0 || entry.IsThinking);
		_thinkingIcon.IsVisible(!entry.Compact);
		var thinkingLabel = entry.IsThinking ? Strings.ThinkingActive
			: entry.Compact && entry.DurationMs is { } thinkingMs ? Strings.ThinkingDuration(thinkingMs) : Strings.ThinkingComplete;
		if (!ReferenceEquals(thinkingLabel, _thinkingLabel))
		{
			_thinkingLabel = thinkingLabel;
			_thinkingHeader.Text(thinkingLabel);
		}

		// 折叠预览只看首行：展开时不算，取到完整首行后也不再算 —— 以前每帧从头扫一遍思考正文。
		if (!_thinking.IsExpanded && !_previewSettled)
		{
			var preview = Preview(entry.Thinking, out var settled);
			_previewSettled = settled;
			_previewText = preview;
			_thinkingPreview.Text(preview);
		}

		_thinkingPreview.IsVisible(!_thinking.IsExpanded && _previewText.Length > 0);
		if (_thinking.IsExpanded)
		{
			EnsureThinkingBody(entry);
			// 已经由用户展开并进入跟随的思考继续贴住末尾；恢复出来的展开状态不自动开始跟随。
			if (entry.IsThinking)
			{
				if (_followingThinking) _focus.FollowGrowth(_thinking);
			}
			else if (_followingThinking)
			{
				// 思考结束只停止“思考自身的跟底”，不要把已置底的组体自动跟随也关掉：
				// 组体滚动条仍在底部时，后续工具步骤应继续贴底；用户上滚时 SmoothScroll
				// 自己会退出跟底。
				_followingThinking = false;
			}
		}
		else
		{
			_followingThinking = false;
		}
		var showChanges = !entry.IsStreaming && !entry.Compact && entry.ChangedFiles.Count > 0;
		_changes.IsVisible(showChanges);
		if (showChanges)
		{
			_changes.Text(Strings.ChangedFilesSummary(entry.ChangedFiles));
		}
		_body.IsVisible(entry.Text.Length > 0);
		_copy.IsVisible(!entry.Compact && !entry.IsStreaming && entry.Text.Length > 0);

		if (entry.IsStreaming)
		{
			if (entry.Text.Length > 0)
			{
				_streamingText ??= Track(new StreamingTextView());
				_streamingText.Update(entry.Text);
				if (!ReferenceEquals(_body.Child, _streamingText.View))
				{
					var previous = _body.Child;
					_body.Child(_streamingText.View);
					if (previous is not null) UiLifetime.Release(previous);
				}
			}
			else
			{
				// 只有思考、还没有正文时不要预建空的流式文本框（过程组里每行都建一个是白烧布局）。
				var previous = _body.Child;
				if (previous is not null)
				{
					_body.Child(null);
					UiLifetime.Release(previous);
					if (ReferenceEquals(previous, _streamingText?.View))
					{
						_streamingText = null;
					}
				}
			}
		}
		else if (entry.Text.Length > 0 && (_entry is null || _entry.IsStreaming || _entry.Text != entry.Text))
		{
			var previous = _body.Child;
			_body.Child(Markdown(entry.Text));
			// 文本被再次替换时旧 Markdown 已脱离视觉树，必须显式释放其中的文本/代码视图。
			if (previous is not null && !ReferenceEquals(previous, _streamingText?.View)) UiLifetime.Release(previous);
		}

		_entry = entry;
	}

	/// <summary>取首行摘要；<paramref name="settled"/> 为 true 表示后面再长的思考也不会改变它。</summary>
	private static string Preview(string thinking, out bool settled)
	{
		settled = false;
		// 只在有限窗口里找：流式期每帧都会调用，扫描整个已到达的思考正文是白烧。
		var window = thinking.Length <= PreviewScanLimit ? thinking.AsSpan() : thinking[..PreviewScanLimit].AsSpan();
		if (thinking.Length > PreviewScanLimit) settled = true;
		var remaining = window;
		while (remaining.Length > 0)
		{
			var newline = remaining.IndexOf('\n');
			var line = (newline < 0 ? remaining : remaining[..newline]).Trim();
			if (line.Length > 0)
			{
				settled = true;
				return line.Length <= 72 ? line.ToString() : line[..72].ToString() + "…";
			}
			if (newline < 0) break;
			remaining = remaining[(newline + 1)..];
		}
		return string.Empty;
	}

	private const int PreviewScanLimit = 4096;

	private (long Timestamp, bool Streaming, double? Duration) _headerKey;
	private string? _thinkingLabel;
	private string _previewText = string.Empty;
	private bool _previewSettled;

	private StreamingTextView Track(StreamingTextView text)
	{
		_focus.Track(text.View);
		return text;
	}

	/// <summary>
	/// 展开的思考：流式阶段用纯文本逐帧更新，定稿后一次性换成 Markdown。
	/// MarkdownPresenter 每次 Markdown 变化都会丢弃并重建整棵文档树（Markdig 解析本身
	/// 只占很小一部分），长思考逐帧重建会超过帧预算；纯文本布局则便宜得多。
	/// </summary>
	private void EnsureThinkingBody(ChatEntry? entry = null)
	{
		var current = entry ?? _entry;
		if (current is null) return;
		if (current.IsThinking) EnsureThinkingText(current);
		else EnsureThinkingMarkdown(current);
	}

	private void EnsureThinkingText(ChatEntry current)
	{
		var thinking = current.Thinking;
		if (_thinkingText is null)
		{
			if (_thinkingMarkdown is not null)
			{
				UiLifetime.Release(_thinkingMarkdown);
				_thinkingMarkdown = null;
			}
			_thinkingText = Track(new StreamingTextView(fontSize: AppTheme.FontSizeCaption, muted: true));
		}
		SetThinkingContent(_thinkingText.View);
		// 只追加不改前缀时是文档增量编辑，成本与总长度无关（实测 64 K 约 1 ms），可以逐帧更新。
		_thinkingText.Update(thinking);
	}

	private void SetThinkingContent(UIElement content)
	{
		if (_thinking.Content is Border { Child: var current } && ReferenceEquals(current, content)) return;
		_thinking.Content(new Border()
			.Padding(new Thickness(AppTheme.Space3, AppTheme.Space2, 0, AppTheme.Space2))
			.Child(content));
	}

	/// <summary>思考正文按 Markdown 渲染；折叠时仍只显示纯文本首行，避免为不可见内容建树。</summary>
	private void EnsureThinkingMarkdown(ChatEntry? entry = null)
	{
		var current = entry ?? _entry;
		var thinking = current?.Thinking ?? string.Empty;
		if (thinking.Length == 0 && !(current?.IsThinking ?? false))
		{
			if (_thinkingMarkdown is not null) _thinkingMarkdown.Markdown = string.Empty;
			return;
		}
		if (_thinkingText is not null)
		{
			UiLifetime.Release(_thinkingText.View);
			_thinkingText = null;
		}
		if (_thinkingMarkdown is null)
		{
			_thinkingMarkdown = CreateMarkdownViewer(AppTheme.FontSizeCaption, muted: true);
		}
		SetThinkingContent(_thinkingMarkdown);
		// 定稿保留框架默认的同步解析（ParseDelay 默认就是 0）：这一步只发生一次，不会被
		// 流式增量反复取消；改成后台解析反而省不了什么——32 K 文本 Markdig 解析约 1.3 ms，
		// 真正贵的是随后在 UI 线程重建控件树（约 40 ms），异步绕不开；而新建的 presenter
		// 在首个文档到达前没有树可画，异步会多出一帧空白。
		if (string.Equals(_thinkingMarkdown.Markdown, thinking, StringComparison.Ordinal)) return;
		// 定稿要在 UI 线程重建整棵文档树，是「思考结束那一下卡一下」的主因，单独计时。
		var watch = Diagnostics.Enabled ? Stopwatch.StartNew() : null;
		_thinkingMarkdown.Markdown = thinking;
		if (watch is not null)
		{
			Diagnostics.Record("markdown.finalize", watch, $"chars={thinking.Length}");
		}
	}

	internal static MultiLineTextBox CreateCodeText(string code, string? language, TextFocusScrollGuard focus)
	{
		var text = MessageText.Create(code, mono: true, wrap: false, fontSize: AppTheme.FontSizeCaption);
		focus.Track(text);
		text.Extensions.Classifiers.Add(new KeywordClassifier(language));
		// 换肤后重建视图并重新排布，避免代码块只剩背景（见 AppStyles.RefreshTextView）。
		text.WithTheme((theme, view) => AppStyles.RefreshTextView(view));
		return text;
	}

	private MarkdownPresenter Markdown(string text) => CreateMarkdownViewer(AppTheme.FontSizeChat).Markdown(text);

	private MarkdownPresenter CreateMarkdownViewer(double fontSize, bool muted = false)
	{
		// 会话本身已经有纵向视口，每条回复只负责排版，避免嵌套滚动与焦点联动。
		// 保留默认的 ParseDelay = 0（同步解析）：正文与思考都只换一次 Markdown，解析本身很便宜
		// （32 K 约 1.3 ms），改成后台解析反而会先空一帧再补内容。
		var viewer = new MarkdownPresenter().FontSize(fontSize).FontWeight(FontWeight.Normal);
		viewer.SelectionChanged += () => PrimarySelectionText.Service?.Publish(
			viewer.SelectedText.Length <= LinuxPrimarySelection.MaxCharacters ? viewer.SelectedText : string.Empty);
		_focus.Track(viewer);
		viewer.CodeBlockFactory = (code, language) => CreateCodeText(code ?? string.Empty, language, _focus);
		viewer.OnLinkRequested(link => _openLink(link.Url));
		if (muted) viewer.WithTheme((theme, view) => view.Foreground = AppTheme.For(theme).TextMuted);
		return viewer.SmoothScrolling();
	}
}
