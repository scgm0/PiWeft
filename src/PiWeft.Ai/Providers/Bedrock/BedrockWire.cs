using System.Buffers.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiWeft.Ai.Utils;

namespace PiWeft.Ai.Providers.Bedrock;

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
		// Bedrock 按 cachePoint 切缓存；不加的话每轮都把整段对话重算一遍。
		var cacheRetention = options.CacheRetention ?? CacheRetentions.Short;
		var cachePoint = cacheRetention != CacheRetentions.None && SupportsPromptCaching(model);
		WriteMessages(writer, model, messages, cachePoint, cacheRetention == CacheRetentions.Long);

		if (tools.Count > 0)
		{
			var supportsStrictMode = ModelCompat.FlagOr(model, "supportsStrictMode", false);
			writer.WritePropertyName("toolConfig");
			writer.WriteStartObject();
			writer.WritePropertyName("tools");
			writer.WriteStartArray();
			foreach (var tool in tools)
			{
				var strict = ConstrainedSampling.ResolveStrictJsonSchema(tool, supportsStrictMode);
				writer.WriteStartObject();
				writer.WritePropertyName("toolSpec");
				writer.WriteStartObject();
				writer.WriteString("name", tool.Name);
				writer.WriteString("description", tool.Description);
				if (strict == true)
				{
					writer.WriteBoolean("strict", true);
				}

				writer.WritePropertyName("inputSchema");
				writer.WriteStartObject();
				writer.WritePropertyName("json");
				ConstrainedSampling.GetJsonSchemaToolParameters(tool, strict).WriteTo(writer);
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

	private static void WriteMessages(
		Utf8JsonWriter writer,
		Model model,
		IReadOnlyList<Message> messages,
		bool cachePoint,
		bool longTtl)
	{
		var last = messages.Count - 1;
		writer.WriteStartArray();
		for (var i = 0; i < messages.Count; i++)
		{
			switch (messages[i])
			{
				case SystemMessage:
					// 由顶层的 system 字段承载。
					break;

				case UserMessage user:
					WriteUser(writer, user, cachePoint && i == last, longTtl);
					break;

				case AssistantMessage assistant:
					WriteAssistant(writer, assistant);
					break;

				case ToolResultMessage:
				{
					// Bedrock 要求同一轮次的所有 toolResult 块放在一条 user 消息内，且一个 toolUseId
					// 只能出现一次：文本与图片写在同一个 toolResult 的 content 数组里。
					writer.WriteStartObject();
					writer.WriteString("role", "user");
					writer.WritePropertyName("content");
					writer.WriteStartArray();

					// Claude 拒绝错误工具结果里的非文本内容，图片挪到本轮所有结果之后。
					var moveErrorImages = IsAnthropicClaude(model);
					var trailing = new List<(ToolResultMessage Result, IReadOnlyList<ImageContent> Images)>();

					while (i < messages.Count && messages[i] is ToolResultMessage result)
					{
						var moved = moveErrorImages && result.IsError
							? result.Content.OfType<ImageContent>().ToList()
							: [];
						WriteToolResult(writer, result, moved.Count > 0);
						if (moved.Count > 0)
						{
							trailing.Add((result, moved));
						}

						i++;
					}

					i--;
					foreach (var (result, images) in trailing)
					{
						writer.WriteStartObject();
						writer.WriteString(
							"text",
							TextSanitizer.SanitizeSurrogates(
								$"Images from the error result of tool call {result.ToolCallId} ({result.ToolName}):"));
						writer.WriteEndObject();
						foreach (var image in images)
						{
							WriteImageBlock(writer, image);
						}
					}

					if (cachePoint && i == last)
					{
						WriteCachePoint(writer, longTtl);
					}

					writer.WriteEndArray();
					writer.WriteEndObject();
					break;
				}
			}
		}

		writer.WriteEndArray();
	}

	private static void WriteUser(Utf8JsonWriter writer, UserMessage message, bool cachePoint, bool longTtl)
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
			if (cachePoint)
			{
				WriteCachePoint(writer, longTtl);
			}

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
					WriteImageBlock(writer, image);
					break;
			}
		}

		if (cachePoint)
		{
			WriteCachePoint(writer, longTtl);
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

	private static void WriteToolResult(Utf8JsonWriter writer, ToolResultMessage message, bool imagesMovedOut)
	{
		writer.WriteStartObject();
		writer.WritePropertyName("toolResult");
		writer.WriteStartObject();
		writer.WriteString("toolUseId", message.ToolCallId);
		writer.WritePropertyName("content");
		writer.WriteStartArray();

		var wroteContent = false;
		var wrotePlaceholder = false;
		foreach (var block in message.Content)
		{
			switch (block)
			{
				case TextContent text when !string.IsNullOrWhiteSpace(text.Text):
					writer.WriteStartObject();
					writer.WriteString("text", TextSanitizer.SanitizeSurrogates(text.Text));
					writer.WriteEndObject();
					wroteContent = true;
					break;
				case ImageContent image when !imagesMovedOut:
					WriteImageBlock(writer, image);
					wroteContent = true;
					break;
				case ImageContent when imagesMovedOut:
					// 图片已移到本轮之后；这里留一句话，让模型知道原位曾有图。
					if (!wrotePlaceholder)
					{
						writer.WriteStartObject();
						writer.WriteString("text", ErrorToolImagePlaceholder);
						writer.WriteEndObject();
						wrotePlaceholder = true;
						wroteContent = true;
					}

					break;
			}
		}

		if (!wroteContent)
		{
			writer.WriteStartObject();
			writer.WriteString("text", "(no output)");
			writer.WriteEndObject();
		}

		writer.WriteEndArray();
		writer.WriteString("status", message.IsError ? "error" : "success");
		writer.WriteEndObject();
		writer.WriteEndObject();
	}

	/// Bedrock 的提示词缓存只对能带 cachePoint 的模型开放；不支持的模型收到 cachePoint 会直接报错。
	private static bool SupportsPromptCaching(Model model)
	{
		if (!Contains(model, "claude"))
		{
			// 应用推理配置的 ARN 不带模型名，用环境变量显式开启。
			return Environment.GetEnvironmentVariable("AWS_BEDROCK_FORCE_CACHE") == "1";
		}

		return Contains(model, "fable-5") || Contains(model, "opus-5") || Contains(model, "sonnet-5") ||
			Contains(model, "haiku-5") || Contains(model, "-4-") ||
			Contains(model, "claude-3-7-sonnet") || Contains(model, "claude-3-5-haiku");
	}

	private static bool Contains(Model model, string value) =>
		model.Id.Contains(value, StringComparison.OrdinalIgnoreCase) ||
		model.Name.Contains(value, StringComparison.OrdinalIgnoreCase);

	private static void WriteCachePoint(Utf8JsonWriter writer, bool longTtl)
	{
		writer.WriteStartObject();
		writer.WritePropertyName("cachePoint");
		writer.WriteStartObject();
		writer.WriteString("type", "default");
		if (longTtl)
		{
			writer.WriteString("ttl", "1h");
		}

		writer.WriteEndObject();
		writer.WriteEndObject();
	}

	private static void WriteImageBlock(Utf8JsonWriter writer, ImageContent image)
	{
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
	}

	/// Claude 拒绝错误工具结果里除文本以外的内容，图片要挪到本轮结果之后。
	private const string ErrorToolImagePlaceholder = "(image attached after the tool results)";

	/// Claude 拒绝错误工具结果里除文本以外的内容，图片要挪到本轮结果之后。
	private static bool IsAnthropicClaude(Model model) =>
		model.Id.Contains("anthropic.claude", StringComparison.OrdinalIgnoreCase) ||
		model.Id.Contains("anthropic/claude", StringComparison.OrdinalIgnoreCase) ||
		model.Name.Contains("anthropic.claude", StringComparison.OrdinalIgnoreCase) ||
		model.Name.Contains("anthropic/claude", StringComparison.OrdinalIgnoreCase) ||
		model.Name.Contains("claude", StringComparison.OrdinalIgnoreCase);

	internal static string FormatOf(string mimeType) => mimeType switch
	{
		"image/jpeg" => "jpeg",
		"image/png" => "png",
		"image/gif" => "gif",
		"image/webp" => "webp",
		_ => mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ? mimeType[6..] : mimeType,
	};
}
