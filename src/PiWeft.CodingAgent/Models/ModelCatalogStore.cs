using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiWeft.Ai;
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

	/// <summary>强制刷新所有 Provider 的远程目录并写回缓存。</summary>
	public static async Task<(int Updated, int Failed)> RefreshAllAsync(
		string? agentDir = null,
		Action<string>? onError = null,
		HttpMessageHandler? handler = null,
		CancellationToken cancellationToken = default)
	{
		var registry = ModelRegistry.LoadDefault();
		var providers = registry.Providers.OrderBy(static id => id, StringComparer.Ordinal).ToList();
		var root = ReadRoot(agentDir) ?? new JsonObject();
		var providerNodes = root["providers"] as JsonObject ?? new JsonObject();
		root["providers"] = providerNodes;

		var updated = 0;
		var failed = 0;
		using var client = handler is null ? null : new HttpClient(handler);
		var http = client ?? SharedClient;

		foreach (var providerId in providers)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var url = $"{CatalogBaseUrl}/api/models/providers/{Uri.EscapeDataString(providerId)}?types=chat,image,classifier";
			using var request = new HttpRequestMessage(HttpMethod.Get, url);
			request.Headers.TryAddWithoutValidation("accept", "application/json");
			request.Headers.TryAddWithoutValidation("user-agent", "PiWeft/0.1.0");

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
					["lastModified"] = response.Content.Headers.LastModified?.ToUnixTimeMilliseconds() ?? 0,
					["checkedAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
					["etag"] = response.Headers.ETag?.Tag,
				};
				updated++;
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

		if (updated > 0)
		{
			WriteRoot(root, agentDir);
		}

		return (updated, failed);
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
