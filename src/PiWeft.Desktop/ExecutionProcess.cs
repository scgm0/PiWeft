namespace PiWeft.Desktop;

/// <summary>One execution summary per user turn; the trailing answer always stays outside the fold.</summary>
internal static class ExecutionProcess
{
	public static IReadOnlyList<ChatEntry> Project(IReadOnlyList<ChatEntry> entries, bool busy)
	{
		var rows = new List<ChatEntry>();
		var turn = new List<ChatEntry>();
		string? turnId = null;
		foreach (var entry in entries)
		{
			if (entry.Role == ChatRole.User)
			{
				Flush(false);
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
						EntryId = $"thinking/{entry.EntryId ?? entry.Timestamp.ToString()}", Text = string.Empty, Images = [],
					});
				if (ReferenceEquals(entry, answer)) continue;
				if (entry.Role != ChatRole.Assistant || entry.Text.Length > 0 || entry.Images.Count > 0)
					process.Add(entry.Thinking.Length == 0 && !entry.IsThinking ? entry : entry with { Thinking = string.Empty, IsThinking = false });
			}
			if (process.Count > 0)
				rows.Add(new ChatEntry
				{
					Role = ChatRole.Process, Text = string.Empty,
					EntryId = $"process/{turnId ?? unique[0].EntryId ?? unique[0].Timestamp.ToString()}",
					Timestamp = unique[0].Timestamp, ProcessEntries = process, IsStreaming = live,
				});
			if (answer is not null) rows.Add(answer.Thinking.Length == 0 && !answer.IsThinking ? answer : answer with { Thinking = string.Empty, IsThinking = false });
			turn.Clear();
		}
	}

	public static string Summary(IReadOnlyList<ChatEntry> entries)
	{
		var tools = entries.Count(entry => entry.Role == ChatRole.Tool);
		var thinking = entries.Count(entry => entry.Thinking.Length > 0 || entry.IsThinking);
		var interim = entries.Count(entry => entry.Role == ChatRole.Assistant && (entry.Text.Length > 0 || entry.Images.Count > 0));
		var errors = entries.Count(entry => entry.Role == ChatRole.Error || entry.IsError);
		return string.Join("  ·  ", new[]
		{
			tools > 0 ? $"工具 {tools}" : null,
			thinking > 0 ? $"思考 {thinking}" : null,
			interim > 0 ? $"中间回复 {interim}" : null,
			errors > 0 ? $"失败 {errors}" : null,
		}.Where(part => part is not null));
	}

	public static string GroupSummary(IReadOnlyList<ChatEntry> entries)
	{
		var activities = entries.Where(entry => entry.Role == ChatRole.Tool).Select(entry => entry.ToolName switch
		{
			"read" => "已读取文件", "write" or "edit" => "修改了文件", "bash" => "执行了命令",
			"grep" or "find" or "ls" => "搜索了文件", "todo" => "更新了计划", _ => "调用了工具",
		}).Distinct().ToList();
		if (entries.Any(entry => entry.Thinking.Length > 0 || entry.IsThinking)) activities.Add("思考");
		return string.Join("、", activities);
	}
}
