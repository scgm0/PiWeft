using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiWeft.Ai;
using PiWeft.CodingAgent.Session;

namespace PiWeft.CodingAgent.Import;

/// <summary>
/// 一个可导入的外部会话（只读摘要）。<see cref="Source"/> + <see cref="SessionId"/> 唯一确定一条，
/// 列表与命令行都用它们挑目标 —— 传路径也行，但用户手上通常只有「第 3 条 Claude 会话」这种信息。
/// </summary>
public sealed record ImportedSession
{
	/// <summary>来源标识（<c>claude</c> / <c>codex</c>）。</summary>
	public required string Source { get; init; }

	public required string SourceLabel { get; init; }

	public required string SessionId { get; init; }

	/// <summary>源 transcript 的绝对路径。</summary>
	public required string SourcePath { get; init; }

	/// <summary>源会话的工作目录；决定导入后的会话落在哪个项目下。</summary>
	public required string Cwd { get; init; }

	/// <summary>标题：优先源会话自己的标题，其次首条用户消息的第一行。</summary>
	public string Title { get; init; } = string.Empty;

	public DateTimeOffset Created { get; init; }

	public DateTimeOffset Modified { get; init; }

	public long Size { get; init; }
}

/// <summary>一个来源的可用性概览（<c>pi import</c> 的列表头）。</summary>
public sealed record SessionImportSource(string Key, string Label, string Root, bool Exists, int Count);

/// <summary>导入结果。<see cref="SessionFile"/> 为空表示没落盘。</summary>
public sealed record SessionImportResult(
	string? SessionFile,
	string SessionId,
	int MessageCount,
	string? Error)
{
	public bool Ok => Error is null && SessionFile is not null;

	public static SessionImportResult Failure(string error) => new(null, string.Empty, 0, error);
}

/// <summary>
/// 一种外部会话格式。<see cref="Convert"/> 只产出 pi v3 条目，落盘由 <see cref="SessionImportService"/> 统一做。
/// </summary>
public interface IExternalSessionImporter
{
	/// <summary>来源标识，决定 <c>pi import &lt;source&gt;</c> 的名字。</summary>
	string Key { get; }

	string Label { get; }

	/// <summary>源会话库根目录（<paramref name="home"/> 是可注入的家目录，便于测试）。</summary>
	string ResolveRoot(string home);

	/// <summary>枚举根目录下的会话文件。要有界：有的工具会把几千个文件平铺在一个目录里。</summary>
	IReadOnlyList<string> Enumerate(string root);

	/// <summary>只读文件头部得到摘要；头部取不到元数据时返回 <see langword="null"/>（不进列表）。</summary>
	ImportedSession? Describe(string path);

	/// <summary>把源文件转成 pi v3 条目（不含会话头）。</summary>
	IReadOnlyList<SessionEntry> Convert(ImportedSession session);
}

/// <summary>
/// 外部会话导入：发现 → 转换 → 写成 pi v3 JSONL。
/// <para>
/// 落盘是原子的（同目录临时文件 + rename），并且**文件名由源会话的创建时间与 id 决定**，
/// 所以重复导入同一条会话会覆盖上一次的产物而不是堆出副本 —— 源会话后来续聊了，重导一次就同步。
/// </para>
/// <para>
/// 导入产物里会多一条 <c>custom</c> 条目记下来源（<c>piweft-import</c>），便于事后追溯是哪台工具的哪个文件，
/// 也方便将来换转换器时判断哪些产物该重导。它不参与模型上下文。
/// </para>
/// </summary>
public static class SessionImportService
{
	/// <summary>导入标记的 customType。</summary>
	public const string MarkerType = "piweft-import";

	/// <summary>转换器版本；产物有效性受转换逻辑影响时 +1，便于识别旧产物。</summary>
	public const int ConverterVersion = 1;

	/// <summary>摘要阶段只读这么多：源 transcript 动辄几十 MB，整读会把内存吃穿。</summary>
	private const int HeadBytes = 256 * 1024;

	/// <summary>单次列举的上限，防止一个平铺目录把界面/打印刷爆。</summary>
	public const int MaxSessions = 500;

	private static readonly IExternalSessionImporter[] AllImporters =
	[
		new ClaudeCodeImporter(),
		new CodexCliImporter(),
	];

	public static IReadOnlyList<IExternalSessionImporter> Importers => AllImporters;

	public static IExternalSessionImporter? Find(string key) =>
		AllImporters.FirstOrDefault(importer => string.Equals(importer.Key, key, StringComparison.OrdinalIgnoreCase));

	public static IReadOnlyList<SessionImportSource> DiscoverSources(string? home = null)
	{
		var effectiveHome = home ?? ToolPathsHome();
		var sources = new List<SessionImportSource>(AllImporters.Length);
		foreach (var importer in AllImporters)
		{
			var root = importer.ResolveRoot(effectiveHome);
			var exists = Directory.Exists(root);
			sources.Add(new SessionImportSource(
				importer.Key,
				importer.Label,
				root,
				exists,
				exists ? importer.Enumerate(root).Count : 0));
		}

		return sources;
	}

	/// <summary>
	/// 列出可导入会话，最近的在前。<paramref name="projectPath"/> 非空时只留工作目录在该目录下的会话
	/// （与 PiDeck 的「按项目导入」一致：用户在一次导入里只关心当前项目的历史）。
	/// </summary>
	public static IReadOnlyList<ImportedSession> Discover(
		string sourceKey,
		string? projectPath = null,
		string? home = null,
		IExternalSessionImporter? importerOverride = null)
	{
		var importer = importerOverride ?? Find(sourceKey) ?? throw new ArgumentException($"未知来源：{sourceKey}", nameof(sourceKey));
		var root = importer.ResolveRoot(home ?? ToolPathsHome());
		if (!Directory.Exists(root))
		{
			return [];
		}

		var sessions = new List<ImportedSession>();
		foreach (var path in importer.Enumerate(root))
		{
			ImportedSession? session;
			try
			{
				session = importer.Describe(path);
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
			{
				// 单个坏文件不该让整次列举失败：跳过，其余照常列出。
				continue;
			}

			if (session is null)
			{
				continue;
			}

			if (projectPath is { Length: > 0 } && !SameDirectory(session.Cwd, projectPath))
			{
				continue;
			}

			sessions.Add(session);
		}

		return [.. sessions.OrderByDescending(static session => session.Modified).Take(MaxSessions)];
	}

	/// <summary>全部来源的会话，最近的在前。</summary>
	public static IReadOnlyList<ImportedSession> DiscoverAll(string? projectPath = null, string? home = null)
	{
		var all = new List<ImportedSession>();
		foreach (var importer in AllImporters)
		{
			all.AddRange(Discover(importer.Key, projectPath, home, importer));
		}

		return [.. all.OrderByDescending(static session => session.Modified)];
	}

	/// <summary>
	/// 导入一条会话。返回的 <see cref="SessionImportResult.SessionFile"/> 是产物路径。
	/// <paramref name="cwdOverride"/> 用于源会话记录的工作目录已经不存在时改投一个可用目录。
	/// </summary>
	public static SessionImportResult Import(
		ImportedSession session,
		string? sessionDir = null,
		string? cwdOverride = null)
	{
		var importer = Find(session.Source);
		if (importer is null)
		{
			return SessionImportResult.Failure($"未知来源：{session.Source}");
		}

		var cwd = cwdOverride is { Length: > 0 } ? Path.GetFullPath(cwdOverride) : session.Cwd;
		if (cwd.Length == 0)
		{
			cwd = Directory.GetCurrentDirectory();
		}

		IReadOnlyList<SessionEntry> entries;
		try
		{
			entries = importer.Convert(session);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
		{
			return SessionImportResult.Failure($"读取源会话失败：{exception.Message}");
		}

		if (entries.Count == 0)
		{
			return SessionImportResult.Failure("源会话里没有可导入的消息。");
		}

		var id = SessionPaths.NewSessionId();
		var directory = sessionDir ?? SessionPaths.GetDefaultSessionDir(cwd);
		var created = session.Created == default ? DateTimeOffset.UtcNow : session.Created;
		var fileName = SessionPaths.SessionFileName(SessionPaths.ToIso8601(created), id);
		var path = Path.Combine(directory, fileName);

		var header = new SessionHeader
		{
			Version = SessionFormat.CurrentVersion,
			Id = id,
			Timestamp = SessionPaths.ToIso8601(created),
			Cwd = cwd,
		};

		try
		{
			Directory.CreateDirectory(directory);
			WriteAtomically(path, header, entries, session);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return SessionImportResult.Failure($"写入会话失败：{exception.Message}");
		}

		return new SessionImportResult(path, id, entries.Count, null);
	}

	private static void WriteAtomically(
		string path,
		SessionHeader header,
		IReadOnlyList<SessionEntry> entries,
		ImportedSession session)
	{
		var temporary = path + ".importing";
		using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
		{
			WriteLine(stream, writer => SessionJson.WriteHeader(writer, header));
			foreach (var entry in entries)
			{
				WriteLine(stream, writer => SessionJson.WriteEntry(writer, entry));
			}

			// 标记条目挂在最后一个真实条目之后：它必须留在分支链上，
			// 否则文件最后一行成为叶子、父链断在这里，打开后整个对话都投影不出来。
			WriteLine(stream, writer => SessionJson.WriteEntry(writer, MarkerEntry(session, entries[^1].Id)));
		}

		File.Move(temporary, path, overwrite: true);
	}

	private static CustomEntry MarkerEntry(ImportedSession session, string parentId)
	{
		var data = new JsonObject
		{
			["source"] = session.Source,
			["sourcePath"] = session.SourcePath,
			["sourceSessionId"] = session.SessionId,
			["converterVersion"] = ConverterVersion,
		};

		return new CustomEntry
		{
			Id = new ImportIds().Next(),
			ParentId = parentId,
			Timestamp = SessionPaths.ToIso8601(DateTimeOffset.UtcNow),
			CustomType = MarkerType,
			Data = data,
		};
	}

	/// <summary>一行 JSONL：直接写字节，不经过中间字符串。</summary>
	private static void WriteLine(Stream stream, Action<System.Text.Json.Utf8JsonWriter> write)
	{
		var buffer = new System.Buffers.ArrayBufferWriter<byte>(1024);
		using (var writer = new System.Text.Json.Utf8JsonWriter(buffer))
		{
			write(writer);
			writer.Flush();
		}

		stream.Write(buffer.WrittenSpan);
		stream.Write("\n"u8);
	}

	/// <summary>同目录判断：Windows/macOS 大小写不敏感，Linux 敏感。</summary>
	public static bool SameDirectory(string left, string right)
	{
		if (left.Length == 0 || right.Length == 0)
		{
			return false;
		}

		try
		{
			var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
				? StringComparison.OrdinalIgnoreCase
				: StringComparison.Ordinal;

			var a = Path.TrimEndingDirectorySeparator(Path.GetFullPath(left));
			var b = Path.TrimEndingDirectorySeparator(Path.GetFullPath(right));
			return string.Equals(a, b, comparison);
		}
		catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
		{
			return false;
		}
	}

	internal static string ToolPathsHome() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

	/// <summary>读取文件头部若干字节；按最后一个换行截断，避免把半行当完整行解析。</summary>
	internal static IReadOnlyList<JsonObject> ReadHeadObjects(string path, int maxBytes = HeadBytes)
	{
		using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
		var length = (int)Math.Min(stream.Length, maxBytes);
		if (length == 0)
		{
			return [];
		}

		var buffer = new byte[length];
		var read = 0;
		while (read < length)
		{
			var chunk = stream.Read(buffer, read, length - read);
			if (chunk == 0)
			{
				break;
			}

			read += chunk;
		}

		var text = System.Text.Encoding.UTF8.GetString(buffer, 0, read);
		// 头部可能切在多字节字符或行中间：丢掉最后一段不完整的行。
		if (read == maxBytes)
		{
			var lastBreak = text.LastIndexOf('\n');
			text = lastBreak >= 0 ? text[..lastBreak] : text;
		}

		return ParseLines(text);
	}

	/// <summary>整文件逐行解析（导入阶段用：源文件大小已知，逐行流式读不整块进内存）。</summary>
	internal static IEnumerable<JsonObject> ReadObjects(string path)
	{
		using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
		using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
		while (reader.ReadLine() is { } line)
		{
			if (line.Trim().Length == 0)
			{
				continue;
			}

			JsonObject? node;
			try
			{
				node = JsonNode.Parse(line) as JsonObject;
			}
			catch (JsonException)
			{
				// 坏行跳过：外部工具写到一半崩掉时很常见，不能让整条会话报废。
				continue;
			}

			if (node is not null)
			{
				yield return node;
			}
		}
	}

	internal static IReadOnlyList<JsonObject> ParseLines(string text)
	{
		var objects = new List<JsonObject>();
		foreach (var raw in text.Split('\n'))
		{
			var line = raw.TrimEnd('\r');
			if (line.Trim().Length == 0)
			{
				continue;
			}

			try
			{
				if (JsonNode.Parse(line) is JsonObject node)
				{
					objects.Add(node);
				}
			}
			catch (JsonException)
			{
				// 头部最后一行可能是半行（见 ReadHeadObjects），跳过即可。
			}
		}

		return objects;
	}

	/// <summary>ISO 8601 / epoch 毫秒两路都认；解析不出来返回 <see langword="null"/>。</summary>
	internal static DateTimeOffset? ParseTimestamp(JsonNode? node)
	{
		if (node is not JsonValue value)
		{
			return null;
		}

		if (value.TryGetValue<string>(out var text))
		{
			return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
				? parsed
				: null;
		}

		if (value.TryGetValue<double>(out var number) && number > 0)
		{
			// 秒级与毫秒级都出现过；用阈值区分（1e11 ms ≈ 1973 年，安全下界）。
			return number > 1e11
				? DateTimeOffset.FromUnixTimeMilliseconds((long)number)
				: DateTimeOffset.FromUnixTimeSeconds((long)number);
		}

		return null;
	}

	/// <summary>取字符串字段；非字符串或缺失返回 <see langword="null"/>。</summary>
	internal static string? Text(JsonNode? node, params string[] path)
	{
		var current = node;
		foreach (var name in path)
		{
			if (current is not JsonObject obj || !obj.TryGetPropertyValue(name, out current))
			{
				return null;
			}
		}

		return current is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0 ? text : null;
	}

	/// <summary>
	/// 布尔字段的真值判断。<c>isSidechain</c>/<c>isMeta</c> 这类标记是 JSON 布尔，
	/// 用 <see cref="Text"/> 读会得到 <see langword="null"/> 而不是 <c>false</c> —— 那正好是
	/// 「该跳过却没跳过」的来源。
	/// </summary>
	internal static bool IsTrue(JsonNode? node) =>
		node is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;

	/// <summary>标题取首行并截断：会话列表按单行展示，长标题会被裁掉。</summary>
	internal static string TitleFromText(string? text, int maxLength = 80)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			return string.Empty;
		}

		var line = text.Split('\n', 2)[0].Trim();
		return line.Length <= maxLength ? line : line[..maxLength] + "…";
	}
}

/// <summary>
/// 8 位十六进制 id 生成器，与 pi 自己的条目 id 同形态。
/// 每次导入各建一个实例：只要在**当前文件内**唯一即可，跨文件不需要协调。
/// </summary>
internal sealed class ImportIds
{
	private readonly HashSet<string> _used = new(StringComparer.Ordinal);

	public string Next()
	{
		for (var attempt = 0; attempt < 100; attempt++)
		{
			var id = Guid.NewGuid().ToString("N")[..8];
			if (_used.Add(id))
			{
				return id;
			}
		}

		// 极端情况下退回序号式 id，保证唯一而不是抛异常。
		return $"z{_used.Count:x7}";
	}
}
