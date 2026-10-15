using System.Globalization;

namespace PiWeft.CodingAgent.Session;

/// <summary>pi 会话的文件系统布局与命名规则。</summary>
public static class SessionPaths
{
	public const string EnvAgentDir = "PI_CODING_AGENT_DIR";
	public const string EnvSessionDir = "PI_CODING_AGENT_SESSION_DIR";
	public const string ConfigDirName = ".pi";

	/// <summary>
	/// agent 目录：<c>$PI_CODING_AGENT_DIR</c> 或 <c>~/.pi/agent</c>。
	/// 环境变量里的 <c>~</c> 会展开 —— 否则 <c>PI_CODING_AGENT_DIR=~/pi-agent</c> 会被当成相对路径。
	/// </summary>
	public static string GetAgentDir() =>
		Environment.GetEnvironmentVariable(EnvAgentDir) is { Length: > 0 } configured
			? UserPath.ExpandTilde(configured)
			: Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
				ConfigDirName,
				"agent");

	/// <summary>某个工作目录的默认会话目录：<c>&lt;agentDir&gt;/sessions/--&lt;编码后的 cwd&gt;--</c>。</summary>
	public static string GetDefaultSessionDir(string cwd, string? agentDir = null) =>
		Path.Combine(agentDir ?? GetAgentDir(), "sessions", EncodeCwd(cwd));

	/// <summary>把工作目录编码成安全的单个路径片段。</summary>
	public static string EncodeCwd(string cwd)
	{
		var resolved = Path.GetFullPath(cwd);
		var trimmed = resolved.TrimStart('/', '\\');
		var encoded = trimmed.Replace('/', '-').Replace('\\', '-').Replace(':', '-');
		return $"--{encoded}--";
	}

	/// <summary>新会话的 id：按时间递增的 UUIDv7。</summary>
	public static string NewSessionId() => Guid.CreateVersion7().ToString();

	/// <summary>带毫秒的 ISO 8601 UTC 时间戳，与 pi 的 <c>Date.toISOString()</c> 一致。</summary>
	public static string ToIso8601(DateTimeOffset value) =>
		value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

	/// <summary>会话文件名：<c>&lt;时间戳&gt;_&lt;会话 id&gt;.jsonl</c>，其中 <c>:</c> 与 <c>.</c> 会被替换。</summary>
	public static string SessionFileName(string isoTimestamp, string sessionId) =>
		$"{isoTimestamp.Replace(':', '-').Replace('.', '-')}_{sessionId}{SessionFormat.Extension}";
}
