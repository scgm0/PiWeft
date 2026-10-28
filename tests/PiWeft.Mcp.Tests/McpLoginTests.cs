using System.Net;
using System.Text.Json.Nodes;
using PiWeft.Mcp;
using Xunit;

namespace PiWeft.Mcp.Tests;

/// <summary>
/// <see cref="McpLogin"/> 是 CLI（<c>pi mcp login</c>）与桌面配置页共用的授权入口。
/// 这里钉住三件事：分支选择（客户端凭据 vs 浏览器授权码）、令牌落盘位置、
/// 以及状态查询（未授权 / 已授权 / 已过期）——界面上的三个按钮文案全靠它。
/// </summary>
public sealed class McpLoginTests : IDisposable
{
	private const string Endpoint = "https://mcp.example/mcp";
	private readonly string _agentDir = Path.Combine(Path.GetTempPath(), "piweft-mcp-login-" + Guid.NewGuid().ToString("N")[..8]);

	public McpLoginTests() => Directory.CreateDirectory(_agentDir);

	public void Dispose()
	{
		try
		{
			Directory.Delete(_agentDir, recursive: true);
		}
		catch (IOException)
		{
			// 临时目录清理失败不影响断言结论。
		}
	}

	private static HttpResponseMessage Json(JsonObject node) =>
		new(HttpStatusCode.OK)
		{
			Content = new StringContent(node.ToJsonString(), System.Text.Encoding.UTF8, "application/json"),
		};

	/// <summary>标准的发现链：protected-resource → authorization-server → token。</summary>
	private static ScriptedHttpHandler Discovery(Func<HttpResponseMessage>? token = null) => new()
	{
		Responder = (request, _) => request.RequestUri!.AbsolutePath switch
		{
			"/.well-known/oauth-protected-resource/mcp" => Json(new JsonObject
			{
				["resource"] = Endpoint,
				["authorization_servers"] = new JsonArray("https://auth.example"),
			}),
			"/.well-known/oauth-authorization-server" => Json(new JsonObject
			{
				["issuer"] = "https://auth.example",
				["authorization_endpoint"] = "https://auth.example/authorize",
				["token_endpoint"] = "https://auth.example/token",
				["registration_endpoint"] = "https://auth.example/register",
			}),
			"/token" => token is null
				? new HttpResponseMessage(HttpStatusCode.NotFound)
				: token(),
			"/register" => Json(new JsonObject { ["client_id"] = "dynamic-client" }),
			_ => new HttpResponseMessage(HttpStatusCode.NotFound),
		},
	};

	[Fact]
	public async Task ClientCredentialsConfigSkipsTheBrowser()
	{
		var handler = Discovery(() => Json(new JsonObject { ["access_token"] = "cc-1", ["expires_in"] = 600 }));
		var config = new McpServerConfig
		{
			Name = "gateway",
			Url = Endpoint,
			Auth = "oauth",
			ClientId = "client-1",
			ClientSecret = "secret-1",
		};

		var opened = 0;
		var outcome = await McpLogin.RunAsync(new HttpClient(handler), config, _agentDir, _ => opened++);

		Assert.True(outcome.Ok, outcome.Message);
		Assert.False(outcome.UsedBrowser);
		Assert.Equal(0, opened); // 配了客户端凭据就不该再要浏览器
		Assert.Equal("cc-1", McpOAuthStore.Load(Endpoint, _agentDir)!.Access);
	}

	/// <summary>
	/// 浏览器授权码流要**等回调**才返回：这段等待必须可取消，否则用户在界面上关掉弹窗
	/// 之后，后台任务会一直挂着（回环端口也一直被占）。
	/// </summary>
	[Fact]
	public async Task BrowserFlowHandsTheAuthorizeUrlToTheHostAndHonoursCancellation()
	{
		var handler = Discovery(() => Json(new JsonObject { ["access_token"] = "code-flow" }));
		var config = new McpServerConfig { Name = "remote", Url = Endpoint, Auth = "oauth" };
		using var cts = new CancellationTokenSource();

		string? authorizeUrl = null;
		// openBrowser:false：断言的是「URL 交给宿主」这件事本身，不需要真拉一个浏览器进程；
		// 桌面/CLI 的真实浏览器路径由各自的宿主实现负责。
		var run = McpLogin.RunAsync(new HttpClient(handler), config, _agentDir, url => authorizeUrl = url, cts.Token, openBrowser: false);

		var deadline = DateTime.UtcNow.AddSeconds(20);
		while (authorizeUrl is null && DateTime.UtcNow < deadline)
		{
			await Task.Delay(20);
		}

		Assert.NotNull(authorizeUrl);
		Assert.StartsWith("https://auth.example/authorize", authorizeUrl, StringComparison.Ordinal);
		Assert.Contains("code_challenge=", authorizeUrl, StringComparison.Ordinal);
		Assert.False(run.IsCompleted, "没人完成回调时流程就该等着，不能提前宣布成功");

		cts.Cancel();
		var outcome = await run;
		Assert.False(outcome.Ok);
		Assert.False(McpOAuthStore.Load(Endpoint, _agentDir) is not null, "取消后不该留下半截凭据");
	}

	[Fact]
	public async Task UnreachableServerReportsFailureInsteadOfThrowing()
	{
		var handler = new ScriptedHttpHandler
		{
			Responder = static (_, _) => throw new HttpRequestException("no route to host"),
		};

		var outcome = await McpLogin.RunAsync(
			new HttpClient(handler),
			new McpServerConfig { Name = "down", Url = Endpoint, Auth = "oauth" },
			_agentDir);

		Assert.False(outcome.Ok);
		Assert.True(outcome.Message.Length > 0, "失败必须有可读原因，桌面把它直接显示在 toast 里");
	}

	[Fact]
	public void StdioServersHaveNoAuthorization()
	{
		// stdio 没有 OAuth；界面因此不给它显示授权入口，点了只会让人以为能修好连接问题。
		var config = new McpServerConfig { Name = "local", Command = "mcp-server" };
		Assert.False(McpLogin.SignedIn(config, _agentDir));

		var outcome = McpLogin.RunAsync(new HttpClient(), config, _agentDir).GetAwaiter().GetResult();
		Assert.False(outcome.Ok);
		Assert.Contains("HTTP", outcome.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void StatusReflectsStoredTokenAndItsExpiry()
	{
		var config = new McpServerConfig { Name = "remote", Url = Endpoint };

		Assert.False(McpLogin.SignedIn(config, _agentDir));
		Assert.False(McpLogin.IsExpired(config, _agentDir));

		McpOAuthStore.Save(Endpoint, new McpOAuthToken
		{
			Access = "live",
			ExpiresAt = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(),
		}, _agentDir);
		Assert.True(McpLogin.SignedIn(config, _agentDir));
		Assert.False(McpLogin.IsExpired(config, _agentDir));

		McpOAuthStore.Save(Endpoint, new McpOAuthToken
		{
			Access = "stale",
			ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds(),
		}, _agentDir);
		Assert.True(McpLogin.SignedIn(config, _agentDir), "过期不等于没登录过：界面要能区分「未授权」与「要重新授权」");
		Assert.True(McpLogin.IsExpired(config, _agentDir));

		Assert.True(McpLogin.Logout(config, _agentDir));
		Assert.False(McpLogin.SignedIn(config, _agentDir));
		Assert.False(McpLogin.Logout(config, _agentDir), "重复登出应当报告「没有东西可清除」而不是假装成功");
	}
}
