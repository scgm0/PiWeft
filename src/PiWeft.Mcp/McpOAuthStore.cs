using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiWeft.Ai.Json;

namespace PiWeft.Mcp;

public sealed record McpOAuthToken
{
	public required string Access { get; init; }

	public string? Refresh { get; init; }

	/// 到期时间（Unix 秒）。
	public long? ExpiresAt { get; init; }

	public string? TokenType { get; init; }

	public string? Scope { get; init; }

	public string? ClientId { get; init; }

	public string? ClientSecret { get; init; }

	public string? TokenEndpoint { get; init; }

	public string? AuthorizationServer { get; init; }

	public string? Resource { get; init; }

	/// 尚未建模的顶层字段，写回时原样保留。
	public JsonObject? Extra { get; init; }

	public TimeSpan? Remaining(DateTimeOffset now) => ExpiresAt is { } expires
		? DateTimeOffset.FromUnixTimeSeconds(expires) - now
		: null;

	public bool IsExpiring(DateTimeOffset now, TimeSpan skew) =>
		Remaining(now) is { } remaining && remaining <= skew;
}

/// MCP 的 OAuth 状态存储：<c>&lt;agentDir&gt;/mcp-auth.json</c>，与 pi 共用同一份文件。
/// <para>
/// 键是 <c>mcp__&lt;server&gt;|&lt;url&gt;</c>（与 pi 的 <c>mcpNamespace(name)|url</c> 一致），
/// 值是 pi 的 <c>McpOAuthState</c>：<c>serverUrl</c>、<c>clientInformation</c>、<c>tokens</c>、
/// <c>tokensExpireAt</c>、<c>discovery</c> 等。没有服务器名时退回旧版「只按 URL」的键，
/// 两种键都能读，写回保持原有形状。
/// </para>
public static class McpOAuthStore
{
	public const string FileName = "mcp-auth.json";

	/// 旧版本本端口用的文件名；首次读取时迁移进 <see cref="FileName"/>。
	public const string LegacyFileName = "mcp-oauth.json";

	public static readonly TimeSpan ExpirySkew = TimeSpan.FromMinutes(1);

	public static string Path(string? agentDir = null) =>
		System.IO.Path.Combine(McpConfig.GetAgentDir(agentDir), FileName);

	/// 与 pi 的 <c>mcpNamespace</c> 相同：<c>mcp__</c> 加把 <c>-</c> 换成 <c>_</c> 的服务器名。
	public static string Namespace(string serverName) => $"mcp__{serverName.Replace('-', '_')}";

	/// 规范化 URL：去掉末尾斜杠以外的差异，用作键的后半段。
	/// 与 JavaScript 的 <c>String(new URL(url))</c> 一致：主机小写、默认端口省略、根路径带 <c>/</c>。
	public static string Key(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri)
		? $"{uri.Scheme}://{uri.Host}{(uri.IsDefaultPort ? string.Empty : ":" + uri.Port)}{(uri.AbsolutePath.Length > 0 ? uri.AbsolutePath : "/")}"
		: url.Trim();

	/// 服务器名 + URL 的存储键。名字为空时返回旧版 URL 键。
	public static string Key(string? serverName, string url) =>
		serverName is { Length: > 0 } name ? $"{Namespace(name)}|{Key(url)}" : Key(url);

	public static McpOAuthToken? Load(string url, string? agentDir = null) => Load(null, url, agentDir);

	public static McpOAuthToken? Load(string? serverName, string url, string? agentDir = null)
	{
		var states = Read(agentDir);
		if (states is null)
		{
			return null;
		}

		// 先按「名字|URL」，再按 URL 旧键：同一个服务器换了名字也能继续用旧令牌。
		foreach (var key in CandidateKeys(serverName, url))
		{
			if (states[key] is JsonObject state && ToToken(state, url) is { } token)
			{
				return token;
			}
		}

		return null;
	}

	public static void Save(string url, McpOAuthToken token, string? agentDir = null) =>
		Save(null, url, token, agentDir);

	public static void Save(string? serverName, string url, McpOAuthToken token, string? agentDir = null)
	{
		var key = Key(serverName, url);
		Update(agentDir, states =>
		{
			var existing = states[key] as JsonObject;
			states[key] = Merge(existing, token, url);
		});
	}

	public static bool Remove(string url, string? agentDir = null) => Remove(null, url, agentDir);

	public static bool Remove(string? serverName, string url, string? agentDir = null)
	{
		var removed = false;
		Update(agentDir, states =>
		{
			foreach (var key in CandidateKeys(serverName, url))
			{
				removed |= states.Remove(key);
			}
		});
		return removed;
	}

	public static IReadOnlyList<string> Keys(string? agentDir = null) =>
		Read(agentDir) is { } states ? [.. states.Select(static pair => pair.Key)] : [];

	/// 某个服务器是否存有令牌（pi 的 <c>tokens(name, url)</c>）。
	public static bool HasTokens(string? serverName, string url, string? agentDir = null) =>
		Load(serverName, url, agentDir) is not null;

	private static IEnumerable<string> CandidateKeys(string? serverName, string url)
	{
		if (serverName is { Length: > 0 })
		{
			yield return Key(serverName, url);
		}

		yield return Key(url);
	}

	/// pi 的状态对象 → 本端口的令牌。只读 <c>tokens</c>/<c>tokensExpireAt</c>/<c>clientInformation</c>/
	/// <c>discovery</c>，其它字段在写回时保留。
	private static McpOAuthToken? ToToken(JsonObject state, string url)
	{
		if (state["tokens"] is not JsonObject tokens ||
			tokens["access_token"] is not JsonValue accessValue ||
			!accessValue.TryGetValue<string>(out var access) ||
			access.Length == 0)
		{
			return null;
		}

		var expiresAt = state["tokensExpireAt"] is JsonValue expires && expires.TryGetValue<long>(out var expiresMs)
			? expiresMs / 1000
			: tokens["expires_in"] is JsonValue lifetime && lifetime.TryGetValue<long>(out var seconds)
				? DateTimeOffset.UtcNow.ToUnixTimeSeconds() + seconds
				: (long?)null;

		var discovery = state["discovery"] as JsonObject;
		var metadata = discovery?["authorizationServerMetadata"] as JsonObject;
		var client = state["clientInformation"] as JsonObject;

		return new McpOAuthToken
		{
			Access = access,
			Refresh = Text(tokens, "refresh_token"),
			ExpiresAt = expiresAt,
			TokenType = Text(tokens, "token_type"),
			Scope = Text(tokens, "scope"),
			ClientId = Text(client, "client_id"),
			ClientSecret = Text(client, "client_secret"),
			TokenEndpoint = Text(metadata, "token_endpoint"),
			AuthorizationServer = Text(discovery, "authorizationServerUrl"),
			Resource = Text(state, "serverUrl") ?? url,
		};
	}

	private static JsonObject Merge(JsonObject? existing, McpOAuthToken token, string url)
	{
		var state = existing?.DeepClone().AsObject() ?? new JsonObject();
		state["serverUrl"] = token.Resource is { Length: > 0 } resource ? resource : url;

		var tokens = state["tokens"] as JsonObject ?? new JsonObject();
		tokens["access_token"] = token.Access;
		tokens["token_type"] = token.TokenType is { Length: > 0 } type ? type : "Bearer";
		if (token.Refresh is { Length: > 0 } refresh)
		{
			tokens["refresh_token"] = refresh;
		}

		if (token.Scope is { Length: > 0 } scope)
		{
			tokens["scope"] = scope;
		}

		if (token.ExpiresAt is { } expires)
		{
			tokens["expires_in"] = Math.Max(0, expires - DateTimeOffset.UtcNow.ToUnixTimeSeconds());
			state["tokensExpireAt"] = expires * 1000;
		}
		else
		{
			// 新令牌没有到期时间时不能留下上一枚的 tokensExpireAt：读回会把新令牌当成已过期。
			tokens.Remove("expires_in");
			state.Remove("tokensExpireAt");
		}

		state["tokens"] = tokens;

		if (token.ClientId is { Length: > 0 } clientId)
		{
			var client = state["clientInformation"] as JsonObject ?? new JsonObject();
			client["client_id"] = clientId;
			if (token.ClientSecret is { Length: > 0 } secret)
			{
				client["client_secret"] = secret;
			}

			state["clientInformation"] = client;
		}

		if (token.TokenEndpoint is { Length: > 0 } tokenEndpoint || token.AuthorizationServer is { Length: > 0 })
		{
			var discovery = state["discovery"] as JsonObject ?? new JsonObject();
			if (token.AuthorizationServer is { Length: > 0 } issuer)
			{
				discovery["authorizationServerUrl"] = issuer;
			}

			if (token.TokenEndpoint is { Length: > 0 } endpoint)
			{
				var metadata = discovery["authorizationServerMetadata"] as JsonObject ?? new JsonObject();
				metadata["token_endpoint"] = endpoint;
				discovery["authorizationServerMetadata"] = metadata;
			}

			state["discovery"] = discovery;
		}

		return state;
	}

	private static string? Text(JsonObject? obj, string key) =>
		obj?[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

	private static JsonObject? Read(string? agentDir)
	{
		var path = Path(agentDir);
		JsonObject? states = null;
		if (File.Exists(path))
		{
			try
			{
				states = JsonNode.Parse(PiJson.StripBom(File.ReadAllText(path))) as JsonObject;
			}
			catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
			{
				states = null;
			}
		}

		states ??= new JsonObject();
		MigrateLegacy(agentDir, states);
		return states;
	}

	/// 旧版 <c>mcp-oauth.json</c> 的 <c>servers</c> 表按 URL 键写进新文件；迁移成功就删掉旧文件。
	private static void MigrateLegacy(string? agentDir, JsonObject states)
	{
		var legacyPath = System.IO.Path.Combine(McpConfig.GetAgentDir(agentDir), LegacyFileName);
		if (!File.Exists(legacyPath))
		{
			return;
		}

		JsonObject? legacy = null;
		try
		{
			legacy = JsonNode.Parse(PiJson.StripBom(File.ReadAllText(legacyPath)))?["servers"] as JsonObject;
		}
		catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
		{
			return;
		}

		if (legacy is null)
		{
			return;
		}

		var migrated = false;
		foreach (var (url, node) in legacy)
		{
			if (node is not JsonObject entry || states.ContainsKey(url))
			{
				continue;
			}

			var token = new McpOAuthToken
			{
				Access = Text(entry, "access") ?? string.Empty,
				Refresh = Text(entry, "refresh"),
				ExpiresAt = entry["expiresAt"] is JsonValue expires && expires.TryGetValue<long>(out var seconds) ? seconds : null,
				TokenType = Text(entry, "tokenType"),
				Scope = Text(entry, "scope"),
				ClientId = Text(entry, "clientId"),
				TokenEndpoint = Text(entry, "tokenEndpoint"),
				AuthorizationServer = Text(entry, "authorizationServer"),
				Resource = Text(entry, "resource") ?? url,
			};
			if (token.Access.Length == 0)
			{
				continue;
			}

			states[url] = Merge(null, token, url);
			migrated = true;
		}

		if (!migrated)
		{
			return;
		}

		try
		{
			WriteAtomically(Path(agentDir), states);
			File.Delete(legacyPath);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			// 迁移失败就留着旧文件，下次启动再试。
		}
	}

	private static void Update(string? agentDir, Action<JsonObject> mutate)
	{
		var path = Path(agentDir);
		var directory = System.IO.Path.GetDirectoryName(path);
		if (directory is { Length: > 0 })
		{
			Directory.CreateDirectory(directory);
		}

		// 两个进程同时刷新时只让一个读改写，避免后写者覆盖新令牌。
		using var fileLock = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
		var states = Read(agentDir) ?? new JsonObject();
		mutate(states);
		WriteAtomically(path, states);
	}

	private static void WriteAtomically(string path, JsonObject states)
	{
		var directory = System.IO.Path.GetDirectoryName(path);
		if (directory is { Length: > 0 })
		{
			Directory.CreateDirectory(directory);
		}

		var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
		try
		{
			// pi 用两空格缩进，保持一致以便两个实现来回写。
			File.WriteAllText(
				temporary,
				states.ToJsonString(new JsonSerializerOptions
				{
					WriteIndented = true,
					Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
				}) + "\n",
				new UTF8Encoding(false));
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
