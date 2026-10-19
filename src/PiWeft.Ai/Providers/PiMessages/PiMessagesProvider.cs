using System.Net.Http.Headers;
using System.Net.ServerSentEvents;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using PiWeft.Ai.Json;
namespace PiWeft.Ai.Providers.PiMessages;

/// <summary>
/// <c>pi-messages</c> 协议的流式适配器：向 <c>&lt;baseUrl&gt;/messages</c> 发一次
/// <c>{ model, context, options }</c> POST，响应是序列化助手事件的 SSE 流。
/// pi-messages 后端（或 <c>models.json</c> 中 <c>"api": "pi-messages"</c> 的条目）就说这种协议。
/// </summary>
public sealed class PiMessagesProvider : IApiProvider
{
	public const string ApiId = "pi-messages";

	private static readonly byte[] DoneMarker = "[DONE]"u8.ToArray();

	private readonly HttpClient _http;

	public PiMessagesProvider(HttpClient? httpClient = null) =>
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
			var apiKey = ProviderEnvironment.ResolveApiKey(model.Provider, options);
			if (string.IsNullOrEmpty(apiKey))
			{
				PushFailure(stream, model, startedAt, $"No API key provided for provider \"{model.Provider}\"");
				return;
			}

			using var request = BuildRequest(model, context, options, apiKey);

			using var response = await _http
				.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headerTimeout.Token)
				.ConfigureAwait(false);

			options.OnResponse?.Invoke(ProviderHttp.FromResponse(response), model);

			if (!response.IsSuccessStatusCode)
			{
				var body = await response.Content.ReadAsStringAsync(options.CancellationToken).ConfigureAwait(false);
				PushFailure(stream, model, startedAt, FormatError(response, body));
				return;
			}

			await using var bodyStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

			var state = new PiMessagesStreamState(model, stream, startedAt);
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

	private static HttpRequestMessage BuildRequest(Model model, Context context, StreamOptions options, string apiKey)
	{
		var bytes = JsonPayload.Write(writer =>
		{
			writer.WriteStartObject();
			writer.WriteString("model", model.Id);

			writer.WritePropertyName("context");
			writer.WriteStartObject();
			if (!string.IsNullOrEmpty(context.SystemPrompt))
			{
				writer.WriteString("systemPrompt", context.SystemPrompt);
			}

			writer.WritePropertyName("messages");
			writer.WriteStartArray();
			foreach (var message in MessageTransform.Transform(context.Messages, model))
			{
				// 后端期望收到完整会话记录，因此 system 消息保持原位置。
				PiWeft.Ai.Json.MessageJsonConverter.WriteMessage(writer, message);
			}

			writer.WriteEndArray();

			if (context.Tools is { Count: > 0 } tools)
			{
				writer.WritePropertyName("tools");
				writer.WriteStartArray();
				foreach (var tool in tools)
				{
					writer.WriteStartObject();
					writer.WriteString("name", tool.Name);
					writer.WriteString("description", tool.Description);
					writer.WritePropertyName("parameters");
					tool.Parameters.WriteTo(writer);
					writer.WriteEndObject();
				}

				writer.WriteEndArray();
			}

			writer.WriteEndObject();

			writer.WritePropertyName("options");
			writer.WriteStartObject();
			if (options.Temperature is { } temperature)
			{
				writer.WriteNumber("temperature", temperature);
			}

			if (options.MaxTokens is { } maxTokens)
			{
				writer.WriteNumber("maxTokens", maxTokens);
			}

			if (options.ThinkingLevel is { Length: > 0 } thinking)
			{
				writer.WriteString("reasoning", thinking);
			}

			if (options.CacheRetention is { Length: > 0 } retention)
			{
				writer.WriteString("cacheRetention", retention);
			}

			if (options.SessionId is { Length: > 0 } sessionId)
			{
				writer.WriteString("sessionId", sessionId);
			}

			writer.WriteEndObject();
			writer.WriteEndObject();
		});

		if (options.OnPayload is { } onPayload)
		{
			var node = JsonNode.Parse(bytes);
			if (node is not null && onPayload(node, model) is { } replaced)
			{
				bytes = Encoding.UTF8.GetBytes(replaced.ToJsonString(PiJson.NodeOptions));
			}
		}

		var baseUrl = model.BaseUrl.TrimEnd('/');
		var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/messages")
		{
			Content = new ByteArrayContent(bytes),
		};

		request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
		request.Headers.TryAddWithoutValidation("accept", "text/event-stream");
		request.Headers.TryAddWithoutValidation("user-agent", SharedHttpClient.UserAgent);
		request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

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

	private static string FormatError(HttpResponseMessage response, string body)
	{
		try
		{
			var parsed = JsonNode.Parse(body) as JsonObject;
			var message = parsed?["error"]?["message"]?.GetValue<string>();
			var code = parsed?["error"]?["code"]?.GetValue<string>();
			if (message is { Length: > 0 })
			{
				return $"{(int)response.StatusCode} {response.ReasonPhrase}: {message}{(code is { Length: > 0 } ? $" ({code})" : string.Empty)}";
			}
		}
		catch (JsonException)
		{
			// 兜底：使用原始响应体。
		}

		return $"{(int)response.StatusCode} {response.ReasonPhrase}: {Truncate(body, 500)}";
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
