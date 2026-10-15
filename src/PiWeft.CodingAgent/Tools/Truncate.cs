using System.Text;

namespace PiWeft.CodingAgent.Tools;

/// <summary>截断结果。行数与字节数两个上限谁先命中就按谁截；永远不返回半行内容。</summary>
public sealed record TruncationResult
{
	public required string Content { get; init; }

	public bool Truncated { get; init; }

	/// <summary>取值 <c>lines</c>、<c>bytes</c>；未发生截断时为 <c>null</c>。</summary>
	public string? TruncatedBy { get; init; }

	public int TotalLines { get; init; }

	public long TotalBytes { get; init; }

	public int OutputLines { get; init; }

	public long OutputBytes { get; init; }

	/// <summary>第一行被从中间截断时为 true（尾部截断的边界情况）。</summary>
	public bool LastLinePartial { get; init; }

	/// <summary>仅第一行就超过字节上限时为 true（头部截断）。</summary>
	public bool FirstLineExceedsLimit { get; init; }

	public int MaxLines { get; init; }

	public long MaxBytes { get; init; }
}

/// <summary>对应 pi 的 <c>truncate.ts</c>。</summary>
public static class Truncate
{
	public const int DefaultMaxLines = 2000;
	public const long DefaultMaxBytes = 50 * 1024;

	/// <summary>单行 grep 匹配结果保留的最大字符数。</summary>
	public const int GrepMaxLineLength = 500;

	public static string FormatSize(long bytes) => bytes switch
	{
		< 1024 => $"{bytes}B",
		< 1024 * 1024 => $"{(bytes / 1024.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)}KB",
		_ => $"{(bytes / (1024.0 * 1024.0)).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)}MB",
	};

	/// <summary>保留前 N 行或前 N 字节，适用于开头更重要的读取。</summary>
	public static TruncationResult Head(string content, int maxLines = DefaultMaxLines, long maxBytes = DefaultMaxBytes)
	{
		var totalBytes = ByteCount(content);
		var lines = SplitLines(content);
		var totalLines = lines.Count;

		if (totalLines <= maxLines && totalBytes <= maxBytes)
		{
			return Untruncated(content, totalLines, totalBytes, maxLines, maxBytes);
		}

		if (lines.Count > 0 && ByteCount(lines[0]) > maxBytes)
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

		var kept = new List<string>();
		long outputBytes = 0;
		var truncatedBy = "lines";

		for (var i = 0; i < lines.Count && i < maxLines; i++)
		{
			var lineBytes = ByteCount(lines[i]) + (i > 0 ? 1 : 0);
			if (outputBytes + lineBytes > maxBytes)
			{
				truncatedBy = "bytes";
				break;
			}

			kept.Add(lines[i]);
			outputBytes += lineBytes;
		}

		if (kept.Count >= maxLines && outputBytes <= maxBytes)
		{
			truncatedBy = "lines";
		}

		var output = string.Join("\n", kept);
		return new TruncationResult
		{
			Content = output,
			Truncated = true,
			TruncatedBy = truncatedBy,
			TotalLines = totalLines,
			TotalBytes = totalBytes,
			OutputLines = kept.Count,
			OutputBytes = ByteCount(output),
			MaxLines = maxLines,
			MaxBytes = maxBytes,
		};
	}

	/// <summary>保留最后 N 行或最后 N 字节，适用于结尾更重要的命令输出。</summary>
	public static TruncationResult Tail(string content, int maxLines = DefaultMaxLines, long maxBytes = DefaultMaxBytes)
	{
		var totalBytes = ByteCount(content);
		var lines = SplitLines(content);
		var totalLines = lines.Count;

		if (totalLines <= maxLines && totalBytes <= maxBytes)
		{
			return Untruncated(content, totalLines, totalBytes, maxLines, maxBytes);
		}

		var kept = new List<string>();
		long outputBytes = 0;
		var truncatedBy = "lines";
		var lastLinePartial = false;

		for (var i = lines.Count - 1; i >= 0 && kept.Count < maxLines; i--)
		{
			var lineBytes = ByteCount(lines[i]) + (kept.Count > 0 ? 1 : 0);
			if (outputBytes + lineBytes > maxBytes)
			{
				truncatedBy = "bytes";
				if (kept.Count == 0)
				{
					// 最后一行本身就超过上限：保留它的尾部。
					var partial = TakeBytesFromEnd(lines[i], maxBytes);
					kept.Insert(0, partial);
					outputBytes = ByteCount(partial);
					lastLinePartial = true;
				}

				break;
			}

			kept.Insert(0, lines[i]);
			outputBytes += lineBytes;
		}

		if (kept.Count >= maxLines && outputBytes <= maxBytes)
		{
			truncatedBy = "lines";
		}

		var output = string.Join("\n", kept);
		return new TruncationResult
		{
			Content = output,
			Truncated = true,
			TruncatedBy = truncatedBy,
			TotalLines = totalLines,
			TotalBytes = totalBytes,
			OutputLines = kept.Count,
			OutputBytes = ByteCount(output),
			LastLinePartial = lastLinePartial,
			MaxLines = maxLines,
			MaxBytes = maxBytes,
		};
	}

	/// <summary>把一行截断到 <paramref name="maxChars"/> 并追加标记；grep 匹配结果会用到。</summary>
	public static (string Text, bool WasTruncated) Line(string line, int maxChars = GrepMaxLineLength) =>
		line.Length <= maxChars
			? (line, false)
			: (string.Concat(line.AsSpan(0, maxChars), "... [truncated]"), true);

	internal static long ByteCount(string value) => Encoding.UTF8.GetByteCount(value);

	internal static List<string> SplitLines(string content)
	{
		if (content.Length == 0)
		{
			return [];
		}

		var lines = new List<string>(content.Split('\n'));
		if (content.EndsWith('\n') && lines.Count > 0)
		{
			lines.RemoveAt(lines.Count - 1);
		}

		return lines;
	}

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

	/// <summary>在字符边界上保留最后 <paramref name="maxBytes"/> 个 UTF-8 字节。</summary>
	private static string TakeBytesFromEnd(string value, long maxBytes)
	{
		var bytes = Encoding.UTF8.GetBytes(value);
		if (bytes.LongLength <= maxBytes)
		{
			return value;
		}

		var start = (int)Math.Max(0, bytes.LongLength - maxBytes);
		while (start < bytes.Length && (bytes[start] & 0xC0) == 0x80)
		{
			start++;
		}

		return Encoding.UTF8.GetString(bytes, start, bytes.Length - start);
	}
}
