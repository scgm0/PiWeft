using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Web;

namespace PiWeft.Ai.Auth;

/// <summary>OpenRouter OAuth：PKCE 授权码换取一个永久 API key。</summary>
public static class OpenRouterOAuth
{
	public const string ProviderKey = "openrouter";
	private const string AuthorizeUrl = "https://openrouter.ai/auth";
	private const string TokenUrl = "https://openrouter.ai/api/v1/auth/keys";

	public static async Task<OAuthCredential> LoginAsync(
		TextReader input,
		TextWriter output,
		string? agentDir = null,
		CancellationToken cancellationToken = default)
	{
		var verifier = RandomBase64Url(48);
		var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
		var path = $"/oauth/callback/{Guid.NewGuid():N}";
		using var listener = new HttpListener();
		var port = FreePort();
		var redirectUri = $"http://127.0.0.1:{port}{path}";
		listener.Prefixes.Add($"http://127.0.0.1:{port}/");
		listener.Start();

		try
		{
			var url = $"{AuthorizeUrl}?callback_url={Uri.EscapeDataString(redirectUri)}" +
				$"&code_challenge={Uri.EscapeDataString(challenge)}&code_challenge_method=S256";
			output.WriteLine("在浏览器完成 OpenRouter 登录：");
			output.WriteLine(url);
			TryOpenBrowser(url);

			string? code = null;
			if (!Console.IsInputRedirected)
			{
				code = await WaitForCodeAsync(listener, path, cancellationToken).ConfigureAwait(false);
			}
			else
			{
				output.Write("粘贴授权码或最终回调 URL：");
				output.Flush();
				var manual = await input.ReadLineAsync(cancellationToken).ConfigureAwait(false);
				code = ParseCode(manual);
			}

			if (code is not { Length: > 0 })
			{
				throw new CodexAuthException("OpenRouter 登录缺少授权码");
			}

			using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
			using var response = await http.PostAsync(
				TokenUrl,
				new StringContent(new JsonObject
				{
					["code"] = code,
					["code_verifier"] = verifier,
					["code_challenge_method"] = "S256",
				}.ToJsonString(), Encoding.UTF8, "application/json"),
				cancellationToken).ConfigureAwait(false);
			var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
			if (!response.IsSuccessStatusCode)
			{
				throw new CodexAuthException($"OpenRouter key 交换失败（HTTP {(int)response.StatusCode}）：{body}");
			}

			var key = (JsonNode.Parse(body) as JsonObject)?["key"]?.GetValue<string>();
			if (key is not { Length: > 0 })
			{
				throw new CodexAuthException("OpenRouter 响应没有 key");
			}

			var credential = new OAuthCredential
			{
				Access = key,
				Refresh = string.Empty,
				Expires = long.MaxValue,
			};
			OAuthStore.Save(ProviderKey, credential, agentDir);
			output.WriteLine("OpenRouter 登录成功，API key 已写入 auth.json。");
			return credential;
		}
		finally
		{
			try { listener.Stop(); listener.Close(); } catch (ObjectDisposedException) { }
		}
	}

	private static async Task<string?> WaitForCodeAsync(HttpListener listener, string expectedPath, CancellationToken cancellationToken)
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
				return null;
			}

			var code = context.Request.QueryString["code"];
			var error = context.Request.QueryString["error"];
			var ok = error is null && code is { Length: > 0 } &&
				string.Equals(context.Request.Url?.AbsolutePath, expectedPath, StringComparison.Ordinal);
			var bytes = Encoding.UTF8.GetBytes(ok
				? "<html><body><h1>PiWeft</h1><p>OpenRouter login complete.</p></body></html>"
				: "<html><body><h1>PiWeft</h1><p>Invalid callback.</p></body></html>");
			context.Response.ContentType = "text/html; charset=utf-8";
			context.Response.ContentLength64 = bytes.Length;
			await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
			context.Response.Close();
			if (error is { Length: > 0 })
			{
				throw new CodexAuthException($"OpenRouter 授权失败：{error}");
			}

			if (ok)
			{
				return code;
			}
		}
	}

	private static string? ParseCode(string? input)
	{
		if (string.IsNullOrWhiteSpace(input))
		{
			return null;
		}

		var value = input.Trim();
		if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
		{
			return HttpUtility.ParseQueryString(uri.Query)["code"];
		}

		if (value.Contains("code=", StringComparison.Ordinal))
		{
			return HttpUtility.ParseQueryString(value)["code"];
		}

		return value;
	}

	private static int FreePort()
	{
		using var socket = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
		socket.Start();
		var port = ((IPEndPoint)socket.LocalEndpoint).Port;
		socket.Stop();
		return port;
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
