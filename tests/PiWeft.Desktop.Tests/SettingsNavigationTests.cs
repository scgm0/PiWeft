using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using PiWeft.CodingAgent.Prompts;
using PiWeft.CodingAgent.Resources;
using PiWeft.CodingAgent.Skills;
using PiWeft.CodingAgent.Themes;
using Xunit;
using static PiWeft.Desktop.Tests.VisualTree;

namespace PiWeft.Desktop.Tests;

[Collection("mewui-controls")]
public sealed class SettingsNavigationTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "pi-settings-navigation-" + Guid.NewGuid().ToString("N"));

	[Fact]
	public async Task SettingsLoadsConfigurationAndArchiveOnlyWhenVisited()
	{
		var archiveReads = 0;
		var view = Create(() => { archiveReads++; return []; });
		Assert.Empty(Find<ComboBox>(view.View));
		Assert.Equal(0, archiveReads);
		view.ShowConfiguration();
		Assert.Single(Find<ComboBox>(view.View));
		Assert.Equal(0, archiveReads);
		// A fresh system page has no configuration controls and reads the archive on demand.
		var system = Create(() => { archiveReads++; return []; });
		await system.SelectSectionAsync("archive");
		Assert.Equal(1, archiveReads);
	}

	[Fact]
	public async Task ThemeRefreshPreservesTheAppearancePickerAndShortcutCapture()
	{
		var view = Create(() => []);
		await view.SelectSectionAsync("appearance");
		var pickers = Find<ComboBox>(view.View).ToArray();
		var picker = pickers[0];
		view.ApplyTheme();
		Assert.Equal(pickers, Find<ComboBox>(view.View).ToArray());
		await view.SelectSectionAsync("shortcuts");
		view.BeginCapture(ShortcutActions.Palette);
		view.ApplyTheme();
		Assert.Equal(ShortcutActions.Palette, view.CapturingId);
	}

	[Fact]
	public async Task ResourcesSection_RendersHintsAndEmptyState()
	{
		var reloads = 0;
		var view = new SettingsView(
			() => new SettingsSnapshot
			{
				AgentDir = Path.Combine(_root, "agent"), Cwd = _root, ModelCount = 0,
				Tools = [], Skills = [], McpServers = [], Resources = ResourceSnapshot.Empty,
			},
			reloadResources: () =>
			{
				reloads++;
				return Task.CompletedTask;
			});

		await view.SelectSectionAsync("resources");

		// 构建期间不能有被吞掉的错误（例如引用了不存在的内嵌图标）。
		Assert.Null(view.LastSectionError);

		var texts = Find<TextBlock>(view.View).Select(block => block.Text).ToArray();
		Assert.Contains(Strings.ResourcesSkillHint, texts);
		Assert.Contains(Strings.ResourcesEmpty, texts);
		Assert.Contains(Strings.ResourcesReload, texts);
		Assert.Equal(0, reloads);
	}

	[Fact]
	public async Task ResourcesSection_ListsDiscoveredResourcesAndDiagnostics()
	{
		var origin = new ResourceOrigin(ResourceScope.User, "/home/u/.pi/agent", ResourceOrigin.UserRank);
		var resources = new ResourceSnapshot
		{
			Skills =
			[
				new Skill
				{
					Name = "demo-skill", Description = "演示技能",
					FilePath = "/home/u/.pi/agent/skills/demo/SKILL.md", BaseDirectory = "/home/u/.pi/agent/skills/demo",
					Origin = origin,
				},
			],
			Templates =
			[
				new PromptTemplate { Name = "review", Content = "review", FilePath = "/home/u/.pi/agent/prompts/review.md", Origin = origin },
			],
			Themes =
			[
				new ThemeEntry
				{
					Name = "forest", FilePath = "/home/u/.pi/agent/themes/forest.json", Origin = origin,
					Theme = PiTheme.Parse("""{ "name": "forest", "appearance": "light", "colors": { "text": "#ffffff" } }""",
						"/home/u/.pi/agent/themes/forest.json", origin, out _, out _),
				},
			],
			Packages =
			[
				new PiPackage { Name = "demo-pkg", Source = "npm:demo-pkg", Root = "/home/u/.pi/agent/npm/node_modules/demo-pkg", Scope = ResourceScope.User },
			],
			Diagnostics = ["主题 broken.json 无效：缺少必需的 name 字段"],
		};

		var view = new SettingsView(() => new SettingsSnapshot
		{
			AgentDir = Path.Combine(_root, "agent"), Cwd = _root, ModelCount = 0,
			Tools = [], Skills = [], McpServers = [], Resources = resources,
		});

		await view.SelectSectionAsync("resources");

		Assert.Null(view.LastSectionError);
		var texts = Find<TextBlock>(view.View).Select(block => block.Text).ToArray();
		Assert.Contains("demo-skill", texts);
		Assert.Contains("review", texts);
		Assert.Contains("forest", texts);
		Assert.Contains("demo-pkg", texts);
		Assert.Contains(Strings.ResourcesLight, texts);
		Assert.Contains("主题 broken.json 无效：缺少必需的 name 字段", texts);
		Assert.Contains(Strings.ResourcesDiagnostics, texts);
	}

	[Fact]
	public void SettingsDialogAttachesAsAWindowOverlayAndDetachesOnHide()
	{
		var view = Create(() => []);
		var dialog = new SettingsDialog(view);
		var window = new Window();
		var closed = 0;
		dialog.Closed += () => closed++;

		Assert.False(dialog.IsOpen);
		dialog.Show(window);
		Assert.True(dialog.IsOpen);
		Assert.True(window.OverlayLayer.Contains(dialog.View));
		Assert.Same(window, dialog.View.Parent);

		dialog.Hide();
		Assert.False(dialog.IsOpen);
		Assert.Null(dialog.View.Parent);
		Assert.Equal(0, window.OverlayLayer.Count);
		Assert.Equal(1, closed);
		dialog.Hide();
		Assert.Equal(1, closed);
	}

	[Fact]
	public void SettingsDialogDimsTheTitleBarOnlyWhenMaximizedOrFullScreen()
	{
		var view = Create(() => []);
		var dialog = new SettingsDialog(view);
		var window = new Window();
		dialog.Show(window);
		Assert.Equal(1, window.OverlayLayer.Count);

		window.WindowState = WindowState.Maximized;
		Assert.Equal(2, window.OverlayLayer.Count);

		window.WindowState = WindowState.FullScreen;
		Assert.Equal(2, window.OverlayLayer.Count);

		window.WindowState = WindowState.Normal;
		Assert.Equal(1, window.OverlayLayer.Count);

		dialog.Hide();
		Assert.Equal(0, window.OverlayLayer.Count);
	}

	private SettingsView Create(Func<IReadOnlyList<SessionListItem>> archive) => new(() => new SettingsSnapshot
	{
		AgentDir = Path.Combine(_root, "agent"), Cwd = _root, ModelCount = 0,
		Tools = [], Skills = [], McpServers = [], LoadArchived = archive,
	});

	public void Dispose()
	{
		if (Directory.Exists(_root)) Directory.Delete(_root, true);
	}
}
