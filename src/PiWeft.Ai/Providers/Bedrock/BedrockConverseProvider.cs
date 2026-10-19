using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

using PiWeft.Ai.Json;
namespace PiWeft.Ai.Providers.Bedrock;

/// <summary>
/// Bedrock Converse API（<c>bedrock-converse-stream</c>）的流式适配器。请求用 SigV4 签名，
/// 响应是 AWS 二进制事件流而非 SSE；两者都在本程序集内实现，因此
/// 不需要 AWS SDK。
/// </summary>
public sealed class BedrockConverseProvider : IApiProvider
{
	public const string ApiId = "bedrock-converse-stream";
	public const string ServiceName = "bedrock";

	private readonly HttpClient _http;

	public BedrockConverseProvider(HttpClient? httpClient = null) =>
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
			var credentials = AwsCredentials.FromEnvironment();
			if (!credentials.IsValid)
			{
				PushFailure(
					stream,
					model,
					startedAt,
					"Missing AWS credentials. Set AWS_ACCESS_KEY_ID and AWS_SECRET_ACCESS_KEY.");
				return;
			}

			var messages = Transcript.Normalize(context, model);
			var tools = Transcript.GetTools(messages);
			var thinkingBudget = ResolveThinkingBudget(model, options);
			var maxTokens = options.MaxTokens ?? (model.MaxTokens > 0 ? model.MaxTokens : null);

			using var request = BuildRequest(
				model,
				messages,
				tools,
				options,
				thinkingBudget,
				maxTokens,
				credentials);

			using var response = await _http
				.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headerTimeout.Token)
				.ConfigureAwait(false);

			options.OnResponse?.Invoke(ProviderHttp.FromResponse(response), model);

			if (!response.IsSuccessStatusCode)
			{
				var body = await response.Content.ReadAsStringAsync(options.CancellationToken).ConfigureAwait(false);
				PushFailure(stream, model, startedAt, $"Bedrock request failed ({(int)response.StatusCode}): {Truncate(body, 2000)}");
				return;
			}

			await using var bodyStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

			var state = new BedrockStreamState(model, stream, startedAt);
			await foreach (var message in AwsEventStream.ReadAsync(bodyStream, cancellationToken).ConfigureAwait(false))
			{
				state.Handle(message);
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
		int? maxTokens,
		AwsCredentials credentials)
	{
		var bytes = JsonPayload.Write(writer =>
			BedrockWire.WriteRequest(writer, model, messages, tools, options, thinkingBudget, maxTokens));

		if (options.OnPayload is { } onPayload)
		{
			var node = JsonNode.Parse(bytes);
			if (node is not null && onPayload(node, model) is { } replaced)
			{
				bytes = Encoding.UTF8.GetBytes(replaced.ToJsonString(PiJson.NodeOptions));
			}
		}

		var url = BuildUrl(model);
		var region = ResolveRegion(model);

		var signed = AwsSignatureV4.Sign(
			"POST",
			url,
			new Dictionary<string, string>(StringComparer.Ordinal)
			{
				["content-type"] = "application/json",
				["accept"] = "application/vnd.amazon.eventstream",
			},
			bytes,
			region,
			ServiceName,
			credentials);

		var request = new HttpRequestMessage(HttpMethod.Post, url)
		{
			Content = new ByteArrayContent(bytes),
		};

		request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
		request.Headers.TryAddWithoutValidation("accept", "application/vnd.amazon.eventstream");
		request.Headers.TryAddWithoutValidation("x-amz-date", signed.Headers["x-amz-date"]);
		request.Headers.TryAddWithoutValidation("x-amz-content-sha256", signed.Headers["x-amz-content-sha256"]);
		request.Headers.TryAddWithoutValidation("authorization", signed.Authorization);

		if (signed.Headers.TryGetValue("x-amz-security-token", out var token))
		{
			request.Headers.TryAddWithoutValidation("x-amz-security-token", token);
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

	/// <summary>某个模型的 Converse 流式端点。</summary>
	internal static string BuildUrl(Model model)
	{
		var baseUrl = string.IsNullOrEmpty(model.BaseUrl)
			? "https://bedrock-runtime.us-east-1.amazonaws.com"
			: model.BaseUrl.TrimEnd('/');

		return $"{baseUrl}/model/{Uri.EscapeDataString(model.Id)}/converse-stream";
	}

	/// <summary>先从端点主机名推导区域，再从环境变量推导。</summary>
	internal static string ResolveRegion(Model model)
	{
		if (!string.IsNullOrEmpty(model.BaseUrl) &&
			Uri.TryCreate(model.BaseUrl, UriKind.Absolute, out var uri) &&
			uri.Host.StartsWith("bedrock-runtime.", StringComparison.OrdinalIgnoreCase))
		{
			var host = uri.Host["bedrock-runtime.".Length..];
			var separator = host.IndexOf('.');
			if (separator > 0)
			{
				return host[..separator];
			}
		}

		return Environment.GetEnvironmentVariable("AWS_REGION")
			?? Environment.GetEnvironmentVariable("AWS_DEFAULT_REGION")
			?? "us-east-1";
	}

	/// <summary>Bedrock 只对 Anthropic 模型使用基于预算的思考配置。</summary>
	internal static int? ResolveThinkingBudget(Model model, StreamOptions options)
	{
		if (!model.Reasoning || !model.Id.StartsWith("anthropic.", StringComparison.OrdinalIgnoreCase))
		{
			return null;
		}

		var level = ThinkingLevelResolver.Clamp(model, options.ThinkingLevel ?? ThinkingLevels.High);
		return level == ThinkingLevels.Off
			? null
			: Anthropic.AnthropicProvider.BudgetFor(level);
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
