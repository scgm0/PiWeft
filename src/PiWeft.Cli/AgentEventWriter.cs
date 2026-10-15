using System.Text.Json;
using System.Text.Json.Nodes;
using PiWeft.Ai;
using PiWeft.Ai.Json;
using PiWeft.Agent;

namespace PiWeft.Cli;

/// <summary>
/// 写出代理事件的 JSON/RPC 线格式，与 pi 的 <c>docs/json.md</c> 一致：更新只发增量，
/// 因此省略累积消息与 <c>assistantMessageEvent.partial</c> 快照，流体积保持线性。
/// </summary>
public static class AgentEventWriter
{
	private static readonly JsonWriterOptions WriterOptions = new()
	{
		Indented = false,
		SkipValidation = true,
		Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
	};

	/// <summary>把一个事件序列化成单行 JSONL（不带结尾换行）。</summary>
	public static string Write(AgentEvent @event)
	{
		using var buffer = new MemoryStream(256);
		using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
		{
			Write(writer, @event);
		}

		return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
	}

	public static void Write(Utf8JsonWriter writer, AgentEvent @event)
	{
		writer.WriteStartObject();
		switch (@event)
		{
			case AutoRetryStartEvent retry:
				writer.WriteString("type", "auto_retry_start");
				writer.WriteNumber("attempt", retry.Attempt);
				writer.WriteNumber("maxAttempts", retry.MaxAttempts);
				writer.WriteNumber("delayMs", retry.DelayMs);
				writer.WriteString("errorMessage", retry.ErrorMessage);
				break;

			case AutoRetryEndEvent retry:
				writer.WriteString("type", "auto_retry_end");
				writer.WriteBoolean("success", retry.Success);
				writer.WriteNumber("attempt", retry.Attempt);
				if (retry.FinalError is not null)
				{
					writer.WriteString("finalError", retry.FinalError);
				}

				break;

			case AgentStartEvent:
				writer.WriteString("type", "agent_start");
				break;

			case AgentEndEvent end:
				writer.WriteString("type", "agent_end");
				writer.WritePropertyName("messages");
				WriteMessages(writer, end.Messages);
				writer.WriteBoolean("willRetry", false);
				break;

			case TurnStartEvent:
				writer.WriteString("type", "turn_start");
				break;

			case TurnEndEvent turnEnd:
				writer.WriteString("type", "turn_end");
				writer.WritePropertyName("message");
				MessageJsonConverter.WriteMessage(writer, turnEnd.Message);
				writer.WritePropertyName("toolResults");
				WriteMessages(writer, turnEnd.ToolResults);
				break;

			case MessageStartEvent start:
				writer.WriteString("type", "message_start");
				writer.WritePropertyName("message");
				MessageJsonConverter.WriteMessage(writer, start.Message);
				break;

			case MessageUpdateEvent update:
				writer.WriteString("type", "message_update");
				writer.WritePropertyName("usage");
				PiJsonValue.WriteUsage(writer, update.AssistantMessageEvent.Partial.Usage);
				writer.WritePropertyName("assistantMessageEvent");
				WriteAssistantMessageEvent(writer, update.AssistantMessageEvent);
				break;

			case MessageEndEvent end:
				writer.WriteString("type", "message_end");
				writer.WritePropertyName("message");
				MessageJsonConverter.WriteMessage(writer, end.Message);
				break;

			case ToolExecutionStartEvent toolStart:
				writer.WriteString("type", "tool_execution_start");
				writer.WriteString("toolCallId", toolStart.ToolCallId);
				writer.WriteString("toolName", toolStart.ToolName);
				writer.WritePropertyName("args");
				toolStart.Arguments.WriteTo(writer);
				break;

			case ToolExecutionUpdateEvent toolUpdate:
				writer.WriteString("type", "tool_execution_update");
				writer.WriteString("toolCallId", toolUpdate.ToolCallId);
				writer.WriteString("toolName", toolUpdate.ToolName);
				break;

			case ToolExecutionEndEvent toolEnd:
				writer.WriteString("type", "tool_execution_end");
				writer.WriteString("toolCallId", toolEnd.ToolCallId);
				writer.WriteString("toolName", toolEnd.ToolName);
				writer.WriteBoolean("isError", toolEnd.IsError);
				if (toolEnd.DurationMs is { } duration)
				{
					writer.WriteNumber("durationMs", duration);
				}

				break;
		}

		writer.WriteEndObject();
	}

	/// <summary>嵌套的 <c>assistantMessageEvent</c> 记录。终止事件不在这里发出。</summary>
	public static void WriteAssistantMessageEvent(Utf8JsonWriter writer, AssistantMessageEvent @event)
	{
		writer.WriteStartObject();
		switch (@event)
		{
			case StartEvent:
				writer.WriteString("type", "start");
				break;

			case TextStartEvent start:
				writer.WriteString("type", "text_start");
				writer.WriteNumber("contentIndex", start.ContentIndex);
				break;

			case TextDeltaEvent delta:
				writer.WriteString("type", "text_delta");
				writer.WriteNumber("contentIndex", delta.ContentIndex);
				writer.WriteString("delta", delta.Delta);
				break;

			case TextEndEvent end:
				writer.WriteString("type", "text_end");
				writer.WriteNumber("contentIndex", end.ContentIndex);
				writer.WriteString("content", end.Content);
				break;

			case ThinkingStartEvent start:
				writer.WriteString("type", "thinking_start");
				writer.WriteNumber("contentIndex", start.ContentIndex);
				break;

			case ThinkingDeltaEvent delta:
				writer.WriteString("type", "thinking_delta");
				writer.WriteNumber("contentIndex", delta.ContentIndex);
				writer.WriteString("delta", delta.Delta);
				break;

			case ThinkingEndEvent end:
				writer.WriteString("type", "thinking_end");
				writer.WriteNumber("contentIndex", end.ContentIndex);
				writer.WriteString("content", end.Content);
				break;

			case ToolCallStartEvent start:
				writer.WriteString("type", "toolcall_start");
				writer.WriteNumber("contentIndex", start.ContentIndex);
				break;

			case ToolCallDeltaEvent delta:
				writer.WriteString("type", "toolcall_delta");
				writer.WriteNumber("contentIndex", delta.ContentIndex);
				writer.WriteString("delta", delta.Delta);
				break;

			case ToolCallEndEvent end:
				writer.WriteString("type", "toolcall_end");
				writer.WriteNumber("contentIndex", end.ContentIndex);
				writer.WritePropertyName("toolCall");
				PiJsonValue.WriteBlock(writer, end.ToolCall);
				break;

			case DoneEvent done:
				writer.WriteString("type", "done");
				writer.WriteString("reason", done.Reason.ToWire());
				break;

			case ErrorEvent error:
				writer.WriteString("type", "error");
				writer.WriteString("reason", error.Reason.ToWire());
				break;
		}

		writer.WriteEndObject();
	}

	private static void WriteMessages(Utf8JsonWriter writer, IReadOnlyList<Message> messages)
	{
		writer.WriteStartArray();
		foreach (var message in messages)
		{
			MessageJsonConverter.WriteMessage(writer, message);
		}

		writer.WriteEndArray();
	}

	/// <summary>客户端收到的用量对象，与线格式一致。</summary>
	internal static JsonObject UsageRecord(Usage usage) => new()
	{
		["input"] = usage.Input,
		["output"] = usage.Output,
		["cacheRead"] = usage.CacheRead,
		["cacheWrite"] = usage.CacheWrite,
		["totalTokens"] = usage.TotalTokens,
		["cost"] = new JsonObject
		{
			["input"] = usage.Cost.Input,
			["output"] = usage.Cost.Output,
			["cacheRead"] = usage.Cost.CacheRead,
			["cacheWrite"] = usage.Cost.CacheWrite,
			["total"] = usage.Cost.Total,
		},
	};
}
