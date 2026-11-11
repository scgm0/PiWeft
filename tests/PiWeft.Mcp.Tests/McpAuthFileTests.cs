using System.Text.Json.Nodes;
using PiWeft.Mcp;
using Xunit;

namespace PiWeft.Mcp.Tests;

/// 与 pi 共用 <c>mcp-auth.json</c>：键、状态形状、旧文件迁移。
public sealed class McpAuthFileTests : IDisposable
{
	private const string Endpoint = "https://mcp.example/mcp";

	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-mcpauth-" + Guid.NewGuid().ToString("N")[..8]);

	public McpAuthFileTests() => Directory.CreateDirectory(_root);

	public void Dispose()
	{
		try
		{
			Directory.Delete(_root, recursive: true);
		}
		catch (IOException)
		{
		}
	}

	private string AuthPath => Path.Combine(_root, McpOAuthStore.FileName);

	[Fact]
	public void SaveWritesPiStateShape()
	{
		McpOAuthStore.Save("demo", Endpoint, new McpOAuthToken
		{
			Access = "access-1",
			Refresh = "refresh-1",
			ExpiresAt = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(),
			Scope = "mcp.read",
			ClientId = "client-1",
			TokenEndpoint = "https://auth.example/token",
			AuthorizationServer = "https://auth.example",
		}, _root);

		var root = JsonNode.Parse(File.ReadAllText(AuthPath))!.AsObject();
		var key = Assert.Single(root.Select(static pair => pair.Key).ToList());
		Assert.Equal($"mcp__demo|{Endpoint}", key);

		var state = root[key]!.AsObject();
		Assert.Equal(Endpoint, state["serverUrl"]!.GetValue<string>());
		Assert.Equal("access-1", state["tokens"]!["access_token"]!.GetValue<string>());
		Assert.Equal("refresh-1", state["tokens"]!["refresh_token"]!.GetValue<string>());
		Assert.Equal("Bearer", state["tokens"]!["token_type"]!.GetValue<string>());
		Assert.Equal("client-1", state["clientInformation"]!["client_id"]!.GetValue<string>());
		Assert.Equal("https://auth.example/token", state["discovery"]!["authorizationServerMetadata"]!["token_endpoint"]!.GetValue<string>());
		Assert.Equal("https://auth.example", state["discovery"]!["authorizationServerUrl"]!.GetValue<string>());
		Assert.True(state["tokensExpireAt"]!.GetValue<long>() > 0);
	}

	[Fact]
	public void ReadsAFileWrittenByPi()
	{
		File.WriteAllText(AuthPath, """
			{
			  "mcp__demo|https://mcp.example/mcp": {
			    "serverUrl": "https://mcp.example/mcp",
			    "clientInformation": { "client_id": "pi-client" },
			    "tokens": {
			      "access_token": "pi-access",
			      "token_type": "Bearer",
			      "expires_in": 3600,
			      "refresh_token": "pi-refresh",
			      "scope": "mcp.read"
			    },
			    "tokensExpireAt": 4102444800000,
			    "discovery": {
			      "authorizationServerUrl": "https://auth.example",
			      "authorizationServerMetadata": { "token_endpoint": "https://auth.example/token" }
			    }
			  }
			}
			""");

		var token = McpOAuthStore.Load("demo", Endpoint, _root);

		Assert.NotNull(token);
		Assert.Equal("pi-access", token.Access);
		Assert.Equal("pi-refresh", token.Refresh);
		Assert.Equal("pi-client", token.ClientId);
		Assert.Equal("https://auth.example/token", token.TokenEndpoint);
		Assert.Equal(4102444800, token.ExpiresAt);
		Assert.False(token.IsExpiring(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1)));
	}

	[Fact]
	public void KeepsUnknownPiFieldsWhenSaving()
	{
		File.WriteAllText(AuthPath, """
			{
			  "mcp__demo|https://mcp.example/mcp": {
			    "serverUrl": "https://mcp.example/mcp",
			    "codeVerifier": "verifier-1",
			    "tokens": { "access_token": "old", "token_type": "Bearer" }
			  }
			}
			""");

		McpOAuthStore.Save("demo", Endpoint, new McpOAuthToken { Access = "new" }, _root);

		var state = JsonNode.Parse(File.ReadAllText(AuthPath))!["mcp__demo|https://mcp.example/mcp"]!.AsObject();
		Assert.Equal("verifier-1", state["codeVerifier"]!.GetValue<string>());
		Assert.Equal("new", state["tokens"]!["access_token"]!.GetValue<string>());
	}

	[Fact]
	public void ServersWithTheSameUrlKeepSeparateAccounts()
	{
		McpOAuthStore.Save("work", Endpoint, new McpOAuthToken { Access = "work" }, _root);
		McpOAuthStore.Save("personal", Endpoint, new McpOAuthToken { Access = "personal" }, _root);

		Assert.Equal("work", McpOAuthStore.Load("work", Endpoint, _root)!.Access);
		Assert.Equal("personal", McpOAuthStore.Load("personal", Endpoint, _root)!.Access);
	}

	[Fact]
	public void LegacyFileIsMigratedAndRemoved()
	{
		File.WriteAllText(Path.Combine(_root, McpOAuthStore.LegacyFileName), """
			{ "servers": { "https://mcp.example/mcp": { "access": "legacy", "refresh": "legacy-refresh" } } }
			""");

		var token = McpOAuthStore.Load("demo", Endpoint, _root);

		Assert.Equal("legacy", token!.Access);
		Assert.Equal("legacy-refresh", token.Refresh);
		Assert.True(File.Exists(AuthPath));
		Assert.False(File.Exists(Path.Combine(_root, McpOAuthStore.LegacyFileName)));
	}

	[Fact]
	public void RemoveDeletesBothKeysOfAServer()
	{
		McpOAuthStore.Save(Endpoint, new McpOAuthToken { Access = "url-key" }, _root);
		McpOAuthStore.Save("demo", Endpoint, new McpOAuthToken { Access = "named-key" }, _root);

		Assert.True(McpOAuthStore.Remove("demo", Endpoint, _root));

		Assert.Null(McpOAuthStore.Load("demo", Endpoint, _root));
		Assert.Null(McpOAuthStore.Load(Endpoint, _root));
	}
}
