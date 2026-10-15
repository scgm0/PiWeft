using Aprillz.MewUI;
using PiWeft.CodingAgent.Themes;

namespace PiWeft.Desktop;

/// <summary>
/// 把界面偏好里的主题名解析成 MewUI 变体与自定义调色板。
/// 内建名（<c>dark</c>/<c>light</c>/<c>system</c>）由框架配色处理；
/// 其余名字按 pi 主题加载（<c>&lt;agentDir&gt;/themes/&lt;name&gt;.json</c> 或项目 <c>.pi/themes</c>）。
/// </summary>
internal static class DesktopTheme
{
	/// <param name="Variant">要应用的 MewUI 主题变体。</param>
	/// <param name="Light">自定义浅色配色；为 null 时用内建浅色。</param>
	/// <param name="Dark">自定义深色配色；为 null 时用内建深色。</param>
	public readonly record struct Resolution(ThemeVariant Variant, AppPalette? Light, AppPalette? Dark);

	public static Resolution Resolve(string theme, string cwd, string? agentDir)
	{
		if (string.Equals(theme, DesktopPreference.LightTheme, StringComparison.Ordinal))
		{
			return new Resolution(ThemeVariant.Light, null, null);
		}

		if (string.Equals(theme, DesktopPreference.SystemTheme, StringComparison.Ordinal))
		{
			return new Resolution(ThemeVariant.System, null, null);
		}

		if (string.Equals(theme, DesktopPreference.DarkTheme, StringComparison.Ordinal))
		{
			return new Resolution(ThemeVariant.Dark, null, null);
		}

		var parsed = ThemeCatalog.Load(theme, cwd, agentDir, includeProject: true);
		if (parsed is null)
		{
			// 主题被删除或损坏时回退到内建深色，而不是让界面起不来。
			return new Resolution(ThemeVariant.Dark, null, null);
		}

		var isLight = parsed.Appearance == ThemeAppearance.Light;
		var palette = ThemePalette.FromTheme(parsed, isLight ? AppPalette.Light : AppPalette.Dark);
		return isLight
			? new Resolution(ThemeVariant.Light, palette, null)
			: new Resolution(ThemeVariant.Dark, null, palette);
	}

	/// <summary>可选的界面主题列表：三个内建项加上所有已发现的自定义主题。</summary>
	public static IReadOnlyList<(string Name, string? Error)> ListCustomThemes(string cwd, string? agentDir)
	{
		var themes = new List<(string, string?)>();
		foreach (var entry in ThemeCatalog.Discover(cwd, agentDir, includeProject: true))
		{
			// 文件名要等于主题名，pi 的热重载依赖这个约定；这里只展示能被解析的主题。
			if (entry.Theme is not null)
			{
				themes.Add((entry.Name, null));
			}
			else
			{
				themes.Add((entry.Name, entry.Error));
			}
		}

		return themes;
	}
}
