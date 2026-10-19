using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiWeft.Ai;
using PiWeft.CodingAgent.Session;
using PiWeft.Cli;

namespace PiWeft.Desktop;

/// <summary>侧栏中的一个会话文件。</summary>
public sealed record SessionListItem
{
	public required string Path { get; init; }

	public required string Id { get; init; }

	public string Name { get; init; } = string.Empty;

	public string Cwd { get; init; } = string.Empty;

	public DateTimeOffset Modified { get; init; }

	public int MessageCount { get; init; }

	/// <summary>第一条用户消息的摘要，用作没有命名时的标题。</summary>
	public string Preview { get; init; } = string.Empty;

	/// <summary>
	/// 列表里的标题：会话名优先，其次第一条用户消息，最后退回「新会话」。
	/// 文件名（时间戳 + id）对用户没有意义，不拿出来当标题。
	/// </summary>
	public string Display => Name.Length > 0
		? Name
		: Preview.Length > 0
			? Preview
			: Strings.UntitledSession;
}

/// <summary>
/// 列出并打开磁盘上的会话。文件格式与 pi CLI 共用，因此终端里开的会话会出现在这里，反之亦然。
/// </summary>
public sealed class SessionCatalog
{
	private readonly Dictionary<string, SummaryStamp> _summaries = new(StringComparer.Ordinal);
	private sealed record SummaryStamp(long Length, DateTime LastWrite, SessionListItem Item);

	/// <summary>agent 目录下的全部会话，最近的在前；可按工作目录过滤。</summary>
	public IReadOnlyList<SessionListItem> List(string? cwdFilter = null, int limit = 200) => ListCore(false, cwdFilter, limit);

	/// <summary>Reuse unchanged summaries and prune deleted paths; cached data never retains message bodies.</summary>
	private IReadOnlyList<SessionListItem> ListCore(bool archived, string? cwdFilter, int limit)
	{
		var root = Path.Combine(SessionPaths.GetAgentDir(), "sessions");
		if (!Directory.Exists(root)) { _summaries.Clear(); return []; }
		var paths = Directory.EnumerateFiles(root, "*" + SessionFormat.Extension, SearchOption.AllDirectories).ToArray();
		var alive = paths.ToHashSet(StringComparer.Ordinal);
		foreach (var path in _summaries.Keys.Where(path => !alive.Contains(path)).ToArray()) _summaries.Remove(path);
		var trash = $"{Path.DirectorySeparatorChar}.trash{Path.DirectorySeparatorChar}";
		var result = paths.Where(path => path.Contains(trash, StringComparison.Ordinal) == archived)
			.Select(ToItem).Where(item => cwdFilter is null || string.Equals(item.Cwd, cwdFilter, StringComparison.Ordinal))
			.OrderByDescending(static item => item.Modified).Take(limit).ToArray();
		// A machine may have thousands of old sessions; cap the metadata cache as well as the visible list.
		if (_summaries.Count > 4096)
			foreach (var path in _summaries.OrderByDescending(static entry => entry.Value.LastWrite).Skip(4096).Select(static entry => entry.Key).ToArray())
				_summaries.Remove(path);
		return result;
	}

	/// <summary>按关键字过滤会话：匹配名称、id、工作目录或文件名（不含 .jsonl 后缀，否则搜 "json" 会命中全部），忽略大小写。</summary>
	public static IReadOnlyList<SessionListItem> Search(IReadOnlyList<SessionListItem> sessions, string? query)
	{
		var trimmed = query?.Trim() ?? string.Empty;
		if (trimmed.Length == 0)
		{
			return sessions;
		}

		return
		[
			.. sessions.Where(item =>
				item.Preview.Contains(trimmed, StringComparison.OrdinalIgnoreCase) ||
				item.Name.Contains(trimmed, StringComparison.OrdinalIgnoreCase) ||
				item.Id.Contains(trimmed, StringComparison.OrdinalIgnoreCase) ||
				item.Cwd.Contains(trimmed, StringComparison.OrdinalIgnoreCase) ||
				Path.GetFileNameWithoutExtension(item.Path).Contains(trimmed, StringComparison.OrdinalIgnoreCase)),
		];
	}

	/// <summary>列出归档（<c>.trash</c>）里的会话。</summary>
	public IReadOnlyList<SessionListItem> ListDeleted(int limit = 200) => ListCore(true, null, limit);

	/// <summary>把归档里的会话移回它原来的目录。</summary>
	public string Restore(string trashPath)
	{
		var resolved = Path.GetFullPath(trashPath);
		var trashDirectory = Path.GetDirectoryName(resolved)
			?? throw new InvalidOperationException($"路径没有父目录：{trashPath}");

		var targetDirectory = Path.GetDirectoryName(trashDirectory)
			?? throw new InvalidOperationException($"归档目录没有父目录：{trashDirectory}");

		Directory.CreateDirectory(targetDirectory);
		var target = Path.Combine(targetDirectory, Path.GetFileName(resolved));
		File.Move(resolved, target, overwrite: true);
		return target;
	}

	/// <summary>清空归档，返回删除的会话数。调用方负责先确认。</summary>
	public int PurgeDeleted()
	{
		var deleted = ListDeleted(limit: int.MaxValue);
		var removed = 0;

		foreach (var item in deleted)
		{
			try
			{
				File.Delete(item.Path);
				removed++;
			}
			catch (IOException ex)
			{
				AppLog.Failure(AppLog.For<SessionCatalog>(), "Delete archived session", ex);
			}
		}

		return removed;
	}

	/// <summary>一条内容命中：命中的会话与所在行的摘要。</summary>
	public sealed record ContentMatch(SessionListItem Session, string Excerpt);

	/// <summary>
	/// 在会话正文里找关键字。只扫描最近的若干个文件、每个文件最多读 <paramref name="maxBytesPerFile"/>，
	/// 命中即停 —— 聊天记录里工具输出往往很长，全量读会拖慢输入。
	/// </summary>
	public static IReadOnlyList<ContentMatch> SearchContent(
		IReadOnlyList<SessionListItem> sessions,
		string? query,
		int maxFiles = 200,
		int maxBytesPerFile = 512 * 1024,
		CancellationToken cancellationToken = default)
	{
		var trimmed = query?.Trim() ?? string.Empty;
		if (trimmed.Length == 0)
		{
			return [];
		}

		var matches = new List<ContentMatch>();

		foreach (var session in sessions.Take(maxFiles))
		{
			cancellationToken.ThrowIfCancellationRequested();
			var excerpt = FindInFile(session.Path, trimmed, maxBytesPerFile, cancellationToken);
			if (excerpt is not null)
			{
				matches.Add(new ContentMatch(session, excerpt));
			}
		}

		return matches;
	}

	/// <summary>在一个会话文件里找到第一处命中，返回该行摘要。</summary>
	private static string? FindInFile(string path, string query, int maxBytes, CancellationToken cancellationToken)
	{
		if (maxBytes <= 0) return null;
		// Limit the actual bytes, including a single huge JSONL line. Reuse the read buffer between files.
		var limit = Math.Min(maxBytes, 512 * 1024);
		var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(limit);
		try
		{
			using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			var length = 0;
			while (length < limit)
			{
				cancellationToken.ThrowIfCancellationRequested();
				var count = file.Read(buffer, length, Math.Min(8192, limit - length));
				if (count == 0) break;
				length += count;
			}
			cancellationToken.ThrowIfCancellationRequested();
			var text = System.Text.Encoding.UTF8.GetString(buffer, 0, length);
			var hit = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
			if (hit < 0) return null;
			var start = hit == 0 ? 0 : text.LastIndexOf('\n', hit - 1) + 1;
			var end = text.IndexOf('\n', hit);
			return Summarize(text[start..(end < 0 ? text.Length : end)], query);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			AppLog.Failure(AppLog.For<SessionCatalog>(), "Search session content", ex);
			return null;
		}
		finally { System.Buffers.ArrayPool<byte>.Shared.Return(buffer); }
	}

	/// <summary>
	/// 把命中行压成一段以关键字为中心的摘要。会话文件是 JSONL，命中多半在消息文本里，
	/// 因此先试着只取 JSON 中的字符串内容，避免把字段名与转义噪音一起端给用户；
	/// 取不到（或那里面没有关键字）时退回整行摘要。
	/// </summary>
	internal static string Summarize(string line, string query, int radius = 60)
	{
		if (line.StartsWith('{') &&
			ExtractText(line) is { Length: > 0 } text &&
			text.Contains(query, StringComparison.OrdinalIgnoreCase))
		{
			return Center(text, query, radius);
		}

		return Center(line, query, radius);
	}

	/// <summary>围绕关键字截取一段，两端加省略号。</summary>
	internal static string Center(string text, string query, int radius = 60)
	{
		var index = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
		if (index < 0)
		{
			return text.Length <= radius * 2 ? text : string.Concat(text.AsSpan(0, radius * 2), "…");
		}

		var start = Math.Max(0, index - radius);
		var end = Math.Min(text.Length, index + query.Length + radius);

		return string.Concat(
			start > 0 ? "…" : string.Empty,
			text[start..end],
			end < text.Length ? "…" : string.Empty);
	}

	/// <summary>把一行 JSONL 里所有字符串值拼成可读文本，用于展示摘要。</summary>
	internal static string? ExtractText(string line)
	{
		try
		{
			var node = JsonNode.Parse(line);
			if (node is null)
			{
				return null;
			}

			var parts = new List<string>();
			CollectStrings(node, parts);

			return parts.Count == 0 ? null : string.Join(' ', parts);
		}
		catch (JsonException)
		{
			return null;
		}
	}

	private static void CollectStrings(JsonNode node, List<string> parts)
	{
		switch (node)
		{
			case JsonValue value when value.TryGetValue<string>(out var text):
				if (text.Length > 0)
				{
					parts.Add(text);
				}

				break;

			case JsonObject obj:
				foreach (var (_, child) in obj)
				{
					if (child is not null)
					{
						CollectStrings(child, parts);
					}
				}

				break;

			case JsonArray array:
				foreach (var child in array)
				{
					if (child is not null)
					{
						CollectStrings(child, parts);
					}
				}

				break;
		}
	}

	/// <summary>按文件路径打开会话，恢复其中的模型与思考级别。</summary>
	public SessionManager Open(string path) => SessionManager.Open(path);

	/// <summary>给会话设置显示名（追加一条 <c>session_info</c> 条目，历史不受影响）。</summary>
	public void Rename(string path, string? name) => SessionManager.Open(path).AppendSessionInfo(name);

	/// <summary>
	/// 把会话文件移到同目录下的 <c>.trash</c> 里。pi 的会话是只追加的，直接删除无法恢复，
	/// 因此这里保留一份，用户要彻底清理时自行删除该目录。
	/// </summary>
	public string Delete(string path)
	{
		var directory = Path.GetDirectoryName(Path.GetFullPath(path))
			?? throw new InvalidOperationException($"路径没有父目录：{path}");

		var trash = Path.Combine(directory, ".trash");
		Directory.CreateDirectory(trash);

		var target = Path.Combine(trash, Path.GetFileName(path));
		File.Move(path, target, overwrite: true);
		return target;
	}

	/// <summary>在某个工作目录下新建会话。</summary>
	public SessionManager Create(string cwd, string? sessionDir = null) =>
		SessionManager.Create(cwd, sessionDir);

	private SessionListItem ToItem(string path)
	{
		var info = new FileInfo(path);
		var modified = info.LastWriteTimeUtc;
		var length = info.Exists ? info.Length : 0;
		if (_summaries.TryGetValue(path, out var cached) && cached.LastWrite == modified && cached.Length == length) return cached.Item;
		var id = Path.GetFileNameWithoutExtension(path);

		// 文件名是 <时间戳>_<uuid>，取最后一个下划线之后的部分作为 id。
		var separator = id.LastIndexOf('_');
		if (separator >= 0 && separator + 1 < id.Length)
		{
			id = id[(separator + 1)..];
		}

		var scan = new CatalogScan();
		var readSucceeded = false;

		try
		{
			using var lines = new Utf8LineReader(path);
			while (lines.TryReadLine(out var line))
			{
				if (line.Length == 0 || line[0] != (byte)'{')
				{
					continue;
				}

				// Catalog rows need metadata, not typed tool output, image data or model usage.
				try
				{
					// Parse into a copy so a malformed trailing token cannot leave a partial line behind.
					var candidate = scan;
					ScanLine(line, ref candidate);
					scan = candidate;
				}
				catch (Exception ex) when (ex is JsonException or InvalidOperationException)
				{
					continue; // Ignore interrupted writes and malformed records without poisoning the rest of the file.
				}
			}
			readSucceeded = true;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			AppLog.Failure(AppLog.For<SessionCatalog>(), "Read session catalog", ex);
		}

		var item = new SessionListItem
		{
			Path = path,
			Id = id,
			Name = scan.Name ?? string.Empty,
			Cwd = scan.Cwd ?? string.Empty,
			Modified = modified,
			MessageCount = scan.Messages,
			Preview = scan.Preview ?? string.Empty,
		};
		info.Refresh();
		// A writer may have appended while we scanned. Cache only a complete, stable snapshot.
		if (readSucceeded && info.Exists && info.Length == length && info.LastWriteTimeUtc == modified)
			_summaries[path] = new SummaryStamp(length, modified, item);
		return item;
	}

	private ref struct CatalogScan
	{
		public string? Name;
		public string? Cwd;
		public string? Preview;
		public int Messages;
	}

	private static void ScanLine(ReadOnlySpan<byte> line, ref CatalogScan scan)
	{
		var reader = new Utf8JsonReader(line, isFinalBlock: true, state: default);
		if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
		{
			return;
		}

		string? type = null;
		string? cwd = null;
		string? name = null;
		var hasCwd = false;
		while (reader.Read())
		{
			if (reader.TokenType == JsonTokenType.EndObject)
			{
				break;
			}

			if (reader.TokenType != JsonTokenType.PropertyName)
			{
				reader.Skip();
				continue;
			}

			if (reader.ValueTextEquals("type"u8))
			{
				reader.Read();
				type = reader.GetString();
			}
			else if (reader.ValueTextEquals("cwd"u8))
			{
				reader.Read();
				hasCwd = true;
				cwd = reader.GetString();
			}
			else if (reader.ValueTextEquals("name"u8))
			{
				reader.Read();
				name = reader.GetString();
			}
			else if (reader.ValueTextEquals("message"u8))
			{
				reader.Read();
				ScanMessage(ref reader, ref scan);
			}
			else
			{
				reader.Skip();
			}
		}

		// JsonDocument.Parse rejected trailing garbage; keep the catalog's tolerance identical.
		while (reader.Read())
		{
			throw new JsonException("Unexpected trailing JSON value.");
		}

		if (type == "session" && hasCwd)
		{
			scan.Cwd = cwd ?? string.Empty;
		}
		else if (type == "session_info")
		{
			// A later session_info entry replaces the previous name, including a cleared one.
			scan.Name = name ?? string.Empty;
		}
	}

	private static void ScanMessage(ref Utf8JsonReader reader, ref CatalogScan scan)
	{
		if (reader.TokenType != JsonTokenType.StartObject)
		{
			reader.Skip();
			return;
		}

		string? role = null;
		string? contentText = null;
		while (reader.Read())
		{
			if (reader.TokenType == JsonTokenType.EndObject)
			{
				break;
			}

			if (reader.TokenType != JsonTokenType.PropertyName)
			{
				reader.Skip();
				continue;
			}

			if (reader.ValueTextEquals("role"u8))
			{
				reader.Read();
				role = reader.GetString();
			}
			else if (reader.ValueTextEquals("content"u8))
			{
				reader.Read();
				// pi writes role before content. When an external writer does not, read the
				// content anyway so the first user preview is still found.
				var mayNeedPreview = role == "user" || (role is null && string.IsNullOrEmpty(scan.Preview));
				contentText = ReadContentText(ref reader, mayNeedPreview);
			}
			else
			{
				reader.Skip();
			}
		}

		if (role is not ("user" or "assistant" or "toolResult"))
		{
			return;
		}

		scan.Messages++;
		if (role == "user" && string.IsNullOrEmpty(scan.Preview) && contentText is { Length: > 0 })
		{
			scan.Preview = Summarize(contentText);
		}
	}

	private static string? ReadContentText(ref Utf8JsonReader reader, bool read)
	{
		if (!read)
		{
			reader.Skip();
			return null;
		}

		if (reader.TokenType == JsonTokenType.String)
		{
			return reader.GetString();
		}

		if (reader.TokenType != JsonTokenType.StartArray)
		{
			reader.Skip();
			return null;
		}

		StringBuilder? builder = null;
		while (reader.Read())
		{
			if (reader.TokenType == JsonTokenType.EndArray)
			{
				break;
			}

			if (reader.TokenType != JsonTokenType.StartObject)
			{
				reader.Skip();
				continue;
			}

			string? kind = null;
			string? text = null;
			while (reader.Read())
			{
				if (reader.TokenType == JsonTokenType.EndObject)
				{
					break;
				}

				if (reader.TokenType != JsonTokenType.PropertyName)
				{
					reader.Skip();
					continue;
				}

				if (reader.ValueTextEquals("type"u8))
				{
					reader.Read();
					kind = reader.GetString();
				}
				else if (reader.ValueTextEquals("text"u8))
				{
					reader.Read();
					text = reader.GetString();
				}
				else
				{
					reader.Skip();
				}
			}

			if (kind == "text" && text is { Length: > 0 })
			{
				if (builder is null)
				{
					builder = new StringBuilder(text);
				}
				else
				{
					builder.Append('\n').Append(text);
				}
			}
		}

		return builder?.ToString();
	}

	/// <summary>窗口less 测试用：扫描一行会话摘要。</summary>
	internal static (string? Cwd, string? Name, string? Preview, int Messages) ScanLineForTest(string line) =>
		ScanLinesForTest([line]);

	/// <summary>窗口less 测试用：按文件顺序扫描多行会话摘要。</summary>
	internal static (string? Cwd, string? Name, string? Preview, int Messages) ScanLinesForTest(IEnumerable<string> lines)
	{
		var scan = new CatalogScan();
		foreach (var line in lines)
		{
			var candidate = scan;
			try
			{
				ScanLine(System.Text.Encoding.UTF8.GetBytes(line), ref candidate);
			}
			catch (JsonException)
			{
				continue;
			}

			scan = candidate;
		}

		return (scan.Cwd, scan.Name, scan.Preview, scan.Messages);
	}

	/// <summary>把首条用户消息压成一行摘要。</summary>
	private static string Summarize(string? text)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			return string.Empty;
		}

		var flat = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
		while (flat.Contains("  ", StringComparison.Ordinal))
		{
			flat = flat.Replace("  ", " ", StringComparison.Ordinal);
		}

		return flat.Length <= 42 ? flat : string.Concat(flat.AsSpan(0, 42), "…");
	}
}
