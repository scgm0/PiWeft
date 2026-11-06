using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PiWeft.CodingAgent.Compatibility;

namespace PiWeft.CodingAgent;

/// 目标模式的状态。<see cref="Phase"/> 取 <c>active</c> / <c>paused</c> / <c>blocked</c> / <c>complete</c>。
public sealed record GoalState(
	bool Enabled,
	string Phase,
	string Objective,
	int RoundsStarted,
	int MaxRounds,
	string? BlockReason)
{
	public const string Active = "active";
	public const string Paused = "paused";
	public const string Blocked = "blocked";
	public const string Complete = "complete";

	public static GoalState Initial { get; } = new(false, Paused, string.Empty, 0, GoalMode.DefaultMaxRounds, null);

	/// 只有「启用 + active + 有目标」才续轮。
	public bool Running => Enabled && Phase == Active && Objective.Length > 0;
}

/// 目标模式：围绕一条 objective 自动续轮，直到 <c>GOAL_COMPLETE</c> / <c>GOAL_BLOCKED:</c> / 轮次上限。
/// 与 PiDeck 的 <c>pi-deck-goal-mode.ts</c> 同一套标记、文案与 widget 行格式。
public static partial class GoalMode
{
	public const string Marker = PiDeckProtocol.GoalModeMarker;

	/// 状态快照条目：跨重启只恢复为 paused，不自动续跑。
	public const string EntryType = PiDeckProtocol.GoalEntryType;

	/// 续轮指令的消息角色。不写进会话，只在本次运行里当 follow-up 交给模型。
	public const string ContinueRole = PiWeftProtocol.GoalContinueRole;

	public const string WidgetKey = PiDeckProtocol.GoalWidgetKey;

	public const int DefaultMaxRounds = 32;

	public static bool TryStripMarker(string text, out string objective)
	{
		if (!text.StartsWith(Marker, StringComparison.Ordinal))
		{
			objective = text;
			return false;
		}

		objective = text[Marker.Length..].TrimStart();
		return true;
	}

	/// 终态标记：`GOAL_COMPLETE`，或 `GOAL_BLOCKED: 原因`。
	public static (string Phase, string? Reason)? DetectTerminal(string text)
	{
		if (CompleteMarker().IsMatch(text))
		{
			return (GoalState.Complete, null);
		}

		var blocked = BlockedMarker().Match(text);
		if (blocked.Success)
		{
			var reason = blocked.Groups[1].Value.Trim();
			return (GoalState.Blocked, reason.Length > 0 ? reason : "blocked");
		}

		return null;
	}

	/// 系统提示里的静态规则与目标全文：一次运行内不变，前缀缓存不失效。
	public static string Rules(string objective) =>
		"Rules:\n" +
		"- Work toward the objective. Prefer concrete tool use over asking the user.\n" +
		"- If the objective is fully achieved, end with the exact token GOAL_COMPLETE.\n" +
		"- If you are blocked (missing info, failed approach, or need a user decision), end with GOAL_BLOCKED: <short reason>.\n" +
		"- Do not wait for extra confirmation once the next step is clear.\n\n" +
		$"Objective: {objective}";

	/// 每轮真正变化的只有轮次：单独一条隐藏消息，避免目标全文每轮重复进历史。
	public static string RoundLine(GoalState state) =>
		$"[GOAL MODE ACTIVE] Round: {state.RoundsStarted + 1}/{state.MaxRounds}";

	public static string ContinuationText(GoalState state) =>
		"Continue the current goal.\n" +
		$"Rounds used: {state.RoundsStarted}/{state.MaxRounds}\n" +
		"If done, write GOAL_COMPLETE. If blocked, write GOAL_BLOCKED: <reason>.";

	public static IReadOnlyList<string> WidgetLines(GoalState state)
	{
		if (state.Objective.Length == 0)
		{
			return [];
		}

		var phase = state.Phase switch
		{
			GoalState.Active => "进行中",
			GoalState.Blocked => "已阻塞",
			GoalState.Complete => "已完成",
			_ => "已暂停",
		};
		var lines = new List<string>(3)
		{
			$"目标{phase} {state.RoundsStarted}/{state.MaxRounds}",
			state.Objective,
		};
		if (state.BlockReason is { Length: > 0 } reason)
		{
			lines.Add(reason);
		}

		return lines;
	}

	public static JsonObject Encode(GoalState state)
	{
		var data = new JsonObject
		{
			["enabled"] = state.Enabled,
			["phase"] = state.Phase,
			["objective"] = state.Objective,
			["roundsStarted"] = state.RoundsStarted,
			["maxGoalRounds"] = state.MaxRounds,
		};
		if (state.BlockReason is { Length: > 0 } reason)
		{
			data["blockReason"] = reason;
		}

		return data;
	}

	/// 只恢复「暂停/阻塞/完成」的旧目标：重启后自动续跑等于绕过用户。
	public static GoalState Decode(JsonNode? data)
	{
		if (data is not JsonObject obj)
		{
			return GoalState.Initial;
		}

		var objective = obj["objective"] is JsonValue objectiveValue && objectiveValue.TryGetValue<string>(out var text)
			? text
			: string.Empty;
		var phase = obj["phase"] is JsonValue phaseValue && phaseValue.TryGetValue<string>(out var phaseText)
			? phaseText
			: GoalState.Paused;
		if (phase is not (GoalState.Active or GoalState.Paused or GoalState.Blocked or GoalState.Complete))
		{
			phase = GoalState.Paused;
		}

		var rounds = obj["roundsStarted"] is JsonValue roundsValue && roundsValue.TryGetValue<int>(out var started)
			? Math.Max(0, started)
			: 0;
		var max = obj["maxGoalRounds"] is JsonValue maxValue && maxValue.TryGetValue<int>(out var limit) && limit > 0
			? limit
			: DefaultMaxRounds;
		var reason = obj["blockReason"] is JsonValue reasonValue && reasonValue.TryGetValue<string>(out var reasonText)
			? reasonText
			: null;

		return new GoalState(
			Enabled: objective.Length > 0 && phase is GoalState.Paused or GoalState.Blocked or GoalState.Complete,
			Phase: phase == GoalState.Active ? GoalState.Paused : phase,
			Objective: objective,
			RoundsStarted: rounds,
			MaxRounds: max,
			BlockReason: reason);
	}

	[GeneratedRegex(@"\bGOAL_COMPLETE\b", RegexOptions.CultureInvariant)]
	private static partial Regex CompleteMarker();

	[GeneratedRegex(@"\bGOAL_BLOCKED\s*:\s*(.+)", RegexOptions.CultureInvariant)]
	private static partial Regex BlockedMarker();
}
