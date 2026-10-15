using System.Text.Json.Nodes;
using PiWeft.Ai;
using PiWeft.Cli;
using PiWeft.CodingAgent.Session;
using PiWeft.CodingAgent.Settings;
using Xunit;

namespace PiWeft.Cli.Tests;

/// <summary>RPC commands added for parity with pi: session switching, queues, shell and compaction toggles.</summary>
public class RpcCommandTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-rpc2-" + Guid.NewGuid().ToString("N")[..8]);

	public RpcCommandTests() => Directory.CreateDirectory(_root);

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	private string SessionsDir => Path.Combine(_root, "sessions");

	private (CliEnvironment Environment, StringWriter Output) Setup()
	{
		var model = TestModels.Model();
		var registry = new ModelRegistry();
		registry.Add(model);

		var environment = CliEnvironment.ForTests(
			registry,
			new CodingAgentSettings(),
			model,
			PiWeft.CodingAgent.Tools.BuiltinTools.Tools(_root),
			new ScriptedStreamFn().Invoke);

		return (environment, new StringWriter());
	}

	private CliArguments Arguments(params string[] extra) => CliArguments.Parse(
	[
		"--mode", "rpc",
		"--model", "test/test-model",
		"--session-dir", SessionsDir,
		"--no-skills", "--no-prompt-templates", "--no-context-files",
		.. extra,
	]).Arguments!;

	private async Task<List<JsonNode>> RunAsync(CliRunner runner, CliArguments arguments, string commands, StringWriter output)
	{
		await runner.RunAsync(arguments, _root, new StringReader(commands), CancellationToken.None);

		return
		[
			.. output.ToString()
				.Split('\n', StringSplitOptions.RemoveEmptyEntries)
				.Select(static line => JsonNode.Parse(line)!),
		];
	}

	private static JsonObject ResponseFor(List<JsonNode> records, string command) =>
		records.Single(node =>
			node["type"]!.GetValue<string>() == "response" && node["command"]!.GetValue<string>() == command)!
			.AsObject();

	[Fact]
	public async Task QueueModesAndClearQueueRoundTrip()
	{
		var (environment, output) = Setup();
		var runner = new CliRunner(environment, output, new StringWriter());

		var commands = string.Join(
			'\n',
			"""{"id":"1","type":"set_steering_mode","mode":"all"}""",
			"""{"id":"2","type":"set_follow_up_mode","mode":"one-at-a-time"}""",
			"""{"id":"3","type":"clear_queue"}""",
			"""{"id":"4","type":"exit"}""");

		var records = await RunAsync(runner, Arguments("--no-session"), commands, output);

		Assert.Equal("all", ResponseFor(records, "set_steering_mode")["data"]!["mode"]!.GetValue<string>());
		Assert.Equal("one-at-a-time", ResponseFor(records, "set_follow_up_mode")["data"]!["mode"]!.GetValue<string>());

		var cleared = ResponseFor(records, "clear_queue")["data"]!;
		Assert.Empty(cleared["steering"]!.AsArray());
		Assert.Empty(cleared["followUp"]!.AsArray());
	}

	[Fact]
	public async Task AutoCompactionCanBeToggled()
	{
		var (environment, output) = Setup();
		var runner = new CliRunner(environment, output, new StringWriter());

		var commands = string.Join(
			'\n',
			"""{"id":"1","type":"set_auto_compaction","enabled":false}""",
			"""{"id":"2","type":"exit"}""");

		var records = await RunAsync(runner, Arguments("--no-session"), commands, output);

		Assert.False(ResponseFor(records, "set_auto_compaction")["data"]!["enabled"]!.GetValue<bool>());
	}

	[Fact]
	public async Task BashRunsImmediatelyStreamsUpdatesAndReturnsTheResult()
	{
		if (OperatingSystem.IsWindows())
		{
			return;
		}

		var (environment, output) = Setup();
		var runner = new CliRunner(environment, output, new StringWriter());

		var commands = string.Join(
			'\n',
			"""{"id":"1","type":"bash","command":"printf 'hello from bash\\n'"}""",
			"""{"id":"2","type":"exit"}""");

		var records = await RunAsync(runner, Arguments("--no-session"), commands, output);

		var bash = ResponseFor(records, "bash");
		Assert.True(bash["success"]!.GetValue<bool>());
		Assert.Equal("hello from bash\n", bash["data"]!["output"]!.GetValue<string>());
		Assert.Equal(0, bash["data"]!["exitCode"]!.GetValue<int>());
		Assert.False(bash["data"]!["truncated"]!.GetValue<bool>());
		Assert.Contains(records, static node => node["type"]!.GetValue<string>() == "bash_execution_update");
	}

	[Fact]
	public async Task BashReportsFailuresAndExitCodes()
	{
		if (OperatingSystem.IsWindows())
		{
			return;
		}

		var (environment, output) = Setup();
		var runner = new CliRunner(environment, output, new StringWriter());

		var commands = string.Join(
			'\n',
			"""{"id":"1","type":"bash","command":"echo boom >&2; exit 4"}""",
			"""{"id":"2","type":"exit"}""");

		var records = await RunAsync(runner, Arguments("--no-session"), commands, output);
		var data = ResponseFor(records, "bash")["data"]!;

		Assert.Equal(4, data["exitCode"]!.GetValue<int>());
		Assert.Contains("boom", data["output"]!.GetValue<string>());
	}

	[Fact]
	public async Task NewSessionStartsAFreshFileAndKeepsServing()
	{
		var (environment, output) = Setup();
		var runner = new CliRunner(environment, output, new StringWriter());

		var commands = string.Join(
			'\n',
			"""{"id":"1","type":"get_state"}""",
			"""{"id":"2","type":"new_session"}""",
			"""{"id":"3","type":"get_state"}""",
			"""{"id":"4","type":"exit"}""");

		var records = await RunAsync(runner, Arguments(), commands, output);

		var states = records
			.Where(static node => node["type"]!.GetValue<string>() == "response" && node["command"]!.GetValue<string>() == "get_state")
			.ToList();

		Assert.Equal(2, states.Count);
		Assert.False(ResponseFor(records, "new_session")["data"]!["cancelled"]!.GetValue<bool>());

		var first = states[0]!["data"]!["sessionId"]!.GetValue<string>();
		var second = states[1]!["data"]!["sessionId"]!.GetValue<string>();
		Assert.NotEqual(first, second);

		var files = Directory.EnumerateFiles(SessionsDir, "*.jsonl").ToList();
		Assert.Equal(2, files.Count);
		Assert.All(files, static file => Assert.Equal(3, SessionManager.Open(file).Header!.Version));
	}

	[Fact]
	public async Task NewSessionRecordsTheParentSession()
	{
		var parent = SessionManager.Create(_root, SessionsDir);
		parent.AppendMessage(new UserMessage { Content = MessageContent.FromText("parent"), Timestamp = 1 });

		var (environment, output) = Setup();
		var runner = new CliRunner(environment, output, new StringWriter());

		var command = new JsonObject
		{
			["id"] = "1",
			["type"] = "new_session",
			["parentSession"] = parent.SessionFile,
		};

		var commands = string.Join('\n', command.ToJsonString(), """{"id":"2","type":"exit"}""");
		await RunAsync(runner, Arguments(), commands, output);

		var forked = SessionManager.Open(
			Directory.EnumerateFiles(SessionsDir, "*.jsonl")
				.OrderByDescending(File.GetLastWriteTimeUtc)
				.First());

		Assert.Equal(parent.SessionFile, forked.Header!.ParentSession);
	}

	[Fact]
	public async Task GetStateReportsTheToolSet()
	{
		var (environment, output) = Setup();
		var runner = new CliRunner(environment, output, new StringWriter());

		var commands = string.Join(
			'\n',
			"""{"id":"1","type":"get_state"}""",
			"""{"id":"2","type":"exit"}""");

		var records = await RunAsync(runner, Arguments("--no-session"), commands, output);
		var tools = ResponseFor(records, "get_state")["data"]!["tools"]!.AsArray()
			.Select(static node => node!.GetValue<string>())
			.ToList();

		Assert.Contains("read", tools);
		Assert.Contains("bash", tools);
	}
}
