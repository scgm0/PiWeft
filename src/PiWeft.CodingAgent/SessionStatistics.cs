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

/// <summary>
/// 会话统计的累加器。抽成对象是为了让「增量累加」只维护一份状态，而不是在缓存里摊成十几个局部变量。
/// </summary>
internal sealed class StatisticsAccumulator
{
	private double _hitSum;
	private double _firstTokenSum;

	public long Input { get; private set; }

	public long Output { get; private set; }

	public long CacheRead { get; private set; }

	public long CacheWrite { get; private set; }

	public long Tokens { get; private set; }

	public double Cost { get; private set; }

	public int UserMessages { get; private set; }

	public int AssistantMessages { get; private set; }

	public int ToolResults { get; private set; }

	public int ToolErrors { get; private set; }

	public double LlmDurationMs { get; private set; }

	public double ToolDurationMs { get; private set; }

	public int CacheHitSamples { get; private set; }

	public int FirstTokenSamples { get; private set; }

	public double? LatestCacheHitPercent { get; private set; }

	public ReplyPerformance? LatestReply { get; private set; }

	public void Add(SessionEntry entry)
	{
		Usage? usage = null;
		switch (entry)
		{
			case SessionMessageEntry { Message: UserMessage }:
				UserMessages++;
				break;
			case SessionMessageEntry { Message: AssistantMessage assistant }:
				AssistantMessages++;
				usage = assistant.Usage;
				var prompt = usage.Input + usage.CacheRead + usage.CacheWrite;
				if (prompt > 0)
				{
					LatestCacheHitPercent = 100.0 * usage.CacheRead / prompt;
					_hitSum += LatestCacheHitPercent.Value;
					CacheHitSamples++;
				}

				var duration = Valid(assistant.DurationMs);
				var first = Valid(assistant.FirstTokenMs);
				var streaming = Valid(assistant.StreamingDurationMs);
				LlmDurationMs += duration ?? 0;
				if (first is { } ttft)
				{
					_firstTokenSum += ttft;
					FirstTokenSamples++;
				}

				LatestReply = new(first, duration, streaming, usage.Output);
				break;
			case SessionMessageEntry { Message: ToolResultMessage tool }:
				ToolResults++;
				ToolErrors += tool.IsError ? 1 : 0;
				ToolDurationMs += Valid(tool.DurationMs) ?? 0;
				break;
			case UsageEntry extra:
				usage = extra.Usage;
				break;
		}

		if (usage is null)
		{
			return;
		}

		Input += usage.Input;
		Output += usage.Output;
		CacheRead += usage.CacheRead;
		CacheWrite += usage.CacheWrite;
		Tokens += usage.TotalTokens;
		Cost += usage.Cost.Total;
	}

	public SessionStatistics ToStatistics() => new()
	{
		Input = Input,
		Output = Output,
		CacheRead = CacheRead,
		CacheWrite = CacheWrite,
		Tokens = Tokens,
		Cost = Cost,
		UserMessages = UserMessages,
		AssistantMessages = AssistantMessages,
		ToolResults = ToolResults,
		ToolErrors = ToolErrors,
		LlmDurationMs = LlmDurationMs,
		ToolDurationMs = ToolDurationMs,
		LatestCacheHitPercent = LatestCacheHitPercent,
		AverageCacheHitPercent = CacheHitSamples > 0 ? _hitSum / CacheHitSamples : null,
		CacheHitSamples = CacheHitSamples,
		FirstTokenSamples = FirstTokenSamples,
		AverageFirstTokenMs = FirstTokenSamples > 0 ? _firstTokenSum / FirstTokenSamples : null,
		LatestReply = LatestReply,
	};

	private static double? Valid(double? value) => value is >= 0 && double.IsFinite(value.Value) ? value : null;
}

/// <summary>追加时仅消费新条目，派生/重置/重载时重新计数，不持有历史命中率数组。</summary>
internal sealed class SessionStatisticsCache
{
	private string? _sessionId;
	private SessionManager? _manager;
	private SessionEntry? _lastEntry;
	private int _count;
	private long _revision = -1;
	private StatisticsAccumulator _accumulator = new();
	private SessionStatistics _value = new();

	public SessionStatistics Get(SessionManager manager)
	{
		// 版本没变就返回同一个实例：UI 每帧都会问一次，重建对象等于给稳定的统计数据制造抖动。
		if (ReferenceEquals(_manager, manager) && _revision == manager.Revision)
		{
			return _value;
		}



		var snapshot = manager.CaptureEntries();
		var entries = snapshot.Entries;
		if (!ReferenceEquals(_manager, manager) || _sessionId != snapshot.SessionId || entries.Count < _count ||
			(_count > 0 && !ReferenceEquals(entries[_count - 1], _lastEntry)))
		{
			_count = 0;
			_value = new();
			_accumulator = new StatisticsAccumulator();
		}

		for (var i = _count; i < entries.Count; i++)
		{
			_accumulator.Add(entries[i]);
		}

		_manager = manager;
		_sessionId = snapshot.SessionId;
		_revision = snapshot.Revision;
		_count = entries.Count;
		_lastEntry = entries.Count > 0 ? entries[^1] : null;
		return _value = _accumulator.ToStatistics();
	}
}
