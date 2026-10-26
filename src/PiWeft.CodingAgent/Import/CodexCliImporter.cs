using System.Text.Json.Nodes;
using PiWeft.Ai;
using PiWeft.CodingAgent.Session;

namespace PiWeft.CodingAgent.Import;

/// <summary>
/// Codex CLI 会话导入（<c>~/.codex/sessions/&lt;YYYY&gt;/&lt;MM&gt;/&lt;DD&gt;/rollout-*.jsonl</c>）。
/// <para>
/// 每行是 <c>{"timestamp":..,"type":..,"payload":{..}}</c>。第一行是 <c>session_meta</c>（带 id / cwd / model），
/// 对话主体在 <c>response_item</c> 里：<c>message</c>（user/assistant）、<c>reasoning</c>、
/// <c>function_call</c>、<c>function_call_output</c>。
/// </para>
/// <para>
/// 旧版 CLI（≤0.129）把用户输入写在 <c>event_msg/user_message</c> 里、新桌面版直接写
/// <c>response_item/message</c>；两条路径都认，同一段文本只落一次（以先出现的为准）。
/// </para>
/// </summary>
internal sealed class CodexCliImporter : IExternalSessionImporter
{
	public string Key => "codex";

	public string Label => "Codex CLI";

	public string ResolveRoot(string home) => Path.Combine(home, ".codex", "sessions");

	/// <summary>按 <c>YYYY/MM/DD</c> 分层存放，因此要递归。</summary>
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

		string? id = null;
		string? cwd = null;
		string? title = null;
		DateTimeOffset? created = null;
		DateTimeOffset last = default;

		foreach (var record in head)
		{
			var type = SessionImportService.Text(record, "type");
			if (type == "session_meta")
			{
				id ??= SessionImportService.Text(record, "payload", "id");
				cwd ??= SessionImportService.Text(record, "payload", "cwd");
			}

			// 只在真的取到文本时才定标题：空串会把后续更好的候选挡掉。
			if (title is null && SessionImportService.TitleFromText(ExtractUserText(record)) is { Length: > 0 } candidate)
			{
				title = candidate;
			}

			if (SessionImportService.ParseTimestamp(record["timestamp"]) is { } timestamp)
			{
				created ??= timestamp;
				if (timestamp > last)
				{
					last = timestamp;
				}
			}
		}

		if (id is null || cwd is null)
		{
			return null;
		}

		return new ImportedSession
		{
			Source = Key,
			SourceLabel = Label,
			SessionId = id,
			SourcePath = path,
			Cwd = cwd,
			Title = title ?? string.Empty,
			Created = created ?? info.LastWriteTimeUtc,
			Modified = last == default ? info.LastWriteTimeUtc : last,
			Size = info.Length,
		};
	}

	public IReadOnlyList<SessionEntry> Convert(ImportedSession session)
	{
		var ids = new ImportIds();
		var entries = new List<SessionEntry>();
		var toolNames = new Dictionary<string, string>(StringComparer.Ordinal);
		var toolStartedAt = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
		string? parentId = null;
		string? pendingThinking = null;
		var provider = "codex";
		var model = "codex";

		foreach (var record in SessionImportService.ReadObjects(session.SourcePath))
		{
			var type = SessionImportService.Text(record, "type");
			var timestamp = SessionImportService.ParseTimestamp(record["timestamp"]);

			if (type == "session_meta")
			{
				provider = SessionImportService.Text(record, "payload", "model_provider") ?? provider;
				model = SessionImportService.Text(record, "payload", "model") ?? model;
				continue;
			}

			var payload = record["payload"] as JsonObject;
			if (payload is null)
			{
				continue;
			}

			// 旧版 CLI：用户输入在 event_msg 里。
			if (type == "event_msg" && SessionImportService.Text(payload, "type") == "user_message")
			{
				var text = SessionImportService.Text(payload, "message")?.Trim();
				if (text is { Length: > 0 })
				{
					parentId = Append(entries, ids, parentId, timestamp, new UserMessage
					{
						Content = MessageContent.FromText(text),
						Timestamp = ClaudeCodeImporter.ToUnixMs(timestamp),
					});
				}

				continue;
			}

			if (type != "response_item")
			{
				continue;
			}

			switch (SessionImportService.Text(payload, "type"))
			{
				case "message" when SessionImportService.Text(payload, "role") == "user":
				{
					var text = FlattenText(payload).Trim();
					if (text.Length > 0)
					{
						parentId = Append(entries, ids, parentId, timestamp, new UserMessage
						{
							Content = MessageContent.FromText(text),
							Timestamp = ClaudeCodeImporter.ToUnixMs(timestamp),
						});
					}

					break;
				}

				case "reasoning":
					// Codex 把思考拆成多条 reasoning 记录，连续合并成一段更好读。
					var reasoning = FlattenText(payload).Trim();
					if (reasoning.Length > 0)
					{
						pendingThinking = pendingThinking is null ? reasoning : pendingThinking + "\n\n" + reasoning;
					}

					break;

				case "message" when SessionImportService.Text(payload, "role") == "assistant":
				{
					var parts = new List<ContentBlock>(2);
					if (pendingThinking is { Length: > 0 } thinking)
					{
						parts.Add(new ThinkingContent { Thinking = thinking, ThinkingSignature = "codex_reasoning" });
						pendingThinking = null;
					}

					if (FlattenText(payload).Trim() is { Length: > 0 } text)
					{
						parts.Add(new TextContent { Text = text });
					}

					if (parts.Count > 0)
					{
						parentId = Append(entries, ids, parentId, timestamp, new AssistantMessage
						{
							Content = parts,
							Provider = provider,
							Model = model,
							Api = "openai-responses",
							StopReason = StopReason.Stop,
							Timestamp = ClaudeCodeImporter.ToUnixMs(timestamp),
						});
					}

					break;
				}

				case "function_call":
				case "custom_tool_call":
				{
					var callId = SessionImportService.Text(payload, "call_id")
						?? SessionImportService.Text(payload, "id")
						?? ids.Next();
					var name = SessionImportService.Text(payload, "name") ?? "tool";
					toolNames[callId] = name;
					if (timestamp is { } startedAt)
					{
						toolStartedAt[callId] = startedAt;
					}

					var parts = new List<ContentBlock>(2);
					if (pendingThinking is { Length: > 0 } thinking)
					{
						parts.Add(new ThinkingContent { Thinking = thinking, ThinkingSignature = "codex_reasoning" });
						pendingThinking = null;
					}

					parts.Add(new ToolCall
					{
						Id = callId,
						Name = name,
						Arguments = ParseArguments(payload["arguments"] ?? payload["input"]),
					});

					parentId = Append(entries, ids, parentId, timestamp, new AssistantMessage
					{
						Content = parts,
						Provider = provider,
						Model = model,
						Api = "openai-responses",
						StopReason = StopReason.ToolUse,
						Timestamp = ClaudeCodeImporter.ToUnixMs(timestamp),
					});

					break;
				}

				case "function_call_output":
				case "custom_tool_call_output":
				{
					var callId = SessionImportService.Text(payload, "call_id") ?? SessionImportService.Text(payload, "id");
					if (callId is null)
					{
						break;
					}

					var isError = SessionImportService.IsTrue(payload["is_error"]);
					var output = FlattenOutput(payload);

					// 源里只有 call 与 output 的时间戳：导出时把派生耗时一起写上，桌面端的工具卡片
					// 才能和原生 pi 会话一样显示「用时」。
					double? durationMs = timestamp is { } finishedAt && toolStartedAt.TryGetValue(callId, out var began)
						? Math.Max(0, (finishedAt - began).TotalMilliseconds)
						: null;

					parentId = Append(entries, ids, parentId, timestamp, new ToolResultMessage
					{
						ToolCallId = callId,
						ToolName = toolNames.TryGetValue(callId, out var name) ? name : string.Empty,
						HasToolName = toolNames.ContainsKey(callId),
						Content = output.Length == 0 ? [] : [new TextContent { Text = output }],
						IsError = isError,
						DurationMs = durationMs,
						Timestamp = ClaudeCodeImporter.ToUnixMs(timestamp),
					});

					break;
				}
			}
		}

		// 文件以思考结尾：别把它丢掉。
		if (pendingThinking is { Length: > 0 } trailing)
		{
			parentId = Append(entries, ids, parentId, null, new AssistantMessage
			{
				Content = [new ThinkingContent { Thinking = trailing, ThinkingSignature = "codex_reasoning" }],
				Provider = provider,
				Model = model,
				Api = "openai-responses",
				StopReason = StopReason.Stop,
				Timestamp = ClaudeCodeImporter.ToUnixMs(null),
			});
		}

		_ = parentId;
		return entries;
	}

	/// <summary>desktop 版把用户输入包在 <c>&lt;user_message&gt;</c> 之类的包装块里，这里剥掉。</summary>
	private static string? ExtractUserText(JsonObject record)
	{
		var type = SessionImportService.Text(record, "type");
		if (type == "event_msg" && SessionImportService.Text(record, "payload", "type") == "user_message")
		{
			return SessionImportService.Text(record, "payload", "message");
		}

		if (type == "response_item" &&
			SessionImportService.Text(record, "payload", "type") == "message" &&
			SessionImportService.Text(record, "payload", "role") == "user")
		{
			return FlattenText(record["payload"] as JsonObject);
		}

		return null;
	}

	/// <summary>content 是块数组（<c>input_text</c>/<c>output_text</c>/<c>text</c>）或纯字符串。</summary>
	private static string FlattenText(JsonObject? payload)
	{
		if (payload is null)
		{
			return string.Empty;
		}

		var content = payload["content"] ?? payload["summary"] ?? payload["text"];
		if (content is JsonValue value && value.TryGetValue<string>(out var single))
		{
			return single;
		}

		if (content is not JsonArray blocks)
		{
			return string.Empty;
		}

		var parts = new List<string>(blocks.Count);
		foreach (var block in blocks)
		{
			if (block is JsonValue blockValue && blockValue.TryGetValue<string>(out var text))
			{
				parts.Add(text);
				continue;
			}

			if (SessionImportService.Text(block, "text") is { Length: > 0 } blockText)
			{
				parts.Add(blockText);
			}
		}

		return string.Join("\n", parts);
	}

	private static string FlattenOutput(JsonObject payload)
	{
		var output = payload["output"] ?? payload["content"];
		if (output is JsonValue value && value.TryGetValue<string>(out var single))
		{
			return single;
		}

		if (output is JsonArray blocks)
		{
			var text = FlattenText(new JsonObject { ["content"] = blocks.DeepClone() });
			if (text.Length > 0)
			{
				return text;
			}
		}

		// 结构化输出（exec 的 JSON 报告等）：原样给出去，不要凭空改写。
		return output is null ? string.Empty : output.ToJsonString();
	}

	/// <summary>Codex 的 arguments 是 JSON 字符串（偶尔已经是对象）。</summary>
	private static JsonObject ParseArguments(JsonNode? node)
	{
		if (node is JsonObject obj)
		{
			return (JsonObject)obj.DeepClone();
		}

		if (node is JsonValue value && value.TryGetValue<string>(out var text) && text.Trim().Length > 0)
		{
			try
			{
				if (JsonNode.Parse(text) is JsonObject parsed)
				{
					return parsed;
				}
			}
			catch (System.Text.Json.JsonException)
			{
				// 不是合法 JSON：保留原文，让调用方能看出源工具收到了什么。
			}

			return new JsonObject { ["_raw"] = text };
		}

		return new JsonObject();
	}

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
}
