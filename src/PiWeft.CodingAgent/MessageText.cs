using System.Text;
using PiWeft.Ai;

namespace PiWeft.CodingAgent;

/// 消息文本读取：回合判断与回报只关心「最后一条助手文本」，这里避免每次 agent_end 都走
/// LINQ 的 OfType/LastOrDefault（分配迭代器并从头扫到尾）。
internal static class MessageText
{
	/// 从尾部找最后一条助手消息；没有返回 null。
	public static AssistantMessage? LastAssistant(IReadOnlyList<Message> messages)
	{
		for (var index = messages.Count - 1; index >= 0; index--)
		{
			if (messages[index] is AssistantMessage assistant)
			{
				return assistant;
			}
		}

		return null;
	}

	/// 助手正文的总字符数：fork 估算历史大小时只关心长度，不必把正文拼出来。
	public static int TextLength(AssistantMessage message)
	{
		var total = 0;
		foreach (var block in message.Content)
		{
			if (block is TextContent text)
			{
				total += text.Text.Length;
			}
		}

		return total;
	}

	/// 助手消息里的正文块按段落拼接；思考、工具调用不算。
	public static string Of(AssistantMessage message)
	{
		if (message.Content.Count == 0)
		{
			return string.Empty;
		}

		var builder = new StringBuilder();
		foreach (var block in message.Content)
		{
			if (block is not TextContent text)
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
}
