using System.Text;

namespace PiWeft.Ai;

/// 各 provider 共用的会话记录辅助方法。provider 会依据回放状态重建开头的 system 消息，
/// 因此每次流式请求都只携带一条 system 消息。
public static class Transcript
{
	/// 把 <see cref="Context.SystemPrompt"/> 与 <see cref="Context.Tools"/> 折叠进消息列表，
	/// 并把所有 system 消息合并成开头的一条。其余消息保持原有顺序。
	public static List<Message> Normalize(Context context, Model? model = null)
	{
		ArgumentNullException.ThrowIfNull(context);

		var source = model is null ? context.Messages : MessageTransform.Transform(context.Messages, model);
		var prompt = new StringBuilder();
		var sections = new Dictionary<string, string?>(StringComparer.Ordinal);
		IReadOnlyList<Tool>? tools = context.Tools;

		if (!string.IsNullOrEmpty(context.SystemPrompt))
		{
			prompt.Append(context.SystemPrompt);
		}

		var rest = new List<Message>(source.Count);
		foreach (var message in source)
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
						// null 表示删除章节，不能留成键值都存在的 null。
						if (value is null)
						{
							sections.Remove(name);
						}
						else
						{
							sections[name] = value;
						}
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

	/// <summary>合并所有 system 消息得到当前的工具集：后出现的 ToolsRemoved 删除后出现的 ToolsAdded 添加。</summary>
	public static IReadOnlyList<Tool> GetTools(IReadOnlyList<Message> messages)
	{
		var tools = new Dictionary<string, Tool>(StringComparer.Ordinal);
		foreach (var message in messages)
		{
			if (message is not SystemMessage system)
			{
				continue;
			}

			if (system.ToolsRemoved is { Count: > 0 } removed)
			{
				foreach (var tool in removed)
				{
					tools.Remove(tool.Name);
				}
			}

			if (system.ToolsAdded is { Count: > 0 } added)
			{
				foreach (var tool in added)
				{
					tools[tool.Name] = tool;
				}
			}
		}

		return [.. tools.Values];
	}

	/// 把所有 system 消息重放成一条：正文按顺序拼接，sections 按名字覆盖（null 删除），
	/// 工具按增删顺序合并。压缩的检查点用它保存当时的完整提示词状态。
	public static SystemMessage? GetCurrentSystemMessage(IReadOnlyList<Message> messages)
	{
		var content = new List<string>();
		var sections = new Dictionary<string, string?>(StringComparer.Ordinal);
		long? timestamp = null;
		var found = false;
		foreach (var message in messages)
		{
			if (message is not SystemMessage system)
			{
				continue;
			}

			found = true;
			timestamp ??= system.Timestamp;
			var text = GetText(system);
			if (text.Length > 0)
			{
				content.Add(text);
			}

			if (system.Sections is { Count: > 0 } systemSections)
			{
				foreach (var (name, value) in systemSections)
				{
					if (value is null)
					{
						sections.Remove(name);
					}
					else
					{
						sections[name] = value;
					}
				}
			}
		}

		var tools = GetTools(messages);
		if (!found && tools.Count == 0)
		{
			return null;
		}

		return new SystemMessage
		{
			Content = MessageContent.FromText(string.Join("\n\n", content)),
			Sections = sections.Count > 0 ? sections : null,
			ToolsAdded = tools.Count > 0 ? tools : null,
			Timestamp = timestamp ?? 0,
		};
	}

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
