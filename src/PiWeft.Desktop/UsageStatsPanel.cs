using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using PiWeft.Ai;
using PiWeft.CodingAgent.Stats;

namespace PiWeft.Desktop;

/// 使用统计的「应用用量」面板：汇总条、活动热力图、区间趋势与模型占比。
/// 扫描通过共享 UsageStatsCache 在后台线程执行；区间切换只换下半页，不重扫会话。
internal sealed class UsageStatsPanel
{
	private enum Range
	{
		SevenDays,
		ThirtyDays,
		All,
	}

	private readonly Func<string> _sessionsRoot;
	private readonly Func<string, string, ModelCostRates?> _rates;
	private readonly UsageStatsCache _cache;
	private readonly bool _showHeading;
	private readonly StackPanel _root = new();
	private readonly StackPanel _staticBody = new();
	private readonly StackPanel _rangeBody = new();
	private readonly Dictionary<Range, UsageStatsSlice> _slices = [];
	private readonly TextBlock _source = new();
	private readonly TextBlock _lastRefresh = new();
	private readonly Button _refresh;
	private UsageStatsReport? _report;
	private UsageStatsSlice? _lifetime;
	private string? _error;
	private Range _range = Range.SevenDays;
	private CancellationTokenSource? _abort;
	private int _generation;
	private bool _busy;
	private bool _open = true;
	private DateOnly _sliceDay = DateOnly.FromDateTime(DateTime.Now);
	private DateTimeOffset? _refreshedAt;

	public UsageStatsPanel(Func<string> sessionsRoot, Func<string, string, ModelCostRates?>? rates = null, bool showHeading = true, UsageStatsCache? cache = null)
	{
		_sessionsRoot = sessionsRoot;
		_rates = rates ?? ((_, _) => null);
		_cache = cache ?? new UsageStatsCache();
		_showHeading = showHeading;
		_refresh = AppIcons.TextButton("refresh-cw", Strings.UsageStatsRefresh, () => _ = RefreshAsync());
		_source.FontSize(AppTheme.FontSizeMicro).FaintText().TextWrapping(TextWrapping.Wrap)
			.HoverTip(Strings.UsageStatsTooltip);
		_lastRefresh.FontSize(AppTheme.FontSizeMicro).FaintText().VerticalAlignment(VerticalAlignment.Center);
		_staticBody.Spacing(AppTheme.Space3);
		_rangeBody.Spacing(AppTheme.Space3);
		_root.Spacing(AppTheme.Space3).MaxWidth(960)
			.Children(Header(), _source, _staticBody, _rangeBody, Footer());
		Render();
	}

	public UIElement View => _root;

	public void ApplyTheme() => AppStyles.ApplyTextColors(_root);

	/// <summary>取消在途扫描，但保留已渲染的数据；重新进入分区时再刷新。</summary>
	public void Cancel()
	{
		_generation++;
		var abort = _abort;
		_abort = null;
		Cancellation.CancelQuietly(abort);
		abort?.Dispose();
		_busy = false;
		UpdateFooter();
	}

	public void Close()
	{
		_open = false;
		Cancel();
	}

	/// <summary>进入页面时先让共享缓存核对文件指纹；未变化就直接复用。</summary>
	public Task RefreshIfStaleAsync()
	{
		if (_busy)
		{
			return Task.CompletedTask;
		}

		return RefreshCoreAsync(force: false);
	}

	/// <summary>手动刷新绕过缓存，强制重扫。</summary>
	public Task RefreshAsync() => RefreshCoreAsync(force: true);

	private async Task RefreshCoreAsync(bool force)
	{
		if (!_open)
		{
			return;
		}

		var generation = ++_generation;
		Cancellation.CancelQuietly(_abort);
		var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
		_abort = cancellation;
		_busy = true;
		var hadError = _error is not null;
		_error = null;
		UpdateFooter();
		if (_report is null || hadError)
		{
			Render();
		}

		UsageStatsCacheResult? result = null;
		string? error = null;
		try
		{
			var root = _sessionsRoot();
			result = await _cache.GetAsync(root, _rates, force, cancellation.Token).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			error = Strings.UsageStatsTimeout;
		}
		catch (Exception ex)
		{
			error = ex.Message;
		}

		void Complete()
		{
			cancellation.Dispose();
			if (!_open || generation != _generation)
			{
				return;
			}

			_abort = null;
			_busy = false;
			if (result is { } loaded)
			{
				ApplyReport(loaded.Report, loaded.RefreshedAt);
				return;
			}

			_error = error ?? Strings.UsageStatsUnavailable;
			Render();
		}

		var dispatcher = Application.IsRunning ? Application.Current.Dispatcher : null;
		if (dispatcher is null || dispatcher.IsOnUIThread)
		{
			Complete();
		}
		else
		{
			dispatcher.BeginInvoke(Complete);
		}
	}

	private void ApplyReport(UsageStatsReport report, DateTimeOffset refreshedAt)
	{
		if (ReferenceEquals(report, _report) && _refreshedAt == refreshedAt)
		{
			var previousDay = _sliceDay;
			var today = Today();
			if (today != previousDay)
			{
				// 缓存命中但跨过了午夜：连续天数与近 N 日必须按新的今天重算。
				Render();
			}
			else
			{
				UpdateFooter();
			}

			return;
		}

		InvalidateSlices();
		_report = report;
		_error = null;
		_refreshedAt = refreshedAt;
		Render();
	}

	/// <summary>仅供测试：直接套用一份失败状态，不启动扫描。</summary>
	internal void Fail(string error)
	{
		InvalidateSlices();
		_report = null;
		_error = error;
		_busy = false;
		Render();
	}

	/// <summary>仅供测试：直接套用一份报告，不启动扫描。</summary>
	internal void Apply(UsageStatsReport report, DateTimeOffset refreshedAt)
	{
		InvalidateSlices();
		_report = report;
		_error = null;
		_refreshedAt = refreshedAt;
		_busy = false;
		Render();
	}

	private Element Header()
	{
		var row = new StackPanel().Orientation(Orientation.Horizontal)
			.VerticalAlignment(VerticalAlignment.Center).Spacing(AppTheme.Space2);
		if (_showHeading)
		{
			row.Children(new TextBlock().Text(Strings.UsageStatsTitle)
				.FontSize(18).FontWeight(FontWeight.SemiBold).PrimaryText()
				.VerticalAlignment(VerticalAlignment.Center));
		}

		row.Children(new Border()
			.Padding(new Thickness(8, 2, 8, 2))
			.CornerRadius(AppTheme.RadiusXs)
			.WithTheme(static (theme, border) => border.Background = AppTheme.For(theme).CardBackgroundAlt)
			.Child(new TextBlock().Text(Strings.UsageStatsAppUsage)
				.FontSize(AppTheme.FontSizeMicro).MutedText()
				.VerticalAlignment(VerticalAlignment.Center))
			.VerticalAlignment(VerticalAlignment.Center));
		return row;
	}

	private Element Footer()
	{
		return new DockPanel().LastChildFill()
			.Margin(new Thickness(0, AppTheme.Space2, 0, 0))
			.Children(_refresh.DockRight(), _lastRefresh);
	}

	private void UpdateFooter()
	{
		_lastRefresh.Text(_busy
			? Strings.UsageStatsLoading
			: _refreshedAt is { } refreshedAt ? Strings.UsageStatsLastRefresh(UsageStatsFormat.RefreshTime(refreshedAt)) : string.Empty);
		_refresh.IsEnabled = !_busy;
	}

	private void UpdateSource()
	{
		var source = Strings.UsageStatsSourceHint;
		if (_report is { } report)
		{
			source += " " + Strings.UsageStatsSessionSummary(report.Sessions, report.Turns);
			if (report.Truncated)
			{
				source += " " + Strings.UsageStatsTruncatedHint;
			}
		}

		_source.Text(source);
	}

	private void Render()
	{
		RenderStatic();
		RenderRange();
		UpdateSource();
		UpdateFooter();
	}

	// 汇总条与热力图只跟扫描结果走；切区间只重建下半页，364 个热力格不必每次释放重排。
	private void RenderStatic()
	{
		Clear(_staticBody);
		var today = Today();
		var lifetime = Lifetime(today);
		_staticBody.Children(SummaryStrip(lifetime, _report?.LongestSessionMs ?? 0));
		if (_report is { } report && lifetime is { Totals.Tokens: > 0 })
		{
			_staticBody.Children(HeatmapCard(report, today));
		}
	}

	private void RenderRange()
	{
		Clear(_rangeBody);
		_rangeBody.Children(RangeHeader());
		if (_error is { Length: > 0 } error)
		{
			_rangeBody.Children(ErrorNotice(error));
		}

		if (_report is null)
		{
			_rangeBody.Children(_busy
				? EmptyState(Strings.UsageStatsLoadingTitle, Strings.UsageStatsLoadingDescription)
				: EmptyState(Strings.UsageStatsEmptyTitle, Strings.UsageStatsEmptyDescription));
			return;
		}

		var slice = SliceFor(_range, Today());
		if (slice.Totals.Tokens == 0)
		{
			_rangeBody.Children(EmptyState(Strings.UsageStatsEmptyTitle, Strings.UsageStatsEmptyDescription));
			return;
		}

		_rangeBody.Children(TrendCard(slice));
		_rangeBody.Children(ModelCard(slice));
		_rangeBody.Children(DetailsCard(slice));
		if (_report.Projects.Count > 0)
		{
			_rangeBody.Children(ProjectsCard(_report));
		}
	}

	private static void Clear(StackPanel panel)
	{
		foreach (var child in panel.Children.ToArray())
		{
			UiLifetime.Release(child);
		}

		panel.Clear();
	}

	private UsageStatsSlice? Lifetime(DateOnly today)
	{
		if (_report is null)
		{
			return null;
		}

		return _lifetime ??= UsageStats.Slice(_report, 0, today);
	}

	private UsageStatsSlice SliceFor(Range range, DateOnly today)
	{
		if (_slices.TryGetValue(range, out var cached))
		{
			return cached;
		}

		var slice = UsageStats.Slice(_report!, RangeDays(range), today);
		_slices[range] = slice;
		return slice;
	}

	private DateOnly Today()
	{
		var today = DateOnly.FromDateTime(DateTime.Now);
		if (today != _sliceDay)
		{
			// 面板跨过午夜时，连续天数与近 N 日要按新的今天重算。
			_sliceDay = today;
			InvalidateSlices();
		}

		return today;
	}

	private void InvalidateSlices()
	{
		_slices.Clear();
		_lifetime = null;
	}

	private static int RangeDays(Range range) => range switch
	{
		Range.SevenDays => 7,
		Range.ThirtyDays => 30,
		_ => 0,
	};

	private static Element SummaryStrip(UsageStatsSlice? lifetime, double longestSessionMs)
	{
		var values = new[]
		{
			(lifetime is null ? "--" : UsageStatsFormat.Tokens(lifetime.Totals.Tokens), Strings.UsageStatsLifetimeTotal, Strings.UsageStatsTotalHint),
			(lifetime is null ? "--" : UsageStatsFormat.Tokens(lifetime.PeakDayTokens), Strings.UsageStatsLifetimePeak, Strings.UsageStatsPeakHint),
			(lifetime is null ? "--" : UsageStatsFormat.Duration(longestSessionMs), Strings.UsageStatsLongestChat, Strings.UsageStatsLongestChatHint),
			(lifetime is null ? "--" : UsageStatsFormat.Days(lifetime.CurrentStreakDays), Strings.UsageStatsCurrentStreak, Strings.UsageStatsStreakHint),
			(lifetime is null ? "--" : UsageStatsFormat.Days(lifetime.LongestStreakDays), Strings.UsageStatsLongestStreak, Strings.UsageStatsStreakHint),
		};

		var grid = new Grid().Columns("*,auto,*,auto,*,auto,*,auto,*");
		for (var index = 0; index < values.Length; index++)
		{
			var (value, label, hint) = values[index];
			var cell = new StackPanel().Spacing(2).VerticalAlignment(VerticalAlignment.Center)
				.HoverTip(hint)
				.Children(
					new TextBlock().Text(value)
						.FontFamily(AppTheme.MonoFont).FontSize(17).FontWeight(FontWeight.SemiBold)
						.PrimaryText().TextAlignment(TextAlignment.Center)
						.TextTrimming(TextTrimming.CharacterEllipsis),
					new TextBlock().Text(label)
						.FontSize(AppTheme.FontSizeMicro).SubtleText().TextAlignment(TextAlignment.Center)
						.TextTrimming(TextTrimming.CharacterEllipsis));
			if (index > 0)
			{
				grid.Children(new Border().Width(1).Height(28)
					.VerticalAlignment(VerticalAlignment.Center)
					.WithTheme(static (theme, line) => line.Background = AppTheme.For(theme).BorderSubtle)
					.Column((index * 2) - 1));
			}

			grid.Children(cell.Column(index * 2));
		}

		return new Border().StyleName(AppStyles.Card).CornerRadius(AppTheme.RadiusMd)
			.Padding(new Thickness(AppTheme.Space4, AppTheme.Space3, AppTheme.Space4, AppTheme.Space3))
			.Child(grid);
	}

	private Element HeatmapCard(UsageStatsReport report, DateOnly today)
	{
		const int weeks = 52;
		var cells = UsageStats.Heatmap(report, weeks, today);
		var rows = new StackPanel().Spacing(2);
		for (var weekday = 0; weekday < 7; weekday++)
		{
			var row = new StackPanel().Orientation(Orientation.Horizontal).Spacing(2);
			for (var week = 0; week < weeks; week++)
			{
				var cell = cells[(week * 7) + weekday];
				var square = new Border()
					.Width(11).Height(11).CornerRadius(2)
					.WithTheme((theme, border) => border.Background = HeatColor(theme, cell.Level))
					.HoverTip(Strings.UsageStatsHeatmapCell(cell.Date, UsageStatsFormat.Tokens(cell.Tokens)));
				row.Children(square);
			}

			rows.Children(row);
		}

		var legend = new StackPanel().Orientation(Orientation.Horizontal)
			.Spacing(3).VerticalAlignment(VerticalAlignment.Center);
		legend.Children(new TextBlock().Text("少").FontSize(11).FaintText()
			.VerticalAlignment(VerticalAlignment.Center));
		for (var level = 0; level <= 4; level++)
		{
			var value = level;
			legend.Children(new Border().Width(10).Height(10).CornerRadius(2)
				.VerticalAlignment(VerticalAlignment.Center)
				.WithTheme((theme, border) => border.Background = HeatColor(theme, value)));
		}

		legend.Children(new TextBlock().Text("多").FontSize(11).FaintText()
			.VerticalAlignment(VerticalAlignment.Center));

		// 热力图按自然宽高缩放：宽窗口填满卡片，窄窗口（设置弹窗缩到 720）整体缩小也不被裁掉。
		var heatmap = new Viewbox
		{
			Child = rows,
			Stretch = Stretch.Uniform,
			StretchDirection = StretchDirection.Both,
		};
		return Card(Strings.UsageStatsHeatmap, new StackPanel().Spacing(AppTheme.Space2).Children(
			heatmap,
			new TextBlock().Text(Strings.UsageStatsHeatmapHint)
				.FontSize(AppTheme.FontSizeMicro).FaintText().TextWrapping(TextWrapping.Wrap)), legend);
	}

	private static Color HeatColor(Theme theme, int level)
	{
		var palette = AppTheme.For(theme);
		return level switch
		{
			1 => palette.Accent.WithAlpha(0x22),
			2 => palette.Accent.WithAlpha(0x48),
			3 => palette.Accent.WithAlpha(0x78),
			4 => palette.Accent.WithAlpha(0xB4),
			_ => palette.CardBackgroundAlt,
		};
	}

	private Element RangeHeader()
	{
		var track = new Border().StyleName(AppStyles.SegmentTrack).Padding(new Thickness(3));
		var grid = new Grid().Columns("*,*,*").Spacing(4);
		var entries = new (Range Range, string Label)[]
		{
			(Range.SevenDays, Strings.UsageStatsRange7d),
			(Range.ThirtyDays, Strings.UsageStatsRange30d),
			(Range.All, Strings.UsageStatsRangeAll),
		};
		for (var index = 0; index < entries.Length; index++)
		{
			var (range, label) = entries[index];
			var active = _range == range;
			var text = new TextBlock().Text(label).FontSize(AppTheme.FontSizeCaption)
				.HorizontalAlignment(HorizontalAlignment.Center);
			if (active)
			{
				text.PrimaryText().FontWeight(FontWeight.SemiBold);
			}
			else
			{
				text.MutedText();
			}

			var button = new Button()
				.Content(text)
				.OnClick(() => SetRange(range))
				.StyleName(active ? AppStyles.SegmentButtonActive : AppStyles.SegmentButton)
				.Height(AppTheme.ControlHeightSm + 2)
				.HorizontalAlignment(HorizontalAlignment.Stretch)
				.Column(index);
			grid.Children(button);
		}

		track.Child(grid);
		return new DockPanel().LastChildFill()
			.Children(track.Width(240).DockRight(),
				new TextBlock().Text(Strings.UsageStatsRangeTitle)
					.FontSize(AppTheme.FontSizeCaption).FontWeight(FontWeight.Medium).PrimaryText()
					.VerticalAlignment(VerticalAlignment.Center));
	}

	private void SetRange(Range range)
	{
		if (_range == range)
		{
			return;
		}

		_range = range;
		if (DateOnly.FromDateTime(DateTime.Now) != _sliceDay)
		{
			// 跨过午夜时汇总条也要重算，不能只换下半页。
			Render();
		}
		else
		{
			RenderRange();
		}
	}

	private Element TrendCard(UsageStatsSlice slice)
	{
		var buckets = UsageStatsCharts.Trend(slice, out var monthly);
		var title = monthly ? Strings.UsageStatsMonthlyChart : Strings.UsageStatsDailyChart;
		var hint = monthly
			? Strings.UsageStatsMonthlyChartHint(buckets.Count)
			: Strings.UsageStatsDailyChartHint(buckets.Count);
		var peak = new TextBlock()
			.Text($"峰值 {UsageStatsFormat.Tokens(slice.PeakDayTokens)}" +
				(slice.PeakDay is { Length: > 0 } day ? " · " + UsageStatsFormat.ShortDate(day) : string.Empty))
			.FontFamily(AppTheme.MonoFont).FontSize(AppTheme.FontSizeMicro).SubtleText()
			.HoverTip(Strings.UsageStatsPeakHint);
		return Card(title, new StackPanel().Spacing(AppTheme.Space2).Children(
			UsageStatsCharts.TrendChart(buckets),
			new TextBlock().Text(hint).FontSize(AppTheme.FontSizeMicro)
				.FaintText().TextWrapping(TextWrapping.Wrap)), peak);
	}

	private static Element ModelCard(UsageStatsSlice slice)
	{
		var slices = UsageStatsCharts.Slices(slice.Models, slice.Totals.Tokens);
		var donut = UsageStatsCharts.Donut(slices);
		var center = new StackPanel().Spacing(1)
			.HorizontalAlignment(HorizontalAlignment.Center)
			.VerticalAlignment(VerticalAlignment.Center)
			.Children(
				new TextBlock().Text(UsageStatsFormat.Tokens(slice.Totals.Tokens))
					.FontFamily(AppTheme.MonoFont).FontSize(16).FontWeight(FontWeight.SemiBold).PrimaryText()
					.TextAlignment(TextAlignment.Center),
				new TextBlock().Text(Strings.UsageStatsTokenUnit)
					.FontSize(11).FaintText().TextAlignment(TextAlignment.Center));
		var visual = new Grid().Width(180).Height(180).Children(donut, center);

		var list = new StackPanel().Spacing(0);
		if (slices.Count == 0)
		{
			list.Children(new TextBlock().Text(Strings.UsageStatsNoModels)
				.FontSize(AppTheme.FontSizeCaption).MutedText());
		}

		foreach (var model in slices)
		{
			var swatch = new Border().Width(8).Height(8).CornerRadius(2)
				.VerticalAlignment(VerticalAlignment.Center)
				.WithTheme((theme, border) => border.Background = UsageStatsCharts.SliceColor(AppTheme.For(theme), model.ColorIndex));
			var name = new TextBlock().Text(model.Label)
				.FontFamily(AppTheme.MonoFont).FontSize(AppTheme.FontSizeCaption).PrimaryText()
				.TextTrimming(TextTrimming.CharacterEllipsis)
				.HoverTip(model.Hint);
			var detail = new TextBlock()
				.Text(Strings.UsageStatsModelUsage(UsageStatsFormat.Tokens(model.Tokens), model.Replies))
				.FontSize(11).FaintText().TextTrimming(TextTrimming.CharacterEllipsis);
			var share = new TextBlock()
				.Text(UsageStatsFormat.Percent(slice.Totals.Tokens > 0 ? 100.0 * model.Tokens / slice.Totals.Tokens : null))
				.FontFamily(AppTheme.MonoFont).FontSize(AppTheme.FontSizeCaption).SubtleText()
				.DockRight().Margin(new Thickness(AppTheme.Space3, 0, 0, 0));
			var text = new StackPanel().Spacing(2).Children(name, detail);
			var row = new DockPanel().LastChildFill()
				.Padding(new Thickness(0, AppTheme.Space2, 0, AppTheme.Space2))
				.HoverTip(model.Hint)
				.Children(swatch.DockLeft().Margin(new Thickness(0, 2, AppTheme.Space2, 0)), share, text);
			list.Children(row);
		}

		return Card(Strings.UsageStatsModelChart,
			new Grid().Columns("auto,*").Spacing(AppTheme.Space4).Children(
				visual.Column(0).VerticalAlignment(VerticalAlignment.Center),
				new Border().Padding(new Thickness(AppTheme.Space3, 0, 0, 0)).Child(list).Column(1)));
	}

	private static Element DetailsCard(UsageStatsSlice slice)
	{
		var totals = slice.Totals;
		var rows = new StackPanel().Spacing(0).Children(
			MetricRow(Strings.UsageStatsTotal, UsageStatsFormat.Tokens(totals.Tokens), Strings.UsageStatsRangeTotalHint),
			MetricRow(Strings.UsageStatsInput, UsageStatsFormat.Tokens(totals.Input), null),
			MetricRow(Strings.UsageStatsOutput, UsageStatsFormat.Tokens(totals.Output), null),
			MetricRow(Strings.UsageStatsCacheRead, UsageStatsFormat.Tokens(totals.CacheRead), null),
			MetricRow(Strings.UsageStatsCacheWrite, UsageStatsFormat.Tokens(totals.CacheWrite), null),
			MetricRow(Strings.UsageStatsCacheHit, UsageStatsFormat.Percent(totals.CacheHitPercent), Strings.UsageStatsCacheHitHint),
			MetricRow(Strings.UsageStatsReplies, totals.Messages.ToString(System.Globalization.CultureInfo.InvariantCulture), null),
			MetricRow(Strings.UsageStatsActiveDays, UsageStatsFormat.Days(slice.ActiveDays), null));
		return Card(Strings.UsageStatsDetails, rows);
	}

	private static Element MetricRow(string label, string value, string? hint)
	{
		var row = new DockPanel().LastChildFill()
			.Padding(new Thickness(0, AppTheme.Space2, 0, AppTheme.Space2))
			.Children(
				new TextBlock().Text(value)
					.FontFamily(AppTheme.MonoFont).FontSize(AppTheme.FontSizeCaption).PrimaryText()
					.DockRight().Margin(new Thickness(AppTheme.Space3, 0, 0, 0)),
				new TextBlock().Text(label)
					.FontSize(AppTheme.FontSizeCaption).SubtleText()
					.TextTrimming(TextTrimming.CharacterEllipsis));
		if (hint is not null)
		{
			row.HoverTip(hint);
		}

		return row;
	}

	private static Element ProjectsCard(UsageStatsReport report)
	{
		var body = new StackPanel().Spacing(0);
		foreach (var project in report.Projects.Take(8))
		{
			var name = new TextBlock().Text(project.Directory)
				.FontFamily(AppTheme.MonoFont).FontSize(AppTheme.FontSizeCaption).PrimaryText()
				.TextTrimming(TextTrimming.CharacterEllipsis)
				.HoverTip(project.Directory);
			var value = new TextBlock()
				.Text(UsageStatsFormat.Tokens(project.Totals.Tokens) +
					(project.Totals.Cost > 0 ? " · " + UsageStatsFormat.Cost(project.Totals.Cost) : string.Empty))
				.FontFamily(AppTheme.MonoFont).FontSize(AppTheme.FontSizeCaption).SubtleText()
				.DockRight().Margin(new Thickness(AppTheme.Space3, 0, 0, 0))
				.HoverTip(Strings.UsageStatsCostHint);
			body.Children(new DockPanel().LastChildFill()
				.Padding(new Thickness(0, AppTheme.Space2, 0, AppTheme.Space2))
				.Children(value, name));
		}

		return Card(Strings.UsageStatsProjects, body,
			new TextBlock().Text(Strings.UsageStatsAllTime).FontSize(11).FaintText());
	}

	private static Element ErrorNotice(string error)
	{
		var weft = new Border().Width(2).Height(18).CornerRadius(1)
			.Margin(new Thickness(0, 0, AppTheme.Space2, 0))
			.WithTheme(static (theme, line) => line.Background = AppTheme.For(theme).Error);
		var message = new StackPanel().Orientation(Orientation.Horizontal)
			.Spacing(AppTheme.Space2).VerticalAlignment(VerticalAlignment.Center)
			.Children(
				AppIcons.Icon("triangle-alert", AppStyles.RoleError, 14),
				new TextBlock().Text(Strings.UsageStatsError)
					.FontSize(AppTheme.FontSizeCaption)
					.TextWrapping(TextWrapping.Wrap)
					.WithTheme(static (theme, block) => block.Foreground = AppTheme.For(theme).Error)
					.VerticalAlignment(VerticalAlignment.Center));
		return new Border()
			.CornerRadius(AppTheme.RadiusXs)
			.Padding(new Thickness(AppTheme.Space3, AppTheme.Space2, AppTheme.Space3, AppTheme.Space2))
			.WithTheme(static (theme, border) => border.Background = AppTheme.For(theme).Error.WithAlpha(0x14))
			.Child(new DockPanel().LastChildFill().Children(weft.DockLeft(), message))
			.HoverTip(error);
	}

	private static Element EmptyState(string title, string description)
	{
		return new Border()
			.StyleName(AppStyles.CardMuted)
			.CornerRadius(AppTheme.RadiusMd)
			.Padding(new Thickness(AppTheme.Space6))
			.Child(new StackPanel().Spacing(AppTheme.Space1)
				.HorizontalAlignment(HorizontalAlignment.Center)
				.VerticalAlignment(VerticalAlignment.Center)
				.Children(
					new TextBlock().Text(title)
						.FontSize(AppTheme.FontSizeControl).FontWeight(FontWeight.Medium).PrimaryText()
						.TextAlignment(TextAlignment.Center),
					new TextBlock().Text(description)
						.FontSize(AppTheme.FontSizeMicro).SubtleText()
						.TextAlignment(TextAlignment.Center).TextWrapping(TextWrapping.Wrap)));
	}

	private static Element Card(string title, Element body, Element? right = null)
	{
		var header = new DockPanel().LastChildFill();
		if (right is not null)
		{
			header.Children(right.DockRight());
		}

		header.Children(new TextBlock().Text(title)
			.FontSize(AppTheme.FontSizeCaption).FontWeight(FontWeight.SemiBold).PrimaryText()
			.VerticalAlignment(VerticalAlignment.Center));
		return new Border().StyleName(AppStyles.Card).CornerRadius(AppTheme.RadiusMd)
			.Padding(new Thickness(AppTheme.Space4))
			.Child(new StackPanel().Spacing(AppTheme.Space3).Children(header, body));
	}
}
