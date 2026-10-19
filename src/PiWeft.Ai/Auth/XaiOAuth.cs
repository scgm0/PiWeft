using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace PiWeft.Ai.Auth;

/// <summary>xAI OAuth 设备码流程。</summary>
public static class XaiOAuth
{
	public const string ProviderKey = "xai";
	private const string ClientId = "b1a00492-073a-47ea-816f-4c329264a828";
	private const string Scope = "openid profile email offline_access grok-cli:access api:access";
	private const string DeviceCodeUrl = "https://auth.x.ai/oauth2/device/code";
	private const string TokenUrl = "https://auth.x.ai/oauth2/token";

	public static async Task<OAuthCredential> LoginAsync(
		TextWriter output,
		CancellationToken cancellationToken = default)
	{
		using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
		using var start = await http.PostAsync(
			DeviceCodeUrl,
			new FormUrlEncodedContent(new Dictionary<string, string>
			{
				["client_id"] = ClientId,
				["scope"] = Scope,
			}),
			cancellationToken).ConfigureAwait(false);
		var startBody = await start.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
		if (!start.IsSuccessStatusCode)
		{
			throw new CodexAuthException($"xAI 设备码请求失败（HTTP {(int)start.StatusCode}）：{startBody}");
		}

		var device = JsonNode.Parse(startBody) as JsonObject
			?? throw new CodexAuthException("xAI 设备码响应不是对象");
		var deviceCode = device["device_code"]?.GetValue<string>();
		var userCode = device["user_code"]?.GetValue<string>();
		var verification = device["verification_uri"]?.GetValue<string>();
		var interval = device["interval"]?.GetValue<int>() ?? 5;
		var expiresIn = device["expires_in"]?.GetValue<int>() ?? 300;
		if (deviceCode is not { Length: > 0 } || userCode is not { Length: > 0 } || verification is not { Length: > 0 })
		{
			throw new CodexAuthException("xAI 设备码响应缺少字段");
		}

		output.WriteLine($"在 {verification} 输入代码 {userCode}");
		var deadline = DateTimeOffset.UtcNow.AddSeconds(expiresIn);
		while (DateTimeOffset.UtcNow < deadline)
		{
			await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, interval)), cancellationToken).ConfigureAwait(false);
			using var poll = await http.PostAsync(
				TokenUrl,
				new FormUrlEncodedContent(new Dictionary<string, string>
				{
					["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
					["device_code"] = deviceCode,
					["client_id"] = ClientId,
				}),
				cancellationToken).ConfigureAwait(false);
			var body = await poll.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
			var json = JsonNode.Parse(body) as JsonObject;
			if (poll.IsSuccessStatusCode)
			{
				var token = ParseToken(json, null);
				OAuthStore.Save(ProviderKey, token);
				output.WriteLine("xAI 登录成功，凭据已写入 auth.json。");
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

			throw new CodexAuthException($"xAI 登录失败：{error ?? body}");
		}

		throw new CodexAuthException("xAI 设备码登录超时");
	}

	public static async Task<OAuthCredential?> RefreshAsync(
		OAuthCredential credential,
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
				TokenUrl,
				new FormUrlEncodedContent(new Dictionary<string, string>
				{
					["grant_type"] = "refresh_token",
					["refresh_token"] = credential.Refresh,
					["client_id"] = ClientId,
				}),
				cancellationToken).ConfigureAwait(false);
			var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
			if (!response.IsSuccessStatusCode)
			{
				return null;
			}

			var token = ParseToken(JsonNode.Parse(body) as JsonObject, credential);
			OAuthStore.Save(ProviderKey, token);
			return token;
		}
		catch (Exception exception) when (exception is HttpRequestException or System.Text.Json.JsonException)
		{
			return null;
		}
	}

	private static OAuthCredential ParseToken(JsonObject? json, OAuthCredential? previous)
	{
		var access = json?["access_token"]?.GetValue<string>();
		var refresh = json?["refresh_token"]?.GetValue<string>() ?? previous?.Refresh;
		var expiresIn = json?["expires_in"]?.GetValue<double>() ?? 3600;
		if (access is not { Length: > 0 } || refresh is not { Length: > 0 })
		{
			throw new CodexAuthException("xAI OAuth token 响应缺少字段");
		}

		return new OAuthCredential
		{
			Access = access,
			Refresh = refresh,
			Expires = DateTimeOffset.UtcNow.AddSeconds(expiresIn).AddMinutes(-5).ToUnixTimeSeconds(),
			ClientId = ClientId,
			Scopes = [.. Scope.Split(' ')],
		};
	}
}
