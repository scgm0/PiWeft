using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Aprillz.MewUI;
using PiWeft.Cli;
using PiWeft.CodingAgent.Session;
using ZLogger;

namespace PiWeft.Desktop;

/// <summary>
/// 性能诊断采样（对齐 PiDeck 设置里的「性能诊断」）：默认关闭，关闭时调用点只做一次布尔判断，
/// 不分配、不落盘。打开后按秒采样进程内存与 UI 队列延迟，并把关键路径耗时逐条写入
/// <c>&lt;agentDir&gt;/diagnostics/timings-*.jsonl</c>，同时按名称聚合次数/总耗时/最大值，
/// 供「优化前后对照」与「谁把 UI 线程占住了」这类排查使用。
/// </summary>
internal sealed class Diagnostics
{
	/// <summary>单条耗时记录。</summary>
	internal readonly record struct Timing(string Name, DateTime StartedAt, double DurationMs, string? Detail);

	/// <summary>按名称聚合的耗时统计。</summary>
	internal readonly record struct Stat(string Name, int Count, double TotalMs, double MaxMs, double LastMs);

	/// <summary>一次采样的快照：内存、UI 队列延迟与累计统计。</summary>
	internal sealed record Snapshot(
		bool Enabled,
		DateTimeOffset SampledAt,
		long WorkingSetBytes,
		long PrivateMemoryBytes,
		long HeapBytes,
		long AllocatedBytes,
		long FragmentedBytes,
		int Gen0Collections,
		int Gen2Collections,
		double QueueLagMs,
		double QueueLagMaxMs,
		string? TimingsPath,
		string DirectoryPath,
		IReadOnlyList<Timing> Recent,
		IReadOnlyList<Stat> Stats);

	/// <summary>队列延迟超过这个值就记一条警告：UI 线程被什么东西占住了。</summary>
	private const double LagWarnMs = 50;

	/// <summary>耗时超过这个值的路径按警告记录，方便在日志里直接看到慢路径。</summary>
	private const double SlowPathMs = 200;

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
	private static int _draining;

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
	/// 采样窗口内的一次性作用域：<c>using (Diagnostics.Track("session.open.read"))</c>。
	/// 关闭时返回一个什么都不做的句柄，连字符串拼接都不会发生。
	/// </summary>
	public static Scope Track(string name) => new(name);

	/// <summary>取一次快照（内存与队列延迟取最近一次采样值，不触发系统调用）。</summary>
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
				Volatile.Read(ref _gen0Collections),
				Volatile.Read(ref _gen2Collections),
				BitsToDouble(Volatile.Read(ref _queueLagBits)),
				BitsToDouble(Volatile.Read(ref _queueLagMaxBits)),
				_timingsPath,
				_directory,
				[.. Recent.Take(40)],
				[.. Stats.Values.Select(static bucket => bucket.Stat()).OrderByDescending(static stat => stat.TotalMs)]);
		}
	}

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
			Interlocked.Exchange(ref _allocatedBytes, GC.GetTotalAllocatedBytes(precise: false));
			Volatile.Write(ref _gen0Collections, GC.CollectionCount(0));
			Volatile.Write(ref _gen2Collections, GC.CollectionCount(2));
		}
		catch (Exception ex)
		{
			AppLog.Failure(AppLog.For<Diagnostics>(), "Diagnostics memory sample", ex);
		}

		ProbeQueueLag();
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
