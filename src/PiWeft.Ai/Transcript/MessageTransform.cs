using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace PiWeft.Ai;

/// <summary>
/// 请求前的消息归一化。对应 pi 的 <c>api/transform-messages.ts</c>：
/// 丢弃不属于当前模型的 thinking、跳过失败的助手回合、为孤儿工具调用补结果、
/// 归一工具调用 id，并把不支持图片的模型收到的图片降级为文本。
/// </summary>
public static class MessageTransform
{
	private const string UserImagePlaceholder = "(image omitted: model does not support images)";
	private const string ToolImagePlaceholder = "(tool image omitted: model does not support images)";

	/// <summary>应用全部变换，返回可以安全发送给 <paramref name="model"/> 的消息。</summary>
	public static List<Message> Transform(IReadOnlyList<Message> messages, Model model)
	{
		ArgumentNullException.ThrowIfNull(messages);
		ArgumentNullException.ThrowIfNull(model);

		var toolCallIds = new Dictionary<string, string>(StringComparer.Ordinal);
		var transformed = new List<Message>(messages.Count);

		foreach (var message in messages)
		{
			switch (message)
			{
				case UserMessage user:
					transformed.Add(DowngradeUserImages(user, model));
					break;

				case ToolResultMessage result:
					transformed.Add(DowngradeToolImages(result, model, toolCallIds));
					break;

				case AssistantMessage assistant:
					transformed.Add(TransformAssistant(assistant, model, toolCallIds));
					break;

				default:
					transformed.Add(message);
					break;
			}
		}

		return ReconcileToolCalls(transformed);
	}

	private static AssistantMessage TransformAssistant(
		AssistantMessage message,
		Model model,
		Dictionary<string, string> toolCallIds)
	{
		// 旧会话/手写消息可能没有 provider/api/model 元数据；此时按当前模型处理，
		// 否则升级后所有历史 thinking 都会被当成“别的模型”丢掉。
		var hasIdentity = message.Provider.Length > 0 || message.Api.Length > 0 || message.Model.Length > 0;
		var sameModel = !hasIdentity ||
			(string.Equals(message.Provider, model.Provider, StringComparison.Ordinal) &&
			 string.Equals(message.Api, model.Api, StringComparison.Ordinal) &&
			 string.Equals(message.Model, model.Id, StringComparison.Ordinal));

		var content = new List<ContentBlock>(message.Content.Count);
		foreach (var block in message.Content)
		{
			switch (block)
			{
				case ThinkingContent thinking:
					// 其它模型的 thinking/signature 不可重放；同模型的空思考块也没有价值。
					if (!sameModel)
					{
						continue;
					}

					if (!thinking.Redacted.GetValueOrDefault() &&
						string.IsNullOrEmpty(thinking.ThinkingSignature) &&
						string.IsNullOrWhiteSpace(thinking.Thinking))
					{
						continue;
					}

					content.Add(thinking);
					break;

				case TextContent text:
					content.Add(sameModel ? text : new TextContent { Text = text.Text });
					break;

				case ToolCall call:
				{
					var transformed = call;
					if (!sameModel)
					{
						if (transformed.ThoughtSignature is not null)
						{
							transformed = transformed with { ThoughtSignature = null };
						}

						var normalizedId = NormalizeToolCallId(transformed.Id);
						if (!string.Equals(normalizedId, transformed.Id, StringComparison.Ordinal))
						{
							toolCallIds[transformed.Id] = normalizedId;
							transformed = transformed with { Id = normalizedId };
						}
					}

					content.Add(transformed);
					break;
				}

				default:
					content.Add(block);
					break;
			}
		}

		return message with { Content = content };
	}

	private static UserMessage DowngradeUserImages(UserMessage message, Model model)
	{
		if (model.SupportsImages || message.Content.IsText || message.Content.Blocks is not { Count: > 0 } blocks)
		{
			return message;
		}

		return message with
		{
			Content = MessageContent.FromBlocks(ReplaceImages(blocks, UserImagePlaceholder)),
		};
	}

	private static ToolResultMessage DowngradeToolImages(
		ToolResultMessage message,
		Model model,
		Dictionary<string, string> toolCallIds)
	{
		var result = message;
		if (!model.SupportsImages)
		{
			result = result with { Content = ReplaceImages(result.Content, ToolImagePlaceholder) };
		}

		if (toolCallIds.TryGetValue(result.ToolCallId, out var normalizedId))
		{
			result = result with { ToolCallId = normalizedId };
		}

		return result;
	}

	private static List<ContentBlock> ReplaceImages(IReadOnlyList<ContentBlock> blocks, string placeholder)
	{
		var replaced = new List<ContentBlock>(blocks.Count);
		var previousWasPlaceholder = false;

		foreach (var block in blocks)
		{
			if (block is ImageContent)
			{
				if (!previousWasPlaceholder)
				{
					replaced.Add(new TextContent { Text = placeholder });
				}

				previousWasPlaceholder = true;
				continue;
			}

			replaced.Add(block);
			previousWasPlaceholder = block is TextContent text && string.Equals(text.Text, placeholder, StringComparison.Ordinal);
		}

		return replaced;
	}

	/// <summary>
	/// 重放前整理工具调用与结果：跳过 error/aborted 助手消息，为没有结果的调用补一条
	/// “No result provided”，并把 system 消息挪到工具结果之后。
	/// </summary>
	private static List<Message> ReconcileToolCalls(IReadOnlyList<Message> messages)
	{
		var result = new List<Message>(messages.Count);
		var pendingCalls = new List<ToolCall>();
		var existingResults = new HashSet<string>(StringComparer.Ordinal);
		var heldSystem = new List<Message>();

		void ClosePending()
		{
			foreach (var call in pendingCalls)
			{
				if (existingResults.Contains(call.Id))
				{
					continue;
				}

				result.Add(new ToolResultMessage
				{
					ToolCallId = call.Id,
					ToolName = call.Name,
					Content = [new TextContent { Text = "No result provided" }],
					IsError = true,
					Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
				});
			}

			pendingCalls.Clear();
			existingResults.Clear();

			if (heldSystem.Count > 0)
			{
				result.AddRange(heldSystem);
				heldSystem.Clear();
			}
		}

		foreach (var message in messages)
		{
			switch (message)
			{
				case AssistantMessage assistant:
					ClosePending();

					// 失败/中止的助手回合可能只包含半截推理或不完整工具调用，不能重放。
					if (assistant.StopReason is StopReason.Error or StopReason.Aborted)
					{
						continue;
					}

					var calls = assistant.Content.OfType<ToolCall>().ToList();
					if (calls.Count > 0)
					{
						pendingCalls.AddRange(calls);
					}

					result.Add(assistant);
					break;

				case ToolResultMessage toolResult:
					existingResults.Add(toolResult.ToolCallId);
					result.Add(toolResult);
					break;

				case SystemMessage system:
					if (pendingCalls.Count > 0)
					{
						heldSystem.Add(system);
					}
					else
					{
						result.Add(system);
					}

					break;

				case UserMessage:
					// 新的用户回合意味着上一批工具调用已经结束。
					ClosePending();
					result.Add(message);
					break;

				default:
					result.Add(message);
					break;
			}
		}

		ClosePending();
		return result;
	}

	/// <summary>把 provider 不接受的工具调用 id 归一成短而稳定的形式。</summary>
	private static string NormalizeToolCallId(string id)
	{
		if (id.Length is > 0 and <= 64 && id.All(static c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-'))
		{
			return id;
		}

		var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id))).ToLowerInvariant();
		return $"call_{hash[..32]}";
	}
}
