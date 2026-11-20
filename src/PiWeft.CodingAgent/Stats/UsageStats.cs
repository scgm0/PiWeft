using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using PiWeft.Ai;

namespace PiWeft.CodingAgent.Stats;

public sealed record StatsTotals
{
	public long Input { get; init; }

	public long Output { get; init; }

	public long CacheRead { get; init; }

	public long CacheWrite { get; init; }

	public long Messages { get; init; }

	public double Cost { get; init; }

	/// <summary>供应商回报的总 token 数；为 0 时回退到四个桶之和。</summary>
	public long ReportedTokens { get; init; }

	public long Tokens => ReportedTokens > 0 ? ReportedTokens : Input + Output + CacheRead + CacheWrite;

	/// <summary>缓存读取占全部输入侧 token 的比例；没有输入时为空。</summary>
	public double? CacheHitPercent => Input + CacheRead + CacheWrite > 0
		? 100.0 * CacheRead / (Input + CacheRead + CacheWrite)
		: null;
}

public sealed record DailyStats(string Date, StatsTotals Totals);

public sealed record DailyModelStats(string Date, IReadOnlyList<ModelStats> Models);

public sealed record ModelStats(string Provider, string Model, StatsTotals Totals)
{
	/// <summary>模型展示名；没有模型名时退回「未知模型」。</summary>
	public string DisplayName => Model.Length > 0 ? Model : Provider.Length > 0 ? Provider : string.Empty;
}

public sealed record ProjectStats(string Directory, StatsTotals Totals);

/// <summary>一段连续日期内的用量切片。</summary>
public sealed record UsageStatsSlice(
	IReadOnlyList<DailyStats> Days,
	IReadOnlyList<DailyModelStats> DailyModels,
	IReadOnlyList<ModelStats> Models,
	StatsTotals Totals,
	long PeakDayTokens,
	string? PeakDay,
	int ActiveDays,
	int CurrentStreakDays,
	int LongestStreakDays);

/// <summary>连续活跃天数。</summary>
public sealed record UsageStreak(int Current, int Longest);

public sealed record UsageStatsReport(
	IReadOnlyList<DailyStats> Days,
	IReadOnlyList<ModelStats> Models,
	IReadOnlyList<ProjectStats> Projects,
	StatsTotals Totals)
{
	public static UsageStatsReport Empty { get; } = new([], [], [], new StatsTotals());

	/// <summary>按日期拆开的模型用量；区间切片和趋势图只从这里取。</summary>
	public IReadOnlyList<DailyModelStats> DailyModels { get; init; } = [];

	/// <summary>扫描到的会话文件数。</summary>
	public int Sessions { get; init; }

	/// <summary>用户亲手发出的消息数；注入上下文不计。</summary>
	public int Turns { get; init; }

	/// <summary>单个会话内助手回复时长之和的最大值（毫秒）；没有时长记录时退回首末时间跨度。</summary>
	public double LongestSessionMs { get; init; }

	/// <summary>是否只扫描了部分会话，或某份文件读失败时保留了旧数据。</summary>
	public bool Truncated { get; init; }
}

public sealed record UsageHeatCell(string Date, long Tokens, int Level);

public sealed record UsageStatsOptions
{
	public int MaxFiles { get; init; } = 2000;

	public int MaxFilesPerProject { get; init; } = 2000;

	/// <summary>单行字节上限；0 表示不限制。超长行整行跳过。</summary>
	public int MaxLineBytes { get; init; } = 256 * 1024;

	public DateTimeOffset? Since { get; init; }

	public string? Project { get; init; }

	/// <summary>按哪个时区把时间戳归到自然日；缺省用本机时区。</summary>
	public TimeZoneInfo? TimeZone { get; init; }

	public Func<string, string, ModelCostRates?>? Rates { get; init; }
}

/// 跨会话用量统计。只读会话 JSONL，逐行解析，文件数与行长都有上限。
/// 单次调用不缓存；要反复刷新时传入 ScanState，按文件增量重读。
public static partial class UsageStats
{
	private static ReadOnlySpan<byte> UsageMarker => "\"usage\""u8;

	private static ReadOnlySpan<byte> TimestampMarker => "\"timestamp\""u8;

	private static ReadOnlySpan<byte> SessionMarker => "\"session\""u8;

	private static ReadOnlySpan<byte> MessageMarker => "\"message\""u8;

	private static ReadOnlySpan<byte> RoleMarker => "\"role\""u8;

	private static ReadOnlySpan<byte> AssistantValue => "assistant"u8;

	private static ReadOnlySpan<byte> UserValue => "user"u8;

	private static ReadOnlySpan<byte> CustomTypeMarker => "\"customType\""u8;

	public static string DefaultRoot()
	{
		var configured = Environment.GetEnvironmentVariable(Session.SessionPaths.EnvSessionDir);
		return !string.IsNullOrEmpty(configured) ? configured : Path.Combine(Session.SessionPaths.GetAgentDir(), "sessions");
	}

	public static UsageStatsReport Scan(string sessionsRoot, UsageStatsOptions? options = null, CancellationToken cancellationToken = default)
		=> Scan(sessionsRoot, state: null, options, cancellationToken);

	/// <summary>
	/// 在 <paramref name="state"/> 上做增量扫描：未改动的文件直接复用上次的聚合，
	/// 只重读新增或变更的文件。重读与解析随变更量增长；报告构建仍随文件数与天数增长。
	/// </summary>
	public static UsageStatsReport Scan(
		string sessionsRoot,
		ScanState? state,
		UsageStatsOptions? options = null,
		CancellationToken cancellationToken = default)
	{
		options ??= new UsageStatsOptions();
		var timeZone = options.TimeZone ?? TimeZoneInfo.Local;
		state?.Prepare(sessionsRoot, timeZone, options);

		var scanned = new List<FileScan>();
		var turns = 0;
		var longestSessionMs = 0.0;
		var truncated = false;
		var rootExists = Directory.Exists(sessionsRoot);
		HashSet<string>? seen = state is null ? null : new HashSet<string>(StringComparer.Ordinal);
		var candidates = new List<FileCandidate>();

		if (rootExists)
		{
			var remaining = Math.Max(0, options.MaxFiles);
			foreach (var directory in EnumerateDirectories(sessionsRoot, options.Project))
			{
				if (remaining == 0 || cancellationToken.IsCancellationRequested)
				{
					truncated = true;
					break;
				}

				var projectFiles = 0;
				foreach (var file in EnumerateSessionFiles(directory))
				{
					if (remaining == 0 || projectFiles >= options.MaxFilesPerProject)
					{
						truncated = true;
						break;
					}

					if (cancellationToken.IsCancellationRequested)
					{
						break;
					}

					projectFiles++;
					remaining--;
					seen?.Add(file);

					FileScan? cached = null;
					if (state is not null)
					{
						state.TryGetFile(file, out var found);
						cached = found;
					}

					var (length, writeTicks) = Snapshot(file);
					candidates.Add(new FileCandidate(file, directory, cached, length, writeTicks));
				}
			}
		}

		var scans = new FileScan?[candidates.Count];
		var pending = new List<int>();
		for (var index = 0; index < candidates.Count; index++)
		{
			var candidate = candidates[index];
			if (candidate.Cached is { } cached && candidate.Length >= 0 &&
				cached.Length == candidate.Length && cached.LastWriteTicks == candidate.WriteTicks)
			{
				scans[index] = cached;
				continue;
			}

			pending.Add(index);
		}

		if (state is not null)
		{
			state.ReparsedFiles = pending.Count;
		}

		// 顺序扫描：并行读同一块磁盘在冷启动时反而抢 IO，增量场景又只有一两个文件，
		// 省下的调度开销比多出来的线程更有用。
		foreach (var index in pending)
		{
			var candidate = candidates[index];
			scans[index] = ScanFile(candidate.File, candidate.Directory, candidate.Cached,
				candidate.Length, candidate.WriteTicks, options, timeZone, cancellationToken);
		}

		var sessions = candidates.Count;
		for (var index = 0; index < candidates.Count; index++)
		{
			var candidate = candidates[index];
			var scan = scans[index];
			if (scan is null)
			{
				// 取消或读失败时保留上一份数据，别让页面上的总量缩水；
				// 但这份数据已经旧了，报告要标成不完整。
				scan = candidate.Cached;
				if (scan is not null)
				{
					truncated = true;
				}
			}
			else if (state is not null && !ReferenceEquals(scan, candidate.Cached))
			{
				state.SetFile(candidate.File, scan);
				if (scan.Resumed)
				{
					state.ResumedFiles++;
				}
			}

			// 文件在 sessions 目录里就算一次会话；头部损坏但仍有 usage 的文件不能显示成 0 会话。
			if (scan is null)
			{
				continue;
			}

			scanned.Add(scan);
			turns += scan.Turns;
			var wallClock = scan.Start is { } start && scan.End is { } end && end > start
				? (end - start).TotalMilliseconds
				: 0;
			// 有回复时长但缺消息时间戳时不能退回首末跨度，否则和 SessionStatistics 的活跃时长口径不一致。
			longestSessionMs = Math.Max(longestSessionMs, scan.HasDurations ? scan.ActiveMs : wallClock);
		}

		if (cancellationToken.IsCancellationRequested)
		{
			truncated = true;
		}

		// 根目录不存在时看到的不是“文件都删了”，跳过裁剪，免得临时不可用清掉整份缓存。
		if (rootExists)
		{
			state?.Trim(seen, options.Project, cancellationToken.IsCancellationRequested);
		}

		return BuildReport(scanned, options, sessions, turns, longestSessionMs, truncated);
	}

	private readonly record struct FileCandidate(
		string File,
		string Directory,
		FileScan? Cached,
		long Length,
		long WriteTicks);

	private static UsageStatsReport BuildReport(
		List<FileScan> scans,
		UsageStatsOptions options,
		int sessions,
		int turns,
		double longestSessionMs,
		bool truncated)
	{
		var days = new Dictionary<DateOnly, Bucket>();
		var models = new Dictionary<ModelKey, Bucket>();
		var projects = new Dictionary<string, Bucket>(StringComparer.Ordinal);
		var dailyModels = new Dictionary<DateOnly, Dictionary<ModelKey, Bucket>>();
		var undated = new Bucket();
		var rates = options.Rates;

		// 文件缓存里保留未定价的 token 桶，价格只在这里应用一次；rates 变化不必重扫文件。
		foreach (var scan in scans)
		{
			foreach (var (date, byModel) in scan.Days)
			{
				if (!days.TryGetValue(date, out var day))
				{
					day = new Bucket();
					days[date] = day;
				}

				if (!dailyModels.TryGetValue(date, out var daily))
				{
					daily = new Dictionary<ModelKey, Bucket>();
					dailyModels[date] = daily;
				}

				foreach (var (key, bucket) in byModel)
				{
					var cost = bucket.CostWith(rates, key.Provider, key.Model);
					day.AddCosted(bucket, cost);
					AddCosted(daily, key, bucket, cost);
					AddCosted(models, key, bucket, cost);
					AddCosted(projects, scan.Project, bucket, cost);
				}
			}

			foreach (var (key, bucket) in scan.Undated)
			{
				var cost = bucket.CostWith(rates, key.Provider, key.Model);
				undated.AddCosted(bucket, cost);
				AddCosted(models, key, bucket, cost);
				AddCosted(projects, scan.Project, bucket, cost);
			}
		}

		return new UsageStatsReport(
			[.. days.OrderBy(static pair => pair.Key).Select(static pair => new DailyStats(FormatDate(pair.Key), pair.Value.ToTotals()))],
			[.. models.OrderByDescending(static pair => pair.Value.Tokens).ThenByDescending(static pair => pair.Value.Cost)
				.Select(static pair => new ModelStats(pair.Key.Provider, pair.Key.Model, pair.Value.ToTotals()))],
			[.. projects.OrderByDescending(static pair => pair.Value.Tokens)
				.Select(static pair => new ProjectStats(pair.Key, pair.Value.ToTotals()))],
			Sum(days.Values, undated))
		{
			DailyModels =
			[
				.. dailyModels.OrderBy(static pair => pair.Key).Select(static pair => new DailyModelStats(
					FormatDate(pair.Key),
					[.. pair.Value.OrderByDescending(static bucket => bucket.Value.Tokens)
						.ThenByDescending(static bucket => bucket.Value.Cost)
						.Select(static bucket => new ModelStats(bucket.Key.Provider, bucket.Key.Model, bucket.Value.ToTotals()))]))
			],
			Sessions = sessions,
			Turns = turns,
			LongestSessionMs = longestSessionMs,
			Truncated = truncated,
		};
	}

	private static void AddCosted<TKey>(Dictionary<TKey, Bucket> map, TKey key, Bucket source, double cost)
		where TKey : notnull
	{
		ref var target = ref CollectionsMarshal.GetValueRefOrAddDefault(map, key, out _);
		target ??= new Bucket();
		target.AddCosted(source, cost);
	}

	/// <summary>
	/// 取最近 <paramref name="days"/> 个自然日的切片；<paramref name="days"/> 为 0 时返回全部历史。
	/// 返回的日序列连续补齐，图表不必再处理空档。
	/// </summary>
	public static UsageStatsSlice Slice(UsageStatsReport report, int days, DateOnly today)
	{
		var byDate = new Dictionary<DateOnly, StatsTotals>();
		foreach (var day in report.Days)
		{
			if (DateOnly.TryParseExact(day.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
			{
				byDate[date] = day.Totals;
			}
		}

		DateOnly start;
		if (days > 0)
		{
			start = today.AddDays(1 - days);
		}
		else if (report.Days.Count > 0 &&
			DateOnly.TryParseExact(report.Days[0].Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var first))
		{
			start = first;
		}
		else
		{
			start = today;
		}

		if (start > today)
		{
			start = today;
		}

		var series = new List<DailyStats>(Math.Max(0, today.DayNumber - start.DayNumber + 1));
		for (var date = start; date <= today; date = date.AddDays(1))
		{
			series.Add(new DailyStats(FormatDate(date), byDate.TryGetValue(date, out var dayTotals) ? dayTotals : new StatsTotals()));
		}

		var dailyModels = new List<DailyModelStats>();
		var modelBuckets = new Dictionary<ModelKey, Bucket>();
		foreach (var day in report.DailyModels)
		{
			if (day.Date.Length == 0 ||
				!DateOnly.TryParseExact(day.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ||
				date < start || date > today)
			{
				continue;
			}

			var kept = new List<ModelStats>(day.Models.Count);
			foreach (var model in day.Models)
			{
				kept.Add(model);
				var key = new ModelKey(model.Provider, model.Model);
				if (!modelBuckets.TryGetValue(key, out var bucket))
				{
					bucket = new Bucket();
					modelBuckets[key] = bucket;
				}

				bucket.Add(model.Totals);
			}

			if (kept.Count > 0)
			{
				dailyModels.Add(new DailyModelStats(day.Date, kept));
			}
		}

		// 旧报告没有日模型数据时，至少让「全部」区间能显示模型排行。
		var models = modelBuckets
			.OrderByDescending(static pair => pair.Value.Tokens).ThenByDescending(static pair => pair.Value.Cost)
			.Select(static pair => new ModelStats(pair.Key.Provider, pair.Key.Model, pair.Value.ToTotals()))
			.ToList();
		if (models.Count == 0 && days <= 0)
		{
			models = [.. report.Models];
		}

		long peak = 0;
		string? peakDay = null;
		var activeDays = 0;
		var longestStreak = 0;
		var run = 0;
		var totals = new Bucket();
		foreach (var day in series)
		{
			totals.Add(day.Totals);
			var tokens = day.Totals.Tokens;
			if (tokens > 0)
			{
				activeDays++;
				run++;
				if (run > longestStreak)
				{
					longestStreak = run;
				}
			}
			else
			{
				run = 0;
			}

			if (tokens > peak)
			{
				peak = tokens;
				peakDay = day.Date;
			}
		}

		// 序列连续且以 today 结尾，当前连续天数从尾部倒数即可，不再建集合重复解析日期。
		var currentStreak = 0;
		var index = series.Count - 1;
		if (index >= 0 && series[index].Totals.Tokens == 0)
		{
			index--;
		}

		while (index >= 0 && series[index].Totals.Tokens > 0)
		{
			currentStreak++;
			index--;
		}

		return new UsageStatsSlice(
			series,
			dailyModels,
			models,
			totals.ToTotals(),
			peak,
			peakDay,
			activeDays,
			currentStreak,
			longestStreak);
	}

	/// <summary>连续活跃天数：今天还没使用不算断，从昨天继续算。</summary>
	public static UsageStreak Streaks(IReadOnlyList<DailyStats> days, DateOnly today)
	{
		var active = new HashSet<DateOnly>();
		foreach (var day in days)
		{
			if (day.Totals.Tokens > 0 &&
				DateOnly.TryParseExact(day.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
			{
				active.Add(date);
			}
		}

		var longest = 0;
		var run = 0;
		var ordered = active.Order().ToList();
		DateOnly? previous = null;
		foreach (var date in ordered)
		{
			run = previous is { } prior && date == prior.AddDays(1) ? run + 1 : 1;
			longest = Math.Max(longest, run);
			previous = date;
		}

		var current = 0;
		var cursor = active.Contains(today) ? today : today.AddDays(-1);
		while (active.Contains(cursor))
		{
			current++;
			cursor = cursor.AddDays(-1);
		}

		return new UsageStreak(current, longest);
	}

	public static IReadOnlyList<UsageHeatCell> Heatmap(UsageStatsReport report, int weeks = 26, DateOnly? today = null)
	{
		weeks = Math.Clamp(weeks, 1, 104);
		var byDate = new Dictionary<string, long>(StringComparer.Ordinal);
		foreach (var day in report.Days)
		{
			byDate[day.Date] = day.Totals.Tokens;
		}

		var last = today ?? DateOnly.FromDateTime(DateTime.Now);
		// 对齐到本周周一，让列是整周。
		var end = last.AddDays(-(((int)last.DayOfWeek + 6) % 7));
		var start = end.AddDays(-7 * (weeks - 1));
		var max = byDate.Count == 0 ? 0 : byDate.Values.Max();

		var cells = new List<UsageHeatCell>(weeks * 7);
		for (var date = start; date <= end.AddDays(6); date = date.AddDays(1))
		{
			var key = FormatDate(date);
			var tokens = date <= last && byDate.TryGetValue(key, out var value) ? value : 0;
			cells.Add(new UsageHeatCell(key, tokens, Level(tokens, max)));
		}

		return cells;
	}

	private static int Level(long tokens, long max)
	{
		if (tokens <= 0 || max <= 0)
		{
			return 0;
		}

		var ratio = (double)tokens / max;
		return ratio switch
		{
			<= 0.25 => 1,
			<= 0.5 => 2,
			<= 0.75 => 3,
			_ => 4,
		};
	}

	private static IEnumerable<string> EnumerateDirectories(string root, string? project)
	{
		if (project is { Length: > 0 })
		{
			var direct = Path.Combine(root, project);
			if (Directory.Exists(direct))
			{
				yield return direct;
				yield break;
			}
		}

		string[] directories;
		try
		{
			directories = Directory.GetDirectories(root);
		}
		catch (IOException)
		{
			yield break;
		}
		catch (UnauthorizedAccessException)
		{
			yield break;
		}

		foreach (var directory in directories)
		{
			yield return directory;
		}
	}

	private static IEnumerable<string> EnumerateSessionFiles(string directory)
	{
		string[] files;
		try
		{
			files = Directory.GetFiles(directory, "*" + Session.SessionFormat.Extension);
		}
		catch (IOException)
		{
			yield break;
		}
		catch (UnauthorizedAccessException)
		{
			yield break;
		}

		Array.Sort(files, static (left, right) => string.CompareOrdinal(Path.GetFileName(right), Path.GetFileName(left)));
		foreach (var file in files)
		{
			yield return file;
		}
	}

	internal readonly record struct ModelKey(string Provider, string Model);

	private static FileScan? ScanFile(
		string file,
		string directory,
		FileScan? cached,
		long length,
		long writeTicks,
		UsageStatsOptions options,
		TimeZoneInfo timeZone,
		CancellationToken cancellationToken)
	{
		// 只有上次读到了换行收尾的 EOF 且首尾字节都对得上，才能从断点续读；否则整份重扫。
		var resume = cached is { Boundary: > 0 } && cached.Boundary == cached.Length && length > cached.Boundary &&
			BoundaryMatches(file, cached.Boundary, cached.BoundaryHash, cached.HeadHash);
		var scan = resume ? cached!.Clone() : new FileScan { Project = Path.GetFileName(directory) };
		scan.Resumed = resume;
		var firstLine = !resume;
		var lineCount = 0;
		var cancelled = false;
		var bytesRead = -1L;

		try
		{
			using var reader = new Session.Utf8LineReader(file, resume ? cached!.Boundary : 0, options.MaxLineBytes);
			while (reader.TryReadLine(out var line))
			{
				if ((++lineCount & 0xFF) == 0 && cancellationToken.IsCancellationRequested)
				{
					cancelled = true;
					break;
				}

				// 超长行由 reader 丢弃，返回空行；这里再核一次长度覆盖 reader 未启用上限的情况。
				if (line.Length == 0 || (options.MaxLineBytes > 0 && line.Length > options.MaxLineBytes))
				{
					continue;
				}

				var hasTimestamp = TryReadTimestamp(line, out var timestamp);
				if (hasTimestamp)
				{
					if (scan.Start is null || timestamp < scan.Start)
					{
						scan.Start = timestamp;
					}

					if (scan.End is null || timestamp > scan.End)
					{
						scan.End = timestamp;
					}
				}

				// 第一行按会话头解析；第三方写出的 JSON 可能带空格，不能只靠前缀匹配。
				if (firstLine)
				{
					firstLine = false;
					if (TryReadHeader(reader.CurrentLineMemory, out var headerCwd))
					{
						if (headerCwd is { Length: > 0 })
						{
							scan.Project = headerCwd;
						}

						continue;
					}
				}

				// 只看 message 条目：先取角色，工具结果/用户/系统正文都不必再扫一遍找 usage。
				if (line.IndexOf(MessageMarker) >= 0)
				{
					var role = ReadRole(line);
					if (role.Length > 0 && !role.SequenceEqual(AssistantValue))
					{
						if (role.SequenceEqual(UserValue) && line.IndexOf(CustomTypeMarker) < 0)
						{
							scan.Turns++;
						}

						continue;
					}
				}

				if (line.IndexOf(UsageMarker) < 0 || (!hasTimestamp && line.IndexOf(TimestampMarker) < 0))
				{
					continue;
				}

				if (options.Since is { } cutoff && hasTimestamp && timestamp < cutoff)
				{
					continue;
				}

				var date = hasTimestamp ? DateOf(timestamp, timeZone) : (DateOnly?)null;
				var sample = ScanLine(reader.CurrentLineMemory, date, scan);
				if (sample.DurationMs > 0)
				{
					scan.HasDurations = true;
					if (sample.Timestamp > 0)
					{
						scan.Active.Add(sample.Timestamp, sample.Timestamp + sample.DurationMs);
					}
				}
			}

			bytesRead = reader.BytesRead;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
		{
			// 单个文件读失败不影响整体统计；不保存半截状态，下次按长度/时间变化重试。
			return null;
		}

		if (cancelled || cancellationToken.IsCancellationRequested)
		{
			return null;
		}

		// 只在整份文件读完且以换行收尾时记边界：下次追加可从边界续读，不用重扫全文。
		scan.Boundary = 0;
		if (length > 0 && bytesRead == length && TryCaptureBoundary(file, length, out var boundaryHash, out var headHash))
		{
			scan.Boundary = length;
			scan.BoundaryHash = boundaryHash;
			scan.HeadHash = headHash;
		}

		scan.Length = length;
		scan.LastWriteTicks = writeTicks;
		return scan;
	}

	private static (double DurationMs, long Timestamp) ScanLine(ReadOnlyMemory<byte> line, DateOnly? date, FileScan scan)
	{
		if (!TryReadUsageLine(line, out var sample))
		{
			return default;
		}

		// 供应商总量通常等于四个桶之和；总量更大时保留它（思考等额外口径），
		// 总量更小时用桶之和，避免明细比合计还大。总量缺失则用桶之和。
		var bucketTokens = sample.Input + sample.Output + sample.CacheRead + sample.CacheWrite;
		var totalTokens = sample.ReportedTokens > bucketTokens ? sample.ReportedTokens : bucketTokens;
		scan.Add(date, new ModelKey(sample.Provider, sample.Model), sample.Input, sample.Output,
			sample.CacheRead, sample.CacheWrite, totalTokens, sample.Messages, sample.Cost);
		return (sample.DurationMs, sample.Timestamp);
	}

	private static DateOnly DateOf(DateTimeOffset timestamp, TimeZoneInfo timeZone) =>
		DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(timestamp, timeZone).DateTime);

	private static bool TryReadHeader(ReadOnlyMemory<byte> line, out string? cwd)
	{
		cwd = null;
		if (line.Span.IndexOf(SessionMarker) < 0)
		{
			return false;
		}

		using var document = TryParse(line);
		if (document is null)
		{
			return false;
		}

		var root = document.RootElement;
		if (root.ValueKind != JsonValueKind.Object || ReadString(root, "type") != "session")
		{
			return false;
		}

		cwd = ReadString(root, "cwd");
		return true;
	}

	private static ReadOnlySpan<byte> ReadRole(ReadOnlySpan<byte> line)
	{
		var index = line.IndexOf(RoleMarker);
		if (index < 0)
		{
			return default;
		}

		var rest = line[(index + RoleMarker.Length)..];
		while (rest.Length > 0 && (rest[0] == (byte)':' || rest[0] == (byte)' ' || rest[0] == (byte)'\t'))
		{
			rest = rest[1..];
		}

		if (rest.Length == 0 || rest[0] != (byte)'"')
		{
			return default;
		}

		rest = rest[1..];
		var end = rest.IndexOf((byte)'"');
		return end < 0 ? default : rest[..end];
	}

	private static bool TryReadTimestamp(ReadOnlySpan<byte> line, out DateTimeOffset timestamp)
	{
		timestamp = default;
		var index = line.IndexOf(TimestampMarker);
		if (index < 0)
		{
			return false;
		}

		index += TimestampMarker.Length;
		while (index < line.Length && (line[index] == (byte)':' || line[index] == (byte)' ' || line[index] == (byte)'\t'))
		{
			index++;
		}

		if (index >= line.Length || line[index] != (byte)'"')
		{
			return false;
		}

		index++;
		var end = line[index..].IndexOf((byte)'"');
		if (end <= 0)
		{
			return false;
		}

		var value = line.Slice(index, end);
		Span<char> buffer = stackalloc char[64];
		if (value.Length > buffer.Length)
		{
			return false;
		}

		var length = Encoding.UTF8.GetChars(value, buffer);
		return DateTimeOffset.TryParse(buffer[..length], CultureInfo.InvariantCulture,
			DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out timestamp);
	}

	private static string FormatDate(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

	private static JsonDocument? TryParse(ReadOnlyMemory<byte> line)
	{
		try
		{
			return JsonDocument.Parse(line);
		}
		catch (JsonException)
		{
			// 单行损坏只丢这一行，不能让同一文件后面的用量全消失。
			return null;
		}
	}

	private static string? ReadString(JsonElement element, string property) =>
		element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

	private static StatsTotals Sum(IEnumerable<Bucket> buckets, Bucket extra)
	{
		var sum = new Bucket();
		foreach (var bucket in buckets)
		{
			sum.Add(bucket);
		}

		sum.Add(extra);
		return sum.ToTotals();
	}

	internal sealed class Bucket
	{
		public long Input { get; private set; }

		public long Output { get; private set; }

		public long CacheRead { get; private set; }

		public long CacheWrite { get; private set; }

		public long ReportedTokens { get; private set; }

		public long Messages { get; private set; }

		/// <summary>已写进会话的 cost 之和；没写 cost 的 token 另记，构建报告时按价格表估算。</summary>
		public double Cost { get; private set; }

		public long UnpricedInput { get; private set; }

		public long UnpricedOutput { get; private set; }

		public long UnpricedCacheRead { get; private set; }

		public long UnpricedCacheWrite { get; private set; }

		public long Tokens => ReportedTokens > 0 ? ReportedTokens : Input + Output + CacheRead + CacheWrite;

		public void Add(long input, long output, long cacheRead, long cacheWrite, long reportedTokens, long messages, double cost)
		{
			Input += input;
			Output += output;
			CacheRead += cacheRead;
			CacheWrite += cacheWrite;
			ReportedTokens += reportedTokens;
			Messages += messages;
			if (cost > 0)
			{
				Cost += cost;
				return;
			}

			UnpricedInput += input;
			UnpricedOutput += output;
			UnpricedCacheRead += cacheRead;
			UnpricedCacheWrite += cacheWrite;
		}

		public void AddCosted(Bucket source, double cost) =>
			Add(source.Input, source.Output, source.CacheRead, source.CacheWrite, source.Tokens, source.Messages, cost);

		/// <summary>没记 cost 的 token 按价格表估算；已记 cost 的部分不重复算。</summary>
		public double CostWith(Func<string, string, ModelCostRates?>? rates, string provider, string model)
		{
			if (rates is null || UnpricedInput + UnpricedOutput + UnpricedCacheRead + UnpricedCacheWrite == 0 ||
				rates(provider, model) is not { } value)
			{
				return Cost;
			}

			return Cost + value.Input / 1_000_000.0 * UnpricedInput + value.Output / 1_000_000.0 * UnpricedOutput +
				value.CacheRead / 1_000_000.0 * UnpricedCacheRead + value.CacheWrite / 1_000_000.0 * UnpricedCacheWrite;
		}

		public Bucket Clone() => new()
		{
			Input = Input,
			Output = Output,
			CacheRead = CacheRead,
			CacheWrite = CacheWrite,
			ReportedTokens = ReportedTokens,
			Messages = Messages,
			Cost = Cost,
			UnpricedInput = UnpricedInput,
			UnpricedOutput = UnpricedOutput,
			UnpricedCacheRead = UnpricedCacheRead,
			UnpricedCacheWrite = UnpricedCacheWrite,
		};

		public void Add(StatsTotals totals) =>
			Add(totals.Input, totals.Output, totals.CacheRead, totals.CacheWrite, totals.Tokens, totals.Messages, totals.Cost);

		public void Add(Bucket other) =>
			Add(other.Input, other.Output, other.CacheRead, other.CacheWrite, other.Tokens, other.Messages, other.Cost);

		public StatsTotals ToTotals() => new()
		{
			Input = Input,
			Output = Output,
			CacheRead = CacheRead,
			CacheWrite = CacheWrite,
			ReportedTokens = ReportedTokens,
			Messages = Messages,
			Cost = Cost,
		};
	}
}
