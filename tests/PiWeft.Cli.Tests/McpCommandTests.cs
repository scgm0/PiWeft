using PiWeft.Cli;
using Xunit;

namespace PiWeft.Cli.Tests;

/// <summary>
/// <c>pi mcp add/list/remove</c> 的配置读写。agent 目录通过参数注入，
/// 因此测试不改进程环境变量（也就不会影响并行运行的其它测试）。
/// </summary>
public sealed class McpCommandTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "pi-mcp-" + Guid.NewGuid().ToString("N"));

	private readonly string _agentDir;

	private readonly string _cwd;

	public McpCommandTests()
	{
		_agentDir = Path.Combine(_root, "agent");
		_cwd = Path.Combine(_root, "repo");
		Directory.CreateDirectory(_agentDir);
		Directory.CreateDirectory(_cwd);
	}

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	private string SettingsPath => Path.Combine(_agentDir, "mcp.json");

	private async Task<int> RunAsync(string command, string[] rest, StringWriter output, StringWriter error)
	{
		var exit = await McpCommands.RunAsync(command, rest, output, error, _cwd, CancellationToken.None, _agentDir);
		return exit ?? -1;
	}

	[Fact]
	public async Task AddThenListThenRemove_UsesTheUserConfig()
	{
		var output = new StringWriter();
		var error = new StringWriter();

		Assert.Equal(0, await RunAsync("add", ["demo", "--url", "https://mcp.example/mcp", "--auth", "oauth"], output, error));
		Assert.Empty(error.ToString());

		var json = File.ReadAllText(SettingsPath);
		Assert.Contains("\"demo\"", json, StringComparison.Ordinal);
		Assert.Contains("oauth", json, StringComparison.Ordinal);
		Assert.Contains("OAuth", output.ToString(), StringComparison.Ordinal);

		output.GetStringBuilder().Clear();
		Assert.Equal(0, await RunAsync("list", [], output, error));
		Assert.Contains("demo", output.ToString(), StringComparison.Ordinal);
		Assert.Contains("未授权", output.ToString(), StringComparison.Ordinal);

		output.GetStringBuilder().Clear();
		Assert.Equal(0, await RunAsync("remove", ["demo"], output, error));
		Assert.DoesNotContain("demo", File.ReadAllText(SettingsPath), StringComparison.Ordinal);
	}

	[Fact]
	public async Task Add_StdioServer_WritesArgsAndEnv()
	{
		var output = new StringWriter();
		var error = new StringWriter();

		var exit = await RunAsync(
			"add",
			["files", "--command", "npx", "--arg", "-y", "--arg", "@modelcontextprotocol/server-filesystem", "--env", "HOME=/tmp"],
			output,
			error);

		Assert.Equal(0, exit);
		var json = File.ReadAllText(SettingsPath);
		Assert.Contains("npx", json, StringComparison.Ordinal);
		Assert.Contains("-y", json, StringComparison.Ordinal);
		Assert.Contains("HOME", json, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Add_WithBothTransports_Fails()
	{
		var output = new StringWriter();
		var error = new StringWriter();

		Assert.Equal(2, await RunAsync("add", ["x", "--url", "https://mcp.example/mcp", "--command", "npx"], output, error));
		Assert.Contains("只能", error.ToString(), StringComparison.Ordinal);
	}

	[Fact]
	public async Task Add_WithoutAnyTransport_Fails()
	{
		var output = new StringWriter();
		var error = new StringWriter();

		Assert.Equal(2, await RunAsync("add", ["x"], output, error));
		Assert.Contains("--url", error.ToString(), StringComparison.Ordinal);
	}

	[Fact]
	public async Task Add_InvalidName_Fails()
	{
		var output = new StringWriter();
		var error = new StringWriter();

		Assert.Equal(2, await RunAsync("add", ["bad name", "--url", "https://mcp.example/mcp"], output, error));
	}

	[Fact]
	public async Task Add_LocalScope_WritesProjectConfig()
	{
		var output = new StringWriter();
		var error = new StringWriter();

		Assert.Equal(0, await RunAsync("add", ["demo", "--url", "https://mcp.example/mcp", "--local"], output, error));

		Assert.True(File.Exists(Path.Combine(_cwd, ".pi", "mcp.json")));
		Assert.False(File.Exists(SettingsPath));
	}

	[Fact]
	public async Task List_WithoutServers_PrintsAHint()
	{
		var output = new StringWriter();
		var error = new StringWriter();

		Assert.Equal(0, await RunAsync("list", [], output, error));
		Assert.Contains("没有配置 MCP 服务器", output.ToString(), StringComparison.Ordinal);
	}

	[Fact]
	public async Task List_Json_ReportsOAuthState()
	{
		var output = new StringWriter();
		var error = new StringWriter();
		await RunAsync("add", ["demo", "--url", "https://mcp.example/mcp", "--auth", "oauth"], output, error);

		output.GetStringBuilder().Clear();
		Assert.Equal(0, await RunAsync("list", ["--json"], output, error));

		var json = System.Text.Json.Nodes.JsonNode.Parse(output.ToString())!;
		var server = json["servers"]![0]!;
		Assert.Equal("demo", server["name"]!.GetValue<string>());
		Assert.Equal("http", server["transport"]!.GetValue<string>());
		Assert.True(server["oauth"]!.GetValue<bool>());
		Assert.False(server["authorized"]!.GetValue<bool>());
	}

	[Fact]
	public async Task Remove_UnknownServer_Fails()
	{
		var output = new StringWriter();
		var error = new StringWriter();

		Assert.Equal(1, await RunAsync("remove", ["ghost"], output, error));
	}

	[Fact]
	public async Task UnknownSubcommand_ReturnsUsageError()
	{
		var output = new StringWriter();
		var error = new StringWriter();

		Assert.Equal(2, await RunAsync("bogus", [], output, error));
		Assert.Contains("未知的 mcp 子命令", error.ToString(), StringComparison.Ordinal);
	}
}
