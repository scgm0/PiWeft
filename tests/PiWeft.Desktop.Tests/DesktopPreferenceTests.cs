using System.Text.Json.Nodes;
using Aprillz.MewUI;
using PiWeft.Desktop;
using Xunit;

namespace PiWeft.Desktop.Tests;

/// <summary>界面偏好的落盘与容错。主题在启动前读取，因此这里的行为直接影响启动结果。</summary>
public class DesktopPreferenceTests : IDisposable
{
	private readonly string _agentDir = Path.Combine(Path.GetTempPath(), "piweft-desktop-pref-" + Guid.NewGuid().ToString("N")[..8]);

	public DesktopPreferenceTests() => Directory.CreateDirectory(_agentDir);

	public void Dispose()
	{
		if (Directory.Exists(_agentDir))
		{
			Directory.Delete(_agentDir, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	[Fact]
	public void DefaultsToDarkWhenNoFileExists()
	{
		var preference = DesktopPreference.Load(_agentDir);

		Assert.Equal(DesktopPreference.DarkTheme, preference.Theme);
		Assert.False(preference.IsLight);
		Assert.False(File.Exists(DesktopPreference.Path(_agentDir)));
	}

	[Fact]
	public void CycleGoesDarkLightSystemAndRoundTripsThroughTheFile()
	{
		var light = DesktopPreference.Load(_agentDir).Cycle();
		light.Save(_agentDir);
		Assert.True(light.IsLight);
		Assert.Equal(ThemeVariant.Light, light.Variant);
		Assert.True(DesktopPreference.Load(_agentDir).IsLight);

		var system = DesktopPreference.Load(_agentDir).Cycle();
		system.Save(_agentDir);
		Assert.True(system.IsSystem);
		Assert.Equal(ThemeVariant.System, system.Variant);
		Assert.True(DesktopPreference.Load(_agentDir).IsSystem);

		var dark = DesktopPreference.Load(_agentDir).Cycle();
		dark.Save(_agentDir);
		Assert.False(DesktopPreference.Load(_agentDir).IsLight);
		Assert.Equal(ThemeVariant.Dark, DesktopPreference.Load(_agentDir).Variant);
	}

	[Fact]
	public void SaveKeepsOtherFieldsInTheFile()
	{
		var path = DesktopPreference.Path(_agentDir);
		File.WriteAllText(path, """{"theme":"light","windowWidth":1280}""");

		DesktopPreference.Load(_agentDir).Cycle().Save(_agentDir);

		var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();

		// 轮换一次：light → system，其它字段原样保留。
		Assert.Equal(DesktopPreference.SystemTheme, root["theme"]!.GetValue<string>());
		Assert.Equal(1280, root["windowWidth"]!.GetValue<int>());
	}

	[Fact]
	public void PinnedSessionsRoundTripAndToggle()
	{
		var preference = DesktopPreference.Load(_agentDir).TogglePinned("session-a").TogglePinned("session-b");
		Assert.Equal(["session-b", "session-a"], preference.Pinned);
		preference.Save(_agentDir);

		var loaded = DesktopPreference.Load(_agentDir);
		Assert.True(loaded.IsPinned("session-a"));
		Assert.True(loaded.IsPinned("session-b"));

		loaded.TogglePinned("session-a").Save(_agentDir);
		var after = DesktopPreference.Load(_agentDir);
		Assert.False(after.IsPinned("session-a"));
		Assert.True(after.IsPinned("session-b"));

		// 全部取消后不再写出空数组。
		after.TogglePinned("session-b").Save(_agentDir);
		Assert.DoesNotContain("pinned", File.ReadAllText(DesktopPreference.Path(_agentDir)));
	}

	[Fact]
	public void WindowStateRoundTrips()
	{
		DesktopPreference.Load(_agentDir).WithWindow(1440, 900, 80, 40).Save(_agentDir);

		var loaded = DesktopPreference.Load(_agentDir);
		Assert.Equal(1440, loaded.WindowWidth);
		Assert.Equal(900, loaded.WindowHeight);
		Assert.Equal(80, loaded.WindowLeft);
		Assert.Equal(40, loaded.WindowTop);

		// 非法尺寸会被忽略而不是写进文件。
		DesktopPreference.Load(_agentDir).WithWindow(0, -1, 0, 0).Save(_agentDir);
		Assert.Null(DesktopPreference.Load(_agentDir).WindowWidth);
	}

	[Fact]
	public void RecentProjectsAndModelsAreBoundedDeduplicatedAndRoundTrip()
	{
		var preference = new DesktopPreference();
		for (var index = 0; index < 12; index++)
		{
			preference = preference.RememberProject(Path.Combine(_agentDir, $"project-{index}"))
				.RememberModel("provider", $"model-{index}");
		}

		preference = preference.RememberProject(Path.Combine(_agentDir, "project-8"))
			.RememberModel("provider", "model-8");
		preference.Save(_agentDir);
		var loaded = DesktopPreference.Load(_agentDir);
		Assert.Equal(8, loaded.RecentProjects.Count);
		Assert.Equal(8, loaded.RecentModels.Count);
		Assert.EndsWith("project-8", loaded.RecentProjects[0]);
		Assert.Equal("provider/model-8", loaded.RecentModels[0]);
		Assert.Equal(8, loaded.RecentModels.Distinct().Count());
		Assert.Equal(preference.RecentProjects, loaded.RecentProjects);
	}

	[Theory]
	[InlineData("{\"theme\":42}")]
	[InlineData("{\"theme\":[]}")]
	public void InvalidThemeTypeDoesNotCrashStartup(string raw)
	{
		File.WriteAllText(DesktopPreference.Path(_agentDir), raw);
		Assert.Equal(DesktopPreference.DarkTheme, DesktopPreference.Load(_agentDir).Theme);
	}

	[Theory]
	[InlineData(0, 0)]
	[InlineData(-1920, -200)]
	public void WindowPositionsIncludeZeroAndNegativeCoordinates(double left, double top)
	{
		new DesktopPreference().WithWindow(1280, 820, left, top).Save(_agentDir);
		var loaded = DesktopPreference.Load(_agentDir);
		Assert.Equal(left, loaded.WindowLeft);
		Assert.Equal(top, loaded.WindowTop);
	}

	[Fact]
	public void SidebarStateRoundTripsAndInvalidWidthIsClamped()
	{
		new DesktopPreference { SidebarCollapsed = true, SidebarWidth = 336 }.Save(_agentDir);
		var preference = DesktopPreference.Load(_agentDir);
		Assert.True(preference.SidebarCollapsed);
		Assert.Equal(336, preference.SidebarWidth);
		File.WriteAllText(DesktopPreference.Path(_agentDir), "{\"sidebarWidth\":99999,\"sidebarCollapsed\":42}");
		preference = DesktopPreference.Load(_agentDir);
		Assert.False(preference.SidebarCollapsed);
		Assert.Equal(480, preference.SidebarWidth);
	}

	[Fact]
	public void ReadingPreferencesRoundTripAndRejectInvalidNumbers()
	{
		new DesktopPreference { ChatFontSize = 20, ReadingWidth = 1360 }.Save(_agentDir);
		var loaded = DesktopPreference.Load(_agentDir);
		Assert.Equal(20, loaded.ChatFontSize);
		Assert.Equal(1360, loaded.ReadingWidth);
		File.WriteAllText(DesktopPreference.Path(_agentDir), "{\"chatFontSize\":99,\"readingWidth\":1}");
		loaded = DesktopPreference.Load(_agentDir);
		Assert.Equal(22, loaded.ChatFontSize);
		Assert.Equal(760, loaded.ReadingWidth);
	}

	[Fact]
	public void CorruptOrUnknownValuesFallBackToDark()
	{
		var path = DesktopPreference.Path(_agentDir);

		File.WriteAllText(path, "{ not json");
		Assert.False(DesktopPreference.Load(_agentDir).IsLight);

		File.WriteAllText(path, """{"theme":"solarized"}""");
		Assert.False(DesktopPreference.Load(_agentDir).IsLight);
	}
}

/// <summary>把默认模型写回 settings.json：只改两个键，别的字段（含未建模的）必须原样保留。</summary>
public class DefaultModelSettingTests : IDisposable
{
	private readonly string _agentDir = Path.Combine(Path.GetTempPath(), "piweft-desktop-default-" + Guid.NewGuid().ToString("N")[..8]);

	public DefaultModelSettingTests() => Directory.CreateDirectory(_agentDir);

	public void Dispose()
	{
		if (Directory.Exists(_agentDir))
		{
			Directory.Delete(_agentDir, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	[Fact]
	public void WritingKeepsUnknownKeysAndIsReadableBack()
	{
		var path = Path.Combine(_agentDir, "settings.json");
		File.WriteAllText(path, """
			{
			  "defaultModel": "old-model",
			  "timeoutMs": 1234,
			  "someFutureKey": { "nested": true },
			  "compaction": { "enabled": false, "keepRecentTokens": 4321 }
			}
			""");

		PiWeft.CodingAgent.Settings.CodingAgentSettings.SetDefaultModel("anthropic", "claude-sonnet-4-5", _agentDir);

		var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject();
		Assert.Equal("anthropic", root["defaultProvider"]!.GetValue<string>());
		Assert.Equal("claude-sonnet-4-5", root["defaultModel"]!.GetValue<string>());

		// 未建模的键与已有设置都还在。
		Assert.True(root["someFutureKey"]!["nested"]!.GetValue<bool>());
		Assert.Equal(1234, root["timeoutMs"]!.GetValue<int>());
		Assert.False(root["compaction"]!["enabled"]!.GetValue<bool>());

		// 重新加载时生效。
		var settings = PiWeft.CodingAgent.Settings.CodingAgentSettings.Load("/tmp", _agentDir);
		Assert.Equal("anthropic", settings.DefaultProvider);
		Assert.Equal("claude-sonnet-4-5", settings.DefaultModel);
	}

	[Fact]
	public void WritingCreatesTheFileWhenMissing()
	{
		PiWeft.CodingAgent.Settings.CodingAgentSettings.SetDefaultModel("openai", "gpt-5", _agentDir);

		var settings = PiWeft.CodingAgent.Settings.CodingAgentSettings.Load("/tmp", _agentDir);
		Assert.Equal("openai", settings.DefaultProvider);
		Assert.Equal("gpt-5", settings.DefaultModel);
	}
}

/// <summary>快捷键解析、覆盖与冲突检测。</summary>
public class ShortcutMapTests
{
	[Theory]
	[InlineData("Ctrl+P", "Ctrl+P")]
	[InlineData("ctrl+shift+p", "Ctrl+Shift+P")]
	[InlineData("F1", "F1")]
	[InlineData("Ctrl+1", "Ctrl+1")]
	[InlineData("Ctrl+0", "Ctrl+0")]
	[InlineData("Alt+Enter", "Alt+Enter")]
	public void ParsesAndFormatsRoundTrip(string text, string expected)
	{
		var binding = ShortcutBinding.TryParse(text);
		Assert.NotNull(binding);
		Assert.Equal(expected, binding!.ToString());
	}

	[Theory]
	[InlineData("")]
	[InlineData("   ")]
	[InlineData("Ctrl+")]
	[InlineData("Kp")]
	[InlineData("Ctrl+P+Q")]
	public void RejectsInvalidInput(string text) => Assert.Null(ShortcutBinding.TryParse(text));

	[Fact]
	public void DefaultsComeFromTheTableAndOverridesWin()
	{
		var defaults = new ShortcutMap();
		Assert.Equal("Ctrl+P", defaults.Describe(ShortcutActions.Palette));
		Assert.Empty(defaults.Overrides);

		var custom = new ShortcutMap(new Dictionary<string, string> { [ShortcutActions.Palette] = "Ctrl+Shift+K" });
		Assert.Equal("Ctrl+Shift+K", custom.Describe(ShortcutActions.Palette));
		Assert.Equal("Ctrl+Shift+K", custom.Overrides[ShortcutActions.Palette]);

		// 别的动作不受影响。
		Assert.Equal("Ctrl+T", custom.Describe(ShortcutActions.NewSession));
	}

	[Fact]
	public void BadOverridesFallBackToTheDefaultInsteadOfBreakingEverything()
	{
		var map = new ShortcutMap(new Dictionary<string, string>
		{
			[ShortcutActions.Palette] = "not a key",
			["noSuchAction"] = "Ctrl+J",
		});

		Assert.Equal("Ctrl+P", map.Describe(ShortcutActions.Palette));
		Assert.Contains(ShortcutActions.Palette, map.Ignored);
		Assert.Contains("noSuchAction", map.Ignored);
		Assert.Empty(map.Overrides);
	}

	[Fact]
	public void DetectsBindingsThatCollide()
	{
		var map = new ShortcutMap(new Dictionary<string, string> { [ShortcutActions.Git] = "Ctrl+P" });
		var conflict = Assert.Single(map.Conflicts());

		Assert.Equal(ShortcutActions.Git, conflict.Id);
		Assert.Equal(ShortcutActions.Palette, conflict.OtherId);
		Assert.Equal("Ctrl+P", conflict.Binding);
	}

	[Fact]
	public void RebindAndResetChangeOnlyThatAction()
	{
		var map = new ShortcutMap();
		Assert.True(map.Rebind(ShortcutActions.Git, "Ctrl+G"));
		Assert.Equal("Ctrl+G", map.Describe(ShortcutActions.Git));
		Assert.False(map.Rebind(ShortcutActions.Git, "nonsense"));
		Assert.Equal("Ctrl+G", map.Describe(ShortcutActions.Git));

		map.Reset(ShortcutActions.Git);
		Assert.Equal("Ctrl+O", map.Describe(ShortcutActions.Git));
		Assert.Empty(map.Overrides);
	}
}
