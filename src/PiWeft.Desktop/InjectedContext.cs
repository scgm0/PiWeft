using PiWeft.CodingAgent;
using PiWeft.CodingAgent.Compatibility;
using PiWeft.CodingAgent.Subagents;

namespace PiWeft.Desktop;

/// 由扩展或原生能力注入的 user 消息：模型看到它，用户没写它。
/// 认这些类型是为了给它们一条独立的行样式，不冒充用户气泡。
internal static class InjectedContext
{
	/// 类型对应的基础标题；不是注入的消息返回 null。
	/// <paramref name="text"/> 只用于旧会话：修 customType 落盘之前写出的回报没有类型标记，
	/// 按我们自己的正文前缀认回来（只管显示，不改文件也不影响分叉与重试）。
	public static string? Label(string? customType, string text) =>
		customType is not null ? LabelFor(customType) : LegacyLabel(text);

	private static string? LabelFor(string customType) => customType switch
	{
		SubagentHub.ResultMessageType => Strings.ReportSubagentResult,
		SubagentHub.MessageType => Strings.ReportParentMessage,
		PiWeftProtocol.LinkedReportMessageType => Strings.ReportLinkedChild,
		PiWeftProtocol.LinkedMessageType => Strings.ReportLinkedMessage,
		_ => null,
	};

	private static string? LegacyLabel(string text) =>
		text.StartsWith("子代理「", StringComparison.Ordinal) ? Strings.ReportSubagentResult
		: text.StartsWith("子会话「", StringComparison.Ordinal) ? Strings.ReportLinkedChild
		: text.StartsWith("来自父会话「", StringComparison.Ordinal) ? Strings.ReportLinkedMessage
		: text.StartsWith("来自子会话「", StringComparison.Ordinal) ? Strings.ReportLinkedMessage
		: text.StartsWith("在这里做：", StringComparison.Ordinal) ? Strings.ReportLinkedMessage
		: null;

	/// 首行预览：跳过「子代理「x」完成了一轮。session_id: …」这层信封，取第一句正文。
	public static string Preview(string text, int max)
	{
		var remaining = text.AsSpan();
		while (!remaining.IsEmpty)
		{
			var newline = remaining.IndexOf('\n');
			var line = (newline < 0 ? remaining : remaining[..newline]).Trim();
			remaining = newline < 0 ? default : remaining[(newline + 1)..];
			// 跳过信封：回报首行是「子代理「x」完成了一轮。session_id: …」，
			// 它已经在标题里出现过，预览要给人看的是正文第一句。
			if (line.IsEmpty ||
				line.StartsWith("子代理「", StringComparison.Ordinal) ||
				line.StartsWith("子会话「", StringComparison.Ordinal) ||
				line.StartsWith("来自父会话「", StringComparison.Ordinal) ||
				line.StartsWith("来自子会话「", StringComparison.Ordinal) ||
				line.StartsWith("session_id", StringComparison.Ordinal))
			{
				continue;
			}

			var value = line.ToString();
			return value.Length <= max ? value : string.Concat(value.AsSpan(0, max), "…");
		}

		return string.Empty;
	}

	/// 正文以「子代理「scout-1」…」开头时把名字提到标题上，读起来不必先看正文。
	public static string Title(string label, string text)
	{
		var open = text.IndexOf('「');
		var close = open >= 0 ? text.IndexOf('」', open + 1) : -1;
		// 只认开头的「（子代理「…」「子会话「…」「来自父会话「…」）：正文里引用的括号
		// 不该把标题改成那句引用。
		if (open < 0 || open > 5 || close <= open + 1 || close - open > 40)
		{
			return label;
		}

		return string.Concat(label, "「", text.AsSpan(open + 1, close - open - 1), "」");
	}
}
