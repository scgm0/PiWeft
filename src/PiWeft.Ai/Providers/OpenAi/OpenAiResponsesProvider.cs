using System.Net.Http.Headers;
using System.Net.ServerSentEvents;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using PiWeft.Ai.Json;
using PiWeft.Ai.Utils;
namespace PiWeft.Ai.Providers.OpenAi;

/// OpenAI Responses API（<c>openai-responses</c>）的流式适配器。GPT-5 级别与推理模型使用该 API，
/// 它也覆盖 Azure Responses 与 OpenAI Codex 的端点形状。
public sealed class OpenAiResponsesProvider : IApiProvider
{
	public const string ApiId = "openai-responses";

	private static readonly byte[] DoneMarker = "[DONE]"u8.ToArray();

	private readonly HttpClient _http;

	public OpenAiResponsesProvider(HttpClient? httpClient = null) =>
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
			var messages = Transcript.Normalize(context, model);
			var tools = Transcript.GetTools(messages);
			var reasoningEffort = ResolveReasoningEffort(model, options);
			var apiKey = await ProviderEnvironment
				.ResolveApiKeyAsync(model.Provider, options, cancellationToken)
				.ConfigureAwait(false);

			using var request = BuildRequest(model, messages, tools, options, reasoningEffort, apiKey);

			using var response = await _http
				.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headerTimeout.Token)
				.ConfigureAwait(false);
			headerTimeout.Stop();

			options.OnResponse?.Invoke(ProviderHttp.FromResponse(response), model);

			if (!response.IsSuccessStatusCode)
			{
				var body = await ProviderHttp.ReadBodyTextAsync(response, options, options.CancellationToken).ConfigureAwait(false);
				PushFailure(stream, model, startedAt, $"Responses request failed ({(int)response.StatusCode}): {Truncate(body, 2000)}");
				return;
			}

			await using var bodyStream = await ProviderHttp.ReadBodyAsync(response, options, cancellationToken).ConfigureAwait(false);

			var state = new OpenAiResponsesStreamState(model, stream, startedAt);
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
			PushFailure(stream, model, startedAt, ex.Message);
		}
	}

	private static HttpRequestMessage BuildRequest(
		Model model,
		IReadOnlyList<Message> messages,
		IReadOnlyList<Tool> tools,
		StreamOptions options,
		string? reasoningEffort,
		string? apiKey)
	{
		var maxOutputTokens = OpenAiContextBudget.Clamp(model, messages, options.MaxTokens ?? (model.MaxTokens > 0 ? model.MaxTokens : null));
		var cacheRetention = options.CacheRetention ?? CacheRetentions.Short;
		var promptCacheMode = ModelCompat.Text(model, "promptCacheKeyMode") ?? "auto";
		// 与 pi-ai 一致：cacheRetention=none 或显式 disabled 时不带 key。
		var promptCacheKey = cacheRetention == CacheRetentions.None ||
			string.Equals(promptCacheMode, "disabled", StringComparison.OrdinalIgnoreCase) ||
			string.IsNullOrEmpty(options.SessionId)
			? null
			: options.SessionId.Length <= 64 ? options.SessionId : options.SessionId[..64];
		var supportsLongCacheRetention = ModelCompat.FlagOr(model, "supportsLongCacheRetention", true);
		var explicitPromptCache = ModelCompat.FlagOr(model, "supportsExplicitPromptCacheMode", false);
		var promptCacheRetention = !explicitPromptCache && cacheRetention == CacheRetentions.Long && supportsLongCacheRetention
			? "24h"
			: null;
		JsonObject? promptCacheOptions = explicitPromptCache
			? cacheRetention == CacheRetentions.None
				? new JsonObject { ["mode"] = "explicit" }
				: cacheRetention == CacheRetentions.Long && supportsLongCacheRetention
					? new JsonObject { ["ttl"] = "30m" }
					: null
			: null;
		var bytes = JsonPayload.Write(writer =>
			OpenAiResponsesWire.WriteRequest(writer, model, messages, tools, options, reasoningEffort, maxOutputTokens,
				promptCacheKey: promptCacheKey,
				promptCacheRetention: promptCacheRetention,
				promptCacheOptions: promptCacheOptions,
				supportsStrictMode: ModelCompat.Flag(model, "supportsStrictMode") ?? false));

		if (options.OnPayload is { } onPayload)
		{
			var node = JsonNode.Parse(bytes);
			if (node is not null && onPayload(node, model) is { } replaced)
			{
				bytes = Encoding.UTF8.GetBytes(replaced.ToJsonString(PiJson.NodeOptions));
			}
		}

		var baseUrl = ProviderEnvironment.ExpandBaseUrl(model.Provider, model.BaseUrl).TrimEnd('/');
		var request = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/responses")
		{
			Content = new ByteArrayContent(bytes),
		};

		request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
		request.Headers.TryAddWithoutValidation("accept", "text/event-stream");
		request.Headers.TryAddWithoutValidation("user-agent", SharedHttpClient.UserAgent);

		if (apiKey is { Length: > 0 })
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

		ProviderSessionHeaders.ApplyResponses(model, options, request);
		CopilotHeaders.Apply(model, messages, request);
		OpenCodeHeaders.Apply(model, options, request);
		return request;
	}

	/// 把 pi 的思考级别映射到 Responses 的 <c>reasoning.effort</c>。返回 <c>null</c> 表示不带 reasoning 字段，
	/// 这只会发生在非推理模型上。
	internal static string? ResolveReasoningEffort(Model model, StreamOptions options)
	{
		if (!model.Reasoning)
		{
			return null;
		}

		var level = ThinkingLevelResolver.Clamp(model, options.ThinkingLevel ?? ThinkingLevels.Medium);
		if (model.ThinkingLevelMap?.TryGetValue(level, out var mapped) == true)
		{
			mapped ??= "none";
			return mapped;
		}

		return level == ThinkingLevels.Off ? "none" : level;
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

	private static string Truncate(string value, int max) =>
		value.Length <= max ? value : string.Concat(value.AsSpan(0, max), "…");
}
