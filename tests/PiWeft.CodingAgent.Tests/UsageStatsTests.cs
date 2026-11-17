using System.Text;
using PiWeft.Ai;
using PiWeft.CodingAgent.Session;
using PiWeft.CodingAgent.Stats;
using Xunit;

namespace PiWeft.CodingAgent.Tests;

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

	private static string Assistant(string timestamp, string provider, string model, long input, long output, long cacheRead = 0, long cacheWrite = 0, double? cost = null, double? durationMs = null, long? totalTokens = null)
	{
		var usage = $"{{\"input\":{input},\"output\":{output},\"cacheRead\":{cacheRead},\"cacheWrite\":{cacheWrite}" +
			(cost is { } value ? $",\"cost\":{{\"total\":{value.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}" : string.Empty) +
			(totalTokens is { } total ? $",\"totalTokens\":{total}" : string.Empty) + "}";
		var duration = durationMs is { } ms
			? $",\"durationMs\":{ms.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
			: string.Empty;
		var messageTimestamp = DateTimeOffset.TryParse(timestamp,
			System.Globalization.CultureInfo.InvariantCulture,
			System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed)
			? parsed.ToUnixTimeMilliseconds()
			: 0;
		return $"{{\"type\":\"message\",\"id\":\"m1\",\"parentId\":null,\"timestamp\":\"{timestamp}\",\"message\":{{\"role\":\"assistant\",\"provider\":\"{provider}\",\"model\":\"{model}\",\"usage\":{usage}{duration},\"timestamp\":{messageTimestamp}}}}}";
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

		// 不存在的根目录返回空报告，不抛异常。
		Assert.Empty(UsageStats.Scan(Path.Combine(_root, "missing")).Days);
		Assert.Equal(0, UsageStats.Scan(Path.Combine(_root, "missing")).Totals.Tokens);
	}

	[Fact]
	public void ScanTracksDailyModelsSessionsTurnsAndLongestSession()
	{
		Session("--a--", "2026-06-01T10-00-00-000Z_s1.jsonl",
			Header("2026-06-01T10:00:00.000Z", "/work/alpha"),
			"""{"type":"message","id":"u1","timestamp":"2026-06-01T10:00:01.000Z","message":{"role":"user","content":"hi"}}""",
			Assistant("2026-06-01T10:04:00.000Z", "openai", "gpt-a", 100, 10, cost: 0.5),
			Assistant("2026-06-01T10:20:00.000Z", "anthropic", "claude", 200, 20, cacheRead: 30, cost: 0.25),
			// 注入上下文也是 user 角色，但带 customType，不算用户轮次。
			"""{"type":"message","id":"u2","timestamp":"2026-06-01T10:21:00.000Z","message":{"role":"user","customType":"injected","content":"context"}}""");

		var report = UsageStats.Scan(_sessions, new UsageStatsOptions { TimeZone = TimeZoneInfo.Utc });

		Assert.Equal(1, report.Sessions);
		Assert.Equal(1, report.Turns);
		Assert.Equal(21 * 60 * 1000, report.LongestSessionMs, 0);
		var day = Assert.Single(report.DailyModels);
		Assert.Equal("2026-06-01", day.Date);
		Assert.Equal(["claude", "gpt-a"], day.Models.Select(static model => model.Model));
		Assert.Equal(30, day.Models[0].Totals.CacheRead);
	}

	[Fact]
	public void ScanFlagsTruncatedFileLimits()
	{
		Session("--a--", "2026-06-03T10-00-00-000Z_s3.jsonl",
			Header("2026-06-03T10:00:00.000Z", "/work/alpha"),
			Assistant("2026-06-03T10:00:01.000Z", "openai", "gpt-a", 10, 1));
		Session("--a--", "2026-06-02T10-00-00-000Z_s2.jsonl",
			Header("2026-06-02T10:00:00.000Z", "/work/alpha"),
			Assistant("2026-06-02T10:00:01.000Z", "openai", "gpt-a", 10, 1));
		Session("--b--", "2026-06-01T10-00-00-000Z_s1.jsonl",
			Header("2026-06-01T10:00:00.000Z", "/work/beta"),
			Assistant("2026-06-01T10:00:01.000Z", "openai", "gpt-a", 10, 1));

		var limited = UsageStats.Scan(_sessions, new UsageStatsOptions { MaxFiles = 2, TimeZone = TimeZoneInfo.Utc });
		Assert.True(limited.Truncated);
		Assert.Equal(2, limited.Sessions);

		var perProject = UsageStats.Scan(_sessions, new UsageStatsOptions { MaxFilesPerProject = 1, TimeZone = TimeZoneInfo.Utc });
		Assert.True(perProject.Truncated);
		Assert.Equal(2, perProject.Sessions);

		var complete = UsageStats.Scan(_sessions, new UsageStatsOptions { TimeZone = TimeZoneInfo.Utc });
		Assert.False(complete.Truncated);
		Assert.Equal(3, complete.Sessions);
	}

	[Fact]
	public void ScanPrefersReplyDurationsOverWallClockSpan()
	{
		Session("--a--", "2026-06-01T10-00-00-000Z_s1.jsonl",
			Header("2026-06-01T10:00:00.000Z", "/work/alpha"),
			Assistant("2026-06-01T10:10:00.000Z", "openai", "gpt-a", 10, 1, durationMs: 60_000),
			Assistant("2026-06-01T11:00:00.000Z", "openai", "gpt-a", 10, 1, durationMs: 120_000));

		var report = UsageStats.Scan(_sessions, new UsageStatsOptions { TimeZone = TimeZoneInfo.Utc });

		// 墙钟跨度 60 分钟，但真实回复时长只有 3 分钟；空闲间隔不能算聊天时长。
		Assert.Equal(180_000, report.LongestSessionMs, 0);
	}

	[Fact]
	public void ScanAcceptsUtf8BomInSessionFiles()
	{
		var directory = Path.Combine(_sessions, "--a--");
		Directory.CreateDirectory(directory);
		var path = Path.Combine(directory, "2026-06-01T10-00-00-000Z_s1.jsonl");
		var lines = string.Join('\n',
			Header("2026-06-01T10:00:00.000Z", "/work/alpha"),
			Assistant("2026-06-01T10:00:01.000Z", "openai", "gpt-a", 10, 1));
		File.WriteAllBytes(path, [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(lines)]);

		var report = UsageStats.Scan(_sessions, new UsageStatsOptions { TimeZone = TimeZoneInfo.Utc });

		Assert.Equal(1, report.Sessions);
		Assert.Equal(10, report.Totals.Input);
	}

	[Fact]
	public void ScanAcceptsSpacedJsonFromImportedSessions()
	{
		Session("--a--", "2026-06-01T10-00-00-000Z_s1.jsonl",
			"""{ "type": "session", "version": 3, "id": "s1", "timestamp": "2026-06-01T10:00:00.000Z", "cwd": "/work/alpha" }""",
			"""{ "type": "message", "id": "u1", "timestamp": "2026-06-01T10:00:00.000Z", "message": { "role": "user", "content": "hi" } }""",
			"""{ "type": "message", "id": "a1", "timestamp": "2026-06-01T10:04:00.000Z", "message": { "role": "assistant", "provider": "openai", "model": "gpt-a", "usage": { "input": 10, "output": 1 } } }""");

		var report = UsageStats.Scan(_sessions, new UsageStatsOptions { TimeZone = TimeZoneInfo.Utc });

		Assert.Equal(1, report.Sessions);
		Assert.Equal(1, report.Turns);
		Assert.Equal("/work/alpha", Assert.Single(report.Projects).Directory);
		Assert.Equal(4 * 60 * 1000, report.LongestSessionMs, 0);
	}

	[Fact]
	public void ScanBucketsDaysInTheRequestedTimeZone()
	{
		Session("--a--", "2026-06-01T20-00-00-000Z_s1.jsonl",
			Header("2026-06-01T20:00:00.000Z", "/work/alpha"),
			Assistant("2026-06-01T20:00:01.000Z", "openai", "gpt-a", 10, 1));

		var zone = TimeZoneInfo.CreateCustomTimeZone("Test+08", TimeSpan.FromHours(8), "Test+08", "Test+08");
		var report = UsageStats.Scan(_sessions, new UsageStatsOptions { TimeZone = zone });

		Assert.Equal("2026-06-02", Assert.Single(report.Days).Date);
	}

	[Fact]
	public void StreaksCountConsecutiveActiveDaysAndGiveTodayGrace()
	{
		var days = new[]
		{
			new DailyStats("2026-06-01", new StatsTotals { Input = 10 }),
			new DailyStats("2026-06-02", new StatsTotals { Input = 10 }),
			new DailyStats("2026-06-03", new StatsTotals { Input = 10 }),
			new DailyStats("2026-06-04", new StatsTotals { Input = 10 }),
			new DailyStats("2026-06-05", new StatsTotals()),
		};

		// 今天还没用：从昨天继续算，连续 4 天不断。
		Assert.Equal(new UsageStreak(4, 4), UsageStats.Streaks(days, new DateOnly(2026, 6, 5)));
		// 今天用了：仍是 5 天。
		days[4] = days[4] with { Totals = new StatsTotals { Input = 1 } };
		Assert.Equal(new UsageStreak(5, 5), UsageStats.Streaks(days, new DateOnly(2026, 6, 5)));
		// 昨天也没用：断在今天之前。
		Assert.Equal(new UsageStreak(0, 5), UsageStats.Streaks(days, new DateOnly(2026, 6, 7)));
	}

	[Fact]
	public void SliceFillsMissingDaysAndComputesPeakAndStreak()
	{
		var days = new[]
		{
			new DailyStats("2026-06-01", new StatsTotals { Input = 100 }),
			new DailyStats("2026-06-03", new StatsTotals { Input = 300 }),
			new DailyStats("2026-06-05", new StatsTotals { Input = 50 }),
		};
		var dailyModels = new[]
		{
			new DailyModelStats("2026-06-03", [new ModelStats("openai", "gpt-a", new StatsTotals { Input = 300, Messages = 1 })]),
			new DailyModelStats("2026-06-01", [new ModelStats("openai", "gpt-a", new StatsTotals { Input = 100, Messages = 1 })]),
		};
		var report = new UsageStatsReport(days, [], [], new StatsTotals { Input = 450 })
		{
			DailyModels = dailyModels,
		};

		var recent = UsageStats.Slice(report, 5, new DateOnly(2026, 6, 5));
		Assert.Equal(5, recent.Days.Count);
		Assert.Equal(450, recent.Totals.Input);
		Assert.Equal(300, recent.PeakDayTokens);
		Assert.Equal("2026-06-03", recent.PeakDay);
		Assert.Equal(3, recent.ActiveDays);
		Assert.Equal(1, recent.CurrentStreakDays);
		Assert.Equal(1, recent.LongestStreakDays);
		Assert.Equal(400, Assert.Single(recent.Models).Totals.Input);

		var all = UsageStats.Slice(report, 0, new DateOnly(2026, 6, 5));
		Assert.Equal("2026-06-01", all.Days[0].Date);
		Assert.Equal(5, all.Days.Count);
	}

	[Fact]
	public void ScanMatchesSessionStatisticsForTheSameSession()
	{
		var directory = Path.Combine(_sessions, "--a--");
		Directory.CreateDirectory(directory);
		var start = new DateTimeOffset(2026, 6, 1, 10, 0, 0, TimeSpan.Zero);
		var path = Path.Combine(directory, "2026-06-01T10-00-00-000Z_s1.jsonl");
		var header = new SessionHeader { Id = "s1", Timestamp = Iso(start), Cwd = "/work/alpha" };
		var user = new SessionMessageEntry
		{
			Id = "u1",
			Timestamp = Iso(start.AddSeconds(1)),
			Message = new UserMessage { Timestamp = start.AddSeconds(1).ToUnixTimeMilliseconds(), Content = "hi" },
		};
		var reply = new SessionMessageEntry
		{
			Id = "a1",
			Timestamp = Iso(start.AddSeconds(2)),
			Message = new AssistantMessage
			{
				Timestamp = start.AddSeconds(2).ToUnixTimeMilliseconds(),
				Provider = "openai",
				Model = "gpt-a",
				Content = [new TextContent { Text = new string('x', 5000) }],
				Usage = UsageOf(100, 10, 20, 5, 135, 0.5),
				DurationMs = 60_000,
			},
		};
		var cacheWarm = new UsageEntry
		{
			Id = "warm",
			Timestamp = Iso(start.AddSeconds(70)),
			Kind = "cache_warm",
			Provider = "openai",
			Model = "gpt-a",
			Usage = UsageOf(7, 0, 3, 0, 10, 0.01),
		};
		var tool = new SessionMessageEntry
		{
			Id = "tool",
			Timestamp = Iso(start.AddSeconds(80)),
			Message = new ToolResultMessage
			{
				ToolCallId = "call",
				ToolName = "read",
				Content = [new TextContent { Text = "tool output" }],
				IsError = false,
				DurationMs = 20,
				// 工具结果上的 usage 不算模型回复；两边都应忽略。
				Usage = UsageOf(999, 0, 0, 0, 999, 9),
			},
		};
		var compaction = new CompactionEntry
		{
			Id = "compact",
			Timestamp = Iso(start.AddSeconds(90)),
			Summary = "summary",
			FirstKeptEntryId = "u1",
			// 压缩/分支摘要用量在 SessionStatistics 里也不计；保持两边同口径。
			Usage = UsageOf(500, 50, 0, 0, 550, 5),
		};
		var branch = new BranchSummaryEntry
		{
			Id = "branch",
			Timestamp = Iso(start.AddSeconds(100)),
			FromId = "u1",
			Summary = "branch",
			Usage = UsageOf(600, 60, 0, 0, 660, 6),
		};
		var reply2 = new SessionMessageEntry
		{
			Id = "a2",
			Timestamp = Iso(start.AddSeconds(120)),
			Message = new AssistantMessage
			{
				Timestamp = start.AddSeconds(120).ToUnixTimeMilliseconds(),
				Provider = "openai",
				Model = "gpt-a",
				Content = [new TextContent { Text = new string('y', 3000) }],
				Usage = UsageOf(200, 20, 30, 0, 250, 0.25),
				DurationMs = 30_000,
			},
		};

		File.WriteAllLines(path,
		[
			SessionJson.Serialize(header), SessionJson.Serialize(user), SessionJson.Serialize(reply),
			SessionJson.Serialize(cacheWarm), SessionJson.Serialize(tool), SessionJson.Serialize(compaction),
			SessionJson.Serialize(branch), SessionJson.Serialize(reply2),
		]);

		var manager = SessionManager.Open(path);
		var expected = new SessionStatisticsCache().Get(manager);
		var report = UsageStats.Scan(_sessions, new UsageStatsOptions { TimeZone = TimeZoneInfo.Utc });

		Assert.Equal(expected.Input, report.Totals.Input);
		Assert.Equal(expected.Output, report.Totals.Output);
		Assert.Equal(expected.CacheRead, report.Totals.CacheRead);
		Assert.Equal(expected.CacheWrite, report.Totals.CacheWrite);
		Assert.Equal(expected.Tokens, report.Totals.Tokens);
		Assert.Equal(expected.Cost, report.Totals.Cost, 6);
		Assert.Equal(expected.AssistantMessages, report.Totals.Messages);
		Assert.Equal(expected.UserMessages, report.Turns);
		Assert.Equal(1, report.Sessions);
		Assert.Equal(90_000, report.LongestSessionMs, 0);
	}

	private static string Iso(DateTimeOffset time) =>
		time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);

	private static Usage UsageOf(long input, long output, long cacheRead, long cacheWrite, long total, double cost) => new()
	{
		Input = input,
		Output = output,
		CacheRead = cacheRead,
		CacheWrite = cacheWrite,
		TotalTokens = total,
		Cost = new UsageCost { Total = cost },
	};

	[Fact]
	public void PersistentSessionAppendInvalidatesTheGeneration()
	{
		var before = UsageStats.Generation;
		var manager = SessionManager.Create("/work", Path.Combine(_root, "sessions"));
		manager.AppendMessage(new UserMessage { Content = "hi" });

		Assert.True(UsageStats.Generation > before);
	}

	[Fact]
	public void ScanCountsAFileWithoutAHeaderAsASession()
	{
		var directory = Path.Combine(_sessions, "--a--");
		Directory.CreateDirectory(directory);
		File.WriteAllLines(Path.Combine(directory, "2026-06-01T10-00-00-000Z_s1.jsonl"),
		[
			Assistant("2026-06-01T10:00:01.000Z", "openai", "gpt-a", 10, 1),
		]);

		var report = UsageStats.Scan(_sessions, new UsageStatsOptions { TimeZone = TimeZoneInfo.Utc });

		Assert.Equal(1, report.Sessions);
		Assert.Equal(10, report.Totals.Input);
	}

	[Fact]
	public void ScanSkipsAMalformedLineWithoutLosingTheRestOfTheFile()
	{
		Session("--a--", "2026-06-01T10-00-00-000Z_s1.jsonl",
			Header("2026-06-01T10:00:00.000Z", "/work/alpha"),
			Assistant("2026-06-01T10:00:01.000Z", "openai", "gpt-a", 10, 1),
			// 带 usage/timestamp 标记但 JSON 不完整；只跳过这一行。
			"""{"type":"message","timestamp":"2026-06-01T10:00:02.000Z","message":{"role":"assistant","usage":""",
			Assistant("2026-06-01T10:01:00.000Z", "openai", "gpt-a", 20, 2));

		var report = UsageStats.Scan(_sessions, new UsageStatsOptions { TimeZone = TimeZoneInfo.Utc });

		Assert.Equal(30, report.Totals.Input);
		Assert.Equal(2, report.Totals.Messages);
	}

	[Fact]
	public void ScanMergesOverlappingReplyDurations()
	{
		Session("--a--", "2026-06-01T10-00-00-000Z_s1.jsonl",
			Header("2026-06-01T10:00:00.000Z", "/work/alpha"),
			Assistant("2026-06-01T10:00:01.000Z", "openai", "gpt-a", 10, 1, durationMs: 60_000),
			Assistant("2026-06-01T10:00:31.000Z", "openai", "gpt-a", 10, 1, durationMs: 60_000),
			Assistant("2026-06-01T10:05:00.000Z", "openai", "gpt-a", 10, 1, durationMs: 30_000));

		var report = UsageStats.Scan(_sessions, new UsageStatsOptions { TimeZone = TimeZoneInfo.Utc });

		// 前两段重叠 30 秒，并集 90 秒；再加 30 秒独立回复，总活跃 120 秒，而不是 150 秒。
		Assert.Equal(120_000, report.LongestSessionMs, 0);
	}

	[Fact]
	public void ScanUsesProviderTotalWhenItExceedsTheBucketSum()
	{
		Session("--a--", "2026-06-01T10-00-00-000Z_s1.jsonl",
			Header("2026-06-01T10:00:00.000Z", "/work/alpha"),
			// 供应商总量大于四桶之和（例如思考令牌），保留供应商总量。
			Assistant("2026-06-01T10:00:01.000Z", "openai", "gpt-a", 100, 10, cacheRead: 5, totalTokens: 500),
			// 供应商总量小于四桶之和时退回桶之和，不能让明细大于合计。
			Assistant("2026-06-01T10:01:01.000Z", "openai", "gpt-a", 100, 10, cacheRead: 5, totalTokens: 50));

		var report = UsageStats.Scan(_sessions, new UsageStatsOptions { TimeZone = TimeZoneInfo.Utc });

		Assert.Equal(200, report.Totals.Input);
		Assert.Equal(20, report.Totals.Output);
		Assert.Equal(10, report.Totals.CacheRead);
		Assert.Equal(615, report.Totals.Tokens);
		Assert.Equal(615, Assert.Single(report.Days).Totals.Tokens);
		Assert.Equal(615, Assert.Single(report.Models).Totals.Tokens);
	}

	[Fact]
	public void CacheHitPercentUsesAllPromptSideBuckets()
	{
		var totals = new StatsTotals { Input = 100, CacheRead = 300, CacheWrite = 100 };
		Assert.Equal(60, totals.CacheHitPercent);
		Assert.Null(new StatsTotals().CacheHitPercent);
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
