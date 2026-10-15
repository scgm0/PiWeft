using PiWeft.Ai;
using PiWeft.CodingAgent.Session;

namespace PiWeft.CodingAgent;

/// <summary>整个会话文件的累计用量；压缩和导航不会抹去已产生的费用。</summary>
public sealed record SessionStatistics
{
	public long Input { get; init; }
	public long Output { get; init; }
	public long CacheRead { get; init; }
	public long CacheWrite { get; init; }
	public long Tokens { get; init; }
	public double Cost { get; init; }
	public int UserMessages { get; init; }
	public int AssistantMessages { get; init; }
	public int ToolResults { get; init; }
	public int ToolErrors { get; init; }
	public double LlmDurationMs { get; init; }
	public double ToolDurationMs { get; init; }
	public double? LatestCacheHitPercent { get; init; }
	public double? AverageCacheHitPercent { get; init; }
	public int CacheHitSamples { get; init; }
	public double? AverageFirstTokenMs { get; init; }
	public int FirstTokenSamples { get; init; }
	public ReplyPerformance? LatestReply { get; init; }

	public double? WeightedCacheHitPercent => Input + CacheRead + CacheWrite > 0
		? 100.0 * CacheRead / (Input + CacheRead + CacheWrite) : null;
}

/// <summary>单次模型回复，不包含工具执行或重试等待时间。</summary>
public sealed record ReplyPerformance(double? FirstTokenMs, double? DurationMs, double? StreamingDurationMs, long Output)
{
	public double? TokensPerSecond => Rate(Output, StreamingDurationMs);
	public double? EndToEndTokensPerSecond => Rate(Output, DurationMs);

	private static double? Rate(long tokens, double? milliseconds) =>
		tokens > 0 && milliseconds is > 0 && double.IsFinite(milliseconds.Value)
			? tokens * 1000.0 / milliseconds.Value : null;
}

/// <summary>追加时仅消费新条目，派生/重置/重载时重新计数，不持有历史命中率数组。</summary>
internal sealed class SessionStatisticsCache
{
	private string? _sessionId;
	private SessionManager? _manager;
	private SessionEntry? _lastEntry;
	private int _count;
	private long _revision = -1;
	private SessionStatistics _value = new();
	private double _hitSum;
	private double _firstTokenSum;

	public SessionStatistics Get(SessionManager manager)
	{
		if (ReferenceEquals(_manager, manager) && _revision == manager.Revision) return _value;
		var snapshot = manager.CaptureEntries();
		var entries = snapshot.Entries;
		if (!ReferenceEquals(_manager, manager) || _sessionId != snapshot.SessionId || entries.Count < _count ||
			(_count > 0 && !ReferenceEquals(entries[_count - 1], _lastEntry)))
		{
			_count = 0;
			_value = new();
			_hitSum = 0;
			_firstTokenSum = 0;
		}

		var input = _value.Input;
		var output = _value.Output;
		var read = _value.CacheRead;
		var write = _value.CacheWrite;
		var tokens = _value.Tokens;
		var cost = _value.Cost;
		var users = _value.UserMessages;
		var assistants = _value.AssistantMessages;
		var tools = _value.ToolResults;
		var errors = _value.ToolErrors;
		var llmMs = _value.LlmDurationMs;
		var toolMs = _value.ToolDurationMs;
		var hitSamples = _value.CacheHitSamples;
		var firstSamples = _value.FirstTokenSamples;
		var latestHit = _value.LatestCacheHitPercent;
		var latestReply = _value.LatestReply;

		for (var i = _count; i < entries.Count; i++)
		{
			Usage? usage = null;
			switch (entries[i])
			{
				case SessionMessageEntry { Message: UserMessage }:
					users++;
					break;
				case SessionMessageEntry { Message: AssistantMessage assistant }:
					assistants++;
					usage = assistant.Usage;
					var prompt = usage.Input + usage.CacheRead + usage.CacheWrite;
					if (prompt > 0)
					{
						latestHit = 100.0 * usage.CacheRead / prompt;
						_hitSum += latestHit.Value;
						hitSamples++;
					}
					var duration = Valid(assistant.DurationMs);
					var first = Valid(assistant.FirstTokenMs);
					var streaming = Valid(assistant.StreamingDurationMs);
					llmMs += duration ?? 0;
					if (first is { } ttft)
					{
						_firstTokenSum += ttft;
						firstSamples++;
					}
					latestReply = new(first, duration, streaming, usage.Output);
					break;
				case SessionMessageEntry { Message: ToolResultMessage tool }:
					tools++;
					errors += tool.IsError ? 1 : 0;
					toolMs += Valid(tool.DurationMs) ?? 0;
					break;
				case UsageEntry extra:
					usage = extra.Usage;
					break;
			}

			if (usage is null) continue;
			input += usage.Input;
			output += usage.Output;
			read += usage.CacheRead;
			write += usage.CacheWrite;
			tokens += usage.TotalTokens;
			cost += usage.Cost.Total;
		}

		_manager = manager;
		_sessionId = snapshot.SessionId;
		_revision = snapshot.Revision;
		_count = entries.Count;
		_lastEntry = entries.Count > 0 ? entries[^1] : null;
		return _value = new SessionStatistics
		{
			Input = input, Output = output, CacheRead = read, CacheWrite = write, Tokens = tokens, Cost = cost,
			UserMessages = users, AssistantMessages = assistants, ToolResults = tools, ToolErrors = errors,
			LlmDurationMs = llmMs, ToolDurationMs = toolMs,
			LatestCacheHitPercent = latestHit, AverageCacheHitPercent = hitSamples > 0 ? _hitSum / hitSamples : null,
			CacheHitSamples = hitSamples, FirstTokenSamples = firstSamples,
			AverageFirstTokenMs = firstSamples > 0 ? _firstTokenSum / firstSamples : null, LatestReply = latestReply,
		};
	}

	private static double? Valid(double? value) => value is >= 0 && double.IsFinite(value.Value) ? value : null;
}
