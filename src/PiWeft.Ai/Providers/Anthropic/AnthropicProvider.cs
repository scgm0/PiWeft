using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.ServerSentEvents;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using PiWeft.Ai.Json;
using PiWeft.Ai.Utils;

namespace PiWeft.Ai.Providers.Anthropic;

/// Anthropic Messages API（<c>anthropic-messages</c>）的流式适配器。请求用 <see cref="Utf8JsonWriter"/> 手写，
/// SSE 响应用 <c>System.Net.ServerSentEvents</c> 解析；整条链路
/// 都不使用反射，满足 NativeAOT 要求。
public sealed class AnthropicProvider : IApiProvider
{
	public const string ApiId = "anthropic-messages";
	public const string AnthropicVersion = "2023-06-01";
	public const string OAuthBeta = "claude-code-20250219,oauth-2025-04-20";
	public const string MidConversationOutputConfigBeta = "mid-conversation-output-config-2026-07-01";
	public const string ThinkingBindingControlsBeta = "thinking-binding-controls-2026-08-01";
	public const string ServerSideFallbackBeta = "server-side-fallback-2026-07-01";
	public const string ClaudeCliUserAgent = "claude-cli/2.1.322";
	public const string DefaultBaseUrl = "https://api.anthropic.com";

	private static readonly byte[] DoneMarker = "[DONE]"u8.ToArray();
	private static readonly ConcurrentDictionary<string, byte> StrictToolLimitSessions = new(StringComparer.Ordinal);
	private const int MaxStrictToolLimitSessions = 1024;

	private readonly HttpClient _http;

	public AnthropicProvider(HttpClient? httpClient = null) => _http = httpClient ?? SharedHttpClient.Instance;

	public string Api => ApiId;

	public AssistantMessageEventStream Stream(Model model, Context context, StreamOptions options)
	{
		var stream = new AssistantMessageEventStream();
		_ = Task.Run(() => RunAsync(model, context, options, stream), CancellationToken.None);
		return stream;
	}

	private async Task RunAsync(Model model, Context context, StreamOptions options, AssistantMessageEventStream stream)
	{
		var startedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		// 超时只覆盖「等响应头」：正文长流（长时间思考、慢生成）不能被固定时限切断。
		HeaderTimeoutScope? headerTimeout = null;
		var cancellationToken = options.CancellationToken;

		try
		{
			var apiKey = await ResolveApiKeyAsync(model, options, cancellationToken).ConfigureAwait(false);
			if (string.IsNullOrEmpty(apiKey))
			{
				Fail(stream, model, startedAt, "Missing Anthropic API key. Set ANTHROPIC_API_KEY or pass ApiKey.");
				return;
			}

			var messages = Transcript.Normalize(context, model);
			var tools = Transcript.GetTools(messages);
			var thinking = ResolveThinking(model, options);
			var maxTokens = ResolveMaxTokens(model, options, thinking);

			// Claude 对严格工具的额度/文法有上限（工具数、联合类型参数数），超了整个请求 400。
			// 命中时把「希望严格」的降级成普通 schema 重发一次；声明了 require 的仍会报错。
			var hasPreferredStrictTools = HasPreferredStrictTools(model, tools);
			var disablePreferredStrictTools = StrictToolsDisabledForSession(options);
			while (true)
			{
				// 上一次尝试可能已经把响应头等到了（严限重试）：先收掉它的计时器。
				headerTimeout?.Dispose();
				headerTimeout = ProviderHttp.BeginHeaderTimeout(options);
				using var request = BuildRequest(
					model, messages, tools, options, apiKey, thinking, maxTokens, disablePreferredStrictTools, out var payloadJson);
				options.NotifyStreamEvent(payloadJson, model);

				using var response = await _http
					.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headerTimeout.Token)
					.ConfigureAwait(false);
				headerTimeout.Stop();

				options.OnResponse?.Invoke(ProviderHttp.FromResponse(response), model);

				if (response.IsSuccessStatusCode)
				{
					await using var bodyStream = await ProviderHttp
						.ReadBodyAsync(response, options, options.CancellationToken)
						.ConfigureAwait(false);

					await ParseAsync(bodyStream, model, options, stream, startedAt, options.CancellationToken)
						.ConfigureAwait(false);
					return;
				}

				var body = await ProviderHttp.ReadBodyTextAsync(response, options, options.CancellationToken).ConfigureAwait(false);
				if (!disablePreferredStrictTools && hasPreferredStrictTools &&
					(int)response.StatusCode == 400 && ConstrainedSampling.IsStrictToolLimitError(body))
				{
					disablePreferredStrictTools = true;
					RememberStrictToolLimit(options);
					continue;
				}

				Fail(
					stream,
					model,
					startedAt,
					$"Anthropic request failed ({(int)response.StatusCode}): {Truncate(body, 2000)}");
				return;
			}
		}
		catch (OperationCanceledException) when (options.CancellationToken.IsCancellationRequested)
		{
			Fail(stream, model, startedAt, "Request aborted", StopReason.Aborted);
		}
		catch (OperationCanceledException)
		{
			Fail(stream, model, startedAt,
				headerTimeout is { } timeout ? ProviderHttp.TimeoutMessage(timeout) : "Request timed out");
		}
		catch (Exception ex)
		{
			Fail(stream, model, startedAt, DescribeError(ex));
		}
		finally
		{
			headerTimeout?.Dispose();
		}
	}

	/// 本次请求里是否有「希望严格」且真的能发严格 schema 的工具：只有它们值得为额度超限重试。
	private static bool HasPreferredStrictTools(Model model, IReadOnlyList<Tool> tools)
	{
		if (!ModelCompat.FlagOr(model, "supportsStrictTools", false))
		{
			return false;
		}

		foreach (var tool in tools)
		{
			if (ConstrainedSampling.PrefersStrictJsonSchema(tool) &&
				ConstrainedSampling.ResolveStrictJsonSchema(
					tool, supportsStrictMode: true, ConstrainedSampling.IsAnthropicStrictUnsupportedKeyword) == true)
			{
				return true;
			}
		}

		return false;
	}

	private HttpRequestMessage BuildRequest(
		Model model,
		IReadOnlyList<Message> messages,
		IReadOnlyList<Tool> tools,
		StreamOptions options,
		string apiKey,
		ThinkingConfig thinking,
		int maxTokens,
		bool disablePreferredStrictTools,
		out string payloadJson)
	{
		var isAnthropicOAuth = apiKey.StartsWith("sk-ant-oat", StringComparison.Ordinal);
		var bytes = JsonPayload.Write(writer => AnthropicWire.WriteRequest(
			writer, model, messages, tools, options, thinking, maxTokens, isAnthropicOAuth, disablePreferredStrictTools));

		if (options.OnPayload is { } onPayload)
		{
			var node = JsonNode.Parse(bytes);
			if (node is not null && onPayload(node, model) is { } replaced)
			{
				bytes = Encoding.UTF8.GetBytes(replaced.ToJsonString(PiJson.NodeOptions));
			}
		}

		payloadJson = Encoding.UTF8.GetString(bytes);

		var baseUrl = string.IsNullOrEmpty(model.BaseUrl)
			? DefaultBaseUrl
			: ProviderEnvironment.ExpandBaseUrl(model.Provider, model.BaseUrl);
		// Anthropic 官方 API key 用 x-api-key；Claude OAuth 与 GitHub Copilot 用 Bearer。
		// Copilot 的 Anthropic 代理还要求浏览器直连标记；它不接受 x-api-key 形式的 Copilot token。
		var isCopilot = string.Equals(model.Provider, "github-copilot", StringComparison.Ordinal);

		var request = new HttpRequestMessage(HttpMethod.Post, baseUrl.TrimEnd('/') + "/v1/messages")
		{
			Content = new ByteArrayContent(bytes),
		};

		request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
		request.Headers.TryAddWithoutValidation("accept", "text/event-stream");
		request.Headers.TryAddWithoutValidation("anthropic-version", AnthropicVersion);
		request.Headers.TryAddWithoutValidation("user-agent", SharedHttpClient.UserAgent);

		var betas = new List<string>();
		if (isAnthropicOAuth)
		{
			request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
			// Claude Code CLI 的 UA / x-app / beta 是 OAuth 订阅凭据的配套元数据，与 pi-ai 对齐。
			request.Headers.Remove("user-agent");
			request.Headers.TryAddWithoutValidation("user-agent", ClaudeCliUserAgent);
			request.Headers.TryAddWithoutValidation("x-app", "cli");
			request.Headers.TryAddWithoutValidation("anthropic-dangerous-direct-browser-access", "true");
			betas.AddRange(OAuthBeta.Split(','));
		}
		else if (isCopilot)
		{
			request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
			request.Headers.TryAddWithoutValidation("anthropic-dangerous-direct-browser-access", "true");
		}
		else if (string.Equals(model.Provider, "cloudflare-ai-gateway", StringComparison.Ordinal))
		{
			// AI Gateway 的凭据是网关令牌，上游密钥由网关 BYOK 管理；发 x-api-key 会被网关拒绝。
			request.Headers.TryAddWithoutValidation("cf-aig-authorization", "Bearer " + apiKey);
		}
		else
		{
			request.Headers.TryAddWithoutValidation("x-api-key", apiKey);
		}

		if (ModelCompat.FlagOr(model, "supportsMidConvoEffort", false))
		{
			betas.Add(MidConversationOutputConfigBeta);
			betas.Add(ThinkingBindingControlsBeta);
		}

		if (ModelCompat.Array(model, "allowedFallbackModels").Count > 0)
		{
			betas.Add(ServerSideFallbackBeta);
		}

		if (betas.Count > 0)
		{
			request.Headers.TryAddWithoutValidation("anthropic-beta", string.Join(',', betas));
		}

		if (model.Headers is { Count: > 0 } modelHeaders)
		{
			foreach (var (name, value) in modelHeaders)
			{
				request.Headers.TryAddWithoutValidation(name, value);
			}
		}

		if (options.Headers is { Count: > 0 } extra)
		{
			foreach (var (name, value) in extra)
			{
				if (value is null)
				{
					request.Headers.Remove(name);
					continue;
				}

				request.Headers.TryAddWithoutValidation(name, value);
			}
		}

		ProviderAuthHeader.Apply(model, apiKey, request);
		ProviderSessionHeaders.ApplyAnthropic(model, options, request);
		CopilotHeaders.Apply(model, messages, request);
		OpenCodeHeaders.Apply(model, options, request);
		return request;
	}

	private static async Task ParseAsync(
		Stream body,
		Model model,
		StreamOptions options,
		AssistantMessageEventStream stream,
		long startedAt,
		CancellationToken cancellationToken)
	{
		var state = new AnthropicStreamState(model, stream, startedAt);
		var sse = new SseJsonReader();
		var parser = SseParser.Create<bool>(body, (eventType, span) =>
		{
			if (span.SequenceEqual(DoneMarker))
			{
				return false;
			}

			using var document = sse.Parse(eventType, span);
			options.NotifyStreamEvent(document.RootElement, model);
			state.Handle(eventType, document.RootElement);
			return true;
		});

		await foreach (var item in parser.EnumerateAsync(cancellationToken).ConfigureAwait(false))
		{
			if (!item.Data)
			{
				break; // data: [DONE]
			}
		}

		state.Finish();
	}

	internal static ThinkingConfig ResolveThinking(Model model, StreamOptions options)
	{
		if (!model.Reasoning)
		{
			return ThinkingConfig.None;
		}

		var level = ThinkingLevelResolver.Clamp(model, options.ThinkingLevel ?? ThinkingLevels.High);
		if (level == ThinkingLevels.Off)
		{
			return ThinkingConfig.Disabled;
		}

		// Opus 5.5 / Fable 这类模型只接受 adaptive thinking；发 budget_tokens 会 400。
		return ModelCompat.FlagOr(model, "forceAdaptiveThinking", false)
			? ThinkingConfig.Adaptive(EffortFor(model, level))
			: ThinkingConfig.On(BudgetFor(level, options.ThinkingBudgets));
	}

	/// 把思考级别映射成 Anthropic 的 effort。目录里的 thinkingLevelMap 优先，未声明时按 pi 的默认表。
	private static string EffortFor(Model model, string level)
	{
		if (model.ThinkingLevelMap?.TryGetValue(level, out var mapped) == true &&
			mapped is "low" or "medium" or "high" or "xhigh" or "max")
		{
			return mapped;
		}

		return level switch
		{
			ThinkingLevels.Minimal or ThinkingLevels.Low => "low",
			ThinkingLevels.Medium => "medium",
			_ => "high",
		};
	}

	internal static int BudgetFor(string level, IReadOnlyDictionary<string, int>? customBudgets = null)
	{
		if (customBudgets?.TryGetValue(level, out var custom) == true && custom > 0)
		{
			return custom;
		}

		return level switch
		{
			ThinkingLevels.Minimal => 1024,
			ThinkingLevels.Low => 2048,
			ThinkingLevels.Medium => 8192,
			ThinkingLevels.High => 16384,
			ThinkingLevels.XHigh => 32768,
			ThinkingLevels.Max => 65536,
			_ => 2048,
		};
	}

	private static int ResolveMaxTokens(Model model, StreamOptions options, ThinkingConfig thinking)
	{
		var maxTokens = options.MaxTokens ?? model.MaxTokens;
		// Adaptive thinking 不占 budget_tokens，不能把 max_tokens 抬过模型上限。
		if (thinking.Mode == ThinkingMode.Budget && maxTokens <= thinking.BudgetTokens)
		{
			maxTokens = thinking.BudgetTokens + 1024;
		}

		return maxTokens;
	}

	private static bool StrictToolsDisabledForSession(StreamOptions options) =>
		options.SessionId is { Length: > 0 } sessionId && StrictToolLimitSessions.ContainsKey(sessionId);

	/// 严格工具被 Claude 拒绝后，本会话后续请求直接发普通 schema，避免每轮都撞一次 400。
	private static void RememberStrictToolLimit(StreamOptions options)
	{
		if (options.SessionId is not { Length: > 0 } sessionId)
		{
			return;
		}

		if (StrictToolLimitSessions.Count >= MaxStrictToolLimitSessions)
		{
			StrictToolLimitSessions.Clear();
		}

		StrictToolLimitSessions[sessionId] = 0;
	}

	internal static void ResetSessionStrictTools(string sessionId) => StrictToolLimitSessions.TryRemove(sessionId, out _);

	private static Task<string?> ResolveApiKeyAsync(Model model, StreamOptions options, CancellationToken cancellationToken) =>
		ProviderEnvironment.ResolveApiKeyAsync(model.Provider, options, cancellationToken);

	internal static void Fail(
		AssistantMessageEventStream stream,
		Model model,
		long startedAt,
		string message,
		StopReason reason = StopReason.Error)
	{
		var error = new AssistantMessage
		{
			Api = ApiId,
			Provider = model.Provider,
			Model = model.Id,
			Timestamp = startedAt,
			StopReason = reason,
			ErrorMessage = message,
		};

		stream.Push(new ErrorEvent { Reason = reason, Partial = error });
	}

	internal static string DescribeError(Exception exception) =>
		Environment.GetEnvironmentVariable("PI_DEBUG_ERRORS") == "1" ? exception.ToString() : exception.Message;

	internal static string Truncate(string value, int max) =>
		value.Length <= max ? value : string.Concat(value.AsSpan(0, max), "…");
}
