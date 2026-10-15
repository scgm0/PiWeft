using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PiWeft.CodingAgent;

/// <summary>远程模型目录拉取结果。</summary>
public sealed record RemoteModelCatalogResult(bool Ok, IReadOnlyList<string> Models, string? Error);

/// <summary>
/// 按 API 从供应商端点拉取模型目录（PiDeck 的「拉取远程模型」）。
/// 只覆盖能安全探测的协议：OpenAI 兼容家族的 <c>GET /models</c>、Anthropic 的
/// <c>/v1/models</c>、Google Generative AI 的 <c>/models</c>；
/// Azure/Vertex/Bedrock/Codex/pi-messages 需要各自的鉴权或专用端点，明确报告不支持。
/// </summary>
public static class RemoteModelCatalog
{
	/// <summary>响应体上限，避免恶意或异常端点返回超大 JSON。</summary>
	public const int MaxResponseBytes = 2 * 1024 * 1024;

	/// <summary>默认超时，避免不可达端点拖住配置界面。</summary>
	public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

	private static readonly HttpClient SharedClient = new(new SocketsHttpHandler
	{
		PooledConnectionLifetime = TimeSpan.FromMinutes(5),
		AutomaticDecompression = DecompressionMethods.All,
		ConnectTimeout = TimeSpan.FromSeconds(30),
	})
	{
		Timeout = Timeout.InfiniteTimeSpan,
	};

	/// <summary>该 api 是否支持远程目录拉取。</summary>
	public static bool Supports(string api) => api is
		"openai-completions" or "openai-responses" or "mistral-conversations" or "anthropic-messages" or "google-generative-ai";

	/// <summary>拉取模型 id 列表。</summary>
	public static async Task<RemoteModelCatalogResult> FetchAsync(
		string api,
		string baseUrl,
		string? apiKey,
		HttpMessageHandler? handler = null,
		CancellationToken cancellationToken = default)
	{
		if (!Supports(api))
		{
			return new RemoteModelCatalogResult(false, [], $"远程模型目录不支持 api '{api}'");
		}

		if (string.IsNullOrWhiteSpace(baseUrl))
		{
			return new RemoteModelCatalogResult(false, [], "缺少 Base URL");
		}

		Uri url;
		try
		{
			url = new Uri(BuildUrl(api, baseUrl.TrimEnd('/')));
		}
		catch (UriFormatException)
		{
			return new RemoteModelCatalogResult(false, [], $"Base URL 无效：{baseUrl}");
		}

		using var request = new HttpRequestMessage(HttpMethod.Get, url);
		request.Headers.TryAddWithoutValidation("accept", "application/json");
		request.Headers.TryAddWithoutValidation("user-agent", "PiWeft/0.1.0");
		ApplyAuth(request, api, apiKey);

		using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		cancellation.CancelAfter(DefaultTimeout);

		HttpClient client;
		var ownsClient = handler is not null;
		if (handler is not null)
		{
			client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
		}
		else
		{
			client = SharedClient;
		}

		try
		{
			using var response = await client
				.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation.Token)
				.ConfigureAwait(false);

			var body = await ReadBoundedAsync(response.Content, cancellation.Token).ConfigureAwait(false);

			if (!response.IsSuccessStatusCode)
			{
				var detail = Summarize(body);
				return new RemoteModelCatalogResult(false, [], $"HTTP {(int)response.StatusCode}{(detail.Length > 0 ? $": {detail}" : string.Empty)}");
			}

			var models = Parse(body);
			return models.Count == 0
				? new RemoteModelCatalogResult(false, [], "端点没有返回可识别的模型")
				: new RemoteModelCatalogResult(true, models, null);
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			return new RemoteModelCatalogResult(false, [], "拉取超时");
		}
		catch (OperationCanceledException)
		{
			return new RemoteModelCatalogResult(false, [], "已取消");
		}
		catch (HttpRequestException ex)
		{
			return new RemoteModelCatalogResult(false, [], ex.Message);
		}
		finally
		{
			if (ownsClient)
			{
				client.Dispose();
			}
		}
	}

	private static string BuildUrl(string api, string baseUrl) => api switch
	{
		"anthropic-messages" => $"{baseUrl}/v1/models?limit=100",
		"google-generative-ai" => $"{baseUrl}/models",
		_ => $"{baseUrl}/models",
	};

	private static void ApplyAuth(HttpRequestMessage request, string api, string? apiKey)
	{
		if (string.IsNullOrEmpty(apiKey))
		{
			return;
		}

		switch (api)
		{
			case "anthropic-messages":
				if (apiKey.StartsWith("sk-ant-oat", StringComparison.Ordinal))
				{
					request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
				}
				else
				{
					request.Headers.TryAddWithoutValidation("x-api-key", apiKey);
				}

				request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
				break;

			case "google-generative-ai":
				request.Headers.TryAddWithoutValidation("x-goog-api-key", apiKey);
				break;

			default:
				request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
				break;
		}
	}

	/// <summary>兼容 <c>{data:[{id}]}</c>（OpenAI/Anthropic）与 <c>{models:[{name}]}</c>（Google）。</summary>
	internal static IReadOnlyList<string> Parse(string body)
	{
		JsonObject? root;
		try
		{
			root = JsonNode.Parse(body) as JsonObject;
		}
		catch (JsonException)
		{
			return [];
		}

		if (root is null)
		{
			return [];
		}

		var models = new List<string>();
		if (root["data"] is JsonArray data)
		{
			foreach (var item in data)
			{
				Add(models, item?["id"]?.GetValue<string>(), stripPrefix: false);
			}
		}

		if (root["models"] is JsonArray google)
		{
			foreach (var item in google)
			{
				Add(models, item?["name"]?.GetValue<string>(), stripPrefix: true);
			}
		}

		return models;
	}

	private static void Add(List<string> models, string? id, bool stripPrefix)
	{
		if (id is not { Length: > 0 })
		{
			return;
		}

		var value = stripPrefix && id.StartsWith("models/", StringComparison.Ordinal) ? id["models/".Length..] : id;
		if (value.Length > 0 && !models.Contains(value, StringComparer.Ordinal))
		{
			models.Add(value);
		}
	}

	/// <summary>有界读取响应体（超出上限直接截断），避免大响应占内存。</summary>
	private static async Task<string> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
	{
		await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
		using var buffer = new MemoryStream();
		var chunk = new byte[16 * 1024];
		int read;
		while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
		{
			var remaining = MaxResponseBytes - (int)buffer.Length;
			if (remaining <= 0)
			{
				break;
			}

			buffer.Write(chunk, 0, Math.Min(read, remaining));
		}

		return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
	}

	private static string Summarize(string body)
	{
		var trimmed = body.Trim();
		return trimmed.Length <= 200 ? trimmed : trimmed[..200] + "…";
	}
}
