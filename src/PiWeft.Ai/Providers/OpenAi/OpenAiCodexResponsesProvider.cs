using System.Net;
using System.Net.ServerSentEvents;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiWeft.Ai.Auth;

using PiWeft.Ai.Json;
namespace PiWeft.Ai.Providers.OpenAi;

/// <summary>
/// ChatGPT Codex 后端（<c>openai-codex-responses</c>）的流式适配器。它使用 Responses 线格式，
/// 面向 <c>https://chatgpt.com/backend-api/codex</c>，用 ChatGPT OAuth 凭据鉴权，并且始终发送
/// <c>store: false</c> —— 该端点强制要求这个值。
/// </summary>
public sealed class OpenAiCodexResponsesProvider : IApiProvider
{
	public const string ApiId = "openai-codex-responses";

	private static readonly byte[] DoneMarker = "[DONE]"u8.ToArray();

	private readonly HttpClient _http;
	private readonly HttpClient _authHttp;

	public OpenAiCodexResponsesProvider(HttpClient? httpClient = null)
	{
		_http = httpClient ?? SharedHttpClient.Instance;

		// 令牌刷新不经过调用方提供的处理器，因为那个处理器只服务于推理流量。
		_authHttp = httpClient is null ? SharedHttpClient.Instance : new HttpClient();
	}

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
		var cancellationToken = options.CancellationToken;

		try
		{
			// 显式传入的 key 优先，调用方借此注入自己保管的短期令牌。
			var credentials = !string.IsNullOrEmpty(options.ApiKey)
				? new CodexCredentials { Access = options.ApiKey, Refresh = string.Empty }
				: await CodexAuth.ResolveAsync(_authHttp, agentDir: null, authBaseUrl: null, cancellationToken)
					.ConfigureAwait(false);

			var accountId = CodexAuth.GetAccountId(credentials.Access)
				?? throw new CodexAuthException("Failed to extract the ChatGPT account id from the access token");

			var messages = Transcript.Normalize(context, model);
			var tools = Transcript.GetTools(messages);
			var reasoningEffort = OpenAiResponsesProvider.ResolveReasoningEffort(model, options);
			var maxOutputTokens = options.MaxTokens ?? (model.MaxTokens > 0 ? model.MaxTokens : null);
			var cacheKey = options.SessionId ?? options.Metadata?["prompt_cache_key"]?.GetValue<string>();

			var body = BuildRequestBody(
				model,
				messages,
				tools,
				options,
				reasoningEffort,
				maxOutputTokens,
				cacheKey);
			var transport = (options.Transport ?? "auto").ToLowerInvariant();
			var webSocket = transport is "websocket" or "websocket-cached" or "auto";
			if (webSocket)
			{
				var handled = await RunWebSocketAsync(
					model,
					body,
					options,
					stream,
					startedAt,
					accountId,
					credentials.Access,
					allowFallback: transport == "auto",
					cancellationToken).ConfigureAwait(false);
				if (handled)
				{
					return;
				}
			}

			using var request = BuildHttpRequest(model, body, options, accountId, credentials.Access);
			using var response = await _http
				.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headerTimeout.Token)
				.ConfigureAwait(false);

			options.OnResponse?.Invoke(ProviderHttp.FromResponse(response), model);

			if (!response.IsSuccessStatusCode)
			{
				var errorBody = await response.Content.ReadAsStringAsync(options.CancellationToken).ConfigureAwait(false);
				PushFailure(stream, model, startedAt, $"Codex request failed ({(int)response.StatusCode}): {Truncate(errorBody, 2000)}");
				return;
			}

			await using var bodyStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

			var state = new OpenAiResponsesStreamState(model, stream, startedAt, ApiId);
			var sse = new SseJsonReader();
			var parser = SseParser.Create<bool>(bodyStream, (eventType, span) =>
			{
				if (span.SequenceEqual(DoneMarker))
				{
					return false;
				}

				using var document = sse.Parse(eventType, span);
				options.NotifyStreamEvent(document.RootElement, model);
				state.Handle(document.RootElement);
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
		catch (OperationCanceledException) when (options.CancellationToken.IsCancellationRequested)
		{
			PushFailure(stream, model, startedAt, "Request aborted", StopReason.Aborted);
		}
		catch (OperationCanceledException)
		{
			PushFailure(stream, model, startedAt, ProviderHttp.TimeoutMessage(headerTimeout));
		}
		catch (Exception ex)
		{
			PushFailure(stream, model, startedAt, DescribeError(ex));
		}
	}

	private static byte[] BuildRequestBody(
		Model model,
		IReadOnlyList<Message> messages,
		IReadOnlyList<Tool> tools,
		StreamOptions options,
		string? reasoningEffort,
		int? maxOutputTokens,
		string? cacheKey)
	{
		var bytes = JsonPayload.Write(writer => OpenAiResponsesWire.WriteRequest(
			writer,
			model,
			messages,
			tools,
			options,
			reasoningEffort,
			maxOutputTokens,
			modelNameOverride: null,
			store: false,
			promptCacheKey: cacheKey));

		if (options.OnPayload is { } onPayload)
		{
			var node = JsonNode.Parse(bytes);
			if (node is not null && onPayload(node, model) is { } replaced)
			{
				bytes = Encoding.UTF8.GetBytes(replaced.ToJsonString(PiJson.NodeOptions));
			}
		}

		return bytes;
	}

	private static HttpRequestMessage BuildHttpRequest(
		Model model,
		byte[] bytes,
		StreamOptions options,
		string accountId,
		string accessToken)
	{
		var request = new HttpRequestMessage(HttpMethod.Post, BuildUrl(model))
		{
			Content = new ByteArrayContent(bytes),
		};

		request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
		request.Headers.TryAddWithoutValidation("accept", "text/event-stream");
		request.Headers.TryAddWithoutValidation("originator", "pi");
		request.Headers.TryAddWithoutValidation("user-agent", SharedHttpClient.UserAgent);
		request.Headers.TryAddWithoutValidation("authorization", $"Bearer {accessToken}");
		request.Headers.TryAddWithoutValidation("chatgpt-account-id", accountId);

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

	private async Task<bool> RunWebSocketAsync(
		Model model,
		byte[] body,
		StreamOptions options,
		AssistantMessageEventStream stream,
		long startedAt,
		string accountId,
		string accessToken,
		bool allowFallback,
		CancellationToken cancellationToken)
	{
		var started = false;
		using var socket = new ClientWebSocket();
		try
		{
			var requestId = options.SessionId is { Length: > 0 } sessionId ? sessionId : Guid.NewGuid().ToString("N");
			SetWebSocketHeaders(socket, model, options, accountId, accessToken, requestId);

			using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			connectTimeout.CancelAfter(TimeSpan.FromMilliseconds(options.TimeoutMs is > 0 ? options.TimeoutMs.Value : 15_000));
			await socket.ConnectAsync(new Uri(BuildWebSocketUrl(model)), connectTimeout.Token).ConfigureAwait(false);

			var payload = JsonNode.Parse(body) as JsonObject ?? new JsonObject();
			payload["type"] = "response.create";
			var frame = Encoding.UTF8.GetBytes(payload.ToJsonString(PiJson.NodeOptions));
			await socket.SendAsync(frame, WebSocketMessageType.Text, endOfMessage: true, cancellationToken).ConfigureAwait(false);

			var state = new OpenAiResponsesStreamState(model, stream, startedAt, ApiId);
			var buffer = new byte[64 * 1024];
			var received = new MemoryStream();
			var terminal = false;
			while (!terminal && socket.State == WebSocketState.Open)
			{
				var result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
				if (result.MessageType == WebSocketMessageType.Close)
				{
					throw new InvalidOperationException("Codex WebSocket closed before response.completed");
				}

				received.Write(buffer, 0, result.Count);
				if (!result.EndOfMessage)
				{
					continue;
				}

				var text = Encoding.UTF8.GetString(received.GetBuffer(), 0, (int)received.Length);
				received.SetLength(0);
				if (text.Length == 0)
				{
					continue;
				}

				using var document = JsonDocument.Parse(text);
				started = true;
				options.NotifyStreamEvent(document.RootElement, model);
				state.Handle(document.RootElement);
				terminal = IsTerminalEvent(document.RootElement);
			}

			if (!terminal)
			{
				throw new InvalidOperationException("Codex WebSocket ended before response.completed");
			}

			state.Finish();
			return true;
		}
		catch (Exception) when (
			!started &&
			allowFallback &&
			!cancellationToken.IsCancellationRequested)
		{
			// “auto” 在握手/首帧失败时静默回退到 SSE。
			return false;
		}
		catch (Exception exception)
		{
			PushFailure(
				stream,
				model,
				startedAt,
				cancellationToken.IsCancellationRequested ? "Request aborted" : $"Codex WebSocket failed: {exception.Message}",
				cancellationToken.IsCancellationRequested ? StopReason.Aborted : StopReason.Error);
			return true;
		}
	}

	private static void SetWebSocketHeaders(
		ClientWebSocket socket,
		Model model,
		StreamOptions options,
		string accountId,
		string accessToken,
		string requestId)
	{
		socket.Options.SetRequestHeader("originator", "pi");
		socket.Options.SetRequestHeader("user-agent", SharedHttpClient.UserAgent);
		socket.Options.SetRequestHeader("authorization", $"Bearer {accessToken}");
		socket.Options.SetRequestHeader("chatgpt-account-id", accountId);
		socket.Options.SetRequestHeader("OpenAI-Beta", "responses_websockets=2026-02-06");
		socket.Options.SetRequestHeader("x-client-request-id", requestId);
		socket.Options.SetRequestHeader("session-id", requestId);

		if (model.Headers is { Count: > 0 } modelHeaders)
		{
			foreach (var (name, value) in modelHeaders)
			{
				socket.Options.SetRequestHeader(name, value);
			}
		}

		if (options.Headers is { Count: > 0 } extra)
		{
			foreach (var (name, value) in extra)
			{
				if (value is not null)
				{
					socket.Options.SetRequestHeader(name, value);
				}
			}
		}
	}

	private static bool IsTerminalEvent(JsonElement root)
	{
		var type = root.TryGetProperty("type", out var typeNode) ? typeNode.GetString() : null;
		return type is "response.completed" or "response.done" or "response.incomplete" or "response.failed" or "error";
	}

	/// <summary>把 responses 端点换成 WebSocket URL。</summary>
	internal static string BuildWebSocketUrl(Model model)
	{
		var uri = new Uri(BuildUrl(model));
		var builder = new UriBuilder(uri)
		{
			Scheme = uri.Scheme switch
			{
				"https" => "wss",
				"http" => "ws",
				_ => uri.Scheme,
			},
		};
		return builder.Uri.ToString();
	}

	/// <summary>Codex 的 responses 端点，兼容已经带上该路径的 base URL。</summary>
	internal static string BuildUrl(Model model)
	{
		var baseUrl = string.IsNullOrEmpty(model.BaseUrl)
			? "https://chatgpt.com/backend-api"
			: model.BaseUrl.TrimEnd('/');

		if (baseUrl.EndsWith("/codex/responses", StringComparison.Ordinal))
		{
			return baseUrl;
		}

		return baseUrl.EndsWith("/codex", StringComparison.Ordinal)
			? $"{baseUrl}/responses"
			: $"{baseUrl}/codex/responses";
	}

	private static void PushFailure(
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

	private static string DescribeError(Exception exception) =>
		Environment.GetEnvironmentVariable("PI_DEBUG_ERRORS") == "1" ? exception.ToString() : exception.Message;

	private static string Truncate(string value, int max) =>
		value.Length <= max ? value : string.Concat(value.AsSpan(0, max), "…");
}
