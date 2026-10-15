using Aprillz.MewUI;
using Xunit;

namespace PiWeft.Desktop.Tests;

/// <summary>界面偏好里的主题名解析与 pi 主题到桌面配色的映射。</summary>
public class DesktopThemeTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-desktoptheme-" + Guid.NewGuid().ToString("N")[..8]);

	private readonly string _agentDir;

	private readonly string _cwd;

	public DesktopThemeTests()
	{
		_agentDir = Path.Combine(_root, "agent");
		_cwd = Path.Combine(_root, "repo");
		Directory.CreateDirectory(Path.Combine(_agentDir, "themes"));
		Directory.CreateDirectory(_cwd);
	}

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	[Theory]
	[InlineData(DesktopPreference.DarkTheme, ThemeVariant.Dark)]
	[InlineData(DesktopPreference.LightTheme, ThemeVariant.Light)]
	[InlineData(DesktopPreference.SystemTheme, ThemeVariant.System)]
	public void Resolve_BuiltInNames_HaveNoCustomPalette(string name, ThemeVariant expected)
	{
		var resolution = DesktopTheme.Resolve(name, _cwd, _agentDir);

		Assert.Equal(expected, resolution.Variant);
		Assert.Null(resolution.Light);
		Assert.Null(resolution.Dark);
	}

	[Fact]
	public void Resolve_CustomLightTheme_MapsRolesToPalette()
	{
		WriteTheme("my", """
			{
			  "name": "my",
			  "appearance": "light",
			  "vars": { "brand": "oklch(62% 0.12 250)" },
			  "colors": {
			    "text": "#1a1a1a", "muted": "#444444", "border": "#dddddd",
			    "accent": "brand", "userMessageBg": "#fafafa", "toolPendingBg": "#f0f0f2"
			  }
			}
			""");

		var resolution = DesktopTheme.Resolve("my", _cwd, _agentDir);

		Assert.Equal(ThemeVariant.Light, resolution.Variant);
		Assert.NotNull(resolution.Light);
		Assert.Null(resolution.Dark);

		var palette = resolution.Light!;
		Assert.Equal(new Color(0x1a, 0x1a, 0x1a), palette.TextPrimary);
		Assert.Equal(new Color(0xdd, 0xdd, 0xdd), palette.Border);

		// 主题没有声明的角色沿用内建浅色，界面不会出现空白区域。
		Assert.Equal(AppPalette.Light.CodeBackground, palette.CodeBackground);
		Assert.Equal(AppPalette.Light.Transparent, palette.Transparent);
	}

	[Fact]
	public void Resolve_CustomDarkTheme_FillsDarkSlot()
	{
		WriteTheme("night", """{ "name": "night", "appearance": "dark", "colors": { "text": "#eeeeee", "userMessageBg": "#101010" } }""");

		var resolution = DesktopTheme.Resolve("night", _cwd, _agentDir);

		Assert.Equal(ThemeVariant.Dark, resolution.Variant);
		Assert.NotNull(resolution.Dark);
		Assert.Null(resolution.Light);
		Assert.Equal(new Color(0x10, 0x10, 0x10), resolution.Dark!.CardBackground);
	}

	[Fact]
	public void Resolve_MissingOrBrokenTheme_FallsBackToBuiltInDark()
	{
		WriteTheme("broken", """{ "colors": { "text": "#fff" } }""");

		foreach (var name in new[] { "ghost", "broken" })
		{
			var resolution = DesktopTheme.Resolve(name, _cwd, _agentDir);
			Assert.Equal(ThemeVariant.Dark, resolution.Variant);
			Assert.Null(resolution.Light);
			Assert.Null(resolution.Dark);
		}
	}

	[Fact]
	public void ListCustomThemes_ReportsParseErrors()
	{
		WriteTheme("good", """{ "name": "good", "colors": { "text": "#ffffff" } }""");
		WriteTheme("bad", """{ "colors": {} }""");

		var themes = DesktopTheme.ListCustomThemes(_cwd, _agentDir);

		Assert.Contains(themes, entry => entry.Name == "good" && entry.Error is null);
		Assert.Contains(themes, entry => entry.Error is not null);
	}

	[Fact]
	public void DesktopPreference_KeepsCustomThemeName()
	{
		File.WriteAllText(DesktopPreference.Path(_agentDir), """{ "theme": "my-theme" }""");

		Assert.Equal("my-theme", DesktopPreference.Load(_agentDir).Theme);
	}

	[Fact]
	public void DesktopPreference_BlankTheme_UsesDarkDefault()
	{
		File.WriteAllText(DesktopPreference.Path(_agentDir), """{ "theme": "  " }""");

		Assert.Equal(DesktopPreference.DarkTheme, DesktopPreference.Load(_agentDir).Theme);
	}

	private void WriteTheme(string name, string json) =>
		File.WriteAllText(Path.Combine(_agentDir, "themes", name + ".json"), json);
}
