using System.Buffers.Text;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PiWeft.Mcp;

/// <summary>授权服务器元数据（RFC 8414，或 OIDC 的 <c>openid-configuration</c>）。</summary>
public sealed record McpAuthorizationServerMetadata
{
	public required string AuthorizationEndpoint { get; init; }

	public required string TokenEndpoint { get; init; }

	public string? Issuer { get; init; }

	/// <summary>动态客户端注册端点（RFC 7591）；为 null 时必须在配置里写死 clientId。</summary>
	public string? RegistrationEndpoint { get; init; }

	public IReadOnlyList<string> ScopesSupported { get; init; } = [];
}

/// <summary>受保护资源元数据（RFC 9728）。</summary>
public sealed record McpProtectedResourceMetadata
{
	public required string Resource { get; init; }

	public IReadOnlyList<string> AuthorizationServers { get; init; } = [];

	public IReadOnlyList<string> ScopesSupported { get; init; } = [];
}

/// <summary>一次授权或刷新的结果。</summary>
public sealed record McpOAuthResult
{
	public required bool Ok { get; init; }

	public McpOAuthToken? Token { get; init; }

	public string? Error { get; init; }

	public static McpOAuthResult Success(McpOAuthToken token) => new() { Ok = true, Token = token };

	public static McpOAuthResult Failure(string error) => new() { Ok = false, Error = error };
}

/// <summary>
/// MCP 的 OAuth 2.1 授权码流程（含 PKCE 与回环回调）。
/// 依次走 RFC 9728（受保护资源元数据）→ RFC 8414（授权服务器元数据）→
/// 可选的 RFC 7591（动态客户端注册）→ 授权码 + PKCE → 令牌交换。
/// </summary>
public static partial class McpOAuth
{
	/// <summary>元数据与令牌请求的超时。</summary>
	private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

	/// <summary>等待用户在浏览器里完成授权的时间。</summary>
	public static readonly TimeSpan AuthorizationTimeout = TimeSpan.FromMinutes(5);

	public const string ClientName = "PiWeft";

	public const string RedirectPath = "/callback";

	/// <summary>PKCE 的 <c>code_verifier</c> 字节数（编码后 43–128 个字符）。</summary>
	private const int VerifierBytes = 48;

	/// <summary>
	/// 从 <c>WWW-Authenticate</c> 响应头里取 <c>resource_metadata</c>；
	/// 没有时退回 RFC 9728 的规范位置。
	/// </summary>
	public static string? ResourceMetadataUrl(string? wwwAuthenticate, string endpoint)
	{
		if (wwwAuthenticate is { Length: > 0 })
		{
			var match = ResourceMetadataPattern().Match(wwwAuthenticate);
			if (match.Success)
			{
				return match.Groups["url"].Value;
			}
		}

		if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
		{
			return null;
		}

		var path = uri.AbsolutePath.TrimEnd('/');
		return $"{uri.Scheme}://{uri.Authority}/.well-known/oauth-protected-resource{path}";
	}

	/// <summary>
	/// 发现授权服务器：先拉受保护资源元数据，再对它的第一个 <c>authorization_servers</c>
	/// 拉授权服务器元数据。<paramref name="challenge"/> 是上一次 401 响应的 <c>WWW-Authenticate</c> 值。
	/// </summary>
	public static async Task<McpAuthorizationServerMetadata?> DiscoverAsync(
		HttpClient http,
		string endpoint,
		string? challenge,
		CancellationToken cancellationToken,
		string? authServerMetadataUrl = null)
	{
		if (authServerMetadataUrl is { Length: > 0 })
		{
			var configured = await GetJsonAsync(http, authServerMetadataUrl, cancellationToken).ConfigureAwait(false);
			return configured is null ? null : MetadataFromDocument(configured, authServerMetadataUrl);
		}

		var resourceUrl = ResourceMetadataUrl(challenge, endpoint);
		var resource = resourceUrl is null ? null : await GetJsonAsync(http, resourceUrl, cancellationToken).ConfigureAwait(false);

		var authorizationServers = Strings(resource?["authorization_servers"]);
		var resourceScopes = Strings(resource?["scopes_supported"]);
		var issuer = authorizationServers.Count > 0 ? authorizationServers[0] : endpoint;

		foreach (var metadataUrl in AuthorizationServerMetadataUrls(issuer))
		{
			var document = await GetJsonAsync(http, metadataUrl, cancellationToken).ConfigureAwait(false);
			if (document is null)
			{
				continue;
			}

			var authorizationEndpoint = String(document, "authorization_endpoint");
			var tokenEndpoint = String(document, "token_endpoint");
			if (authorizationEndpoint is not { Length: > 0 } || tokenEndpoint is not { Length: > 0 })
			{
				continue;
			}

			return MetadataFromDocument(document, issuer, resourceScopes);
		}

		return null;
	}

	private static McpAuthorizationServerMetadata? MetadataFromDocument(
		JsonObject document,
		string fallbackIssuer,
		IReadOnlyList<string>? fallbackScopes = null)
	{
		var authorizationEndpoint = String(document, "authorization_endpoint");
		var tokenEndpoint = String(document, "token_endpoint");
		if (authorizationEndpoint is not { Length: > 0 } || tokenEndpoint is not { Length: > 0 })
		{
			return null;
		}

		var scopes = Strings(document["scopes_supported"]);
		return new McpAuthorizationServerMetadata
		{
			AuthorizationEndpoint = authorizationEndpoint,
			TokenEndpoint = tokenEndpoint,
			Issuer = String(document, "issuer") ?? fallbackIssuer,
			RegistrationEndpoint = String(document, "registration_endpoint"),
			ScopesSupported = scopes.Count > 0 ? scopes : fallbackScopes ?? [],
		};
	}

	/// <summary>按 RFC 8414 的顺序列出候选的授权服务器元数据地址。</summary>
	private static IEnumerable<string> AuthorizationServerMetadataUrls(string issuer)
	{
		if (!Uri.TryCreate(issuer, UriKind.Absolute, out var uri))
		{
			yield break;
		}

		var path = uri.AbsolutePath.TrimEnd('/');
		yield return $"{uri.Scheme}://{uri.Authority}/.well-known/oauth-authorization-server{path}";
		yield return $"{uri.Scheme}://{uri.Authority}/.well-known/openid-configuration{path}";
	}

	/// <summary>
	/// 跑完整的授权码 + PKCE 流程并在成功后把令牌写进存储。
	/// <paramref name="onAuthorizationUrl"/> 用于把授权地址展示给用户（无图形环境时尤其重要）；
	/// <paramref name="openBrowser"/> 为 <c>false</c> 时不尝试启动浏览器（测试与无人值守场景）。
	/// </summary>
	public static async Task<McpOAuthResult> AuthorizeAsync(
		HttpClient http,
		string endpoint,
		McpServerConfig config,
		McpAuthorizationServerMetadata metadata,
		string? agentDir,
		Action<string>? onAuthorizationUrl,
		CancellationToken cancellationToken,
		bool openBrowser = true)
	{
		var callbackPort = config.OAuth?.CallbackPort is > 0 and <= 65535 ? config.OAuth.CallbackPort.Value : 0;
		var listener = new TcpListener(IPAddress.Loopback, callbackPort);
		try
		{
			listener.Start();
		}
		catch (SocketException exception)
		{
			return McpOAuthResult.Failure($"无法启动本地回调端口：{exception.Message}");
		}

		try
		{
			var port = ((IPEndPoint)listener.LocalEndpoint).Port;
			var redirectUri = $"http://127.0.0.1:{port}{RedirectPath}";

			var existing = McpOAuthStore.Load(endpoint, agentDir);
			var clientId = config.ClientId ?? existing?.ClientId;
			if (clientId is not { Length: > 0 })
			{
				var registration = await RegisterClientAsync(
					http,
					metadata,
					redirectUri,
					cancellationToken,
					config.OAuth?.ClientName).ConfigureAwait(false);
				if (!registration.Ok)
				{
					return registration;
				}

				clientId = registration.Token!.Access;
			}

			var verifier = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(VerifierBytes));
			var challenge = Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
			var state = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16));

			var authorizationUrl = BuildAuthorizationUrl(metadata, config, clientId, redirectUri, challenge, state, endpoint);
			onAuthorizationUrl?.Invoke(authorizationUrl);
			if (openBrowser)
			{
				TryOpenBrowser(authorizationUrl);
			}

			var code = await WaitForCallbackAsync(listener, state, metadata.Issuer, cancellationToken).ConfigureAwait(false);
			if (code is null)
			{
				return McpOAuthResult.Failure("授权回调超时或被取消");
			}

			var token = await ExchangeAsync(http, metadata.TokenEndpoint, new Dictionary<string, string>
			{
				["grant_type"] = "authorization_code",
				["code"] = code,
				["redirect_uri"] = redirectUri,
				["client_id"] = clientId,
				["code_verifier"] = verifier,
				["resource"] = endpoint,
			}, clientId, metadata.Issuer, endpoint, cancellationToken).ConfigureAwait(false);

			if (!token.Ok)
			{
				return token;
			}

			McpOAuthStore.Save(endpoint, token.Token!, agentDir);
			return token;
		}
		finally
		{
			listener.Stop();
		}
	}

	/// <summary>
	/// 配置里是否同时给了 <c>clientId</c> 与 <c>clientSecret</c>：此时用客户端凭据授权
	/// （RFC 6749 §4.4），不需要浏览器参与。
	/// </summary>
	public static bool UsesClientCredentials(McpServerConfig config) =>
		config.ClientId is { Length: > 0 } && config.ClientSecret is { Length: > 0 };

	/// <summary>
	/// 客户端凭据授权：直接向令牌端点请求访问令牌并写进存储。
	/// 适用于网关/机器账号等无需用户参与的场景；没有 refresh token，到期后重新走本流程。
	/// </summary>
	public static async Task<McpOAuthResult> ClientCredentialsAsync(
		HttpClient http,
		string endpoint,
		McpServerConfig config,
		string? agentDir,
		CancellationToken cancellationToken,
		McpAuthorizationServerMetadata? metadata = null)
	{
		if (!UsesClientCredentials(config))
		{
			return McpOAuthResult.Failure("客户端凭据授权需要同时配置 clientId 与 clientSecret");
		}

		metadata ??= await DiscoverAsync(http, endpoint, challenge: null, cancellationToken).ConfigureAwait(false);
		if (metadata is null)
		{
			return McpOAuthResult.Failure("无法发现授权服务器元数据，客户端凭据授权需要可访问的元数据端点");
		}

		var parameters = new Dictionary<string, string>
		{
			["grant_type"] = "client_credentials",
			["client_id"] = config.ClientId!,
			["client_secret"] = config.ClientSecret!,
		};

		var scopes = config.Scopes.Count > 0 ? config.Scopes : metadata.ScopesSupported;
		if (scopes.Count > 0)
		{
			parameters["scope"] = string.Join(' ', scopes);
		}

		var token = await ExchangeAsync(
			http,
			metadata.TokenEndpoint,
			parameters,
			config.ClientId,
			metadata.Issuer,
			endpoint,
			cancellationToken).ConfigureAwait(false);

		if (!token.Ok)
		{
			return token;
		}

		McpOAuthStore.Save(endpoint, token.Token!, agentDir);
		return token;
	}

	/// <summary>用 refresh token 换一个新的访问令牌。</summary>
	public static async Task<McpOAuthResult> RefreshAsync(HttpClient http, McpOAuthToken token, CancellationToken cancellationToken)
	{
		if (token.TokenEndpoint is not { Length: > 0 } tokenEndpoint || token.Refresh is not { Length: > 0 } refresh)
		{
			return McpOAuthResult.Failure("没有可用于刷新的 refresh token");
		}

		var parameters = new Dictionary<string, string>
		{
			["grant_type"] = "refresh_token",
			["refresh_token"] = refresh,
		};

		if (token.ClientId is { Length: > 0 } clientId)
		{
			parameters["client_id"] = clientId;
		}

		if (token.Resource is { Length: > 0 } resource)
		{
			parameters["resource"] = resource;
		}

		return await ExchangeAsync(
			http,
			tokenEndpoint,
			parameters,
			token.ClientId,
			token.AuthorizationServer,
			token.Resource,
			cancellationToken,
			fallbackRefresh: refresh).ConfigureAwait(false);
	}

	/// <summary>
	/// 取一个可直接使用的令牌：已保存且不在刷新窗口内就原样返回；否则尝试刷新。
	/// 没有保存过令牌时返回 <c>null</c>（调用方应引导用户执行 <c>pi mcp login</c>）。
	/// </summary>
	public static async Task<McpOAuthToken?> ResolveAsync(
		HttpClient http,
		string endpoint,
		string? agentDir,
		CancellationToken cancellationToken)
	{
		var token = McpOAuthStore.Load(endpoint, agentDir);
		if (token is null)
		{
			return null;
		}

		if (!token.IsExpiring(DateTimeOffset.UtcNow, McpOAuthStore.ExpirySkew) || token.Refresh is not { Length: > 0 })
		{
			return token;
		}

		var refreshed = await RefreshAsync(http, token, cancellationToken).ConfigureAwait(false);
		if (!refreshed.Ok)
		{
			// 刷新失败时退回旧令牌，让请求自己去撞 401 并给出真实错误。
			return token;
		}

		McpOAuthStore.Save(endpoint, refreshed.Token!, agentDir);
		return refreshed.Token;
	}

	/// <summary>构造授权地址（PKCE + resource 参数）。</summary>
	internal static string BuildAuthorizationUrl(
		McpAuthorizationServerMetadata metadata,
		McpServerConfig config,
		string clientId,
		string redirectUri,
		string challenge,
		string state,
		string resource)
	{
		var scopes = config.Scopes.Count > 0 ? config.Scopes : metadata.ScopesSupported;
		var query = new List<string>
		{
			"response_type=code",
			$"client_id={Encode(clientId)}",
			$"redirect_uri={Encode(redirectUri)}",
			$"state={Encode(state)}",
			$"code_challenge={Encode(challenge)}",
			"code_challenge_method=S256",
			$"resource={Encode(resource)}",
		};

		if (scopes.Count > 0)
		{
			query.Add($"scope={Encode(string.Join(' ', scopes))}");
		}

		var separator = metadata.AuthorizationEndpoint.Contains('?', StringComparison.Ordinal) ? '&' : '?';
		return $"{metadata.AuthorizationEndpoint}{separator}{string.Join('&', query)}";
	}

	/// <summary>动态注册一个公共客户端（RFC 7591）；把 client_id 临时放在结果的 <c>Access</c> 字段里。</summary>
	private static async Task<McpOAuthResult> RegisterClientAsync(
		HttpClient http,
		McpAuthorizationServerMetadata metadata,
		string redirectUri,
		CancellationToken cancellationToken,
		string? clientName = null)
	{
		if (metadata.RegistrationEndpoint is not { Length: > 0 } registrationEndpoint)
		{
			return McpOAuthResult.Failure("授权服务器不支持动态客户端注册，请在 mcp.json 里配置 clientId");
		}

		var payload = new JsonObject
		{
			["redirect_uris"] = new JsonArray(redirectUri),
			["client_name"] = clientName is { Length: > 0 } ? clientName : ClientName,
			["grant_types"] = new JsonArray("authorization_code", "refresh_token"),
			["response_types"] = new JsonArray("code"),
			["token_endpoint_auth_method"] = "none",
		};

		using var request = new HttpRequestMessage(HttpMethod.Post, registrationEndpoint)
		{
			Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
		};
		request.Headers.TryAddWithoutValidation("accept", "application/json");

		var document = await SendJsonAsync(http, request, cancellationToken).ConfigureAwait(false);
		var clientId = document is null ? null : String(document, "client_id");
		return clientId is { Length: > 0 }
			? McpOAuthResult.Success(new McpOAuthToken { Access = clientId })
			: McpOAuthResult.Failure("动态客户端注册没有返回 client_id");
	}

	/// <summary>用授权码或 refresh token 换令牌。</summary>
	private static async Task<McpOAuthResult> ExchangeAsync(
		HttpClient http,
		string tokenEndpoint,
		Dictionary<string, string> parameters,
		string? clientId,
		string? authorizationServer,
		string? resource,
		CancellationToken cancellationToken,
		string? fallbackRefresh = null)
	{
		using var request = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint)
		{
			Content = new FormUrlEncodedContent(parameters),
		};
		request.Headers.TryAddWithoutValidation("accept", "application/json");

		var document = await SendJsonAsync(http, request, cancellationToken).ConfigureAwait(false);
		var access = document is null ? null : String(document, "access_token");
		if (access is not { Length: > 0 })
		{
			var error = document is null ? "令牌端点没有返回可解析的响应" : String(document, "error_description") ?? String(document, "error");
			return McpOAuthResult.Failure($"令牌请求失败：{error ?? "缺少 access_token"}");
		}

		// 先按整数读（绝大多数服务器都发整数）；小数形态再兜底，避免直接截断或读不出来。
		long? expiresIn = null;
		if (document!["expires_in"] is JsonValue expiry)
		{
			if (expiry.TryGetValue<long>(out var whole) && whole > 0)
			{
				expiresIn = whole;
			}
			else if (expiry.TryGetValue<double>(out var fractional) && fractional > 0)
			{
				expiresIn = (long)fractional;
			}
		}

		return McpOAuthResult.Success(new McpOAuthToken
		{
			Access = access,
			// 刷新时服务器可能不返回新的 refresh token，此时沿用旧的。
			Refresh = String(document, "refresh_token") ?? fallbackRefresh,
			ExpiresAt = expiresIn is { } lifetime ? DateTimeOffset.UtcNow.ToUnixTimeSeconds() + lifetime : null,
			TokenType = String(document, "token_type"),
			Scope = String(document, "scope"),
			ClientId = clientId,
			TokenEndpoint = tokenEndpoint,
			AuthorizationServer = authorizationServer,
			Resource = resource,
		});
	}

	/// <summary>等待回环回调，返回授权码；超时或 state 不匹配时返回 null。</summary>
	private static async Task<string?> WaitForCallbackAsync(
		TcpListener listener,
		string expectedState,
		string? expectedIssuer,
		CancellationToken cancellationToken)
	{
		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeout.CancelAfter(AuthorizationTimeout);

		try
		{
			while (true)
			{
				using var client = await listener.AcceptTcpClientAsync(timeout.Token).ConfigureAwait(false);
				await using var stream = client.GetStream();

				var requestLine = await ReadLineAsync(stream, timeout.Token).ConfigureAwait(false);
				// 读掉请求头，避免连接被半开。
				while (await ReadLineAsync(stream, timeout.Token).ConfigureAwait(false) is { Length: > 0 })
				{
				}

				var target = requestLine.Split(' ') is [_, var path, ..] ? path : string.Empty;
				var query = QueryOf(target, out var error);
				var code = query.GetValueOrDefault("code");
				var state = query.GetValueOrDefault("state");
				var issuer = query.GetValueOrDefault("iss");

				if (error is { Length: > 0 })
				{
					await WriteCallbackResponseAsync(stream, ok: false, timeout.Token).ConfigureAwait(false);
					return null;
				}

				// 浏览器可能先发来 favicon、预连接或端口扫描之类的杂散请求：
				// 没有 code 就继续等真正的回调，不要因为一次杂散连接就判定登录失败。
				if (code is not { Length: > 0 })
				{
					await WriteCallbackResponseAsync(stream, ok: false, timeout.Token).ConfigureAwait(false);
					continue;
				}

				// RFC 9207：带 iss 时必须能校验；校验不了就失败，而不是接受未知发行方。
				var issuerValid = issuer is null
					? expectedIssuer is null
					: expectedIssuer is not null && string.Equals(issuer, expectedIssuer, StringComparison.Ordinal);
				if (!issuerValid && issuer is not null)
				{
					await WriteCallbackResponseAsync(stream, ok: false, timeout.Token).ConfigureAwait(false);
					return null;
				}

				// state 不匹配时不向浏览器显示「已完成授权」。
				var valid = string.Equals(state, expectedState, StringComparison.Ordinal);
				await WriteCallbackResponseAsync(stream, valid, timeout.Token).ConfigureAwait(false);
				return valid ? code : null;
			}
		}
		catch (OperationCanceledException)
		{
			return null;
		}
		catch (IOException)
		{
			return null;
		}
	}

	private static async Task<string> ReadLineAsync(NetworkStream stream, CancellationToken cancellationToken)
	{
		var buffer = new byte[1];
		var builder = new StringBuilder();
		while (true)
		{
			var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
			if (read == 0)
			{
				break;
			}

			if (buffer[0] == (byte)'\n')
			{
				break;
			}

			if (buffer[0] != (byte)'\r')
			{
				builder.Append((char)buffer[0]);
			}
		}

		return builder.ToString();
	}

	private static async Task WriteCallbackResponseAsync(NetworkStream stream, bool ok, CancellationToken cancellationToken)
	{
		var message = ok ? "PiWeft 已完成授权，可以关闭此页面。" : "授权未完成，请回到 PiWeft 查看提示。";
		var body = Encoding.UTF8.GetBytes($"<!doctype html><meta charset=\"utf-8\"><title>PiWeft</title><p>{message}</p>");
		var header = Encoding.ASCII.GetBytes(
			"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\n" +
			$"Content-Length: {body.Length}\r\nConnection: close\r\n\r\n");
		await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
		await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
		await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
	}

	/// <summary>解析回调 URL 的查询串。</summary>
	private static Dictionary<string, string> QueryOf(string target, out string? error)
	{
		var result = new Dictionary<string, string>(StringComparer.Ordinal);
		error = null;

		var index = target.IndexOf('?', StringComparison.Ordinal);
		if (index < 0)
		{
			return result;
		}

		foreach (var pair in target[(index + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
		{
			var separator = pair.IndexOf('=', StringComparison.Ordinal);
			if (separator <= 0)
			{
				continue;
			}

			var key = Uri.UnescapeDataString(pair[..separator]);
			var value = Uri.UnescapeDataString(pair[(separator + 1)..].Replace('+', ' '));
			result[key] = value;
			if (key is "error")
			{
				error = value;
			}
		}

		return result;
	}

	private static void TryOpenBrowser(string url)
	{
		try
		{
			using var process = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
		}
		catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or PlatformNotSupportedException)
		{
			// 无图形环境：调用方已经把 URL 打印出来，用户可以手动打开。
		}
	}

	private static async Task<JsonObject?> GetJsonAsync(HttpClient http, string url, CancellationToken cancellationToken)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, url);
		request.Headers.TryAddWithoutValidation("accept", "application/json");
		return await SendJsonAsync(http, request, cancellationToken).ConfigureAwait(false);
	}

	private static async Task<JsonObject?> SendJsonAsync(HttpClient http, HttpRequestMessage request, CancellationToken cancellationToken)
	{
		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeout.CancelAfter(RequestTimeout);

		try
		{
			using var response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
			var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
			return JsonNode.Parse(body) as JsonObject;
		}
		catch (Exception exception) when (exception is HttpRequestException or IOException or OperationCanceledException or JsonException)
		{
			return null;
		}
	}

	private static string? String(JsonObject? document, string name) =>
		document?[name] is JsonValue value && value.GetValueKind() == JsonValueKind.String
			? value.GetValue<string>()
			: null;

	private static List<string> Strings(JsonNode? node)
	{
		var values = new List<string>();
		if (node is not JsonArray array)
		{
			return values;
		}

		foreach (var item in array)
		{
			if (item is JsonValue value && value.GetValueKind() == JsonValueKind.String)
			{
				values.Add(value.GetValue<string>());
			}
		}

		return values;
	}

	private static string Encode(string value) => Uri.EscapeDataString(value);

	[GeneratedRegex("resource_metadata\\s*=\\s*\"(?<url>[^\"]+)\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
	private static partial Regex ResourceMetadataPattern();
}
