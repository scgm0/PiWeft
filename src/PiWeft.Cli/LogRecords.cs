using System.Buffers;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Logging;

namespace PiWeft.Cli;

/// 一条应用日志记录。<see cref="Time"/> 是 UTC：文件前缀用 utc-longdate，展示时转本地。
public readonly record struct LogRecord(DateTimeOffset Time, LogLevel Level, string Category, string Message);

/// 日志查询条件。页码从 0 开始，<see cref="PageSize"/> 上限 <see cref="LogRecords.MaxPageSize"/>。
public sealed record LogQuery
{
	/// 页码，0 是最新一页。
	public int Page { get; init; }

	/// 每页条数。
	public int PageSize { get; init; } = 50;

	/// 最低级别：只看该级别及以上。
	public LogLevel MinimumLevel { get; init; } = LogLevel.Trace;

	/// 关键词，匹配类别与正文，忽略大小写。
	public string? Search { get; init; }
}

/// 一页查询结果。最新的记录在前。
public sealed record LogPage(IReadOnlyList<LogRecord> Records, int Total, int Page, int PageSize)
{
	/// 当前页之后还有记录。
	public bool HasMore => (Page + 1) * PageSize < Total;
}

/// 应用日志的读取与查询。
/// <remarks>
/// 只扫最近若干个文件（<see cref="MaxFiles"/>），每个文件只读末尾 <see cref="MaxBytesPerFile"/>。
/// 设置页每次筛选都要重跑一遍，所以按字符块倒着扫：命中的行才建字符串，翻页只物化当前页。
/// 整批物化的代价很具体——1.4 MB 日志全量解析要分配 8 MB、耗时 16 ms，而其中真正显示的只有 50 行。
/// </remarks>
public static class LogRecords
{
	/// 默认扫描的文件数（按修改时间从新到旧）。
	public const int MaxFiles = 4;

	/// 单个文件默认读取的尾部字节数。
	/// <remarks>
	/// 比 1 MiB 少 1 KiB：<see cref="ArrayPool{T}.Shared"/> 只缓存到 1 M 个元素，请求再大就每次新分配。
	/// 满 1 MiB 的日志文件按 1 MiB 请求，等于每查一次都新开 1 MiB 字节 + 2 MiB 字符缓冲。
	/// </remarks>
	public const long MaxBytesPerFile = (1024 * 1024) - 1024;

	/// 每页条数上限：再多的行一次性交给 UI 只会拖垮渲染。
	public const int MaxPageSize = 200;

	/// 无上限读取时单文件最多进内存的字节数，防一个畸形大文件把进程拖死。
	private const long MaxReadBytes = 8L * 1024 * 1024;

	/// 日志文件，最新在前。
	public static IReadOnlyList<FileInfo> Files(string? directory = null, int maxFiles = int.MaxValue)
	{
		directory ??= AppLog.DirectoryPath;
		try
		{
			return string.IsNullOrEmpty(directory) || !Directory.Exists(directory)
				? []
				: [.. new DirectoryInfo(directory).EnumerateFiles("*.log")
					.OrderByDescending(static file => file.LastWriteTimeUtc)
					.Take(Math.Max(0, maxFiles))];
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return [];
		}
	}

	/// 文件集合的总字节数。已枚举过文件时用它，别再扫一遍目录。
	public static long TotalBytes(IReadOnlyList<FileInfo> files)
	{
		long total = 0;
		for (var index = 0; index < files.Count; index++)
		{
			try
			{
				total += files[index].Length;
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				// 单个文件统计失败不影响整体。
			}
		}

		return total;
	}

	/// 删除日志文件，返回删掉的个数。
	/// <remarks>
	/// 还在写的文件（本窗口的当前文件、其它窗口与 CLI）会拿不到独占句柄，直接跳过：
	/// 删掉正在追加的文件在 Windows 上本来也会失败，在 Unix 上则是写入一个已删除的 inode。
	/// </remarks>
	public static int Clear(string? directory = null)
	{
		var removed = 0;
		foreach (var file in Files(directory))
		{
			try
			{
				using (new FileStream(file.FullName, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
				file.Delete();
				removed++;
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				// 正在使用的文件跳过。
			}
		}

		return removed;
	}

	/// 读日志文件并按条件分页。
	/// <param name="query">查询条件。</param>
	/// <param name="directory">日志目录，空则用 <see cref="AppLog.DirectoryPath"/>。</param>
	/// <param name="maxFiles">最多扫描的文件数。</param>
	/// <param name="maxBytesPerFile">单文件读取上限，0 表示整份读。</param>
	public static LogPage Query(
		LogQuery query,
		string? directory = null,
		int maxFiles = MaxFiles,
		long maxBytesPerFile = MaxBytesPerFile) =>
		Query(query, Files(directory, maxFiles), maxBytesPerFile);

	/// 同上，文件集合由调用方给：目录只枚举一次时用这个重载。
	public static LogPage Query(LogQuery query, IReadOnlyList<FileInfo> files, long maxBytesPerFile = MaxBytesPerFile)
	{
		var pageSize = Math.Clamp(query.PageSize, 1, MaxPageSize);
		var page = Math.Max(0, query.Page);
		var records = new List<LogRecord>(Math.Min(pageSize, 64));
		var total = Scan(query, files, maxBytesPerFile, page * pageSize, pageSize, records);
		return new LogPage(records, total, page, pageSize);
	}

	/// 按条件读取记录，最新的在前。整批物化，诊断包这类一次性用途用它。
	public static IReadOnlyList<LogRecord> Read(
		LogQuery? query = null,
		string? directory = null,
		int maxFiles = MaxFiles,
		long maxBytesPerFile = MaxBytesPerFile)
	{
		var records = new List<LogRecord>();
		Scan(query ?? new LogQuery(), Files(directory, maxFiles), maxBytesPerFile, 0, int.MaxValue, records);
		return records;
	}

	/// 解析一行。带前缀的行开始一条新记录，其余行按旧日志里的异常栈续行处理。
	public static bool TryParse(string line, out LogRecord record)
	{
		if (!TryParseSpans(line, out var time, out var level, out var category, out var message))
		{
			record = default;
			return false;
		}

		record = new LogRecord(time, level, category.ToString(), message.ToString());
		return true;
	}

	/// 把一行日志格式化成与文件一致的前缀行；续行已经并回正文，这里保证单行。
	public static string Format(LogRecord record, LogRedactor? redactor = null)
	{
		var text = string.Create(CultureInfo.InvariantCulture,
			$"{record.Time.UtcDateTime:yyyy-MM-dd HH:mm:ss.fff} [{Short(record.Level)}] {record.Category} | {record.Message}");
		return redactor is null ? text : redactor.Line(text);
	}

	/// 级别缩写，与 ZLogger 的 <c>short</c> 格式一致。
	public static string Short(LogLevel level) => level switch
	{
		LogLevel.Trace => "TRC",
		LogLevel.Debug => "DBG",
		LogLevel.Information => "INF",
		LogLevel.Warning => "WRN",
		LogLevel.Error => "ERR",
		LogLevel.Critical => "CRT",
		_ => "NON",
	};

	/// 倒着扫文件：先出最新一条，命中窗口的记录才建字符串。
	/// <returns>命中的总条数。</returns>
	private static int Scan(
		LogQuery query,
		IReadOnlyList<FileInfo> files,
		long maxBytesPerFile,
		int windowStart,
		int windowCount,
		List<LogRecord> sink)
	{
		var search = string.IsNullOrWhiteSpace(query.Search)
			? ReadOnlySpan<char>.Empty
			: query.Search.Trim().AsSpan();
		var windowEnd = windowCount == int.MaxValue ? int.MaxValue : windowStart + windowCount;
		var matched = 0;
		foreach (var file in files)
		{
			var budget = ByteBudget(file, maxBytesPerFile);
			var bytes = ArrayPool<byte>.Shared.Rent(budget);
			var buffer = ArrayPool<char>.Shared.Rent(budget + 2);
			try
			{
				if (!TryReadTail(file.FullName, maxBytesPerFile, bytes, buffer, out var start, out var end))
				{
					continue;
				}

				var span = buffer.AsSpan(start, end - start);
				// 旧日志的异常栈是一行一条、没有前缀。倒着扫时它出现在自己的前缀行之前，
				// 先攒起来，碰到前缀行再并回正文；新日志不会再产生这种行，这里不给热路径添分配。
				StringBuilder? continuation = null;
				while (!span.IsEmpty)
				{
					// 从尾部切一行：文件里是旧到新，倒着读出来就是「新的在前」。
					var newline = span.LastIndexOf('\n');
					var line = newline < 0 ? span : span[(newline + 1)..];
					span = newline < 0 ? default : span[..newline];
					if (!line.IsEmpty && line[^1] == '\r')
					{
						line = line[..^1];
					}

					if (line.IsEmpty)
					{
						continue;
					}

					if (!TryParseSpans(line, out var time, out var level, out var category, out var message))
					{
						// 倒着攒：后见到的在文件里更靠前，插到最前面才是原顺序。
						continuation ??= new StringBuilder();
						if (continuation.Length > 0)
						{
							continuation.Insert(0, ' ');
						}

						continuation.Insert(0, line);
						continue;
					}

					// 续行属于紧挨着的这条前缀行：记录被筛掉也要清掉，否则会粘到更老的一条上。
					if (continuation is { Length: > 0 })
					{
						message = string.Concat(message, " ", continuation.ToString());
						continuation.Clear();
					}

					if (level < query.MinimumLevel ||
						(!search.IsEmpty &&
							!category.Contains(search, StringComparison.OrdinalIgnoreCase) &&
							!message.Contains(search, StringComparison.OrdinalIgnoreCase)))
					{
						continue;
					}

					if (matched >= windowStart && matched < windowEnd)
					{
						sink.Add(new LogRecord(time, level, category.ToString(), message.ToString()));
					}

					matched++;
				}
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				// 读不出来就跳过这个文件：面板显示已读到的部分。
			}
			finally
			{
				ArrayPool<char>.Shared.Return(buffer);
				ArrayPool<byte>.Shared.Return(bytes);
			}
		}

		return matched;
	}

	/// 一个文件最多读多少字节。
	private static int ByteBudget(FileInfo file, long maxBytesPerFile)
	{
		long length;
		try
		{
			length = file.Length;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return 1024;
		}

		var budget = maxBytesPerFile > 0 ? Math.Min(length, maxBytesPerFile) : Math.Min(length, MaxReadBytes);
		return (int)Math.Clamp(budget, 1024, MaxReadBytes) + 2;
	}

	/// 把文件尾部读进字符缓冲。从中间起读时第一行是半条，跳过它。
	/// <remarks>
	/// 不走 <see cref="StreamReader"/>：它自带的 64 KB 缓冲在每次查询里都是一次 4 个文件的新分配，
	/// 比解码本身还贵。这里用定位读，句柄之外没有缓冲，字节与字符缓冲都从池里租。
	/// </remarks>
	private static bool TryReadTail(
		string path,
		long maxBytesPerFile,
		byte[] bytes,
		char[] buffer,
		out int start,
		out int end)
	{
		start = 0;
		end = 0;
		using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
		var length = RandomAccess.GetLength(handle);
		var seeked = maxBytesPerFile > 0 && length > maxBytesPerFile;
		var offset = seeked ? length - maxBytesPerFile : 0L;

		// 两个池租来的数组长度不一定相等：读取上限要按字符缓冲算，否则解码会越界。
		var limit = Math.Min(bytes.Length, buffer.Length - 1);
		var read = 0;
		var complete = false;
		while (read < limit)
		{
			var count = RandomAccess.Read(handle, bytes.AsSpan(read, limit - read), offset + read);
			if (count <= 0)
			{
				complete = true;
				break;
			}

			read += count;
		}

		var chars = Encoding.UTF8.GetChars(bytes.AsSpan(0, read), buffer);
		if (!complete && chars > 0)
		{
			// 文件在读到一半时又长了：末尾那条是半行，留着会把半截路径当成一条真日志。
			var newline = buffer.AsSpan(0, chars).LastIndexOf('\n');
			chars = newline < 0 ? 0 : newline;
		}
		if (!seeked && chars > 0 && buffer[0] == '\uFEFF')
		{
			start = 1;
		}

		if (seeked)
		{
			// 从中间起读的第一行是半条：拼成记录会把半截路径说成一条真日志。
			var newline = buffer.AsSpan(start, chars - start).IndexOf('\n');
			start = newline < 0 ? chars : start + newline + 1;
		}

		end = chars;
		return end > start;
	}

	/// 解析前缀，返回各段的切片：不建字符串，命中过滤条件之后才物化。
	/// <remarks>
	/// 时间戳按固定列手工拆：<see cref="DateTimeOffset.TryParse(ReadOnlySpan{char}, IFormatProvider, DateTimeStyles, out DateTimeOffset)"/>
	/// 在 1.5 MB 日志上要花掉查询时间的一大半，而这份格式是本进程自己写出来的。
	/// </remarks>
	private static bool TryParseSpans(
		ReadOnlySpan<char> line,
		out DateTimeOffset time,
		out LogLevel level,
		out ReadOnlySpan<char> category,
		out ReadOnlySpan<char> message)
	{
		time = default;
		level = LogLevel.None;
		category = default;
		message = default;
		if (line.Length < 32 ||
			line[4] != '-' || line[7] != '-' || line[10] != ' ' || line[13] != ':' || line[16] != ':' ||
			!TryNumber(line, 0, 4, out var year) || !TryNumber(line, 5, 2, out var month) ||
			!TryNumber(line, 8, 2, out var day) || !TryNumber(line, 11, 2, out var hour) ||
			!TryNumber(line, 14, 2, out var minute) || !TryNumber(line, 17, 2, out var second))
		{
			return false;
		}

		// 小数位数不固定（毫秒或 7 位），量出它的位置；超出的位数按 100 ns 精度截掉。
		var index = 19;
		var fraction = 0L;
		if (index < line.Length && line[index] == '.')
		{
			var digits = ++index;
			while (index < line.Length && char.IsAsciiDigit(line[index]))
			{
				if (index - digits < 7)
				{
					fraction = (fraction * 10) + (line[index] - '0');
				}

				index++;
			}

			var count = Math.Min(index - digits, 7);
			if (count == 0)
			{
				return false;
			}

			for (; count < 7; count++)
			{
				fraction *= 10;
			}
		}

		if (year is < 1 or > 9999 || month is < 1 or > 12 || day < 1 ||
			day > DateTime.DaysInMonth(year, month) || hour > 23 || minute > 59 || second > 59)
		{
			return false;
		}

		time = new DateTimeOffset(
			new DateTime(year, month, day, hour, minute, second, DateTimeKind.Utc).Ticks + fraction,
			TimeSpan.Zero);

		if (index + 2 >= line.Length || line[index] != ' ' || line[index + 1] != '[')
		{
			return false;
		}

		var close = line[index..].IndexOf(']');
		if (close < 0)
		{
			return false;
		}

		close += index;
		if (!TryLevel(line[(index + 2)..close], out level))
		{
			return false;
		}

		var rest = line[(close + 1)..].TrimStart();
		var separator = rest.IndexOf(" | ", StringComparison.Ordinal);
		category = separator < 0 ? default : rest[..separator];
		message = separator < 0 ? rest : rest[(separator + 3)..];
		return true;
	}

	/// 定长十进制段。比 <c>int.Parse</c> 省掉一个 span 切片与溢出检查。
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static bool TryNumber(ReadOnlySpan<char> line, int start, int length, out int value)
	{
		value = 0;
		if ((uint)(start + length) > (uint)line.Length)
		{
			return false;
		}

		for (var index = start; index < start + length; index++)
		{
			var digit = (uint)(line[index] - '0');
			if (digit > 9)
			{
				return false;
			}

			value = (value * 10) + (int)digit;
		}

		return true;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static bool TryLevel(ReadOnlySpan<char> token, out LogLevel level)
	{
		switch (token)
		{
			case "TRC": level = LogLevel.Trace; return true;
			case "DBG": level = LogLevel.Debug; return true;
			case "INF": level = LogLevel.Information; return true;
			case "WRN": level = LogLevel.Warning; return true;
			case "ERR": level = LogLevel.Error; return true;
			case "CRT": level = LogLevel.Critical; return true;
			default: level = LogLevel.None; return false;
		}
	}
}
