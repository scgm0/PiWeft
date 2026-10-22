using System.Diagnostics;
using System.Globalization;
using PiWeft.Ai;
using PiWeft.CodingAgent;
using PiWeft.CodingAgent.Session;

namespace PiWeft.Desktop;

public sealed record StatisticRow(string Group, string Label, string Value, string Hint);

/// <summary>可测试的统计展示快照，不包含 UI 控件或网络查询。</summary>
public sealed record SessionStatisticsDisplay(SessionStatistics Totals, long ContextTokens, int ContextWindow, int Turns)
{
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

		var turnsWatch = Diagnostics.Enabled ? Stopwatch.StartNew() : null;
		var turns = session.Session.GetBranch().Count(static entry => entry is SessionMessageEntry { Message: UserMessage });
		if (turnsWatch is not null)
		{
			Diagnostics.Record("stats.turns", turnsWatch, $"turns={turns}");
		}

		return new SessionStatisticsDisplay(totals, context, session.Model.ContextWindow, turns);
	}

	public string ContextLabel => $"{Strings.Context} {Percent(ContextPercent)}";

	public string Summary()
	{
		var parts = new List<string> { Strings.StatisticsTurnCount(Turns) };
		if (Totals.LatestReply is { } reply)
		{
			if (reply.FirstTokenMs is not null) parts.Add($"首 token {Duration(reply.FirstTokenMs)}");
			if (reply.DurationMs is not null) parts.Add($"回复 {Duration(reply.DurationMs)}");
			if (reply.TokensPerSecond is { } speed) parts.Add(Speed(speed));
		}
		if (Totals.LatestCacheHitPercent is not null) parts.Add($"{Strings.CacheHit} {Percent(Totals.LatestCacheHitPercent)}");
		parts.Add($"输入 {Compact(Totals.Input)} · 输出 {Compact(Totals.Output)} tok");
		return string.Join("  |  ", parts);
	}

	public string ContextCapacity => $"{Compact(ContextTokens)} / {Compact(ContextWindow)}";
	public string ContextValue => Percent(ContextPercent);
	public string CostValue => Totals.Cost.ToString("$0.000", CultureInfo.InvariantCulture);
	public string LatestSpeed => Speed(Totals.LatestReply?.TokensPerSecond);
	public string LatestLatency => Duration(Totals.LatestReply?.FirstTokenMs);
	public string LatestDuration => Duration(Totals.LatestReply?.DurationMs);

	public IReadOnlyList<StatisticRow> OverviewRows() => Rows().Where(row => row.Label is
		Strings.StatisticsInputOutput or Strings.StatisticsLatestHit or Strings.StatisticsCny or
		Strings.StatisticsFirstToken or Strings.StatisticsReplyDuration or Strings.StatisticsGenerationSpeed).ToArray();

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
		new(Strings.StatisticsLastReply, Strings.StatisticsGenerationSpeed, Speed(Totals.LatestReply?.TokensPerSecond), Strings.StatisticsSpeedHint),
		new(Strings.StatisticsLastReply, Strings.StatisticsEndToEndSpeed, Speed(Totals.LatestReply?.EndToEndTokensPerSecond), Strings.StatisticsEndToEndHint),
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
		? number.ToString("0.#", CultureInfo.InvariantCulture) + " tok/s" : Strings.StatisticsMissing;
	private static string Duration(double? value) => value is { } ms && double.IsFinite(ms) && ms >= 0
		? ms < 1000 ? ms.ToString("0", CultureInfo.InvariantCulture) + "ms"
			: ms < 60_000 ? (ms / 1000).ToString("0.#", CultureInfo.InvariantCulture) + "s"
			: (ms / 60_000).ToString("0.#", CultureInfo.InvariantCulture) + "min"
		: Strings.StatisticsMissing;
}
