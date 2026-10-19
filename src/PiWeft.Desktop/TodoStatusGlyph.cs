using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Aprillz.MewUI.Rendering;

namespace PiWeft.Desktop;

/// <summary>
/// 待办行的状态字形（对齐 PiDeck）：完成是实心勾圈、进行中是强调色弧环、待处理是虚线环。
/// 用绘制的 16×16 图形而不是字体符号，笔画粗细与颜色都跟主题 token 走，换肤不需要重建列表。
/// </summary>
internal sealed class TodoStatusGlyph : FrameworkElement
{
	public const double DefaultSize = 16;

	private static readonly StrokeStyle RoundStroke = new() { LineCap = StrokeLineCap.Round, LineJoin = StrokeLineJoin.Round };
	private static readonly StrokeStyle DashedStroke = new() { LineCap = StrokeLineCap.Round, LineJoin = StrokeLineJoin.Round, DashArray = [2.0, 2.0] };

	private string _status = "pending";

	public TodoStatusGlyph(string status)
	{
		Width = DefaultSize;
		Height = DefaultSize;
		VerticalAlignment = VerticalAlignment.Center;
		SetStatus(status);
	}

	public string Status => _status;

	public void SetStatus(string status)
	{
		if (string.Equals(_status, status, StringComparison.Ordinal)) return;
		_status = status;
		InvalidateVisual();
	}

	protected override Size MeasureContent(Size availableSize) => new(DefaultSize, DefaultSize);

	protected override void OnThemeChanged(Theme oldTheme, Theme newTheme) => InvalidateVisual();

	protected override void OnRender(IGraphicsContext context)
	{
		var palette = AppTheme.For(Theme);
		var bounds = Bounds;
		var side = Math.Min(bounds.Width, bounds.Height);
		if (side <= 0) return;

		var cx = bounds.X + bounds.Width / 2;
		var cy = bounds.Y + bounds.Height / 2;
		var thickness = Math.Max(1, side * 0.075);
		var radius = (side - thickness) / 2 - 0.4;
		var ring = new Rect(cx - radius, cy - radius, radius * 2, radius * 2);

		switch (_status)
		{
			case "completed":
				context.DrawEllipse(ring, palette.Success, thickness);
				var check = new PathGeometry();
				check.MoveTo(cx - radius * 0.52, cy + radius * 0.02);
				check.LineTo(cx - radius * 0.13, cy + radius * 0.4);
				check.LineTo(cx + radius * 0.55, cy - radius * 0.38);
				context.DrawPath(check, new Pen(palette.Success, thickness, RoundStroke));
				break;

			case "in_progress":
				// 底环是强调色的极淡版本；前景弧留一个开口，转动感不依赖额外动画。
				context.DrawEllipse(ring, palette.Accent.WithAlpha(0x38), thickness);
				var start = -Math.PI / 2;
				var end = start + Math.PI * 1.5;
				var arc = new PathGeometry();
				arc.MoveTo(cx + radius * Math.Cos(start), cy + radius * Math.Sin(start));
				arc.Arc(cx, cy, radius, radius, start, end);
				context.DrawPath(arc, new Pen(palette.Accent, thickness, RoundStroke));
				break;

			default:
				context.DrawEllipse(ring, new Pen(palette.TextFaint, thickness, DashedStroke));
				break;
		}
	}
}
