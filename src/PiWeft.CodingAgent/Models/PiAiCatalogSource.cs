using System.Buffers;
using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using PiWeft.Ai;

namespace PiWeft.CodingAgent.Models;

/// pi-ai npm 包里的现成目录。数据文件按 API 分组（<c>{ group: { key: model } }</c>），
/// 每个模型自带 provider；合并成 PiWeft 的 <c>provider → models</c> 格式即可直接用。
///
/// 取目录只下载一个 npm tarball（约 0.9 MB），在内存里用 <see cref="TarReader"/> 取
/// <c>dist/providers/data/*.json</c>，不落盘。tarball 先过 registry 的 SHA-512 完整性，
/// 每个数据文件再过包内 <c>.manifest.json</c> 的 SHA-256，代理或 CDN 返回半份内容会被拒绝。
internal static class PiAiCatalogSource
{
	public const string PackageName = "@earendil-works/pi-ai";

	private const string DataPrefix = "package/dist/providers/data/";
	private const string ManifestName = ".manifest.json";
	private const int MaxMetadataBytes = 256 * 1024;
	private const int MaxTarballBytes = 16 * 1024 * 1024;
	private const int MaxDecompressedBytes = 64 * 1024 * 1024;
	private const int MaxDataFileBytes = 4 * 1024 * 1024;
	private const int MaxTotalBytes = 32 * 1024 * 1024;
	private const int MaxFiles = 256;

	private static readonly string PackageFileName = PackageName[(PackageName.IndexOf('/') + 1)..];

	private static readonly JsonDocumentOptions StrictDocument = new()
	{
		AllowDuplicateProperties = false,
		MaxDepth = 32,
	};

	/// 两个 npm registry 的元数据与 tarball 地址。国内镜像优先，官方兜底。
	private static readonly RegistryEndpoint[] Registries =
	[
		new("registry.npmmirror.com"),
		new("registry.npmjs.org"),
	];

	/// 数据来源优先级：先 pi-ai npm（最新），拿不到时由调用方回退仓库单文件。
	/// 已有 pi-ai 覆盖版时不再回退，避免用没有版本号的仓库文件把目录降级。
	public static async Task<PiAiSourceOutcome> FetchAsync(
		HttpClient http,
		string? currentVersion,
		ModelCatalogFetchOptions options,
		CancellationToken cancellationToken)
	{
		ModelCatalogError lastError = ModelCatalogError.Network;
		var lastMessage = string.Empty;
		foreach (var registry in Registries)
		{
			cancellationToken.ThrowIfCancellationRequested();
			try
			{
				var package = await ResolvePackageAsync(http, registry, options.Timeout, cancellationToken).ConfigureAwait(false);
				if (currentVersion is not null && CompareSemver(package.Version, currentVersion) <= 0)
				{
					// 远端不高于当前版本：不降级，也不必下载 tarball。
					return PiAiSourceOutcome.NotModified(package.Version);
				}

				var tarball = await GetAsync(
					http,
					package.TarballUrl ?? registry.TarballUrl(package.Version),
					MaxTarballBytes,
					options.Timeout,
					cancellationToken).ConfigureAwait(false);
				if (!VerifyIntegrity(tarball, package.Integrity))
				{
					throw new JsonException("pi-ai tarball failed integrity check");
				}

				return PiAiSourceOutcome.Updated(BuildCandidate(tarball, package.Version, cancellationToken));
			}
			catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
			{
				lastError = ModelCatalogError.Network;
				lastMessage = $"pi-ai request timed out on {registry.Host}";
			}
			catch (Exception ex) when (ex is JsonException or InvalidDataException or FormatException)
			{
				lastError = ModelCatalogError.Validation;
				lastMessage = $"{registry.Host}: {ex.Message}";
			}
			catch (HttpRequestException ex)
			{
				if (lastError != ModelCatalogError.Validation) lastError = ModelCatalogError.Network;
				lastMessage = $"{registry.Host}: {ex.Message}";
			}
			catch (IOException ex)
			{
				if (lastError != ModelCatalogError.Validation) lastError = ModelCatalogError.Network;
				lastMessage = $"{registry.Host}: {ex.Message}";
			}
		}

		return PiAiSourceOutcome.Failed(lastError, lastMessage);
	}

	private static async Task<PiAiPackage> ResolvePackageAsync(
		HttpClient http,
		RegistryEndpoint registry,
		TimeSpan timeout,
		CancellationToken cancellationToken)
	{
		var bytes = await GetAsync(http, registry.LatestUrl, MaxMetadataBytes, timeout, cancellationToken).ConfigureAwait(false);
		using var document = JsonDocument.Parse(bytes, StrictDocument);
		var root = document.RootElement;
		if (root.ValueKind != JsonValueKind.Object ||
			!root.TryGetProperty("version", out var versionElement) ||
			versionElement.ValueKind != JsonValueKind.String)
		{
			throw new JsonException("pi-ai metadata has no version");
		}

		var version = NormalizeVersion(versionElement.GetString());
		if (version is null)
		{
			throw new JsonException("pi-ai metadata has an invalid version");
		}

		string? tarballUrl = null;
		string? integrity = null;
		if (root.TryGetProperty("dist", out var dist) && dist.ValueKind == JsonValueKind.Object)
		{
			if (dist.TryGetProperty("tarball", out var tarball) && tarball.ValueKind == JsonValueKind.String)
			{
				var candidate = tarball.GetString();
				if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
				{
					tarballUrl = candidate;
				}
			}

			if (dist.TryGetProperty("integrity", out var sri) && sri.ValueKind == JsonValueKind.String)
			{
				integrity = sri.GetString();
			}
		}

		return new PiAiPackage(version, tarballUrl, integrity);
	}

	private static CatalogCandidate BuildCandidate(byte[] tarball, string version, CancellationToken cancellationToken)
	{
		// 先解压到内存再交给 TarReader：tar 可以 seek，跳过源码/类型声明条目更快；
		// 同时给解压总量设上限，压缩炸弹不会把进程拖进无界解压。
		using var decompressed = new MemoryStream();
		using (var gzip = new GZipStream(new MemoryStream(tarball, writable: false), CompressionMode.Decompress))
		{
			CopyBounded(gzip, decompressed, MaxDecompressedBytes, cancellationToken);
		}

		decompressed.Position = 0;
		using var reader = new TarReader(decompressed);
		var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
		byte[]? manifest = null;
		var totalBytes = 0L;

		while (reader.GetNextEntry() is { } entry)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (!entry.Name.StartsWith(DataPrefix, StringComparison.Ordinal) ||
				!entry.Name.EndsWith(".json", StringComparison.Ordinal) ||
				entry.DataStream is null)
			{
				continue;
			}

			var name = entry.Name[DataPrefix.Length..];
			if (name.Length == 0 || name.Contains('/') || name.Contains('\\') || name.Contains("..", StringComparison.Ordinal))
			{
				continue;
			}

			var bytes = ReadEntry(entry.DataStream, entry.Length, cancellationToken);
			totalBytes += bytes.Length;
			if (totalBytes > MaxTotalBytes)
			{
				throw new JsonException($"pi-ai catalog exceeds {MaxTotalBytes} bytes");
			}

			if (name == ManifestName)
			{
				manifest = bytes;
			}
			else
			{
				files[name] = bytes;
			}
		}

		if (manifest is null)
		{
			throw new JsonException("pi-ai tarball has no data manifest");
		}

		var manifestHash = ModelCatalogOverlay.Digest(manifest);
		var (expected, generatedAt) = ParseManifest(manifest);
		foreach (var (name, hash) in expected)
		{
			if (!files.TryGetValue(name, out var bytes))
			{
				throw new JsonException($"pi-ai tarball is missing {name}");
			}

			var actual = Convert.ToHexStringLower(SHA256.HashData(bytes));
			if (!string.Equals(actual, hash, StringComparison.OrdinalIgnoreCase))
			{
				throw new JsonException($"pi-ai data file {name} does not match its manifest hash");
			}
		}

		var providers = Merge(expected.Keys, files);
		// 用 PiWeft 的落盘格式重新序列化；后续读取与内嵌目录走同一条解析路径。
		var catalog = JsonSerializer.SerializeToUtf8Bytes(providers, ModelCatalogJsonContext.Default.DictionaryStringListModel);
		var parsed = ModelCatalogOverlay.ParseCatalog(catalog);
		var summary = ModelCatalogOverlay.Summarize(parsed);
		var info = new ModelCatalogInfo(
			summary.ProviderCount,
			summary.EntryCount,
			generatedAt ?? DateTimeOffset.UtcNow,
			"pi-ai",
			version);
		return new CatalogCandidate(
			catalog,
			ModelCatalogOverlay.CanonicalDigest(parsed),
			info,
			ETag: null,
			DataManifestHash: manifestHash);
	}

	private static void CopyBounded(Stream source, Stream destination, int limit, CancellationToken cancellationToken)
	{
		var chunk = ArrayPool<byte>.Shared.Rent(64 * 1024);
		try
		{
			var total = 0;
			int read;
			while ((read = source.Read(chunk, 0, chunk.Length)) > 0)
			{
				cancellationToken.ThrowIfCancellationRequested();
				total += read;
				if (total > limit)
				{
					throw new JsonException($"pi-ai tarball expands beyond {limit} bytes");
				}

				destination.Write(chunk, 0, read);
			}
		}
		finally
		{
			ArrayPool<byte>.Shared.Return(chunk);
		}
	}

	private static byte[] ReadEntry(Stream stream, long length, CancellationToken cancellationToken)
	{
		if (length is < 0 or > MaxDataFileBytes)
		{
			throw new JsonException($"pi-ai data entry exceeds {MaxDataFileBytes} bytes");
		}

		using var buffer = new MemoryStream((int)length);
		// TarReader 的 DataStream 已按条目长度截断；CopyTo 内部用池化缓冲，不用逐条再分配。
		stream.CopyTo(buffer);
		if (buffer.Length > MaxDataFileBytes)
		{
			throw new JsonException($"pi-ai data entry exceeds {MaxDataFileBytes} bytes");
		}

		cancellationToken.ThrowIfCancellationRequested();
		return buffer.ToArray();
	}

	private static (Dictionary<string, string> Files, DateTimeOffset? GeneratedAt) ParseManifest(byte[] bytes)
	{
		using var document = JsonDocument.Parse(bytes, StrictDocument);
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

	private static Dictionary<string, List<Model>> Merge(IEnumerable<string> fileNames, IReadOnlyDictionary<string, byte[]> contents)
	{
		var providers = new Dictionary<string, List<Model>>(StringComparer.Ordinal);
		var total = 0;
		foreach (var name in fileNames.OrderBy(static key => key, StringComparer.Ordinal))
		{
			// 每个文件是 { API 分组: { 模型键: 模型 } }。一次源生成反序列化拿到全部模型，
			// 比先建 JsonDocument 再逐条解析少一层 DOM 与中间对象。
			var groups = JsonSerializer.Deserialize(contents[name], ModelCatalogJsonContext.Default.DictionaryStringDictionaryStringModel)
				?? throw new JsonException($"pi-ai data file {name} is empty");

			// 分组顺序与条目顺序都照上游保留，重复 ID 的覆盖顺序才与内嵌目录一致。
			foreach (var group in groups.Values)
			{
				if (group is null)
				{
					continue;
				}

				foreach (var model in group.Values)
				{
					if (model is null || string.IsNullOrWhiteSpace(model.Id))
					{
						continue;
					}

					if (++total > ModelCatalogFiles.MaxCatalogModels)
					{
						throw new JsonException($"pi-ai catalog has more than {ModelCatalogFiles.MaxCatalogModels} models");
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

	private static bool VerifyIntegrity(byte[] tarball, string? integrity)
	{
		// npm 的 dist.integrity 是 sha512；没有它时仍有逐文件 SHA-256 兜底。
		if (string.IsNullOrWhiteSpace(integrity))
		{
			return true;
		}

		const string prefix = "sha512-";
		if (!integrity.StartsWith(prefix, StringComparison.Ordinal))
		{
			return false;
		}

		try
		{
			var expected = Convert.FromBase64String(integrity[prefix.Length..]);
			return CryptographicOperations.FixedTimeEquals(SHA512.HashData(tarball), expected);
		}
		catch (FormatException)
		{
			return false;
		}
	}

	private static string? NormalizeVersion(string? value)
	{
		var text = value?.Trim();
		if (text is { Length: > 1 } && text[0] == 'v')
		{
			text = text[1..];
		}

		if (string.IsNullOrEmpty(text) || text.Length > 64 ||
			!text.All(static ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-' or '+'))
		{
			return null;
		}

		return text;
	}

	/// semver 比较：主版本逐段数值，预发布段按数值/字典序。解析失败按 0 处理。
	internal static int CompareSemver(string left, string right)
	{
		static (int[] Numbers, string[] Pre) Parse(string value)
		{
			var parts = value.Trim().Split('-', 2);
			// semver 允许 `+build` 元数据：它不参与优先级比较，必须在拆段前去掉。
			var main = parts[0].Split('+', 2)[0];
			var numbers = main.Split('.').Select(part =>
				int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : 0).ToArray();
			var pre = parts.Length > 1 ? parts[1].Split('+', 2)[0] : string.Empty;
			return (numbers, pre.Length > 0 ? pre.Split('.') : []);
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

	private sealed record RegistryEndpoint(string Host)
	{
		public string LatestUrl => $"https://{Host}/{PackageName}/latest";

		public string TarballUrl(string version) => $"https://{Host}/{PackageName}/-/{PackageFileName}-{version}.tgz";
	}

	private sealed record PiAiPackage(string Version, string? TarballUrl, string? Integrity);
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
