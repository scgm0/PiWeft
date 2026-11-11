using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PiWeft.Ai;
using PiWeft.CodingAgent.Session;
using PiWeft.CodingAgent.Settings;

namespace PiWeft.CodingAgent.Models;

/// 在线模型目录落地到 agent 目录的文件名与路径。覆盖层与备份都在这里，
/// 读取端只需要认识这几个名字。
public static class ModelCatalogFiles
{
	/// 覆盖层目录文件名。
	public const string CatalogName = "model-catalog.json";

	/// 覆盖层本身的元数据：摘要、来源版本、条目数与抓取时间。摘要用于识别半写与外部改动。
	public const string MetaName = "model-catalog.meta.json";

	/// 备份后缀。目录与元数据各自备份，恢复时成对使用。
	public const string BackupSuffix = ".bak";

	/// 单份目录的大小上限。内嵌目录约 1.3 MB，16 MB 足够容纳上游扩张。
	public const int MaxCatalogBytes = 16 * 1024 * 1024;

	/// 元数据文件的大小上限。
	public const int MaxMetaBytes = 64 * 1024;

	/// 覆盖层目录的完整路径。
	public static string CatalogPath(string? agentDir = null) =>
		Path.Combine(agentDir ?? SessionPaths.GetAgentDir(), CatalogName);

	/// 覆盖层元数据的完整路径。
	public static string MetaPath(string? agentDir = null) =>
		Path.Combine(agentDir ?? SessionPaths.GetAgentDir(), MetaName);

	/// 上一个覆盖版目录的备份路径。
	public static string CatalogBackupPath(string? agentDir = null) =>
		CatalogPath(agentDir) + BackupSuffix;

	/// 上一个覆盖版元数据的备份路径。
	public static string MetaBackupPath(string? agentDir = null) =>
		MetaPath(agentDir) + BackupSuffix;
}

/// 一份生效目录的摘要：供应商数、模型条数、抓取时间、来源与上游版本。
public sealed record ModelCatalogInfo(int ProviderCount, int EntryCount, DateTimeOffset? FetchedAt, string? Source, string? Version);

/// 设置页要展示的目录状态。内置目录始终有效；覆盖层可能缺失、损坏或被备份。
public sealed record ModelCatalogStatus(
	ModelCatalogInfo Builtin,
	ModelCatalogInfo? Overlay,
	bool HasOverlayFiles,
	bool HasBackup,
	bool OverlayInvalid);

/// 覆盖层元数据，写在 <see cref="ModelCatalogFiles.MetaName"/>。
internal sealed record ModelCatalogMeta
{
	public required string Digest { get; init; }

	public int EntryCount { get; init; }

	public int ProviderCount { get; init; }

	public DateTimeOffset FetchedAt { get; init; }

	public string Source { get; init; } = string.Empty;

	/// pi-ai npm 包版本；仓库单文件源没有版本，留空。
	public string? PackageVersion { get; init; }

	/// pi-ai 数据清单的摘要，便于诊断源数据是否变化。
	public string? DataManifestHash { get; init; }

	public string? ETag { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ModelCatalogMeta))]
internal partial class ModelCatalogJsonContext : JsonSerializerContext;

/// 内置目录之上的一层在线目录：读取校验通过才生效，否则回退内嵌 <c>models.json</c>。
/// 校验同时绑定文件与摘要，防止下载中断后留下半份目录被当成新版本。
public static class ModelCatalogOverlay
{
	private static readonly Lazy<EmbeddedCatalog> Embedded = new(ReadEmbedded);

	/// 内嵌目录的摘要与条目数，供在线检查比较。
	internal static EmbeddedCatalog Builtin => Embedded.Value;

	/// 在线覆盖层优先：有效时整份替换内嵌目录，上游删掉的模型不会继续留在选择器里；
	/// 覆盖层缺失或校验失败时才回退内嵌 <c>models.json</c>。
	public static ModelRegistry CreateRegistry(string? agentDir = null)
	{
		var registry = new ModelRegistry();
		if (Apply(registry, agentDir) > 0)
		{
			return registry;
		}

		return ModelRegistry.LoadDefault();
	}

	/// 把有效的覆盖层合入已有注册表，返回合入的模型数；无效或缺失时返回 0。
	public static int Apply(ModelRegistry registry, string? agentDir = null)
	{
		ArgumentNullException.ThrowIfNull(registry);
		var overlay = ReadOverlay(agentDir);
		if (overlay is null)
		{
			return 0;
		}

		try
		{
			using var stream = new MemoryStream(overlay.Catalog, writable: false);
			return registry.AddCatalog(stream);
		}
		catch (JsonException)
		{
			// ReadOverlay 已解析过一次；这里失败说明两次读取之间文件被换掉了，按无覆盖处理。
			return 0;
		}
	}

	/// 当前设置页状态。损坏的覆盖层不算生效，但仍报告文件存在，用户才能还原。
	public static ModelCatalogStatus Status(string? agentDir = null)
	{
		var overlay = ReadOverlay(agentDir);
		var hasFiles = File.Exists(ModelCatalogFiles.CatalogPath(agentDir)) ||
			File.Exists(ModelCatalogFiles.MetaPath(agentDir));
		return new ModelCatalogStatus(
			new ModelCatalogInfo(Builtin.ProviderCount, Builtin.EntryCount, null, null, null),
			overlay?.Info,
			hasFiles,
			File.Exists(ModelCatalogFiles.CatalogBackupPath(agentDir)) &&
				File.Exists(ModelCatalogFiles.MetaBackupPath(agentDir)),
			hasFiles && overlay is null);
	}

	/// 生效目录文件：覆盖层有效时是 agent 目录里的那份；否则内置目录没有独立文件。
	public static string? EffectivePath(string? agentDir = null) =>
		ReadOverlay(agentDir) is null ? null : ModelCatalogFiles.CatalogPath(agentDir);

	/// 当前生效目录的内容摘要。按 provider 与模型 ID 归一化后计算，
	/// 因此同一份目录由 pi-ai 现生成或从仓库单文件下载，只要内容相同就得到相同摘要。
	internal static string EffectiveContentDigest(string? agentDir = null) =>
		ReadOverlay(agentDir)?.ContentDigest ?? Builtin.ContentDigest;

	internal static ModelCatalogInfo EffectiveInfo(string? agentDir = null) =>
		ReadOverlay(agentDir)?.Info ?? new ModelCatalogInfo(Builtin.ProviderCount, Builtin.EntryCount, null, null, null);

	/// 读取并校验覆盖层。两个文件必须成对存在，摘要必须与目录内容一致。
	internal static OverlaySnapshot? ReadOverlay(string? agentDir = null)
	{
		var catalogPath = ModelCatalogFiles.CatalogPath(agentDir);
		var metaPath = ModelCatalogFiles.MetaPath(agentDir);
		try
		{
			if (!File.Exists(catalogPath) || !File.Exists(metaPath))
			{
				return null;
			}

			var catalogLength = new FileInfo(catalogPath).Length;
			var metaLength = new FileInfo(metaPath).Length;
			if (catalogLength is <= 0 or > ModelCatalogFiles.MaxCatalogBytes ||
				metaLength is <= 0 or > ModelCatalogFiles.MaxMetaBytes)
			{
				return null;
			}

			var catalog = File.ReadAllBytes(catalogPath);
			var meta = JsonSerializer.Deserialize(File.ReadAllText(metaPath), ModelCatalogJsonContext.Default.ModelCatalogMeta);
			if (meta is null || string.IsNullOrWhiteSpace(meta.Digest))
			{
				return null;
			}

			if (!string.Equals(Digest(catalog), meta.Digest, StringComparison.OrdinalIgnoreCase))
			{
				return null;
			}

			var models = ParseCatalog(catalog);
			var summary = Summarize(models);
			return new OverlaySnapshot(
				catalog,
				meta,
				models,
				CanonicalDigest(models),
				new ModelCatalogInfo(summary.ProviderCount, summary.EntryCount, meta.FetchedAt, meta.Source, meta.PackageVersion));
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
		{
			return null;
		}
	}

	/// 把 <c>provider → 模型数组</c> 解析成注册表可用的字典。
	/// 空 ID、空分组整份拒绝：在线目录是一个文件，缺字段通常意味着下载被截断。
	internal static Dictionary<string, List<Model>> ParseCatalog(ReadOnlySpan<byte> catalog)
	{
		if (catalog.Length is 0 or > ModelCatalogFiles.MaxCatalogBytes)
		{
			throw new JsonException("model catalog size is out of range");
		}

		var providers = JsonSerializer.Deserialize(catalog, ModelsJsonContext.Default.DictionaryStringListModel)
			?? throw new JsonException("model catalog was empty");

		var result = new Dictionary<string, List<Model>>(providers.Count, StringComparer.Ordinal);
		var total = 0;
		foreach (var (providerId, models) in providers)
		{
			if (string.IsNullOrWhiteSpace(providerId))
			{
				throw new JsonException("model catalog has an empty provider id");
			}

			var list = new List<Model>(models.Count);
			foreach (var model in models)
			{
				if (string.IsNullOrWhiteSpace(model.Id))
				{
					throw new JsonException($"model catalog entry of '{providerId}' has an empty id");
				}

				list.Add(model.Provider == providerId ? model : model with { Provider = providerId });
				total++;
			}

			if (list.Count > 0)
			{
				result[providerId] = list;
			}
		}

		return total == 0 ? throw new JsonException("model catalog has no models") : result;
	}

	/// 归一化摘要：provider 与模型 ID 排序，同 ID 取最后一条（与注册表后写覆盖先写一致）。
	internal static string CanonicalDigest(IReadOnlyDictionary<string, List<Model>> catalog)
	{
		using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
		foreach (var provider in catalog.Keys.OrderBy(static key => key, StringComparer.Ordinal))
		{
			var models = catalog[provider];
			if (models.Count == 0)
			{
				continue;
			}

			var unique = new Dictionary<string, Model>(StringComparer.Ordinal);
			foreach (var model in models)
			{
				unique[model.Id] = model;
			}

			hash.AppendData(Encoding.UTF8.GetBytes(provider));
			hash.AppendData([0]);
			foreach (var id in unique.Keys.OrderBy(static key => key, StringComparer.Ordinal))
			{
				hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(unique[id], ModelsJsonContext.Default.Model));
				hash.AppendData([0]);
			}
		}

		return Convert.ToHexStringLower(hash.GetHashAndReset());
	}

	internal static CatalogSummary Summarize(IReadOnlyDictionary<string, List<Model>> catalog) =>
		new(catalog.Count, catalog.Sum(static pair => pair.Value.Count));

	internal static string Digest(ReadOnlySpan<byte> bytes) =>
		Convert.ToHexStringLower(SHA256.HashData(bytes));

	private static EmbeddedCatalog ReadEmbedded()
	{
		using var source = ModelRegistry.OpenEmbeddedCatalog();
		using var buffer = new MemoryStream();
		source.CopyTo(buffer);
		var bytes = buffer.ToArray();
		var models = ParseCatalog(bytes);
		var summary = Summarize(models);
		return new EmbeddedCatalog(Digest(bytes), CanonicalDigest(models), summary.ProviderCount, summary.EntryCount);
	}
}

internal sealed record CatalogSummary(int ProviderCount, int EntryCount);

internal sealed record EmbeddedCatalog(string Digest, string ContentDigest, int ProviderCount, int EntryCount);

internal sealed record OverlaySnapshot(
	byte[] Catalog,
	ModelCatalogMeta Meta,
	IReadOnlyDictionary<string, List<Model>> Models,
	string ContentDigest,
	ModelCatalogInfo Info);
