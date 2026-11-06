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

	public static bool IsOverflow(AssistantMessage message) =>
		message.StopReason == StopReason.Error && IsOverflowText(message.ErrorMessage);

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
