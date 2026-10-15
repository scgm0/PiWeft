using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiWeft.Ai;
using PiWeft.Ai.Json;

namespace PiWeft.CodingAgent.Session;

/// <summary>
/// 读写 pi 的会话文件格式（JSONL，第 3 版）。全部手写，保证整条链路
/// 不使用反射，满足 NativeAOT 要求。
/// </summary>
public static class SessionJson
{
	private static readonly JsonWriterOptions WriterOptions = new()
	{
		Indented = false,
		SkipValidation = true,

		// pi 用 JS 的 JSON.stringify 写会话文件，它不转义 <、>、& 与 '。
		// 保持一致才能让文件逐字节可比、也更容易阅读。
		Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
	};

	/// <summary>把头部序列化成一行 JSONL（不带结尾换行）。</summary>
	public static string Serialize(SessionHeader header)
	{
		var buffer = new ArrayBufferWriter<byte>(256);
		using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
		{
			WriteHeader(writer, header);
			writer.Flush();
		}

		return Encoding.UTF8.GetString(buffer.WrittenSpan);
	}

	/// <summary>把一个条目序列化成一行 JSONL（不带结尾换行）。</summary>
	public static string Serialize(SessionEntry entry)
	{
		var buffer = new ArrayBufferWriter<byte>(512);
		using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
		{
			WriteEntry(writer, entry);
			writer.Flush();
		}

		return Encoding.UTF8.GetString(buffer.WrittenSpan);
	}

	public static void WriteHeader(Utf8JsonWriter writer, SessionHeader header)
	{
		writer.WriteStartObject();
		writer.WriteString("type", "session");
		writer.WriteNumber("version", header.Version);
		writer.WriteString("id", header.Id);
		writer.WriteString("timestamp", header.Timestamp);
		writer.WriteString("cwd", header.Cwd);
		if (header.ParentSession is { Length: > 0 } parent)
		{
			writer.WriteString("parentSession", parent);
		}

		writer.WriteEndObject();
	}

	public static void WriteEntry(Utf8JsonWriter writer, SessionEntry entry)
	{
		writer.WriteStartObject();
		writer.WriteString("type", entry.Type);
		writer.WriteString("id", entry.Id);
		if (entry.ParentId is null)
		{
			writer.WriteNull("parentId");
		}
		else
		{
			writer.WriteString("parentId", entry.ParentId);
		}

		writer.WriteString("timestamp", entry.Timestamp);

		switch (entry)
		{
			case SessionMessageEntry message:
				writer.WritePropertyName("message");
				MessageJsonConverter.WriteMessage(writer, message.Message);
				break;

			case ModelChangeEntry modelChange:
				writer.WriteString("provider", modelChange.Provider);
				writer.WriteString("modelId", modelChange.ModelId);
				break;

			case ThinkingLevelChangeEntry thinking:
				writer.WriteString("thinkingLevel", thinking.ThinkingLevel);
				break;

			case UsageEntry usage:
				writer.WriteString("kind", usage.Kind);
				writer.WriteString("provider", usage.Provider);
				writer.WriteString("model", usage.Model);
				writer.WritePropertyName("usage");
				PiJsonValue.WriteUsage(writer, usage.Usage);
				PiJsonValue.WriteOptional(writer, "note", usage.Note);
				break;

			case CompactionEntry compaction:
				writer.WriteString("summary", compaction.Summary);
				writer.WriteString("firstKeptEntryId", compaction.FirstKeptEntryId);
				writer.WriteNumber("tokensBefore", compaction.TokensBefore);
				WriteOptionalNode(writer, "details", compaction.Details);
				WriteOptionalUsage(writer, compaction.Usage);
				WriteOptionalBool(writer, "fromHook", compaction.FromHook);
				if (compaction.SystemMessage is { } checkpoint)
				{
					writer.WritePropertyName("systemMessage");
					MessageJsonConverter.WriteMessage(writer, checkpoint);
				}

				break;

			case BranchSummaryEntry branch:
				writer.WriteString("fromId", branch.FromId);
				writer.WriteString("summary", branch.Summary);
				WriteOptionalNode(writer, "details", branch.Details);
				WriteOptionalUsage(writer, branch.Usage);
				WriteOptionalBool(writer, "fromHook", branch.FromHook);
				break;

			case CustomEntry custom:
				writer.WriteString("customType", custom.CustomType);
				WriteOptionalNode(writer, "data", custom.Data);
				break;

			case CustomMessageEntry customMessage:
				writer.WriteString("customType", customMessage.CustomType);
				writer.WritePropertyName("content");
				PiJsonValue.WriteMessageContent(writer, customMessage.Content);
				WriteOptionalNode(writer, "details", customMessage.Details);
				writer.WriteBoolean("display", customMessage.Display);
				break;

			case ContextEditEntry edit:
				writer.WriteString("targetId", edit.TargetId);
				writer.WritePropertyName("replacement");
				if (edit.Replacement is { } replacement)
				{
					writer.WriteStartObject();
					writer.WritePropertyName("content");
					PiJsonValue.WriteMessageContent(writer, replacement);
					writer.WriteEndObject();
				}
				else
				{
					writer.WriteNullValue();
				}

				break;

			case LabelEntry label:
				writer.WriteString("targetId", label.TargetId);
				PiJsonValue.WriteOptional(writer, "label", label.Label);
				break;

			case SessionInfoEntry info:
				PiJsonValue.WriteOptional(writer, "name", info.Name);
				break;
		}

		writer.WriteEndObject();
	}

	/// <summary>把一行 JSONL 解析成 <see cref="SessionHeader"/> 或 <see cref="SessionEntry"/>。</summary>
	public static object? ParseLine(string line)
	{
		if (string.IsNullOrWhiteSpace(line))
		{
			return null;
		}

		var node = JsonNode.Parse(line);
		if (node is not JsonObject obj)
		{
			return null;
		}

		return ParseObject(obj);
	}

	public static object? ParseObject(JsonObject obj)
	{
		var type = PiJsonValue.Str(obj, "type");
		return type switch
		{
			"session" => ParseHeader(obj),
			null => null,
			_ => ParseEntry(obj, type),
		};
	}

	public static SessionHeader ParseHeader(JsonObject obj) => new()
	{
		Version = (int)(PiJsonValue.Long(obj, "version") ?? 1),
		Id = PiJsonValue.Str(obj, "id") ?? string.Empty,
		Timestamp = PiJsonValue.Str(obj, "timestamp") ?? string.Empty,
		Cwd = PiJsonValue.Str(obj, "cwd") ?? string.Empty,
		ParentSession = PiJsonValue.Str(obj, "parentSession"),
	};

	public static SessionEntry ParseEntry(JsonObject obj, string type)
	{
		var id = PiJsonValue.Str(obj, "id") ?? string.Empty;
		var parentId = PiJsonValue.Str(obj, "parentId");
		var timestamp = PiJsonValue.Str(obj, "timestamp") ?? string.Empty;

		SessionEntry entry = type switch
		{
			"message" => new SessionMessageEntry
			{
				Message = obj["message"] is JsonObject message
					? MessageJsonConverter.ReadMessage(message)
					: new UserMessage { Content = MessageContent.Empty, Timestamp = 0 },
			},
			"model_change" => new ModelChangeEntry
			{
				Provider = PiJsonValue.Str(obj, "provider") ?? string.Empty,
				ModelId = PiJsonValue.Str(obj, "modelId") ?? string.Empty,
			},
			"thinking_level_change" => new ThinkingLevelChangeEntry
			{
				ThinkingLevel = PiJsonValue.Str(obj, "thinkingLevel") ?? ThinkingLevels.Off,
			},
			"usage" => new UsageEntry
			{
				Kind = PiJsonValue.Str(obj, "kind") ?? string.Empty,
				Provider = PiJsonValue.Str(obj, "provider") ?? string.Empty,
				Model = PiJsonValue.Str(obj, "model") ?? string.Empty,
				Usage = PiJsonValue.ReadUsage(PiJsonValue.Object(obj, "usage")),
				Note = PiJsonValue.Str(obj, "note"),
			},
			"compaction" => new CompactionEntry
			{
				Summary = PiJsonValue.Str(obj, "summary") ?? string.Empty,
				FirstKeptEntryId = PiJsonValue.Str(obj, "firstKeptEntryId") ?? string.Empty,
				TokensBefore = PiJsonValue.Long(obj, "tokensBefore") ?? 0,
				Details = obj.TryGetPropertyValue("details", out var details) ? details : null,
				Usage = PiJsonValue.Object(obj, "usage") is { } compactionUsage
					? PiJsonValue.ReadUsage(compactionUsage)
					: null,
				FromHook = PiJsonValue.Bool(obj, "fromHook"),
				SystemMessage = PiJsonValue.Object(obj, "systemMessage") is { } checkpoint
					? MessageJsonConverter.ReadMessage(checkpoint) as SystemMessage
					: null,
			},
			"branch_summary" => new BranchSummaryEntry
			{
				FromId = PiJsonValue.Str(obj, "fromId") ?? string.Empty,
				Summary = PiJsonValue.Str(obj, "summary") ?? string.Empty,
				Details = obj.TryGetPropertyValue("details", out var branchDetails) ? branchDetails : null,
				Usage = PiJsonValue.Object(obj, "usage") is { } branchUsage
					? PiJsonValue.ReadUsage(branchUsage)
					: null,
				FromHook = PiJsonValue.Bool(obj, "fromHook"),
			},
			"custom" => new CustomEntry
			{
				CustomType = PiJsonValue.Str(obj, "customType") ?? string.Empty,
				Data = obj.TryGetPropertyValue("data", out var data) ? data : null,
			},
			"custom_message" => new CustomMessageEntry
			{
				CustomType = PiJsonValue.Str(obj, "customType") ?? string.Empty,
				Content = PiJsonValue.ReadMessageContent(obj.TryGetPropertyValue("content", out var content) ? content : null),
				Details = obj.TryGetPropertyValue("details", out var customDetails) ? customDetails : null,
				Display = PiJsonValue.Bool(obj, "display") ?? true,
			},
			"context_edit" => new ContextEditEntry
			{
				TargetId = PiJsonValue.Str(obj, "targetId") ?? string.Empty,
				Replacement = PiJsonValue.Object(obj, "replacement") is { } replacement
					? (MessageContent?)PiJsonValue.ReadMessageContent(
						replacement.TryGetPropertyValue("content", out var replacementContent) ? replacementContent : null)
					: null,
			},
			"label" => new LabelEntry
			{
				TargetId = PiJsonValue.Str(obj, "targetId") ?? string.Empty,
				Label = PiJsonValue.Str(obj, "label"),
			},
			"session_info" => new SessionInfoEntry
			{
				Name = PiJsonValue.Str(obj, "name"),
			},
			_ => new CustomEntry { CustomType = type, Data = obj.DeepClone() },
		};

		return entry with { Id = id, ParentId = parentId, Timestamp = timestamp };
	}

	private static void WriteOptionalNode(Utf8JsonWriter writer, string name, JsonNode? node)
	{
		if (node is null)
		{
			return;
		}

		writer.WritePropertyName(name);
		node.WriteTo(writer);
	}

	private static void WriteOptionalUsage(Utf8JsonWriter writer, Usage? usage)
	{
		if (usage is null)
		{
			return;
		}

		writer.WritePropertyName("usage");
		PiJsonValue.WriteUsage(writer, usage);
	}

	private static void WriteOptionalBool(Utf8JsonWriter writer, string name, bool? value)
	{
		if (value is not null)
		{
			writer.WriteBoolean(name, value.Value);
		}
	}
}
