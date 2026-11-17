using Microsoft.Extensions.Logging;
using PiWeft.Cli;
using Xunit;

namespace PiWeft.Cli.Tests;

/// 日志文件的解析与查询：一行一条记录，筛选在分页之前完成。
public sealed class LogRecordsTests : IDisposable
{
	private readonly string _directory = Path.Combine(Path.GetTempPath(), "piweft-logrec-" + Guid.NewGuid().ToString("N")[..8]);

	public LogRecordsTests() => Directory.CreateDirectory(_directory);

	public void Dispose()
	{
		try
		{
			Directory.Delete(_directory, recursive: true);
		}
		catch (IOException)
		{
		}
	}

	private static string Line(string time, string level, string category, string message) =>
		$"{time} [{level}] {category} | {message}";

	[Fact]
	public void ParsesPrefixAndMessage()
	{
		Assert.True(LogRecords.TryParse(Line("2026-11-17 05:18:52.385", "INF", "PiWeft.Cli.AppLog", "started"), out var record));
		Assert.Equal(LogLevel.Information, record.Level);
		Assert.Equal("PiWeft.Cli.AppLog", record.Category);
		Assert.Equal("started", record.Message);
		Assert.Equal(TimeSpan.Zero, record.Time.Offset);
		Assert.Equal(2026, record.Time.Year);
	}

	[Fact]
	public void ParsesSevenDigitFraction()
	{
		Assert.True(LogRecords.TryParse(Line("2026-11-17 05:18:52.3851234", "ERR", "Cat", "boom"), out var record));
		Assert.Equal(LogLevel.Error, record.Level);
		Assert.Equal("boom", record.Message);
	}

	[Fact]
	public void EmptyMessageIsKept()
	{
		Assert.True(LogRecords.TryParse("2026-11-17 05:18:52.385 [WRN] Cat | ", out var record));
		Assert.Equal(string.Empty, record.Message);
	}

	[Theory]
	[InlineData("not a log line")]
	[InlineData("2026-11-17 05:18:52.385 [???] Cat | x")]
	[InlineData("2026-13-17 05:18:52.385 [INF] Cat | x")]
	public void RejectsNonRecords(string line) => Assert.False(LogRecords.TryParse(line, out _));

	[Fact]
	public void QueriesNewestFirstAndFiltersByLevel()
	{
		File.WriteAllLines(Path.Combine(_directory, "piweft-cli-1-20261117-000.log"),
		[
			Line("2026-11-17 05:00:00.000", "INF", "Cat", "first"),
			Line("2026-11-17 05:00:01.000", "WRN", "Cat", "second"),
			Line("2026-11-17 05:00:02.000", "ERR", "Cat", "third"),
		]);

		var all = LogRecords.Query(new LogQuery(), _directory);
		Assert.Equal(["third", "second", "first"], all.Records.Select(static record => record.Message));

		var warnings = LogRecords.Query(new LogQuery { MinimumLevel = LogLevel.Warning }, _directory);
		Assert.Equal(["third", "second"], warnings.Records.Select(static record => record.Message));
	}

	[Fact]
	public void SearchMatchesCategoryAndMessage()
	{
		File.WriteAllLines(Path.Combine(_directory, "piweft-cli-1-20261117-000.log"),
		[
			Line("2026-11-17 05:00:00.000", "INF", "PiWeft.Desktop.SoundPlayer", "no audio player"),
			Line("2026-11-17 05:00:01.000", "INF", "PiWeft.Desktop.MainWindow", "desktop ready"),
		]);

		Assert.Single(LogRecords.Query(new LogQuery { Search = "sound" }, _directory).Records);
		Assert.Single(LogRecords.Query(new LogQuery { Search = "READY" }, _directory).Records);
	}

	[Fact]
	public void PagesAfterFiltering()
	{
		File.WriteAllLines(Path.Combine(_directory, "piweft-cli-1-20261117-000.log"),
			[.. Enumerable.Range(0, 120).Select(index =>
				Line("2026-11-17 05:00:00.000", "INF", "Cat", $"message {index}"))]);

		var first = LogRecords.Query(new LogQuery { PageSize = 50 }, _directory);
		Assert.Equal(120, first.Total);
		Assert.Equal(50, first.Records.Count);
		Assert.Equal("message 119", first.Records[0].Message);
		Assert.True(first.HasMore);

		var last = LogRecords.Query(new LogQuery { Page = 2, PageSize = 50 }, _directory);
		Assert.Equal(20, last.Records.Count);
		Assert.False(last.HasMore);
	}

	[Fact]
	public void ContinuesMultiLineStackRecords()
	{
		File.WriteAllLines(Path.Combine(_directory, "piweft-cli-1-20261117-000.log"),
		[
			Line("2026-11-17 05:00:00.000", "ERR", "Cat", "Open session: errorType=IOException, stack=   at Foo()"),
			"   at Bar()",
			"--- End of stack trace ---",
		]);

		var page = LogRecords.Query(new LogQuery(), _directory);
		var record = Assert.Single(page.Records);
		Assert.Contains("at Foo()", record.Message, StringComparison.Ordinal);
		Assert.Contains("at Bar()", record.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void DefaultBudgetStaysWithinTheArrayPool()
	{
		// 超了 ArrayPool 的上限就会每次新分配：常量改大之前先看这里。
		Assert.True(LogRecords.MaxBytesPerFile + 4 <= 1024 * 1024);
	}

	[Fact]
	public void ContinuationOfAFilteredRecordDoesNotStickToTheNextOne()
	{
		File.WriteAllLines(Path.Combine(_directory, "piweft-cli-1-20261117-000.log"),
		[
			Line("2026-11-17 05:00:00.000", "ERR", "Cat", "older"),
			Line("2026-11-17 05:00:01.000", "INF", "Cat", "info-with-stack"),
			"   at Foo()",
			Line("2026-11-17 05:00:02.000", "ERR", "Cat", "newest"),
		]);

		// INF 那条被筛掉，它的续行不能粘到更老的 older 上。
		var page = LogRecords.Query(new LogQuery { MinimumLevel = LogLevel.Warning }, _directory);
		Assert.Equal(["newest", "older"], page.Records.Select(static record => record.Message));
	}

	[Fact]
	public void LastLineWithoutTrailingNewlineIsRead()
	{
		File.WriteAllText(Path.Combine(_directory, "piweft-cli-1-20261117-000.log"),
			Line("2026-11-17 05:00:00.000", "INF", "Cat", "first") + "\n" +
			Line("2026-11-17 05:00:01.000", "INF", "Cat", "last"));

		var page = LogRecords.Query(new LogQuery(), _directory);
		Assert.Equal(["last", "first"], page.Records.Select(static record => record.Message));
	}

	[Fact]
	public void WindowsLineEndingsAreAccepted()
	{
		File.WriteAllText(Path.Combine(_directory, "piweft-cli-1-20261117-000.log"),
			Line("2026-11-17 05:00:00.000", "WRN", "Cat", "crlf") + "\r\n");

		var record = Assert.Single(LogRecords.Query(new LogQuery(), _directory).Records);
		Assert.Equal("crlf", record.Message);
		Assert.Equal(LogLevel.Warning, record.Level);
	}

	[Fact]
	public void WholeFileReadKeepsEveryRecord()
	{
		File.WriteAllLines(Path.Combine(_directory, "piweft-cli-1-20261117-000.log"),
			[.. Enumerable.Range(0, 300).Select(index => Line("2026-11-17 05:00:00.000", "INF", "Cat", $"m{index}"))]);

		var records = LogRecords.Read(new LogQuery(), _directory, maxFiles: 1, maxBytesPerFile: 0);
		Assert.Equal(300, records.Count);
		Assert.Equal("m299", records[0].Message);
	}

	[Fact]
	public void ClearDeletesUnusedFilesOnly()
	{
		var active = Path.Combine(_directory, "piweft-cli-1-20261117-000.log");
		var stale = Path.Combine(_directory, "piweft-cli-2-20261116-000.log");
		File.WriteAllText(active, Line("2026-11-17 05:00:00.000", "INF", "Cat", "live") + "\n");
		File.WriteAllText(stale, Line("2026-11-16 05:00:00.000", "INF", "Cat", "old") + "\n");

		using (new FileStream(active, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
		{
			Assert.Equal(1, LogRecords.Clear(_directory));
			Assert.True(File.Exists(active));
			Assert.False(File.Exists(stale));
		}
	}

	[Fact]
	public void MissingDirectoryReturnsEmptyPage()
	{
		var page = LogRecords.Query(new LogQuery(), Path.Combine(_directory, "absent"));
		Assert.Empty(page.Records);
		Assert.Equal(0, page.Total);
	}
}
