using System.Buffers;
using System.Net;
using System.Text.Json;
using PiWeft.CodingAgent.Session;

namespace PiWeft.CodingAgent.Models;

/// 在线目录操作失败的原因。文案由界面按这个枚举决定，异常消息只进日志。
public enum ModelCatalogError
{
	None,
	Network,
	Validation,
	Write,
	NoBackup,
	Offline,
}

/// 检查结果：远端目录与当前生效目录是否不同。
public sealed record ModelCatalogCheckResult(
	bool Ok,
	bool HasUpdate,
	ModelCatalogInfo Local,
	ModelCatalogInfo Remote,
	string? RemoteDigest,
	ModelCatalogError Error,
	string? Message);

/// 更新或还原结果。<see cref="Updated"/> 为 <see langword="false"/> 表示无需写入（已是最新）。
public sealed record ModelCatalogUpdateResult(
	bool Ok,
	bool Updated,
	ModelCatalogError Error,
	string? Message,
	ModelCatalogInfo? Info = null);

/// 下载参数。测试注入本地 URL 与超时；默认先取 pi-ai npm 最新，失败时回退仓库 main 单文件。
public sealed record ModelCatalogFetchOptions
{
	/// 预生成目录在仓库里的位置（main 分支）。仓库文件是兜底源，字段格式与内嵌目录相同。
	public const string CatalogPathInRepo = "src/PiWeft.Ai/Resources/models.json";

	public const string GitHubUrl = "https://raw.githubusercontent.com/scgm0/PiWeft/main/" + CatalogPathInRepo;

	/// 镜像只是前缀代理：直连失败时把完整 GitHub raw URL 拼在 host 后面。
	/// 目录是公开数据，不携带凭据；镜像不可用只影响一次检查。
	public static IReadOnlyList<string> DefaultUrls { get; } =
	[
		GitHubUrl,
		"https://gh-proxy.com/" + GitHubUrl,
		"https://ghproxy.net/" + GitHubUrl,
	];

	public IReadOnlyList<string> Urls { get; init; } = DefaultUrls;

	/// 每次请求的超时时间。
	public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(20);

	/// 单个响应的大小上限。
	public int MaxBytes { get; init; } = ModelCatalogFiles.MaxCatalogBytes;

	/// 先从 pi-ai npm 取最新目录（带版本号、逐文件哈希）。关掉只用于测试仓库单文件源。
	public bool IncludePiAi { get; init; } = true;

	/// pi-ai 不可用时回退仓库 main 单文件。已有 pi-ai 覆盖版时不会回退，避免降级。
	public bool IncludeRepo { get; init; } = true;
}

/// 在线模型目录：优先从 pi-ai npm 生成最新目录，失败时回退仓库 main 单文件；
/// 校验后写入 agent 目录覆盖内置目录。写之前备份上一个覆盖版，任一步失败都不破坏当前生效目录。
public static class OnlineModelCatalog
{
	/// 目录与备份的读写共用一把锁：设置页手动更新与启动自动检查可能同时到达。
	private static readonly Lock Gate = new();

	private static readonly HttpClient SharedClient = new(new SocketsHttpHandler
	{
		PooledConnectionLifetime = TimeSpan.FromMinutes(5),
		AutomaticDecompression = DecompressionMethods.All,
		ConnectTimeout = TimeSpan.FromSeconds(10),
	})
	{
		Timeout = Timeout.InfiniteTimeSpan,
	};

	/// 检查远端目录是否与当前生效目录不同。304 或内容摘要相同都算已是最新。
	public static async Task<ModelCatalogCheckResult> CheckAsync(
		string? agentDir = null,
		HttpMessageHandler? handler = null,
		ModelCatalogFetchOptions? options = null,
		CancellationToken cancellationToken = default)
	{
		var local = ModelCatalogOverlay.EffectiveInfo(agentDir);
		if (PiEnvironment.Offline)
		{
			return new ModelCatalogCheckResult(false, false, local, local, null, ModelCatalogError.Offline, "offline mode");
		}

		var resolution = await ResolveAsync(agentDir, handler, options ?? new ModelCatalogFetchOptions(), cancellationToken)
			.ConfigureAwait(false);
		return new ModelCatalogCheckResult(
			resolution.Ok,
			resolution.HasUpdate,
			local,
			resolution.Remote,
			resolution.Candidate?.ContentDigest ?? ModelCatalogOverlay.EffectiveContentDigest(agentDir),
			resolution.Error,
			resolution.Message);
	}

	/// 下载并写入覆盖层。远端内容与当前一致时直接返回「已是最新」，不覆盖本地文件。
	public static async Task<ModelCatalogUpdateResult> UpdateAsync(
		string? agentDir = null,
		HttpMessageHandler? handler = null,
		ModelCatalogFetchOptions? options = null,
		CancellationToken cancellationToken = default)
	{
		if (PiEnvironment.Offline)
		{
			return new ModelCatalogUpdateResult(false, false, ModelCatalogError.Offline, "offline mode");
		}

		var resolution = await ResolveAsync(agentDir, handler, options ?? new ModelCatalogFetchOptions(), cancellationToken)
			.ConfigureAwait(false);
		if (!resolution.Ok)
		{
			return new ModelCatalogUpdateResult(false, false, resolution.Error, resolution.Message);
		}

		if (!resolution.HasUpdate || resolution.Candidate is not { } candidate)
		{
			return new ModelCatalogUpdateResult(true, false, ModelCatalogError.None, null, resolution.Remote);
		}

		lock (Gate)
		{
			// 下载期间可能有另一条路径刚写完同一份目录：以落盘时刻的摘要重新判定，避免重复备份。
			if (string.Equals(candidate.ContentDigest, ModelCatalogOverlay.EffectiveContentDigest(agentDir),
				StringComparison.OrdinalIgnoreCase))
			{
				return new ModelCatalogUpdateResult(true, false, ModelCatalogError.None, null, candidate.Info);
			}

			var meta = new ModelCatalogMeta
			{
				Digest = ModelCatalogOverlay.Digest(candidate.Bytes),
				EntryCount = candidate.Info.EntryCount,
				ProviderCount = candidate.Info.ProviderCount,
				FetchedAt = candidate.Info.FetchedAt ?? DateTimeOffset.UtcNow,
				Source = candidate.Info.Source ?? string.Empty,
				PackageVersion = candidate.Info.Version,
				DataManifestHash = candidate.DataManifestHash,
				ETag = candidate.ETag,
			};

			try
			{
				WriteOverlay(candidate.Bytes, meta, agentDir);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
			{
				return new ModelCatalogUpdateResult(false, false, ModelCatalogError.Write, ex.Message);
			}

			return new ModelCatalogUpdateResult(true, true, ModelCatalogError.None, null, candidate.Info);
		}
	}

	/// 还原内置目录：把当前覆盖层移到备份（下一次还原上一个覆盖版仍可用）。
	public static ModelCatalogUpdateResult RestoreBuiltin(string? agentDir = null)
	{
		lock (Gate)
		{
			var catalogPath = ModelCatalogFiles.CatalogPath(agentDir);
			var metaPath = ModelCatalogFiles.MetaPath(agentDir);
			var hasCatalog = File.Exists(catalogPath);
			var hasMeta = File.Exists(metaPath);
			if (!hasCatalog && !hasMeta)
			{
				return new ModelCatalogUpdateResult(true, false, ModelCatalogError.None, null);
			}

			try
			{
				if (hasCatalog && hasMeta && ModelCatalogOverlay.ReadOverlay(agentDir) is not null)
				{
					// 先写两枚临时副本，再替换旧的备份对；中途失败不会留下「目录是新的、元数据是旧的」。
					BackupPair(catalogPath, metaPath, ModelCatalogFiles.CatalogBackupPath(agentDir), ModelCatalogFiles.MetaBackupPath(agentDir));
					File.Delete(catalogPath);
					File.Delete(metaPath);
				}
				else
				{
					// 半份或校验不过的覆盖层本来就不生效：删当前文件即可，
					// 不要用它覆盖已有的有效备份。
					if (hasCatalog) File.Delete(catalogPath);
					if (hasMeta) File.Delete(metaPath);
				}
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				ModelCatalogOverlay.InvalidateCache();
				return new ModelCatalogUpdateResult(false, false, ModelCatalogError.Write, ex.Message);
			}

			ModelCatalogOverlay.InvalidateCache();
			return new ModelCatalogUpdateResult(true, true, ModelCatalogError.None, null);
		}
	}

	/// 恢复上一个覆盖版：校验备份后写回覆盖层，当前覆盖层转为新的备份（两版互换）。
	public static ModelCatalogUpdateResult RestorePrevious(string? agentDir = null)
	{
		lock (Gate)
		{
			var catalogBackup = ModelCatalogFiles.CatalogBackupPath(agentDir);
			var metaBackup = ModelCatalogFiles.MetaBackupPath(agentDir);
			if (!File.Exists(catalogBackup) || !File.Exists(metaBackup))
			{
				return new ModelCatalogUpdateResult(false, false, ModelCatalogError.NoBackup, "no overlay backup found");
			}

			try
			{
				var bytes = File.ReadAllBytes(catalogBackup);
				var meta = JsonSerializer.Deserialize(File.ReadAllText(metaBackup), ModelCatalogJsonContext.Default.ModelCatalogMeta);
				if (meta is null ||
					!string.Equals(ModelCatalogOverlay.Digest(bytes), meta.Digest, StringComparison.OrdinalIgnoreCase))
				{
					return new ModelCatalogUpdateResult(false, false, ModelCatalogError.Validation, "backup digest mismatch");
				}

				var models = ModelCatalogOverlay.ParseCatalog(bytes);
				var summary = ModelCatalogOverlay.Summarize(models);
				WriteOverlay(bytes, meta with { EntryCount = summary.EntryCount, ProviderCount = summary.ProviderCount }, agentDir);
				return new ModelCatalogUpdateResult(true, true, ModelCatalogError.None, null,
					new ModelCatalogInfo(
						summary.ProviderCount,
						summary.EntryCount,
						meta.FetchedAt,
						meta.Source,
						meta.PackageVersion));
			}
			catch (JsonException ex)
			{
				return new ModelCatalogUpdateResult(false, false, ModelCatalogError.Validation, ex.Message);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				return new ModelCatalogUpdateResult(false, false, ModelCatalogError.Write, ex.Message);
			}
		}
	}

	/// 解析并比较两个目录源：pi-ai npm 优先，已有 pi-ai 覆盖版时不再回退无版本号的仓库文件。
	private static async Task<Resolution> ResolveAsync(
		string? agentDir,
		HttpMessageHandler? handler,
		ModelCatalogFetchOptions options,
		CancellationToken cancellationToken)
	{
		var overlay = ModelCatalogOverlay.ReadOverlay(agentDir);
		var local = overlay?.Info ?? new ModelCatalogInfo(
			ModelCatalogOverlay.Builtin.ProviderCount,
			ModelCatalogOverlay.Builtin.EntryCount,
			null,
			null,
			null);
		var localDigest = overlay?.ContentDigest ?? ModelCatalogOverlay.Builtin.ContentDigest;

		using var client = handler is null ? null : new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
		var http = client ?? SharedClient;

		PiAiSourceOutcome? npm = null;
		if (options.IncludePiAi)
		{
			npm = await PiAiCatalogSource.FetchAsync(http, overlay?.Meta.PackageVersion, options, cancellationToken)
				.ConfigureAwait(false);
			if (npm.Status == PiAiSourceStatus.NotModified)
			{
				// 远端不高于当前：显示本地版本，避免「本地 1.5.3、提示最新 1.5.2」。
				return Resolution.NoUpdate(local.Version is null ? local with { Source = "pi-ai", Version = npm.PackageVersion } : local);
			}

			if (npm is { Status: PiAiSourceStatus.Updated, Candidate: { } candidate })
			{
				return SameContent(candidate, localDigest)
					? Resolution.NoUpdate(candidate.Info)
					: Resolution.Update(candidate);
			}

			// 覆盖层来自 pi-ai 时，仓库单文件没有版本号，可能比当前目录旧；宁可报错也不降级。
			if (overlay?.Meta.PackageVersion is not null)
			{
				return Resolution.Failed(npm.Error, npm.Message);
			}
		}

		if (options.IncludeRepo)
		{
			var repo = await DownloadRepoAsync(http, options, overlay?.Meta.ETag, cancellationToken).ConfigureAwait(false);
			if (repo.Error != ModelCatalogError.None)
			{
				return Resolution.Failed(repo.Error, repo.Message);
			}

			if (repo.NotModified)
			{
				return Resolution.NoUpdate(local);
			}

			var candidate = repo.Candidate!;
			return SameContent(candidate, localDigest)
				? Resolution.NoUpdate(candidate.Info)
				: Resolution.Update(candidate);
		}

		return npm is null
			? Resolution.Failed(ModelCatalogError.Network, "no catalog source enabled")
			: Resolution.Failed(npm.Error, npm.Message);
	}

	private static bool SameContent(CatalogCandidate candidate, string localDigest) =>
		string.Equals(candidate.ContentDigest, localDigest, StringComparison.OrdinalIgnoreCase);

	/// 仓库单文件源：一个请求，目录本身就是落盘格式，不需要再生成。
	private static async Task<RepoOutcome> DownloadRepoAsync(
		HttpClient http,
		ModelCatalogFetchOptions options,
		string? etag,
		CancellationToken cancellationToken)
	{
		if (options.Urls.Count == 0)
		{
			return RepoOutcome.Failed(ModelCatalogError.Network, "no catalog source configured");
		}

		var lastMessage = string.Empty;
		var sawInvalidContent = false;
		foreach (var url in options.Urls)
		{
			cancellationToken.ThrowIfCancellationRequested();
			try
			{
				using var request = new HttpRequestMessage(HttpMethod.Get, url);
				request.Headers.TryAddWithoutValidation("accept", "application/json");
				request.Headers.TryAddWithoutValidation("user-agent", "PiWeft/0.1.0");
				if (!string.IsNullOrWhiteSpace(etag))
				{
					request.Headers.TryAddWithoutValidation("if-none-match", etag);
				}

				using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
				cancellation.CancelAfter(options.Timeout);
				using var response = await http
					.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation.Token)
					.ConfigureAwait(false);

				if (response.StatusCode == HttpStatusCode.NotModified)
				{
					return RepoOutcome.Unmodified();
				}

				if (!response.IsSuccessStatusCode)
				{
					lastMessage = $"HTTP {(int)response.StatusCode} from {HostOf(url)}";
					continue;
				}

				var bytes = await ReadBoundedAsync(response.Content, options.MaxBytes, cancellation.Token).ConfigureAwait(false);
				// 代理在失败时可能回一张 HTML 错误页；内容不像目录就换下一个源。
				var models = ModelCatalogOverlay.ParseCatalog(bytes);
				var summary = ModelCatalogOverlay.Summarize(models);
				var info = new ModelCatalogInfo(summary.ProviderCount, summary.EntryCount, DateTimeOffset.UtcNow, HostOf(url), null);
				return RepoOutcome.Updated(new CatalogCandidate(
					bytes,
					ModelCatalogOverlay.CanonicalDigest(models),
					info,
					response.Headers.ETag?.Tag,
					DataManifestHash: null));
			}
			catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
			{
				lastMessage = $"timeout from {HostOf(url)}";
			}
			catch (JsonException ex)
			{
				sawInvalidContent = true;
				lastMessage = $"invalid catalog from {HostOf(url)}: {ex.Message}";
			}
			catch (HttpRequestException ex)
			{
				lastMessage = $"network error from {HostOf(url)}: {ex.Message}";
			}
			catch (IOException ex)
			{
				lastMessage = $"failed to read catalog from {HostOf(url)}: {ex.Message}";
			}
		}

		return RepoOutcome.Failed(sawInvalidContent ? ModelCatalogError.Validation : ModelCatalogError.Network, lastMessage);
	}

	internal static async Task<byte[]> ReadBoundedAsync(HttpContent content, int maxBytes, CancellationToken cancellationToken)
	{
		if (content.Headers.ContentLength is { } length && length > maxBytes)
		{
			throw new JsonException($"catalog content length {length} exceeds {maxBytes}");
		}

		await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
		// 43 个数据文件并发下载时，每份再临时分配 64 KB 归零缓冲区没必要；租一块循环用。
		var chunk = ArrayPool<byte>.Shared.Rent(64 * 1024);
		try
		{
			using var buffer = content.Headers.ContentLength is { } expected && expected <= maxBytes
				? new MemoryStream((int)expected)
				: new MemoryStream();
			int read;
			while ((read = await stream.ReadAsync(chunk.AsMemory(0, chunk.Length), cancellationToken).ConfigureAwait(false)) > 0)
			{
				if (buffer.Length + read > maxBytes)
				{
					throw new JsonException($"catalog content exceeds {maxBytes} bytes");
				}

				buffer.Write(chunk, 0, read);
			}

			return buffer.ToArray();
		}
		finally
		{
			ArrayPool<byte>.Shared.Return(chunk);
		}
	}

	/// 写入覆盖层：先把当前有效的一对文件复制为备份，再原子替换两个文件。
	private static void WriteOverlay(byte[] catalog, ModelCatalogMeta meta, string? agentDir)
	{
		var directory = agentDir ?? SessionPaths.GetAgentDir();
		Directory.CreateDirectory(directory);
		var catalogPath = ModelCatalogFiles.CatalogPath(directory);
		var metaPath = ModelCatalogFiles.MetaPath(directory);
		var catalogBackup = ModelCatalogFiles.CatalogBackupPath(directory);
		var metaBackup = ModelCatalogFiles.MetaBackupPath(directory);

		// 只有当前这一对校验通过才值得备份；半份或坏掉的当前目录会毁掉可恢复的上一版。
		// 当前没有覆盖层时保留旧备份，用户在「还原内置版」之后仍能恢复上一个覆盖版。
		var hasCurrentPair = File.Exists(catalogPath) && File.Exists(metaPath);
		if (hasCurrentPair && ModelCatalogOverlay.ReadOverlay(directory) is not null)
		{
			BackupPair(catalogPath, metaPath, catalogBackup, metaBackup);
		}

		WriteAtomically(catalogPath, catalog);
		WriteAtomically(metaPath, JsonSerializer.SerializeToUtf8Bytes(meta, ModelCatalogJsonContext.Default.ModelCatalogMeta));
		ModelCatalogOverlay.InvalidateCache();
	}

	/// 成对备份：两枚临时文件都写成功后才替换旧备份，失败时清掉可能只替换了一半的新备份。
	private static void BackupPair(string catalogPath, string metaPath, string catalogBackup, string metaBackup)
	{
		// 两个进程可能同时写同一份备份：临时文件名带上 GUID，避免互相覆盖/删除。
		var catalogTemporary = $"{catalogBackup}.{Guid.NewGuid():N}.tmp";
		var metaTemporary = $"{metaBackup}.{Guid.NewGuid():N}.tmp";
		try
		{
			File.Copy(catalogPath, catalogTemporary, overwrite: true);
			File.Copy(metaPath, metaTemporary, overwrite: true);
		}
		catch
		{
			TryDelete(catalogTemporary);
			TryDelete(metaTemporary);
			throw;
		}

		var catalogMoved = false;
		try
		{
			File.Move(catalogTemporary, catalogBackup, overwrite: true);
			catalogMoved = true;
			File.Move(metaTemporary, metaBackup, overwrite: true);
		}
		catch
		{
			// 只换了一半的备份无法通过校验：宁可清掉，也不要让「恢复上一个覆盖版」指向错配的一对。
			// 第一枚还没换时旧备份仍是完整的，保留它。
			if (catalogMoved)
			{
				TryDelete(catalogBackup);
				TryDelete(metaBackup);
			}
			TryDelete(catalogTemporary);
			TryDelete(metaTemporary);
			throw;
		}
	}

	private static void WriteAtomically(string path, ReadOnlySpan<byte> bytes)
	{
		var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
		try
		{
			File.WriteAllBytes(temporary, bytes);
			File.Move(temporary, path, overwrite: true);
		}
		finally
		{
			TryDelete(temporary);
		}
	}

	private static void TryDelete(string path)
	{
		try
		{
			if (File.Exists(path)) File.Delete(path);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			// 清临时文件失败不影响主流程：下次写入会覆盖同名目标。
		}
	}

	private static string HostOf(string url) =>
		Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;

	private sealed record Resolution(
		bool Ok,
		bool HasUpdate,
		CatalogCandidate? Candidate,
		ModelCatalogInfo Remote,
		ModelCatalogError Error,
		string? Message)
	{
		public static Resolution NoUpdate(ModelCatalogInfo remote) =>
			new(true, false, null, remote, ModelCatalogError.None, null);

		public static Resolution Update(CatalogCandidate candidate) =>
			new(true, true, candidate, candidate.Info, ModelCatalogError.None, null);

		public static Resolution Failed(ModelCatalogError error, string? message) =>
			new(false, false, null, new ModelCatalogInfo(0, 0, null, null, null), error, message ?? "catalog update failed");
	}

	private sealed record RepoOutcome(
		bool NotModified,
		CatalogCandidate? Candidate,
		ModelCatalogError Error,
		string? Message)
	{
		public static RepoOutcome Unmodified() => new(true, null, ModelCatalogError.None, null);

		public static RepoOutcome Updated(CatalogCandidate candidate) => new(false, candidate, ModelCatalogError.None, null);

		public static RepoOutcome Failed(ModelCatalogError error, string message) => new(false, null, error, message);
	}
}
