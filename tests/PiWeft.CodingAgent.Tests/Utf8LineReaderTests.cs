using System.Text;
using PiWeft.CodingAgent.Session;
using Xunit;

namespace PiWeft.CodingAgent.Tests;

public sealed class Utf8LineReaderTests
{
	[Fact]
	public void ReadsBomCrlfLongLinesAndMissingTrailingNewline()
	{
		var path = Path.Combine(Path.GetTempPath(), "piweft-lines-" + Guid.NewGuid().ToString("N") + ".jsonl");
		try
		{
			var longText = new string('x', 70_000);
			var content = "\uFEFFfirst\r\n" + longText + "\n" + "中文最后一行";
			File.WriteAllBytes(path, Encoding.UTF8.GetBytes(content));

			using var reader = new Utf8LineReader(path);
			var lines = new List<string>();
			var memories = new List<byte[]>();
			while (reader.TryReadLine(out var line))
			{
				lines.Add(Encoding.UTF8.GetString(line));
				memories.Add(reader.CurrentLineMemory.ToArray());
				Assert.True(reader.CurrentLineMemory.Span.SequenceEqual(line));
			}

			Assert.Equal(3, lines.Count);
			Assert.Equal("first", lines[0]);
			Assert.Equal(longText, lines[1]);
			Assert.Equal("中文最后一行", lines[2]);
			Assert.Equal(Encoding.UTF8.GetBytes("first"), memories[0]);
			Assert.Equal(Encoding.UTF8.GetBytes(longText), memories[1]);
			Assert.Equal(Encoding.UTF8.GetBytes("中文最后一行"), memories[2]);
		}
		finally
		{
			File.Delete(path);
		}
	}

	[Fact]
	public void DropsOversizedLinesButKeepsFollowingLinesAndOffsets()
	{
		var path = Path.Combine(Path.GetTempPath(), "piweft-lines-" + Guid.NewGuid().ToString("N") + ".jsonl");
		try
		{
			var longText = new string('x', 100_000);
			File.WriteAllBytes(path, Encoding.UTF8.GetBytes("first\n" + longText + "\nafter\n"));

			using var reader = new Utf8LineReader(path, maxLineBytes: 1024);
			var lines = new List<string>();
			var offsets = new List<long>();
			while (reader.TryReadLine(out var line))
			{
				lines.Add(Encoding.UTF8.GetString(line));
				offsets.Add(reader.BytesRead);
			}

			Assert.Equal(["first", string.Empty, "after"], lines);
			// 丢弃的行仍消费到换行，断点仍落在行边界。
			Assert.Equal(new FileInfo(path).Length, offsets[^1]);

			// 从第一行之后续读：超长行仍被丢弃，第三行正常返回。
			using var resumed = new Utf8LineReader(path, offsets[0], maxLineBytes: 1024);
			Assert.True(resumed.TryReadLine(out var resumedLine));
			Assert.Empty(Encoding.UTF8.GetString(resumedLine));
			Assert.True(resumed.TryReadLine(out resumedLine));
			Assert.Equal("after", Encoding.UTF8.GetString(resumedLine));
		}
		finally
		{
			File.Delete(path);
		}
	}

	[Fact]
	public void DropsAnOversizedFinalLineWithoutTrailingNewline()
	{
		var path = Path.Combine(Path.GetTempPath(), "piweft-lines-" + Guid.NewGuid().ToString("N") + ".jsonl");
		try
		{
			File.WriteAllBytes(path, Encoding.UTF8.GetBytes("first\n" + new string('x', 5000)));
			using var reader = new Utf8LineReader(path, maxLineBytes: 1024);

			Assert.True(reader.TryReadLine(out var first));
			Assert.Equal("first", Encoding.UTF8.GetString(first));
			// 末尾的超长行没有换行，也要作为一行空内容交回，再结束。
			Assert.True(reader.TryReadLine(out var oversized));
			Assert.Empty(Encoding.UTF8.GetString(oversized));
			Assert.False(reader.TryReadLine(out _));
			Assert.Equal(new FileInfo(path).Length, reader.BytesRead);
		}
		finally
		{
			File.Delete(path);
		}
	}

	[Fact]
	public void KeepsALineThatIsExactlyTheLimit()
	{
		var path = Path.Combine(Path.GetTempPath(), "piweft-lines-" + Guid.NewGuid().ToString("N") + ".jsonl");
		try
		{
			File.WriteAllBytes(path, Encoding.UTF8.GetBytes("abcd\nabcde\n"));
			using var reader = new Utf8LineReader(path, maxLineBytes: 4);

			Assert.True(reader.TryReadLine(out var exact));
			Assert.Equal("abcd", Encoding.UTF8.GetString(exact));
			// 超过一个字节就丢弃。
			Assert.True(reader.TryReadLine(out var oversized));
			Assert.Empty(Encoding.UTF8.GetString(oversized));
		}
		finally
		{
			File.Delete(path);
		}
	}

	[Fact]
	public void CountsTheLimitAfterStrippingCarriageReturn()
	{
		var path = Path.Combine(Path.GetTempPath(), "piweft-lines-" + Guid.NewGuid().ToString("N") + ".jsonl");
		try
		{
			File.WriteAllBytes(path, Encoding.UTF8.GetBytes("abcd\r\nabcde\r\n"));
			using var reader = new Utf8LineReader(path, maxLineBytes: 4);

			Assert.True(reader.TryReadLine(out var exact));
			Assert.Equal("abcd", Encoding.UTF8.GetString(exact));
			Assert.True(reader.TryReadLine(out var oversized));
			Assert.Empty(Encoding.UTF8.GetString(oversized));
		}
		finally
		{
			File.Delete(path);
		}
	}

	[Fact]
	public void ReturnsFalseForAnEmptyFile()
	{
		var path = Path.Combine(Path.GetTempPath(), "piweft-empty-" + Guid.NewGuid().ToString("N") + ".jsonl");
		try
		{
			File.WriteAllBytes(path, []);
			using var reader = new Utf8LineReader(path);
			Assert.False(reader.TryReadLine(out _));
		}
		finally
		{
			File.Delete(path);
		}
	}
}
