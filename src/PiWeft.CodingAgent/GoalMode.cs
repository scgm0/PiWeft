using System.Text.Json.Nodes;
using PiWeft.CodingAgent.Compatibility;

namespace PiWeft.CodingAgent;

/// 目标模式的状态。<see cref="Phase"/> 取 <c>active</c> / <c>paused</c> / <c>blocked</c> / <c>complete</c>。
public sealed record GoalState(
	bool Enabled,
	string Phase,
	string Objective,
	int RoundsStarted,
	int MaxRounds,
	string? BlockReason,
	string? BlockCode = null,
	int ConsecutiveFailures = 0,
	long MaxTokens = 0,
	long TokensAtStart = 0)
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

	/// 目标停在轮次上限：界面据此把「恢复」换成「再加 N 轮」。
	public const string RoundLimitCode = "round-limit";

	/// 「再加 N 轮」每次加的量。界面文案与实现共用，别各写一份。
	public const int ExtraRounds = 10;

	/// 同一类失败连续这么多轮才阻塞。单次网络抖动先重试，别把整条目标停在一次错误上。
	public const int MaxConsecutiveFailures = 3;

	/// 目标累计 token 达到预算：界面据此提示「加预算」。
	public const string TokenLimitCode = "token-limit";

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

	/// 终态标记：独占一行的 `GOAL_COMPLETE`，或 `GOAL_BLOCKED: 原因`。
	/// 代码块里的示例不算；正文里引用这两个词也不算，否则一句「完成后写 GOAL_COMPLETE」
	/// 会让目标提前结束。
	public static (string Phase, string? Reason)? DetectTerminal(string text)
	{
		foreach (var line in MarkdownScan.Lines(text))
		{
			var trimmed = line.Trim();
			if (trimmed.SequenceEqual("GOAL_COMPLETE"))
			{
				return (GoalState.Complete, null);
			}

			if (TryParseBlocked(trimmed, out var reason))
			{
				return (GoalState.Blocked, reason.Length > 0 ? reason : "blocked");
			}
		}

		return null;
	}

	/// `GOAL_BLOCKED` 后可带空格再跟冒号；原因取到行尾，手工扫描避开每行正则。
	private static bool TryParseBlocked(ReadOnlySpan<char> line, out string reason)
	{
		const string marker = "GOAL_BLOCKED";
		reason = string.Empty;
		if (!line.StartsWith(marker, StringComparison.Ordinal))
		{
			return false;
		}

		var rest = line[marker.Length..].TrimStart();
		if (rest.IsEmpty || rest[0] != ':')
		{
			return false;
		}

		reason = rest[1..].Trim().ToString();
		return true;
	}

	/// 展示用：去掉回复末尾的终态标记。标记仍参与状态判定，只是不该出现在正文里。
	public static string StripTerminal(string text)
	{
		var trimmed = text.TrimEnd();
		var lineStart = trimmed.LastIndexOf('\n') + 1;
		var lastLine = trimmed[lineStart..].Trim();
		if (!string.Equals(lastLine, "GOAL_COMPLETE", StringComparison.Ordinal) &&
			!TryParseBlocked(lastLine, out _))
		{
			return text;
		}

		return trimmed[..lineStart].TrimEnd();
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

	/// 旧续轮消息在投影里保留位置、内容改成这一句：直接删除会让 assistant 消息相邻，
	/// 也会让历史里的轮次标记整段消失。
	public const string FinishedRoundText = "[GOAL MODE ACTIVE] the previous round has finished.";

	/// 续轮指令带轮次，但不重复目标全文（目标在系统提示 section 里）。
	public static string ContinuationText(GoalState state) =>
		RoundLine(state) + "\n" +
		"Continue the current goal.\n" +
		"If done, write GOAL_COMPLETE. If blocked, write GOAL_BLOCKED: <reason>.";

	/// widget 行格式与 PiDeck 的 <c>parsePiGoalWidget</c> 一致：首行 <c>phase · n/m</c>，
	/// 第二行目标全文，阻塞原因第三行；完成或无目标不占位。
	public static IReadOnlyList<string> WidgetLines(GoalState state)
	{
		if (state.Objective.Length == 0 || state.Phase == GoalState.Complete)
		{
			return [];
		}

		var head = $"{state.Phase} · {state.RoundsStarted}/{state.MaxRounds}";
		return state.Phase == GoalState.Blocked && state.BlockReason is { Length: > 0 } reason
			? [head, state.Objective, reason]
			: [head, state.Objective];
	}

	/// 目标全文只在变化时写：32 轮上限下每轮都写一份目标原文会堆满历史。
	/// 清除要显式写空目标（<paramref name="includeObjective"/> 为 true），读端才不会穿透到旧目标；
	/// 完成靠 <c>enabled:false</c> 在回溯时截断，不必重复目标。
	public static JsonObject Encode(GoalState state, bool includeObjective = true)
	{
		var data = new JsonObject
		{
			["enabled"] = state.Enabled,
			["phase"] = state.Phase,
			["roundsStarted"] = state.RoundsStarted,
			["maxGoalRounds"] = state.MaxRounds,
		};
		if (includeObjective)
		{
			data["objective"] = state.Objective;
		}

		if (state.BlockReason is { Length: > 0 } reason)
		{
			data["blockReason"] = reason;
		}

		if (state.BlockCode is { Length: > 0 } code)
		{
			data["blockCode"] = code;
		}

		if (state.ConsecutiveFailures > 0)
		{
			data["consecutiveFailures"] = state.ConsecutiveFailures;
		}

		if (state.MaxTokens > 0)
		{
			data["maxTokens"] = state.MaxTokens;
		}

		if (state.TokensAtStart > 0)
		{
			data["tokensAtStart"] = state.TokensAtStart;
		}

		return data;
	}

	/// 从单条记录恢复，保留给只读一条条目的调用方。
	public static GoalState Decode(JsonNode? data) => Decode(data is null ? [] : [data]);

	/// 按分支顺序恢复目标。目标全文不是每条都写，得从尾部往回找最近一条带 objective 的记录；
	/// 遇到 <c>enabled:false</c> 立即截断，否则清除或完成之后重启会穿透到更早的目标（PiDeck #395）。
	/// 恢复出来的目标一律为 paused：重启后自动续跑等于绕过用户。
	public static GoalState Decode(IReadOnlyList<JsonNode?> entries)
	{
		if (entries.Count == 0)
		{
			return GoalState.Initial;
		}

		var objective = string.Empty;
		for (var index = entries.Count - 1; index >= 0; index--)
		{
			if (entries[index] is not JsonObject entry)
			{
				continue;
			}

			if (entry["enabled"] is JsonValue enabled && enabled.TryGetValue<bool>(out var on) && !on)
			{
				break;
			}

			if (entry["objective"] is JsonValue value && value.TryGetValue<string>(out var text))
			{
				objective = text;
				break;
			}
		}

		if (objective.Length == 0)
		{
			return GoalState.Initial;
		}

		var latest = entries[^1] as JsonObject;
		var rounds = latest?["roundsStarted"] is JsonValue roundsValue && roundsValue.TryGetValue<int>(out var started)
			? Math.Max(0, started)
			: 0;
		var max = latest?["maxGoalRounds"] is JsonValue maxValue && maxValue.TryGetValue<int>(out var limit) && limit > 0
			? limit
			: DefaultMaxRounds;
		var reason = latest?["blockReason"] is JsonValue reasonValue && reasonValue.TryGetValue<string>(out var reasonText)
			? reasonText
			: null;
		var code = latest?["blockCode"] is JsonValue codeValue && codeValue.TryGetValue<string>(out var codeText)
			? codeText
			: null;
		var failures = latest?["consecutiveFailures"] is JsonValue failuresValue &&
			failuresValue.TryGetValue<int>(out var failureCount) && failureCount > 0
				? failureCount
				: 0;
		var maxTokens = latest?["maxTokens"] is JsonValue maxTokensValue &&
			maxTokensValue.TryGetValue<long>(out var tokenLimit) && tokenLimit > 0
				? tokenLimit
				: 0;
		var tokensAtStart = latest?["tokensAtStart"] is JsonValue startValue &&
			startValue.TryGetValue<long>(out var startTokens) && startTokens > 0
				? startTokens
				: 0;

		return new GoalState(
			Enabled: true,
			Phase: GoalState.Paused,
			Objective: objective,
			RoundsStarted: rounds,
			MaxRounds: max,
			BlockReason: reason,
			BlockCode: code,
			ConsecutiveFailures: failures,
			MaxTokens: maxTokens,
			TokensAtStart: tokensAtStart);
	}


}
