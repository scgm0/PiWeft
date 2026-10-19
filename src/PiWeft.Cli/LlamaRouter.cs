using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiWeft.Ai;

namespace PiWeft.Cli;

/// <summary>llama.cpp router 的最小管理客户端，对应 pi 的 <c>/llama</c>。</summary>
public static class LlamaRouter
{
	public const string BaseUrlVariable = "LLAMA_BASE_URL";
	public const string ApiKeyVariable = "LLAMA_API_KEY";
	public const string DefaultBaseUrl = "http://127.0.0.1:8080";

	public sealed record ModelInfo(
		string Id,
		string Status,
		string? Name,
		int? ContextWindow = null,
		int? MaxTokens = null,
		bool SupportsImages = false);

	public sealed record HuggingFaceModel(string Id, long Downloads);

	public sealed record HuggingFaceDetails(string Id, string Gated, IReadOnlyList<string> Quantizations);

	public static string BaseUrl => Environment.GetEnvironmentVariable(BaseUrlVariable) is { Length: > 0 } value
		? value.TrimEnd('/')
		: DefaultBaseUrl;

	public static string InferenceBaseUrl => BaseUrl + "/v1";

	/// <summary>把 router 模型映射为可供会话选择的 Model。</summary>
	public static Model ToModel(ModelInfo model)
	{
		var context = model.ContextWindow is > 0 ? model.ContextWindow.Value : 128000;
		return new Model
		{
			Id = model.Id,
			Name = model.Name is { Length: > 0 } name ? name : model.Id,
			Provider = "llama.cpp",
			Api = "openai-completions",
			BaseUrl = InferenceBaseUrl,
			Input = model.SupportsImages ? ["text", "image"] : ["text"],
			ContextWindow = context,
			MaxTokens = model.MaxTokens is > 0 ? model.MaxTokens.Value : 32768,
			Reasoning = false,
			Type = ModelType.Chat,
		};
	}

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
			var meta = model["meta"] as JsonObject;
			var context = IntOf(meta?["n_ctx"]) ?? IntOf(meta?["n_ctx_train"]) ?? ArgInt(model);
			var maxTokens = IntOf(meta?["n_predict"]) ?? 32768;
			var modalities = model["architecture"]?["input_modalities"] as JsonArray;
			var images = modalities?.Any(static value => value?.GetValue<string>() == "image") == true;
			models.Add(new ModelInfo(id, status, model["name"]?.GetValue<string>(), context, maxTokens, images));
		}

		return models;
	}

	public static Task LoadAsync(string model, HttpMessageHandler? handler = null, CancellationToken cancellationToken = default) =>
		RequestAsync(HttpMethod.Post, "/models/load", new JsonObject { ["model"] = model }, handler, cancellationToken);

	public static Task UnloadAsync(string model, HttpMessageHandler? handler = null, CancellationToken cancellationToken = default) =>
		RequestAsync(HttpMethod.Post, "/models/unload", new JsonObject { ["model"] = model }, handler, cancellationToken);

	/// <summary>从 Hugging Face 下载模型（<c>owner/repo[:quant]</c>）。</summary>
	public static Task DownloadAsync(string model, HttpMessageHandler? handler = null, CancellationToken cancellationToken = default) =>
		RequestAsync(HttpMethod.Post, "/models", new JsonObject { ["model"] = model }, handler, cancellationToken);

	public static async Task<IReadOnlyList<HuggingFaceModel>> SearchHuggingFaceAsync(
		string query,
		HttpMessageHandler? handler = null,
		CancellationToken cancellationToken = default)
	{
		var payload = await GetJsonAsync(
			$"https://huggingface.co/api/models?search={Uri.EscapeDataString(query)}&filter=gguf&sort=downloads&direction=-1&limit=20",
			handler,
			cancellationToken).ConfigureAwait(false);
		var models = new List<HuggingFaceModel>();
		if (payload is JsonArray array)
		{
			foreach (var item in array)
			{
				if (item is JsonObject model && model["id"]?.GetValue<string>() is { Length: > 0 } id)
				{
					models.Add(new HuggingFaceModel(id, model["downloads"]?.GetValue<long>() ?? 0));
				}
			}
		}

		return models;
	}

	public static async Task<HuggingFaceDetails?> HuggingFaceDetailsAsync(
		string repository,
		HttpMessageHandler? handler = null,
		CancellationToken cancellationToken = default)
	{
		var encoded = string.Join('/', repository.Split('/').Select(Uri.EscapeDataString));
		var payload = await GetJsonAsync(
			$"https://huggingface.co/api/models/{encoded}?blobs=true",
			handler,
			cancellationToken).ConfigureAwait(false);
		if (payload is not JsonObject model)
		{
			return null;
		}

		var gated = model["gated"]?.GetValue<string>() ?? (model["gated"]?.GetValue<bool>() == true ? "auto" : "false");
		var quantizations = new SortedSet<string>(StringComparer.Ordinal);
		if (model["siblings"] is JsonArray siblings)
		{
			foreach (var item in siblings)
			{
				if (item?["rfilename"]?.GetValue<string>() is not { } filename || !filename.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}

				var stem = Path.GetFileNameWithoutExtension(filename);
				if (stem.StartsWith("mmproj", StringComparison.OrdinalIgnoreCase)) continue;
				var marker = stem.ToUpperInvariant();
				foreach (var quant in new[] { "Q4_K_M", "Q5_K_M", "Q6_K", "Q8_0", "IQ4_XS", "IQ4_NL", "BF16", "F16" })
				{
					if (marker.EndsWith(quant, StringComparison.Ordinal)) quantizations.Add(quant);
				}
			}
		}

		return new HuggingFaceDetails(repository, gated, [.. quantizations]);
	}

	private static async Task<JsonNode?> GetJsonAsync(
		string url,
		HttpMessageHandler? handler,
		CancellationToken cancellationToken)
	{
		using var client = handler is null ? new HttpClient() : new HttpClient(handler);
		client.Timeout = TimeSpan.FromSeconds(30);
		using var request = new HttpRequestMessage(HttpMethod.Get, url);
		request.Headers.TryAddWithoutValidation("accept", "application/json");
		if (Environment.GetEnvironmentVariable("HF_TOKEN") is { Length: > 0 } token)
		{
			request.Headers.TryAddWithoutValidation("authorization", $"Bearer {token}");
		}

		using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
		var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
		if (!response.IsSuccessStatusCode)
		{
			throw new InvalidOperationException($"HTTP {(int)response.StatusCode}: {body}");
		}

		return body.Length > 0 ? JsonNode.Parse(body) : null;
	}

	private static int? IntOf(JsonNode? node) =>
		node is JsonValue value && value.TryGetValue<int>(out var number) ? number : null;

	private static int? ArgInt(JsonObject model)
	{
		if (model["status"]?["args"] is not JsonArray args) return null;
		for (var index = 0; index + 1 < args.Count; index++)
		{
			var flag = args[index]?.GetValue<string>();
			if (flag is "--ctx-size" or "-c" or "-ctx" &&
				int.TryParse(args[index + 1]?.ToString(), out var value) && value > 0)
			{
				return value;
			}
		}

		return null;
	}

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
