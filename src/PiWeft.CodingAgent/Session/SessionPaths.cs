using System.Globalization;

namespace PiWeft.CodingAgent.Session;

public static class SessionPaths
{
	public const string EnvAgentDir = "PI_CODING_AGENT_DIR";
	public const string EnvSessionDir = "PI_CODING_AGENT_SESSION_DIR";
	public const string ConfigDirName = ".pi";

	/// agent 目录：<c>$PI_CODING_AGENT_DIR</c> 或 <c>~/.pi/agent</c>。
	/// 环境变量里的 <c>~</c> 会展开，否则 <c>PI_CODING_AGENT_DIR=~/pi-agent</c> 会被当成相对路径。
	public static string GetAgentDir() =>
		Environment.GetEnvironmentVariable(EnvAgentDir) is { Length: > 0 } configured
			? UserPath.ExpandTilde(configured)
			: Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
				ConfigDirName,
				"agent");

	public static string GetDefaultSessionDir(string cwd, string? agentDir = null) =>
		Path.Combine(agentDir ?? GetAgentDir(), "sessions", EncodeCwd(cwd));

	public static string EncodeCwd(string cwd)
	{
		var resolved = Path.GetFullPath(cwd);
		var trimmed = resolved.TrimStart('/', '\\');
		var encoded = trimmed.Replace('/', '-').Replace('\\', '-').Replace(':', '-');
		return $"--{encoded}--";
	}

	public static string NewSessionId() => Guid.CreateVersion7().ToString();

	public static string ToIso8601(DateTimeOffset value) =>
		value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

	public static string SessionFileName(string isoTimestamp, string sessionId) =>
		$"{isoTimestamp.Replace(':', '-').Replace('.', '-')}_{sessionId}{SessionFormat.Extension}";
}
