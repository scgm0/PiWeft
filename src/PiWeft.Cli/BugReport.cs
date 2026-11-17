using System.IO.Compression;
using System.Text;
using Microsoft.Extensions.Logging;
using PiWeft.CodingAgent;
using PiWeft.CodingAgent.Session;
using ZLogger;

namespace PiWeft.Cli;

/// 诊断包：生成可分享的本地 zip，包含环境、脱敏后的配置、警告/错误日志、MCP 日志、崩溃记录
/// 与可选的会话记录。
/// <remarks>
/// 日志进入 zip 前逐条脱敏：家目录、agent 目录、工作目录换成占位符，凭据换成 <c>[REDACTED]</c>。
/// 旧实现直接筛原文行，用户把包发给别人时路径和 URL 里的 token 一起走了。
/// </remarks>
public static class BugReport
{
	/// 进入诊断包的日志条数上限。
	public const int MaxLogRecords = 200;

	/// <summary><c>/bug</c>：带会话记录的诊断包。</summary>
	public static string Create(
		CodingAgentSession session,
		string? description,
		bool includeTranscript = true,
		string? agentDir = null)
	{
		agentDir ??= SessionPaths.GetAgentDir();
		var report = BuildSessionReport(session, description);
		return Pack(
			agentDir,
			session.Session.Cwd,
			report,
			includeTranscript ? session.Session.SessionFile : null);
	}

	/// 只带环境与日志的诊断包：桌面没有会话对象，用这条入口。
	public static string CreateLogBundle(
		string? agentDir = null,
		string? cwd = null,
		string? description = null)
	{
		agentDir ??= SessionPaths.GetAgentDir();
		cwd ??= Environment.CurrentDirectory;
		return Pack(agentDir, cwd, BuildEnvironmentReport(agentDir, cwd, description), sessionFile: null);
	}

	private static string Pack(string agentDir, string? cwd, string report, string? sessionFile)
	{
		var directory = Path.Combine(agentDir, "bug-reports");
		Directory.CreateDirectory(directory);
		var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
		var staging = Path.Combine(directory, $".piweft-bug-{stamp}-{Guid.NewGuid():N}");
		Directory.CreateDirectory(staging);
		var redactor = new LogRedactor(cwd, agentDir);

		try
		{
			File.WriteAllText(Path.Combine(staging, "report.md"), report, new UTF8Encoding(false));

			if (sessionFile is { Length: > 0 } transcript && File.Exists(transcript))
			{
				File.Copy(transcript, Path.Combine(staging, "session.jsonl"));
			}

			WriteLimited(staging, "logs.txt", CollectLogs(agentDir, redactor));
			WriteLimited(staging, "mcp.txt", CollectMcpLog(agentDir, redactor));

			var crashes = CrashLog.Read(agentDir);
			if (crashes.Count > 0)
			{
				File.WriteAllText(Path.Combine(staging, "crashes.txt"), DescribeCrashes(crashes, redactor), new UTF8Encoding(false));
			}

			var archive = Path.Combine(directory, $"piweft-bug-{stamp}.zip");
			ZipFile.CreateFromDirectory(staging, archive, CompressionLevel.Optimal, includeBaseDirectory: false);
			AppLog.For<AppLog>().ZLogInformation($"Bug report written: {archive}");
			return archive;
		}
		finally
		{
			try
			{
				if (Directory.Exists(staging))
				{
					Directory.Delete(staging, recursive: true);
				}
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				// 清理失败不影响报告。
			}
		}
	}

	/// 写一份纯文本附件；<paramref name="text"/> 已经脱敏，这里只负责空内容不落文件。
	private static void WriteLimited(string staging, string name, string text)
	{
		if (text.Length == 0)
		{
			return;
		}

		File.WriteAllText(Path.Combine(staging, name), text, new UTF8Encoding(false));
	}

	/// 警告与错误记录，最新的在前。按级别解析而不是搜 "error" 子串：
	/// 旧做法会把正文里带 error 的普通信息行一起收进来，也会漏掉 WRN 级别的真实故障。
	private static string CollectLogs(string agentDir, LogRedactor redactor)
	{
		var page = LogRecords.Query(
			new LogQuery { MinimumLevel = LogLevel.Warning, PageSize = MaxLogRecords },
			Path.Combine(agentDir, "logs"));
		if (page.Records.Count == 0)
		{
			return string.Empty;
		}

		var builder = new StringBuilder();
		builder.AppendLine($"# 最近 {page.Records.Count} 条警告与错误（共扫描到 {page.Total} 条），最新在前");
		foreach (var record in page.Records)
		{
			builder.AppendLine(redactor.Line(LogRecords.Format(record)));
		}

		return builder.ToString();
	}

	private static string CollectMcpLog(string agentDir, LogRedactor redactor)
	{
		var path = Path.Combine(agentDir, PiWeft.Mcp.McpServerLog.FileName);
		if (!File.Exists(path))
		{
			return string.Empty;
		}

		var builder = new StringBuilder();
		try
		{
			var lines = File.ReadLines(path).TakeLast(200).ToArray();
			if (lines.Length == 0)
			{
				return string.Empty;
			}

			builder.AppendLine("# MCP 日志尾部");
			foreach (var line in lines)
			{
				builder.AppendLine(redactor.Line(line));
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			builder.AppendLine($"(MCP 日志读取失败：{exception.GetType().Name})");
		}

		return builder.ToString();
	}

	private static string DescribeCrashes(IReadOnlyList<CrashRecord> crashes, LogRedactor redactor)
	{
		var builder = new StringBuilder();
		foreach (var crash in crashes)
		{
			builder.AppendLine($"{crash.Timestamp:u} [{crash.Application} {crash.Version}] {crash.Kind}");
			builder.AppendLine($"  message: {redactor.Line(crash.Message)}");
			if (crash.Stack is { Length: > 0 } stack)
			{
				builder.AppendLine($"  stack: {redactor.Line(stack)}");
			}

			if (crash.Cwd is { Length: > 0 } cwd)
			{
				builder.AppendLine($"  cwd: {redactor.Text(cwd)}");
			}

			builder.AppendLine();
		}

		return builder.ToString();
	}

	private static string BuildEnvironmentReport(string agentDir, string cwd, string? description)
	{
		var builder = new StringBuilder();
		builder.AppendLine("# PiWeft diagnostics bundle");
		builder.AppendLine();
		builder.AppendLine("## Summary");
		builder.AppendLine(description is { Length: > 0 } text ? text : "(not provided)");
		builder.AppendLine();
		AppendEnvironment(builder, agentDir, cwd);
		builder.AppendLine("## Contents");
		builder.AppendLine("- report.md：本文件");
		builder.AppendLine("- logs.txt：最近 200 条警告与错误（已脱敏）");
		builder.AppendLine("- mcp.txt：MCP 日志尾部（已脱敏）");
		builder.AppendLine("- crashes.txt：崩溃记录（已脱敏）");
		builder.AppendLine("- session.jsonl：会话记录（`/bug` 默认包含）");
		builder.AppendLine();
		builder.AppendLine("每个附件按需生成：没有对应内容时文件不会出现。");
		builder.AppendLine();
		builder.AppendLine("Paths and credentials are redacted. Check the remaining text before sharing.");
		return builder.ToString();
	}

	private static void AppendEnvironment(StringBuilder builder, string agentDir, string cwd)
	{
		builder.AppendLine("## Environment");
		builder.AppendLine($"- PiWeft pi compatibility: {CliRunner.PiCompatibilityVersion}");
		builder.AppendLine($"- PiWeft RPC contract: {CliRunner.RpcContractVersion}");
		builder.AppendLine($"- Application: {AppLog.Application}");
		builder.AppendLine($"- Runtime: {Environment.Version}");
		builder.AppendLine($"- OS: {Environment.OSVersion} ({System.Runtime.InteropServices.RuntimeInformation.OSDescription})");
		builder.AppendLine($"- Architecture: {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
		builder.AppendLine($"- CWD: {cwd}");
		builder.AppendLine($"- Agent dir: {agentDir}");
		builder.AppendLine($"- Log level: {AppLog.MinimumLevel}");
		builder.AppendLine();
	}

	private static string BuildSessionReport(CodingAgentSession session, string? description)
	{
		var settings = session.Settings;
		var builder = new StringBuilder();
		builder.AppendLine("# PiWeft bug report");
		builder.AppendLine();
		builder.AppendLine("## Summary");
		builder.AppendLine(description is { Length: > 0 } text ? text : "(not provided)");
		builder.AppendLine();
		AppendEnvironment(
			builder,
			SessionPaths.GetAgentDir(),
			session.Cwd);

		builder.AppendLine("## Session");
		builder.AppendLine($"- ID: {session.Session.SessionId}");
		builder.AppendLine($"- Name: {session.SessionName ?? "(none)"}");
		builder.AppendLine($"- File: {session.Session.SessionFile ?? "(in-memory)"}");
		builder.AppendLine($"- Entries: {session.Session.Entries.Count}");
		builder.AppendLine($"- Project trusted: {session.ProjectTrusted}");
		builder.AppendLine();

		builder.AppendLine("## Model");
		builder.AppendLine($"- Provider: {session.Model.Provider}");
		builder.AppendLine($"- Model: {session.Model.Id}");
		builder.AppendLine($"- API: {session.Model.Api}");
		builder.AppendLine($"- Base URL: {session.Model.BaseUrl}");
		builder.AppendLine($"- Thinking level: {session.ThinkingLevel}");
		builder.AppendLine();

		builder.AppendLine("## Settings (redacted)");
		builder.AppendLine($"- defaultProvider: {settings.DefaultProvider ?? "(auto)"}");
		builder.AppendLine($"- defaultModel: {settings.DefaultModel ?? "(auto)"}");
		builder.AppendLine($"- defaultThinkingLevel: {settings.DefaultThinkingLevel ?? "(default)"}");
		builder.AppendLine($"- defaultProjectTrust: {settings.DefaultProjectTrust}");
		builder.AppendLine($"- defaultTools: {string.Join(", ", settings.DefaultTools)}");
		builder.AppendLine($"- shellPath: {settings.ShellPath ?? "(platform default)"}");
		builder.AppendLine($"- sessionDir: {settings.SessionDir ?? "(default)"}");
		builder.AppendLine($"- compaction: enabled={settings.Compaction.Enabled}, reserve={settings.Compaction.ReserveTokens}, keepRecent={settings.Compaction.KeepRecentTokens}");
		builder.AppendLine($"- retry: enabled={settings.Retry.Enabled}, maxRetries={settings.Retry.MaxRetries}, baseDelayMs={settings.Retry.BaseDelayMs}");
		builder.AppendLine();
		builder.AppendLine("Credential values are never included.");
		return builder.ToString();
	}
}
