using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PiWeft.Cli;

/// <summary>llama.cpp router 的最小管理客户端，对应 pi 的 <c>/llama</c>。</summary>
public static class LlamaRouter
{
	public const string BaseUrlVariable = "LLAMA_BASE_URL";
	public const string ApiKeyVariable = "LLAMA_API_KEY";
	public const string DefaultBaseUrl = "http://127.0.0.1:8080";

	public sealed record ModelInfo(string Id, string Status, string? Name);

	public static string BaseUrl => Environment.GetEnvironmentVariable(BaseUrlVariable) is { Length: > 0 } value
		? value.TrimEnd('/')
		: DefaultBaseUrl;

	public static async Task<IReadOnlyList<ModelInfo>> ListAsync(
		HttpMessageHandler? handler = null,
		CancellationToken cancellationToken = default)
	{
		var payload = await RequestAsync(HttpMethod.Get, "/models", null, handler, cancellationToken).ConfigureAwait(false);
		if (payload?["data"] is not JsonArray data)
		{
			throw new InvalidOperationException("llama.cpp 没有返回 data 数组；请确认服务器以 router 模式启动。");
		}

		var models = new List<ModelInfo>();
		foreach (var item in data)
		{
			if (item is not JsonObject model || model["id"]?.GetValue<string>() is not { Length: > 0 } id)
			{
				continue;
			}

			var status = model["status"] is JsonObject statusObject
				? statusObject["value"]?.GetValue<string>() ?? "unknown"
				: model["status"]?.GetValue<string>() ?? "unknown";
			models.Add(new ModelInfo(id, status, model["name"]?.GetValue<string>()));
		}

		return models;
	}

	public static Task LoadAsync(string model, HttpMessageHandler? handler = null, CancellationToken cancellationToken = default) =>
		RequestAsync(HttpMethod.Post, "/models/load", new JsonObject { ["model"] = model }, handler, cancellationToken);

	public static Task UnloadAsync(string model, HttpMessageHandler? handler = null, CancellationToken cancellationToken = default) =>
		RequestAsync(HttpMethod.Post, "/models/unload", new JsonObject { ["model"] = model }, handler, cancellationToken);

	private static async Task<JsonNode?> RequestAsync(
		HttpMethod method,
		string path,
		JsonObject? body,
		HttpMessageHandler? handler,
		CancellationToken cancellationToken)
	{
		using var client = handler is null ? new HttpClient() : new HttpClient(handler);
		client.Timeout = TimeSpan.FromSeconds(30);
		using var request = new HttpRequestMessage(method, BaseUrl + path);
		request.Headers.TryAddWithoutValidation("accept", "application/json");
		if (Environment.GetEnvironmentVariable(ApiKeyVariable) is { Length: > 0 } key)
		{
			request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
		}

		if (body is not null)
		{
			request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
		}

		using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
		var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
		JsonNode? payload = null;
		try
		{
			payload = text.Length > 0 ? JsonNode.Parse(text) : null;
		}
		catch (JsonException)
		{
			// 非 JSON 的成功响应按空处理。
		}

		if (!response.IsSuccessStatusCode)
		{
			var message = payload?["error"]?.ToString() ?? text.Trim();
			throw new InvalidOperationException(
				$"llama.cpp HTTP {(int)response.StatusCode}{(message.Length > 0 ? $": {message}" : string.Empty)}");
		}

		return payload;
	}
}
