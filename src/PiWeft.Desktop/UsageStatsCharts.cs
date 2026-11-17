using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Aprillz.MewUI.Rendering;
using PiWeft.CodingAgent.Stats;

namespace PiWeft.Desktop;

internal sealed record UsageTrendBucket(string Label, string Tooltip, long Tokens);

internal sealed record UsageDonutSlice(string Label, long Tokens, long Replies, int ColorIndex, string Hint);

/// 使用统计的两张图。柱状图用真实控件拼出来，留出可悬停的命中层；
/// 环形图自绘，因为 MewUI 没有扇形路径控件。
internal static class UsageStatsCharts
{
	public const double TrendHeight = 146;

	private static readonly byte[] SliceAlphas = [0xF0, 0xC8, 0xA0, 0x80, 0x66, 0x50, 0x3C];

	public static IReadOnlyList<UsageTrendBucket> Trend(UsageStatsSlice slice, out bool monthly)
	{
		monthly = slice.Days.Count > 92;
		if (!monthly)
		{
			return
			[
				.. slice.Days.Select(static day => new UsageTrendBucket(
					UsageStatsFormat.DayLabel(day.Date),
					Strings.UsageStatsHeatmapCell(day.Date, UsageStatsFormat.Tokens(day.Totals.Tokens)),
					day.Totals.Tokens))
			];
		}

		var months = new List<UsageTrendBucket>();
		var index = 0;
		while (index < slice.Days.Count)
		{
			var month = slice.Days[index].Date[..Math.Min(7, slice.Days[index].Date.Length)];
			long tokens = 0;
			long replies = 0;
			while (index < slice.Days.Count && slice.Days[index].Date.StartsWith(month, StringComparison.Ordinal))
			{
				tokens += slice.Days[index].Totals.Tokens;
				replies += slice.Days[index].Totals.Messages;
				index++;
			}

			months.Add(new UsageTrendBucket(
				UsageStatsFormat.MonthLabel(month),
				$"{month} · {UsageStatsFormat.Tokens(tokens)} tokens · {replies} 条回复",
				tokens));
		}

		return months;
	}

	public static Element TrendChart(IReadOnlyList<UsageTrendBucket> buckets)
	{
		var count = Math.Max(1, buckets.Count);
		var columns = Columns(count);
		var max = buckets.Count == 0 ? 0 : buckets.Max(static bucket => bucket.Tokens);
		var chart = new Grid().Height(TrendHeight).Columns(columns);
		for (var index = 0; index < buckets.Count; index++)
		{
			var bucket = buckets[index];
			var height = max <= 0 || bucket.Tokens <= 0
				? 0
				: Math.Max(2, TrendHeight * bucket.Tokens / max);
			var bar = new Border()
				.Height(height)
				.Margin(new Thickness(2, 0, 2, 0))
				.VerticalAlignment(VerticalAlignment.Bottom)
				.WithTheme(static (theme, border) => border.Background = AppTheme.For(theme).Accent.WithAlpha(0xB4));
			var hit = new Border()
				.Background(AppTheme.Transparent)
				.HoverTip(bucket.Tooltip);
			chart.Children(bar.Column(index), hit.Column(index));
		}

		var baseline = new Border().Height(1)
			.WithTheme(static (theme, border) => border.Background = AppTheme.For(theme).BorderSubtle);
		var labels = new Grid().Columns(columns);
		var step = buckets.Count <= 8 ? 1 : buckets.Count <= 30 ? 7 : Math.Max(1, buckets.Count / 6);
		for (var index = 0; index < buckets.Count; index++)
		{
			// 末尾标签与上一个太近时让给上一个，30 天的坐标轴不会叠字。
			var show = index % step == 0 ||
				(index == buckets.Count - 1 && (buckets.Count - 1) % step >= step / 2);
			if (!show)
			{
				continue;
			}

			labels.Children(new TextBlock()
				.Text(buckets[index].Label)
				.FontFamily(AppTheme.MonoFont)
				.FontSize(11)
				.SubtleText()
				.HorizontalAlignment(HorizontalAlignment.Center)
				.Column(index));
		}

		return new StackPanel().Spacing(4).Children(chart, baseline, labels);
	}

	public static IReadOnlyList<UsageDonutSlice> Slices(IReadOnlyList<ModelStats> models, long totalTokens)
	{
		var slices = new List<UsageDonutSlice>();
		if (totalTokens <= 0)
		{
			return slices;
		}

		var shown = models.Take(6).ToList();
		// 同名模型来自不同供应商时只写模型名会并成两行一样的字；只给重名的那些补供应商。
		var duplicated = shown
			.GroupBy(UsageStatsFormat.ModelName, StringComparer.Ordinal)
			.Where(static group => group.Count() > 1)
			.Select(static group => group.Key)
			.ToHashSet(StringComparer.Ordinal);
		for (var index = 0; index < shown.Count; index++)
		{
			var model = shown[index];
			var name = UsageStatsFormat.ModelName(model);
			var full = model.Provider.Length > 0 && model.Model.Length > 0 ? $"{model.Provider}/{name}" : name;
			var label = model.Model.Length > 0 && duplicated.Contains(name) ? full : name;
			slices.Add(new UsageDonutSlice(label, model.Totals.Tokens, model.Totals.Messages, index, full));
		}

		var shownTokens = shown.Sum(static model => model.Totals.Tokens);
		if (shownTokens < totalTokens)
		{
			var shownReplies = shown.Sum(static model => model.Totals.Messages);
			slices.Add(new UsageDonutSlice("其他", totalTokens - shownTokens,
				Math.Max(0, models.Sum(static model => model.Totals.Messages) - shownReplies), 6, "其他"));
		}

		return slices;
	}

	public static UsageDonutChart Donut(IReadOnlyList<UsageDonutSlice> slices)
	{
		var chart = new UsageDonutChart();
		chart.SetSlices(slices);
		return chart;
	}

	public static Color SliceColor(AppPalette palette, int index) =>
		palette.TextPrimary.WithAlpha(SliceAlphas[Math.Clamp(index, 0, SliceAlphas.Length - 1)]);

	public static string Columns(int count) => string.Join(',', Enumerable.Repeat("*", Math.Max(1, count)));
}

/// 模型占比环形图。颜色只用主文字色的透明度阶梯，深浅主题都落在同一套墨色里。
internal sealed class UsageDonutChart : FrameworkElement
{
	private IReadOnlyList<UsageDonutSlice> _slices = [];
	private long _total;

	public UsageDonutChart()
	{
		Width = 168;
		Height = 168;
		IsHitTestVisible = false;
	}

	public void SetSlices(IReadOnlyList<UsageDonutSlice> slices)
	{
		_slices = slices;
		_total = 0;
		foreach (var slice in slices)
		{
			_total += Math.Max(0, slice.Tokens);
		}

		InvalidateVisual();
	}

	protected override Size MeasureContent(Size availableSize) => new(Width, Height);

	protected override void OnThemeChanged(Theme oldTheme, Theme newTheme) => InvalidateVisual();

	protected override void OnRender(IGraphicsContext context)
	{
		var box = Bounds;
		var radius = Math.Min(box.Width, box.Height) / 2 - 2;
		if (radius <= 2 || _total <= 0)
		{
			return;
		}

		var palette = AppTheme.For(Theme);
		var centerX = box.X + box.Width / 2;
		var centerY = box.Y + box.Height / 2;
		var inner = radius * 0.62;
		if (_slices.Count == 1)
		{
			context.FillEllipse(new Rect(centerX - radius, centerY - radius, radius * 2, radius * 2),
				UsageStatsCharts.SliceColor(palette, 0));
			context.FillEllipse(new Rect(centerX - inner, centerY - inner, inner * 2, inner * 2), palette.CardBackground);
			return;
		}

		var angle = -Math.PI / 2;
		foreach (var slice in _slices)
		{
			var sweep = Math.Tau * slice.Tokens / _total;
			if (sweep <= 0)
			{
				continue;
			}

			// 段间留一道缝，缝宽跟段本身成比例，小段不会被吃掉。
			var gap = Math.Min(0.03, sweep * 0.12);
			var start = angle + gap / 2;
			var end = angle + sweep - gap / 2;
			angle += sweep;
			if (end <= start)
			{
				continue;
			}

			var path = new PathGeometry();
			path.MoveTo(centerX + radius * Math.Cos(start), centerY + radius * Math.Sin(start));
			path.Arc(centerX, centerY, radius, radius, start, end);
			path.LineTo(centerX + inner * Math.Cos(end), centerY + inner * Math.Sin(end));
			path.Arc(centerX, centerY, inner, inner, end, start, counterClockwise: true);
			path.Close();
			context.FillPath(path, UsageStatsCharts.SliceColor(palette, slice.ColorIndex));
		}
	}
}
