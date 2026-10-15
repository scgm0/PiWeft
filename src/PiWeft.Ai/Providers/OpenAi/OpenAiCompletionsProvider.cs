using System.Net.Http.Headers;
using System.Net.ServerSentEvents;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using PiWeft.Ai.Json;
namespace PiWeft.Ai.Providers.OpenAi;

/// <summary>
/// OpenAI 兼容的 Chat Completions API（<c>openai-completions</c>）的流式适配器，既覆盖 OpenAI 本身，
/// 也覆盖所有使用同一线格式的服务端：DeepSeek、Groq、xAI、OpenRouter、Mistral、vLLM、llama.cpp、Ollama。
/// </summary>
public sealed class OpenAiCompletionsProvider : IApiProvider
{
	public const string ApiId = "openai-completions";

	private static readonly byte[] DoneMarker = "[DONE]"u8.ToArray();

	private readonly HttpClient _http;

	public OpenAiCompletionsProvider(HttpClient? httpClient = null) =>
		_http = httpClient ?? SharedHttpClient.Instance;

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
			var messages = Transcript.Normalize(context);
			var tools = Transcript.GetTools(messages);
			var reasoningEffort = ResolveReasoningEffort(model, options);

			using var request = BuildRequest(model, messages, tools, options, reasoningEffort);

			using var response = await _http
				.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headerTimeout.Token)
				.ConfigureAwait(false);

			options.OnResponse?.Invoke(ProviderHttp.FromResponse(response), model);

			if (!response.IsSuccessStatusCode)
			{
				var body = await response.Content.ReadAsStringAsync(options.CancellationToken).ConfigureAwait(false);
				stream.Push(new ErrorEvent
				{
					Reason = StopReason.Error,
					Partial = Failure(model, startedAt, $"OpenAI-compatible request failed ({(int)response.StatusCode}): {Truncate(body, 2000)}"),
				});
				return;
			}

			await using var bodyStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

			var state = new OpenAiCompletionsStreamState(model, stream, startedAt);
			var parser = SseParser.Create<JsonDocument?>(bodyStream, static (eventType, span) =>
			{
				if (span.SequenceEqual(DoneMarker))
				{
					return null;
				}

				try
				{
					return JsonDocument.Parse(span.ToArray());
				}
				catch (JsonException ex)
				{
					throw new InvalidOperationException(
						$"Could not parse provider SSE event '{eventType}': {ex.Message}",
						ex);
				}
			});

			await foreach (var item in parser.EnumerateAsync(cancellationToken).ConfigureAwait(false))
			{
				if (item.Data is null)
				{
					break; // data: [DONE]
				}

				using var document = item.Data;
				options.OnProviderStreamEvent?.Invoke(JsonNode.Parse(document.RootElement.GetRawText())!, model);
				state.Handle(document.RootElement);
			}

			state.Finish();
		}
		catch (OperationCanceledException) when (options.CancellationToken.IsCancellationRequested)
		{
			stream.Push(new ErrorEvent { Reason = StopReason.Aborted, Partial = Failure(model, startedAt, "Request aborted") });
		}
		catch (OperationCanceledException)
		{
			stream.Push(new ErrorEvent { Reason = StopReason.Error, Partial = Failure(model, startedAt, ProviderHttp.TimeoutMessage(headerTimeout)) });
		}
		catch (Exception ex)
		{
			stream.Push(new ErrorEvent { Reason = StopReason.Error, Partial = Failure(model, startedAt, ex.Message) });
		}
	}

	private static HttpRequestMessage BuildRequest(
		Model model,
		IReadOnlyList<Message> messages,
		IReadOnlyList<Tool> tools,
		StreamOptions options,
		string? reasoningEffort)
	{
		var maxTokens = options.MaxTokens ?? (model.MaxTokens > 0 ? model.MaxTokens : null);
		var bytes = JsonPayload.Write(writer =>
			OpenAiCompletionsWire.WriteRequest(writer, model, messages, tools, options, reasoningEffort, maxTokens));

		if (options.OnPayload is { } onPayload)
		{
			var node = JsonNode.Parse(bytes);
			if (node is not null && onPayload(node, model) is { } replaced)
			{
				bytes = Encoding.UTF8.GetBytes(replaced.ToJsonString(PiJson.NodeOptions));
			}
		}

		var baseUrl = model.BaseUrl.TrimEnd('/');
		var request = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/chat/completions")
		{
			Content = new ByteArrayContent(bytes),
		};

		request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
		request.Headers.TryAddWithoutValidation("accept", "text/event-stream");
		request.Headers.TryAddWithoutValidation("user-agent", SharedHttpClient.UserAgent);

		if (ProviderEnvironment.ResolveApiKey(model.Provider, options) is { Length: > 0 } apiKey)
		{
			request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
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

	/// <summary>把 pi 的思考级别映射到 provider 的 <c>reasoning_effort</c>；返回 <c>null</c> 表示不带该字段。</summary>
	internal static string? ResolveReasoningEffort(Model model, StreamOptions options)
	{
		if (!model.Reasoning)
		{
			return null;
		}

		var level = ThinkingLevelResolver.Clamp(model, options.ThinkingLevel ?? ThinkingLevels.Medium);
		if (model.ThinkingLevelMap?.TryGetValue(level, out var mapped) == true)
		{
			return mapped;
		}

		return level == ThinkingLevels.Off ? null : level;
	}

	private static AssistantMessage Failure(Model model, long startedAt, string message) => new()	{
		Api = ApiId,
		Provider = model.Provider,
		Model = model.Id,
		Timestamp = startedAt,
		StopReason = StopReason.Error,
		ErrorMessage = message,
	};

	private static string Truncate(string value, int max) =>
		value.Length <= max ? value : string.Concat(value.AsSpan(0, max), "…");
}
