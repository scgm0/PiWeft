using System.Text.Json;
using System.Text.Json.Nodes;
using PiWeft.Ai.Utils;

using PiWeft.Ai.Json;
namespace PiWeft.Ai.Providers.OpenAi;

internal static class OpenAiCompletionsWire
{
	public static void WriteRequest(
		Utf8JsonWriter writer,
		Model model,
		IReadOnlyList<Message> messages,
		IReadOnlyList<Tool> tools,
		StreamOptions options,
		string? reasoningEffort,
		string? thinkingMode,
		int? maxTokens,
		string? promptCacheKey = null)
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

		if (reasoningEffort is not null && ModelCompat.FlagOr(model, "supportsReasoningEffort", true))
		{
			writer.WriteString("reasoning_effort", reasoningEffort);
		}

		if (thinkingMode is not null)
		{
			// DeepSeek 的开关与强度是两个字段：type 决定是否思考，reasoning_effort 只管档位。
			// 默认是「开启」，关闭要显式写出来。
			writer.WritePropertyName("thinking");
			writer.WriteStartObject();
			writer.WriteString("type", thinkingMode);
			writer.WriteEndObject();
		}

		writer.WriteBoolean("stream", true);
		if (ModelCompat.FlagOr(model, "supportsUsageInStreaming", true))
		{
			writer.WritePropertyName("stream_options");
			writer.WriteStartObject();
			writer.WriteBoolean("include_usage", true);
			writer.WriteEndObject();
		}

		if (promptCacheKey is { Length: > 0 })
		{
			writer.WriteString("prompt_cache_key", promptCacheKey);
		}

		writer.WritePropertyName("messages");
		WriteMessages(writer, messages, model);

		if (tools.Count > 0)
		{
			WriteTools(writer, tools, StrictSamplingFor(model));
		}
		else if (ModelCompat.FlagOr(model, "requiresToolsWithToolHistory", false) && HasToolHistory(messages))
		{
			// 老 LiteLLM 这类端点要求有过工具调用的会话仍带 tools 字段，即使为空。
			writer.WritePropertyName("tools");
			writer.WriteStartArray();
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

	internal static void WriteMessages(Utf8JsonWriter writer, IReadOnlyList<Message> messages, Model? model = null)
	{
		var bridgeAfterToolResult = model is not null &&
			ModelCompat.FlagOr(model, "requiresAssistantAfterToolResult", false);
		var lastWasToolResult = false;
		writer.WriteStartArray();
		foreach (var message in messages)
		{
			switch (message)
			{
				case SystemMessage system:
					WriteSystem(writer, system, model);
					lastWasToolResult = false;
					break;

				case UserMessage user:
					if (bridgeAfterToolResult && lastWasToolResult)
					{
						WriteAssistantBridge(writer);
					}

					WriteUser(writer, user);
					lastWasToolResult = false;
					break;

				case AssistantMessage assistant:
					WriteAssistant(writer, assistant, model);
					lastWasToolResult = false;
					break;

				case ToolResultMessage result:
					WriteToolResult(writer, result, model);
					lastWasToolResult = true;
					break;
			}
		}

		writer.WriteEndArray();
	}

	private static void WriteAssistantBridge(Utf8JsonWriter writer)
	{
		writer.WriteStartObject();
		writer.WriteString("role", "assistant");
		writer.WriteString("content", "I have processed the tool results.");
		writer.WriteEndObject();
	}

	private static void WriteSystem(Utf8JsonWriter writer, SystemMessage message, Model? model)
	{
		var text = Transcript.GetText(message);
		if (string.IsNullOrWhiteSpace(text))
		{
			return;
		}

		var supportsDeveloperRole = model is not null &&
			ModelCompat.FlagOr(model, "supportsDeveloperRole", model.BaseUrl.Contains("api.openai.com", StringComparison.OrdinalIgnoreCase));
		writer.WriteStartObject();
		writer.WriteString("role", supportsDeveloperRole ? "developer" : "system");
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

	private static void WriteAssistant(Utf8JsonWriter writer, AssistantMessage message, Model? model)
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
		else if (model is not null && ModelCompat.FlagOr(model, "requiresNonNullAssistantContent", false))
		{
			writer.WriteString("content", string.Empty);
		}
		else
		{
			writer.WriteNull("content");
		}

		// 思考原文回放：签名记着这段思考来自哪个字段（reasoning_content 等），原样写回即可让端点续上上下文。
		var thinking = message.Content
			.OfType<ThinkingContent>()
			.Where(static block => block.Redacted != true && !string.IsNullOrWhiteSpace(block.Thinking))
			.ToList();
		var signature = thinking
			.Select(static block => block.ThinkingSignature)
			.FirstOrDefault(static field => !string.IsNullOrEmpty(field));
		var reasoningField = signature is { Length: > 0 } && Array.IndexOf(ReasoningFields, signature) >= 0
			? signature
			: null;

		if (reasoningField is not null)
		{
			writer.WriteString(
				reasoningField,
				TextSanitizer.SanitizeSurrogates(string.Join("\n", thinking.Select(static block => block.Thinking))));
		}
		else if (model is { Reasoning: true } && RequiresReasoningContent(model))
		{
			// DeepSeek 带工具的请求要求每条助手消息都有 reasoning_content；这一轮没有思考就发空串。
			writer.WriteString("reasoning_content", string.Empty);
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

	private static void WriteToolResult(Utf8JsonWriter writer, ToolResultMessage message, Model? model = null)
	{
		var text = string.Join(
			"\n",
			message.Content.OfType<TextContent>().Select(static t => t.Text));
		var hasImages = message.Content.Any(static b => b is ImageContent);
		if (string.IsNullOrWhiteSpace(text))
		{
			text = hasImages ? "(see attached image)" : "(no tool output)";
		}

		writer.WriteStartObject();
		writer.WriteString("role", "tool");
		writer.WriteString("tool_call_id", message.ToolCallId);
		if (model is not null && message.HasToolName && message.ToolName.Length > 0 &&
			ModelCompat.FlagOr(model, "requiresToolResultName", false))
		{
			writer.WriteString("name", message.ToolName);
		}

		writer.WriteString("content", TextSanitizer.SanitizeSurrogates(text));
		writer.WriteEndObject();
	}

	/// 这个模型的工具是否带 <c>strict</c>。pi 对 OpenAI 兼容端点默认带上
	/// （<c>compat.supportsStrictMode !== false</c>）；少数端点拒绝未知字段，在目录里显式关掉。
	private static bool HasToolHistory(IReadOnlyList<Message> messages) =>
		messages.Any(static message => message is ToolResultMessage ||
			(message is AssistantMessage assistant && assistant.Content.Any(static block => block is ToolCall)));

	internal static bool StrictSamplingFor(Model model) => ModelCompat.FlagOr(model, "supportsStrictMode", true);

	internal static void WriteTools(Utf8JsonWriter writer, IReadOnlyList<Tool> tools, bool supportsStrictMode)
	{
		writer.WritePropertyName("tools");
		writer.WriteStartArray();
		foreach (var tool in tools)
		{
			var strict = ConstrainedSampling.ResolveStrictJsonSchema(tool, supportsStrictMode);
			writer.WriteStartObject();
			writer.WriteString("type", "function");
			writer.WritePropertyName("function");
			writer.WriteStartObject();
			writer.WriteString("name", tool.Name);
			writer.WriteString("description", tool.Description);
			writer.WritePropertyName("parameters");
			ConstrainedSampling.GetJsonSchemaToolParameters(tool, strict).WriteTo(writer);
			if (supportsStrictMode)
			{
				// 只支持严格模式的端点不必看到这个字段；不支持的端点看到它会直接拒绝。
				writer.WriteBoolean("strict", strict == true);
			}

			writer.WriteEndObject();
			writer.WriteEndObject();
		}

		writer.WriteEndArray();
	}

	internal static bool UsesMaxCompletionTokens(Model model) =>
		model.BaseUrl.Contains("api.openai.com", StringComparison.OrdinalIgnoreCase);

	private static readonly string[] ReasoningFields = ["reasoning_content", "reasoning", "reasoning_text"];

	internal static bool RequiresReasoningContent(Model model) =>
		model.Compat?["requiresReasoningContentOnAssistantMessages"] is JsonValue flag &&
		flag.TryGetValue<bool>(out var required) &&
		required;
}
