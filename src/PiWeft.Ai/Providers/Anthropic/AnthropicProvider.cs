using System.Net;
using System.Net.Http.Headers;
using System.Net.ServerSentEvents;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using PiWeft.Ai.Json;
namespace PiWeft.Ai.Providers.Anthropic;

/// <summary>
/// Anthropic Messages API（<c>anthropic-messages</c>）的流式适配器。请求用 <see cref="Utf8JsonWriter"/> 手写，
/// SSE 响应用 <c>System.Net.ServerSentEvents</c> 解析；整条链路
/// 都不使用反射，满足 NativeAOT 要求。
/// </summary>
public sealed class AnthropicProvider : IApiProvider
{
	public const string ApiId = "anthropic-messages";
	public const string AnthropicVersion = "2023-06-01";
	public const string OAuthBeta = "oauth-2025-04-20";
	public const string DefaultBaseUrl = "https://api.anthropic.com";

	private static readonly byte[] DoneMarker = "[DONE]"u8.ToArray();

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
		using var headerTimeout = ProviderHttp.BeginHeaderTimeout(options);

		try
		{
			var apiKey = ResolveApiKey(options);
			if (string.IsNullOrEmpty(apiKey))
			{
				Fail(stream, model, startedAt, "Missing Anthropic API key. Set ANTHROPIC_API_KEY or pass ApiKey.");
				return;
			}

			var messages = Transcript.Normalize(context, model);
			var tools = Transcript.GetTools(messages);
			var thinking = ResolveThinking(model, options);
			var maxTokens = ResolveMaxTokens(model, options, thinking);

			using var request = BuildRequest(model, messages, tools, options, apiKey, thinking, maxTokens, out var payloadJson);
			options.NotifyStreamEvent(payloadJson, model);

			using var response = await _http
				.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headerTimeout.Token)
				.ConfigureAwait(false);

			options.OnResponse?.Invoke(ProviderHttp.FromResponse(response), model);

			if (!response.IsSuccessStatusCode)
			{
				var body = await response.Content.ReadAsStringAsync(options.CancellationToken).ConfigureAwait(false);
				Fail(
					stream,
					model,
					startedAt,
					$"Anthropic request failed ({(int)response.StatusCode}): {Truncate(body, 2000)}");
				return;
			}

			await using var bodyStream = await response.Content
				.ReadAsStreamAsync(options.CancellationToken)
				.ConfigureAwait(false);

			await ParseAsync(bodyStream, model, options, stream, startedAt, options.CancellationToken)
				.ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (options.CancellationToken.IsCancellationRequested)
		{
			Fail(stream, model, startedAt, "Request aborted", StopReason.Aborted);
		}
		catch (OperationCanceledException)
		{
			Fail(stream, model, startedAt, ProviderHttp.TimeoutMessage(headerTimeout));
		}
		catch (Exception ex)
		{
			Fail(stream, model, startedAt, DescribeError(ex));
		}
	}

	private HttpRequestMessage BuildRequest(
		Model model,
		IReadOnlyList<Message> messages,
		IReadOnlyList<Tool> tools,
		StreamOptions options,
		string apiKey,
		ThinkingConfig thinking,
		int maxTokens,
		out string payloadJson)
	{
		var bytes = JsonPayload.Write(writer =>
			AnthropicWire.WriteRequest(writer, model, messages, tools, options, thinking, maxTokens));

		if (options.OnPayload is { } onPayload)
		{
			var node = JsonNode.Parse(bytes);
			if (node is not null && onPayload(node, model) is { } replaced)
			{
				bytes = Encoding.UTF8.GetBytes(replaced.ToJsonString(PiJson.NodeOptions));
			}
		}

		payloadJson = Encoding.UTF8.GetString(bytes);

		var baseUrl = string.IsNullOrEmpty(model.BaseUrl) ? DefaultBaseUrl : model.BaseUrl;
		var isOAuth = apiKey.StartsWith("sk-ant-oat", StringComparison.Ordinal);

		var request = new HttpRequestMessage(HttpMethod.Post, baseUrl.TrimEnd('/') + "/v1/messages")
		{
			Content = new ByteArrayContent(bytes),
		};

		request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
		request.Headers.TryAddWithoutValidation("accept", "text/event-stream");
		request.Headers.TryAddWithoutValidation("anthropic-version", AnthropicVersion);
		request.Headers.TryAddWithoutValidation("user-agent", SharedHttpClient.UserAgent);

		if (isOAuth)
		{
			request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
			request.Headers.TryAddWithoutValidation("anthropic-beta", OAuthBeta);
		}
		else
		{
			request.Headers.TryAddWithoutValidation("x-api-key", apiKey);
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
		return level == ThinkingLevels.Off ? ThinkingConfig.Disabled : ThinkingConfig.On(BudgetFor(level));
	}

	internal static int BudgetFor(string level) => level switch
	{
		ThinkingLevels.Minimal => 1024,
		ThinkingLevels.Low => 2048,
		ThinkingLevels.Medium => 8192,
		ThinkingLevels.High => 16384,
		ThinkingLevels.XHigh => 32768,
		ThinkingLevels.Max => 65536,
		_ => 2048,
	};

	private static int ResolveMaxTokens(Model model, StreamOptions options, ThinkingConfig thinking)
	{
		var maxTokens = options.MaxTokens ?? model.MaxTokens;
		if (thinking.Enabled && maxTokens <= thinking.BudgetTokens)
		{
			maxTokens = thinking.BudgetTokens + 1024;
		}

		return maxTokens;
	}

	private static string? ResolveApiKey(StreamOptions options) =>
		ProviderEnvironment.ResolveApiKey("anthropic", options);

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

	/// <summary>给模型看的错误文本；设置 <c>PI_DEBUG_ERRORS=1</c> 时会附带堆栈便于排查。</summary>
	internal static string DescribeError(Exception exception) =>
		Environment.GetEnvironmentVariable("PI_DEBUG_ERRORS") == "1" ? exception.ToString() : exception.Message;

	internal static string Truncate(string value, int max) =>
		value.Length <= max ? value : string.Concat(value.AsSpan(0, max), "…");
}
