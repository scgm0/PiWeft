using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Aprillz.MewUI.Rendering;
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

	[Fact]
	public void FrameWindowSummaryIsWrittenEverySecond()
	{
		Diagnostics.SetEnabled(true, _agentDir);
		Diagnostics.Reset();

		var stats = new RenderStats(412, 96);
		Diagnostics.ObserveFrame(stats);
		Diagnostics.Count("row.build", 3);
		Thread.Sleep(30);
		Diagnostics.ObserveFrame(stats);
		// 采样器每秒一次：等它至少跑过一轮再关文件。
		Thread.Sleep(1400);

		var path = Diagnostics.TakeSnapshot().TimingsPath!;
		Diagnostics.Close();
		var line = Assert.Single(File.ReadAllLines(path), static text => text.Contains("runtime.window", StringComparison.Ordinal));
		using var json = JsonDocument.Parse(line);
		Assert.Equal("runtime.window", json.RootElement.GetProperty("name").GetString());
		var detail = json.RootElement.GetProperty("detail").GetString()!;
		Assert.Contains("fps=", detail, StringComparison.Ordinal);
		Assert.Contains("p95=", detail, StringComparison.Ordinal);
		Assert.Contains("counters=[row.build:3]", detail, StringComparison.Ordinal);
		// 空闲也必须有这一行：判断堆是否一直长，靠的就是没有重绘的时间段。
		Assert.Contains("heap=", detail, StringComparison.Ordinal);
		Assert.Contains("loh=", detail, StringComparison.Ordinal);
		Assert.Contains("rss=", detail, StringComparison.Ordinal);
	}

	[Fact]
	public void ResourceSampleFieldsArePopulatedByTheSampler()
	{
		Diagnostics.SetEnabled(true, _agentDir);
		Diagnostics.Reset();

		// 采样器每秒一次：等到第一次采样落地再读，字段必须齐全且合理。
		var deadline = Stopwatch.StartNew();
		Diagnostics.Snapshot snapshot;
		do
		{
			Thread.Sleep(120);
			snapshot = Diagnostics.TakeSnapshot();
		}
		while (deadline.Elapsed < TimeSpan.FromSeconds(6) && snapshot.CommittedBytes <= 0);

		Assert.True(snapshot.CommittedBytes > 0, "committed bytes must come from the GC snapshot");
		Assert.True(snapshot.PoolThreads >= 0);
		Assert.True(snapshot.CpuPercent >= 0 && snapshot.CpuPercent <= 100, $"cpu={snapshot.CpuPercent}");
		Assert.True(snapshot.QueueLagMs >= 0);
	}

	[Fact]
	public void FrameTierIsInertWhileSamplingIsOff()
	{
		Diagnostics.Close();
		Diagnostics.Reset();

		Diagnostics.ObserveFrame(new RenderStats(412, 96, Primitives()));
		Diagnostics.NoteInput();
		Diagnostics.Count("row.build");
		Diagnostics.Slow("ui.render", 90, 8, "rows=3");

		var snapshot = Diagnostics.TakeSnapshot();
		Assert.Equal(0, snapshot.Frame.Frames);
		Assert.Empty(snapshot.Counters);
		Assert.Empty(snapshot.Stats);
	}

	[Fact]
	public void FrameTierAggregatesWithoutPerFrameAllocation()
	{
		Diagnostics.SetEnabled(true, _agentDir);
		Diagnostics.Reset();
		Assert.True(Diagnostics.Enabled);

		var stats = new RenderStats(412, 96, Primitives());
		// 预热：桶的首次创建会分配，之后热路径必须一个字节都不产生。
		for (var index = 0; index < 64; index++)
		{
			Diagnostics.ObserveFrame(stats);
			Diagnostics.Count("row.build");
		}

		var before = GC.GetAllocatedBytesForCurrentThread();
		for (var index = 0; index < 20_000; index++)
		{
			Diagnostics.ObserveFrame(stats);
			Diagnostics.Count("row.build");
			Diagnostics.Count("row.release", 2);
		}
		var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
		Assert.True(allocated < 4096, $"the frame tier allocated {allocated} bytes for 20k frames");

		var frame = Diagnostics.TakeSnapshot().Frame;
		// 第一次观测没有前帧可比，因此不产生样本。
		Assert.InRange(frame.Frames, 20_000, 20_064);
		Assert.Equal(412, frame.DrawCalls);
		Assert.Equal(96, frame.CulledCalls);
		Assert.Equal(40, frame.TextDraws);
		Assert.True(frame.AvgMs >= 0);
	}

	[Fact]
	public void DroppedAndLongFramesComeFromRealFrameGaps()
	{
		Diagnostics.SetEnabled(true, _agentDir);
		Diagnostics.Reset();

		Diagnostics.ObserveFrame(new RenderStats(10, 1));
		Thread.Sleep(25);
		Diagnostics.ObserveFrame(new RenderStats(10, 1));
		Thread.Sleep(60);
		Diagnostics.ObserveFrame(new RenderStats(10, 1));

		var frame = Diagnostics.TakeSnapshot().Frame;
		Assert.True(frame.DroppedFrames >= 1, $"dropped={frame.DroppedFrames}");
		Assert.True(frame.LongFrames >= 1, $"long={frame.LongFrames}");
		Assert.True(frame.MaxMs >= 20, $"max={frame.MaxMs}");
	}

	[Fact]
	public void TheWheelSamplingPointFeedsTheFrameTier()
	{
		// 滚轮在文本上会被阅读守卫接管、不再冒泡到窗口根，所以取样点必须挂在那里；
		// 这条测试钉住「守卫 → 帧档」的连线，而不是只测帧档本身。
		Diagnostics.SetEnabled(true, _agentDir);
		Diagnostics.Reset();

		var scroll = new ScrollViewer();
		using var guard = new TextFocusScrollGuard(scroll);
		var text = new Aprillz.MewUI.Controls.MultiLineTextBox(new Aprillz.MewUI.Text.Editing.EditableTextDocument());
		guard.Track(text);
		var stats = new RenderStats(8, 2);
		Diagnostics.ObserveFrame(stats);

		guard.RouteWheel(text, new MouseWheelEventArgs(new Point(10, 10), new Point(10, 10), new Vector(0, 3)));
		Thread.Sleep(20);
		Diagnostics.ObserveFrame(stats);

		var frame = Diagnostics.TakeSnapshot().Frame;
		Assert.Equal(1, frame.Inputs);
		Assert.True(frame.InputP95Ms > 0, $"p95={frame.InputP95Ms:0.0}");
	}

	[Fact]
	public void ReadingGuardsFeedTheInputSamplingPoint()
	{
		// 滚轮在文本上被阅读守卫接管，窗口根收不到事件：取样点必须挂在守卫里，
		// 否则「输入→绘制」会永远没有滚轮样本。
		Diagnostics.SetEnabled(true, _agentDir);
		Diagnostics.Reset();
		using var guard = new TextFocusScrollGuard(new ScrollViewer());
		var text = new Border();
		guard.Track(text);
		var stats = new RenderStats(8, 2);
		Diagnostics.ObserveFrame(stats);

		guard.RouteWheel(text, new MouseWheelEventArgs(new Point(10, 10), new Point(10, 10), new Vector(0, 3)));
		Thread.Sleep(20);
		Diagnostics.ObserveFrame(stats);

		Assert.Equal(1, Diagnostics.TakeSnapshot().Frame.Inputs);
	}

	[Fact]
	public void IdleGapsAreReportedSeparatelyFromFrameSamples()
	{
		// 渲染循环是按请求跑的：空闲后的第一次重绘不是「慢帧」，而是「刚才没画」。
		// 把它混进帧样本会让 FPS、p95 与 GC 占比全部失真，所以必须单独归类成停顿。
		Diagnostics.SetEnabled(true, _agentDir);
		Diagnostics.Reset();
		var stats = new RenderStats(8, 2);

		Diagnostics.ObserveFrame(stats);
		Thread.Sleep(60);
		Diagnostics.ObserveFrame(stats);
		Thread.Sleep(450);
		Diagnostics.ObserveFrame(stats);

		var frame = Diagnostics.TakeSnapshot().Frame;
		Assert.Equal(1, frame.Frames);
		Assert.Equal(1, frame.LongFrames);
		Assert.Equal(1, frame.IdleGaps);
		Assert.True(frame.MaxGapMs >= 400, $"max gap={frame.MaxGapMs:0.0}");
		Assert.True(frame.MaxMs < 400, $"the idle gap must not become a frame sample: max={frame.MaxMs:0.0}");
	}

	[Fact]
	public void InputLatencyIsMeasuredAgainstTheNextFrame()
	{
		Diagnostics.SetEnabled(true, _agentDir);
		Diagnostics.Reset();

		var stats = new RenderStats(10, 1);
		Diagnostics.ObserveFrame(stats);
		Diagnostics.NoteInput();
		Thread.Sleep(30);
		Diagnostics.ObserveFrame(stats);

		var frame = Diagnostics.TakeSnapshot().Frame;
		Assert.Equal(1, frame.Inputs);
		Assert.InRange(frame.InputMaxMs, 20, 2000);
	}

	[Fact]
	public void SlowPathCountsEveryCallAndRecordsOnlyPastTheThreshold()
	{
		Diagnostics.SetEnabled(true, _agentDir);
		Diagnostics.Reset();

		Diagnostics.Slow("ui.render", 3, 8, "rows=1");
		Diagnostics.Slow("ui.render", 40, 8, "rows=2");

		var snapshot = Diagnostics.TakeSnapshot();
		var counter = snapshot.Counters.Single(item => item.Name == "ui.render");
		Assert.Equal(2, counter.Value);
		Assert.Equal(43, counter.TotalMs, 1); // 累计含每一次调用
		// 明细只留慢的那一次：快帧不产生字符串与 JSON。
		var stat = snapshot.Stats.Single(item => item.Name == "ui.render");
		Assert.Equal(1, stat.Count);
		Assert.Equal(40, stat.MaxMs, 1);
	}

	[Fact]
	public void ChurnCountersAccumulateAndReset()
	{
		Diagnostics.SetEnabled(true, _agentDir);
		Diagnostics.Reset();

		Diagnostics.Count("row.build", 3);
		Diagnostics.Count("row.build");
		Diagnostics.Count("row.inplace", 7);

		var snapshot = Diagnostics.TakeSnapshot();
		Assert.Equal(4, snapshot.Counters.Single(item => item.Name == "row.build").Value);
		Assert.Equal(7, snapshot.Counters.Single(item => item.Name == "row.inplace").Value);

		Diagnostics.Reset();
		Assert.Empty(Diagnostics.TakeSnapshot().Counters);
	}

	private static RenderPrimitiveStats Primitives() => new(
		saveCount: 12, restoreCount: 12, clipCount: 5,
		drawLineCount: 1, drawRectangleCount: 2, fillRectangleCount: 3,
		drawRoundedRectangleCount: 4, fillRoundedRectangleCount: 5,
		drawEllipseCount: 0, fillEllipseCount: 0,
		drawPathCount: 1, fillPathCount: 1,
		drawTextCount: 40, drawImageCount: 2);
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
		// 名称与数值分在两枚文本块里（表格对齐），必须分开断言。
		Assert.Contains("session.open.read", texts);
		Assert.Contains(texts, static text => text.Contains("×1") && text.Contains("884.3"));
		Assert.Contains(texts, static text => text.Contains("timings-", StringComparison.Ordinal));
	}

	[Fact]
	public async Task OnStateShowsTheFrameTierAndCounters()
	{
		File.WriteAllText(Path.Combine(_root, "agent", "desktop.json"), """{"theme":"dark","diagnostics":true}""");
		Diagnostics.SetEnabled(true, Path.Combine(_root, "agent"));
		Diagnostics.Reset();
		var stats = new RenderStats(412, 96, new RenderPrimitiveStats(
			1, 1, 2, 0, 1, 1, 2, 3, 0, 0, 0, 1, 17, 0));
		Diagnostics.ObserveFrame(stats);
		Thread.Sleep(25);
		Diagnostics.ObserveFrame(stats);
		Diagnostics.NoteInput();
		Thread.Sleep(25);
		Diagnostics.ObserveFrame(stats);
		Diagnostics.Count("row.build", 9);

		var view = Create();
		await view.SelectSectionAsync("diagnostics");

		var texts = Texts(view);
		Assert.Null(view.LastSectionError);
		Assert.Contains(Strings.DiagnosticsFrameHeader, texts);
		Assert.Contains(Strings.DiagnosticsLoopHint, texts);
		// 名称与数值分列：帧间隔一行给 p50 / p95 与 FPS，绘制量单独一行。
		Assert.Contains(texts, static text => text.Contains("p50 / p95", StringComparison.Ordinal));
		Assert.Contains(Strings.LabelFrameCounts, texts);
		Assert.Contains(texts, static text => text.Contains("412 调用", StringComparison.Ordinal));
		Assert.Contains(Strings.LabelInputLatency, texts);
		Assert.Contains(Strings.DiagnosticsCountersHeader, texts);
		Assert.Contains("row.build", texts);
		Assert.Contains(texts, static text => text.Contains("×9"));
		Assert.Contains(Strings.LabelCpu, texts);
		Assert.Contains(Strings.DiagnosticsCompact, texts);
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
