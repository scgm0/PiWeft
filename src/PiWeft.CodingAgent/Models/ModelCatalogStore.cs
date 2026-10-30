using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiWeft.Ai;
using PiWeft.Ai.Auth;
using PiWeft.CodingAgent.Session;
using PiWeft.CodingAgent.Settings;

namespace PiWeft.CodingAgent.Models;

/// <summary>pi.dev 远程模型目录的本地缓存与刷新。对应 pi 的 models-store.json + remote catalog。</summary>
public static class ModelCatalogStore
{
	public const string CatalogBaseUrl = "https://pi.dev";
	private const int MaxResponseBytes = 4 * 1024 * 1024;
	private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

	private static readonly HttpClient SharedClient = new(new SocketsHttpHandler
	{
		PooledConnectionLifetime = TimeSpan.FromMinutes(5),
		AutomaticDecompression = DecompressionMethods.All,
		ConnectTimeout = TimeSpan.FromSeconds(10),
	})
	{
		Timeout = Timeout.InfiniteTimeSpan,
	};

	public static string PathFor(string? agentDir = null) =>
		Path.Combine(agentDir ?? SessionPaths.GetAgentDir(), "models-store.json");

	/// <summary>把已缓存的远程目录叠加到注册表；不存在或损坏时什么都不做。</summary>
	public static int LoadCached(ModelRegistry registry, string? agentDir = null)
	{
		var root = ReadRoot(agentDir);
		if (root?["providers"] is not JsonObject providers)
		{
			return 0;
		}

		var added = 0;
		foreach (var (providerId, node) in providers)
		{
			if (node?["models"] is not JsonArray models)
			{
				continue;
			}

			foreach (var raw in models)
			{
				if (raw is not JsonObject obj)
				{
					continue;
				}

				try
				{
					var model = JsonSerializer.Deserialize(obj, ModelsJsonContext.Default.Model);
					if (model is null || string.IsNullOrEmpty(model.Id))
					{
						continue;
					}

					var template = registry.Resolve(providerId, model.Id) ?? registry.List(providerId).FirstOrDefault();
					registry.Add(model with
					{
						Provider = providerId,
						Name = string.IsNullOrEmpty(model.Name) ? model.Id : model.Name,
						Api = string.IsNullOrEmpty(model.Api) ? template?.Api ?? "openai-completions" : model.Api,
						BaseUrl = string.IsNullOrEmpty(model.BaseUrl) ? template?.BaseUrl ?? string.Empty : model.BaseUrl,
						Headers = model.Headers ?? template?.Headers,
					});
					added++;
				}
				catch (JsonException)
				{
					// 单条坏数据不影响其它模型。
				}
			}
		}

		return added;
	}

	/// <summary>只刷新一个账号级目录（OpenRouter / Radius / GitHub Copilot）。</summary>
	public static async Task<bool> RefreshAccountCatalogAsync(
		string provider,
		string? agentDir = null,
		HttpMessageHandler? handler = null,
		CancellationToken cancellationToken = default)
	{
		var root = ReadRoot(agentDir) ?? new JsonObject();
		var providerNodes = root["providers"] as JsonObject ?? new JsonObject();
		root["providers"] = providerNodes;
		using var client = handler is null ? null : new HttpClient(handler);
		var http = client ?? SharedClient;
		var updated = provider switch
		{
			"openrouter" => await RefreshOpenRouterAsync(http, providerNodes, agentDir, cancellationToken).ConfigureAwait(false),
			"radius" => await RefreshRadiusAsync(http, providerNodes, agentDir, cancellationToken).ConfigureAwait(false),
			"github-copilot" => await RefreshGitHubCopilotAsync(http, providerNodes, agentDir, cancellationToken).ConfigureAwait(false),
			_ => false,
		};

		if (updated)
		{
			WriteRoot(root, agentDir);
		}

		return updated;
	}

	/// <summary>强制刷新所有 Provider 的远程目录并写回缓存。</summary>
	/// <summary>两次全量刷新的最小间隔。显式 <c>pi update --models</c> 传 <c>force: true</c> 绕过。</summary>
	public static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(6);

	/// <summary>
	/// 是否该刷了：取缓存里最新的 <c>checkedAt</c> 与 <see cref="RefreshInterval"/> 比较。
	/// 没有缓存或时间戳缺失/在未来（手改文件、时钟回拨）时都返回 <see langword="true"/> ——
	/// 节流不该变成「永远不刷新」。
	/// </summary>
	public static bool ShouldRefresh(string? agentDir = null, TimeSpan? interval = null, DateTimeOffset? now = null)
	{
		var root = ReadRoot(agentDir);
		if (root?["providers"] is not JsonObject providers)
		{
			return true;
		}

		var newest = 0L;
		foreach (var node in providers)
		{
			if (node.Value?["checkedAt"] is JsonValue value && value.TryGetValue<long>(out var checkedAt) && checkedAt > newest)
			{
				newest = checkedAt;
			}
		}

		if (newest <= 0)
		{
			return true;
		}

		var elapsed = (now ?? DateTimeOffset.UtcNow) - DateTimeOffset.FromUnixTimeMilliseconds(newest);
		return elapsed >= (interval ?? RefreshInterval);
	}

	/// <summary>刷新结果：更新了几个、几个失败、几个命中 304（内容未变，省下了传输）。</summary>
	public sealed record RefreshOutcome(int Updated, int Failed, int NotModified)
	{
		public bool NothingToDo => Updated == 0 && Failed == 0 && NotModified == 0;
	}

	public static async Task<RefreshOutcome> RefreshAllAsync(
		string? agentDir = null,
		Action<string>? onError = null,
		HttpMessageHandler? handler = null,
		bool force = false,
		CancellationToken cancellationToken = default)
	{
		// 离线模式：目录刷新是「自动/批量」网络操作，直接跳过；缓存的目录仍然可用。
		if (PiEnvironment.Offline)
		{
			return new RefreshOutcome(0, 0, 0);
		}

		if (!force && !ShouldRefresh(agentDir))
		{
			return new RefreshOutcome(0, 0, 0);
		}

		var registry = ModelRegistry.LoadDefault();
		var providers = registry.Providers.OrderBy(static id => id, StringComparer.Ordinal).ToList();
		var root = ReadRoot(agentDir) ?? new JsonObject();
		var providerNodes = root["providers"] as JsonObject ?? new JsonObject();
		root["providers"] = providerNodes;

		var updated = 0;
		var failed = 0;
		var notModified = 0;
		// 只要有任何一次网络往返（含 304）就要落盘新的 checkedAt：否则节流窗口永远推不动，
		// 每次启动都会重新全量拉一遍。
		var touched = false;
		using var client = handler is null ? null : new HttpClient(handler);
		var http = client ?? SharedClient;

		foreach (var providerId in providers)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var url = $"{CatalogBaseUrl}/api/models/providers/{Uri.EscapeDataString(providerId)}?types=chat,image,classifier";
			using var request = new HttpRequestMessage(HttpMethod.Get, url);
			request.Headers.TryAddWithoutValidation("accept", "application/json");
			request.Headers.TryAddWithoutValidation("user-agent", "PiWeft/0.1.0");

			// 条件请求：目录是按 provider 全量返回的大 JSON，绝大多数刷新其实什么都没变。
			// 把缓存里的 ETag / Last-Modified 带回去，命中 304 就不用传也不用解析。
			if (providerNodes[providerId] is JsonObject cached)
			{
				if (cached["etag"] is JsonValue etag && etag.TryGetValue<string>(out var tag) && !string.IsNullOrWhiteSpace(tag))
				{
					request.Headers.TryAddWithoutValidation("if-none-match", tag);
				}

				if (cached["lastModified"] is JsonValue modified && modified.TryGetValue<long>(out var modifiedMs) && modifiedMs > 0)
				{
					request.Headers.TryAddWithoutValidation("if-modified-since",
						DateTimeOffset.FromUnixTimeMilliseconds(modifiedMs).ToUniversalTime().ToString("R"));
				}
			}

			using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			timeout.CancelAfter(RequestTimeout);

			try
			{
				using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
					.ConfigureAwait(false);
				if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NotImplemented)
				{
					continue;
				}

				if (response.StatusCode == HttpStatusCode.NotModified)
				{
					// 内容没变：模型保持缓存里那份，只推进 checkedAt。
					notModified++;
					touched = true;
					if (providerNodes[providerId] is JsonObject unchanged)
					{
						unchanged["checkedAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
					}

					continue;
				}

				if (!response.IsSuccessStatusCode)
				{
					failed++;
					onError?.Invoke($"{providerId}: HTTP {(int)response.StatusCode}");
					continue;
				}

				var body = await ReadBoundedAsync(response.Content, timeout.Token).ConfigureAwait(false);
				var models = ParseProviderCatalog(body);
				if (models is null)
				{
					failed++;
					onError?.Invoke($"{providerId}: invalid catalog payload");
					continue;
				}

				providerNodes[providerId] = new JsonObject
				{
					["models"] = models,
					// 服务端没给 Last-Modified 时留 0，而不是把「现在」当成资源修改时间：
					// 后者会让下一次的 If-Modified-Since 说是现在，撞上时钟偏差就永久 304。
					["lastModified"] = response.Content.Headers.LastModified?.ToUnixTimeMilliseconds() ?? 0,
					["checkedAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
					["etag"] = response.Headers.ETag?.Tag,
				};
				updated++;
				touched = true;
			}
			catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
			{
				failed++;
				onError?.Invoke($"{providerId}: timeout");
			}
			catch (HttpRequestException exception)
			{
				failed++;
				onError?.Invoke($"{providerId}: {exception.Message}");
			}
		}

		// 账号级目录：OpenRouter 按 key 过滤，Radius 用 gateway 的 /v1/config。
		if (await RefreshOpenRouterAsync(http, providerNodes, agentDir, cancellationToken).ConfigureAwait(false))
		{
			updated++;
		}

		if (await RefreshRadiusAsync(http, providerNodes, agentDir, cancellationToken).ConfigureAwait(false))
		{
			updated++;
		}

		if (await RefreshGitHubCopilotAsync(http, providerNodes, agentDir, cancellationToken).ConfigureAwait(false))
		{
			updated++;
		}

		if (touched || updated > 0)
		{
			WriteRoot(root, agentDir);
		}

		return new RefreshOutcome(updated, failed, notModified);
	}

	private static async Task<bool> RefreshOpenRouterAsync(
		HttpClient http,
		JsonObject providerNodes,
		string? agentDir,
		CancellationToken cancellationToken)
	{
		var key = OAuthStore.Read("openrouter", agentDir)?.Access ??
			AuthStore.ReadApiKey("openrouter", agentDir) ??
			Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
		if (key is not { Length: > 0 })
		{
			return false;
		}

		using var request = new HttpRequestMessage(HttpMethod.Get, "https://openrouter.ai/api/v1/models/user");
		request.Headers.TryAddWithoutValidation("accept", "application/json");
		request.Headers.TryAddWithoutValidation("authorization", $"Bearer {key}");
		try
		{
			using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
			if (!response.IsSuccessStatusCode)
			{
				return false;
			}

			var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
			var models = ParseOpenRouterModels(body);
			if (models is not { Count: > 0 })
			{
				return false;
			}

			providerNodes["openrouter"] = new JsonObject
			{
				["models"] = models,
				["checkedAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
			};
			return true;
		}
		catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or JsonException)
		{
			return false;
		}
	}

	private static JsonArray? ParseOpenRouterModels(string body)
	{
		JsonNode? node;
		try
		{
			node = JsonNode.Parse(body);
		}
		catch (JsonException)
		{
			return null;
		}

		if (node?["data"] is not JsonArray data)
		{
			return null;
		}

		var result = new JsonArray();
		foreach (var item in data)
		{
			if (item is not JsonObject model || model["id"]?.GetValue<string>() is not { Length: > 0 } id)
			{
				continue;
			}

			var contextWindow = IntOf(model["context_length"]) ?? IntOf(model["top_provider"]?["context_length"]) ?? 128000;
			var maxTokens = IntOf(model["top_provider"]?["max_completion_tokens"]) ?? 16384;
			var cost = model["pricing"] as JsonObject;
			var input = model["architecture"]?["input_modalities"] is JsonArray modalities &&
				modalities.Any(static value => value?.GetValue<string>() == "image")
					? new JsonArray("text", "image")
					: new JsonArray("text");
			var supported = model["supported_parameters"] is JsonArray parameters &&
				parameters.Any(static value => value?.GetValue<string>() is "reasoning" or "include_reasoning");

			result.Add((JsonNode)new JsonObject
			{
				["id"] = id,
				["name"] = model["name"]?.GetValue<string>() ?? id,
				["api"] = "openai-completions",
				["provider"] = "openrouter",
				["baseUrl"] = "https://openrouter.ai/api/v1",
				["reasoning"] = supported,
				["input"] = input,
				["contextWindow"] = contextWindow,
				["maxTokens"] = maxTokens,
				["cost"] = new JsonObject
				{
					["input"] = Price(cost?["prompt"]),
					["output"] = Price(cost?["completion"]),
					["cacheRead"] = Price(cost?["input_cache_read"]),
					["cacheWrite"] = Price(cost?["input_cache_write"]),
				},
				["type"] = "chat",
			});
		}

		return result;
	}

	private static async Task<bool> RefreshRadiusAsync(
		HttpClient http,
		JsonObject providerNodes,
		string? agentDir,
		CancellationToken cancellationToken)
	{
		var credential = OAuthStore.Read("radius", agentDir);
		if (credential is null)
		{
			return false;
		}

		var gateway = (Environment.GetEnvironmentVariable("PI_RADIUS_GATEWAY") is { Length: > 0 } value
			? value
			: "https://radius.pi.dev").TrimEnd('/');
		using var request = new HttpRequestMessage(HttpMethod.Get, $"{gateway}/v1/config");
		request.Headers.TryAddWithoutValidation("accept", "application/json");
		request.Headers.TryAddWithoutValidation("authorization", $"Bearer {credential.Access}");
		try
		{
			using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
			if (!response.IsSuccessStatusCode)
			{
				return false;
			}

			var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)) as JsonObject;
			if (json?["models"] is not JsonArray models || json["baseUrl"]?.GetValue<string>() is not { Length: > 0 } baseUrl)
			{
				return false;
			}

			var items = new JsonArray();
			foreach (var item in models)
			{
				if (item is not JsonObject model || model["id"]?.GetValue<string>() is not { Length: > 0 } id)
				{
					continue;
				}

				var copy = (JsonObject)model.DeepClone();
				copy["provider"] = "radius";
				copy["api"] = "pi-messages";
				copy["baseUrl"] = baseUrl;
				copy["type"] = "chat";
				items.Add((JsonNode)copy);
			}

			providerNodes["radius"] = new JsonObject
			{
				["models"] = items,
				["checkedAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
			};
			return true;
		}
		catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or JsonException)
		{
			return false;
		}
	}

	private static async Task<bool> RefreshGitHubCopilotAsync(
		HttpClient http,
		JsonObject providerNodes,
		string? agentDir,
		CancellationToken cancellationToken)
	{
		var credential = OAuthStore.Read("github-copilot", agentDir);
		if (credential is null)
		{
			return false;
		}

		var baseUrl = credential.BaseUrl ?? "https://api.individual.githubcopilot.com";
		using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/models");
		request.Headers.TryAddWithoutValidation("accept", "application/json");
		request.Headers.TryAddWithoutValidation("authorization", $"Bearer {credential.Access}");
		request.Headers.TryAddWithoutValidation("user-agent", "GitHubCopilotChat/0.35.0");
		request.Headers.TryAddWithoutValidation("editor-version", "vscode/1.107.0");
		request.Headers.TryAddWithoutValidation("editor-plugin-version", "copilot-chat/0.35.0");
		request.Headers.TryAddWithoutValidation("copilot-integration-id", "vscode-chat");
		request.Headers.TryAddWithoutValidation("x-github-api-version", "2026-06-01");
		try
		{
			using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
			if (!response.IsSuccessStatusCode)
			{
				return false;
			}

			var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
			if ((JsonNode.Parse(body) as JsonObject)?["data"] is not JsonArray data)
			{
				return false;
			}

			var registry = ModelRegistry.LoadDefault();
			var items = new JsonArray();
			foreach (var item in data)
			{
				if (item is not JsonObject model || model["id"]?.GetValue<string>() is not { Length: > 0 } id)
				{
					continue;
				}

				var template = registry.Resolve("github-copilot", id) ?? registry.List("github-copilot").FirstOrDefault();
				items.Add((JsonNode)new JsonObject
				{
					["id"] = id,
					["name"] = model["name"]?.GetValue<string>() ?? template?.Name ?? id,
					["api"] = template?.Api ?? "openai-completions",
					["provider"] = "github-copilot",
					["baseUrl"] = baseUrl,
					["reasoning"] = template?.Reasoning ?? false,
					["input"] = template?.Input is { Count: > 0 } input
						? new JsonArray([.. input.Select(static value => (JsonNode?)JsonValue.Create(value))])
						: new JsonArray("text"),
					["contextWindow"] = template?.ContextWindow ?? 128000,
					["maxTokens"] = template?.MaxTokens ?? 16384,
					["headers"] = template?.Headers is { Count: > 0 } headers
						? new JsonObject(headers.Select(static pair => new KeyValuePair<string, JsonNode?>(pair.Key, pair.Value)))
						: null,
					["type"] = "chat",
				});
			}

			providerNodes["github-copilot"] = new JsonObject
			{
				["models"] = items,
				["checkedAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
			};
			return true;
		}
		catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or JsonException)
		{
			return false;
		}
	}

	private static int? IntOf(JsonNode? node) =>
		node is JsonValue value && value.TryGetValue<int>(out var number) ? number : null;

	private static double Price(JsonNode? node)
	{
		var text = node?.GetValue<string>();
		if (!double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var perToken) ||
			perToken < 0)
		{
			return 0;
		}

		return Math.Round(perToken * 1_000_000, 6);
	}

	private static JsonArray? ParseProviderCatalog(string body)
	{
		JsonNode? node;
		try
		{
			node = JsonNode.Parse(body);
		}
		catch (JsonException)
		{
			return null;
		}

		IEnumerable<JsonNode?>? entries = node switch
		{
			JsonArray array => array,
			JsonObject obj when obj["data"] is JsonArray data => data,
			JsonObject obj when obj["models"] is JsonArray models => models,
			JsonObject obj => obj.Select(static pair => pair.Value),
			_ => null,
		};

		if (entries is null)
		{
			return null;
		}

		var result = new JsonArray();
		foreach (var entry in entries)
		{
			if (entry is JsonObject obj && obj["id"] is JsonValue id && id.GetValueKind() == JsonValueKind.String)
			{
				result.Add(obj.DeepClone());
			}
		}

		return result;
	}

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

	private static JsonObject? ReadRoot(string? agentDir)
	{
		var path = PathFor(agentDir);
		if (!File.Exists(path))
		{
			return null;
		}

		try
		{
			return JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
		}
		catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
		{
			return null;
		}
	}

	private static void WriteRoot(JsonObject root, string? agentDir)
	{
		var path = PathFor(agentDir);
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
		File.WriteAllText(temporary, root.ToJsonString(new JsonSerializerOptions
		{
			WriteIndented = true,
			Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
		}));
		File.Move(temporary, path, overwrite: true);
	}
}
