using System.Text.Json;

namespace PiWeft.Mcp;

public sealed record McpOAuthToken
{
	public required string Access { get; init; }

	public string? Refresh { get; init; }

	public long? ExpiresAt { get; init; }

	public string? TokenType { get; init; }

	public string? Scope { get; init; }

	public string? ClientId { get; init; }

	public string? TokenEndpoint { get; init; }

	public string? AuthorizationServer { get; init; }

	public string? Resource { get; init; }

	public TimeSpan? Remaining(DateTimeOffset now) => ExpiresAt is { } expires
		? DateTimeOffset.FromUnixTimeSeconds(expires) - now
		: null;

	public bool IsExpiring(DateTimeOffset now, TimeSpan skew) =>
		Remaining(now) is { } remaining && remaining <= skew;
}

public sealed record McpOAuthFile
{
	public IReadOnlyDictionary<string, McpOAuthToken>? Servers { get; init; }
}

/// MCP 的 OAuth 令牌存储：<c>&lt;agentDir&gt;/mcp-oauth.json</c>。
/// 与 provider 凭据（<c>auth.json</c>）分开，避免把第三方 MCP 服务器的令牌混进 pi 的凭据文件；
/// 写入是原子的，Unix 下仅用户可读写。
public static class McpOAuthStore
{
	public const string FileName = "mcp-oauth.json";

	public static readonly TimeSpan ExpirySkew = TimeSpan.FromMinutes(1);

	public static string Path(string? agentDir = null) =>
		System.IO.Path.Combine(McpConfig.GetAgentDir(agentDir), FileName);

	public static string Key(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri)
		? $"{uri.Scheme}://{uri.Host}{(uri.IsDefaultPort ? string.Empty : ":" + uri.Port)}{uri.AbsolutePath}"
		: url.Trim();

	public static McpOAuthToken? Load(string url, string? agentDir = null)
	{
		var servers = Read(agentDir)?.Servers;
		return servers is not null && servers.TryGetValue(Key(url), out var token) ? token : null;
	}

	public static void Save(string url, McpOAuthToken token, string? agentDir = null) =>
		Update(agentDir, servers => servers[Key(url)] = token);

	public static bool Remove(string url, string? agentDir = null)
	{
		var removed = false;
		Update(agentDir, servers => removed = servers.Remove(Key(url)));
		return removed;
	}

	public static IReadOnlyList<string> Keys(string? agentDir = null) =>
		Read(agentDir)?.Servers is { } servers ? [.. servers.Keys] : [];

	private static McpOAuthFile? Read(string? agentDir)
	{
		var path = Path(agentDir);
		if (!File.Exists(path))
		{
			return null;
		}

		try
		{
			return JsonSerializer.Deserialize(File.ReadAllText(path), McpJsonContext.Default.McpOAuthFile);
		}
		catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
		{
			return null;
		}
	}

	private static void Update(string? agentDir, Action<Dictionary<string, McpOAuthToken>> mutate)
	{
		var servers = Read(agentDir)?.Servers is { } existing
			? new Dictionary<string, McpOAuthToken>(existing, StringComparer.Ordinal)
			: new Dictionary<string, McpOAuthToken>(StringComparer.Ordinal);

		mutate(servers);
		WriteAtomically(Path(agentDir), new McpOAuthFile { Servers = servers });
	}

	private static void WriteAtomically(string path, McpOAuthFile file)
	{
		var directory = System.IO.Path.GetDirectoryName(path);
		if (directory is { Length: > 0 })
		{
			Directory.CreateDirectory(directory);
		}

		var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
		try
		{
			File.WriteAllText(temporary, JsonSerializer.Serialize(file, McpJsonContext.Default.McpOAuthFile));
			if (!OperatingSystem.IsWindows())
			{
				File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
			}

			File.Move(temporary, path, overwrite: true);
		}
		finally
		{
			// 失败时不要留下含令牌的 .tmp 垃圾；成功时 Move 已经把它搬走了。
			if (File.Exists(temporary))
			{
				try
				{
					File.Delete(temporary);
				}
				catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
				{
					// 清理失败不影响主流程的错误上报。
				}
			}
		}
	}
}
