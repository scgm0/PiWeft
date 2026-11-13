using System.Text;
using System.Text.Json.Nodes;
using PiWeft.Ai;

using PiWeft.Ai.Json;
namespace PiWeft.CodingAgent.Compaction;

/// 把消息渲染成文本供摘要请求使用，避免模型把输入当成需要继续的对话。
/// 对应 pi 的 <c>compaction/utils.ts</c>。
public static class ConversationSerializer
{
	public const int ToolResultMaxChars = 2000;

	public sealed record FileOperations
	{
		public HashSet<string> ReadFiles { get; } = new(StringComparer.Ordinal);

		public HashSet<string> ModifiedFiles { get; } = new(StringComparer.Ordinal);
	}

	public static string Serialize(IEnumerable<Message> messages)
	{
		var builder = new StringBuilder();
		var first = true;

		void Separate()
		{
			if (!first)
			{
				builder.Append("\n\n");
			}

			first = false;
		}

		foreach (var message in messages)
		{
			switch (message)
			{
				case UserMessage user:
				{
					var content = ContentText(user.Content);
					if (content.Length > 0)
					{
						Separate();
						builder.Append("[User]: ").Append(content);
					}

					break;
				}

				case AssistantMessage assistant:
				{
					var text = ContentText(assistant.Content);
					if (text.Length > 0)
					{
						Separate();
						builder.Append("[Assistant]: ").Append(text);
					}

					var firstCall = true;
					foreach (var block in assistant.Content)
					{
						if (block is not ToolCall call)
						{
							continue;
						}

						if (firstCall)
						{
							Separate();
							builder.Append("[Assistant tool calls]: ");
							firstCall = false;
						}
						else
						{
							builder.Append("; ");
						}

						builder.Append(call.Name).Append('(');
						var firstArgument = true;
						foreach (var (key, value) in call.Arguments)
						{
							if (!firstArgument)
							{
								builder.Append(", ");
							}

							firstArgument = false;
							builder.Append(key).Append('=')
								.Append(value?.ToJsonString(PiJson.NodeOptions) ?? "null");
						}

						builder.Append(')');
					}

					break;
				}

				case ToolResultMessage result:
				{
					var content = ContentText(result.Content);
					if (content.Length > 0)
					{
						Separate();
						builder.Append("[Tool result]: ").Append(Truncate(content, ToolResultMaxChars));
					}

					break;
				}
			}
		}

		return builder.ToString();
	}

	public static FileOperations CollectFileOperations(IEnumerable<Message> messages)
	{
		var operations = new FileOperations();
		foreach (var message in messages)
		{
			if (message is not AssistantMessage assistant)
			{
				continue;
			}

			foreach (var block in assistant.Content)
			{
				if (block is not ToolCall call)
				{
					continue;
				}

				var path = StringArgument(call.Arguments, "path");
				if (path.Length == 0)
				{
					continue;
				}

				// 与 pi 的 addFileOp 一致：只有 read 算读，write/edit 算改。
				// grep/find/ls 的 path 可能是目录，不当成读过的文件写进摘要。
				switch (call.Name)
				{
					case "read":
						operations.ReadFiles.Add(path);
						break;
					case "write":
					case "edit":
						operations.ModifiedFiles.Add(path);
						break;
				}
			}
		}

		return operations;
	}

	/// <summary>算最终文件清单：改过的文件不再列在只读清单里。</summary>
	public static (IReadOnlyList<string> ReadFiles, IReadOnlyList<string> ModifiedFiles) ComputeFileLists(
		FileOperations operations)
	{
		var modified = operations.ModifiedFiles.OrderBy(static f => f, StringComparer.Ordinal).ToArray();
		var read = operations.ReadFiles
			.Where(path => !operations.ModifiedFiles.Contains(path))
			.OrderBy(static f => f, StringComparer.Ordinal)
			.ToArray();
		return (read, modified);
	}

	/// 渲染成 pi 的 `<read-files>` / `<modified-files>` 片段，前面带两个换行便于直接接在摘要后。
	public static string FormatFileOperations(IReadOnlyCollection<string> readFiles, IReadOnlyCollection<string> modifiedFiles)
	{
		var modified = new HashSet<string>(modifiedFiles, StringComparer.Ordinal);
		var readOnly = readFiles.Where(path => !modified.Contains(path)).ToArray();
		if (readOnly.Length == 0 && modified.Count == 0)
		{
			return string.Empty;
		}

		var sections = new List<string>(2);
		if (readOnly.Length > 0)
		{
			var builder = new StringBuilder("<read-files>\n");
			foreach (var file in readOnly.OrderBy(static f => f, StringComparer.Ordinal))
			{
				builder.Append(file).Append('\n');
			}

			sections.Add(builder.Append("</read-files>").ToString());
		}

		if (modified.Count > 0)
		{
			var builder = new StringBuilder("<modified-files>\n");
			foreach (var file in modified.OrderBy(static f => f, StringComparer.Ordinal))
			{
				builder.Append(file).Append('\n');
			}

			sections.Add(builder.Append("</modified-files>").ToString());
		}

		return "\n\n" + string.Join("\n\n", sections);
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

		return $"{PiWeft.CodingAgent.Tools.Truncate.SliceHead(text, maxChars)}\n\n[... {text.Length - maxChars} more characters truncated]";
	}

	private static string StringArgument(JsonObject arguments, string name) =>
		arguments[name]?.GetValueKind() == System.Text.Json.JsonValueKind.String
			? arguments[name]!.GetValue<string>()
			: string.Empty;
}
