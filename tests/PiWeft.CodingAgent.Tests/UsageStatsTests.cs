using PiWeft.Ai;
using PiWeft.CodingAgent.Stats;
using Xunit;

namespace PiWeft.CodingAgent.Tests;

/// <summary>D-07：跨会话用量统计的有界扫描、聚合维度与热力图分档。</summary>
public class UsageStatsTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-stats-" + Guid.NewGuid().ToString("N")[..8]);
	private readonly string _sessions;

	public UsageStatsTests()
	{
		_sessions = Path.Combine(_root, "sessions");
		Directory.CreateDirectory(_sessions);
	}

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	private void Session(string project, string fileName, params string[] lines)
	{
		var directory = Path.Combine(_sessions, project);
		Directory.CreateDirectory(directory);
		File.WriteAllLines(Path.Combine(directory, fileName), lines);
	}

	private static string Header(string timestamp, string cwd) =>
		$$"""{"type":"session","version":3,"id":"s1","timestamp":"{{timestamp}}","cwd":"{{cwd}}"}""";

	private static string Assistant(string timestamp, string provider, string model, long input, long output, long cacheRead = 0, long cacheWrite = 0, double? cost = null)
	{
		var usage = $"{{\"input\":{input},\"output\":{output},\"cacheRead\":{cacheRead},\"cacheWrite\":{cacheWrite}" +
			(cost is { } value ? $",\"cost\":{{\"total\":{value.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}" : string.Empty) + "}";
		return $"{{\"type\":\"message\",\"id\":\"m1\",\"parentId\":null,\"timestamp\":\"{timestamp}\",\"message\":{{\"role\":\"assistant\",\"provider\":\"{provider}\",\"model\":\"{model}\",\"usage\":{usage}}}}}";
	}

	[Fact]
	public void ScanAggregatesDaysModelsAndProjects()
	{
		Session("--a--", "2026-01-01T10-00-00-000Z_s1.jsonl",
			Header("2026-01-01T10:00:00.000Z", "/work/alpha"),
			Assistant("2026-01-01T10:00:01.000Z", "openai", "gpt-a", 100, 10, cost: 0.5),
			Assistant("2026-01-02T09:00:00.000Z", "openai", "gpt-a", 200, 20, cost: 1.5),
			// 工具结果里也含 "usage" 字样，但角色不是 assistant，不应计入。
			"""{"type":"message","id":"t1","timestamp":"2026-01-02T09:00:02.000Z","message":{"role":"toolResult","content":[{"type":"text","text":"usage"}]}}""");

		Session("--b--", "2026-01-02T11-00-00-000Z_s2.jsonl",
			Header("2026-01-02T11:00:00.000Z", "/work/beta"),
			Assistant("2026-01-02T11:00:01.000Z", "anthropic", "claude", 300, 30, cacheRead: 40, cost: 2.0));

		var report = UsageStats.Scan(_sessions);

		Assert.Equal(600, report.Totals.Input);
		Assert.Equal(60, report.Totals.Output);
		Assert.Equal(40, report.Totals.CacheRead);
		Assert.Equal(3, report.Totals.Messages);
		Assert.Equal(4.0, report.Totals.Cost, 6);

		// 日期升序，且两天的令牌数正确。
		Assert.Equal(["2026-01-01", "2026-01-02"], report.Days.Select(static day => day.Date));
		Assert.Equal(110, report.Days[0].Totals.Tokens);
		Assert.Equal(590, report.Days[1].Totals.Tokens);

		// 模型分两行：gpt-a 两条消息，claude 一条且带缓存读。
		Assert.Equal(2, report.Models.Count);
		Assert.Contains(report.Models, static model => model is { Provider: "openai", Model: "gpt-a" } && model.Totals.Messages == 2);
		Assert.Contains(report.Models, static model => model is { Provider: "anthropic", Model: "claude" } && model.Totals.CacheRead == 40);

		// 项目名取会话头里的 cwd。
		Assert.Equal(["/work/alpha", "/work/beta"], report.Projects.Select(static project => project.Directory).Order(StringComparer.Ordinal));
	}

	[Fact]
	public void ScanSkipsToolOutputAndCountsUsageEntriesWithoutMessages()
	{
		Session("--a--", "2026-02-01T00-00-00-000Z_s1.jsonl",
			Header("2026-02-01T00:00:00.000Z", "/work/alpha"),
			// 缓存保温：用量条目，令牌计入但不算助手回复。
			"""{"type":"usage","id":"u1","timestamp":"2026-02-01T00:00:05.000Z","kind":"cache_warm","provider":"openai","model":"gpt-a","usage":{"input":7,"output":0,"cacheRead":3,"cacheWrite":0}}""",
			Assistant("2026-02-01T00:00:06.000Z", "openai", "gpt-a", 10, 1));

		var report = UsageStats.Scan(_sessions);

		Assert.Equal(17, report.Totals.Input);
		Assert.Equal(3, report.Totals.CacheRead);
		Assert.Equal(1, report.Totals.Messages);
	}

	[Fact]
	public void ScanHonoursSinceProjectFiltersAndFileLimits()
	{
		Session("--a--", "2026-03-01T00-00-00-000Z_s1.jsonl",
			Header("2026-03-01T00:00:00.000Z", "/work/alpha"),
			Assistant("2026-03-01T00:00:01.000Z", "openai", "gpt-a", 10, 1));
		Session("--b--", "2026-03-02T00-00-00-000Z_s2.jsonl",
			Header("2026-03-02T00:00:00.000Z", "/work/beta"),
			Assistant("2026-03-02T00:00:01.000Z", "openai", "gpt-a", 100, 10));

		// Since 只保留 3 月 2 日之后。
		var recent = UsageStats.Scan(_sessions, new UsageStatsOptions { Since = new DateTimeOffset(2026, 3, 2, 0, 0, 0, TimeSpan.Zero) });
		Assert.Equal(100, recent.Totals.Input);

		// Project 过滤到单个项目目录。
		var onlyBeta = UsageStats.Scan(_sessions, new UsageStatsOptions { Project = "--b--" });
		Assert.Equal(100, onlyBeta.Totals.Input);
		Assert.Single(onlyBeta.Projects);

		// 文件上限为 1 时只读最近的一个。
		var latest = UsageStats.Scan(_sessions, new UsageStatsOptions { MaxFiles = 1 });
		Assert.Equal(1, latest.Totals.Messages);
	}

	[Fact]
	public void ScanComputesCostFromRatesWhenSessionsLackCost()
	{
		Session("--a--", "2026-04-01T00-00-00-000Z_s1.jsonl",
			Header("2026-04-01T00:00:00.000Z", "/work/alpha"),
			Assistant("2026-04-01T00:00:01.000Z", "openai", "gpt-a", 1_000_000, 1_000_000, cacheRead: 1_000_000, cacheWrite: 1_000_000));

		var rates = new ModelCostRates { Input = 1, Output = 2, CacheRead = 0.1, CacheWrite = 0.5 };
		var report = UsageStats.Scan(_sessions, new UsageStatsOptions { Rates = (_, _) => rates });

		Assert.Equal(1 + 2 + 0.1 + 0.5, report.Totals.Cost, 6);
	}

	[Fact]
	public void ScanIgnoresOversizedLinesAndMissingRoots()
	{
		Session("--a--", "2026-05-01T00-00-00-000Z_s1.jsonl",
			Header("2026-05-01T00:00:00.000Z", "/work/alpha"),
			Assistant("2026-05-01T00:00:01.000Z", "openai", "gpt-a", 10, 1),
			// 超长行直接跳过，不让它撑爆一次刷新。
			$$"""{"type":"message","timestamp":"2026-05-01T00:00:02.000Z","message":{"role":"assistant","provider":"openai","model":"gpt-a","usage":{"input":999,"output":999},"pad":"{{new string('x', 5000)}}"}, "junk":"usage"}""");

		var report = UsageStats.Scan(_sessions, new UsageStatsOptions { MaxLineBytes = 1024 });
		Assert.Equal(10, report.Totals.Input);

		// 不存在的根目录返回空报告而不是抛异常。
		Assert.Empty(UsageStats.Scan(Path.Combine(_root, "missing")).Days);
		Assert.Equal(0, UsageStats.Scan(Path.Combine(_root, "missing")).Totals.Tokens);
	}

	[Fact]
	public void HeatmapBucketsTheLastWeeksIntoFourLevels()
	{
		var days = new[]
		{
			new DailyStats("2026-06-01", new StatsTotals { Input = 0 }),
			new DailyStats("2026-06-02", new StatsTotals { Input = 25 }),
			new DailyStats("2026-06-03", new StatsTotals { Input = 50 }),
			new DailyStats("2026-06-04", new StatsTotals { Input = 75 }),
			new DailyStats("2026-06-05", new StatsTotals { Input = 100 }),
		};

		var report = new UsageStatsReport(days, [], [], new StatsTotals());
		var cells = UsageStats.Heatmap(report, weeks: 2, today: new DateOnly(2026, 6, 7));

		Assert.Equal(14, cells.Count);
		Assert.Equal("2026-05-25", cells[0].Date);
		Assert.Equal("2026-06-07", cells[^1].Date);
		Assert.Equal(4, cells.Single(cell => cell.Date == "2026-06-05").Level);
		Assert.Equal(3, cells.Single(cell => cell.Date == "2026-06-04").Level);
		Assert.Equal(2, cells.Single(cell => cell.Date == "2026-06-03").Level);
		Assert.Equal(1, cells.Single(cell => cell.Date == "2026-06-02").Level);
		Assert.Equal(0, cells.Single(cell => cell.Date == "2026-06-01").Level);
		// 报告里没有的日期保持 0，不会凭空画用量。
		Assert.Equal(0, cells.Single(cell => cell.Date == "2026-06-06").Level);
	}
}
