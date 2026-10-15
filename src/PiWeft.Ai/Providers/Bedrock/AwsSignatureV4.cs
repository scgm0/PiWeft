using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace PiWeft.Ai.Providers.Bedrock;

/// <summary>用于 SigV4 签名的 AWS 凭据。</summary>
public sealed record AwsCredentials
{
	public required string AccessKeyId { get; init; }

	public required string SecretAccessKey { get; init; }

	/// <summary>临时凭据（STS、SSO、实例角色）才有该值。</summary>
	public string? SessionToken { get; init; }

	public bool IsValid => AccessKeyId.Length > 0 && SecretAccessKey.Length > 0;

	/// <summary>
	/// 从环境变量读取凭据，支持标准的 <c>AWS_*</c> 变量；命名 profile 与实例元数据
	/// 不在本端口的实现范围内。
	/// </summary>
	public static AwsCredentials FromEnvironment()
	{
		var accessKey = Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID");
		var secret = Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY");

		// 两者同时存在时命名 profile 的值优先，与 AWS CLI 的优先级大致一致。
		accessKey ??= Environment.GetEnvironmentVariable("AWS_ACCESS_KEY");
		secret ??= Environment.GetEnvironmentVariable("AWS_SECRET_KEY");

		return new AwsCredentials
		{
			AccessKeyId = accessKey ?? string.Empty,
			SecretAccessKey = secret ?? string.Empty,
			SessionToken = Environment.GetEnvironmentVariable("AWS_SESSION_TOKEN"),
		};
	}
}

/// <summary>
/// AWS Signature Version 4 请求签名。按规范直接实现，因此不需要 AWS SDK，
/// 让 NativeAOT 构建不必引入依赖反射的库。
/// </summary>
public static class AwsSignatureV4
{
	public const string Algorithm = "AWS4-HMAC-SHA256";
	public const string EmptyPayloadHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

	/// <summary>请求签名过程中的各个中间结果，便于诊断与测试。</summary>
	public sealed record Signed
	{
		public required string CanonicalRequest { get; init; }

		public required string StringToSign { get; init; }

		public required string Signature { get; init; }

		public required string Authorization { get; init; }

		public required IReadOnlyDictionary<string, string> Headers { get; init; }
	}

	/// <summary>
	/// 对请求签名。请求头里必须已经包含 <c>host</c>，否则要显式提供 Host 值。
	/// <paramref name="includeContentHash"/> 会加上 Bedrock 必需的 <c>x-amz-content-sha256</c>；
	/// 而经典的 SigV4 测试向量里还没有这个头。
	/// </summary>
	public static Signed Sign(
		string method,
		string url,
		IReadOnlyDictionary<string, string> headers,
		byte[] payload,
		string region,
		string service,
		AwsCredentials credentials,
		DateTimeOffset? timestamp = null,
		bool includeContentHash = true)
	{
		var uri = new Uri(url, UriKind.Absolute);
		var now = timestamp ?? DateTimeOffset.UtcNow;
		var amzDate = now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
		var dateStamp = now.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

		var payloadHash = Hex(SHA256.HashData(payload));

		// 头部值先去除首尾空白，名称转小写，再按名称排序。
		var canonical = new SortedDictionary<string, string>(StringComparer.Ordinal);
		foreach (var (name, value) in headers)
		{
			canonical[name.ToLowerInvariant()] = value.Trim();
		}

		if (!canonical.ContainsKey("host"))
		{
			canonical["host"] = uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
		}

		canonical["x-amz-date"] = amzDate;
		if (includeContentHash)
		{
			canonical["x-amz-content-sha256"] = payloadHash;
		}

		if (credentials.SessionToken is { Length: > 0 } token)
		{
			canonical["x-amz-security-token"] = token;
		}

		var canonicalHeaders = new StringBuilder();
		foreach (var (name, value) in canonical)
		{
			canonicalHeaders.Append(name).Append(':').Append(value).Append('\n');
		}

		var signedHeaders = string.Join(';', canonical.Keys);

		var canonicalQuery = CanonicalQueryString(uri.Query);
		var canonicalRequest =
			$"{method}\n" +
			$"{CanonicalUri(uri)}\n" +
			$"{canonicalQuery}\n" +
			$"{canonicalHeaders}\n" +
			$"{signedHeaders}\n" +
			payloadHash;

		var scope = $"{dateStamp}/{region}/{service}/aws4_request";
		var stringToSign =
			$"{Algorithm}\n" +
			$"{amzDate}\n" +
			$"{scope}\n" +
			Hex(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest)));

		var signingKey = DeriveSigningKey(credentials.SecretAccessKey, dateStamp, region, service);
		var signature = Hex(Hmac(signingKey, stringToSign));

		var authorization =
			$"{Algorithm} Credential={credentials.AccessKeyId}/{scope}, " +
			$"SignedHeaders={signedHeaders}, Signature={signature}";

		var result = new Dictionary<string, string>(StringComparer.Ordinal)
		{
			["x-amz-date"] = amzDate,
			["authorization"] = authorization,
		};

		if (includeContentHash)
		{
			result["x-amz-content-sha256"] = payloadHash;
		}

		if (credentials.SessionToken is { Length: > 0 })
		{
			result["x-amz-security-token"] = credentials.SessionToken;
		}

		return new Signed
		{
			CanonicalRequest = canonicalRequest,
			StringToSign = stringToSign,
			Signature = signature,
			Authorization = authorization,
			Headers = result,
		};
	}

	/// <summary>SigV4 的密钥派生链：日期、区域、服务，最后是请求后缀。</summary>
	public static byte[] DeriveSigningKey(string secretAccessKey, string dateStamp, string region, string service)
	{
		var date = Hmac(Encoding.UTF8.GetBytes("AWS4" + secretAccessKey), dateStamp);
		var regionKey = Hmac(date, region);
		var serviceKey = Hmac(regionKey, service);
		return Hmac(serviceKey, "aws4_request");
	}

	/// <summary>RFC 3986 的 URI 编码，SigV4 要求查询串与路径都按此处理。</summary>
	internal static string UriEncode(string value, bool encodeSlash)
	{
		var builder = new StringBuilder(value.Length * 2);
		foreach (var b in Encoding.UTF8.GetBytes(value))
		{
			var c = (char)b;
			var unreserved = c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_' or '.' or '~';
			if (unreserved || (c == '/' && !encodeSlash))
			{
				builder.Append(c);
				continue;
			}

			builder.Append('%').Append(((int)b).ToString("X2", CultureInfo.InvariantCulture));
		}

		return builder.ToString();
	}

	private static string CanonicalUri(Uri uri)
	{
		var path = uri.AbsolutePath;
		if (path.Length == 0)
		{
			return "/";
		}

		// Uri.AbsolutePath 会保留已有转义，所以必须先解码，否则 "x%3A0" 会被编成 "x%253A0"。
		return UriEncode(Uri.UnescapeDataString(path), encodeSlash: false);
	}

	private static string CanonicalQueryString(string query)
	{
		if (query.Length <= 1)
		{
			return string.Empty;
		}

		var pairs = new List<(string Key, string Value)>();
		foreach (var pair in query[1..].Split('&', StringSplitOptions.RemoveEmptyEntries))
		{
			var separator = pair.IndexOf('=');
			var key = separator < 0 ? pair : pair[..separator];
			var value = separator < 0 ? string.Empty : pair[(separator + 1)..];
			pairs.Add((UriEncode(Uri.UnescapeDataString(key), encodeSlash: true), UriEncode(Uri.UnescapeDataString(value), encodeSlash: true)));
		}

		return string.Join(
			"&",
			pairs.OrderBy(static pair => pair.Key, StringComparer.Ordinal)
				.ThenBy(static pair => pair.Value, StringComparer.Ordinal)
				.Select(static pair => $"{pair.Key}={pair.Value}"));
	}

	private static byte[] Hmac(byte[] key, string data) =>
		HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));

	internal static string Hex(byte[] bytes) => Convert.ToHexStringLower(bytes);
}
