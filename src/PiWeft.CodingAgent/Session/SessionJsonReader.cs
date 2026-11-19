using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiWeft.Ai;
using PiWeft.Ai.Json;

namespace PiWeft.CodingAgent.Session;

/// 会话行的 <see cref="Utf8JsonReader"/> 解析路径。与 <see cref="SessionJson.ParseObject(JsonObject)"/> 同语义，
/// 但不建 JsonNode 树，也不走 <c>JsonDocument.GetString()</c>：大文本块在未转义时直接接管
/// <c>ValueSpan</c> 的 UTF-8 字节，省掉「UTF-8 → UTF-16 → UTF-8」的往返。
/// 30.2 MiB / 600 条 50 KiB 工具结果的会话，打开分配从 ~125 MiB 降到 ~35 MiB。
internal static class SessionJsonReader
{
	public static SessionLine? Parse(ReadOnlyMemory<byte> utf8Line) => Parse(utf8Line.Span);

	public static SessionLine? Parse(ReadOnlySpan<byte> utf8Line)
	{
		if (utf8Line.IsEmpty || SessionJson.IsWhiteSpace(utf8Line))
		{
			return null;
		}

		var type = ReadType(utf8Line);
		var reader = new Utf8JsonReader(utf8Line);
		if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
		{
			return null;
		}

		return type switch
		{
			"session" => ReadHeader(ref reader),
			null => null,
			_ => ReadEntry(ref reader, type, utf8Line),
		};
	}

	/// 第一遍只找 <c>type</c>：字段顺序不固定，先定类型，第二遍才能按类型读字段。
	/// 两遍都是 UTF-8 扫描，比建 JsonNode 树便宜得多。
	private static string? ReadType(ReadOnlySpan<byte> utf8Line)
	{
		var reader = new Utf8JsonReader(utf8Line);
		if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
		{
			return null;
		}

		while (reader.Read())
		{
			if (reader.TokenType == JsonTokenType.EndObject)
			{
				break;
			}

			var isType = reader.ValueTextEquals("type"u8);
			if (!reader.Read())
			{
				break;
			}

			if (isType)
			{
				return reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
			}

			reader.Skip();
		}

		return null;
	}

	private static SessionHeader ReadHeader(ref Utf8JsonReader reader)
	{
		long version = 1;
		var id = string.Empty;
		var timestamp = string.Empty;
		var cwd = string.Empty;
		string? parentSession = null;
		string? origin = null;
		JsonObject? extra = null;

		while (reader.Read())
		{
			if (reader.TokenType == JsonTokenType.EndObject)
			{
				break;
			}

			var name = reader.GetString();
			if (!reader.Read())
			{
				break;
			}

			switch (name)
			{
				// type 已经在第一遍读过；不能落到 default，否则会被当成未知字段写回末尾。
				case "type": break;
				case "version": version = Long(ref reader) ?? 1; break;
				case "id": id = Str(ref reader) ?? string.Empty; break;
				case "timestamp": timestamp = Str(ref reader) ?? string.Empty; break;
				case "cwd": cwd = Str(ref reader) ?? string.Empty; break;
				case "parentSession": parentSession = Str(ref reader); break;
				case "origin": origin = Str(ref reader); break;
				default:
					extra ??= new JsonObject();
					extra[name!] = JsonNode.Parse(ref reader);
					break;
			}
		}

		return new SessionHeader
		{
			Version = (int)version,
			Id = id,
			Timestamp = timestamp,
			Cwd = cwd,
			ParentSession = parentSession,
			Origin = origin,
			Extra = extra,
		};
	}

	private static SessionEntry ReadEntry(ref Utf8JsonReader reader, string type, ReadOnlySpan<byte> utf8Line)
	{
		var id = string.Empty;
		string? parentId = null;
		var timestamp = string.Empty;
		JsonObject? extra = null;

		Message? message = null;
		string? provider = null;
		string? modelId = null;
		string? thinkingLevel = null;
		string? kind = null;
		string? model = null;
		string? note = null;
		Usage? usage = null;
		string? summary = null;
		string? firstKeptEntryId = null;
		long? tokensBefore = null;
		JsonNode? details = null;
		bool? fromHook = null;
		SystemMessage? systemMessage = null;
		string? fromId = null;
		string? customType = null;
		JsonNode? data = null;
		MessageContent? content = null;
		bool? display = null;
		string? targetId = null;
		MessageContent? replacement = null;
		var hasReplacement = false;
		string? label = null;
		string? originalEntryId = null;
		string? reason = null;
		string? name = null;

		// 本类型的已知字段名单与 JsonNode 路径共用：名单外的键进 Extra，写回时原样输出。
		// 不能只靠下面的 switch：别的类型才认识的字段（例如 model_change 上的 usage）
		// 会被同名 case 吃掉，写回时静默丢失。
		var known = SessionJson.KnownKeys(type);
		while (reader.Read())
		{
			if (reader.TokenType == JsonTokenType.EndObject)
			{
				break;
			}

			var key = reader.GetString();
			if (!reader.Read())
			{
				break;
			}

			if (!known.Contains(key))
			{
				extra ??= new JsonObject();
				extra[key!] = JsonNode.Parse(ref reader);
				continue;
			}

			switch (key)
			{
				// type 已经在第一遍读过；这里不能落到 default，否则会被当成未知字段写回末尾。
				case "type": break;
				case "id": id = Str(ref reader) ?? string.Empty; break;
				case "parentId": parentId = Str(ref reader); break;
				case "timestamp": timestamp = Str(ref reader) ?? string.Empty; break;
				case "message":
					if (reader.TokenType == JsonTokenType.StartObject)
					{
						// 未知角色的消息要把原始对象整份留下：写回时直接输出，重新序列化会丢字段。
						var start = (int)reader.TokenStartIndex;
						message = ReadMessage(ref reader, utf8Line, start);
						if (message is CustomMessage custom && custom.Raw is null or { Count: 0 })
						{
							message = custom with
							{
								Raw = JsonNode.Parse(utf8Line[start..(int)reader.BytesConsumed]) as JsonObject ?? new JsonObject(),
							};
						}
					}
					else
					{
						reader.Skip();
					}

					break;
				case "provider": provider = Str(ref reader); break;
				case "modelId": modelId = Str(ref reader); break;
				case "thinkingLevel": thinkingLevel = Str(ref reader); break;
				case "kind": kind = Str(ref reader); break;
				case "model": model = Str(ref reader); break;
				case "note": note = Str(ref reader); break;
				case "usage":
					if (reader.TokenType == JsonTokenType.StartObject) usage = ReadUsage(ref reader);
					else reader.Skip();
					break;
				case "summary": summary = Str(ref reader); break;
				case "firstKeptEntryId": firstKeptEntryId = Str(ref reader); break;
				case "tokensBefore": tokensBefore = Long(ref reader); break;
				case "details": details = JsonNode.Parse(ref reader); break;
				case "fromHook": fromHook = Bool(ref reader); break;
				case "systemMessage":
					if (reader.TokenType == JsonTokenType.StartObject)
					{
						systemMessage = ReadMessage(ref reader, utf8Line, (int)reader.TokenStartIndex) as SystemMessage;
					}
					else
					{
						reader.Skip();
					}

					break;
				case "fromId": fromId = Str(ref reader); break;
				case "customType": customType = Str(ref reader); break;
				case "data": data = JsonNode.Parse(ref reader); break;
				case "content": content = ReadMessageContent(ref reader); break;
				case "display": display = Bool(ref reader); break;
				case "targetId": targetId = Str(ref reader); break;
				case "replacement":
					if (reader.TokenType == JsonTokenType.StartObject)
					{
						replacement = ReadReplacement(ref reader) ?? MessageContent.Empty;
						hasReplacement = true;
					}
					else
					{
						reader.Skip();
					}

					break;
				case "label": label = Str(ref reader); break;
				case "originalEntryId": originalEntryId = Str(ref reader); break;
				case "reason": reason = Str(ref reader); break;
				case "name": name = Str(ref reader); break;
				default:
					extra ??= new JsonObject();
					extra[key!] = JsonNode.Parse(ref reader);
					break;
			}
		}

		SessionEntry entry = type switch
		{
			"message" => new SessionMessageEntry
			{
				Message = message ?? new UserMessage { Content = MessageContent.Empty, Timestamp = 0 },
			},
			"model_change" => new ModelChangeEntry
			{
				Provider = provider ?? string.Empty,
				ModelId = modelId ?? string.Empty,
			},
			"thinking_level_change" => new ThinkingLevelChangeEntry
			{
				ThinkingLevel = thinkingLevel ?? ThinkingLevels.Off,
			},
			"usage" => new UsageEntry
			{
				Kind = kind ?? string.Empty,
				Provider = provider ?? string.Empty,
				Model = model ?? string.Empty,
				Usage = usage ?? new Usage(),
				Note = note,
			},
			"compaction" => new CompactionEntry
			{
				Summary = summary ?? string.Empty,
				FirstKeptEntryId = firstKeptEntryId ?? string.Empty,
				TokensBefore = tokensBefore ?? 0,
				Details = details,
				Usage = usage,
				FromHook = fromHook,
				SystemMessage = systemMessage,
			},
			"branch_summary" => new BranchSummaryEntry
			{
				FromId = fromId ?? string.Empty,
				Summary = summary ?? string.Empty,
				Details = details,
				Usage = usage,
				FromHook = fromHook,
			},
			"custom" => new CustomEntry
			{
				CustomType = customType ?? string.Empty,
				Data = data,
			},
			"custom_message" => new CustomMessageEntry
			{
				CustomType = customType ?? string.Empty,
				Content = content ?? MessageContent.Empty,
				Details = details,
				Display = display ?? true,
			},
			"context_edit" => new ContextEditEntry
			{
				TargetId = targetId ?? string.Empty,
				Replacement = hasReplacement ? replacement : null,
			},
			"label" => new LabelEntry
			{
				TargetId = targetId ?? string.Empty,
				Label = label,
			},
			"deleted" => new DeletedEntry
			{
				OriginalEntryId = originalEntryId ?? id,
				Reason = reason,
			},
			"session_info" => new SessionInfoEntry
			{
				Name = name,
			},
			_ => new UnknownSessionEntry
			{
				// 未知条目整行保留：写回时按原始对象输出，模型上只改 id/parentId。
				Raw = JsonNode.Parse(utf8Line) as JsonObject ?? new JsonObject(),
			},
		};

		return entry is UnknownSessionEntry
			? entry with { Id = id, ParentId = parentId, Timestamp = timestamp }
			: entry with { Id = id, ParentId = parentId, Timestamp = timestamp, Extra = extra };
	}

	private static Message ReadMessage(ref Utf8JsonReader reader, ReadOnlySpan<byte> utf8Line, int start)
	{
		// 先定角色：房段（已知键名单）按角色选，别的角色的字段进 Extra 而不是被同名 case 吃掉。
		var role = ReadRole(utf8Line[start..]);
		var known = KeysForRole(role);
		string? roleValue = role;
		MessageContent content = MessageContent.Empty;
		Dictionary<string, string?>? sections = null;
		List<Tool>? toolsAdded = null;
		List<ToolReference>? toolsRemoved = null;
		string? customType = null;
		bool? display = null;
		string? api = null;
		string? provider = null;
		string? model = null;
		string? responseModel = null;
		string? responseId = null;
		string? providerThinkingLevel = null;
		string? thinkingLevel = null;
		Usage? usage = null;
		string? stopReason = null;
		string? errorMessage = null;
		string? rawStopReason = null;
		bool? endTurn = null;
		long timestamp = 0;
		double? durationMs = null;
		double? firstTokenMs = null;
		double? streamingDurationMs = null;
		string? toolCallId = null;
		string? toolName = null;
		var hasToolName = false;
		JsonNode? details = null;
		bool? isError = null;
		JsonObject? extra = null;

		while (reader.Read())
		{
			if (reader.TokenType == JsonTokenType.EndObject)
			{
				break;
			}

			var name = reader.GetString();
			if (!reader.Read())
			{
				break;
			}

			if (!known.Contains(name))
			{
				extra ??= new JsonObject();
				extra[name!] = JsonNode.Parse(ref reader);
				continue;
			}

			switch (name)
			{
				case "role": roleValue = Str(ref reader); break;
				case "content": content = ReadMessageContent(ref reader); break;
				case "sections":
					if (reader.TokenType == JsonTokenType.StartObject) sections = ReadSections(ref reader);
					else reader.Skip();
					break;
				case "toolsAdded":
					if (reader.TokenType == JsonTokenType.StartArray) toolsAdded = ReadTools(ref reader);
					else reader.Skip();
					break;
				case "toolsRemoved":
					if (reader.TokenType == JsonTokenType.StartArray) toolsRemoved = ReadToolReferences(ref reader);
					else reader.Skip();
					break;
				case "customType": customType = Str(ref reader); break;
				case "display": display = Bool(ref reader); break;
				case "api": api = Str(ref reader); break;
				case "provider": provider = Str(ref reader); break;
				case "model": model = Str(ref reader); break;
				case "responseModel": responseModel = Str(ref reader); break;
				case "responseId": responseId = Str(ref reader); break;
				case "providerThinkingLevel": providerThinkingLevel = Str(ref reader); break;
				case "thinkingLevel": thinkingLevel = Str(ref reader); break;
				case "usage":
					if (reader.TokenType == JsonTokenType.StartObject) usage = ReadUsage(ref reader);
					else reader.Skip();
					break;
				case "stopReason": stopReason = Str(ref reader); break;
				case "errorMessage": errorMessage = Str(ref reader); break;
				case "rawStopReason": rawStopReason = Str(ref reader); break;
				case "endTurn": endTurn = Bool(ref reader); break;
				case "timestamp": timestamp = Long(ref reader) ?? 0; break;
				case "durationMs": durationMs = Double(ref reader); break;
				case "firstTokenMs": firstTokenMs = Double(ref reader); break;
				case "streamingDurationMs": streamingDurationMs = Double(ref reader); break;
				case "toolCallId": toolCallId = Str(ref reader); break;
				case "toolName": toolName = Str(ref reader); hasToolName = true; break;
				case "details": details = JsonNode.Parse(ref reader); break;
				case "isError": isError = Bool(ref reader); break;
				default:
					extra ??= new JsonObject();
					extra[name!] = JsonNode.Parse(ref reader);
					break;
			}
		}

		return roleValue switch
		{
			"system" => new SystemMessage
			{
				Content = content,
				Sections = sections,
				ToolsAdded = toolsAdded is { Count: > 0 } ? toolsAdded : null,
				ToolsRemoved = toolsRemoved is { Count: > 0 } ? toolsRemoved : null,
				Timestamp = timestamp,
				Extra = extra,
			},
			"user" => new UserMessage
			{
				Content = content,
				CustomType = customType,
				Display = display ?? true,
				Timestamp = timestamp,
				Extra = extra,
			},
			"assistant" => new AssistantMessage
			{
				Content = content.Blocks ?? [],
				Api = api ?? string.Empty,
				Provider = provider ?? string.Empty,
				Model = model ?? string.Empty,
				ResponseModel = responseModel,
				ResponseId = responseId,
				ProviderThinkingLevel = providerThinkingLevel,
				ThinkingLevel = thinkingLevel,
				Usage = usage ?? new Usage(),
				StopReason = StopReasonExtensions.ParseStopReason(stopReason),
				ErrorMessage = errorMessage,
				RawStopReason = rawStopReason,
				EndTurn = endTurn,
				Timestamp = timestamp,
				DurationMs = durationMs,
				FirstTokenMs = firstTokenMs,
				StreamingDurationMs = streamingDurationMs,
				Extra = extra,
			},
			"toolResult" => new ToolResultMessage
			{
				ToolCallId = toolCallId ?? string.Empty,
				ToolName = toolName ?? string.Empty,
				HasToolName = hasToolName,
				Content = content.Blocks ?? [],
				Details = details,
				Usage = usage,
				IsError = isError ?? false,
				Timestamp = timestamp,
				DurationMs = durationMs,
				Extra = extra,
			},
			// 未知角色：Raw 由调用方按原始字节补上（这里拿不到整段对象）。
			_ => new CustomMessage
			{
				CustomRole = roleValue ?? string.Empty,
				Content = content,
				Timestamp = timestamp,
				Raw = new JsonObject(),
			},
		};
	}

	/// 第一遍只找 <c>role</c>：消息字段顺序不固定，先定角色才能按角色的已知键名单分流。
	private static string? ReadRole(ReadOnlySpan<byte> message)
	{
		var reader = new Utf8JsonReader(message);
		if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
		{
			return null;
		}

		while (reader.Read())
		{
			if (reader.TokenType == JsonTokenType.EndObject)
			{
				break;
			}

			var isRole = reader.ValueTextEquals("role"u8);
			if (!reader.Read())
			{
				break;
			}

			if (isRole)
			{
				return reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
			}

			reader.Skip();
		}

		return null;
	}

	// 每种角色自己消费的字段名单，与 MessageJsonConverter 的名单一致。新增字段时两处都要改。
	private static readonly string[] SystemKeys = ["role", "content", "sections", "toolsAdded", "toolsRemoved", "timestamp"];

	private static readonly string[] UserKeys = ["role", "content", "customType", "display", "timestamp"];

	private static readonly string[] AssistantKeys =
	[
		"role", "content", "api", "provider", "model", "responseModel", "responseId",
		"providerThinkingLevel", "thinkingLevel", "usage", "stopReason", "errorMessage",
		"rawStopReason", "endTurn", "durationMs", "firstTokenMs", "streamingDurationMs", "timestamp",
	];

	private static readonly string[] ToolResultKeys =
	[
		"role", "toolCallId", "toolName", "content", "details", "usage", "isError", "timestamp", "durationMs",
	];

	private static readonly string[] AllMessageKeys =
		[.. SystemKeys, .. UserKeys, .. AssistantKeys, .. ToolResultKeys];

	private static string[] KeysForRole(string? role) => role switch
	{
		"system" => SystemKeys,
		"user" => UserKeys,
		"assistant" => AssistantKeys,
		"toolResult" => ToolResultKeys,
		// 未知角色整段原始对象已经由调用方保留，这里用并集避免再分配一份 Extra。
		_ => AllMessageKeys,
	};

	private static Dictionary<string, string?> ReadSections(ref Utf8JsonReader reader)
	{
		var sections = new Dictionary<string, string?>(StringComparer.Ordinal);
		while (reader.Read())
		{
			if (reader.TokenType == JsonTokenType.EndObject)
			{
				break;
			}

			var name = reader.GetString();
			if (!reader.Read())
			{
				break;
			}

			sections[name!] = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
			if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
			{
				reader.Skip();
			}
		}

		return sections;
	}

	private static MessageContent ReadMessageContent(ref Utf8JsonReader reader) => reader.TokenType switch
	{
		JsonTokenType.String => MessageContent.FromText(ReadString(ref reader) ?? string.Empty),
		JsonTokenType.StartArray => MessageContent.FromBlocks(ReadBlocks(ref reader)),
		_ => SkipAndEmpty(ref reader),
	};

	private static MessageContent SkipAndEmpty(ref Utf8JsonReader reader)
	{
		reader.Skip();
		return MessageContent.Empty;
	}

	private static MessageContent? ReadReplacement(ref Utf8JsonReader reader)
	{
		MessageContent? content = null;
		while (reader.Read())
		{
			if (reader.TokenType == JsonTokenType.EndObject)
			{
				break;
			}

			var name = reader.GetString();
			if (!reader.Read())
			{
				break;
			}

			if (name == "content")
			{
				content = ReadMessageContent(ref reader);
			}
			else
			{
				reader.Skip();
			}
		}

		return content;
	}

	private static List<ContentBlock> ReadBlocks(ref Utf8JsonReader reader)
	{
		var blocks = new List<ContentBlock>();
		while (reader.Read())
		{
			if (reader.TokenType == JsonTokenType.EndArray)
			{
				break;
			}

			if (reader.TokenType == JsonTokenType.StartObject)
			{
				blocks.Add(ReadBlock(ref reader));
			}
			else
			{
				reader.Skip();
			}
		}

		return blocks;
	}

	private static ContentBlock ReadBlock(ref Utf8JsonReader reader)
	{
		var type = string.Empty;
		string? text = null;
		byte[]? textUtf8 = null;
		var textLength = 0;
		string? signature = null;
		string? thinking = null;
		string? thinkingSignature = null;
		bool? redacted = null;
		string? data = null;
		string? mimeType = null;
		string? id = null;
		string? name = null;
		JsonObject? arguments = null;
		string? argumentsError = null;
		string? thoughtSignature = null;
		string? toolNamespace = null;

		while (reader.Read())
		{
			if (reader.TokenType == JsonTokenType.EndObject)
			{
				break;
			}

			var key = reader.GetString();
			if (!reader.Read())
			{
				break;
			}

			switch (key)
			{
				case "type": type = Str(ref reader) ?? string.Empty; break;
				case "text":
					if (reader.TokenType != JsonTokenType.String)
					{
						reader.Skip();
						break;
					}

					// 大文本按 UTF-8 字节直接接管：未转义时 ValueSpan 就是文件原文，
					// 含转义（换行、引号）时解转义到 UTF-8 缓冲，都不经过 UTF-16。
					if (reader.ValueSpan.Length > TextContent.DeferredTextThreshold)
					{
						if (!reader.ValueIsEscaped)
						{
							var length = Encoding.UTF8.GetCharCount(reader.ValueSpan);
							if (length > TextContent.DeferredTextThreshold)
							{
								textUtf8 = reader.ValueSpan.ToArray();
								textLength = length;
								break;
							}
						}
						else if (SessionJsonUnescape.TryUnescape(reader.ValueSpan, out var unescaped, out var unescapedLength) &&
							unescapedLength > TextContent.DeferredTextThreshold)
						{
							textUtf8 = unescaped;
							textLength = unescapedLength;
							break;
						}
					}

					var value = reader.GetString() ?? string.Empty;
					if (value.Length > TextContent.DeferredTextThreshold && SessionJson.TryEncodeUtf8(value, out var bytes))
					{
						textUtf8 = bytes;
						textLength = value.Length;
						break;
					}

					text = value;
					break;
				case "textSignature": signature = Str(ref reader); break;
				case "thinking": thinking = Str(ref reader); break;
				case "thinkingSignature": thinkingSignature = Str(ref reader); break;
				case "redacted": redacted = Bool(ref reader); break;
				case "data": data = Str(ref reader); break;
				case "mimeType": mimeType = Str(ref reader); break;
				case "id": id = Str(ref reader); break;
				case "name": name = Str(ref reader); break;
				case "arguments":
					if (reader.TokenType == JsonTokenType.StartObject) arguments = JsonNode.Parse(ref reader) as JsonObject;
					else reader.Skip();
					break;
				case "argumentsError": argumentsError = Str(ref reader); break;
				case "thoughtSignature": thoughtSignature = Str(ref reader); break;
				case "namespace": toolNamespace = Str(ref reader); break;
				default: reader.Skip(); break;
			}
		}

		return type switch
		{
			"text" => textUtf8 is not null
				? TextContent.FromOwnedUtf8(textUtf8, textLength) with { TextSignature = signature }
				: new TextContent { Text = text ?? string.Empty, TextSignature = signature },
			"thinking" => new ThinkingContent
			{
				Thinking = thinking ?? string.Empty,
				ThinkingSignature = thinkingSignature,
				Redacted = redacted,
			},
			"image" => new ImageContent
			{
				Data = data ?? string.Empty,
				MimeType = mimeType ?? string.Empty,
			},
			"toolCall" => new ToolCall
			{
				Id = id ?? string.Empty,
				Name = name ?? string.Empty,
				Arguments = arguments ?? new JsonObject(),
				ArgumentsError = argumentsError,
				ThoughtSignature = thoughtSignature,
				Namespace = toolNamespace,
			},
			_ => throw new JsonException($"unknown content block type '{type}'"),
		};
	}

	private static Usage ReadUsage(ref Utf8JsonReader reader)
	{
		long input = 0, output = 0, cacheRead = 0, cacheWrite = 0, totalTokens = 0;
		long? cacheWrite1h = null, reasoning = null;
		var cost = new UsageCost();
		JsonObject? extra = null;
		JsonObject? costExtra = null;

		while (reader.Read())
		{
			if (reader.TokenType == JsonTokenType.EndObject)
			{
				break;
			}

			var name = reader.GetString();
			if (!reader.Read())
			{
				break;
			}

			switch (name)
			{
				case "input": input = Long(ref reader) ?? 0; break;
				case "output": output = Long(ref reader) ?? 0; break;
				case "cacheRead": cacheRead = Long(ref reader) ?? 0; break;
				case "cacheWrite": cacheWrite = Long(ref reader) ?? 0; break;
				case "cacheWrite1h": cacheWrite1h = Long(ref reader); break;
				case "reasoning": reasoning = Long(ref reader); break;
				case "totalTokens": totalTokens = Long(ref reader) ?? 0; break;
				case "cost":
					if (reader.TokenType == JsonTokenType.StartObject) (cost, costExtra) = ReadUsageCost(ref reader);
					else reader.Skip();
					break;
				default:
					extra ??= new JsonObject();
					extra[name!] = JsonNode.Parse(ref reader);
					break;
			}
		}

		return new Usage
		{
			Input = input,
			Output = output,
			CacheRead = cacheRead,
			CacheWrite = cacheWrite,
			CacheWrite1h = cacheWrite1h,
			Reasoning = reasoning,
			TotalTokens = totalTokens,
			Extra = extra,
			Cost = cost with { Extra = costExtra },
		};
	}

	private static (UsageCost Cost, JsonObject? Extra) ReadUsageCost(ref Utf8JsonReader reader)
	{
		double input = 0, output = 0, cacheRead = 0, cacheWrite = 0, total = 0;
		JsonObject? extra = null;
		while (reader.Read())
		{
			if (reader.TokenType == JsonTokenType.EndObject)
			{
				break;
			}

			var name = reader.GetString();
			if (!reader.Read())
			{
				break;
			}

			switch (name)
			{
				case "input": input = Double(ref reader) ?? 0; break;
				case "output": output = Double(ref reader) ?? 0; break;
				case "cacheRead": cacheRead = Double(ref reader) ?? 0; break;
				case "cacheWrite": cacheWrite = Double(ref reader) ?? 0; break;
				case "total": total = Double(ref reader) ?? 0; break;
				default:
					extra ??= new JsonObject();
					extra[name!] = JsonNode.Parse(ref reader);
					break;
			}
		}

		return (new UsageCost { Input = input, Output = output, CacheRead = cacheRead, CacheWrite = cacheWrite, Total = total }, extra);
	}

	private static List<Tool> ReadTools(ref Utf8JsonReader reader)
	{
		var tools = new List<Tool>();
		while (reader.Read())
		{
			if (reader.TokenType == JsonTokenType.EndArray)
			{
				break;
			}

			if (reader.TokenType != JsonTokenType.StartObject)
			{
				reader.Skip();
				continue;
			}

			var name = string.Empty;
			var description = string.Empty;
			JsonObject? parameters = null;
			JsonObject? constrained = null;
			while (reader.Read())
			{
				if (reader.TokenType == JsonTokenType.EndObject)
				{
					break;
				}

				var key = reader.GetString();
				if (!reader.Read())
				{
					break;
				}

				switch (key)
				{
					case "name": name = Str(ref reader) ?? string.Empty; break;
					case "description": description = Str(ref reader) ?? string.Empty; break;
					case "parameters":
						parameters = reader.TokenType == JsonTokenType.StartObject ? JsonNode.Parse(ref reader) as JsonObject : SkipObject(ref reader);
						break;
					case "constrainedSampling":
						constrained = reader.TokenType == JsonTokenType.StartObject ? JsonNode.Parse(ref reader) as JsonObject : SkipObject(ref reader);
						break;
					default: reader.Skip(); break;
				}
			}

			tools.Add(new Tool
			{
				Name = name,
				Description = description,
				Parameters = parameters ?? new JsonObject(),
				ConstrainedSampling = constrained,
			});
		}

		return tools;
	}

	private static JsonObject? SkipObject(ref Utf8JsonReader reader)
	{
		reader.Skip();
		return null;
	}

	private static List<ToolReference> ReadToolReferences(ref Utf8JsonReader reader)
	{
		var references = new List<ToolReference>();
		while (reader.Read())
		{
			if (reader.TokenType == JsonTokenType.EndArray)
			{
				break;
			}

			if (reader.TokenType != JsonTokenType.StartObject)
			{
				reader.Skip();
				continue;
			}

			string? name = null;
			while (reader.Read())
			{
				if (reader.TokenType == JsonTokenType.EndObject)
				{
					break;
				}

				var key = reader.GetString();
				if (!reader.Read())
				{
					break;
				}

				if (key == "name")
				{
					name = Str(ref reader);
				}
				else
				{
					reader.Skip();
				}
			}

			if (name is { Length: > 0 })
			{
				references.Add(new ToolReference { Name = name });
			}
		}

		return references;
	}

	private static string? ReadString(ref Utf8JsonReader reader) =>
		reader.TokenType == JsonTokenType.String ? reader.GetString() : null;

	private static string? Str(ref Utf8JsonReader reader) => ReadString(ref reader);

	private static bool? Bool(ref Utf8JsonReader reader) => reader.TokenType switch
	{
		JsonTokenType.True => true,
		JsonTokenType.False => false,
		_ => null,
	};

	private static long? Long(ref Utf8JsonReader reader) =>
		reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out var value) ? value : null;

	private static double? Double(ref Utf8JsonReader reader) =>
		reader.TokenType == JsonTokenType.Number && reader.TryGetDouble(out var value) ? value : null;
}
