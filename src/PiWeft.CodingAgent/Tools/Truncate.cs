using System.Buffers;
using System.Text;

namespace PiWeft.CodingAgent.Tools;

public sealed record TruncationResult
{
	public required string Content { get; init; }

	public bool Truncated { get; init; }

	public string? TruncatedBy { get; init; }

	public int TotalLines { get; init; }

	public long TotalBytes { get; init; }

	public int OutputLines { get; init; }

	public long OutputBytes { get; init; }

	public bool LastLinePartial { get; init; }

	public bool FirstLineExceedsLimit { get; init; }

	public int MaxLines { get; init; }

	public long MaxBytes { get; init; }
}

public static class Truncate
{
	public const int DefaultMaxLines = 2000;
	public const long DefaultMaxBytes = 50 * 1024;

	public const int GrepMaxLineLength = 500;

	public static string FormatSize(long bytes) => bytes switch
	{
		< 1024 => $"{bytes}B",
		< 1024 * 1024 => $"{(bytes / 1024.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)}KB",
		_ => $"{(bytes / (1024.0 * 1024.0)).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)}MB",
	};

	public static TruncationResult Head(string content, int maxLines = DefaultMaxLines, long maxBytes = DefaultMaxBytes)
	{
		var span = content.AsSpan();
		var totalBytes = Encoding.UTF8.GetByteCount(span);
		var totalLines = CountLines(span);

		if (totalLines <= maxLines && totalBytes <= maxBytes)
		{
			return Untruncated(content, totalLines, totalBytes, maxLines, maxBytes);
		}

		if (totalLines > 0 && Utf8ByteCount(FirstLine(span)) > maxBytes)
		{
			return new TruncationResult
			{
				Content = string.Empty,
				Truncated = true,
				TruncatedBy = "bytes",
				TotalLines = totalLines,
				TotalBytes = totalBytes,
				OutputLines = 0,
				OutputBytes = 0,
				FirstLineExceedsLimit = true,
				MaxLines = maxLines,
				MaxBytes = maxBytes,
			};
		}

		var kept = 0;
		long outputBytes = 0;
		var outputEnd = 0;
		var position = 0;
		var truncatedBy = "lines";

		while (position < span.Length && kept < maxLines)
		{
			var lineEnd = LineEnd(span, position);
			var lineBytes = Utf8ByteCount(span[position..lineEnd]) + (kept > 0 ? 1 : 0);
			if (outputBytes + lineBytes > maxBytes)
			{
				truncatedBy = "bytes";
				break;
			}

			kept++;
			outputBytes += lineBytes;
			outputEnd = lineEnd;
			if (lineEnd == span.Length)
			{
				break;
			}

			position = lineEnd + 1;
		}

		if (kept >= maxLines && outputBytes <= maxBytes)
		{
			truncatedBy = "lines";
		}

		var output = span[..outputEnd].ToString();
		return new TruncationResult
		{
			Content = output,
			Truncated = true,
			TruncatedBy = truncatedBy,
			TotalLines = totalLines,
			TotalBytes = totalBytes,
			OutputLines = kept,
			OutputBytes = ByteCount(output),
			MaxLines = maxLines,
			MaxBytes = maxBytes,
		};
	}

	public static TruncationResult Tail(string content, int maxLines = DefaultMaxLines, long maxBytes = DefaultMaxBytes)
	{
		var span = content.AsSpan();
		var totalBytes = Encoding.UTF8.GetByteCount(span);
		var totalLines = CountLines(span);

		if (totalLines <= maxLines && totalBytes <= maxBytes)
		{
			return Untruncated(content, totalLines, totalBytes, maxLines, maxBytes);
		}

		// SplitLines 会丢掉结尾换行产生的空行；尾部切片也要按同样口径找最后一行。
		var contentEnd = span.Length;
		if (contentEnd > 0 && span[contentEnd - 1] == '\n')
		{
			contentEnd--;
		}

		var kept = 0;
		long outputBytes = 0;
		var outputStart = contentEnd;
		var lineEnd = contentEnd;
		var atStart = span.Length == 0;
		var truncatedBy = "lines";
		var lastLinePartial = false;
		string? partial = null;
		var keptAllBlank = true;

		while (!atStart && kept < maxLines)
		{
			var lineStart = lineEnd;
			while (lineStart > 0 && span[lineStart - 1] != '\n')
			{
				lineStart--;
			}

			var line = span[lineStart..lineEnd];
			// 行字节数只算一次：partial 分支还要用它换算成「换行 + 尾部」的增量。
			var lineByteCount = Utf8ByteCount(line);
			var lineBytes = lineByteCount + (kept > 0 ? 1 : 0);
			if (outputBytes + lineBytes > maxBytes)
			{
				truncatedBy = "bytes";
				// 这一行超限、后面又只有空行时，取这一行的尾部：否则输出只剩空行，看起来像没有输出。
				var remainingBytes = maxBytes - outputBytes - (kept > 0 ? 1 : 0);
				if (kept == 0 || (!IsBlank(line) && keptAllBlank && remainingBytes > 0))
				{
					partial = TakeBytesFromEnd(line, lineByteCount, remainingBytes);
					// 与 pi 的字节账一致：尾部与已保留行之间多一个换行，后续截断归类看这个数。
					outputBytes += lineBytes - lineByteCount + Utf8ByteCount(partial);
					kept++;
					lastLinePartial = true;
				}

				break;
			}

			kept++;
			outputBytes += lineBytes;
			outputStart = lineStart;
			keptAllBlank &= IsBlank(line);
			if (lineStart == 0)
			{
				atStart = true;
			}
			else
			{
				lineEnd = lineStart - 1;
			}
		}

		if (kept >= maxLines && outputBytes <= maxBytes)
		{
			truncatedBy = "lines";
		}

		var tail = span[outputStart..contentEnd].ToString();
		var output = partial is null
			? tail
			: kept > 1 ? string.Concat(partial, "\n", tail) : partial;
		return new TruncationResult
		{
			Content = output,
			Truncated = true,
			TruncatedBy = truncatedBy,
			TotalLines = totalLines,
			TotalBytes = totalBytes,
			OutputLines = kept,
			OutputBytes = ByteCount(output),
			LastLinePartial = lastLinePartial,
			MaxLines = maxLines,
			MaxBytes = maxBytes,
		};
	}

	public static (string Text, bool WasTruncated) Line(string line, int maxChars = GrepMaxLineLength) =>
		line.Length <= maxChars
			? (line, false)
			: (string.Concat(SliceHead(line, maxChars), "... [truncated]"), true);

	/// <summary>最多取前 <paramref name="maxUnits"/> 个 UTF-16 码元，不劈开代理对。</summary>
	public static string SliceHead(string text, int maxUnits)
	{
		var end = Math.Clamp(maxUnits, 0, text.Length);
		return end > 0 && end < text.Length &&
			char.IsHighSurrogate(text[end - 1]) && char.IsLowSurrogate(text[end])
			? text[..(end - 1)]
			: text[..end];
	}

	/// <summary>最多取后 <paramref name="maxUnits"/> 个 UTF-16 码元，不劈开代理对。</summary>
	public static string SliceTail(string text, int maxUnits)
	{
		if (maxUnits <= 0)
		{
			return string.Empty;
		}

		var start = Math.Max(0, text.Length - maxUnits);
		return start > 0 && start < text.Length &&
			char.IsLowSurrogate(text[start]) && char.IsHighSurrogate(text[start - 1])
			? text[(start + 1)..]
			: text[start..];
	}

	internal static long ByteCount(string value) => Encoding.UTF8.GetByteCount(value);

	/// <summary>一段文本的 UTF-8 字节数；调用方已有 span 时不复制成字符串。</summary>
	internal static long ByteCount(ReadOnlySpan<char> value) => Utf8ByteCount(value);

	private static int CountLines(ReadOnlySpan<char> content)
	{
		if (content.IsEmpty)
		{
			return 0;
		}

		var lines = content.Count('\n') + 1;
		return content[^1] == '\n' ? lines - 1 : lines;
	}

	private static ReadOnlySpan<char> FirstLine(ReadOnlySpan<char> content)
	{
		var newline = content.IndexOf('\n');
		return newline < 0 ? content : content[..newline];
	}

	private static int LineEnd(ReadOnlySpan<char> content, int start)
	{
		var newline = content[start..].IndexOf('\n');
		return newline < 0 ? content.Length : start + newline;
	}

	/// 绝大多数 shell 输出是 ASCII，先用 <see cref="Ascii.IsValid(ReadOnlySpan{char})"/> 走免编码器的路。
	private static long Utf8ByteCount(ReadOnlySpan<char> value) =>
		Ascii.IsValid(value) ? value.Length : Encoding.UTF8.GetByteCount(value);

	private static TruncationResult Untruncated(string content, int totalLines, long totalBytes, int maxLines, long maxBytes) =>
		new()
		{
			Content = content,
			Truncated = false,
			TruncatedBy = null,
			TotalLines = totalLines,
			TotalBytes = totalBytes,
			OutputLines = totalLines,
			OutputBytes = totalBytes,
			MaxLines = maxLines,
			MaxBytes = maxBytes,
		};

	/// <summary>行内容只有空白时为 true（与 pi 的 <c>trim().length === 0</c> 同义）。</summary>
	private static bool IsBlank(ReadOnlySpan<char> line) => line.Trim().IsEmpty;

	/// 取一段 UTF-8 文本的尾部，不劈开多字节字符。直接按 UTF-16 切片算字节，
	/// 不为超长行分配整串字符串与整份 UTF-8 字节数组（单行几十 MB 的日志很常见）。
	private static string TakeBytesFromEnd(ReadOnlySpan<char> value, long byteCount, long maxBytes)
	{
		if (maxBytes <= 0 || value.IsEmpty)
		{
			return string.Empty;
		}

		if (byteCount <= maxBytes)
		{
			return value.ToString();
		}

		var start = value.Length;
		long bytes = 0;
		while (start > 0)
		{
			var status = Rune.DecodeLastFromUtf16(value[..start], out var rune, out var consumed);
			// 孤立代理在 Encoding.UTF8 里写成替换字符 EF BF BD，占 3 字节、消耗一个 char。
			var size = status == OperationStatus.Done ? rune.Utf8SequenceLength : 3;
			if (status != OperationStatus.Done)
			{
				consumed = 1;
			}

			if (bytes + size > maxBytes)
			{
				break;
			}

			bytes += size;
			start -= consumed;
		}

		return value[start..].ToString();
	}
}
