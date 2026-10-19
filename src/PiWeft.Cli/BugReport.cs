using System.IO.Compression;
using System.Text;
using PiWeft.CodingAgent;
using PiWeft.CodingAgent.Session;

namespace PiWeft.Cli;

/// <summary>
/// <c>/bug</c>：生成可分享的本地诊断包（zip），包含环境、脱敏后的配置、最近错误日志和可选的会话记录。
/// </summary>
public static class BugReport
{
	public static string Create(
		CodingAgentSession session,
		string? description,
		bool includeTranscript = true,
		string? agentDir = null)
	{
		agentDir ??= SessionPaths.GetAgentDir();
		var directory = Path.Combine(agentDir, "bug-reports");
		Directory.CreateDirectory(directory);
		var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
		var staging = Path.Combine(directory, $".piweft-bug-{stamp}-{Guid.NewGuid():N}");
		Directory.CreateDirectory(staging);

		try
		{
			File.WriteAllText(Path.Combine(staging, "report.md"), BuildReport(session, description), new UTF8Encoding(false));

			if (includeTranscript && session.Session.SessionFile is { Length: > 0 } transcript &&
				File.Exists(transcript))
			{
				File.Copy(transcript, Path.Combine(staging, "session.jsonl"));
			}

			var logs = CollectErrorLogs();
			if (logs.Length > 0)
			{
				File.WriteAllText(Path.Combine(staging, "errors.log"), logs, new UTF8Encoding(false));
			}

			var archive = Path.Combine(directory, $"piweft-bug-{stamp}.zip");
			ZipFile.CreateFromDirectory(staging, archive, CompressionLevel.Optimal, includeBaseDirectory: false);
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

	private static string BuildReport(CodingAgentSession session, string? description)
	{
		var settings = session.Settings;
		var builder = new StringBuilder();
		builder.AppendLine("# PiWeft bug report");
		builder.AppendLine();
		builder.AppendLine("## Summary");
		builder.AppendLine(description is { Length: > 0 } text ? text : "(not provided)");
		builder.AppendLine();

		builder.AppendLine("## Environment");
		builder.AppendLine($"- PiWeft RPC contract: {CliRunner.RpcContractVersion}");
		builder.AppendLine($"- Runtime: {Environment.Version}");
		builder.AppendLine($"- OS: {Environment.OSVersion} ({System.Runtime.InteropServices.RuntimeInformation.OSDescription})");
		builder.AppendLine($"- Architecture: {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
		builder.AppendLine($"- CWD: {session.Cwd}");
		builder.AppendLine($"- Agent dir: {SessionPaths.GetAgentDir()}");
		builder.AppendLine();

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

	private static string CollectErrorLogs()
	{
		var directory = AppLog.DirectoryPath;
		if (directory.Length == 0 || !Directory.Exists(directory))
		{
			return string.Empty;
		}

		var builder = new StringBuilder();
		try
		{
			var files = new DirectoryInfo(directory).EnumerateFiles("*.log")
				.OrderByDescending(static file => file.LastWriteTimeUtc)
				.Take(3)
				.ToList();
			foreach (var file in files)
			{
				builder.Append("==== ").AppendLine(file.Name);
				foreach (var line in File.ReadLines(file.FullName).TakeLast(4000)
					.Where(static line => line.Contains("error", StringComparison.OrdinalIgnoreCase) ||
						line.Contains("fail", StringComparison.OrdinalIgnoreCase) ||
						line.Contains("exception", StringComparison.OrdinalIgnoreCase))
					.TakeLast(200))
				{
					builder.AppendLine(line);
				}
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			builder.AppendLine($"(could not read logs: {exception.GetType().Name})");
		}

		return builder.ToString();
	}
}
