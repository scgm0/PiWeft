using System.Text.RegularExpressions;

namespace PiWeft.Ai;

/// <summary>
/// 自动重试策略，对应 pi 的 <c>settings.retry</c>。每次尝试之间的延迟是
/// <c>baseDelayMs * 2^(attempt-1)</c>，并以 <see cref="MaxAgentDelayMs"/> 封顶。
/// </summary>
/// <param name="Enabled">是否启用自动重试。</param>
/// <param name="MaxRetries">最大重试次数（0 表示不重试，首次调用不计入）。</param>
/// <param name="BaseDelayMs">首次退避的基准延迟。</param>
/// <param name="MaxAgentDelayMs">单次退避的上限，默认 60 秒。</param>
public sealed record RetryPolicy(
	bool Enabled = true,
	int MaxRetries = 3,
	int BaseDelayMs = 2000,
	int MaxAgentDelayMs = RetryClassifier.DefaultMaxAgentDelayMs);

/// <summary>
/// 判断一次失败的助手回复是否值得重试，并算出退避时长。对应 pi 的
/// <c>utils/retry.ts</c>：配额/账单类错误不重试，网络抖动与服务端临时故障重试。
/// </summary>
public static class RetryClassifier
{
	/// <summary>退避上限，与 pi 的 <c>DEFAULT_MAX_AGENT_RETRY_DELAY_MS</c> 一致。</summary>
	public const int DefaultMaxAgentDelayMs = 60_000;

	/// <summary>
	/// 明确不可重试的用量/配额类错误：这些是账号级限制，等几秒重试也不会变好。
	/// </summary>
	private static readonly Regex NonRetryable = new(
		string.Join('|',
		[
			"GoUsageLimitError",
			"FreeUsageLimitError",
			"Monthly usage limit reached",
			"available balance",
			"insufficient_quota",
			"out of budget",
			"quota exceeded",
			"billing",
			"subscription_sharing_usage_limit_exceeded",
		]),
		RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

	/// <summary>临时性故障：负载、限流、网关与网络层抖动、流被提前关闭。</summary>
	private static readonly Regex Retryable = new(
		string.Join('|',
		[
			// 负载与 HTTP 状态
			"overloaded",
			"server_busy",
			"servers are currently busy",
			"currently experiencing high demand",
			"model is at capacity",
			"rate.?limit",
			"too many requests",
			"429",
			"500",
			"502",
			"503",
			"504",
			"520",
			"524",
			"service.?unavailable",
			"server.?error",
			"internal.?error",

			// 上游/网关的临时失败措辞（含 OpenRouter 的 "Provider returned error"）
			"provider.?returned.?(?:an.?)?error",
			"exceeded request buffer limit while retrying upstream",

			// 网络与传输层
			"network.?error",
			"connection.?error",
			"connection.?refused",
			"connection.?lost",
			"other side closed",
			"fetch failed",
			"getaddrinfo",
			"ENOTFOUND",
			"EAI_AGAIN",
			"upstream.?connect",
			"reset before headers",
			"socket hang up",
			"socket connection was closed",
			"timed? out",
			"timeout",
			"terminated",

			// WebSocket 传输
			"websocket.?closed",
			"websocket.?error",

			// 流被提前结束
			"ended without",
			"stream ended before message_stop",
			"stream ended before a terminal response event",
			"http2 request did not get a response",
			"pending stream has been canceled",

			"retry delay",

			// provider 明确要求重试
			"you can retry your request",
			"try your request again",
			"please retry your request",

			// gRPC 型 provider（例如 NVIDIA NIM）
			"ResourceExhausted",

			// Sign in with ChatGPT 的临时不可用
			"subscription_sharing_usage_unavailable",
			"subscription_sharing_user_unavailable",
		]),
		RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

	/// <summary>这次失败的助手回复是否值得重试。</summary>
	public static bool IsRetryable(AssistantMessage message)
	{
		if (message.StopReason != StopReason.Error || string.IsNullOrEmpty(message.ErrorMessage))
		{
			return false;
		}

		if (NonRetryable.IsMatch(message.ErrorMessage))
		{
			return false;
		}

		return Retryable.IsMatch(message.ErrorMessage);
	}

	/// <summary>第 <paramref name="attempt"/> 次重试前的退避时长（1 起算）。</summary>
	public static int DelayMs(RetryPolicy policy, int attempt)
	{
		var exponent = Math.Max(0, attempt - 1);

		// 指数增长可能溢出，先按上限裁剪指数再计算。
		var cap = policy.MaxAgentDelayMs <= 0 ? DefaultMaxAgentDelayMs : policy.MaxAgentDelayMs;
		var baseDelay = Math.Max(0, policy.BaseDelayMs);
		if (baseDelay == 0)
		{
			return 0;
		}

		var maxExponent = 0;
		var probe = (long)baseDelay;
		while (probe < cap && maxExponent < exponent)
		{
			probe *= 2;
			maxExponent++;
		}

		var delay = (long)baseDelay << Math.Min(exponent, maxExponent);
		return (int)Math.Min(delay, cap);
	}
}
