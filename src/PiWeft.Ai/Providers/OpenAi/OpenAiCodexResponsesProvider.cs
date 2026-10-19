using System.Net;
using System.Net.ServerSentEvents;
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

			var messages = Transcript.Normalize(context);
			var tools = Transcript.GetTools(messages);
			var reasoningEffort = OpenAiResponsesProvider.ResolveReasoningEffort(model, options);
			var maxOutputTokens = options.MaxTokens ?? (model.MaxTokens > 0 ? model.MaxTokens : null);
			var cacheKey = options.SessionId ?? options.Metadata?["prompt_cache_key"]?.GetValue<string>();

			using var request = BuildRequest(
				model,
				messages,
				tools,
				options,
				reasoningEffort,
				maxOutputTokens,
				cacheKey,
				accountId,
				credentials.Access);

			using var response = await _http
				.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headerTimeout.Token)
				.ConfigureAwait(false);

			options.OnResponse?.Invoke(ProviderHttp.FromResponse(response), model);

			if (!response.IsSuccessStatusCode)
			{
				var body = await response.Content.ReadAsStringAsync(options.CancellationToken).ConfigureAwait(false);
				PushFailure(stream, model, startedAt, $"Codex request failed ({(int)response.StatusCode}): {Truncate(body, 2000)}");
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

	private static HttpRequestMessage BuildRequest(
		Model model,
		IReadOnlyList<Message> messages,
		IReadOnlyList<Tool> tools,
		StreamOptions options,
		string? reasoningEffort,
		int? maxOutputTokens,
		string? cacheKey,
		string accountId,
		string accessToken)
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
