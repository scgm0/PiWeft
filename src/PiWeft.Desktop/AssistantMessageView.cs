using System.Diagnostics;
using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using PiWeft.Markdown;

namespace PiWeft.Desktop;

/// <summary>助手消息的持久视图：思考独立折叠，正文与思考在流式期间增量渲染 Markdown，定稿只切状态。</summary>
internal sealed class AssistantMessageView : IDisposable
{
	private readonly TextBlock _header = new();
	private readonly DockPanel _headerRow;
	private readonly TextBlock _thinkingHeader = new();
	private readonly TextBlock _thinkingPreview = new();
	private readonly Image _thinkingIcon;
	// 思考与正文各持有一个增量 Markdown 视图：流式期间只更新尾块，定稿只切状态。
	private MarkdownView? _thinkingMarkdown;
	private MarkdownView? _bodyMarkdown;
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
		if (_bodyMarkdown is not null)
		{
			UiLifetime.Release(_bodyMarkdown);
			_bodyMarkdown = null;
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

		if (entry.Text.Length > 0)
		{
			var view = EnsureBodyMarkdown();
			if (!ReferenceEquals(_body.Child, view))
			{
				var previous = _body.Child;
				_body.Child(view);
				if (previous is not null) UiLifetime.Release(previous);
			}
			view.IsStreaming = entry.IsStreaming;
			if (!string.Equals(view.Source, entry.Text, StringComparison.Ordinal))
			{
				var watch = Diagnostics.Enabled ? Stopwatch.StartNew() : null;
				view.Source = entry.Text;
				if (watch is not null)
				{
					Diagnostics.Record(entry.IsStreaming ? "markdown.stream" : "markdown.finalize", watch, $"chars={entry.Text.Length}");
				}
			}
		}
		else
		{
			// 只有思考、还没有正文时不要预建空的 Markdown 视图（过程组里每行都建一个是白烧布局）。
			var previous = _body.Child;
			if (previous is not null)
			{
				_body.Child(null);
				UiLifetime.Release(previous);
				if (ReferenceEquals(previous, _bodyMarkdown)) _bodyMarkdown = null;
			}
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

	/// <summary>
	/// 展开的思考：同一个增量 Markdown 视图从流式第一帧用到定稿，不再做纯文本→Markdown 的替换。
	/// </summary>
	private void EnsureThinkingBody(ChatEntry? entry = null)
	{
		var current = entry ?? _entry;
		if (current is null) return;
		var thinking = current.Thinking;
		if (thinking.Length == 0 && !current.IsThinking)
		{
			if (_thinkingMarkdown is not null) _thinkingMarkdown.Source = string.Empty;
			return;
		}
		_thinkingMarkdown ??= CreateMarkdownView(AppTheme.FontSizeCaption, muted: true);
		SetThinkingContent(_thinkingMarkdown);
		_thinkingMarkdown.IsStreaming = current.IsThinking;
		if (string.Equals(_thinkingMarkdown.Source, thinking, StringComparison.Ordinal)) return;
		var watch = Diagnostics.Enabled ? Stopwatch.StartNew() : null;
		_thinkingMarkdown.Source = thinking;
		if (watch is not null)
		{
			Diagnostics.Record(current.IsThinking ? "markdown.thinking.stream" : "markdown.thinking.finalize", watch, $"chars={thinking.Length}");
		}
	}

	private void SetThinkingContent(UIElement content)
	{
		if (_thinking.Content is Border { Child: var current } && ReferenceEquals(current, content)) return;
		_thinking.Content(new Border()
			.Padding(new Thickness(AppTheme.Space3, AppTheme.Space2, 0, AppTheme.Space2))
			.Child(content));
	}

	internal static MultiLineTextBox CreateCodeText(string code, string? language, TextFocusScrollGuard focus)
	{
		var text = MessageText.Create(code, mono: true, wrap: false, fontSize: AppTheme.FontSizeCaption);
		ConfigureCodeText(text, language, focus, muted: false);
		return text;
	}

	private static void ConfigureCodeText(MultiLineTextBox text, string? language, TextFocusScrollGuard focus, bool muted)
	{
		focus.Track(text);
		// 代码块的横向视口在 Markdown 建块时才出现（MarkdownView 建树早于它）：
		// 这里补装同一个平滑滚动控制器，触控板横向滑动才不会落进 ScrollViewer 的即时跳变。
		for (var parent = text.Parent; parent is not null; parent = parent.Parent)
		{
			if (parent is not ScrollViewer viewport) continue;
			viewport.SmoothScrolling();
			break;
		}
		text.Tag = muted ? AppStyles.RoleMuted : AppStyles.RolePrimary;
		text.Extensions.Classifiers.Add(new KeywordClassifier(language));
		// 换肤后重建视图并重新排布，避免代码块只剩背景（见 AppStyles.RefreshTextView）。
		text.WithTheme((theme, view) => AppStyles.RefreshTextView(view));
	}

	private MarkdownView EnsureBodyMarkdown() => _bodyMarkdown ??= CreateMarkdownView(AppTheme.FontSizeChat);

	private MarkdownView CreateMarkdownView(double fontSize, bool muted = false)
	{
		// 会话本身已经有纵向视口，每条回复只负责排版；块回收由外层视口驱动。
		var view = new MarkdownView
		{
			FontSize = fontSize,
			FontWeight = FontWeight.Normal,
			MarkdownTheme = new MarkdownTheme
			{
				CodeFontFamily = AppTheme.MonoFont,
				CodeFontSize = AppTheme.FontSizeCaption,
				CodeCopyLabel = "复制",
				CodeCopyTooltip = "复制代码",
				// 图标一律走 Lucide 内嵌 SVG（tools/fetch-lucide-icons.sh 取的那一套），
				// 控件库自己不画图标；复制成功换成对勾，1.1 秒后退回复制图标。
				CodeCopyIcon = size => AppIcons.Icon("copy", AppStyles.RoleSubtle, size),
				CodeCopiedIcon = size => AppIcons.Icon("check", AppStyles.RoleSuccess, size),
				BlockSpacing = AppTheme.Space2,
				ListIndent = 28,
				CardCornerRadius = AppTheme.RadiusXl,
				CodeBackgroundSelector = theme => AppTheme.For(theme).CodeBackground,
				InlineCodeBackgroundSelector = theme => AppTheme.For(theme).CardBackgroundAlt,
				InlineCodeBorderSelector = theme => AppTheme.For(theme).Border,
			}
		};
		// 丢渲染一旦发生就要在 timings 里留下现场（窗口/偏移/高度），否则只能靠猜：
		// 正常情况下这条分支永远不走，成本为零。
		view.NothingRealized += geometry =>
		{
			Diagnostics.Count("markdown.empty.realize");
			Diagnostics.Record("markdown.empty.realize", 0, geometry);
		};
		view.SelectionChanged += () => PrimarySelectionText.Service?.Publish(
			view.SelectedText.Length <= LinuxPrimarySelection.MaxCharacters ? view.SelectedText : string.Empty);
		_focus.Track(view);
		view.LinkRequested += link => _openLink(link.Url);
		view.CodeTextCreated = (text, language) => ConfigureCodeText(text, language, _focus, muted);
		// Link paragraphs are their own focus targets. Without a guard a click asks the group body
		// and the conversation to scroll the focused paragraph into view, which can move the
		// reader's position even though the clicked word was already visible.
		view.FocusableElementCreated = paragraph => _focus.Track(paragraph);
		if (muted) view.WithTheme((theme, element) => element.Foreground = AppTheme.For(theme).TextMuted);
		return view.SmoothScrolling();
	}

}
