using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using PiWeft.Ai.Json;

namespace PiWeft.Ai.Auth;

/// <summary>为 Codex 后端保存的 ChatGPT OAuth 凭据。</summary>
public sealed record CodexCredentials
{
	[JsonPropertyName("type")]
	public string Type { get; init; } = "oauth";

	[JsonPropertyName("access")]
	public required string Access { get; init; }

	[JsonPropertyName("refresh")]
	public required string Refresh { get; init; }

	/// <summary>访问令牌过期的 Unix 秒时间戳。</summary>
	[JsonPropertyName("expires")]
	public long Expires { get; init; }

	public DateTimeOffset ExpiresAt => DateTimeOffset.FromUnixTimeSeconds(Expires);

	public static CodexCredentials FromTokens(CodexTokens tokens) => new()
	{
		Access = tokens.Access,
		Refresh = tokens.Refresh,
		Expires = tokens.ExpiresAt.ToUnixTimeSeconds(),
	};
}

/// <summary>刚签发的一对令牌。</summary>
public sealed record CodexTokens(string Access, string Refresh, DateTimeOffset ExpiresAt);

/// <summary>用户输入的设备授权码。</summary>
public sealed record CodexDeviceCode(string DeviceAuthId, string UserCode, TimeSpan Interval, string VerificationUri);

/// <summary>OAuth 流程无法继续时抛出。</summary>
public sealed class CodexAuthException(string message) : Exception(message);

/// <summary>
/// Codex 后端使用的 ChatGPT OAuth：RFC 8628 设备码授权、令牌交换与刷新。
/// 凭据存放在 <c>&lt;agentDir&gt;/auth.json</c> 的 <c>openai-codex</c> 键下，与 pi 的位置一致。
/// </summary>
public static class CodexAuth
{
	public const string ProviderKey = "openai-codex";
	public const string ClientId = "app_EMoamEEZ73f0CkXaXp7hrann";
	public const string DefaultAuthBaseUrl = "https://auth.openai.com";

	/// <summary>携带 ChatGPT 账号 id 的声明命名空间。</summary>
	public const string ClaimPath = "https://api.openai.com/auth";

	private const string VerificationPath = "/codex/device";
	private const string RedirectUri = "https://auth.openai.com/deviceauth/callback";

	/// <summary>提前一分钟刷新，避免在途请求用到已过期的令牌。</summary>
	private static readonly TimeSpan ExpirySkew = TimeSpan.FromMinutes(1);

	public static string CredentialsPath(string? agentDir = null) =>
		Path.Combine(agentDir ?? AgentPaths.AgentDirectory(), "auth.json");

	/// <summary>启动设备码流程，并返回需要展示给用户的信息。</summary>
	public static async Task<CodexDeviceCode> StartDeviceAsync(
		HttpClient http,
		string? authBaseUrl = null,
		CancellationToken cancellationToken = default)
	{
		var baseUrl = (authBaseUrl ?? DefaultAuthBaseUrl).TrimEnd('/');

		using var response = await PostJsonAsync(
			http,
			$"{baseUrl}/api/accounts/deviceauth/usercode",
			new JsonObject { ["client_id"] = ClientId },
			cancellationToken).ConfigureAwait(false);

		var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

		if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
		{
			throw new CodexAuthException(
				"OpenAI Codex device code login is not enabled for this server. Use browser login or verify the server URL.");
		}

		if (!response.IsSuccessStatusCode)
		{
			throw new CodexAuthException($"OpenAI Codex device code request failed ({(int)response.StatusCode}): {body}");
		}

		var json = JsonNode.Parse(body) as JsonObject
			?? throw new CodexAuthException("OpenAI Codex device code response was not an object");

		var deviceAuthId = json["device_auth_id"]?.GetValue<string>();
		var userCode = json["user_code"]?.GetValue<string>();
		var interval = ReadInterval(json["interval"]);

		if (string.IsNullOrEmpty(deviceAuthId) || string.IsNullOrEmpty(userCode) || interval is null)
		{
			throw new CodexAuthException($"Invalid OpenAI Codex device code response: {body}");
		}

		return new CodexDeviceCode(deviceAuthId, userCode, interval.Value, $"{baseUrl}{VerificationPath}");
	}

	/// <summary>
	/// 轮询直到用户完成设备授权，再用授权码换取令牌。按 RFC 8628 的要求，
	/// 遇到 <c>slow_down</c> 时会拉长轮询间隔。
	/// </summary>
	public static async Task<CodexTokens> PollDeviceAsync(
		HttpClient http,
		CodexDeviceCode device,
		string? authBaseUrl = null,
		TimeSpan? timeout = null,
		CancellationToken cancellationToken = default)
	{
		var baseUrl = (authBaseUrl ?? DefaultAuthBaseUrl).TrimEnd('/');
		var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromMinutes(15));
		var interval = device.Interval;

		while (DateTimeOffset.UtcNow < deadline)
		{
			using var response = await PostJsonAsync(
				http,
				$"{baseUrl}/api/accounts/deviceauth/token",
				new JsonObject
				{
					["device_auth_id"] = device.DeviceAuthId,
					["user_code"] = device.UserCode,
				},
				cancellationToken).ConfigureAwait(false);

			if (response.IsSuccessStatusCode)
			{
				var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
				var json = JsonNode.Parse(payload) as JsonObject;
				var authorizationCode = json?["authorization_code"]?.GetValue<string>();
				var codeVerifier = json?["code_verifier"]?.GetValue<string>();

				if (string.IsNullOrEmpty(authorizationCode) || string.IsNullOrEmpty(codeVerifier))
				{
					throw new CodexAuthException($"Invalid OpenAI Codex device auth response: {payload}");
				}

				return await ExchangeAsync(http, authorizationCode, codeVerifier, baseUrl, cancellationToken)
					.ConfigureAwait(false);
			}

			var error = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
			var code = JsonNode.Parse(error)?["error"]?.GetValue<string>();

			// 用户尚未完成授权；客户端轮询过快时服务器会要求 slow_down。
			if (code == "slow_down" || (int)response.StatusCode == 429)
			{
				interval += TimeSpan.FromSeconds(5);
			}
			else if (code is not null and not "authorization_pending")
			{
				throw new CodexAuthException($"OpenAI Codex device authorization failed ({code}): {error}");
			}

			await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
		}

		throw new CodexAuthException("OpenAI Codex device authorization timed out");
	}

	/// <summary>用授权码换取一对令牌。</summary>
	public static async Task<CodexTokens> ExchangeAsync(
		HttpClient http,
		string authorizationCode,
		string codeVerifier,
		string? authBaseUrl = null,
		CancellationToken cancellationToken = default)
	{
		var baseUrl = (authBaseUrl ?? DefaultAuthBaseUrl).TrimEnd('/');

		using var response = await PostJsonAsync(
			http,
			$"{baseUrl}/oauth/token",
			new JsonObject
			{
				["grant_type"] = "authorization_code",
				["code"] = authorizationCode,
				["code_verifier"] = codeVerifier,
				["client_id"] = ClientId,
				["redirect_uri"] = RedirectUri,
			},
			cancellationToken).ConfigureAwait(false);

		return await ReadTokensAsync(response, "exchange", cancellationToken).ConfigureAwait(false);
	}

	/// <summary>刷新已过期的访问令牌。</summary>
	public static async Task<CodexTokens> RefreshAsync(
		HttpClient http,
		string refreshToken,
		string? authBaseUrl = null,
		CancellationToken cancellationToken = default)
	{
		var baseUrl = (authBaseUrl ?? DefaultAuthBaseUrl).TrimEnd('/');

		using var response = await PostJsonAsync(
			http,
			$"{baseUrl}/oauth/token",
			new JsonObject
			{
				["grant_type"] = "refresh_token",
				["refresh_token"] = refreshToken,
				["client_id"] = ClientId,
			},
			cancellationToken).ConfigureAwait(false);

		return await ReadTokensAsync(response, "refresh", cancellationToken).ConfigureAwait(false);
	}

	/// <summary>从访问令牌中读取 <c>chatgpt_account_id</c> 声明。</summary>
	public static string? GetAccountId(string accessToken)
	{
		try
		{
			var parts = accessToken.Split('.');
			if (parts.Length < 2)
			{
				return null;
			}

			var payload = JsonNode.Parse(DecodeBase64Url(parts[1])) as JsonObject;
			var accountId = payload?[ClaimPath]?["chatgpt_account_id"]?.GetValue<string>();
			return string.IsNullOrEmpty(accountId) ? null : accountId;
		}
		catch (JsonException)
		{
			return null;
		}
		catch (FormatException)
		{
			return null;
		}
	}

	/// <summary>加载已保存的凭据，即将过期时先刷新。</summary>
	public static async Task<CodexCredentials> ResolveAsync(
		HttpClient http,
		string? agentDir = null,
		string? authBaseUrl = null,
		CancellationToken cancellationToken = default)
	{
		var stored = Load(agentDir)
			?? throw new CodexAuthException(
				"No ChatGPT credentials found. Run the Codex device login first, or set them in <agentDir>/auth.json.");

		if (stored.ExpiresAt - ExpirySkew > DateTimeOffset.UtcNow)
		{
			return stored;
		}

		var refreshed = await RefreshAsync(http, stored.Refresh, authBaseUrl, cancellationToken).ConfigureAwait(false);
		var credentials = CodexCredentials.FromTokens(refreshed);
		Save(credentials, agentDir);
		return credentials;
	}

	/// <summary>读取已保存的凭据；不存在时返回 <c>null</c>。</summary>
	public static CodexCredentials? Load(string? agentDir = null)
	{
		var path = CredentialsPath(agentDir);
		if (path is null || !File.Exists(path))
		{
			return null;
		}

		try
		{
			var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
			var entry = root?[ProviderKey] as JsonObject;
			if (entry is null)
			{
				return null;
			}

			var access = entry["access"]?.GetValue<string>();
			var refresh = entry["refresh"]?.GetValue<string>();
			if (string.IsNullOrEmpty(access) || string.IsNullOrEmpty(refresh))
			{
				return null;
			}

			return new CodexCredentials
			{
				Access = access,
				Refresh = refresh,
				Expires = entry["expires"]?.GetValue<long>() ?? 0,
			};
		}
		catch (JsonException)
		{
			return null;
		}
		catch (IOException)
		{
			return null;
		}
	}

	/// <summary>写入凭据，同时保留文件中其它 provider 的条目。</summary>
	public static void Save(CodexCredentials credentials, string? agentDir = null)
	{
		var path = CredentialsPath(agentDir);
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);

		JsonObject root;
		try
		{
			root = File.Exists(path)
				? JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject()
				: new JsonObject();
		}
		catch (JsonException)
		{
			root = new JsonObject();
		}

		root[ProviderKey] = JsonSerializer.SerializeToNode(credentials, AuthJsonContext.Default.CodexCredentials);
		File.WriteAllText(path, root.ToJsonString(WriteOptions));
	}

	/// <summary>提交 JSON 请求体。手写实现，保证裁剪后仍然是反射无关的路径。</summary>
	private static Task<HttpResponseMessage> PostJsonAsync(
		HttpClient http,
		string url,
		JsonObject payload,
		CancellationToken cancellationToken) =>
		http.PostAsync(
			url,
			new StringContent(payload.ToJsonString(PiJson.NodeOptions), Encoding.UTF8, "application/json"),
			cancellationToken);

	private static readonly JsonSerializerOptions WriteOptions = new()
	{
		WriteIndented = true,
		Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
	};

	private static async Task<CodexTokens> ReadTokensAsync(
		HttpResponseMessage response,
		string operation,
		CancellationToken cancellationToken)
	{
		var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

		if (!response.IsSuccessStatusCode)
		{
			throw new CodexAuthException($"OpenAI Codex token {operation} failed ({(int)response.StatusCode}): {body}");
		}

		var json = JsonNode.Parse(body) as JsonObject;
		var access = json?["access_token"]?.GetValue<string>();
		var refresh = json?["refresh_token"]?.GetValue<string>();
		var expiresIn = json?["expires_in"]?.GetValue<double>();

		if (string.IsNullOrEmpty(access) || string.IsNullOrEmpty(refresh) || expiresIn is null)
		{
			throw new CodexAuthException($"OpenAI Codex token {operation} response missing fields: {body}");
		}

		return new CodexTokens(
			access,
			refresh,
			DateTimeOffset.UtcNow.AddSeconds(expiresIn.Value));
	}

	private static TimeSpan? ReadInterval(JsonNode? node)
	{
		var seconds = node switch
		{
			null => null,
			JsonValue value when value.TryGetValue<double>(out var number) => number,
			JsonValue value when value.TryGetValue<string>(out var text) &&
				double.TryParse(text.Trim(), System.Globalization.CultureInfo.InvariantCulture, out var parsed) => parsed,
			_ => (double?)null,
		};

		// RFC 8628：缺少或非法的 interval 表示五秒。
		if (seconds is null or < 0 || !double.IsFinite(seconds.Value))
		{
			return seconds is null ? TimeSpan.FromSeconds(5) : null;
		}

		return TimeSpan.FromSeconds(Math.Max(1, seconds.Value));
	}

	private static string DecodeBase64Url(string value)
	{
		var padded = value.Replace('-', '+').Replace('_', '/');
		padded += new string('=', (4 - (padded.Length % 4)) % 4);
		return Encoding.UTF8.GetString(Convert.FromBase64String(padded));
	}
}

/// <summary>定位 pi 的 agent 目录，且不依赖编码代理程序集。</summary>
internal static class AgentPaths
{
	public const string EnvAgentDir = "PI_CODING_AGENT_DIR";

	public static string AgentDirectory() =>
		Environment.GetEnvironmentVariable(EnvAgentDir) switch
		{
			{ Length: > 0 } configured => configured,
			_ => Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
				".pi",
				"agent"),
		};
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(CodexCredentials))]
internal partial class AuthJsonContext : JsonSerializerContext
{
	// 保留源生成选项，只替换编码器。
	static AuthJsonContext()
	{
		Default = new AuthJsonContext(CreateOptions(Default));
	}

	private static JsonSerializerOptions CreateOptions(AuthJsonContext defaultContext) =>
		new(defaultContext.GeneratedSerializerOptions!)
		{
			Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
		};
}
