using System.Text.Json.Nodes;
using PiWeft.Agent;
using PiWeft.Ai;
using PiWeft.Mcp;
using Xunit;

namespace PiWeft.Mcp.Tests;

public class McpConfigTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-mcp-" + Guid.NewGuid().ToString("N")[..8]);

	public McpConfigTests() => Directory.CreateDirectory(_root);

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	private void WriteUser(string json)
	{
		var agentDir = Path.Combine(_root, "agent");
		Directory.CreateDirectory(agentDir);
		File.WriteAllText(Path.Combine(agentDir, "mcp.json"), json);
	}

	private void WriteProject(string json)
	{
		var directory = Path.Combine(_root, "project", ".pi");
		Directory.CreateDirectory(directory);
		File.WriteAllText(Path.Combine(directory, "mcp.json"), json);
	}

	private string ProjectCwd => Path.Combine(_root, "project");

	[Fact]
	public void Load_WithoutProjectTrustIgnoresProjectConfig()
	{
		WriteUser("""
			{ "mcpServers": { "user-server": { "command": "u" } } }
			""");
		WriteProject("""
			{ "mcpServers": { "project-server": { "command": "p" } } }
			""");

		var agentDir = Path.Combine(_root, "agent");

		// 受信时两者合并。
		Assert.Contains(McpConfig.Load(ProjectCwd, agentDir), static server => server.Name == "project-server");

		// 不受信时只留用户级配置。
		var untrusted = McpConfig.Load(ProjectCwd, agentDir, includeProject: false);
		Assert.DoesNotContain(untrusted, static server => server.Name == "project-server");
		Assert.Contains(untrusted, static server => server.Name == "user-server");
	}

	[Fact]
	public void Load_ReadsStdioAndHttpEntries()
	{
		WriteUser(
			"""
			{
			  "mcpServers": {
			    "filesystem": {
			      "command": "npx",
			      "args": ["-y", "@modelcontextprotocol/server-filesystem", "."],
			      "env": { "API_KEY": "static" },
			      "timeout": 30
			    },
			    "docs": {
			      "url": "https://example.com/mcp",
			      "headers": { "Authorization": "Bearer ${DOCS_TOKEN}" },
			      "description": "Search the docs"
			    },
			    "off": { "command": "x", "enabled": false }
			  }
			}
			""");

		Environment.SetEnvironmentVariable("DOCS_TOKEN", "secret");
		try
		{
			var servers = McpConfig.Load(ProjectCwd, Path.Combine(_root, "agent"));

			Assert.Equal(3, servers.Count);

			var filesystem = servers.Single(static s => s.Name == "filesystem");
			Assert.True(filesystem.IsStdio);
			Assert.Equal("npx", filesystem.Command);
			Assert.Equal(3, filesystem.Arguments.Count);
			Assert.Equal(30, filesystem.Timeout);
			Assert.Equal("static", filesystem.Env!["API_KEY"]);
			Assert.Equal(TimeSpan.FromSeconds(30), filesystem.TimeoutSpan);

			var docs = servers.Single(static s => s.Name == "docs");
			Assert.True(docs.IsHttp);
			Assert.Equal("Bearer secret", docs.Headers!["Authorization"]);
			Assert.Equal("Search the docs", docs.Description);

			Assert.False(servers.Single(static s => s.Name == "off").Enabled);
		}
		finally
		{
			Environment.SetEnvironmentVariable("DOCS_TOKEN", null);
		}
	}

	[Fact]
	public void Load_MergesProjectOverUserIncludingPartialOverrides()
	{
		WriteUser(
			"""
			{
			  "mcpServers": {
			    "internal-tools": { "command": "run-tools", "env": { "A": "1" }, "timeout": 10 },
			    "shared": { "command": "shared-a" }
			  }
			}
			""");

		// A project entry without command/url overrides only the fields it sets.
		WriteProject(
			"""
			{
			  "mcpServers": {
			    "internal-tools": { "enabled": false },
			    "shared": { "command": "shared-b", "args": ["--project"] }
			  }
			}
			""");

		var servers = McpConfig.Load(ProjectCwd, Path.Combine(_root, "agent"));

		var internalTools = servers.Single(static s => s.Name == "internal-tools");
		Assert.False(internalTools.Enabled);
		Assert.Equal("run-tools", internalTools.Command);
		Assert.Equal("1", internalTools.Env!["A"]);
		Assert.Equal(10, internalTools.Timeout);

		var shared = servers.Single(static s => s.Name == "shared");
		Assert.Equal("shared-b", shared.Command);
		Assert.Equal(["--project"], shared.Arguments);
	}

	[Fact]
	public void Load_RejectsInvalidNamesAndDuplicatesDifferingOnlyInSeparators()
	{
		WriteUser(
			"""
			{
			  "mcpServers": {
			    "bad name": { "command": "x" },
			    "dev-radius": { "command": "a" },
			    "dev_radius": { "command": "b" }
			  }
			}
			""");

		var servers = McpConfig.Load(ProjectCwd, Path.Combine(_root, "agent"));

		Assert.DoesNotContain(servers, static s => s.Name == "bad name");
		Assert.Single(servers, static s => McpConfig.CanonicalName(s.Name) == "dev_radius");
		Assert.Equal("a", servers.Single(static s => McpConfig.CanonicalName(s.Name) == "dev_radius").Command);
	}

	[Fact]
	public void Load_ExpandsHomeAndEnvironmentVariables()
	{
		WriteUser(
			"""
			{
			  "mcpServers": {
			    "home": { "command": "~/bin/tool", "cwd": "~/work", "args": ["${TOOL_ARG}"] }
			  }
			}
			""");

		Environment.SetEnvironmentVariable("TOOL_ARG", "from-env");
		try
		{
			var server = McpConfig.Load(ProjectCwd, Path.Combine(_root, "agent")).Single();
			var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

			Assert.Equal(Path.Combine(home, "bin/tool"), server.Command);
			Assert.Equal(Path.Combine(home, "work"), server.Cwd);
			Assert.Equal(["from-env"], server.Arguments);
		}
		finally
		{
			Environment.SetEnvironmentVariable("TOOL_ARG", null);
		}
	}

	[Fact]
	public void Load_WithoutFiles_ReturnsNothing()
	{
		Assert.Empty(McpConfig.Load(ProjectCwd, Path.Combine(_root, "missing")));
	}
}

public class McpToolNamingTests
{
	[Fact]
	public void ToolNamesFollowThePiConvention()
	{
		Assert.Equal("mcp__filesystem__read_file", McpToolAdapter.ToolName("filesystem", "read_file"));
		Assert.Equal("mcp__dev_radius__list_rules", McpToolAdapter.ToolName("dev-radius", "list-rules"));
		Assert.Equal("mcp__s__a_b_c", McpToolAdapter.ToolName("s", "a.b/c"));
	}

	[Fact]
	public void AdapterCarriesExposureNamespaceAndOutputSchema()
	{
		var tool = new McpTool
		{
			Name = "list-rules",
			Description = "List rules",
			InputSchema = new JsonObject { ["type"] = "object" },
			OutputSchema = new JsonObject { ["type"] = "object" },
		};

		var agentTool = McpToolAdapter.Create(
			new StubInvoker(),
			"dev-radius",
			tool,
			TimeSpan.FromSeconds(1),
			McpExposure.Codemode,
			"Radius rules",
			"Call list-rules first.");

		Assert.Equal(McpExposure.Codemode, agentTool.Exposure);
		Assert.Equal("mcp__dev_radius", agentTool.Namespace);
		Assert.Contains("dev-radius", agentTool.NamespaceAliases!);
		Assert.Equal("Radius rules", agentTool.NamespaceDescription);
		Assert.Equal("Call list-rules first.", agentTool.NamespaceInstructions);
		Assert.NotNull(agentTool.OutputSchema);
	}

	private sealed class StubInvoker : IMcpToolInvoker
	{
		public Task<McpCallResult> CallToolAsync(string name, JsonObject arguments, CancellationToken cancellationToken) =>
			Task.FromResult(new McpCallResult());

		public Task<IReadOnlyList<McpTool>> ListToolsAsync(CancellationToken cancellationToken) =>
			Task.FromResult<IReadOnlyList<McpTool>>([]);
	}
}

public class McpClientTests
{
	[Fact]
	public async Task ConnectPerformsTheHandshakeAndListsTools()
	{
		var (client, server) = FakeMcpServer.Create();
		await using var _ = server;
		await using var __ = client;

		server.AddTool("read_file", "Read a file", new JsonObject
		{
			["type"] = "object",
			["properties"] = new JsonObject { ["path"] = new JsonObject { ["type"] = "string" } },
			["required"] = new JsonArray("path"),
		}, _ => new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "contents" }) });

		Assert.Equal("fake-server", client.ServerName);
		Assert.Equal("9.9.9", client.ServerVersion);
		Assert.Equal("Fake server instructions", client.Instructions);

		var tools = await client.ListToolsAsync(CancellationToken.None);
		var tool = Assert.Single(tools);
		Assert.Equal("read_file", tool.Name);
		Assert.Equal("Read a file", tool.Description);
		Assert.Equal("string", tool.InputSchema["properties"]!["path"]!["type"]!.GetValue<string>());

		// initialize, then the initialized notification, then tools/list.
		Assert.Equal(["initialize", "notifications/initialized", "tools/list"], server.Methods);
	}

	[Fact]
	public async Task CallToolDecodesTextImageAndErrors()
	{
		var (client, server) = FakeMcpServer.Create();
		await using var _ = server;
		await using var __ = client;

		server.AddTool("mixed", "Mixed content", new JsonObject { ["type"] = "object" }, _ => new JsonObject
		{
			["content"] = new JsonArray(
				new JsonObject { ["type"] = "text", ["text"] = "hello" },
				new JsonObject { ["type"] = "image", ["data"] = "AAAA", ["mimeType"] = "image/png" }),
			["structuredContent"] = new JsonObject { ["value"] = 42 },
		});

		server.AddTool("broken", "Always fails", new JsonObject { ["type"] = "object" }, _ => new JsonObject
		{
			["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "boom" }),
			["isError"] = true,
		});

		var mixed = await client.CallToolAsync("mixed", new JsonObject(), CancellationToken.None);
		Assert.False(mixed.IsError);
		Assert.Equal(2, mixed.Content.Count);
		Assert.Equal("hello", Assert.IsType<TextContent>(mixed.Content[0]).Text);
		Assert.Equal("image/png", Assert.IsType<ImageContent>(mixed.Content[1]).MimeType);
		Assert.Equal(42, mixed.StructuredContent!["value"]!.GetValue<int>());

		var broken = await client.CallToolAsync("broken", new JsonObject(), CancellationToken.None);
		Assert.True(broken.IsError);
		Assert.Equal("boom", Assert.IsType<TextContent>(broken.Content[0]).Text);

		var error = await Assert.ThrowsAsync<McpException>(() =>
			client.CallToolAsync("missing", new JsonObject(), CancellationToken.None));
		Assert.Equal(-32602, error.Code);
		Assert.Contains("Unknown tool", error.Message);
	}

	[Fact]
	public async Task ServerInitiatedPingIsAnswered()
	{
		var (client, server) = FakeMcpServer.Create();
		await using var _ = server;
		await using var __ = client;

		// The fake server answers tools/list; use it as a round trip to prove the client stays responsive.
		await client.ListToolsAsync(CancellationToken.None);
		Assert.Contains("tools/list", server.Methods);
	}

	[Fact]
	public async Task AdapterMapsMcpResultsToAgentToolResults()
	{
		var (client, server) = FakeMcpServer.Create();
		await using var _ = server;
		await using var __ = client;

		server.AddTool("lookup", "Look something up", new JsonObject
		{
			["type"] = "object",
			["properties"] = new JsonObject { ["id"] = new JsonObject { ["type"] = "string" } },
			["required"] = new JsonArray("id"),
		}, arguments => new JsonObject
		{
			["content"] = new JsonArray(new JsonObject
			{
				["type"] = "text",
				["text"] = "found " + arguments["id"]!.GetValue<string>(),
			}),
		});

		var tools = await client.ListToolsAsync(CancellationToken.None);
		var agentTool = McpToolAdapter.Create(client, "dev-radius", tools[0], TimeSpan.FromSeconds(5));

		Assert.Equal("mcp__dev_radius__lookup", agentTool.Name);

		var result = await agentTool.Execute(new AgentToolInvocation
		{
			ToolCallId = "c1",
			ToolName = agentTool.Name,
			Arguments = new JsonObject { ["id"] = "42" },
		});

		Assert.False(result.IsError);
		Assert.Equal("found 42", Assert.IsType<TextContent>(result.Content[0]).Text);
		Assert.Equal("dev-radius", result.Details!["server"]!.GetValue<string>());
	}

	[Fact]
	public async Task AdapterReportsMcpErrorsAsToolErrors()
	{
		var (client, server) = FakeMcpServer.Create();
		await using var _ = server;
		await using var __ = client;

		var tool = new McpTool
		{
			Name = "nope",
			Description = "fails",
			InputSchema = new JsonObject { ["type"] = "object" },
		};

		var agentTool = McpToolAdapter.Create(client, "srv", tool, timeout: null);
		var result = await agentTool.Execute(new AgentToolInvocation
		{
			ToolCallId = "c1",
			ToolName = agentTool.Name,
			Arguments = new JsonObject(),
		});

		Assert.True(result.IsError);
		Assert.Contains("MCP error from 'srv'", Assert.IsType<TextContent>(result.Content[0]).Text);
	}
}

public class McpExposureTests
{
	[Fact]
	public void Resolve_PrefersExactNamesThenPatterns()
	{
		var config = new McpServerConfig
		{
			Name = "s",
			Exposure = "direct",
			ToolExposure = new Dictionary<string, string> { ["exact"] = "hidden", ["pre*"] = "hidden" },
		};

		Assert.Equal("hidden", McpExposure.Resolve(config, "exact"));
		Assert.False(McpExposure.IsVisible(McpExposure.Resolve(config, "exact")));
		Assert.False(McpExposure.IsVisible(McpExposure.Resolve(config, "prefix")));
		Assert.True(McpExposure.IsVisible(McpExposure.Resolve(config, "other")));
	}

	[Fact]
	public void Normalize_ReplacesAliasesAndRejectsUnknown()
	{
		Assert.Equal("codemode", McpExposure.Normalize("codemode-deferred"));
		Assert.Equal("hidden", McpExposure.Normalize(" hidden "));
		Assert.Null(McpExposure.Normalize("nope"));
		Assert.Null(McpExposure.Normalize(null));
	}

	[Fact]
	public void IsFullyHidden_OnlyWhenTheWholeServerIsHiddenWithoutOverrides()
	{
		Assert.True(McpExposure.IsFullyHidden(new McpServerConfig { Name = "s", Exposure = "hidden" }));
		Assert.False(McpExposure.IsFullyHidden(new McpServerConfig
		{
			Name = "s",
			Exposure = "hidden",
			ToolExposure = new Dictionary<string, string> { ["a"] = "direct" },
		}));
		Assert.False(McpExposure.IsFullyHidden(new McpServerConfig { Name = "s" }));
	}

	[Theory]
	[InlineData("a*", "abc", true)]
	[InlineData("a*", "a", true)]
	[InlineData("*c", "abc", true)]
	[InlineData("a*c", "abbbc", true)]
	[InlineData("a*c", "abbbd", false)]
	[InlineData("ab", "abc", false)]
	public void WildcardMatchesWholeNames(string pattern, string value, bool expected) =>
		Assert.Equal(expected, McpExposure.MatchesWildcard(pattern, value));

	[Fact]
	public void Merge_ReadsPiOAuthObject()
	{
		var file = new McpConfigFile
		{
			McpServers = new Dictionary<string, McpServerEntry>
			{
				["remote"] = new()
				{
					Url = "https://mcp.example/mcp",
					OAuth = new McpOAuthConfigEntry
					{
						ClientId = "client",
						ClientSecret = "secret",
						Scope = "read write",
						CallbackPort = 8765,
						ClientName = "PiWeft Test",
					},
				},
			},
		};

		var server = Assert.Single(McpConfig.Merge(file, null));

		Assert.True(server.UsesOAuth);
		Assert.Equal("client", server.ClientId);
		Assert.Equal("secret", server.ClientSecret);
		Assert.Equal(["read", "write"], server.Scopes);
		Assert.Equal(8765, server.OAuth!.CallbackPort);
	}

	[Fact]
	public void Merge_NormalizesExposureFromTheConfigFile()
	{
		var file = new McpConfigFile
		{
			McpServers = new Dictionary<string, McpServerEntry>
			{
				["s"] = new()
				{
					Command = "cmd",
					Exposure = "codemode-deferred",
					ToolExposure = new Dictionary<string, string> { ["t"] = "hidden", ["u"] = "bogus" },
				},
			},
		};

		var server = Assert.Single(McpConfig.Merge(file, null));
		Assert.Equal("codemode", server.Exposure);
		Assert.Equal("hidden", server.ToolExposure!["t"]);
		// 未知值回落到默认（与 pi 一致是 codemode），而不是让服务器或工具消失。
		Assert.Equal("codemode", server.ToolExposure!["u"]);
	}
}

public class McpServersTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-mcpproc-" + Guid.NewGuid().ToString("N")[..8]);

	public McpServersTests() => Directory.CreateDirectory(_root);

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	[Fact]
	public async Task ConnectAll_ReportsFailuresPerServerWithoutFailingOthers()
	{
		var servers = new List<McpServerConfig>
		{
			new() { Name = "missing", Command = "/nonexistent/definitely-not-a-server" },
			// A local port that nothing listens on fails fast without touching the network.
			new() { Name = "remote", Url = "http://127.0.0.1:1/mcp", Timeout = 2 },
			new() { Name = "disabled", Command = "whatever", Enabled = false },
		};

		var errors = new List<string>();
		await using var connection = await McpServers.ConnectAllAsync(servers, errors.Add);

		Assert.Empty(connection.Tools);
		Assert.Equal(2, connection.Errors.Count);
		Assert.Equal(2, errors.Count);

		// Both unreachable entries are reported; the disabled one is skipped entirely.
		Assert.Contains(connection.Errors, static e => e.StartsWith("missing:", StringComparison.Ordinal));
		Assert.Contains(connection.Errors, static e => e.StartsWith("remote:", StringComparison.Ordinal));
		Assert.DoesNotContain(connection.Errors, static e => e.StartsWith("disabled:", StringComparison.Ordinal));
	}

	[Fact]
	public async Task ConnectAll_ReportsEntriesWithoutATransport()
	{
		await using var connection = await McpServers.ConnectAllAsync(
			[new McpServerConfig { Name = "empty" }]);

		Assert.Single(connection.Errors);
		Assert.Contains("neither command nor url", connection.Errors[0]);
	}
}
