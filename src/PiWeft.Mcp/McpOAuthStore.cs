using System.Text.Json;

namespace PiWeft.Mcp;

/// <summary>某个 MCP 服务器的 OAuth 令牌与端点信息。</summary>
public sealed record McpOAuthToken
{
	/// <summary>访问令牌。</summary>
	public required string Access { get; init; }

	public string? Refresh { get; init; }

	/// <summary>到期时间（Unix 秒）；为 null 表示服务器没有给出有效期。</summary>
	public long? ExpiresAt { get; init; }

	public string? TokenType { get; init; }

	public string? Scope { get; init; }

	/// <summary>使用的客户端 id（可能是动态注册得到的）。</summary>
	public string? ClientId { get; init; }

	/// <summary>令牌端点，刷新时使用。</summary>
	public string? TokenEndpoint { get; init; }

	/// <summary>授权服务器标识，仅用于诊断与 <c>pi mcp list</c> 展示。</summary>
	public string? AuthorizationServer { get; init; }

	/// <summary>资源标识（RFC 8707），刷新时原样回传。</summary>
	public string? Resource { get; init; }

	/// <summary>距离到期的剩余时间；没有有效期时为 null。</summary>
	public TimeSpan? Remaining(DateTimeOffset now) => ExpiresAt is { } expires
		? DateTimeOffset.FromUnixTimeSeconds(expires) - now
		: null;

	/// <summary>是否已经在刷新窗口内（含 <paramref name="skew"/> 提前量）。</summary>
	public bool IsExpiring(DateTimeOffset now, TimeSpan skew) =>
		Remaining(now) is { } remaining && remaining <= skew;
}

/// <summary><c>mcp-oauth.json</c> 的磁盘形状。</summary>
public sealed record McpOAuthFile
{
	public IReadOnlyDictionary<string, McpOAuthToken>? Servers { get; init; }
}

/// <summary>
/// MCP 的 OAuth 令牌存储：<c>&lt;agentDir&gt;/mcp-oauth.json</c>。
/// 与 provider 凭据（<c>auth.json</c>）分开，避免把第三方 MCP 服务器的令牌混进 pi 的凭据文件；
/// 写入是原子的，Unix 下仅用户可读写。
/// </summary>
public static class McpOAuthStore
{
	public const string FileName = "mcp-oauth.json";

	/// <summary>到期前多久开始刷新。</summary>
	public static readonly TimeSpan ExpirySkew = TimeSpan.FromMinutes(1);

	public static string Path(string? agentDir = null) =>
		System.IO.Path.Combine(McpConfig.GetAgentDir(agentDir), FileName);

	/// <summary>把服务器 URL 规范化成存储键：scheme 与 host 小写，保留端口与路径。</summary>
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

	/// <summary>已保存令牌的服务器键（规范化 URL）。</summary>
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
