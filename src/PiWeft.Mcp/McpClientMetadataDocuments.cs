using System.Security.Cryptography;
using System.Text;

namespace PiWeft.Mcp;

/// Client ID Metadata Document（CIMD）：客户端用自己发布的 HTTPS 文档充当 <c>client_id</c>，
/// 不必动态注册。pi 在 <c>https://pi.dev/oauth</c> 下发布这些文档，本端口沿用同一份，
/// 因此两个实现能互相接续同一个授权服务器上的登录。
public static class McpClientMetadataDocuments
{
	public const string BaseUrl = "https://pi.dev/oauth";

	/// 文档里列出的回环回调：<c>http://localhost/&lt;path&gt;</c> 或 <c>http://127.0.0.1/&lt;path&gt;</c>，端口任意（RFC 8252）。
	public static bool ListsRedirectUrl(string redirectUrl)
	{
		if (!Uri.TryCreate(redirectUrl, UriKind.Absolute, out var uri) ||
			!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}

		var host = uri.Host;
		return (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
			string.Equals(host, "127.0.0.1", StringComparison.Ordinal)) &&
			string.Equals(uri.AbsolutePath, McpOAuth.RedirectPath, StringComparison.Ordinal);
	}

	/// 文档 URL 与它的回调地址。授权服务器不支持 RFC 9207 的 <c>iss</c> 时，
	/// 文档与回调路径按 MCP 服务器区分，避免响应串到别的授权服务器。
	public static (string Url, string RedirectUrl)? Resolve(
		string serverUrl,
		string redirectUrl,
		McpAuthorizationServerMetadata metadata)
	{
		if (!metadata.ClientIdMetadataDocumentSupported ||
			!metadata.TokenEndpointAuthMethodsSupported.Contains("none", StringComparer.Ordinal))
		{
			return null;
		}

		if (metadata.AuthorizationResponseIssParameterSupported)
		{
			return ($"{BaseUrl}/client.json", redirectUrl);
		}

		if (!Uri.TryCreate(redirectUrl, UriKind.Absolute, out var uri))
		{
			return null;
		}

		var id = CallbackId(serverUrl);
		var path = $"{uri.AbsolutePath.TrimEnd('/')}/{id}";
		var builder = new UriBuilder(uri) { Path = path };
		return ($"{BaseUrl}/{id}/client.json", builder.Uri.ToString());
	}

	/// 12 字符的服务器标识，与 pi 一样取 URL（去掉 fragment）的 SHA-256 前 9 字节。
	public static string CallbackId(string serverUrl)
	{
		if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri))
		{
			return "unknown";
		}

		var href = new UriBuilder(uri) { Fragment = string.Empty }.Uri.AbsoluteUri;
		var hash = SHA256.HashData(Encoding.UTF8.GetBytes(href));
		return Base64UrlEncode(hash.AsSpan(0, 9));
	}

	private static string Base64UrlEncode(ReadOnlySpan<byte> bytes) =>
		System.Buffers.Text.Base64Url.EncodeToString(bytes);
}
