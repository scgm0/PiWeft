using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Aprillz.MewUI.Rendering;

namespace PiWeft.Desktop;

/// <summary>
/// 光标层：把光标按「字格几何」画出来，对齐参考实现 MewUI.Terminal 的
/// <c>TerminalCursorVisual</c> / <c>TerminalRenderer.RenderCursor</c>。
/// </summary>
/// <remarks>
/// 旧做法把光标烘进文本 run：块光标 = 给那个 run 铺背景色，竖线/下划线 = 把字符染成光标色再加一条下划线。
/// 于是出现三类渲染缺陷：
/// <list type="bullet">
/// <item>竖线光标根本不是竖线，只是「字符变色 + 多一条下划线」；</item>
/// <item>块光标只盖住该 run 自己的排布宽度；行尾那一格没有字形可亮，还要临时补一个 run，
/// 于是块落在「最后一个有字形的格子」后面 —— 光标越往右，块偏得越左；</item>
/// <item>失焦的空心框用 <see cref="Controls.Border"/> 画在小数坐标上，1 DIP 描边落在像素之间，
/// 上下两条边被抗锯齿吃掉，只剩左右两撇 <c>[ ]</c>。</item>
/// </list>
/// 现在形状全部由矩形几何画出，并且：
/// <list type="bullet">
/// <item>格子矩形横向取自本行真实排布（那个字符在哪，光标就压在哪），纵向取自该行 arrange 后的实际位置；</item>
/// <item>四条边都贴到设备像素，描边按一个物理像素计 —— 失焦就是完整的一圈空心框；</item>
/// <item>同一个类用两份：实心块那份排在文本行底下（块是「格子背景」，块下那个字符由文本层
/// 按底色绘制，仍然读得出来）；竖线/底线/失焦空心框那份排在文本行之上，不遮字，
/// 单元格自带背景色（vim 高亮行、状态栏）时也不会被盖掉。</item>
/// </list>
/// </remarks>
internal sealed class TerminalCursorLayer : FrameworkElement
{
	/// <summary>竖线宽度占格宽的比例（与 MewUI.Terminal 同口径）。</summary>
	private const double BarWidthRatio = 0.12;

	/// <summary>底线厚度占行高的比例。</summary>
	private const double UnderlineHeightRatio = 0.1;

	private Rect _cell;
	private Color _color;
	private TerminalCursorStyle _style = TerminalCursorStyle.Block;
	private bool _focused = true;
	private bool _visible;

	public TerminalCursorLayer()
	{
		IsHitTestVisible = false;
		HorizontalAlignment = HorizontalAlignment.Stretch;
		VerticalAlignment = VerticalAlignment.Stretch;
		IsVisible = false;
	}

	/// <summary>窗口less 测试用：光标当前是否画得出来。</summary>
	internal bool CursorVisible => _visible;

	/// <summary>窗口less 测试用：聚焦态（决定实心块还是空心框）。</summary>
	internal bool CursorFocused => _focused;

	/// <summary>窗口less 测试用：光标色。</summary>
	internal Color CursorColor => _color;

	/// <summary>窗口less 测试用：当前光标格（窗口坐标，未做像素对齐）。</summary>
	internal Rect CursorCell => _cell;

	/// <summary>写入本帧要画的光标；几何与终端一致、只是没对齐到像素，对齐在 <see cref="OnRender"/> 里做。</summary>
	internal void SetCursor(Rect cell, Color color, TerminalCursorStyle style, bool focused, bool visible)
	{
		var changed = visible != _visible || focused != _focused ||
			style != _style || color != _color || cell != _cell;
		if (!changed)
		{
			return;
		}

		_cell = cell;
		_color = color;
		_style = style;
		_focused = focused;
		_visible = visible;
		IsVisible = visible;
		InvalidateVisual();
	}

	protected override Size MeasureContent(Size availableSize) =>
		new(
			double.IsFinite(availableSize.Width) ? availableSize.Width : 0,
			double.IsFinite(availableSize.Height) ? availableSize.Height : 0);

	protected override void OnThemeChanged(Theme oldTheme, Theme newTheme) => InvalidateVisual();

	protected override void OnRender(IGraphicsContext context)
	{
		if (!_visible || _cell.Width <= 0 || _cell.Height <= 0)
		{
			return;
		}

		// 边贴到设备像素，线条按物理像素计：小数坐标上的 1 DIP 描边会被抗锯齿糊掉半边。
		var scale = context.DpiScale;
		var cell = LayoutRounding.SnapRectEdgesToPixels(_cell, scale);
		var pixel = 1.0 / scale;
		var thickness = Math.Max(1, Math.Round(scale)) / scale;

		if (!_focused)
		{
			// 失焦：一整圈一个物理像素的空心框，字符保持原色，不做反白也不染色。
			context.DrawRectangle(cell, _color, pixel, strokeInset: true);
			return;
		}

		switch (_style)
		{
			case TerminalCursorStyle.Bar:
			{
				// 竖线：格宽的一成二，至少一个物理像素，占满整格高度。
				var width = Math.Max(thickness, Math.Round(cell.Width * BarWidthRatio * scale) / scale);
				context.FillRectangle(new Rect(cell.X, cell.Y, width, cell.Height), _color);
				break;
			}

			case TerminalCursorStyle.Underline:
			{
				// 下划线：格底一条线，画在字符底下而不是压在字符上。
				var height = Math.Max(thickness, Math.Round(cell.Height * UnderlineHeightRatio * scale) / scale);
				context.FillRectangle(new Rect(cell.X, cell.Bottom - height, cell.Width, height), _color);
				break;
			}

			default:
				context.FillRectangle(cell, _color);
				break;
		}
	}
}
