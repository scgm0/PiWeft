using System.IO.Compression;
using PiWeft.Cli;
using Xunit;

namespace PiWeft.Cli.Tests;

/// 诊断包：按级别挑行、导出前脱敏、附带 MCP 日志与崩溃记录。
public sealed class BugReportTests : IDisposable
{
	private readonly string _agentDir = Path.Combine(Path.GetTempPath(), "piweft-bundle-" + Guid.NewGuid().ToString("N")[..8]);
	private readonly string _cwd;
	private readonly string _home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

	public BugReportTests()
	{
		_cwd = Path.Combine(_home, "projects", "demo");
		Directory.CreateDirectory(Path.Combine(_agentDir, "logs"));
		Directory.CreateDirectory(Path.Combine(_agentDir, "bug-reports"));
	}

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
	public void BundleRedactsPathsAndKeepsOnlyWarningsAndErrors()
	{
		File.WriteAllLines(Path.Combine(_agentDir, "logs", "piweft-desktop-1-20261117-000.log"),
		[
			$"2026-11-17 05:00:00.000 [INF] PiWeft.Desktop.MainWindow | started in {_cwd}",
			$"2026-11-17 05:00:01.000 [WRN] PiWeft.Desktop.Diagnostics | slow path at {_cwd}/src/App.cs",
			"2026-11-17 05:00:02.000 [ERR] PiWeft.Desktop.SessionTab | auth failed: Bearer abcdefghijklmnop",
		]);
		File.WriteAllLines(Path.Combine(_agentDir, PiWeft.Mcp.McpServerLog.FileName),
		[
			"2026-11-17T05:00:00.000Z [demo] warning token=ghp_abcdefghijklmnopqrstuvwxyz0123",
		]);
		CrashLog.Record(CrashKind.FatalError, new InvalidOperationException("boom at sk-abcdefghijklmnopqrstuvwxyz"),
			cwd: _cwd, agentDir: _agentDir, application: "desktop");

		var archive = BugReport.CreateLogBundle(_agentDir, _cwd);
		using var zip = ZipFile.OpenRead(archive);
		var names = zip.Entries.Select(static entry => entry.FullName).ToArray();
		Assert.Contains("report.md", names);
		Assert.Contains("logs.txt", names);
		Assert.Contains("mcp.txt", names);
		Assert.Contains("crashes.txt", names);

		var logs = Read(zip, "logs.txt");
		Assert.Contains("slow path", logs, StringComparison.Ordinal);
		Assert.Contains("auth failed", logs, StringComparison.Ordinal);
		// 普通信息行不进诊断包；路径与凭据必须已经脱敏。
		Assert.DoesNotContain("started in", logs, StringComparison.Ordinal);
		Assert.DoesNotContain(Path.Combine(_home, "projects"), logs, StringComparison.Ordinal);
		Assert.DoesNotContain("abcdefghijklmnop", logs, StringComparison.Ordinal);
		Assert.DoesNotContain("ghp_", Read(zip, "mcp.txt"), StringComparison.Ordinal);
		Assert.DoesNotContain("abcdefghijklmnop", Read(zip, "crashes.txt"), StringComparison.Ordinal);
	}

	[Fact]
	public void BundleWithoutLogsStillHasReport()
	{
		var archive = BugReport.CreateLogBundle(_agentDir, _cwd, description: "manual report");
		using var zip = ZipFile.OpenRead(archive);
		Assert.Single(zip.Entries, static entry => entry.FullName == "report.md");
		Assert.DoesNotContain(zip.Entries, static entry => entry.FullName == "logs.txt");
		Assert.Contains("manual report", Read(zip, "report.md"), StringComparison.Ordinal);
	}

	private static string Read(ZipArchive zip, string name)
	{
		using var reader = new StreamReader(zip.GetEntry(name)!.Open());
		return reader.ReadToEnd();
	}
}
