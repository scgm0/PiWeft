using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Unicode;

namespace PiWeft.Ai.Json;

/// 消息模型的手写 JSON 转换。所有分支都显式写出，因此在没有反射解析器的情况下
/// 类型依然满足 NativeAOT / 裁剪安全。
internal static class PiJsonValue
{
	public static string? Str(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var s) ? s : null;

	public static string? Str(JsonObject obj, string name) => obj.TryGetPropertyValue(name, out var node) ? Str(node) : null;

	public static bool? Bool(JsonNode? node) => node is JsonValue value && value.TryGetValue<bool>(out var b) ? b : null;

	public static bool? Bool(JsonObject obj, string name) => obj.TryGetPropertyValue(name, out var node) ? Bool(node) : null;

	public static long? Long(JsonNode? node) => node is JsonValue value && value.TryGetValue<long>(out var l) ? l : null;

	public static long? Long(JsonObject obj, string name) => obj.TryGetPropertyValue(name, out var node) ? Long(node) : null;

	public static double? Double(JsonNode? node) => node is JsonValue value && value.TryGetValue<double>(out var d) ? d : null;

	public static double? Double(JsonObject obj, string name) => obj.TryGetPropertyValue(name, out var node) ? Double(node) : null;

	public static JsonObject? Object(JsonObject obj, string name) =>
		obj.TryGetPropertyValue(name, out var node) ? node as JsonObject : null;

	public static List<JsonNode?>? Array(JsonObject obj, string name) =>
		obj.TryGetPropertyValue(name, out var node) && node is JsonArray array ? [.. array] : null;

	/// 收集未建模的字段，重写时原样输出。pi 与各类会话工具会往消息里加字段
	/// （例如 codemode 在 toolResult 上写 <c>nestedCalls</c>），模型层不认识也要原样搬运：
	/// 丢弃等于改写用户历史，<c>fork</c>、导入后再写回时那些数据就没了。
	/// 与配置侧的「未知字段保留」是同一条纪律。
	public static JsonObject? ReadExtras(JsonObject obj, params ReadOnlySpan<string> known)
	{
		JsonObject? extras = null;
		foreach (var (key, value) in obj)
		{
			if (known.Contains(key))
			{
				continue;
			}

			extras ??= new JsonObject();
			extras[key] = value?.DeepClone();
		}

		return extras;
	}

	public static void WriteExtras(Utf8JsonWriter writer, JsonObject? extras)
	{
		if (extras is null)
		{
			return;
		}

		foreach (var (key, value) in extras)
		{
			if (value is null)
			{
				writer.WriteNull(key);
			}
			else
			{
				writer.WritePropertyName(key);
				value.WriteTo(writer);
			}
		}
	}

	public static ContentBlock ReadBlock(JsonObject obj)
	{
		var type = Str(obj, "type") ?? throw new JsonException("content block is missing its type");
		return type switch
		{
			"text" => ReadTextBlock(obj),
			"thinking" => new ThinkingContent
			{
				Thinking = Str(obj, "thinking") ?? string.Empty,
				ThinkingSignature = Str(obj, "thinkingSignature"),
				Redacted = Bool(obj, "redacted"),
			},
			"image" => new ImageContent
			{
				Data = Str(obj, "data") ?? string.Empty,
				MimeType = Str(obj, "mimeType") ?? string.Empty,
			},
			"toolCall" => new ToolCall
			{
				Id = Str(obj, "id") ?? string.Empty,
				Name = Str(obj, "name") ?? string.Empty,
				Arguments = Object(obj, "arguments") ?? new JsonObject(),
				ArgumentsError = Str(obj, "argumentsError"),
				ThoughtSignature = Str(obj, "thoughtSignature"),
				Namespace = Str(obj, "namespace"),
			},
			_ => throw new JsonException($"unknown content block type '{type}'"),
		};
	}

	/// 大段正文（工具输出）先按字符串读出、立刻编码成 UTF-8 再丢掉字符串：稳态只留一份
	/// UTF-8，峰值多一份临时字符串（逐行解析，随即变成垃圾）。往返仍按字符串写，输出逐字节不变。
	private static TextContent ReadTextBlock(JsonObject obj)
	{
		var text = Str(obj, "text") ?? string.Empty;
		var signature = Str(obj, "textSignature");
		if (text.Length > TextContent.DeferredTextThreshold && TryEncodeUtf8(text, out var utf8))
		{
			return TextContent.FromOwnedUtf8(utf8, text.Length) with { TextSignature = signature };
		}

		return new TextContent { Text = text, TextSignature = signature };
	}

	/// 编码的同时校验 UTF-16：孤立代理项编成 UTF-8 会变成 U+FFFD，回来就不是同一段文字了
	/// （会话文件里可以出现 <c>"\ud800"</c> 这类转义，内存里构造的 JsonObject 也可能带）。
	/// 这类输入建不了延迟存储，退回字符串。校验与编码同一趟，不额外扫一遍正文。
	private static bool TryEncodeUtf8(string text, out byte[] utf8)
	{
		var bytes = GC.AllocateUninitializedArray<byte>(Encoding.UTF8.GetByteCount(text));
		if (Utf8.FromUtf16(text, bytes, out _, out var written, replaceInvalidSequences: false) != OperationStatus.Done)
		{
			utf8 = [];
			return false;
		}

		// 长度已在上面算过，写不满只可能是编码器判了非法输入；这种情况已经在上面返回了。
		utf8 = written == bytes.Length ? bytes : bytes[..written];
		return true;
	}

	public static List<ContentBlock> ReadBlocks(JsonNode? node)
	{
		var result = new List<ContentBlock>();
		if (node is JsonArray array)
		{
			foreach (var item in array)
			{
				if (item is JsonObject obj)
				{
					result.Add(ReadBlock(obj));
				}
			}
		}

		return result;
	}

	public static void WriteBlock(Utf8JsonWriter writer, ContentBlock block)
	{
		writer.WriteStartObject();
		switch (block)
		{
			case TextContent text:
				writer.WriteString("type", "text");
				writer.WriteString("text", text.Text);
				WriteOptional(writer, "textSignature", text.TextSignature);
				break;
			case ThinkingContent thinking:
				writer.WriteString("type", "thinking");
				writer.WriteString("thinking", thinking.Thinking);
				WriteOptional(writer, "thinkingSignature", thinking.ThinkingSignature);
				if (thinking.Redacted is { } redacted)
				{
					writer.WriteBoolean("redacted", redacted);
				}

				break;
			case ImageContent image:
				writer.WriteString("type", "image");
				writer.WriteString("data", image.Data);
				writer.WriteString("mimeType", image.MimeType);
				break;
			case ToolCall call:
				writer.WriteString("type", "toolCall");
				writer.WriteString("id", call.Id);
				writer.WriteString("name", call.Name);
				writer.WritePropertyName("arguments");
				call.Arguments.WriteTo(writer);
				WriteOptional(writer, "argumentsError", call.ArgumentsError);
				WriteOptional(writer, "thoughtSignature", call.ThoughtSignature);
				WriteOptional(writer, "namespace", call.Namespace);
				break;
			default:
				throw new JsonException($"unknown content block {block.GetType().Name}");
		}

		writer.WriteEndObject();
	}

	public static void WriteBlocks(Utf8JsonWriter writer, IReadOnlyList<ContentBlock> blocks)
	{
		writer.WriteStartArray();
		foreach (var block in blocks)
		{
			WriteBlock(writer, block);
		}

		writer.WriteEndArray();
	}

	private static readonly string[] UsageKeys =
		["input", "output", "cacheRead", "cacheWrite", "cacheWrite1h", "reasoning", "totalTokens", "cost"];

	private static readonly string[] UsageCostKeys = ["input", "output", "cacheRead", "cacheWrite", "total"];

	public static Usage ReadUsage(JsonObject? obj)
	{
		if (obj is null)
		{
			return new Usage();
		}

		var cost = Object(obj, "cost") ?? new JsonObject();
		return new Usage
		{
			Input = Long(obj, "input") ?? 0,
			Output = Long(obj, "output") ?? 0,
			CacheRead = Long(obj, "cacheRead") ?? 0,
			CacheWrite = Long(obj, "cacheWrite") ?? 0,
			CacheWrite1h = Long(obj, "cacheWrite1h"),
			Reasoning = Long(obj, "reasoning"),
			TotalTokens = Long(obj, "totalTokens") ?? 0,
			Extra = ReadExtras(obj, UsageKeys),
			Cost = new UsageCost
			{
				Input = Double(cost, "input") ?? 0,
				Output = Double(cost, "output") ?? 0,
				CacheRead = Double(cost, "cacheRead") ?? 0,
				CacheWrite = Double(cost, "cacheWrite") ?? 0,
				Total = Double(cost, "total") ?? 0,
				Extra = ReadExtras(cost, UsageCostKeys),
			},
		};
	}

	public static void WriteUsage(Utf8JsonWriter writer, Usage usage)
	{
		writer.WriteStartObject();
		writer.WriteNumber("input", usage.Input);
		writer.WriteNumber("output", usage.Output);
		writer.WriteNumber("cacheRead", usage.CacheRead);
		writer.WriteNumber("cacheWrite", usage.CacheWrite);
		if (usage.CacheWrite1h is { } cacheWrite1h)
		{
			writer.WriteNumber("cacheWrite1h", cacheWrite1h);
		}

		if (usage.Reasoning is { } reasoning)
		{
			writer.WriteNumber("reasoning", reasoning);
		}

		writer.WriteNumber("totalTokens", usage.TotalTokens);
		writer.WritePropertyName("cost");
		writer.WriteStartObject();
		writer.WriteNumber("input", usage.Cost.Input);
		writer.WriteNumber("output", usage.Cost.Output);
		writer.WriteNumber("cacheRead", usage.Cost.CacheRead);
		writer.WriteNumber("cacheWrite", usage.Cost.CacheWrite);
		writer.WriteNumber("total", usage.Cost.Total);
		WriteExtras(writer, usage.Cost.Extra);
		writer.WriteEndObject();
		WriteExtras(writer, usage.Extra);
		writer.WriteEndObject();
	}

	public static Tool ReadTool(JsonObject obj) => new()
	{
		Name = Str(obj, "name") ?? string.Empty,
		Description = Str(obj, "description") ?? string.Empty,
		Parameters = Object(obj, "parameters") ?? new JsonObject(),
		ConstrainedSampling = obj.TryGetPropertyValue("constrainedSampling", out var constrained) ? constrained : null,
	};

	public static void WriteTool(Utf8JsonWriter writer, Tool tool)
	{
		writer.WriteStartObject();
		writer.WriteString("name", tool.Name);
		writer.WriteString("description", tool.Description);
		writer.WritePropertyName("parameters");
		tool.Parameters.WriteTo(writer);
		if (tool.ConstrainedSampling is { } constrained)
		{
			writer.WritePropertyName("constrainedSampling");
			constrained.WriteTo(writer);
		}

		writer.WriteEndObject();
	}

	public static List<Tool> ReadTools(JsonNode? node)
	{
		var tools = new List<Tool>();
		if (node is JsonArray array)
		{
			foreach (var item in array)
			{
				if (item is JsonObject obj)
				{
					tools.Add(ReadTool(obj));
				}
			}
		}

		return tools;
	}

	public static void WriteTools(Utf8JsonWriter writer, IReadOnlyList<Tool> tools)
	{
		writer.WriteStartArray();
		foreach (var tool in tools)
		{
			WriteTool(writer, tool);
		}

		writer.WriteEndArray();
	}

	public static List<ToolReference> ReadToolReferences(JsonNode? node)
	{
		var references = new List<ToolReference>();
		if (node is JsonArray array)
		{
			foreach (var item in array)
			{
				if (item is JsonObject obj && Str(obj, "name") is { Length: > 0 } name)
				{
					references.Add(new ToolReference { Name = name });
				}
			}
		}

		return references;
	}

	public static void WriteOptional(Utf8JsonWriter writer, string name, string? value)
	{
		if (value is not null)
		{
			writer.WriteString(name, value);
		}
	}

	public static MessageContent ReadMessageContent(JsonNode? node)
	{
		if (node is null)
		{
			return MessageContent.Empty;
		}

		return node is JsonValue
			? MessageContent.FromText(Str(node) ?? string.Empty)
			: MessageContent.FromBlocks(ReadBlocks(node));
	}

	public static void WriteMessageContent(Utf8JsonWriter writer, MessageContent content)
	{
		if (content.IsText)
		{
			writer.WriteStringValue(content.Text);
			return;
		}

		WriteBlocks(writer, content.Blocks ?? []);
	}
}

internal sealed class ContentBlockJsonConverter : JsonConverter<ContentBlock>
{
	public override ContentBlock Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
	{
		var node = JsonNode.Parse(ref reader);
		if (node is not JsonObject obj)
		{
			throw new JsonException("content block must be an object");
		}

		return PiJsonValue.ReadBlock(obj);
	}

	public override void Write(Utf8JsonWriter writer, ContentBlock value, JsonSerializerOptions options) =>
		PiJsonValue.WriteBlock(writer, value);
}

internal sealed class MessageContentJsonConverter : JsonConverter<MessageContent>
{
	public override MessageContent Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
	{
		if (reader.TokenType == JsonTokenType.String)
		{
			return MessageContent.FromText(reader.GetString() ?? string.Empty);
		}

		var node = JsonNode.Parse(ref reader);
		return MessageContent.FromBlocks(PiJsonValue.ReadBlocks(node));
	}

	public override void Write(Utf8JsonWriter writer, MessageContent value, JsonSerializerOptions options)
	{
		if (value.IsText)
		{
			writer.WriteStringValue(value.Text);
			return;
		}

		PiJsonValue.WriteBlocks(writer, value.Blocks ?? []);
	}
}

internal sealed class MessageJsonConverter : JsonConverter<Message>
{
	// 每种角色自己消费的字段名单。名单外的字段进 Message.Extra，写回时原样输出。
	// 新增字段时同步名单，否则重写时会被当成未知字段重复输出。
	private static readonly string[] SystemKeys = ["role", "content", "sections", "toolsAdded", "toolsRemoved", "timestamp"];

	// customType/display 是 pi 的注入消息标记：漏写的话重启后回报就变回普通用户提问。
	private static readonly string[] UserKeys = ["role", "content", "customType", "display", "timestamp"];

	private static readonly string[] AssistantKeys =
	[
		"role", "content", "api", "provider", "model", "responseModel", "responseId",
		"providerThinkingLevel", "thinkingLevel", "usage", "stopReason", "errorMessage",
		"rawStopReason", "endTurn", "durationMs", "firstTokenMs", "streamingDurationMs", "timestamp",
	];

	private static readonly string[] ToolResultKeys =
		["role", "toolCallId", "toolName", "content", "details", "usage", "isError", "durationMs", "timestamp"];

	public override Message Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
	{
		var node = JsonNode.Parse(ref reader);
		if (node is not JsonObject obj)
		{
			throw new JsonException("message must be an object");
		}

		return ReadMessage(obj);
	}

	internal static Message ReadMessage(JsonObject obj)
	{
		var role = PiJsonValue.Str(obj, "role") ?? throw new JsonException("message is missing its role");
		return role switch
		{
			"system" => ReadSystem(obj),
			"user" => ReadUser(obj),
			"assistant" => ReadAssistant(obj),
			"toolResult" => ReadToolResult(obj),
			_ => new CustomMessage
			{
				CustomRole = role,
				Content = PiJsonValue.ReadMessageContent(obj.TryGetPropertyValue("content", out var rawContent) ? rawContent : null),
				Timestamp = PiJsonValue.Long(obj, "timestamp") ?? 0,
				Raw = (JsonObject)obj.DeepClone(),
			},
		};
	}

	private static SystemMessage ReadSystem(JsonObject obj) => new()
	{
		Content = obj.TryGetPropertyValue("content", out var content) && content is not null
			? content is JsonValue
				? MessageContent.FromText(PiJsonValue.Str(content) ?? string.Empty)
				: MessageContent.FromBlocks(PiJsonValue.ReadBlocks(content))
			: MessageContent.Empty,
		Sections = PiJsonValue.Object(obj, "sections") is { } sections
			? sections.ToDictionary(p => p.Key, p => PiJsonValue.Str(p.Value))
			: null,
		ToolsAdded = PiJsonValue.ReadTools(obj.TryGetPropertyValue("toolsAdded", out var added) ? added : null) is { Count: > 0 } addedTools
			? addedTools
			: null,
		ToolsRemoved = PiJsonValue.ReadToolReferences(obj.TryGetPropertyValue("toolsRemoved", out var removed) ? removed : null)
			is { Count: > 0 } removedTools
			? removedTools
			: null,
		Timestamp = PiJsonValue.Long(obj, "timestamp") ?? 0,
		Extra = PiJsonValue.ReadExtras(obj, SystemKeys),
	};

	private static UserMessage ReadUser(JsonObject obj) => new()
	{
		Content = obj.TryGetPropertyValue("content", out var content) && content is not null
			? content is JsonValue
				? MessageContent.FromText(PiJsonValue.Str(content) ?? string.Empty)
				: MessageContent.FromBlocks(PiJsonValue.ReadBlocks(content))
			: MessageContent.Empty,
		CustomType = PiJsonValue.Str(obj, "customType"),
		Display = PiJsonValue.Bool(obj, "display") ?? true,
		Timestamp = PiJsonValue.Long(obj, "timestamp") ?? 0,
		Extra = PiJsonValue.ReadExtras(obj, UserKeys),
	};

	private static AssistantMessage ReadAssistant(JsonObject obj) => new()
	{
		Content = PiJsonValue.ReadBlocks(obj.TryGetPropertyValue("content", out var content) ? content : null),
		Api = PiJsonValue.Str(obj, "api") ?? string.Empty,
		Provider = PiJsonValue.Str(obj, "provider") ?? string.Empty,
		Model = PiJsonValue.Str(obj, "model") ?? string.Empty,
		ResponseModel = PiJsonValue.Str(obj, "responseModel"),
		ResponseId = PiJsonValue.Str(obj, "responseId"),
		ProviderThinkingLevel = PiJsonValue.Str(obj, "providerThinkingLevel"),
		ThinkingLevel = PiJsonValue.Str(obj, "thinkingLevel"),
		Usage = PiJsonValue.ReadUsage(PiJsonValue.Object(obj, "usage")),
		StopReason = StopReasonExtensions.ParseStopReason(PiJsonValue.Str(obj, "stopReason")),
		ErrorMessage = PiJsonValue.Str(obj, "errorMessage"),
		RawStopReason = PiJsonValue.Str(obj, "rawStopReason"),
		EndTurn = PiJsonValue.Bool(obj, "endTurn"),
		Timestamp = PiJsonValue.Long(obj, "timestamp") ?? 0,
		DurationMs = PiJsonValue.Double(obj, "durationMs"),
		FirstTokenMs = PiJsonValue.Double(obj, "firstTokenMs"),
		StreamingDurationMs = PiJsonValue.Double(obj, "streamingDurationMs"),
		Extra = PiJsonValue.ReadExtras(obj, AssistantKeys),
	};

	private static ToolResultMessage ReadToolResult(JsonObject obj) => new()
	{
		ToolCallId = PiJsonValue.Str(obj, "toolCallId") ?? string.Empty,
		ToolName = PiJsonValue.Str(obj, "toolName") ?? string.Empty,
		HasToolName = obj.ContainsKey("toolName"),
		Content = PiJsonValue.ReadBlocks(obj.TryGetPropertyValue("content", out var content) ? content : null),
		Details = obj.TryGetPropertyValue("details", out var details) ? details : null,
		Usage = PiJsonValue.Object(obj, "usage") is { } usage ? PiJsonValue.ReadUsage(usage) : null,
		IsError = PiJsonValue.Bool(obj, "isError") ?? false,
		Timestamp = PiJsonValue.Long(obj, "timestamp") ?? 0,
		DurationMs = PiJsonValue.Double(obj, "durationMs"),
		Extra = PiJsonValue.ReadExtras(obj, ToolResultKeys),
	};

	public override void Write(Utf8JsonWriter writer, Message value, JsonSerializerOptions options) =>
		WriteMessage(writer, value);

	internal static void WriteMessage(Utf8JsonWriter writer, Message value)
	{
		if (value is CustomMessage { Raw: { } raw })
		{
			// 未知角色与未知字段都按读取时的原样保留。
			raw.WriteTo(writer);
			return;
		}

		writer.WriteStartObject();
		switch (value)
		{
			case SystemMessage system:
				writer.WriteString("role", "system");
				writer.WritePropertyName("content");
				WriteMessageContent(writer, system.Content);
				if (system.Sections is { Count: > 0 } sections)
				{
					writer.WritePropertyName("sections");
					writer.WriteStartObject();
					foreach (var (key, section) in sections)
					{
						if (section is null)
						{
							writer.WriteNull(key);
						}
						else
						{
							writer.WriteString(key, section);
						}
					}

					writer.WriteEndObject();
				}

				if (system.ToolsAdded is { Count: > 0 } toolsAdded)
				{
					writer.WritePropertyName("toolsAdded");
					PiJsonValue.WriteTools(writer, toolsAdded);
				}

				if (system.ToolsRemoved is { Count: > 0 } toolsRemoved)
				{
					writer.WritePropertyName("toolsRemoved");
					writer.WriteStartArray();
					foreach (var reference in toolsRemoved)
					{
						writer.WriteStartObject();
						writer.WriteString("name", reference.Name);
						writer.WriteEndObject();
					}

					writer.WriteEndArray();
				}

				break;
			case UserMessage user:
				writer.WriteString("role", "user");
				writer.WritePropertyName("content");
				WriteMessageContent(writer, user.Content);
				// 注入消息（回报、关联会话、继续生成收据）靠这两项与用户提问区分；
				// 不写回去，重启后分叉点、重试、标题与导出都会把它们当成用户说的话。
				PiJsonValue.WriteOptional(writer, "customType", user.CustomType);
				if (!user.Display)
				{
					writer.WriteBoolean("display", false);
				}

				break;
			case AssistantMessage assistant:
				writer.WriteString("role", "assistant");
				writer.WritePropertyName("content");
				PiJsonValue.WriteBlocks(writer, assistant.Content);
				writer.WriteString("api", assistant.Api);
				writer.WriteString("provider", assistant.Provider);
				writer.WriteString("model", assistant.Model);
				PiJsonValue.WriteOptional(writer, "responseModel", assistant.ResponseModel);
				PiJsonValue.WriteOptional(writer, "responseId", assistant.ResponseId);
				PiJsonValue.WriteOptional(writer, "providerThinkingLevel", assistant.ProviderThinkingLevel);
				PiJsonValue.WriteOptional(writer, "thinkingLevel", assistant.ThinkingLevel);
				writer.WritePropertyName("usage");
				PiJsonValue.WriteUsage(writer, assistant.Usage);
				writer.WriteString("stopReason", assistant.StopReason.ToWire());
				PiJsonValue.WriteOptional(writer, "errorMessage", assistant.ErrorMessage);
				PiJsonValue.WriteOptional(writer, "rawStopReason", assistant.RawStopReason);
				if (assistant.EndTurn is { } endTurn)
				{
					writer.WriteBoolean("endTurn", endTurn);
				}

				if (assistant.DurationMs is { } durationMs)
				{
					writer.WriteNumber("durationMs", durationMs);
				}

				if (assistant.FirstTokenMs is { } firstTokenMs)
				{
					writer.WriteNumber("firstTokenMs", firstTokenMs);
				}

				if (assistant.StreamingDurationMs is { } streamingDurationMs)
				{
					writer.WriteNumber("streamingDurationMs", streamingDurationMs);
				}

				break;
			case ToolResultMessage result:
				writer.WriteString("role", "toolResult");
				writer.WriteString("toolCallId", result.ToolCallId);
				if (result.HasToolName)
				{
					writer.WriteString("toolName", result.ToolName);
				}
				writer.WritePropertyName("content");
				PiJsonValue.WriteBlocks(writer, result.Content);
				if (result.Details is not null)
				{
					writer.WritePropertyName("details");
					result.Details.WriteTo(writer);
				}

				if (result.Usage is not null)
				{
					writer.WritePropertyName("usage");
					PiJsonValue.WriteUsage(writer, result.Usage);
				}

				writer.WriteBoolean("isError", result.IsError);
				if (result.DurationMs is { } resultDuration)
				{
					writer.WriteNumber("durationMs", resultDuration);
				}

				break;
			default:
				throw new JsonException($"unknown message {value.GetType().Name}");
		}

		writer.WriteNumber("timestamp", value.Timestamp);
		PiJsonValue.WriteExtras(writer, value.Extra);
		writer.WriteEndObject();
	}

	private static void WriteMessageContent(Utf8JsonWriter writer, MessageContent content)
	{
		if (content.IsText)
		{
			writer.WriteStringValue(content.Text);
		}
		else
		{
			PiJsonValue.WriteBlocks(writer, content.Blocks ?? []);
		}
	}
}
