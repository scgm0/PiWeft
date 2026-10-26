using System.Text.Json;
using System.Text.Json.Nodes;
using PiWeft.CodingAgent.Session;

namespace PiWeft.Desktop;

/// <summary>侧栏一行的持久化形态。只放文件名/路径/预览这类已经在内存里缓存过的元数据。</summary>
internal sealed record SessionSummaryStamp
{
	public required long Length { get; init; }

	public required long LastWriteTicks { get; init; }

	/// <summary>文件首行的指纹，用于「变长时续扫」前的边界校验。</summary>
	public required long HeadHash { get; init; }

	/// <summary>上次扫到的字节位置。</summary>
	public required long Offset { get; init; }

	public required string Id { get; init; }

	public required string Name { get; init; }

	public required string Cwd { get; init; }

	public required int MessageCount { get; init; }

	public required string Preview { get; init; }
}

/// <summary>
/// 侧栏摘要的磁盘缓存（<c>&lt;agentDir&gt;/session-summaries.json</c>）。
/// <para>
/// 为什么需要它：<see cref="SessionCatalog"/> 的进程内摘要缓存只在一次运行里有效，
/// 而侧栏列表要显示每个会话的**消息数**与首条预览 —— 这两样都得读到文件尾部才能得到。
/// 实测 92 个会话 / 331 MiB 的冷扫描是 **1100 ms**（每次启动都要付），
/// 而这些文件绝大多数自上次启动后根本没变。把摘要按（长度 + mtime + 首行指纹）持久化之后，
/// 启动只需要 stat 一遍文件。
/// </para>
/// <para>
/// 只缓存「显示用的元数据」：文件名、名称、工作目录、消息数、首条消息的短预览（截断 200 字符）。
/// 失效判据与内存缓存完全一致（长度 / mtime / 首行指纹），所以外部改写、截断、换行符变化都会重扫。
/// 文件不存在、缓存损坏、版本不符 → 当作没有缓存，最坏退化成一次全扫。
/// </para>
/// </summary>
internal static class SessionSummaryStore
{
	/// <summary>缓存格式版本；改结构必须 +1。</summary>
	public const int Version = 1;

	/// <summary>预览截断长度：摘要只用来给侧栏显示一行。</summary>
	private const int PreviewLimit = 200;

	/// <summary>缓存条目上限（与内存缓存同一档）。</summary>
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

	/// <summary>写回缓存；同目录临时文件 + rename，失败静默（缓存丢了只影响启动速度）。</summary>
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

	/// <summary>
	/// 读整数。必须是 <c>long</c>：mtime 的 ticks 与首行指纹都是 64 位，
	/// 走 <c>double</c> 会在 2^53 以上丢精度 —— 那样读回来的 ticks 永远对不上，缓存等于没命中。
	/// </summary>
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
