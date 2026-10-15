using System.Diagnostics;
using System.Text.Json.Nodes;
using PiWeft.Agent;
using PiWeft.Ai;
using PiWeft.Ai.Utils;
using PiWeft.Mcp;
using Xunit;

namespace PiWeft.Mcp.Tests;

/// <summary>真实子进程上的 stdio/HTTP 握手、分页与工具调用。</summary>
public class McpProcessTests
{
	private static string DotnetHost => Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";

	private static string FixtureAssembly()
	{
		var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
		var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../tools/PiWeft.Mcp.FixtureServer/bin",
			configuration, "net10.0/PiWeft.Mcp.FixtureServer.dll"));
		Assert.True(File.Exists(path), $"fixture is not built: {path}");
		return path;
	}

	private static McpServerConfig Config(string name) => new()
	{
		Name = name, Command = DotnetHost, Arguments = [FixtureAssembly()], Timeout = 30,
	};

	[Fact]
	public async Task ConnectsToARealStdioServerAndCallsItsTools()
	{
		var config = Config("fixture") with { Env = new Dictionary<string, string> { ["FIXTURE_GREETING"] = "from-env" } };
		await using var process = await McpProcess.StartAsync(config, CancellationToken.None);
		Assert.Equal("fixture-server", process.Client.ServerName);
		Assert.Equal("Fixture server for integration tests.", process.Client.Instructions);
		var tools = await process.Client.ListToolsAsync(CancellationToken.None);
		Assert.Equal(3, tools.Count);
		Assert.Contains(tools, static tool => tool.Name == "echo");
		Assert.Contains(tools, static tool => tool.Name == "fail");
		Assert.Contains(tools, static tool => tool.Name == "add_tool");
		var echo = McpToolAdapter.Create(process.Client, config.Name, tools.Single(static t => t.Name == "echo"), config.TimeoutSpan);
		Assert.Null(JsonSchemaValidator.ValidateToolArguments(echo.ToDeclaration(), new JsonObject { ["message"] = "hi" }).Error);
		var result = await echo.Execute(new AgentToolInvocation
		{
			ToolCallId = "c1", ToolName = echo.Name, Arguments = new JsonObject { ["message"] = "hi" },
		});
		Assert.False(result.IsError);
		Assert.Equal("from-env: hi", Assert.IsType<TextContent>(result.Content[0]).Text);
		Assert.Equal("hi", result.StructuredContent!["message"]!.GetValue<string>());
		var fail = McpToolAdapter.Create(process.Client, config.Name, tools.Single(static t => t.Name == "fail"), config.TimeoutSpan);
		var failed = await fail.Execute(new AgentToolInvocation
		{
			ToolCallId = "c2", ToolName = fail.Name, Arguments = new JsonObject(),
		});
		Assert.True(failed.IsError);
		Assert.Equal("fixture failure", Assert.IsType<TextContent>(failed.Content[0]).Text);
	}

	[Fact]
	public async Task ExposureFiltersToolsAndSkipsFullyHiddenServers()
	{
		// 默认 direct：三个工具都声明。
		await using (var all = await McpServers.ConnectAllAsync([Config("all")]))
		{
			Assert.Equal(3, all.Tools.Count);
		}

		// 整台 hidden：不连接、不暴露工具。
		await using (var hidden = await McpServers.ConnectAllAsync([Config("hidden") with { Exposure = "hidden" }]))
		{
			Assert.Empty(hidden.Tools);
			Assert.Equal("hidden", Assert.Single(hidden.Servers).Error);
		}

		// 逐工具 hidden：只留 echo。
		await using (var filtered = await McpServers.ConnectAllAsync(
			[Config("filtered") with
			{
				ToolExposure = new Dictionary<string, string> { ["fail"] = "hidden", ["add_*"] = "hidden" },
			}]))
		{
			Assert.Equal(["mcp__filtered__echo"], filtered.Tools.Select(static tool => tool.Name));
		}
	}

	[Fact]
	public async Task ConnectsToARealHttpServer()
	{
		await using var fixture = await HttpFixture.StartAsync();
		await using var client = await McpHttpClient.ConnectAsync(fixture.Url,
			headers: new Dictionary<string, string> { ["X-Test"] = "1" }, timeout: TimeSpan.FromSeconds(20));
		Assert.Equal("fixture-server", client.ServerName);
		Assert.Equal("Fixture server for integration tests.", client.Instructions);
		Assert.Equal("fixture-session", client.SessionId);
		var tools = await client.ListToolsAsync(CancellationToken.None);
		Assert.Equal(3, tools.Count);
		var echo = McpToolAdapter.Create(client, "http-fixture", tools.Single(static t => t.Name == "echo"), TimeSpan.FromSeconds(20));
		Assert.Equal("mcp__http_fixture__echo", echo.Name);
		var result = await echo.Execute(new AgentToolInvocation
		{
			ToolCallId = "c1", ToolName = echo.Name, Arguments = new JsonObject { ["message"] = "hi" },
		});
		Assert.False(result.IsError);
		Assert.Equal("http: hi", Assert.IsType<TextContent>(result.Content[0]).Text);
	}

	[Fact]
	public async Task ConnectAllUsesTheHttpTransportForUrlEntries()
	{
		await using var fixture = await HttpFixture.StartAsync();
		await using var connection = await McpServers.ConnectAllAsync(
			[new McpServerConfig { Name = "remote-fixture", Url = fixture.Url, Timeout = 20 }]);
		Assert.Empty(connection.Errors);
		Assert.Equal(3, connection.Tools.Count);
		Assert.Contains(connection.Tools, static tool => tool.Name == "mcp__remote_fixture__echo");
	}

	[Fact]
	public async Task ConnectAllExposesRealServerToolsWithPiNaming()
	{
		await using var connection = await McpServers.ConnectAllAsync([Config("fixture-server")]);
		Assert.Empty(connection.Errors);
		Assert.Equal(3, connection.Tools.Count);
		Assert.All(connection.Tools, static tool => Assert.StartsWith("mcp__fixture_server__", tool.Name));
		Assert.Contains(connection.Tools, static tool => tool.Name == "mcp__fixture_server__echo");
	}

	private sealed class HttpFixture(Process process, int port) : IAsyncDisposable
	{
		public string Url => $"http://127.0.0.1:{port}/mcp/";

		public static async Task<HttpFixture> StartAsync()
		{
			var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
			listener.Start();
			var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
			listener.Stop();
			var info = new ProcessStartInfo(DotnetHost)
			{
				RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false,
			};
			foreach (var argument in new[] { FixtureAssembly(), "--http", port.ToString() }) info.ArgumentList.Add(argument);
			var process = Process.Start(info)!;
			var fixture = new HttpFixture(process, port);
			_ = process.StandardError.ReadToEndAsync();
			try
			{
				Assert.Contains("listening", await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)));
				return fixture;
			}
			catch
			{
				await fixture.DisposeAsync();
				throw;
			}
		}

		public async ValueTask DisposeAsync()
		{
			try
			{
				if (!process.HasExited) process.Kill(entireProcessTree: true);
				await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
			}
			finally { process.Dispose(); }
		}
	}
}
