using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Aprillz.MewUI.Input;
using Aprillz.MewUI.Rendering;
using Aprillz.MewUI.Text.Editing;

namespace PiWeft.Markdown;

/// <summary>Implemented by code-block views that can update their text without being rebuilt.</summary>
public interface IMarkdownCodeView
{
	void SetCode(string code, string? language);
}

/// <summary>
/// Default fenced-code presentation: a single card with a compact language/action header and a
/// horizontally scrollable, appendable code surface. The chrome mirrors the table card so
/// Markdown blocks do not become a stack of nested boxes.
/// </summary>
internal sealed class MarkdownCodeBlockView : ContentControl, IMarkdownCodeView
{
	private const double HeaderHeight = 34;
	private const double BodyPadding = 12;

	private readonly MultiLineTextBox _text;
	private readonly TextBlock _language;
	private readonly Border _languageChip;
	private readonly Button _copy;
	private readonly Border _header;
	private readonly FrameworkElement _copyIdle;
	private readonly MarkdownTheme _theme;
	private DispatcherTimer? _copyReset;
	private readonly ScrollViewer _scroll;
	private readonly MarkdownCodeGutter? _gutter;
	private readonly Grid _grid;
	private readonly Border _chrome;
	private readonly Color _background;
	private readonly Func<Theme, Color>? _backgroundSelector;
	private string _code = string.Empty;
	private string? _languageText;

	internal MarkdownCodeBlockView(
		MarkdownTheme theme,
		double fontSize,
		Color foreground,
		Color background,
		Func<Theme, Color>? backgroundSelector)
	{
		_theme = theme;
		_background = background;
		_backgroundSelector = backgroundSelector;
		_text = new MultiLineTextBox(new EditableTextDocument())
		{
			IsReadOnly = true,
			ImeMode = ImeMode.Disabled,
			SizeToDocument = true,
			Wrap = false,
			Background = Color.Transparent,
			BorderThickness = 0,
			Padding = Thickness.Zero,
			MinHeight = 0,
			CornerRadius = 0,
			FontFamily = theme.CodeFontFamily,
			FontSize = theme.CodeFontSize ?? Math.Max(10, fontSize * theme.CodeFontSizeScale),
			Foreground = foreground,
		};
		_text.Document.UndoSizeLimit = 0;

		// The language is a quiet pill in the header, not an overlay floating over the first
		// line of code.
		_language = new TextBlock
		{
			FontFamily = theme.CodeFontFamily,
			FontSize = Math.Max(10, fontSize * 0.75),
			IsHitTestVisible = false,
			VerticalAlignment = VerticalAlignment.Center,
		};
		_languageChip = new Border
		{
			Child = _language,
			Padding = new Thickness(7, 2, 7, 2),
			CornerRadius = 99,
			HorizontalAlignment = HorizontalAlignment.Left,
			VerticalAlignment = VerticalAlignment.Center,
			IsVisible = false,
		};
		_languageChip.WithTheme((uiTheme, chip) => chip.Background = uiTheme.Palette.ButtonFace.Lerp(uiTheme.Palette.WindowText, 0.06));
		_language.WithTheme((uiTheme, label) => label.Foreground = SubtleText(uiTheme));

		// PiDeck's code header keeps a single quiet action: an icon, dimmed at rest and full
		// strength on hover. The glyph itself comes from the host's icon set (Lucide on desktop) —
		// this control library never draws icons of its own; without a host icon it falls back to
		// the localised text label.
		_copyIdle = CenterOrFallback(theme, fontSize);
		_copy = new Button
		{
			Content = _copyIdle,
			Width = theme.CodeCopyIcon is null ? 46 : 28,
			Height = 24,
			MinWidth = theme.CodeCopyIcon is null ? 42 : 28,
			MinHeight = 24,
			Padding = theme.CodeCopyIcon is null ? new Thickness(6, 0, 6, 0) : Thickness.Zero,
			CornerRadius = 5,
			StyleName = BuiltInStyles.FlatButton,
			Opacity = IdleActionOpacity,
		};
		// 宿主提供提示挂接时走宿主的悬浮层（统一的显示延迟与样式）；否则退回内建 ToolTip。
		var copyTip = string.IsNullOrWhiteSpace(theme.CodeCopyTooltip) ? theme.CodeCopyLabel : theme.CodeCopyTooltip;
		if (theme.AttachToolTip is { } attachTip)
		{
			attachTip(_copy, copyTip);
		}
		else
		{
			_copy.ToolTip = new TextBlock { Text = copyTip };
		}
		_copy.Click += CopyCode;
		_copy.WithTheme((uiTheme, button) => button.Foreground = ActionText(uiTheme));
		_copy.MouseEnter += () => _copy.Opacity = 1;
		_copy.MouseLeave += () => _copy.Opacity = IdleActionOpacity;

		var headerContent = new Grid().Columns("*,Auto").Children(
			_languageChip.Column(0).VerticalAlignment(VerticalAlignment.Center),
			_copy.Column(1).VerticalAlignment(VerticalAlignment.Center));
		_header = new Border
		{
			Height = HeaderHeight,
			Padding = new Thickness(BodyPadding, 0, 6, 0),
			NonUniformBorderThickness = new Thickness(0, 0, 0, 1),
			Child = headerContent,
			VerticalAlignment = VerticalAlignment.Stretch,
		};
		_header.WithTheme((uiTheme, header) =>
		{
			header.Background = HeaderBackground(uiTheme);
			header.BorderBrush = uiTheme.Palette.ControlBorder;
		});

		_scroll = new ScrollViewer
		{
			Padding = theme.ShowCodeLineNumbers
				? new Thickness(0, BodyPadding, BodyPadding, BodyPadding)
				: new Thickness(BodyPadding),
			HorizontalScroll = ScrollMode.Auto,
			VerticalScroll = ScrollMode.Disabled,
			Background = Color.Transparent,
			Content = _text
		};
		// 行号列与代码区同排：行号横向不随代码滚动，纵向按代码行自己的 Y 对齐。
		FrameworkElement body = _scroll;
		if (theme.ShowCodeLineNumbers)
		{
			_gutter = new MarkdownCodeGutter(_text, CodeBackground);
			body = new Grid().Columns("Auto,*").Children(
				_gutter.Column(0),
				_scroll.Column(1));
		}
		_grid = new Grid();
		_grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Pixels(HeaderHeight) });
		_grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		Grid.SetRow(_header, 0);
		Grid.SetRow(body, 1);
		_grid.Add(_header);
		_grid.Add(body);

		_chrome = new Border
		{
			Child = _grid,
			Padding = Thickness.Zero,
			BorderThickness = 1,
			CornerRadius = theme.CardCornerRadius,
			ClipToBounds = true,
			Background = background,
		};
		_chrome.WithTheme((uiTheme, border) =>
		{
			border.Background = CodeBackground(uiTheme);
			border.BorderBrush = uiTheme.Palette.ControlBorder;
		});
		Content = _chrome;
	}

	public void SetCode(string code, string? language)
	{
		code = Normalize(code);
		if (!string.Equals(code, _code, StringComparison.Ordinal))
		{
			if (code.Length >= _code.Length && code.StartsWith(_code, StringComparison.Ordinal))
			{
				_text.ReplaceRange(_code.Length, 0, code[_code.Length..]);
			}
			else
			{
				_text.ReplaceRange(0, _code.Length, code);
			}
			_code = code;
			InvalidateMeasure();
		}

		if (!string.Equals(language, _languageText, StringComparison.Ordinal))
		{
			_languageText = language;
			_language.Text = language ?? string.Empty;
			_languageChip.IsVisible = !string.IsNullOrEmpty(language);
		}
	}

	internal MultiLineTextBox TextView => _text;

	internal MarkdownCodeGutter? Gutter => _gutter;

	internal Border Chrome => _chrome;

	private Color CodeBackground(Theme uiTheme)
		=> _backgroundSelector?.Invoke(uiTheme) ?? _background;

	/// <summary>宿主没给图标时，退回本地化文本标签当动作内容。</summary>
	private static FrameworkElement CenterOrFallback(MarkdownTheme theme, double fontSize) =>
		Center(theme.CodeCopyIcon?.Invoke(theme.CodeCopyIconSize) ?? new TextBlock
		{
			Text = theme.CodeCopyLabel,
			FontSize = Math.Max(10, fontSize * 0.72),
		});

	/// <summary>
	/// 按钮里的动作内容两向都居中：宿主给的图标常常只设了纵向居中（固定宽度下水平默认是
	/// Stretch，于是图标贴在按钮左侧），文本标签同理。
	/// </summary>
	private static FrameworkElement Center(FrameworkElement element)
	{
		element.HorizontalAlignment = HorizontalAlignment.Center;
		element.VerticalAlignment = VerticalAlignment.Center;
		return element;
	}

	/// <summary>Copies the fenced code and shows the host's confirmation icon for a moment.</summary>
	private void CopyCode()
	{
		if (_code.Length == 0 || !Application.IsRunning ||
			Application.Current.PlatformServices.Clipboard?.TrySetText(_code) != true)
		{
			return;
		}

		FrameworkElement? confirmed = _theme.CodeCopiedIcon?.Invoke(_theme.CodeCopyIconSize);
		confirmed = confirmed is null ? null : Center(confirmed);
		if (confirmed is null)
		{
			return;
		}

		_copy.Content = confirmed;
		_copy.Opacity = 1;
		DispatcherTimer reset = _copyReset ??= new DispatcherTimer(TimeSpan.FromMilliseconds(1100));
		reset.Tick -= RestoreCopyAction;
		reset.Tick += RestoreCopyAction;
		reset.Start();
	}

	private void RestoreCopyAction()
	{
		_copyReset?.Stop();
		_copy.Content = _copyIdle;
		_copy.Opacity = _copy.IsMouseOver ? 1 : IdleActionOpacity;
	}

	private Color HeaderBackground(Theme uiTheme)
		=> uiTheme.Palette.ControlBackground.Lerp(CodeBackground(uiTheme), 0.18);

	private static Color SubtleText(Theme uiTheme)
		=> uiTheme.Palette.WindowText.Lerp(uiTheme.Palette.WindowBackground, 0.35);

	/// <summary>静止时压暗、悬停恢复全强度：动作不该和代码本身抢注意力。</summary>
	private const double IdleActionOpacity = 0.62;

	private static Color ActionText(Theme uiTheme)
		=> uiTheme.Palette.WindowText.Lerp(uiTheme.Palette.WindowBackground, 0.18);

	private static string Normalize(string text)
		=> text.Contains('\r', StringComparison.Ordinal)
			? text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')
			: text;

	protected override void OnDispose()
	{
		_copyReset?.Dispose();
		_copyReset = null;
		_text.Dispose();
		base.OnDispose();
	}
}
