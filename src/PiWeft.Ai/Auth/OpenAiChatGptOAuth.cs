using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Web;

namespace PiWeft.Ai.Auth;

/// <summary>OpenAI ChatGPT 订阅 OAuth（动态客户端 + PKCE + 回环回调）。</summary>
public static class OpenAiChatGptOAuth
{
	public const string ProviderKey = "openai";
	private const string DynamicClientId = "dynamic_agent_client";
	private const string AuthorizeUrl = "https://auth.openai.com/api/accounts/authorize";
	private const string TokenUrl = "https://auth.openai.com/api/accounts/oauth/token";
	private const string Resource = "https://api.openai.com/v1";
	private const string CallbackHost = "127.0.0.1";
	private const int CallbackPort = 1455;
	private const string CallbackPath = "/auth/callback";
	private const string DirectTokenScope = "chatgpt.tokens.use.direct";
	private const string Scope = "openid profile email offline_access resource.invoke " + DirectTokenScope;

	public static async Task<OAuthCredential> LoginAsync(
		TextReader input,
		TextWriter output,
		string? agentDir = null,
		CancellationToken cancellationToken = default)
	{
		var verifier = RandomBase64Url(48);
		var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
		var state = RandomBase64Url(32);
		var redirectUri = $"http://{CallbackHost}:{CallbackPort}{CallbackPath}";
		var deviceId = OAuthDeviceId.GetOrCreate(agentDir);

		using var listener = new HttpListener();
		listener.Prefixes.Add($"http://{CallbackHost}:{CallbackPort}/");
		try
		{
			listener.Start();
		}
		catch (HttpListenerException exception)
		{
			throw new CodexAuthException(
				$"端口 {CallbackPort} 被占用，可能还有未完成的 ChatGPT/Codex 登录：{exception.Message}");
		}

		try
		{
			var url = new UriBuilder(AuthorizeUrl)
			{
				Query = string.Join('&',
				[
					$"client_id={Uri.EscapeDataString(DynamicClientId)}",
					"agent_name_hint=PiWeft",
					$"ext_agent_host_id={Uri.EscapeDataString($"urn:uuid:{deviceId}")}",
					"response_type=code",
					$"redirect_uri={Uri.EscapeDataString(redirectUri)}",
					$"resource={Uri.EscapeDataString(Resource)}",
					$"scope={Uri.EscapeDataString(Scope)}",
					$"state={Uri.EscapeDataString(state)}",
					$"code_challenge={Uri.EscapeDataString(challenge)}",
					"code_challenge_method=S256",
					$"nonce={Uri.EscapeDataString(RandomBase64Url(32))}",
				]),
			}.Uri.ToString();

			output.WriteLine("在浏览器完成 ChatGPT 登录：");
			output.WriteLine(url);
			TryOpenBrowser(url);

			string? code = null;
			string? issuedClientId = null;
			if (!Console.IsInputRedirected)
			{
				var callback = await WaitForCallbackAsync(listener, state, cancellationToken).ConfigureAwait(false);
				code = callback.Code;
				issuedClientId = callback.ClientId;
			}
			else
			{
				output.Write("粘贴浏览器最终回调 URL：");
				output.Flush();
				var manual = await input.ReadLineAsync(cancellationToken).ConfigureAwait(false);
				(code, issuedClientId) = ParseManual(manual, state, redirectUri);
			}

			if (code is not { Length: > 0 } || issuedClientId is not { Length: > 0 })
			{
				throw new CodexAuthException("ChatGPT 登录缺少授权码或 client_id");
			}

			using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
			var credential = await RequestTokenAsync(http, new Dictionary<string, string>
			{
				["grant_type"] = "authorization_code",
				["client_id"] = issuedClientId,
				["code"] = code,
				["code_verifier"] = verifier,
				["redirect_uri"] = redirectUri,
				["resource"] = Resource,
			}, issuedClientId, cancellationToken).ConfigureAwait(false);

			OAuthStore.Save(ProviderKey, credential, agentDir);
			output.WriteLine("ChatGPT 登录成功，凭据已写入 auth.json。");
			return credential;
		}
		finally
		{
			try { listener.Stop(); listener.Close(); } catch (ObjectDisposedException) { }
		}
	}

	public static async Task<OAuthCredential?> RefreshAsync(
		OAuthCredential credential,
		string? agentDir = null,
		CancellationToken cancellationToken = default)
	{
		if (credential.Refresh.Length == 0 || credential.ClientId is not { Length: > 0 } clientId)
		{
			return null;
		}

		using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
		try
		{
			var refreshed = await RequestTokenAsync(http, new Dictionary<string, string>
			{
				["grant_type"] = "refresh_token",
				["client_id"] = clientId,
				["refresh_token"] = credential.Refresh,
				["resource"] = Resource,
			}, clientId, cancellationToken).ConfigureAwait(false);
			OAuthStore.Save(ProviderKey, refreshed, agentDir);
			return refreshed;
		}
		catch (CodexAuthException)
		{
			return null;
		}
	}

	private static async Task<OAuthCredential> RequestTokenAsync(
		HttpClient http,
		Dictionary<string, string> form,
		string clientId,
		CancellationToken cancellationToken)
	{
		using var response = await http.PostAsync(TokenUrl, new FormUrlEncodedContent(form), cancellationToken).ConfigureAwait(false);
		var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
		if (!response.IsSuccessStatusCode)
		{
			throw new CodexAuthException($"ChatGPT OAuth token 请求失败（HTTP {(int)response.StatusCode}）：{body}");
		}

		var json = JsonNode.Parse(body) as JsonObject
			?? throw new CodexAuthException("ChatGPT OAuth token 响应不是对象");
		var access = json["access_token"]?.GetValue<string>();
		var refresh = json["refresh_token"]?.GetValue<string>();
		var expiresIn = json["expires_in"]?.GetValue<double>() ?? 0;
		if (access is not { Length: > 0 } || refresh is not { Length: > 0 } || expiresIn <= 0)
		{
			throw new CodexAuthException("ChatGPT OAuth token 响应缺少字段");
		}

		var scopes = json["scope"]?.GetValue<string>() is { Length: > 0 } scope
			? scope.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			: [];
		return new OAuthCredential
		{
			Access = access,
			Refresh = refresh,
			Expires = DateTimeOffset.UtcNow.AddSeconds(expiresIn).AddMinutes(-3).ToUnixTimeSeconds(),
			ClientId = clientId,
			Scopes = scopes,
		};
	}

	private static async Task<(string Code, string ClientId)> WaitForCallbackAsync(
		HttpListener listener,
		string expectedState,
		CancellationToken cancellationToken)
	{
		using var registration = cancellationToken.Register(static state => ((HttpListener)state!).Stop(), listener);
		while (true)
		{
			HttpListenerContext context;
			try
			{
				context = await listener.GetContextAsync().ConfigureAwait(false);
			}
			catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException or InvalidOperationException)
			{
				throw new CodexAuthException("ChatGPT OAuth 回调被取消");
			}

			var query = context.Request.QueryString;
			var error = query["error"];
			var code = query["code"];
			var state = query["state"];
			var clientId = query["client_id"]?.Trim();
			var ok = error is null && code is { Length: > 0 } && string.Equals(state, expectedState, StringComparison.Ordinal) &&
				clientId is { Length: > 0 };
			await WriteResponseAsync(context.Response, ok, ok ? "ChatGPT login complete. You can close this window." : error ?? "Invalid callback.")
				.ConfigureAwait(false);
			if (error is { Length: > 0 })
			{
				throw new CodexAuthException($"ChatGPT 授权失败：{error}");
			}

			if (ok)
			{
				return (code!, clientId!);
			}
		}
	}

	private static (string? Code, string? ClientId) ParseManual(string? input, string state, string redirectUri)
	{
		if (string.IsNullOrWhiteSpace(input))
		{
			return (null, null);
		}

		if (!Uri.TryCreate(input.Trim(), UriKind.Absolute, out var uri))
		{
			return (null, null);
		}

		var expected = new Uri(redirectUri);
		if (uri.Scheme != expected.Scheme || uri.Host != expected.Host || uri.Port != expected.Port || uri.AbsolutePath != expected.AbsolutePath)
		{
			throw new CodexAuthException($"回调 URL 必须以 {redirectUri} 开头");
		}

		var query = HttpUtility.ParseQueryString(uri.Query);
		if (query["state"] is { Length: > 0 } actual && !string.Equals(actual, state, StringComparison.Ordinal))
		{
			throw new CodexAuthException("ChatGPT OAuth state 不匹配");
		}

		return (query["code"], query["client_id"]);
	}

	private static async Task WriteResponseAsync(HttpListenerResponse response, bool ok, string message)
	{
		var bytes = Encoding.UTF8.GetBytes(
			$"<html><body><h1>PiWeft</h1><p>{WebUtility.HtmlEncode(message)}</p></body></html>");
		response.ContentType = "text/html; charset=utf-8";
		response.ContentLength64 = bytes.Length;
		await response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
		response.Close();
	}

	private static void TryOpenBrowser(string url)
	{
		try
		{
			if (OperatingSystem.IsWindows())
			{
				Process.Start(new ProcessStartInfo("cmd.exe", $"/c start \"\" \"{url}\"") { UseShellExecute = false });
			}
			else if (OperatingSystem.IsMacOS())
			{
				Process.Start("open", [url]);
			}
			else
			{
				Process.Start("xdg-open", [url]);
			}
		}
		catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
		{
		}
	}

	private static string RandomBase64Url(int bytes) => Base64Url(RandomNumberGenerator.GetBytes(bytes));

	private static string Base64Url(byte[] bytes) =>
		Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
