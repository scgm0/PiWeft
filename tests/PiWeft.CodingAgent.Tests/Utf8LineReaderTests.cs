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
			while (reader.TryReadLine(out var line))
			{
				lines.Add(Encoding.UTF8.GetString(line));
			}

			Assert.Equal(3, lines.Count);
			Assert.Equal("first", lines[0]);
			Assert.Equal(longText, lines[1]);
			Assert.Equal("中文最后一行", lines[2]);
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
