using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using PiWeft.Ai.Auth;
using Xunit;

namespace PiWeft.Cli.Tests;

/// <summary>
/// Drives <c>pi login</c> / <c>pi auth</c> through the real executable against a stub OAuth server, so the device-code
/// flow, the credential file, and the printed output are all exercised the way a user hits them.
/// </summary>
public sealed class AuthCommandProcessTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "pi-login-" + Guid.NewGuid().ToString("N")[..8]);
	private readonly HttpListener _listener = new();
	private readonly string _baseUrl;
	private readonly CancellationTokenSource _server = new();

	public AuthCommandProcessTests()
	{
		Directory.CreateDirectory(_root);

		var port = FreePort();
		_baseUrl = $"http://127.0.0.1:{port}";
		_listener.Prefixes.Add($"http://127.0.0.1:{port}/");
		_listener.Start();
		_ = Task.Run(ServeAsync);
	}

	public void Dispose()
	{
		_server.Cancel();
		_listener.Close();
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	private static int FreePort()
	{
		var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
		probe.Start();
		var port = ((IPEndPoint)probe.LocalEndpoint).Port;
		probe.Stop();
		return port;
	}

	private async Task ServeAsync()
	{
		while (!_server.IsCancellationRequested)
		{
			HttpListenerContext context;
			try
			{
				context = await _listener.GetContextAsync();
			}
			catch (Exception)
			{
				return;
			}

			var path = context.Request.Url!.AbsolutePath;
			var body = path switch
			{
				"/api/accounts/deviceauth/usercode" =>
					"""{"device_auth_id":"dev-1","user_code":"WXYZ-9876","interval":0}""",
				"/api/accounts/deviceauth/token" =>
					"""{"authorization_code":"code-1","code_verifier":"verifier-1"}""",
				"/oauth/token" => Token(),
				_ => "{}",
			};

			var bytes = Encoding.UTF8.GetBytes(body);
			context.Response.StatusCode = path == "/missing" ? 404 : 200;
			context.Response.ContentType = "application/json";
			context.Response.ContentLength64 = bytes.Length;
			await context.Response.OutputStream.WriteAsync(bytes);
			context.Response.Close();
		}
	}

	private static string Token() =>
		$$"""
		{"access_token":"{{Jwt()}}","refresh_token":"refresh-1","expires_in":3600}
		""";

	private static string Jwt()
	{
		static string Encode(string value) =>
			Convert.ToBase64String(Encoding.UTF8.GetBytes(value)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

		return $"{Encode("""{"alg":"none"}""")}.{Encode("""{"https://api.openai.com/auth":{"chatgpt_account_id":"acct_cli"}}""")}.sig";
	}

	private async Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(params string[] args)
	{
		var info = new ProcessStartInfo("dotnet")
		{
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
		};

		info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "pi.dll"));
		foreach (var argument in args)
		{
			info.ArgumentList.Add(argument);
		}

		info.Environment[AgentPaths.EnvAgentDir] = _root;
		info.Environment["PI_CODEX_AUTH_BASE_URL"] = _baseUrl;

		using var process = Process.Start(info)!;
		var stdout = process.StandardOutput.ReadToEndAsync();
		var stderr = process.StandardError.ReadToEndAsync();
		await process.WaitForExitAsync();
		return (process.ExitCode, await stdout, await stderr);
	}

	[Fact]
	public async Task LoginRunsTheDeviceFlowAndWritesCredentials()
	{
		var (exitCode, stdout, stderr) = await RunAsync("login", "openai-codex");

		Assert.True(exitCode == 0, stderr);
		Assert.Contains("Enter code WXYZ-9876", stdout);
		Assert.Contains($"{_baseUrl}/codex/device", stdout);
		Assert.Contains("account acct_cli", stdout);

		// The credential file uses the same shape pi uses, and the token is refreshable.
		var path = Path.Combine(_root, "auth.json");
		Assert.True(File.Exists(path), stdout);
		var entry = JsonNode.Parse(File.ReadAllText(path))!["openai-codex"]!;
		Assert.Equal("oauth", entry["type"]!.GetValue<string>());
		Assert.Equal("refresh-1", entry["refresh"]!.GetValue<string>());
		Assert.Equal("acct_cli", CodexAuth.GetAccountId(entry["access"]!.GetValue<string>()));
	}

	[Fact]
	public async Task AuthCheckReportsSignedInAndPrintBearerTokenEmitsTheToken()
	{
		Assert.Equal(0, (await RunAsync("login", "openai-codex")).ExitCode);

		// 协议与 pi 一致：check 输出状态串，退出码 0/1/2；--json 给出结构化结果。
		var check = await RunAsync("auth", "check", "--provider", "openai-codex");
		Assert.Equal(0, check.ExitCode);
		Assert.Equal("ready", check.StdOut.Trim());

		var json = await RunAsync("auth", "check", "--provider", "openai-codex", "--json");
		Assert.Equal(0, json.ExitCode);
		var payload = JsonNode.Parse(json.StdOut)!;
		Assert.Equal("ready", payload["status"]!.GetValue<string>());
		Assert.Equal("openai-codex", payload["provider"]!.GetValue<string>());
		Assert.Equal("oauth", payload["authType"]!.GetValue<string>());

		var token = await RunAsync("auth", "print-bearer-token", "--provider", "openai-codex");
		Assert.Equal(0, token.ExitCode);
		Assert.Equal("acct_cli", CodexAuth.GetAccountId(token.StdOut.Trim()));
	}

	[Fact]
	public async Task AuthCheckWithoutCredentialsFails()
	{
		var (exitCode, stdout, _) = await RunAsync("auth", "check", "--provider", "openai-codex");

		Assert.Equal(1, exitCode);
		Assert.Equal("not_ready", stdout.Trim());
	}

	[Fact]
	public async Task LogoutRemovesOnlyTheCodexEntry()
	{
		Assert.Equal(0, (await RunAsync("login", "openai-codex")).ExitCode);

		var path = Path.Combine(_root, "auth.json");
		var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
		root["deepseek"] = new JsonObject { ["type"] = "api_key", ["key"] = "sk-keep", ["note"] = "保留 \"引号\" <tag>" };
		File.WriteAllText(path, root.ToJsonString());

		var logout = await RunAsync("logout", "openai-codex");
		Assert.Equal(0, logout.ExitCode);

		var raw = File.ReadAllText(path);
		var after = JsonNode.Parse(raw)!.AsObject();
		Assert.Null(after["openai-codex"]);
		Assert.Equal("sk-keep", after["deepseek"]!["key"]!.GetValue<string>());
		Assert.Equal("保留 \"引号\" <tag>", after["deepseek"]!["note"]!.GetValue<string>());
		Assert.Contains("保留", raw);
		Assert.DoesNotContain("\\u", raw);

		// codex 已经登出，但 deepseek 的 api_key 仍在 —— 两个 provider 的凭据互不影响。
		Assert.Equal(1, (await RunAsync("auth", "check", "--provider", "openai-codex")).ExitCode);
		Assert.Equal(0, (await RunAsync("auth", "check", "--provider", "deepseek")).ExitCode);
	}

	[Fact]
	public async Task AuthCheckRequiresAProviderOrModel()
	{
		var (exitCode, _, stderr) = await RunAsync("auth", "check");

		Assert.Equal(2, exitCode);
		Assert.Contains("at least one of --provider or --model", stderr);
	}

	[Fact]
	public async Task ApiKeyProviderIsDetectedFromAuthJsonAndCanBePrinted()
	{
		// 这正是用户遇到 401 的场景：key 存在 auth.json 里而不是环境变量。
		var path = Path.Combine(_root, "auth.json");
		File.WriteAllText(
			path,
			"""{"deepseek":{"type":"api_key","key":"sk-stored-key"}}""");

		var check = await RunAsync("auth", "check", "--provider", "deepseek");
		Assert.Equal(0, check.ExitCode);
		Assert.Equal("ready", check.StdOut.Trim());

		var json = await RunAsync("auth", "check", "--provider", "deepseek", "--json");
		Assert.Equal("api_key", JsonNode.Parse(json.StdOut)!["authType"]!.GetValue<string>());

		var key = await RunAsync("auth", "print-api-key", "--provider", "deepseek");
		Assert.Equal(0, key.ExitCode);
		Assert.Equal("sk-stored-key", key.StdOut.Trim());

		// --credentials 把凭据一起交出来（非 JSON 模式下就是凭据本身）。
		var credentials = await RunAsync("auth", "check", "--provider", "deepseek", "--credentials");
		Assert.Equal("sk-stored-key", credentials.StdOut.Trim());
	}

	[Fact]
	public async Task ModelArgumentResolvesTheProvider()
	{
		var path = Path.Combine(_root, "auth.json");
		File.WriteAllText(path, """{"deepseek":{"type":"api_key","key":"sk-by-model"}}""");

		var (exitCode, stdout, _) = await RunAsync("auth", "check", "--model", "deepseek/deepseek-flash");

		Assert.True(exitCode == 0, stdout);
		Assert.Equal("ready", stdout.Trim());
	}

	[Fact]
	public async Task OauthProviderRejectsApiKeyPrinting()
	{
		Assert.Equal(0, (await RunAsync("login", "openai-codex")).ExitCode);

		var (exitCode, _, stderr) = await RunAsync("auth", "print-api-key", "--provider", "openai-codex");

		Assert.Equal(1, exitCode);
		Assert.Contains("configured with OAuth, not an API key", stderr);
	}

	[Fact]
	public async Task MinExpiryIsOnlyAcceptedByBearerToken()
	{
		var (exitCode, _, stderr) = await RunAsync("auth", "check", "--provider", "openai-codex", "--min-expiry", "30m");

		Assert.Equal(2, exitCode);
		Assert.Contains("only supported by print-bearer-token", stderr);

		var bad = await RunAsync(
			"auth", "print-bearer-token", "--provider", "openai-codex", "--min-expiry", "nonsense");

		Assert.Equal(2, bad.ExitCode);
		Assert.Contains("duration such as 30m or 1h", bad.StdErr);
	}

	[Fact]
	public async Task UnknownProviderIsRejectedWithoutTouchingTheStore()
	{
		var (exitCode, _, stderr) = await RunAsync("login", "anthropic");

		Assert.Equal(2, exitCode);
		Assert.Contains("does not use OAuth login", stderr);
		Assert.False(File.Exists(Path.Combine(_root, "auth.json")));
	}
}
