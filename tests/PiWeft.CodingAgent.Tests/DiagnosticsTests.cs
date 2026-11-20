using System.Text.Json.Nodes;
using PiWeft.Ai;
using PiWeft.CodingAgent.Session;
using Xunit;

namespace PiWeft.CodingAgent.Tests;

public class AssistantDiagnosticsTests
{
	[Fact]
	public void OpenAiReasoningDropsAreSummarized()
	{
		var message = Assistant(new AssistantDiagnostic
		{
			Type = "openai_input_transformations",
			Timestamp = 1,
			Details = Transformations(
				("reasoning", "prefix_mismatch"),
				("reasoning", "prefix_mismatch"),
				("reasoning", "organization_mismatch")),
		});

		var dropped = AssistantDiagnostics.DroppedReasoningOf(message.Diagnostics);

		Assert.NotNull(dropped);
		Assert.Equal("OpenAI", dropped.Provider);
		Assert.Equal(3, dropped.Count);
		Assert.Equal(["prefix_mismatch", "organization_mismatch"], dropped.Reasons);
		Assert.Equal(
			"OpenAI dropped 3 reasoning items: prefix_mismatch, organization_mismatch (details in session)",
			dropped.Notice);
	}

	[Fact]
	public void AnthropicThinkingDropsUseTheirOwnNoun()
	{
		var message = Assistant(new AssistantDiagnostic
		{
			Type = "anthropic_input_transformations",
			Timestamp = 1,
			Details = Transformations(("thinking_dropped", "prefix_binding_mismatch")),
		});

		var dropped = AssistantDiagnostics.DroppedReasoningOf(message.Diagnostics);

		Assert.NotNull(dropped);
		Assert.Equal("Anthropic dropped 1 thinking block: prefix_binding_mismatch (details in session)", dropped.Notice);
	}

	[Fact]
	public void MalformedDiagnosticsNeverThrow()
	{
		// 会话文件可能被外部工具写坏：字段类型不对时按「没有诊断」处理，不能把界面投影打崩。
		var message = Assistant(new AssistantDiagnostic
		{
			Type = "openai_input_transformations",
			Timestamp = 1,
			Details = new JsonObject
			{
				["transformations"] = new JsonArray
				{
					(JsonNode?)new JsonObject { ["type"] = 42, ["reason"] = new JsonObject() },
					(JsonNode?)new JsonObject { ["type"] = "reasoning", ["reason"] = 13 },
					(JsonNode?)new JsonObject { ["type"] = "reasoning" },
				},
			},
		});

		var dropped = AssistantDiagnostics.DroppedReasoningOf(message.Diagnostics);

		// 第二、三条 type 有效：计数照算，坏原因与缺失原因都不进列表。
		Assert.NotNull(dropped);
		Assert.Equal(2, dropped.Count);
		Assert.Empty(dropped.Reasons);
	}

	[Fact]
	public void UnknownDiagnosticBeforeKnownOneIsSkipped()
	{
		var message = Assistant(
			new AssistantDiagnostic
			{
				Type = "some_future_diagnostic",
				Details = Transformations(("reasoning", "prefix_mismatch")),
			},
			new AssistantDiagnostic
			{
				Type = "openai_input_transformations",
				Details = Transformations(("reasoning", "prefix_mismatch")),
			});

		var dropped = AssistantDiagnostics.DroppedReasoningOf(message.Diagnostics);

		Assert.NotNull(dropped);
		Assert.Equal(1, dropped.Count);
	}

	[Fact]
	public void TransformationsThatAreNotAnArrayAreIgnored()
	{
		var message = Assistant(new AssistantDiagnostic
		{
			Type = "openai_input_transformations",
			Details = new JsonObject { ["transformations"] = new JsonObject() },
		});

		Assert.Null(AssistantDiagnostics.DroppedReasoningOf(message.Diagnostics));
	}

	[Fact]
	public void UnknownDiagnosticsProduceNoNotice()
	{
		var message = Assistant(new AssistantDiagnostic
		{
			Type = "some_future_diagnostic",
			Timestamp = 1,
			Details = Transformations(("reasoning", "prefix_mismatch")),
		});

		Assert.Null(AssistantDiagnostics.DroppedReasoningOf(message.Diagnostics));
		Assert.Null(AssistantDiagnostics.DroppedReasoningOf(null));
	}

	private static AssistantMessage Assistant(params AssistantDiagnostic[] diagnostics) => new()
	{
		Api = "openai-responses",
		Provider = "openai",
		Model = "gpt-6.2-sol",
		StopReason = StopReason.Stop,
		Diagnostics = diagnostics,
	};

	private static JsonObject Transformations(params (string Type, string Reason)[] entries)
	{
		var list = new JsonArray();
		foreach (var (type, reason) in entries)
		{
			list.Add((JsonNode?)new JsonObject { ["type"] = type, ["reason"] = reason });
		}

		return new JsonObject { ["transformations"] = list };
	}
}

public class CacheMissDetectorTests
{
	[Fact]
	public void MissedPromptIsReported()
	{
		var previous = Assistant(input: 90_000, cacheRead: 10_000, timestamp: 1_000);
		var current = Assistant(input: 100_000, cacheRead: 0, timestamp: 401_000);

		var miss = CacheMissDetector.Detect(previous, current);

		Assert.NotNull(miss);
		Assert.Equal(100_000, miss.MissedTokens);
		Assert.True(miss.IsNoteworthy);
		Assert.False(miss.ModelChanged);
		Assert.StartsWith("Cache miss after 7m idle: 100k tokens re-billed", miss.Notice);
	}

	[Fact]
	public void ProvidersThatNeverReportCachingAreIgnored()
	{
		var previous = Assistant(input: 90_000, cacheRead: 0, timestamp: 1_000);
		var current = Assistant(input: 120_000, cacheRead: 0, timestamp: 2_000);

		Assert.Null(CacheMissDetector.Detect(previous, current));
	}

	[Fact]
	public void SmallMissesStayBelowTheNoiseFloor()
	{
		var previous = Assistant(input: 900, cacheRead: 100, timestamp: 1_000);
		var current = Assistant(input: 1_000, cacheRead: 0, timestamp: 2_000);

		Assert.Null(CacheMissDetector.Detect(previous, current));
	}

	[Fact]
	public void ModelSwitchIsNamed()
	{
		var previous = Assistant(input: 90_000, cacheRead: 10_000, timestamp: 1_000);
		var current = Assistant(input: 100_000, cacheRead: 0, timestamp: 2_000) with { Model = "gpt-6.1-sol" };

		var miss = CacheMissDetector.Detect(previous, current);

		Assert.NotNull(miss);
		Assert.True(miss.ModelChanged);
		Assert.StartsWith("Cache miss after model switch", miss.Notice);
	}

	[Fact]
	public void CacheActivityStaysStickyAcrossConsecutiveMisses()
	{
		// 更早的请求报过缓存：之后连续两条都没报读写，仍算全未命中。
		var early = Assistant(input: 50_000, cacheRead: 10_000, timestamp: 1_000);
		var previous = Assistant(input: 100_000, cacheRead: 0, timestamp: 2_000);
		var current = Assistant(input: 100_000, cacheRead: 0, timestamp: 3_000);

		var miss = CacheMissDetector.Detect([early, previous, current]);

		Assert.NotNull(miss);
		Assert.Equal(100_000, miss.MissedTokens);
	}

	[Fact]
	public void CompactionResetsTheComparison()
	{
		var previous = Assistant(input: 90_000, cacheRead: 10_000, timestamp: 1_000);
		var current = Assistant(input: 100_000, cacheRead: 0, timestamp: 2_000);
		// 投影里的压缩摘要是一条带固定前缀的 user 消息，不是 CustomMessage。
		var summary = new UserMessage
		{
			Content = MessageContent.FromText(
				CodingMessages.CompactionSummaryPrefix + "x" + CodingMessages.CompactionSummarySuffix),
			Timestamp = 3_000,
		};

		Assert.Null(CacheMissDetector.Detect([previous, summary, current]));
		Assert.Null(CacheMissDetector.Detect([previous, new CustomMessage { CustomRole = "branchSummary" }, current]));
	}

	[Fact]
	public void CostComesFromTheMessagesOwnUsageBuckets()
	{
		// 上一条请求有 10k 缓存读，当前 100k 全部按输入价重算；
		// 多付的差额 = 100k * (输入价 - 缓存读价)，不是整额。
		var previous = Assistant(input: 90_000, cacheRead: 10_000, timestamp: 1_000);
		var current = Assistant(input: 100_000, cacheRead: 0, timestamp: 2_000) with
		{
			Usage = new Usage
			{
				Input = 100_000,
				TotalTokens = 100_000,
				Cost = new UsageCost { Input = 0.3, CacheRead = 0 },
			},
		};

		var miss = CacheMissDetector.Detect(previous, current);

		Assert.NotNull(miss);
		Assert.Equal(0.3, miss.MissedCost, 6);
		Assert.Contains("(~$0.30)", miss.Notice);
	}

	[Fact]
	public void NoiseFloorBoundaryIsInclusive()
	{
		// 1024 及以下不算未命中；1025 起算。
		Assert.Null(CacheMissDetector.Detect(
			Assistant(input: 1_024, cacheRead: 0, timestamp: 1_000),
			Assistant(input: 1_024, cacheRead: 0, timestamp: 2_000),
			reportedCache: true));
		Assert.NotNull(CacheMissDetector.Detect(
			Assistant(input: 1_025, cacheRead: 0, timestamp: 1_000),
			Assistant(input: 1_025, cacheRead: 0, timestamp: 2_000),
			reportedCache: true));
	}

	[Theory]
	[InlineData(999, "999")]
	[InlineData(1_000, "1.0k")]
	[InlineData(1_024, "1.0k")]
	[InlineData(9_999, "10.0k")]
	[InlineData(10_000, "10k")]
	[InlineData(999_999, "1000k")]
	[InlineData(1_500_000, "1.5M")]
	public void TokenAbbreviationMatchesPi(long tokens, string expected) =>
		Assert.Equal(expected, CacheMissDetector.FormatTokens(tokens));

	private static AssistantMessage Assistant(long input, long cacheRead, long timestamp) => new()
	{
		Api = "openai-responses",
		Provider = "openai",
		Model = "gpt-6.2-sol",
		StopReason = StopReason.Stop,
		Timestamp = timestamp,
		Usage = new Usage
		{
			Input = input,
			CacheRead = cacheRead,
			TotalTokens = input + cacheRead,
		},
	};
}
