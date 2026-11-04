using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using PiWeft.Cli;
using ZLogger;

namespace PiWeft.Desktop;

/// 诊断导出：JSONL 是原始数据，Chrome Trace 与 Firefox Profiler 是两种视图。
/// 两者都不改变采样本身，只把 span/counter 映射到对应查看器的事件模型。
internal sealed partial class Diagnostics
{
	/// 一条可导出的记录：span 有起止，counter 是每秒的窗口值。
	private readonly record struct TraceRecord(
		string Name,
		string Kind,
		string Category,
		int Pid,
		int Tid,
		long TsUs,
		long DurationUs,
		string? Detail);

	private readonly record struct CounterSpec(string Key, string Series, string Category, string Description);

	// 逐秒摘要里固定导出这几条：再多会把查看器的计数器轨道塞满，完整数据仍在 JSONL。
	private static readonly CounterSpec[] CounterSpecs =
	[
		new("p95", "frame.p95ms", "PiWeft", "Frame cost p95 in the last second (ms)"),
		new("long", "frame.long", "PiWeft", "Frames at or above 50 ms in the last second"),
		new("dropped", "frame.dropped", "PiWeft", "Frames over the budget in the last second"),
		new("alloc/frame", "frame.allocB", "PiWeft", "UI-thread allocation per frame (bytes)"),
		new("gc", "frame.gcPct", "PiWeft", "GC pause share of frame time (%)"),
		new("lag", "ui.lagMs", "PiWeft", "UI dispatcher queue lag (ms)"),
		new("input/p95", "ui.inputP95ms", "PiWeft", "Input to paint p95 (ms)"),
		new("cpu", "cpu.pct", "PiWeft", "Process CPU usage (%)"),
		new("heap", "mem.heapMiB", "PiWeft", "Managed heap (MiB)"),
		new("loh", "mem.lohMiB", "PiWeft", "Large object heap (MiB)"),
		new("poh", "mem.pohMiB", "PiWeft", "Pinned object heap (MiB)"),
		new("rss", "mem.rssMiB", "PiWeft", "Working set (MiB)"),
	];

	private static readonly HashSet<string> UiFrameCategories =
		new(StringComparer.Ordinal) { "ui", "row", "timeline", "markdown", "chat", "process", "frame" };

	/// <summary>Chrome Trace Event Format：Perfetto / Chrome DevTools 原生支持，Firefox Profiler 可导入为 marker。</summary>
	public static string? ExportChromeTrace(string? destination = null)
	{
		var directory = Volatile.Read(ref _directory);
		if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
		{
			return null;
		}

		var records = ReadTraceRecords(directory);
		if (records.Count == 0) return null;

		destination ??= Path.Combine(
			directory,
			$"trace-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}.json");

		try
		{
			using var stream = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.Read);
			using var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false });
			json.WriteStartObject();
			json.WriteString("displayTimeUnit", "ms");
			json.WriteString("name", "PiWeft diagnostics");
			json.WriteStartArray("traceEvents");
			// Chrome Trace 的 ts 用相对时间：Firefox Profiler 的导入器不会把绝对 epoch µs
			// 归一化，留成 1.7e15 会让时间轴从 1970 年铺到现在的宽度。
			var baseUs = records[0].TsUs;
			WriteProcessMetadata(json, baseUs);
			var threads = new HashSet<int>();

			foreach (var record in records)
			{
				if (threads.Add(record.Tid)) WriteThreadMetadata(json, record.Pid, record.Tid);
				if (string.Equals(record.Kind, "counter", StringComparison.Ordinal))
				{
					WriteChromeCounters(json, record, baseUs);
				}
				else
				{
					WriteChromeSpan(json, record, baseUs);
				}
			}

			json.WriteEndArray();
			json.WriteEndObject();
			return destination;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
		{
			AppLog.Failure(AppLog.For<Diagnostics>(), "Export Chrome trace", exception);
			return null;
		}
	}

	/// <summary>
	/// Firefox Profiler 原生格式（Gecko profile v37）：marker 展示 span，counter 展示逐秒指标，
	/// 与 Ultra 生成的 trace 用同一个查看器。不需要安装任何组件。
	/// </summary>
	public static string? ExportFirefoxProfile(string? destination = null)
	{
		var directory = Volatile.Read(ref _directory);
		if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
		{
			return null;
		}

		var records = ReadTraceRecords(directory);
		if (records.Count == 0) return null;

		destination ??= Path.Combine(
			directory,
			$"profile-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}.json");

		try
		{
			using var stream = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.Read);
			using var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false });
			WriteFirefoxProfile(json, records);
			return destination;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
		{
			AppLog.Failure(AppLog.For<Diagnostics>(), "Export Firefox profile", exception);
			return null;
		}
	}

	private static void WriteProcessMetadata(Utf8JsonWriter json, long baseUs)
	{
		json.WriteStartObject();
		json.WriteString("name", "process_name");
		json.WriteString("ph", "M");
		json.WriteNumber("pid", Environment.ProcessId);
		json.WriteStartObject("args");
		json.WriteString("name", "PiWeft");
		json.WriteNumber("epochStartUs", baseUs);
		json.WriteEndObject();
		json.WriteEndObject();

		json.WriteStartObject();
		json.WriteString("name", "process_sort_index");
		json.WriteString("ph", "M");
		json.WriteNumber("pid", Environment.ProcessId);
		json.WriteStartObject("args");
		json.WriteNumber("sort_index", 1);
		json.WriteEndObject();
		json.WriteEndObject();
	}

	private static void WriteThreadMetadata(Utf8JsonWriter json, int pid, int tid)
	{
		json.WriteStartObject();
		json.WriteString("name", "thread_name");
		json.WriteString("ph", "M");
		json.WriteNumber("pid", pid);
		json.WriteNumber("tid", tid);
		json.WriteStartObject("args");
		json.WriteString("name", $"Thread {tid}");
		json.WriteEndObject();
		json.WriteEndObject();
	}

	private static void WriteChromeSpan(Utf8JsonWriter json, TraceRecord record, long baseUs)
	{
		json.WriteStartObject();
		json.WriteString("name", record.Name);
		json.WriteString("cat", record.Category);
		json.WriteString("ph", "X");
		json.WriteNumber("ts", Math.Max(0, record.TsUs - baseUs));
		json.WriteNumber("dur", Math.Max(0, record.DurationUs));
		json.WriteNumber("pid", record.Pid);
		json.WriteNumber("tid", record.Tid);
		json.WriteStartObject("args");
		if (!string.IsNullOrEmpty(record.Detail))
		{
			json.WriteString("detail", record.Detail);
			WriteDetailArguments(json, record.Detail);
		}

		json.WriteEndObject();
		json.WriteEndObject();
	}

	private static void WriteChromeCounters(Utf8JsonWriter json, TraceRecord record, long baseUs)
	{
		if (string.IsNullOrEmpty(record.Detail)) return;
		foreach (var spec in CounterSpecs)
		{
			if (!TryReadDetailNumber(record.Detail, spec.Key, out var value)) continue;
			json.WriteStartObject();
			json.WriteString("name", spec.Series);
			json.WriteString("cat", spec.Category);
			json.WriteString("ph", "C");
			json.WriteNumber("ts", Math.Max(0, record.TsUs - baseUs));
			json.WriteNumber("pid", record.Pid);
			json.WriteNumber("tid", record.Tid);
			json.WriteStartObject("args");
			json.WriteNumber(spec.Series, Math.Round(value, 3));
			json.WriteEndObject();
			json.WriteEndObject();
		}
	}

	private static void WriteFirefoxProfile(Utf8JsonWriter json, List<TraceRecord> records)
	{
		var spans = records.Where(static record => !string.Equals(record.Kind, "counter", StringComparison.Ordinal)).ToList();
		var counters = records.Where(static record => string.Equals(record.Kind, "counter", StringComparison.Ordinal)).ToList();
		var startUs = records.Min(static record => record.TsUs);
		var endUs = records.Max(static record => record.TsUs + Math.Max(0, record.DurationUs));
		var startMs = startUs / 1000.0;
		var pid = records[0].Pid;

		json.WriteStartObject();
		json.WriteStartObject("meta");
		json.WriteNumber("version", 37);
		json.WriteNumber("startTime", startMs);
		json.WriteNumber("profilingStartTime", 0);
		json.WriteNumber("profilingEndTime", (endUs - startUs) / 1000.0);
		json.WriteNull("shutdownTime");
		json.WriteNumber("interval", 1);
		json.WriteNumber("stackwalk", 0);
		json.WriteNumber("debug", 0);
		json.WriteNumber("gcpoison", 0);
		json.WriteNumber("asyncstack", 0);
		json.WriteNumber("processType", 0);
		json.WriteString("product", "PiWeft");
		json.WriteString("platform", System.Runtime.InteropServices.RuntimeInformation.OSDescription);
		json.WriteString("oscpu", System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString());
		json.WriteString("misc", "");
		json.WriteString("abi", "");
		json.WriteString("toolkit", "PiWeft");
		json.WriteString("appBuildID", typeof(Diagnostics).Assembly.GetName().Version?.ToString() ?? "");
		json.WriteString("sourceURL", "");
		json.WriteNumber("physicalCPUs", Environment.ProcessorCount);
		json.WriteNumber("logicalCPUs", Environment.ProcessorCount);
		json.WriteString("CPUName", "");
		json.WriteBoolean("symbolicated", true);
		json.WriteStartArray("categories");
		WriteCategories(json);
		json.WriteEndArray();
		json.WriteStartArray("markerSchema");
		json.WriteEndArray();
		json.WriteEndObject();

		json.WriteStartArray("libs");
		json.WriteEndArray();
		json.WriteStartArray("pages");
		json.WriteEndArray();
		json.WriteStartArray("pausedRanges");
		json.WriteEndArray();
		json.WriteStartArray("threads");

		var mainTid = PickMainThread(spans, counters);
		var byThread = spans.GroupBy(static record => record.Tid).ToDictionary(static group => group.Key, static group => group.ToList());
		if (!byThread.ContainsKey(mainTid))
		{
			byThread[mainTid] = [];
		}

		foreach (var (tid, threadSpans) in byThread.OrderBy(static pair => pair.Key))
		{
			WriteFirefoxThread(json, pid, tid, tid == mainTid, threadSpans, startUs);
		}

		json.WriteEndArray();
		WriteFirefoxCounters(json, counters, startUs, pid);
		json.WriteStartArray("processes");
		json.WriteEndArray();
		json.WriteEndObject();
	}

	private static void WriteCategories(Utf8JsonWriter json)
	{
		// 前 8 个与 Firefox Profiler 默认分类一致；后两个是本项目追加的语义分类。
		WriteCategory(json, "Other", "grey");
		WriteCategory(json, "Idle", "transparent");
		WriteCategory(json, "Layout", "purple");
		WriteCategory(json, "JavaScript", "yellow");
		WriteCategory(json, "GC / CC", "orange");
		WriteCategory(json, "Network", "lightblue");
		WriteCategory(json, "Graphics", "green");
		WriteCategory(json, "DOM", "blue");
		WriteCategory(json, "Session", "magenta");
		WriteCategory(json, "Tool", "brown");
	}

	private static void WriteCategory(Utf8JsonWriter json, string name, string color)
	{
		json.WriteStartObject();
		json.WriteString("name", name);
		json.WriteString("color", color);
		json.WriteStartArray("subcategories");
		json.WriteStringValue("Other");
		json.WriteEndArray();
		json.WriteEndObject();
	}

	private static int FirefoxCategoryIndex(string category) => category switch
	{
		"ui" or "row" or "timeline" or "markdown" or "chat" or "frame" => 6, // Graphics
		"session" or "sidebar" or "startup" or "environment" => 8, // Session
		"mcp" => 9, // Tool
		_ => 0,
	};

	private static int PickMainThread(List<TraceRecord> spans, List<TraceRecord> counters)
	{
		if (spans.Count > 0)
		{
			return spans
				.GroupBy(static record => record.Tid)
				.OrderByDescending(group => group.Count(static record => UiFrameCategories.Contains(record.Category)))
				.ThenByDescending(static group => group.Count())
				.First()
				.Key;
		}

		return counters.Count > 0 ? counters[0].Tid : Environment.CurrentManagedThreadId;
	}

	private static void WriteFirefoxThread(
		Utf8JsonWriter json,
		int pid,
		int tid,
		bool isMain,
		List<TraceRecord> spans,
		long startUs)
	{
		var names = new List<string>();
		var nameIndex = new Dictionary<string, int>(StringComparer.Ordinal);
		foreach (var span in spans)
		{
			if (nameIndex.ContainsKey(span.Name)) continue;
			nameIndex[span.Name] = names.Count;
			names.Add(span.Name);
		}

		json.WriteStartObject();
		// 计数器只有在存在 GeckoMain 线程时才会被 Firefox Profiler 处理；显示名取 processName。
		json.WriteString("name", isMain ? "GeckoMain" : $"Thread {tid}");
		json.WriteString("processType", "default");
		json.WriteString("processName", isMain ? "PiWeft" : $"Thread {tid}");
		json.WriteNumber("registerTime", 0);
		json.WriteNull("unregisterTime");
		json.WriteNumber("pid", pid);
		json.WriteNumber("tid", tid);

		json.WriteStartObject("markers");
		json.WriteStartObject("schema");
		json.WriteNumber("name", 0);
		json.WriteNumber("startTime", 1);
		json.WriteNumber("endTime", 2);
		json.WriteNumber("phase", 3);
		json.WriteNumber("category", 4);
		json.WriteNumber("data", 5);
		json.WriteEndObject();
		json.WriteStartArray("data");
		foreach (var span in spans)
		{
			var start = (span.TsUs - startUs) / 1000.0;
			var interval = span.DurationUs > 0;
			json.WriteStartArray();
			json.WriteNumberValue(nameIndex[span.Name]);
			json.WriteNumberValue(start);
			if (interval) json.WriteNumberValue((span.TsUs + span.DurationUs - startUs) / 1000.0);
			else json.WriteNullValue();
			json.WriteNumberValue(interval ? 1 : 0); // INTERVAL / INSTANT
			json.WriteNumberValue(FirefoxCategoryIndex(span.Category));
			if (string.IsNullOrEmpty(span.Detail))
			{
				json.WriteNullValue();
			}
			else
			{
				json.WriteStartObject();
				json.WriteString("type", "Text");
				json.WriteString("name", span.Detail);
				json.WriteEndObject();
			}

			json.WriteEndArray();
		}

		json.WriteEndArray();
		json.WriteEndObject();

		// 纯 marker/counter 档案：没有 CPU 采样栈，表格仍然需要合法的空表。
		json.WriteStartObject("samples");
		json.WriteStartObject("schema");
		json.WriteNumber("stack", 0);
		json.WriteNumber("time", 1);
		json.WriteNumber("eventDelay", 2);
		json.WriteEndObject();
		json.WriteStartArray("data");
		json.WriteEndArray();
		json.WriteEndObject();

		json.WriteStartObject("frameTable");
		json.WriteStartObject("schema");
		json.WriteNumber("location", 0);
		json.WriteNumber("relevantForJS", 1);
		json.WriteNumber("innerWindowID", 2);
		json.WriteNumber("implementation", 3);
		json.WriteNumber("line", 4);
		json.WriteNumber("column", 5);
		json.WriteNumber("category", 6);
		json.WriteNumber("subcategory", 7);
		json.WriteEndObject();
		json.WriteStartArray("data");
		json.WriteEndArray();
		json.WriteEndObject();

		json.WriteStartObject("stackTable");
		json.WriteStartObject("schema");
		json.WriteNumber("prefix", 0);
		json.WriteNumber("frame", 1);
		json.WriteEndObject();
		json.WriteStartArray("data");
		json.WriteEndArray();
		json.WriteEndObject();

		json.WriteStartArray("stringTable");
		foreach (var name in names) json.WriteStringValue(name);
		json.WriteEndArray();
		json.WriteEndObject();
	}

	private static void WriteFirefoxCounters(Utf8JsonWriter json, List<TraceRecord> counters, long startUs, int pid)
	{
		json.WriteStartArray("counters");
		foreach (var spec in CounterSpecs)
		{
			var points = new List<(double TimeMs, double Value)>();
			foreach (var counter in counters)
			{
				if (counter.Pid != pid || string.IsNullOrEmpty(counter.Detail)) continue;
				if (!TryReadDetailNumber(counter.Detail, spec.Key, out var value)) continue;
				points.Add(((counter.TsUs - startUs) / 1000.0, value));
			}

			if (points.Count == 0) continue;
			json.WriteStartObject();
			json.WriteString("name", spec.Series);
			json.WriteString("category", spec.Category);
			json.WriteString("description", spec.Description);
			json.WriteStartObject("samples");
			json.WriteStartObject("schema");
			json.WriteNumber("time", 0);
			json.WriteNumber("count", 1);
			json.WriteNumber("number", 2);
			json.WriteEndObject();
			json.WriteStartArray("data");
			var previousMs = 0.0;
			var first = true;
			foreach (var (timeMs, value) in points.OrderBy(static point => point.TimeMs))
			{
				// Firefox Profiler 的 counter 只有“速率”和“累计”两种画法：把 count 记成
				// 「值 × 距上一个样本的秒数」，line-rate 画出来正好等于值本身；
				// number 保留原值供 tooltip 读取。
				var deltaSeconds = Math.Max(0, (timeMs - previousMs) / 1000.0);
				previousMs = timeMs;
				json.WriteStartArray();
				json.WriteNumberValue(Math.Round(timeMs, 3));
				json.WriteNumberValue(first ? 0 : Math.Round(value * deltaSeconds, 6));
				json.WriteNumberValue(Math.Round(value, 3));
				json.WriteEndArray();
				first = false;
			}

			json.WriteEndArray();
			json.WriteEndObject();
			json.WriteEndObject();
		}

		json.WriteEndArray();
	}

	/// 读取目录下所有采集文件；按时间排序后再导出，旧文件缺 `tsUs` 时退回 `startedAt`。
	private static List<TraceRecord> ReadTraceRecords(string directory)
	{
		if (_enabled) Drain();
		var records = new List<TraceRecord>(1024);
		foreach (var file in Directory.EnumerateFiles(directory, "timings-*.jsonl").OrderBy(static path => path, StringComparer.Ordinal))
		{
			foreach (var line in File.ReadLines(file))
			{
				if (line.Length == 0 || line[0] != '{') continue;
				try
				{
					using var document = JsonDocument.Parse(line);
					var root = document.RootElement;
					var kind = ReadString(root, "kind") ?? "span";
					if (string.Equals(kind, "meta", StringComparison.Ordinal)) continue;
					var name = ReadString(root, "name");
					if (string.IsNullOrEmpty(name)) continue;
					var startedAt = ReadString(root, "startedAt");
					var tsUs = ReadLong(root, "tsUs")
						?? (DateTimeOffset.TryParse(startedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var started)
							? started.ToUnixTimeMilliseconds() * 1000L
							: DateTimeOffset.Now.ToUnixTimeMilliseconds() * 1000L);
					var durationUs = ReadLong(root, "durationUs")
						?? (ReadDouble(root, "durationMs") is { } durationMs ? (long)Math.Round(durationMs * 1000) : 0);
					records.Add(new TraceRecord(
						name,
						kind,
						ReadString(root, "cat") ?? CategoryOf(name),
						(int)(ReadLong(root, "pid") ?? Environment.ProcessId),
						(int)(ReadLong(root, "tid") ?? 0),
						tsUs,
						durationUs,
						ReadString(root, "detail")));
				}
				catch (JsonException)
				{
					// 采集期间被截断的尾行跳过，其余记录仍然可导出。
				}
			}
		}

		records.Sort(static (left, right) => left.TsUs.CompareTo(right.TsUs));
		return records;
	}

	private static string? ReadString(JsonElement element, string name) =>
		element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
			? value.GetString()
			: null;

	private static long? ReadLong(JsonElement element, string name) =>
		element.TryGetProperty(name, out var value) && value.TryGetInt64(out var number) ? number : null;

	private static double? ReadDouble(JsonElement element, string name) =>
		element.TryGetProperty(name, out var value) && value.TryGetDouble(out var number) ? number : null;

	private static bool TryReadDetailNumber(string detail, string key, out double value)
	{
		value = 0;
		var remaining = detail.AsSpan();
		while (!remaining.IsEmpty)
		{
			var space = remaining.IndexOf(' ');
			var token = space < 0 ? remaining : remaining[..space];
			remaining = space < 0 ? default : remaining[(space + 1)..];
			if (token.IsEmpty) continue;
			var equals = token.IndexOf('=');
			if (equals <= 0 || !token[..equals].SequenceEqual(key)) continue;
			return TryParseLeadingNumber(token[(equals + 1)..], out value);
		}

		return false;
	}

	/// 逐秒摘要里的数值带单位（`676B`、`0.5%`、`13MiB`）：只取数字前缀，单位由 series 名表达。
	private static bool TryParseLeadingNumber(ReadOnlySpan<char> text, out double value)
	{
		value = 0;
		var end = 0;
		while (end < text.Length &&
			(char.IsDigit(text[end]) || text[end] is '.' or '-' or '+' or 'e' or 'E'))
		{
			end++;
		}

		return end > 0 && double.TryParse(text[..end], NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
			double.IsFinite(value);
	}

	/// 把 `p95=18.2 dropped=3` 这类 detail 拆成 Chrome Trace 的 args，数值项可被图表直接读取。
	private static void WriteDetailArguments(Utf8JsonWriter json, string detail)
	{
		var remaining = detail.AsSpan();
		while (!remaining.IsEmpty)
		{
			var space = remaining.IndexOf(' ');
			var token = space < 0 ? remaining : remaining[..space];
			remaining = space < 0 ? default : remaining[(space + 1)..];
			if (token.IsEmpty) continue;
			var equals = token.IndexOf('=');
			if (equals <= 0 || equals == token.Length - 1) continue;
			// detail 里的分隔符常粘在值尾巴上（`rows=100,`）：导出 args 时去掉。
			var key = TrimToken(token[..equals]);
			var value = TrimToken(token[(equals + 1)..]);
			if (!IsTraceKey(key)) continue;
			if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number))
			{
				json.WriteNumber(key, number);
			}
			else
			{
				json.WriteString(key, value);
			}
		}
	}

	private static ReadOnlySpan<char> TrimToken(ReadOnlySpan<char> span)
	{
		var end = span.Length;
		while (end > 0 && span[end - 1] is ',' or ';' or ' ') end--;
		return span[..end];
	}

	private static bool IsTraceKey(ReadOnlySpan<char> key)
	{
		if (key.IsEmpty || !char.IsLetter(key[0])) return false;
		foreach (var c in key)
		{
			if (!char.IsLetterOrDigit(c) && c is not ('_' or '.' or '/')) return false;
		}

		return true;
	}
}
