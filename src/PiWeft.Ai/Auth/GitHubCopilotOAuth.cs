using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PiWeft.Ai.Auth;

/// <summary>GitHub Copilot OAuth：GitHub 设备码 → Copilot 短期令牌。</summary>
public static partial class GitHubCopilotOAuth
{
	public const string ProviderKey = "github-copilot";
	private const string ClientId = "Iv1.b507a08c87ecfe98";
	private const string CopilotUserAgent = "GitHubCopilotChat/0.35.0";

	public static async Task<OAuthCredential> LoginAsync(
		TextReader input,
		TextWriter output,
		string? agentDir = null,
		CancellationToken cancellationToken = default)
	{
		output.Write("GitHub Enterprise URL/domain（直接回车使用 github.com）：");
		output.Flush();
		var enterprise = (await input.ReadLineAsync(cancellationToken).ConfigureAwait(false))?.Trim();
		var domain = NormalizeDomain(enterprise) ?? "github.com";

		using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
		using var start = await http.PostAsync(
			$"https://{domain}/login/device/code",
			new FormUrlEncodedContent(new Dictionary<string, string>
			{
				["client_id"] = ClientId,
				["scope"] = "read:user",
			}),
			cancellationToken).ConfigureAwait(false);
		var startBody = await start.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
		if (!start.IsSuccessStatusCode)
		{
			throw new CodexAuthException($"GitHub 设备码请求失败（HTTP {(int)start.StatusCode}）：{startBody}");
		}

		var device = JsonNode.Parse(startBody) as JsonObject
			?? throw new CodexAuthException("GitHub 设备码响应不是对象");
		var deviceCode = device["device_code"]?.GetValue<string>();
		var userCode = device["user_code"]?.GetValue<string>();
		var verification = device["verification_uri"]?.GetValue<string>();
		var interval = device["interval"]?.GetValue<int>() ?? 5;
		var expiresIn = device["expires_in"]?.GetValue<int>() ?? 900;
		if (deviceCode is not { Length: > 0 } || userCode is not { Length: > 0 } || verification is not { Length: > 0 })
		{
			throw new CodexAuthException("GitHub 设备码响应缺少字段");
		}

		output.WriteLine($"在 {verification} 输入代码 {userCode}");
		var githubToken = await PollAccessTokenAsync(http, domain, deviceCode, interval, expiresIn, cancellationToken)
			.ConfigureAwait(false);
		var credential = await ExchangeCopilotTokenAsync(http, githubToken, domain, cancellationToken).ConfigureAwait(false);
		OAuthStore.Save(ProviderKey, credential, agentDir);
		output.WriteLine("GitHub Copilot 登录成功，凭据已写入 auth.json。");
		return credential;
	}

	public static async Task<OAuthCredential?> RefreshAsync(
		OAuthCredential credential,
		string? agentDir = null,
		CancellationToken cancellationToken = default)
	{
		if (credential.Refresh.Length == 0)
		{
			return null;
		}

		using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
		try
		{
			var domain = credential.EnterpriseUrl is { Length: > 0 } enterprise
				? NormalizeDomain(enterprise) ?? "github.com"
				: "github.com";
			var refreshed = await ExchangeCopilotTokenAsync(http, credential.Refresh, domain, cancellationToken)
				.ConfigureAwait(false);
			OAuthStore.Save(ProviderKey, refreshed, agentDir);
			return refreshed;
		}
		catch (CodexAuthException)
		{
			return null;
		}
	}

	private static async Task<string> PollAccessTokenAsync(
		HttpClient http,
		string domain,
		string deviceCode,
		int interval,
		int expiresIn,
		CancellationToken cancellationToken)
	{
		var deadline = DateTimeOffset.UtcNow.AddSeconds(expiresIn);
		while (DateTimeOffset.UtcNow < deadline)
		{
			await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, interval)), cancellationToken).ConfigureAwait(false);
			using var poll = await http.PostAsync(
				$"https://{domain}/login/oauth/access_token",
				new FormUrlEncodedContent(new Dictionary<string, string>
				{
					["client_id"] = ClientId,
					["device_code"] = deviceCode,
					["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
				}),
				cancellationToken).ConfigureAwait(false);
			var body = await poll.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
			var json = JsonNode.Parse(body) as JsonObject;
			if (json?["access_token"]?.GetValue<string>() is { Length: > 0 } token)
			{
				return token;
			}

			var error = json?["error"]?.GetValue<string>();
			if (error == "authorization_pending")
			{
				continue;
			}

			if (error == "slow_down")
			{
				interval += 5;
				continue;
			}

			throw new CodexAuthException($"GitHub 设备授权失败：{error ?? body}");
		}

		throw new CodexAuthException("GitHub 设备授权超时");
	}

	private static async Task<OAuthCredential> ExchangeCopilotTokenAsync(
		HttpClient http,
		string githubToken,
		string domain,
		CancellationToken cancellationToken)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.{domain}/copilot_internal/v2/token");
		request.Headers.TryAddWithoutValidation("accept", "application/json");
		request.Headers.TryAddWithoutValidation("authorization", $"Bearer {githubToken}");
		request.Headers.TryAddWithoutValidation("user-agent", CopilotUserAgent);
		request.Headers.TryAddWithoutValidation("editor-version", "vscode/1.107.0");
		request.Headers.TryAddWithoutValidation("editor-plugin-version", "copilot-chat/0.35.0");
		request.Headers.TryAddWithoutValidation("copilot-integration-id", "vscode-chat");
		using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
		var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
		if (!response.IsSuccessStatusCode)
		{
			throw new CodexAuthException($"Copilot 令牌交换失败（HTTP {(int)response.StatusCode}）：{body}");
		}

		var json = JsonNode.Parse(body) as JsonObject
			?? throw new CodexAuthException("Copilot 令牌响应不是对象");
		var token = json["token"]?.GetValue<string>();
		var expiresAt = json["expires_at"]?.GetValue<double>() ?? 0;
		if (token is not { Length: > 0 } || expiresAt <= 0)
		{
			throw new CodexAuthException("Copilot 令牌响应缺少字段");
		}

		return new OAuthCredential
		{
			Access = token,
			Refresh = githubToken,
			Expires = (long)expiresAt - 300,
			EnterpriseUrl = domain == "github.com" ? null : domain,
			BaseUrl = BaseUrlFromToken(token) ?? (domain == "github.com" ? "https://api.individual.githubcopilot.com" : $"https://copilot-api.{domain}"),
		};
	}

	private static string? BaseUrlFromToken(string token)
	{
		var match = ProxyEndpoint().Match(token);
		if (!match.Success)
		{
			return null;
		}

		var host = match.Groups[1].Value.Replace("proxy.", "api.", StringComparison.Ordinal);
		return $"https://{host}";
	}

	private static string? NormalizeDomain(string? input)
	{
		if (string.IsNullOrWhiteSpace(input))
		{
			return null;
		}

		var value = input.Trim();
		try
		{
			var uri = value.Contains("://", StringComparison.Ordinal) ? new Uri(value) : new Uri($"https://{value}");
			return uri.Host;
		}
		catch (UriFormatException)
		{
			return null;
		}
	}

	[GeneratedRegex("proxy-ep=([^;]+)")]
	private static partial Regex ProxyEndpoint();
}
