using System.Globalization;
using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Aprillz.MewUI.Rendering;
using Aprillz.MewUI.Text;

namespace PiWeft.Markdown;

/// <summary>
/// Fenced-code line-number gutter. Each number is its own cached single-line layout in the code
/// view's exact style, drawn at that code line's own top, so a line made taller by a fallback font
/// (CJK, emoji) shifts every number below it by the same amount the code shifts. The gutter is a
/// sibling column of the text view, not part of it: numbers stay put while the code scrolls
/// horizontally. Coordinates are absolute window space, the space every MewUI render draws in.
/// </summary>
internal sealed class MarkdownCodeGutter : FrameworkElement
{
	private readonly MultiLineTextBox _code;
	private readonly Func<Theme, Color> _codeBackground;
	private readonly Dictionary<string, ITextLayout> _layouts = [];
	private const double Leading = 10;
	private const double GapToCode = 8;

	public MarkdownCodeGutter(MultiLineTextBox code, Func<Theme, Color> codeBackground)
	{
		_code = code;
		_codeBackground = codeBackground;
		IsHitTestVisible = false;
		VerticalAlignment = VerticalAlignment.Stretch;

		code.DocumentChanged += OnCodeChanged;
		code.LinesChanged += OnCodeChanged;
		code.ScrollOffsetChanged += OnCodeScrolled;
	}

	private void OnCodeChanged(ITextViewHost host)
	{
		// 行数跨过位数边界时列宽要跟着变，其余情况重画即可。
		InvalidateMeasure();
		InvalidateVisual();
	}

	private void OnCodeScrolled(ITextViewHost host) => InvalidateVisual();

	protected override Size MeasureContent(Size availableSize)
	{
		int lines = _code.Document.LineCount;
		if (lines <= 0)
		{
			return default;
		}
		double digits = GetLayout(lines.ToString(CultureInfo.InvariantCulture)).MeasuredSize.Width;
		return new Size(Leading + digits + GapToCode, 0);
	}

	protected override void OnRender(IGraphicsContext context)
	{
		int lines = _code.Document.LineCount;
		if (lines <= 0)
		{
			return;
		}

		Color color = Theme.Palette.WindowText.Lerp(_codeBackground(Theme), 0.55);
		var render = context.Text;
		double scrollY = _code.ScrollOffset.Y;
		double textTop = _code.TextViewportBounds.Y;
		double numberRight = Bounds.Right - GapToCode;
		for (int line = 0; line < lines; line++)
		{
			ITextLayout layout = GetLayout((line + 1).ToString(CultureInfo.InvariantCulture));
			render.Draw(layout, new Point(numberRight - layout.MeasuredSize.Width,
				textTop + _code.GetLineY(line) - scrollY), new TextDrawOptions(color, Owner: this));
		}
	}

	internal ITextLayout GetLayout(string number)
	{
		if (_layouts.TryGetValue(number, out ITextLayout? layout))
		{
			return layout;
		}

		layout = GetGraphicsFactory().TextEngine.GetOrCreateLayout(new TextLayoutRequest
		{
			Text = number.AsMemory(),
			Dpi = GetDpi(),
			DefaultStyle = new TextRunStyle(_code.FontFamily, _code.FontSize),
		}, TextLayoutCachePolicy.Owner, this);
		_layouts[number] = layout;
		return layout;
	}

	protected override void OnDpiChanged(uint oldDpi, uint newDpi) => _layouts.Clear();

	protected override void OnThemeChanged(Theme oldTheme, Theme newTheme) => InvalidateVisual();

	protected override void OnDispose()
	{
		_code.DocumentChanged -= OnCodeChanged;
		_code.LinesChanged -= OnCodeChanged;
		_code.ScrollOffsetChanged -= OnCodeScrolled;
		_layouts.Clear();
		GetGraphicsFactory().TextEngine.ManagedCache.ReleaseOwner(this);
		base.OnDispose();
	}
}
