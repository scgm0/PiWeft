using System.Text.Json;
using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Xunit;
using static PiWeft.Desktop.Tests.VisualTree;

namespace PiWeft.Desktop.Tests;

/// <summary>
/// 性能诊断采样的契约：默认关闭且零副作用，开启后按名称聚合、逐条落盘、
/// 重复设置同值不重开文件，关掉之后必须把缓冲里的记录写完。
/// </summary>
[Collection("mewui-controls")]
public sealed class DiagnosticsTests : IDisposable
{
	private readonly string _agentDir = Path.Combine(Path.GetTempPath(), "piweft-diagnostics-" + Guid.NewGuid().ToString("N")[..8]);

	public DiagnosticsTests() => Directory.CreateDirectory(_agentDir);

	public void Dispose()
	{
		// 采样器是进程级单例：测试结束必须关掉，否则别的用例会读到开着的状态。
		Diagnostics.Close();
		if (Directory.Exists(_agentDir))
		{
			Directory.Delete(_agentDir, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	[Fact]
	public void DisabledByDefaultAndRecordingIsANoOp()
	{
		Diagnostics.Close();
		Diagnostics.Reset();
		Assert.False(Diagnostics.Enabled);

		Diagnostics.Record("noop.path", 12.3, "a=1");

		var snapshot = Diagnostics.TakeSnapshot();
		Assert.False(snapshot.Enabled);
		Assert.Null(snapshot.TimingsPath);
		Assert.Empty(snapshot.Stats);
		Assert.Empty(snapshot.Recent);
		Assert.False(Directory.Exists(Diagnostics.DirectoryPath(_agentDir)));
	}

	[Fact]
	public void EnablingStartsAcleanAggregate()
	{
		Diagnostics.SetEnabled(true, _agentDir);
		Diagnostics.Record("old.path", 500);
		Diagnostics.Close();

		// 重新开启对应新的 timings 文件，聚合必须跟着清零。
		Diagnostics.SetEnabled(true, _agentDir);
		var snapshot = Diagnostics.TakeSnapshot();
		Assert.DoesNotContain("old.path", snapshot.Stats.Select(static stat => stat.Name));
	}

	[Fact]
	public void EnabledSamplingAggregatesAndWritesJsonl()
	{
		Diagnostics.SetEnabled(true, _agentDir);
		Assert.True(Diagnostics.Enabled);

		Diagnostics.Record("session.open.read", 884.3, "bytes=47451970, lines=26985");
		Diagnostics.Record("session.open.read", 120.5, "bytes=1024");
		Diagnostics.Record("ui.render", 8.4);

		var live = Diagnostics.TakeSnapshot();
		Assert.True(live.Enabled);
		Assert.NotNull(live.TimingsPath);
		var read = live.Stats.Single(stat => stat.Name == "session.open.read");
		Assert.Equal(2, read.Count);
		Assert.Equal(1004.8, read.TotalMs, 1);
		Assert.Equal(884.3, read.MaxMs, 1);
		Assert.Equal(120.5, read.LastMs, 1);
		// 最近记录新→旧。
		Assert.Equal("ui.render", live.Recent[0].Name);

		// 关掉才保证落盘完成（运行中是 250 ms 一批）。
		var path = live.TimingsPath!;
		Diagnostics.Close();
		var lines = File.ReadAllLines(path);
		Assert.Equal(3, lines.Length);
		using var first = JsonDocument.Parse(lines[0]);
		Assert.Equal("session.open.read", first.RootElement.GetProperty("name").GetString());
		Assert.Equal(884.3, first.RootElement.GetProperty("durationMs").GetDouble());
		Assert.Equal("bytes=47451970, lines=26985", first.RootElement.GetProperty("detail").GetString());
		Assert.True(first.RootElement.TryGetProperty("rssBytes", out _), "每行都带上当时的内存，才能把慢和涨对上");
	}

	[Fact]
	public void SettingTheSameValueTwiceKeepsTheSameFile()
	{
		Diagnostics.SetEnabled(true, _agentDir);
		var path = Diagnostics.TakeSnapshot().TimingsPath;

		Diagnostics.SetEnabled(true, _agentDir);

		Assert.Equal(path, Diagnostics.TakeSnapshot().TimingsPath);
	}

	[Fact]
	public void TrackRecordsOnlyWhenEnabled()
	{
		Diagnostics.Record("off.baseline", 1);
		Diagnostics.Close();

		using (Diagnostics.Track("scoped.path"))
		{
		}

		Assert.DoesNotContain("scoped.path", Diagnostics.TakeSnapshot().Stats.Select(static stat => stat.Name));

		Diagnostics.SetEnabled(true, _agentDir);
		using (Diagnostics.Track("scoped.path"))
		{
		}

		Assert.Contains("scoped.path", Diagnostics.TakeSnapshot().Stats.Select(static stat => stat.Name));
	}

	[Fact]
	public void ResetClearsCounters()
	{
		Diagnostics.SetEnabled(true, _agentDir);
		Diagnostics.Record("sidebar.refresh", 30);
		Assert.NotEmpty(Diagnostics.TakeSnapshot().Stats);

		Diagnostics.Reset();

		var snapshot = Diagnostics.TakeSnapshot();
		Assert.Empty(snapshot.Stats);
		Assert.Empty(snapshot.Recent);
	}

	[Fact]
	public void ConcurrentRecordsAreAllCounted()
	{
		Diagnostics.SetEnabled(true, _agentDir);

		Parallel.For(0, 200, index => Diagnostics.Record("stress.path", index % 7, $"i={index}"));

		var stat = Diagnostics.TakeSnapshot().Stats.Single(item => item.Name == "stress.path");
		Assert.Equal(200, stat.Count);
		Assert.Equal(6, stat.MaxMs, 1);
	}

	[Fact]
	public void SnapshotCarriesMemorySampleFields()
	{
		Diagnostics.SetEnabled(true, _agentDir);
		// 采样每秒一次；快照在第一次采样前内存字段为 0，但字段必须齐备且不为负。
		var snapshot = Diagnostics.TakeSnapshot();
		Assert.True(snapshot.WorkingSetBytes >= 0);
		Assert.True(snapshot.QueueLagMaxMs >= 0);
		Assert.Equal(Diagnostics.DirectoryPath(_agentDir), snapshot.DirectoryPath);
	}
}

/// <summary>设置页的诊断分区：关闭时给提示，打开时给聚合数据；构建过程不许吞异常。</summary>
[Collection("mewui-controls")]
public sealed class SettingsDiagnosticsSectionTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-settings-diagnostics-" + Guid.NewGuid().ToString("N")[..8]);

	public SettingsDiagnosticsSectionTests()
	{
		Directory.CreateDirectory(Path.Combine(_root, "agent"));
	}

	public void Dispose()
	{
		Diagnostics.Close();
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	[Fact]
	public async Task OffStateExplainsThatSamplingIsDisabled()
	{
		File.WriteAllText(Path.Combine(_root, "agent", "desktop.json"), """{"theme":"dark"}""");
		var view = Create();

		await view.SelectSectionAsync("diagnostics");

		Assert.Null(view.LastSectionError);
		Assert.Contains(Strings.DiagnosticsOff, Texts(view));
		Assert.DoesNotContain(Strings.DiagnosticsStatsHeader, Texts(view));
	}

	[Fact]
	public async Task OnStateShowsAggregatesAndTheTimingsFile()
	{
		File.WriteAllText(Path.Combine(_root, "agent", "desktop.json"), """{"theme":"dark","diagnostics":true}""");
		Diagnostics.SetEnabled(true, Path.Combine(_root, "agent"));
		Diagnostics.Record("session.open.read", 884.3, "lines=26985");
		var view = Create();

		await view.SelectSectionAsync("diagnostics");

		var texts = Texts(view);
		Assert.Null(view.LastSectionError);
		Assert.Contains(Strings.DiagnosticsStatsHeader, texts);
		Assert.Contains(texts, static text => text.Contains("session.open.read", StringComparison.Ordinal) && text.Contains("884.3"));
		Assert.Contains(texts, static text => text.Contains("timings-", StringComparison.Ordinal));
	}

	private SettingsView Create() => new(
		() => new SettingsSnapshot
		{
			AgentDir = Path.Combine(_root, "agent"), Cwd = _root, ModelCount = 0,
			Tools = [], Skills = [], McpServers = [], Resources = PiWeft.CodingAgent.Resources.ResourceSnapshot.Empty,
		});

	private static IReadOnlyList<string> Texts(SettingsView view) =>
		Find<TextBlock>(view.View).Select(static block => block.Text ?? string.Empty).ToList();
}
