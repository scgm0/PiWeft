using System.Text.Json.Nodes;

namespace PiWeft.Desktop;

/// <summary>One execution summary per user turn; the trailing answer always stays outside the fold.</summary>
internal static class ExecutionProcess
{
	public static IReadOnlyList<ChatEntry> Project(IReadOnlyList<ChatEntry> entries, bool busy) =>
		ProjectRange(entries, 0, busy, out _);

	/// <summary>
	/// Projects from <paramref name="start"/> and reports where the last user turn begins in the result.
	/// Turn boundaries are independent, so a streaming tail can be re-projected without rebuilding the
	/// historical prefix.
	/// </summary>
	internal static List<ChatEntry> ProjectRange(
		IReadOnlyList<ChatEntry> entries,
		int start,
		bool busy,
		out int lastTurnStart)
	{
		var rows = new List<ChatEntry>();
		var turn = new List<ChatEntry>();
		string? turnId = null;
		lastTurnStart = 0;
		for (var index = start; index < entries.Count; index++)
		{
			var entry = entries[index];
			if (entry.Role == ChatRole.User)
			{
				Flush(false);
				lastTurnStart = rows.Count;
				rows.Add(entry);
				turnId = entry.EntryId ?? $"user/{rows.Count}/{entry.Timestamp}";
			}
			else turn.Add(entry);
		}
		Flush(busy);
		return rows;

		void Flush(bool live)
		{
			if (turn.Count == 0) return;
			var tools = new Dictionary<string, int>();
			var unique = new List<ChatEntry>();
			foreach (var entry in turn)
			{
				if (entry.Role == ChatRole.Tool && entry.ToolCallId is { Length: > 0 } call)
				{
					if (tools.TryGetValue(call, out var index))
					{
						// Result replaces the pending call in place, retaining the group's identity.
						unique[index] = entry with
						{
							EntryId = unique[index].EntryId,
							ToolArguments = entry.ToolArguments ?? unique[index].ToolArguments ?? unique[index].Text,
						};
						continue;
					}
					tools[call] = unique.Count;
				}
				unique.Add(entry);
			}
			var last = unique[^1];
			var answer = last.Role == ChatRole.Assistant && (last.Text.Length > 0 || last.Images.Count > 0) ? last : null;
			var turnKey = turnId ?? unique[0].EntryId ?? unique[0].Timestamp.ToString();
			// 思考步骤的键必须跨「流式行 → 落盘后的最终行」稳定：源 EntryId 会从
			// live-assistant/... 变成会话条目 id，用轮次 + 序号代替。否则组键/步骤键
			// 在思考结束的瞬间变化，虚拟化行被重建、展开的思考也随之折叠。
			var thinkingSequence = 0;
			var process = new List<ChatEntry>();
			foreach (var entry in unique)
			{
				if (entry.Thinking.Length > 0 || entry.IsThinking)
					process.Add(entry with
					{
						EntryId = $"thinking/{turnKey}/{thinkingSequence++}", Text = string.Empty, Images = [], Compact = true,
						// 只有纯思考回复的 DurationMs 才可视为思考耗时；含正文时那是整条回复的耗时。
						DurationMs = entry.Text.Length == 0 && entry.Images.Count == 0 ? entry.DurationMs : null,
					});
				if (ReferenceEquals(entry, answer)) continue;
				if (entry.Role != ChatRole.Assistant || entry.Text.Length > 0 || entry.Images.Count > 0)
				{
					var item = entry.Thinking.Length == 0 && !entry.IsThinking
						? entry with { Compact = true }
						: entry with { Thinking = string.Empty, IsThinking = false, Compact = true };
					if (!live && item.Role == ChatRole.Tool && item.IsStreaming)
						item = item with { IsStreaming = false, IsStopped = true };
					// 工具步骤也用调用 id 而不是消息条目 id 作键：流式消息落盘后
					// ChatViewModel 会换掉行 EntryId，否则组键/展开态会跟着重建。
					if (item.Role == ChatRole.Tool && item.ToolCallId is { Length: > 0 } toolCallId)
						item = item with { EntryId = $"tool/{toolCallId}" };
					process.Add(item);
				}
			}
			if (process.Count > 0)
				rows.Add(new ChatEntry
				{
					Role = ChatRole.Process, Text = string.Empty,
					EntryId = $"process/{turnKey}",
					Timestamp = unique[0].Timestamp, ProcessEntries = process, IsStreaming = live,
					// 总耗时取自本轮最终回复；思考/工具行本身没有独立的墙钟时间。
					DurationMs = unique.LastOrDefault(static entry => entry.Role == ChatRole.Assistant)?.DurationMs,
				});
			if (answer is not null) rows.Add(answer.Thinking.Length == 0 && !answer.IsThinking ? answer : answer with { Thinking = string.Empty, IsThinking = false });
			turn.Clear();
		}
	}

	public readonly record struct Stats(int Tools, int Thinking, int Interim, int Errors);

	public static Stats Count(IReadOnlyList<ChatEntry> entries)
	{
		var tools = 0;
		var thinking = 0;
		var interim = 0;
		var errors = 0;
		foreach (var entry in entries)
		{
			if (entry.Role == ChatRole.Tool)
			{
				tools++;
			}

			if (entry.Thinking.Length > 0 || entry.IsThinking)
			{
				thinking++;
			}

			if (entry.Role == ChatRole.Assistant && (entry.Text.Length > 0 || entry.Images.Count > 0))
			{
				interim++;
			}

			if (entry.Role == ChatRole.Error || entry.IsError)
			{
				errors++;
			}
		}

		return new Stats(tools, thinking, interim, errors);
	}

	public static string Summary(IReadOnlyList<ChatEntry> entries)
	{
		var stats = Count(entries);
		if (stats.Tools + stats.Thinking + stats.Interim + stats.Errors == 0)
		{
			return string.Empty;
		}

		var builder = new System.Text.StringBuilder();
		Append("工具", stats.Tools);
		Append("思考", stats.Thinking);
		Append("中间回复", stats.Interim);
		Append("失败", stats.Errors);
		return builder.ToString();

		void Append(string label, int count)
		{
			if (count == 0)
			{
				return;
			}

			if (builder.Length > 0)
			{
				builder.Append("  ·  ");
			}

			builder.Append(label).Append(' ').Append(count);
		}
	}

	/// <summary>
	/// 组头结束态摘要：只列工具类别（PiDeck 同款，思考不计入；纯思考组由调用方给「已完成分析」）。
	/// 类别超过三个时收成「… 等」，否则组头会跟着工具种类无限变长。
	/// </summary>
	public static string GroupSummary(IReadOnlyList<ChatEntry> entries)
	{
		List<string>? activities = null;
		HashSet<string>? seen = null;
		var truncated = false;
		foreach (var entry in entries)
		{
			if (entry.Role != ChatRole.Tool)
			{
				continue;
			}

			var activity = entry.ToolName switch
			{
				"read" => "已读取文件", "write" => "已写入文件", "edit" => "修改了文件", "bash" => "执行了命令",
				"grep" or "find" or "ls" => "已搜索代码", "todo" => "更新了计划", _ => "已调用工具",
			};
			activities ??= [];
			seen ??= new HashSet<string>(StringComparer.Ordinal);
			if (!seen.Add(activity)) continue;
			if (activities.Count < 3) activities.Add(activity);
			else truncated = true;
		}

		if (activities is null) return string.Empty;
		var summary = string.Join("、", activities);
		return truncated ? Strings.ProcessGroupMore(summary) : summary;
	}

	/// <summary>组头运行态文案：按最新工具类别给「正在…」，纯思考组退回「正在分析请求」。</summary>
	public static string RunningLabel(IReadOnlyList<ChatEntry> entries)
	{
		for (var index = entries.Count - 1; index >= 0; index--)
		{
			if (entries[index].Role == ChatRole.Tool)
			{
				return RunningLabelFor(entries[index].ToolName);
			}
		}

		return Strings.ProcessGroupAnalyzing;
	}

	private static string RunningLabelFor(string? toolName) => toolName switch
	{
		"read" => Strings.ProcessGroupRead,
		"write" => Strings.ProcessGroupWrite,
		"edit" => Strings.ProcessGroupEdit,
		"bash" or "terminal" => Strings.ProcessGroupCommands,
		"grep" or "find" or "ls" => Strings.ProcessGroupSearch,
		"todo" => Strings.ProcessGroupTodo,
		_ => Strings.ProcessGroupTools,
	};

	/// <summary>组头类别图标：与 <see cref="RunningLabel"/> 同源。</summary>
	public static string GroupIcon(IReadOnlyList<ChatEntry> entries)
	{
		for (var index = entries.Count - 1; index >= 0; index--)
		{
			if (entries[index].Role == ChatRole.Tool)
			{
				return GroupIconFor(entries[index].ToolName);
			}
		}

		return "sparkles";
	}

	private static string GroupIconFor(string? toolName) => toolName switch
	{
		"read" => "file-text",
		"write" or "edit" => "square-pen",
		"bash" or "terminal" => "terminal",
		"grep" or "find" or "ls" => "search",
		"todo" => "list-checks",
		_ => "play",
	};

	/// <summary>组头实时详情：最新成员是工具时给「正在执行 …」，切回思考时消失（PiDeck 同款）。</summary>
	public static string? RunningDetail(IReadOnlyList<ChatEntry> entries) =>
		entries.Count > 0 && entries[^1] is { Role: ChatRole.Tool } tool ? ToolAction(tool, running: true) : null;

	/// <summary>工具行动词短语：「正在执行 cd …」/「读取 src/App.cs」；未知工具保留原摘要。</summary>
	public static string ToolAction(ChatEntry entry, bool running)
	{
		var verb = entry.ToolName switch
		{
			"bash" or "terminal" => Strings.ToolActionExecute,
			"read" => Strings.ToolActionRead,
			"write" or "edit" => Strings.ToolActionEdit,
			"grep" or "find" or "ls" => Strings.ToolActionSearch,
			_ => null,
		};
		if (verb is null)
		{
			return Strings.ToolDetail(entry.ToolArguments is { Length: > 0 } arguments ? arguments : entry.Text);
		}

		return Strings.ToolAction(verb, DetailPreview(entry), running);
	}

	/// <summary>行内摘要：bash 取命令，文件类工具取路径，其余退回参数/结果摘要。</summary>
	private static string DetailPreview(ChatEntry entry)
	{
		if (entry.ToolArguments is not { Length: > 0 } arguments)
		{
			return Strings.ToolDetail(entry.Text);
		}

		if (entry.ToolName is "bash" or "terminal" && JsonField(arguments, "command") is { } command)
		{
			return Strings.ToolDetail(command);
		}

		if (entry.ToolName is "read" or "write" or "edit" or "grep" or "find" or "ls")
		{
			if (JsonField(arguments, "path", "file_path", "filePath", "pattern") is { } target)
			{
				return Strings.ToolDetail(target);
			}
		}

		return Strings.ToolDetail(arguments);
	}

	/// <summary>从工具参数 JSON 里取第一个存在的字符串字段。</summary>
	private static string? JsonField(string arguments, params string[] names)
	{
		try
		{
			if (JsonNode.Parse(arguments) is JsonObject root)
			{
				foreach (var name in names)
				{
					if (root[name]?.GetValue<string>() is { Length: > 0 } value)
					{
						return value;
					}
				}
			}
		}
		catch (Exception error) when (error is System.Text.Json.JsonException or InvalidOperationException or FormatException)
		{
		}

		return null;
	}
}

/// <summary>
/// Keeps the projected history across streaming frames: only the last user turn is projected again
/// while its tail changes. The static <see cref="ExecutionProcess.Project"/> still does a full pass.
/// </summary>
internal sealed class ExecutionProcessProjector
{
	private ChatEntry[] _source = [];
	private ChatEntry[] _projected = [];
	private int _lastUserSourceIndex = -1;
	private int _prefixRows;
	private bool _busy;

	/// <summary>Rows before the last user turn that the previous projection can vouch for.</summary>
	public int LastStablePrefix { get; private set; }

	/// <summary>Whether those rows are reference-identical to the previous projection.</summary>
	public bool LastPrefixReused { get; private set; }

	public IReadOnlyList<ChatEntry> Project(IReadOnlyList<ChatEntry> entries, bool busy)
	{
		var lastUser = LastUserIndex(entries);
		if (lastUser >= 0 && lastUser == _lastUserSourceIndex && busy == _busy &&
			PrefixEquals(entries, _source, lastUser))
		{
			// Turn boundaries are independent, so the rows of every earlier turn are still valid.
			var tail = ExecutionProcess.ProjectRange(entries, lastUser, busy, out _);
			var rows = new ChatEntry[_prefixRows + tail.Count];
			_projected.AsSpan(0, _prefixRows).CopyTo(rows);
			tail.CopyTo(rows.AsSpan(_prefixRows));
			_projected = rows;
			LastStablePrefix = _prefixRows;
			LastPrefixReused = true;
			return rows;
		}

		var full = ExecutionProcess.ProjectRange(entries, 0, busy, out var lastTurnStart);
		_source = [.. entries];
		_projected = [.. full];
		_lastUserSourceIndex = lastUser;
		_prefixRows = lastUser >= 0 ? lastTurnStart : 0;
		_busy = busy;
		LastStablePrefix = _prefixRows;
		LastPrefixReused = false;
		return _projected;
	}

	private static int LastUserIndex(IReadOnlyList<ChatEntry> entries)
	{
		for (var index = entries.Count - 1; index >= 0; index--)
		{
			if (entries[index].Role == ChatRole.User)
			{
				return index;
			}
		}

		return -1;
	}

	private static bool PrefixEquals(IReadOnlyList<ChatEntry> entries, ChatEntry[] source, int count)
	{
		if (source.Length < count || entries.Count < count)
		{
			return false;
		}

		for (var index = 0; index < count; index++)
		{
			if (!ReferenceEquals(entries[index], source[index]))
			{
				return false;
			}
		}

		return true;
	}
}
