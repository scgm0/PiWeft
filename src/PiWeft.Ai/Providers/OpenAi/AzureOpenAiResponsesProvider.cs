using System.Net.Http.Headers;
using System.Net.ServerSentEvents;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using PiWeft.Ai.Json;
namespace PiWeft.Ai.Providers.OpenAi;

/// <summary>
/// Azure OpenAI Responses API（<c>azure-openai-responses</c>）的流式适配器。它沿用 OpenAI Responses 的
/// 线格式，区别有三点：端点带 API 版本、凭据放在 <c>api-key</c> 头里，
/// 并且请求体里写的是 Azure 的<em>部署名</em>而不是模型名。
/// </summary>
public sealed class AzureOpenAiResponsesProvider : IApiProvider
{
	public const string ApiId = "azure-openai-responses";
	public const string DefaultApiVersion = "v1";

	private static readonly byte[] DoneMarker = "[DONE]"u8.ToArray();

	private readonly HttpClient _http;

	public AzureOpenAiResponsesProvider(HttpClient? httpClient = null) =>
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
			var apiKey = ResolveApiKey(model, options);
			if (string.IsNullOrEmpty(apiKey))
			{
				PushFailure(
					stream,
					model,
					startedAt,
					"Missing Azure OpenAI API key. Set AZURE_OPENAI_API_KEY or pass ApiKey.");
				return;
			}

			var messages = Transcript.Normalize(context, model);
			var tools = Transcript.GetTools(messages);
			var reasoningEffort = OpenAiResponsesProvider.ResolveReasoningEffort(model, options);
			var deployment = ResolveDeploymentName(model);
			var (baseUrl, apiVersion) = ResolveEndpoint(model, options);

			using var request = BuildRequest(
				model,
				messages,
				tools,
				options,
				reasoningEffort,
				deployment,
				baseUrl,
				apiVersion,
				apiKey);

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
					$"Azure OpenAI request failed ({(int)response.StatusCode}): {Truncate(body, 2000)}");
				return;
			}

			await using var bodyStream = await ProviderHttp.ReadBodyAsync(response, options, cancellationToken).ConfigureAwait(false);

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
		string deployment,
		string baseUrl,
		string apiVersion,
		string apiKey)
	{
		var maxOutputTokens = options.MaxTokens ?? (model.MaxTokens > 0 ? model.MaxTokens : null);
		var bytes = JsonPayload.Write(writer => OpenAiResponsesWire.WriteRequest(
			writer,
			model,
			messages,
			tools,
			options,
			reasoningEffort,
			maxOutputTokens,
			deployment));

		if (options.OnPayload is { } onPayload)
		{
			var node = JsonNode.Parse(bytes);
			if (node is not null && onPayload(node, model) is { } replaced)
			{
				bytes = Encoding.UTF8.GetBytes(replaced.ToJsonString(PiJson.NodeOptions));
			}
		}

		var url = $"{baseUrl}/responses?api-version={Uri.EscapeDataString(apiVersion)}";
		var request = new HttpRequestMessage(HttpMethod.Post, url)
		{
			Content = new ByteArrayContent(bytes),
		};

		request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
		request.Headers.TryAddWithoutValidation("accept", "text/event-stream");
		request.Headers.TryAddWithoutValidation("user-agent", SharedHttpClient.UserAgent);

		// Azure 用 api-key 头鉴权，而不是 bearer 令牌。
		request.Headers.TryAddWithoutValidation("api-key", apiKey);

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
	/// 解析 Azure 资源端点。部署是按客户划分的，因此目录里没有 base URL；
	/// 它来自模型配置、<c>AZURE_OPENAI_BASE_URL</c> 或 <c>AZURE_OPENAI_RESOURCE_NAME</c>。
	/// </summary>
	internal static (string BaseUrl, string ApiVersion) ResolveEndpoint(Model model, StreamOptions options)
	{
		var apiVersion = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_VERSION") is { Length: > 0 } version
			? version
			: DefaultApiVersion;

		var baseUrl = !string.IsNullOrEmpty(model.BaseUrl)
			? model.BaseUrl
			: Environment.GetEnvironmentVariable("AZURE_OPENAI_BASE_URL") is { Length: > 0 } configured
				? configured
				: Environment.GetEnvironmentVariable("AZURE_OPENAI_RESOURCE_NAME") is { Length: > 0 } resource
					? $"https://{resource}.openai.azure.com"
					: string.Empty;

		if (baseUrl.Length == 0)
		{
			throw new InvalidOperationException(
				"Azure OpenAI needs an endpoint. Pass a baseUrl in models.json, or set AZURE_OPENAI_BASE_URL or AZURE_OPENAI_RESOURCE_NAME.");
		}

		return (NormalizeBaseUrl(baseUrl), apiVersion);
	}

	/// <summary>确保端点以 <c>/openai/v1</c> 结尾，便于后续拼接 <c>/responses</c>。</summary>
	internal static string NormalizeBaseUrl(string baseUrl)
	{
		var trimmed = baseUrl.Trim().TrimEnd('/');

		if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
		{
			throw new InvalidOperationException($"Invalid Azure OpenAI base URL: {baseUrl}");
		}

		var isAzureHost =
			uri.Host.EndsWith(".openai.azure.com", StringComparison.OrdinalIgnoreCase) ||
			uri.Host.EndsWith(".cognitiveservices.azure.com", StringComparison.OrdinalIgnoreCase) ||
			uri.Host.EndsWith(".ai.azure.com", StringComparison.OrdinalIgnoreCase);

		var path = uri.AbsolutePath.TrimEnd('/');

		if (isAzureHost && path is "" or "/" or "/openai")
		{
			return $"{uri.Scheme}://{uri.Authority}/openai/v1";
		}

		return trimmed;
	}

	/// <summary>模型对应的部署名：有显式映射就用映射，否则用模型 id。</summary>
	internal static string ResolveDeploymentName(Model model) =>
		ParseDeploymentMap(Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT_NAME_MAP"))
			.GetValueOrDefault(model.Id) ?? model.Id;

	internal static Dictionary<string, string> ParseDeploymentMap(string? value)
	{
		var map = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (var entry in (value ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries))
		{
			var parts = entry.Split('=', 2);
			if (parts.Length == 2 && parts[0].Trim() is { Length: > 0 } modelId && parts[1].Trim() is { Length: > 0 } deployment)
			{
				map[modelId.Trim()] = deployment.Trim();
			}
		}

		return map;
	}

	private static string? ResolveApiKey(Model model, StreamOptions options) =>
		!string.IsNullOrEmpty(options.ApiKey)
			? options.ApiKey
			: ProviderEnvironment.ResolveApiKey(model.Provider, options)
				?? Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY");

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
