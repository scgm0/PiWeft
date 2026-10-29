using PiWeft.Ai;
using Xunit;

namespace PiWeft.Ai.Tests;

public class ModelTests
{
	private static readonly ModelRegistry Registry = ModelRegistry.LoadDefault();

	[Fact]
	public void DefaultCatalog_LoadsRealModels()
	{
		Assert.True(Registry.Count > 1000, $"expected the full catalog, got {Registry.Count} models");
		Assert.Contains("anthropic", Registry.Providers);
		Assert.Contains("openai", Registry.Providers);
	}

	[Fact]
	public void AnthropicCatalog_ExposesChatModels()
	{
		var models = Registry.ChatModels("anthropic");
		Assert.NotEmpty(models);
		Assert.All(models, m => Assert.Equal("anthropic-messages", m.Api));

		var sonnet = models.FirstOrDefault(m => m.Id.Contains("sonnet", StringComparison.Ordinal));
		Assert.NotNull(sonnet);
		Assert.True(sonnet!.ContextWindow > 0);
		Assert.True(sonnet.MaxTokens > 0);
	}

	[Fact]
	public void DeepSeekCatalogEntryDeclaresTheThinkingSwitch()
	{
		// 目录的 compat.thinkingFormat 决定请求里怎么开关思考；读不到它，DeepSeek 就会一直按默认开启思考。
		var model = Registry.Get("deepseek", "deepseek-flash");

		Assert.NotNull(model);
		Assert.Equal("deepseek", model!.Compat?["thinkingFormat"]?.GetValue<string>());
	}

	[Fact]
	public void CatalogPromptCacheLifetimesAreLoaded()
	{
		var model = Registry.Resolve("anthropic", "claude-fable-5");

		Assert.NotNull(model);
		Assert.Equal(300, model!.PromptCache!["short"]);
		Assert.Equal(3600, model.PromptCache["long"]);
	}

	[Fact]
	public void CostCalculator_AppliesPerMillionRates()
	{
		var model = new Model
		{
			Id = "test",
			Provider = "test",
			Cost = new ModelCost { Input = 3, Output = 15, CacheRead = 0.3, CacheWrite = 3.75 },
		};

		var usage = new Usage { Input = 1_000_000, Output = 1_000_000, CacheRead = 1_000_000, CacheWrite = 1_000_000 };
		var cost = CostCalculator.Calculate(model, usage).Cost;

		Assert.Equal(3.0, cost.Input, 6);
		Assert.Equal(15.0, cost.Output, 6);
		Assert.Equal(0.3, cost.CacheRead, 6);
		Assert.Equal(3.75, cost.CacheWrite, 6);
		Assert.Equal(3.0 + 15.0 + 0.3 + 3.75, cost.Total, 6);
	}

	[Fact]
	public void CostCalculator_AppliesHighestMatchingTier()
	{
		var model = new Model
		{
			Id = "tiered",
			Provider = "test",
			Cost = new ModelCost
			{
				Input = 3,
				Output = 15,
				Tiers = [new ModelCostTier { InputTokensAbove = 200_000, Input = 6, Output = 22.5 }],
			},
		};

		var below = CostCalculator.Calculate(model, new Usage { Input = 199_999 }).Cost;
		Assert.Equal(3 * 199_999 / 1_000_000.0, below.Input, 6);

		// Crossing the threshold re-rates the whole request, not just the excess.
		var above = CostCalculator.Calculate(model, new Usage { Input = 200_001, Output = 1_000_000 }).Cost;
		Assert.Equal(6 * 200_001 / 1_000_000.0, above.Input, 6);
		Assert.Equal(22.5, above.Output, 6);
	}

	[Fact]
	public void CostCalculator_ChargesDoubleInputForOneHourCacheWrites()
	{
		var model = new Model
		{
			Id = "anthropic-ish",
			Provider = "test",
			Cost = new ModelCost { Input = 10, CacheWrite = 1 },
		};

		var usage = new Usage { CacheWrite = 1_000_000, CacheWrite1h = 1_000_000 };
		var cost = CostCalculator.Calculate(model, usage).Cost;

		// All of it is a 1h write: charged at 2x base input (20/M), not the 1h rate.
		Assert.Equal(20.0, cost.CacheWrite, 6);
	}

	[Fact]
	public void ThinkingLevels_NonReasoningModelOnlySupportsOff()
	{
		var model = new Model { Id = "plain", Provider = "test", Reasoning = false };
		Assert.Equal([ThinkingLevels.Off], ThinkingLevelResolver.GetSupported(model));
	}

	[Fact]
	public void ThinkingLevels_ClampPicksNearestSupported()
	{
		// Levels absent from the map stay supported; an explicit null removes one; xhigh/max need an explicit value.
		var model = new Model
		{
			Id = "reasoner",
			Provider = "test",
			Reasoning = true,
			ThinkingLevelMap = new Dictionary<string, string?> { ["low"] = "low", ["high"] = "high" },
		};

		Assert.Equal(
			[ThinkingLevels.Off, ThinkingLevels.Minimal, ThinkingLevels.Low, ThinkingLevels.Medium, ThinkingLevels.High],
			ThinkingLevelResolver.GetSupported(model));
		Assert.Equal(ThinkingLevels.Minimal, ThinkingLevelResolver.Clamp(model, ThinkingLevels.Minimal));
		Assert.Equal(ThinkingLevels.Medium, ThinkingLevelResolver.Clamp(model, ThinkingLevels.Medium));
		Assert.Equal(ThinkingLevels.High, ThinkingLevelResolver.Clamp(model, "max"));

		var onlyHigh = model with
		{
			ThinkingLevelMap = new Dictionary<string, string?>
			{
				["minimal"] = null,
				["low"] = null,
				["medium"] = null,
				["high"] = "high",
			},
		};

		Assert.Equal([ThinkingLevels.Off, ThinkingLevels.High], ThinkingLevelResolver.GetSupported(onlyHigh));
		Assert.Equal(ThinkingLevels.High, ThinkingLevelResolver.Clamp(onlyHigh, ThinkingLevels.Low));
		Assert.Equal(ThinkingLevels.Off, ThinkingLevelResolver.Clamp(onlyHigh, ThinkingLevels.Off));
	}
}

/// <summary>
/// 价格的负占位值：OpenRouter 对路由模型（`openrouter/auto`）报 -1，pi 一律当 0。
/// 界面上显示 -$1,000,000/1M 会让人以为计费坏了。
/// </summary>
public class NegativeCostTests
{
	[Fact]
	public void NegativeRatesAreClampedToZero()
	{
		var model = new Model
		{
			Id = "openrouter/auto",
			Provider = "openrouter",
			Name = "Auto",
			Cost = new ModelCost { Input = -1_000_000, Output = -1_000_000 },
		};

		var sanitized = ModelRegistry.SanitizeCost(model);

		Assert.Equal(0, sanitized.Cost.Input);
		Assert.Equal(0, sanitized.Cost.Output);
	}

	[Fact]
	public void NormalRatesAreLeftAlone()
	{
		var model = new Model
		{
			Id = "gpt-test",
			Provider = "openai",
			Name = "Test",
			Cost = new ModelCost { Input = 0.3, Output = 1.2, CacheRead = 0.03 },
		};

		Assert.Same(model, ModelRegistry.SanitizeCost(model));
	}

	[Fact]
	public void EmbeddedCatalogHasNoNegativePrices()
	{
		var registry = ModelRegistry.LoadDefault();

		// 目录里任何负价都会在费用显示上变成负数金额。
		foreach (var model in registry.List())
		{
			Assert.True(model.Cost.Input >= 0, $"{model.Provider}/{model.Id} input={model.Cost.Input}");
			Assert.True(model.Cost.Output >= 0, $"{model.Provider}/{model.Id} output={model.Cost.Output}");
			Assert.True(model.Cost.CacheRead >= 0, $"{model.Provider}/{model.Id} cacheRead={model.Cost.CacheRead}");
			Assert.True(model.Cost.CacheWrite >= 0, $"{model.Provider}/{model.Id} cacheWrite={model.Cost.CacheWrite}");
		}
	}
}

/// <summary>
/// 重试分类与退避：对应 pi 的 <c>utils/retry.ts</c>。配额类错误不该重试，
/// 网络抖动与服务端临时故障该重试。
/// </summary>
public class RetryClassifierTests
{
	private static AssistantMessage Failure(string message) => new()
	{
		Content = [],
		Api = "openai-completions",
		Provider = "openrouter",
		Model = "auto",
		StopReason = StopReason.Error,
		ErrorMessage = message,
	};

	[Theory]
	[InlineData("The upstream provider returned an error")]
	[InlineData("Provider returned error")]
	[InlineData("429 Too Many Requests")]
	[InlineData("Service Unavailable")]
	[InlineData("The service is temporarily unavailable")]
	[InlineData("upstream_stream_read_error")]
	[InlineData("Stream was interrupted")]
	[InlineData("HTTP/2 stream failed")]
	[InlineData("request to https://ark.cn-beijing.volces.com failed, reason: ")]
	[InlineData("connection refused")]
	[InlineData("fetch failed")]
	[InlineData("Anthropic stream ended before message_stop")]
	[InlineData("Request timed out")]
	public void TransientFailuresAreRetryable(string message) =>
		Assert.True(RetryClassifier.IsRetryable(Failure(message)));

	/// <summary>
	/// 火山方舟的排队错误：403 + 嵌套 JSON，没有 429/限流字样，曾经直接当作不可重试。
	/// </summary>
	[Fact]
	public void ProviderQueueErrorsAreRetryable()
	{
		const string ark =
			"""
			OpenAI-compatible request failed (403): {"code":"403","message":"{\"code\":\"10605\",\"message\":\"\\\"isQueued\\\":true,\\"dfmodel\\":{\\"queueCount\\\":0,\\"queueType\\\":\\"p3\\\",\\"retryAfterSeconds\\\":30}}"}"}
			""";

		Assert.True(RetryClassifier.IsRetryable(Failure(ark)));
		Assert.Equal(30_000, RetryClassifier.ServerDelayMs(ark));
	}

	[Fact]
	public void QueuedRetryWaitsForTheProviderHintInsteadOfTheBackoff()
	{
		var policy = new RetryPolicy(BaseDelayMs: 2000, MaxAgentDelayMs: 60_000);

		// retryAfterSeconds 比退避长时听供应商的；超出上限时按上限截，不无限等。
		Assert.Equal(30_000, RetryClassifier.DelayMs(policy, 1, "retryAfterSeconds\":30"));
		Assert.Equal(60_000, RetryClassifier.DelayMs(policy, 1, "retry-after: 600"));

		// 没有提示时回到指数退避。
		Assert.Equal(2000, RetryClassifier.DelayMs(policy, 1, "429 Too Many Requests"));
		Assert.Equal(4000, RetryClassifier.DelayMs(policy, 2, null));
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("connection refused")]
	[InlineData("retry-after: Wed, 21 Oct 2015 07:28:00 GMT")]
	[InlineData("retryAfterSeconds\":0")]
	public void AbsentOrUnparsableRetryHintsFallBackToBackoff(string? message) =>
		Assert.Null(RetryClassifier.ServerDelayMs(message));

	[Theory]
	[InlineData("insufficient_quota: you exceeded your current quota")]
	[InlineData("Monthly usage limit reached")]
	[InlineData("GoUsageLimitError")]
	[InlineData("Your billing account is not active")]
	public void QuotaAndBillingFailuresAreNotRetryable(string message) =>
		Assert.False(RetryClassifier.IsRetryable(Failure(message)));

	[Fact]
	public void SuccessAndAbortsAreNeverRetried()
	{
		Assert.False(RetryClassifier.IsRetryable(Failure("timeout") with { StopReason = StopReason.Stop }));
		Assert.False(RetryClassifier.IsRetryable(Failure("timeout") with { StopReason = StopReason.Aborted }));
		Assert.False(RetryClassifier.IsRetryable(Failure(string.Empty)));
	}

	[Fact]
	public void BackoffDoublesAndIsCapped()
	{
		var policy = new RetryPolicy(BaseDelayMs: 2000, MaxAgentDelayMs: 60_000);

		Assert.Equal(2000, RetryClassifier.DelayMs(policy, 1));
		Assert.Equal(4000, RetryClassifier.DelayMs(policy, 2));
		Assert.Equal(8000, RetryClassifier.DelayMs(policy, 3));
		Assert.Equal(60_000, RetryClassifier.DelayMs(policy, 10));
		Assert.Equal(60_000, RetryClassifier.DelayMs(policy, 200));
	}

	[Fact]
	public void ZeroBaseDelayMeansNoWait() =>
		Assert.Equal(0, RetryClassifier.DelayMs(new RetryPolicy(BaseDelayMs: 0), 3));
}
