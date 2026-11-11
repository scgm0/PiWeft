using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using PiWeft.Ai;
using PiWeft.CodingAgent.Settings;

namespace PiWeft.CodingAgent.Models;

/// pi-ai npm 包里的现成目录。数据文件按 API 分组（<c>{ group: { key: model } }</c>），
/// 每个模型自带 provider；合并成 PiWeft 的 <c>provider → models</c> 格式即可直接用。
/// 逐文件 SHA-256 与包里的 <c>.manifest.json</c> 对齐，代理或 CDN 返回半份内容会被拒绝。
internal static class PiAiCatalogSource
{
	public const string PackageName = "@earendil-works/pi-ai";

	private const string DataDirectory = "dist/providers/data/";
	private const int MaxManifestBytes = 256 * 1024;
	private const int MaxDataFileBytes = 4 * 1024 * 1024;
	private const int MaxFiles = 256;
	private const int ParallelDownloads = 6;

	/// 版本解析源：国内镜像优先，官方兜底。
	public static IReadOnlyList<string> RegistryUrls { get; } =
	[
		$"https://registry.npmmirror.com/{PackageName}/latest",
		$"https://registry.npmjs.org/{PackageName}/latest",
	];

	/// 数据来源优先级：先 pi-ai npm（最新），拿不到时由调用方回退仓库单文件。
	/// 已有 pi-ai 覆盖版时不再回退，避免用没有版本号的仓库文件把目录降级。
	public static async Task<PiAiSourceOutcome> FetchAsync(
		HttpClient http,
		string? currentVersion,
		ModelCatalogFetchOptions options,
		CancellationToken cancellationToken)
	{
		try
		{
			var version = await ResolveLatestVersionAsync(http, options.Timeout, cancellationToken).ConfigureAwait(false);
			if (version is null)
			{
				return PiAiSourceOutcome.Failed(ModelCatalogError.Network, "cannot resolve pi-ai latest version");
			}

			if (currentVersion is not null)
			{
				var comparison = CompareSemver(version, currentVersion);
				// 远端不高于当前版本：不降级。版本相同就不必下载 43 个数据文件。
				if (comparison <= 0)
				{
					return PiAiSourceOutcome.NotModified(version);
				}
			}

			var baseUrl = $"https://cdn.jsdelivr.net/npm/{PackageName}@{version}/{DataDirectory}";
			var manifestBytes = await GetAsync(http, baseUrl + ".manifest.json", MaxManifestBytes, options.Timeout, cancellationToken)
				.ConfigureAwait(false);
			var manifestHash = ModelCatalogOverlay.Digest(manifestBytes);
			var (files, generatedAt) = ParseManifest(manifestBytes);
			var contents = await DownloadFilesAsync(http, baseUrl, files, options.Timeout, cancellationToken).ConfigureAwait(false);
			var providers = Merge(files.Keys, contents);

			// 用 PiWeft 的落盘格式重新序列化；后续读取与内嵌目录走同一条解析路径。
			var catalog = JsonSerializer.SerializeToUtf8Bytes(providers, ModelsJsonContext.Default.DictionaryStringListModel);
			var parsed = ModelCatalogOverlay.ParseCatalog(catalog);
			var summary = ModelCatalogOverlay.Summarize(parsed);
			var info = new ModelCatalogInfo(
				summary.ProviderCount,
				summary.EntryCount,
				generatedAt ?? DateTimeOffset.UtcNow,
				"pi-ai",
				version);
			return PiAiSourceOutcome.Updated(new CatalogCandidate(
				catalog,
				ModelCatalogOverlay.CanonicalDigest(parsed),
				info,
				ETag: null,
				DataManifestHash: manifestHash));
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			return PiAiSourceOutcome.Failed(ModelCatalogError.Network, "pi-ai catalog request timed out");
		}
		catch (JsonException ex)
		{
			return PiAiSourceOutcome.Failed(ModelCatalogError.Validation, ex.Message);
		}
		catch (HttpRequestException ex)
		{
			return PiAiSourceOutcome.Failed(ModelCatalogError.Network, ex.Message);
		}
		catch (IOException ex)
		{
			return PiAiSourceOutcome.Failed(ModelCatalogError.Network, ex.Message);
		}
	}

	private static async Task<string?> ResolveLatestVersionAsync(HttpClient http, TimeSpan timeout, CancellationToken cancellationToken)
	{
		foreach (var url in RegistryUrls)
		{
			try
			{
				var bytes = await GetAsync(http, url, 64 * 1024, timeout, cancellationToken).ConfigureAwait(false);
				using var document = JsonDocument.Parse(bytes);
				if (document.RootElement.ValueKind == JsonValueKind.Object &&
					document.RootElement.TryGetProperty("version", out var version) &&
					version.ValueKind == JsonValueKind.String)
				{
					var text = version.GetString()?.Trim();
					if (!string.IsNullOrEmpty(text) && text.Length <= 64 &&
						text.All(static ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-' or '+'))
					{
						return text;
					}
				}
			}
			catch (Exception ex) when (ex is JsonException or HttpRequestException or IOException or OperationCanceledException)
			{
				// 换下一个镜像；取消由调用方检查，超时只影响这一路。
				if (cancellationToken.IsCancellationRequested)
				{
					throw;
				}
			}
		}

		return null;
	}

	private static (Dictionary<string, string> Files, DateTimeOffset? GeneratedAt) ParseManifest(byte[] bytes)
	{
		using var document = JsonDocument.Parse(bytes);
		var root = document.RootElement;
		if (root.ValueKind != JsonValueKind.Object ||
			!root.TryGetProperty("files", out var files) ||
			files.ValueKind != JsonValueKind.Object)
		{
			throw new JsonException("pi-ai data manifest has no files map");
		}

		var result = new Dictionary<string, string>(StringComparer.Ordinal);
		var seen = 0;
		foreach (var file in files.EnumerateObject())
		{
			var name = file.Name;
			if (file.Value.ValueKind != JsonValueKind.String ||
				!name.EndsWith(".json", StringComparison.Ordinal) ||
				name.StartsWith('.') ||
				name.Contains('/') ||
				name.Contains('\\') ||
				name.Contains("..", StringComparison.Ordinal))
			{
				continue;
			}

			seen++;
			if (seen > MaxFiles)
			{
				throw new JsonException($"pi-ai data manifest lists more than {MaxFiles} catalog files");
			}

			var hash = file.Value.GetString();
			if (!string.IsNullOrWhiteSpace(hash))
			{
				result[name] = hash;
			}
		}

		if (result.Count == 0)
		{
			throw new JsonException("pi-ai data manifest has no catalog files");
		}

		DateTimeOffset? generatedAt = null;
		if (root.TryGetProperty("generatedAt", out var generated) &&
			generated.ValueKind == JsonValueKind.String &&
			DateTimeOffset.TryParse(generated.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
		{
			generatedAt = parsed;
		}

		return (result, generatedAt);
	}

	private static async Task<Dictionary<string, byte[]>> DownloadFilesAsync(
		HttpClient http,
		string baseUrl,
		IReadOnlyDictionary<string, string> files,
		TimeSpan timeout,
		CancellationToken cancellationToken)
	{
		var contents = new Dictionary<string, byte[]>(files.Count, StringComparer.Ordinal);
		using var gate = new SemaphoreSlim(ParallelDownloads);
		var tasks = files.Select(async file =>
		{
			await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
			try
			{
				var bytes = await GetAsync(http, baseUrl + file.Key, MaxDataFileBytes, timeout, cancellationToken).ConfigureAwait(false);
				var actual = Convert.ToHexStringLower(SHA256.HashData(bytes));
				if (!string.Equals(actual, file.Value, StringComparison.OrdinalIgnoreCase))
				{
					throw new JsonException($"pi-ai data file {file.Key} does not match its manifest hash");
				}

				lock (contents)
				{
					contents[file.Key] = bytes;
				}
			}
			finally
			{
				gate.Release();
			}
		});

		await Task.WhenAll(tasks).ConfigureAwait(false);
		return contents;
	}

	private static Dictionary<string, List<Model>> Merge(IEnumerable<string> fileNames, IReadOnlyDictionary<string, byte[]> contents)
	{
		var providers = new Dictionary<string, List<Model>>(StringComparer.Ordinal);
		foreach (var name in fileNames.OrderBy(static key => key, StringComparer.Ordinal))
		{
			using var document = JsonDocument.Parse(contents[name]);
			if (document.RootElement.ValueKind != JsonValueKind.Object)
			{
				throw new JsonException($"pi-ai data file {name} is not an object");
			}

			// 文件结构是 { API 分组: { 模型键: 模型 } }；分组顺序与条目顺序都照上游保留。
			foreach (var group in document.RootElement.EnumerateObject())
			{
				if (group.Value.ValueKind != JsonValueKind.Object)
				{
					continue;
				}

				foreach (var entry in group.Value.EnumerateObject())
				{
					if (entry.Value.ValueKind != JsonValueKind.Object)
					{
						continue;
					}

					var model = JsonSerializer.Deserialize(entry.Value, ModelsJsonContext.Default.Model);
					if (model is null || string.IsNullOrWhiteSpace(model.Id))
					{
						continue;
					}

					// 极少数条目缺 provider；用文件名兜底，避免模型落进空分组。
					var providerId = string.IsNullOrWhiteSpace(model.Provider)
						? name[..^".json".Length]
						: model.Provider;
					if (!providers.TryGetValue(providerId, out var list))
					{
						providers[providerId] = list = [];
					}

					list.Add(model.Provider == providerId ? model : model with { Provider = providerId });
				}
			}
		}

		return providers;
	}

	private static async Task<byte[]> GetAsync(HttpClient http, string url, int maxBytes, TimeSpan timeout, CancellationToken cancellationToken)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, url);
		request.Headers.TryAddWithoutValidation("accept", "application/json");
		request.Headers.TryAddWithoutValidation("user-agent", "PiWeft/0.1.0");
		using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		cancellation.CancelAfter(timeout);
		using var response = await http
			.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation.Token)
			.ConfigureAwait(false);
		if (!response.IsSuccessStatusCode)
		{
			throw new HttpRequestException($"HTTP {(int)response.StatusCode} {response.ReasonPhrase} from {new Uri(url).Host}");
		}

		return await OnlineModelCatalog.ReadBoundedAsync(response.Content, maxBytes, cancellation.Token).ConfigureAwait(false);
	}

	/// semver 比较：主版本逐段数值，预发布段按数值/字典序。解析失败按 0 处理。
	internal static int CompareSemver(string left, string right)
	{
		static (int[] Numbers, string[] Pre) Parse(string value)
		{
			var parts = value.Trim().Split('-', 2);
			var numbers = parts[0].Split('.').Select(part =>
				int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : 0).ToArray();
			return (numbers, parts.Length > 1 ? parts[1].Split('.') : []);
		}

		var (leftNumbers, leftPre) = Parse(left);
		var (rightNumbers, rightPre) = Parse(right);
		for (var index = 0; index < 3; index++)
		{
			var l = index < leftNumbers.Length ? leftNumbers[index] : 0;
			var r = index < rightNumbers.Length ? rightNumbers[index] : 0;
			if (l != r)
			{
				return l < r ? -1 : 1;
			}
		}

		if (leftPre.Length == 0 && rightPre.Length == 0)
		{
			return 0;
		}

		if (leftPre.Length == 0)
		{
			return 1;
		}

		if (rightPre.Length == 0)
		{
			return -1;
		}

		var length = Math.Max(leftPre.Length, rightPre.Length);
		for (var index = 0; index < length; index++)
		{
			var l = index < leftPre.Length ? leftPre[index] : null;
			var r = index < rightPre.Length ? rightPre[index] : null;
			if (l == r)
			{
				continue;
			}

			if (l is null)
			{
				return -1;
			}

			if (r is null)
			{
				return 1;
			}

			var leftNumeric = int.TryParse(l, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ln);
			var rightNumeric = int.TryParse(r, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rn);
			if (leftNumeric && rightNumeric)
			{
				return ln.CompareTo(rn);
			}

			if (leftNumeric)
			{
				return -1;
			}

			if (rightNumeric)
			{
				return 1;
			}

			return string.CompareOrdinal(l, r) < 0 ? -1 : 1;
		}

		return 0;
	}
}

internal enum PiAiSourceStatus
{
	Updated,
	NotModified,
	Failed,
}

internal sealed record PiAiSourceOutcome(
	PiAiSourceStatus Status,
	CatalogCandidate? Candidate,
	string? PackageVersion,
	ModelCatalogError Error,
	string? Message)
{
	public static PiAiSourceOutcome Updated(CatalogCandidate candidate) =>
		new(PiAiSourceStatus.Updated, candidate, candidate.Info.Version, ModelCatalogError.None, null);

	public static PiAiSourceOutcome NotModified(string version) =>
		new(PiAiSourceStatus.NotModified, null, version, ModelCatalogError.None, null);

	public static PiAiSourceOutcome Failed(ModelCatalogError error, string message) =>
		new(PiAiSourceStatus.Failed, null, null, error, message);
}

internal sealed record CatalogCandidate(
	byte[] Bytes,
	string ContentDigest,
	ModelCatalogInfo Info,
	string? ETag,
	string? DataManifestHash);
