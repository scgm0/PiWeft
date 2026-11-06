using System.Text.Json;
using System.Text.Json.Nodes;
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
}

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
	public const int Version = 1;

	private const int PreviewLimit = 200;

	public const int MaxEntries = 4096;

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
			if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject root ||
				Number(root["version"]) != Version ||
				root["entries"] is not JsonObject items)
			{
				return entries;
			}

			foreach (var (key, node) in items)
			{
				if (node is not JsonObject item)
				{
					continue;
				}

				var length = Number(item["length"]);
				var lastWrite = Number(item["mtime"]);
				if (length is null || lastWrite is null)
				{
					continue;
				}

				entries[key] = new SessionSummaryStamp
				{
					Length = length.Value,
					LastWriteTicks = lastWrite.Value,
					HeadHash = Number(item["head"]) ?? 0,
					Offset = Number(item["offset"]) ?? 0,
					TailHash = Number(item["tail"]) ?? 0,
					Id = Text(item["id"]),
					Name = Text(item["name"]),
					Cwd = Text(item["cwd"]),
					MessageCount = (int)(Number(item["messages"]) ?? 0),
					Preview = Text(item["preview"]),
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
		var items = new JsonObject();
		var count = 0;

		foreach (var (key, stamp) in entries.OrderByDescending(static entry => entry.Value.LastWriteTicks))
		{
			if (count++ >= MaxEntries)
			{
				break;
			}

			items[key] = new JsonObject
			{
				["length"] = stamp.Length,
				["mtime"] = stamp.LastWriteTicks,
				["head"] = stamp.HeadHash,
				["offset"] = stamp.Offset,
				["tail"] = stamp.TailHash,
				["id"] = stamp.Id,
				["name"] = stamp.Name,
				["cwd"] = stamp.Cwd,
				["messages"] = stamp.MessageCount,
				["preview"] = stamp.Preview.Length > PreviewLimit ? stamp.Preview[..PreviewLimit] : stamp.Preview,
			};
		}

		var root = new JsonObject
		{
			["version"] = Version,
			["entries"] = items,
		};

		try
		{
			Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
			var temporary = path + ".tmp";
			File.WriteAllText(temporary, root.ToJsonString(new JsonSerializerOptions
			{
				// 预览里会有中文与路径，转义成 \uXXXX 的话文件没法排查。
				Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
			}));

			File.Move(temporary, path, overwrite: true);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			// 写不了就不写：下次启动多扫一遍而已。
		}
	}

	/// 读整数。必须是 <c>long</c>：mtime 的 ticks 与首行指纹都是 64 位，
	/// 走 <c>double</c> 会在 2^53 以上丢精度，读回来的 ticks 对不上，缓存等于没命中。
	private static long? Number(JsonNode? node)
	{
		if (node is not JsonValue value)
		{
			return null;
		}

		if (value.TryGetValue<long>(out var integer))
		{
			return integer;
		}

		return value.TryGetValue<double>(out var number) ? (long)number : null;
	}

	private static string Text(JsonNode? node) =>
		node is JsonValue value && value.TryGetValue<string>(out var text) ? text : string.Empty;
}
