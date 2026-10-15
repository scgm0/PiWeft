using System.Text.Json.Nodes;
using PiWeft.Ai;
using PiWeft.CodingAgent.Settings;

using PiWeft.Ai.Json;
namespace PiWeft.CodingAgent.Compaction;

/// <summary>上下文窗口核算。对应 pi 在 <c>compaction.ts</c> 里的令牌辅助方法。</summary>
public static class ContextTokens
{
	/// <summary>单张图片计入的字符数 —— 像素数据本身不直接计数。</summary>
	public const long EstimatedImageChars = 4800;

	public static long CalculateContextTokens(Usage usage) =>
		usage.TotalTokens != 0
			? usage.TotalTokens
			: usage.Input + usage.Output + usage.CacheRead + usage.CacheWrite;

	/// <summary>该用量能否作为基准：未中断、未失败，且不是全零。</summary>
	private static bool IsUsableUsage(AssistantMessage message) =>
		message.StopReason is not (StopReason.Aborted or StopReason.Error) &&
		CalculateContextTokens(message.Usage) > 0;

	/// <summary>最后一条给出可用用量的助手消息的下标与用量。</summary>
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

	/// <summary>单条消息在上下文窗口中占用的估算令牌数。</summary>
	public static long EstimateTokens(Message message)
	{
		long chars = message switch
		{
			SystemMessage system => SystemChars(system),
			UserMessage user => ContentChars(user.Content),
			AssistantMessage assistant => AssistantChars(assistant),
			ToolResultMessage result => result.Content.Sum(BlockChars),
			CustomMessage custom => custom.Raw is { } raw ? RawChars(raw) : ContentChars(custom.Content),
			_ => 0,
		};

		return (chars + 3) / 4; // 即 ceil(chars / 4)
	}

	/// <summary>
	/// 估算的上下文大小：以最后一次可用用量为基准，其后的内容按估算累加 —— pi 也这么做，
	/// 以免很长的工具结果被重复计算。
	/// </summary>
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

	/// <summary>上下文已接近窗口上限、应当触发压缩时为 true。</summary>
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
				_ => 0,
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

		return (content.Blocks ?? []).Sum(BlockChars);
	}

	private static long BlockChars(ContentBlock block) => block switch
	{
		TextContent text => text.Text.Length,
		ImageContent => EstimatedImageChars,
		ThinkingContent thinking => thinking.Thinking.Length,
		_ => 0,
	};

	/// <summary>自定义消息的字符数，从其保留的 JSON 中读取：bash 输出或摘要。</summary>
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
