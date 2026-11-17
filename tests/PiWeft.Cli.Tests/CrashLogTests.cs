using PiWeft.Cli;
using Xunit;

namespace PiWeft.Cli.Tests;

/// 崩溃记录：进程崩掉之后，下次启动要能把这件事读出来并只提示一次。
public sealed class CrashLogTests : IDisposable
{
	private readonly string _agentDir = Path.Combine(Path.GetTempPath(), "piweft-crash-" + Guid.NewGuid().ToString("N")[..8]);

	public CrashLogTests() => Directory.CreateDirectory(_agentDir);

	public void Dispose()
	{
		try
		{
			Directory.Delete(_agentDir, recursive: true);
		}
		catch (IOException)
		{
		}
	}

	[Fact]
	public void RecordThenReadKeepsTheChain()
	{
		var record = CrashLog.Record(
			CrashKind.FatalError,
			new InvalidOperationException("outer", new Exception("inner")),
			sessionFile: "/tmp/session.jsonl",
			cwd: "/tmp/demo",
			agentDir: _agentDir,
			application: "desktop");

		Assert.NotNull(record);
		var stored = Assert.Single(CrashLog.Read(_agentDir));
		Assert.Equal(CrashKind.FatalError, stored.Kind);
		Assert.Equal("desktop", stored.Application);
		Assert.Contains("outer", stored.Message, StringComparison.Ordinal);
		Assert.Contains("inner", stored.Message, StringComparison.Ordinal);
		Assert.False(stored.Notified);
	}

	[Fact]
	public void TakeUnnotifiedReturnsOnce()
	{
		CrashLog.Record(CrashKind.UnhandledException, new Exception("boom"), agentDir: _agentDir);

		Assert.NotNull(CrashLog.TakeUnnotified(_agentDir));
		Assert.Null(CrashLog.TakeUnnotified(_agentDir));
		Assert.True(Assert.Single(CrashLog.Read(_agentDir)).Notified);
	}

	[Fact]
	public void NewestPendingCrashWins()
	{
		CrashLog.Record(CrashKind.UnhandledException, new Exception("first"), agentDir: _agentDir);
		CrashLog.Record(CrashKind.FatalError, new Exception("second"), agentDir: _agentDir);

		Assert.Contains("second", CrashLog.TakeUnnotified(_agentDir)!.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void RecordsAreCapped()
	{
		for (var index = 0; index < CrashLog.MaxRecords + 3; index++)
		{
			CrashLog.Record(CrashKind.UnhandledException, new Exception($"crash {index}"), agentDir: _agentDir);
		}

		var records = CrashLog.Read(_agentDir);
		Assert.Equal(CrashLog.MaxRecords, records.Count);
		Assert.DoesNotContain(records, static record => record.Message.Contains("crash 0", StringComparison.Ordinal));
	}

	[Fact]
	public void ExpiredRecordsAreNotReported()
	{
		var record = CrashLog.Record(CrashKind.UnhandledException, new Exception("old"), agentDir: _agentDir)!;
		Assert.Null(CrashLog.TakeUnnotified(_agentDir, now: record.Timestamp + CrashLog.MaxAge + TimeSpan.FromHours(1)));
	}

	[Fact]
	public void SecretsAreRedacted()
	{
		CrashLog.Record(
			CrashKind.UnhandledException,
			new InvalidOperationException("request failed with sk-abcdefghijklmnopqrstuvwxyz"),
			cwd: "/home/piweft-user/project",
			agentDir: _agentDir);

		var record = Assert.Single(CrashLog.Read(_agentDir));
		Assert.DoesNotContain("abcdefghijklmnop", record.Message, StringComparison.Ordinal);
		Assert.Equal("<workspace>", record.Cwd);
	}

	[Fact]
	public void DamagedFileReadsAsEmpty()
	{
		File.WriteAllText(CrashLog.PathFor(_agentDir), "{not json");
		Assert.Empty(CrashLog.Read(_agentDir));
		Assert.Null(CrashLog.TakeUnnotified(_agentDir));
	}

	[Fact]
	public void ClearRemovesFile()
	{
		CrashLog.Record(CrashKind.UnhandledException, new Exception("boom"), agentDir: _agentDir);
		CrashLog.Clear(_agentDir);
		Assert.Empty(CrashLog.Read(_agentDir));
	}
}
