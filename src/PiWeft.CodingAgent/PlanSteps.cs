using System.Text.RegularExpressions;
using PiWeft.CodingAgent.Compatibility;

namespace PiWeft.CodingAgent;

/// 计划模式下的一条编号步骤。<see cref="Step"/> 从 1 开始，与模型写的 `[DONE:n]` 对应。
public sealed record PlanStep(int Step, string Text, bool Completed);

/// 计划模式的步骤跟踪：从回复里抽 `Plan:` / `计划：` 下的编号行，按 `[DONE:n]` 勾进度。
/// 与 PiDeck 的 <c>pi-deck-plan-mode.ts</c> 同一套解析与 widget 行格式。
public static partial class PlanSteps
{
	public const string WidgetKey = PiDeckProtocol.PlanWidgetKey;

	/// 标题前缀：桌面端据此渲染「计划草案已就绪」选单，而不是普通问题卡片。
	public const string NextMarker = PiDeckProtocol.PlanNextMarker;

	public const string ExecuteOption = "开始执行";

	public const string ContinueOption = "先不执行";

	public static IReadOnlyList<PlanStep> Extract(string message)
	{
		if (message.Length == 0)
		{
			return [];
		}

		var header = FindPlanHeader(message);
		if (header is null)
		{
			return [];
		}

		return ParseSteps(message.AsSpan(header.Index + header.Length));
	}

	/// 结构化提交的步骤（submit_plan 工具）：与文本解析共用同一套清洗与过滤规则。
	public static IReadOnlyList<PlanStep> Normalize(IEnumerable<string> lines)
	{
		var steps = new List<PlanStep>();
		foreach (var raw in lines)
		{
			var text = Clean(raw.AsSpan());
			if (text.Length > 3 && !text.StartsWith('/'))
			{
				steps.Add(new PlanStep(steps.Count + 1, text, Completed: false));
			}
		}

		return steps;
	}

	/// 选第一个落在代码块外的 `Plan:` / `计划：` 标题；文档示例里的格式提示不算计划。
	private static Match? FindPlanHeader(string message)
	{
		foreach (Match match in PlanHeader().Matches(message))
		{
			// 英文 `Plan:` 必须自占一行：正文里引用「按 Plan: 的格式」不是计划。
			// 中文「实际计划：」允许带前缀，沿用 PiDeck 的宽松匹配。
			var english = match.Value.Contains("Plan", StringComparison.OrdinalIgnoreCase);
			if (!InsideFence(message, match.Index) &&
				(!english || AtLineStart(message, match.Index)))
			{
				return match;
			}
		}

		return null;
	}

	private static bool AtLineStart(string message, int index)
	{
		var lineStart = message.LastIndexOf('\n', Math.Max(0, index - 1)) + 1;
		var prefix = message.AsSpan(lineStart, index - lineStart).Trim();
		if (prefix.IsEmpty)
		{
			return true;
		}

		// 允许 Markdown 标题/列表/引用前缀：`## Plan:`、`- Plan:` 仍是标题；
		// 「注意 Plan:」这类正文引用不通过。
		return prefix[0] is '#' or '-' or '*' or '>';
	}

	private static bool InsideFence(string message, int index)
	{
		var inFence = false;
		var start = 0;
		while (start < index)
		{
			var end = message.IndexOf('\n', start);
			if (end < 0 || end >= index)
			{
				break;
			}

			if (MarkdownScan.IsFenceLine(message.AsSpan(start, end - start)))
			{
				inFence = !inFence;
			}

			start = end + 1;
		}

		return inFence;
	}

	/// 标题之后的编号行。fenced code block 里的编号是示例，不是步骤。
	/// 行本身是原文切片，只有真正成为步骤的行才分配到字符串。
	private static List<PlanStep> ParseSteps(ReadOnlySpan<char> section)
	{
		var steps = new List<PlanStep>();
		var started = false;
		foreach (var line in MarkdownScan.Lines(section))
		{
			if (TryParseNumbered(line, out var raw))
			{
				started = true;
				var text = Clean(raw);
				// 与 PiDeck 一致：太短的碎片与斜杠命令不算步骤。
				if (text.Length > 3 && !text.StartsWith('/'))
				{
					steps.Add(new PlanStep(steps.Count + 1, text, Completed: false));
				}

				continue;
			}

			// 步骤段落之后出现正文（例如「已知风险：1. ...」）就收尾：
			// 系统提示要求步骤放在回复末尾，后续编号是说明，不是计划。
			if (started && !line.Trim().IsEmpty)
			{
				break;
			}
		}

		return steps;
	}

	/// 编号行：`1.` / `1)` / `1、` / `1:` 后至少一个空白。手工扫描避开每行正则。
	private static bool TryParseNumbered(ReadOnlySpan<char> line, out ReadOnlySpan<char> text)
	{
		text = default;
		var trimmed = line.TrimStart();
		var index = 0;
		while (index < trimmed.Length && char.IsAsciiDigit(trimmed[index]))
		{
			index++;
		}

		if (index == 0 || index >= trimmed.Length)
		{
			return false;
		}

		if (trimmed[index] is not ('.' or ')' or '、' or ':'))
		{
			return false;
		}

		index++;
		if (index >= trimmed.Length || !char.IsWhiteSpace(trimmed[index]))
		{
			return false;
		}

		while (index < trimmed.Length && char.IsWhiteSpace(trimmed[index]))
		{
			index++;
		}

		if (index >= trimmed.Length)
		{
			return false;
		}

		text = trimmed[index..];
		return true;
	}

	/// 把回复里出现的 `[DONE:n]` 标记成已完成；返回本次变化的条数。
	public static int MarkCompleted(string message, IList<PlanStep> steps)
	{
		var changed = 0;
		foreach (var line in MarkdownScan.Lines(message))
		{
			changed += MarkDoneInLine(line, steps);
		}

		return changed;
	}

	private static int MarkDoneInLine(ReadOnlySpan<char> line, IList<PlanStep> steps)
	{
		const string marker = "[DONE:";
		var changed = 0;
		var remaining = line;
		while (true)
		{
			var start = remaining.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
			if (start < 0)
			{
				break;
			}

			remaining = remaining[(start + marker.Length)..];
			var end = remaining.IndexOf(']');
			if (end <= 0)
			{
				break;
			}

			if (int.TryParse(remaining[..end], out var step))
			{
				for (var index = 0; index < steps.Count; index++)
				{
					if (steps[index].Step == step && !steps[index].Completed)
					{
						steps[index] = steps[index] with { Completed = true };
						changed++;
						break;
					}
				}
			}

			remaining = remaining[(end + 1)..];
		}

		return changed;
	}

	/// widget 行：首行是标题，其余 `☑/☐ {step}. {text}`。
	/// 计划卡/ widget 的首行摘要。
	public static string Summary(IReadOnlyList<PlanStep> steps, bool executing)
	{
		if (!executing)
		{
			return $"计划草案 {steps.Count} 步";
		}

		// 步骤不多，但摘要每次 widget 刷新都会算：索引循环，不生成委托与接口枚举器。
		var completed = 0;
		for (var index = 0; index < steps.Count; index++)
		{
			if (steps[index].Completed)
			{
				completed++;
			}
		}

		return $"计划进度 {completed}/{steps.Count}";
	}

	public static IReadOnlyList<string> WidgetLines(IReadOnlyList<PlanStep> steps, bool executing)
	{
		if (steps.Count == 0)
		{
			return [];
		}

		// 固定长度：widget 行数就是步骤数加标题，不需要 List 扩容。
		var lines = new string[steps.Count + 1];
		lines[0] = Summary(steps, executing);
		for (var index = 0; index < steps.Count; index++)
		{
			var step = steps[index];
			lines[index + 1] = $"{(step.Completed ? "☑" : "☐")} {step.Step}. {step.Text}";
		}

		return lines;
	}

	/// 模型可见的计划正文：每步之间空一行，与 PiDeck 的选单/执行消息一致。
	public static string ModelText(IReadOnlyList<PlanStep> steps) =>
		string.Join("\n\n", steps.Select(static step => $"{step.Step}. {step.Text}"));

	public static string NextTitle(IReadOnlyList<PlanStep> steps) =>
		$"{NextMarker} 计划草案已就绪（{steps.Count} 步），是否执行？\n" +
		"具体计划见上方计划条，可展开查看完整步骤\n\n" +
		ModelText(steps);

	/// 回复里有编号行但没有 `Plan:` 标题：多半是模型写了计划、却没按格式收尾。
	/// 用于计划模式给一次可见提示，不用于解析。代码块里的编号不算。
	public static bool HasNumberedLines(string message)
	{
		foreach (var line in MarkdownScan.Lines(message))
		{
			if (TryParseNumbered(line, out _))
			{
				return true;
			}
		}

		return false;
	}

	/// 回复里是否有可用的 `Plan:` 标题（代码块里的示例不算）。
	public static bool HasPlanHeading(string message) => FindPlanHeader(message) is not null;

	/// 计划评审选单的标题拆成「一行问题」与「计划正文」。
	/// 选单把计划全文接在首行之后；桌面卡片把正文放进可滚动区，标题才不会被省略号截断。
	public static (string Title, string? Detail) SplitReview(string title)
	{
		var display = UiSelectText.DisplayTitle(title);
		var newline = display.IndexOf('\n');
		if (newline < 0)
		{
			return (display, null);
		}

		var detail = display[(newline + 1)..].Trim();
		return (display[..newline].TrimEnd(), detail.Length > 0 ? detail : null);
	}

	public static string ExecuteMessage(IReadOnlyList<PlanStep> steps) =>
		$"Execute the approved plan.\n\n{ModelText(steps)}\n\nAfter completing a step, include [DONE:n].";

	public static string StepsMessage(IReadOnlyList<PlanStep> steps) =>
		$"**Plan Steps ({steps.Count})**\n\n{ModelText(steps)}";

	public static string CompleteMessage(IReadOnlyList<PlanStep> steps) =>
		$"**计划已完成** ✓\n\n{string.Join('\n', steps.Select(static step => $"- {step.Text}"))}";

	private static string Clean(ReadOnlySpan<char> text)
	{
		var source = text.ToString();
		var cleaned = InlineCode().Replace(BoldMarkers().Replace(source, "$1"), "$1");
		cleaned = Whitespace().Replace(cleaned, " ").Trim();
		if (cleaned.Length == 0)
		{
			return cleaned;
		}

		// 只重写首字母，不再多分配一个去掉首字符的子串。
		return string.Create(cleaned.Length, cleaned, static (span, value) =>
		{
			span[0] = char.ToUpperInvariant(value[0]);
			value.AsSpan(1).CopyTo(span[1..]);
		});
	}

	[GeneratedRegex(@"(?:\*{0,2}Plan:\*{0,2}|计划[:：])\s*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
	private static partial Regex PlanHeader();

	[GeneratedRegex(@"\*{1,2}([^*]+)\*{1,2}", RegexOptions.CultureInvariant)]
	private static partial Regex BoldMarkers();

	[GeneratedRegex(@"`([^`]+)`", RegexOptions.CultureInvariant)]
	private static partial Regex InlineCode();

	[GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
	private static partial Regex Whitespace();
}
