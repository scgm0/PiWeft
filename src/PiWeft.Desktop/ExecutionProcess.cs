using System.Text.Json;

namespace PiWeft.Desktop;

internal static class ExecutionProcess
{
	public static IReadOnlyList<ChatEntry> Project(IReadOnlyList<ChatEntry> entries, bool busy) =>
		ProjectRange(entries, 0, busy, out _);

	/// Projects from <paramref name="start"/> and reports where the last user turn begins in the result.
	/// Turn boundaries are independent, so a streaming tail can be re-projected without rebuilding the
	/// historical prefix.
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
			else if (entry.Role == ChatRole.Notice)
			{
				// 继续生成的收据自己开一轮：前面失败的那一轮要保独立，
				// 否则它的半截回答会被挤进下一轮的过程组。
				Flush(false);
				lastTurnStart = rows.Count;
				rows.Add(entry);
				turnId = entry.EntryId ?? $"notice/{rows.Count}/{entry.Timestamp}";
			}
			else turn.Add(entry);
		}
		Flush(busy);
		return rows;

		void Flush(bool live)
		{
			if (turn.Count == 0) return;
			// Most turns contain no tool calls; allocating a dictionary and a second list for
			// every tail projection was measurable GC churn during streaming. Compact the
			// already-owned turn list in place instead.
			Dictionary<string, int>? tools = null;
			int uniqueCount = 0;
			for (int read = 0; read < turn.Count; read++)
			{
				var entry = turn[read];
				if (entry.Role == ChatRole.Tool && entry.ToolCallId is { Length: > 0 } call)
				{
					if (tools is not null && tools.TryGetValue(call, out var index))
					{
						// Result replaces the pending call in place, retaining the group's identity.
						turn[index] = entry with
						{
							EntryId = turn[index].EntryId,
							ToolArguments = entry.ToolArguments ?? turn[index].ToolArguments ?? turn[index].Text,
						};
						continue;
					}
					(tools ??= new Dictionary<string, int>(StringComparer.Ordinal))[call] = uniqueCount;
				}
				turn[uniqueCount++] = entry;
			}
			if (uniqueCount < turn.Count)
			{
				turn.RemoveRange(uniqueCount, turn.Count - uniqueCount);
			}
			var unique = turn;
			var last = unique[^1];
			var answer = last.Role == ChatRole.Assistant && (last.TextLength > 0 || last.Images.Count > 0) ? last : null;
			var turnKey = turnId ?? unique[0].EntryId ?? unique[0].Timestamp.ToString();
			// 思考步骤的键必须跨「流式行 → 落盘后的最终行」稳定：源 EntryId 会从
			// live-assistant/... 变成会话条目 id，用轮次 + 序号代替。否则组键/步骤键
			// 在思考结束的瞬间变化，虚拟化行被重建、展开的思考也随之折叠。
			var thinkingSequence = 0;
			var process = new List<ChatEntry>(unique.Count);
			double? answerDuration = null;
			foreach (var entry in unique)
			{
				if (entry.Role == ChatRole.Assistant) answerDuration = entry.DurationMs;
				if (entry.ThinkingLength > 0 || entry.IsThinking)
				{
					var step = entry with
					{
						EntryId = $"thinking/{turnKey}/{thinkingSequence++}", Text = string.Empty, Images = [], Compact = true,
						// 独立的 thinking_start/end 跨度优先（有正文的组合消息也用它）；
						// 历史会话没有这份事实时才退回整条回复的 DurationMs，且只在纯思考时可信。
						DurationMs = entry.ThinkingDurationMs is { } thinkingMs
							? thinkingMs
							: entry.TextLength == 0 && entry.Images.Count == 0 ? entry.DurationMs : null,
					};
					// 已收口的轮次不可能还在思考：中途插入的用户消息会把上一轮按 non-live 投影，
					// 留下 live 快照就会带着「正在思考…」和秒表被缓存进前缀，之后没人再更新这一行。
					if (!live && (step.IsThinking || step.IsStreaming))
						step = step with { IsThinking = false, IsStreaming = false };
					process.Add(step);
				}
				if (ReferenceEquals(entry, answer)) continue;
				if (entry.Role != ChatRole.Assistant || entry.TextLength > 0 || entry.Images.Count > 0)
				{
					// 组内步骤不显示轮末 chip（Compact）：清掉改动列表，省下每帧的补丁字符串比较。
					var item = entry.ThinkingLength == 0 && !entry.IsThinking
						? entry with { Compact = true, ChangedFiles = [] }
						: entry with { Thinking = string.Empty, IsThinking = false, Compact = true, ChangedFiles = [] };
					if (!live && item.IsStreaming)
						item = item.Role == ChatRole.Tool
							? item with { IsStreaming = false, IsStopped = true }
							: item with { IsStreaming = false };
					// 工具步骤用调用 id 作键，不用消息条目 id：流式消息落盘后
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
					DurationMs = answerDuration,
				});
			if (answer is not null)
			{
				var finalAnswer = answer.ThinkingLength == 0 && !answer.IsThinking
					? answer
					: answer with { Thinking = string.Empty, IsThinking = false };
				// 收口后的轮次不再显示「正在回复…」：live 快照被按 non-live 投影时也要落定。
				if (!live && finalAnswer.IsStreaming) finalAnswer = finalAnswer with { IsStreaming = false };
				rows.Add(finalAnswer);
			}
			turn.Clear();
		}
	}

	public readonly record struct Stats(int Tools, int Thinking, int Interim, int Errors, int Stopped);

	public static Stats Count(IReadOnlyList<ChatEntry> entries)
	{
		var tools = 0;
		var thinking = 0;
		var interim = 0;
		var errors = 0;
		var stopped = 0;
		// 索引循环：IReadOnlyList<T> 的 foreach 每帧都要分配一个接口枚举器。
		for (var index = 0; index < entries.Count; index++)
		{
			var entry = entries[index];
			if (entry.Role == ChatRole.Tool)
			{
				tools++;
			}

			if (entry.ThinkingLength > 0 || entry.IsThinking)
			{
				thinking++;
			}

			if (entry.Role == ChatRole.Assistant && (entry.TextLength > 0 || entry.Images.Count > 0))
			{
				interim++;
			}

			if (entry.Role == ChatRole.Error || entry.IsError)
			{
				errors++;
			}

			if (entry.IsStopped)
			{
				stopped++;
			}
		}

		return new Stats(tools, thinking, interim, errors, stopped);
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

	/// 组头结束态摘要：只列工具类别（PiDeck 同款，思考不计入；纯思考组由调用方给「已完成分析」）。
	/// 类别超过三个时收成「… 等」，否则组头会跟着工具种类无限变长。
	public static string GroupSummary(IReadOnlyList<ChatEntry> entries)
	{
		// 类别只有 8 种：用位掩码去重、栈上存下标，不再为每次组头变化分配 List + HashSet + string.Join。
		Span<int> picked = stackalloc int[3];
		var count = 0;
		var seen = 0;
		var truncated = false;
		for (var index = 0; index < entries.Count; index++)
		{
			var entry = entries[index];
			if (entry.Role != ChatRole.Tool)
			{
				continue;
			}

			var kind = entry.ToolName switch
			{
				"read" => 0, "write" => 1, "edit" => 2, "bash" or "terminal" or "powershell" => 3,
				"grep" or "find" or "ls" => 4, "todo" => 5, "ask_question" => 6, _ => 7,
			};
			var bit = 1 << kind;
			if ((seen & bit) != 0) continue;
			seen |= bit;
			if (count < 3) picked[count++] = kind;
			else truncated = true;
		}

		if (count == 0) return string.Empty;
		// 最多三项，直接拼：类别顺序仍是首次出现的顺序。
		var summary = count switch
		{
			1 => Activities[picked[0]],
			2 => $"{Activities[picked[0]]}、{Activities[picked[1]]}",
			_ => $"{Activities[picked[0]]}、{Activities[picked[1]]}、{Activities[picked[2]]}",
		};
		return truncated ? Strings.ProcessGroupMore(summary) : summary;
	}

	// 与上面的 switch 分支一一对应（下标即类别号）。
	private static readonly string[] Activities =
	[
		"已读取文件", "已写入文件", "修改了文件", "执行了命令",
		"已搜索代码", "更新了计划", "已向你提问", "已调用工具",
	];

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
		"bash" or "terminal" or "powershell" => Strings.ProcessGroupCommands,
		"grep" or "find" or "ls" => Strings.ProcessGroupSearch,
		"todo" => Strings.ProcessGroupTodo,
		"ask_question" => Strings.ProcessGroupAsk,
		_ => Strings.ProcessGroupTools,
	};

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
		"bash" or "terminal" or "powershell" => "terminal",
		"grep" or "find" or "ls" => "search",
		"todo" => "list-checks",
		"ask_question" => "circle-question-mark",
		_ => "play",
	};

	public static string? RunningDetail(IReadOnlyList<ChatEntry> entries)
	{
		if (entries.Count == 0 || entries[^1] is not { Role: ChatRole.Tool } tool)
		{
			return null;
		}

		// ask_question 的正文是 JSON 题包，直接把第一题原文放到组头；读不到就只留「正在等待回答」。
		if (tool.ToolName is "ask_question")
		{
			return AskQuestionPreview(tool);
		}

		return ToolAction(tool, running: true);
	}

	private static string? AskQuestionPreview(ChatEntry entry)
	{
		if (entry.ToolArguments is not { Length: > 0 } arguments)
		{
			return null;
		}

		JsonDocument document;
		try
		{
			document = JsonDocument.Parse(arguments);
		}
		catch (Exception error) when (error is JsonException or ArgumentException)
		{
			return null;
		}

		using (document)
		{
			var root = document.RootElement;
			if (root.ValueKind != JsonValueKind.Object)
			{
				return null;
			}

			if (root.TryGetProperty("question", out var single) && single.ValueKind == JsonValueKind.String &&
				single.GetString() is { Length: > 0 } text)
			{
				return Strings.ToolDetail(text);
			}

			if (root.TryGetProperty("questions", out var questions) && questions.ValueKind == JsonValueKind.Array)
			{
				foreach (var item in questions.EnumerateArray())
				{
					if (item.ValueKind != JsonValueKind.Object ||
						!item.TryGetProperty("question", out var value) || value.ValueKind != JsonValueKind.String ||
						value.GetString() is not { Length: > 0 } question)
					{
						continue;
					}

					return Strings.ToolDetail(question);
				}
			}
		}

		return null;
	}

	public static string ToolAction(ChatEntry entry, bool running)
	{
		// ask_question 的参数是题包 JSON，行内只给第一题原文（读不到就不占位）。
		if (entry.ToolName is "ask_question")
		{
			return AskQuestionPreview(entry) ?? string.Empty;
		}

		if (entry.ToolName is "todo")
		{
			return ToolCallDetails.TodoPreview(entry.ToolArguments) ??
				Strings.ToolDetail(entry.ToolArguments is { Length: > 0 } todoArguments ? todoArguments : entry.Text);
		}

		var verb = entry.ToolName switch
		{
			"bash" or "terminal" or "powershell" => Strings.ToolActionExecute,
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

	private static string DetailPreview(ChatEntry entry)
	{
		if (entry.ToolArguments is not { Length: > 0 } arguments)
		{
			return Strings.ToolDetail(entry.Text);
		}

		if (entry.ToolName is "bash" or "terminal" or "powershell" && JsonField(arguments, "command") is { } command)
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

	/// 形参用 span，不用数组：这条路径在一次投影里会被每个工具条目调用一次，
	/// <c>params string[]</c> 每次调用都要分配一个小数组，而实参全是内联字面量。
	private static string? JsonField(string arguments, params ReadOnlySpan<string> names)
	{
		JsonDocument document;
		try
		{
			document = JsonDocument.Parse(arguments);
		}
		catch (Exception error) when (error is JsonException or ArgumentException)
		{
			return null;
		}

		using (document)
		{
			var root = document.RootElement;
			if (root.ValueKind != JsonValueKind.Object)
			{
				return null;
			}

			foreach (var name in names)
			{
				if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
					value.GetString() is { Length: > 0 } text)
				{
					return text;
				}
			}
		}

		return null;
	}
}

/// Keeps the projected history across streaming frames: only the last user turn is projected again
/// while its tail changes. The static <see cref="ExecutionProcess.Project"/> still does a full pass.
internal sealed class ExecutionProcessProjector
{
	private ChatEntry[] _source = [];
	private ChatEntry[] _projected = [];
	private int _lastBoundarySourceIndex = -1;
	private int _prefixRows;
	private bool _busy;

	public int LastStablePrefix { get; private set; }

	public bool LastPrefixReused { get; private set; }

	public long TailPasses { get; private set; }

	public long FullPasses { get; private set; }

	public IReadOnlyList<ChatEntry> Project(IReadOnlyList<ChatEntry> entries, bool busy)
	{
		var lastBoundary = LastBoundaryIndex(entries);
		// 缓存能担保的前缀是「上一次投影时的最后一个轮次边界」之前的行：那些轮次早已定稿，
		// 投影结果与 busy 无关。因此 busy 翻转（回合开始/结束）与新边界都只需要从旧的
		// 边界重投尾部。长会话上一次全量要一百多毫秒，正好落在每回合的起止帧上。
		if (lastBoundary >= 0 && _lastBoundarySourceIndex >= 0 && lastBoundary >= _lastBoundarySourceIndex &&
			PrefixEquals(entries, _source, _lastBoundarySourceIndex))
		{
			// Turn boundaries are independent, so the rows of every earlier turn are still valid.
			TailPasses++;
			var tail = ExecutionProcess.ProjectRange(entries, _lastBoundarySourceIndex, busy, out var lastTurnStart);
			var rows = new ChatEntry[_prefixRows + tail.Count];
			_projected.AsSpan(0, _prefixRows).CopyTo(rows);
			tail.CopyTo(rows.AsSpan(_prefixRows));
			_projected = rows;
			// 消费端（时间线行表）手里还是上一帧的行。这一帧若把刚收口的一轮从尾部移进前缀，
			// 那些行是本次重新投影的，未必与上一帧同引用；只能先担保旧前缀，下一帧再担保整段。
			// 否则行表会跳过它们、继续用上一帧的流式快照，思考/工具秒表就停不下来。
			var stablePrefix = _prefixRows;
			if (lastBoundary != _lastBoundarySourceIndex)
			{
				// 新一轮开始：前一次的流式尾部随本轮定稿，从现在起由新边界担保。
				// lastTurnStart 是相对本次重投起点的行号，转成绝对前缀长度要加上旧前缀。
				_source = [.. entries];
				_prefixRows += lastTurnStart;
				_lastBoundarySourceIndex = lastBoundary;
			}
			_busy = busy;
			LastStablePrefix = stablePrefix;
			LastPrefixReused = true;
			return rows;
		}

		FullPasses++;
		var full = ExecutionProcess.ProjectRange(entries, 0, busy, out var lastTurnStartFull);
		_source = [.. entries];
		_projected = [.. full];
		_lastBoundarySourceIndex = lastBoundary;
		_prefixRows = lastBoundary >= 0 ? lastTurnStartFull : 0;
		_busy = busy;
		LastStablePrefix = _prefixRows;
		LastPrefixReused = false;
		return _projected;
	}

	/// 尾投的起点必须和 <see cref="ExecutionProcess.ProjectRange"/> 认的轮次边界一致：
	/// 只认用户消息的话，继续生成的收据会把前缀盖到边界之后，尾投再把同一段铺一遍。
	private static int LastBoundaryIndex(IReadOnlyList<ChatEntry> entries)
	{
		for (var index = entries.Count - 1; index >= 0; index--)
		{
			if (entries[index].Role is ChatRole.User or ChatRole.Notice)
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
