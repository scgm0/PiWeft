using System.Text;

namespace PiWeft.Ai;

/// <summary>
/// 各 provider 共用的会话记录辅助方法。provider 会依据回放状态重建开头的 system 消息，
/// 因此每次流式请求都只携带一条 system 消息。
/// </summary>
public static class Transcript
{
	/// <summary>
	/// 把 <see cref="Context.SystemPrompt"/> 与 <see cref="Context.Tools"/> 折叠进消息列表，
	/// 并把所有 system 消息合并成开头的一条。其余消息保持原有顺序。
	/// </summary>
	public static List<Message> Normalize(Context context)
	{
		ArgumentNullException.ThrowIfNull(context);

		var prompt = new StringBuilder();
		var sections = new Dictionary<string, string?>(StringComparer.Ordinal);
		IReadOnlyList<Tool>? tools = context.Tools;

		if (!string.IsNullOrEmpty(context.SystemPrompt))
		{
			prompt.Append(context.SystemPrompt);
		}

		var rest = new List<Message>(context.Messages.Count);
		foreach (var message in context.Messages)
		{
			if (message is SystemMessage system)
			{
				var text = GetText(system);
				if (text.Length > 0)
				{
					if (prompt.Length > 0)
					{
						prompt.Append("\n\n");
					}

					prompt.Append(text);
				}

				if (system.Sections is { Count: > 0 } systemSections)
				{
					foreach (var (name, value) in systemSections)
					{
						sections[name] = value;
					}
				}

				if (system.ToolsAdded is { Count: > 0 } added)
				{
					tools = added;
				}

				continue;
			}

			rest.Add(message);
		}

		var normalized = new List<Message>(rest.Count + 1);
		if (prompt.Length > 0 || sections.Count > 0 || (tools is { Count: > 0 }))
		{
			normalized.Add(new SystemMessage
			{
				Content = MessageContent.FromText(prompt.ToString()),
				Sections = sections.Count > 0 ? sections : null,
				ToolsAdded = tools is { Count: > 0 } ? tools : null,
				Timestamp = rest.Count > 0 ? rest[0].Timestamp : 0,
			});
		}

		normalized.AddRange(rest);
		return normalized;
	}

	/// <summary>system 消息渲染后的文本：先是内容，再按插入顺序拼接具名章节。</summary>
	public static string GetText(SystemMessage message)
	{
		if (message.Sections is not { Count: > 0 } sections)
		{
			return message.Text;
		}

		var builder = new StringBuilder(message.Text);
		foreach (var (_, value) in sections)
		{
			if (string.IsNullOrEmpty(value))
			{
				continue;
			}

			if (builder.Length > 0)
			{
				builder.Append("\n\n");
			}

			builder.Append(value);
		}

		return builder.ToString();
	}

	/// <summary>会话记录末尾生效的工具集合。</summary>
	public static IReadOnlyList<Tool> GetTools(IReadOnlyList<Message> messages)
	{
		IReadOnlyList<Tool> tools = [];
		foreach (var message in messages)
		{
			if (message is SystemMessage { ToolsAdded: { Count: > 0 } added })
			{
				tools = added;
			}
		}

		return tools;
	}

	/// <summary>第一条 system 消息的文本，没有则为空字符串。</summary>
	public static string GetInitialSystemText(IReadOnlyList<Message> messages)
	{
		foreach (var message in messages)
		{
			if (message is SystemMessage system)
			{
				return GetText(system);
			}
		}

		return string.Empty;
	}
}
