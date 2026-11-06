using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

namespace PiWeft.Desktop;

/// 诊断实时区：控件只建一次，每秒只改文本与帧条。
/// 旧实现每秒 Clear + 重建几十个控件，面板自己就会制造布局与垃圾。
internal sealed class DiagnosticsLiveView : StackPanel
{
	private readonly MetricTileView _frameCost;
	private readonly MetricTileView _frameCounts;
	private readonly MetricTileView _workingSet;
	private readonly MetricTileView _heap;
	private readonly FrameTimeStrip _strip = new();
	private readonly TextBlock _frameDetail;
	private readonly SectionView _loop;
	private readonly SectionView _memory;
	private readonly SectionView _counters;
	private readonly SectionView _stats;
	private readonly SectionView _recent;

	public DiagnosticsLiveView()
	{
		Spacing = AppTheme.Space3;
		_frameCost = new MetricTileView(Strings.LabelFrameGap, 0, 0);
		_frameCounts = new MetricTileView(Strings.LabelFrameCounts, 1, 0);
		_workingSet = new MetricTileView(Strings.LabelWorkingSet, 0, 1);
		_heap = new MetricTileView(Strings.LabelHeap, 1, 1);
		var tiles = new Grid().Columns("*,*").Rows("Auto,Auto")
			.Children(_frameCost.View, _frameCounts.View, _workingSet.View, _heap.View);

		_frameDetail = new TextBlock()
			.FontSize(AppTheme.FontSizeMicro).FontFamily(AppTheme.MonoFont).MutedText()
			.TextWrapping(TextWrapping.Wrap);
		var frameSection = new StackPanel().Spacing(2).Children(
			new TextBlock().Text(Strings.DiagnosticsFrameHeader)
				.FontSize(AppTheme.FontSizeMicro).SubtleText(),
			_strip,
			FrameLegend(),
			_frameDetail);

		_loop = new SectionView(Strings.DiagnosticsLoopHeader, 4);
		_memory = new SectionView(Strings.DiagnosticsMemoryHeader, 6);
		_counters = new SectionView(Strings.DiagnosticsCountersHeader, 12);
		_stats = new SectionView(Strings.DiagnosticsStatsHeader, 14);
		_recent = new SectionView(Strings.DiagnosticsRecentHeader, 10);

		AddRange(
			new StackPanel().Spacing(AppTheme.Space2).Children(
				new TextBlock().Text(Strings.DiagnosticsOverview)
					.FontSize(AppTheme.FontSizeMicro).SubtleText(),
				tiles),
			new TextBlock().Text(Strings.DiagnosticsLoopHint)
				.FontSize(AppTheme.FontSizeMicro).FaintText().TextWrapping(TextWrapping.Wrap),
			frameSection,
			_loop,
			_memory,
			_counters,
			_stats,
			_recent);
	}

	public void Update(Diagnostics.Snapshot fresh)
	{
		var frame = fresh.Frame;
		var window = fresh.FrameWindow;
		var budget = fresh.FrameBudgetMs > 0 ? fresh.FrameBudgetMs : 16.7;

		_frameCost.Set(
			window.Frames > 0
				? $"{window.P50Ms:0.0} / {window.P95Ms:0.0}ms"
				: frame.Frames > 0 ? $"{frame.P50Ms:0.0} / {frame.P95Ms:0.0}ms" : "—",
			window.Frames > 0
				? $"p50 / p95（最近一秒）· 累计 p95 {frame.P95Ms:0.0}ms · 预算 {budget:0.0}ms"
				: frame.Frames > 0
					? $"p50 / p95（累计）· 预算 {budget:0.0}ms"
					: "等待第一帧");
		_frameCounts.Set(
			$"{window.DroppedFrames} / {window.LongFrames} / {window.IdleGaps}",
			$"最近一秒掉帧/长帧/停顿 · 累计 {frame.DroppedFrames}/{frame.LongFrames}/{frame.IdleGaps} · 最长停顿 {frame.MaxGapMs:0}ms");
		_workingSet.Set(
			fresh.HasSample ? Strings.FormatBytes(fresh.WorkingSetBytes) : "—",
			fresh.HasSample
				? $"提交 {Strings.FormatBytes(fresh.CommittedBytes)} · {Strings.LabelCpu} {fresh.CpuPercent:0.#}%"
				: "等待第一次采样");
		_heap.Set(
			fresh.HasSample ? Strings.FormatBytes(fresh.HeapBytes) : "—",
			fresh.HasSample
				? $"{Strings.LabelLoh} {Strings.FormatBytes(fresh.LargeObjectHeapBytes)} · POH {Strings.FormatBytes(fresh.PinnedHeapBytes)} · {Strings.LabelFragmented} {Strings.FormatBytes(fresh.FragmentedBytes)}"
				: "等待第一次采样");

		_strip.SetSamples(frame.Samples ?? [], budget);
		var windowActive = window.Frames > 0;
		var allocPerFrame = windowActive ? window.AllocBytesPerFrame : frame.AllocBytesPerFrame;
		var gcPercent = windowActive ? window.GcPausePercent : frame.GcPausePercent;
		SetText(_frameDetail,
			$"最近 {(frame.LastMs > 0 ? $"{frame.LastMs:0.0}ms" : "—")}" +
			$" · 窗口 p95 {(windowActive ? $"{window.P95Ms:0.0}ms" : "—")} · 累计 p95 {frame.P95Ms:0.0}ms" +
			$" · {Strings.LabelAllocPerFrame} {Strings.FormatBytes((long)Math.Round(allocPerFrame))}" +
			$" · {Strings.LabelGcPause} {gcPercent:0.##}%");

		_loop.Update([
			(Strings.LabelFrameRate, fresh.ContinuousLoop
				? $"{frame.Fps:0.#} fps · 帧间隔 p50 {frame.GapP50Ms:0.0}ms / p95 {frame.GapP95Ms:0.0}ms"
				: $"按请求重绘 · 最近一秒 {fresh.FramesPerSecond:0} 帧 · 典型 FPS 不适用",
				fresh.ContinuousLoop ? "帧间隔可换算 FPS" : "帧间隔含空闲，不换算 FPS"),
			(Strings.LabelInputLatency, frame.Inputs > 0
				? $"p95 {frame.InputP95Ms:0.0}ms · 最大 {frame.InputMaxMs:0.0}ms（样本 {frame.Inputs}）"
				: "还没有按键/滚轮样本", (string?)null),
			(Strings.LabelQueueLag, fresh.QueueLagMaxMs >= Diagnostics.LagWarnMs
				? $"最近 {fresh.QueueLagMs:0.0}ms · 累计最大 {fresh.QueueLagMaxMs:0.0}ms（≥50ms 表示 UI 线程被占住）"
				: $"最近 {fresh.QueueLagMs:0.0}ms · 累计最大 {fresh.QueueLagMaxMs:0.0}ms", (string?)null),
			(Strings.LabelDrawCalls, frame.DrawCalls > 0
				? $"{frame.DrawCalls} 调用 · 剔除 {frame.CulledCalls} · 文本 {frame.TextDraws} · 形状 {frame.ShapeDraws}" +
					$" · 图片 {frame.ImageDraws} · 裁剪 {frame.ClipCount}"
				: "—", (string?)null),
		]);

		_memory.Update([
			(Strings.LabelHeap, fresh.HasSample
				? $"{Strings.FormatBytes(fresh.HeapBytes)} · {Strings.LabelLoh} {Strings.FormatBytes(fresh.LargeObjectHeapBytes)} · POH {Strings.FormatBytes(fresh.PinnedHeapBytes)}"
				: "—", (string?)null),
			(Strings.LabelAllocated, fresh.HasSample
				? $"{Strings.FormatBytes(fresh.AllocatedBytes)} · gen0 {fresh.Gen0Collections} / gen2 {fresh.Gen2Collections}（进程累计）"
				: "—", (string?)null),
			(Strings.LabelFragmented, fresh.HasSample ? Strings.FormatBytes(fresh.FragmentedBytes) : "—", (string?)null),
			(Strings.LabelPinned, fresh.HasSample
				? $"{fresh.PinnedObjects} · {Strings.LabelFinalizers} {fresh.FinalizationPending}"
				: "—", (string?)null),
			(Strings.LabelCpu, fresh.HasSample
				? $"{fresh.CpuPercent:0.#}% · {Strings.LabelPool} {fresh.PoolThreads} · 排队 {fresh.PoolPendingWork}"
				: "—", null),
			("诊断队列", fresh.DroppedLines > 0
				? $"丢弃 {fresh.DroppedLines} 行（写盘跟不上）"
				: "没有丢弃", (string?)null),
			(Strings.DiagnosticsCompactHint, Strings.DiagnosticsMemoryHint, (string?)null),
		]);

		_counters.Update(fresh.Counters.Take(12)
			.Select(static counter => (
				counter.Name,
				counter.TotalMs > 0 ? $"×{counter.Value} · Σ {counter.TotalMs:0.0}ms" : $"×{counter.Value}",
				counter.MaxMs > 0 ? $"最大 {counter.MaxMs:0.0}ms · 明细只含慢样本" : null))
			.ToArray());

		_stats.Update(fresh.Stats.Take(14)
			.Select(static stat => (
				stat.Name,
				$"×{stat.Count} · Σ {stat.TotalMs:0.0}ms",
				(string?)$"p50 {stat.P50Ms:0.0} · p95 {stat.P95Ms:0.0} · max {stat.MaxMs:0.0} · last {stat.LastMs:0.0}"))
			.ToArray());

		_recent.Update(fresh.Recent.Take(10)
			.Select(static timing => (
				$"{timing.StartedAt:HH:mm:ss}  {timing.Name}",
				$"{timing.DurationMs:0.0}ms",
				timing.Detail))
			.ToArray());
	}

	/// 帧条图例：颜色必须是真的画笔色，不能只写一句「● 预算」让读者猜。
	private static Element FrameLegend()
	{
		var legend = new WrapPanel().Spacing(AppTheme.Space3);
		legend.Children(
			LegendItem(Strings.LabelFrameBudget, static palette => palette.Info),
			LegendItem(">预算", static palette => palette.Accent),
			LegendItem($">={Diagnostics.LongFrameReportMs:0}ms", static palette => palette.Error));
		return legend;
	}

	private static Element LegendItem(string text, Func<AppPalette, Color> color)
	{
		var dot = new Border().Width(7).Height(7).CornerRadius(3.5)
			.VerticalAlignment(VerticalAlignment.Center)
			.WithTheme((theme, border) => border.Background = color(AppTheme.For(theme)));
		return new StackPanel().Orientation(Orientation.Horizontal).Spacing(4).Children(
			dot,
			new TextBlock().Text(text).FontSize(AppTheme.FontSizeMicro).FaintText()
				.VerticalAlignment(VerticalAlignment.Center));
	}

	private static void SetText(TextBlock block, string value)
	{
		if (!string.Equals(block.Text, value, StringComparison.Ordinal)) block.Text = value;
	}

	private sealed class MetricTileView
	{
		private readonly TextBlock _value;
		private readonly TextBlock _hint;

		public MetricTileView(string label, int column, int row)
		{
			View = new Border()
				.Margin(new Thickness(column == 0 ? 0 : 3, 0, column == 0 ? 3 : 0, row == 0 ? 0 : 3))
				.Padding(new Thickness(AppTheme.Space3, AppTheme.Space2, AppTheme.Space3, AppTheme.Space2))
				.CornerRadius(AppTheme.RadiusSm)
				.BorderThickness(1);
			View.WithTheme(static (theme, border) =>
			{
				border.Background = theme.Palette.ContainerBackground;
				border.BorderBrush = theme.Palette.ControlBorder;
			});
			_value = new TextBlock()
				.FontSize(AppTheme.FontSizeCaption).FontWeight(FontWeight.Medium)
				.FontFamily(AppTheme.MonoFont).PrimaryText().TextTrimming(TextTrimming.CharacterEllipsis);
			_hint = new TextBlock()
				.FontSize(AppTheme.FontSizeMicro).FontFamily(AppTheme.MonoFont).FaintText()
				.TextWrapping(TextWrapping.Wrap).TextTrimming(TextTrimming.CharacterEllipsis);
			View.Child(new StackPanel().Spacing(2).Children(
				new TextBlock().Text(label).FontSize(AppTheme.FontSizeMicro).SubtleText(),
				_value,
				_hint));
			Grid.SetColumn(View, column);
			Grid.SetRow(View, row);
		}

		public Border View { get; }

		public void Set(string value, string hint)
		{
			SetText(_value, value);
			SetText(_hint, hint);
		}
	}

	private sealed class SectionView : StackPanel
	{
		private readonly DiagnosticsRow[] _rows;

		public SectionView(string title, int rows)
		{
			Spacing = 2;
			AddRange(
				new TextBlock().Text(title).FontSize(AppTheme.FontSizeMicro).SubtleText(),
				new Border().Height(1).Margin(new Thickness(0, 2, 0, 4))
					.WithTheme(static (theme, line) => line.Background = theme.Palette.ControlBorder));
			_rows = new DiagnosticsRow[rows];
			for (var index = 0; index < rows; index++)
			{
				_rows[index] = new DiagnosticsRow();
				Add(_rows[index].View);
			}
		}

		public void Update(IReadOnlyList<(string Name, string Value, string? Note)> rows)
		{
			IsVisible = rows.Count > 0;
			for (var index = 0; index < _rows.Length; index++)
			{
				var visible = index < rows.Count;
				_rows[index].IsVisible = visible;
				if (visible) _rows[index].Set(rows[index].Name, rows[index].Value, rows[index].Note);
				else _rows[index].Set(string.Empty, string.Empty, null);
			}
		}
	}

	private sealed class DiagnosticsRow
	{
		private readonly TextBlock _name;
		private readonly TextBlock _value;
		private string? _note;

		public DiagnosticsRow()
		{
			_value = new TextBlock()
				.FontFamily(AppTheme.MonoFont).FontSize(AppTheme.FontSizeMicro)
				.PrimaryText().DockRight().Margin(new Thickness(AppTheme.Space3, 0, 0, 0));
			_name = new TextBlock()
				.FontFamily(AppTheme.MonoFont).FontSize(AppTheme.FontSizeMicro)
				.SubtleText().TextTrimming(TextTrimming.CharacterEllipsis);
			View = new DockPanel().Children(_value, _name);
		}

		public DockPanel View { get; }

		public bool IsVisible
		{
			get => View.IsVisible;
			set => View.IsVisible = value;
		}

		public void Set(string name, string value, string? note)
		{
			SetText(_name, name);
			SetText(_value, value);
			if (!string.Equals(_note, note, StringComparison.Ordinal))
			{
				_note = note;
				View.HoverTip(note);
			}
		}
	}
}
