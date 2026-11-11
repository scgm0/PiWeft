using PiWeft.Mcp;
using Xunit;

namespace PiWeft.Mcp.Tests;

/// CIMD：文档 URL 与回调路径的推导，以及 auto/dcr/cimd 的选择。
public sealed class McpClientMetadataDocumentTests
{
	private const string Endpoint = "https://mcp.example/mcp";

	private static McpAuthorizationServerMetadata Metadata(
		bool documents = true,
		bool iss = true,
		params string[] authMethods) => new()
		{
			AuthorizationEndpoint = "https://auth.example/authorize",
			TokenEndpoint = "https://auth.example/token",
			ClientIdMetadataDocumentSupported = documents,
			AuthorizationResponseIssParameterSupported = iss,
			TokenEndpointAuthMethodsSupported = authMethods.Length > 0 ? authMethods : ["none"],
		};

	[Fact]
	public void ListsLoopbackCallbackOnAnyPort()
	{
		Assert.True(McpClientMetadataDocuments.ListsRedirectUrl("http://127.0.0.1:41234/callback"));
		Assert.True(McpClientMetadataDocuments.ListsRedirectUrl("http://localhost:41234/callback"));
		Assert.False(McpClientMetadataDocuments.ListsRedirectUrl("http://127.0.0.1:41234/other"));
		Assert.False(McpClientMetadataDocuments.ListsRedirectUrl("https://example.com/callback"));
	}

	[Fact]
	public void UsesTheSharedDocumentWhenTheServerSendsIss()
	{
		var resolved = McpClientMetadataDocuments.Resolve(Endpoint, "http://127.0.0.1:41234/callback", Metadata());

		Assert.NotNull(resolved);
		Assert.Equal("https://pi.dev/oauth/client.json", resolved!.Value.Url);
		Assert.Equal("http://127.0.0.1:41234/callback", resolved.Value.RedirectUrl);
	}

	[Fact]
	public void UsesAServerSpecificDocumentWithoutIss()
	{
		var resolved = McpClientMetadataDocuments.Resolve(Endpoint, "http://127.0.0.1:41234/callback", Metadata(iss: false));

		Assert.NotNull(resolved);
		var id = McpClientMetadataDocuments.CallbackId(Endpoint);
		Assert.Equal(12, id.Length);
		Assert.Equal($"https://pi.dev/oauth/{id}/client.json", resolved!.Value.Url);
		Assert.Equal($"http://127.0.0.1:41234/callback/{id}", resolved.Value.RedirectUrl);
	}

	[Fact]
	public void RequiresDocumentSupportAndPublicClientAuth()
	{
		Assert.Null(McpClientMetadataDocuments.Resolve(Endpoint, "http://127.0.0.1:1/callback", Metadata(documents: false)));
		Assert.Null(McpClientMetadataDocuments.Resolve(Endpoint, "http://127.0.0.1:1/callback", Metadata(authMethods: "client_secret_basic")));
	}

	[Fact]
	public void RegistrationModeChoosesTheIdentity()
	{
		var plain = new McpServerConfig { Name = "demo", Url = Endpoint };
		var named = plain with { OAuth = new McpOAuthConfigEntry { ClientName = "Claude Code" } };

		Assert.True(McpOAuth.ShouldUseClientMetadataDocument("cimd", plain, Metadata(), "http://127.0.0.1:1/callback"));
		Assert.False(McpOAuth.ShouldUseClientMetadataDocument("dcr", plain, Metadata(), "http://127.0.0.1:1/callback"));
		Assert.True(McpOAuth.ShouldUseClientMetadataDocument("auto", plain, Metadata(), "http://127.0.0.1:1/callback"));
		// 设了 clientName 就一定要注册，否则名字发不出去。
		Assert.False(McpOAuth.ShouldUseClientMetadataDocument("auto", named, Metadata(), "http://127.0.0.1:1/callback"));
		Assert.False(McpOAuth.ShouldUseClientMetadataDocument("auto", plain, Metadata(documents: false), "http://127.0.0.1:1/callback"));
	}

	[Fact]
	public async Task AuthorizeWithCimdSkipsRegistration()
	{
		var root = Path.Combine(Path.GetTempPath(), "piweft-cimd-" + Guid.NewGuid().ToString("N")[..8]);
		var agentDir = Path.Combine(root, "agent");
		Directory.CreateDirectory(agentDir);
		try
		{
			using var server = new FakeOAuthServer();
			var metadata = new McpAuthorizationServerMetadata
			{
				AuthorizationEndpoint = $"{server.BaseUrl}/authorize",
				TokenEndpoint = $"{server.BaseUrl}/token",
				RegistrationEndpoint = $"{server.BaseUrl}/register",
				ClientIdMetadataDocumentSupported = true,
				AuthorizationResponseIssParameterSupported = true,
				TokenEndpointAuthMethodsSupported = ["none"],
			};

			var config = new McpServerConfig
			{
				Name = "remote",
				Url = "https://mcp.example/mcp",
				OAuth = new McpOAuthConfigEntry { ClientRegistration = "cimd" },
			};

			string? authorizationUrl = null;
			var authorize = McpOAuth.AuthorizeAsync(
				new HttpClient(),
				config.Url!,
				config,
				metadata,
				agentDir,
				url =>
				{
					authorizationUrl = url;
					var redirect = TestQueryParameter(url, "redirect_uri")!;
					var state = TestQueryParameter(url, "state")!;
					_ = Task.Run(() => CallbackAsync(redirect, state));
				},
				CancellationToken.None,
				openBrowser: false);

			var result = await authorize.WaitAsync(TimeSpan.FromSeconds(20));

			Assert.True(result.Ok, result.Error);
			Assert.Equal("https://pi.dev/oauth/client.json", TestQueryParameter(authorizationUrl!, "client_id"));
			Assert.False(server.RegistrationCalled);
			Assert.Equal("https://pi.dev/oauth/client.json", McpOAuthStore.Load("remote", config.Url!, agentDir)!.ClientId);
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	private static string? TestQueryParameter(string url, string name)
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

	private static async Task CallbackAsync(string redirect, string state)
	{
		using var http = new HttpClient();
		try
		{
			await http.GetAsync($"{redirect}?code=auth-code&state={Uri.EscapeDataString(state)}");
		}
		catch (Exception)
		{
			// 回调服务器收到就关连接，客户端看到异常是正常的。
		}
	}
}
