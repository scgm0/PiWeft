using System.Text.Json.Nodes;

namespace PiWeft.Ai.Auth;

/// <summary><c>auth.json</c> 中的 OAuth 凭据，与 pi 的字段保持一致。</summary>
public sealed record OAuthCredential
{
	public string Type { get; init; } = "oauth";
	public required string Access { get; init; }
	public required string Refresh { get; init; }

	/// <summary>访问令牌过期的 Unix 秒时间戳。</summary>
	public long Expires { get; init; }

	public string? ClientId { get; init; }
	public IReadOnlyList<string>? Scopes { get; init; }
	public string? AccountId { get; init; }

	public DateTimeOffset ExpiresAt => DateTimeOffset.FromUnixTimeSeconds(Expires);
}

/// <summary>按 provider 读写 <c>auth.json</c> 中的 OAuth 凭据；API key 条目保持不动。</summary>
public static class OAuthStore
{
	public static string Path(string? agentDir = null) =>
		System.IO.Path.Combine(agentDir ?? AgentPaths.AgentDirectory(), "auth.json");

	public static OAuthCredential? Read(string provider, string? agentDir = null)
	{
		var node = ReadRoot(agentDir)?[provider];
		if (node is not JsonObject entry ||
			entry["type"]?.GetValue<string>() != "oauth" ||
			entry["access"]?.GetValue<string>() is not { Length: > 0 } access)
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
			Refresh = entry["refresh"]?.GetValue<string>() ?? string.Empty,
			Expires = entry["expires"]?.GetValue<long>() ?? 0,
			ClientId = entry["clientId"]?.GetValue<string>(),
			Scopes = scopes,
			AccountId = entry["accountId"]?.GetValue<string>(),
		};
	}

	/// <summary>读取未过期（含 skew）的 OAuth access token。</summary>
	public static string? ReadAccessToken(string provider, string? agentDir = null, TimeSpan? skew = null)
	{
		var credential = Read(provider, agentDir);
		if (credential is null)
		{
			return null;
		}

		var expiresAt = credential.ExpiresAt - (skew ?? TimeSpan.FromMinutes(1));
		return expiresAt > DateTimeOffset.UtcNow ? credential.Access : credential.Access;
	}

	public static void Save(string provider, OAuthCredential credential, string? agentDir = null)
	{
		var root = ReadRoot(agentDir) ?? new JsonObject();
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
		};
		Write(root, agentDir);
	}

	public static bool RemoveProvider(string provider, string? agentDir = null)
	{
		var root = ReadRoot(agentDir);
		if (root is null || !root.Remove(provider))
		{
			return false;
		}

		Write(root, agentDir);
		return true;
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
