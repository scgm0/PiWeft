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
	// 流式正文只在真正有流式文本时才建：过程组里的紧凑思考行是虚拟化的，
	// 每行都预建一个 MultiLineTextBox 会白白占住文本视图与布局资源。
	private MultiLineTextBox? _streamingText;
	private readonly Expander _thinking;
	private readonly Border _body = new();
	private readonly TextBlock _changes = new();
	private readonly Button _copy;
	private readonly TextFocusScrollGuard _focus;
	private readonly Action<string> _openLink;
	private ChatEntry? _entry;

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
			if (expanded) EnsureThinkingMarkdown();
		};

		_focus.TrackFold(_thinking);
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
		if (_streamingText is not null)
		{
			UiLifetime.Release(_streamingText);
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
		var time = entry.Timestamp > 0
			? DateTimeOffset.FromUnixTimeMilliseconds(entry.Timestamp).ToLocalTime().ToString("HH:mm")
			: string.Empty;
		var role = entry.IsStreaming ? Strings.RoleAssistantStreaming : Strings.RoleAssistant;
		var timing = !entry.IsStreaming && entry.DurationMs is { } duration
			? $"  ·  {Strings.ResponseDuration(duration)}"
			: string.Empty;
		_header.Text(time.Length > 0 ? $"{role}  ·  {time}{timing}" : $"{role}{timing}");
		_headerRow.IsVisible(!entry.Compact);
		_thinking.IsVisible(entry.Thinking.Length > 0 || entry.IsThinking);
		_thinkingIcon.IsVisible(!entry.Compact);
		_thinkingHeader.Text(entry.IsThinking ? Strings.ThinkingActive
			: entry.Compact && entry.DurationMs is { } thinkingMs ? Strings.ThinkingDuration(thinkingMs) : Strings.ThinkingComplete);
		var preview = Preview(entry.Thinking);
		_thinkingPreview.Text(preview).IsVisible(!_thinking.IsExpanded && preview.Length > 0);
		if (_thinking.IsExpanded) EnsureThinkingMarkdown(entry);
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
			_streamingText ??= Track(MessageText.Create());
			MessageText.Update(_streamingText, entry.Text);
			if (!ReferenceEquals(_body.Child, _streamingText))
			{
				var previous = _body.Child;
				_body.Child(_streamingText);
				if (previous is not null) UiLifetime.Release(previous);
			}
		}
		else if (_entry is null || _entry.IsStreaming || _entry.Text != entry.Text)
		{
			var previous = _body.Child;
			_body.Child(Markdown(entry.Text));
			// 文本被再次替换时旧 Markdown 已脱离视觉树，必须显式释放其中的文本/代码视图。
			if (previous is not null && !ReferenceEquals(previous, _streamingText)) UiLifetime.Release(previous);
		}

		_entry = entry;
	}

	private static string Preview(string thinking)
	{
		var remaining = thinking.AsSpan();
		while (remaining.Length > 0)
		{
			var newline = remaining.IndexOf('\n');
			var line = (newline < 0 ? remaining : remaining[..newline]).Trim();
			if (line.Length > 0)
			{
				return line.Length <= 72 ? line.ToString() : line[..72].ToString() + "…";
			}
			if (newline < 0) break;
			remaining = remaining[(newline + 1)..];
		}
		return string.Empty;
	}

	private MultiLineTextBox Track(MultiLineTextBox text)
	{
		_focus.Track(text);
		return text;
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
		if (_thinkingMarkdown is null)
		{
			_thinkingMarkdown = CreateMarkdownViewer(AppTheme.FontSizeCaption, muted: true);
			_thinking.Content(new Border()
				.Padding(new Thickness(AppTheme.Space3, AppTheme.Space2, 0, AppTheme.Space2))
				.Child(_thinkingMarkdown));
		}
		// 流式追加时合并连续 token；定稿后立即解析，选择/复制不必等后台任务。
		var delay = current?.IsStreaming == true ? TimeSpan.FromMilliseconds(80) : TimeSpan.Zero;
		if (_thinkingMarkdown.ParseDelay != delay) _thinkingMarkdown.ParseDelay = delay;
		if (!string.Equals(_thinkingMarkdown.Markdown, thinking, StringComparison.Ordinal))
			_thinkingMarkdown.Markdown = thinking;
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
