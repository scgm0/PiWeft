using System.Text.Json;
using System.Text.Json.Nodes;
using PiWeft.Ai.Utils;

namespace PiWeft.Ai.Providers.Anthropic;

/// <summary>写出 Anthropic Messages API 的请求体。不使用反射，也不构建中间对象图。</summary>
internal static class AnthropicWire
{
	public static void WriteRequest(
		Utf8JsonWriter writer,
		Model model,
		IReadOnlyList<Message> messages,
		IReadOnlyList<Tool> tools,
		StreamOptions options,
		ThinkingConfig thinking,
		int maxTokens)
	{
		writer.WriteStartObject();
		writer.WriteString("model", model.Id);
		writer.WriteNumber("max_tokens", maxTokens);
		writer.WriteBoolean("stream", true);

		var systemText = Transcript.GetInitialSystemText(messages);
		if (!string.IsNullOrWhiteSpace(systemText))
		{
			writer.WritePropertyName("system");
			writer.WriteStartArray();
			writer.WriteStartObject();
			writer.WriteString("type", "text");
			writer.WriteString("text", TextSanitizer.SanitizeSurrogates(systemText));
			WriteCacheControl(writer, options);
			writer.WriteEndObject();
			writer.WriteEndArray();
		}

		if (options.Temperature is { } temperature && !thinking.Enabled)
		{
			writer.WriteNumber("temperature", temperature);
		}

		writer.WritePropertyName("messages");
		WriteMessages(writer, messages);

		if (tools.Count > 0)
		{
			writer.WritePropertyName("tools");
			writer.WriteStartArray();
			foreach (var tool in tools)
			{
				writer.WriteStartObject();
				writer.WriteString("name", tool.Name);
				writer.WriteString("description", tool.Description);
				writer.WritePropertyName("input_schema");
				tool.Parameters.WriteTo(writer);
				writer.WriteEndObject();
			}

			writer.WriteEndArray();
		}

		if (thinking.Enabled)
		{
			writer.WritePropertyName("thinking");
			writer.WriteStartObject();
			writer.WriteString("type", "enabled");
			writer.WriteNumber("budget_tokens", thinking.BudgetTokens);
			writer.WriteEndObject();
		}
		else if (thinking.SendDisabled)
		{
			writer.WritePropertyName("thinking");
			writer.WriteStartObject();
			writer.WriteString("type", "disabled");
			writer.WriteEndObject();
		}

		if (options.Metadata?["user_id"] is JsonValue userId && userId.TryGetValue<string>(out var userIdText))
		{
			writer.WritePropertyName("metadata");
			writer.WriteStartObject();
			writer.WriteString("user_id", userIdText);
			writer.WriteEndObject();
		}

		if (options.SamplingParams is { Count: > 0 } sampling)
		{
			foreach (var (key, value) in sampling)
			{
				if (value is null)
				{
					continue;
				}

				writer.WritePropertyName(key);
				value.WriteTo(writer);
			}
		}

		writer.WriteEndObject();
	}

	private static void WriteMessages(Utf8JsonWriter writer, IReadOnlyList<Message> messages)
	{
		writer.WriteStartArray();
		for (var i = 0; i < messages.Count; i++)
		{
			switch (messages[i])
			{
				case SystemMessage:
					// 已经由 Transcript.Normalize 折叠进开头的 system 字段。
					break;

				case UserMessage user:
					WriteUser(writer, user);
					break;

				case AssistantMessage assistant:
					WriteAssistant(writer, assistant);
					break;

				case ToolResultMessage:
				{
					// Anthropic 要求 tool_result 块位于 user 消息中，因此连续的多个结果会合并到一条。
					writer.WriteStartObject();
					writer.WriteString("role", "user");
					writer.WritePropertyName("content");
					writer.WriteStartArray();
					while (i < messages.Count && messages[i] is ToolResultMessage result)
					{
						WriteToolResult(writer, result);
						i++;
					}

					i--;
					writer.WriteEndArray();
					writer.WriteEndObject();
					break;
				}
			}
		}

		writer.WriteEndArray();
	}

	private static void WriteUser(Utf8JsonWriter writer, UserMessage message)
	{
		if (message.Content.IsText)
		{
			if (string.IsNullOrWhiteSpace(message.Content.Text))
			{
				return;
			}

			writer.WriteStartObject();
			writer.WriteString("role", "user");
			writer.WriteString("content", TextSanitizer.SanitizeSurrogates(message.Content.Text));
			writer.WriteEndObject();
			return;
		}

		var blocks = message.Content.Blocks ?? [];
		if (!blocks.Any(IsRenderableUserBlock))
		{
			return;
		}

		writer.WriteStartObject();
		writer.WriteString("role", "user");
		writer.WritePropertyName("content");
		writer.WriteStartArray();
		foreach (var block in blocks)
		{
			switch (block)
			{
				case TextContent text when !string.IsNullOrWhiteSpace(text.Text):
					writer.WriteStartObject();
					writer.WriteString("type", "text");
					writer.WriteString("text", TextSanitizer.SanitizeSurrogates(text.Text));
					writer.WriteEndObject();
					break;
				case ImageContent image:
					writer.WriteStartObject();
					writer.WriteString("type", "image");
					writer.WritePropertyName("source");
					writer.WriteStartObject();
					writer.WriteString("type", "base64");
					writer.WriteString("media_type", image.MimeType);
					writer.WriteString("data", image.Data);
					writer.WriteEndObject();
					writer.WriteEndObject();
					break;
			}
		}

		writer.WriteEndArray();
		writer.WriteEndObject();
	}

	private static bool IsRenderableUserBlock(ContentBlock block) => block switch
	{
		TextContent text => !string.IsNullOrWhiteSpace(text.Text),
		ImageContent => true,
		_ => false,
	};

	private static void WriteAssistant(Utf8JsonWriter writer, AssistantMessage message)
	{
		writer.WriteStartObject();
		writer.WriteString("role", "assistant");
		writer.WritePropertyName("content");
		writer.WriteStartArray();

		foreach (var block in message.Content)
		{
			switch (block)
			{
				case TextContent text when !string.IsNullOrWhiteSpace(text.Text):
					writer.WriteStartObject();
					writer.WriteString("type", "text");
					writer.WriteString("text", TextSanitizer.SanitizeSurrogates(text.Text));
					writer.WriteEndObject();
					break;

				case ThinkingContent { Redacted: true } redacted:
					writer.WriteStartObject();
					writer.WriteString("type", "redacted_thinking");
					writer.WriteString("data", redacted.ThinkingSignature ?? string.Empty);
					writer.WriteEndObject();
					break;

				case ThinkingContent thinking:
				{
					var hasSignature = !string.IsNullOrWhiteSpace(thinking.ThinkingSignature);
					if (string.IsNullOrWhiteSpace(thinking.Thinking) && !hasSignature)
					{
						break;
					}

					writer.WriteStartObject();
					if (hasSignature)
					{
						writer.WriteString("type", "thinking");
						writer.WriteString("thinking", TextSanitizer.SanitizeSurrogates(thinking.Thinking));
						writer.WriteString("signature", thinking.ThinkingSignature);
					}
					else
					{
						// 被中断的流可能留下没有签名的 thinking 块，此时按纯文本发送。
						writer.WriteString("type", "text");
						writer.WriteString("text", TextSanitizer.SanitizeSurrogates(thinking.Thinking));
					}

					writer.WriteEndObject();
					break;
				}

				case ToolCall call:
					writer.WriteStartObject();
					writer.WriteString("type", "tool_use");
					writer.WriteString("id", call.Id);
					writer.WriteString("name", call.Name);
					writer.WritePropertyName("input");
					call.Arguments.WriteTo(writer);
					writer.WriteEndObject();
					break;
			}
		}

		writer.WriteEndArray();
		writer.WriteEndObject();
	}

	private static void WriteToolResult(Utf8JsonWriter writer, ToolResultMessage message)
	{
		writer.WriteStartObject();
		writer.WriteString("type", "tool_result");
		writer.WriteString("tool_use_id", message.ToolCallId);
		writer.WritePropertyName("content");
		writer.WriteStartArray();
		foreach (var block in message.Content)
		{
			switch (block)
			{
				case TextContent text:
					writer.WriteStartObject();
					writer.WriteString("type", "text");
					writer.WriteString("text", TextSanitizer.SanitizeSurrogates(text.Text));
					writer.WriteEndObject();
					break;
				case ImageContent image:
					writer.WriteStartObject();
					writer.WriteString("type", "image");
					writer.WritePropertyName("source");
					writer.WriteStartObject();
					writer.WriteString("type", "base64");
					writer.WriteString("media_type", image.MimeType);
					writer.WriteString("data", image.Data);
					writer.WriteEndObject();
					writer.WriteEndObject();
					break;
			}
		}

		writer.WriteEndArray();
		writer.WriteBoolean("is_error", message.IsError);
		writer.WriteEndObject();
	}

	private static void WriteCacheControl(Utf8JsonWriter writer, StreamOptions options)
	{
		var retention = options.CacheRetention ?? CacheRetentions.Short;
		if (retention == CacheRetentions.None)
		{
			return;
		}

		writer.WritePropertyName("cache_control");
		writer.WriteStartObject();
		writer.WriteString("type", "ephemeral");
		if (retention == CacheRetentions.Long)
		{
			writer.WriteString("ttl", "1h");
		}

		writer.WriteEndObject();
	}
}
