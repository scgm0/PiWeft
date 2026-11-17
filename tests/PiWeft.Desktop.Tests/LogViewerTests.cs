using Aprillz.MewUI.Controls;
using PiWeft.Cli;
using Xunit;
using static PiWeft.Desktop.Tests.VisualTree;

namespace PiWeft.Desktop.Tests;

/// 设置 → 日志：查看器按级别/关键词筛选，页码与计数跟着结果走。
[Collection("mewui-controls")]
public sealed class LogViewerTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-logview-" + Guid.NewGuid().ToString("N")[..8]);

	private string LogDirectory => Path.Combine(_root, "logs");

	public LogViewerTests()
	{
		Directory.CreateDirectory(LogDirectory);
		File.WriteAllLines(Path.Combine(LogDirectory, "piweft-desktop-1-20261117-000.log"),
		[
			"2026-11-17 05:00:00.000 [INF] PiWeft.Desktop.MainWindow | desktop ready",
			"2026-11-17 05:00:01.000 [WRN] PiWeft.Desktop.Diagnostics | Slow path: 320.0 ms",
			"2026-11-17 05:00:02.000 [ERR] PiWeft.Desktop.SessionTab | Open session: errorType=IOException",
		]);
	}

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	private LogViewer Create() => new(_root, _root);

	[Fact]
	public async Task ShowsNewestRecordsFirst()
	{
		var viewer = Create();
		ConversationPerformanceTests.Layout(viewer);
		await viewer.PendingReload;

		Assert.Equal(3, viewer.Lines.Count);
		Assert.Contains("Open session", viewer.Lines[0], StringComparison.Ordinal);
		Assert.Contains("desktop ready", viewer.Lines[2], StringComparison.Ordinal);
		Assert.Contains(Texts(viewer), text => text.Contains("3 条匹配", StringComparison.Ordinal));
	}

	[Fact]
	public async Task LevelFilterApplies()
	{
		var viewer = Create();
		ConversationPerformanceTests.Layout(viewer);
		await viewer.PendingReload;

		// 0=全部 1=调试 2=信息 3=警告 4=错误
		var filter = Find<ComboBox>(viewer).Single();
		filter.SelectedIndex = 4;
		await viewer.PendingReload;

		var line = Assert.Single(viewer.Lines);
		Assert.Contains("ERR", line, StringComparison.Ordinal);
	}

	[Fact]
	public async Task SettingsSectionHostsTheViewerAndLevelPicker()
	{
		var view = new SettingsView(() => new SettingsSnapshot
		{
			AgentDir = _root,
			Cwd = _root,
			ModelCount = 0,
			Tools = [],
			Skills = [],
			McpServers = [],
			Resources = PiWeft.CodingAgent.Resources.ResourceSnapshot.Empty,
		});
		await view.SelectSectionAsync("logs");
		ConversationPerformanceTests.Layout(view.View);

		Assert.Null(view.LastSectionError);
		Assert.Single(Find<LogViewer>(view.View));
		Assert.Contains(Texts(view.View), text => text.Contains(Strings.LogLevel, StringComparison.Ordinal));
	}

	[Fact]
	public async Task MissingLogDirectoryShowsNothing()
	{
		Directory.Delete(LogDirectory, recursive: true);
		var viewer = Create();
		ConversationPerformanceTests.Layout(viewer);
		await viewer.PendingReload;

		Assert.Empty(viewer.Lines);
	}

	private static IEnumerable<string> Texts(Element root) =>
		Find<TextBlock>(root).Select(static block => block.Text ?? string.Empty);
}
