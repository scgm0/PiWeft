using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
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
