using System.Buffers.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiWeft.Ai.Utils;

namespace PiWeft.Ai.Providers.Bedrock;

/// <summary>写出 Bedrock Converse API 的请求体。</summary>
internal static class BedrockWire
{
	public static void WriteRequest(
		Utf8JsonWriter writer,
		Model model,
		IReadOnlyList<Message> messages,
		IReadOnlyList<Tool> tools,
		StreamOptions options,
		int? thinkingBudget,
		int? maxTokens)
	{
		writer.WriteStartObject();

		if (Transcript.GetInitialSystemText(messages) is { Length: > 0 } systemText)
		{
			writer.WritePropertyName("system");
			writer.WriteStartArray();
			writer.WriteStartObject();
			writer.WriteString("text", TextSanitizer.SanitizeSurrogates(systemText));
			writer.WriteEndObject();
			writer.WriteEndArray();
		}

		writer.WritePropertyName("messages");
		WriteMessages(writer, messages);

		if (tools.Count > 0)
		{
			writer.WritePropertyName("toolConfig");
			writer.WriteStartObject();
			writer.WritePropertyName("tools");
			writer.WriteStartArray();
			foreach (var tool in tools)
			{
				writer.WriteStartObject();
				writer.WritePropertyName("toolSpec");
				writer.WriteStartObject();
				writer.WriteString("name", tool.Name);
				writer.WriteString("description", tool.Description);
				writer.WritePropertyName("inputSchema");
				writer.WriteStartObject();
				writer.WritePropertyName("json");
				tool.Parameters.WriteTo(writer);
				writer.WriteEndObject();
				writer.WriteEndObject();
				writer.WriteEndObject();
			}

			writer.WriteEndArray();
			writer.WriteEndObject();
		}

		var hasInferenceConfig = maxTokens is not null || options.Temperature is not null;
		if (hasInferenceConfig)
		{
			writer.WritePropertyName("inferenceConfig");
			writer.WriteStartObject();
			if (maxTokens is { } tokens)
			{
				writer.WriteNumber("maxTokens", tokens);
			}

			if (options.Temperature is { } temperature)
			{
				writer.WriteNumber("temperature", temperature);
			}

			writer.WriteEndObject();
		}

		// Bedrock 上的 Anthropic 模型通过 model-specific 扩展字段接收扩展思考配置。
		if (thinkingBudget is { } budget && model.Id.StartsWith("anthropic.", StringComparison.OrdinalIgnoreCase))
		{
			writer.WritePropertyName("additionalModelRequestFields");
			writer.WriteStartObject();
			writer.WritePropertyName("thinking");
			writer.WriteStartObject();
			writer.WriteString("type", "enabled");
			writer.WriteNumber("budget_tokens", budget);
			writer.WriteEndObject();
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
					// 由顶层的 system 字段承载。
					break;

				case UserMessage user:
					WriteUser(writer, user);
					break;

				case AssistantMessage assistant:
					WriteAssistant(writer, assistant);
					break;

				case ToolResultMessage:
				{
					// Bedrock 要求同一轮次的所有 toolResult 块放在一条 user 消息内。
					writer.WriteStartObject();
					writer.WriteString("role", "user");
					writer.WritePropertyName("content");
					writer.WriteStartArray();

					while (i < messages.Count && messages[i] is ToolResultMessage result)
					{
						if (result.Content.Count == 0)
						{
							WriteToolResultBlock(writer, result, null);
						}
						else
						{
							foreach (var block in result.Content)
							{
								WriteToolResultBlock(writer, result, block);
							}
						}

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
			writer.WritePropertyName("content");
			writer.WriteStartArray();
			writer.WriteStartObject();
			writer.WriteString("text", TextSanitizer.SanitizeSurrogates(message.Content.Text));
			writer.WriteEndObject();
			writer.WriteEndArray();
			writer.WriteEndObject();
			return;
		}

		var blocks = message.Content.Blocks ?? [];
		if (!blocks.Any(static b => b is ImageContent || (b is TextContent t && !string.IsNullOrWhiteSpace(t.Text))))
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
					writer.WriteString("text", TextSanitizer.SanitizeSurrogates(text.Text));
					writer.WriteEndObject();
					break;
				case ImageContent image:
					writer.WriteStartObject();
					writer.WritePropertyName("image");
					writer.WriteStartObject();
					writer.WriteString("format", FormatOf(image.MimeType));
					writer.WritePropertyName("source");
					writer.WriteStartObject();
					writer.WriteString("bytes", image.Data);
					writer.WriteEndObject();
					writer.WriteEndObject();
					writer.WriteEndObject();
					break;
			}
		}

		writer.WriteEndArray();
		writer.WriteEndObject();
	}

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
					writer.WriteString("text", TextSanitizer.SanitizeSurrogates(text.Text));
					writer.WriteEndObject();
					break;

				case ToolCall call:
					writer.WriteStartObject();
					writer.WritePropertyName("toolUse");
					writer.WriteStartObject();
					writer.WriteString("toolUseId", call.Id);
					writer.WriteString("name", call.Name);
					writer.WritePropertyName("input");
					call.Arguments.WriteTo(writer);
					writer.WriteEndObject();
					writer.WriteEndObject();
					break;
			}
		}

		writer.WriteEndArray();
		writer.WriteEndObject();
	}

	private static void WriteToolResultBlock(Utf8JsonWriter writer, ToolResultMessage message, ContentBlock? block)
	{
		writer.WriteStartObject();
		writer.WritePropertyName("toolResult");
		writer.WriteStartObject();
		writer.WriteString("toolUseId", message.ToolCallId);

		var text = block switch
		{
			TextContent content => content.Text,
			null => string.Join("\n", message.Content.OfType<TextContent>().Select(static c => c.Text)),
			_ => null,
		};

		// Bedrock 也接受工具结果里的图片。
		if (block is ImageContent)
		{
			writer.WritePropertyName("content");
			writer.WriteStartArray();
			writer.WriteStartObject();
			writer.WritePropertyName("image");
			writer.WriteStartObject();
			writer.WriteString("format", FormatOf(((ImageContent)block).MimeType));
			writer.WritePropertyName("source");
			writer.WriteStartObject();
			writer.WriteString("bytes", ((ImageContent)block).Data);
			writer.WriteEndObject();
			writer.WriteEndObject();
			writer.WriteEndObject();
			writer.WriteEndArray();
		}
		else
		{
			writer.WritePropertyName("content");
			writer.WriteStartArray();
			writer.WriteStartObject();
			writer.WriteString("text", TextSanitizer.SanitizeSurrogates(text ?? "(no output)"));
			writer.WriteEndObject();
			writer.WriteEndArray();
		}

		writer.WriteString("status", message.IsError ? "error" : "success");
		writer.WriteEndObject();
		writer.WriteEndObject();
	}

	/// <summary>Bedrock 的图片格式是裸名称，不是媒体类型。</summary>
	internal static string FormatOf(string mimeType) => mimeType switch
	{
		"image/jpeg" => "jpeg",
		"image/png" => "png",
		"image/gif" => "gif",
		"image/webp" => "webp",
		_ => mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ? mimeType[6..] : mimeType,
	};
}
