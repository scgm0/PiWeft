using System.Text.RegularExpressions;
using PiWeft.Ai;

namespace PiWeft.CodingAgent.Compaction;

public static partial class ContextOverflow
{
	private static readonly Regex[] Patterns =
	[
		PromptTooLong(),
		PromptExceedsMaxLength(),
		RequestTooLarge(),
		InputTooLong(),
		ExceedsContextWindow(),
		ExceedsMaximumContextLength(),
		InputTokenCountExceeds(),
		MaximumPromptLength(),
		ReduceMessages(),
		MaximumContextLength(),
		MaximumAllowedInputLength(),
		InputLongerThanContext(),
		ExceedsLimit(),
		AvailableContextSize(),
		GreaterThanContextLength(),
		ContextWindowExceedsLimit(),
		ContextWindowExceeded(),
		ModelContextWindowExceeded(),
		ExceededModelTokenLimit(),
		TooLargeForModel(),
		PromptHasTokens(),
		ContextLengthExceeded(),
		TooManyTokens(),
		TokenLimitExceeded(),
		RangeOfInputLength(),
		PromptTooLongExceeded(),
		PrefillCapRefused(),
		PrefillCellAtCapacity(),
		Http413(),
		PayloadTooLarge(),
	];

	private static readonly Regex[] NonOverflow =
	[
		Throttling(),
		RateLimit(),
		TooManyRequests(),
	];

	/// <summary>只看错误文本的溢出判定；有窗口大小时用带 window 的重载。</summary>
	public static bool IsOverflow(AssistantMessage message) =>
		message.StopReason == StopReason.Error && IsOverflowText(message.ErrorMessage);

	/// <summary>
	/// 带窗口大小的溢出判定。除了错误文本，还识别两类不报错的溢出：请求成功但
	/// input + cacheRead 超过窗口（z.ai），以及服务端把输入截到窗口后没有输出余量、
	/// 以 length 结束（Xiaomi MiMo）。窗口为 0 时只看错误文本。
	/// </summary>
	public static bool IsOverflow(AssistantMessage message, int contextWindow)
	{
		if (message.StopReason == StopReason.Error)
		{
			return IsOverflowText(message.ErrorMessage) ||
				(string.Equals(message.Provider, "cerebras", StringComparison.OrdinalIgnoreCase) &&
					BodylessStatus().IsMatch(message.ErrorMessage ?? string.Empty));
		}

		if (contextWindow <= 0)
		{
			return false;
		}

		var inputTokens = message.Usage.Input + message.Usage.CacheRead;
		return message.StopReason switch
		{
			StopReason.Stop => inputTokens > contextWindow,
			StopReason.Length => message.Usage.Output == 0 && inputTokens >= contextWindow * 0.99,
			_ => false,
		};
	}

	/// <summary>
	/// length 结束且输出没到模型的输出上限：可能是上下文挤压或供应商截断。
	/// <paramref name="desiredMaxOutput"/> 要传请求钳制之前的原始上限。
	/// </summary>
	public static bool IsRecoverableLength(AssistantMessage message, int desiredMaxOutput) =>
		message.StopReason == StopReason.Length &&
		desiredMaxOutput > 0 &&
		message.Usage.Output < desiredMaxOutput;

	/// <summary>
	/// 判断一段错误文本是「请求太大」还是普通限流。压缩摘要请求本身也可能太大：
	/// gateway 会把超出 prefill 容量的请求报成 429 prefill_cap_refused，而非 413。
	/// </summary>
	public static bool IsOverflowText(string? errorMessage)
	{
		if (string.IsNullOrEmpty(errorMessage))
		{
			return false;
		}

		if (NonOverflow.Any(pattern => pattern.IsMatch(errorMessage)))
		{
			return false;
		}

		return Patterns.Any(pattern => pattern.IsMatch(errorMessage));
	}

	[GeneratedRegex(@"prompt (?:is )?too long", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
	private static partial Regex PromptTooLong();

	[GeneratedRegex(@"prompt exceeds max length", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
	private static partial Regex PromptExceedsMaxLength();

	[GeneratedRegex(@"request_too_large", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
	private static partial Regex RequestTooLarge();

	[GeneratedRegex(@"input is too long for requested model", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
	private static partial Regex InputTooLong();

	[GeneratedRegex(@"exceeds the context window", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
	private static partial Regex ExceedsContextWindow();

	[GeneratedRegex(@"exceeds (?:the )?(?:model'?s )?maximum context length", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
	private static partial Regex ExceedsMaximumContextLength();

	[GeneratedRegex(@"input token count.*exceeds the maximum", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
	private static partial Regex InputTokenCountExceeds();

	[GeneratedRegex(@"maximum prompt length is \d+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
	private static partial Regex MaximumPromptLength();

	[GeneratedRegex(@"reduce the length of the messages", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
	private static partial Regex ReduceMessages();

	[GeneratedRegex(@"maximum context length is \d+ tokens", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
	private static partial Regex MaximumContextLength();

	[GeneratedRegex(@"exceeds (?:the )?maximum allowed input length", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
	private static partial Regex MaximumAllowedInputLength();

	[GeneratedRegex(@"is longer than the model'?s context length", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
	private static partial Regex InputLongerThanContext();

	[GeneratedRegex(@"exceeds the limit of \d+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
	private static partial Regex ExceedsLimit();

	[GeneratedRegex(@"exceeds the available context size", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
	private static partial Regex AvailableContextSize();

	[GeneratedRegex(@"greater than the context length", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
	private static partial Regex GreaterThanContextLength();

	[GeneratedRegex(@"context window exceeds limit", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
	private static partial Regex ContextWindowExceedsLimit();

	[GeneratedRegex(@"context window exceeded", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
	private static partial Regex ContextWindowExceeded();

	[GeneratedRegex(@"model_context_window_exceeded", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
	private static partial Regex ModelContextWindowExceeded();

	// 只有 Cerebras 的空响应体溢出算上下文问题，其余供应商的 400/413 空响应按瞬态重试。
	[GeneratedRegex(@"^4(?:00|13)\s*(?:status code)?\s*\(no body\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
	private static partial Regex BodylessStatus();

	[GeneratedRegex(@"exceeded model token limit", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
	private static partial Regex ExceededModelTokenLimit();

	[GeneratedRegex(@"too large for model with \d+ maximum context length", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
	private static partial Regex TooLargeForModel();

	[GeneratedRegex(@"prompt has [\d,]+ tokens?, but the configured context size is", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
	private static partial Regex PromptHasTokens();

	[GeneratedRegex(@"context[_ ]length[_ ]exceeded", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
	private static partial Regex ContextLengthExceeded();

	[GeneratedRegex(@"too many tokens", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
	private static partial Regex TooManyTokens();

	[GeneratedRegex(@"token limit exceeded", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
	private static partial Regex TokenLimitExceeded();

	[GeneratedRegex(@"range of input length should be", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
	private static partial Regex RangeOfInputLength();

	[GeneratedRegex(@"prompt too long; exceeded (?:max )?context length", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
	private static partial Regex PromptTooLongExceeded();

	[GeneratedRegex(@"prefill[_-]?cap[_-]?refused", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
	private static partial Regex PrefillCapRefused();

	[GeneratedRegex(@"(?<![a-z0-9])413(?![a-z0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
	private static partial Regex Http413();

	[GeneratedRegex(@"(?:request (?:entity|body|content)|payload) too large", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
	private static partial Regex PayloadTooLarge();

	[GeneratedRegex(@"\bcell is at capacity\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
	private static partial Regex PrefillCellAtCapacity();

	[GeneratedRegex(@"^(Throttling error|Service unavailable):", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
	private static partial Regex Throttling();

	[GeneratedRegex(@"rate limit", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
	private static partial Regex RateLimit();

	[GeneratedRegex(@"too many requests", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
	private static partial Regex TooManyRequests();
}
