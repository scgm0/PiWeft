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
			var process = new List<ChatEntry>();
			foreach (var entry in unique)
			{
				if (entry.Thinking.Length > 0 || entry.IsThinking)
					process.Add(entry with
					{
						EntryId = $"thinking/{entry.EntryId ?? entry.Timestamp.ToString()}", Text = string.Empty, Images = [], Compact = true,
						// 只有纯思考回复的 DurationMs 才可视为思考耗时；含正文时那是整条回复的耗时。
						DurationMs = entry.Text.Length == 0 && entry.Images.Count == 0 ? entry.DurationMs : null,
					});
				if (ReferenceEquals(entry, answer)) continue;
				if (entry.Role != ChatRole.Assistant || entry.Text.Length > 0 || entry.Images.Count > 0)
					process.Add(entry.Thinking.Length == 0 && !entry.IsThinking
						? entry with { Compact = true }
						: entry with { Thinking = string.Empty, IsThinking = false, Compact = true });
			}
			if (process.Count > 0)
				rows.Add(new ChatEntry
				{
					Role = ChatRole.Process, Text = string.Empty,
					EntryId = $"process/{turnId ?? unique[0].EntryId ?? unique[0].Timestamp.ToString()}",
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

	public static string GroupSummary(IReadOnlyList<ChatEntry> entries)
	{
		List<string>? activities = null;
		HashSet<string>? seen = null;
		foreach (var entry in entries)
		{
			string? activity = entry.Role == ChatRole.Tool ? entry.ToolName switch
			{
				"read" => "已读取文件", "write" or "edit" => "修改了文件", "bash" => "执行了命令",
				"grep" or "find" or "ls" => "搜索了文件", "todo" => "更新了计划", _ => "调用了工具",
			} : entry.Thinking.Length > 0 || entry.IsThinking ? "思考" : null;
			if (activity is null)
			{
				continue;
			}

			activities ??= [];
			seen ??= new HashSet<string>(StringComparer.Ordinal);
			if (seen.Add(activity))
			{
				activities.Add(activity);
			}
		}

		return activities is null ? string.Empty : string.Join("、", activities);
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
