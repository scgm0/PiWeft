using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Aprillz.MewUI.Rendering;
using Aprillz.MewUI.Resources;

namespace PiWeft.Desktop;

/// <summary>
/// 待办行的状态图标（对齐 PiDeck 的 lucide-react 用法）：完成 <c>circle-check</c>、
/// 进行中 <c>loader-circle</c>、待处理 <c>circle-dashed</c>，全部取自内嵌 Lucide SVG
/// （tools/fetch-lucide-icons.sh 那套），颜色按主题角色解析后交给 SVG 着色。
/// 自己不画图标：笔画风格、圆头与 2.25 号线宽和全应用其余图标一致，换肤只需重绘。
/// </summary>
internal sealed class TodoStatusGlyph : FrameworkElement
{
	public const double DefaultSize = 16;

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
		if (side <= 1) return;

		// 图标本身来自 Lucide；这里只决定画在哪个方框里。
		var tint = _status switch
		{
			"completed" => palette.Success,
			"in_progress" => palette.Accent,
			_ => palette.TextFaint,
		};
		if (AppIcons.Get(IconName, tint) is not IVectorImageSource vector)
		{
			return;
		}

		// Lucide 的画布留白已经算在 viewBox 里，这里只需再留半像素避免贴边。
		const double inset = 0.5;
		vector.Render(context, new Rect(
			bounds.X + inset, bounds.Y + inset, side - inset * 2, side - inset * 2));
	}

	/// <summary>状态 → Lucide 图标名。</summary>
	private string IconName => _status switch
	{
		"completed" => "circle-check",
		"in_progress" => "loader-circle",
		_ => "circle-dashed",
	};
}
