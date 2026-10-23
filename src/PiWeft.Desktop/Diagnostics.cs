using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Aprillz.MewUI;
using Aprillz.MewUI.Rendering;
using PiWeft.Cli;
using PiWeft.CodingAgent.Session;
using ZLogger;

namespace PiWeft.Desktop;

/// <summary>
/// 性能诊断采样（对齐 PiDeck 设置里的「性能诊断」）：默认关闭，关闭时调用点只做一次布尔判断，
/// 不分配、不落盘。采样分三档，成本依次递增，热路径只走前两档：
/// <list type="bullet">
/// <item><description>帧档：<see cref="ObserveFrame"/> 每帧写两个定长槽位与几个 Interlocked 计数，
/// 一帧约百纳秒、零分配，给出帧时间分布、掉帧/长帧、每帧分配、GC 暂停占比、输入→绘制延迟与绘制量；</description></item>
/// <item><description>计数档：<see cref="Count"/> 只累加事件次数（行 realize/回收、重建轮次等），
/// 同样零分配；<see cref="Slow"/> 是「计数 + 只在超过阈值时才落明细」的组合；</description></item>
/// <item><description>明细档：<see cref="Record(string,double,string?)"/> 每次约 2 µs 与近 1 KB 垃圾，
/// 因此只给低频粗路径（启动、打开会话、投影、定稿）与慢于阈值的帧使用。</description></item>
/// </list>
/// 打开后每秒采一次进程内存/CPU 与 UI 队列延迟，明细逐条写入
/// <c>&lt;agentDir&gt;/diagnostics/timings-*.jsonl</c>，并按名称聚合次数/总耗时/最大值，
/// 供「优化前后对照」与「谁把 UI 线程占住了」这类排查使用。
/// </summary>
internal sealed class Diagnostics
{
	/// <summary>单条耗时记录。</summary>
	internal readonly record struct Timing(string Name, DateTime StartedAt, double DurationMs, string? Detail);

	/// <summary>按名称聚合的耗时统计。</summary>
	internal readonly record struct Stat(string Name, int Count, double TotalMs, double MaxMs, double LastMs);

	/// <summary>零分配的事件计数；<paramref name="TotalMs"/> 只在 <see cref="Slow"/> 路径上有值。</summary>
	internal readonly record struct CounterStat(string Name, long Value, double TotalMs);

	/// <summary>
	/// 帧档聚合：全部来自定长环形缓冲与 Interlocked 计数，读取时才算分位数，
	/// 因此每帧的成本与是否显示面板无关。
	/// </summary>
	internal readonly record struct FrameStats(
		long Frames,
		/// <summary>由帧间隔中位数换算的典型 FPS（不是算术平均的倒数）。</summary>
		double Fps,
		double LastMs,
		double AvgMs,
		double P50Ms,
		double P95Ms,
		double MaxMs,
		long DroppedFrames,
		long LongFrames,
		/// <summary>最近若干帧的间隔（毫秒，旧→新），给面板画帧时间条；正常滚动下不会超过 64 个。</summary>
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

	/// <summary>一次采样的快照：内存、CPU、UI 队列延迟、帧档与累计统计。</summary>
	internal sealed record Snapshot(
		bool Enabled,
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
		long LargeObjectHeapBytes,
		FrameStats Frame,
		string? TimingsPath,
		string DirectoryPath,
		IReadOnlyList<Timing> Recent,
		IReadOnlyList<Stat> Stats,
		IReadOnlyList<CounterStat> Counters);

	/// <summary>队列延迟超过这个值就记一条警告：UI 线程被什么东西占住了。</summary>
	internal const double LagWarnMs = 50;

	/// <summary>耗时超过这个值的路径按警告记录，方便在日志里直接看到慢路径。</summary>
	private const double SlowPathMs = 200;

	// 帧档的环形缓冲必须是 2 的幂：热路径用 & 取模，不做除法也不取余。
	private const int FrameSlots = 2048;
	/// <summary>面板帧时间条的样本数：够看出一次掉帧风暴，又不至于把面板画成噪声。</summary>
	private const int SampleSlots = 64;
	private const int InputSlots = 512;
	/// <summary>一帧超过这个时长算掉帧：60 FPS 预算的 1.5 倍。</summary>
	private const double DroppedFrameMs = 20;
	/// <summary>一帧超过这个时长算长帧：读者能明确感到卡顿。</summary>
	internal const double LongFrameReportMs = 50;

	private const double LongFrameMs = LongFrameReportMs;
	/// <summary>
	/// 两帧之间超过这个时长就不算帧样本：渲染循环是按请求跑的，空闲后的第一次重绘
	/// 只说明「这段时间没人要求重绘」，把它当成帧时间会让 p95 与 GC 占比都失真。
	/// 它单独记成「停顿」，因为那确实可能是 UI 被占住。
	/// </summary>
	private const double IdleGapMs = 400;
	/// <summary>输入到绘制超过这个时长就不再算样本：那是空闲后的一次重绘，不是延迟。</summary>
	private const double InputWindowMs = 1000;
	/// <summary>长帧日志的限流窗口：一秒最多一条，够定位又不刷屏。</summary>
	private static readonly long JankWarnIntervalTicks = Stopwatch.Frequency;

	private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(1);
	private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(250);
	private static readonly Lock Gate = new();
	private static readonly ConcurrentQueue<string> Pending = new();
	private static readonly Dictionary<string, Aggregator> Stats = new(StringComparer.Ordinal);
	private static readonly List<Timing> Recent = [];
	private static readonly Process Process = Process.GetCurrentProcess();

	private static Timer? _sampleTimer;
	private static Timer? _flushTimer;
	private static StreamWriter? _writer;
	private static volatile bool _enabled;
	private static string _directory = string.Empty;
	private static string? _timingsPath;

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
	private static long _framesReportedBaseline;
	private static long _lohBytes;
	private static long _framesLastSecond;
	private static int _loopContinuous;
	private static int _loopVSync;
	private static int _loopTargetFps;
	private static int _pinnedObjects;
	private static int _finalizationPending;
	private static int _draining;

	// ── 帧档：定长环形缓冲 + Interlocked，热路径不分配、不加锁、不碰字符串 ──
	private static readonly long[] FrameDurations = new long[FrameSlots];
	private static readonly long[] FrameAllocations = new long[FrameSlots];
	private static readonly long[] InputLatencies = new long[InputSlots];
	private static readonly ConcurrentDictionary<string, CounterBucket> Counters = new(StringComparer.Ordinal);
	// 分位数只在读快照时算，因此共用一块排序缓冲（快照在 Gate 内，单线程访问）。
	private static readonly long[] Scratch = new long[FrameSlots];

	private static int _frameCursor;
	private static long _frameLastTicks;
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
	private static int _inputCursor;
	private static long _inputPendingTicks;
	private static long _inputCount;
	private static long _jankWarnTicks;
	private static long _windowFrameBaseline;
	private static int _frameDrawCalls;
	private static int _frameCullCount;
	private static int _frameTextDraws;
	private static int _frameShapeDraws;
	private static int _frameImageDraws;
	private static int _frameClipCount;
	private static long _cpuTicksBaseline;
	private static long _cpuWallBaseline;

	/// <summary>诊断数据目录：<c>&lt;agentDir&gt;/diagnostics</c>，与偏好文件同级，便于一起备份。</summary>
	public static string DirectoryPath(string? agentDir = null) =>
		Path.Combine(string.IsNullOrWhiteSpace(agentDir) ? SessionPaths.GetAgentDir() : agentDir!, "diagnostics");

	/// <summary>采样是否开着。调用点用它做零成本短路。</summary>
	public static bool Enabled => _enabled;

	/// <summary>
	/// 按设置开关启停。重复设置同值不会重开文件，避免设置页抖一下就把日志切成两份。
	/// </summary>
	public static void SetEnabled(bool enabled, string? agentDir = null)
	{
		lock (Gate)
		{
			if (enabled == _enabled)
			{
				return;
			}

			if (!enabled)
			{
				Stop();
				return;
			}

			_directory = DirectoryPath(agentDir);
			Directory.CreateDirectory(_directory);
			var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
			_timingsPath = Path.Combine(_directory, $"timings-{stamp}.jsonl");
			// 顺序追加写：64 KB 缓冲 + 显式 Flush，避免每条耗时都撞一次系统调用。
			var stream = new FileStream(_timingsPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
			_writer = new StreamWriter(stream, new UTF8Encoding(false), 64 * 1024);
			_enabled = true;
			// 一次开启对应一份新的 timings 文件：内存里的聚合也必须一起清零，
			// 否则上一轮的慢路径会混进这一轮的对照里。
			Stats.Clear();
			Recent.Clear();
			ResetCounters();
			_sampleTimer = new Timer(_ => Sample(), null, SampleInterval, SampleInterval);
			_flushTimer = new Timer(_ => Flush(), null, FlushInterval, FlushInterval);
		}
	}

	/// <summary>记录一段耗时。<paramref name="detail"/> 是自由文本（键=值 形式），一起进 JSONL。</summary>
	public static void Record(string name, double durationMs, string? detail = null)
	{
		if (!_enabled)
		{
			return;
		}

		var startedAt = DateTime.Now.AddMilliseconds(-durationMs);
		lock (Gate)
		{
			if (!Stats.TryGetValue(name, out var bucket))
			{
				bucket = new Aggregator(name);
				Stats[name] = bucket;
			}

			bucket.Add(durationMs);
			Recent.Insert(0, new Timing(name, startedAt, durationMs, detail));
			// 只留最近这么多条给设置页展示；文件里是全量。
			if (Recent.Count > 200)
			{
				Recent.RemoveRange(80, Recent.Count - 80);
			}
		}

		var line = Serialize(name, startedAt, durationMs, detail);
		Pending.Enqueue(line);
		if (durationMs >= SlowPathMs)
		{
			AppLog.For<Diagnostics>().ZLogWarning($"Slow path: {name} {durationMs:0.0} ms {detail}");
		}
	}

	/// <summary>记录一段耗时：把正在走的 <paramref name="watch"/> 停下并读毫秒，少一次样板代码。</summary>
	public static void Record(string name, Stopwatch watch, string? detail = null)
	{
		if (!_enabled)
		{
			watch.Stop();
			return;
		}

		Record(name, Elapsed(watch), detail);
	}

	/// <summary>
	/// 每帧一次（接 <see cref="Window.FrameRendered"/>）：只写两个定长槽位和几个 Interlocked 计数。
	/// 关闭时是一次 volatile 判断；开着时一帧约百纳秒、零分配，所以可以常驻而不必担心测出「观测者效应」。
	/// <paramref name="stats"/> 来自 <see cref="Window.LastFrameStats"/>，框架本来就每帧无条件填好。
	/// </summary>
	public static void ObserveFrame(in RenderStats stats)
	{
		if (!_enabled)
		{
			return;
		}

		var now = Stopwatch.GetTimestamp();
		var previous = Interlocked.Exchange(ref _frameLastTicks, now);
		var duration = previous == 0L ? 0L : now - previous;
		var idle = duration > 0L && TicksToMs(duration) > IdleGapMs;
		if (idle)
		{
			Interlocked.Increment(ref _idleGaps);
			if (duration > Interlocked.Read(ref _maxGapTicks))
			{
				Interlocked.Exchange(ref _maxGapTicks, duration);
			}
		}

		var slot = Interlocked.Increment(ref _frameCursor) & (FrameSlots - 1);
		// 只把「有间隔、且不算空闲」的帧记进样本：第一帧没有前帧可比，空闲间隔另有归属。
		var sampled = !idle && duration > 0L;
		Volatile.Write(ref FrameDurations[slot], sampled ? duration : 0L);
		if (sampled)
		{
			Interlocked.Add(ref _frameTotalTicks, duration);
			Interlocked.Increment(ref _frameCount);
		}

		if (sampled)
		{
			var ms = TicksToMs(duration);
			if (ms >= LongFrameMs)
			{
				Interlocked.Increment(ref _longFrames);
				// 长帧值得单独出现在日志里：它是「哪一刻卡住」的唯一确定信号，
				// 但限流后再写，避免一次掉帧风暴刷满日志。
				var previousWarn = Interlocked.Read(ref _jankWarnTicks);
				if (now - previousWarn >= JankWarnIntervalTicks &&
					Interlocked.CompareExchange(ref _jankWarnTicks, now, previousWarn) == previousWarn)
				{
					AppLog.For<Diagnostics>().ZLogWarning(
						$"Long frame: {ms:0.0} ms (dropped {Interlocked.Read(ref _droppedFrames)}, " +
						$"long {Interlocked.Read(ref _longFrames)}, queue lag {BitsToDouble(Volatile.Read(ref _queueLagMaxBits)):0.0} ms)");
				}
			}
			else if (ms >= DroppedFrameMs)
			{
				Interlocked.Increment(ref _droppedFrames);
			}
		}

		// 每帧分配与 GC 暂停都只取差值：这两个读数本身就是纳秒级，且不采样堆。
		// 分配与 GC 暂停的基线每帧都要走一遍，但空闲间隔里的量不算进帧均值。
		var allocated = GC.GetAllocatedBytesForCurrentThread();
		var allocBase = Interlocked.Exchange(ref _frameAllocBaseline, allocated);
		var allocDelta = allocBase == 0L || idle ? 0L : allocated - allocBase;
		Volatile.Write(ref FrameAllocations[slot], allocDelta);
		Interlocked.Add(ref _frameAllocTotal, allocDelta);

		var pause = GC.GetTotalPauseDuration().Ticks;
		var pauseBase = Interlocked.Exchange(ref _gcPauseBaselineTicks, pause);
		if (pauseBase != 0L)
		{
			Interlocked.Add(ref _gcPauseTotalTicks, pause - pauseBase);
		}

		// 输入→绘制：把上一次按键/指针事件到这一帧的时间记进小环形缓冲。
		// 空闲很久之后的第一次重绘不算样本，否则面板会显示一个荒谬的「延迟」。
		var pending = Interlocked.Exchange(ref _inputPendingTicks, 0L);
		if (pending != 0L)
		{
			var latency = now - pending;
			if (TicksToMs(latency) <= InputWindowMs)
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

	/// <summary>记一次输入事件的时间戳；下一帧渲染时它变成「输入→绘制」延迟样本。</summary>
	public static void NoteInput()
	{
		if (_enabled)
		{
			Interlocked.Exchange(ref _inputPendingTicks, Stopwatch.GetTimestamp());
		}
	}

	/// <summary>计数档：只累加事件次数或量值，不记时间、不产生字符串。</summary>
	public static void Count(string name, long delta = 1)
	{
		if (!_enabled)
		{
			return;
		}

		Counters.GetOrAdd(name, static key => new CounterBucket(key)).Add(delta);
	}

	/// <summary>
	/// 热路径用的组合档：先记一次计数，只有超过 <paramref name="thresholdMs"/> 才落明细。
	/// 每帧都会发生的路径用它，否则诊断本身就成了每秒上百 KB 的垃圾来源。
	/// </summary>
	public static void Slow(string name, double durationMs, double thresholdMs, string? detail = null)
	{
		if (!_enabled)
		{
			return;
		}

		var bucket = Counters.GetOrAdd(name, static key => new CounterBucket(key));
		bucket.Add(1);
		// 累计耗时包含每一次调用：计数行因此能说明「这条热路径总共花了多少」，
		// 而明细只保留慢于阈值的样本，用于定位是哪一帧。
		bucket.AddTime((long)Math.Round(durationMs * 1000));
		if (durationMs >= thresholdMs)
		{
			Record(name, durationMs, detail);
		}
	}

	/// <summary><see cref="Slow(string,double,double,string?)"/> 的秒表版本：关闭时只停下秒表。</summary>
	public static void Slow(string name, Stopwatch watch, double thresholdMs, string? detail = null)
	{
		if (!_enabled)
		{
			watch.Stop();
			return;
		}

		Slow(name, Elapsed(watch), thresholdMs, detail);
	}

	/// <summary>
	/// 采样窗口内的一次性作用域：<c>using (Diagnostics.Track("session.open.read"))</c>。
	/// 关闭时返回一个什么都不做的句柄，连字符串拼接都不会发生。
	/// </summary>
	public static Scope Track(string name) => new(name);

	/// <summary>
	/// 主动压一次堆：返回回收前后托管堆的字节数。判断「150 MiB 是不是泄漏」只有这条路径说得准 ——
	/// 差值稳定说明是活的缓存，剩得越来越多才是泄漏。会阻塞当前线程，所以只挂在面板按钮上。
	/// </summary>
	public static (long BeforeBytes, long AfterBytes, double PauseMs) Compact()
	{
		var started = Stopwatch.GetTimestamp();
		var before = GC.GetTotalMemory(forceFullCollection: false);
		var after = GC.GetTotalMemory(forceFullCollection: true);
		return (before, after, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
	}

	/// <summary>取一次快照：内存/CPU/队列延迟读最近一次采样值，帧档从环形缓冲现算分位数。</summary>
	public static Snapshot TakeSnapshot()
	{
		lock (Gate)
		{
			return new Snapshot(
				_enabled,
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
				Interlocked.Read(ref _framesLastSecond),
				Volatile.Read(ref _loopContinuous) == 1,
				Volatile.Read(ref _loopVSync) == 1,
				Volatile.Read(ref _loopTargetFps),
				Interlocked.Read(ref _lohBytes),
				BuildFrameStats(),
				_timingsPath,
				_directory,
				[.. Recent.Take(40)],
				[.. Stats.Values.Select(static bucket => bucket.Stat()).OrderByDescending(static stat => stat.TotalMs)],
				[.. Counters.Values.Select(static bucket => bucket.Stat()).OrderByDescending(static counter => counter.Value)]);
		}
	}

	/// <summary>
	/// 帧档汇总：分位数只在读面板时算一次（拷贝到排序缓冲再取），
	/// 因此热路径永远只需要写一个槽位。
	/// </summary>
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
		var average = TicksToMs(totalTicks / frames);
		var median = QuantileMs(samples, 0.5);
		// FPS 取「典型帧」而不是算术平均：一两帧 380 ms 的建树就会把平均值压到个位数，
		// 那是长帧指标该说的话，不该混进 FPS。
		var fps = median > 0 ? Math.Round(1000 / median, 1) : 0;
		var last = Volatile.Read(ref FrameDurations[(Volatile.Read(ref _frameCursor) - 1) & (FrameSlots - 1)]);
		var pauseTicks = Interlocked.Read(ref _gcPauseTotalTicks);
		var allocTotal = Interlocked.Read(ref _frameAllocTotal);

		var inputSamples = 0;
		if (inputs > 0L)
		{
			// 复用同一块排序缓冲：快照在 Gate 内，单线程访问。
			for (var index = 0; index < InputSlots; index++)
			{
				var latency = Volatile.Read(ref InputLatencies[index]);
				if (latency > 0L)
				{
					Scratch[inputSamples++] = latency;
				}
			}

			Array.Sort(Scratch, 0, inputSamples);
		}

		// 面板画帧时间条用：取最近 64 个帧间隔（旧→新）。快照在 Gate 内，一次性拷贝。
		var history = new List<double>(SampleSlots);
		var newest = Volatile.Read(ref _frameCursor);
		for (var offset = SampleSlots - 1; offset >= 0; offset--)
		{
			var duration = Volatile.Read(ref FrameDurations[(newest - offset) & (FrameSlots - 1)]);
			if (duration > 0L)
			{
				history.Add(Math.Round(TicksToMs(duration), 1));
			}
		}

		return new FrameStats(
			frames,
			fps,
			TicksToMs(last),
			average,
			median,
			QuantileMs(samples, 0.95),
			samples > 0 ? TicksToMs(Scratch[samples - 1]) : 0,
			Interlocked.Read(ref _droppedFrames),
			Interlocked.Read(ref _longFrames),
			history,
			Interlocked.Read(ref _idleGaps),
			TicksToMs(Interlocked.Read(ref _maxGapTicks)),
			frames > 0 ? (double)allocTotal / frames : 0,
			totalTicks > 0 ? Math.Round(pauseTicks * 100.0 / totalTicks, 2) : 0,
			inputs,
			QuantileMs(inputSamples, 0.95),
			inputSamples > 0 ? TicksToMs(Scratch[inputSamples - 1]) : 0,
			Volatile.Read(ref _frameDrawCalls),
			Volatile.Read(ref _frameCullCount),
			Volatile.Read(ref _frameTextDraws),
			Volatile.Read(ref _frameShapeDraws),
			Volatile.Read(ref _frameImageDraws),
			Volatile.Read(ref _frameClipCount));
	}

	/// <summary>排序缓冲里取分位数（最近邻）；没有样本时是 0。</summary>
	private static double QuantileMs(int samples, double quantile) =>
		samples <= 0 ? 0 : TicksToMs(Scratch[Math.Clamp((int)Math.Round(quantile * (samples - 1)), 0, samples - 1)]);

	private static double TicksToMs(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

	/// <summary>清空累计统计与文件队列（设置页的「重新开始」用，避免旧数据干扰对照）。</summary>
	public static void Reset()
	{
		lock (Gate)
		{
			Stats.Clear();
			Recent.Clear();
			ResetCounters();
		}

		while (Pending.TryDequeue(out _))
		{
		}
	}

	/// <summary>退出前收摊：落盘并释放文件句柄。</summary>
	public static void Close() => SetEnabled(false);

	private static void Stop()
	{
		_enabled = false;
		_sampleTimer?.Dispose();
		_sampleTimer = null;
		_flushTimer?.Dispose();
		_flushTimer = null;
		Drain();
		_writer?.Dispose();
		_writer = null;
		_timingsPath = null;
	}

	private static void ResetCounters()
	{
		Interlocked.Exchange(ref _queueLagBits, 0);
		Interlocked.Exchange(ref _queueLagMaxBits, 0);
		Interlocked.Exchange(ref _cpuPercentBits, 0);
		Interlocked.Exchange(ref _cpuTicksBaseline, 0);
		Interlocked.Exchange(ref _cpuWallBaseline, 0);
		Interlocked.Exchange(ref _poolPendingWork, 0);
		Interlocked.Exchange(ref _lohBytes, 0);
		Interlocked.Exchange(ref _framesLastSecond, 0);
		Interlocked.Exchange(ref _framesReportedBaseline, 0);
		Array.Clear(FrameDurations);
		Array.Clear(FrameAllocations);
		Array.Clear(InputLatencies);
		_frameCursor = 0;
		_inputCursor = 0;
		Interlocked.Exchange(ref _frameLastTicks, 0);
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
		Interlocked.Exchange(ref _windowFrameBaseline, 0);
		Counters.Clear();
	}

	/// <summary>每秒一次：内存 + UI 队列延迟。都在池线程执行，绝不占用 UI 线程。</summary>
	private static void Sample()
	{
		if (!_enabled)
		{
			return;
		}

		try
		{
			// Refresh 会重新读 /proc，1 秒一次的代价可以接受；托管堆信息则随时可读。
			Process.Refresh();
			Interlocked.Exchange(ref _workingSetBytes, Process.WorkingSet64);
			Interlocked.Exchange(ref _privateMemoryBytes, Process.PrivateMemorySize64);
			var info = GC.GetGCMemoryInfo();
			Interlocked.Exchange(ref _heapBytes, info.HeapSizeBytes);
			Interlocked.Exchange(ref _fragmentedBytes, info.FragmentedBytes);
			Interlocked.Exchange(ref _committedBytes, info.TotalCommittedBytes);
			Volatile.Write(ref _pinnedObjects, (int)Math.Min(int.MaxValue, info.PinnedObjectsCount));
			Volatile.Write(ref _finalizationPending, (int)Math.Min(int.MaxValue, info.FinalizationPendingCount));
			Interlocked.Exchange(ref _allocatedBytes, GC.GetTotalAllocatedBytes(precise: false));
			Volatile.Write(ref _gen0Collections, GC.CollectionCount(0));
			Volatile.Write(ref _gen2Collections, GC.CollectionCount(2));

			// CPU 占比与线程池积压：都是每步一次的读数，1 Hz 采样下成本可以忽略。
			var cpuTicks = Process.TotalProcessorTime.Ticks;
			var wall = Stopwatch.GetTimestamp();
			var cpuBase = Interlocked.Exchange(ref _cpuTicksBaseline, cpuTicks);
			var wallBase = Interlocked.Exchange(ref _cpuWallBaseline, wall);
			if (cpuBase != 0L && wall > wallBase)
			{
				var busyMs = (cpuTicks - cpuBase) / (Stopwatch.Frequency / 1000.0);
				var spanMs = TicksToMs(wall - wallBase);
				var percent = spanMs > 0 ? busyMs / spanMs * 100 / Environment.ProcessorCount : 0;
				Interlocked.Exchange(ref _cpuPercentBits, BitConverter.DoubleToInt64Bits(Math.Round(Math.Clamp(percent, 0, 100), 1)));
			}

			Volatile.Write(ref _poolThreads, ThreadPool.ThreadCount);
			Interlocked.Exchange(ref _poolPendingWork, ThreadPool.PendingWorkItemCount);
			// 大对象堆与代大小：判断「托管堆涨的是谁」时必须分开看，否则 150 MiB 说明不了问题。
			var large = 0L;
			foreach (var generation in info.GenerationInfo)
			{
				large += generation.SizeAfterBytes;
			}

			var generations = GC.GetGCMemoryInfo().GenerationInfo;
			if (generations.Length > 3)
			{
				large = generations[3].SizeAfterBytes;
			}

			Interlocked.Exchange(ref _lohBytes, large);
			Interlocked.Exchange(ref _framesLastSecond,
				Interlocked.Read(ref _frameCount) - Interlocked.Read(ref _framesReportedBaseline));
			Interlocked.Exchange(ref _framesReportedBaseline, Interlocked.Read(ref _frameCount));
			if (Application.IsRunning && Application.Current.RenderLoopSettings is { } loop)
			{
				Volatile.Write(ref _loopContinuous, loop.IsContinuous ? 1 : 0);
				Volatile.Write(ref _loopVSync, loop.VSyncEnabled ? 1 : 0);
				Volatile.Write(ref _loopTargetFps, loop.TargetFps);
			}

			// 每秒一行的运行时摘要按秒落盘（空闲也写）：热路径仍然只是每帧一个槽位，
			// 文件里却有逐秒的堆与帧曲线可对照 —— 「越来越大的堆是不是泄漏」只能靠趋势回答。
			WriteFrameWindow();
		}
		catch (Exception ex)
		{
			AppLog.Failure(AppLog.For<Diagnostics>(), "Diagnostics memory sample", ex);
		}

		ProbeQueueLag();
	}

	/// <summary>
	/// 每秒一行的运行时摘要（<c>name="runtime.window"</c>）写进同一个 timings 文件：
	/// 「优化前后对照」和「堆是不是在长」要的是逐秒曲线，而不是每帧明细；空闲帧也照样写一行，
	/// 否则一段没有重绘的时间里看不出堆的变化。明细档只有慢样本会落盘。
	/// </summary>
	private static void WriteFrameWindow()
	{
		Diagnostics.FrameStats frame;
		long frames;
		lock (Gate)
		{
			frame = BuildFrameStats();
			frames = frame.Frames - Interlocked.Exchange(ref _windowFrameBaseline, frame.Frames);
			var detail = $"frames={frames} fps={frame.Fps:0.#} p50={frame.P50Ms:0.0} p95={frame.P95Ms:0.0} " +
				$"max={frame.MaxMs:0.0} dropped={frame.DroppedFrames} long={frame.LongFrames} " +
				$"stall={frame.IdleGaps}/{frame.MaxGapMs:0.0}ms " +
				$"alloc/frame={frame.AllocBytesPerFrame:0}B gc={frame.GcPausePercent:0.##}% " +
				$"heap={Interlocked.Read(ref _heapBytes) / (1024 * 1024)}MiB " +
				$"loh={Interlocked.Read(ref _lohBytes) / (1024 * 1024)}MiB " +
				$"gen0={Volatile.Read(ref _gen0Collections)} gen2={Volatile.Read(ref _gen2Collections)} " +
				$"finq={Volatile.Read(ref _finalizationPending)} pinned={Volatile.Read(ref _pinnedObjects)} " +
				$"rss={Interlocked.Read(ref _workingSetBytes) / (1024 * 1024)}MiB " +
				$"draw={frame.DrawCalls} cull={frame.CulledCalls} text={frame.TextDraws} " +
				$"input/p95={frame.InputP95Ms:0.0} lag={BitsToDouble(Volatile.Read(ref _queueLagBits)):0.0} " +
				$"cpu={BitsToDouble(Volatile.Read(ref _cpuPercentBits)):0.#}% " +
				$"pending={Interlocked.Read(ref _poolPendingWork)}";
			if (Counters.Count > 0)
			{
				detail += " counters=[" + string.Join(",", Counters.Values
					.OrderByDescending(static bucket => bucket.Value)
					.Take(4)
					.Select(static bucket => $"{bucket.Name}:{bucket.Value}")) + "]";
			}

			Pending.Enqueue(Serialize("runtime.window", DateTime.Now.AddSeconds(-1), frame.P95Ms, detail));
		}
	}

	/// <summary>
	/// UI 队列延迟：从池线程投递一个工作项，量它被 UI 线程接手前排队了多久。
	/// 这就是 PiDeck 里 setImmediate 往返的等价物 —— 流式渲染把 UI 线程占住时，这个值会直接抬头。
	/// </summary>
	private static void ProbeQueueLag()
	{
		// 窗口正在关闭时投递会失败或永远不执行：丢掉这一次探针即可，诊断不该影响退出。
		if (!Application.IsRunning || Application.Current?.Dispatcher is not { } dispatcher)
		{
			return;
		}

		var dispatched = Stopwatch.GetTimestamp();
		try
		{
			dispatcher.BeginInvoke(() =>
			{
				var lag = Stopwatch.GetElapsedTime(dispatched).TotalMilliseconds;
				RecordLag(lag);
			});
		}
		catch (Exception ex)
		{
			// 窗口正在关闭时投递会失败：丢掉这一次探针即可，诊断不该影响退出。
			AppLog.For<Diagnostics>().ZLogWarning($"Queue probe skipped: {ex.Message}");
		}
	}

	private static void RecordLag(double lagMs)
	{
		Interlocked.Exchange(ref _queueLagBits, BitConverter.DoubleToInt64Bits(lagMs));
		if (lagMs > BitsToDouble(Interlocked.Read(ref _queueLagMaxBits)))
		{
			Interlocked.Exchange(ref _queueLagMaxBits, BitConverter.DoubleToInt64Bits(lagMs));
		}

		if (lagMs >= LagWarnMs)
		{
			AppLog.For<Diagnostics>().ZLogWarning($"UI queue stall: {lagMs:0.0} ms");
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

	private static void Drain()
	{
		var writer = _writer;
		if (writer is null)
		{
			while (Pending.TryDequeue(out _))
			{
			}

			return;
		}

		try
		{
			while (Pending.TryDequeue(out var line))
			{
				writer.WriteLine(line);
			}

			writer.Flush();
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

	private static string Serialize(string name, DateTime startedAt, double durationMs, string? detail)
	{
		var buffer = new ArrayBufferWriter<byte>(256);
		using (var json = new Utf8JsonWriter(buffer))
		{
			json.WriteStartObject();
			json.WriteString("name", name);
			json.WriteString("startedAt", startedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
			json.WriteNumber("durationMs", Math.Round(durationMs, 1));
			if (!string.IsNullOrEmpty(detail))
			{
				json.WriteString("detail", detail);
			}

			// 带上最近一次采样的内存值：把「这一步很慢」和「当时涨到多少」对上，才看得出是分配压力还是布局抖动。
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

	/// <summary><see cref="Track"/> 的作用域句柄。</summary>
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

	/// <summary>
	/// 计数档的桶：值与累计耗时都用 Interlocked 累加，读取只取快照，
	/// 因此每帧一次的调用点不会有锁、字符串或分配。
	/// </summary>
	private sealed class CounterBucket(string name)
	{
		private long _value;
		private long _timeMicros;

		public string Name { get; } = name;

		public void Add(long delta) => Interlocked.Add(ref _value, delta);

		public void AddTime(long microseconds) => Interlocked.Add(ref _timeMicros, microseconds);

		public long Value => Interlocked.Read(ref _value);

		public CounterStat Stat() => new(Name, Value, Interlocked.Read(ref _timeMicros) / 1000.0);
	}

	private sealed class Aggregator(string name)
	{
		private readonly Lock _gate = new();
		private int _count;
		private double _total;
		private double _max;
		private double _last;

		public void Add(double durationMs)
		{
			lock (_gate)
			{
				_count++;
				_total += durationMs;
				_last = durationMs;
				if (durationMs > _max)
				{
					_max = durationMs;
				}
			}
		}

		public Stat Stat()
		{
			lock (_gate)
			{
				return new Stat(name, _count, _total, _max, _last);
			}
		}
	}
}
