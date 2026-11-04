using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Aprillz.MewUI;
using Aprillz.MewUI.Rendering;
using PiWeft.Cli;
using PiWeft.CodingAgent.Session;
using ZLogger;

namespace PiWeft.Desktop;

/// 性能诊断采样（对齐 PiDeck 设置里的「性能诊断」）：默认关闭，关闭时调用点只做一次布尔判断，
/// 不分配、不落盘。采样分三档，成本依次递增，热路径只走前两档：
/// <list type="bullet">
/// <item><description>帧档：<see cref="ObserveFrame"/> 每帧写几个定长槽位与几个 Interlocked 计数，
/// 一帧约百纳秒、零分配，给出帧成本分布、掉帧/长帧、每帧分配、GC 暂停、输入→绘制延迟与绘制量；</description></item>
/// <item><description>计数档：<see cref="Count"/> 只累加事件次数（行 realize/回收、重建轮次等），
/// 同样零分配；<see cref="Slow"/> 是「计数 + 只在超过阈值时才落明细」的组合，明细文本延迟到超阈值后才生成；</description></item>
/// <item><description>明细档：<see cref="Record(string,double,string?)"/> 每次约 2 µs 与近 1 KB 垃圾，
/// 因此只给低频粗路径（启动、打开会话、投影、定稿）与慢于阈值的帧使用。</description></item>
/// </list>
/// 打开后每秒采一次进程内存/CPU 与 UI 队列延迟，明细逐条写入
/// <c>&lt;agentDir&gt;/diagnostics/timings-*.jsonl</c>，并按名称聚合次数/总耗时/最大/分位数，
/// 供「优化前后对照」与「谁把 UI 线程占住了」这类排查使用。
internal sealed partial class Diagnostics
{
	internal readonly record struct Timing(string Name, DateTime StartedAt, double DurationMs, string? Detail, int ThreadId);

	internal readonly record struct Stat(
		string Name,
		int Count,
		double TotalMs,
		double MaxMs,
		double LastMs,
		double P50Ms,
		double P95Ms,
		double P99Ms);

	internal readonly record struct CounterStat(string Name, long Value, double TotalMs, double MaxMs);

	/// 帧档聚合：全部来自定长环形缓冲与 Interlocked 计数，读取时才算分位数，
	/// 因此每帧的成本与是否显示面板无关。
	internal readonly record struct FrameStats(
		long Frames,
		double Fps,
		double LastMs,
		double AvgMs,
		double P50Ms,
		double P95Ms,
		double MaxMs,
		double GapP50Ms,
		double GapP95Ms,
		long DroppedFrames,
		long LongFrames,
		IReadOnlyList<double> Samples,
		long IdleGaps,
		double MaxGapMs,
		double AllocBytesPerFrame,
		double GcPausePercent,
		long Inputs,
		double InputP95Ms,
		double InputMaxMs,
		int DrawCalls,
		int CulledCalls,
		int TextDraws,
		int ShapeDraws,
		int ImageDraws,
		int ClipCount);

	/// 最近一秒的窗口值：与累计值分开，文件里才能画出真正的逐秒曲线。
	internal readonly record struct FrameWindowStats(
		long Frames,
		double Fps,
		double P50Ms,
		double P95Ms,
		double MaxMs,
		long DroppedFrames,
		long LongFrames,
		long IdleGaps,
		double MaxGapMs,
		double AllocBytesPerFrame,
		double GcPausePercent);

	internal sealed record Snapshot(
		bool Enabled,
		bool HasSample,
		string? LastError,
		DateTimeOffset SampledAt,
		long WorkingSetBytes,
		long PrivateMemoryBytes,
		long HeapBytes,
		long AllocatedBytes,
		long FragmentedBytes,
		long CommittedBytes,
		int PinnedObjects,
		int FinalizationPending,
		int Gen0Collections,
		int Gen2Collections,
		double CpuPercent,
		int PoolThreads,
		long PoolPendingWork,
		double QueueLagMs,
		double QueueLagMaxMs,
		double FramesPerSecond,
		bool ContinuousLoop,
		bool VSync,
		int TargetFps,
		double FrameBudgetMs,
		long LargeObjectHeapBytes,
		long PinnedHeapBytes,
		long DroppedLines,
		FrameStats Frame,
		FrameWindowStats FrameWindow,
		string? TimingsPath,
		string DirectoryPath,
		IReadOnlyList<Timing> Recent,
		IReadOnlyList<Stat> Stats,
		IReadOnlyList<CounterStat> Counters);

	internal const double LagWarnMs = 50;

	private const double SlowPathMs = 200;

	// 帧档的环形缓冲必须是 2 的幂：热路径用 & 取模，不做除法也不取余。
	private const int FrameSlots = 2048;
	private const int SampleSlots = 64;
	private const int InputSlots = 512;
	private const int RecentSlots = 256;
	private const int MaxPendingLines = 4096;
	private const int MaxDetailChars = 4096;
	private const int MaxStats = 128;
	private const int MaxCounters = 256;
	internal const long MaxFileBytes = 16L * 1024 * 1024;
	internal static long FileLimitBytes { get; set; } = MaxFileBytes;
	private const int MaxFiles = 12;
	private const int KeepRecentLines = 40;

	private const double DefaultFrameBudgetMs = 1000.0 / 60;
	internal const double LongFrameReportMs = 50;
	private const double LongFrameMs = LongFrameReportMs;
	private const double IdleGapMs = 400;
	/// 没有应用侧请求时，间隔超过这个长度就当作空闲而不是帧成本：框架内部重绘（悬停、动画）
	/// 通常紧跟上一帧，真正的长帧会有输入或应用侧请求在前面。
	private const double UnsolicitedFrameCapMs = 50;
	private const double InputWindowMs = 1000;
	private static readonly long JankWarnIntervalTicks = Stopwatch.Frequency;
	private static readonly long IdleGapTicks = (long)(Stopwatch.Frequency * IdleGapMs / 1000);
	private static readonly long UnsolicitedFrameCapTicks = (long)(Stopwatch.Frequency * UnsolicitedFrameCapMs / 1000);
	private static readonly long InputWindowTicks = (long)(Stopwatch.Frequency * InputWindowMs / 1000);

	private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(1);
	private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(250);
	private static readonly Lock Gate = new();
	private static readonly Lock WriterGate = new();
	private static readonly Lock PendingGate = new();
	private static readonly ConcurrentQueue<string> Pending = new();
	private static readonly Dictionary<string, Aggregator> Stats = new(StringComparer.Ordinal);
	private static readonly Timing[] Recent = new Timing[RecentSlots];
	private static readonly Process Process = Process.GetCurrentProcess();
	private static Timer? _sampleTimer;
	private static Timer? _flushTimer;
	private static WriterSession? _session;
	private static volatile bool _enabled;
	private static string _directory = string.Empty;
	private static string _timingsPath = string.Empty;
	private static string? _lastError;
	private static int _hasSample;
	private static int _sampling;
	private static int _draining;
	private static int _pendingLines;
	private static long _droppedLines;
	private static int _recentCursor;
	private static int _recentCount;

	// 采样值由线程池写入、UI 线程读取，因此用 Volatile 包住（double/long 需要 long 位中转）。
	private static long _workingSetBytes;
	private static long _privateMemoryBytes;
	private static long _heapBytes;
	private static long _allocatedBytes;
	private static long _fragmentedBytes;
	private static int _gen0Collections;
	private static int _gen2Collections;
	private static long _queueLagBits;
	private static long _queueLagMaxBits;
	private static long _cpuPercentBits;
	private static int _poolThreads;
	private static long _poolPendingWork;
	private static long _committedBytes;
	private static long _lohBytes;
	private static long _pohBytes;
	private static int _loopContinuous;
	private static int _loopVSync;
	private static int _loopTargetFps;
	private static int _pinnedObjects;
	private static int _finalizationPending;
	private static long _rssAnonBytes;
	private static long _rssFileBytes;
	private static long _rssShmemBytes;
	private static int _fontMapCount;
	private static long _fontMapBytes;
	private static long _gpuMapBytes;
	private static long _lastSmapsTicks;
	private static long _lastMemoryTrimTicks;

	// ── 帧档：定长环形缓冲 + Interlocked，热路径不分配、不加锁、不碰字符串 ──
	private static readonly long[] FrameDurations = new long[FrameSlots];
	private static readonly long[] FrameGaps = new long[FrameSlots];
	private static readonly long[] FrameIdleGaps = new long[FrameSlots];
	private static readonly long[] FrameAllocations = new long[FrameSlots];
	private static readonly long[] FrameGcPause = new long[FrameSlots];
	private static readonly long[] FrameTimestamps = new long[FrameSlots];
	private static readonly byte[] FrameFlags = new byte[FrameSlots];
	private static readonly long[] InputLatencies = new long[InputSlots];
	private static readonly ConcurrentDictionary<string, CounterBucket> Counters = new(StringComparer.Ordinal);
	// 分位数只在读快照时算，因此共用一块排序缓冲（快照在 Gate 内，单线程访问）。
	private static readonly long[] Scratch = new long[FrameSlots];
	private static readonly long[] WindowScratch = new long[FrameSlots];

	private const byte FlagSampled = 1;
	private const byte FlagIdle = 2;
	private const byte FlagDropped = 4;
	private const byte FlagLong = 8;

	private static int _frameCursor;
	private static long _frameLastTicks;
	private static long _frameRequestTicks;
	private static long _frameTotalTicks;
	private static long _frameCount;
	private static long _droppedFrames;
	private static long _longFrames;
	private static long _idleGaps;
	private static long _maxGapTicks;
	private static long _frameAllocTotal;
	private static long _frameAllocBaseline;
	private static long _gcPauseTotalTicks;
	private static long _gcPauseBaselineTicks;
	private static long _frameBudgetTicks = (long)(Stopwatch.Frequency * DefaultFrameBudgetMs / 1000);
	private static int _inputCursor;
	private static long _inputPendingTicks;
	private static long _inputCount;
	private static long _jankWarnTicks;
	private static long _attributionTicks;
	private static int _frameDrawCalls;
	private static int _frameCullCount;
	private static int _frameTextDraws;
	private static int _frameShapeDraws;
	private static int _frameImageDraws;
	private static int _frameClipCount;
	private static long _cpuTicksBaseline;
	private static long _cpuWallBaseline;

	public static string DirectoryPath(string? agentDir = null) =>
		Path.Combine(string.IsNullOrWhiteSpace(agentDir) ? SessionPaths.GetAgentDir() : agentDir!, "diagnostics");

	public static bool Enabled => _enabled;

	public static string? LastError => Volatile.Read(ref _lastError);

	/// 按设置开关启停。重复设置同值不会重开文件，避免设置页抖一下就把日志切成两份。
	/// 诊断是辅助功能：目录不可写、磁盘满或文件被占用时必须静默降级，不能影响主流程。
	public static void SetEnabled(bool enabled, string? agentDir = null)
	{
		if (!enabled)
		{
			Stop();
			return;
		}

		lock (Gate)
		{
			if (_enabled)
			{
				return;
			}

			WriterSession? session = null;
			try
			{
				var directory = DirectoryPath(agentDir);
				Directory.CreateDirectory(directory);
				CleanupOldFiles(directory);
				session = OpenWriter(directory, sequence: 0);
				_directory = directory;
				_timingsPath = session.Path;
				Volatile.Write(ref _session, session);
				_lastError = null;
				_enabled = true;
				ResetLocked();
				WriteMetaLocked(session);
				_sampleTimer = new Timer(_ => Sample(), null, SampleInterval, SampleInterval);
				_flushTimer = new Timer(_ => Flush(), null, FlushInterval, FlushInterval);
			}
			catch (Exception ex)
			{
				_lastError = ex.Message;
				_enabled = false;
				_sampleTimer?.Dispose();
				_sampleTimer = null;
				_flushTimer?.Dispose();
				_flushTimer = null;
				session?.Dispose();
				_session = null;
				_timingsPath = string.Empty;
				AppLog.Failure(AppLog.For<Diagnostics>(), "Start diagnostics", ex);
			}
		}
	}

	public static void Record(string name, double durationMs, string? detail = null)
	{
		if (!_enabled)
		{
			return;
		}

		var startedAt = DateTime.Now.AddMilliseconds(-durationMs);
		var threadId = Environment.CurrentManagedThreadId;
		detail = TrimDetail(detail);
		lock (Gate)
		{
			var bucket = StatsBucketLocked(name);
			bucket.Add(durationMs);
			AppendRecentLocked(new Timing(name, startedAt, durationMs, detail, threadId));
		}

		EnqueueLine(Serialize(name, startedAt, durationMs, detail, "span", threadId));
		if (durationMs >= SlowPathMs)
		{
			AppLog.For<Diagnostics>().ZLogWarning($"Slow path: {name} {durationMs:0.0} ms {detail}");
		}
	}

	public static void Record(string name, Stopwatch watch, string? detail = null)
	{
		if (!_enabled)
		{
			watch.Stop();
			return;
		}

		Record(name, Elapsed(watch), detail);
	}

	// 长帧归因：框架的 PerformanceProfiler 相位分解在发行包里被 DevToolsGate 关闭，读出来恒为零；
	// 这里记录本帧已经无条件可得的绘制量，把「应用侧绘制多 vs 帧间空白」分开。
	private static void AttributeLongFrame(double ms, in RenderStats stats)
	{
		Record("frame.long", ms,
			$"draws={stats.DrawCalls} cull={stats.CullCount} text={stats.PrimitiveStats.DrawTextCount} " +
			$"shape={stats.PrimitiveStats.ShapeCount} clip={stats.PrimitiveStats.ClipCount}");
	}

	/// 应用侧发起一次重绘（数据变更、滚动动画、流式打字机等）。记下最早的那次请求，
	/// <see cref="ObserveFrame"/> 用它把「按请求重绘前的空闲」从「本帧真实成本」里切掉。
	/// 不依赖框架回调，因此 MewUI 不需要任何改动；覆盖不到的框架内部重绘（悬停、框架动画）
	/// 退回帧间隔口径，见 <see cref="ObserveFrame"/>。
	public static void NoteFrameRequest()
	{
		if (!_enabled) return;
		var now = Stopwatch.GetTimestamp();
		var current = Interlocked.Read(ref _frameRequestTicks);
		while (current == 0L || now < current)
		{
			var previous = Interlocked.CompareExchange(ref _frameRequestTicks, now, current);
			if (previous == current) return;
			current = previous;
		}
	}

	/// 每帧结束（接 <see cref="Window.FrameRendered"/>）：只写定长槽位和几个 Interlocked 计数。
	/// 关闭时是一次 volatile 判断；开着时一帧约百纳秒、零分配。
	/// <paramref name="stats"/> 来自 <see cref="Window.LastFrameStats"/>，框架本来就每帧无条件填好。
	public static void ObserveFrame(in RenderStats stats)
	{
		if (!_enabled)
		{
			return;
		}

		var now = Stopwatch.GetTimestamp();
		var request = Interlocked.Exchange(ref _frameRequestTicks, 0L);
		var previousEnd = Interlocked.Exchange(ref _frameLastTicks, now);
		var interval = previousEnd != 0L ? now - previousEnd : 0L;
		var hasRequest = request != 0L && request <= now;

		// 有请求：请求之前是空闲，请求之后才是这一帧的成本。
		// 没有请求：框架内部重绘（悬停/框架动画），间隔短按成本算；间隔已经像空闲就整段算空闲。
		long work;
		var idleBefore = 0L;
		if (hasRequest)
		{
			// 请求落在上一帧之内（渲染回调里再次请求）时，从上一帧结束算起，避免重复计时。
			var start = previousEnd != 0L && request < previousEnd ? previousEnd : request;
			work = Math.Max(0L, now - start);
			if (previousEnd != 0L && request > previousEnd) idleBefore = request - previousEnd;
		}
		else
		{
			work = interval < UnsolicitedFrameCapTicks ? interval : 0L;
			if (interval >= UnsolicitedFrameCapTicks) idleBefore = interval;
		}

		var stall = idleBefore >= IdleGapTicks;
		if (stall)
		{
			Interlocked.Increment(ref _idleGaps);
			UpdateMax(ref _maxGapTicks, idleBefore);
		}

		var slot = Interlocked.Increment(ref _frameCursor) & (FrameSlots - 1);
		var sampled = work > 0L;
		var flags = sampled ? FlagSampled : (byte)0;
		if (stall) flags |= FlagIdle;

		var workMs = TicksToMs(work);
		if (sampled)
		{
			Interlocked.Add(ref _frameTotalTicks, work);
			Interlocked.Increment(ref _frameCount);
			if (workMs >= LongFrameMs)
			{
				flags |= FlagLong;
				Interlocked.Increment(ref _longFrames);
				// 长帧值得单独出现在日志里：它是「哪一刻卡住」的唯一确定信号，
				// 但限流后再写，避免一次掉帧风暴刷满日志。
				var previousWarn = Interlocked.Read(ref _jankWarnTicks);
				if (now - previousWarn >= JankWarnIntervalTicks &&
					Interlocked.CompareExchange(ref _jankWarnTicks, now, previousWarn) == previousWarn)
				{
					AppLog.For<Diagnostics>().ZLogWarning(
						$"Long frame: {workMs:0.0} ms (dropped {Interlocked.Read(ref _droppedFrames)}, " +
						$"long {Interlocked.Read(ref _longFrames)}, queue lag {BitsToDouble(Volatile.Read(ref _queueLagMaxBits)):0.0} ms)");
					// 相位归因单独限流（每秒最多两条）：长帧高频出现时也能留下足够的样本分布。
					var previousAttribution = Interlocked.Read(ref _attributionTicks);
					if (now - previousAttribution >= Stopwatch.Frequency / 2 &&
						Interlocked.CompareExchange(ref _attributionTicks, now, previousAttribution) == previousAttribution)
					{
						AttributeLongFrame(workMs, stats);
					}
				}
			}
			else if (work >= Interlocked.Read(ref _frameBudgetTicks))
			{
				flags |= FlagDropped;
				Interlocked.Increment(ref _droppedFrames);
			}
		}

		// 每帧分配与 GC 暂停按「上一帧到这一帧」的差值统计；长停顿里的量不算进帧均值，
		// 否则「没动鼠标时的后台 GC」会把帧成本算爆。
		var allocated = GC.GetAllocatedBytesForCurrentThread();
		var allocBase = Interlocked.Exchange(ref _frameAllocBaseline, allocated);
		var allocDelta = allocBase != 0L && !stall ? allocated - allocBase : 0L;

		var pause = GC.GetTotalPauseDuration().Ticks;
		var pauseBase = Interlocked.Exchange(ref _gcPauseBaselineTicks, pause);
		var pauseDelta = pauseBase != 0L && !stall ? pause - pauseBase : 0L;

		Volatile.Write(ref FrameTimestamps[slot], now);
		Volatile.Write(ref FrameGaps[slot], interval);
		Volatile.Write(ref FrameIdleGaps[slot], idleBefore);
		Volatile.Write(ref FrameDurations[slot], sampled ? work : 0L);
		Volatile.Write(ref FrameAllocations[slot], sampled ? allocDelta : 0L);
		Volatile.Write(ref FrameGcPause[slot], sampled ? pauseDelta : 0L);
		Volatile.Write(ref FrameFlags[slot], flags);
		if (sampled)
		{
			Interlocked.Add(ref _frameAllocTotal, allocDelta);
			Interlocked.Add(ref _gcPauseTotalTicks, pauseDelta);
		}

		// 输入→绘制：把上一次按键/指针事件到这一帧呈现的时间记进小环形缓冲。
		// 空闲很久之后的第一次重绘不算样本，否则面板会显示一个荒谬的「延迟」。
		var pending = Interlocked.Exchange(ref _inputPendingTicks, 0L);
		if (pending != 0L)
		{
			var latency = now - pending;
			if (latency >= 0L && latency <= InputWindowTicks)
			{
				Volatile.Write(ref InputLatencies[Interlocked.Increment(ref _inputCursor) & (InputSlots - 1)], latency);
				Interlocked.Increment(ref _inputCount);
			}
		}

		var primitives = stats.PrimitiveStats;
		Volatile.Write(ref _frameDrawCalls, stats.DrawCalls);
		Volatile.Write(ref _frameCullCount, stats.CullCount);
		Volatile.Write(ref _frameTextDraws, primitives.DrawTextCount);
		Volatile.Write(ref _frameShapeDraws, primitives.ShapeCount);
		Volatile.Write(ref _frameImageDraws, primitives.DrawImageCount);
		Volatile.Write(ref _frameClipCount, primitives.ClipCount);
	}

	public static void NoteInput()
	{
		if (!_enabled) return;
		// 输入本身就是一次重绘请求：空闲从输入那一刻结束，而不是从下一帧开始算。
		NoteFrameRequest();
		var now = Stopwatch.GetTimestamp();
		// 一帧内可能有多条输入：保留最早的那条，下一帧记的是最坏等待，而不是最后一次。
		var current = Interlocked.Read(ref _inputPendingTicks);
		while (current == 0L || now < current)
		{
			var previous = Interlocked.CompareExchange(ref _inputPendingTicks, now, current);
			if (previous == current) return;
			current = previous;
		}
	}

	public static void Count(string name, long delta = 1)
	{
		if (!_enabled)
		{
			return;
		}

		if (Counters.Count >= MaxCounters && !Counters.ContainsKey(name))
		{
			name = "other";
		}

		Counters.GetOrAdd(name, static key => new CounterBucket(key)).Add(delta);
	}

	/// 热路径用的组合档：先记一次计数，只有超过 <paramref name="thresholdMs"/> 才落明细。
	/// 每帧都会发生的路径用它，否则诊断本身就成了每秒上百 KB 的垃圾来源。
	public static void Slow(string name, double durationMs, double thresholdMs, string? detail = null)
	{
		if (!_enabled)
		{
			return;
		}

		var bucket = GetCounterBucket(name);
		bucket.Add(1);
		bucket.AddTime((long)Math.Round(durationMs * 1000));
		if (durationMs >= thresholdMs)
		{
			Record(name, durationMs, detail);
		}
	}

	/// <summary>
	/// 延迟生成明细的重载：低于阈值时连字符串都不创建。热路径优先用它，
	/// 避免调用点每帧先拼好 detail，再被 <see cref="Slow"/> 丢掉。
	/// </summary>
	public static void Slow<TState>(
		string name,
		double durationMs,
		double thresholdMs,
		Func<TState, string> detailFactory,
		TState state)
	{
		if (!_enabled)
		{
			return;
		}

		var bucket = GetCounterBucket(name);
		bucket.Add(1);
		bucket.AddTime((long)Math.Round(durationMs * 1000));
		if (durationMs >= thresholdMs)
		{
			Record(name, durationMs, detailFactory(state));
		}
	}

	public static void Slow(string name, Stopwatch watch, double thresholdMs, string? detail = null)
	{
		if (!_enabled)
		{
			watch.Stop();
			return;
		}

		Slow(name, Elapsed(watch), thresholdMs, detail);
	}

	public static void Slow<TState>(
		string name,
		Stopwatch watch,
		double thresholdMs,
		Func<TState, string> detailFactory,
		TState state)
	{
		if (!_enabled)
		{
			watch.Stop();
			return;
		}

		Slow(name, Elapsed(watch), thresholdMs, detailFactory, state);
	}

	/// 采样窗口内的一次性作用域：<c>using (Diagnostics.Track("session.open.read"))</c>。
	/// 关闭时返回一个什么都不做的句柄，连字符串拼接都不会发生。
	public static Scope Track(string name) => new(name);

	/// 主动压一次堆：返回回收前后托管堆的字节数。会阻塞当前线程，所以只挂在面板按钮上。
	public static (long BeforeBytes, long AfterBytes, double PauseMs) Compact()
	{
		var started = Stopwatch.GetTimestamp();
		var before = GC.GetTotalMemory(forceFullCollection: false);
		var after = GC.GetTotalMemory(forceFullCollection: true);
		return (before, after, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
	}

	public static Snapshot TakeSnapshot()
	{
		lock (Gate)
		{
			return new Snapshot(
				_enabled,
				Volatile.Read(ref _hasSample) == 1,
				Volatile.Read(ref _lastError),
				DateTimeOffset.Now,
				Interlocked.Read(ref _workingSetBytes),
				Interlocked.Read(ref _privateMemoryBytes),
				Interlocked.Read(ref _heapBytes),
				Interlocked.Read(ref _allocatedBytes),
				Interlocked.Read(ref _fragmentedBytes),
				Interlocked.Read(ref _committedBytes),
				Volatile.Read(ref _pinnedObjects),
				Volatile.Read(ref _finalizationPending),
				Volatile.Read(ref _gen0Collections),
				Volatile.Read(ref _gen2Collections),
				BitsToDouble(Volatile.Read(ref _cpuPercentBits)),
				Volatile.Read(ref _poolThreads),
				Interlocked.Read(ref _poolPendingWork),
				BitsToDouble(Volatile.Read(ref _queueLagBits)),
				BitsToDouble(Volatile.Read(ref _queueLagMaxBits)),
				Volatile.Read(ref _framesLastSecondSnapshot),
				Volatile.Read(ref _loopContinuous) == 1,
				Volatile.Read(ref _loopVSync) == 1,
				Volatile.Read(ref _loopTargetFps),
				TicksToMs(Interlocked.Read(ref _frameBudgetTicks)),
				Interlocked.Read(ref _lohBytes),
				Interlocked.Read(ref _pohBytes),
				Interlocked.Read(ref _droppedLines),
				BuildFrameStats(),
				BuildWindowStats(Stopwatch.GetTimestamp()),
				string.IsNullOrEmpty(_timingsPath) ? null : _timingsPath,
				_directory,
				SnapshotRecent(KeepRecentLines),
				[.. Stats.Values.Select(static bucket => bucket.Stat()).OrderByDescending(static stat => stat.TotalMs)],
				[.. Counters.Values.Select(static bucket => bucket.Stat()).OrderByDescending(static counter => counter.Value)]);
		}
	}

	private static long _framesLastSecondSnapshot;

	/// 帧档汇总：分位数只在读面板时算一次（拷贝到排序缓冲再取），
	/// 因此热路径永远只需要写一个槽位。
	private static FrameStats BuildFrameStats()
	{
		var frames = Interlocked.Read(ref _frameCount);
		var inputs = Interlocked.Read(ref _inputCount);
		var gaps = Interlocked.Read(ref _idleGaps);
		if (frames == 0L && inputs == 0L && gaps == 0L)
		{
			return default;
		}

		var samples = 0;
		for (var index = 0; index < FrameSlots; index++)
		{
			var duration = Volatile.Read(ref FrameDurations[index]);
			if (duration > 0L)
			{
				Scratch[samples++] = duration;
			}
		}

		Array.Sort(Scratch, 0, samples);
		var totalTicks = Interlocked.Read(ref _frameTotalTicks);
		var average = frames > 0L ? TicksToMs(totalTicks / frames) : 0;
		var median = QuantileMs(Scratch, samples, 0.5);
		var p95 = QuantileMs(Scratch, samples, 0.95);
		var max = samples > 0 ? TicksToMs(Scratch[samples - 1]) : 0;

		// 连续循环才有「典型 FPS」；按请求重绘时帧间隔主要是空闲，不能换算成 FPS。
		var gapSamples = 0;
		if (Volatile.Read(ref _loopContinuous) == 1)
		{
			for (var index = 0; index < FrameSlots; index++)
			{
				if ((Volatile.Read(ref FrameFlags[index]) & FlagSampled) != 0)
				{
					var gap = Volatile.Read(ref FrameGaps[index]);
					if (gap > 0L) Scratch[gapSamples++] = gap;
				}
			}

			Array.Sort(Scratch, 0, gapSamples);
		}

		var gapP50 = QuantileMs(Scratch, gapSamples, 0.5);
		var gapP95 = QuantileMs(Scratch, gapSamples, 0.95);
		var fps = gapP50 > 0 ? Math.Round(1000 / gapP50, 1) : 0;
		var last = Volatile.Read(ref FrameDurations[(Volatile.Read(ref _frameCursor) - 1) & (FrameSlots - 1)]);
		var pauseTicks = Interlocked.Read(ref _gcPauseTotalTicks);
		var allocTotal = Interlocked.Read(ref _frameAllocTotal);

		var inputSamples = 0;
		if (inputs > 0L)
		{
			for (var index = 0; index < InputSlots; index++)
			{
				var latency = Volatile.Read(ref InputLatencies[index]);
				if (latency > 0L) Scratch[inputSamples++] = latency;
			}

			Array.Sort(Scratch, 0, inputSamples);
		}

		// 面板画帧时间条用：取最近 64 个真实帧成本（旧→新）。
		var history = new List<double>(SampleSlots);
		var newest = Volatile.Read(ref _frameCursor);
		for (var offset = SampleSlots - 1; offset >= 0; offset--)
		{
			var duration = Volatile.Read(ref FrameDurations[(newest - offset) & (FrameSlots - 1)]);
			if (duration > 0L) history.Add(Math.Round(TicksToMs(duration), 1));
		}

		return new FrameStats(
			frames,
			fps,
			TicksToMs(last),
			average,
			median,
			p95,
			max,
			gapP50,
			gapP95,
			Interlocked.Read(ref _droppedFrames),
			Interlocked.Read(ref _longFrames),
			history,
			Interlocked.Read(ref _idleGaps),
			TicksToMs(Interlocked.Read(ref _maxGapTicks)),
			frames > 0L ? (double)allocTotal / frames : 0,
			totalTicks > 0L ? Math.Round(pauseTicks * 100.0 / totalTicks, 2) : 0,
			inputs,
			QuantileMs(Scratch, inputSamples, 0.95),
			inputSamples > 0 ? TicksToMs(Scratch[inputSamples - 1]) : 0,
			Volatile.Read(ref _frameDrawCalls),
			Volatile.Read(ref _frameCullCount),
			Volatile.Read(ref _frameTextDraws),
			Volatile.Read(ref _frameShapeDraws),
			Volatile.Read(ref _frameImageDraws),
			Volatile.Read(ref _frameClipCount));
	}

	/// 最近一秒的窗口：从帧环形缓冲按时间戳重算，避免把累计 max 当成每秒 max。
	private static FrameWindowStats BuildWindowStats(long now)
	{
		var since = now - (long)(Stopwatch.Frequency * SampleInterval.TotalSeconds);
		var count = 0;
		long dropped = 0, longFrames = 0, idle = 0, maxGap = 0, alloc = 0, pause = 0, total = 0, max = 0;
		for (var index = 0; index < FrameSlots; index++)
		{
			var timestamp = Volatile.Read(ref FrameTimestamps[index]);
			if (timestamp == 0L || timestamp < since || timestamp > now) continue;
			var flags = Volatile.Read(ref FrameFlags[index]);
			if ((flags & FlagIdle) != 0)
			{
				idle++;
				var gap = Volatile.Read(ref FrameIdleGaps[index]);
				if (gap > maxGap) maxGap = gap;
			}

			if ((flags & FlagSampled) == 0) continue;
			var duration = Volatile.Read(ref FrameDurations[index]);
			if (duration <= 0L) continue;
			WindowScratch[count++] = duration;
			total += duration;
			alloc += Volatile.Read(ref FrameAllocations[index]);
			pause += Volatile.Read(ref FrameGcPause[index]);
			if (duration > max) max = duration;
			if ((flags & FlagDropped) != 0) dropped++;
			if ((flags & FlagLong) != 0) longFrames++;
		}

		Array.Sort(WindowScratch, 0, count);
		return new FrameWindowStats(
			count,
			count * 1000.0 / SampleInterval.TotalMilliseconds,
			QuantileMs(WindowScratch, count, 0.5),
			QuantileMs(WindowScratch, count, 0.95),
			TicksToMs(max),
			dropped,
			longFrames,
			idle,
			TicksToMs(maxGap),
			count > 0 ? (double)alloc / count : 0,
			total > 0L ? Math.Round(pause * 100.0 / total, 2) : 0);
	}

	private static double QuantileMs(long[] samples, int count, double quantile) =>
		count <= 0 ? 0 : TicksToMs(samples[Math.Clamp((int)Math.Round(quantile * (count - 1)), 0, count - 1)]);

	private static double TicksToMs(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

	/// CPU tick 是 TimeSpan 的 100 ns，不是 Stopwatch tick；单独抽出来便于测试换算口径。
	internal static double CpuPercentFromTicks(long cpuTicks, long wallTicks, int cores)
	{
		if (cores <= 0 || wallTicks <= 0) return 0;
		var busyMs = cpuTicks / (double)TimeSpan.TicksPerMillisecond;
		var spanMs = TicksToMs(wallTicks);
		return spanMs > 0 ? Math.Clamp(busyMs / spanMs * 100 / cores, 0, 100) : 0;
	}

	private static void UpdateMax(ref long target, long value)
	{
		var current = Interlocked.Read(ref target);
		while (value > current)
		{
			var previous = Interlocked.CompareExchange(ref target, value, current);
			if (previous == current) return;
			current = previous;
		}
	}

	public static void Reset()
	{
		lock (Gate)
		{
			ResetLocked();
		}
	}

	private static void ResetLocked()
	{
		Stats.Clear();
		Array.Clear(Recent);
		_recentCursor = 0;
		_recentCount = 0;
		Counters.Clear();
		Interlocked.Exchange(ref _queueLagBits, 0);
		Interlocked.Exchange(ref _queueLagMaxBits, 0);
		Interlocked.Exchange(ref _cpuPercentBits, 0);
		Interlocked.Exchange(ref _cpuTicksBaseline, 0);
		Interlocked.Exchange(ref _cpuWallBaseline, 0);
		Interlocked.Exchange(ref _poolPendingWork, 0);
		Interlocked.Exchange(ref _lohBytes, 0);
		Interlocked.Exchange(ref _pohBytes, 0);
		Interlocked.Exchange(ref _rssAnonBytes, 0);
		Interlocked.Exchange(ref _rssFileBytes, 0);
		Interlocked.Exchange(ref _rssShmemBytes, 0);
		Volatile.Write(ref _fontMapCount, 0);
		Interlocked.Exchange(ref _fontMapBytes, 0);
		Interlocked.Exchange(ref _gpuMapBytes, 0);
		Interlocked.Exchange(ref _lastSmapsTicks, 0);
		Interlocked.Exchange(ref _lastMemoryTrimTicks, 0);
		Interlocked.Exchange(ref _framesLastSecondSnapshot, 0);
		Array.Clear(FrameDurations);
		Array.Clear(FrameGaps);
		Array.Clear(FrameIdleGaps);
		Array.Clear(FrameAllocations);
		Array.Clear(FrameGcPause);
		Array.Clear(FrameTimestamps);
		Array.Clear(FrameFlags);
		Array.Clear(InputLatencies);
		_frameCursor = 0;
		_inputCursor = 0;
		Interlocked.Exchange(ref _frameLastTicks, 0);
		Interlocked.Exchange(ref _frameRequestTicks, 0);
		Interlocked.Exchange(ref _frameTotalTicks, 0);
		Interlocked.Exchange(ref _frameCount, 0);
		Interlocked.Exchange(ref _droppedFrames, 0);
		Interlocked.Exchange(ref _longFrames, 0);
		Interlocked.Exchange(ref _idleGaps, 0);
		Interlocked.Exchange(ref _maxGapTicks, 0);
		Interlocked.Exchange(ref _frameAllocTotal, 0);
		Interlocked.Exchange(ref _frameAllocBaseline, 0);
		Interlocked.Exchange(ref _gcPauseTotalTicks, 0);
		Interlocked.Exchange(ref _gcPauseBaselineTicks, 0);
		Interlocked.Exchange(ref _inputPendingTicks, 0);
		Interlocked.Exchange(ref _inputCount, 0);
		Interlocked.Exchange(ref _jankWarnTicks, 0);
		Interlocked.Exchange(ref _attributionTicks, 0);
		Interlocked.Exchange(ref _droppedLines, 0);
		Interlocked.Exchange(ref _frameBudgetTicks, (long)(Stopwatch.Frequency * DefaultFrameBudgetMs / 1000));
		lock (PendingGate)
		{
			while (Pending.TryDequeue(out _))
			{
			}

			_pendingLines = 0;
		}
	}

	public static void Close() => SetEnabled(false);

	private static void Stop()
	{
		lock (Gate)
		{
			if (!_enabled && _session is null)
			{
				return;
			}

			_enabled = false;
			_sampleTimer?.Dispose();
			_sampleTimer = null;
			_flushTimer?.Dispose();
			_flushTimer = null;
		}

		// 先尽量把内存里的最后一批写出去；关闭阶段有上限，磁盘慢时不拖住窗口退出。
		Drain(maxLines: MaxPendingLines);
		lock (WriterGate)
		{
			_session?.Dispose();
			_session = null;
			_timingsPath = string.Empty;
		}
	}

	private static void Sample()
	{
		if (!_enabled || Interlocked.Exchange(ref _sampling, 1) != 0)
		{
			return;
		}

		try
		{
			// Refresh 会重新读 /proc，1 秒一次的代价可以接受；托管堆信息则随时可读。
			Process.Refresh();
			Interlocked.Exchange(ref _workingSetBytes, Process.WorkingSet64);
			Interlocked.Exchange(ref _privateMemoryBytes, Process.PrivateMemorySize64);
			UpdateNativeMemoryBreakdown();
			TrimIfMemoryPressure();
			var info = GC.GetGCMemoryInfo();
			Interlocked.Exchange(ref _heapBytes, info.HeapSizeBytes);
			Interlocked.Exchange(ref _fragmentedBytes, info.FragmentedBytes);
			Interlocked.Exchange(ref _committedBytes, info.TotalCommittedBytes);
			Volatile.Write(ref _pinnedObjects, (int)Math.Min(int.MaxValue, info.PinnedObjectsCount));
			Volatile.Write(ref _finalizationPending, (int)Math.Min(int.MaxValue, info.FinalizationPendingCount));
			Interlocked.Exchange(ref _allocatedBytes, GC.GetTotalAllocatedBytes(precise: false));
			Volatile.Write(ref _gen0Collections, GC.CollectionCount(0));
			Volatile.Write(ref _gen2Collections, GC.CollectionCount(2));

			// 大对象堆 / Pinned Object Heap：判断「托管堆涨的是谁」时必须分开看。
			var generations = info.GenerationInfo;
			Interlocked.Exchange(ref _lohBytes, generations.Length > 3 ? generations[3].SizeAfterBytes : 0);
			Interlocked.Exchange(ref _pohBytes, generations.Length > 4 ? generations[4].SizeAfterBytes : 0);

			// CPU 占比与线程池积压：都是每步一次的读数，1 Hz 采样下成本可以忽略。
			// Process.TotalProcessorTime 是 TimeSpan.Ticks（100 ns），不能用 Stopwatch.Frequency 换算。
			var cpuTicks = Process.TotalProcessorTime.Ticks;
			var wall = Stopwatch.GetTimestamp();
			var cpuBase = Interlocked.Exchange(ref _cpuTicksBaseline, cpuTicks);
			var wallBase = Interlocked.Exchange(ref _cpuWallBaseline, wall);
			if (cpuBase != 0L && wall > wallBase)
			{
				var percent = CpuPercentFromTicks(cpuTicks - cpuBase, wall - wallBase, Environment.ProcessorCount);
				Interlocked.Exchange(ref _cpuPercentBits, BitConverter.DoubleToInt64Bits(Math.Round(percent, 1)));
			}

			Volatile.Write(ref _poolThreads, ThreadPool.ThreadCount);
			Interlocked.Exchange(ref _poolPendingWork, ThreadPool.PendingWorkItemCount);
			if (Application.IsRunning && Application.Current.RenderLoopSettings is { } loop)
			{
				Volatile.Write(ref _loopContinuous, loop.IsContinuous ? 1 : 0);
				Volatile.Write(ref _loopVSync, loop.VSyncEnabled ? 1 : 0);
				Volatile.Write(ref _loopTargetFps, loop.TargetFps);
			}

			Volatile.Write(ref _hasSample, 1);
			// 每秒一行：窗口值与累计值都写，空闲也写，堆趋势和帧曲线才能对上。
			WriteFrameWindow();
		}
		catch (Exception ex)
		{
			AppLog.Failure(AppLog.For<Diagnostics>(), "Diagnostics memory sample", ex);
		}
		finally
		{
			Interlocked.Exchange(ref _sampling, 0);
		}

		ProbeQueueLag();
	}

	/// 分离匿名/文件/共享内存与字体映射数：工作集是 GB 级而 GC 提交只有百 MB 时，
	/// 单看 rss 无法判断是托管堆、字体映射还是 GPU/驱动内存。Linux 每 5 秒读一次 procfs。
	private static void UpdateNativeMemoryBreakdown()
	{
		if (!OperatingSystem.IsLinux())
		{
			return;
		}

		var now = Stopwatch.GetTimestamp();
		var previous = Interlocked.Read(ref _lastSmapsTicks);
		if (previous != 0 && now - previous < Stopwatch.Frequency * 5)
		{
			return;
		}

		if (Interlocked.CompareExchange(ref _lastSmapsTicks, now, previous) != previous)
		{
			return;
		}

		try
		{
			long anon = 0, file = 0, shmem = 0;
			foreach (var line in File.ReadLines("/proc/self/status"))
			{
				if (line.StartsWith("RssAnon:", StringComparison.Ordinal)) anon = Kb(line);
				else if (line.StartsWith("RssFile:", StringComparison.Ordinal)) file = Kb(line);
				else if (line.StartsWith("RssShmem:", StringComparison.Ordinal)) shmem = Kb(line);
			}

			Interlocked.Exchange(ref _rssAnonBytes, anon * 1024);
			Interlocked.Exchange(ref _rssFileBytes, file * 1024);
			Interlocked.Exchange(ref _rssShmemBytes, shmem * 1024);

			var fonts = 0;
			long fontBytes = 0, gpuBytes = 0;
			foreach (var line in File.ReadLines("/proc/self/maps"))
			{
				if (line.Contains("MewUI/FontCache", StringComparison.Ordinal))
				{
					fonts++;
					fontBytes += MapBytes(line);
				}
				else if (line.Contains("/dev/nvidiactl", StringComparison.Ordinal) ||
					line.Contains("/dev/dri/", StringComparison.Ordinal))
				{
					gpuBytes += MapBytes(line);
				}
			}
			Volatile.Write(ref _fontMapCount, fonts);
			Interlocked.Exchange(ref _fontMapBytes, fontBytes);
			Interlocked.Exchange(ref _gpuMapBytes, gpuBytes);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			// procfs 不可读时保留上一次读数；诊断本身不能影响主流程。
		}
	}

	private static long Kb(string line)
	{
		var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
		return parts.Length > 1 && long.TryParse(parts[1], out var value) ? value : 0;
	}

	private static long MapBytes(string line)
	{
		var dash = line.IndexOf('-');
		var space = line.IndexOf(' ', dash + 1);
		if (dash <= 0 || space <= dash) return 0;
		return long.TryParse(line.AsSpan(0, dash), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var start) &&
			long.TryParse(line.AsSpan(dash + 1, space - dash - 1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var end) &&
			end > start ? end - start : 0;
	}

	/// 工作集异常增大时释放可清理的渲染缓存。只处理 >2 GiB 的异常态，
	/// 正常窗口不会触发；限流 30 秒，避免与长流/大图叠加时反复抖动。
	private static void TrimIfMemoryPressure()
	{
		if (Interlocked.Read(ref _workingSetBytes) < 2L * 1024 * 1024 * 1024) return;
		if (!Application.IsRunning || Application.Current?.Dispatcher is not { } dispatcher) return;

		var now = Stopwatch.GetTimestamp();
		var previous = Interlocked.Read(ref _lastMemoryTrimTicks);
		if (previous != 0 && now - previous < Stopwatch.Frequency * 30) return;
		if (Interlocked.CompareExchange(ref _lastMemoryTrimTicks, now, previous) != previous) return;

		try
		{
			dispatcher.BeginInvoke(() =>
			{
				Application.Current?.GraphicsFactory.TextEngine.ManagedCache.Trim();
			});
		}
		catch (Exception exception)
		{
			AppLog.For<Diagnostics>().ZLogWarning($"Render cache trim skipped: {exception.Message}");
		}
	}

	/// 每秒一行的运行时摘要（<c>name="runtime.window"</c>）写进同一个 timings 文件。
	/// 窗口值（p95/dropped/long/alloc/gc）来自最近一秒，累计值单独加 <c>total.</c> 前缀；
	/// 这样文件里既有逐秒曲线，又能看出整个采集期的峰值。
	private static void WriteFrameWindow()
	{
		FrameStats frame;
		FrameWindowStats window;
		(string Name, long Value, double MaxMs)[] topCounters;
		long budgetMs;
		lock (Gate)
		{
			frame = BuildFrameStats();
			window = BuildWindowStats(Stopwatch.GetTimestamp());
			topCounters = [.. Counters.Values
				.OrderByDescending(static bucket => bucket.Value)
				.Take(4)
				.Select(static bucket => (bucket.Name, bucket.Value, bucket.Stat().MaxMs))];
			UpdateFrameBudgetLocked(frame);
			budgetMs = (long)Math.Round(TicksToMs(Interlocked.Read(ref _frameBudgetTicks)));
			Interlocked.Exchange(ref _framesLastSecondSnapshot, window.Frames);
		}

		var detail = $"frames={window.Frames} window.fps={window.Fps:0.#} p50={window.P50Ms:0.0} p95={window.P95Ms:0.0} " +
			$"max={window.MaxMs:0.0} dropped={window.DroppedFrames} long={window.LongFrames} " +
			$"stall={window.IdleGaps}/{window.MaxGapMs:0.0}ms " +
			$"alloc/frame={window.AllocBytesPerFrame:0}B gc={window.GcPausePercent:0.##}% " +
			$"total.frames={frame.Frames} total.fps={frame.Fps:0.#} total.p50={frame.P50Ms:0.0} total.p95={frame.P95Ms:0.0} " +
			$"total.max={frame.MaxMs:0.0} total.dropped={frame.DroppedFrames} total.long={frame.LongFrames} " +
			$"total.stall={frame.IdleGaps}/{frame.MaxGapMs:0.0}ms " +
			$"total.alloc/frame={frame.AllocBytesPerFrame:0}B total.gc={frame.GcPausePercent:0.##}% " +
			$"budget={budgetMs}ms " +
			$"heap={Interlocked.Read(ref _heapBytes) / (1024 * 1024)}MiB " +
			$"loh={Interlocked.Read(ref _lohBytes) / (1024 * 1024)}MiB " +
			$"poh={Interlocked.Read(ref _pohBytes) / (1024 * 1024)}MiB " +
			$"gen0={Volatile.Read(ref _gen0Collections)} gen2={Volatile.Read(ref _gen2Collections)} " +
			$"finq={Volatile.Read(ref _finalizationPending)} pinned={Volatile.Read(ref _pinnedObjects)} " +
			$"rss={Interlocked.Read(ref _workingSetBytes) / (1024 * 1024)}MiB " +
			$"priv={Interlocked.Read(ref _privateMemoryBytes) / (1024 * 1024)}MiB " +
			$"commit={Interlocked.Read(ref _committedBytes) / (1024 * 1024)}MiB " +
			$"anon={Interlocked.Read(ref _rssAnonBytes) / (1024 * 1024)}MiB " +
			$"file={Interlocked.Read(ref _rssFileBytes) / (1024 * 1024)}MiB " +
			$"shmem={Interlocked.Read(ref _rssShmemBytes) / (1024 * 1024)}MiB fonts={Volatile.Read(ref _fontMapCount)} " +
			$"fontmb={Interlocked.Read(ref _fontMapBytes) / (1024 * 1024)}MiB gpu={Interlocked.Read(ref _gpuMapBytes) / (1024 * 1024)}MiB " +
			$"draw={frame.DrawCalls} cull={frame.CulledCalls} text={frame.TextDraws} " +
			$"input/p95={frame.InputP95Ms:0.0} lag={BitsToDouble(Volatile.Read(ref _queueLagBits)):0.0} " +
			$"cpu={BitsToDouble(Volatile.Read(ref _cpuPercentBits)):0.#}% " +
			$"pending={Interlocked.Read(ref _poolPendingWork)} dropped-lines={Interlocked.Read(ref _droppedLines)}";

		// 渲染缓存台账（框架自己统计的像素/几何字节）：工作集是 GB 级时托管堆说明不了问题，
		// 必须把文本位图/几何/图集/图片/持久表面分开记，才看得出是哪一类在长。
		var resources = RenderResourceMetrics.Snapshot();
		detail += $" rtext={resources.TextCacheBytes / 1024}KiB rgeom={resources.GeometryCacheBytes / 1024}KiB " +
			$"ratlas={resources.AtlasPageCount}/{resources.AtlasPageBytes / 1024}KiB " +
			$"rpersist={resources.PersistentResourceCount}/{resources.PersistentResourceBytes / 1024}KiB " +
			$"rscratch={resources.ScratchActiveCount}a/{resources.ScratchPooledCount}p/{resources.ScratchPooledBytes / 1024}KiB " +
			$"rdecoded={resources.DecodedPixelCount}/{resources.DecodedPixelBytes / 1024}KiB " +
			$"rbitmap={resources.BitmapCacheCount}/{resources.BitmapCacheBytes / 1024}KiB " +
			$"rvector={resources.VectorCacheCount}/{resources.VectorCacheBytes / 1024}KiB " +
			$"rencoded={resources.EncodedBackingCount}/{resources.EncodedBackingBytes / 1024}KiB " +
			$"rdecode={resources.DecodeAttempts}/{resources.DecodeSucceeded}/{resources.DecodeFailed} " +
			$"rtemp={resources.DecodeTemporaryPeakBytes / 1024}KiB rpending={resources.PendingReleaseCount}/{resources.PendingReleaseBytes / 1024}KiB " +
			$"rupload={resources.UploadStagingCount}/{resources.UploadStagingBytes / 1024}KiB";
		if (topCounters.Length > 0)
		{
			detail += " counters=[" + string.Join(",", topCounters
				.Select(static counter => $"{counter.Name}:{counter.Value}")) + "]";
		}

		EnqueueLine(Serialize("runtime.window", DateTime.Now.AddSeconds(-1), SampleInterval.TotalMilliseconds, detail, "counter", Environment.CurrentManagedThreadId));
	}

	/// 中位帧间隔会被启动/动画噪声带偏：吸附到常见刷新率（144/120/90/60/30），
	/// 预算就不会一秒一变，掉帧计数才有可比性。
	internal static double SnapFrameBudget(double measuredMs)
	{
		double[] candidates = [1000.0 / 144, 1000.0 / 120, 1000.0 / 90, 1000.0 / 60, 1000.0 / 30];
		var best = DefaultFrameBudgetMs;
		var bestDistance = double.MaxValue;
		foreach (var candidate in candidates)
		{
			var distance = Math.Abs(candidate - measuredMs);
			if (distance < bestDistance)
			{
				bestDistance = distance;
				best = candidate;
			}
		}

		// 偏离所有标准刷新率超过 30%：宁可用 60 fps 默认值，也不要编出一个不存在的刷新率。
		return bestDistance <= measuredMs * 0.3 ? best : DefaultFrameBudgetMs;
	}

	private static void UpdateFrameBudgetLocked(FrameStats frame)
	{
		var target = Volatile.Read(ref _loopTargetFps);
		var budget = target > 0 ? 1000.0 / target : 0;
		// 只有落在合理刷新区间里的中位帧间隔才可信；被一次长停顿抬到 40 ms 以上时回退 60 fps，
		// 否则「预算」会跟着卡顿一起变宽，掉帧反而检测不到。
		if (budget <= 0 && Volatile.Read(ref _loopContinuous) == 1 && frame.GapP50Ms is > 4 and < 40)
		{
			budget = SnapFrameBudget(frame.GapP50Ms);
		}

		if (budget <= 0) budget = DefaultFrameBudgetMs;
		budget = Math.Clamp(budget, 4, 50);
		Interlocked.Exchange(ref _frameBudgetTicks, (long)(Stopwatch.Frequency * budget / 1000));
	}

	/// UI 队列延迟：从池线程投递一个工作项，量它被 UI 线程接手前排队了多久。
	/// 用 Input 优先级而不是默认的 Normal：Normal 比 Input/Render 都高，会插队，
	/// 测出来的不是输入/绘制真正排了多久。
	private static void ProbeQueueLag()
	{
		if (!Application.IsRunning || Application.Current?.Dispatcher is not { } dispatcher)
		{
			return;
		}

		var dispatched = Stopwatch.GetTimestamp();
		try
		{
			dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
			{
				var lag = Stopwatch.GetElapsedTime(dispatched).TotalMilliseconds;
				RecordLag(lag);
			});
		}
		catch (Exception ex)
		{
			AppLog.For<Diagnostics>().ZLogWarning($"Queue probe skipped: {ex.Message}");
		}
	}

	private static void RecordLag(double lagMs)
	{
		if (!_enabled) return;
		Interlocked.Exchange(ref _queueLagBits, BitConverter.DoubleToInt64Bits(lagMs));
		UpdateDoubleMax(ref _queueLagMaxBits, lagMs);
		if (lagMs >= LagWarnMs)
		{
			AppLog.For<Diagnostics>().ZLogWarning($"UI queue stall: {lagMs:0.0} ms");
		}
	}

	private static void UpdateDoubleMax(ref long target, double value)
	{
		var bits = BitConverter.DoubleToInt64Bits(value);
		var currentBits = Interlocked.Read(ref target);
		while (value > BitsToDouble(currentBits))
		{
			var previous = Interlocked.CompareExchange(ref target, bits, currentBits);
			if (previous == currentBits) return;
			currentBits = previous;
		}
	}

	private static double BitsToDouble(long bits) => BitConverter.Int64BitsToDouble(bits);

	private static void Flush()
	{
		if (!_enabled || Interlocked.Exchange(ref _draining, 1) != 0)
		{
			return;
		}

		try
		{
			Drain();
		}
		finally
		{
			Interlocked.Exchange(ref _draining, 0);
		}
	}

	private static void Drain(int maxLines = int.MaxValue)
	{
		var session = Volatile.Read(ref _session);
		if (session is null)
		{
			while (maxLines-- > 0)
			{
				lock (PendingGate)
				{
					if (!Pending.TryDequeue(out _)) break;
					_pendingLines--;
				}
			}

			return;
		}

		try
		{
			lock (WriterGate)
			{
				while (maxLines-- > 0)
				{
					string? line;
					lock (PendingGate)
					{
						if (!Pending.TryDequeue(out line)) break;
						_pendingLines--;
					}

					if (session.Bytes + line.Length + 1 > FileLimitBytes)
					{
						session = RotateLocked(session);
					}

					session.Writer.WriteLine(line);
					session.Bytes += line.Length + 1;
				}

				session.Writer.Flush();
			}
		}
		catch (Exception ex)
		{
			// 诊断落盘失败不该影响使用；关掉开关避免每 250 毫秒重复报错。
			AppLog.Failure(AppLog.For<Diagnostics>(), "Flush diagnostics timings", ex);
			if (_enabled)
			{
				SetEnabled(false);
			}
		}
	}

	private static void EnqueueLine(string line)
	{
		lock (PendingGate)
		{
			if (_pendingLines >= MaxPendingLines)
			{
				Interlocked.Increment(ref _droppedLines);
				return;
			}

			Pending.Enqueue(line);
			_pendingLines++;
		}
	}

	private static void AppendRecentLocked(Timing timing)
	{
		Recent[_recentCursor] = timing;
		_recentCursor = (_recentCursor + 1) % RecentSlots;
		if (_recentCount < RecentSlots) _recentCount++;
	}

	private static Timing[] SnapshotRecent(int max)
	{
		var count = Math.Min(_recentCount, max);
		var result = new Timing[count];
		for (var index = 0; index < count; index++)
		{
			var slot = (_recentCursor - 1 - index + RecentSlots * 2) % RecentSlots;
			result[index] = Recent[slot];
		}

		return result;
	}

	private static Aggregator StatsBucketLocked(string name)
	{
		if (Stats.TryGetValue(name, out var bucket))
		{
			return bucket;
		}

		if (Stats.Count >= MaxStats)
		{
			name = "other";
			if (Stats.TryGetValue(name, out bucket)) return bucket;
		}

		bucket = new Aggregator(name);
		Stats[name] = bucket;
		return bucket;
	}

	private static CounterBucket GetCounterBucket(string name)
	{
		if (Counters.Count >= MaxCounters && !Counters.ContainsKey(name))
		{
			name = "other";
		}

		return Counters.GetOrAdd(name, static key => new CounterBucket(key));
	}

	private static string? TrimDetail(string? detail) => detail is { Length: > MaxDetailChars }
		? detail[..MaxDetailChars] + "…"
		: detail;

	private static string CategoryOf(string name)
	{
		var dot = name.IndexOf('.');
		return dot > 0 ? name[..dot] : name;
	}

	private static string Serialize(
		string name,
		DateTime startedAt,
		double durationMs,
		string? detail,
		string kind,
		int threadId)
	{
		var buffer = new ArrayBufferWriter<byte>(256);
		using (var json = new Utf8JsonWriter(buffer))
		{
			json.WriteStartObject();
			json.WriteNumber("v", 1);
			json.WriteString("kind", kind);
			json.WriteString("cat", CategoryOf(name));
			json.WriteString("name", name);
			json.WriteString("startedAt", startedAt.ToString("O", CultureInfo.InvariantCulture));
			json.WriteNumber("durationMs", Math.Round(durationMs, 1));
			json.WriteNumber("pid", Environment.ProcessId);
			json.WriteNumber("tid", threadId);
			if (!string.IsNullOrEmpty(detail))
			{
				json.WriteString("detail", detail);
			}

			// 带上最近一次采样的内存值：把「这一步很慢」和「当时涨到多少」对上。
			json.WriteNumber("rssBytes", Interlocked.Read(ref _workingSetBytes));
			json.WriteNumber("heapBytes", Interlocked.Read(ref _heapBytes));
			json.WriteEndObject();
		}

		return Encoding.UTF8.GetString(buffer.WrittenSpan);
	}

	private static double Elapsed(Stopwatch watch)
	{
		watch.Stop();
		return watch.Elapsed.TotalMilliseconds;
	}

	internal ref struct Scope
	{
		private readonly string? _name;
		private readonly Stopwatch? _watch;

		internal Scope(string name)
		{
			if (!_enabled)
			{
				_name = null;
				_watch = null;
				return;
			}

			_name = name;
			_watch = Stopwatch.StartNew();
		}

		public void Dispose()
		{
			if (_name is null || _watch is null)
			{
				return;
			}

			Record(_name, _watch);
		}
	}

	/// 计数档的桶：值与累计耗时、最大值都用 Interlocked 累加，读取只取快照，
	/// 因此每帧一次的调用点不会有锁、字符串或分配。
	private sealed class CounterBucket(string name)
	{
		private long _value;
		private long _timeMicros;
		private long _maxMicros;

		public string Name { get; } = name;

		public void Add(long delta) => Interlocked.Add(ref _value, delta);

		public void AddTime(long microseconds)
		{
			if (microseconds < 0) return;
			Interlocked.Add(ref _timeMicros, microseconds);
			UpdateMax(ref _maxMicros, microseconds);
		}

		public long Value => Interlocked.Read(ref _value);

		public CounterStat Stat() => new(Name, Value, Interlocked.Read(ref _timeMicros) / 1000.0, Interlocked.Read(ref _maxMicros) / 1000.0);
	}

	/// 明细档的聚合：保留最近 256 个样本，读取时算 p50/p95/p99。
	/// 所有访问都在全局 Gate 内，不再需要每桶一把锁。
	private sealed class Aggregator(string name)
	{
		private const int Capacity = 256;
		private readonly double[] _samples = new double[Capacity];
		private int _cursor;
		private int _count;
		private int _totalCount;
		private double _total;
		private double _max;
		private double _last;

		public void Add(double durationMs)
		{
			_samples[_cursor] = durationMs;
			_cursor = (_cursor + 1) % Capacity;
			if (_count < Capacity) _count++;
			_totalCount++;
			_total += durationMs;
			_last = durationMs;
			if (durationMs > _max) _max = durationMs;
		}

		public Stat Stat()
		{
			if (_count == 0)
			{
				return new Stat(name, 0, 0, 0, 0, 0, 0, 0);
			}

			var samples = new double[_count];
			Array.Copy(_samples, samples, _count);
			Array.Sort(samples);
			return new Stat(
				name,
				_totalCount,
				_total,
				_max,
				_last,
				Quantile(samples, 0.5),
				Quantile(samples, 0.95),
				Quantile(samples, 0.99));
		}


		private static double Quantile(double[] samples, double quantile) =>
			samples[Math.Clamp((int)Math.Round(quantile * (samples.Length - 1)), 0, samples.Length - 1)];
	}
}
