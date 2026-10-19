using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using PiWeft.Ai;
using PiWeft.Cli;
using PiWeft.CodingAgent.Session;
using Xunit;

namespace PiWeft.Cli.Tests;

/// <summary>Serves a canned Anthropic SSE stream so the CLI can be exercised end to end without a network.</summary>
internal sealed class FakeAnthropicHandler : HttpMessageHandler
{
	public const string StreamBody = """
		event: message_start
		data: {"type":"message_start","message":{"id":"msg_cli","model":"claude-sonnet-4-5","usage":{"input_tokens":12}}}

		event: content_block_start
		data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}

		event: content_block_delta
		data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"cli says hi"}}

		event: content_block_stop
		data: {"type":"content_block_stop","index":0}

		event: message_delta
		data: {"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":3}}

		event: message_stop
		data: {"type":"message_stop"}

		""";

	public int Requests { get; private set; }

	public string? LastBody { get; private set; }

	protected override async Task<HttpResponseMessage> SendAsync(
		HttpRequestMessage request,
		CancellationToken cancellationToken)
	{
		Requests++;
		LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);

		return new HttpResponseMessage(HttpStatusCode.OK)
		{
			Content = new StringContent(StreamBody, Encoding.UTF8, "text/event-stream"),
		};
	}
}

public class CliEndToEndTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-cli-e2e-" + Guid.NewGuid().ToString("N")[..8]);

	public CliEndToEndTests() => Directory.CreateDirectory(_root);

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	private static CliArguments RpcArguments(string sessionDir) => CliArguments.Parse(
	[
		"--mode", "rpc",
		"--model", "anthropic/claude-sonnet-4-5",
		"--api-key", "test-key",
		"--session-dir", sessionDir,
		"--no-skills",
		"--no-prompt-templates",
		"--no-context-files",
		"--approve",
	]).Arguments!;

	[Fact]
	public async Task RpcPromptRunsARealProviderStreamAndPersistsTheSession()
	{
		var handler = new FakeAnthropicHandler();
		var sessionDir = Path.Combine(_root, "sessions");

		var (environment, error) = CliEnvironment.Create(RpcArguments(sessionDir), _root, handler);
		Assert.Null(error);
		Assert.NotNull(environment);

		var output = new StringWriter();
		var runner = new CliRunner(environment!, output, new StringWriter());

		var commands = string.Join(
			'\n',
			"""{"id":"1","type":"prompt","message":"hello over http"}""",
			"""{"id":"2","type":"exit"}""");

		await runner.RunAsync(RpcArguments(sessionDir), _root, new StringReader(commands), CancellationToken.None);

		// Wait for the asynchronous run to finish streaming.
		var records = await WaitForAsync(output, static text => text.Contains("\"type\":\"agent_end\""));

		// The request really went through the provider HTTP path.
		Assert.Equal(1, handler.Requests);
		Assert.Contains("hello over http", handler.LastBody);

		var messageEnd = records.Last(static record =>
			record["type"]!.GetValue<string>() == "message_end" &&
			record["message"]!["role"]!.GetValue<string>() == "assistant");

		Assert.Equal("cli says hi", messageEnd["message"]!["content"]![0]!["text"]!.GetValue<string>());
		Assert.Equal("msg_cli", messageEnd["message"]!["responseId"]!.GetValue<string>());

		var usage = messageEnd["message"]!["usage"]!;
		Assert.Equal(12, usage["input"]!.GetValue<int>());
		Assert.Equal(3, usage["output"]!.GetValue<int>());

		// The session file was written and is readable by the session layer.
		var file = Assert.Single(Directory.EnumerateFiles(sessionDir, "*.jsonl"));
		var session = SessionManager.Open(file);
		Assert.Equal(SessionFormat.CurrentVersion, session.Header!.Version);
		Assert.Equal(_root, session.Header.Cwd);

		var messages = session.BuildProjectedMessages();
		Assert.Contains(messages, static m => m is UserMessage user && user.Content.Text == "hello over http");
		Assert.Contains(messages, static m => m is AssistantMessage assistant &&
			assistant.Content.OfType<TextContent>().Any(block => block.Text == "cli says hi"));
	}

	[Fact]
	public async Task McpServersContributeTheirToolsAndTheResourceTools()
	{
		var fixture = FixtureAssembly();
		var dotnet = DotnetHost();
		Assert.False(fixture is null || dotnet is null, $"fixture={fixture ?? "<null>"} dotnet={dotnet ?? "<null>"}");

		// A project-level mcp.json: the fixture server over stdio.
		var configDir = Path.Combine(_root, ".pi");
		Directory.CreateDirectory(configDir);
		File.WriteAllText(
			Path.Combine(configDir, "mcp.json"),
			$$"""
			{
			  "mcpServers": {
			    "fixture": {
			      "command": {{CliTestJson.String(dotnet!)}},
			      "args": [{{CliTestJson.String(fixture!)}}],
			      "timeout": 30
			    }
			  }
			}
			""");

		var sessionDir = Path.Combine(_root, "sessions-mcp");
		var (environment, error) = CliEnvironment.Create(RpcArguments(sessionDir), _root, new FakeAnthropicHandler());
		Assert.Null(error);

		var output = new StringWriter();
		var runner = new CliRunner(environment!, output, new StringWriter());

		await runner.RunAsync(
			RpcArguments(sessionDir),
			_root,
			new StringReader(string.Join('\n', """{"id":"1","type":"get_state"}""", """{"id":"2","type":"exit"}""")),
			CancellationToken.None);

		var records = await WaitForAsync(output, static text => text.Contains("\"command\":\"get_state\""));
		var response = records.First(static record =>
			record["type"]!.GetValue<string>() == "response" &&
			record["command"]!.GetValue<string>() == "get_state");
		var tools = response["data"]!["tools"]!.AsArray().Select(static node => node!.GetValue<string>()).ToList();

		// Every tool of the server, across both listing pages, plus the three resource tools.
		// MCP defaults to codemode exposure, but those tools remain executable and are listed here.
		Assert.Contains("mcp__fixture__echo", tools);
		Assert.Contains("mcp__fixture__fail", tools);
		Assert.Contains("mcp__fixture__add_tool", tools);
		Assert.Contains("list_mcp_resources", tools);
		Assert.Contains("list_mcp_resource_templates", tools);
		Assert.Contains("read_mcp_resource", tools);

		// Built-ins are still there.
		Assert.Contains("read", tools);
		Assert.Contains("bash", tools);
	}

	[Fact]
	public async Task JsonModeWritesHeaderAndStreamsProviderEvents()
	{
		var handler = new FakeAnthropicHandler();
		var sessionDir = Path.Combine(_root, "sessions-json");

		var (environment, error) = CliEnvironment.Create(RpcArguments(sessionDir), _root, handler);
		Assert.Null(error);

		var output = new StringWriter();
		var runner = new CliRunner(environment!, output, new StringWriter());

		var arguments = CliArguments.Parse(
		[
			"--mode", "json",
			"--model", "anthropic/claude-sonnet-4-5",
			"--api-key", "test-key",
			"--session-dir", sessionDir,
			"--no-skills", "--no-prompt-templates", "--no-context-files",
			"hello json",
		]).Arguments!;

		var exitCode = await runner.RunAsync(arguments, _root, new StringReader(string.Empty), CancellationToken.None);
		Assert.Equal(0, exitCode);

		var records = output.ToString()
			.Split('\n', StringSplitOptions.RemoveEmptyEntries)
			.Select(static line => JsonNode.Parse(line)!)
			.ToList();

		Assert.Equal("session", records[0]!["type"]!.GetValue<string>());

		var types = records.Select(static record => record["type"]!.GetValue<string>()).ToList();
		Assert.Contains("message_update", types);
		Assert.Equal("agent_settled", types[^1]);

		var delta = records.First(static record =>
			record["type"]!.GetValue<string>() == "message_update" &&
			record["assistantMessageEvent"]!["type"]!.GetValue<string>() == "text_delta");
		Assert.Equal("cli says hi", delta["assistantMessageEvent"]!["delta"]!.GetValue<string>());
	}

	private static string? FixtureAssembly()
	{
		var directory = AppContext.BaseDirectory;
		var candidates = new[]
		{
			Path.Combine(directory, "PiWeft.Mcp.FixtureServer.dll"),
			Path.Combine(directory, "..", "..", "..", "..", "..", "tools", "PiWeft.Mcp.FixtureServer", "bin", "Debug", "net10.0", "PiWeft.Mcp.FixtureServer.dll"),
			Path.Combine(directory, "..", "..", "..", "..", "..", "tools", "PiWeft.Mcp.FixtureServer", "bin", "Release", "net10.0", "PiWeft.Mcp.FixtureServer.dll"),
		};

		return candidates.Select(Path.GetFullPath).FirstOrDefault(File.Exists);
	}

	private static string? DotnetHost()
	{
		var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
		foreach (var directory in path.Split(Path.PathSeparator))
		{
			if (directory.Length == 0)
			{
				continue;
			}

			var candidate = Path.Combine(directory, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
			if (File.Exists(candidate))
			{
				return candidate;
			}
		}

		return null;
	}

	private static async Task<List<JsonNode>> WaitForAsync(StringWriter output, Func<string, bool> condition)
	{
		for (var attempt = 0; attempt < 300; attempt++)
		{
			if (condition(output.ToString()))
			{
				break;
			}

			await Task.Delay(10);
		}

		return
		[
			.. output.ToString()
				.Split('\n', StringSplitOptions.RemoveEmptyEntries)
				.Where(static line => line.StartsWith('{'))
				.Select(static line => JsonNode.Parse(line)!),
		];
	}
}
