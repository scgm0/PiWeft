using System.Text;
using System.Text.Json.Nodes;
using PiWeft.Ai;
using PiWeft.Ai.Json;
using PiWeft.CodingAgent.Tools;
namespace PiWeft.CodingAgent;

/// 编码代理特有的消息，以及把它们转换成面向 LLM 的消息的转换器。
/// 对应 pi 的 <c>core/messages.ts</c>。
public static class CodingMessages
{
	public const string CompactionSummaryPrefix = """
		The conversation history before this point was compacted into the following summary:

		<summary>

		""";

	public const string CompactionSummarySuffix = """

		</summary>
		""";

	public const string BranchSummaryPrefix = """
		The following is a summary of a branch that this conversation came back from:

		<summary>

		""";

	public const string BranchSummarySuffix = """

		</summary>
		""";

	public static bool IsConvertibleRole(string role) =>
		role is "bashExecution" or "custom" or "branchSummary" or "compactionSummary";

	/// 把代理的会话记录转换成 provider 能理解的消息：纯状态消息被过滤掉，
	/// 自定义角色变成 user 消息，与 pi 的 <c>convertToLlm</c> 完全一致。
	public static IReadOnlyList<Message> ConvertToLlm(IReadOnlyList<Message> messages, CancellationToken cancellationToken)
	{
		var converted = new List<Message>(messages.Count);
		foreach (var message in messages)
		{
			if (message is not CustomMessage custom)
			{
				converted.Add(SanitizeToolResult(message));
				continue;
			}

			switch (custom.CustomRole)
			{
				case "bashExecution":
					if (custom.Raw?["excludeFromContext"]?.GetValue<bool>() == true)
					{
						continue;
					}

					converted.Add(User(custom, BashExecutionToText(custom)));
					break;

				case "branchSummary":
					converted.Add(User(
						custom,
						BranchSummaryPrefix + ShellTextSanitizer.Sanitize(RawString(custom, "summary")) + BranchSummarySuffix));
					break;

				case "compactionSummary":
					converted.Add(User(
						custom,
						CompactionSummaryPrefix + ShellTextSanitizer.Sanitize(RawString(custom, "summary")) + CompactionSummarySuffix));
					break;

				default:
					converted.Add(custom.Content.IsText && custom.Content.Text is { Length: 0 }
						? custom with { Content = MessageContent.Empty }
						: User(custom, SanitizeToolContent(custom.Content)));
					break;
			}
		}

		return converted;
	}

	public static string BashExecutionToText(CustomMessage message)
	{
		var raw = message.Raw;
		var builder = new StringBuilder();
		builder.Append("Ran `")
			.Append(ShellTextSanitizer.Sanitize(RawString(message, "command")))
			.Append("`\n");

		var output = ShellTextSanitizer.Sanitize(RawString(message, "output"));
		if (output.Contains('\r'))
		{
			// 用户 `!` 命令按 pi 的 bash-executor 语义去掉 CR；旧会话也在这里一并修复。
			output = output.Replace("\r", string.Empty);
		}

		if (output.Length > 0)
		{
			builder.Append("```\n").Append(output).Append("\n```");
		}
		else
		{
			builder.Append("(no output)");
		}

		if (raw?["cancelled"]?.GetValue<bool>() == true)
		{
			builder.Append("\n\n(command cancelled)");
		}
		else if (raw?["exitCode"] is { } exitCode &&
			exitCode.GetValueKind() == System.Text.Json.JsonValueKind.Number &&
			exitCode.GetValue<int>() != 0)
		{
			builder.Append("\n\nCommand exited with code ").Append(exitCode.GetValue<int>());
		}

		if (raw?["truncated"]?.GetValue<bool>() == true &&
			RawString(message, "fullOutputPath") is { Length: > 0 } fullOutputPath)
		{
			builder.Append("\n\n[Output truncated. Full output: ").Append(fullOutputPath).Append(']');
		}

		return builder.ToString();
	}

	/// 粗略的上下文大小估算（每四个字符一个令牌，外加每条消息的小额开销）。pi 的估算器更精细，
	/// 这里的实现只用于压缩阈值判断。
	public static long EstimateTokens(IEnumerable<Message> messages)
	{
		long tokens = 0;
		foreach (var message in messages)
		{
			tokens += 4;
			foreach (var block in Blocks(message))
			{
				tokens += block switch
				{
					TextContent text => text.TextLength / 4,
					ThinkingContent thinking => thinking.Thinking.Length / 4,
					ToolCall call => (call.Name.Length + call.Arguments.ToJsonString(PiJson.NodeOptions).Length) / 4,
					ImageContent => 1000,
					_ => 0,
				};
			}
		}

		return tokens;
	}

	private static IEnumerable<ContentBlock> Blocks(Message message) => message switch
	{
		UserMessage user => user.Content.Blocks ?? (user.Content.Text is { Length: > 0 } text
			? [new TextContent { Text = text }]
			: []),
		AssistantMessage assistant => assistant.Content,
		ToolResultMessage result => result.Content,
		CustomMessage custom => custom.Content.Blocks ?? (custom.Content.Text is { Length: > 0 } text
			? [new TextContent { Text = text }]
			: []),
		SystemMessage system => system.Content.Blocks ?? (system.Content.Text is { Length: > 0 } text
			? [new TextContent { Text = text }]
			: []),
		_ => [],
	};

	/// <summary>
	/// 旧会话或其它工具的 toolResult 可能带 NUL/ANSI（例如 `bash` 打印二进制文件）。
	/// 在发给 provider 前统一清洗，避免一条被污染的会话从此再也发不出去。
	/// </summary>
	private static Message SanitizeToolResult(Message message)
	{
		if (message is not ToolResultMessage result)
		{
			return message;
		}

		var content = SanitizeBlocks(result.Content);
		return ReferenceEquals(content, result.Content) ? message : result with { Content = content };
	}

	private static MessageContent SanitizeToolContent(MessageContent content)
	{
		if (content.IsText)
		{
			var text = content.Text ?? string.Empty;
			var clean = ShellTextSanitizer.Sanitize(text);
			return Same(clean, text) ? content : MessageContent.FromText(clean);
		}

		var blocks = SanitizeBlocks(content.Blocks ?? []);
		return ReferenceEquals(blocks, content.Blocks) ? content : MessageContent.FromBlocks(blocks);
	}

	private static IReadOnlyList<ContentBlock> SanitizeBlocks(IReadOnlyList<ContentBlock> blocks)
	{
		List<ContentBlock>? copy = null;
		for (var index = 0; index < blocks.Count; index++)
		{
			if (blocks[index] is not TextContent text)
			{
				continue;
			}

			var clean = ShellTextSanitizer.Sanitize(text.Text);
			if (Same(clean, text.Text))
			{
				continue;
			}

			copy ??= [.. blocks];
			copy[index] = text with { Text = clean };
		}

		return copy ?? blocks;
	}

	private static bool Same(string left, string right) =>
		ReferenceEquals(left, right) || string.Equals(left, right, StringComparison.Ordinal);

	private static UserMessage User(CustomMessage source, string text) => new()
	{
		Content = MessageContent.FromText(text),
		Timestamp = source.Timestamp,
	};

	private static UserMessage User(CustomMessage source, MessageContent content) => new()
	{
		Content = content,
		Timestamp = source.Timestamp,
	};

	private static string RawString(CustomMessage message, string name) =>
		message.Raw?[name]?.GetValueKind() == System.Text.Json.JsonValueKind.String
			? message.Raw[name]!.GetValue<string>()
			: string.Empty;

	public static CustomMessage BashExecution(
		string command,
		string output,
		int? exitCode,
		bool cancelled,
		bool truncated,
		string? fullOutputPath,
		bool excludeFromContext,
		long timestamp)
	{
		var raw = new JsonObject
		{
			["role"] = "bashExecution",
			["command"] = command,
			["output"] = output,
			["cancelled"] = cancelled,
			["truncated"] = truncated,
			["timestamp"] = timestamp,
		};

		if (exitCode is { } code)
		{
			raw["exitCode"] = code;
		}

		if (fullOutputPath is { Length: > 0 } path)
		{
			raw["fullOutputPath"] = path;
		}

		if (excludeFromContext)
		{
			raw["excludeFromContext"] = true;
		}

		return new CustomMessage
		{
			CustomRole = "bashExecution",
			Raw = raw,
			Timestamp = timestamp,
		};
	}
}
