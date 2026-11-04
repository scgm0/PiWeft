using System.Net.Http.Headers;
using System.Net.ServerSentEvents;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using PiWeft.Ai.Json;
namespace PiWeft.Ai.Providers.Google;

/// Vertex AI（<c>google-vertex</c>）的流式适配器。它与 <see cref="GoogleGenerativeAiProvider"/> 使用相同的
/// generateContent 线格式，区别在端点（项目、区域、发布方）、模型路径
/// 与鉴权方式（ADC bearer 令牌，或 Vertex API key）。
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
			var apiKey = await ResolveApiKeyAsync(model, options, cancellationToken).ConfigureAwait(false);
			// API key 走 express，不需要项目/区域；ADC 一次读盘后同时供令牌与 URL 使用。
			var stored = apiKey is null ? ProviderEnvironment.StoredEnvironment(model.Provider) : null;
			var bearer = apiKey is null
				? await GoogleAdc.GetAccessTokenAsync(_tokenHttp, stored, cancellationToken).ConfigureAwait(false)
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
				bearer,
				stored);

			using var response = await _http
				.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headerTimeout.Token)
				.ConfigureAwait(false);
			headerTimeout.Stop();

			options.OnResponse?.Invoke(ProviderHttp.FromResponse(response), model);

			if (!response.IsSuccessStatusCode)
			{
				var body = await ProviderHttp.ReadBodyTextAsync(response, options, options.CancellationToken).ConfigureAwait(false);
				PushFailure(
					stream,
					model,
					startedAt,
					$"Vertex AI request failed ({(int)response.StatusCode}): {Truncate(body, 2000)}");
				return;
			}

			await using var bodyStream = await ProviderHttp.ReadBodyAsync(response, options, cancellationToken).ConfigureAwait(false);

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
		string? bearer,
		IReadOnlyDictionary<string, string>? stored)
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

		var url = BuildUrl(model, options, apiKey is not null, stored);
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

	/// 构建 Vertex 的 generateContent URL。目录里给的是带 <c>{location}</c> 的模板，
	/// 在这里做替换；<c>global</c> 是合法取值，此时不加区域前缀。
	internal static string BuildUrl(Model model, StreamOptions options) =>
		BuildUrl(model, options, express: false, stored: null);

	internal static string BuildUrl(Model model, StreamOptions options, bool express) =>
		BuildUrl(model, options, express, stored: null);

	internal static string BuildUrl(Model model, StreamOptions options, bool express, IReadOnlyDictionary<string, string>? stored)
	{
		var template = string.IsNullOrEmpty(model.BaseUrl)
			? "https://{location}-aiplatform.googleapis.com"
			: model.BaseUrl;

		if (express)
		{
			// Vertex Express（API key）由 Google 从 key 解析项目，不需要 project/location 路径；
			// 这与 pi 的 google-vertex API key 分支一致。
			var expressBase = template.Contains("{location}", StringComparison.Ordinal)
				? "https://aiplatform.googleapis.com"
				: template;
			return expressBase.TrimEnd('/') +
				$"/v1/publishers/google/models/{Uri.EscapeDataString(model.Id)}:streamGenerateContent?alt=sse";
		}

		var location = ResolveLocationFromEnvironment(options, stored);
		var project = ResolveProjectFromEnvironment(stored);

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
		ResolveLocation(options, provider: null);

	internal static string ResolveLocation(StreamOptions options, string? provider) =>
		ResolveLocationFromEnvironment(options,
			provider is null ? null : ProviderEnvironment.StoredEnvironment(provider));

	private static string ResolveLocationFromEnvironment(
		StreamOptions options,
		IReadOnlyDictionary<string, string>? stored)
	{
		string? Value(string name) => ProviderEnvironment.EnvironmentValue(stored, name);

		return options.Metadata?["location"]?.GetValue<string>() is { Length: > 0 } fromOptions
			? fromOptions
			: Value("GOOGLE_CLOUD_LOCATION") is { Length: > 0 } location
				? location
				: Value("GOOGLE_CLOUD_REGION") is { Length: > 0 } region
					? region
					: DefaultLocation;
	}

	internal static string ResolveProject() => ResolveProject(provider: null);

	internal static string ResolveProject(string? provider) =>
		ResolveProjectFromEnvironment(provider is null ? null : ProviderEnvironment.StoredEnvironment(provider));

	private static string ResolveProjectFromEnvironment(IReadOnlyDictionary<string, string>? stored)
	{
		string? Value(string name) => ProviderEnvironment.EnvironmentValue(stored, name);

		return Value("GOOGLE_CLOUD_PROJECT") is { Length: > 0 } project
			? project
			: Value("GOOGLE_PROJECT_ID") is { Length: > 0 } fallback
				? fallback
				: throw new InvalidOperationException(
					"Vertex AI requires a project. Set GOOGLE_CLOUD_PROJECT or GOOGLE_PROJECT_ID.");
	}

	private static async Task<string?> ResolveApiKeyAsync(Model model, StreamOptions options, CancellationToken cancellationToken) =>
		!string.IsNullOrEmpty(options.ApiKey)
			? options.ApiKey
			: await ProviderEnvironment.ResolveApiKeyAsync(model.Provider, options, cancellationToken).ConfigureAwait(false)
				?? ProviderEnvironment.EnvironmentValue(model.Provider, "GOOGLE_CLOUD_API_KEY");

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
