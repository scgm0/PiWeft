using System.Text.Json.Nodes;
using PiWeft.Ai;

namespace PiWeft.CodingAgent.Session;

/// <summary>会话树中的一个节点，即 <c>get_tree</c> 返回的形状。</summary>
public sealed record SessionTreeNode
{
	public required SessionEntry Entry { get; init; }

	public IReadOnlyList<SessionTreeNode> Children { get; init; } = [];

	/// <summary>由 <c>label</c> 条目附在该条目上的标签（如果有）。</summary>
	public string? Label { get; init; }

	public string? LabelTimestamp { get; init; }
}

/// <summary>客户端可以据此派生的用户消息，即 <c>get_fork_messages</c> 返回的形状。</summary>
public sealed record ForkMessage(string EntryId, string Text);

/// <summary>树构建、条目查找，以及 RPC 面所需的各种会话生命周期操作。</summary>
public sealed partial class SessionManager
{
	/// <summary>该会话是否写入文件。</summary>
	public bool IsPersisted => _persist;

	/// <summary>按 id 查找条目。</summary>
	public SessionEntry? GetEntry(string id) => _byId.TryGetValue(id, out var entry) ? entry : null;

	/// <summary>会话中是否存在该 id 的条目。</summary>
	public bool Contains(string id) => _byId.ContainsKey(id);

	/// <summary><paramref name="sinceId"/> 之后的全部条目（不含自身），按文件顺序；id 未知时返回 <c>null</c>。</summary>
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

	/// <summary>
	/// 会话以条目树的形式呈现。结果是数组，因为一个会话可能有多个根：
	/// 父条目缺失（文件损坏或被裁剪）的条目会各自成为一棵树的根。
	/// </summary>
	public IReadOnlyList<SessionTreeNode> BuildTree()
	{
		var entries = Entries;
		var childrenByParent = new Dictionary<string, List<SessionEntry>>(StringComparer.Ordinal);
		var roots = new List<SessionEntry>();
		var labelTimestamps = new Dictionary<string, string>(StringComparer.Ordinal);

		foreach (var entry in entries)
		{
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

	/// <summary>当前分支上的用户消息，按时间从早到晚，供派生选择器使用。</summary>
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

	/// <summary>最后一条带文本的助手消息内容，没有则为 <c>null</c>。</summary>
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

	/// <summary>当前显示名，取自最新的 <c>session_info</c> 条目；没有则为 <c>null</c>。</summary>
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

	/// <summary>
	/// 在该管理器上开一个新会话：新 id、新文件，并把 <paramref name="parentSession"/> 记为来源。
	/// <c>new_session</c> 以及从空白处派生的场景会用到。
	/// </summary>
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

	/// <summary>
	/// 把根到 <paramref name="targetLeafId"/> 的路径写进新的会话文件，并把本管理器切过去，
	/// 后续追加都落在派生分支上。返回新文件路径。
	/// </summary>
	/// <remarks>
	/// pi 不改动原文件，而是把保留的条目复制进新文件；这也正是客户端对“在此派生”的预期：
	/// 旧会话仍然留在历史里可用。
	/// </remarks>
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
			_leafId = entry.Id;

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

	/// <summary>从文件重建本管理器的视图，复用同一实例，便于调用方继续持有引用。</summary>
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
		MigrateLegacyEntries(rewriteFile: true);
	}
}
