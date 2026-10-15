using PiWeft.CodingAgent.Resources;
using PiWeft.CodingAgent.Themes;
using Xunit;

namespace PiWeft.CodingAgent.Tests;

/// <summary>
/// 主题颜色解析。黄金值取自 pi 的 <c>packages/tui/test/colors.test.ts</c>，
/// 保证同一份主题文件在两侧解析出相同的颜色。
/// </summary>
public class ThemeColorParserTests
{
	[Fact]
	public void Hex_ParsesThreeAndSixDigitForms()
	{
		Assert.True(ThemeColorParser.TryParse("#abc", null, out var shortForm, out _));
		Assert.Equal(new ThemeColor(0xaa, 0xbb, 0xcc), shortForm);

		Assert.True(ThemeColorParser.TryParse("#00aaff", null, out var longForm, out _));
		Assert.Equal(new ThemeColor(0x00, 0xaa, 0xff), longForm);
	}

	[Fact]
	public void EmptyString_IsTerminalDefault()
	{
		Assert.True(ThemeColorParser.TryParse("", null, out var color, out _));
		Assert.True(color.IsTerminalDefault);
	}

	[Theory]
	[InlineData("0")]
	[InlineData("39")]
	[InlineData("255")]
	public void AnsiIndex_UsesXtermPalette(string value)
	{
		Assert.True(ThemeColorParser.TryParse(value, null, out var color, out _));
		Assert.True(AnsiPalette.TryGet(int.Parse(value, System.Globalization.CultureInfo.InvariantCulture), out var expected));
		Assert.Equal(expected, color);
		Assert.False(color.IsTerminalDefault);
	}

	[Fact]
	public void Oklch_MatchesPiReferenceValues()
	{
		// 全饱和的红色 cusp。
		Assert.True(ThemeColorParser.TryParse("oklch(62.7955% 0.257683 29.2339)", null, out var red, out _));
		Assert.Equal(new ThemeColor(255, 0, 0), red);

		Assert.True(ThemeColorParser.TryParse("oklch(1 0.3 150)", null, out var white, out _));
		Assert.Equal(new ThemeColor(255, 255, 255), white);

		Assert.True(ThemeColorParser.TryParse("oklch(0 0.3 150)", null, out var black, out _));
		Assert.Equal(new ThemeColor(0, 0, 0), black);
	}

	[Fact]
	public void Okhsl_MatchesPiReferenceValues()
	{
		Assert.True(ThemeColorParser.TryParse("okhsl(29.23 100% 56.8%)", null, out var red, out _));
		Assert.Equal(new ThemeColor(255, 0, 0), red);

		// 大小写与 deg 后缀都应被接受，且与不带后缀等价。
		Assert.True(ThemeColorParser.TryParse("OKHSL(250deg 60% 55%)", null, out var withSuffix, out _));
		Assert.True(ThemeColorParser.TryParse("okhsl(250 60% 55%)", null, out var plain, out _));
		Assert.Equal(plain, withSuffix);
	}

	[Fact]
	public void VariableReference_ResolvesChains()
	{
		var variables = new Dictionary<string, string>(StringComparer.Ordinal)
		{
			["primary"] = "okhsl(29.23 100% 56.8%)",
			["alias"] = "primary",
		};

		Assert.True(ThemeColorParser.TryParse("alias", variables, out var color, out _));
		Assert.Equal(new ThemeColor(255, 0, 0), color);
	}

	[Fact]
	public void MissingVariable_Fails()
	{
		Assert.False(ThemeColorParser.TryParse("ghost", new Dictionary<string, string>(), out _, out var error));
		Assert.NotNull(error);
	}

	[Fact]
	public void CircularReference_Fails()
	{
		var variables = new Dictionary<string, string>(StringComparer.Ordinal)
		{
			["a"] = "b",
			["b"] = "a",
		};

		Assert.False(ThemeColorParser.TryParse("a", variables, out _, out var error));
		Assert.Contains("循环", error, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("#ab")]
	[InlineData("#gggggg")]
	[InlineData("oklch(2 0.1 200)")]
	[InlineData("okhsl(250 160% 55%)")]
	[InlineData("256")]
	[InlineData("red")]
	public void InvalidValues_Fail(string value)
	{
		Assert.False(ThemeColorParser.TryParse(value, new Dictionary<string, string>(), out _, out var error));
		Assert.NotNull(error);
	}
}

/// <summary>主题文件的解析与发现。</summary>
public class ThemeCatalogTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-themes-" + Guid.NewGuid().ToString("N")[..8]);
	private readonly string _agentDir;

	public ThemeCatalogTests()
	{
		_agentDir = Path.Combine(_root, "agent");
		Directory.CreateDirectory(Path.Combine(_agentDir, "themes"));
		Directory.CreateDirectory(_root);
		Directory.CreateDirectory(Path.Combine(_root, ".git"));
	}

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	[Fact]
	public void Parse_ReadsNameAppearanceAndColors()
	{
		var json = """
			{
			  "name": "forest",
			  "appearance": "light",
			  "vars": { "base": "okhsl(120 40% 40%)" },
			  "colors": { "text": "#112233", "accent": "base" }
			}
			""";

		var theme = PiTheme.Parse(json, "/tmp/forest.json", ResourceOrigin.UserAuto, out var error, out var warnings);

		Assert.Null(error);
		Assert.NotNull(theme);
		Assert.Equal("forest", theme!.Name);
		Assert.Equal(ThemeAppearance.Light, theme.Appearance);
		Assert.Equal(new ThemeColor(0x11, 0x22, 0x33), theme.Colors["text"]);
		Assert.Contains("缺少常用角色", string.Join(';', warnings), StringComparison.Ordinal);
	}

	[Fact]
	public void Parse_WithoutName_Fails()
	{
		var theme = PiTheme.Parse("""{ "colors": { "text": "#fff" } }""", "/tmp/x.json", ResourceOrigin.UserAuto, out var error, out _);
		Assert.Null(theme);
		Assert.Contains("name", error!, StringComparison.Ordinal);
	}

	[Fact]
	public void Parse_WithUndefinedVariable_Fails()
	{
		var json = """{ "name": "broken", "colors": { "text": "missing" } }""";
		var theme = PiTheme.Parse(json, "/tmp/broken.json", ResourceOrigin.UserAuto, out var error, out _);
		Assert.Null(theme);
		Assert.Contains("missing", error!, StringComparison.Ordinal);
	}

	[Fact]
	public void Discover_FindsUserThemeAndSkipsReservedSystemName()
	{
		File.WriteAllText(Path.Combine(_agentDir, "themes", "forest.json"), """{ "name": "forest", "colors": { "text": "#fff" } }""");
		File.WriteAllText(Path.Combine(_agentDir, "themes", "system.json"), """{ "name": "system", "colors": { "text": "#fff" } }""");

		var entries = ThemeCatalog.Discover(_root, _agentDir, includeProject: true);

		Assert.Contains(entries, entry => entry.Name == "forest" && entry.IsValid);
		Assert.DoesNotContain(entries, entry => entry.Name == "system");
	}

	[Fact]
	public void Discover_ProjectThemeOverridesUserThemeWithSameName()
	{
		var projectThemes = Path.Combine(_root, ".pi", "themes");
		Directory.CreateDirectory(projectThemes);
		File.WriteAllText(Path.Combine(projectThemes, "shared.json"), """{ "name": "shared", "colors": { "text": "#000000" } }""");
		File.WriteAllText(Path.Combine(_agentDir, "themes", "shared.json"), """{ "name": "shared", "colors": { "text": "#ffffff" } }""");

		var entries = ThemeCatalog.Discover(_root, _agentDir, includeProject: true);
		var shared = Assert.Single(entries, entry => entry.Name == "shared");

		Assert.Equal(new ThemeColor(0, 0, 0), shared.Theme!.Colors["text"]);
	}

	[Fact]
	public void Load_ReturnsNullForBuiltInNames()
	{
		Assert.Null(ThemeCatalog.Load(ThemeCatalog.DarkThemeName, _root, _agentDir));
	}
}
