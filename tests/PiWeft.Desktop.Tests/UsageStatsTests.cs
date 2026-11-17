using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using PiWeft.CodingAgent.Stats;
using Xunit;
using static PiWeft.Desktop.Tests.VisualTree;

namespace PiWeft.Desktop.Tests;

public sealed class UsageStatsFormatTests
{
	[Fact]
	public void TokensUseCompactBinaryPrefixes()
	{
		Assert.Equal("0", UsageStatsFormat.Tokens(0));
		Assert.Equal("999", UsageStatsFormat.Tokens(999));
		Assert.Equal("1.2K", UsageStatsFormat.Tokens(1_234));
		Assert.Equal("2M", UsageStatsFormat.Tokens(2_000_000));
		Assert.Equal("3.5B", UsageStatsFormat.Tokens(3_500_000_000));
	}

	[Fact]
	public void ChatDurationShowsDaysHoursAndMinutes()
	{
		Assert.Equal("--", UsageStatsFormat.Duration(0));
		Assert.Equal("<1 分", UsageStatsFormat.Duration(30_000));
		Assert.Equal("12 分", UsageStatsFormat.Duration(12 * 60_000));
		Assert.Equal("1 小时", UsageStatsFormat.Duration(60 * 60_000));
		Assert.Equal("2 小时 5 分", UsageStatsFormat.Duration((2 * 60 + 5) * 60_000));
		Assert.Equal("3 天 4 小时", UsageStatsFormat.Duration((3 * 24 + 4) * 60 * 60_000));
	}

	[Fact]
	public void DateLabelsAreCompactAndLocaleStable()
	{
		Assert.Equal("6/1", UsageStatsFormat.DayLabel("2026-06-01"));
		Assert.Equal("26/6", UsageStatsFormat.MonthLabel("2026-06"));
		Assert.Equal("2026/6/1", UsageStatsFormat.ShortDate("2026-06-01"));
		Assert.Equal("--", UsageStatsFormat.Percent(null));
		Assert.Equal("60%", UsageStatsFormat.Percent(60));
	}
}

[Collection("mewui-controls")]
public sealed class UsageStatsPanelTests
{
	[Fact]
	public void PanelRendersLifetimeSummaryRangeChartsAndDetails()
	{
		var today = DateOnly.FromDateTime(DateTime.Now);
		var date = today.ToString("yyyy-MM-dd");
		var totals = new StatsTotals { Input = 400, Output = 200, CacheRead = 600, Messages = 2, Cost = 0.5 };
		var model = new ModelStats("openai", "gpt-a", totals);
		var report = new UsageStatsReport(
			[new DailyStats(date, totals)],
			[model],
			[new ProjectStats("/work/alpha", totals)],
			totals)
		{
			DailyModels = [new DailyModelStats(date, [model])],
			Sessions = 3,
			Turns = 4,
			LongestSessionMs = ((2 * 60) + 5) * 60_000,
		};

		var panel = new UsageStatsPanel(() => "/missing", null, showHeading: true);
		panel.Apply(report, DateTimeOffset.Now);
		var texts = Find<TextBlock>(panel.View).Select(static text => text.Text).ToList();

		// 汇总条：累计 / 峰值 / 最长聊天 / 连续天数。
		Assert.Contains("1.2K", texts);
		Assert.Contains("2 小时 5 分", texts);
		Assert.Contains("1 天", texts);
		// 区间、模型、明细与项目。
		Assert.Contains("每日 Token 趋势", texts);
		Assert.Contains("模型用量", texts);
		Assert.Contains("gpt-a", texts);
		Assert.Contains("60%", texts);
		Assert.Contains("/work/alpha", texts);
		Assert.DoesNotContain(Strings.UsageStatsEmptyTitle, texts);
		UiLifetime.Release(panel.View);
	}

	[Fact]
	public void SwitchingRangeRebuildsOnlyTheLowerPanel()
	{
		var today = DateOnly.FromDateTime(DateTime.Now);
		var days = new List<DailyStats>();
		for (var offset = 130; offset >= 0; offset--)
		{
			days.Add(new DailyStats(today.AddDays(-offset).ToString("yyyy-MM-dd"), new StatsTotals { Input = 10, Messages = 1 }));
		}

		var report = new UsageStatsReport(days, [], [], new StatsTotals { Input = 10 * days.Count })
		{
			Sessions = 1,
			Turns = days.Count,
			LongestSessionMs = 60_000,
		};
		var panel = new UsageStatsPanel(() => "/missing", null, showHeading: false);
		panel.Apply(report, DateTimeOffset.Now);
		Assert.Contains(Find<TextBlock>(panel.View), text => text.Text == Strings.UsageStatsDailyChart);

		var all = Find<TextBlock>(panel.View).Single(text => text.Text == Strings.UsageStatsRangeAll);
		ImagePreviewTests.Click(Assert.IsType<Button>(all.Parent));

		var texts = Find<TextBlock>(panel.View).Select(static text => text.Text).ToList();
		Assert.Contains(Strings.UsageStatsMonthlyChart, texts);
		Assert.Contains(Strings.UsageStatsLifetimeTotal, texts);
		UiLifetime.Release(panel.View);
	}

	[Fact]
	public void PanelShowsEmptyStateWhenTheRangeHasNoUsage()
	{
		var panel = new UsageStatsPanel(() => "/missing", null, showHeading: false);
		panel.Apply(UsageStatsReport.Empty, DateTimeOffset.Now);
		var texts = Find<TextBlock>(panel.View).Select(static text => text.Text).ToList();

		Assert.Contains(Strings.UsageStatsEmptyTitle, texts);
		Assert.Contains(Strings.UsageStatsEmptyDescription, texts);
		Assert.DoesNotContain(Strings.UsageStatsDailyChart, texts);
		UiLifetime.Release(panel.View);
	}

	[Fact]
	public void PanelShowsErrorNoticeAndKeepsEmptyState()
	{
		var panel = new UsageStatsPanel(() => "/missing", null, showHeading: false);
		panel.Fail("permission denied");
		var texts = Find<TextBlock>(panel.View).Select(static text => text.Text).ToList();

		Assert.Contains(Strings.UsageStatsError, texts);
		Assert.Contains(Strings.UsageStatsEmptyTitle, texts);
		UiLifetime.Release(panel.View);
	}

	[Fact]
	public void LongRangesBucketTheTrendByMonth()
	{
		var today = new DateOnly(2026, 6, 1);
		var days = new List<DailyStats>();
		for (var offset = 130; offset >= 0; offset--)
		{
			days.Add(new DailyStats(today.AddDays(-offset).ToString("yyyy-MM-dd"),
				new StatsTotals { Input = 10 }));
		}

		var slice = new UsageStatsSlice(days, [], [], new StatsTotals { Input = 10 * days.Count }, 10, days[^1].Date,
			days.Count, 1, 1);
		var buckets = UsageStatsCharts.Trend(slice, out var monthly);

		Assert.True(monthly);
		Assert.InRange(buckets.Count, 4, 6);
		Assert.Equal(10 * days.Count, buckets.Sum(static bucket => bucket.Tokens));
	}
}
