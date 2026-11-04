using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using PiWeft.Ai.Json;
namespace PiWeft.Ai.Providers.Google;

/// Vertex AI 的应用默认凭据（ADC）：显式访问令牌、服务账号密钥文件，或 gcloud 用户凭据。
/// 令牌按凭据来源缓存，直到临近过期前才刷新。
public sealed class GoogleAdc
{
	private static readonly Lock Gate = new();
	private static readonly Dictionary<string, Entry> Cache = new(StringComparer.Ordinal);

	private sealed record Entry(string Token, DateTimeOffset ExpiresAt);

	public const string CredentialsVariable = "GOOGLE_APPLICATION_CREDENTIALS";

	public const string AccessTokenVariable = "GOOGLE_ACCESS_TOKEN";

	public const string CloudPlatformScope = "https://www.googleapis.com/auth/cloud-platform";

	public static Task<string> GetAccessTokenAsync(HttpClient http, CancellationToken cancellationToken) =>
		GetAccessTokenAsync(http, stored: null, allowDefaultAdc: false, cancellationToken);

	/// <paramref name="provider"/> 非空时同时读取 auth.json 凭据里的 env：桌面端 Vertex ADC
	/// 表单把项目、区域和凭据文件路径存在那里，不需要用户再改进程环境。
	public static Task<string> GetAccessTokenAsync(
		HttpClient http,
		string? provider,
		CancellationToken cancellationToken) =>
		GetAccessTokenAsync(http,
			provider is null ? null : ProviderEnvironment.StoredEnvironment(provider),
			allowDefaultAdc: true, cancellationToken);

	/// 请求方已经读过凭据 env 时直接传入，避免同一个请求重复读 auth.json。
	public static Task<string> GetAccessTokenAsync(
		HttpClient http,
		IReadOnlyDictionary<string, string>? stored,
		CancellationToken cancellationToken) =>
		GetAccessTokenAsync(http, stored, allowDefaultAdc: true, cancellationToken);

	private static async Task<string> GetAccessTokenAsync(
		HttpClient http,
		IReadOnlyDictionary<string, string>? stored,
		bool allowDefaultAdc,
		CancellationToken cancellationToken)
	{
		var provided = ProviderEnvironment.EnvironmentValue(stored, AccessTokenVariable);
		if (provided is { Length: > 0 })
		{
			return provided;
		}

		var configured = ProviderEnvironment.EnvironmentValue(stored, CredentialsVariable);
		var path = string.IsNullOrEmpty(configured)
			? allowDefaultAdc ? DefaultCredentialsPath() : null
			: ExpandHome(configured);
		if (string.IsNullOrEmpty(path) || !File.Exists(path))
		{
			throw new InvalidOperationException(
				"Vertex AI needs credentials. Set GOOGLE_ACCESS_TOKEN, or GOOGLE_APPLICATION_CREDENTIALS to a service-account file, or GOOGLE_CLOUD_API_KEY. " +
				(string.IsNullOrEmpty(path)
					? "No default ADC file path is available."
					: $"The default ADC file was not found at {path}."));
		}

		lock (Gate)
		{
			if (Cache.TryGetValue(path, out var cached) && cached.ExpiresAt > DateTimeOffset.UtcNow)
			{
				return cached.Token;
			}
		}

		var credential = JsonNode.Parse(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false))?.AsObject()
			?? throw new InvalidOperationException($"Credential file is not a JSON object: {path}");

		var type = credential["type"]?.GetValue<string>() ?? string.Empty;
		var (token, lifetime) = type switch
		{
			"service_account" => await RefreshServiceAccountAsync(http, credential, cancellationToken).ConfigureAwait(false),
			"authorized_user" => await RefreshUserAsync(http, credential, cancellationToken).ConfigureAwait(false),
			_ => throw new InvalidOperationException($"Unsupported credential type '{type}' in {path}"),
		};

		lock (Gate)
		{
			Cache[path] = new Entry(token, DateTimeOffset.UtcNow.Add(lifetime));
		}

		return token;
	}

	public static void ClearCache()
	{
		lock (Gate)
		{
			Cache.Clear();
		}
	}

	/// 模型可用性筛选用的无网络检查：API Key（express），或带项目/区域的访问令牌/ADC 文件。
	public static bool HasConfiguredCredentials(string? provider = null) =>
		HasConfiguredCredentials(provider is null ? null : ProviderEnvironment.StoredEnvironment(provider));

	public static bool HasConfiguredCredentials(IReadOnlyDictionary<string, string>? stored)
	{
		string? Env(string name) => ProviderEnvironment.EnvironmentValue(stored, name);

		// Vertex Express（API key）不需要项目/区域。
		if (Env("GOOGLE_CLOUD_API_KEY") is { Length: > 0 }) return true;

		// 访问令牌与 ADC 都走 projects/{project}/locations/{location} 端点，缺项目/区域不可用。
		if (string.IsNullOrEmpty(Env("GOOGLE_CLOUD_PROJECT") ?? Env("GOOGLE_PROJECT_ID")) ||
			string.IsNullOrEmpty(Env("GOOGLE_CLOUD_LOCATION") ?? Env("GOOGLE_CLOUD_REGION")))
		{
			return false;
		}

		if (Env(AccessTokenVariable) is { Length: > 0 }) return true;

		var path = Env(CredentialsVariable);
		path = string.IsNullOrEmpty(path) ? DefaultCredentialsPath() : ExpandHome(path);
		return !string.IsNullOrEmpty(path) && File.Exists(path);
	}

	/// gcloud 的默认 ADC 位置；与 pi 一样支持不显式设置 GOOGLE_APPLICATION_CREDENTIALS。
	private static string? DefaultCredentialsPath()
	{
		var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		if (string.IsNullOrEmpty(home)) return null;
		return OperatingSystem.IsWindows()
			? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "gcloud", "application_default_credentials.json")
			: Path.Combine(home, ".config", "gcloud", "application_default_credentials.json");
	}

	private static string ExpandHome(string path)
	{
		if (path != "~" && !path.StartsWith("~/", StringComparison.Ordinal) && !path.StartsWith("~\\", StringComparison.Ordinal))
		{
			return path;
		}

		var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		return path.Length <= 2 ? home : Path.Combine(home, path[2..]);
	}

	internal static async Task<(string Token, TimeSpan Lifetime)> RefreshServiceAccountAsync(
		HttpClient http,
		JsonObject credential,
		CancellationToken cancellationToken)
	{
		var email = credential["client_email"]?.GetValue<string>()
			?? throw new InvalidOperationException("A service-account file needs client_email");
		var privateKey = credential["private_key"]?.GetValue<string>()
			?? throw new InvalidOperationException("A service-account file needs private_key");
		var tokenUri = credential["token_uri"]?.GetValue<string>()
			?? "https://oauth2.googleapis.com/token";

		var assertion = CreateAssertion(email, tokenUri, privateKey);
		return await ExchangeAsync(http, tokenUri, new Dictionary<string, string>
		{
			["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
			["assertion"] = assertion,
		}, cancellationToken).ConfigureAwait(false);
	}

	internal static async Task<(string Token, TimeSpan Lifetime)> RefreshUserAsync(
		HttpClient http,
		JsonObject credential,
		CancellationToken cancellationToken)
	{
		var tokenUri = credential["token_uri"]?.GetValue<string>() ?? "https://oauth2.googleapis.com/token";
		var clientId = credential["client_id"]?.GetValue<string>()
			?? throw new InvalidOperationException("An authorized-user file needs client_id");
		var clientSecret = credential["client_secret"]?.GetValue<string>()
			?? throw new InvalidOperationException("An authorized-user file needs client_secret");
		var refreshToken = credential["refresh_token"]?.GetValue<string>()
			?? throw new InvalidOperationException("An authorized-user file needs refresh_token");

		return await ExchangeAsync(http, tokenUri, new Dictionary<string, string>
		{
			["grant_type"] = "refresh_token",
			["client_id"] = clientId,
			["client_secret"] = clientSecret,
			["refresh_token"] = refreshToken,
		}, cancellationToken).ConfigureAwait(false);
	}

	private static async Task<(string Token, TimeSpan Lifetime)> ExchangeAsync(
		HttpClient http,
		string tokenUri,
		Dictionary<string, string> form,
		CancellationToken cancellationToken)
	{
		using var content = new FormUrlEncodedContent(form);
		using var response = await http.PostAsync(tokenUri, content, cancellationToken).ConfigureAwait(false);

		var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
		if (!response.IsSuccessStatusCode)
		{
			throw new InvalidOperationException(
				$"Token exchange failed ({(int)response.StatusCode}): {Truncate(payload, 300)}");
		}

		var json = JsonNode.Parse(payload)?.AsObject()
			?? throw new InvalidOperationException("Token endpoint returned a non-object body");

		var token = json["access_token"]?.GetValue<string>()
			?? throw new InvalidOperationException("Token endpoint returned no access_token");

		// 提前一分钟续期，避免在途请求用到已过期的令牌。
		var expiresIn = json["expires_in"]?.GetValue<int>() ?? 3600;
		return (token, TimeSpan.FromSeconds(Math.Max(60, expiresIn - 60)));
	}

	internal static string CreateAssertion(string email, string audience, string privateKeyPem)
	{
		var issuedAt = DateTimeOffset.UtcNow;
		var header = new JsonObject { ["alg"] = "RS256", ["typ"] = "JWT" };
		var claims = new JsonObject
		{
			["iss"] = email,
			["scope"] = CloudPlatformScope,
			["aud"] = audience,
			["iat"] = issuedAt.ToUnixTimeSeconds(),
			["exp"] = issuedAt.AddHours(1).ToUnixTimeSeconds(),
		};

		var signingInput = $"{Base64Url(header.ToJsonString(PiJson.NodeOptions))}.{Base64Url(claims.ToJsonString(PiJson.NodeOptions))}";

		using var rsa = RSA.Create();
		rsa.ImportFromPem(privateKeyPem);

		var signature = rsa.SignData(
			Encoding.UTF8.GetBytes(signingInput),
			HashAlgorithmName.SHA256,
			RSASignaturePadding.Pkcs1);

		return $"{signingInput}.{Base64Url(signature)}";
	}

	internal static string Base64Url(string value) => Base64Url(Encoding.UTF8.GetBytes(value));

	internal static string Base64Url(byte[] bytes) =>
		Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

	private static string Truncate(string value, int max) =>
		value.Length <= max ? value : string.Concat(value.AsSpan(0, max), "…");
}
