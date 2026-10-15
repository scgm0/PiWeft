using System.Text.Json;
using System.Text.Json.Nodes;
using PiWeft.Ai.Utils;

namespace PiWeft.Ai.Providers.Google;

/// <summary>写出 Google Generative Language（Gemini）的请求体。</summary>
internal static class GoogleWire
{
	public static void WriteRequest(
		Utf8JsonWriter writer,
		Model model,
		IReadOnlyList<Message> messages,
		IReadOnlyList<Tool> tools,
		StreamOptions options,
		int? thinkingBudget,
		int? maxOutputTokens)
	{
		writer.WriteStartObject();

		if (Transcript.GetInitialSystemText(messages) is { Length: > 0 } systemText)
		{
			writer.WritePropertyName("systemInstruction");
			writer.WriteStartObject();
			writer.WritePropertyName("parts");
			writer.WriteStartArray();
			writer.WriteStartObject();
			writer.WriteString("text", TextSanitizer.SanitizeSurrogates(systemText));
			writer.WriteEndObject();
			writer.WriteEndArray();
			writer.WriteEndObject();
		}

		writer.WritePropertyName("contents");
		WriteContents(writer, model, messages);

		if (tools.Count > 0)
		{
			writer.WritePropertyName("tools");
			writer.WriteStartArray();
			writer.WriteStartObject();
			writer.WritePropertyName("functionDeclarations");
			writer.WriteStartArray();
			foreach (var tool in tools)
			{
				writer.WriteStartObject();
				writer.WriteString("name", tool.Name);
				writer.WriteString("description", tool.Description);
				writer.WritePropertyName("parametersJsonSchema");
				tool.Parameters.WriteTo(writer);
				writer.WriteEndObject();
			}

			writer.WriteEndArray();
			writer.WriteEndObject();
			writer.WriteEndArray();
		}

		if (options.Metadata?["toolChoice"] is JsonValue toolChoice && toolChoice.TryGetValue<string>(out var choice))
		{
			writer.WritePropertyName("toolConfig");
			writer.WriteStartObject();
			writer.WritePropertyName("functionCallingConfig");
			writer.WriteStartObject();
			writer.WriteString("mode", choice switch
			{
				"none" => "NONE",
				"any" => "ANY",
				_ => "AUTO",
			});
			writer.WriteEndObject();
			writer.WriteEndObject();
		}

		writer.WritePropertyName("generationConfig");
		writer.WriteStartObject();

		if (options.Temperature is { } temperature)
		{
			writer.WriteNumber("temperature", temperature);
		}

		if (maxOutputTokens is { } tokens)
		{
			writer.WriteNumber("maxOutputTokens", tokens);
		}

		if (thinkingBudget is { } budget)
		{
			writer.WritePropertyName("thinkingConfig");
			writer.WriteStartObject();
			writer.WriteBoolean("includeThoughts", budget > 0);
			writer.WriteNumber("thinkingBudget", budget);
			writer.WriteEndObject();
		}

		writer.WriteEndObject();

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

	private static void WriteContents(Utf8JsonWriter writer, Model model, IReadOnlyList<Message> messages)
	{
		writer.WriteStartArray();
		var pendingToolResults = new List<ToolResultMessage>();

		void FlushToolResults()
		{
			if (pendingToolResults.Count == 0)
			{
				return;
			}

			// Gemini 要求同一轮次的所有 functionResponse 放在同一条 user content 内。
			writer.WriteStartObject();
			writer.WriteString("role", "user");
			writer.WritePropertyName("parts");
			writer.WriteStartArray();
			foreach (var result in pendingToolResults)
			{
				writer.WriteStartObject();
				writer.WritePropertyName("functionResponse");
				writer.WriteStartObject();
				writer.WriteString("name", result.ToolName);
				writer.WritePropertyName("response");
				writer.WriteStartObject();
				if (result.IsError)
				{
					writer.WriteString("error", ToolResultText(result));
				}
				else
				{
					writer.WriteString("output", ToolResultText(result));
				}

				writer.WriteEndObject();
				writer.WriteEndObject();
				writer.WriteEndObject();
			}

			writer.WriteEndArray();
			writer.WriteEndObject();
			pendingToolResults.Clear();
		}

		foreach (var message in messages)
		{
			switch (message)
			{
				case SystemMessage:
					// 由 systemInstruction 承载。
					break;

				case ToolResultMessage toolResult:
					pendingToolResults.Add(toolResult);
					continue;

				case UserMessage user:
					FlushToolResults();
					WriteUser(writer, user);
					break;

				case AssistantMessage assistant:
					FlushToolResults();
					WriteAssistant(writer, model, assistant);
					break;
			}
		}

		FlushToolResults();
		writer.WriteEndArray();
	}

	private static string ToolResultText(ToolResultMessage message)
	{
		var text = string.Join("\n", message.Content.OfType<TextContent>().Select(static block => block.Text));
		if (text.Length > 0)
		{
			return TextSanitizer.SanitizeSurrogates(text);
		}

		return message.Content.Any(static block => block is ImageContent) ? "(see attached image)" : "(no output)";
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
			writer.WritePropertyName("parts");
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
		writer.WritePropertyName("parts");
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
					writer.WritePropertyName("inlineData");
					writer.WriteStartObject();
					writer.WriteString("mimeType", image.MimeType);
					writer.WriteString("data", image.Data);
					writer.WriteEndObject();
					writer.WriteEndObject();
					break;
			}
		}

		writer.WriteEndArray();
		writer.WriteEndObject();
	}

	private static void WriteAssistant(Utf8JsonWriter writer, Model model, AssistantMessage message)
	{
		// 思考块只会回放给产生它的那个 provider 与模型。
		var isSameModel =
			string.Equals(message.Provider, model.Provider, StringComparison.Ordinal) &&
			string.Equals(message.Model, model.Id, StringComparison.Ordinal);

		writer.WriteStartObject();
		writer.WriteString("role", "model");
		writer.WritePropertyName("parts");
		writer.WriteStartArray();

		foreach (var block in message.Content)
		{
			switch (block)
			{
				case TextContent text:
				{
					var signature = text.TextSignature;
					if (string.IsNullOrWhiteSpace(text.Text) && string.IsNullOrEmpty(signature))
					{
						break;
					}

					writer.WriteStartObject();
					writer.WriteString("text", TextSanitizer.SanitizeSurrogates(text.Text));
					if (!string.IsNullOrEmpty(signature))
					{
						writer.WriteString("thoughtSignature", signature);
					}

					writer.WriteEndObject();
					break;
				}

				case ThinkingContent thinking:
				{
					if (string.IsNullOrWhiteSpace(thinking.Thinking) && string.IsNullOrEmpty(thinking.ThinkingSignature))
					{
						break;
					}

					writer.WriteStartObject();
					if (isSameModel)
					{
						writer.WriteBoolean("thought", true);
					}

					writer.WriteString("text", TextSanitizer.SanitizeSurrogates(thinking.Thinking));
					if (isSameModel && !string.IsNullOrEmpty(thinking.ThinkingSignature))
					{
						writer.WriteString("thoughtSignature", thinking.ThinkingSignature);
					}

					writer.WriteEndObject();
					break;
				}

				case ToolCall call:
					writer.WriteStartObject();
					writer.WritePropertyName("functionCall");
					writer.WriteStartObject();
					writer.WriteString("name", call.Name);
					writer.WritePropertyName("args");
					call.Arguments.WriteTo(writer);
					if (!string.IsNullOrEmpty(call.Id))
					{
						writer.WriteString("id", call.Id);
					}

					writer.WriteEndObject();
					if (!string.IsNullOrEmpty(call.ThoughtSignature))
					{
						writer.WriteString("thoughtSignature", call.ThoughtSignature);
					}

					writer.WriteEndObject();
					break;
			}
		}

		writer.WriteEndArray();
		writer.WriteEndObject();
	}
}
