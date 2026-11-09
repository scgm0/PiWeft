using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Aprillz.MewUI.Input;

namespace PiWeft.Desktop;

internal static class MessageText
{
	/// 超过这个规模改用固定视口：<c>SizeToDocument</c> 会把整份文档逐行撑开，展开大文件会卡住界面。
	internal const int LargeTextCharacters = 32 * 1024;
	internal const int LargeTextLines = 400;
	internal const double LargeTextMaxHeight = 240;

	/// 大文本按字符数或行数判定；字符数先短路，常见的小代码块不必扫换行。
	internal static bool IsLargeText(string text) =>
		text.Length >= LargeTextCharacters || text.AsSpan().Count('\n') >= LargeTextLines;

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
		double? fontSize = null, bool smoothScroll = true)
	{
		return new MultiLineTextBox
		{
			Text = text,
			IsReadOnly = true,
			// 只读正文/代码不需要输入法：避免在代码块或工具输出上误触发组合输入，
			// 也避免组合范围在文本被流式替换后变成过期的渲染装饰。
			ImeMode = ImeMode.Disabled,
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
		}.PrimarySelection(smoothScroll);
	}
}
