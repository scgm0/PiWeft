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

	/// <summary>阴影画布比圆钮每边多出的空白；模糊半径 + 位移都必须小于它，才能画满不裁边。</summary>
	internal const double Inset = 10;

	protected override void OnRender(IGraphicsContext context)
	{
		if (Bounds.Width <= 0 || Bounds.Height <= 0) return;
		// 阴影挂在浮层里，收不到窗口的换肤通知（层通知只到根元素），
		// 所以按应用当前配色判断深浅——用元素上的旧 Theme 会在切换后残留深色的重阴影。
		var alpha = (byte)(AppTheme.IsLightTheme ? 0x22 : 0x96);
		// 只给中间那颗圆钮投影：贴住下缘、收小模糊半径，避免看起来像一块分离的灰色底板。
		var button = new Rect(Bounds.X + Inset, Bounds.Y + Inset,
			Math.Max(0, Bounds.Width - Inset * 2), Math.Max(0, Bounds.Height - Inset * 2));
		if (button.Width <= 0 || button.Height <= 0) return;
		context.DrawBoxShadow(button, button.Width / 2, 7, Color.FromArgb(alpha, 0, 0, 0), 0, 2);
	}
}
