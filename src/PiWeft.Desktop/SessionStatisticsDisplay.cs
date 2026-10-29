using System.Diagnostics;
using System.Globalization;
using PiWeft.Agent;
using PiWeft.Ai;
using PiWeft.Ai.Json;
using PiWeft.CodingAgent;
using PiWeft.CodingAgent.Session;

namespace PiWeft.Desktop;

public sealed record StatisticRow(string Group, string Label, string Value, string Hint);

/// <summary>上下文占用的三段构成（PiDeck 的 contextBreakdown 同义）。数值均为估算。</summary>
/// <param name="SystemTokens">系统提示词与上下文文件占的部分。</param>
/// <param name="ToolsTokens">工具的 JSON Schema 声明占的部分。</param>
/// <param name="ConversationTokens">对话本身（用户 / 助手 / 工具结果）。</param>
public sealed record ContextBreakdown(long SystemTokens, long ToolsTokens, long ConversationTokens)
{
	public long Total => SystemTokens + ToolsTokens + ConversationTokens;

	public bool IsEmpty => SystemTokens <= 0 && ToolsTokens <= 0 && ConversationTokens <= 0;
}

/// <summary>可测试的统计展示快照，不包含 UI 控件或网络查询。</summary>
public sealed record SessionStatisticsDisplay(SessionStatistics Totals, long ContextTokens, int ContextWindow, int Turns)
{
	/// <summary>上下文构成（系统 / 工具 / 对话），无数据时为 null。</summary>
	public ContextBreakdown? Breakdown { get; init; }

	public double? ContextPercent => ContextWindow > 0 ? 100.0 * ContextTokens / ContextWindow : null;


	public static SessionStatisticsDisplay Create(CodingAgentSession session)
	{
		// 这一段以前整块藏在渲染里：拆成三段计时才能看出 200 ms 到底花在谁身上。
		var totalsWatch = Diagnostics.Enabled ? Stopwatch.StartNew() : null;
		var totals = session.Statistics();
		if (totalsWatch is not null)
		{
			Diagnostics.Record("stats.totals", totalsWatch, $"entries={session.Session.EntryCount}");
		}

		// 投影与估算分开计时：长会话里「重建整段投影」和「重扫历史估 token」是两件事，
		// 混在一起就分不清该修哪一个。先取投影（会填内部缓存），ContextTokens 只会剩估算。
		var projectWatch = Diagnostics.Enabled ? Stopwatch.StartNew() : null;
		var projected = session.Session.BuildProjectedMessages();
		if (projectWatch is not null)
		{
			Diagnostics.Record("stats.project", projectWatch, $"messages={projected.Count}");
		}

		var contextWatch = Diagnostics.Enabled ? Stopwatch.StartNew() : null;
		var context = session.ContextTokens().Tokens;
		if (contextWatch is not null)
		{
			Diagnostics.Record("stats.context", contextWatch, $"tokens={context}");
		}

		var breakdownWatch = Diagnostics.Enabled ? Stopwatch.StartNew() : null;
		var breakdown = ContextBreakdownOf(projected, session.DeclaredTools, context);
		if (breakdownWatch is not null)
		{
			Diagnostics.Record("stats.contextBreakdown", breakdownWatch,
				$"system={breakdown.SystemTokens}, tools={breakdown.ToolsTokens}, conversation={breakdown.ConversationTokens}");
		}

		var turnsWatch = Diagnostics.Enabled ? Stopwatch.StartNew() : null;
		var turns = session.Session.GetBranch().Count(static entry => entry is SessionMessageEntry { Message: UserMessage });
		if (turnsWatch is not null)
		{
			Diagnostics.Record("stats.turns", turnsWatch, $"turns={turns}");
		}

		return new SessionStatisticsDisplay(totals, context, session.Model.ContextWindow, turns) { Breakdown = breakdown };
	}

	/// <summary>
	/// 拆分上下文占用（PiDeck 的三段口径）：系统提示词与工具 schema 是“固定开销”，
	/// 对话是剩下的部分。系统与工具直接按字符估算，对话取「总占用 − 两者」的差值——
	/// 总占用里包含 thinking、图片等估算器不单列的内容，差值把它们归到对话里，
	/// 三段之和因此总等于总占用，进度条不会多出一截空隙。
	/// </summary>
	internal static ContextBreakdown ContextBreakdownOf(IReadOnlyList<Message> messages, IReadOnlyList<AgentTool> tools, long contextTokens)
	{
		long system = 0;
		foreach (var message in messages)
		{
			if (message is SystemMessage)
			{
				// 属性 ContextTokens 与静态类同名：完全限定到 Compaction 命名空间下的估算器。
				system += PiWeft.CodingAgent.Compaction.ContextTokens.EstimateTokens(message);
			}
		}

		var toolTokens = EstimateToolDeclarationTokens(tools);
		// 畸形数据（估算值大于总占用）不能让三段之和超出总占用，否则进度条会画出窗口。
		// 先给系统预留，剩下的才分给工具，顺序与图例一致。
		if (system > contextTokens) system = contextTokens;
		var toolsBudget = Math.Max(0, contextTokens - system);
		if (toolTokens > toolsBudget) toolTokens = toolsBudget;
		var conversation = Math.Max(0, contextTokens - system - toolTokens);
		return new ContextBreakdown(system, toolTokens, conversation);
	}

	/// <summary>
	/// 工具声明的 token 估算：按 provider 实际下发的形状（name + description + parameters）
	/// 序列化后除以 4。按工具集合的引用缓存：流式期间每帧重建统计时不重复序列化 MCP 的大 schema。
	/// </summary>
	internal static long EstimateToolDeclarationTokens(IReadOnlyList<AgentTool> tools)
	{
		if (tools.Count == 0)
		{
			return 0;
		}

		return ToolTokensCache.GetValue(tools, static list =>
		{
			long chars = 0;
			foreach (var tool in list)
			{
				chars += tool.Name.Length + tool.Description.Length;
				chars += tool.Parameters.Count == 0 ? 2 : tool.Parameters.ToJsonString(PiJson.NodeOptions).Length;
			}

			return new Box<long>((chars + 3) / 4);
		}).Value;
	}

	private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IReadOnlyList<AgentTool>, Box<long>> ToolTokensCache = new();

	/// <summary>ConditionalWeakTable 的值必须是引用类型；装箱一次就够。</summary>
	private sealed class Box<T>(T value)
	{
		public T Value { get; } = value;
	}

	public string ContextLabel => $"{Strings.Context} {Percent(ContextPercent)}";

	/// <summary>弹窗标题行：先给结论「上下文已用 21%」（PiDeck 同文案），再在右侧跟 ~已用/窗口。</summary>
	public string ContextUsedLabel => $"{Strings.ContextUsed} {Percent(ContextPercent)}";

	/// <summary>
	/// 输入框正下方的指标条（PiDeck 的 composerStats 同口径）：
	/// 「N 轮 | 首 token · 回复 · 流式 x tok/s · 均速 y tok/s | 缓存命中 z% | 输入 a · 输出 b tok」。
	/// 首 token / 回复 / 流式速度描述的是最近一次回复，均速描述的是整段会话，
	/// 两段用「·」分开、组间用「|」，跟 PiDeck 一样不会把“最近一次”和“整个会话”混读。
	/// </summary>
	public string Summary()
	{
		var groups = new List<string>(4) { Strings.StatisticsTurnCount(Turns) };
		if (Totals.LatestReply is { } reply)
		{
			var last = new List<string>(4);
			if (reply.FirstTokenMs is not null) last.Add($"首 token {Duration(reply.FirstTokenMs)}");
			if (reply.DurationMs is not null) last.Add($"回复 {Duration(reply.DurationMs)}");
			// 流式速度没采样（旧记录缺首 delta）时不写占位，免得看起来像失败。
			if (reply.TokensPerSecond is { } speed) last.Add($"流式 {Speed(speed)}");
			if (Totals.SessionStreamingTokensPerSecond is { } average) last.Add($"均速 {Speed(average)}");
			if (last.Count > 0) groups.Add(string.Join("  ·  ", last));
		}
		else if (Totals.SessionStreamingTokensPerSecond is { } idleAverage)
		{
			// 还没有可读的最近回复样本（例如刚压缩完），会话均速仍然有参考价值。
			groups.Add($"均速 {Speed(idleAverage)}");
		}

		if (Totals.LatestCacheHitPercent is not null) groups.Add($"{Strings.CacheHit} {Percent(Totals.LatestCacheHitPercent)}");
		groups.Add($"输入 {Compact(Totals.Input)} · 输出 {Compact(Totals.Output)} tok");
		return string.Join("  |  ", groups);
	}

	/// <summary>容量读作「~213K / 1M」：token 是估算值，加 ~ 提示不是精确计费数。</summary>
	public string ContextCapacity => $"~{Compact(ContextTokens)} / {Compact(ContextWindow)}";
	public string ContextValue => Percent(ContextPercent);
	public string CostValue => Totals.Cost.ToString("$0.000", CultureInfo.InvariantCulture);
	public string LatestSpeed => Speed(Totals.LatestReply?.TokensPerSecond);
	public string LatestLatency => Duration(Totals.LatestReply?.FirstTokenMs);
	public string LatestDuration => Duration(Totals.LatestReply?.DurationMs);

	public IReadOnlyList<StatisticRow> OverviewRows() => Rows().Where(row => row.Label is
		Strings.StatisticsInputOutput or Strings.StatisticsLatestHit or Strings.StatisticsCny or
		Strings.StatisticsFirstToken or Strings.StatisticsReplyDuration or Strings.StatisticsSpeedHintLabel).ToArray();

	public IReadOnlyList<StatisticRow> Rows() =>
	[
		new(Strings.StatisticsUsage, Strings.Context, $"{Percent(ContextPercent)} / {Compact(ContextTokens)} / {Compact(ContextWindow)}", Strings.StatisticsContextHint),
		new(Strings.StatisticsUsage, Strings.StatisticsInputOutput, $"{Compact(Totals.Input)} / {Compact(Totals.Output)}", Strings.StatisticsUsageHint),
		new(Strings.StatisticsUsage, Strings.StatisticsCacheIO, $"{Compact(Totals.CacheRead)} / {Compact(Totals.CacheWrite)}", Strings.StatisticsUsageHint),
		new(Strings.StatisticsUsage, Strings.StatisticsCacheTotal, Compact(Totals.CacheRead + Totals.CacheWrite), Strings.StatisticsUsageHint),
		new(Strings.StatisticsUsage, Strings.StatisticsLatestHit, Percent(Totals.LatestCacheHitPercent), Strings.StatisticsHitHint),
		new(Strings.StatisticsUsage, Strings.StatisticsAverageHit, $"{Percent(Totals.AverageCacheHitPercent)} ({Strings.StatisticsSamples(Totals.CacheHitSamples)})", Strings.StatisticsAverageHint),
		new(Strings.StatisticsUsage, Strings.StatisticsWeightedHit, Percent(Totals.WeightedCacheHitPercent), Strings.StatisticsHitHint),
		new(Strings.StatisticsUsage, Strings.StatisticsCost, Totals.Cost.ToString("$0.000", CultureInfo.InvariantCulture), Strings.StatisticsUsageHint),
		new(Strings.StatisticsUsage, Strings.StatisticsCny, (Totals.Cost * 7.2).ToString("¥0.00", CultureInfo.InvariantCulture), Strings.StatisticsCnyHint),
		new(Strings.SessionStatistics, Strings.StatisticsTurns, Turns.ToString(CultureInfo.InvariantCulture), "当前分支原始用户消息数，压缩不改变轮次；摘要不算用户轮次。"),
		new(Strings.SessionStatistics, Strings.StatisticsMessages, $"{Totals.UserMessages} / {Totals.AssistantMessages}", Strings.StatisticsUsageHint),
		new(Strings.SessionStatistics, Strings.StatisticsTools, $"{Totals.ToolResults} / {Totals.ToolErrors}", Strings.StatisticsUsageHint),
		new(Strings.StatisticsLastReply, Strings.StatisticsFirstToken, Duration(Totals.LatestReply?.FirstTokenMs), Strings.StatisticsFirstTokenHint),
		new(Strings.StatisticsLastReply, Strings.StatisticsReplyDuration, Duration(Totals.LatestReply?.DurationMs), Strings.StatisticsEndToEndHint),
		new(Strings.StatisticsLastReply, Strings.StatisticsSpeedHintLabel, Speed(Totals.LatestReply?.TokensPerSecond), Strings.StatisticsSpeedHint),
		new(Strings.StatisticsLastReply, Strings.StatisticsSessionSpeed, Speed(Totals.SessionStreamingTokensPerSecond), Strings.StatisticsSessionSpeedHint),
		new(Strings.StatisticsLastReply, Strings.StatisticsEndToEndSpeed, Speed(Totals.LatestReply?.EndToEndTokensPerSecond), Strings.StatisticsEndToEndHint),
		new(Strings.StatisticsLastReply, Strings.StatisticsSessionEndToEndSpeed, Speed(Totals.SessionEndToEndTokensPerSecond), Strings.StatisticsEndToEndHint),
		new(Strings.StatisticsTiming, Strings.StatisticsLlmDuration, Duration(Totals.LlmDurationMs), Strings.StatisticsUsageHint),
		new(Strings.StatisticsTiming, Strings.StatisticsToolDuration, Duration(Totals.ToolDurationMs), Strings.StatisticsToolTimingHint),
		new(Strings.StatisticsTiming, Strings.StatisticsFirstTokenAverage, $"{Duration(Totals.AverageFirstTokenMs)} ({Strings.StatisticsSamples(Totals.FirstTokenSamples)})", Strings.StatisticsFirstTokenHint),
	];

	public static string Compact(long value) => value switch
	{
		>= 1_000_000_000 => (value / 1_000_000_000.0).ToString("0.#", CultureInfo.InvariantCulture) + "B",
		>= 1_000_000 => (value / 1_000_000.0).ToString("0.#", CultureInfo.InvariantCulture) + "M",
		>= 1_000 => (value / 1_000.0).ToString("0.#", CultureInfo.InvariantCulture) + "K",
		_ => value.ToString(CultureInfo.InvariantCulture),
	};

	private static string Percent(double? value) => value is { } number && double.IsFinite(number)
		? number.ToString("0.#", CultureInfo.InvariantCulture) + "%" : Strings.StatisticsMissing;
	private static string Speed(double? value) => value is { } number && double.IsFinite(number)
		? number.ToString("0", CultureInfo.InvariantCulture) + " tok/s" : Strings.StatisticsMissing;
	private static string Duration(double? value) => value is { } ms && double.IsFinite(ms) && ms >= 0
		? ms < 1000 ? ms.ToString("0", CultureInfo.InvariantCulture) + "ms"
			: ms < 60_000 ? (ms / 1000).ToString("0.#", CultureInfo.InvariantCulture) + "s"
			: (ms / 60_000).ToString("0.#", CultureInfo.InvariantCulture) + "min"
		: Strings.StatisticsMissing;
}
