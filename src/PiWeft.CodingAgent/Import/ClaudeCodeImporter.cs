using System.Text.Json;
using System.Text.Json.Nodes;
using PiWeft.Ai;
using PiWeft.CodingAgent.Session;

namespace PiWeft.CodingAgent.Import;

/// <summary>
/// Claude Code 会话导入（<c>~/.claude/projects/&lt;项目 slug&gt;/&lt;sessionId&gt;.jsonl</c>）。
/// <para>
/// 每行一条记录：<c>{"type":"user"|"assistant"|...,"message":{...},"cwd":..,"sessionId":..,"timestamp":..}</c>。
/// <c>message.content</c> 是块数组：<c>text</c> / <c>thinking</c> / <c>tool_use</c> / <c>tool_result</c>。
/// </para>
/// <para>
/// 跳过 <c>isSidechain</c>（子代理的旁支 transcript）与 <c>isMeta</c>（宿主注入的元信息，不是对话）。
/// <c>tool_result</c> 块写在 user 行里，必须还原成 pi 的 <c>toolResult</c> 消息 ——
/// 当成用户文本会让模型看到「工具输出」的原始 JSON，也会破坏 toolCall ↔ toolResult 的配对。
/// </para>
/// </summary>
internal sealed class ClaudeCodeImporter : IExternalSessionImporter
{
	public string Key => "claude";

	public string Label => "Claude Code";

	public string ResolveRoot(string home) => Path.Combine(home, ".claude", "projects");

	/// <summary>形如 <c>projects/&lt;slug&gt;/&lt;id&gt;.jsonl</c>；子目录也扫（有的版本按日期分层）。</summary>
	public IReadOnlyList<string> Enumerate(string root)
	{
		var files = new List<string>();
		try
		{
			foreach (var path in Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories))
			{
				files.Add(path);
				if (files.Count >= SessionImportService.MaxSessions)
				{
					break;
				}
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return files;
		}

		return files;
	}

	public ImportedSession? Describe(string path)
	{
		var info = new FileInfo(path);
		var head = SessionImportService.ReadHeadObjects(path);

		string? sessionId = null;
		string? cwd = null;
		string? title = null;
		DateTimeOffset? first = null;
		DateTimeOffset last = default;

		foreach (var record in head)
		{
			// 子代理旁支与宿主注入的元信息都不是这条对话的一部分。
			if (SessionImportService.IsTrue(record["isSidechain"]) || SessionImportService.IsTrue(record["isMeta"]))
			{
				continue;
			}

			sessionId ??= SessionImportService.Text(record, "sessionId");
			cwd ??= SessionImportService.Text(record, "cwd");

			if (SessionImportService.ParseTimestamp(record["timestamp"]) is { } timestamp)
			{
				first ??= timestamp;
				if (timestamp > last)
				{
					last = timestamp;
				}
			}

			// 只在真的取到文本时才定标题：空串会把后续更好的候选挡掉。
			if (title is null &&
				SessionImportService.Text(record, "type") == "user" &&
				SessionImportService.TitleFromText(UserText(record)) is { Length: > 0 } candidate)
			{
				title = candidate;
			}
		}

		// 头部没有元数据就不进列表：真实导入会全量读，但列表里放一条点不开的行没有意义。
		cwd ??= CwdFromProjectSlug(path);
		if (sessionId is null || cwd is null)
		{
			return null;
		}

		return new ImportedSession
		{
			Source = Key,
			SourceLabel = Label,
			SessionId = sessionId,
			SourcePath = path,
			Cwd = cwd,
			Title = title ?? string.Empty,
			Created = first ?? info.LastWriteTimeUtc,
			Modified = last == default ? info.LastWriteTimeUtc : last,
			Size = info.Length,
		};
	}

	public IReadOnlyList<SessionEntry> Convert(ImportedSession session)
	{
		var ids = new ImportIds();
		var entries = new List<SessionEntry>();
		string? parentId = null;

		foreach (var record in SessionImportService.ReadObjects(session.SourcePath))
		{
			// 子代理旁支与宿主机注入的元信息都不属于这条对话。
			if (SessionImportService.IsTrue(record["isSidechain"]) || SessionImportService.IsTrue(record["isMeta"]))
			{
				continue;
			}

			var type = SessionImportService.Text(record, "type");
			var timestamp = SessionImportService.ParseTimestamp(record["timestamp"])
				?? SessionImportService.ParseTimestamp(record["message"]?["timestamp"]);

			switch (type)
			{
				case "user":
					parentId = AppendUser(record, entries, ids, parentId, timestamp);
					break;
				case "assistant":
					parentId = AppendAssistant(record, entries, ids, parentId, timestamp);
					break;
				case "tool_result":
					// 少数导出把 tool_result 抬到顶层；主流写法在 user 的 content 里（见 AppendUser）。
					parentId = AppendToolResult(record, entries, ids, parentId, timestamp);
					break;
			}
		}

		return entries;
	}

	/// <summary>
	/// user 行可能同时承载「用户输入」与「工具结果」：一行里先有 tool_result 块、后有文本时，
	/// 工具结果必须排在前面 —— 否则 user 气泡会插在 toolCall 与 toolResult 之间。
	/// </summary>
	private static string? AppendUser(
		JsonObject record,
		List<SessionEntry> entries,
		ImportIds ids,
		string? parentId,
		DateTimeOffset? timestamp)
	{
		var content = record["message"]?["content"];
		if (content is JsonValue value && value.TryGetValue<string>(out var single))
		{
			return single.Trim().Length == 0
				? parentId
				: Append(entries, ids, parentId, timestamp, new UserMessage
				{
					Content = MessageContent.FromText(single),
					Timestamp = ToUnixMs(timestamp),
				});
		}

		if (content is not JsonArray blocks)
		{
			return parentId;
		}

		string? result = parentId;

		foreach (var block in blocks)
		{
			if (block is null)
			{
				continue;
			}

			var blockType = SessionImportService.Text(block, "type");
			if (blockType == "tool_result")
			{
				var toolCallId = SessionImportService.Text(block, "tool_use_id") ?? SessionImportService.Text(block, "toolCallId");
				if (toolCallId is null)
				{
					continue;
				}

				var isError = block["is_error"] is JsonValue errorValue && errorValue.TryGetValue<bool>(out var flag) && flag;
				var text = FlattenContent(block["content"]);
				result = Append(entries, ids, result, timestamp, new ToolResultMessage
				{
					ToolCallId = toolCallId,
					// 源里不记工具名；留空比编一个假名字诚实，桌面端会按「工具结果」展示。
					ToolName = string.Empty,
					HasToolName = false,
					Content = text.Length == 0 ? [] : [new TextContent { Text = text }],
					IsError = isError,
					Timestamp = ToUnixMs(timestamp),
				});
			}
		}

		// 用户自己的文本排在工具结果之后（同一行的真实顺序就是这样）。
		var own = UserText(record);
		if (own.Length > 0)
		{
			result = Append(entries, ids, result, timestamp, new UserMessage
			{
				Content = MessageContent.FromText(own),
				Timestamp = ToUnixMs(timestamp),
			});
		}

		return result;
	}

	private static string? AppendAssistant(
		JsonObject record,
		List<SessionEntry> entries,
		ImportIds ids,
		string? parentId,
		DateTimeOffset? timestamp)
	{
		var content = record["message"]?["content"];
		if (content is JsonValue value && value.TryGetValue<string>(out var single))
		{
			return single.Trim().Length == 0
				? parentId
				: Append(entries, ids, parentId, timestamp, Assistant(text: single));
		}

		if (content is not JsonArray blocks)
		{
			return parentId;
		}

		var rendered = new List<ContentBlock>(blocks.Count);
		string? pendingThinking = null;
		string? result = parentId;

		void Flush(List<ContentBlock> parts)
		{
			if (parts.Count == 0)
			{
				return;
			}

			var message = Assistant(text: null) with
			{
				Content = [.. parts],
				StopReason = parts.Any(static part => part is ToolCall) ? StopReason.ToolUse : StopReason.Stop,
				Usage = ReadUsage(record),
				Model = SessionImportService.Text(record, "message", "model") ?? string.Empty,
				Provider = "anthropic",
				Api = "anthropic-messages",
			};

			result = Append(entries, ids, result, timestamp, message);
			parts.Clear();
		}

		foreach (var block in blocks)
		{
			if (block is null)
			{
				continue;
			}

			switch (SessionImportService.Text(block, "type"))
			{
				case "thinking":
					// 连续 thinking 块合成一段，与 pi 的「一条消息一段思考」一致。
					pendingThinking = pendingThinking is null
						? SessionImportService.Text(block, "thinking")
						: pendingThinking + "\n\n" + SessionImportService.Text(block, "thinking");
					break;
				case "text":
					if (pendingThinking is { Length: > 0 } thinking)
					{
						rendered.Add(new ThinkingContent { Thinking = thinking });
						pendingThinking = null;
					}

					if (SessionImportService.Text(block, "text") is { } text && text.Length > 0)
					{
						rendered.Add(new TextContent { Text = text });
					}

					break;
				case "tool_use":
					if (pendingThinking is { Length: > 0 } beforeCall)
					{
						rendered.Add(new ThinkingContent { Thinking = beforeCall });
						pendingThinking = null;
					}

					var callId = SessionImportService.Text(block, "id") ?? new ImportIds().Next();
					rendered.Add(new ToolCall
					{
						Id = callId,
						Name = SessionImportService.Text(block, "name") ?? "tool",
						Arguments = block["input"] as JsonObject ?? new JsonObject(),
					});

					// 一个 tool_use 收尾一条助手消息：pi 的 toolCall ↔ toolResult 必须相邻可配对。
					Flush(rendered);
					break;
			}
		}

		if (pendingThinking is { Length: > 0 } tail)
		{
			rendered.Add(new ThinkingContent { Thinking = tail });
		}

		Flush(rendered);
		return result;
	}

	private static string? AppendToolResult(
		JsonObject record,
		List<SessionEntry> entries,
		ImportIds ids,
		string? parentId,
		DateTimeOffset? timestamp)
	{
		var toolCallId = SessionImportService.Text(record, "tool_use_id") ?? SessionImportService.Text(record, "toolCallId");
		if (toolCallId is null)
		{
			return parentId;
		}

		var text = FlattenContent(record["content"]);
		var isError = record["is_error"] is JsonValue errorValue && errorValue.TryGetValue<bool>(out var flag) && flag;

		return Append(entries, ids, parentId, timestamp, new ToolResultMessage
		{
			ToolCallId = toolCallId,
			ToolName = string.Empty,
			HasToolName = false,
			Content = text.Length == 0 ? [] : [new TextContent { Text = text }],
			IsError = isError,
			Timestamp = ToUnixMs(timestamp),
		});
	}

	private static AssistantMessage Assistant(string? text) => new()
	{
		Content = text is null ? [] : [new TextContent { Text = text }],
		StopReason = StopReason.Stop,
	};

	private static string? Append(
		List<SessionEntry> entries,
		ImportIds ids,
		string? parentId,
		DateTimeOffset? timestamp,
		Message message)
	{
		var id = ids.Next();
		entries.Add(new SessionMessageEntry
		{
			Id = id,
			ParentId = parentId,
			Timestamp = SessionPaths.ToIso8601(timestamp ?? DateTimeOffset.UtcNow),
			Message = message,
		});

		return id;
	}

	/// <summary>只取 user 行里「用户自己说的话」：text 块，不含 tool_result。</summary>
	private static string UserText(JsonObject record)
	{
		var content = record["message"]?["content"];
		if (content is JsonValue value && value.TryGetValue<string>(out var single))
		{
			return single.Trim();
		}

		if (content is not JsonArray blocks)
		{
			return string.Empty;
		}

		var parts = new List<string>(blocks.Count);
		foreach (var block in blocks)
		{
			if (SessionImportService.Text(block, "type") == "text" &&
				SessionImportService.Text(block, "text") is { Length: > 0 } text)
			{
				parts.Add(text);
			}
		}

		return string.Join("\n", parts).Trim();
	}

	/// <summary>把 tool_result 的 content（字符串或块数组）摊平成文本。</summary>
	private static string FlattenContent(JsonNode? node)
	{
		if (node is JsonValue value && value.TryGetValue<string>(out var single))
		{
			return single;
		}

		if (node is not JsonArray blocks)
		{
			return node is null ? string.Empty : node.ToJsonString();
		}

		var parts = new List<string>(blocks.Count);
		foreach (var block in blocks)
		{
			if (block is JsonValue blockValue && blockValue.TryGetValue<string>(out var text))
			{
				parts.Add(text);
				continue;
			}

			if (SessionImportService.Text(block, "type") == "text" &&
				SessionImportService.Text(block, "text") is { Length: > 0 } blockText)
			{
				parts.Add(blockText);
				continue;
			}

			// 图片等非文本块：留一句占位，不让它在导入后静默消失。
			if (SessionImportService.Text(block, "type") is { Length: > 0 } other)
			{
				parts.Add($"[{other}]");
			}
		}

		return string.Join("\n", parts);
	}

	private static Usage ReadUsage(JsonObject record)
	{
		var usage = record["message"]?["usage"] as JsonObject;
		if (usage is null)
		{
			return new Usage();
		}

		long Number(string name) =>
			usage[name] is JsonValue value && value.TryGetValue<long>(out var number) ? number : 0;

		var input = Number("input_tokens");
		var output = Number("output_tokens");
		var cacheRead = Number("cache_read_input_tokens");
		var cacheWrite = Number("cache_creation_input_tokens");

		return new Usage
		{
			Input = input,
			Output = output,
			CacheRead = cacheRead,
			CacheWrite = cacheWrite,
			TotalTokens = input + output + cacheRead + cacheWrite,
		};
	}

	/// <summary>Claude 的项目 slug 是路径编码：<c>-home-user-repo</c>；拿不到 cwd 时据此还原。</summary>
	private static string? CwdFromProjectSlug(string path)
	{
		var directory = Path.GetDirectoryName(path);
		if (directory is null)
		{
			return null;
		}

		var slug = Path.GetFileName(directory);
		if (slug.Length == 0 || slug[0] != '-')
		{
			return null;
		}

		// 编码把路径分隔符换成 '-'，无法无歧义还原；只作为兜底，且只处理 Unix 绝对路径。
		var restored = slug.Replace('-', '/');
		return Directory.Exists(restored) ? restored : null;
	}

	internal static long ToUnixMs(DateTimeOffset? timestamp) =>
		timestamp is { } value ? value.ToUnixTimeMilliseconds() : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
