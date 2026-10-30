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

	/// <summary>目录里 <c>compat.thinkingFormat</c> 的取值：用 <c>thinking.type</c> 开关思考。</summary>
	private const string DeepSeekThinkingFormat = "deepseek";

	private const string ThinkingEnabled = "enabled";
	private const string ThinkingDisabled = "disabled";

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
			var messages = Transcript.Normalize(context, model);
			var tools = Transcript.GetTools(messages);
			var thinkingMode = ResolveThinkingMode(model, options);
			// 关闭思考时不下发强度：两个字段同时出现时，端点的开关与档位可能互相矛盾。
			var reasoningEffort = thinkingMode == ThinkingDisabled ? null : ResolveReasoningEffort(model, options);

			var requestOptions = MergeModelSamplingParams(model, options);
			using var request = BuildRequest(model, messages, tools, requestOptions, reasoningEffort, thinkingMode);

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

			await using var bodyStream = await ProviderHttp.ReadBodyAsync(response, options, cancellationToken).ConfigureAwait(false);

			var state = new OpenAiCompletionsStreamState(model, stream, startedAt);
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

	private static StreamOptions MergeModelSamplingParams(Model model, StreamOptions options)
	{
		if (model.SamplingParams is not { Count: > 0 } modelParams)
		{
			return options;
		}

		var merged = (JsonObject)modelParams.DeepClone();
		foreach (var (key, value) in options.SamplingParams ?? new JsonObject())
		{
			merged[key] = value?.DeepClone();
		}

		return options with { SamplingParams = merged };
	}

	private static HttpRequestMessage BuildRequest(
		Model model,
		IReadOnlyList<Message> messages,
		IReadOnlyList<Tool> tools,
		StreamOptions options,
		string? reasoningEffort,
		string? thinkingMode)
	{
		var maxTokens = options.MaxTokens ?? (model.MaxTokens > 0 ? model.MaxTokens : null);
		var bytes = JsonPayload.Write(writer =>
			OpenAiCompletionsWire.WriteRequest(writer, model, messages, tools, options, reasoningEffort, thinkingMode, maxTokens));

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

	/// <summary>
	/// 用 <c>thinking.type</c> 显式开关思考的端点在「关闭」时返回 <c>disabled</c>，
	/// 其余思考档位返回 <c>enabled</c>；没有这个开关的端点返回 <c>null</c>。
	/// 目录里 <c>off: null</c> 表示「省略思考参数即关闭」，此时按 pi 的做法不下发开关。
	/// </summary>
	internal static string? ResolveThinkingMode(Model model, StreamOptions options)
	{
		if (!model.Reasoning || !UsesThinkingSwitch(model))
		{
			return null;
		}

		var level = ThinkingLevelResolver.Clamp(model, options.ThinkingLevel ?? ThinkingLevels.Medium);
		if (level != ThinkingLevels.Off)
		{
			return ThinkingEnabled;
		}

		var offDeclaredAsOmitted = model.ThinkingLevelMap?.TryGetValue(ThinkingLevels.Off, out var off) == true && off is null;
		return offDeclaredAsOmitted ? null : ThinkingDisabled;
	}

	/// <summary>
	/// 目录用 <c>compat.thinkingFormat</c> 声明思考字段的形状。<c>deepseek</c> 默认开启思考，
	/// 只发 <c>reasoning_effort</c> 关不掉它，必须带上 <c>thinking.type</c>。
	/// </summary>
	internal static bool UsesThinkingSwitch(Model model) =>
		model.Compat?["thinkingFormat"] is JsonValue value &&
		value.TryGetValue<string>(out var format) &&
		string.Equals(format, DeepSeekThinkingFormat, StringComparison.Ordinal);

	/// <summary>把 pi 的思考级别映射到 provider 的 <c>reasoning_effort</c>；返回 <c>null</c> 表示不带该字段。</summary>
	internal static string? ResolveReasoningEffort(Model model, StreamOptions options)
	{
		if (!model.Reasoning)
		{
			return null;
		}

		var level = ThinkingLevelResolver.Clamp(model, options.ThinkingLevel ?? ThinkingLevels.Medium);
		// 与 pi 的 `thinkingLevelMap?.[level] ?? level` 一致：映射值为 null 时按档位名原样发送。
		if (model.ThinkingLevelMap?.TryGetValue(level, out var mapped) == true && mapped is not null)
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
