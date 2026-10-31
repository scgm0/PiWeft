using System.Net.Http.Headers;
using System.Net.ServerSentEvents;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using PiWeft.Ai.Json;
namespace PiWeft.Ai.Providers.Google;

/// <summary>
/// Google Generative Language API（<c>google-generative-ai</c>）的流式适配器，覆盖 Gemini 系列。
/// 配置好端点与鉴权后，同样的线格式也服务于 <c>google-vertex</c>。
/// </summary>
public sealed class GoogleGenerativeAiProvider : IApiProvider
{
	public const string ApiId = "google-generative-ai";

	private static readonly byte[] DoneMarker = "[DONE]"u8.ToArray();

	private readonly HttpClient _http;

	public GoogleGenerativeAiProvider(HttpClient? httpClient = null) =>
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
			var thinkingBudget = ResolveThinkingBudget(model, options);
			var maxOutputTokens = options.MaxTokens ?? (model.MaxTokens > 0 ? model.MaxTokens : null);

			using var request = BuildRequest(model, messages, tools, options, thinkingBudget, maxOutputTokens);

			using var response = await _http
				.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headerTimeout.Token)
				.ConfigureAwait(false);

			options.OnResponse?.Invoke(ProviderHttp.FromResponse(response), model);

			if (!response.IsSuccessStatusCode)
			{
				var body = await ProviderHttp.ReadBodyTextAsync(response, options, options.CancellationToken).ConfigureAwait(false);
				PushFailure(stream, model, startedAt, $"Gemini request failed ({(int)response.StatusCode}): {Truncate(body, 2000)}");
				return;
			}

			await using var bodyStream = await ProviderHttp.ReadBodyAsync(response, options, cancellationToken).ConfigureAwait(false);

			var state = new GoogleStreamState(model, stream, startedAt);
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
		int? thinkingBudget,
		int? maxOutputTokens)
	{
		var bytes = JsonPayload.Write(writer =>
			GoogleWire.WriteRequest(writer, model, messages, tools, options, thinkingBudget, maxOutputTokens));

		if (options.OnPayload is { } onPayload)
		{
			var node = JsonNode.Parse(bytes);
			if (node is not null && onPayload(node, model) is { } replaced)
			{
				bytes = Encoding.UTF8.GetBytes(replaced.ToJsonString(PiJson.NodeOptions));
			}
		}

		var baseUrl = string.IsNullOrEmpty(model.BaseUrl)
			? "https://generativelanguage.googleapis.com/v1beta"
			: model.BaseUrl.TrimEnd('/');

		// Gemini 把模型名放在路径里；设置 alt=sse 时以 SSE 流式返回。
		var url = $"{baseUrl}/models/{Uri.EscapeDataString(model.Id)}:streamGenerateContent?alt=sse";
		var request = new HttpRequestMessage(HttpMethod.Post, url)
		{
			Content = new ByteArrayContent(bytes),
		};

		request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
		request.Headers.TryAddWithoutValidation("accept", "text/event-stream");
		request.Headers.TryAddWithoutValidation("user-agent", SharedHttpClient.UserAgent);

		if (ProviderEnvironment.ResolveApiKey(model.Provider, options) is { Length: > 0 } apiKey)
		{
			request.Headers.TryAddWithoutValidation("x-goog-api-key", apiKey);
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
	/// 把 pi 的思考级别映射到 Gemini 的 <c>thinkingConfig.thinkingBudget</c>。返回 <c>null</c> 表示不带该字段，
	/// 这正是非推理模型所期望的。
	/// </summary>
	internal static int? ResolveThinkingBudget(Model model, StreamOptions options)
	{
		if (!model.Reasoning)
		{
			return null;
		}

		var level = ThinkingLevelResolver.Clamp(model, options.ThinkingLevel ?? ThinkingLevels.Medium);

		// Gemini 用零预算表示关闭思考；目录里已有映射值时按原值使用。
		if (model.ThinkingLevelMap?.TryGetValue(level, out var mapped) == true &&
			mapped is { Length: > 0 } &&
			int.TryParse(mapped, out var mappedBudget))
		{
			return mappedBudget;
		}

		if (level == ThinkingLevels.Off)
		{
			return 0;
		}

		return level switch
		{
			ThinkingLevels.Minimal => 512,
			ThinkingLevels.Low => 2048,
			ThinkingLevels.Medium => 8192,
			ThinkingLevels.High => 16384,
			ThinkingLevels.XHigh => 24576,
			ThinkingLevels.Max => 32768,
			_ => 8192,
		};
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
