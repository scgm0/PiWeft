using System.Globalization;
using System.Text.Json;
using PiWeft.Ai;

namespace PiWeft.CodingAgent.Stats;

/// <summary>一个统计维度的令牌与费用合计。</summary>
public sealed record StatsTotals
{
	public long Input { get; init; }

	public long Output { get; init; }

	public long CacheRead { get; init; }

	public long CacheWrite { get; init; }

	/// <summary>计入的助手回复数（<c>usage</c> 条目不计）。</summary>
	public long Messages { get; init; }

	public double Cost { get; init; }

	public long Tokens => Input + Output + CacheRead + CacheWrite;
}

/// <summary>按日期聚合的用量。</summary>
public sealed record DailyStats(string Date, StatsTotals Totals);

/// <summary>按供应商 + 模型聚合的用量。</summary>
public sealed record ModelStats(string Provider, string Model, StatsTotals Totals);

/// <summary>按项目（工作目录）聚合的用量。</summary>
public sealed record ProjectStats(string Directory, StatsTotals Totals);

/// <summary>跨会话统计结果；各维度都按同一遍扫描得到，互不重复计数。</summary>
public sealed record UsageStatsReport(
	IReadOnlyList<DailyStats> Days,
	IReadOnlyList<ModelStats> Models,
	IReadOnlyList<ProjectStats> Projects,
	StatsTotals Totals)
{
	public static UsageStatsReport Empty { get; } = new([], [], [], new StatsTotals());
}

/// <summary>热力图格子的用量档位，0 表示无用量。</summary>
public sealed record UsageHeatCell(string Date, long Tokens, int Level);

/// <summary>扫描参数；默认值面向桌面按需刷新，不做轮询。</summary>
public sealed record UsageStatsOptions
{
	/// <summary>最多扫描的会话文件数（按文件名倒序，先读最近的）。</summary>
	public int MaxFiles { get; init; } = 2000;

	/// <summary>单个项目目录最多列举的文件数，防止超大目录拖慢一次刷新。</summary>
	public int MaxFilesPerProject { get; init; } = 2000;

	/// <summary>超过该长度的行直接跳过（工具结果可能很大）。</summary>
	public int MaxLineBytes { get; init; } = 256 * 1024;

	/// <summary>只统计该时间点之后的条目（ISO 8601 字符串比较，与写入格式一致）。</summary>
	public DateTimeOffset? Since { get; init; }

	/// <summary>只统计该项目（会话根下的目录名，或它的工作目录路径）。</summary>
	public string? Project { get; init; }

	/// <summary>会话文件里没有 <c>cost</c> 时用来补算费用的价格表；返回 <c>null</c> 表示不补算。</summary>
	public Func<string, string, ModelCostRates?>? Rates { get; init; }
}

/// <summary>
/// 跨会话用量统计。只读会话 JSONL，按需调用、单遍扫描、逐行解析，
/// 不做缓存也不驻留后台线程；文件数与行长都有上限。
/// </summary>
public static class UsageStats
{
	/// <summary>默认的会话根目录：优先 <c>PI_CODING_AGENT_SESSION_DIR</c>，否则 <c>&lt;agentDir&gt;/sessions</c>。</summary>
	public static string DefaultRoot()
	{
		var configured = Environment.GetEnvironmentVariable(Session.SessionPaths.EnvSessionDir);
		return !string.IsNullOrEmpty(configured) ? configured : Path.Combine(Session.SessionPaths.GetAgentDir(), "sessions");
	}

	public static UsageStatsReport Scan(string sessionsRoot, UsageStatsOptions? options = null, CancellationToken cancellationToken = default)
	{
		options ??= new UsageStatsOptions();
		var days = new Dictionary<string, Bucket>(StringComparer.Ordinal);
		var models = new Dictionary<string, Bucket>(StringComparer.Ordinal);
		var projects = new Dictionary<string, Bucket>(StringComparer.Ordinal);

		if (Directory.Exists(sessionsRoot))
		{
			var remaining = Math.Max(0, options.MaxFiles);
			foreach (var directory in EnumerateDirectories(sessionsRoot, options.Project))
			{
				if (remaining == 0)
				{
					break;
				}

				foreach (var file in EnumerateSessionFiles(directory, options.MaxFilesPerProject))
				{
					if (remaining == 0 || cancellationToken.IsCancellationRequested)
					{
						break;
					}

					remaining--;
					ScanFile(file, directory, options, days, models, projects, cancellationToken);
				}
			}
		}

		return new UsageStatsReport(
			[.. days.OrderBy(static pair => pair.Key, StringComparer.Ordinal).Select(static pair => new DailyStats(pair.Key, pair.Value.ToTotals()))],
			[.. models.OrderByDescending(static pair => pair.Value.Cost).ThenByDescending(static pair => pair.Value.Tokens)
				.Select(static pair => new ModelStats(Field(pair.Key, 0), Field(pair.Key, 1), pair.Value.ToTotals()))],
			[.. projects.OrderByDescending(static pair => pair.Value.Tokens)
				.Select(static pair => new ProjectStats(pair.Key, pair.Value.ToTotals()))],
			Sum(days.Values));
	}

	/// <summary>把每日用量整理成最近 <paramref name="weeks"/> 周的热力图，按周排列、周一为第一天。</summary>
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
			var tokens = date <= last && byDate.TryGetValue(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), out var value) ? value : 0;
			cells.Add(new UsageHeatCell(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), tokens, Level(tokens, max)));
		}

		return cells;
	}

	/// <summary>四档强度（另有 0 档无用量），阈值与 GitHub 热力图同量级。</summary>
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

	/// <summary>会话文件名以 ISO 时间戳开头，倒序即最近优先。</summary>
	private static IEnumerable<string> EnumerateSessionFiles(string directory, int limit)
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
		for (var index = 0; index < files.Length && index < limit; index++)
		{
			yield return files[index];
		}
	}

	private static void ScanFile(
		string file,
		string directory,
		UsageStatsOptions options,
		Dictionary<string, Bucket> days,
		Dictionary<string, Bucket> models,
		Dictionary<string, Bucket> projects,
		CancellationToken cancellationToken)
	{
		var since = options.Since is { } cutoff
			? cutoff.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)
			: null;

		string? cwd = null;
		var project = Path.GetFileName(directory);
		var lineCount = 0;

		try
		{
			using var reader = new StreamReader(file);
			while (reader.ReadLine() is { } line)
			{
				if ((++lineCount & 0xFF) == 0 && cancellationToken.IsCancellationRequested)
				{
					return;
				}

				if (line.Length == 0 || line.Length > options.MaxLineBytes)
				{
					continue;
				}

				// 先看头部确定项目名，其余行只解析含用量的行。
				if (cwd is null && line.StartsWith("{\"type\":\"session\"", StringComparison.Ordinal))
				{
					cwd = ReadString(line, "cwd");
					if (cwd is { Length: > 0 })
					{
						project = cwd;
					}

					continue;
				}

				if (!line.Contains("\"usage\"", StringComparison.Ordinal) || !line.Contains("\"timestamp\"", StringComparison.Ordinal))
				{
					continue;
				}

				ScanLine(line, project, options, since, days, models, projects);
			}
		}
		catch (IOException)
		{
			// 单个文件读失败不影响整体统计。
		}
		catch (UnauthorizedAccessException)
		{
		}
		catch (JsonException)
		{
		}
	}

	private static void ScanLine(
		string line,
		string project,
		UsageStatsOptions options,
		string? since,
		Dictionary<string, Bucket> days,
		Dictionary<string, Bucket> models,
		Dictionary<string, Bucket> projects)
	{
		using var document = JsonDocument.Parse(line);
		var root = document.RootElement;
		if (root.ValueKind != JsonValueKind.Object)
		{
			return;
		}

		var type = root.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
		var isMessage = type is null or "message";
		JsonElement usage;
		string provider;
		string model;

		if (isMessage)
		{
			if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object ||
				!message.TryGetProperty("usage", out usage) || usage.ValueKind != JsonValueKind.Object ||
				(message.TryGetProperty("role", out var role) && role.GetString() != "assistant"))
			{
				return;
			}

			provider = ReadString(message, "provider") ?? string.Empty;
			model = ReadString(message, "model") ?? string.Empty;
		}
		else if (type == "usage")
		{
			// 会话级用量条目（例如缓存保温），不计入助手回复数。
			if (!root.TryGetProperty("usage", out usage) || usage.ValueKind != JsonValueKind.Object)
			{
				return;
			}

			provider = ReadString(root, "provider") ?? string.Empty;
			model = ReadString(root, "model") ?? string.Empty;
		}
		else
		{
			return;
		}

		var timestamp = ReadString(root, "timestamp");
		if (since is { Length: > 0 } bound && timestamp is { Length: > 0 } && string.CompareOrdinal(timestamp, bound) < 0)
		{
			return;
		}

		var input = ReadLong(usage, "input");
		var output = ReadLong(usage, "output");
		var cacheRead = ReadLong(usage, "cacheRead");
		var cacheWrite = ReadLong(usage, "cacheWrite");
		var cost = ReadCost(usage);
		var counted = isMessage && type is not null ? 1 : 0;

		if (cost <= 0 && options.Rates?.Invoke(provider, model) is { } rates)
		{
			cost = rates.Input / 1_000_000.0 * input + rates.Output / 1_000_000.0 * output +
				rates.CacheRead / 1_000_000.0 * cacheRead + rates.CacheWrite / 1_000_000.0 * cacheWrite;
		}

		var date = timestamp is { Length: >= 10 } ? timestamp[..10] : string.Empty;
		Add(days, date, input, output, cacheRead, cacheWrite, counted, cost);
		Add(models, provider + "\u0000" + model, input, output, cacheRead, cacheWrite, counted, cost);
		Add(projects, project, input, output, cacheRead, cacheWrite, counted, cost);
	}

	private static void Add(Dictionary<string, Bucket> map, string key, long input, long output, long cacheRead, long cacheWrite, long messages, double cost)
	{
		if (!map.TryGetValue(key, out var bucket))
		{
			bucket = new Bucket();
			map[key] = bucket;
		}

		bucket.Add(input, output, cacheRead, cacheWrite, messages, cost);
	}

	private static string? ReadString(string line, string property)
	{
		using var document = JsonDocument.Parse(line);
		return ReadString(document.RootElement, property);
	}

	private static string? ReadString(JsonElement element, string property) =>
		element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

	private static long ReadLong(JsonElement element, string property) =>
		element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)
			? Math.Max(0, number)
			: 0;

	/// <summary>会话里写入的 <c>usage.cost.total</c>；没有 cost 对象时返回 0。</summary>
	private static double ReadCost(JsonElement usage) =>
		usage.TryGetProperty("cost", out var cost) && cost.ValueKind == JsonValueKind.Object &&
			cost.TryGetProperty("total", out var total) && total.ValueKind == JsonValueKind.Number && total.TryGetDouble(out var value) && value > 0
			? value
			: 0;

	private static StatsTotals Sum(IEnumerable<Bucket> buckets)
	{
		var sum = new Bucket();
		foreach (var bucket in buckets)
		{
			sum.Add(bucket.Input, bucket.Output, bucket.CacheRead, bucket.CacheWrite, bucket.Messages, bucket.Cost);
		}

		return sum.ToTotals();
	}

	private static string Field(string key, int index)
	{
		var parts = key.Split('\u0000');
		return index < parts.Length ? parts[index] : string.Empty;
	}

	/// <summary>可变累加器，避免每行都构造新纪录。</summary>
	internal sealed class Bucket(long input = 0, long output = 0, long cacheRead = 0, long cacheWrite = 0, long messages = 0, double cost = 0)
	{
		public long Input { get; private set; } = input;

		public long Output { get; private set; } = output;

		public long CacheRead { get; private set; } = cacheRead;

		public long CacheWrite { get; private set; } = cacheWrite;

		public long Messages { get; private set; } = messages;

		public double Cost { get; private set; } = cost;

		public long Tokens => Input + Output + CacheRead + CacheWrite;

		public void Add(long input, long output, long cacheRead, long cacheWrite, long messages, double cost)
		{
			Input += input;
			Output += output;
			CacheRead += cacheRead;
			CacheWrite += cacheWrite;
			Messages += messages;
			Cost += cost;
		}

		public StatsTotals ToTotals() => new()
		{
			Input = Input,
			Output = Output,
			CacheRead = CacheRead,
			CacheWrite = CacheWrite,
			Messages = Messages,
			Cost = Cost,
		};
	}
}
