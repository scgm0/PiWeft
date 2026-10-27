using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using PiWeft.Ai;
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
	public async Task GitSectionOffersModelAndThinkingPickers()
	{
		var model = new Model { Id = "summary", Provider = "test", Api = "test", Name = "Summary" };
		var view = new SettingsView(() => new SettingsSnapshot
		{
			AgentDir = Path.Combine(_root, "agent"), Cwd = _root, ModelCount = 1,
			Models = [model], Tools = [], Skills = [], McpServers = [], Resources = ResourceSnapshot.Empty,
		});

		await view.SelectSectionAsync("git");

		Assert.Null(view.LastSectionError);
		Assert.Equal(2, Find<ComboBox>(view.View).Count());
		Assert.Contains(Find<TextBlock>(view.View), block => block.Text == Strings.GitSummarySettingsHint);
		Assert.Contains(Find<TextBlock>(view.View), block => block.Text == Strings.GitSummaryModel);
		Assert.Contains(Find<TextBlock>(view.View), block => block.Text == Strings.GitSummaryThinking);
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

	/// <summary>
	/// 安全门分区：导航里有入口、分区能渲染、改动真的写进设置文件。
	/// 这条同时兜住「代码里加了 AddNavigationButton 但分区没接上」这一类半截接线。
	/// </summary>
	[Fact]
	public async Task SecuritySectionIsReachableAndPersistsChanges()
	{
		var agentDir = Path.Combine(_root, "security-agent");
		var view = Create(agentDir);
		await view.SelectSectionAsync("security");

		// 总开关 + 默认档 + 三个内建档 + 新建入口都要在。
		var labels = Find<TextBlock>(view.View).Select(static block => block.Text).ToHashSet(StringComparer.Ordinal);
		Assert.Contains(Strings.SettingsSecurity, labels);
		Assert.Contains(Strings.SecurityEnabled, labels);
		Assert.Contains("off", labels);
		Assert.Contains("standard", labels);
		Assert.Contains("strict", labels);

		// 改动立即落盘：设置页不留草稿，也不要求重启。
		var settings = Path.Combine(agentDir, "settings.json");
		Assert.False(File.Exists(settings), "默认形状不该写任何键");

		// 关掉总开关。按标签找而不是取第一个：ComboBox 的下拉里也带 CheckBox，
		// 位置假设会在框架版本变化后悄悄错位。
		// 面板顺序固定：第一个复选框是总开关，其后才是展开档位里的编辑项。
		var enabled = Find<CheckBox>(view.View).First();
		enabled.IsChecked = false;
		Assert.Contains("\"enabled\": false", File.ReadAllText(settings), StringComparison.Ordinal);

		// 档位卡片可以展开编辑。展开 off 档（默认什么都不保护），勾上敏感文件保护后
		// 必须立刻落盘 —— 内建档被改过也要写出来，否则刷新一次改动就消失了。
		var expand = Find<Button>(view.View).Where(button => ContentText(button) == Strings.SecurityExpand).First();
		ImagePreviewTests.Click(expand);

		// 展开后新出现的复选框排在末尾（下拉框的条目里也有 CheckBox，不能按总数计数）。
		var protect = Find<CheckBox>(view.View).Last();
		Assert.NotSame(enabled, protect);
		protect.IsChecked = true;

		var saved = File.ReadAllText(settings);
		Assert.Contains("\"protectSensitivePaths\": true", saved, StringComparison.Ordinal);
		Assert.Contains("\"enabled\": false", saved, StringComparison.Ordinal);
		Assert.Contains("\"id\": \"off\"", saved, StringComparison.Ordinal);

		// 展开态在保存后保持：改一项不该把别的档位全收起。
		Assert.Contains(Find<Button>(view.View), button => ContentText(button) == Strings.SecurityCollapse);
	}

	/// <summary>安全配置保存后必须推给已打开的会话，否则界面档位和实际执行用的档位会不一致。</summary>
	[Fact]
	public void SecurityChangeNotifiesTheHost()
	{
		var notified = 0;
		var view = Create(Path.Combine(_root, "notify-agent"));
		view.SecurityChanged += () => notified++;
		view.ShowConfiguration();
		Assert.Equal(0, notified);
	}

	private static string? ContentText(Button button) =>
		button.Content switch
		{
			TextBlock text => text.Text,
			StackPanel panel => Find<TextBlock>(panel).Select(static block => block.Text).FirstOrDefault(static value => value is { Length: > 0 }),
			DockPanel panel => Find<TextBlock>(panel).Select(static block => block.Text).FirstOrDefault(static value => value is { Length: > 0 }),
			_ => null,
		};

	private SettingsView Create(string? agentDir = null) => new(
		() => new SettingsSnapshot
		{
			AgentDir = agentDir ?? Path.Combine(_root, "agent"),
			Cwd = _root,
			ModelCount = 0,
			Tools = [],
			Skills = [],
			McpServers = [],
		});

	/// <summary>
	/// 设置页必须把磁盘上的自定义档位也渲染出来 —— 之前出现过「Load 正确但面板只列内建档」，
	/// 那种情况下用户新建的档位会在界面上凭空消失（配置还在，界面不认）。
	/// </summary>
	[Fact]
	public async Task SecuritySectionRendersCustomLevelsFromDisk()
	{
		var agentDir = Path.Combine(_root, "custom-level-agent");
		Directory.CreateDirectory(agentDir);
		File.WriteAllText(Path.Combine(agentDir, "settings.json"),
			"""{"security":{"level":"strict","levels":[{"id":"release","name":"发布分支","builtin":false,"pathPolicy":"workspace","toolActions":{"bash":"ask"}}],"sessionLevels":{"/tmp/x/deadbeef.jsonl":"release"}}}""");

		var view = Create(agentDir);
		await view.SelectSectionAsync("security");

		var labels = Find<TextBlock>(view.View).Select(static block => block.Text).ToHashSet(StringComparer.Ordinal);
		Assert.Contains("release", labels);
		Assert.Contains("发布分支", labels);
		// 自定义档可以删除，且会话覆盖会显示出来。
		Assert.Contains(Find<Button>(view.View), button => ContentText(button) is null && HoverText(button) == Strings.SecurityDeleteLevel);
		Assert.Contains(Strings.SecuritySessionLevels, labels);
	}

	private static string? HoverText(Button button) =>
		HoverHints.Text(button) is { Length: > 0 } text ? text : null;
}
