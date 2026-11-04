using System.Text.Json.Nodes;
using PiWeft.Ai;

namespace PiWeft.CodingAgent.Session;

public sealed record SessionTreeNode
{
	public required SessionEntry Entry { get; init; }

	public IReadOnlyList<SessionTreeNode> Children { get; init; } = [];

	public string? Label { get; init; }

	public string? LabelTimestamp { get; init; }
}

public sealed record ForkMessage(string EntryId, string Text);

public sealed partial class SessionManager
{
	public bool IsPersisted => _persist;

	public SessionEntry? GetEntry(string id) => _byId.TryGetValue(id, out var entry) ? entry : null;

	public bool Contains(string id) => _byId.ContainsKey(id);

	public IReadOnlyList<SessionEntry>? EntriesSince(string sinceId)
	{
		var entries = Entries;
		var index = -1;

		for (var i = 0; i < entries.Count; i++)
		{
			if (entries[i].Id == sinceId)
			{
				index = i;
				break;
			}
		}

		if (index < 0)
		{
			return null;
		}

		return [.. entries.Skip(index + 1)];
	}

	/// 会话以条目树的形式呈现。结果是数组，因为一个会话可能有多个根：
	/// 父条目缺失（文件损坏或被裁剪）的条目会各自成为一棵树的根。
	public IReadOnlyList<SessionTreeNode> BuildTree()
	{
		var entries = Entries;
		var childrenByParent = new Dictionary<string, List<SessionEntry>>(StringComparer.Ordinal);
		var roots = new List<SessionEntry>();
		var labelTimestamps = new Dictionary<string, string>(StringComparer.Ordinal);

		foreach (var entry in entries)
		{
			// 墓碑是「编辑重发」截断掉的死分支：不进树，否则每次重发都会多出一排无法打开的分支。
			if (entry is DeletedEntry)
			{
				continue;
			}

			if (entry.ParentId is { Length: > 0 } parentId && _byId.ContainsKey(parentId))
			{
				if (!childrenByParent.TryGetValue(parentId, out var children))
				{
					children = [];
					childrenByParent[parentId] = children;
				}

				children.Add(entry);
			}
			else
			{
				roots.Add(entry);
			}

			if (entry is LabelEntry { Label: { Length: > 0 } } label)
			{
				labelTimestamps[label.TargetId] = label.Timestamp;
			}
		}

		// 用显式栈自底向上构建：长会话的嵌套深度等于条目数，递归遍历会把栈耗尽。
		var built = new Dictionary<string, SessionTreeNode>(StringComparer.Ordinal);
		var stack = new Stack<(SessionEntry Entry, bool ChildrenDone)>();

		foreach (var root in roots)
		{
			stack.Push((root, false));
		}

		while (stack.Count > 0)
		{
			var (entry, childrenDone) = stack.Pop();
			var children = childrenByParent.TryGetValue(entry.Id, out var found) ? found : null;

			if (!childrenDone)
			{
				stack.Push((entry, true));
				if (children is not null)
				{
					foreach (var child in children)
					{
						stack.Push((child, false));
					}
				}

				continue;
			}

			_labelsById.TryGetValue(entry.Id, out var label);
			var hasLabel = !string.IsNullOrEmpty(label);

			built[entry.Id] = new SessionTreeNode
			{
				Entry = entry,
				Children = children is null ? [] : [.. children.Select(child => built[child.Id])],
				Label = hasLabel ? label : null,
				LabelTimestamp = hasLabel && labelTimestamps.TryGetValue(entry.Id, out var timestamp) ? timestamp : null,
			};
		}

		return [.. roots.Select(root => built[root.Id])];
	}

	public IReadOnlyList<ForkMessage> GetUserMessagesForForking()
	{
		var messages = new List<ForkMessage>();
		foreach (var entry in GetBranch())
		{
			if (entry is SessionMessageEntry { Message: UserMessage user })
			{
				messages.Add(new ForkMessage(entry.Id, user.Content.Text ?? string.Empty));
			}
		}

		return messages;
	}

	public string? LastAssistantText()
	{
		foreach (var entry in Enumerable.Reverse(Entries))
		{
			if (entry is not SessionMessageEntry { Message: AssistantMessage assistant })
			{
				continue;
			}

			var text = string.Concat(assistant.Content.OfType<TextContent>().Select(static block => block.Text));
			if (text.Length > 0)
			{
				return text;
			}
		}

		return null;
	}

	public string? CurrentName()
	{
		string? name = null;
		foreach (var entry in Entries)
		{
			if (entry is SessionInfoEntry info)
			{
				name = info.Name;
			}
		}

		return string.IsNullOrEmpty(name) ? null : name;
	}

	/// 在该管理器上开一个新会话：新 id、新文件，并把 <paramref name="parentSession"/> 记为来源。
	/// <c>new_session</c> 以及从空白处派生的场景会用到。
	public string StartNew(string? parentSession = null, DateTimeOffset? now = null)
	{
		using var scope = _stateGate.EnterScope();
		Invalidate();
		var timestamp = SessionPaths.ToIso8601(now ?? DateTimeOffset.UtcNow);
		SessionId = SessionPaths.NewSessionId();

		_fileEntries.Clear();
		_byId.Clear();
		_labelsById.Clear();
		_leafId = null;

		_fileEntries.Add(new SessionHeader
		{
			Id = SessionId,
			Timestamp = timestamp,
			Cwd = Cwd,
			ParentSession = parentSession,
		});

		if (_persist && SessionDir.Length > 0)
		{
			Directory.CreateDirectory(SessionDir);
			SessionFile = Path.Combine(SessionDir, SessionPaths.SessionFileName(timestamp, SessionId));
			File.WriteAllText(SessionFile, SessionJson.Serialize(Header!) + "\n");
		}
		else
		{
			SessionFile = null;
		}

		return SessionId;
	}

	/// 把根到 <paramref name="targetLeafId"/> 的路径写进新的会话文件，并把本管理器切过去，
	/// 后续追加都落在派生分支上。返回新文件路径。
	/// pi 不改动原文件，而是把保留的条目复制进新文件；这也正是客户端对“在此派生”的预期：
	/// 旧会话仍然留在历史里可用。
	public string CreateBranchedSession(string targetLeafId)
	{
		using var scope = _stateGate.EnterScope();
		if (!_byId.ContainsKey(targetLeafId))
		{
			throw new InvalidOperationException($"Entry {targetLeafId} not found");
		}

		if (!_persist || SessionFile is null)
		{
			throw new InvalidOperationException("Cannot branch a session that is not persisted");
		}

		if (_byId[targetLeafId] is DeletedEntry)
		{
			throw new InvalidOperationException($"Entry {targetLeafId} has been deleted");
		}

		var retained = GetBranch(targetLeafId);
		Invalidate();
		var sourceFile = SessionFile;
		var timestamp = SessionPaths.ToIso8601(DateTimeOffset.UtcNow);

		SessionId = SessionPaths.NewSessionId();

		_fileEntries.Clear();
		_byId.Clear();
		_labelsById.Clear();
		_leafId = null;

		_fileEntries.Add(new SessionHeader
		{
			Id = SessionId,
			Timestamp = timestamp,
			Cwd = Cwd,
			ParentSession = sourceFile,
		});

		Directory.CreateDirectory(SessionDir);
		var target = Path.Combine(SessionDir, SessionPaths.SessionFileName(timestamp, SessionId));
		var lines = new List<string> { SessionJson.Serialize(Header!) };

		foreach (var entry in retained)
		{
			_fileEntries.Add(entry);
			_byId[entry.Id] = entry;
			if (entry is not DeletedEntry)
			{
				_leafId = entry.Id;
			}

			if (entry is LabelEntry { Label: { Length: > 0 } label } labelEntry)
			{
				_labelsById[labelEntry.TargetId] = label;
			}
			else if (entry is LabelEntry { Label: null } cleared)
			{
				_labelsById.Remove(cleared.TargetId);
			}

			lines.Add(SessionJson.Serialize(entry));
		}

		File.WriteAllText(target, string.Concat(lines.Select(static line => line + "\n")));
		SessionFile = target;
		return target;
	}

	/// 把 <paramref name="entryId"/> 及其全部后继原地改写成墓碑，并让叶子回到它的父条目。
	/// 「编辑重发」用它：分支真的被截断（旧回答的 tool_call / toolResult 配对不会残留），
	/// 但会话 id 与文件不变，侧栏不会每改一次就多出一条会话。
	/// <returns>被改写的条目数。</returns>
	public int TruncateForResend(string entryId)
	{
		using var scope = _stateGate.EnterScope();
		if (!_byId.TryGetValue(entryId, out var target) || target is DeletedEntry)
		{
			throw new InvalidOperationException($"Entry {entryId} not found");
		}

		// 只处理当前分支上的条目：死分支已经不在投影里，截它没有意义。
		if (!GetBranch().Any(entry => entry.Id == entryId))
		{
			throw new InvalidOperationException($"Entry {entryId} is not on the active branch");
		}

		// 先按 parentId 建一次子索引，再自顶向下收集整棵子树；逐条目扫全表在长会话里是 O(n²)。
		var children = new Dictionary<string, List<string>>(StringComparer.Ordinal);
		foreach (var entry in _fileEntries.OfType<SessionEntry>())
		{
			if (entry is DeletedEntry || entry.ParentId is not { Length: > 0 } parentId)
			{
				continue;
			}

			if (!children.TryGetValue(parentId, out var siblings))
			{
				children[parentId] = siblings = [];
			}

			siblings.Add(entry.Id);
		}

		var doomed = new HashSet<string>(StringComparer.Ordinal) { entryId };
		var pending = new Stack<string>();
		pending.Push(entryId);
		while (pending.Count > 0)
		{
			if (!children.TryGetValue(pending.Pop(), out var descendants))
			{
				continue;
			}

			foreach (var child in descendants)
			{
				if (doomed.Add(child))
				{
					pending.Push(child);
				}
			}
		}

		if (!_persist || SessionFile is null)
		{
			// 内存会话没有文件可留墓碑：直接移除被放弃的条目，投影与树与持久化会话一致。
			var deleted = _fileEntries.RemoveAll(item => item is SessionEntry entry && doomed.Contains(entry.Id));
			if (deleted == 0)
			{
				return 0;
			}

			RebuildIndex();
			_leafId = target.ParentId;
			Invalidate();
			return deleted;
		}

		var timestamp = SessionPaths.ToIso8601(DateTimeOffset.UtcNow);
		var rewritten = new List<object>(_fileEntries.Count);
		var truncated = 0;
		foreach (var item in _fileEntries)
		{
			if (item is SessionEntry entry && doomed.Contains(entry.Id))
			{
				rewritten.Add(new DeletedEntry
				{
					Id = entry.Id,
					ParentId = entry.ParentId,
					Timestamp = timestamp,
					OriginalEntryId = entry.Id,
					Reason = "resend-truncate",
				});
				truncated++;
				continue;
			}

			rewritten.Add(item);
		}

		if (truncated == 0)
		{
			return 0;
		}

		// 先落盘再换内存视图：写失败时抛出去，会话保持原样，避免「内存已截断、文件没变」。
		RewriteFile(rewritten);
		_fileEntries.Clear();
		_fileEntries.AddRange(rewritten);
		RebuildIndex();
		// 之后追加的消息接在被截断条目的父级：墓碑子树成为死分支，从 leaf 回溯永远走不到它。
		_leafId = target.ParentId;
		Invalidate();
		return truncated;
	}

	internal void ReloadFrom(string path)
	{
		using var scope = _stateGate.EnterScope();
		var resolved = Path.GetFullPath(path);
		var entries = ReadFileEntries(resolved);
		var header = entries.OfType<SessionHeader>().FirstOrDefault();

		_fileEntries.Clear();
		_fileEntries.AddRange(entries);
		SessionId = header?.Id ?? SessionPaths.NewSessionId();
		SessionFile = resolved;
		RebuildIndex();
		MigrateLegacyEntries(header, rewriteFile: true);
	}
}
