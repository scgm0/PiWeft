using System.Collections.Frozen;
using System.Collections.Immutable;
using PiWeft.Ai;

namespace PiWeft.CodingAgent.Session;

public readonly record struct SessionReadProgress(long BytesRead, long TotalBytes, int Lines)
{
	public double Fraction => TotalBytes <= 0 ? 0 : Math.Clamp((double)BytesRead / TotalBytes, 0, 1);
}

/// 读写 pi 的会话文件：用 <c>id</c>/<c>parentId</c> 串联的仅追加 JSONL 树，感知压缩的
/// 上下文构建，以及分支导航。磁盘格式与 pi 二进制线上兼容。
public sealed partial class SessionManager
{
	private readonly Lock _stateGate = new();
	private long _revision;
	private readonly List<SessionLine> _fileEntries = [];
	private readonly Dictionary<string, SessionEntry> _byId = new(StringComparer.Ordinal);
	private readonly Dictionary<string, string> _labelsById = new(StringComparer.Ordinal);
	private readonly bool _persist;
	private readonly System.Buffers.ArrayBufferWriter<byte> _persistBuffer = new(1024);
	private string? _leafId;
	private IReadOnlyList<SessionEntry>? _entriesSnapshot;
	private IReadOnlyDictionary<string, string>? _labelsSnapshot;
	private ImmutableList<SessionEntry>? _branchSnapshot;
	private IReadOnlyList<Message>? _projectedSnapshot;
	private List<Message>? _incrementalBase;
	private List<Message> _incrementalTail = [];
	private IReadOnlyList<SessionEntry>? _incrementalBranch;

	public long Revision { get { lock (_stateGate) return _revision; } }

	private void Invalidate(bool keepIncrementalProjection = false, bool keepBranch = false)
	{
		_revision++;
		_entriesSnapshot = null;
		_labelsSnapshot = null;
		if (!keepBranch)
		{
			_branchSnapshot = null;
		}

		_projectedSnapshot = null;
		if (!keepIncrementalProjection)
		{
			_incrementalBase = null;
			_incrementalTail = [];
			_incrementalBranch = null;
		}
	}

	private SessionManager(string cwd, string sessionDir, string? sessionFile, bool persist)
	{
		Cwd = cwd;
		SessionDir = sessionDir;
		SessionFile = sessionFile;
		_persist = persist;
		SessionId = string.Empty;
	}

	public string Cwd { get; }

	public string SessionDir { get; }

	public string? SessionFile { get; private set; }

	public string SessionId { get; private set; }

	/// 当前叶子。引用读是原子的，不取闸：读到的可能是刚被下一次追加换掉的值，
	/// 只适合展示与跳转。要基于叶子做决定（校验、算父链接）必须进闸。
	public string? LeafId => _leafId;

	public int EntryCount
	{
		get
		{
			lock (_stateGate) return _byId.Count;
		}
	}

	public IReadOnlyList<SessionEntry> Entries
	{
		get
		{
			lock (_stateGate) return _entriesSnapshot ??= Array.AsReadOnly([.. EnumerateEntries()]);
		}
	}

	/// 按文件顺序枚举条目行；头部落在这之外。
	private IEnumerable<SessionEntry> EnumerateEntries()
	{
		foreach (var line in _fileEntries)
		{
			if (line is SessionEntry entry)
			{
				yield return entry;
			}
		}
	}

	internal (long Revision, string SessionId, IReadOnlyList<SessionEntry> Entries) CaptureEntries()
	{
		using var scope = _stateGate.EnterScope();
		return (_revision, SessionId, Entries);
	}

	public SessionHeader? Header
	{
		get
		{
			// 和 Revision/Entries 同一个闸：追加在后台线程发生，这里要遍历 _fileEntries，
			// 不加锁会撞上「Collection was modified」。Lock 可重入，ToSessionInfo 持闸调用不会死锁。
			using var scope = _stateGate.EnterScope();
			foreach (var line in _fileEntries)
			{
				if (line is SessionHeader header)
				{
					return header;
				}
			}

			return null;
		}
	}


	public static SessionManager Create(
		string cwd,
		string? sessionDir = null,
		string? id = null,
		string? parentSession = null,
		string? origin = null,
		DateTimeOffset? now = null)
	{
		if (id is not null && !SessionPaths.IsValidSessionId(id))
		{
			throw new InvalidOperationException(
				$"Invalid session ID \"{id}\": use letters, numbers, \".\", \"_\", \"-\", and start and end with a letter or number.");
		}

		var dir = sessionDir ?? SessionPaths.GetDefaultSessionDir(cwd);
		Directory.CreateDirectory(dir);

		var manager = new SessionManager(Path.GetFullPath(cwd), dir, null, true);
		var timestamp = SessionPaths.ToIso8601(now ?? DateTimeOffset.UtcNow);
		manager.SessionId = id ?? SessionPaths.NewSessionId();
		manager.SessionFile = Path.Combine(dir, SessionPaths.SessionFileName(timestamp, manager.SessionId));
		manager._fileEntries.Add(new SessionHeader
		{
			Id = manager.SessionId,
			Timestamp = timestamp,
			Cwd = manager.Cwd,
			ParentSession = parentSession,
			Origin = origin,
		});

		File.WriteAllText(manager.SessionFile, SessionJson.Serialize(manager.Header!) + "\n");
		return manager;
	}

	/// <param name="upgradeFile">
	/// 旧版会话（v1/v2）是否就地升级并回写文件。只读导出传 <see langword="false"/>：
	/// 内存里仍按当前格式解释，但不改用户的文件。
	/// </param>
	public static SessionManager Open(
		string path,
		string? sessionDir = null,
		string? cwdOverride = null,
		string? cwdFallback = null,
		bool upgradeFile = true)
		=> Open(path, sessionDir, cwdOverride, cwdFallback, progress: null, cancellationToken: default, upgradeFile: upgradeFile);

	/// 同上，并支持进度回调与取消。长会话（几十 MB）解析要上百毫秒到数秒，
	/// 桌面把它放到后台线程并显示进度；<paramref name="progress"/> 会在读取线程上被调用。
	public static SessionManager Open(
		string path,
		string? sessionDir,
		string? cwdOverride,
		string? cwdFallback,
		IProgress<SessionReadProgress>? progress,
		CancellationToken cancellationToken,
		bool upgradeFile = true)
	{
		var resolved = Path.GetFullPath(path);
		var entries = ReadFileEntries(resolved, progress, cancellationToken, repairTrailingNewline: upgradeFile);

		// 头部几乎总在第一行：先看首项，损坏/外部文件再退回全表扫描。
		var header = entries.Count > 0 && entries[0] is SessionHeader first
			? first
			: entries.Select(static line => line.Header).FirstOrDefault();
		var cwd = NormalizeCwd(cwdOverride)
			?? NormalizeCwd(header?.Cwd)
			?? NormalizeCwd(cwdFallback)
			?? Environment.CurrentDirectory;
		var dir = sessionDir ?? Path.GetDirectoryName(resolved) ?? Environment.CurrentDirectory;

		var manager = new SessionManager(cwd, dir, resolved, true);
		manager.LoadEntries(entries, header);
		manager.MigrateLegacyEntries(header, upgradeFile);
		return manager;
	}

	private static string? NormalizeCwd(string? cwd)
	{
		if (string.IsNullOrWhiteSpace(cwd))
		{
			return null;
		}

		try
		{
			return Path.GetFullPath(cwd);
		}
		catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
		{
			return null;
		}
	}

	public static SessionManager ContinueRecent(string cwd, string? sessionDir = null)
	{
		var dir = sessionDir ?? SessionPaths.GetDefaultSessionDir(cwd);
		var filterCwd = sessionDir is not null;
		var mostRecent = FindMostRecentSession(dir, filterCwd ? Path.GetFullPath(cwd) : null);
		return mostRecent is null ? Create(cwd, dir) : Open(mostRecent, dir);
	}

	/// 把会话文件复制到 <paramref name="targetCwd"/>，保留全部条目及其 id/parentId，
	/// 使派生会话从同一个叶子继续。新头部会把源文件记为父会话。
	public static SessionManager ForkFrom(
		string sourcePath,
		string targetCwd,
		string? sessionDir = null,
		string? id = null)
	{
		var resolved = Path.GetFullPath(sourcePath);
		var entries = ReadFileEntries(resolved);

		if (!entries.Any(static line => line is SessionHeader))
		{
			throw new InvalidOperationException($"Cannot fork: source session has no header: {resolved}");
		}

		// fork 只读源文件，派生会话必须是当前格式：v1 条目没有 id，直接复制过去会让分支投影只剩最后一条。
		// 迁移只在内存里做，源文件交给 pi 或下一次打开处理。
		SessionMigration.Migrate(entries);

		var manager = Create(targetCwd, sessionDir, id, parentSession: resolved);
		try
		{
			foreach (var line in entries)
			{
				if (line is not SessionEntry entry)
				{
					continue;
				}

				manager.AppendExisting(entry, persist: false);
			}

			// 一条 fd 写完再原子替换：中途失败不会留下半个派生会话，也不会每写一条就打开一次文件。
			manager.RewriteFile();
		}
		catch
		{
			// 失败时删掉刚建出的文件：内存里的 manager 不会交出去，磁盘上不该留下空壳。
			try
			{
				if (manager.SessionFile is { } path && File.Exists(path))
				{
					File.Delete(path);
				}
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
			}

			throw;
		}

		return manager;
	}

	private void AppendExisting(SessionEntry entry, bool persist = true)
	{
		using var scope = _stateGate.EnterScope();
		Invalidate();
		_fileEntries.Add(entry);
		_byId[entry.Id] = entry;

		if (entry is LabelEntry { Label: { Length: > 0 } label } labelEntry)
		{
			_labelsById[labelEntry.TargetId] = label;
		}
		else if (entry is LabelEntry { Label: null } cleared)
		{
			_labelsById.Remove(cleared.TargetId);
		}

		if (entry is not DeletedEntry)
		{
			_leafId = entry.Id;
		}

		if (persist)
		{
			Persist(entry);
		}
	}

	public static SessionManager InMemory(string cwd, string? id = null)
	{
		var manager = new SessionManager(Path.GetFullPath(cwd), string.Empty, null, false);
		var timestamp = SessionPaths.ToIso8601(DateTimeOffset.UtcNow);
		manager.SessionId = id ?? SessionPaths.NewSessionId();
		manager._fileEntries.Add(new SessionHeader
		{
			Id = manager.SessionId,
			Timestamp = timestamp,
			Cwd = manager.Cwd,
		});
		return manager;
	}

	private void LoadEntries(List<SessionLine> entries, SessionHeader? header)
	{
		_fileEntries.AddRange(entries);
		SessionId = header?.Id ?? SessionPaths.NewSessionId();
		RebuildIndex();
	}

	/// 旧版会话升级到当前格式。内存里总会升级，否则 v1 的树与分支投影是坏的；
	/// 只有 <paramref name="rewriteFile"/> 时才回写磁盘。pi 打开会话即迁移并重写，
	/// 本端口保持一致；只读导出只解释，不落盘。
	private void MigrateLegacyEntries(SessionHeader? header, bool rewriteFile)
	{
		// 当前版本的会话占绝大多数：先用已解析的头部挡掉，避免每次打开都线性扫一遍条目。
		if (header is null || header.Version >= SessionFormat.CurrentVersion)
		{
			return;
		}

		if (!SessionMigration.Migrate(_fileEntries))
		{
			return;
		}

		RebuildIndex();
		if (!rewriteFile)
		{
			return;
		}

		try
		{
			RewriteFile();
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			// 只读介质或磁盘满：内存里已升级，仍能浏览与继续对话。
			// 因为迁移失败就打不开会话，是更坏的交换。
		}
	}

	/// 把整份会话按当前格式重写：同目录临时文件加原子替换，中途崩溃不会留下半个会话。
	/// 用于格式升级与「编辑重发」截断；正常路径只追加。
	/// <param name="source">要写入的条目序列；为空时写当前内存视图。</param>
	private void RewriteFile(IReadOnlyList<SessionLine>? source = null)
	{
		if (SessionFile is not { Length: > 0 } path || !File.Exists(path))
		{
			return;
		}

		// 会话文件可能是符号链接（dotfiles 管理器常见）：写入要跟随到目标，
		// 否则一次迁移会把链接换成普通文件，目标里的旧内容还留在原地。
		var target = ResolveSessionTarget(path);
		var directory = Path.GetDirectoryName(target);
		if (directory is not { Length: > 0 })
		{
			return;
		}

		var temporary = Path.Combine(directory, $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.tmp");
		try
		{
			using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
			{
				// 一次迁移可能重写几十 MB：复用缓冲区，按行直接写 UTF-8 字节，
				// 不为每行生成字符串再编码一次。
				var buffer = new System.Buffers.ArrayBufferWriter<byte>(1024);
				foreach (var item in source ?? _fileEntries)
				{
					buffer.Clear();
					switch (item)
					{
						case SessionHeader header:
							SessionJson.WriteHeaderLine(buffer, header);
							break;
						case SessionEntry entry:
							SessionJson.WriteEntryLine(buffer, entry);
							break;
						default:
							continue;
					}

					stream.Write(buffer.WrittenSpan);
				}

				stream.Flush(flushToDisk: true);
			}

			if (!OperatingSystem.IsWindows())
			{
				// 迁移不该顺手放宽会话文件的权限（pi 用默认 umask 写，只读用户可能有更严的设置）。
				File.SetUnixFileMode(temporary, File.GetUnixFileMode(target));
			}

			File.Move(temporary, target, overwrite: true);
		}
		finally
		{
			try
			{
				if (File.Exists(temporary))
				{
					File.Delete(temporary);
				}
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				// 临时文件清理失败不影响已完成的迁移。
			}
		}
	}

	/// 在文件最前面插入一行会话头，其余字节原样保留。
	/// 先写同目录临时文件再原子替换；读原文与写临时文件同时进行，不需要把整个文件读进内存。
	private static void PrependHeader(string target, SessionHeader header)
	{
		var directory = Path.GetDirectoryName(target);
		if (directory is not { Length: > 0 })
		{
			throw new IOException($"Session file has no directory: {target}");
		}

		var temporary = Path.Combine(directory, $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.tmp");
		try
		{
			using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
			{
				var buffer = new System.Buffers.ArrayBufferWriter<byte>(256);
				SessionJson.WriteHeaderLine(buffer, header);
				output.Write(buffer.WrittenSpan);

				if (File.Exists(target))
				{
					using var input = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
					input.CopyTo(output);
				}

				output.Flush(flushToDisk: true);
			}

			if (!OperatingSystem.IsWindows() && File.Exists(target))
			{
				// 修复头部不该顺手放宽/改变会话文件的权限。
				File.SetUnixFileMode(temporary, File.GetUnixFileMode(target));
			}

			File.Move(temporary, target, overwrite: true);
		}
		finally
		{
			try
			{
				if (File.Exists(temporary))
				{
					File.Delete(temporary);
				}
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
			}
		}
	}

	private static string ResolveSessionTarget(string path)
	{
		try
		{
			return new FileInfo(path).LinkTarget is null
				? path
				: File.ResolveLinkTarget(path, returnFinalTarget: true) is { } target
					? Path.GetFullPath(target.FullName)
					: path;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
			or ArgumentException or NotSupportedException)
		{
			return path;
		}
	}

	private static List<SessionLine> ReadFileEntries(
		string path,
		IProgress<SessionReadProgress>? progress = null,
		CancellationToken cancellationToken = default,
		bool repairTrailingNewline = false)
	{
		var entries = new List<SessionLine>();
		if (!File.Exists(path))
		{
			return entries;
		}

		// 符号链接的长度是链接本身的长度，不是目标文件的长度：拿它当读取上界会把会话截断
		// （只读到头部）。读、写都统一解析到最终目标。
		var target = ResolveSessionTarget(path);
		var totalBytes = new FileInfo(target).Length;
		var chunks = ChunkCount(totalBytes);
		if (chunks <= 1)
		{
			var counted = 0;
			long consumedTotal = 0;
			long reported = 0;
			ScanRange(target, 0, totalBytes, entries, cancellationToken, (delta, lines) =>
			{
				consumedTotal += delta;
				counted += lines;
				// 每 256 行或 4 MB 汇报一次：进度条不必逐行更新，回调本身也要钱。
				if (progress is not null && (counted % 256 == 0 || consumedTotal - reported >= 4 * 1024 * 1024))
				{
					reported = consumedTotal;
					progress.Report(new SessionReadProgress(reported, totalBytes, counted));
				}
			});

			progress?.Report(new SessionReadProgress(totalBytes, totalBytes, counted));
			if (repairTrailingNewline)
			{
				EnsureTrailingNewline(target);
			}

			return entries;
		}

		// 会话文件是行式 JSON，条目之间只靠 parentId 关联，解析阶段互不依赖：
		// 按字节切成几段并行扫，结果按段序拼接，顺序与单线程读完全一致。
		var parts = new List<SessionLine>?[chunks];
		for (var index = 0; index < chunks; index++)
		{
			parts[index] = new List<SessionLine>();
		}

		long bytesRead = 0;
		long linesRead = 0;
		long reportedBytes = 0;
		var reportGate = new Lock();

		// 进度只能单调向前。各分片同时在扫，"算完字节数"与"发出去"之间存在窗口：
		// 先算到 12 MB 的线程可能比算到 8 MB 的那个晚调用 Report，界面上的进度条就会
		// 倒退一格。所以在闸门里重读计数、只允许更大值出去，Report 也顺带变成单线程调用。
		void PublishProgress()
		{
			if (progress is null)
			{
				return;
			}

			// 快路径：绝大多数行都还没跑够 4 MB，不进锁。（读旧值只会让我们多进一次锁，
			// 不影响单调性，判断真伪都在锁内重做。）
			if (Interlocked.Read(ref bytesRead) - Volatile.Read(ref reportedBytes) < 4 * 1024 * 1024)
			{
				return;
			}

			lock (reportGate)
			{
				var done = Interlocked.Read(ref bytesRead);
				if (done - reportedBytes < 4 * 1024 * 1024)
				{
					return;
				}

				reportedBytes = done;
				progress.Report(new SessionReadProgress(
					Math.Min(done, totalBytes),
					totalBytes,
					(int)Interlocked.Read(ref linesRead)));
			}
		}

		Parallel.For(
			0,
			chunks,
			new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = chunks },
			index =>
			{
				var from = (long)index * totalBytes / chunks;
				var to = index + 1 < chunks ? (long)(index + 1) * totalBytes / chunks : totalBytes;
				ScanRange(
					target, from, to, parts[index]!, cancellationToken,
					(delta, lines) =>
					{
						Interlocked.Add(ref bytesRead, delta);
						Interlocked.Add(ref linesRead, lines);
						PublishProgress();
					});
			});

		foreach (var part in parts)
		{
			if (part is { Count: > 0 } parsed)
			{
				entries.AddRange(parsed);
			}
		}

		progress?.Report(new SessionReadProgress(totalBytes, totalBytes, (int)Interlocked.Read(ref linesRead)));
		if (repairTrailingNewline)
		{
			EnsureTrailingNewline(target);
		}

		return entries;
	}

	/// 只有几十 MB 级别的文件才值得并行：小文件切段后，句柄与调度开销比省下的时间还多。
	/// 并行度留余量：打开会话时 UI 线程还要建树，不该吃满整机核数。
	private static int ChunkCount(long totalBytes)
	{
		if (totalBytes < 8 * 1024 * 1024)
		{
			return 1;
		}

		var limit = Environment.ProcessorCount switch
		{
			<= 2 => 1,
			<= 4 => 2,
			<= 8 => 3,
			_ => 4,
		};

		return Math.Min(limit, Math.Max(1, (int)(totalBytes / (4 * 1024 * 1024))));
	}

	/// 扫描 <paramref name="from"/>..<paramref name="to"/> 之间的整行。非首段先丢掉被切开的那半行
	/// （它归上一段），所以每行恰好被解析一次；<paramref name="onLine"/> 收到该行的字节增量与行数。
	private static void ScanRange(
		string path,
		long from,
		long to,
		List<SessionLine> sink,
		CancellationToken cancellationToken,
		Action<int, int>? onLine = null)
	{
		// 段首的半行由 reader 自己跳掉：它归上一段，这里只消费整行。
		using var reader = new Utf8LineReader(path, from);
		var previousBytes = reader.BytesRead;
		while (reader.TryReadLine(out var line))
		{
			var consumed = reader.BytesRead;
			var delta = (int)Math.Max(1, consumed - previousBytes);
			previousBytes = consumed;
			cancellationToken.ThrowIfCancellationRequested();
			try
			{
				if (SessionJson.ParseLine(line) is { } parsed)
				{
					sink.Add(parsed);
				}
			}
			catch (System.Text.Json.JsonException)
			{
				// 跳过格式错误的行，不丢掉后面的会话内容。
			}

			onLine?.Invoke(delta, 1);
			if (consumed >= to)
			{
				break;
			}
		}
	}

		private static void EnsureTrailingNewline(string path)
	{
		try
		{
			using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
			if (stream.Length == 0)
			{
				return;
			}

			stream.Seek(-1, SeekOrigin.End);
			if (stream.ReadByte() == '\n')
			{
				return;
			}

			stream.Seek(0, SeekOrigin.End);
			stream.WriteByte((byte)'\n');
			stream.Flush(flushToDisk: true);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			// 修复失败不影响打开会话本身。
		}
	}

	private void RebuildIndex()
	{
		using var scope = _stateGate.EnterScope();
		Invalidate();
		_byId.Clear();
		_labelsById.Clear();
		_leafId = null;

		foreach (var entry in EnumerateEntries())
		{
			_byId[entry.Id] = entry;

			// 墓碑是死分支上的占位：它不抢叶子，也不参与标签。
			if (entry is DeletedEntry)
			{
				continue;
			}

			_leafId = entry.Id;

			if (entry is LabelEntry { Label: { Length: > 0 } label } labelEntry)
			{
				_labelsById[labelEntry.TargetId] = label;
			}
			else if (entry is LabelEntry { Label: null } cleared)
			{
				_labelsById.Remove(cleared.TargetId);
			}
		}
	}


	public string AppendMessage(Message message) => Append(new SessionMessageEntry
	{
		Timestamp = SessionPaths.ToIso8601(DateTimeOffset.UtcNow),
		Message = message,
	}).Id;

	public string AppendModelChange(string provider, string modelId) => Append(new ModelChangeEntry
	{
		Timestamp = SessionPaths.ToIso8601(DateTimeOffset.UtcNow),
		Provider = provider,
		ModelId = modelId,
	}).Id;

	public string AppendThinkingLevelChange(string thinkingLevel) => Append(new ThinkingLevelChangeEntry
	{
		Timestamp = SessionPaths.ToIso8601(DateTimeOffset.UtcNow),
		ThinkingLevel = thinkingLevel,
	}).Id;

	public UsageEntry AppendUsage(string kind, string provider, string model, Usage usage, string? note = null)
		=> (UsageEntry)Append(new UsageEntry
		{
			Timestamp = SessionPaths.ToIso8601(DateTimeOffset.UtcNow),
			Kind = kind,
			Provider = provider,
			Model = model,
			Usage = usage,
			Note = note,
		});

	public string AppendCompaction(
		string summary,
		string firstKeptEntryId,
		long tokensBefore,
		SystemMessage? systemMessage = null,
		Usage? usage = null,
		System.Text.Json.Nodes.JsonNode? details = null,
		bool? fromHook = null)
	{
		var now = DateTimeOffset.UtcNow;
		return Append(new CompactionEntry
		{
			Timestamp = SessionPaths.ToIso8601(now),
			Summary = summary,
			FirstKeptEntryId = firstKeptEntryId,
			TokensBefore = tokensBefore,
			// 与 pi 一致：检查点时间戳跟 compaction 条目对齐，回放时不保留旧提示词的构建时间。
			SystemMessage = systemMessage is { } checkpoint
				? checkpoint with { Timestamp = now.ToUnixTimeMilliseconds() }
				: null,
			Usage = usage,
			Details = details,
			FromHook = fromHook,
		}).Id;
	}

	public string AppendBranchSummary(
		string fromId,
		string summary,
		Usage? usage = null,
		System.Text.Json.Nodes.JsonNode? details = null) => Append(new BranchSummaryEntry
	{
		Timestamp = SessionPaths.ToIso8601(DateTimeOffset.UtcNow),
		FromId = fromId,
		Summary = summary,
		Usage = usage,
		Details = details,
	}).Id;

	public string AppendCustom(string customType, System.Text.Json.Nodes.JsonNode? data = null) => Append(new CustomEntry
	{
		Timestamp = SessionPaths.ToIso8601(DateTimeOffset.UtcNow),
		CustomType = customType,
		Data = data,
	}).Id;

	public string AppendCustomMessage(
		string customType,
		MessageContent content,
		bool display = true,
		System.Text.Json.Nodes.JsonNode? details = null) => Append(new CustomMessageEntry
	{
		Timestamp = SessionPaths.ToIso8601(DateTimeOffset.UtcNow),
		CustomType = customType,
		Content = content,
		Display = display,
		Details = details,
	}).Id;

	public string AppendSessionInfo(string? name) => Append(new SessionInfoEntry
	{
		Timestamp = SessionPaths.ToIso8601(DateTimeOffset.UtcNow),
		Name = name,
	}).Id;

	/// 追加一条上下文编辑：<paramref name="replacement"/> 为 null 表示把目标条目从模型上下文中移除，
	/// 有值则替换其内容。pi 的重试就靠它把失败的那次尝试从投影里去掉（原文保留在文件里）。
	public string AppendContextEdit(string targetId, MessageContent? replacement)
	{
		// 校验与追加要在同一个闸里：分开的话目标可能在两步之间被换掉。
		using var scope = _stateGate.EnterScope();
		if (!_byId.ContainsKey(targetId))
		{
			throw new InvalidOperationException($"Entry {targetId} not found");
		}

		return Append(new ContextEditEntry
		{
			Timestamp = SessionPaths.ToIso8601(DateTimeOffset.UtcNow),
			TargetId = targetId,
			Replacement = replacement,
		}).Id;
	}

	public string AppendLabelChange(string targetId, string? label)
	{
		// 标签的增删由 Append 在闸内完成，这里只做存在性校验。
		using var scope = _stateGate.EnterScope();
		if (!_byId.ContainsKey(targetId))
		{
			throw new InvalidOperationException($"Entry {targetId} not found");
		}

		return Append(new LabelEntry
		{
			Timestamp = SessionPaths.ToIso8601(DateTimeOffset.UtcNow),
			TargetId = targetId,
			Label = label,
		}).Id;
	}

	private SessionEntry Append(SessionEntry entry)
	{
		using var scope = _stateGate.EnterScope();

		// Id 与父链接都在闸内定。闸外读 _leafId 的话两个并发追加会挂到同一个父上，
		// 文件里出现分叉而不是一条链；NextId 也要读 _byId，同样不能落在闸外。
		var previousLeafId = _leafId;
		entry = entry with { Id = NextId(), ParentId = previousLeafId };

		var hadLabel = false;
		string? previousLabel = null;
		if (entry is LabelEntry capturedLabel)
		{
			hadLabel = _labelsById.TryGetValue(capturedLabel.TargetId, out previousLabel);
		}

		// 这一条总接在当前叶子后面，增量投影可以保留；只有能改早前消息的条目要整体重建。
		// A normal message only extends the projection. Edits, compaction and summaries can
		// change earlier messages, so those still rebuild it from the branch.
		Invalidate(
			keepIncrementalProjection:
				entry is not (ContextEditEntry or CompactionEntry or BranchSummaryEntry or CustomMessageEntry),
			keepBranch: true);
		_fileEntries.Add(entry);
		_byId[entry.Id] = entry;
		if (_branchSnapshot is { } branch)
		{
			// ImmutableList shares the old path; the projection's cached branch stays unchanged.
			_branchSnapshot = branch.Add(entry);
		}

		if (entry is LabelEntry { Label: { Length: > 0 } label } labelEntry)
		{
			_labelsById[labelEntry.TargetId] = label;
		}
		else if (entry is LabelEntry { Label: null } cleared)
		{
			_labelsById.Remove(cleared.TargetId);
		}

		_leafId = entry.Id;
		try
		{
			Persist(entry);
		}
		catch
		{
			// 与 pi 的 _appendEntry 一致：没写进文件的条目不能留在内存里，否则它会成为下一条的父级，
			// 文件里出现指向不存在父级的链，重开会话时这一段连同后续追加都读不出来（数据丢失）。
			Rollback(entry, previousLeafId, hadLabel, previousLabel);
			throw;
		}

		return entry;
	}

	/// 追加失败（磁盘满、只读、被独占）时把内存回退到追加之前。
	/// 条目按追加顺序存放，失败的那条总在表尾（补会话头的场景会在它前面插一行，但本条仍是最后一项）；
	/// 缓存一律丢弃重建，只把叶子与标签恢复成原值。
	private void Rollback(SessionEntry entry, string? previousLeafId, bool hadLabel, string? previousLabel)
	{
		if (_fileEntries.Count > 0 && ReferenceEquals(_fileEntries[^1].Value, entry))
		{
			_fileEntries.RemoveAt(_fileEntries.Count - 1);
		}
		else
		{
			_fileEntries.RemoveAll(item => ReferenceEquals(item.Value, entry));
		}

		_byId.Remove(entry.Id);
		if (entry is LabelEntry labelEntry)
		{
			if (hadLabel)
			{
				_labelsById[labelEntry.TargetId] = previousLabel!;
			}
			else
			{
				_labelsById.Remove(labelEntry.TargetId);
			}
		}

		_leafId = previousLeafId;
		Invalidate();
	}

	private void Persist(SessionEntry entry)
	{
		if (!_persist || SessionFile is null)
		{
			return;
		}

		// 文件里没有头部（外部工具写出的会话）时先补一个，别让它一直缺下去：
		// pi 与 PiDeck 都按「首行是 session 头」解析，缺头的文件读不出会话身份。
		// 头部要落在第一行，所以这里整体重写文件，不追加到末尾。
		if (Header is null)
		{
			var header = new SessionHeader
			{
				Id = SessionId,
				Timestamp = SessionPaths.ToIso8601(DateTimeOffset.UtcNow),
				Cwd = Cwd,
			};

			// 补头部要整体重写文件：写穿符号链接，否则链接会被替换成普通文件（与 RewriteFile 同一理由）。
			// 用临时文件 + 原子替换，不原地截断重写：外部工具写出的会话可能很大，
			// 截断后写到一半崩溃会把整份会话弄丢。
			var target = ResolveSessionTarget(SessionFile);
			PrependHeader(target, header);
			_fileEntries.Insert(0, header);
		}

		_persistBuffer.Clear();
		SessionJson.WriteEntryLine(_persistBuffer, entry);
		File.AppendAllBytes(SessionFile, _persistBuffer.WrittenSpan);
	}

	private string NextId()
	{
		for (var attempt = 0; attempt < 100; attempt++)
		{
			var id = Guid.NewGuid().ToString("N")[..8];
			if (!_byId.ContainsKey(id))
			{
				return id;
			}
		}

		return Guid.NewGuid().ToString();
	}


	public void Branch(string branchFromId)
	{
		using var scope = _stateGate.EnterScope();
		if (!_byId.ContainsKey(branchFromId))
		{
			throw new InvalidOperationException($"Entry {branchFromId} not found");
		}

		_leafId = branchFromId;
		Invalidate();
	}

	public void ResetLeaf()
	{
		using var scope = _stateGate.EnterScope();
		_leafId = null;
		Invalidate();
	}

	public IReadOnlyList<SessionEntry> GetBranch(string? fromId = null)
	{
		using var scope = _stateGate.EnterScope();
		if (fromId is null && _branchSnapshot is { } cached)
		{
			return cached;
		}

		var path = BuildBranchPath(fromId ?? _leafId);
		if (fromId is null)
		{
			_branchSnapshot = path;
		}

		return path;
	}

	/// <summary>从当前叶子向根找第一条满足条件的条目；不构建分支快照。谓词在会话锁内执行，不能回调本管理器。</summary>
	public SessionEntry? FindFromLeaf(Func<SessionEntry, bool> match)
	{
		ArgumentNullException.ThrowIfNull(match);
		using var scope = _stateGate.EnterScope();
		var currentId = _leafId;
		while (currentId is not null && _byId.TryGetValue(currentId, out var current))
		{
			if (match(current))
			{
				return current;
			}

			currentId = current.ParentId;
		}

		return null;
	}

	private ImmutableList<SessionEntry> BuildBranchPath(string? leafId)
	{
		var path = new List<SessionEntry>();
		var currentId = leafId;
		while (currentId is not null && _byId.TryGetValue(currentId, out var current))
		{
			path.Add(current);
			currentId = current.ParentId;
		}

		path.Reverse();
		return ImmutableList.CreateRange(path);
	}

	public IReadOnlyList<SessionEntry> BuildContextEntries() => BuildContextEntries(GetBranch());

	private IReadOnlyList<SessionEntry> BuildContextEntries(IReadOnlyList<SessionEntry> path)
	{
		CompactionEntry? compaction = null;
		foreach (var entry in path)
		{
			if (entry is CompactionEntry candidate)
			{
				compaction = candidate;
			}
		}

		if (compaction is null)
		{
			return path;
		}

		var contextEntries = new List<SessionEntry> { compaction };
		var foundFirstKept = false;
		var beforeCompaction = true;
		foreach (var entry in path)
		{
			if (beforeCompaction)
			{
				if (entry.Id == compaction.Id)
				{
					beforeCompaction = false;
					continue;
				}

				if (entry.Id == compaction.FirstKeptEntryId)
				{
					foundFirstKept = true;
				}

				// 被保留范围内的旧 compaction 不再贡献消息：最新检查点已经取代它。
				// 留着会把旧摘要重复放进投影，也会让 Compactor 把旧摘要当成 previousSummary。
				if (foundFirstKept && entry is not CompactionEntry &&
					entry is not SessionMessageEntry { Message: SystemMessage })
				{
					contextEntries.Add(entry);
				}

				continue;
			}

			contextEntries.Add(entry);
		}

		return contextEntries;
	}

	public IReadOnlyList<Message> BuildProjectedMessages()
	{
		using var scope = _stateGate.EnterScope();
		if (_projectedSnapshot is { } cached) return cached;
		var branch = GetBranch();
		if (TryExtendProjection(branch, out var extended))
		{
			return _projectedSnapshot = extended;
		}

		var entries = BuildContextEntries(branch);

		// 对每个目标条目，取当前分支上最后一次编辑的结果。
		var edits = new Dictionary<string, ContextEditEntry>(StringComparer.Ordinal);
		foreach (var entry in branch)
		{
			if (entry is ContextEditEntry edit)
			{
				edits[edit.TargetId] = edit;
			}
		}

		var messages = new List<Message>(entries.Count);
		foreach (var entry in entries)
		{
			if (edits.TryGetValue(entry.Id, out var edit))
			{
				if (edit.Replacement is null)
				{
					continue;
				}

				AddEntryMessages(messages, entry, edit.Replacement);
			}
			else
			{
				AddEntryMessages(messages, entry, replacement: null);
			}
		}

		_incrementalBase = messages;
		_incrementalTail = [];
		_incrementalBranch = branch;
		return _projectedSnapshot = messages.AsReadOnly();
	}

	private bool TryExtendProjection(IReadOnlyList<SessionEntry> branch, out IReadOnlyList<Message> messages)
	{
		messages = [];
		if (_incrementalBase is not { } baseMessages || _incrementalBranch is not { } previousBranch ||
			branch.Count < previousBranch.Count)
		{
			return false;
		}

		// The branch is a tree path, so the last shared entry proves the whole prefix: its
		// ParentId chain fixes every entry before it.
		if (previousBranch.Count > 0 &&
			!ReferenceEquals(branch[previousBranch.Count - 1], previousBranch[previousBranch.Count - 1]))
		{
			return false;
		}

		for (var index = previousBranch.Count; index < branch.Count; index++)
		{
			if (branch[index] is ContextEditEntry or CompactionEntry or BranchSummaryEntry or CustomMessageEntry)
			{
				return false;
			}
		}

		var tail = new List<Message>(_incrementalTail);
		for (var index = previousBranch.Count; index < branch.Count; index++)
		{
			if (branch[index] is SessionMessageEntry messageEntry)
			{
				AddEntryMessages(tail, messageEntry, replacement: null);
			}
		}

		_incrementalTail = tail;
		_incrementalBranch = branch;

		// Fold a long tail back into the base occasionally; most appends add one or two messages,
		// and the joined view keeps the 10k-message prefix shared instead of copying it.
		const int MaxTail = 512;
		if (tail.Count > MaxTail)
		{
			var merged = new List<Message>(baseMessages.Count + tail.Count);
			merged.AddRange(baseMessages);
			merged.AddRange(tail);
			_incrementalBase = merged;
			_incrementalTail = [];
			messages = merged.AsReadOnly();
			return true;
		}

		messages = tail.Count == 0 ? baseMessages.AsReadOnly() : new JoinedMessages(baseMessages, tail.ToArray());
		return true;
	}

	private sealed class JoinedMessages(IReadOnlyList<Message> prefix, Message[] tail) : IReadOnlyList<Message>
	{
		public int Count => prefix.Count + tail.Length;

		public Message this[int index] => index < prefix.Count ? prefix[index] : tail[index - prefix.Count];

		public IEnumerator<Message> GetEnumerator()
		{
			foreach (var message in prefix)
			{
				yield return message;
			}

			foreach (var message in tail)
			{
				yield return message;
			}
		}

		System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
	}

	/// Appends an entry's projected messages without building a temporary list per entry.
	/// A context edit replaces the content of every message the entry contributes.
	private static void AddEntryMessages(List<Message> messages, SessionEntry entry, MessageContent? replacement)
	{
		switch (entry)
		{
			case SessionMessageEntry message:
				messages.Add(replacement is null ? message.Message : ReplaceContent(message.Message, replacement.Value));
				break;

			case CompactionEntry compaction:
				if (compaction.SystemMessage is { } checkpoint)
				{
					messages.Add(replacement is null ? checkpoint : ReplaceContent(checkpoint, replacement.Value));
				}

				var summary = new UserMessage
				{
					Content = MessageContent.FromText(
						CodingMessages.CompactionSummaryPrefix + compaction.Summary + CodingMessages.CompactionSummarySuffix),
					Timestamp = ToUnixMs(compaction.Timestamp),
				};
				messages.Add(replacement is null ? summary : ReplaceContent(summary, replacement.Value));
				break;

			case BranchSummaryEntry branch:
				var branchMessage = new UserMessage
				{
					Content = MessageContent.FromText(
						CodingMessages.BranchSummaryPrefix + branch.Summary + CodingMessages.BranchSummarySuffix),
					Timestamp = ToUnixMs(branch.Timestamp),
				};
				messages.Add(replacement is null ? branchMessage : ReplaceContent(branchMessage, replacement.Value));
				break;

			case CustomMessageEntry custom:
				var customMessage = new UserMessage
				{
					Content = custom.Content,
					Timestamp = ToUnixMs(custom.Timestamp),
					CustomType = custom.CustomType,
					Display = custom.Display,
				};
				messages.Add(replacement is null ? customMessage : ReplaceContent(customMessage, replacement.Value));
				break;
		}
	}

	public SessionContext BuildSessionContext()
	{
		using var scope = _stateGate.EnterScope();
		(string Provider, string ModelId)? model = null;
		var thinkingLevel = ThinkingLevels.Off;

		foreach (var entry in GetBranch())
		{
			switch (entry)
			{
				case ModelChangeEntry modelChange:
					model = (modelChange.Provider, modelChange.ModelId);
					break;
				case ThinkingLevelChangeEntry thinking:
					thinkingLevel = thinking.ThinkingLevel;
					break;
			}
		}

		return new SessionContext
		{
			Messages = BuildProjectedMessages(),
			ThinkingLevel = thinkingLevel,
			Model = model,
		};
	}

	public string FirstUserMessageText()
	{
		using var scope = _stateGate.EnterScope();
		foreach (var entry in EnumerateEntries())
		{
			// 回报开场的会话不该拿机器文本当标题。
			if (entry is SessionMessageEntry { Message: UserMessage user } && user.IsUserAuthored &&
				ContentText(user.Content) is { Length: > 0 } text)
			{
				return text;
			}
		}

		return string.Empty;
	}

	public string? SessionName()
	{
		using var scope = _stateGate.EnterScope();
		string? name = null;
		foreach (var entry in EnumerateEntries())
		{
			if (entry is SessionInfoEntry info)
			{
				name = info.Name;
			}
		}

		return name;
	}

	/// 标签快照。返回的是冻结副本，不是线上字典：调用方在锁外枚举它，
	/// 而追加在后台线程发生，直接给出白线字典会拼“Collection was modified”。
	public IReadOnlyDictionary<string, string> Labels
	{
		get
		{
			lock (_stateGate)
			{
				return _labelsSnapshot ??= _labelsById.ToFrozenDictionary(StringComparer.Ordinal);
			}
		}
	}

	public SessionInfo ToSessionInfo()
	{
		using var scope = _stateGate.EnterScope();
		var file = SessionFile ?? string.Empty;
		var created = DateTimeOffset.TryParse(Header?.Timestamp, out var parsed) ? parsed : DateTimeOffset.MinValue;
		var modified = created;
		var messageCount = 0;
		var allText = new System.Text.StringBuilder();

		foreach (var entry in EnumerateEntries())
		{
			if (DateTimeOffset.TryParse(entry.Timestamp, out var entryTime) && entryTime > modified)
			{
				modified = entryTime;
			}

			if (entry is SessionMessageEntry { Message: AssistantMessage } or SessionMessageEntry { Message: UserMessage })
			{
				messageCount++;
			}

			if (entry is SessionMessageEntry { Message: UserMessage user } && ContentText(user.Content) is { Length: > 0 } text)
			{
				allText.Append(text).Append('\n');
			}
		}

		return new SessionInfo
		{
			Path = file,
			Id = SessionId,
			Cwd = Cwd,
			Name = SessionName(),
			ParentSessionPath = Header?.ParentSession,
			Created = created,
			Modified = modified,
			MessageCount = messageCount,
			FirstMessage = FirstUserMessageText(),
			AllMessagesText = allText.ToString(),
		};
	}


	public static IReadOnlyList<SessionInfo> ListSessions(string sessionDir, string? cwdFilter = null)
	{
		if (!Directory.Exists(sessionDir))
		{
			return [];
		}

		var sessions = new List<SessionInfo>();
		foreach (var path in Directory.EnumerateFiles(sessionDir, "*" + SessionFormat.Extension))
		{
			try
			{
				var info = Open(path).ToSessionInfo();
				if (cwdFilter is not null && !PathsEqual(info.Cwd, cwdFilter))
				{
					continue;
				}

				sessions.Add(info);
			}
			catch (IOException)
			{
				// 忽略已消失或无法读取的文件。
			}
		}

		sessions.Sort(static (a, b) => b.Modified.CompareTo(a.Modified));
		return sessions;
	}

	public static string? FindMostRecentSession(string sessionDir, string? cwdFilter = null)
	{
		var sessions = ListSessions(sessionDir, cwdFilter);
		return sessions.Count == 0 ? null : sessions[0].Path;
	}


	public static IReadOnlyList<Message> MessagesForEntry(SessionEntry entry) => ToMessages(entry);

	/// 一组条目里每个目标的最新 <c>context_edit</c>（后写的覆盖先写的）。
	/// 分支摘要与压缩必须按同一套投影规则取消息：被隐藏的失败尝试不能重新进入摘要请求，
	/// 内容替换也要按替换后的版本摘要（参照 pi 的 buildSessionProjection / prepareBranchEntries）。
	internal static Dictionary<string, ContextEditEntry> LatestContextEdits(IEnumerable<SessionEntry> entries)
	{
		var edits = new Dictionary<string, ContextEditEntry>(StringComparer.Ordinal);
		foreach (var entry in entries)
		{
			if (entry is ContextEditEntry edit)
			{
				edits[edit.TargetId] = edit;
			}
		}

		return edits;
	}

	/// 把单个条目投影成模型可见消息，并应用针对它的 <c>context_edit</c>（<c>null</c> 表示移除）。
	internal static IReadOnlyList<Message> ProjectEntry(SessionEntry entry, ContextEditEntry? edit)
	{
		// 单个消息条目是最常见的形态，避免为一条消息分配 List 再走一遍替换循环。
		if (entry is SessionMessageEntry messageEntry)
		{
			if (edit is null)
			{
				return [messageEntry.Message];
			}

			return edit.Replacement is { } singleReplacement
				? [ReplaceContent(messageEntry.Message, singleReplacement)]
				: [];
		}

		var messages = ToMessages(entry);
		if (edit is not { Replacement: { } replacement })
		{
			return edit is null ? messages : [];
		}

		for (var index = 0; index < messages.Count; index++)
		{
			messages[index] = ReplaceContent(messages[index], replacement);
		}

		return messages;
	}

	private static List<Message> ToMessages(SessionEntry entry) => entry switch
	{
		SessionMessageEntry message => [message.Message],
		CompactionEntry compaction => CompactionMessages(compaction),
		BranchSummaryEntry branch =>
		[
			new UserMessage
			{
				Content = MessageContent.FromText(
					CodingMessages.BranchSummaryPrefix + branch.Summary + CodingMessages.BranchSummarySuffix),
				Timestamp = ToUnixMs(branch.Timestamp),
			},
		],
		CustomMessageEntry custom =>
		[
			new UserMessage
			{
				Content = custom.Content,
				Timestamp = ToUnixMs(custom.Timestamp),
				CustomType = custom.CustomType,
				Display = custom.Display,
			},
		],
		_ => [],
	};

	private static List<Message> CompactionMessages(CompactionEntry compaction)
	{
		var messages = new List<Message>(2);
		if (compaction.SystemMessage is { } checkpoint)
		{
			messages.Add(checkpoint);
		}

		messages.Add(new UserMessage
		{
			Content = MessageContent.FromText(
				CodingMessages.CompactionSummaryPrefix + compaction.Summary + CodingMessages.CompactionSummarySuffix),
			Timestamp = ToUnixMs(compaction.Timestamp),
		});

		return messages;
	}

	private static Message ReplaceContent(Message message, MessageContent replacement) => message switch
	{
		UserMessage user => user with { Content = replacement },
		SystemMessage system => system with { Content = replacement },
		AssistantMessage assistant => assistant with { Content = ToBlocks(replacement) },
		ToolResultMessage result => result with { Content = ToBlocks(replacement) },
		CustomMessage custom => custom with { Content = replacement },
		_ => message,
	};

	private static List<ContentBlock> ToBlocks(MessageContent content) =>
		content.IsText
			? [new TextContent { Text = content.Text ?? string.Empty }]
			: [.. content.Blocks ?? []];

	private static string? ContentText(MessageContent content)
	{
		if (content.IsText)
		{
			return content.Text;
		}

		var text = string.Concat(content.Blocks?.OfType<TextContent>().Select(static block => block.Text) ?? []);
		return text.Length == 0 ? null : text;
	}

	private static long ToUnixMs(string isoTimestamp) =>
		DateTimeOffset.TryParse(isoTimestamp, out var parsed) ? parsed.ToUnixTimeMilliseconds() : 0;

	private static bool PathsEqual(string a, string b) =>
		string.Equals(
			Path.GetFullPath(a).TrimEnd('/', '\\'),
			Path.GetFullPath(b).TrimEnd('/', '\\'),
			OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
