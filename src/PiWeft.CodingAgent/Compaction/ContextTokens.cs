using System.Text.Json.Nodes;
using PiWeft.Ai;
using PiWeft.CodingAgent.Settings;

using PiWeft.Ai.Json;
namespace PiWeft.CodingAgent.Compaction;

public static class ContextTokens
{
	public const long EstimatedImageChars = 4800;

	public static long CalculateContextTokens(Usage usage) =>
		usage.TotalTokens != 0
			? usage.TotalTokens
			: usage.Input + usage.Output + usage.CacheRead + usage.CacheWrite;

	private static bool IsUsableUsage(AssistantMessage message) =>
		message.StopReason is not (StopReason.Aborted or StopReason.Error) &&
		CalculateContextTokens(message.Usage) > 0;

	public static (int Index, Usage Usage)? LastAssistantUsage(IReadOnlyList<Message> messages)
	{
		for (var i = messages.Count - 1; i >= 0; i--)
		{
			if (messages[i] is AssistantMessage assistant && IsUsableUsage(assistant))
			{
				return (i, assistant.Usage);
			}
		}

		return null;
	}

	public static long EstimateTokens(Message message)
	{
		long chars = message switch
		{
			SystemMessage system => SystemChars(system),
			UserMessage user => ContentChars(user.Content),
			AssistantMessage assistant => AssistantChars(assistant),
			ToolResultMessage result => BlocksChars(result.Content),
			CustomMessage custom => custom.Raw is { } raw ? RawChars(raw) : ContentChars(custom.Content),
			_ => 0,
		};

		return (chars + 3) / 4; // 即 ceil(chars / 4)
	}

	/// 估算的上下文大小：以最后一次可用用量为基准，其后的内容按估算累加；pi 也这么做，
	/// 以免很长的工具结果被重复计算。
	public static (long Tokens, int? LastUsageIndex) EstimateContextTokens(IReadOnlyList<Message> messages)
	{
		var last = LastAssistantUsage(messages);
		if (last is null)
		{
			long total = 0;
			foreach (var message in messages)
			{
				total += EstimateTokens(message);
			}

			return (total, null);
		}

		long tokens = CalculateContextTokens(last.Value.Usage);
		for (var i = last.Value.Index + 1; i < messages.Count; i++)
		{
			tokens += EstimateTokens(messages[i]);
		}

		return (tokens, last.Value.Index);
	}

	public static bool ShouldCompact(long contextTokens, int contextWindow, CompactionSettings settings) =>
		settings.Enabled && contextTokens > contextWindow - settings.ReserveTokens;

	private static long SystemChars(SystemMessage system)
	{
		long chars = ContentChars(system.Content);
		if (system.Sections is { Count: > 0 } sections)
		{
			foreach (var section in sections.Values)
			{
				chars += section?.Length ?? 0;
			}
		}

		if (system.ToolsAdded is { Count: > 0 } tools)
		{
			chars += ToolsChars(tools);
		}

		return chars;
	}

	private static long ToolsChars(IReadOnlyList<Tool> tools)
	{
		long chars = 0;
		foreach (var tool in tools)
		{
			chars += tool.Name.Length + tool.Description.Length + tool.Parameters.ToJsonString(PiJson.NodeOptions).Length;
		}

		return chars;
	}

	private static long AssistantChars(AssistantMessage assistant)
	{
		long chars = 0;
		foreach (var block in assistant.Content)
		{
			chars += block switch
			{
				TextContent text => text.Text.Length,
				ThinkingContent thinking => thinking.Thinking.Length,
				ToolCall call => call.Name.Length + call.Arguments.ToJsonString(PiJson.NodeOptions).Length,
				ImageContent => 0,
			};
		}

		return chars;
	}

	private static long ContentChars(MessageContent content)
	{
		if (content.IsText)
		{
			return content.Text?.Length ?? 0;
		}

		return BlocksChars(content.Blocks ?? []);
	}

	private static long BlocksChars(IReadOnlyList<ContentBlock> blocks)
	{
		long chars = 0;
		foreach (var block in blocks)
		{
			chars += block switch
			{
				TextContent text => text.Text.Length,
				ImageContent => EstimatedImageChars,
				ThinkingContent thinking => thinking.Thinking.Length,
				ToolCall => 0,
			};
		}

		return chars;
	}

	private static long RawChars(JsonObject raw)
	{
		long chars = 0;
		chars += StringLength(raw, "output");
		chars += StringLength(raw, "command");
		chars += StringLength(raw, "summary");

		if (chars == 0 && raw["content"] is { } content)
		{
			chars += content.GetValueKind() == System.Text.Json.JsonValueKind.String
				? content.GetValue<string>().Length
				: content.ToJsonString(PiJson.NodeOptions).Length;
		}

		return chars;
	}

	private static long StringLength(JsonObject raw, string name) =>
		raw[name]?.GetValueKind() == System.Text.Json.JsonValueKind.String
			? raw[name]!.GetValue<string>().Length
			: 0;
}
