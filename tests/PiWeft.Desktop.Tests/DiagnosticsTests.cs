using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Aprillz.MewUI.Rendering;
using Xunit;
using static PiWeft.Desktop.Tests.VisualTree;

namespace PiWeft.Desktop.Tests;

/// 性能诊断采样的契约：默认关闭且零副作用；开启后按名称聚合、逐条落盘；
/// 重复设置同值不重开文件；关掉后要把缓冲里的记录写完。
[Collection("mewui-controls")]
public sealed class DiagnosticsTests : IDisposable
{
	private readonly string _agentDir = Path.Combine(Path.GetTempPath(), "piweft-diagnostics-" + Guid.NewGuid().ToString("N")[..8]);

	public DiagnosticsTests() => Directory.CreateDirectory(_agentDir);

	public void Dispose()
	{
		// 采样器是进程级单例：测试结束要关掉，否则别的用例会读到开着的状态。
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
		Assert.Contains(lines, static line => line.Contains("\"kind\":\"meta\"", StringComparison.Ordinal));
		var records = lines.Where(static line => !line.Contains("\"kind\":\"meta\"", StringComparison.Ordinal)).ToArray();
		Assert.Equal(3, records.Length);
		using var first = JsonDocument.Parse(records[0]);
		Assert.Equal("session.open.read", first.RootElement.GetProperty("name").GetString());
		Assert.Equal(884.3, first.RootElement.GetProperty("durationMs").GetDouble());
		Assert.Equal("bytes=47451970, lines=26985", first.RootElement.GetProperty("detail").GetString());
		Assert.True(first.RootElement.GetProperty("v").GetInt32() >= 1, "文件要带 schema 版本");
		Assert.Equal("span", first.RootElement.GetProperty("kind").GetString());
		Assert.Equal("session", first.RootElement.GetProperty("cat").GetString());
		Assert.True(first.RootElement.TryGetProperty("tid", out _), "每条记录要能对上线程");
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
		Assert.Contains("window.fps=", detail, StringComparison.Ordinal);
		Assert.Contains("total.fps=", detail, StringComparison.Ordinal);
		Assert.Contains("p95=", detail, StringComparison.Ordinal);
		Assert.Contains("counters=[row.build:3]", detail, StringComparison.Ordinal);
		// 空闲也必须有这一行：判断堆是否一直长，靠的就是没有重绘的时间段。
		Assert.Contains("heap=", detail, StringComparison.Ordinal);
		Assert.Contains("loh=", detail, StringComparison.Ordinal);
		Assert.Contains("poh=", detail, StringComparison.Ordinal);
		Assert.Contains("rss=", detail, StringComparison.Ordinal);
		Assert.Contains("total.p95=", detail, StringComparison.Ordinal);
		Assert.Contains("budget=", detail, StringComparison.Ordinal);
		Assert.Contains("dropped-lines=", detail, StringComparison.Ordinal);
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
		// 预热：桶的首次创建会分配，之后热路径不产生字节。
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
		// 每次请求之后才发生「帧工作」，请求之前的等待不计入成本。
		Diagnostics.NoteFrameRequest();
		Thread.Sleep(25);
		Diagnostics.ObserveFrame(new RenderStats(10, 1));
		Diagnostics.NoteFrameRequest();
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
		// 滚轮在文本上会被阅读守卫接管，不再冒泡到窗口根，所以取样点挂在那里；
		// 这条测试钉住「守卫 → 帧档」的连线，不只测帧档本身。
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
		Diagnostics.NoteFrameRequest();
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


	[Fact]
	public void CpuPercentUsesTimeSpanTicks()
	{
		// Process.TotalProcessorTime 是 100 ns 的 TimeSpan tick；用 Stopwatch.Frequency 换算会差 100 倍。
		Assert.Equal(100, Diagnostics.CpuPercentFromTicks(TimeSpan.TicksPerSecond, Stopwatch.Frequency, 1), 1);
		Assert.Equal(50, Diagnostics.CpuPercentFromTicks(TimeSpan.TicksPerSecond, Stopwatch.Frequency, 2), 1);
	}

	[Fact]
	public void FrameRequestSeparatesIdleFromFrameCost()
	{
		Diagnostics.SetEnabled(true, _agentDir);
		Diagnostics.Reset();
		var stats = new RenderStats(10, 1);

		Diagnostics.NoteFrameRequest();
		Thread.Sleep(20);
		Diagnostics.ObserveFrame(stats);

		// 300 ms 的空闲在旧口径下会变成「长帧」；有了请求时间戳，它只是空闲。
		Thread.Sleep(300);
		Diagnostics.NoteFrameRequest();
		Thread.Sleep(10);
		Diagnostics.ObserveFrame(stats);

		var frame = Diagnostics.TakeSnapshot().Frame;
		Assert.Equal(2, frame.Frames);
		Assert.Equal(0, frame.IdleGaps);
		Assert.Equal(0, frame.LongFrames);
		Assert.True(frame.MaxMs < 100, $"idle must not become frame cost: max={frame.MaxMs:0.0}");
	}

	[Fact]
	public void ExportSplitsWindowMetricsIntoTraceCounters()
	{
		Diagnostics.SetEnabled(true, _agentDir);
		Diagnostics.Reset();
		// 采样器每秒写一行 runtime.window；等它落盘后再导出。
		var deadline = Stopwatch.StartNew();
		while (deadline.Elapsed < TimeSpan.FromSeconds(4) &&
			!File.Exists(Diagnostics.TakeSnapshot().TimingsPath) )
		{
			Thread.Sleep(100);
		}

		Thread.Sleep(1400);
		var path = Diagnostics.ExportChromeTrace();
		Assert.NotNull(path);
		using var document = JsonDocument.Parse(File.ReadAllText(path!));
		var events = document.RootElement.GetProperty("traceEvents").EnumerateArray().ToArray();
		Assert.Contains(events, static item => item.TryGetProperty("name", out var name) &&
			name.GetString() == "thread_name");
		Assert.Contains(events, static item => item.TryGetProperty("ph", out var phase) &&
			phase.GetString() == "C" &&
			item.TryGetProperty("name", out var name) &&
			name.GetString() == "frame.p95ms");
		Assert.DoesNotContain(events, static item => item.TryGetProperty("name", out var name) &&
			name.GetString() == "runtime.window");
	}

	[Fact]
	public void ExportWritesFirefoxProfileWithMarkersAndCounters()
	{
		Diagnostics.SetEnabled(true, _agentDir);
		Diagnostics.Reset();
		Diagnostics.Record("session.open.read", 884.3, "bytes=47451970, lines=26985");
		Diagnostics.Record("tool.exec", 12.5, "tool=read");
		Thread.Sleep(1400);

		var path = Diagnostics.ExportFirefoxProfile();
		Assert.NotNull(path);
		// 手工验证钩子：需要把产物交给真实的 Firefox Profiler 导入器时，指一个输出路径即可。
		if (Environment.GetEnvironmentVariable("PIWEFT_FF_PROFILE_OUT") is { Length: > 0 } copyTo)
		{
			File.Copy(path!, copyTo, overwrite: true);
		}

		using var document = JsonDocument.Parse(File.ReadAllText(path!));
		var root = document.RootElement;
		Assert.Equal(37, root.GetProperty("meta").GetProperty("version").GetInt32());
		Assert.Equal("PiWeft", root.GetProperty("meta").GetProperty("product").GetString());
		Assert.True(root.GetProperty("meta").GetProperty("categories").GetArrayLength() >= 10);

		var threads = root.GetProperty("threads").EnumerateArray().ToArray();
		Assert.NotEmpty(threads);
		var main = threads.Single(thread => thread.GetProperty("name").GetString() == "GeckoMain");
		Assert.Equal("PiWeft", main.GetProperty("processName").GetString());
		var markers = main.GetProperty("markers");
		Assert.Equal(0, markers.GetProperty("schema").GetProperty("name").GetInt32());
		Assert.Equal(5, markers.GetProperty("schema").GetProperty("data").GetInt32());
		var rows = markers.GetProperty("data").EnumerateArray().ToArray();
		Assert.Contains(rows, static row => row[3].GetInt32() == 1); // INTERVAL
		// 纯 marker 档案：samples/frameTable/stackTable 是合法空表。
		Assert.Equal(0, main.GetProperty("samples").GetProperty("data").GetArrayLength());
		Assert.Equal(0, main.GetProperty("frameTable").GetProperty("data").GetArrayLength());
		Assert.Equal(0, main.GetProperty("stackTable").GetProperty("data").GetArrayLength());

		var counters = root.GetProperty("counters").EnumerateArray().ToArray();
		Assert.Contains(counters, static counter => counter.GetProperty("name").GetString() == "frame.p95ms");
		var counter = counters.First();
		Assert.Equal(0, counter.GetProperty("samples").GetProperty("schema").GetProperty("time").GetInt32());
		Assert.Equal(2, counter.GetProperty("samples").GetProperty("schema").GetProperty("number").GetInt32());
		Assert.NotEmpty(counter.GetProperty("samples").GetProperty("data").EnumerateArray());
	}

	[Fact]
	public void FrameBudgetSnapsToCommonRefreshRates()
	{
		// 启动/动画噪声会把中位间隔抬到 24.6 ms：吸附到 16.7 ms，不发明 40 fps 的预算。
		Assert.Equal(1000.0 / 60, Diagnostics.SnapFrameBudget(16.4), 1);
		Assert.Equal(1000.0 / 60, Diagnostics.SnapFrameBudget(24.6), 1);
		Assert.Equal(1000.0 / 120, Diagnostics.SnapFrameBudget(8.5), 1);
		Assert.Equal(1000.0 / 30, Diagnostics.SnapFrameBudget(33.0), 1);
		// 偏离所有常见刷新率太远：回退 60 fps。
		Assert.Equal(1000.0 / 60, Diagnostics.SnapFrameBudget(5.0), 1);
	}

	[Fact]
	public void LongPauseWithoutRequestIsAStallAndNotAFrame()
	{
		Diagnostics.SetEnabled(true, _agentDir);
		Diagnostics.Reset();
		var stats = new RenderStats(10, 1);

		Diagnostics.NoteFrameRequest();
		Thread.Sleep(10);
		Diagnostics.ObserveFrame(stats);
		Thread.Sleep(500);
		Diagnostics.ObserveFrame(stats);

		var frame = Diagnostics.TakeSnapshot().Frame;
		Assert.Equal(1, frame.Frames);
		Assert.Equal(1, frame.IdleGaps);
		Assert.True(frame.MaxGapMs >= 400, $"max gap={frame.MaxGapMs:0.0}");
	}

	[Fact]
	public void LazySlowDetailDoesNotAllocateBelowThreshold()
	{
		Diagnostics.SetEnabled(true, _agentDir);
		Diagnostics.Reset();
		// 预热计数器桶；之后每次调用都必须零分配。
		Diagnostics.Slow("lazy.path", 1, 8, static value => $"v={value}", 0);

		var before = GC.GetAllocatedBytesForCurrentThread();
		for (var index = 0; index < 1000; index++)
		{
			Diagnostics.Slow("lazy.path", 1, 8, static value => $"v={value}", index);
		}

		var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
		Assert.True(allocated < 4096, $"lazy Slow allocated {allocated} bytes");
		var counter = Diagnostics.TakeSnapshot().Counters.Single(item => item.Name == "lazy.path");
		Assert.Equal(1001, counter.Value);
	}

	[Fact]
	public void DiagnosticsStartFailureIsNonFatal()
	{
		var blocker = Path.Combine(_agentDir, "not-a-directory");
		File.WriteAllText(blocker, "x");

		Diagnostics.SetEnabled(true, blocker);

		Assert.False(Diagnostics.Enabled);
		Assert.False(string.IsNullOrWhiteSpace(Diagnostics.LastError));
		Diagnostics.Close();
	}

	[Fact]
	public void ExportWritesChromeTrace()
	{
		Diagnostics.SetEnabled(true, _agentDir);
		Diagnostics.Record("export.span", 12.5, "rows=3");
		Diagnostics.Close();

		var path = Diagnostics.ExportChromeTrace();
		Assert.NotNull(path);
		Assert.True(File.Exists(path));
		using var document = JsonDocument.Parse(File.ReadAllText(path!));
		var events = document.RootElement.GetProperty("traceEvents").EnumerateArray().ToArray();
		var span = events.Single(item => item.TryGetProperty("name", out var name) &&
			name.GetString() == "export.span");
		Assert.Equal("X", span.GetProperty("ph").GetString());
		Assert.Equal(12500, span.GetProperty("dur").GetInt64());
		Assert.Equal(3, span.GetProperty("args").GetProperty("rows").GetInt32());
	}


	[Fact]
	public void FileLimitRotatesInsteadOfGrowingForever()
	{
		var oldLimit = Diagnostics.FileLimitBytes;
		Diagnostics.FileLimitBytes = 2048;
		try
		{
			Diagnostics.SetEnabled(true, _agentDir);
			for (var index = 0; index < 80; index++)
			{
				Diagnostics.Record("rotate.span", index, new string('x', 160));
			}

			Diagnostics.Close();
			var files = Directory.GetFiles(Diagnostics.DirectoryPath(_agentDir), "timings-*.jsonl");
			Assert.True(files.Length > 1, $"expected rotation, got {files.Length} file(s)");
		}
		finally
		{
			Diagnostics.FileLimitBytes = oldLimit;
			Diagnostics.Close();
		}
	}

	private static RenderPrimitiveStats Primitives() => new(
		saveCount: 12, restoreCount: 12, clipCount: 5,
		drawLineCount: 1, drawRectangleCount: 2, fillRectangleCount: 3,
		drawRoundedRectangleCount: 4, fillRoundedRectangleCount: 5,
		drawEllipseCount: 0, fillEllipseCount: 0,
		drawPathCount: 1, fillPathCount: 1,
		drawTextCount: 40, drawImageCount: 2);
}


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
		Assert.Contains(Strings.DiagnosticsExportTrace, texts);
		Assert.Contains(Strings.DiagnosticsExportFirefox, texts);
	}

	[Fact]
	public async Task LiveViewUpdatesInPlaceAndPauseFreezesIt()
	{
		File.WriteAllText(Path.Combine(_root, "agent", "desktop.json"), """{"theme":"dark","diagnostics":true}""");
		Diagnostics.SetEnabled(true, Path.Combine(_root, "agent"));
		Diagnostics.Record("live.path", 12);
		var view = Create();
		await view.SelectSectionAsync("diagnostics");

		// 实时区的控件只建一次：脉冲刷新不能把整棵树换掉。
		var before = Find<TextBlock>(view.View).ToArray();
		Diagnostics.Record("live.path", 34);
		view.RefreshDiagnosticsLive();
		var after = Find<TextBlock>(view.View).ToArray();
		Assert.All(before, block => Assert.Contains(block, after));
		Assert.Contains(after, block => (block.Text ?? string.Empty).Contains("×2", StringComparison.Ordinal));

		// 暂停后脉冲不再改数值，但采样仍在继续（计数照常累加）。
		var pause = Find<Button>(view.View).Single(button => ButtonText(button).Contains(Strings.DiagnosticsPause, StringComparison.Ordinal));
		Click(pause);
		// 按钮切换本身会重建分区；从暂停后的这一份开始比，才能看出脉冲是否真的冻结了数值。
		var frozen = Find<TextBlock>(view.View).Select(static block => block.Text).ToArray();
		Diagnostics.Record("live.path", 56);
		view.RefreshDiagnosticsLive();
		Assert.Equal(frozen, Find<TextBlock>(view.View).Select(static block => block.Text).ToArray());
		Assert.Contains(Strings.DiagnosticsPaused, Texts(view));
		Assert.Equal(3, Diagnostics.TakeSnapshot().Stats.Single(static stat => stat.Name == "live.path").Count);

		var resume = Find<Button>(view.View).Single(button => ButtonText(button).Contains(Strings.DiagnosticsResume, StringComparison.Ordinal));
		Click(resume);
		Assert.Contains(Strings.DiagnosticsSampling, Texts(view));
	}

	[Fact]
	public async Task ExportButtonWritesATraceNextToTheTimings()
	{
		File.WriteAllText(Path.Combine(_root, "agent", "desktop.json"), """{"theme":"dark","diagnostics":true}""");
		Diagnostics.SetEnabled(true, Path.Combine(_root, "agent"));
		Diagnostics.Record("export.ui", 4.2);
		var view = Create();
		await view.SelectSectionAsync("diagnostics");

		var button = Find<Button>(view.View).Single(button => ButtonText(button).Contains(Strings.DiagnosticsExportTrace, StringComparison.Ordinal));
		Click(button);

		var deadline = Stopwatch.StartNew();
		while (deadline.Elapsed < TimeSpan.FromSeconds(5) &&
			!Directory.GetFiles(Diagnostics.DirectoryPath(Path.Combine(_root, "agent")), "trace-*.json").Any())
		{
			await Task.Delay(50);
		}

		var trace = Directory.GetFiles(Diagnostics.DirectoryPath(Path.Combine(_root, "agent")), "trace-*.json").Single();
		using var document = JsonDocument.Parse(File.ReadAllText(trace));
		Assert.Contains(document.RootElement.GetProperty("traceEvents").EnumerateArray(),
			item => item.TryGetProperty("name", out var name) && name.GetString() == "export.ui");
	}

	// Button.OnClick is protected: the other UI tests invoke it the same way.
	private static void Click(Button button) =>
		typeof(Button).GetMethod("OnClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(button, null);

	private static string ButtonText(Button button) =>
		button.Content is Element content
			? string.Join(" ", Find<TextBlock>(content).Select(static block => block.Text ?? string.Empty))
			: string.Empty;

	private SettingsView Create() => new(
		() => new SettingsSnapshot
		{
			AgentDir = Path.Combine(_root, "agent"), Cwd = _root, ModelCount = 0,
			Tools = [], Skills = [], McpServers = [], Resources = PiWeft.CodingAgent.Resources.ResourceSnapshot.Empty,
		});

	private static IReadOnlyList<string> Texts(SettingsView view) =>
		Find<TextBlock>(view.View).Select(static block => block.Text ?? string.Empty).ToList();
}
