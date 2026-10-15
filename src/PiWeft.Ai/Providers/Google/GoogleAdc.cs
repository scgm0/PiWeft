using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using PiWeft.Ai.Json;
namespace PiWeft.Ai.Providers.Google;

/// <summary>
/// Vertex AI 的应用默认凭据（ADC）：显式访问令牌、服务账号密钥文件，或 gcloud 用户凭据。
/// 令牌按凭据来源缓存，直到临近过期前才刷新。
/// </summary>
public sealed class GoogleAdc
{
	private static readonly Lock Gate = new();
	private static readonly Dictionary<string, Entry> Cache = new(StringComparer.Ordinal);

	private sealed record Entry(string Token, DateTimeOffset ExpiresAt);

	/// <summary>指向服务账号或用户凭据 JSON 文件的环境变量。</summary>
	public const string CredentialsVariable = "GOOGLE_APPLICATION_CREDENTIALS";

	/// <summary>调用方已经拿到的令牌，例如来自工作负载身份或 CI 密钥。</summary>
	public const string AccessTokenVariable = "GOOGLE_ACCESS_TOKEN";

	/// <summary>Vertex AI 要求的 OAuth scope。</summary>
	public const string CloudPlatformScope = "https://www.googleapis.com/auth/cloud-platform";

	/// <summary>返回访问令牌；缓存的令牌临近过期时先刷新。</summary>
	public static async Task<string> GetAccessTokenAsync(HttpClient http, CancellationToken cancellationToken)
	{
		if (Environment.GetEnvironmentVariable(AccessTokenVariable) is { Length: > 0 } provided)
		{
			return provided;
		}

		var path = Environment.GetEnvironmentVariable(CredentialsVariable);
		if (string.IsNullOrEmpty(path))
		{
			throw new InvalidOperationException(
				"Vertex AI needs credentials. Set GOOGLE_ACCESS_TOKEN, or GOOGLE_APPLICATION_CREDENTIALS to a service-account file, or GOOGLE_CLOUD_API_KEY.");
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

	/// <summary>清空令牌缓存。测试用它来隔离不同的凭据来源。</summary>
	public static void ClearCache()
	{
		lock (Gate)
		{
			Cache.Clear();
		}
	}

	/// <summary>用签名后的 JWT 断言换取访问令牌。</summary>
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

	/// <summary>刷新授权用户凭据，也就是 `gcloud auth application-default login` 写入的那种。</summary>
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

	/// <summary>为服务账号构建 RS256 签名的 JWT 断言。</summary>
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

	/// <summary>JWT 要求的无填充 Base64url 编码。</summary>
	internal static string Base64Url(string value) => Base64Url(Encoding.UTF8.GetBytes(value));

	internal static string Base64Url(byte[] bytes) =>
		Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

	private static string Truncate(string value, int max) =>
		value.Length <= max ? value : string.Concat(value.AsSpan(0, max), "…");
}
