using System.Text.Json.Nodes;
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

		var panel = new UsageStatsPanel(() => "/missing", null);
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
		var panel = new UsageStatsPanel(() => "/missing", null);
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
		var panel = new UsageStatsPanel(() => "/missing", null);
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
		var panel = new UsageStatsPanel(() => "/missing", null);
		panel.Fail("permission denied");
		var texts = Find<TextBlock>(panel.View).Select(static text => text.Text).ToList();

		Assert.Contains(Strings.UsageStatsError, texts);
		Assert.Contains(Strings.UsageStatsEmptyTitle, texts);
		UiLifetime.Release(panel.View);
	}

	private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
	{
		var deadline = Environment.TickCount64 + timeoutMs;
		while (!condition() && Environment.TickCount64 < deadline)
		{
			await Task.Delay(25);
		}
	}

	[Fact]
	public async Task SettingsUsageSectionReusesTheSamePanel()
	{
		var root = Path.Combine(Path.GetTempPath(), "piweft-usage-settings-" + Guid.NewGuid().ToString("N")[..8]);
		Directory.CreateDirectory(root);
		try
		{
			var cache = new UsageStatsCache();
			var settings = new SettingsView(() => new SettingsSnapshot
			{
				AgentDir = root,
				Cwd = root,
				ModelCount = 0,
				Tools = [],
				Skills = [],
				McpServers = [],
				Resources = PiWeft.CodingAgent.Resources.ResourceSnapshot.Empty,
				SessionsRoot = root,
			}, null, null, null, null, cache);

			await settings.SelectSectionAsync("usage");
			// 等后台扫描落地：没有 dispatcher 时完成回调直接在后台线程改可视化树，
			// 这里不等就会和下面的 Find 撞上 MewUI 共享的遍历栈。
			await settings.WaitForSectionWorkAsync();
			// 让后台扫描完成，再验证重进时复用同一块面板而不是重新创建。
			await WaitUntilAsync(() => cache.Scans == 1);
			Assert.Equal(1, cache.Scans);
			var before = Find<TextBlock>(settings.View).First(text => text.Text.StartsWith(Strings.UsageStatsSourceHint, StringComparison.Ordinal));

			await settings.SelectSectionAsync("overview");
			await settings.SelectSectionAsync("usage");
			await settings.WaitForSectionWorkAsync();

			// 重进会核对一次文件（增量，未变时不重读），面板本身仍然是同一块。
			await WaitUntilAsync(() => cache.Scans == 2);
			Assert.Equal(2, cache.Scans);
			var after = Find<TextBlock>(settings.View).First(text => text.Text.StartsWith(Strings.UsageStatsSourceHint, StringComparison.Ordinal));
			Assert.Same(before, after);

			// 离开分区取消可能在途的扫描，测试结束不把后台任务带进下一个用例。
			await settings.SelectSectionAsync("overview");
			await Task.Delay(50);
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	[Fact]
	public void DuplicateModelNamesAreQualifiedByProvider()
	{
		var models = new[]
		{
			new ModelStats("openrouter", "shared", new StatsTotals { Input = 10 }),
			new ModelStats("deepseek", "shared", new StatsTotals { Input = 20 }),
			new ModelStats("anthropic", "unique", new StatsTotals { Input = 30 }),
		};

		var slices = UsageStatsCharts.Slices(models, 60);

		Assert.Contains(slices, slice => slice is { Label: "openrouter/shared", Hint: "openrouter/shared" });
		Assert.Contains(slices, slice => slice is { Label: "deepseek/shared", Hint: "deepseek/shared" });
		Assert.Contains(slices, slice => slice is { Label: "unique", Hint: "anthropic/unique" });
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

public sealed class UsageStatsCacheTests
{
	[Fact]
	public async Task RescansWhenFilesChangeAndRereadsOnlyThoseFiles()
	{
		var root = Path.Combine(Path.GetTempPath(), "piweft-usage-cache-" + Guid.NewGuid().ToString("N")[..8]);
		Directory.CreateDirectory(root);
		try
		{
			WriteAssistant(root, 10);
			var cache = new UsageStatsCache();
			var first = await cache.GetAsync(root, (_, _) => null, force: false, CancellationToken.None);
			Assert.Equal(10, first.Report.Totals.Input);
			Assert.Equal(1, cache.Scans);

			// 再进页面会核对文件，内容未变时增量扫描不重读，结果不变。
			var second = await cache.GetAsync(root, (_, _) => null, force: false, CancellationToken.None);
			Assert.Equal(10, second.Report.Totals.Input);
			Assert.Equal(2, cache.Scans);
			Assert.Equal(0, cache.ReparsedFiles);

			// 写不同长度的内容：不依赖 mtime 分辨率，长度变化就能触发重扫。
			WriteAssistant(root, 2000);
			var third = await cache.GetAsync(root, (_, _) => null, force: false, CancellationToken.None);
			Assert.Equal(2000, third.Report.Totals.Input);
			Assert.Equal(3, cache.Scans);
			Assert.Equal(1, cache.ReparsedFiles);

			await cache.GetAsync(root, (_, _) => null, force: true, CancellationToken.None);
			Assert.Equal(4, cache.Scans);
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	[Fact]
	public async Task ConcurrentRequestsShareOneScan()
	{
		var root = Path.Combine(Path.GetTempPath(), "piweft-usage-shared-" + Guid.NewGuid().ToString("N")[..8]);
		Directory.CreateDirectory(root);
		try
		{
			WriteAssistant(root, 10);
			var cache = new UsageStatsCache();
			// 两个并发调用必须传同一个 rates 委托（面板在构造时只取一次快照）。
			Func<string, string, PiWeft.Ai.ModelCostRates?> rates = (_, _) => null;
			var first = cache.GetAsync(root, rates, force: false, CancellationToken.None);
			var second = cache.GetAsync(root, rates, force: false, CancellationToken.None);
			var results = await Task.WhenAll(first, second);

			Assert.Equal(1, cache.Scans);
			Assert.Same(results[0].Report, results[1].Report);
			Assert.Equal(10, results[0].Report.Totals.Input);
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	private static void WriteAssistant(string root, long input)
	{
		var directory = Path.Combine(root, "--a--");
		Directory.CreateDirectory(directory);
		File.WriteAllLines(Path.Combine(directory, "2026-06-01T10-00-00-000Z_s1.jsonl"),
		[
			"""{"type":"session","version":3,"id":"s1","timestamp":"2026-06-01T10:00:00.000Z","cwd":"/work/alpha"}""",
			new JsonObject
			{
				["type"] = "message",
				["id"] = "a1",
				["timestamp"] = "2026-06-01T10:00:01.000Z",
				["message"] = new JsonObject
				{
					["role"] = "assistant",
					["provider"] = "openai",
					["model"] = "gpt-a",
					["usage"] = new JsonObject { ["input"] = input, ["output"] = 1 },
				},
			}.ToJsonString(),
		]);
	}
}
