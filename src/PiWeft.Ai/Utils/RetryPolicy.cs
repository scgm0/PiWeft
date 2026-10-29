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

	/// <summary>
	/// 供应商把「正在排队、等一会儿再来」装在非 429 的响应里：火山方舟用 HTTP 403 返回
	/// <c>{"code":"10605","message":"...\\"isQueued\\":true,...\\"retryAfterSeconds\\":30}"}</c>
	/// （<c>RESOURCE_LIMIT</c>），字面上没有任何通用限流词，靠 <see cref="Retryable"/> 判不出来，
	/// 于是自动重试直接跳过这次失败。这些标记本身就是临时限流的语义，单独认这一类。
	/// </summary>
	private static readonly Regex Queued = new(
		string.Join('|',
		[
			"isQueued",
			"queue.?type",
			"retry.?after",
			"RESOURCE_LIMIT",
			// 转义过的 JSON 里 code 与值之间隔着 \":\" 这类符号。
			@"code\D{0,8}10605(?!\d)",
			"排队",
		]),
		RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

	/// <summary>
	/// 供应商给出的等待提示键：<c>retryAfterSeconds</c>（火山方舟）、<c>retry-after</c> /
	/// <c>Retry-After: 30</c>（HTTP 头透进错误文本时）。值在键后面第一段数字里。
	/// </summary>
	private static readonly Regex ServerDelayKey = new(
		@"retry[ _./-]?after(?:[ _./-]?(?<unit>seconds?|ms))?",
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
			// 也匹配 "The service is temporarily unavailable"。
			"service.?(?:is.?)?(?:temporarily.?)?unavailable",
			"server.?error",
			"internal.?error",

			// 上游/网关的临时失败措辞（含 OpenRouter 的 "Provider returned error"）
			"provider.?returned.?(?:an.?)?error",
			"exceeded request buffer limit while retrying upstream",
			"unable to forward upstream",
			"upstream.?request.?failed",
			"upstream_stream_read_error",
			"stream was interrupted",
			"http/?2 stream failed",

			// 网络与传输层
			"network.?error",
			"connection.?error",
			"connection.?refused",
			"connection.?lost",
			"other side closed",
			"fetch failed",
			@"request to \S+ failed",
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

		return Retryable.IsMatch(message.ErrorMessage) || Queued.IsMatch(message.ErrorMessage);
	}

	/// <summary>
	/// 错误文本里供应商明确要求的等待时长。取第一个能解析出正秒数的提示；
	/// 没有则 <c>null</c>。上限一天，防止畸形值把退避算成溢出。
	/// </summary>
	public static int? ServerDelayMs(string? errorMessage)
	{
		if (string.IsNullOrEmpty(errorMessage))
		{
			return null;
		}

		foreach (Match match in ServerDelayKey.Matches(errorMessage))
		{
			var rest = errorMessage.AsSpan(match.Index + match.Length);
			var i = 0;

			// 跳过 `\":` 这类 JSON 转义与冒号：间隔要短，且一碰到字母就停手，否则
			// "Retry-After: Wed, 21 Oct ..." 这种日期头会把月份后面的天数当成秒数。
			while (i < rest.Length && !char.IsAsciiDigit(rest[i]) && !char.IsLetter(rest[i]) && i < 12)
			{
				i++;
			}

			var start = i;
			while (i < rest.Length && char.IsAsciiDigit(rest[i]))
			{
				i++;
			}

			if (start < i && int.TryParse(rest[start..i], out var value) && value > 0)
			{
				var millis = match.Groups["unit"].Value.Equals("ms", StringComparison.OrdinalIgnoreCase)
					? value
					: Math.Min(value, 86_400) * 1000;
				return Math.Min(millis, 86_400_000);
			}
		}

		return null;
	}

	/// <summary>
	/// 第 <paramref name="attempt"/> 次重试前真正要等的时长。供应商明确给了等待提示就听它的
	/// ——排队时提前重试只会再撞一次 403——并按 <see cref="RetryPolicy.MaxAgentDelayMs"/> 封顶；
	/// 没有提示则回落到 <see cref="DelayMs(RetryPolicy,int)"/> 的指数退避。
	/// </summary>
	public static int DelayMs(RetryPolicy policy, int attempt, string? errorMessage)
	{
		if (ServerDelayMs(errorMessage) is not int hint)
		{
			return DelayMs(policy, attempt);
		}

		var cap = policy.MaxAgentDelayMs <= 0 ? DefaultMaxAgentDelayMs : policy.MaxAgentDelayMs;
		return Math.Min(hint, cap);
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
