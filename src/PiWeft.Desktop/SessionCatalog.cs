using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using PiWeft.Ai;
using PiWeft.CodingAgent.Session;
using PiWeft.Cli;

namespace PiWeft.Desktop;

public sealed record SessionListItem
{
	public required string Path { get; init; }

	public required string Id { get; init; }

	public string Name { get; init; } = string.Empty;

	public string Cwd { get; init; } = string.Empty;

	public DateTimeOffset Modified { get; init; }

	public int MessageCount { get; init; }

	public string Preview { get; init; } = string.Empty;

	/// 子会话的父会话 id；普通会话为空。
	public string ParentSession { get; init; } = string.Empty;

	/// 派生来源；子代理为 subagent，分支/普通会话为空。
	public string Origin { get; init; } = string.Empty;

	/// 列表里的标题：会话名优先，其次第一条用户消息，最后退回「新会话」。
	/// 文件名（时间戳 + id）对用户没有意义，不拿出来当标题。
	public string Display => Name.Length > 0
		? Name
		: Preview.Length > 0
			? Preview
			: Strings.UntitledSession;
}

/// 列出并打开磁盘上的会话。文件格式与 pi CLI 共用：终端里开的会话会出现在这里，
/// 桌面会话在终端也能打开。
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
	// 落盘串行：后台写与关窗时的同步写不能同时动同一个临时文件。
	private readonly Lock _persistGate = new();
	private bool _persistPending;
	/// 侧栏一行的缓存。会话文件是只追加的，因此除了长度/时间戳之外还记住
	/// 上次扫到的行边界与首行指纹：文件变长时只扫新增的字节，不重读整份几十 MB。
	private sealed record SummaryStamp(
		long Length,
		DateTime LastWrite,
		SessionListItem Item,
		long Offset,
		long HeadHash,
		long TailHash);

	private bool _persistedLoaded;

	/// 已被原地重写的会话文件：磁盘摘要里的长度/偏移不再可用，装载时必须跳过它们。
	/// 「编辑重发」的墓碑截断会把文件重写一遍，长度可能反而变长，续扫偏移会指向旧内容。
	private readonly HashSet<string> _stale = new(StringComparer.Ordinal);

	private bool _summariesDirty;


	private void EnsurePersistedLoaded()
	{
		lock (_summaryGate)
		{
			if (_persistedLoaded) return;
			_persistedLoaded = true;

			foreach (var (path, stamp) in SessionSummaryStore.Load())
			{
				// 已原地重写过的文件不能用磁盘上的旧偏移续扫（见 Invalidate）。
				if (_stale.Contains(path)) continue;

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
						ParentSession = stamp.ParentSession,
						Origin = stamp.Origin,
					},
					stamp.Offset,
					stamp.HeadHash,
					stamp.TailHash);
			}
		}
	}

	/// 摘要落盘。会话流式期间每轮刷新都会弄脏，写盘不能占着 UI 线程（上千条会话时
	/// 整份重写是十几毫秒）。单飞：一次写在飞时不再排队，下一次刷新再补。
	private void PersistSummaries()
	{
		lock (_summaryGate)
		{
			if (!_summariesDirty || _persistPending) return;
			_persistPending = true;
		}

		// 目录在调度时就定下：写盘在后台跑，不能等到执行时再去读环境变量
		// （测试里会在进程内换 agent 目录，换完再读就把缓存写进了别人的目录）。
		var agentDir = SessionPaths.GetAgentDir();
		_ = Task.Run(() =>
		{
			try
			{
				lock (_persistGate)
				{
					WriteSummaries(agentDir);
				}
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				AppLog.Failure(AppLog.For<SessionCatalog>(), "Persist session summaries", ex);
			}
			finally
			{
				lock (_summaryGate)
				{
					_persistPending = false;
				}
			}
		});
	}

	/// 立刻把待写的摘要落盘。后台写可能在飞，先等它，避免两路同时写同一个临时文件。
	/// 关窗时调：后台写的最后一份数据不能在进程退出时丢掉。
	public void FlushSummaries()
	{
		var agentDir = SessionPaths.GetAgentDir();
		lock (_persistGate)
		{
			WriteSummaries(agentDir);
		}
	}

	private void WriteSummaries(string agentDir)
	{
		Dictionary<string, SessionSummaryStamp>? snapshot;
		lock (_summaryGate)
		{
			if (!_summariesDirty) return;
			_summariesDirty = false;
			snapshot = _summaries
				.Where(entry => !_stale.Contains(entry.Key))
				.ToDictionary(
				static entry => entry.Key,
				static entry => new SessionSummaryStamp
				{
					Length = entry.Value.Length,
					LastWriteTicks = entry.Value.LastWrite.Ticks,
					HeadHash = entry.Value.HeadHash,
					Offset = entry.Value.Offset,
					TailHash = entry.Value.TailHash,
					Id = entry.Value.Item.Id,
					Name = entry.Value.Item.Name,
					Cwd = entry.Value.Item.Cwd,
					MessageCount = entry.Value.Item.MessageCount,
					Preview = entry.Value.Item.Preview,
					// 文件没变时下次启动直接复用这条摘要，不再读文件；
					// 漏写 parentSession / origin 会让子代理会话重启后挂不回父会话。
					ParentSession = entry.Value.Item.ParentSession,
					Origin = entry.Value.Item.Origin,
				},
				StringComparer.Ordinal);
		}

		SessionSummaryStore.Save(snapshot, agentDir);
	}

	/// 会话目录的列举选项：一个子目录读不了（权限、挂载点掉线）时跳过它，
	/// 不能让整次侧栏刷新抛异常。`AttributesToSkip = 0` 保持与旧的重载一致：
	/// 旧重载不跳隐藏项，会话目录里确实可能有隐藏的归档目录。
	private static readonly EnumerationOptions SessionListOptions = new()
	{
		RecurseSubdirectories = true,
		AttributesToSkip = 0,
		IgnoreInaccessible = true,
		ReturnSpecialDirectories = false,
	};

	public IReadOnlyList<SessionListItem> List(string? cwdFilter = null, int limit = 200) => ListCore(false, cwdFilter, limit);

	private IReadOnlyList<SessionListItem> ListCore(bool archived, string? cwdFilter, int limit)
	{
		if (limit <= 0)
		{
			return [];
		}

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

		var paths = Directory.EnumerateFiles(root, "*" + SessionFormat.Extension, SessionListOptions).ToArray();
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

		// 只扫会显示出来的部分：侧栏按修改时间倒序取前 limit 条，先 stat 排序，再扫头部摘要。
		// 会话上千份时，要读的字节从全部降到前 200 份，不用磁盘缓存。
		if (cwdFilter is null && candidates.Length > limit && limit > 0)
		{
			// 子会话不占顶层名额：已缓存的子会话先跳过，选中父会话后再把它们带上，
			// 即使它们的修改时间比名额线更旧。未缓存的仍按修改时间参与。
			var ordered = candidates
				.Select(static path => (Path: path, Write: LastWriteOf(path)))
				.OrderByDescending(static entry => entry.Write)
				.ToArray();
			var selected = new List<string>(limit);
			var selectedIds = new HashSet<string>(StringComparer.Ordinal);
			foreach (var entry in ordered)
			{
				if (selected.Count >= limit)
				{
					break;
				}

				if (TryGetCachedItem(entry.Path, out var cached) &&
					cached.Origin == "subagent" && cached.ParentSession.Length > 0)
				{
					continue;
				}

				selected.Add(entry.Path);
				selectedIds.Add(SessionIdFromPath(entry.Path));
				selectedIds.Add(entry.Path);
			}

			var selectedPaths = new HashSet<string>(selected, StringComparer.Ordinal);
			foreach (var entry in ordered)
			{
				if (selectedPaths.Contains(entry.Path) ||
					!TryGetCachedItem(entry.Path, out var cached) ||
					cached.Origin != "subagent" ||
					cached.ParentSession.Length == 0 ||
					!selectedIds.Contains(cached.ParentSession))
				{
					continue;
				}

				selected.Add(entry.Path);
			}

			// 父会话都没了（删档、归档或全被过滤）时，子会话本身就是入口，
			// 不能因为缓存里带 parentSession 就整批隐身。
			if (selected.Count == 0)
			{
				selected.AddRange(ordered.Take(limit).Select(static entry => entry.Path));
			}

			candidates = [.. selected];
		}

		var items = new SessionListItem[candidates.Length];

		// 会话之间互不依赖，摘要扫描按文件并行。这是启动时最大的一笔 IO 与解析
		// （实测 78 份 / 304 MB / 单线程约 900 ms）；并行后随核数下降，UI 线程不参与。
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

		var filtered = items
			.Where(item => cwdFilter is null || string.Equals(item.Cwd, cwdFilter, StringComparison.Ordinal))
			.OrderByDescending(static item => item.Modified).ToArray();
		// limit 数的是顶层会话：先把子会话从名额里拿掉，再把它们的行带上。
		// 父会话不在本次扫描结果里时，子会话按顶层处理，避免被静默丢掉。
		// parentSession 可能是 id，也可能是文件路径：两种都建键。
		var byKey = new Dictionary<string, SessionListItem>(filtered.Length * 2, StringComparer.Ordinal);
		foreach (var item in filtered)
		{
			byKey[item.Id] = item;
			if (item.Path.Length > 0)
			{
				byKey[item.Path] = item;
			}
		}

		var rootIds = new HashSet<string>(StringComparer.Ordinal);
		foreach (var item in filtered)
		{
			var hasParent = item.Origin == "subagent" &&
				item.ParentSession.Length > 0 &&
				byKey.TryGetValue(item.ParentSession, out var parent) &&
				parent.Id != item.Id;
			if (!hasParent && rootIds.Count < limit)
			{
				rootIds.Add(item.Id);
			}
		}

		var result = filtered
			.Where(item => rootIds.Contains(item.Id) ||
				(item.Origin == "subagent" &&
				 item.ParentSession.Length > 0 &&
				 byKey.TryGetValue(item.ParentSession, out var parent) &&
				 rootIds.Contains(parent.Id)))
			.ToArray();

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

	private bool TryGetCachedItem(string path, out SessionListItem item)
	{
		lock (_summaryGate)
		{
			if (_summaries.TryGetValue(path, out var stamp))
			{
				item = stamp.Item;
				return true;
			}
		}

		item = null!;
		return false;
	}

	/// 文件名是 <c>&lt;时间戳&gt;_&lt;uuid&gt;</c>，取最后一个下划线之后的部分作为会话 id。
	private static string SessionIdFromPath(string path)
	{
		var id = Path.GetFileNameWithoutExtension(path);
		var separator = id.LastIndexOf('_');
		return separator >= 0 && separator + 1 < id.Length ? id[(separator + 1)..] : id;
	}

	private static DateTime LastWriteOf(string path)
	{
		try
		{
			return ResolveSessionFileInfo(path).LastWriteTimeUtc;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			return DateTime.MinValue;
		}
	}

	/// 扫描并行度：文件够多才开线程，每份会话都要读全文，属 IO + CPU 混合。
	/// 留一个核给 UI 线程，时间线布局才抢得到 CPU。
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

	private const int TailWindow = 64;

	private static long TailHashAt(string path, long offset)
	{
		if (offset <= 0)
		{
			return 0;
		}

		try
		{
			var count = (int)Math.Min(TailWindow, offset);
			using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
				FileShare.ReadWrite | FileShare.Delete, bufferSize: 1, FileOptions.RandomAccess);
			stream.Seek(offset - count, SeekOrigin.Begin);
			var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(count);
			try
			{
				var read = stream.ReadAtLeast(buffer.AsSpan(0, count), count, throwOnEndOfStream: false);
				return read == count ? Fnv1a(buffer.AsSpan(0, count)) : 0;
			}
			finally
			{
				System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
			}
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			return 0;
		}
	}

	private static bool TailMatches(string path, long offset, long expected) =>
		expected != 0 && TailHashAt(path, offset) == expected;

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

	internal long ScannedBytesForTests => Interlocked.Read(ref _scanBytes);

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

	public IReadOnlyList<SessionListItem> ListDeleted(int limit = 200) => ListCore(true, null, limit);

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
			// 只读文件、权限不足都不能让剩下的归档停在原地：能删多少删多少。
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				AppLog.Failure(AppLog.For<SessionCatalog>(), "Delete archived session", ex);
			}
		}

		return removed;
	}

	public sealed record ContentMatch(SessionListItem Session, string Excerpt);

	/// 在会话正文里找关键字，扫的是文件全文：会话只追加，早期的对话在文件头部，
	/// 只读尾部时更早的内容永远搜不到。
	/// 读盘按 1 MB 分块，块之间留一截重叠，关键字跨块也能命中；比较在 UTF-8 字节上做，
	/// 只有命中行附近的一小段才解码成字符串。
	/// 大小写按大写形态折叠比对（与 <see cref="StringComparison.OrdinalIgnoreCase"/> 同口径），
	/// 拉丁变音、西里尔字母的大小写都认；没有大小写之分的字（中日韩、数字）走精确匹配的快路径。
	/// <paramref name="maxBytesTotal"/> 是一次搜索的读盘预算：会话上万的机器上扫完几十 GB
	/// 会把一次按键拖成几秒，到预算就停（列表靠前的会话先读到）。
	/// <paramref name="maxBytesPerFile"/> 是单份上限，超出时读尾部——新内容更可能是要找的。
	public static IReadOnlyList<ContentMatch> SearchContent(
		IReadOnlyList<SessionListItem> sessions,
		string? query,
		int maxFiles = 200,
		long maxBytesPerFile = long.MaxValue,
		long maxBytesTotal = 512L * 1024 * 1024,
		CancellationToken cancellationToken = default)
	{
		var trimmed = query?.Trim() ?? string.Empty;
		if (trimmed.Length == 0 || maxBytesPerFile <= 0 || maxBytesTotal <= 0)
		{
			return [];
		}

		var scanCount = Math.Min(Math.Max(maxFiles, 0), sessions.Count);
		if (scanCount == 0)
		{
			return [];
		}

		// 关键字只编码一次：每份会话都要拿它扫几 MB。
		var needle = new ContentNeedle(trimmed);
		if (needle.Length == 0)
		{
			return [];
		}

		// 每份会话读多少字节先按列表顺序定下来：预算被工作线程抢占着扣的话，
		// 同一条关键字的命中范围会随线程调度变，两次按键的结果不一样。
		var windows = PlanWindows(sessions, scanCount, maxBytesPerFile, maxBytesTotal);
		var matches = new ContentMatch?[scanCount];
		void Scan(int index)
		{
			// 取消检查放在扫描体里而不是抛异常：Parallel.For 会把体内抛出的
			// OperationCanceledException 包成 AggregateException，调用方只认 OCE。
			if (cancellationToken.IsCancellationRequested)
			{
				return;
			}

			var excerpt = FindInFile(
				sessions[index].Path, needle, trimmed, windows[index], cancellationToken);
			if (excerpt is not null)
			{
				matches[index] = new ContentMatch(sessions[index], excerpt);
			}
		}

		var parallelism = ScanParallelism(scanCount);
		if (parallelism > 1)
		{
			Parallel.For(0, scanCount, new ParallelOptions
			{
				MaxDegreeOfParallelism = parallelism,
				CancellationToken = cancellationToken,
			}, Scan);
		}
		else
		{
			cancellationToken.ThrowIfCancellationRequested();
			for (var index = 0; index < scanCount; index++)
			{
				Scan(index);
			}
		}

		var result = new List<ContentMatch>(scanCount);
		foreach (var match in matches)
		{
			if (match is not null)
			{
				result.Add(match);
			}
		}

		return result;
	}

	/// 分块读的大小。块越小跨块重叠的开销越多，越大则取消与预算的粒度越粗。
	private const int ScanChunkBytes = 1 << 20;

	/// 命中行前后读多少字节来凑摘要。
	private const int ExcerptMarginBytes = 4 * 1024;

	/// 按列表顺序摊读盘预算：列表前面（更新）的会话先读满，轮到哪份超额度就截到剩余额度读尾部，
	/// 再后面的整份跳过。命中范围因此是确定的，不随线程调度变。
	private static long[] PlanWindows(
		IReadOnlyList<SessionListItem> sessions, int count, long maxBytesPerFile, long maxBytesTotal)
	{
		var windows = new long[count];
		var left = maxBytesTotal;
		for (var index = 0; index < count && left > 0; index++)
		{
			var window = Math.Min(Math.Min(PathLength(sessions[index].Path), maxBytesPerFile), left);
			if (window <= 0)
			{
				continue;
			}

			windows[index] = window;
			left -= window;
		}

		return windows;
	}

	private static long PathLength(string path)
	{
		try
		{
			return new FileInfo(path).Length;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			return 0;
		}
	}

	private static string? FindInFile(
		string path,
		ContentNeedle needle,
		string query,
		long maxBytes,
		CancellationToken cancellationToken)
	{
		if (maxBytes <= 0) return null;

		// 折叠比较时命中在文本里占的字节数可能与关键字本身不同（同一个字在不同大小写下编码长度可能差 1~3 字节），
		// 跨块重叠按最坏情况——每个 rune 4 字节——留。关键字比一块还长时把块也放大。
		var maxSpan = Math.Max(needle.Length, needle.Folded.Length * 4);
		var chunk = Math.Max(ScanChunkBytes, maxSpan);
		var overlap = maxSpan - 1;
		var bytes = System.Buffers.ArrayPool<byte>.Shared.Rent(chunk + overlap);
		try
		{
			using var handle = File.OpenHandle(
				path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			var length = RandomAccess.GetLength(handle);
			if (length <= 0) return null;

			// 单份上限小于文件时读尾部；否则从文件头开始的全文。
			var window = Math.Min(length, maxBytes);
			var start = length - window;
			var filled = 0;
			while (true)
			{
				if (cancellationToken.IsCancellationRequested) return null;

				// 先看这份文件还剩多少没读：小文件读满就结束，不占一整块的缓冲。
				var want = (int)Math.Min(chunk, length - (start + filled));
				if (want <= 0) return null;

				var read = ReadChunk(handle, bytes.AsSpan(filled, want), start + filled);
				filled += read;
				if (read == 0) return null;

				var index = IndexOf(bytes.AsSpan(0, filled), needle, out var hitLength);
				if (index >= 0)
				{
					var hit = start + index;
					return Excerpt(handle, hit, hit + hitLength, length, query);
				}

				if (read < want) return null;

				// 这一块还没攒够一截重叠（预算被切得比关键字还小）：
				// 再往前推也拼不出完整关键字，直接结束，别去切越界的区间。
				if (filled <= overlap) return null;

				// 把尾巴搬到块首：下一块如果从这截里切开关键字，还能命中。
				bytes.AsSpan(filled - overlap, overlap).CopyTo(bytes);
				start += filled - overlap;
				filled = overlap;
			}
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			AppLog.Failure(AppLog.For<SessionCatalog>(), "Search session content", ex);
			return null;
		}
		finally
		{
			System.Buffers.ArrayPool<byte>.Shared.Return(bytes);
		}
	}

	/// 字节级查找。没有大小写的关键字（中日韩、数字、标点）直接走向量化的 `IndexOf`；
	/// 带字母时逐个 rune 折成大写再比，候选位置由首字节的几种写法筛出来。
	/// <paramref name="length"/> 是命中在文本里实际占的字节数：同一个字在不同大小写下编码长度可能不同。
	private static int IndexOf(ReadOnlySpan<byte> haystack, in ContentNeedle needle, out int length)
	{
		if (!needle.Cased)
		{
			length = needle.Length;
			return haystack.IndexOf(needle.Utf8);
		}

		var at = 0;
		while (at < haystack.Length)
		{
			var index = haystack[at..].IndexOfAny(needle.FirstBytes);
			if (index < 0) break;

			index += at;
			if (MatchesAt(haystack[index..], needle.Folded, out length)) return index;
			at = index + 1;
		}

		length = 0;
		return -1;
	}

	private static bool MatchesAt(ReadOnlySpan<byte> haystack, ReadOnlySpan<Rune> folded, out int consumed)
	{
		consumed = 0;
		foreach (var expected in folded)
		{
			// 块尾切在多字节字符中间时解不出 rune：这段交给下一块（重叠里带着完整字节）。
			if (Rune.DecodeFromUtf8(haystack[consumed..], out var rune, out var size) != System.Buffers.OperationStatus.Done)
			{
				return false;
			}

			if (Fold(rune) != expected) return false;
			consumed += size;
		}

		return true;
	}

	/// 折成大写形态判定相等，与 <see cref="StringComparison.OrdinalIgnoreCase"/> 同口径（都是比大写）。
	/// 只折 BMP：补充平面里的字母在 OrdinalIgnoreCase 下也不折，多折反而会让摘要那边找不回来。
	private static Rune Fold(Rune rune) => rune.Value <= 0xFFFF ? Rune.ToUpperInvariant(rune) : rune;

	private static int ReadChunk(SafeFileHandle handle, Span<byte> destination, long offset)
	{
		var read = 0;
		while (read < destination.Length)
		{
			var count = RandomAccess.Read(handle, destination[read..], offset + read);
			if (count <= 0) break;
			read += count;
		}

		return read;
	}

	/// 命中行的摘要：只读关键字前后各几 KB，不给一句摘要去解几十 MB。
	private static string? Excerpt(
		SafeFileHandle handle, long from, long to, long length, string query)
	{
		var start = Math.Max(0, from - ExcerptMarginBytes);
		var end = Math.Min(length, to + ExcerptMarginBytes);
		var count = (int)(end - start);
		if (count <= 0) return null;

		var bytes = System.Buffers.ArrayPool<byte>.Shared.Rent(count);
		var chars = System.Buffers.ArrayPool<char>.Shared.Rent(count);
		try
		{
			var read = ReadChunk(handle, bytes.AsSpan(0, count), start);
			if (read <= 0) return null;

			var window8 = Utf8Window(bytes.AsSpan(0, read));
			var decoded = System.Text.Encoding.UTF8.GetChars(window8, chars);
			ReadOnlySpan<char> text = chars.AsSpan(0, decoded);

			var hit = text.IndexOf(query.AsSpan(), StringComparison.OrdinalIgnoreCase);
			if (hit < 0)
			{
				// 折叠能命中的字节，解码后按 OrdinalIgnoreCase 也应该找得到；
				// 真找不到（窗口边界缺了开头）就把整段当摘要。
				return Summarize(new string(text), query);
			}

			var (lineStart, lineEnd) = ExcerptBounds(text, hit, query.Length);
			return Summarize(new string(text[lineStart..lineEnd]), query);
		}
		finally
		{
			System.Buffers.ArrayPool<byte>.Shared.Return(bytes);
			System.Buffers.ArrayPool<char>.Shared.Return(chars);
		}
	}

	/// 一次搜索的关键字：原样字节、折成大写后的 rune 序列、首字节候选集。
	private readonly struct ContentNeedle
	{
		public ContentNeedle(string query)
		{
			Utf8 = System.Text.Encoding.UTF8.GetBytes(query);

			var folded = new List<Rune>(Utf8.Length);
			var cased = false;
			foreach (var rune in query.EnumerateRunes())
			{
				folded.Add(Fold(rune));
				// 有大小写之分的字才需要折叠比较；中日韩、数字、标点都走精确匹配的快路径。
				cased |= rune.Value <= 0xFFFF && Rune.ToUpperInvariant(rune) != Rune.ToLowerInvariant(rune);
			}

			Folded = [.. folded];
			Cased = cased;
			Rune.DecodeFromUtf16(query, out var first, out _);
			FirstBytes = FirstBytesOf(first);
		}

		public readonly byte[] Utf8;

		public readonly Rune[] Folded;

		public readonly System.Buffers.SearchValues<byte> FirstBytes;

		/// 关键字里有大小写之分的字，得折着比。
		public readonly bool Cased;

		public int Length => Utf8.Length;
	}

	/// 关键字第一个字的几种写法（原形、大写、小写）各自的首字节：候选位置靠它一次筛出来，
	/// 之后才逐个 rune 折叠比对。像 'ı'/'I' 这种共享同一大写字形、却互不为大小写形式的异体，
	/// 只有当关键字本身带着那个写法时才会命中。
	private static System.Buffers.SearchValues<byte> FirstBytesOf(Rune first)
	{
		Span<Rune> variants = [first, Fold(first), Rune.ToLowerInvariant(first)];
		Span<byte> encoded = stackalloc byte[4];
		Span<byte> candidates = stackalloc byte[4];
		var count = 0;
		foreach (var variant in variants)
		{
			if (variant.EncodeToUtf8(encoded) <= 0) continue;

			var lead = encoded[0];
			var seen = false;
			for (var index = 0; index < count; index++)
			{
				seen |= candidates[index] == lead;
			}

			if (!seen)
			{
				candidates[count++] = lead;
			}
		}

		return System.Buffers.SearchValues.Create(candidates[..count]);
	}

	/// 尾部窗口可能切在多字节字符中间：开头丢掉续字节，结尾丢掉被截断的那几个字节，
	/// 否则摘要里会出现 U+FFFD。
	private static ReadOnlySpan<byte> Utf8Window(ReadOnlySpan<byte> bytes)
	{
		while (bytes.Length > 0 && (bytes[0] & 0xC0) == 0x80) bytes = bytes[1..];

		var back = 0;
		while (back < 3 && back < bytes.Length && (bytes[^(back + 1)] & 0xC0) == 0x80)
		{
			back++;
		}

		if (back < bytes.Length)
		{
			var expected = bytes[^(back + 1)] switch { >= 0xF0 => 4, >= 0xE0 => 3, >= 0xC0 => 2, _ => 1 };
			if (expected > back + 1)
			{
				bytes = bytes[..(bytes.Length - back - 1)];
			}
		}

		return bytes;
	}

	/// 命中行的边界。一条工具输出可以是一行几百 KB，只留关键字附近的一段：
	/// 再长也只是为了给一行摘要，却要解码整行。
	private static (int Start, int End) ExcerptBounds(ReadOnlySpan<char> text, int hit, int length)
	{
		const int radius = 4096;
		var lineStart = hit == 0 ? 0 : text[..hit].LastIndexOf('\n') + 1;
		var newline = text[(hit + length)..].IndexOf('\n');
		var lineEnd = newline < 0 ? text.Length : hit + length + newline;
		if (lineEnd - lineStart <= radius * 2)
		{
			return (lineStart, lineEnd);
		}

		var start = Math.Max(lineStart, hit - radius);
		var end = Math.Min(lineEnd, hit + length + radius);
		// 切点不能落在代理对中间：摘要会露出半个 emoji。
		if (start > lineStart && char.IsLowSurrogate(text[start])) start++;
		if (end < lineEnd && char.IsHighSurrogate(text[end - 1])) end--;
		return (start, end);
	}

	/// 把命中行压成一段以关键字为中心的摘要。会话文件是 JSONL，命中多半在消息文本里，
	/// 因此先试着只取 JSON 中的字符串内容，避免把字段名与转义噪音一起端给用户；
	/// 取不到（或那里面没有关键字）时退回整行摘要。
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

	internal static string Center(string text, string query, int radius = 60)
	{
		var index = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
		if (index < 0)
		{
			return text.Length <= radius * 2 ? text : Clip(text, radius * 2);
		}

		var start = Math.Max(0, index - radius);
		var end = Math.Min(text.Length, index + query.Length + radius);
		// 切点不能落在代理对中间：否则预览会出现半个 emoji。
		if (start > 0 && char.IsLowSurrogate(text[start])) start++;
		if (end < text.Length && char.IsHighSurrogate(text[end - 1])) end--;

		return string.Concat(
			start > 0 ? "…" : string.Empty,
			text[start..end],
			end < text.Length ? "…" : string.Empty);
	}

	private static string Clip(string text, int limit)
	{
		var length = Math.Min(limit, text.Length);
		if (length > 0 && char.IsHighSurrogate(text[length - 1])) length--;
		return length >= text.Length ? text : string.Concat(text.AsSpan(0, length), "…");
	}

	/// 取 JSON 行里的字符串值（跳过键名）。
	/// 用的是 <c>isFinalBlock: false</c> 的读取器：正文搜索只读文件尾部，最后一行常常是半个 JSON，
	/// 完整文档式的解析（JsonNode）会整行报错，摘要就只能退回原始 JSON 噪音。
	internal static string? ExtractText(string line)
	{
		if (line.Length == 0 || line[0] != '{')
		{
			return null;
		}

		try
		{
			var reader = new Utf8JsonReader(
				System.Text.Encoding.UTF8.GetBytes(line), isFinalBlock: false, state: default);
			List<string>? parts = null;
			while (reader.Read())
			{
				if (reader.TokenType == JsonTokenType.String && reader.GetString() is { Length: > 0 } text)
				{
					parts ??= [];
					parts.Add(text);
				}
			}

			return parts is null ? null : string.Join(' ', parts);
		}
		catch (JsonException)
		{
			return null;
		}
	}

	/// 最近修改的会话文件路径：只 stat 目录，不读任何内容。
	/// 启动时用它提前并行解析首个会话，而不必等整份侧栏摘要扫完。
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
			foreach (var path in Directory.EnumerateFiles(root, "*" + SessionFormat.Extension, SessionListOptions))
			{
				if (path.Contains($"{Path.DirectorySeparatorChar}.trash{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
				{
					continue;
				}

				try
				{
					var write = ResolveSessionFileInfo(path).LastWriteTimeUtc;
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

	public SessionManager Open(string path, string? cwdFallback = null) => SessionManager.Open(path, cwdFallback: cwdFallback);

	/// 同上，但可汇报读取进度并可取消。长会话（几十 MB）要读上要近一秒，
	/// 桌面在后台线程调用它，同时让加载提示显示百分比。
	public SessionManager Open(
		string path,
		string? cwdFallback,
		IProgress<SessionReadProgress>? progress,
		CancellationToken cancellationToken) =>
		SessionManager.Open(path, null, null, cwdFallback, progress, cancellationToken);

	public void Rename(string path, string? name) => SessionManager.Open(path).AppendSessionInfo(name);

	/// 把会话文件移到同目录下的 <c>.trash</c> 里。pi 的会话是只追加的，直接删除无法恢复，
	/// 因此这里保留一份，用户要彻底清理时自行删除该目录。
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

	public SessionManager Create(string cwd, string? sessionDir = null) =>
		SessionManager.Create(cwd, sessionDir);

	/// 丢弃某个会话文件的摘要缓存。会话文件被原地重写（编辑重发的墓碑截断）后，长度可能
	/// 反而变长，缓存里的续扫偏移已经不指向同一段内容；不丢弃会把截断前的预览与条数留在侧栏。
	/// 只改内存，不在调用线程上读写磁盘：调用方就在 UI 线程（发送完成、编辑重发），
	/// 而整个摘要文件的重写属于后台扫描的收尾工作。`<c>_stale</c>` 同时挡住「磁盘摘要重新装入」
	/// 这条回头路，`PersistSummaries` 会在下一次列表构建时把那个条目一并从磁盘上抹掉。
	public void Invalidate(string? path)
	{
		if (string.IsNullOrEmpty(path)) return;
		path = Path.GetFullPath(path);

		lock (_summaryGate)
		{
			_stale.Add(path);
			_summaries.Remove(path);
			// 即使内存里没有这一条也要写回：磁盘上的旧条目必须跟着消失。
			_summariesDirty = true;
		}
	}

	private SessionListItem ToItem(string path)
	{
		// 链接的大小是链接目标的路径长度，不是会话内容：用它当摘要缓存键会让侧栏永远显示旧预览。
		var info = ResolveSessionFileInfo(path);
		var modified = info.LastWriteTimeUtc;
		var length = info.Exists ? info.Length : 0;
		SummaryStamp? cached;
		lock (_summaryGate)
		{
			_summaries.TryGetValue(path, out cached);
		}

		if (cached is not null && cached.LastWrite == modified && cached.Length == length) return cached.Item;
		var id = SessionIdFromPath(path);

		// 缓存里那一行就是文件前缀的扫描结果：文件变长时把它当续扫起点。
		CatalogScan scan;
		var resumeFrom = 0L;
		var headHash = 0L;
		SummaryStamp? known;
		lock (_summaryGate)
		{
			_summaries.TryGetValue(path, out known);
		}

		// 续扫要求「上次扫到的位置之前那段字节没被动过」：只比对首行指纹是不够的，
		// 会话被原地重写（本端口的墓碑截断、或外部工具）时首行通常原样保留，偏移却已失效。
		if (known is not null && known.TailHash != 0 && length > known.Length)
		{
			var previous = known.Item;
			scan = new CatalogScan
			{
				Name = previous.Name.Length == 0 ? null : previous.Name,
				Cwd = previous.Cwd.Length == 0 ? string.Empty : previous.Cwd,
				Preview = previous.Preview.Length == 0 ? null : previous.Preview,
				// 续扫从上一轮的偏移开始，读不到首行头部：parentSession 与 origin 只能继承。
				// 漏掉 origin 时子代理会话会被当成顶层会话，侧栏里掉出父会话。
				ParentSession = previous.ParentSession.Length == 0 ? null : previous.ParentSession,
				Origin = previous.Origin.Length == 0 ? null : previous.Origin,
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
			// 首行与续扫点之前的一小段尾巴都要对得上。会话被原地重写时首行通常不变，
			// 只有尾巴能证明「这几十 MB 前缀确实是上次看到的那一份」。
			var start = resumeFrom;
			if (start > 0 && (!HeadMatches(path, headHash) || !TailMatches(path, start, known!.TailHash)))
			{
				start = 0;
				headHash = 0;
				scan = new CatalogScan();
			}

			using var lines = new Utf8LineReader(path, start);
			// 首行（会话头部）的指纹用来判断下次能不能续扫；整份扫描时只看第一行，
			// 不能逐行覆盖：覆盖后存下来的是末行指纹，校验却读首行，续扫会永远失败。
			var fingerprintFirstLine = start == 0;
			while (lines.TryReadLine(out var line))
			{
				if (fingerprintFirstLine)
				{
					headHash = Fnv1a(line);
					fingerprintFirstLine = false;
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
			ParentSession = scan.ParentSession ?? string.Empty,
			Origin = scan.Origin ?? string.Empty,
		};
		// 读过的字节数总是累计：它只是一次原子加法（每份文件一次），
		// 而「续扫到底有没有生效」是回归测试唯一能观察到的量，不能只在开诊断时才有值。
		Interlocked.Add(ref _scanBytes, watchedBytes);
		if (watch is not null)
		{
			Interlocked.Add(ref _scanTicks, watch.ElapsedTicks);
			Interlocked.Increment(ref _scanCount);
		}

		info.Refresh();
		// A writer may have appended while we scanned. Cache only a complete, stable snapshot.
		if (readSucceeded && headHash != 0 && info.Exists && info.Length == length && info.LastWriteTimeUtc == modified)
		{
			var tailHash = TailHashAt(path, scannedTo);
			lock (_summaryGate)
			{
				_summaries[path] = new SummaryStamp(length, modified, item, scannedTo, headHash, tailHash);
				// 重写后的文件已经全量重扫：新摘要可信，不再是「不可续扫」的旧摘要。
				_stale.Remove(path);
				_summariesDirty = true;
			}
		}

		return item;
	}

	/// 会话文件的元数据。链接要解析到最终目标：<c>alias.jsonl</c> 的大小是目标路径的长度、
	/// 修改时间是链接自身的，拿它当摘要缓存键会让侧栏永远显示旧预览。
	/// 只有重解析点才多付一次解析：列目录动辄几百个会话，普通文件不应因此多一次系统调用。
	private static FileInfo ResolveSessionFileInfo(string path)
	{
		var info = new FileInfo(path);
		if ((info.Attributes & FileAttributes.ReparsePoint) == 0)
		{
			return info;
		}

		try
		{
			// 解析失败时退回链接自身：列表仍能显示，只是摘要缓存按链接元数据失效。
			return File.ResolveLinkTarget(path, returnFinalTarget: true) is { } target
				? new FileInfo(target.FullName)
				: info;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
			or ArgumentException or NotSupportedException)
		{
			return info;
		}
	}

	private ref struct CatalogScan
	{
		public string? Name;
		public string? Cwd;
		public string? Preview;
		public string? ParentSession;
		public string? Origin;
		public int Messages;
	}

	/// 不用 JSON 解析判断一行对目录有没有影响。返回 false 表示必须走完整的 <see cref="ScanLine"/>
	/// （会话头部、session_info、需要预览的首条用户消息，以及任何形状不合常规的行）。
	/// 兼容两种真实写法：pi 的 <c>{"id":"…",…,"type":"message"…}</c> 与外部导出的
	/// <c>{"type": "message", …}</c>（键后带空格）。只读行首窗口里的头几个键，正文一概不碰；
	/// 尾部被截断（不以 <c>}</c> 结束）或键序异常的行退回完整解析，容错与原来一致。
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

	private static bool StartsForKey(ReadOnlySpan<byte> head, ReadOnlySpan<byte> key)
	{
		if (head.Length < key.Length + 3 || head[0] != (byte)'{' || head[1] != (byte)'\'')
		{
			return false;
		}

		return head.Slice(2, key.Length).SequenceEqual(key) && head[2 + key.Length] == (byte)'\"';
	}

	/// 在行首窗口里读 <c>"key" : "value"</c>，容忍冒号前后的空白。
	/// 只扫到第一个 <c>"content"</c> 之前，避免把正文里的同名字段当头部键。
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
		string? parentSession = null;
		string? origin = null;
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
			else if (reader.ValueTextEquals("parentSession"u8))
			{
				reader.Read();
				parentSession = reader.GetString();
			}
			else if (reader.ValueTextEquals("origin"u8))
			{
				reader.Read();
				origin = reader.GetString();
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
			scan.ParentSession = parentSession;
			scan.Origin = origin;
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

	internal static (string? Cwd, string? Name, string? Preview, int Messages, string? ParentSession, string? Origin) ScanLineForTest(string line) =>
		ScanLinesForTest([line]);

	/// 无窗口测试用：按真实读盘顺序扫描，先走快速字节判定，判定不了才完整解析。
	/// 与 <see cref="ScanLinesForTest"/> 的结果必须逐字段相同，否则快速路径改写了目录语义。
	internal static (string? Cwd, string? Name, string? Preview, int Messages, string? ParentSession, string? Origin) ScanLinesFastForTest(IEnumerable<string> lines)
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

		return (scan.Cwd, scan.Name, scan.Preview, scan.Messages, scan.ParentSession, scan.Origin);
	}

	internal static (string? Cwd, string? Name, string? Preview, int Messages, string? ParentSession, string? Origin) ScanLinesForTest(IEnumerable<string> lines)
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

		return (scan.Cwd, scan.Name, scan.Preview, scan.Messages, scan.ParentSession, scan.Origin);
	}

	private static string Summarize(string? text)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			return string.Empty;
		}

		// 单遍折叠空白并截到 42 字：旧实现反复 Replace("  ", " ")，
		// 一条多空格的长消息会每轮分配一份整串。
		Span<char> buffer = stackalloc char[42];
		var length = 0;
		var pendingSpace = false;
		foreach (var character in text.AsSpan())
		{
			if (char.IsWhiteSpace(character))
			{
				pendingSpace = length > 0;
				continue;
			}

			if (pendingSpace)
			{
				if (length == buffer.Length) return CutBuffer(buffer);
				buffer[length++] = ' ';
				pendingSpace = false;
			}

			if (length == buffer.Length) return CutBuffer(buffer);
			buffer[length++] = character;
		}

		return new string(buffer[..length]);

		static string CutBuffer(ReadOnlySpan<char> buffer)
		{
			var cut = buffer.Length;
			if (char.IsHighSurrogate(buffer[cut - 1])) cut--;
			return string.Concat(buffer[..cut], "…");
		}
	}
}
