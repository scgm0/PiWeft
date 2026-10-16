using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Aprillz.MewUI.Markdown;

namespace PiWeft.Desktop;

/// <summary>助手消息的持久视图：思考独立折叠，流式只更新文本，结束后渲染 Markdown。</summary>
internal sealed class AssistantMessageView : IDisposable
{
	private readonly TextBlock _header = new();
	private readonly TextBlock _thinkingHeader = new();
	private readonly MultiLineTextBox _thinkingText = MessageText.Create(muted: true, fontSize: AppTheme.FontSizeCaption);
	private readonly MultiLineTextBox _streamingText = MessageText.Create();
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
		_thinkingHeader.FontSize(AppTheme.FontSizeCaption).FontFamily(AppTheme.MonoFont)
			.WithTheme((theme, text) => text.Foreground = AppTheme.For(theme).Thinking);
		_focus.Track(_thinkingText);
		_focus.Track(_streamingText);

		_thinking = new Expander()
			.Header(_thinkingHeader)
			.IsExpanded(false)
			.Content(new Border()
				.Padding(new Thickness(AppTheme.Space3, AppTheme.Space2, 0, AppTheme.Space2))
				.Child(_thinkingText));

		_focus.TrackFold(_thinking);
		_copy = AppIcons.Button("copy", Strings.CopyMessage, () => copy(_entry?.Text ?? string.Empty), size: 14);
		var content = new StackPanel().Spacing(AppTheme.Space2).Children(
			new DockPanel().LastChildFill().Children(_copy.DockRight(), _header),
			_thinking,
			_body,
			_changes.FontSize(AppTheme.FontSizeMicro).SubtleText().TextWrapping(TextWrapping.Wrap).IsVisible(false));

		foreach (var image in entry.Images)
		{
			content.Children(SessionTab.ImageView(image, preview));
		}

		View = new Border()
			.Padding(new Thickness(AppTheme.Space3, AppTheme.Space2, AppTheme.Space3, AppTheme.Space2))
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
		UiLifetime.Release(_streamingText);
		UiLifetime.Release(_thinkingText);
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
		_thinking.IsVisible(entry.Thinking.Length > 0 || entry.IsThinking);
		_thinkingHeader.Text(entry.IsThinking ? Strings.ThinkingActive : Strings.ThinkingComplete);
		var showChanges = !entry.IsStreaming && entry.ChangedFiles.Count > 0;
		_changes.IsVisible(showChanges);
		if (showChanges)
		{
			_changes.Text(Strings.ChangedFilesSummary(entry.ChangedFiles));
		}
		MessageText.Update(_thinkingText, entry.Thinking);
		_body.IsVisible(entry.Text.Length > 0);
		_copy.IsVisible(!entry.IsStreaming && entry.Text.Length > 0);

		if (entry.IsStreaming)
		{
			MessageText.Update(_streamingText, entry.Text);
			if (!ReferenceEquals(_body.Child, _streamingText))
			{
				_body.Child(_streamingText);
			}
		}
		else if (_entry is null || _entry.IsStreaming || _entry.Text != entry.Text)
		{
			_body.Child(Markdown(entry.Text));
		}

		_entry = entry;
	}

	internal static MultiLineTextBox CreateCodeText(string code, string? language, TextFocusScrollGuard focus)
	{
		var text = MessageText.Create(code, mono: true, wrap: false, fontSize: AppTheme.FontSizeCaption);
		focus.Track(text);
		text.Extensions.Classifiers.Add(new KeywordClassifier(language));
		return text;
	}

	private MarkdownPresenter Markdown(string text)
	{
		// 会话本身已经有纵向视口，每条回复只负责排版，避免嵌套滚动与焦点联动。
		var viewer = new MarkdownPresenter().FontSize(AppTheme.FontSizeChat).FontWeight(FontWeight.Normal);
		viewer.SelectionChanged += () => PrimarySelectionText.Service?.Publish(
			viewer.SelectedText.Length <= LinuxPrimarySelection.MaxCharacters ? viewer.SelectedText : string.Empty);
		_focus.Track(viewer);
		viewer.CodeBlockFactory = (code, language) => CreateCodeText(code ?? string.Empty, language, _focus);
		viewer.OnLinkRequested(link => _openLink(link.Url));
		return viewer.Markdown(text).SmoothScrolling();
	}
}
