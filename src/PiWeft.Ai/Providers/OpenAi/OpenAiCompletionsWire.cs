using System.Text.Json;
using System.Text.Json.Nodes;
using PiWeft.Ai.Utils;

using PiWeft.Ai.Json;
namespace PiWeft.Ai.Providers.OpenAi;

/// <summary>写出 OpenAI 兼容的 <c>/chat/completions</c> 请求体。</summary>
internal static class OpenAiCompletionsWire
{
	public static void WriteRequest(
		Utf8JsonWriter writer,
		Model model,
		IReadOnlyList<Message> messages,
		IReadOnlyList<Tool> tools,
		StreamOptions options,
		string? reasoningEffort,
		int? maxTokens)
	{
		writer.WriteStartObject();
		writer.WriteString("model", model.Id);

		if (maxTokens is { } tokens)
		{
			// OpenAI 已用 max_completion_tokens 取代 max_tokens，但多数兼容服务端
			// （vLLM、llama.cpp、DeepSeek 等）仍然只接受 max_tokens。
			var field = UsesMaxCompletionTokens(model) ? "max_completion_tokens" : "max_tokens";
			writer.WriteNumber(field, tokens);
		}

		if (options.Temperature is { } temperature)
		{
			writer.WriteNumber("temperature", temperature);
		}

		if (reasoningEffort is not null)
		{
			writer.WriteString("reasoning_effort", reasoningEffort);
		}

		writer.WriteBoolean("stream", true);
		writer.WritePropertyName("stream_options");
		writer.WriteStartObject();
		writer.WriteBoolean("include_usage", true);
		writer.WriteEndObject();

		writer.WritePropertyName("messages");
		WriteMessages(writer, messages);

		if (tools.Count > 0)
		{
			WriteTools(writer, tools);
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

	internal static void WriteMessages(Utf8JsonWriter writer, IReadOnlyList<Message> messages)
	{
		writer.WriteStartArray();
		foreach (var message in messages)
		{
			switch (message)
			{
				case SystemMessage system:
					WriteSystem(writer, system);
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

	private static void WriteSystem(Utf8JsonWriter writer, SystemMessage message)
	{
		var text = Transcript.GetText(message);
		if (string.IsNullOrWhiteSpace(text))
		{
			return;
		}

		writer.WriteStartObject();
		writer.WriteString("role", "system");
		writer.WriteString("content", TextSanitizer.SanitizeSurrogates(text));
		writer.WriteEndObject();
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
					writer.WriteString("type", "text");
					writer.WriteString("text", TextSanitizer.SanitizeSurrogates(text.Text));
					writer.WriteEndObject();
					break;
				case ImageContent image:
					writer.WriteStartObject();
					writer.WriteString("type", "image_url");
					writer.WritePropertyName("image_url");
					writer.WriteStartObject();
					writer.WriteString("url", $"data:{image.MimeType};base64,{image.Data}");
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
		var text = string.Concat(message.Content
			.OfType<TextContent>()
			.Where(static t => !string.IsNullOrWhiteSpace(t.Text))
			.Select(static t => t.Text));

		var calls = message.Content.OfType<ToolCall>().ToList();
		if (text.Length == 0 && calls.Count == 0)
		{
			return;
		}

		writer.WriteStartObject();
		writer.WriteString("role", "assistant");
		if (text.Length > 0)
		{
			writer.WriteString("content", TextSanitizer.SanitizeSurrogates(text));
		}
		else
		{
			writer.WriteNull("content");
		}

		if (calls.Count > 0)
		{
			writer.WritePropertyName("tool_calls");
			writer.WriteStartArray();
			foreach (var call in calls)
			{
				writer.WriteStartObject();
				writer.WriteString("id", call.Id);
				writer.WriteString("type", "function");
				writer.WritePropertyName("function");
				writer.WriteStartObject();
				writer.WriteString("name", call.Name);
				writer.WriteString("arguments", call.Arguments.ToJsonString(PiJson.NodeOptions));
				writer.WriteEndObject();
				writer.WriteEndObject();
			}

			writer.WriteEndArray();
		}

		writer.WriteEndObject();
	}

	private static void WriteToolResult(Utf8JsonWriter writer, ToolResultMessage message)
	{
		var text = string.Join(
			"\n",
			message.Content.OfType<TextContent>().Select(static t => t.Text));
		var hasImages = message.Content.Any(static b => b is ImageContent);
		if (text.Length == 0)
		{
			text = hasImages ? "(see attached image)" : "(no tool output)";
		}

		writer.WriteStartObject();
		writer.WriteString("role", "tool");
		writer.WriteString("tool_call_id", message.ToolCallId);
		writer.WriteString("content", TextSanitizer.SanitizeSurrogates(text));
		writer.WriteEndObject();
	}

	/// <summary>写出 OpenAI 的工具声明数组；与 Mistral 适配器共用。</summary>
	internal static void WriteTools(Utf8JsonWriter writer, IReadOnlyList<Tool> tools)
	{
		writer.WritePropertyName("tools");
		writer.WriteStartArray();
		foreach (var tool in tools)
		{
			writer.WriteStartObject();
			writer.WriteString("type", "function");
			writer.WritePropertyName("function");
			writer.WriteStartObject();
			writer.WriteString("name", tool.Name);
			writer.WriteString("description", tool.Description);
			writer.WritePropertyName("parameters");
			tool.Parameters.WriteTo(writer);
			writer.WriteEndObject();
			writer.WriteEndObject();
		}

		writer.WriteEndArray();
	}

	internal static bool UsesMaxCompletionTokens(Model model) =>
		model.BaseUrl.Contains("api.openai.com", StringComparison.OrdinalIgnoreCase);
}
