using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace PiWeft.Ai.Auth;

/// <summary>Anthropic Claude Pro/Max 浏览器 OAuth（PKCE + 回环回调）。</summary>
public static class AnthropicOAuth
{
	public const string ProviderKey = "anthropic";
	private const string ClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
	private const string AuthorizeUrl = "https://claude.ai/oauth/authorize";
	private const string TokenUrl = "https://platform.claude.com/v1/oauth/token";
	private const string Scope =
		"org:create_api_key user:profile user:inference user:sessions:claude_code user:mcp_servers user:file_upload";
	private const string CallbackHost = "127.0.0.1";
	private const int CallbackPort = 53692;
	private const string CallbackPath = "/callback";

	public sealed record LoginResult(OAuthCredential Credential);

	public static async Task<OAuthCredential> LoginAsync(
		TextReader input,
		TextWriter output,
		CancellationToken cancellationToken = default)
	{
		var verifier = RandomBase64Url(48);
		var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
		var listener = TryStartListener(out var redirectUri, out var port);
		var actualRedirect = listener is null
			? $"http://localhost:{CallbackPort}{CallbackPath}"
			: $"http://localhost:{port}{CallbackPath}";

		try
		{
			var url =
				$"{AuthorizeUrl}?code=true&client_id={Uri.EscapeDataString(ClientId)}" +
				"&response_type=code" +
				$"&redirect_uri={Uri.EscapeDataString(actualRedirect)}" +
				$"&scope={Uri.EscapeDataString(Scope)}" +
				$"&code_challenge={Uri.EscapeDataString(challenge)}" +
				"&code_challenge_method=S256" +
				$"&state={Uri.EscapeDataString(verifier)}";

			output.WriteLine("在浏览器完成 Anthropic 登录：");
			output.WriteLine(url);
			TryOpenBrowser(url);

			string? code = null;
			string? state = verifier;

			if (listener is not null && !Console.IsInputRedirected)
			{
				// 交互终端下只等回环回调，避免让 ReadLine 与 REPL 争抢下一条输入。
				var callback = await WaitForCallbackAsync(listener, cancellationToken).ConfigureAwait(false);
				code = callback.Code;
				state = callback.State ?? verifier;
			}
			else
			{
				output.Write("粘贴授权码或最终回调 URL：");
				output.Flush();
				var manual = await input.ReadLineAsync(cancellationToken).ConfigureAwait(false);
				(code, state) = ParseManualInput(manual, verifier);
			}

			if (code is not { Length: > 0 })
			{
				throw new CodexAuthException("Anthropic 登录缺少授权码");
			}

			if (!string.Equals(state, verifier, StringComparison.Ordinal))
			{
				throw new CodexAuthException("Anthropic OAuth state 不匹配");
			}

			using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
			var token = await ExchangeAsync(http, new JsonObject
			{
				["grant_type"] = "authorization_code",
				["client_id"] = ClientId,
				["code"] = code,
				["state"] = state,
				["redirect_uri"] = actualRedirect,
				["code_verifier"] = verifier,
			}, cancellationToken).ConfigureAwait(false);

			OAuthStore.Save(ProviderKey, token);
			output.WriteLine("Anthropic 登录成功，凭据已写入 auth.json。");
			return token;
		}
		finally
		{
			if (listener is not null)
			{
				try { listener.Stop(); listener.Close(); } catch (ObjectDisposedException) { }
			}
		}
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
			var refreshed = await ExchangeAsync(http, new JsonObject
			{
				["grant_type"] = "refresh_token",
				["client_id"] = ClientId,
				["refresh_token"] = credential.Refresh,
			}, cancellationToken).ConfigureAwait(false);
			OAuthStore.Save(ProviderKey, refreshed);
			return refreshed;
		}
		catch (CodexAuthException)
		{
			return null;
		}
	}

	private static async Task<OAuthCredential> ExchangeAsync(
		HttpClient http,
		JsonObject body,
		CancellationToken cancellationToken)
	{
		using var response = await http.PostAsync(
			TokenUrl,
			new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
			cancellationToken).ConfigureAwait(false);
		var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
		if (!response.IsSuccessStatusCode)
		{
			throw new CodexAuthException($"Anthropic OAuth token 请求失败（HTTP {(int)response.StatusCode}）：{text}");
		}

		var json = JsonNode.Parse(text) as JsonObject
			?? throw new CodexAuthException("Anthropic OAuth token 响应不是对象");
		var access = json["access_token"]?.GetValue<string>();
		var refresh = json["refresh_token"]?.GetValue<string>();
		var expiresIn = json["expires_in"]?.GetValue<double>() ?? 0;
		if (access is not { Length: > 0 } || refresh is not { Length: > 0 } || expiresIn <= 0)
		{
			throw new CodexAuthException("Anthropic OAuth token 响应缺少字段");
		}

		return new OAuthCredential
		{
			Access = access,
			Refresh = refresh,
			Expires = DateTimeOffset.UtcNow.AddSeconds(expiresIn).AddMinutes(-5).ToUnixTimeSeconds(),
			ClientId = ClientId,
		};
	}

	private static HttpListener? TryStartListener(out string redirectUri, out int port)
	{
		redirectUri = string.Empty;
		port = 0;
		foreach (var candidate in new[] { CallbackPort, 0 })
		{
			var listener = new HttpListener();
			try
			{
				port = candidate == 0 ? FreePort() : candidate;
				listener.Prefixes.Add($"http://{CallbackHost}:{port}/");
				listener.Start();
				redirectUri = $"http://localhost:{port}{CallbackPath}";
				return listener;
			}
			catch (Exception exception) when (exception is HttpListenerException or InvalidOperationException or UnauthorizedAccessException)
			{
				listener.Close();
			}
		}

		return null;
	}

	private static int FreePort()
	{
		using var socket = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
		socket.Start();
		var port = ((IPEndPoint)socket.LocalEndpoint).Port;
		socket.Stop();
		return port;
	}

	private static async Task<(string Code, string? State)> WaitForCallbackAsync(
		HttpListener listener,
		CancellationToken cancellationToken)
	{
		using var registration = cancellationToken.Register(static state => ((HttpListener)state!).Stop(), listener);
		while (true)
		{
			var context = await listener.GetContextAsync().ConfigureAwait(false);
			var query = context.Request.QueryString;
			var code = query["code"];
			var state = query["state"];
			var error = query["error"];
			await WriteResponseAsync(context.Response, error is null ? "Anthropic login complete. You can close this window." : $"Login failed: {error}")
				.ConfigureAwait(false);
			if (error is not null)
			{
				throw new CodexAuthException($"Anthropic 授权失败：{error}");
			}

			if (code is { Length: > 0 })
			{
				return (code, state);
			}
		}
	}

	private static async Task WriteResponseAsync(HttpListenerResponse response, string message)
	{
		var bytes = Encoding.UTF8.GetBytes($"<html><body><h1>PiWeft</h1><p>{WebUtility.HtmlEncode(message)}</p></body></html>");
		response.ContentType = "text/html; charset=utf-8";
		response.ContentLength64 = bytes.Length;
		await response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
		response.Close();
	}

	private static (string? Code, string? State) ParseManualInput(string? input, string fallbackState)
	{
		if (string.IsNullOrWhiteSpace(input))
		{
			return (null, fallbackState);
		}

		var value = input.Trim();
		if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
		{
			var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
			return (query["code"], query["state"] ?? fallbackState);
		}

		if (value.Contains('#'))
		{
			var parts = value.Split('#', 2);
			return (parts[0], parts[1]);
		}

		if (value.Contains("code=", StringComparison.Ordinal))
		{
			var query = System.Web.HttpUtility.ParseQueryString(value);
			return (query["code"], query["state"] ?? fallbackState);
		}

		return (value, fallbackState);
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
			// 浏览器打不开时用户仍可手工打开上面的 URL。
		}
	}

	private static string RandomBase64Url(int bytes) => Base64Url(RandomNumberGenerator.GetBytes(bytes));

	private static string Base64Url(byte[] bytes) =>
		Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
