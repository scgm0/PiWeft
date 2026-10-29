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

	/// <summary>会话累计的活跃墙钟（毫秒）：重叠区间只计一次，分支切换不会括大分母。</summary>
	public double SessionDurationMs { get; init; }

	/// <summary>有首 token 的那部分回复的流式活跃时长（毫秒）。</summary>
	public double SessionStreamingMs { get; init; }

	/// <summary>计入流式均速的输出 token 总和（只统计能定位首 token 的回复）。</summary>
	public long SessionStreamingOutput { get; init; }

	/// <summary>
	/// 会话均速（流式口径）：输出 token / 首 delta 到回复结束的活跃时长并集，
	/// 与 PiDeck 的「均速」同口径，不含工具执行与排队等待。
	/// </summary>
	public double? SessionStreamingTokensPerSecond => Rate(SessionStreamingOutput, SessionStreamingMs);

	/// <summary>会话均速（端到端口径）：输出 token / 请求发起到回复结束的活跃时长并集。</summary>
	public double? SessionEndToEndTokensPerSecond => Rate(Output, SessionDurationMs);

	private static double? Rate(long tokens, double milliseconds) =>
		tokens > 0 && milliseconds > 0 && double.IsFinite(milliseconds)
			? tokens * 1000.0 / milliseconds : null;

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
/// 区间并集：会话活跃墙钟只算一次，重叠/乱序都不重复、不遗漏。
///
/// 两条路径：
/// - 常态（回复串行、起点单调递增）走 O(1) 快路径，直接累加，不排序；
/// - 一旦出现重叠或时间戳乱序（跨分支合并、时钟回拨、导入会话），转精确模式：
///   排序后合并区间，结果与 PiDeck 的全量排序并集同口径。
///
/// 为什么不用纯水位线增量：水位线要求区间按起点非降序到达，乱序时会静默低估
/// 活跃时长（分母偏小 → 均速虚高）。实测排序并集在 1.5 万条区间上也只要约
/// 0.4 ms，而且只在统计重建时算一次（不在流式热路径、不是每帧），不值得用
/// 正确性换那 0.02–0.4 ms。
/// </summary>
internal sealed class ActiveIntervalUnion
{
	/// <summary>按加入顺序保存的区间：精确模式要重排重算，也保留「万一」的原始数据。</summary>
	private (double Start, double End)[] _spans = [];

	private int _count;

	/// <summary>单调快路径的已覆盖末端与累计时长；起点一旦回退就作废。</summary>
	private double _monotonicEnd = double.NegativeInfinity;
	private double _monotonicTotal;

	/// <summary>已经出现过重叠/乱序：此后一律走精确合并。</summary>
	private bool _exact;

	/// <summary>精确结果缓存；Add 之后失效，同一份统计被多次读取时不重复排序。</summary>
	private double _mergedTotal;
	private bool _mergeDirty = true;

	public void Add(double start, double finish)
	{
		if (!double.IsFinite(start) || !double.IsFinite(finish) || finish <= start)
		{
			return;
		}

		if (_count == _spans.Length)
		{
			Array.Resize(ref _spans, Math.Max(4, _count * 2));
		}

		_spans[_count++] = (start, finish);
		_mergeDirty = true;

		if (!_exact && start >= _monotonicEnd)
		{
			// 纯追加且不重叠：并集长度就是各段之和，O(1)。
			_monotonicTotal += finish - start;
			_monotonicEnd = finish;
			return;
		}

		// 重叠（并发/派生任务）或起点回退：从这一段开始转精确模式。
		_exact = true;
	}

	/// <summary>并集总时长（毫秒）。单调输入为 O(1)，否则排序合并一次后缓存。</summary>
	public double TotalMs
	{
		get
		{
			if (!_exact)
			{
				return _monotonicTotal;
			}

			if (!_mergeDirty)
			{
				return _mergedTotal;
			}

			_mergedTotal = Merge();
			_mergeDirty = false;
			return _mergedTotal;
		}
	}

	/// <summary>排序后合并相邻区间；就地排序，后续 Add 追加到已排序前缀之后仍然正确。</summary>
	private double Merge()
	{
		Array.Sort(_spans, 0, _count);
		var total = 0.0;
		var (start, end) = _spans[0];
		for (var index = 1; index < _count; index++)
		{
			var (nextStart, nextEnd) = _spans[index];
			if (nextStart > end)
			{
				total += end - start;
				start = nextStart;
				end = nextEnd;
			}
			else if (nextEnd > end)
			{
				end = nextEnd;
			}
		}

		return total + (end - start);
	}
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

	private readonly ActiveIntervalUnion _wallClock = new();

	private readonly ActiveIntervalUnion _decoding = new();

	public long SessionStreamingOutput { get; private set; }

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

				// 会话均速的分子分母：消息时间戳就是请求发起时刻。旧记录可能没有
				// 时间戳（0），那种样本不参与均值，否则区间会从 1970 开始、把均速压成 0。
				// 重试等待算在端到端区间里（那就是真实的墙钟），重叠部分只计一次。
				if (duration is { } total && assistant.Timestamp > 0)
				{
					_wallClock.Add(assistant.Timestamp, assistant.Timestamp + total);
					if (first is { } lead)
					{
						_decoding.Add(assistant.Timestamp + lead, assistant.Timestamp + total);
						SessionStreamingOutput += usage.Output;
					}
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
		SessionDurationMs = _wallClock.TotalMs,
		SessionStreamingMs = _decoding.TotalMs,
		SessionStreamingOutput = SessionStreamingOutput,
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
