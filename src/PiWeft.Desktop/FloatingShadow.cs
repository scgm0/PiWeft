using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Aprillz.MewUI.Rendering;

namespace PiWeft.Desktop;

/// <summary>Draws only the soft shadow under the floating move-to-latest button.</summary>
internal sealed class FloatingShadow : FrameworkElement
{
	protected override void OnThemeChanged(Theme oldTheme, Theme newTheme)
	{
		base.OnThemeChanged(oldTheme, newTheme);
		InvalidateVisual();
	}

	protected override void OnRender(IGraphicsContext context)
	{
		if (Bounds.Width <= 0 || Bounds.Height <= 0) return;
		var theme = Theme;
		var alpha = (byte)(theme.IsDark ? 0x96 : 0x36);
		// 贴住按钮下缘、收小模糊半径，避免看起来像一块分离的灰色底板。
		context.DrawBoxShadow(Bounds, Bounds.Width / 2, 7, Color.FromArgb(alpha, 0, 0, 0), 0, 2);
	}
}
