using System.Net.Http.Headers;
using System.Net.ServerSentEvents;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiWeft.Ai.Providers.OpenAi;

using PiWeft.Ai.Json;
namespace PiWeft.Ai.Providers.Mistral;

/// <summary>
/// Mistral 对话补全 API（<c>mistral-conversations</c>）的流式适配器。请求与工具形状紧跟 OpenAI，
/// 但流里的 <c>delta.content</c> 既可能是字符串，也可能是分片列表（含推理内容），
/// 并且结束原因使用 Mistral 自己的词汇表。
/// </summary>
public sealed class MistralConversationsProvider : IApiProvider
{
	public const string ApiId = "mistral-conversations";

	private static readonly byte[] DoneMarker = "[DONE]"u8.ToArray();

	private readonly HttpClient _http;

	public MistralConversationsProvider(HttpClient? httpClient = null) =>
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
			var effort = ResolveReasoningEffort(model, options);

			using var request = BuildRequest(model, messages, tools, options, effort);

			using var response = await _http
				.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headerTimeout.Token)
				.ConfigureAwait(false);

			options.OnResponse?.Invoke(ProviderHttp.FromResponse(response), model);

			if (!response.IsSuccessStatusCode)
			{
				var body = await response.Content.ReadAsStringAsync(options.CancellationToken).ConfigureAwait(false);
				PushFailure(stream, model, startedAt, $"Mistral request failed ({(int)response.StatusCode}): {Truncate(body, 2000)}");
				return;
			}

			await using var bodyStream = await ProviderHttp.ReadBodyAsync(response, options, cancellationToken).ConfigureAwait(false);

			var state = new MistralStreamState(model, stream, startedAt);
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
		string? reasoningEffort)
	{
		var bytes = JsonPayload.Write(writer =>
		{
			writer.WriteStartObject();
			writer.WriteString("model", model.Id);
			writer.WriteBoolean("stream", true);

			var maxTokens = options.MaxTokens ?? (model.MaxTokens > 0 ? model.MaxTokens : null);
			if (maxTokens is { } tokens)
			{
				// Mistral 只认 max_tokens。
				writer.WriteNumber("max_tokens", tokens);
			}

			if (options.Temperature is { } temperature)
			{
				writer.WriteNumber("temperature", temperature);
			}

			if (reasoningEffort is not null)
			{
				// 带思考级别映射的模型用 reasoning_effort，其它用 prompt_mode。
				if (model.ThinkingLevelMap is { Count: > 0 })
				{
					writer.WriteString("reasoning_effort", reasoningEffort);
				}
				else
				{
					writer.WriteString("prompt_mode", reasoningEffort == ThinkingLevels.Off ? "reasoning" : "reasoning");
				}
			}

			writer.WritePropertyName("messages");
			OpenAiCompletionsWire.WriteMessages(writer, messages);

			if (tools.Count > 0)
			{
				OpenAiCompletionsWire.WriteTools(writer, tools);
			}

			if (options.Metadata?["toolChoice"] is JsonValue toolChoice &&
				toolChoice.TryGetValue<string>(out var choice))
			{
				writer.WriteString(
					"tool_choice",
					choice switch
					{
						"none" => "none",
						"any" => "any",
						"required" => "required",
						_ => "auto",
					});
			}

			if (options.SamplingParams is { Count: > 0 } sampling)
			{
				foreach (var (key, value) in sampling)
				{
					if (value is null)
					{
						continue;
					}

					writer.WritePropertyName(key);
					value.WriteTo(writer);
				}
			}

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
		var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/chat/completions")
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

	/// <summary>Mistral 的推理控制参数：有映射值就用映射值，否则用 pi 的级别。</summary>
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

		return level;
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
