using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

namespace PiWeft.Desktop;

/// 输入区自绘的占位提示。框架的 Placeholder 聚焦即隐藏，而会话 composer 与起始页的
/// 输入框都会在用户点进来之后继续给出提示；两处用同一份字形与内边距。
internal static class ComposerHints
{
	public static TextBlock Build(string text) =>
		new TextBlock().FontSize(AppTheme.FontSizeChat).SubtleText()
			.TextTrimming(TextTrimming.CharacterEllipsis)
			.Text(text)
			.Margin(new Thickness(AppTheme.Space2))
			.IsHitTestVisible(false)
			.VerticalAlignment(VerticalAlignment.Top);
}
