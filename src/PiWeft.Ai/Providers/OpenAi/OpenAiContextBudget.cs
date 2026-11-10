namespace PiWeft.Ai.Providers.OpenAi;

/// pi 的 <c>clampMaxTokensToContext</c> 轻量版：输入按字符粗估，给输出留 15% 输入余量。
/// OpenRouter 用自己的 tokenizer 计数，输入可能比 provider 估算高十几个百分点；不收敛会在
/// 远未满窗时收到 400，再被误判成上下文溢出。
internal static class OpenAiContextBudget
{
	private const int SafetyTokens = 4096;
	private const int MinOutputWithRatioMargin = 32768;

	public static int? Clamp(Model model, IReadOnlyList<Message> messages, int? requested)
	{
		if (requested is not { } maxTokens || model.ContextWindow <= 0)
		{
			return requested;
		}

		var input = Estimate(messages);
		var margin = Math.Max(SafetyTokens, (int)Math.Ceiling(input * 0.15));
		var room = model.ContextWindow - input - margin;
		if (room < MinOutputWithRatioMargin)
		{
			room = Math.Min(MinOutputWithRatioMargin, model.ContextWindow - input - SafetyTokens);
		}

		return (int)Math.Min(maxTokens, Math.Max(1L, room));
	}

	private static long Estimate(IReadOnlyList<Message> messages)
	{
		long chars = 0;
		foreach (var message in messages)
		{
			switch (message)
			{
				case SystemMessage system:
					chars += Transcript.GetText(system).Length;
					if (system.ToolsAdded is { Count: > 0 } tools)
					{
						foreach (var tool in tools)
						{
							chars += tool.Name.Length + tool.Description.Length + tool.Parameters.ToJsonString().Length;
						}
					}

					break;
				case UserMessage user:
					chars += ContentChars(user.Content);
					break;
				case AssistantMessage assistant:
					foreach (var block in assistant.Content)
					{
						chars += block switch
						{
							TextContent text => text.Text.Length,
							ThinkingContent thinking => thinking.Thinking.Length,
							ToolCall call => call.Name.Length + call.Arguments.ToJsonString().Length,
							_ => 0,
						};
					}

					break;
				case ToolResultMessage result:
					chars += ContentChars(result.Content);
					break;
				case CustomMessage custom:
					chars += ContentChars(custom.Content);
					break;
			}
		}

		// 图片无法按字符估：每张按 1024 token 留余量，宁可少请求输出也不要 400。
		var images = messages.Sum(static message => message switch
		{
			UserMessage user => user.Content.Blocks?.Count(static block => block is ImageContent) ?? 0,
			ToolResultMessage result => result.Content.Count(static block => block is ImageContent),
			_ => 0,
		});
		return chars / 4 + images * 1024L;
	}

	private static long ContentChars(MessageContent content) =>
		content.Blocks is { Count: > 0 } blocks
			? blocks.Sum(static block => block switch
			{
				TextContent text => text.Text.Length,
				ImageContent => 0,
				_ => 0,
			})
			: content.Text?.Length ?? 0;

	private static long ContentChars(IReadOnlyList<ContentBlock> blocks) =>
		blocks.Sum(static block => block switch
		{
			TextContent text => text.Text.Length,
			ImageContent => 0,
			_ => 0,
		});
}
