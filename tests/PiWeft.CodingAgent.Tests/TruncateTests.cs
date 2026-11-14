using System.Text;
using PiWeft.CodingAgent.Tools;
using Xunit;

namespace PiWeft.CodingAgent.Tests;

/// `Truncate.Head`/`Tail` 现在按 span 扫行，不再 `Split` 出整份行列表。
/// 这里用旧实现做参照，随机输入逐字段比对，保证换实现不改行为。
public sealed class TruncateTests
{
	private static readonly string[] Fragments =
	[
		"a", "bc", "def", " ", "\t", "\r",
		"中", "文", "😀", "é", "ß",
		"\n", "\n\n", "line", "0123456789",
	];

	[Theory]
	[InlineData(0)]
	[InlineData(1)]
	[InlineData(2)]
	[InlineData(3)]
	public void HeadAndTailMatchTheReferenceImplementation(int seed)
	{
		var random = new Random(seed);
		for (var iteration = 0; iteration < 1500; iteration++)
		{
			var content = RandomText(random);
			var maxLines = random.Next(0, 10) == 0 ? int.MaxValue : random.Next(0, 12);
			var maxBytes = random.Next(0, 400);

			Assert.Equal(
				ReferenceHead(content, maxLines, maxBytes),
				Truncate.Head(content, maxLines, maxBytes));
			Assert.Equal(
				ReferenceTail(content, maxLines, maxBytes),
				Truncate.Tail(content, maxLines, maxBytes));
		}
	}

	[Fact]
	public void EmptyLinesAndZeroBudgetsMatchTheReference()
	{
		string[] contents =
		[
			"", "\n", "\n\n", "\n\n\n", "a", "a\n", "\na", "\n\na",
			"a\n\n", "a\nb", "a\nb\n", "\nfoo\n", "foo\n\n", "中\n😀\n",
		];
		int[] lineBudgets = [0, 1, 2, 3, int.MaxValue];
		long[] byteBudgets = [0, 1, 2, 3, 5, 10, 100];

		foreach (var content in contents)
		{
			foreach (var maxLines in lineBudgets)
			{
				foreach (var maxBytes in byteBudgets)
				{
					Assert.Equal(
						ReferenceHead(content, maxLines, maxBytes),
						Truncate.Head(content, maxLines, maxBytes));
					Assert.Equal(
						ReferenceTail(content, maxLines, maxBytes),
						Truncate.Tail(content, maxLines, maxBytes));
				}
			}
		}
	}

	private static string RandomText(Random random)
	{
		if (random.Next(20) == 0)
		{
			return string.Empty;
		}

		var builder = new StringBuilder();
		var count = random.Next(0, 120);
		for (var i = 0; i < count; i++)
		{
			builder.Append(Fragments[random.Next(Fragments.Length)]);
		}

		return builder.ToString();
	}

	private static TruncationResult ReferenceHead(string content, int maxLines, long maxBytes)
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

	private static TruncationResult ReferenceTail(string content, int maxLines, long maxBytes)
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

	private static List<string> SplitLines(string content)
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

	private static long ByteCount(string value) => Encoding.UTF8.GetByteCount(value);

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
