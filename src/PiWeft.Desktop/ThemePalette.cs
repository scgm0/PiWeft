using Aprillz.MewUI;
using PiWeft.CodingAgent.Themes;

namespace PiWeft.Desktop;

/// <summary>
/// 把 pi 主题的颜色角色映射到桌面的 <see cref="AppPalette"/>。pi 的角色比桌面配色细，
/// 因此这里做一次「最佳对应」：主题没写的角色沿用内建深浅配色的取值，避免出现空白或不可读的区域。
/// </summary>
internal static class ThemePalette
{
	/// <summary>用 <paramref name="theme"/> 覆盖 <paramref name="fallback"/> 中对应的角色。</summary>
	public static AppPalette FromTheme(PiTheme theme, AppPalette fallback) => new()
	{
		// pi 没有窗口背景角色：优先用导出的页面背景，其次是自定义面板背景。
		WindowBackground = Surface(theme.ExportPageBackground)
			?? Pick(theme, ThemeTokens.CustomMessageBg, fallback.WindowBackground),
		SidebarBackground = Surface(theme.ExportCardBackground)
			?? Pick(theme, ThemeTokens.UserMessageBg, fallback.SidebarBackground),
		CardBackground = Pick(theme, ThemeTokens.UserMessageBg, fallback.CardBackground),
		CardBackgroundAlt = Pick(theme, ThemeTokens.ToolPendingBg, fallback.CardBackgroundAlt),
		HoverBackground = Pick(theme, ThemeTokens.SelectedBg, fallback.HoverBackground),

		// pi 的代码块只有前景色，背景沿用内建取值以保证对比度。
		CodeBackground = fallback.CodeBackground,
		CodeText = Pick(theme, ThemeTokens.MdCodeBlock, Pick(theme, ThemeTokens.MdCode, fallback.CodeText)),

		// 终端是独立的浅/深表面：跟随 fallback 的主题明暗，保留自定义主题的链接色。
		TerminalBackground = fallback.TerminalBackground,
		TerminalForeground = fallback.TerminalForeground,
		TerminalBorder = fallback.TerminalBorder,
		TerminalSelection = fallback.TerminalSelection,
		TerminalCursor = fallback.TerminalCursor,
		TerminalCursorUnfocused = fallback.TerminalCursorUnfocused,
		TerminalLink = Pick(theme, ThemeTokens.MdLink, fallback.TerminalLink),

		TextPrimary = Pick(theme, ThemeTokens.Text, fallback.TextPrimary),
		TextMuted = Pick(theme, ThemeTokens.Muted, fallback.TextMuted),
		TextSubtle = Pick(theme, ThemeTokens.Dim, fallback.TextSubtle),
		TextFaint = Pick(theme, ThemeTokens.Dim, fallback.TextFaint),

		// 强调色作背景时，文字取主题的背景色通常最易读。
		TextOnAccent = Pick(theme, ThemeTokens.UserMessageBg, fallback.TextOnAccent),
		Accent = Pick(theme, ThemeTokens.Accent, fallback.Accent),
		AccentSoft = Pick(theme, ThemeTokens.SelectedBg, fallback.AccentSoft),

		Border = Pick(theme, ThemeTokens.Border, fallback.Border),
		BorderSubtle = Pick(theme, ThemeTokens.BorderMuted, fallback.BorderSubtle),
		Error = Pick(theme, ThemeTokens.Error, fallback.Error),
		Success = Pick(theme, ThemeTokens.Success, fallback.Success),
		Info = Pick(theme, ThemeTokens.MdLink, fallback.Info),
		Thinking = Pick(theme, ThemeTokens.ThinkingText, Pick(theme, ThemeTokens.ThinkingMedium, fallback.Thinking)),
		Transparent = fallback.Transparent,
	};

	private static Color? Surface(ThemeColor? color) =>
		color is { IsTerminalDefault: false } value ? ToColor(value) : null;

	private static Color Pick(PiTheme theme, string token, Color fallback)
	{
		var color = theme.GetOrDefault(token, ThemeColor.TerminalDefault);
		return color.IsTerminalDefault ? fallback : ToColor(color);
	}

	private static Color ToColor(ThemeColor color) => new(color.R, color.G, color.B);
}
