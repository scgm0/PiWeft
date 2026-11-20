using System.Text.RegularExpressions;

namespace PiWeft.Ai;

/// 自动重试策略，对应 pi 的 <c>settings.retry</c>。每次尝试之间的延迟是
/// <c>baseDelayMs * 2^(attempt-1)</c>，并以 <see cref="MaxAgentDelayMs"/> 封顶。
/// <param name="Enabled">是否启用自动重试。</param>
/// <param name="MaxRetries">最大重试次数（0 表示不重试，首次调用不计入）。</param>
/// <param name="BaseDelayMs">首次退避的基准延迟。</param>
/// <param name="MaxAgentDelayMs">单次退避的上限，默认 60 秒。</param>
public sealed record RetryPolicy(
	bool Enabled = true,
	int MaxRetries = 3,
	int BaseDelayMs = 2000,
	int MaxAgentDelayMs = RetryClassifier.DefaultMaxAgentDelayMs);

/// 判断一次失败的助手回复是否值得重试，并算出退避时长。对应 pi 的
/// <c>utils/retry.ts</c>：配额/账单类错误不重试，网络抖动与服务端临时故障重试。
public static class RetryClassifier
{
	public const int DefaultMaxAgentDelayMs = 60_000;

	/// 供应商明确要求等待超过这么久（例如配额要到明天才重置）时不自动重试：
	/// 退避上限 60 秒，提前重试只会再撞一次，会话还一直卡在「正在重试」。
	/// 这类错误交给用户决定换模型还是等重置。
	public const int MaxAutoRetryServerDelayMs = 15 * 60 * 1000;

	/// 账号级限制与凭据问题：自动重试没有意义，但手动继续接得上：用户换一个模型或供应商再发一次就行。
	private static readonly Regex AccountOrCredential = new(
		string.Join('|',
		[
			// 用量 / 配额 / 余额
			"GoUsageLimitError",
			"FreeUsageLimitError",
			"Monthly usage limit reached",
			"available balance",
			"insufficient_quota",
			"exceeded.{0,20}quota",
			"quota.{0,16}(?:exceeded|exhausted)",
			"out of budget",
			"billing",
			// DeepSeek 的「Insufficient Balance」、Anthropic 的「credit balance is too low」。
			"insufficient.?balance",
			"credit balance",
			"balance.{0,20}?(?:too low|insufficient|exhausted|depleted)",
			"payment required",
			"余额不足",
			"subscription_sharing_usage_limit_exceeded",
			// Sign in with ChatGPT 的 Business 工作区管理员设置的用量上限：只有管理员能提高。
			"subscription_sharing_workplace_individual_usage_limit_exceeded",
			// 智谱/Z.ai 的套餐用量上限（HTTP 429 + code 1308）：几小时后才重置，重试没有意义。
			// 只认 JSON 的 code 字段，别把请求 id/字节数里出现的 1308 当成它。
			"\"code\"\\s*:\\s*\"?1308(?![0-9])",
			// 凭据 / 权限
			"authentication.?(?:error|failed|failure|required)",
			"invalid.{0,12}api.?key",
			"api.?key.{0,24}?(?:invalid|missing|expired|revoked|not.?(?:set|found|provided))",
			"missing.{0,8}(?:api.?key|credential|token)",
			"no api key",
			"invalid.{0,8}credential",
			"unauthorized",
			"forbidden",
			"permission.?denied",
			"access.?denied",
			"insufficient.{0,8}permission",
		]),
		RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

	/// 本机 TLS/证书失败：只有修证书、代理或系统时间，换模型也接不上。
	private static readonly Regex Certificate = new(
		string.Join('|',
		[
			"remote certificate is invalid",
			"certificate has expired",
			"certificate verify failed",
			"self-signed certificate",
			"unable to get local issuer certificate",
			"sslv3 alert bad certificate",
		]),
		RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

	/// 换模型或换供应商后可能接上的请求类失败：模型不存在、上下文超限。
	private static readonly Regex ModelOrContext = new(
		string.Join('|',
		[
			"model.?not.?found",
			"unknown.?model",
			"no such model",
			"model.{0,24}?(?:does.?not.?exist|not.?exist)",
			"context.{0,16}?(?:window|length).{0,16}?exceed",
			"maximum context length",
			"context overflow",
		]),
		RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

	/// 供应商把「正在排队、等一会儿再来」装在非 429 的响应里：火山方舟用 HTTP 403 返回
	/// <c>{"code":"10605","message":"...\\"isQueued\\":true,...\\"retryAfterSeconds\\":30}"}</c>
	/// （<c>RESOURCE_LIMIT</c>），字面没有通用限流词，<see cref="Retryable"/> 判不出来，
	/// 自动重试会跳过这次失败。这些标记本身表示临时限流，单独认一类。
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

	/// 供应商给出的等待提示键：<c>retryAfterSeconds</c>（火山方舟）、<c>retry-after</c> /
	/// <c>Retry-After: 30</c>（HTTP 头透进错误文本时）。值在键后面第一段数字里。
	private static readonly Regex ServerDelayKey = new(
		@"retry[ _./-]?after(?:[ _./-]?(?<unit>seconds?|ms))?",
		RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

	private static readonly Regex Retryable = new(
		string.Join('|',
		[
			// 负载与 HTTP 状态
			"overloaded",
			"server_busy",
			"servers are currently busy",
			"currently experiencing high demand",
			"model is at capacity",
			// 排队超过供应商的准入门槛（sference：「waited 73.4s in queue, reaching this model's
			// admission ceiling of 60s; retry after the hinted delay」）。
			"admission ceiling",
			"rate.?limit",
			"too many requests",
			// 状态码不能是更长单词/数字的一部分：请求 id、字节数里出现的 500/429 不该触发重试。
			// 覆盖 5xx 中的网关瞬态（Cloudflare 520-527/530）；501/505/511 这类不重试。
			"(?<![a-z0-9])(?:429|50[0234]|52[0-7]|530)(?![a-z0-9])",
			// 网关空响应：pi 1.4+ 已把 bodyless 400/413 移出溢出判定，按瞬态重试。
			@"\b(?:400|413)\s*(?:status code)?\s*\(no body\)",
			// 也匹配 "The service is temporarily unavailable"；中文网关文案单独列。
			"service.?(?:is.?)?(?:temporarily.?)?unavailable",
			// 网关文案（「The provider is temporarily unavailable. Retry shortly.」）。
			// 必须带 temporarily：光说 unavailable 往往是路由配错，不是瞬态。
			"provider.?(?:is.?)?temporarily.?unavailable",
			"暂时不可用",
			"请稍后重试",
			"server.?error",
			"internal.?error",

			// 上游/网关的临时失败措辞（含 OpenRouter 的 "Provider returned error"）
			"provider.?returned.?(?:an.?)?error",
			// Anthropic 的内部错误，流开始后没有状态码：{"type":"api_error","message":"..."}。
			// 转发 Anthropic 格式的网关也用它报上游失败。
			"\"type\"\\s*:\\s*\"api_error\"",
			// OpenRouter 在供应商中途失败时给 OpenAI 兼容流一个 finish_reason=error。
			"provider finish_reason: error",
			"returned an empty response",
			"stopped before completing the response",
			"exceeded request buffer limit while retrying upstream",
			"unable to forward upstream",
			"upstream.?request.?failed",
			"upstream_stream_read_error",
			"stream_read_error",
			"stream was interrupted",
			"stream disconnected before completion",
			"http/?2 stream failed",
			"GOAWAY",
			"unexpected EOF",
			"unable to reach",
			"tool.?arguments.?stopped making progress",
			"basispoints_tool_arguments_stalled",

			// 网络与传输层
			"network.?error",
			"connection.?error",
			"connection.?refused",
			"connection.?lost",
			// 网关把中途断开包装成 Anthropic 风格的 api_error；PI 1.5.2+ 新增。
			"connection.?closed",
			"connection reset by peer",
			// Windows 的 WSAECONNRESET 文案，和 POSIX 的 connection reset by peer 是一回事。
			"connection was forcibly closed",
			"connection was closed unexpectedly",
			// TLS 握手失败：外层文案统一是「The SSL connection could not be established」，
			// 证书类原因在 Certificate 里先排掉。
			"ssl connection could not be established",
			"could not create ssl/tls secure channel",
			"ssl/tls secure channel",
			"(?:ssl|tls|websocket).?handshake",
			"handshake failed",
			"unexpected packet format",
			"remote party has closed the transport stream",
			"other side closed",
			"fetch failed",
			// 裸 Node socket 错误码（read ECONNRESET / write EPIPE / connect ETIMEDOUT）。
			// 码本身不能是更长标识符的一部分，否则 request id 里的字符串会误命中。
			@"(?<![a-z0-9_])(?:ECONNRESET|ECONNABORTED|ECONNREFUSED|ETIMEDOUT|EPIPE)(?![a-z0-9_])",
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
			// Codex 的 60 分钟连接上限：服务器中途关闭，重试会开新连接。
			"websocket.?connection.?limit.?reached",

			// 流被提前结束
			"ended without",
			"stream ended before message_stop",
			"stream ended before a terminal response event",
			// 网关以 SSE error 帧报的中途失败，没有状态码（#12227）。
			"ended the response before it is complete",
			"connection to the provider was reset",
			"reported a failure while generating the response",
			"http2 request did not get a response",
			"pending stream has been canceled",
			// .NET 的 HTTP 响应体在内容长度之前结束（chunked/SSE 被网关截断），
			// 文本是 "The response ended prematurely. (ResponseEnded)"。
			"response ended prematurely",
			"responseended",

			"retry delay",

			// provider 明确要求重试
			"you can retry your request",
			"try your request again",
			"please retry your request",
			// recursive：「Unable to process request. Repeat the request later.」
			"repeat the request later",

			// gRPC 型 provider（例如 NVIDIA NIM）
			"ResourceExhausted",

			// Sign in with ChatGPT 的临时不可用
			"subscription_sharing_usage_unavailable",
			"subscription_sharing_user_unavailable",
		]),
		RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

	public static bool IsRetryable(AssistantMessage message)
	{
		if (message.StopReason != StopReason.Error || string.IsNullOrEmpty(message.ErrorMessage))
		{
			return false;
		}

		return IsRetryableErrorMessage(message.ErrorMessage);
	}

	/// <summary>按错误文本判断瞬态失败；摘要、分类器等不经过助手消息的调用也用它。</summary>
	public static bool IsRetryableErrorMessage(string? errorMessage)
	{
		if (string.IsNullOrEmpty(errorMessage))
		{
			return false;
		}

		// 账号、凭据、模型与证书类失败自动重试不会变好，留给用户换模型或修配置。
		if (AccountOrCredential.IsMatch(errorMessage) || Certificate.IsMatch(errorMessage) ||
			ModelOrContext.IsMatch(errorMessage))
		{
			return false;
		}

		// 配额冷却几个小时（甚至一天）时，60 秒退避只会重复失败；让错误立即返回。
		if (ServerDelayMs(errorMessage) is { } serverDelay && serverDelay > MaxAutoRetryServerDelayMs)
		{
			return false;
		}

		return Retryable.IsMatch(errorMessage) || Queued.IsMatch(errorMessage);
	}

	/// <summary>
	/// 手动继续生成：这次失败是否值得在（可能换过模型后）再发一次。比自动重试宽：
	/// 配额/余额、凭据、模型不存在、上下文超限不会自动重试，但用户换模型后重发能接上；
	/// 限流冷却超过退避上限时也一样，手动重发不受 15 分钟上限约束。
	/// </summary>
	public static bool IsManuallyRecoverable(AssistantMessage message)
	{
		if (message.StopReason != StopReason.Error || message.ErrorMessage is not { Length: > 0 } errorMessage)
		{
			return false;
		}

		// 证书是本机配置问题：换模型也接不上，先修证书或代理。
		if (Certificate.IsMatch(errorMessage))
		{
			return false;
		}

		return AccountOrCredential.IsMatch(errorMessage) ||
			ModelOrContext.IsMatch(errorMessage) ||
			Retryable.IsMatch(errorMessage) ||
			Queued.IsMatch(errorMessage);
	}

	/// 错误文本里供应商明确要求的等待时长。取第一个能解析出正秒数的提示；
	/// 没有则 <c>null</c>。上限一天，防止畸形值把退避算成溢出。
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

	/// 第 <paramref name="attempt"/> 次重试前要等的时长。供应商给了等待提示就用它，
	/// 提前重试只会再撞一次 403；结果按 <see cref="RetryPolicy.MaxAgentDelayMs"/> 封顶。
	/// 没有提示则用 <see cref="DelayMs(RetryPolicy,int)"/> 的指数退避。
	public static int DelayMs(RetryPolicy policy, int attempt, string? errorMessage)
	{
		if (ServerDelayMs(errorMessage) is not int hint)
		{
			return DelayMs(policy, attempt);
		}

		var cap = policy.MaxAgentDelayMs <= 0 ? DefaultMaxAgentDelayMs : policy.MaxAgentDelayMs;
		return Math.Min(hint, cap);
	}

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
