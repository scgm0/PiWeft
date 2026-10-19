using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

namespace PiWeft.Desktop;

/// <summary>可选择的原文消息，不解析 Markdown，也不创建独立的纵向视口。</summary>
internal static class MessageText
{
	/// <summary>流式追加时保留既有选区；替换原文时不沿用旧位置。</summary>
	public static void Update(MultiLineTextBox text, string value)
	{
		var previous = text.Text;
		if (previous == value) return;
		var start = text.SelectionStart;
		var length = text.SelectionLength;
		var preserve = length > 0 && value.StartsWith(previous, StringComparison.Ordinal);
		text.Text = value;
		if (preserve) text.Select(start, length);
	}

	public static MultiLineTextBox Create(string text = "", bool muted = false, bool mono = false, bool wrap = true,
		double? fontSize = null)
	{
		return new MultiLineTextBox
		{
			Text = text,
			IsReadOnly = true,
			SizeToDocument = true,
			Wrap = wrap,
			Background = AppTheme.Transparent,
			BorderThickness = 0,
			Padding = Thickness.Zero,
			MinHeight = 0,
			CornerRadius = 0,
			FontFamily = mono ? AppTheme.MonoFont : AppFonts.DefaultUi,
			FontSize = fontSize ?? AppTheme.FontSizeChat,
			Foreground = muted ? AppTheme.TextMuted : AppTheme.TextPrimary,
			Tag = muted ? AppStyles.RoleMuted : AppStyles.RolePrimary,
		}.PrimarySelection();
	}
}
