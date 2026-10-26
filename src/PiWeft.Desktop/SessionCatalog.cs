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
	// 诊断计数：一次列表构建里真正重扫了多少文件、花了多少时间（侧栏慢通常是重扫整个 JSONL）。
	// 按文件并行后这些计数会被多个线程更新，因此走 Interlocked。
	private long _scanTicks;
	private long _scanCount;
	private long _scanBytes;
	// 摘要缓存被并行扫描读写；扫描本身在锁外，只有查表与写回加锁。
	private readonly Lock _summaryGate = new();
	/// <summary>
	/// 侧栏一行的缓存。会话文件是只追加的，因此除了长度/时间戳之外还记住
	/// 上次扫到的行边界与首行指纹：文件变长时只扫新增的字节，不重读整份几十 MB。
	/// </summary>
	private sealed record SummaryStamp(long Length, DateTime LastWrite, SessionListItem Item, long Offset, long HeadHash);

	/// <summary>磁盘摘要是否已装载：第一次构建列表时才读，避免构造期做 IO。</summary>
	private bool _persistedLoaded;

	/// <summary>本次构建有没有产生新摘要（需要写回磁盘）。</summary>
	private bool _summariesDirty;

	/// <summary>把上一轮运行留下的摘要装进内存缓存。文件已变的情况下由既有的长度/mtime/指纹判据自然失效。</summary>
	private void EnsurePersistedLoaded()
	{
		lock (_summaryGate)
		{
			if (_persistedLoaded) return;
			_persistedLoaded = true;

			foreach (var (path, stamp) in SessionSummaryStore.Load())
			{
				// 内存里已经有更新的就以内存为准（同一次运行里刚扫过）。
				if (_summaries.ContainsKey(path)) continue;
				_summaries[path] = new SummaryStamp(
					stamp.Length,
					new DateTime(stamp.LastWriteTicks, DateTimeKind.Utc),
					new SessionListItem
					{
						Path = path,
						Id = stamp.Id,
						Name = stamp.Name,
						Cwd = stamp.Cwd,
						// 与扫描路径一致：ListItem.Modified 一律是 UTC（info.LastWriteTimeUtc）。
						// 这里若转本地时间，会和扫描出来的行混着排序；而且 ToLocalTime 逐条很贵
						// （实测 92 条要 133 ms，正好把缓存省下的时间还回去）。
						Modified = new DateTime(stamp.LastWriteTicks, DateTimeKind.Utc),
						MessageCount = stamp.MessageCount,
						Preview = stamp.Preview,
					},
					stamp.Offset,
					stamp.HeadHash);
			}
		}
	}

	/// <summary>把内存摘要写回磁盘（只写仍然存在的文件）。</summary>
	private void PersistSummaries()
	{
		Dictionary<string, SessionSummaryStamp> snapshot;
		lock (_summaryGate)
		{
			if (!_summariesDirty) return;
			_summariesDirty = false;
			snapshot = _summaries.ToDictionary(
				static entry => entry.Key,
				static entry => new SessionSummaryStamp
				{
					Length = entry.Value.Length,
					LastWriteTicks = entry.Value.LastWrite.Ticks,
					HeadHash = entry.Value.HeadHash,
					Offset = entry.Value.Offset,
					Id = entry.Value.Item.Id,
					Name = entry.Value.Item.Name,
					Cwd = entry.Value.Item.Cwd,
					MessageCount = entry.Value.Item.MessageCount,
					Preview = entry.Value.Item.Preview,
				},
				StringComparer.Ordinal);
		}

		SessionSummaryStore.Save(snapshot);
	}

	/// <summary>agent 目录下的全部会话，最近的在前；可按工作目录过滤。</summary>
	public IReadOnlyList<SessionListItem> List(string? cwdFilter = null, int limit = 200) => ListCore(false, cwdFilter, limit);

	/// <summary>Reuse unchanged summaries and prune deleted paths; cached data never retains message bodies.</summary>
	private IReadOnlyList<SessionListItem> ListCore(bool archived, string? cwdFilter, int limit)
	{
		var watch = Diagnostics.Enabled ? Stopwatch.StartNew() : null;
		Interlocked.Exchange(ref _scanTicks, 0);
		Interlocked.Exchange(ref _scanCount, 0);
		Interlocked.Exchange(ref _scanBytes, 0);
		EnsurePersistedLoaded();

		var root = Path.Combine(SessionPaths.GetAgentDir(), "sessions");
		if (!Directory.Exists(root))
		{
			lock (_summaryGate)
			{
				_summaries.Clear();
				_summariesDirty = true;
			}

			PersistSummaries();
			return [];
		}

		var paths = Directory.EnumerateFiles(root, "*" + SessionFormat.Extension, SearchOption.AllDirectories).ToArray();
		var alive = paths.ToHashSet(StringComparer.Ordinal);
		lock (_summaryGate)
		{
			foreach (var path in _summaries.Keys.Where(path => !alive.Contains(path)).ToArray())
			{
				_summaries.Remove(path);
				_summariesDirty = true;
			}
		}

		var trash = $"{Path.DirectorySeparatorChar}.trash{Path.DirectorySeparatorChar}";
		var candidates = paths.Where(path => path.Contains(trash, StringComparison.Ordinal) == archived).ToArray();

		// 只扫「会显示出来的那些」：侧栏按修改时间倒序取前 limit 条，
		// 所以先只 stat 排序、再对头部做摘要扫描。会话上千份的机器上，
		// 这一步把要读的字节数从「全部」降到「前 200 份」——不需要任何磁盘缓存。
		if (cwdFilter is null && candidates.Length > limit && limit > 0)
		{
			candidates = [.. candidates
				.Select(static path => (Path: path, Write: LastWriteOf(path)))
				.OrderByDescending(static entry => entry.Write)
				.Take(limit)
				.Select(static entry => entry.Path)];
		}

		var items = new SessionListItem[candidates.Length];

		// 会话之间互不依赖：一份会话就是一个文件，摘要扫描按文件并行。
		// 这是启动时最大的一笔 IO+解析（实测 78 份 / 304 MB / 单线程约 900 ms），
		// 并行后随核数下降，而 UI 线程全程不参与。
		if (candidates.Length > 1 && ScanParallelism(candidates.Length) > 1)
		{
			Parallel.For(
				0,
				candidates.Length,
				new ParallelOptions { MaxDegreeOfParallelism = ScanParallelism(candidates.Length) },
				index => items[index] = ToItem(candidates[index]));
		}
		else
		{
			for (var index = 0; index < candidates.Length; index++) items[index] = ToItem(candidates[index]);
		}

		var result = items
			.Where(item => cwdFilter is null || string.Equals(item.Cwd, cwdFilter, StringComparison.Ordinal))
			.OrderByDescending(static item => item.Modified).Take(limit).ToArray();

		// A machine may have thousands of old sessions; cap the metadata cache as well as the visible list.
		lock (_summaryGate)
		{
			if (_summaries.Count > SessionSummaryStore.MaxEntries)
			{
				foreach (var path in _summaries.OrderByDescending(static entry => entry.Value.LastWrite)
					.Skip(SessionSummaryStore.MaxEntries).Select(static entry => entry.Key).ToArray())
				{
					_summaries.Remove(path);
				}

				_summariesDirty = true;
			}
		}

		PersistSummaries();

		var rescans = Interlocked.Read(ref _scanCount);
		if (watch is not null)
		{
			var scanned = Interlocked.Read(ref _scanBytes);
			Diagnostics.Record("sidebar.list", watch,
				$"files={paths.Length}, rows={result.Length}, rescans={rescans}, " +
				$"scanMs={Interlocked.Read(ref _scanTicks) / (double)Stopwatch.Frequency * 1000:0.0}, " +
				$"scanBytes={scanned}, workers={ScanParallelism(candidates.Length)}");
		}

		return result;
	}

	/// <summary>读文件的修改时间；读不到的（刚好被删）排到最后，不参与截断。</summary>
	private static DateTime LastWriteOf(string path)
	{
		try
		{
			return File.GetLastWriteTimeUtc(path);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			return DateTime.MinValue;
		}
	}

	/// <summary>
	/// 扫描并行度：文件数量够多才值得开线程（每份会话都要读全文，属 IO+CPU 混合）。
	/// 留一个核给 UI 线程，别让打开会话时的时间线布局抢不到 CPU。
	/// </summary>
	private static int ScanParallelism(int fileCount)
	{
		if (fileCount <= 1) return 1;
		var limit = Environment.ProcessorCount switch
		{
			<= 2 => 1,
			<= 4 => 2,
			<= 8 => 4,
			_ => 6,
		};

		return Math.Min(limit, fileCount);
	}

	/// <summary>只读首行算指纹：文件是追加式的，首行没变就说明前缀可以直接复用。</summary>
	private static bool HeadMatches(string path, long expected)
	{
		try
		{
			using var lines = new Utf8LineReader(path);
			return lines.TryReadLine(out var first) && Fnv1a(first) == expected;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			return false;
		}
	}

	private static long Fnv1a(ReadOnlySpan<byte> value)
	{
		var hash = 1469598103934665603L;
		foreach (var b in value)
		{
			hash = (hash ^ b) * 1099511628211L;
		}

		return hash;
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

	/// <summary>
	/// 最近修改的会话文件路径：只 stat 目录，不读任何内容。
	/// 启动时用它提前并行解析首个会话，而不必等整份侧栏摘要扫完。
	/// </summary>
	public string? MostRecentPath()
	{
		EnsurePersistedLoaded();

		var root = Path.Combine(SessionPaths.GetAgentDir(), "sessions");
		if (!Directory.Exists(root))
		{
			return null;
		}

		string? newest = null;
		DateTime newestWrite = default;
		try
		{
			foreach (var path in Directory.EnumerateFiles(root, "*" + SessionFormat.Extension, SearchOption.AllDirectories))
			{
				if (path.Contains($"{Path.DirectorySeparatorChar}.trash{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
				{
					continue;
				}

				try
				{
					var write = File.GetLastWriteTimeUtc(path);
					if (newest is null || write > newestWrite)
					{
						newest = path;
						newestWrite = write;
					}
				}
				catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
				{
				}
			}
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			AppLog.Failure(AppLog.For<SessionCatalog>(), "Scan newest session", ex);
		}

		return newest;
	}

	/// <summary>按文件路径打开会话，恢复其中的模型与思考级别。</summary>
	public SessionManager Open(string path, string? cwdFallback = null) => SessionManager.Open(path, cwdFallback: cwdFallback);

	/// <summary>
	/// 同上，但可汇报读取进度并可取消。长会话（几十 MB）要读上要近一秒，
	/// 桌面在后台线程调用它，同时让加载提示显示百分比。
	/// </summary>
	public SessionManager Open(
		string path,
		string? cwdFallback,
		IProgress<SessionReadProgress>? progress,
		CancellationToken cancellationToken) =>
		SessionManager.Open(path, null, null, cwdFallback, progress, cancellationToken);

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
		SummaryStamp? cached;
		lock (_summaryGate)
		{
			_summaries.TryGetValue(path, out cached);
		}

		if (cached is not null && cached.LastWrite == modified && cached.Length == length) return cached.Item;
		var id = Path.GetFileNameWithoutExtension(path);

		// 文件名是 <时间戳>_<uuid>，取最后一个下划线之后的部分作为 id。
		var separator = id.LastIndexOf('_');
		if (separator >= 0 && separator + 1 < id.Length)
		{
			id = id[(separator + 1)..];
		}

		// 缓存里那一行就是文件前缀的扫描结果：文件变长时把它当续扫起点。
		CatalogScan scan;
		var resumeFrom = 0L;
		var headHash = 0L;
		SummaryStamp? known;
		lock (_summaryGate)
		{
			_summaries.TryGetValue(path, out known);
		}

		if (known is not null && length > known.Length)
		{
			var previous = known.Item;
			scan = new CatalogScan
			{
				Name = previous.Name.Length == 0 ? null : previous.Name,
				Cwd = previous.Cwd.Length == 0 ? string.Empty : previous.Cwd,
				Preview = previous.Preview.Length == 0 ? null : previous.Preview,
				Messages = previous.MessageCount,
			};
			resumeFrom = known.Offset;
			headHash = known.HeadHash;
		}
		else
		{
			scan = new CatalogScan();
		}

		var readSucceeded = false;
		var watchedBytes = 0L;
		// 续扫的起点边界必须是本次扫描的局部值：按文件并行后，实例字段会被别的文件覆盖。
		var scannedTo = 0L;
		var watch = Diagnostics.Enabled ? Stopwatch.StartNew() : null;

		try
		{
			// 续扫前先验明正身：同名文件被整份替换过（长度恰好也变了）时不能拿旧前缀当起点。
			var start = resumeFrom;
			if (start > 0 && !HeadMatches(path, headHash))
			{
				start = 0;
				headHash = 0;
				scan = new CatalogScan();
			}

			using var lines = new Utf8LineReader(path, start);
			while (lines.TryReadLine(out var line))
			{
				if (start == 0)
				{
					// 首行（会话头部）的指纹用来判断能不能续扫。
					headHash = Fnv1a(line);
				}

				watchedBytes += line.Length + 1;
				if (line.Length == 0 || line[0] != (byte)'{')
				{
					continue;
				}

				// 目录行只需要标题、cwd、预览和条数：绝大多数行按字节确认类别就够了，
				// 为 47 MB 的会话逐行建 JsonNode 树纯属浪费（实测整份扫描的九成时间花在这里）。
				if (FastScanLine(line, ref scan))
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
			scannedTo = lines.BytesRead;
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
		if (watch is not null)
		{
			Interlocked.Add(ref _scanTicks, watch.ElapsedTicks);
			Interlocked.Increment(ref _scanCount);
			Interlocked.Add(ref _scanBytes, watchedBytes);
		}

		info.Refresh();
		// A writer may have appended while we scanned. Cache only a complete, stable snapshot.
		if (readSucceeded && headHash != 0 && info.Exists && info.Length == length && info.LastWriteTimeUtc == modified)
		{
			lock (_summaryGate)
			{
				_summaries[path] = new SummaryStamp(length, modified, item, scannedTo, headHash);
				_summariesDirty = true;
			}
		}

		return item;
	}

	private ref struct CatalogScan
	{
		public string? Name;
		public string? Cwd;
		public string? Preview;
		public int Messages;
	}

	/// <summary>
	/// 不用 JSON 解析判断一行对目录有没有影响。返回 false 表示必须走完整的 <see cref="ScanLine"/>
	/// （会话头部、session_info、需要预览的首条用户消息，以及任何形状不合常规的行）。
	/// 兼容两种真实写法：pi 的 <c>{"id":"…",…,"type":"message"…}</c> 与外部导出的
	/// <c>{"type": "message", …}</c>（键后带空格）。只读行首窗口里的头几个键，正文一概不碰；
	/// 尾部被截断（不以 <c>}</c> 结束）或键序异常的行退回完整解析，容错与原来一致。
	/// </summary>
	private static bool FastScanLine(ReadOnlySpan<byte> line, ref CatalogScan scan)
	{
		if (line.Length < 24 || line[^1] != (byte)'}')
		{
			return false;
		}

		var head = line[..Math.Min(line.Length, 160)];
		if (!StartsForKey(head, "id"u8) && !StartsForKey(head, "type"u8))
		{
			return false;
		}

		if (!TryReadStringField(head, "type"u8, out var type))
		{
			return false;
		}

		if (!type.SequenceEqual("message"u8))
		{
			// 头部、session_info 与其它条目交给完整解析：一份文件里它们只占极少数行。
			return false;
		}

		if (!TryReadStringField(head, "role"u8, out var role))
		{
			return false;
		}

		if (!role.SequenceEqual("user"u8) && !role.SequenceEqual("assistant"u8) && !role.SequenceEqual("toolResult"u8))
		{
			return false;
		}

		if (role.SequenceEqual("user"u8) && string.IsNullOrEmpty(scan.Preview))
		{
			// 预览要真读正文：第一条还没拿到预览的用户消息才走完整解析。
			return false;
		}

		scan.Messages++;
		return true;
	}

	/// <summary>行首是否是 <c>{"key"</c>（pi 与常见导出都把 id 或 type 放在第一个键）。</summary>
	private static bool StartsForKey(ReadOnlySpan<byte> head, ReadOnlySpan<byte> key)
	{
		if (head.Length < key.Length + 3 || head[0] != (byte)'{' || head[1] != (byte)'\'')
		{
			return false;
		}

		return head.Slice(2, key.Length).SequenceEqual(key) && head[2 + key.Length] == (byte)'\"';
	}

	/// <summary>
	/// 在行首窗口里读 <c>"key" : "value"</c>，容忍冒号前后的空白。
	/// 只扫到第一个 <c>"content"</c> 之前，避免把正文里的同名字段当头部键。
	/// </summary>
	private static bool TryReadStringField(ReadOnlySpan<byte> head, ReadOnlySpan<byte> key, out ReadOnlySpan<byte> value)
	{
		value = default;
		var limit = head.IndexOf("\"content\""u8);
		if (limit < 0) limit = head.Length;
		var at = head[..limit].IndexOf(key);
		if (at < 1 || head[at - 1] != (byte)'\"')
		{
			return false;
		}

		var i = at + key.Length;
		if (i >= limit || head[i] != (byte)'\"')
		{
			return false;
		}

		i++;
		while (i < limit && (head[i] == (byte)' ' || head[i] == (byte)'\t')) i++;
		if (i >= limit || head[i] != (byte)':')
		{
			return false;
		}

		i++;
		while (i < limit && (head[i] == (byte)' ' || head[i] == (byte)'\t')) i++;
		if (i >= limit || head[i] != (byte)'\"')
		{
			return false;
		}

		var start = i + 1;
		var end = head[start..limit].IndexOf((byte)'\"');
		if (end <= 0)
		{
			return false;
		}

		value = head.Slice(start, end);
		return true;
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

	/// <summary>
	/// 窗口less 测试用：按真实读盘顺序扫描——先走快速字节判定，判定不了才完整解析。
	/// 与 <see cref="ScanLinesForTest"/> 的结果必须逐字段相同，否则快速路径改写了目录语义。
	/// </summary>
	internal static (string? Cwd, string? Name, string? Preview, int Messages) ScanLinesFastForTest(IEnumerable<string> lines)
	{
		var scan = new CatalogScan();
		foreach (var raw in lines)
		{
			var line = System.Text.Encoding.UTF8.GetBytes(raw);
			if (line.Length == 0 || line[0] != (byte)'{')
			{
				continue;
			}

			if (FastScanLine(line, ref scan))
			{
				continue;
			}

			try
			{
				var candidate = scan;
				ScanLine(line, ref candidate);
				scan = candidate;
			}
			catch (Exception ex) when (ex is JsonException or InvalidOperationException)
			{
			}
		}

		return (scan.Cwd, scan.Name, scan.Preview, scan.Messages);
	}

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
