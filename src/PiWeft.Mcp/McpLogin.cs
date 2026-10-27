using System.Net.Http.Headers;
using System.Text;

namespace PiWeft.Mcp;

/// <summary>
/// MCP 服务器的授权登录流程：探测 <c>WWW-Authenticate</c> → 发现授权服务器元数据 →
/// 客户端凭据授权或浏览器授权码（PKCE），凭据落 <see cref="McpOAuthStore"/>。
/// <para>
/// 这是 CLI（<c>pi mcp login</c>）与桌面配置页共用的唯一实现。两处各写一遍的后果不是重复代码，
/// 而是行为分叉：一边支持客户端凭据、另一边不支持，用户就会在某个界面上登不上某个服务器。
/// </para>
/// </summary>
public static class McpLogin
{
	/// <summary>登录结果。<see cref="Message"/> 是给人看的一句话（成功或失败原因）。</summary>
	public sealed record Outcome(bool Ok, string Message, bool UsedBrowser)
	{
		public static Outcome Failure(string message) => new(false, message, false);
	}

	private const string InitializeBody =
		"""{"jsonrpc":"2.0","id":0,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"PiWeft","version":"0.1.0"}}}""";

	/// <summary>
	/// 执行登录。<paramref name="onAuthorizeUrl"/> 是浏览器授权时要打开的链接：由宿主决定怎么呈现
	/// （CLI 打印出来让用户手动访问，桌面直接调系统浏览器）。为 <c>null</c> 表示宿主无法展示链接。
	/// </summary>
	public static async Task<Outcome> RunAsync(
		HttpClient http,
		McpServerConfig server,
		string? agentDir = null,
		Action<string>? onAuthorizeUrl = null,
		CancellationToken cancellationToken = default)
	{
		if (server.Url is not { Length: > 0 } url)
		{
			return Outcome.Failure("只有 HTTP 传输的服务器需要授权；stdio 服务器没有 OAuth。");
		}

		var challenge = await ProbeChallengeAsync(http, url, cancellationToken).ConfigureAwait(false);
		var metadata = await McpOAuth
			.DiscoverAsync(http, url, challenge, cancellationToken, server.OAuth?.AuthServerMetadataUrl)
			.ConfigureAwait(false);
		if (metadata is null)
		{
			return Outcome.Failure("无法发现授权服务器元数据（检查网络，或服务器返回的 WWW-Authenticate）。");
		}

		// 配了 clientId + clientSecret 的服务器不需要人在浏览器里点一遍。
		if (McpOAuth.UsesClientCredentials(server))
		{
			var granted = await McpOAuth
				.ClientCredentialsAsync(http, url, server, agentDir, cancellationToken, metadata)
				.ConfigureAwait(false);
			return granted.Ok
				? new Outcome(true, $"已登录（客户端凭据授权），令牌保存在 {McpOAuthStore.Path(agentDir)}", UsedBrowser: false)
				: Outcome.Failure($"客户端凭据授权失败：{granted.Error}");
		}

		var result = await McpOAuth.AuthorizeAsync(
			http,
			url,
			server,
			metadata,
			agentDir,
			onAuthorizeUrl is null ? static _ => { } : onAuthorizeUrl,
			cancellationToken).ConfigureAwait(false);

		return result.Ok
			? new Outcome(true, $"已登录，令牌保存在 {McpOAuthStore.Path(agentDir)}", UsedBrowser: true)
			: Outcome.Failure($"授权失败：{result.Error}");
	}

	/// <summary>
	/// 是否已经存有可用令牌（含已过期的：过期只是「要重新授权」，不是「从没授权过」，
	/// 界面上要能区分这两种状态）。
	/// </summary>
	public static McpOAuthToken? StoredToken(McpServerConfig server, string? agentDir = null) =>
		server.Url is { Length: > 0 } url ? McpOAuthStore.Load(url, agentDir) : null;

	public static bool SignedIn(McpServerConfig server, string? agentDir = null) => StoredToken(server, agentDir) is not null;

	/// <summary>令牌是否已过期（没有过期时间的令牌不算过期）。</summary>
	public static bool IsExpired(McpServerConfig server, string? agentDir = null) =>
		StoredToken(server, agentDir) is { } token && token.IsExpiring(DateTimeOffset.UtcNow, TimeSpan.Zero);

	public static bool Logout(McpServerConfig server, string? agentDir = null) =>
		server.Url is { Length: > 0 } url && McpOAuthStore.Remove(url, agentDir);

	/// <summary>
	/// 发一次 <c>initialize</c> 只为拿 <c>WWW-Authenticate</c>：OAuth 发现规范允许服务器用该头
	/// 指明该用哪个授权服务器，漏了它就只能靠猜测的元数据地址（有些服务器不提供默认地址）。
	/// </summary>
	public static async Task<string?> ProbeChallengeAsync(HttpClient http, string url, CancellationToken cancellationToken)
	{
		try
		{
			using var request = new HttpRequestMessage(HttpMethod.Post, url)
			{
				Content = new StringContent(InitializeBody, Encoding.UTF8, "application/json"),
			};
			request.Headers.TryAddWithoutValidation("accept", "application/json, text/event-stream");

			using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
			return response.Headers.TryGetValues("WWW-Authenticate", out var values) ? string.Join(' ', values) : null;
		}
		catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException)
		{
			// 探测失败不影响后续发现流程（走 RFC 默认的元数据地址）。
			return null;
		}
	}
}
