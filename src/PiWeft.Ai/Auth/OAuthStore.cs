using System.Text.Json.Nodes;

namespace PiWeft.Ai.Auth;

public sealed record OAuthCredential
{
	public string Type { get; init; } = "oauth";
	public required string Access { get; init; }
	public required string Refresh { get; init; }

	public long Expires { get; init; }

	public string? ClientId { get; init; }
	public IReadOnlyList<string>? Scopes { get; init; }
	public string? AccountId { get; init; }

	public string? BaseUrl { get; init; }

	public string? EnterpriseUrl { get; init; }

	public DateTimeOffset ExpiresAt => DateTimeOffset.FromUnixTimeSeconds(Expires);
}

public static class OAuthStore
{
	public static string Path(string? agentDir = null) =>
		System.IO.Path.Combine(agentDir ?? AgentPaths.AgentDirectory(), "auth.json");

	public static OAuthCredential? Read(string provider, string? agentDir = null) =>
		ReadCredential(ReadRoot(agentDir)?[provider]);

	public static async Task<OAuthCredential?> ReadAsync(
		string provider,
		string? agentDir = null,
		CancellationToken cancellationToken = default) =>
		ReadCredential((await AuthStore.ReadRootAsync(agentDir, cancellationToken).ConfigureAwait(false))?[provider]);

	/// 一次读盘取出全部 OAuth 凭据。认证页要显示多个 provider 的状态，
	/// 逐个 Read 会对同一份 auth.json 重复读盘/解析；这里只读一次。
	public static IReadOnlyDictionary<string, OAuthCredential> ReadAll(string? agentDir = null)
	{
		var result = new Dictionary<string, OAuthCredential>(StringComparer.Ordinal);
		if (ReadRoot(agentDir) is not { } root) return result;
		foreach (var (provider, node) in root)
		{
			if (ReadCredential(node) is { } credential) result[provider] = credential;
		}
		return result;
	}

	internal static OAuthCredential? ReadCredential(JsonNode? node)
	{
		if (node is not JsonObject entry || Text(entry, "type") != "oauth" || Text(entry, "access") is not { Length: > 0 } access)
		{
			return null;
		}

		var scopes = entry["scopes"] is JsonArray array
			? array.OfType<JsonValue>()
				.Select(static value => value.GetValueKind() == System.Text.Json.JsonValueKind.String ? value.GetValue<string>() : null)
				.Where(static value => value is { Length: > 0 })
				.Select(static value => value!)
				.ToList()
			: null;

		return new OAuthCredential
		{
			Access = access,
			Refresh = Text(entry, "refresh") ?? string.Empty,
			Expires = entry["expires"] is JsonValue expires && expires.TryGetValue<long>(out var expiry) ? expiry : 0,
			ClientId = Text(entry, "clientId"),
			Scopes = scopes,
			AccountId = Text(entry, "accountId"),
			BaseUrl = Text(entry, "baseUrl"),
			EnterpriseUrl = Text(entry, "enterpriseUrl"),
		};
	}

	private static string? Text(JsonObject entry, string key) =>
		entry[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

	public static string? ReadAccessToken(string provider, string? agentDir = null, TimeSpan? skew = null)
	{
		var credential = Read(provider, agentDir);
		if (credential is null)
		{
			return null;
		}

		var expiresAt = credential.ExpiresAt - (skew ?? TimeSpan.FromMinutes(1));
		return expiresAt > DateTimeOffset.UtcNow ? credential.Access : null;
	}

	public static void Save(string provider, OAuthCredential credential, string? agentDir = null)
	{
		lock (AuthFileGate.Gate)
		{
			// 文件存在但解析失败时拒绝覆盖：否则一次 OAuth 登录会把其它供应商的凭据整片抹掉。
			var root = ReadRootForWrite(agentDir);
			var scopes = credential.Scopes is { Count: > 0 }
				? new JsonArray([.. credential.Scopes.Select(static scope => (JsonNode?)JsonValue.Create(scope))])
				: null;
			root[provider] = new JsonObject
			{
				["type"] = "oauth",
				["access"] = credential.Access,
				["refresh"] = credential.Refresh,
				["expires"] = credential.Expires,
				["clientId"] = credential.ClientId,
				["scopes"] = scopes,
				["accountId"] = credential.AccountId,
				["baseUrl"] = credential.BaseUrl,
				["enterpriseUrl"] = credential.EnterpriseUrl,
			};
			Write(root, agentDir);
		}
	}

	public static bool RemoveProvider(string provider, string? agentDir = null)
	{
		lock (AuthFileGate.Gate)
		{
			var root = ReadRoot(agentDir);
			if (root is null || !root.Remove(provider))
			{
				return false;
			}

			Write(root, agentDir);
			return true;
		}
	}

	private static JsonObject ReadRootForWrite(string? agentDir)
	{
		var path = Path(agentDir);
		if (!File.Exists(path)) return new JsonObject();
		try
		{
			return JsonNode.Parse(File.ReadAllText(path)) as JsonObject
				?? throw new CodexAuthException($"无法解析 {path}：顶层不是对象；为避免覆盖其它凭据已中止写入");
		}
		catch (System.Text.Json.JsonException exception)
		{
			throw new CodexAuthException($"无法解析 {path}（{exception.Message}）；为避免覆盖其它凭据已中止写入");
		}
	}

	internal static JsonObject? ReadRoot(string? agentDir)
	{
		var path = Path(agentDir);
		if (!File.Exists(path))
		{
			return null;
		}

		try
		{
			return JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
		}
		catch (Exception exception) when (exception is System.Text.Json.JsonException or IOException or UnauthorizedAccessException)
		{
			return null;
		}
	}

	internal static void Write(JsonObject root, string? agentDir)
	{
		var path = Path(agentDir);
		var directory = System.IO.Path.GetDirectoryName(path);
		if (directory is { Length: > 0 })
		{
			Directory.CreateDirectory(directory);
		}

		var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
		File.WriteAllText(temporary, root.ToJsonString(new System.Text.Json.JsonSerializerOptions
		{
			WriteIndented = true,
			Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
		}));
		if (!OperatingSystem.IsWindows())
		{
			File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
		}

		File.Move(temporary, path, overwrite: true);
	}
}
