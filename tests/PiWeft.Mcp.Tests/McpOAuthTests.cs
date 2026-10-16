using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using PiWeft.Mcp;
using Xunit;

namespace PiWeft.Mcp.Tests;

/// <summary>按 URL 脚本化响应的 HTTP 桩，用于 OAuth 的元数据与令牌请求。</summary>
internal sealed class ScriptedHttpHandler : HttpMessageHandler
{
	public List<(string Method, string Url, string? Body)> Calls { get; } = [];

	public Func<HttpRequestMessage, string?, HttpResponseMessage> Responder { get; set; } =
		(_, _) => new HttpResponseMessage(HttpStatusCode.NotFound);

	protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
	{
		var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
		Calls.Add((request.Method.Method, request.RequestUri!.ToString(), body));
		return Responder(request, body);
	}
}

public class McpOAuthTests : IDisposable
{
	private const string Endpoint = "https://mcp.example/mcp";

	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-mcpoauth-" + Guid.NewGuid().ToString("N")[..8]);

	private readonly string _agentDir;

	public McpOAuthTests()
	{
		_agentDir = Path.Combine(_root, "agent");
		Directory.CreateDirectory(_agentDir);
	}

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	private static HttpResponseMessage Json(JsonObject payload) => new(HttpStatusCode.OK)
	{
		Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
	};

	[Fact]
	public void ResourceMetadataUrl_PrefersTheChallengeHeader()
	{
		var url = McpOAuth.ResourceMetadataUrl(
			"Bearer resource_metadata=\"https://auth.example/.well-known/oauth-protected-resource\"",
			Endpoint);

		Assert.Equal("https://auth.example/.well-known/oauth-protected-resource", url);
	}

	[Fact]
	public void ResourceMetadataUrl_FallsBackToTheWellKnownPath()
	{
		Assert.Equal("https://mcp.example/.well-known/oauth-protected-resource/mcp", McpOAuth.ResourceMetadataUrl(null, Endpoint));
	}

	[Fact]
	public async Task DiscoverAsync_ReadsResourceThenServerMetadata()
	{
		var handler = new ScriptedHttpHandler
		{
			Responder = (request, _) => request.RequestUri!.AbsolutePath switch
			{
				"/.well-known/oauth-protected-resource/mcp" => Json(new JsonObject
				{
					["resource"] = Endpoint,
					["authorization_servers"] = new JsonArray("https://auth.example"),
					["scopes_supported"] = new JsonArray("mcp.read"),
				}),
				"/.well-known/oauth-authorization-server" => Json(new JsonObject
				{
					["issuer"] = "https://auth.example",
					["authorization_endpoint"] = "https://auth.example/authorize",
					["token_endpoint"] = "https://auth.example/token",
					["registration_endpoint"] = "https://auth.example/register",
					["scopes_supported"] = new JsonArray("mcp.read", "mcp.write"),
				}),
				_ => new HttpResponseMessage(HttpStatusCode.NotFound),
			},
		};

		var metadata = await McpOAuth.DiscoverAsync(new HttpClient(handler), Endpoint, null, CancellationToken.None);

		Assert.NotNull(metadata);
		Assert.Equal("https://auth.example/authorize", metadata!.AuthorizationEndpoint);
		Assert.Equal("https://auth.example/token", metadata.TokenEndpoint);
		Assert.Equal("https://auth.example/register", metadata.RegistrationEndpoint);
		Assert.Equal(new[] { "mcp.read", "mcp.write" }, metadata.ScopesSupported);
	}

	[Fact]
	public async Task RefreshAsync_ExchangesTheRefreshTokenAndKeepsItWhenAbsent()
	{
		var handler = new ScriptedHttpHandler
		{
			Responder = (_, _) => Json(new JsonObject
			{
				["access_token"] = "new-access",
				["expires_in"] = 3600,
				["token_type"] = "Bearer",
			}),
		};

		var token = new McpOAuthToken
		{
			Access = "old-access",
			Refresh = "refresh-1",
			TokenEndpoint = "https://auth.example/token",
			ClientId = "client-1",
			Resource = Endpoint,
		};

		var result = await McpOAuth.RefreshAsync(new HttpClient(handler), token, CancellationToken.None);

		Assert.True(result.Ok, result.Error);
		Assert.Equal("new-access", result.Token!.Access);
		// 服务器没有下发新的 refresh token 时沿用旧的，否则下次就刷不动了。
		Assert.Equal("refresh-1", result.Token.Refresh);
		Assert.NotNull(result.Token.ExpiresAt);

		var call = Assert.Single(handler.Calls);
		Assert.Equal("POST", call.Method);
		Assert.Contains("grant_type=refresh_token", call.Body);
		Assert.Contains("refresh_token=refresh-1", call.Body);
		Assert.Contains("client_id=client-1", call.Body);
	}

	[Fact]
	public async Task ResolveAsync_ReusesAValidStoredToken()
	{
		McpOAuthStore.Save(Endpoint, new McpOAuthToken
		{
			Access = "cached",
			Refresh = "refresh-1",
			TokenEndpoint = "https://auth.example/token",
			ExpiresAt = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(),
		}, _agentDir);

		var handler = new ScriptedHttpHandler();
		var token = await McpOAuth.ResolveAsync(new HttpClient(handler), Endpoint, _agentDir, CancellationToken.None);

		Assert.Equal("cached", token!.Access);
		Assert.Empty(handler.Calls);
	}

	[Fact]
	public async Task ResolveAsync_RefreshesAnExpiringTokenAndPersistsIt()
	{
		McpOAuthStore.Save(Endpoint, new McpOAuthToken
		{
			Access = "stale",
			Refresh = "refresh-1",
			TokenEndpoint = "https://auth.example/token",
			ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-5).ToUnixTimeSeconds(),
		}, _agentDir);

		var handler = new ScriptedHttpHandler
		{
			Responder = (_, _) => Json(new JsonObject { ["access_token"] = "fresh", ["expires_in"] = 60 }),
		};

		var token = await McpOAuth.ResolveAsync(new HttpClient(handler), Endpoint, _agentDir, CancellationToken.None);

		Assert.Equal("fresh", token!.Access);
		Assert.Equal("fresh", McpOAuthStore.Load(Endpoint, _agentDir)!.Access);
	}

	[Fact]
	public void BuildAuthorizationUrl_CarriesPkceStateAndResource()
	{
		var metadata = new McpAuthorizationServerMetadata
		{
			AuthorizationEndpoint = "https://auth.example/authorize",
			TokenEndpoint = "https://auth.example/token",
			ScopesSupported = ["mcp.read"],
		};

		var config = new McpServerConfig { Name = "remote", Url = Endpoint };
		var url = McpOAuth.BuildAuthorizationUrl(
			metadata, config, "client-1", "http://127.0.0.1:1234/callback", "challenge-value", "state-value", Endpoint);

		Assert.StartsWith("https://auth.example/authorize?", url, StringComparison.Ordinal);
		Assert.Contains("response_type=code", url, StringComparison.Ordinal);
		Assert.Contains("code_challenge=challenge-value", url, StringComparison.Ordinal);
		Assert.Contains("code_challenge_method=S256", url, StringComparison.Ordinal);
		Assert.Contains("state=state-value", url, StringComparison.Ordinal);
		Assert.Contains("resource=https%3A%2F%2Fmcp.example%2Fmcp", url, StringComparison.Ordinal);
		Assert.Contains("scope=mcp.read", url, StringComparison.Ordinal);
	}

	[Fact]
	public void Store_NormalizesKeysAndRemovesEntries()
	{
		McpOAuthStore.Save("https://MCP.Example:443/mcp", new McpOAuthToken { Access = "a" }, _agentDir);

		Assert.Equal("a", McpOAuthStore.Load("https://mcp.example/mcp", _agentDir)!.Access);
		Assert.True(McpOAuthStore.Remove("https://mcp.example/mcp", _agentDir));
		Assert.Null(McpOAuthStore.Load(Endpoint, _agentDir));
	}

	[Fact]
	public void Store_KeepsOtherServersWhenSaving()
	{
		McpOAuthStore.Save("https://one.example/mcp", new McpOAuthToken { Access = "one" }, _agentDir);
		McpOAuthStore.Save("https://two.example/mcp", new McpOAuthToken { Access = "two" }, _agentDir);

		Assert.Equal("one", McpOAuthStore.Load("https://one.example/mcp", _agentDir)!.Access);
		Assert.Equal("two", McpOAuthStore.Load("https://two.example/mcp", _agentDir)!.Access);
		Assert.Equal(2, McpOAuthStore.Keys(_agentDir).Count);
	}

	[Fact]
	public void ClientCredentials_RequiresBothIdAndSecret()
	{
		Assert.False(McpOAuth.UsesClientCredentials(new McpServerConfig { Name = "x", Url = Endpoint, ClientId = "a" }));
		Assert.False(McpOAuth.UsesClientCredentials(new McpServerConfig { Name = "x", Url = Endpoint, ClientSecret = "b" }));
		Assert.True(McpOAuth.UsesClientCredentials(new McpServerConfig { Name = "x", Url = Endpoint, ClientId = "a", ClientSecret = "b" }));
	}

	[Fact]
	public async Task ClientCredentialsAsync_ExchangesAndStoresTheToken()
	{
		var handler = new ScriptedHttpHandler
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
				}),
				"/token" => Json(new JsonObject { ["access_token"] = "cc-access", ["token_type"] = "Bearer", ["expires_in"] = 600 }),
				_ => new HttpResponseMessage(HttpStatusCode.NotFound),
			},
		};

		var config = new McpServerConfig
		{
			Name = "gateway",
			Url = Endpoint,
			Auth = "oauth",
			ClientId = "client-1",
			ClientSecret = "secret-1",
			Scopes = ["mcp.read"],
		};

		var result = await McpOAuth.ClientCredentialsAsync(new HttpClient(handler), Endpoint, config, _agentDir, CancellationToken.None);

		Assert.True(result.Ok, result.Error);
		Assert.Equal("cc-access", result.Token!.Access);
		Assert.Equal("cc-access", McpOAuthStore.Load(Endpoint, _agentDir)!.Access);

		var tokenCall = Assert.Single(handler.Calls, call => call.Url.EndsWith("/token", StringComparison.Ordinal));
		Assert.Contains("grant_type=client_credentials", tokenCall.Body);
		Assert.Contains("client_id=client-1", tokenCall.Body);
		Assert.Contains("client_secret=secret-1", tokenCall.Body);
		Assert.Contains("scope=mcp.read", tokenCall.Body);
	}

	[Fact]
	public async Task TokenProvider_FallsBackToTheClientCredentialsGrant()
	{
		var handler = new ScriptedHttpHandler
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
				}),
				"/token" => Json(new JsonObject { ["access_token"] = "cc-access", ["expires_in"] = 600 }),
				_ => new HttpResponseMessage(HttpStatusCode.NotFound),
			},
		};

		var config = new McpServerConfig { Name = "gateway", Url = Endpoint, Auth = "oauth", ClientId = "c", ClientSecret = "s" };
		var provider = new McpOAuthTokenProvider(Endpoint, _agentDir, new HttpClient(handler), config);

		var token = await provider.GetTokenAsync(CancellationToken.None);

		Assert.Equal("cc-access", token);
		Assert.Equal("cc-access", provider.Cached!.Access);
	}

	[Fact]
	public async Task AuthorizeAsync_RunsTheLoopbackFlowAndStoresTheToken()
	{
		using var server = new FakeOAuthServer();
		var metadata = new McpAuthorizationServerMetadata
		{
			AuthorizationEndpoint = $"{server.BaseUrl}/authorize",
			TokenEndpoint = $"{server.BaseUrl}/token",
			RegistrationEndpoint = $"{server.BaseUrl}/register",
		};

		var config = new McpServerConfig { Name = "remote", Url = Endpoint };
		var authorize = McpOAuth.AuthorizeAsync(
			new HttpClient(),
			Endpoint,
			config,
			metadata,
			_agentDir,
			url =>
			{
				// 模拟浏览器：把 code 与 state 回调到授权地址里给出的回环 redirect_uri。
				var redirect = QueryParameter(url, "redirect_uri")!;
				var state = QueryParameter(url, "state")!;
				_ = Task.Run(() => CallbackAsync(redirect, state));
			},
			CancellationToken.None,
			openBrowser: false);

		var result = await authorize.WaitAsync(TimeSpan.FromSeconds(20));

		Assert.True(result.Ok, result.Error);
		Assert.Equal("access-1", result.Token!.Access);
		// 没有配置 clientId 时应走动态注册。
		Assert.Equal("dynamic-client", result.Token.ClientId);
		Assert.Equal("access-1", McpOAuthStore.Load(Endpoint, _agentDir)!.Access);
		Assert.Contains("grant_type=authorization_code", server.LastTokenBody);
		Assert.Contains("code_verifier=", server.LastTokenBody);
	}

	private static string? QueryParameter(string url, string name)
	{
		var index = url.IndexOf('?', StringComparison.Ordinal);
		if (index < 0)
		{
			return null;
		}

		foreach (var pair in url[(index + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
		{
			var separator = pair.IndexOf('=', StringComparison.Ordinal);
			if (separator > 0 && Uri.UnescapeDataString(pair[..separator]) == name)
			{
				return Uri.UnescapeDataString(pair[(separator + 1)..]);
			}
		}

		return null;
	}

	private static async Task CallbackAsync(string redirectUri, string state)
	{
		using var client = new HttpClient();
		await client.GetAsync($"{redirectUri}?code=test-code&state={Uri.EscapeDataString(state)}");
	}
}

/// <summary>最小的授权服务器桩：受保护资源元数据、授权服务器元数据、动态注册与令牌端点。</summary>
internal sealed class FakeOAuthServer : IDisposable
{
	private const string Resource = "https://mcp.example/mcp";

	private readonly HttpListener _listener = new();
	private readonly CancellationTokenSource _cts = new();

	public FakeOAuthServer()
	{
		var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
		probe.Start();
		var port = ((IPEndPoint)probe.LocalEndpoint).Port;
		probe.Stop();

		BaseUrl = $"http://127.0.0.1:{port}";
		_listener.Prefixes.Add($"{BaseUrl}/");
		_listener.Start();
		_ = Task.Run(LoopAsync);
	}

	public string BaseUrl { get; }

	public string? LastTokenBody { get; private set; }

	public void Dispose()
	{
		_cts.Cancel();
		try
		{
			_listener.Stop();
		}
		catch (ObjectDisposedException)
		{
			// 已停止。
		}

		_listener.Close();
		GC.SuppressFinalize(this);
	}

	private async Task LoopAsync()
	{
		while (!_cts.IsCancellationRequested)
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

			try
			{
				await RespondAsync(context);
			}
			catch (Exception)
			{
				try
				{
					context.Response.StatusCode = 500;
					context.Response.Close();
				}
				catch (Exception)
				{
					// 连接可能已断开。
				}
			}
		}
	}

	private async Task RespondAsync(HttpListenerContext context)
	{
		var path = context.Request.Url!.AbsolutePath;
		string json;

		switch (path)
		{
			case "/.well-known/oauth-protected-resource/mcp":
				json = new JsonObject
				{
					["resource"] = Resource,
					["authorization_servers"] = new JsonArray(BaseUrl),
				}.ToJsonString();
				break;

			case "/.well-known/oauth-authorization-server":
			case "/.well-known/oauth-authorization-server/mcp":
				json = new JsonObject
				{
					["issuer"] = BaseUrl,
					["authorization_endpoint"] = $"{BaseUrl}/authorize",
					["token_endpoint"] = $"{BaseUrl}/token",
					["registration_endpoint"] = $"{BaseUrl}/register",
				}.ToJsonString();
				break;

			case "/register":
				json = new JsonObject { ["client_id"] = "dynamic-client" }.ToJsonString();
				break;

			case "/token":
				using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
				{
					LastTokenBody = await reader.ReadToEndAsync();
				}

				json = new JsonObject
				{
					["access_token"] = "access-1",
					["refresh_token"] = "refresh-1",
					["token_type"] = "Bearer",
					["expires_in"] = 3600,
				}.ToJsonString();
				break;

			default:
				context.Response.StatusCode = 404;
				context.Response.Close();
				return;
		}

		var payload = Encoding.UTF8.GetBytes(json);
		context.Response.StatusCode = 200;
		context.Response.ContentType = "application/json";
		context.Response.ContentLength64 = payload.Length;
		await context.Response.OutputStream.WriteAsync(payload);
		context.Response.Close();
	}
}
