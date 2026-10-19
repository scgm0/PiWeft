using System.Text.Json.Nodes;

namespace PiWeft.Ai.Auth;

/// <summary>Meta Muse 订阅 OAuth：设备码获取身份令牌，再 mint 一天的 Model API key。</summary>
public static class MetaOAuth
{
	public const string ProviderKey = "meta";
	private const string ClientId = "1031625952748946";
	private const string DeviceAuthorizationUrl = "https://auth.meta.com/oidc/device/authorization/";
	private const string DeviceTokenUrl = "https://auth.meta.com/oidc/device/token/";
	private const string MintUrl = "https://api.meta.ai/muse-code/key";
	private static readonly TimeSpan KeyLifetime = TimeSpan.FromHours(24);

	public static async Task<OAuthCredential> LoginAsync(
		TextWriter output,
		string? agentDir = null,
		CancellationToken cancellationToken = default)
	{
		using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
		using var start = await http.PostAsync(
			DeviceAuthorizationUrl,
			new FormUrlEncodedContent(new Dictionary<string, string> { ["client_id"] = ClientId }),
			cancellationToken).ConfigureAwait(false);
		var startBody = await start.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
		if (!start.IsSuccessStatusCode)
		{
			throw new CodexAuthException($"Meta 设备码请求失败（HTTP {(int)start.StatusCode}）：{startBody}");
		}

		var device = JsonNode.Parse(startBody) as JsonObject
			?? throw new CodexAuthException("Meta 设备码响应不是对象");
		var deviceCode = device["device_code"]?.GetValue<string>();
		var userCode = device["user_code"]?.GetValue<string>();
		var verification = device["verification_uri_complete"]?.GetValue<string>() ??
			device["verification_uri"]?.GetValue<string>();
		var interval = device["interval"]?.GetValue<int>() ?? 5;
		var expiresIn = device["expires_in"]?.GetValue<int>() ?? 900;
		if (deviceCode is not { Length: > 0 } || userCode is not { Length: > 0 } || verification is not { Length: > 0 })
		{
			throw new CodexAuthException("Meta 设备码响应缺少字段");
		}

		output.WriteLine($"在 {verification} 输入代码 {userCode}");
		var identityToken = await PollIdentityTokenAsync(
			http, deviceCode, interval, expiresIn, cancellationToken).ConfigureAwait(false);
		var credential = await MintAsync(http, identityToken, cancellationToken).ConfigureAwait(false);
		OAuthStore.Save(ProviderKey, credential, agentDir);
		output.WriteLine("Meta 登录成功，Model API key 已写入 auth.json。");
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
			var refreshed = await MintAsync(http, credential.Refresh, cancellationToken).ConfigureAwait(false);
			OAuthStore.Save(ProviderKey, refreshed, agentDir);
			return refreshed;
		}
		catch (CodexAuthException)
		{
			return null;
		}
	}

	private static async Task<string> PollIdentityTokenAsync(
		HttpClient http,
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
				DeviceTokenUrl,
				new FormUrlEncodedContent(new Dictionary<string, string>
				{
					["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
					["device_code"] = deviceCode,
					["client_id"] = ClientId,
				}),
				cancellationToken).ConfigureAwait(false);
			var body = await poll.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
			var json = JsonNode.Parse(body) as JsonObject;
			if (poll.IsSuccessStatusCode && json?["access_token"]?.GetValue<string>() is { Length: > 0 } token)
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

			throw new CodexAuthException($"Meta 登录失败：{error ?? body}");
		}

		throw new CodexAuthException("Meta 设备码登录超时");
	}

	private static async Task<OAuthCredential> MintAsync(
		HttpClient http,
		string identityToken,
		CancellationToken cancellationToken)
	{
		using var request = new HttpRequestMessage(HttpMethod.Post, MintUrl)
		{
			Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
		};
		request.Headers.TryAddWithoutValidation("accept", "application/json");
		request.Headers.TryAddWithoutValidation("authorization", $"Bearer {identityToken}");
		request.Headers.TryAddWithoutValidation("x-api-version", "1.0.0");
		using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
		var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
		if (!response.IsSuccessStatusCode)
		{
			throw new CodexAuthException($"Meta API key mint 失败（HTTP {(int)response.StatusCode}）：{body}");
		}

		var json = JsonNode.Parse(body) as JsonObject;
		var key = json?["api_key"]?.GetValue<string>();
		if (key is not { Length: > 0 })
		{
			var action = json?["action_url"]?.GetValue<string>();
			throw new CodexAuthException(
				$"Meta 没有返回 API key。{(action is { Length: > 0 } ? $"请先完成设置：{action}" : string.Empty)}");
		}

		return new OAuthCredential
		{
			Access = key,
			Refresh = identityToken,
			Expires = DateTimeOffset.UtcNow.Add(KeyLifetime).ToUnixTimeSeconds(),
		};
	}
}
