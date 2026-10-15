using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace PiWeft.Ai.Json;

/// <summary>
/// 消息模型的手写 JSON 转换。所有分支都显式写出，因此在没有反射解析器的情况下
/// 类型依然满足 NativeAOT / 裁剪安全。
/// </summary>
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

	public static ContentBlock ReadBlock(JsonObject obj)
	{
		var type = Str(obj, "type") ?? throw new JsonException("content block is missing its type");
		return type switch
		{
			"text" => new TextContent
			{
				Text = Str(obj, "text") ?? string.Empty,
				TextSignature = Str(obj, "textSignature"),
			},
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
				ThoughtSignature = Str(obj, "thoughtSignature"),
				Namespace = Str(obj, "namespace"),
			},
			_ => throw new JsonException($"unknown content block type '{type}'"),
		};
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
			Cost = new UsageCost
			{
				Input = Double(cost, "input") ?? 0,
				Output = Double(cost, "output") ?? 0,
				CacheRead = Double(cost, "cacheRead") ?? 0,
				CacheWrite = Double(cost, "cacheWrite") ?? 0,
				Total = Double(cost, "total") ?? 0,
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
		writer.WriteEndObject();
		writer.WriteEndObject();
	}

	/// <summary>读取一个工具声明。与会话文件格式共用。</summary>
	public static Tool ReadTool(JsonObject obj) => new()
	{
		Name = Str(obj, "name") ?? string.Empty,
		Description = Str(obj, "description") ?? string.Empty,
		Parameters = Object(obj, "parameters") ?? new JsonObject(),
		ConstrainedSampling = obj.TryGetPropertyValue("constrainedSampling", out var constrained) ? constrained : null,
	};

	/// <summary>写出一个工具声明。与会话文件格式共用。</summary>
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

	/// <summary>从数组节点读取工具声明列表。</summary>
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

	/// <summary>写出工具声明列表。</summary>
	public static void WriteTools(Utf8JsonWriter writer, IReadOnlyList<Tool> tools)
	{
		writer.WriteStartArray();
		foreach (var tool in tools)
		{
			WriteTool(writer, tool);
		}

		writer.WriteEndArray();
	}

	/// <summary>从数组节点读取工具引用列表（<c>{ name }</c>）。</summary>
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

	/// <summary>读取 <c>string | ContentBlock[]</c> 形式的内容。与会话文件格式共用。</summary>
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

	/// <summary>写出 <c>string | ContentBlock[]</c> 形式的内容。与会话文件格式共用。</summary>
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
	public override Message Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
	{
		var node = JsonNode.Parse(ref reader);
		if (node is not JsonObject obj)
		{
			throw new JsonException("message must be an object");
		}

		return ReadMessage(obj);
	}

	/// <summary>从已解析的对象读取一条消息。与会话文件格式共用。</summary>
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
	};

	private static UserMessage ReadUser(JsonObject obj) => new()
	{
		Content = obj.TryGetPropertyValue("content", out var content) && content is not null
			? content is JsonValue
				? MessageContent.FromText(PiJsonValue.Str(content) ?? string.Empty)
				: MessageContent.FromBlocks(PiJsonValue.ReadBlocks(content))
			: MessageContent.Empty,
		Timestamp = PiJsonValue.Long(obj, "timestamp") ?? 0,
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
	};

	private static ToolResultMessage ReadToolResult(JsonObject obj) => new()
	{
		ToolCallId = PiJsonValue.Str(obj, "toolCallId") ?? string.Empty,
		ToolName = PiJsonValue.Str(obj, "toolName") ?? string.Empty,
		Content = PiJsonValue.ReadBlocks(obj.TryGetPropertyValue("content", out var content) ? content : null),
		Details = obj.TryGetPropertyValue("details", out var details) ? details : null,
		Usage = PiJsonValue.Object(obj, "usage") is { } usage ? PiJsonValue.ReadUsage(usage) : null,
		IsError = PiJsonValue.Bool(obj, "isError") ?? false,
		Timestamp = PiJsonValue.Long(obj, "timestamp") ?? 0,
		DurationMs = PiJsonValue.Double(obj, "durationMs"),
	};

	public override void Write(Utf8JsonWriter writer, Message value, JsonSerializerOptions options) =>
		WriteMessage(writer, value);

	/// <summary>把一条消息写进已打开的对象作用域。与会话文件格式共用。</summary>
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
				writer.WriteString("toolName", result.ToolName);
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
