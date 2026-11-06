using System.Text;
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

		var header = PlanHeader().Match(message);
		if (!header.Success)
		{
			return [];
		}

		var steps = new List<PlanStep>();
		var section = message[(header.Index + header.Length)..];
		foreach (Match match in NumberedLine().Matches(section))
		{
			var text = Clean(match.Groups[2].Value);
			// 与 PiDeck 一致：太短的碎片与斜杠命令不算步骤。
			if (text.Length > 3 && !text.StartsWith('/'))
			{
				steps.Add(new PlanStep(steps.Count + 1, text, Completed: false));
			}
		}

		return steps;
	}

	/// 把回复里出现的 `[DONE:n]` 标记成已完成；返回本次变化的条数。
	public static int MarkCompleted(string message, IList<PlanStep> steps)
	{
		var changed = 0;
		foreach (Match match in DoneMarker().Matches(message))
		{
			if (!int.TryParse(match.Groups[1].Value, out var step))
			{
				continue;
			}

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

		return changed;
	}

	/// widget 行：首行是标题，其余 `☑/☐ {step}. {text}`。
	public static IReadOnlyList<string> WidgetLines(IReadOnlyList<PlanStep> steps, bool executing)
	{
		if (steps.Count == 0)
		{
			return [];
		}

		var completed = steps.Count(static step => step.Completed);
		var lines = new List<string>(steps.Count + 1)
		{
			executing ? $"计划进度 {completed}/{steps.Count}" : $"计划草案 {steps.Count} 步",
		};

		foreach (var step in steps)
		{
			lines.Add($"{(step.Completed ? "☑" : "☐")} {step.Step}. {step.Text}");
		}

		return lines;
	}

	/// 模型可见的计划正文：每步之间空一行，与 PiDeck 的选单/执行消息一致。
	public static string ModelText(IReadOnlyList<PlanStep> steps) =>
		string.Join("\n\n", steps.Select(static step => $"{step.Step}. {step.Text}"));

	public static string NextTitle(IReadOnlyList<PlanStep> steps) =>
		$"{NextMarker} 计划草案已就绪（{steps.Count} 步），是否执行？\n" +
		"具体计划可点击下方待办查看详细列表\n\n" +
		ModelText(steps);

	public static string ExecuteMessage(IReadOnlyList<PlanStep> steps) =>
		$"Execute the approved plan.\n\n{ModelText(steps)}\n\nAfter completing a step, include [DONE:n].";

	public static string StepsMessage(IReadOnlyList<PlanStep> steps) =>
		$"**Plan Steps ({steps.Count})**\n\n{ModelText(steps)}";

	public static string CompleteMessage(IReadOnlyList<PlanStep> steps) =>
		$"**PiDeck Plan Complete** ✓\n\n{string.Join('\n', steps.Select(static step => $"- {step.Text}"))}";

	private static string Clean(string text)
	{
		var builder = new StringBuilder(text.Length);
		builder.Append(BoldMarkers().Replace(text, "$1"));
		var cleaned = InlineCode().Replace(builder.ToString(), "$1");
		cleaned = Whitespace().Replace(cleaned, " ").Trim();
		return cleaned.Length > 0 ? char.ToUpperInvariant(cleaned[0]) + cleaned[1..] : cleaned;
	}

	[GeneratedRegex(@"(?:\*{0,2}Plan:\*{0,2}|计划[:：])\s*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
	private static partial Regex PlanHeader();

	[GeneratedRegex(@"^\s*(\d+)[.)、:]\s+(.+)$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
	private static partial Regex NumberedLine();

	[GeneratedRegex(@"\[DONE:(\d+)\]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
	private static partial Regex DoneMarker();

	[GeneratedRegex(@"\*{1,2}([^*]+)\*{1,2}", RegexOptions.CultureInvariant)]
	private static partial Regex BoldMarkers();

	[GeneratedRegex(@"`([^`]+)`", RegexOptions.CultureInvariant)]
	private static partial Regex InlineCode();

	[GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
	private static partial Regex Whitespace();
}
