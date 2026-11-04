using System.Globalization;
using System.Text.Json.Nodes;
using PiWeft.Ai;
using PiWeft.CodingAgent.Session;
using Xunit;

namespace PiWeft.Desktop.Tests;

public class TrajectoryTests
{
	private const long Base = 1_700_000_000_000;

	private static string Iso(long unixMs) =>
		DateTimeOffset.FromUnixTimeMilliseconds(unixMs).UtcDateTime
			.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

	private static SessionMessageEntry Entry(string id, long at, Message message) => new()
	{
		Id = id,
		Timestamp = Iso(at),
		Message = message,
	};

	private static UserMessage User(string text, long at) => new() { Content = text, Timestamp = at };

	private static AssistantMessage Assistant(long at, IReadOnlyList<ContentBlock> content,
		double? duration = null, StopReason stop = StopReason.Stop, Usage? usage = null) => new()
	{
		Timestamp = at,
		Content = content,
		DurationMs = duration,
		StopReason = stop,
		Usage = usage ?? new Usage(),
		Provider = "test",
		Model = "model",
		ThinkingLevel = "medium",
	};

	private static ToolCall Call(string id, string name, JsonObject arguments) => new()
	{
		Id = id,
		Name = name,
		Arguments = arguments,
	};

	private static ToolResultMessage Result(string callId, long at, string text, double? duration = null, bool isError = false) => new()
	{
		ToolCallId = callId,
		ToolName = "bash",
		Timestamp = at,
		Content = [TextContent.Of(text)],
		DurationMs = duration,
		IsError = isError,
	};

	[Fact]
	public void UserMessageOpensTurnAndLeadingProcessEntriesJoinIt()
	{
		var entries = new List<SessionEntry>
		{
			new ModelChangeEntry { Id = "model", Timestamp = Iso(Base - 1000), Provider = "wd2", ModelId = "flash" },
			new ThinkingLevelChangeEntry { Id = "level", Timestamp = Iso(Base - 900), ThinkingLevel = "max" },
			Entry("user", Base, User("下一步推荐什么？", Base)),
			Entry("assistant", Base + 1000, Assistant(Base + 1000, [TextContent.Of("好的")], duration: 250)),
		};

		var model = TrajectoryBuilder.Build(entries, [], Base + 5000);

		var turn = Assert.Single(model.Turns);
		Assert.Equal(Base, turn.StartedAt);
		Assert.Equal(4, turn.Records.Count);
		Assert.Equal(TrajectoryKind.ModelChange, turn.Records[0].Kind);
		Assert.Equal(TrajectoryKind.ThinkingChange, turn.Records[1].Kind);
		Assert.Equal(TrajectoryKind.User, turn.Records[2].Kind);
		Assert.True(turn.Records[2].IsInitialPrompt);
		Assert.Equal(TrajectoryKind.Assistant, turn.Records[3].Kind);
		Assert.Equal(250, turn.Records[3].DurationMs);
		Assert.Equal(4, model.Records.Count);
	}

	[Fact]
	public void ToolCallAndResultMergeIntoOneRecordWithMeasuredDuration()
	{
		var entries = new List<SessionEntry>
		{
			Entry("user", Base, User("查一下", Base)),
			Entry("assistant", Base + 100, Assistant(Base + 100,
				[
					new ThinkingContent { Thinking = "先看看目录" },
					TextContent.Of("我来查"),
					Call("c1", "bash", new JsonObject { ["command"] = "ls -la" }),
				],
				duration: 2000,
				stop: StopReason.ToolUse,
				usage: new Usage { Input = 1200, Output = 80, CacheRead = 400 })),
			Entry("result", Base + 3100, Result("c1", Base + 3100, "a.txt\nb.txt", duration: 950)),
		};

		var model = TrajectoryBuilder.Build(entries, [], Base + 6000);
		var turn = Assert.Single(model.Turns);
		Assert.Equal(new[]
		{
			TrajectoryKind.User, TrajectoryKind.Thinking, TrajectoryKind.Assistant, TrajectoryKind.Tool,
		}, turn.Records.Select(record => record.Kind));

		var tool = turn.Records[3];
		Assert.Equal("bash", tool.ToolName);
		Assert.Equal("c1", tool.ToolCallId);
		Assert.Equal("done", tool.Status);
		Assert.Equal(950, tool.DurationMs);
		Assert.Equal(Base + 2150, tool.StartedAt);
		Assert.Equal(Base + 3100, tool.EndedAt);
		Assert.Contains("ls -la", tool.Summary);

		var assistant = turn.Records[2];
		Assert.Equal(1200L, assistant.Usage!.Input);
		Assert.Equal(80L, assistant.Usage.Output);
		Assert.Equal("toolUse", assistant.Status);

		Assert.Equal(1, TrajectoryLedger.Summarize(turn).ToolCalls);
		Assert.Equal(1200L, TrajectoryLedger.Summarize(turn).InputTokens);
		Assert.Equal(80L, TrajectoryLedger.Summarize(turn).OutputTokens);
	}

	[Fact]
	public void PendingToolAndLiveAssistantStayInFlightWithoutFakeDurations()
	{
		var entries = new List<SessionEntry>
		{
			Entry("user", Base, User("跑测试", Base)),
			Entry("assistant", Base + 100, Assistant(Base + 100,
				[Call("c1", "bash", new JsonObject { ["command"] = "npm test" })],
				duration: 500,
				stop: StopReason.Pending)),
		};
		var live = new ChatEntry
		{
			Role = ChatRole.Assistant,
			Text = "正在收尾",
			Timestamp = Base + 5000,
			IsStreaming = true,
		};

		var model = TrajectoryBuilder.Build(entries, [live], Base + 8000, busy: true);

		var tool = Assert.Single(model.Records, record => record.Kind == TrajectoryKind.Tool);
		Assert.True(tool.InFlight);
		Assert.Null(tool.DurationMs);

		var assistant = Assert.Single(model.Records, record => record.Kind == TrajectoryKind.Assistant);
		Assert.True(assistant.InFlight);
		Assert.Equal("正在收尾", assistant.Summary);
		Assert.True(Assert.Single(model.Turns).InFlight);
		Assert.Equal(Base + 8000, model.DomainEnd);
	}

	[Fact]
	public void IdleSessionClosesDanglingToolAsInterrupted()
	{
		var entries = new List<SessionEntry>
		{
			Entry("user", Base, User("跑测试", Base)),
			Entry("assistant", Base + 100, Assistant(Base + 100,
				[Call("c1", "bash", new JsonObject { ["command"] = "npm test" })],
				duration: 500,
				stop: StopReason.ToolUse)),
		};

		var model = TrajectoryBuilder.Build(entries, [], Base + 8000);
		var tool = Assert.Single(model.Records, record => record.Kind == TrajectoryKind.Tool);
		Assert.False(tool.InFlight);
		Assert.Equal("stopped", tool.Status);
		Assert.Null(tool.DurationMs);
		Assert.False(Assert.Single(model.Turns).InFlight);
	}

	[Fact]
	public void LiveTailDoesNotDuplicatePersistedAssistantAtSameTimestamp()
	{
		var entries = new List<SessionEntry>
		{
			Entry("user", Base, User("继续", Base)),
			Entry("assistant", Base + 100, Assistant(Base + 100, [TextContent.Of("已写完")], duration: 100)),
		};

		var model = TrajectoryBuilder.Build(entries,
			[new ChatEntry { Role = ChatRole.Assistant, Text = "已写完", Timestamp = Base + 100, IsStreaming = true }],
			Base + 1000);

		Assert.Single(model.Records, record => record.Kind == TrajectoryKind.Assistant);
	}

	[Fact]
	public void IncrementalProjectionMatchesFullBuildAcrossAppendsAndLiveTail()
	{
		var entries = new List<SessionEntry>
		{
			Entry("system-1", Base - 2000, new SystemMessage { Timestamp = Base - 2000, Content = "你是测试助手。" }),
			new ModelChangeEntry { Id = "model", Timestamp = Iso(Base - 1000), Provider = "wd2", ModelId = "flash" },
			Entry("user-1", Base, User("第一轮", Base)),
			Entry("assistant-1", Base + 100, Assistant(Base + 100,
				[
					new ThinkingContent { Thinking = "先看现状" },
					TextContent.Of("我来查"),
					Call("c1", "bash", new JsonObject { ["command"] = "ls" }),
				],
				duration: 500,
				stop: StopReason.ToolUse,
				usage: new Usage { Input = 1000, Output = 80, CacheRead = 200 })),
			Entry("result-1", Base + 700, Result("c1", Base + 700, "ok", duration: 200)),
		};

		var projector = new TrajectoryProjector();
		AssertSameModel(
			TrajectoryBuilder.Build([.. entries], [], Base + 2000, busy: true),
			projector.Project([.. entries], [], Base + 2000, busy: true));

		entries.Add(Entry("assistant-2", Base + 800, Assistant(Base + 800,
			[TextContent.Of("第一轮完成")], duration: 300, stop: StopReason.Stop,
			usage: new Usage { Input = 300, Output = 60 })));
		AssertSameModel(
			TrajectoryBuilder.Build([.. entries], [], Base + 2000, busy: true),
			projector.Project([.. entries], [], Base + 2000, busy: true));

		entries.Add(Entry("user-2", Base + 5000, User("第二轮", Base + 5000)));
		var afterSecondUser = projector.Project([.. entries], [], Base + 5500, busy: true);
		AssertSameModel(
			TrajectoryBuilder.Build([.. entries], [], Base + 5500, busy: true),
			afterSecondUser);

		entries.Add(Entry("assistant-3", Base + 5100, Assistant(Base + 5100,
			[Call("c2", "read", new JsonObject { ["path"] = "a.cs" })],
			duration: 400, stop: StopReason.ToolUse,
			usage: new Usage { Input = 500, Output = 40 })));
		var afterTailAppend = projector.Project([.. entries], [], Base + 6000, busy: true);
		AssertSameModel(
			TrajectoryBuilder.Build([.. entries], [], Base + 6000, busy: true),
			afterTailAppend);
		// 增量路径真的复用了已收口的第一轮（而不是重新构建了整份会话）。
		Assert.Same(afterSecondUser.Turns[0], afterTailAppend.Turns[0]);

		entries.Add(Entry("result-2", Base + 5600, Result("c2", Base + 5600, "file", duration: 100)));
		var live = new ChatEntry
		{
			Role = ChatRole.Assistant,
			Text = "正在收尾",
			Timestamp = Base + 6000,
			IsStreaming = true,
		};
		AssertSameModel(
			TrajectoryBuilder.Build([.. entries], [live], Base + 7000, busy: true),
			projector.Project([.. entries], [live], Base + 7000, busy: true));

		// 收口：live 行消失、会话空闲，最后一条挂着的工具要标成已中断，且复用轮不能重复收口出错。
		AssertSameModel(
			TrajectoryBuilder.Build([.. entries], [], Base + 8000, busy: false),
			projector.Project([.. entries], [], Base + 8000, busy: false));

		// 分支切换/截断：不是 append-only，投影器必须回退全量。
		var rewound = entries.Take(3).ToList();
		AssertSameModel(
			TrajectoryBuilder.Build(rewound, [], Base + 9000, busy: false),
			projector.Project(rewound, [], Base + 9000, busy: false));
	}

	[Fact]
	public void IncrementalProjectionRebuildsLateToolResultForEarlierTurn()
	{
		var entries = new List<SessionEntry>
		{
			Entry("user-1", Base, User("第一轮", Base)),
			Entry("assistant-1", Base + 100, Assistant(Base + 100,
				[Call("late", "bash", new JsonObject { ["command"] = "sleep 1" })],
				duration: 100, stop: StopReason.ToolUse)),
			Entry("user-2", Base + 2000, User("第二轮", Base + 2000)),
		};

		var projector = new TrajectoryProjector();
		projector.Project([.. entries], [], Base + 3000, busy: true);
		entries.Add(Entry("late-result", Base + 4000, Result("late", Base + 4000, "done", duration: 100)));
		AssertSameModel(
			TrajectoryBuilder.Build([.. entries], [], Base + 5000, busy: false),
			projector.Project([.. entries], [], Base + 5000, busy: false));
	}

	private static void AssertSameModel(TrajectoryModel expected, TrajectoryModel actual)
	{
		Assert.Equal(expected.Turns.Count, actual.Turns.Count);
		for (var index = 0; index < expected.Turns.Count; index++)
		{
			var left = expected.Turns[index];
			var right = actual.Turns[index];
			Assert.Equal(left.Index, right.Index);
			Assert.Equal(left.SourceStartIndex, right.SourceStartIndex);
			Assert.Equal(left.ToolCalls, right.ToolCalls);
			Assert.Equal(left.HasUser, right.HasUser);
			Assert.Equal(left.HasSystemPrompt, right.HasSystemPrompt);
			Assert.Equal(left.StartedAt, right.StartedAt);
			Assert.Equal(left.EndedAt, right.EndedAt);
			Assert.Equal(left.InFlight, right.InFlight);
			Assert.Equal(left.DurationMs, right.DurationMs);
			Assert.Equal(left.InputTokens, right.InputTokens);
			Assert.Equal(left.OutputTokens, right.OutputTokens);
			Assert.Equal(left.ReasoningTokens, right.ReasoningTokens);
			Assert.Equal(left.Records.Count, right.Records.Count);
			for (var recordIndex = 0; recordIndex < left.Records.Count; recordIndex++)
			{
				var expectedRecord = left.Records[recordIndex];
				var actualRecord = right.Records[recordIndex];
				Assert.Equal(expectedRecord.Id, actualRecord.Id);
				Assert.Equal(expectedRecord.Kind, actualRecord.Kind);
				Assert.Equal(expectedRecord.TurnIndex, actualRecord.TurnIndex);
				Assert.Equal(expectedRecord.Summary, actualRecord.Summary);
				Assert.Equal(expectedRecord.StartedAt, actualRecord.StartedAt);
				Assert.Equal(expectedRecord.EndedAt, actualRecord.EndedAt);
				Assert.Equal(expectedRecord.DurationMs, actualRecord.DurationMs);
				Assert.Equal(expectedRecord.Status, actualRecord.Status);
				Assert.Equal(expectedRecord.IsError, actualRecord.IsError);
				Assert.Equal(expectedRecord.IsInitialPrompt, actualRecord.IsInitialPrompt);
				Assert.Equal(expectedRecord.Detail, actualRecord.Detail);
				Assert.Equal(expectedRecord.ToolName, actualRecord.ToolName);
				Assert.Equal(expectedRecord.ToolCallId, actualRecord.ToolCallId);
				Assert.Equal(expectedRecord.Provider, actualRecord.Provider);
				Assert.Equal(expectedRecord.ModelId, actualRecord.ModelId);
				Assert.Equal(expectedRecord.ThinkingLevel, actualRecord.ThinkingLevel);
				Assert.Equal(expectedRecord.CustomType, actualRecord.CustomType);
				Assert.Equal(expectedRecord.TokensBefore, actualRecord.TokensBefore);
				Assert.Equal(expectedRecord.Usage?.Input, actualRecord.Usage?.Input);
				Assert.Equal(expectedRecord.Usage?.Output, actualRecord.Usage?.Output);
				Assert.Equal(expectedRecord.Usage?.Reasoning, actualRecord.Usage?.Reasoning);
			}
		}

		Assert.Equal(expected.Records.Count, actual.Records.Count);
		for (var index = 0; index < expected.Records.Count; index++)
		{
			Assert.Equal(expected.Records[index].Id, actual.Records[index].Id);
			Assert.Equal(expected.Records[index].TurnIndex, actual.Records[index].TurnIndex);
		}

		Assert.Equal(expected.DomainStart, actual.DomainStart);
		Assert.Equal(expected.DomainEnd, actual.DomainEnd);
	}

	[Fact]
	public void ThinkingRecordUsesIndependentStartAndEndWhenKnown()
	{
		var assistantAt = Base + 100;
		var entries = new List<SessionEntry>
		{
			Entry("user", Base, User("检查思考耗时", Base)),
			Entry("assistant", assistantAt, Assistant(assistantAt,
				[new ThinkingContent { Thinking = "先分析一下" }, TextContent.Of("回答")],
				duration: 2000, stop: StopReason.Stop)),
		};
		var timings = new Dictionary<long, ChatThinkingTiming>
		{
			[assistantAt] = new(assistantAt + 250, assistantAt + 1250),
		};

		var model = TrajectoryBuilder.Build(entries, [], Base + 5000, busy: false, timings);
		var thinking = Assert.Single(model.Records, record => record.Kind == TrajectoryKind.Thinking);
		Assert.Equal(assistantAt + 250, thinking.StartedAt);
		Assert.Equal(assistantAt + 1250, thinking.EndedAt);
		Assert.Equal(1000, thinking.DurationMs);
	}

	[Fact]
	public void LiveThinkingRecordUsesEntryTiming()
	{
		var live = new ChatEntry
		{
			Role = ChatRole.Assistant,
			Text = "回答",
			Thinking = "先分析一下",
			IsStreaming = true,
			IsThinking = false,
			Timestamp = Base + 100,
			ThinkingStartedAt = Base + 220,
			ThinkingEndedAt = Base + 920,
		};

		var model = TrajectoryBuilder.Build([], [live], Base + 2000, busy: true);
		var thinking = Assert.Single(model.Records, record => record.Kind == TrajectoryKind.Thinking);
		Assert.Equal(Base + 220, thinking.StartedAt);
		Assert.Equal(Base + 920, thinking.EndedAt);
		Assert.Equal(700, thinking.DurationMs);
		Assert.True(Assert.Single(model.Records, record => record.Kind == TrajectoryKind.Assistant).InFlight);
	}

	[Fact]
	public void SummaryLimitHoldsForMultiBlockAndNewlineHeavyText()
	{
		var blocks = new ContentBlock[]
		{
			TextContent.Of("first"),
			TextContent.Of(new string('b', 500)),
		};

		var summary = TrajectoryText.BlocksSummary(blocks, 96);
		Assert.Equal(96, summary.Length);
		Assert.StartsWith("first", summary, StringComparison.Ordinal);

		var newlines = TrajectoryText.ThinkingSummary(
			[new ThinkingContent { Thinking = new string('\n', 300) + new string('c', 200) }], 96);
		Assert.Equal(96, newlines.Length);
		Assert.StartsWith("ccc", newlines, StringComparison.Ordinal);
	}

	[Fact]
	public void SummaryDoesNotSplitSurrogatePairs()
	{
		var text = new string('a', 94) + "😀" + new string('b', 20);
		var summary = TrajectoryText.Summarize(text, 96);

		Assert.True(summary.Length <= 96);
		for (var index = 0; index < summary.Length; index++)
		{
			if (!char.IsSurrogate(summary[index]))
			{
				continue;
			}

			Assert.True(index + 1 < summary.Length && char.IsHighSurrogate(summary[index]) &&
				char.IsLowSurrogate(summary[index + 1]));
			index++;
		}
	}

	[Fact]
	public void FoldCollapsesOnlyLongConsecutiveToolRuns()
	{
		var records = new List<TrajectoryRecord>();
		for (var index = 0; index < 9; index++)
		{
			records.Add(ToolRecord($"tool-{index}"));
		}

		var folded = TrajectoryLedger.Fold(records);
		var group = Assert.IsType<TrajectoryLedgerItem.Group>(Assert.Single(folded));
		Assert.Equal(9, group.Records.Count);

		// 工具之间隔着思考/回复时不合并：读者要靠这些行看清模型的动作顺序。
		var mixed = new List<TrajectoryRecord>();
		for (var index = 0; index < 9; index++)
		{
			mixed.Add(ToolRecord($"tool-{index}"));
			if (index is 3 or 6)
			{
				mixed.Add(new TrajectoryRecord
				{
					Id = $"thinking-{index}",
					Kind = TrajectoryKind.Thinking,
					Lane = TrajectoryLane.Model,
					Summary = "plan",
					StartedAt = Base + index,
					EndedAt = Base + index,
				});
			}
		}

		var items = TrajectoryLedger.Fold(mixed);
		Assert.DoesNotContain(items, static item => item is TrajectoryLedgerItem.Group);
		Assert.Equal(11, items.Count);
	}

	[Fact]
	public void WindowKeepsTailTurnsAndDefaultFoldLeavesLastTurnOpen()
	{
		var turns = Enumerable.Range(0, 6)
			.Select(index => new TrajectoryTurn(index, Base + index, Base + index, false, 0,
				[UserRecord($"u{index}", Base + index)]))
			.ToList();

		var window = TrajectoryLedger.Window(turns, 3);
		Assert.Equal(new[] { 3, 4, 5 }, window.Select(turn => turn.Index));

		Assert.Equal(new[] { 0, 1, 2, 3, 4 }, TrajectoryLedger.DefaultFoldedTurns(turns).OrderBy(index => index));
		Assert.Equal(6, TrajectoryLedger.Window(turns, 99).Count);
	}

	[Theory]
	[InlineData(999.4, "999ms")]
	[InlineData(1500, "1.5s")]
	[InlineData(33_000, "33.0s")]
	[InlineData(272_000, "4m32s")]
	[InlineData(87_896_000, "24h24m56s")]
	public void DurationKeepsSecondPrecisionForLongTurns(double milliseconds, string expected) =>
		Assert.Equal(expected, TrajectoryFormat.Duration(milliseconds));

	[Fact]
	public void DurationSharesTheStepRowReading()
	{
		// 轨迹与步骤行必须同一口径，否则同一段耗时在两个面板里会写成两种样子。
		Assert.Equal(Strings.FormatDuration(59_999), TrajectoryFormat.Duration(59_999));
		Assert.Equal(Strings.FormatDuration(64_000), TrajectoryFormat.Duration(64_000));
		// 轨迹耗时来自会话文件，缺失或异常时也由同一个函数兜底（NaN/负数按 0 排版）。
		Assert.Equal(Strings.FormatDuration(0), TrajectoryFormat.Duration(double.NaN));
		Assert.Equal(Strings.FormatDuration(0), TrajectoryFormat.Duration(-1));
	}

	[Fact]
	public void CompactTokensMatchesLedgerColumns()
	{
		Assert.Equal("265", TrajectoryFormat.CompactTokens(265));
		Assert.Equal("2.0k", TrajectoryFormat.CompactTokens(2048));
		Assert.Equal("12k", TrajectoryFormat.CompactTokens(12_345));
	}

	private static TrajectoryRecord ToolRecord(string id) => new()
	{
		Id = id,
		Kind = TrajectoryKind.Tool,
		Lane = TrajectoryLane.Tools,
		Summary = "ls",
		StartedAt = Base,
		EndedAt = Base + 1,
		Status = "done",
	};

	private static TrajectoryRecord UserRecord(string id, long at) => new()
	{
		Id = id,
		Kind = TrajectoryKind.User,
		Lane = TrajectoryLane.Input,
		Summary = id,
		StartedAt = at,
		EndedAt = at,
	};
}
