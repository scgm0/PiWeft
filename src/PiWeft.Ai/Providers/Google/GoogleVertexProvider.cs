using System.Net.Http.Headers;
using System.Net.ServerSentEvents;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using PiWeft.Ai.Json;
namespace PiWeft.Ai.Providers.Google;

/// <summary>
/// Vertex AI（<c>google-vertex</c>）的流式适配器。它与 <see cref="GoogleGenerativeAiProvider"/> 使用相同的
/// generateContent 线格式，区别在端点（项目、区域、发布方）、模型路径
/// 与鉴权方式（ADC bearer 令牌，或 Vertex API key）。
/// </summary>
public sealed class GoogleVertexProvider : IApiProvider
{
	public const string ApiId = "google-vertex";
	public const string DefaultLocation = "us-central1";

	private static readonly byte[] DoneMarker = "[DONE]"u8.ToArray();

	private readonly HttpClient _http;
	private readonly HttpClient _tokenHttp;

	public GoogleVertexProvider(HttpClient? httpClient = null)
	{
		_http = httpClient ?? SharedHttpClient.Instance;

		// 令牌交换不经过调用方提供的处理器，因为那个处理器只服务于推理流量。
		_tokenHttp = httpClient is null ? SharedHttpClient.Instance : new HttpClient();
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
			var apiKey = ResolveApiKey(options);
			var bearer = apiKey is null
				? await GoogleAdc.GetAccessTokenAsync(_tokenHttp, cancellationToken).ConfigureAwait(false)
				: null;

			var messages = Transcript.Normalize(context, model);
			var tools = Transcript.GetTools(messages);
			var thinkingBudget = GoogleGenerativeAiProvider.ResolveThinkingBudget(model, options);
			var maxOutputTokens = options.MaxTokens ?? (model.MaxTokens > 0 ? model.MaxTokens : null);

			using var request = BuildRequest(
				model,
				messages,
				tools,
				options,
				thinkingBudget,
				maxOutputTokens,
				apiKey,
				bearer);

			using var response = await _http
				.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headerTimeout.Token)
				.ConfigureAwait(false);

			options.OnResponse?.Invoke(ProviderHttp.FromResponse(response), model);

			if (!response.IsSuccessStatusCode)
			{
				var body = await response.Content.ReadAsStringAsync(options.CancellationToken).ConfigureAwait(false);
				PushFailure(
					stream,
					model,
					startedAt,
					$"Vertex AI request failed ({(int)response.StatusCode}): {Truncate(body, 2000)}");
				return;
			}

			await using var bodyStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

			var state = new GoogleStreamState(model, stream, startedAt, ApiId);
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
		int? thinkingBudget,
		int? maxOutputTokens,
		string? apiKey,
		string? bearer)
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

		var url = BuildUrl(model, options);
		var request = new HttpRequestMessage(HttpMethod.Post, url)
		{
			Content = new ByteArrayContent(bytes),
		};

		request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
		request.Headers.TryAddWithoutValidation("accept", "text/event-stream");
		request.Headers.TryAddWithoutValidation("user-agent", SharedHttpClient.UserAgent);

		if (apiKey is { Length: > 0 })
		{
			// Vertex 的 express 模式接受 API key，而不需要 bearer 令牌。
			request.Headers.TryAddWithoutValidation("x-goog-api-key", apiKey);
		}
		else if (bearer is { Length: > 0 })
		{
			request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
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
	/// 构建 Vertex 的 generateContent URL。目录里给的是带 <c>{location}</c> 的模板，
	/// 在这里做替换；<c>global</c> 是合法取值，此时不加区域前缀。
	/// </summary>
	internal static string BuildUrl(Model model, StreamOptions options)
	{
		var location = ResolveLocation(options);
		var project = ResolveProject();

		var template = string.IsNullOrEmpty(model.BaseUrl)
			? "https://{location}-aiplatform.googleapis.com"
			: model.BaseUrl;

		var baseUrl = template.Contains("{location}", StringComparison.Ordinal)
			? template.Replace("{location}", location, StringComparison.Ordinal)
			: template;

		if (location == "global")
		{
			baseUrl = baseUrl.Replace("global-aiplatform.googleapis.com", "aiplatform.googleapis.com", StringComparison.Ordinal);
		}

		baseUrl = baseUrl.TrimEnd('/');

		return $"{baseUrl}/v1/projects/{Uri.EscapeDataString(project)}/locations/{Uri.EscapeDataString(location)}"
			+ $"/publishers/google/models/{Uri.EscapeDataString(model.Id)}:streamGenerateContent?alt=sse";
	}

	internal static string ResolveLocation(StreamOptions options) =>
		options.Metadata?["location"]?.GetValue<string>() is { Length: > 0 } fromOptions
			? fromOptions
			: Environment.GetEnvironmentVariable("GOOGLE_CLOUD_LOCATION") is { Length: > 0 } location
				? location
				: Environment.GetEnvironmentVariable("GOOGLE_CLOUD_REGION") is { Length: > 0 } region
					? region
					: DefaultLocation;

	internal static string ResolveProject() =>
		Environment.GetEnvironmentVariable("GOOGLE_CLOUD_PROJECT") is { Length: > 0 } project
			? project
			: Environment.GetEnvironmentVariable("GOOGLE_PROJECT_ID") is { Length: > 0 } fallback
				? fallback
				: throw new InvalidOperationException(
					"Vertex AI requires a project. Set GOOGLE_CLOUD_PROJECT or GOOGLE_PROJECT_ID.");

	private static string? ResolveApiKey(StreamOptions options) =>
		!string.IsNullOrEmpty(options.ApiKey)
			? options.ApiKey
			: Environment.GetEnvironmentVariable("GOOGLE_CLOUD_API_KEY") is { Length: > 0 } key
				? key
				: null;

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
