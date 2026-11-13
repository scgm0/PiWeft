using System.Text.Json;
using System.Text.Json.Nodes;
using PiWeft.Ai.Utils;

namespace PiWeft.Ai.Providers.Anthropic;

internal static class AnthropicWire
{
	public static void WriteRequest(
		Utf8JsonWriter writer,
		Model model,
		IReadOnlyList<Message> messages,
		IReadOnlyList<Tool> tools,
		StreamOptions options,
		ThinkingConfig thinking,
		int maxTokens,
		bool oauth = false,
		bool disablePreferredStrictTools = false)
	{
		writer.WriteStartObject();
		writer.WriteString("model", model.Id);
		writer.WriteNumber("max_tokens", maxTokens);
		writer.WriteBoolean("stream", true);

		var systemText = Transcript.GetInitialSystemText(messages);
		// Claude OAuth（sk-ant-oat）要带 Claude Code 身份提示词；官方 API key 不需要。
		if (oauth || !string.IsNullOrWhiteSpace(systemText))
		{
			writer.WritePropertyName("system");
			writer.WriteStartArray();
			if (oauth)
			{
				writer.WriteStartObject();
				writer.WriteString("type", "text");
				writer.WriteString("text", "You are Claude Code, Anthropic's official CLI for Claude.");
				WriteCacheControl(writer, options);
				writer.WriteEndObject();
			}
			if (!string.IsNullOrWhiteSpace(systemText))
			{
				writer.WriteStartObject();
				writer.WriteString("type", "text");
				writer.WriteString("text", TextSanitizer.SanitizeSurrogates(systemText));
				WriteCacheControl(writer, options);
				writer.WriteEndObject();
			}
			writer.WriteEndArray();
		}

		// 扩展思考与高版本 Claude 不支持 temperature；Managed effort 模型也一律不发。
		if (options.Temperature is { } temperature &&
			!thinking.Enabled &&
			!ModelCompat.FlagOr(model, "supportsMidConvoEffort", false) &&
			ModelCompat.FlagOr(model, "supportsTemperature", true))
		{
			writer.WriteNumber("temperature", temperature);
		}

		writer.WritePropertyName("messages");
		WriteMessages(writer, messages);

		if (tools.Count > 0)
		{
			// Claude 的严格工具要模型目录显式声明（compat.supportsStrictTools），
			// 且只接受 JSON Schema 的一个子集；不满足的工具仍按普通 schema 发。
			var supportsStrictTools = ModelCompat.FlagOr(model, "supportsStrictTools", false);
			writer.WritePropertyName("tools");
			writer.WriteStartArray();
			foreach (var tool in tools)
			{
				// disablePreferredStrictTools：严格工具额度已超限，"prefer" 的工具不再要严格。
				var strict =
					disablePreferredStrictTools && ConstrainedSampling.PrefersStrictJsonSchema(tool)
						? null
						: ConstrainedSampling.ResolveStrictJsonSchema(
							tool,
							supportsStrictTools,
							ConstrainedSampling.IsAnthropicStrictUnsupportedKeyword);
				var parameters = ConstrainedSampling.GetJsonSchemaToolParameters(tool, strict);
				writer.WriteStartObject();
				writer.WriteString("name", tool.Name);
				writer.WriteString("description", tool.Description);
				if (strict == true)
				{
					writer.WriteBoolean("strict", true);
				}

				writer.WritePropertyName("input_schema");
				WriteInputSchema(writer, parameters, strict == true);
				writer.WriteEndObject();
			}

			writer.WriteEndArray();
		}

		if (thinking.IsAdaptive)
		{
			writer.WritePropertyName("thinking");
			writer.WriteStartObject();
			writer.WriteString("type", "adaptive");
			writer.WriteString("display", "summarized");
			if (ModelCompat.FlagOr(model, "supportsMidConvoEffort", false))
			{
				// 前缀不一致时由服务端丢弃旧 thinking 块，避免历史里残留的签名让整个会话持续 400。
				writer.WritePropertyName("block_binding");
				writer.WriteStartObject();
				writer.WriteString("prefix_mismatch_behavior", "drop_block");
				writer.WriteEndObject();
			}

			writer.WriteEndObject();

			writer.WritePropertyName("output_config");
			writer.WriteStartObject();
			writer.WriteString("effort", thinking.Effort ?? "high");
			writer.WriteEndObject();
		}
		else if (thinking.Enabled)
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

		WriteFallbacks(writer, model);

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

	/// Anthropic 只认根部的 <c>type</c>/<c>properties</c>/<c>required</c> 与共享定义：
	/// 根级 <c>anyOf</c>/<c>oneOf</c>/<c>allOf</c> 会直接 400。严格模式发完整 schema，
	/// 普通模式只发根部四个字段（与 pi 的 <c>legacyInputSchema</c> 一致）。
	private static void WriteInputSchema(Utf8JsonWriter writer, JsonObject parameters, bool strict)
	{
		writer.WriteStartObject();
		if (strict)
		{
			foreach (var (key, value) in parameters)
			{
				if (key is "type" or "properties" or "required" or "$defs" or "definitions")
				{
					continue;
				}

				writer.WritePropertyName(key);
				value?.WriteTo(writer);
			}
		}

		writer.WriteString("type", "object");
		writer.WritePropertyName("properties");
		(parameters["properties"] as JsonObject ?? new JsonObject()).WriteTo(writer);
		writer.WritePropertyName("required");
		(parameters["required"] as JsonArray ?? new JsonArray()).WriteTo(writer);
		// pi 1.5.2+：非严格模式也保留根 additionalProperties，模型才会知道是否允许多余参数。
		if (!strict && parameters["additionalProperties"] is { } additionalProperties)
		{
			writer.WritePropertyName("additionalProperties");
			additionalProperties.WriteTo(writer);
		}

		foreach (var key in new[] { "$defs", "definitions" })
		{
			if (parameters[key] is { } definitions)
			{
				writer.WritePropertyName(key);
				definitions.WriteTo(writer);
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
					var errorImages = new List<(string Caption, IReadOnlyList<ImageContent> Images)>();
					while (i < messages.Count && messages[i] is ToolResultMessage result)
					{
						var split = SplitErrorImages(result);
						WriteToolResult(writer, split?.Result ?? result);
						if (split is { } value)
						{
							errorImages.Add((value.Caption, value.Images));
						}

						i++;
					}

					// Anthropic 拒绝错误工具结果里出现图片；把图片放到所有 tool_result 之后。
					foreach (var (caption, images) in errorImages)
					{
						writer.WriteStartObject();
						writer.WriteString("type", "text");
						writer.WriteString("text", TextSanitizer.SanitizeSurrogates(caption));
						writer.WriteEndObject();
						foreach (var image in images)
						{
							WriteImage(writer, image);
						}
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
					WriteImage(writer, image);
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
		ThinkingContent => false,
		ToolCall => false,
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
					WriteImage(writer, image);
					break;
			}
		}

		writer.WriteEndArray();
		writer.WriteBoolean("is_error", message.IsError);
		writer.WriteEndObject();
	}

	/// 把一条助手消息按线上形态写成 <c>messages</c> 数组里的一项，供 message thread 记录前缀。
	public static JsonArray WriteAssistantReply(AssistantMessage message)
	{
		var bytes = JsonPayload.Write(writer =>
		{
			writer.WriteStartArray();
			WriteAssistant(writer, message);
			writer.WriteEndArray();
		});

		return JsonNode.Parse(bytes) as JsonArray ?? [];
	}

	private const string ErrorToolImagePlaceholder = "(image attached after the tool results)";

	private static void WriteImage(Utf8JsonWriter writer, ImageContent image)
	{
		writer.WriteStartObject();
		writer.WriteString("type", "image");
		writer.WritePropertyName("source");
		writer.WriteStartObject();
		writer.WriteString("type", "base64");
		writer.WriteString("media_type", image.MimeType);
		writer.WriteString("data", image.Data);
		writer.WriteEndObject();
		writer.WriteEndObject();
	}

	/// 错误工具结果的图片要移到所有 tool_result 之后；这里把图片换成占位文字并带上图片列表。
	private static (ToolResultMessage Result, string Caption, IReadOnlyList<ImageContent> Images)? SplitErrorImages(
		ToolResultMessage message)
	{
		if (!message.IsError)
		{
			return null;
		}

		var images = message.Content.OfType<ImageContent>().ToList();
		if (images.Count == 0)
		{
			return null;
		}

		var content = new List<ContentBlock>(message.Content.Count);
		var previousWasPlaceholder = false;
		foreach (var block in message.Content)
		{
			if (block is ImageContent)
			{
				if (!previousWasPlaceholder)
				{
					content.Add(new TextContent { Text = ErrorToolImagePlaceholder });
				}

				previousWasPlaceholder = true;
				continue;
			}

			content.Add(block);
			previousWasPlaceholder = block is TextContent text &&
				string.Equals(text.Text, ErrorToolImagePlaceholder, StringComparison.Ordinal);
		}

		return (
			message with { Content = content },
			$"Images from the error result of tool call {message.ToolCallId} ({message.ToolName}):",
			images);
	}

	private static void WriteFallbacks(Utf8JsonWriter writer, Model model)
	{
		var fallbacks = ModelCompat.Array(model, "allowedFallbackModels")
			.OfType<JsonObject>()
			.Select(static item => item["model"] is JsonValue value && value.TryGetValue<string>(out var id) ? id : null)
			.Where(static id => id is { Length: > 0 })
			.ToList();
		if (fallbacks.Count == 0)
		{
			return;
		}

		writer.WritePropertyName("fallbacks");
		writer.WriteStartArray();
		foreach (var modelId in fallbacks)
		{
			writer.WriteStartObject();
			writer.WriteString("model", modelId);
			writer.WriteEndObject();
		}

		writer.WriteEndArray();
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
