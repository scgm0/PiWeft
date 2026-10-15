using System.Text;
using System.Text.Json.Nodes;
using PiWeft.Ai;

using PiWeft.Ai.Json;
namespace PiWeft.CodingAgent;

/// <summary>
/// 编码代理特有的消息，以及把它们转换成面向 LLM 的消息的转换器。
/// 对应 pi 的 <c>core/messages.ts</c>。
/// </summary>
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

	/// <summary>不会直接发给模型、因此必须转换的消息角色。</summary>
	public static bool IsConvertibleRole(string role) =>
		role is "bashExecution" or "custom" or "branchSummary" or "compactionSummary";

	/// <summary>
	/// 把代理的会话记录转换成 provider 能理解的消息：纯状态消息被过滤掉，
	/// 自定义角色变成 user 消息 —— 与 pi 的 <c>convertToLlm</c> 完全一致。
	/// </summary>
	public static IReadOnlyList<Message> ConvertToLlm(IReadOnlyList<Message> messages, CancellationToken cancellationToken)
	{
		var converted = new List<Message>(messages.Count);
		foreach (var message in messages)
		{
			if (message is not CustomMessage custom)
			{
				converted.Add(message);
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
						BranchSummaryPrefix + RawString(custom, "summary") + BranchSummarySuffix));
					break;

				case "compactionSummary":
					converted.Add(User(
						custom,
						CompactionSummaryPrefix + RawString(custom, "summary") + CompactionSummarySuffix));
					break;

				default:
					converted.Add(custom.Content.IsText && custom.Content.Text is { Length: 0 }
						? custom with { Content = MessageContent.Empty }
						: User(custom, custom.Content));
					break;
			}
		}

		return converted;
	}

	/// <summary>按 pi 呈现给模型的方式渲染 <c>bashExecution</c> 消息。</summary>
	public static string BashExecutionToText(CustomMessage message)
	{
		var raw = message.Raw;
		var builder = new StringBuilder();
		builder.Append("Ran `").Append(RawString(message, "command")).Append("`\n");

		var output = RawString(message, "output");
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

	/// <summary>
	/// 粗略的上下文大小估算（每四个字符一个令牌，外加每条消息的小额开销）。pi 的估算器更精细，
	/// 这里的实现只用于压缩阈值判断。
	/// </summary>
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
					TextContent text => text.Text.Length / 4,
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

	/// <summary>为通过 <c>!</c> 执行的 shell 命令创建 <c>bashExecution</c> 消息。</summary>
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
