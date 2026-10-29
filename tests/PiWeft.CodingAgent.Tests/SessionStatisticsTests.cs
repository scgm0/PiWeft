using PiWeft.Ai;
using PiWeft.CodingAgent.Session;
using Xunit;

namespace PiWeft.CodingAgent.Tests;

public sealed class SessionStatisticsTests
{
	private static CodingAgentSession Create(SessionManager manager) => new(new CodingAgentSessionOptions
	{
		Cwd = manager.Cwd, Session = manager, Tools = [], Skills = [], PromptTemplates = [],
		Model = new Model { Id = "test", Provider = "test", ContextWindow = 128000 },
		StreamFn = static (_, _, _) => throw new InvalidOperationException(),
	});

	private static AssistantMessage Assistant(long input, long read, long write, double? duration = null) => new()
	{
		Content = [new TextContent { Text = "answer" }], StopReason = StopReason.Stop,
		Usage = new Usage { Input = input, Output = 20, CacheRead = read, CacheWrite = write, TotalTokens = input + read + write + 20, Cost = new UsageCost { Total = 0.5 } },
		DurationMs = duration, FirstTokenMs = duration is null ? null : 100, StreamingDurationMs = duration is null ? null : duration - 100,
	};

	[Fact]
	public void IncrementalTotalsIncludeAuxiliaryUsageButNotToolUsageOrInvalidCacheSamples()
	{
		var manager = SessionManager.InMemory(Environment.CurrentDirectory);
		manager.AppendMessage(new UserMessage());
		manager.AppendMessage(Assistant(100, 800, 100, 1100));
		using var session = Create(manager);
		var initial = session.Statistics();
		Assert.Same(initial, session.Statistics());
		Assert.Equal(80, initial.LatestCacheHitPercent);
		manager.AppendMessage(Assistant(900, 100, 0, 2100));
		manager.AppendMessage(new AssistantMessage());
		manager.AppendMessage(new ToolResultMessage { IsError = true, DurationMs = 50, Usage = new Usage { Input = 999 } });
		manager.AppendUsage("compaction", "test", "test", new Usage { Input = 10, Output = 2, TotalTokens = 12, Cost = new UsageCost { Total = 0.1 } });
		var stats = session.Statistics();
		Assert.Equal(1010, stats.Input);
		Assert.Equal(42, stats.Output);
		Assert.Equal(900, stats.CacheRead);
		Assert.Equal(100, stats.CacheWrite);
		Assert.Equal(2052, stats.Tokens);
		Assert.Equal(1.1, stats.Cost, 6);
		Assert.Equal(10, stats.LatestCacheHitPercent);
		Assert.Equal(45, stats.AverageCacheHitPercent);
		Assert.Equal(2, stats.CacheHitSamples);
		Assert.Equal(1, stats.UserMessages);
		Assert.Equal(3, stats.AssistantMessages);
		Assert.Equal(1, stats.ToolResults);
		Assert.Equal(1, stats.ToolErrors);
		Assert.Equal(3200, stats.LlmDurationMs);
		Assert.Equal(50, stats.ToolDurationMs);
		Assert.Equal(100, stats.AverageFirstTokenMs);
		Assert.Equal(2, stats.FirstTokenSamples);
		Assert.Equal(session.UsageTotals(), (stats.Tokens, stats.Cost));
		Assert.Equal(session.UsageBreakdown(), (stats.Input, stats.Output, stats.CacheRead, stats.CacheWrite));
		Assert.Equal(1020, initial.Tokens); // Old snapshots remain stable.
	}

	[Fact]
	public void NavigationKeepsFileTotalsButContextAndProjectionInvalidate()
	{
		var manager = SessionManager.InMemory(Environment.CurrentDirectory);
		var first = manager.AppendMessage(new UserMessage { Content = MessageContent.FromText("first") });
		manager.AppendMessage(Assistant(100, 800, 100));
		using var session = Create(manager);
		var original = manager.BuildProjectedMessages();
		Assert.Same(original, manager.BuildProjectedMessages());
		Assert.Same(manager.Entries, manager.Entries);
		var total = session.UsageTotals();
		Assert.Equal(1020, session.ContextTokens().Tokens);
		session.NavigateTo(first);
		Assert.Equal(total, session.UsageTotals());
		Assert.NotSame(original, manager.BuildProjectedMessages());
		Assert.Equal(2, session.ContextTokens().Tokens);
		manager.ResetLeaf();
		Assert.Empty(manager.BuildProjectedMessages());
		Assert.Equal(0, session.ContextTokens().Tokens);
		Assert.Equal(2, original.Count);
	}

	[Fact]
	public void ContextEditsAndCompactionInvalidateWithoutLosingTotals()
	{
		var manager = SessionManager.InMemory(Environment.CurrentDirectory);
		var user = manager.AppendMessage(new UserMessage { Content = MessageContent.FromText("before") });
		var assistant = manager.AppendMessage(Assistant(100, 800, 100));
		using var session = Create(manager);
		Assert.Equal(1020, session.ContextTokens().Tokens);
		manager.AppendContextEdit(assistant, null);
		manager.AppendContextEdit(user, MessageContent.FromText("changed content"));
		Assert.Equal("changed content", Assert.IsType<UserMessage>(Assert.Single(manager.BuildProjectedMessages())).Content.Text);
		Assert.Equal(4, session.ContextTokens().Tokens);
		var kept = manager.AppendMessage(new UserMessage { Content = MessageContent.FromText("keep") });
		manager.AppendCompaction("summary", kept, 1000);
		Assert.Contains(manager.BuildProjectedMessages(), message => message is UserMessage u && (u.Content.Text?.Contains("summary") ?? false));
		Assert.Equal(1020, session.UsageTotals().Tokens);
	}

	[Fact]
	public void StartNewAndSwitchingManagersResetAllStatistics()
	{
		var manager = SessionManager.InMemory(Environment.CurrentDirectory);
		manager.AppendMessage(Assistant(100, 800, 100));
		using var session = Create(manager);
		Assert.Equal(1020, session.Statistics().Tokens);
		manager.StartNew();
		Assert.Empty(manager.BuildProjectedMessages());
		Assert.Equal(0, session.Statistics().Tokens);
		manager.AppendMessage(Assistant(1, 0, 0));
		Assert.Equal(21, session.Statistics().Tokens);
		session.SwitchSession(SessionManager.InMemory(manager.Cwd));
		Assert.Equal(0, session.Statistics().Tokens);
		Assert.Null(session.Statistics().AverageCacheHitPercent);
	}

	[Fact]
	public void PersistedForkRestoresTimingAndOnlyCountsRetainedEntries()
	{
		var root = Path.Combine(Path.GetTempPath(), "pi-statistics-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		try
		{
			var manager = SessionManager.Create(root, root);
			manager.AppendMessage(new UserMessage());
			var retained = manager.AppendMessage(Assistant(100, 800, 100, 1100));
			manager.AppendMessage(Assistant(900, 100, 0, 2100));
			using var session = Create(manager);
			Assert.Equal(2040, session.Statistics().Tokens);
			manager.CreateBranchedSession(retained);
			Assert.Equal(1020, session.Statistics().Tokens);
			var restored = SessionManager.Open(manager.SessionFile!);
			using var reopened = Create(restored);
			Assert.Equal(100, reopened.Statistics().LatestReply!.FirstTokenMs);
			Assert.Equal(20, reopened.Statistics().LatestReply!.TokensPerSecond);
			Assert.Equal(20_000.0 / 1100, reopened.Statistics().LatestReply!.EndToEndTokensPerSecond);
		}
		finally { Directory.Delete(root, true); }
	}

	[Fact]
	public async Task ConcurrentAppendsAndQueriesDoNotCacheAnIncompleteRevision()
	{
		var manager = SessionManager.InMemory(Environment.CurrentDirectory);
		using var session = Create(manager);
		var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var writer = Task.Run(async () =>
		{
			await started.Task;
			for (var i = 0; i < 1000; i++) manager.AppendMessage(Assistant(100, 800, 100));
		});
		started.SetResult();
		while (!writer.IsCompleted)
		{
			var stats = session.Statistics();
			Assert.Equal(stats.AssistantMessages * 1020L, stats.Tokens);
			Assert.Equal(stats.AssistantMessages * 100L, stats.Input);
			_ = session.ContextTokens();
			await Task.Yield();
		}
		await writer;
		Assert.Equal(1_020_000, session.Statistics().Tokens);
		Assert.Equal(1000, session.Statistics().CacheHitSamples);
	}

	[Theory]
	[InlineData(0)]
	[InlineData(-1)]
	[InlineData(double.NaN)]
	[InlineData(double.PositiveInfinity)]
	public void ThroughputDoesNotInventSpeeds(double duration)
	{
		Assert.Null(new ReplyPerformance(null, duration, duration, 10).TokensPerSecond);
		Assert.Null(new ReplyPerformance(null, duration, duration, 10).EndToEndTokensPerSecond);
	}

	[Fact]
	public void SessionAverageSpeedDividesByTheUnionOfActiveWallClock()
	{
		var manager = SessionManager.InMemory(Environment.CurrentDirectory);
		manager.AppendMessage(new UserMessage());
		// 两条回复的区间完全重叠（分支切换、并行标签）：墙钟只能算一次。
		manager.AppendMessage(Assistant(0, 0, 0, duration: 1000) with { Timestamp = 1_000, Usage = OutputOnly(100) });
		manager.AppendMessage(Assistant(0, 0, 0, duration: 1000) with { Timestamp = 1_000, Usage = OutputOnly(100) });
		using var session = Create(manager);
		var stats = session.Statistics();

		// 活跃区间并集是 1000 ms，输出共 200 token。
		Assert.Equal(1000, stats.SessionDurationMs);
		Assert.Equal(900, stats.SessionStreamingMs);
		Assert.Equal(200, stats.SessionStreamingOutput);
		Assert.Equal(200, stats.SessionEndToEndTokensPerSecond!.Value, 6);
		Assert.Equal(200.0 / 0.9, stats.SessionStreamingTokensPerSecond!.Value, 6);
	}

	[Fact]
	public void OutOfOrderRepliesStillProduceTheExactUnion()
	{
		var manager = SessionManager.InMemory(Environment.CurrentDirectory);
		manager.AppendMessage(new UserMessage());
		// 先一条晚的，再一条早的（跨分支合并、时钟回拨都可能出现）。
		// 水位线增量会把这 500 ms 吞掉（低估 → 均速虚高）；排序并集必须给出精确的 1500 ms。
		manager.AppendMessage(Assistant(0, 0, 0, duration: 1000) with { Timestamp = 10_000, Usage = OutputOnly(100) });
		manager.AppendMessage(Assistant(0, 0, 0, duration: 500) with { Timestamp = 1_000, Usage = OutputOnly(50) });
		using var session = Create(manager);

		var stats = session.Statistics();
		// 端到端：[10000,11000] + [1000,1500] → 1500。
		Assert.Equal(1500, stats.SessionDurationMs);
		// 流式：[10100,11000] + [1100,1500] → 900 + 400 = 1300。
		Assert.Equal(1300, stats.SessionStreamingMs);
		Assert.True(stats.SessionEndToEndTokensPerSecond is > 0,
			$"均速不能是负数或零：{stats.SessionEndToEndTokensPerSecond}");
	}

	[Fact]
	public void PartiallyOverlappingRepliesCountTheOverlapOnce()
	{
		var manager = SessionManager.InMemory(Environment.CurrentDirectory);
		manager.AppendMessage(new UserMessage());
		// 并发/派生任务：两条回复部分重叠（[1000,3000] 与 [2000,4000]），并集是 3000 而不是 4000。
		manager.AppendMessage(Assistant(0, 0, 0, duration: 2000) with { Timestamp = 1_000, Usage = OutputOnly(100) });
		manager.AppendMessage(Assistant(0, 0, 0, duration: 2000) with { Timestamp = 2_000, Usage = OutputOnly(100) });
		// 转精确模式后再追加的区间也要算进去（排序是就地的，追加落在已排序前缀之后）。
		manager.AppendMessage(Assistant(0, 0, 0, duration: 1000) with { Timestamp = 5_000, Usage = OutputOnly(100) });
		using var session = Create(manager);

		Assert.Equal(4000, session.Statistics().SessionDurationMs);
	}

	[Fact]
	public void SessionAverageSpeedSkipsSamplesWithoutTimestampsOrDurations()
	{
		var manager = SessionManager.InMemory(Environment.CurrentDirectory);
		manager.AppendMessage(new UserMessage());
		// Timestamp 为 0（旧记录）时不参与均速：否则区间从 1970 起算。
		manager.AppendMessage(Assistant(0, 0, 0, duration: 1000));
		// 没有耗时也不参与。
		manager.AppendMessage(Assistant(0, 0, 0) with { Timestamp = 5_000 });
		using var session = Create(manager);
		var stats = session.Statistics();

		Assert.Equal(0, stats.SessionDurationMs);
		Assert.Null(stats.SessionEndToEndTokensPerSecond);
		Assert.Null(stats.SessionStreamingTokensPerSecond);
	}

	private static Usage OutputOnly(long output) => new()
	{
		Input = 0, Output = output, CacheRead = 0, CacheWrite = 0, TotalTokens = output,
		Cost = new UsageCost { Total = 0 },
	};
}
