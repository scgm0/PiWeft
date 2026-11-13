using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using PiWeft.CodingAgent.Session;

namespace PiWeft.Desktop;

internal sealed record SessionSummaryStamp
{
	public required long Length { get; init; }

	public required long LastWriteTicks { get; init; }

	public required long HeadHash { get; init; }

	public required long Offset { get; init; }

	/// 续扫点之前那一小段字节的指纹。0 表示没有（旧缓存）：这种情况不续扫，直接全量重扫。
	/// 会话被原地重写后偏移已经失效，宁可多读一遍也不能拿着错位置续读。
	public long TailHash { get; init; }

	public required string Id { get; init; }

	public required string Name { get; init; }

	public required string Cwd { get; init; }

	public required int MessageCount { get; init; }

	public required string Preview { get; init; }

	/// <summary>子会话的父会话 id；普通会话为空。</summary>
	public string ParentSession { get; init; } = string.Empty;

	/// <summary>派生来源；子代理为 <c>subagent</c>。</summary>
	public string Origin { get; init; } = string.Empty;
}

/// 缓存文件的结构。与 <see cref="SessionSummaryStamp"/> 分开是为了让磁盘键名保持稳定且短：
/// 这个文件是排查侧栏问题的第一现场，键名不该跟着内部类型改名。
internal sealed class SessionSummaryFile
{
	[JsonPropertyName("version")] public int Version { get; init; }

	[JsonPropertyName("entries")] public Dictionary<string, SessionSummaryEntry>? Entries { get; init; }
}

internal sealed class SessionSummaryEntry
{
	[JsonPropertyName("length")] public long Length { get; init; }

	[JsonPropertyName("mtime")] public long LastWriteTicks { get; init; }

	[JsonPropertyName("head")] public long HeadHash { get; init; }

	[JsonPropertyName("offset")] public long Offset { get; init; }

	[JsonPropertyName("tail")] public long TailHash { get; init; }

	[JsonPropertyName("id")] public string Id { get; init; } = string.Empty;

	[JsonPropertyName("name")] public string Name { get; init; } = string.Empty;

	[JsonPropertyName("cwd")] public string Cwd { get; init; } = string.Empty;

	[JsonPropertyName("messages")] public int MessageCount { get; init; }

	[JsonPropertyName("preview")] public string Preview { get; init; } = string.Empty;

	[JsonPropertyName("parentSession")] public string ParentSession { get; init; } = string.Empty;

	[JsonPropertyName("origin")] public string Origin { get; init; } = string.Empty;
}

/// 源生成的序列化元数据：摘要文件在启动时整份读取，`JsonNode` 那套树会给每个值都建一个节点，
/// 实测 141 条 83 KB 的缓存要分配 1 MB 上下。生成器按目标类型直接读，不再有中间树。
[JsonSerializable(typeof(SessionSummaryFile))]
internal sealed partial class SessionSummaryJsonContext : JsonSerializerContext;

/// 侧栏摘要的磁盘缓存（<c>&lt;agentDir&gt;/session-summaries.json</c>）。
/// 为什么需要它：<see cref="SessionCatalog"/> 的进程内摘要缓存只在一次运行里有效，
/// 而侧栏列表要显示每个会话的消息数与首条预览，两样都得读到文件尾部。
/// 实测 92 个会话 / 331 MiB 的冷扫描是 **1100 ms**（每次启动都要付），
/// 而这些文件绝大多数自上次启动后根本没变。把摘要按（长度 + mtime + 首行指纹）持久化之后，
/// 启动只需要 stat 一遍文件。
/// 只缓存「显示用的元数据」：文件名、名称、工作目录、消息数、首条消息的短预览（截断 200 字符）。
/// 失效判据与内存缓存完全一致（长度 / mtime / 首行指纹），所以外部改写、截断、换行符变化都会重扫。
/// 文件不存在、缓存损坏、版本不符 → 当作没有缓存，最坏退化成一次全扫。
internal static class SessionSummaryStore
{
	/// 缓存结构变了就 +1：旧条目里缺的字段不会凭空补上，只能整份丢掉重扫。
	/// 4 → 5：补上 parentSession / origin，让子代理会话重启后仍能挂回父会话。
	public const int Version = 5;

	private const int PreviewLimit = 200;

	public const int MaxEntries = 4096;

	// 预览里会有中文与路径，转义成 \uXXXX 的话文件没法排查。
	// 编码器只能在运行时的选项里改；类型信息仍来自源生成上下文，不退回反射。
	private static readonly JsonSerializerOptions WriteOptions = new(SessionSummaryJsonContext.Default.Options)
	{
		Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
	};

	private static readonly JsonTypeInfo<SessionSummaryFile> FileTypeInfo =
		(JsonTypeInfo<SessionSummaryFile>)WriteOptions.GetTypeInfo(typeof(SessionSummaryFile));

	public static string Path(string? agentDir = null) =>
		System.IO.Path.Combine(agentDir ?? SessionPaths.GetAgentDir(), "session-summaries.json");

	public static Dictionary<string, SessionSummaryStamp> Load(string? agentDir = null)
	{
		var path = Path(agentDir);
		var entries = new Dictionary<string, SessionSummaryStamp>(StringComparer.Ordinal);
		if (!File.Exists(path))
		{
			return entries;
		}

		try
		{
			// 按字节读：8 万行的缓存文件不必先解成 UTF-16 字符串再解析。
			var file = JsonSerializer.Deserialize(
				File.ReadAllBytes(path), SessionSummaryJsonContext.Default.SessionSummaryFile);
			if (file is null || file.Version != Version || file.Entries is null)
			{
				return entries;
			}

			foreach (var (key, item) in file.Entries)
			{
				if (item is null)
				{
					continue;
				}

				entries[key] = new SessionSummaryStamp
				{
					Length = item.Length,
					LastWriteTicks = item.LastWriteTicks,
					HeadHash = item.HeadHash,
					Offset = item.Offset,
					TailHash = item.TailHash,
					Id = item.Id,
					Name = item.Name,
					Cwd = item.Cwd,
					MessageCount = item.MessageCount,
					Preview = item.Preview,
					ParentSession = item.ParentSession,
					Origin = item.Origin,
				};
			}

			return entries;
		}
		catch (Exception exception) when (exception
			is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException)
		{
			// 缓存坏了就当没有：不能因为一个缓存文件让侧栏起不来。
			return entries;
		}
	}

	public static void Save(IReadOnlyDictionary<string, SessionSummaryStamp> entries, string? agentDir = null)
	{
		var path = Path(agentDir);
		var items = new Dictionary<string, SessionSummaryEntry>(
			Math.Min(entries.Count, MaxEntries), StringComparer.Ordinal);
		var count = 0;

		foreach (var (key, stamp) in entries.OrderByDescending(static entry => entry.Value.LastWriteTicks))
		{
			if (count++ >= MaxEntries)
			{
				break;
			}

			items[key] = new SessionSummaryEntry
			{
				Length = stamp.Length,
				LastWriteTicks = stamp.LastWriteTicks,
				HeadHash = stamp.HeadHash,
				Offset = stamp.Offset,
				TailHash = stamp.TailHash,
				Id = stamp.Id,
				Name = stamp.Name,
				Cwd = stamp.Cwd,
				MessageCount = stamp.MessageCount,
				Preview = ClipPreview(stamp.Preview),
				ParentSession = stamp.ParentSession,
				Origin = stamp.Origin,
			};
		}

		var root = new SessionSummaryFile { Version = Version, Entries = items };

		try
		{
			var directory = System.IO.Path.GetDirectoryName(path)!;
			// 目录不在了就不写：后台落盘可能比环境先消失（测试删临时目录、agent 目录被换掉），
			// 这时写回去等于把一个已删的目录重新建出来。
			if (!Directory.Exists(directory))
			{
				return;
			}

			Directory.CreateDirectory(directory);
			var temporary = path + ".tmp";
			File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(root, FileTypeInfo));

			File.Move(temporary, path, overwrite: true);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			// 写不了就不写：下次启动多扫一遍而已。
		}
	}

	/// 预览截到 200 字；切点落在代理对中间时退回一格，避免半个 emoji。
	private static string ClipPreview(string preview)
	{
		if (preview.Length <= PreviewLimit)
		{
			return preview;
		}

		var length = PreviewLimit;
		if (char.IsHighSurrogate(preview[length - 1])) length--;
		return preview[..length];
	}
}
