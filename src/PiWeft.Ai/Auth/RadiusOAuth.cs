using System.Text.Json.Nodes;

namespace PiWeft.Ai.Auth;

/// <summary>Radius gateway OAuth 设备码流程，并在登录后读取 gateway 的 baseUrl。</summary>
public static class RadiusOAuth
{
	public const string ProviderKey = "radius";
	private const string ClientId = "pi-gateway";
	private const string Scope = "gateway offline_access";

	public static string Gateway =>
		(Environment.GetEnvironmentVariable("PI_RADIUS_GATEWAY") is { Length: > 0 } value
			? value
			: "https://radius.pi.dev").TrimEnd('/');

	public static async Task<OAuthCredential> LoginAsync(
		TextWriter output,
		string? agentDir = null,
		CancellationToken cancellationToken = default)
	{
		using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
		using var start = await http.PostAsync(
			$"{Gateway}/v1/oauth/device",
			new FormUrlEncodedContent(new Dictionary<string, string>
			{
				["client_id"] = ClientId,
				["scope"] = Scope,
			}),
			cancellationToken).ConfigureAwait(false);
		var startBody = await start.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
		if (!start.IsSuccessStatusCode)
		{
			throw new CodexAuthException($"Radius 设备码请求失败（HTTP {(int)start.StatusCode}）：{startBody}");
		}

		var device = JsonNode.Parse(startBody) as JsonObject
			?? throw new CodexAuthException("Radius 设备码响应不是对象");
		var deviceCode = device["device_code"]?.GetValue<string>();
		var userCode = device["user_code"]?.GetValue<string>();
		var verification = device["verification_uri"]?.GetValue<string>();
		var interval = device["interval"]?.GetValue<int>() ?? 5;
		var expiresIn = device["expires_in"]?.GetValue<int>() ?? 900;
		if (deviceCode is not { Length: > 0 } || userCode is not { Length: > 0 } || verification is not { Length: > 0 })
		{
			throw new CodexAuthException("Radius 设备码响应缺少字段");
		}

		output.WriteLine($"在 {verification} 输入代码 {userCode}");
		var credential = await PollAsync(http, deviceCode, interval, expiresIn, cancellationToken).ConfigureAwait(false);
		credential = await AddGatewayBaseUrlAsync(http, credential, cancellationToken).ConfigureAwait(false);
		OAuthStore.Save(ProviderKey, credential, agentDir);
		output.WriteLine("Radius 登录成功，凭据已写入 auth.json。");
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
			using var response = await http.PostAsync(
				$"{Gateway}/v1/oauth/token",
				new FormUrlEncodedContent(new Dictionary<string, string>
				{
					["grant_type"] = "refresh_token",
					["client_id"] = ClientId,
					["refresh_token"] = credential.Refresh,
				}),
				cancellationToken).ConfigureAwait(false);
			var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
			if (!response.IsSuccessStatusCode)
			{
				return null;
			}

			var refreshed = ParseToken(body);
			refreshed = await AddGatewayBaseUrlAsync(http, refreshed, cancellationToken).ConfigureAwait(false);
			OAuthStore.Save(ProviderKey, refreshed, agentDir);
			return refreshed;
		}
		catch (Exception exception) when (exception is HttpRequestException or System.Text.Json.JsonException or CodexAuthException)
		{
			return null;
		}
	}

	private static async Task<OAuthCredential> PollAsync(
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
			using var response = await http.PostAsync(
				$"{Gateway}/v1/oauth/token",
				new FormUrlEncodedContent(new Dictionary<string, string>
				{
					["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
					["client_id"] = ClientId,
					["device_code"] = deviceCode,
				}),
				cancellationToken).ConfigureAwait(false);
			var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
			var json = JsonNode.Parse(body) as JsonObject;
			if (response.IsSuccessStatusCode && json?["access_token"]?.GetValue<string>() is { Length: > 0 })
			{
				return ParseToken(body);
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

			throw new CodexAuthException($"Radius 登录失败：{error ?? body}");
		}

		throw new CodexAuthException("Radius 设备码登录超时");
	}

	private static OAuthCredential ParseToken(string body)
	{
		var json = JsonNode.Parse(body) as JsonObject
			?? throw new CodexAuthException("Radius token 响应不是对象");
		var access = json["access_token"]?.GetValue<string>();
		var refresh = json["refresh_token"]?.GetValue<string>();
		var expiresIn = json["expires_in"]?.GetValue<double>() ?? 0;
		if (access is not { Length: > 0 } || refresh is not { Length: > 0 } || expiresIn <= 0)
		{
			throw new CodexAuthException("Radius token 响应缺少字段");
		}

		return new OAuthCredential
		{
			Access = access,
			Refresh = refresh,
			Expires = DateTimeOffset.UtcNow.AddSeconds(expiresIn).AddMinutes(-1).ToUnixTimeSeconds(),
		};
	}

	private static async Task<OAuthCredential> AddGatewayBaseUrlAsync(
		HttpClient http,
		OAuthCredential credential,
		CancellationToken cancellationToken)
	{
		try
		{
			using var request = new HttpRequestMessage(HttpMethod.Get, $"{Gateway}/v1/config");
			request.Headers.TryAddWithoutValidation("accept", "application/json");
			request.Headers.TryAddWithoutValidation("authorization", $"Bearer {credential.Access}");
			using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
			if (!response.IsSuccessStatusCode)
			{
				return credential;
			}

			var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)) as JsonObject;
			var baseUrl = json?["baseUrl"]?.GetValue<string>();
			return baseUrl is { Length: > 0 } ? credential with { BaseUrl = baseUrl } : credential;
		}
		catch (Exception exception) when (exception is HttpRequestException or System.Text.Json.JsonException)
		{
			return credential;
		}
	}
}
