using System.Text;
using System.Text.Json.Nodes;
using PiWeft.Ai;

using PiWeft.Ai.Json;
namespace PiWeft.CodingAgent.Compaction;

/// <summary>
/// 把消息渲染成文本供摘要请求使用，避免模型把输入当成需要继续的对话。
/// 对应 pi 的 <c>compaction/utils.ts</c>。
/// </summary>
public static class ConversationSerializer
{
	/// <summary>工具结果在摘要前会先截断，因为不需要完整文本。</summary>
	public const int ToolResultMaxChars = 2000;

	/// <summary>被摘要区间内涉及的文件路径，用于压缩细节。</summary>
	public sealed record FileOperations
	{
		public HashSet<string> ReadFiles { get; } = new(StringComparer.Ordinal);

		public HashSet<string> ModifiedFiles { get; } = new(StringComparer.Ordinal);
	}

	public static string Serialize(IEnumerable<Message> messages)
	{
		var parts = new List<string>();

		foreach (var message in messages)
		{
			switch (message)
			{
				case UserMessage user:
				{
					var content = ContentText(user.Content);
					if (content.Length > 0)
					{
						parts.Add($"[User]: {content}");
					}

					break;
				}

				case AssistantMessage assistant:
				{
					var calls = new List<string>();
					foreach (var block in assistant.Content)
					{
						if (block is ToolCall call)
						{
							var arguments = string.Join(
								", ",
								call.Arguments.Select(pair => $"{pair.Key}={pair.Value?.ToJsonString(PiJson.NodeOptions) ?? "null"}"));
							calls.Add($"{call.Name}({arguments})");
						}
					}

					var text = ContentText(assistant.Content);
					if (text.Length > 0)
					{
						parts.Add($"[Assistant]: {text}");
					}

					if (calls.Count > 0)
					{
						parts.Add($"[Assistant tool calls]: {string.Join("; ", calls)}");
					}

					break;
				}

				case ToolResultMessage result:
				{
					var content = ContentText(result.Content);
					if (content.Length > 0)
					{
						parts.Add($"[Tool result]: {Truncate(content, ToolResultMaxChars)}");
					}

					break;
				}
			}
		}

		return string.Join("\n\n", parts);
	}

	/// <summary>从工具调用中收集一段消息读写过的文件。</summary>
	public static FileOperations CollectFileOperations(IEnumerable<Message> messages)
	{
		var operations = new FileOperations();
		foreach (var message in messages)
		{
			if (message is not AssistantMessage assistant)
			{
				continue;
			}

			foreach (var call in assistant.Content.OfType<ToolCall>())
			{
				var path = StringArgument(call.Arguments, "path");
				if (path.Length == 0)
				{
					continue;
				}

				switch (call.Name)
				{
					case "read":
					case "grep":
					case "find":
					case "ls":
						operations.ReadFiles.Add(path);
						break;
					case "write":
						operations.ModifiedFiles.Add(path);
						break;
					case "edit":
						operations.ReadFiles.Add(path);
						operations.ModifiedFiles.Add(path);
						break;
				}
			}
		}

		return operations;
	}

	/// <summary>渲染追加在压缩摘要后的文件列表。</summary>
	public static string FormatFileOperations(IReadOnlyCollection<string> readFiles, IReadOnlyCollection<string> modifiedFiles)
	{
		if (readFiles.Count == 0 && modifiedFiles.Count == 0)
		{
			return string.Empty;
		}

		var builder = new StringBuilder();
		if (readFiles.Count > 0)
		{
			builder.Append("<read-files>\n");
			foreach (var file in readFiles.OrderBy(static f => f, StringComparer.Ordinal))
			{
				builder.Append(file).Append('\n');
			}

			builder.Append("</read-files>\n");
		}

		if (modifiedFiles.Count > 0)
		{
			builder.Append("<modified-files>\n");
			foreach (var file in modifiedFiles.OrderBy(static f => f, StringComparer.Ordinal))
			{
				builder.Append(file).Append('\n');
			}

			builder.Append("</modified-files>\n");
		}

		return builder.ToString().TrimEnd('\n');
	}

	internal static string ContentText(MessageContent content)
	{
		if (content.IsText)
		{
			return content.Text ?? string.Empty;
		}

		return ContentText(content.Blocks ?? []);
	}

	internal static string ContentText(IReadOnlyList<ContentBlock> blocks)
	{
		var builder = new StringBuilder();
		foreach (var block in blocks)
		{
			if (block is not TextContent text || text.Text.Length == 0)
			{
				continue;
			}

			if (builder.Length > 0)
			{
				builder.Append('\n');
			}

			builder.Append(text.Text);
		}

		return builder.ToString();
	}

	private static string Truncate(string text, int maxChars)
	{
		if (text.Length <= maxChars)
		{
			return text;
		}

		return $"{text[..maxChars]}\n\n[... {text.Length - maxChars} more characters truncated]";
	}

	private static string StringArgument(JsonObject arguments, string name) =>
		arguments[name]?.GetValueKind() == System.Text.Json.JsonValueKind.String
			? arguments[name]!.GetValue<string>()
			: string.Empty;
}
