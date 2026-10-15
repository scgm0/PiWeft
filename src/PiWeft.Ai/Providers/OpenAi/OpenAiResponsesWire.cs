using System.Text.Json;
using System.Text.Json.Nodes;
using PiWeft.Ai.Utils;

using PiWeft.Ai.Json;
namespace PiWeft.Ai.Providers.OpenAi;

/// <summary>写出 OpenAI Responses API 的请求体。</summary>
internal static class OpenAiResponsesWire
{
	public static void WriteRequest(
		Utf8JsonWriter writer,
		Model model,
		IReadOnlyList<Message> messages,
		IReadOnlyList<Tool> tools,
		StreamOptions options,
		string? reasoningEffort,
		int? maxOutputTokens,
		string? modelNameOverride = null,
		bool store = true,
		string? promptCacheKey = null)
	{
		writer.WriteStartObject();
		// Azure 使用的是部署名，它通常等于模型 id，但并不总是。
		writer.WriteString("model", modelNameOverride ?? model.Id);
		writer.WriteBoolean("stream", true);

		if (!store)
		{
			// ChatGPT Codex 后端拒绝存储响应。
			writer.WriteBoolean("store", false);
		}

		if (promptCacheKey is { Length: > 0 })
		{
			writer.WriteString("prompt_cache_key", promptCacheKey);
		}

		if (!string.IsNullOrWhiteSpace(Transcript.GetInitialSystemText(messages)))
		{
			writer.WriteString("instructions", TextSanitizer.SanitizeSurrogates(Transcript.GetInitialSystemText(messages)));
		}

		if (maxOutputTokens is { } tokens)
		{
			writer.WriteNumber("max_output_tokens", tokens);
		}

		if (options.Temperature is { } temperature && reasoningEffort is null)
		{
			writer.WriteNumber("temperature", temperature);
		}

		if (reasoningEffort is not null)
		{
			writer.WritePropertyName("reasoning");
			writer.WriteStartObject();
			writer.WriteString("effort", reasoningEffort);
			writer.WriteString("summary", "auto");
			writer.WriteEndObject();

			writer.WritePropertyName("include");
			writer.WriteStartArray();
			writer.WriteStringValue("reasoning.encrypted_content");
			writer.WriteEndArray();
		}

		writer.WritePropertyName("input");
		WriteInput(writer, messages);

		if (tools.Count > 0)
		{
			writer.WritePropertyName("tools");
			writer.WriteStartArray();
			foreach (var tool in tools)
			{
				writer.WriteStartObject();
				writer.WriteString("type", "function");
				writer.WriteString("name", tool.Name);
				writer.WriteString("description", tool.Description);
				writer.WritePropertyName("parameters");
				tool.Parameters.WriteTo(writer);
				writer.WriteEndObject();
			}

			writer.WriteEndArray();
		}

		if (options.Metadata?["toolChoice"] is JsonValue toolChoice && toolChoice.TryGetValue<string>(out var choice))
		{
			writer.WriteString("tool_choice", choice);
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

	private static void WriteInput(Utf8JsonWriter writer, IReadOnlyList<Message> messages)
	{
		writer.WriteStartArray();
		foreach (var message in messages)
		{
			switch (message)
			{
				case SystemMessage:
					// 提示词放在顶层的 "instructions" 字段里，而不是作为输入项。
					break;

				case UserMessage user:
					WriteUser(writer, user);
					break;

				case AssistantMessage assistant:
					WriteAssistant(writer, assistant);
					break;

				case ToolResultMessage result:
					WriteToolResult(writer, result);
					break;
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
			writer.WriteString("type", "input_text");
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
					writer.WriteString("type", "input_text");
					writer.WriteString("text", TextSanitizer.SanitizeSurrogates(text.Text));
					writer.WriteEndObject();
					break;
				case ImageContent image:
					writer.WriteStartObject();
					writer.WriteString("type", "input_image");
					writer.WriteString("detail", "auto");
					writer.WriteString("image_url", $"data:{image.MimeType};base64,{image.Data}");
					writer.WriteEndObject();
					break;
			}
		}

		writer.WriteEndArray();
		writer.WriteEndObject();
	}

	private static void WriteAssistant(Utf8JsonWriter writer, AssistantMessage message)
	{
		var textIndex = 0;
		foreach (var block in message.Content)
		{
			switch (block)
			{
				case ThinkingContent { ThinkingSignature: { Length: > 0 } signature }:
					// 回放不透明的推理项，让 provider 能与自己的状态配对。
					try
					{
						using var document = JsonDocument.Parse(signature);
						document.RootElement.WriteTo(writer);
					}
					catch (JsonException)
					{
						// 签名损坏时直接丢弃，而不是让整个请求失败。
					}

					break;

				case TextContent text when text.Text.Length > 0:
					writer.WriteStartObject();
					writer.WriteString("type", "message");
					writer.WriteString("role", "assistant");
					writer.WritePropertyName("content");
					writer.WriteStartArray();
					writer.WriteStartObject();
					writer.WriteString("type", "output_text");
					writer.WriteString("text", TextSanitizer.SanitizeSurrogates(text.Text));
					writer.WritePropertyName("annotations");
					writer.WriteStartArray();
					writer.WriteEndArray();
					writer.WriteEndObject();
					writer.WriteEndArray();
					writer.WriteString("status", "completed");
					writer.WriteString("id", $"msg_pi_{textIndex}");
					writer.WriteEndObject();
					textIndex++;
					break;

				case ToolCall call:
					writer.WriteStartObject();
					writer.WriteString("type", "function_call");
					writer.WriteString("call_id", call.Id);
					writer.WriteString("name", call.Name);
					writer.WriteString("arguments", call.Arguments.ToJsonString(PiJson.NodeOptions));
					writer.WriteEndObject();
					break;
			}
		}
	}

	private static void WriteToolResult(Utf8JsonWriter writer, ToolResultMessage message)
	{
		var text = string.Join("\n", message.Content.OfType<TextContent>().Select(static block => block.Text));
		var images = message.Content.OfType<ImageContent>().ToList();

		writer.WriteStartObject();
		writer.WriteString("type", "function_call_output");
		writer.WriteString("call_id", message.ToolCallId);

		if (images.Count == 0)
		{
			writer.WriteString("output", TextSanitizer.SanitizeSurrogates(text.Length > 0 ? text : "(no tool output)"));
		}
		else
		{
			writer.WritePropertyName("output");
			writer.WriteStartArray();
			writer.WriteStartObject();
			writer.WriteString("type", "input_text");
			writer.WriteString("text", TextSanitizer.SanitizeSurrogates(text.Length > 0 ? text : "(see attached image)"));
			writer.WriteEndObject();

			foreach (var image in images)
			{
				writer.WriteStartObject();
				writer.WriteString("type", "input_image");
				writer.WriteString("detail", "auto");
				writer.WriteString("image_url", $"data:{image.MimeType};base64,{image.Data}");
				writer.WriteEndObject();
			}

			writer.WriteEndArray();
		}

		writer.WriteEndObject();
	}
}
