using System.Collections.Immutable;
using PiWeft.Ai;

namespace PiWeft.CodingAgent.Session;

/// <summary>
/// 读写 pi 的会话文件：用 <c>id</c>/<c>parentId</c> 串联的仅追加 JSONL 树，感知压缩的
/// 上下文构建，以及分支导航。磁盘格式与 pi 二进制线上兼容。
/// </summary>
public sealed partial class SessionManager
{
	private readonly Lock _stateGate = new();
	private long _revision;
	private readonly List<object> _fileEntries = [];
	private readonly Dictionary<string, SessionEntry> _byId = new(StringComparer.Ordinal);
	private readonly Dictionary<string, string> _labelsById = new(StringComparer.Ordinal);
	private readonly bool _persist;
	private readonly System.Buffers.ArrayBufferWriter<byte> _persistBuffer = new(1024);
	private string? _leafId;
	private IReadOnlyList<SessionEntry>? _entriesSnapshot;
	private ImmutableList<SessionEntry>? _branchSnapshot;
	private IReadOnlyList<Message>? _projectedSnapshot;
	private List<Message>? _incrementalBase;
	private List<Message> _incrementalTail = [];
	private IReadOnlyList<SessionEntry>? _incrementalBranch;

	/// <summary>追加、重载、新建或导航后递增，供消费者失效派生缓存。</summary>
	public long Revision { get { lock (_stateGate) return _revision; } }

	private void Invalidate(bool keepIncrementalProjection = false, bool keepBranch = false)
	{
		_revision++;
		_entriesSnapshot = null;
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

	/// <summary>会话启动时的工作目录。</summary>
	public string Cwd { get; }

	public string SessionDir { get; }

	public string? SessionFile { get; private set; }

	public string SessionId { get; private set; }

	public string? LeafId => _leafId;

	public int EntryCount => _byId.Count;

	/// <summary>按文件顺序排列的全部条目，不含头部。</summary>
	public IReadOnlyList<SessionEntry> Entries
	{
		get
		{
			lock (_stateGate) return _entriesSnapshot ??=
				Array.AsReadOnly(_fileEntries.OfType<SessionEntry>().ToArray());
		}
	}

	internal (long Revision, string SessionId, IReadOnlyList<SessionEntry> Entries) CaptureEntries()
	{
		using var scope = _stateGate.EnterScope();
		return (_revision, SessionId, Entries);
	}

	public SessionHeader? Header => _fileEntries.OfType<SessionHeader>().FirstOrDefault();

	// ---------------------------------------------------------------- 构造

	/// <summary>在 <paramref name="cwd"/> 下创建一个会落盘的新会话。</summary>
	public static SessionManager Create(
		string cwd,
		string? sessionDir = null,
		string? id = null,
		string? parentSession = null,
		DateTimeOffset? now = null)
	{
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
		});

		File.WriteAllText(manager.SessionFile, SessionJson.Serialize(manager.Header!) + "\n");
		return manager;
	}

	/// <summary>打开已有的会话文件。工作目录默认取头部记录，可显式覆盖；头部损坏时可用 <paramref name="cwdFallback"/> 兜底。</summary>
	public static SessionManager Open(
		string path,
		string? sessionDir = null,
		string? cwdOverride = null,
		string? cwdFallback = null)
	{
		var resolved = Path.GetFullPath(path);
		var entries = ReadFileEntries(resolved);

		var header = entries.OfType<SessionHeader>().FirstOrDefault();
		var cwd = NormalizeCwd(cwdOverride)
			?? NormalizeCwd(header?.Cwd)
			?? NormalizeCwd(cwdFallback)
			?? Environment.CurrentDirectory;
		var dir = sessionDir ?? Path.GetDirectoryName(resolved) ?? Environment.CurrentDirectory;

		var manager = new SessionManager(cwd, dir, resolved, true);
		manager.LoadEntries(entries, header);
		return manager;
	}

	/// <summary>把头部里的工作目录规范成绝对路径；空值或损坏值交给调用方回退。</summary>
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

	/// <summary>打开该工作目录下最近的会话；不存在则新建。</summary>
	public static SessionManager ContinueRecent(string cwd, string? sessionDir = null)
	{
		var dir = sessionDir ?? SessionPaths.GetDefaultSessionDir(cwd);
		var filterCwd = sessionDir is not null;
		var mostRecent = FindMostRecentSession(dir, filterCwd ? Path.GetFullPath(cwd) : null);
		return mostRecent is null ? Create(cwd, dir) : Open(mostRecent, dir);
	}

	/// <summary>
	/// 把会话文件复制到 <paramref name="targetCwd"/>，保留全部条目及其 id/parentId，
	/// 使派生会话从同一个叶子继续。新头部会把源文件记为父会话。
	/// </summary>
	public static SessionManager ForkFrom(
		string sourcePath,
		string targetCwd,
		string? sessionDir = null,
		string? id = null)
	{
		var resolved = Path.GetFullPath(sourcePath);
		var entries = ReadFileEntries(resolved);

		if (!entries.OfType<SessionHeader>().Any())
		{
			throw new InvalidOperationException($"Cannot fork: source session has no header: {resolved}");
		}

		var manager = Create(targetCwd, sessionDir, id, parentSession: resolved);
		foreach (var entry in entries.OfType<SessionEntry>())
		{
			manager.AppendExisting(entry);
		}

		return manager;
	}

	/// <summary>追加一个已有 id 的条目，复制会话时使用。</summary>
	private void AppendExisting(SessionEntry entry)
	{
		using var scope = _stateGate.EnterScope();
		Invalidate();
		_fileEntries.Add(entry);
		_byId[entry.Id] = entry;

		if (entry is LabelEntry { Label: { Length: > 0 } label } labelEntry)
		{
			_labelsById[labelEntry.TargetId] = label;
		}

		_leafId = entry.Id;
		Persist(entry);
	}

	/// <summary>创建一个完全不落盘的会话。</summary>
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

	private void LoadEntries(List<object> entries, SessionHeader? header)
	{
		_fileEntries.AddRange(entries);
		SessionId = header?.Id ?? SessionPaths.NewSessionId();
		RebuildIndex();
	}

	private static List<object> ReadFileEntries(string path)
	{
		var entries = new List<object>();
		if (!File.Exists(path))
		{
			return entries;
		}

		foreach (var line in File.ReadLines(path))
		{
			try
			{
				if (SessionJson.ParseLine(line) is { } parsed)
				{
					entries.Add(parsed);
				}
			}
			catch (System.Text.Json.JsonException)
			{
				// 跳过格式错误的行，而不是丢掉后面的会话内容。
			}
		}

		EnsureTrailingNewline(path);
		return entries;
	}

	/// <summary>
	/// 上次崩溃可能留下没有换行的半行记录：后续追加会把新记录直接拼上去，合成一行非法 JSON，
	/// 导致新旧两条一起被跳过。打开会话时补一个换行把它封口。
	/// </summary>
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

		foreach (var entry in _fileEntries.OfType<SessionEntry>())
		{
			_byId[entry.Id] = entry;
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

	// ---------------------------------------------------------------- 追加

	public string AppendMessage(Message message) => Append(new SessionMessageEntry
	{
		Id = NextId(),
		ParentId = _leafId,
		Timestamp = SessionPaths.ToIso8601(DateTimeOffset.UtcNow),
		Message = message,
	});

	public string AppendModelChange(string provider, string modelId) => Append(new ModelChangeEntry
	{
		Id = NextId(),
		ParentId = _leafId,
		Timestamp = SessionPaths.ToIso8601(DateTimeOffset.UtcNow),
		Provider = provider,
		ModelId = modelId,
	});

	public string AppendThinkingLevelChange(string thinkingLevel) => Append(new ThinkingLevelChangeEntry
	{
		Id = NextId(),
		ParentId = _leafId,
		Timestamp = SessionPaths.ToIso8601(DateTimeOffset.UtcNow),
		ThinkingLevel = thinkingLevel,
	});

	public UsageEntry AppendUsage(string kind, string provider, string model, Usage usage, string? note = null)
	{
		var entry = new UsageEntry
		{
			Id = NextId(),
			ParentId = _leafId,
			Timestamp = SessionPaths.ToIso8601(DateTimeOffset.UtcNow),
			Kind = kind,
			Provider = provider,
			Model = model,
			Usage = usage,
			Note = note,
		};

		Append(entry);
		return entry;
	}

	public string AppendCompaction(
		string summary,
		string firstKeptEntryId,
		long tokensBefore,
		SystemMessage? systemMessage = null,
		Usage? usage = null,
		System.Text.Json.Nodes.JsonNode? details = null,
		bool? fromHook = null) => Append(new CompactionEntry
	{
		Id = NextId(),
		ParentId = _leafId,
		Timestamp = SessionPaths.ToIso8601(DateTimeOffset.UtcNow),
		Summary = summary,
		FirstKeptEntryId = firstKeptEntryId,
		TokensBefore = tokensBefore,
		SystemMessage = systemMessage,
		Usage = usage,
		Details = details,
		FromHook = fromHook,
	});

	public string AppendBranchSummary(
		string fromId,
		string summary,
		Usage? usage = null,
		System.Text.Json.Nodes.JsonNode? details = null) => Append(new BranchSummaryEntry
	{
		Id = NextId(),
		ParentId = _leafId,
		Timestamp = SessionPaths.ToIso8601(DateTimeOffset.UtcNow),
		FromId = fromId,
		Summary = summary,
		Usage = usage,
		Details = details,
	});

	public string AppendCustom(string customType, System.Text.Json.Nodes.JsonNode? data = null) => Append(new CustomEntry
	{
		Id = NextId(),
		ParentId = _leafId,
		Timestamp = SessionPaths.ToIso8601(DateTimeOffset.UtcNow),
		CustomType = customType,
		Data = data,
	});

	/// <summary>追加一条扩展风格的消息，它会参与 LLM 上下文。</summary>
	public string AppendCustomMessage(
		string customType,
		MessageContent content,
		bool display = true,
		System.Text.Json.Nodes.JsonNode? details = null) => Append(new CustomMessageEntry
	{
		Id = NextId(),
		ParentId = _leafId,
		Timestamp = SessionPaths.ToIso8601(DateTimeOffset.UtcNow),
		CustomType = customType,
		Content = content,
		Display = display,
		Details = details,
	});

	public string AppendSessionInfo(string? name) => Append(new SessionInfoEntry
	{
		Id = NextId(),
		ParentId = _leafId,
		Timestamp = SessionPaths.ToIso8601(DateTimeOffset.UtcNow),
		Name = name,
	});

	/// <summary>
	/// 追加一条上下文编辑：<paramref name="replacement"/> 为 null 表示把目标条目从模型上下文中移除，
	/// 有值则替换其内容。pi 的重试就靠它把失败的那次尝试从投影里去掉（原文保留在文件里）。
	/// </summary>
	public string AppendContextEdit(string targetId, MessageContent? replacement)
	{
		if (!_byId.ContainsKey(targetId))
		{
			throw new InvalidOperationException($"Entry {targetId} not found");
		}

		return Append(new ContextEditEntry
		{
			Id = NextId(),
			ParentId = _leafId,
			Timestamp = SessionPaths.ToIso8601(DateTimeOffset.UtcNow),
			TargetId = targetId,
			Replacement = replacement,
		});
	}

	public string AppendLabelChange(string targetId, string? label)
	{
		if (!_byId.ContainsKey(targetId))
		{
			throw new InvalidOperationException($"Entry {targetId} not found");
		}

		var entry = new LabelEntry
		{
			Id = NextId(),
			ParentId = _leafId,
			Timestamp = SessionPaths.ToIso8601(DateTimeOffset.UtcNow),
			TargetId = targetId,
			Label = label,
		};

		Append(entry);
		if (label is { Length: > 0 })
		{
			_labelsById[targetId] = label;
		}
		else
		{
			_labelsById.Remove(targetId);
		}

		return entry.Id;
	}

	private string Append(SessionEntry entry)
	{
		using var scope = _stateGate.EnterScope();
		var extendsBranch = entry.ParentId == _leafId;
		// A normal message only extends the projection. Edits, compaction and summaries can
		// change earlier messages, so those still rebuild it from the branch.
		Invalidate(
			keepIncrementalProjection: extendsBranch &&
				entry is not (ContextEditEntry or CompactionEntry or BranchSummaryEntry or CustomMessageEntry),
			keepBranch: extendsBranch);
		_fileEntries.Add(entry);
		_byId[entry.Id] = entry;
		if (extendsBranch && _branchSnapshot is { } branch)
		{
			// ImmutableList shares the old path; the projection's cached branch stays unchanged.
			_branchSnapshot = branch.Add(entry);
		}

		if (entry is LabelEntry { Label: { Length: > 0 } label } labelEntry)
		{
			_labelsById[labelEntry.TargetId] = label;
		}

		_leafId = entry.Id;
		Persist(entry);
		return entry.Id;
	}

	private void Persist(SessionEntry entry)
	{
		if (!_persist || SessionFile is null)
		{
			return;
		}

		// 文件里没有头部（外部工具写出的会话）时先补一个，别让它一直缺下去：
		// pi 与 PiDeck 都按「首行是 session 头」解析，缺头的文件读不出会话身份。
		// 头部必须落在第一行，所以这里整体重写文件，而不是追加到末尾。
		if (Header is null)
		{
			var header = new SessionHeader
			{
				Id = SessionId,
				Timestamp = SessionPaths.ToIso8601(DateTimeOffset.UtcNow),
				Cwd = Cwd,
			};

			var existing = File.Exists(SessionFile) ? File.ReadAllText(SessionFile) : string.Empty;
			File.WriteAllText(SessionFile, SessionJson.Serialize(header) + "\n" + existing);
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

	// ---------------------------------------------------------------- 导航

	/// <summary>把叶子移回更早的条目，后续追加就会形成新分支。</summary>
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

	/// <summary>允许下一次追加开启一个新的根。</summary>
	public void ResetLeaf()
	{
		using var scope = _stateGate.EnterScope();
		_leafId = null;
		Invalidate();
	}

	/// <summary>从某个条目回溯到根，按根到叶的顺序返回路径。</summary>
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

	/// <summary>供上下文与渲染使用的、感知压缩的条目列表。</summary>
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

		var compactionIndex = -1;
		for (var i = 0; i < path.Count; i++)
		{
			if (path[i].Id == compaction.Id)
			{
				compactionIndex = i;
				break;
			}
		}

		if (compactionIndex < 0)
		{
			return path;
		}

		var contextEntries = new List<SessionEntry> { compaction };
		var foundFirstKept = false;
		for (var i = 0; i < compactionIndex; i++)
		{
			var entry = path[i];
			if (entry.Id == compaction.FirstKeptEntryId)
			{
				foundFirstKept = true;
			}

			if (foundFirstKept && !(entry is SessionMessageEntry { Message: SystemMessage }))
			{
				contextEntries.Add(entry);
			}
		}

		for (var i = compactionIndex + 1; i < path.Count; i++)
		{
			contextEntries.Add(path[i]);
		}

		return contextEntries;
	}

	/// <summary>发给模型的消息，已应用上下文编辑；被编辑移除的条目不会产生任何消息。</summary>
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

	/// <summary>
	/// Appends an entry's projected messages without building a temporary list per entry.
	/// A context edit replaces the content of every message the entry contributes.
	/// </summary>
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
				};
				messages.Add(replacement is null ? customMessage : ReplaceContent(customMessage, replacement.Value));
				break;
		}
	}

	/// <summary>模型可见的上下文，以及从当前分支解析出的设置。</summary>
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

	/// <summary>第一条用户消息的文本，供会话选择器使用。</summary>
	public string FirstUserMessageText()
	{
		using var scope = _stateGate.EnterScope();
		foreach (var entry in _fileEntries.OfType<SessionEntry>())
		{
			if (entry is SessionMessageEntry { Message: UserMessage user } && ContentText(user.Content) is { Length: > 0 } text)
			{
				return text;
			}
		}

		return string.Empty;
	}

	/// <summary>由 <c>/name</c> 设置的显示名（如果有）。</summary>
	public string? SessionName()
	{
		using var scope = _stateGate.EnterScope();
		string? name = null;
		foreach (var entry in _fileEntries.OfType<SessionEntry>())
		{
			if (entry is SessionInfoEntry info)
			{
				name = info.Name;
			}
		}

		return name;
	}

	/// <summary>按目标条目 id 索引的标签。</summary>
	public IReadOnlyDictionary<string, string> Labels => _labelsById;

	public SessionInfo ToSessionInfo()
	{
		using var scope = _stateGate.EnterScope();
		var file = SessionFile ?? string.Empty;
		var created = DateTimeOffset.TryParse(Header?.Timestamp, out var parsed) ? parsed : DateTimeOffset.MinValue;
		var modified = created;
		var messageCount = 0;
		var allText = new System.Text.StringBuilder();

		foreach (var entry in _fileEntries.OfType<SessionEntry>())
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

	// ---------------------------------------------------------------- 列举

	/// <summary>列出目录中的会话文件，最近的在前。</summary>
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

	/// <summary>目录中最近一个会话的路径，没有则为 <c>null</c>。</summary>
	public static string? FindMostRecentSession(string sessionDir, string? cwdFilter = null)
	{
		var sessions = ListSessions(sessionDir, cwdFilter);
		return sessions.Count == 0 ? null : sessions[0].Path;
	}

	// ---------------------------------------------------------------- 转换

	/// <summary>单个条目贡献的上下文消息，与压缩模块共用。</summary>
	public static IReadOnlyList<Message> MessagesForEntry(SessionEntry entry) => ToMessages(entry);

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
