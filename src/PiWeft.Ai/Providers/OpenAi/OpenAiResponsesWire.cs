using System.Text.Json;
using System.Text.Json.Nodes;
using PiWeft.Ai.Utils;

using PiWeft.Ai.Json;
namespace PiWeft.Ai.Providers.OpenAi;

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
		bool store = false,
		string? promptCacheKey = null,
		string? promptCacheRetention = null,
		JsonObject? promptCacheOptions = null,
		bool supportsStrictMode = false,
		bool systemPromptAsInputItem = false,
		string? configurationUpdateEffort = null)
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

		if (promptCacheRetention is { Length: > 0 })
		{
			writer.WriteString("prompt_cache_retention", promptCacheRetention);
		}

		if (promptCacheOptions is { Count: > 0 })
		{
			writer.WritePropertyName("prompt_cache_options");
			promptCacheOptions.WriteTo(writer);
		}

		var systemPrompt = Transcript.GetInitialSystemText(messages);
		if (!systemPromptAsInputItem && !string.IsNullOrWhiteSpace(systemPrompt))
		{
			writer.WriteString("instructions", TextSanitizer.SanitizeSurrogates(systemPrompt));
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
			// 思考档变化改走 configuration_update 输入项，这里的 effort 固定：否则每次换档
			// 都会改掉请求前缀，提示词缓存在 GPT-6 上全部失效。
			writer.WriteString("effort", configurationUpdateEffort is null ? reasoningEffort : ConfigurationUpdateRequestEffort);
			writer.WriteString("summary", "auto");
			writer.WriteEndObject();

			writer.WritePropertyName("include");
			writer.WriteStartArray();
			writer.WriteStringValue("reasoning.encrypted_content");
			writer.WriteEndArray();
		}

		writer.WritePropertyName("input");
		WriteInput(writer, model, messages, systemPromptAsInputItem ? systemPrompt : null, configurationUpdateEffort);

		if (tools.Count > 0)
		{
			writer.WritePropertyName("tools");
			writer.WriteStartArray();
			foreach (var tool in tools)
			{
				var strict = ConstrainedSampling.ResolveStrictJsonSchema(tool, supportsStrictMode);
				writer.WriteStartObject();
				writer.WriteString("type", "function");
				writer.WriteString("name", tool.Name);
				writer.WriteString("description", tool.Description);
				writer.WritePropertyName("parameters");
				ConstrainedSampling.GetJsonSchemaToolParameters(tool, strict).WriteTo(writer);
				if (supportsStrictMode)
				{
					writer.WriteBoolean("strict", strict == true);
				}

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

	private const string ConfigurationUpdateRequestEffort = "medium";

	private static void WriteInput(
		Utf8JsonWriter writer,
		Model model,
		IReadOnlyList<Message> messages,
		string? leadingSystemText,
		string? configurationUpdateEffort)
	{
		writer.WriteStartArray();
		// 系统提示词作为首条输入项：GPT-6.2 的后端把顶层 instructions 排在 input 之后，
		// 新回合一追加用户消息就把它挤掉，提示词缓存永远命不中。
		if (!string.IsNullOrWhiteSpace(leadingSystemText))
		{
			// 与 pi 同形：developer/system 项的 content 直接是字符串，不包一层 input_text 数组。
			writer.WriteStartObject();
			writer.WriteString("role", model.Reasoning && ModelCompat.Flag(model, "supportsDeveloperRole") != false ? "developer" : "system");
			writer.WriteString("content", TextSanitizer.SanitizeSurrogates(leadingSystemText));
			writer.WriteEndObject();
		}

		foreach (var message in messages)
		{
			switch (message)
			{
				case SystemMessage:
					// 提示词放在顶层 "instructions" 字段或首条输入项，没有中段 system。
					break;

				case UserMessage user:
					WriteUser(writer, user);
					break;

				case AssistantMessage assistant:
					// 历史里记录过供应商真实档位时，在那条回复前重申一次：换档不再改请求前缀。
					if (configurationUpdateEffort is not null &&
						assistant.Api == model.Api && assistant.Provider == model.Provider && assistant.Model == model.Id &&
						assistant.ProviderThinkingLevel is { Length: > 0 } previousEffort)
					{
						WriteConfigurationUpdate(writer, previousEffort);
					}

					WriteAssistant(writer, assistant);
					break;

				case ToolResultMessage result:
					WriteToolResult(writer, result);
					break;
			}
		}

		if (configurationUpdateEffort is not null)
		{
			WriteConfigurationUpdate(writer, configurationUpdateEffort);
		}

		writer.WriteEndArray();
	}

	private static void WriteConfigurationUpdate(Utf8JsonWriter writer, string effort)
	{
		writer.WriteStartObject();
		writer.WriteString("type", "configuration_update");
		writer.WritePropertyName("reasoning");
		writer.WriteStartObject();
		writer.WriteString("effort", effort);
		writer.WriteEndObject();
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
						// 签名损坏时直接丢弃，不让整个请求失败。
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
			writer.WriteString("output", TextSanitizer.SanitizeSurrogates(!string.IsNullOrWhiteSpace(text) ? text : "(no tool output)"));
		}
		else
		{
			writer.WritePropertyName("output");
			writer.WriteStartArray();
			writer.WriteStartObject();
			writer.WriteString("type", "input_text");
			writer.WriteString("text", TextSanitizer.SanitizeSurrogates(!string.IsNullOrWhiteSpace(text) ? text : "(see attached image)"));
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
