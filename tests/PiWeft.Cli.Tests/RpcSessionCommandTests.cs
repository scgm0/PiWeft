using System.Text.Json.Nodes;
using PiWeft.Ai;
using PiWeft.Cli;
using PiWeft.CodingAgent.Session;
using PiWeft.CodingAgent.Settings;
using Xunit;

namespace PiWeft.Cli.Tests;

/// <summary>
/// The RPC surface PiDeck drives: session introspection, navigation (fork/clone/switch), discovery and export. Each
/// test asserts the shapes documented in pi's <c>rpc-commands.md</c>, so a client written against pi works unchanged.
/// </summary>
public class RpcSessionCommandTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-rpc3-" + Guid.NewGuid().ToString("N")[..8]);
	private readonly ScriptedStreamFn _script = new();

	public RpcSessionCommandTests()
	{
		Directory.CreateDirectory(_root);

		// Two turns, so there is something to inspect, fork and export.
		_script.Enqueue("first answer");
		_script.Enqueue("second answer");
	}

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
		registry.Add(TestModels.Model("other-model"));

		var environment = CliEnvironment.ForTests(
			registry,
			new CodingAgentSettings(),
			model,
			PiWeft.CodingAgent.Tools.BuiltinTools.Tools(_root),
			_script.Invoke);

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

	/// <summary>
	/// Reads commands one at a time, pausing after a <c>prompt</c> so the next prompt arrives after the first run
	/// finished. Without the pause a second prompt would be queued as a steering message, which is real pi behavior
	/// but not what these assertions are about.
	/// </summary>
	private sealed class PacedReader(string commands, int pauseMs = 200) : TextReader
	{
		private readonly string[] _lines = commands.Split('\n');
		private int _index;
		private bool _pauseBeforeNext;

		public override string? ReadLine()
		{
			if (_index >= _lines.Length)
			{
				return null;
			}

			// Wait out the prompt before handing over the next command.
			if (_pauseBeforeNext)
			{
				Thread.Sleep(pauseMs);
				_pauseBeforeNext = false;
			}

			var line = _lines[_index++];
			if (line.Contains("\"type\":\"prompt\"", StringComparison.Ordinal))
			{
				_pauseBeforeNext = true;
			}

			return line;
		}
	}

	private async Task<List<JsonNode>> RunAsync(CliRunner runner, CliArguments arguments, string commands, StringWriter output)
	{
		await runner.RunAsync(arguments, _root, new PacedReader(commands), CancellationToken.None);

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

	/// <summary>Runs two prompts so the session has entries to work with.</summary>
	private static string Seed() => string.Join(
		'\n',
		"""{"id":"p1","type":"prompt","message":"first question"}""",
		"""{"id":"p2","type":"prompt","message":"second question"}""");

	[Fact]
	public async Task GetStateReportsEverythingAStatusBarNeeds()
	{
		var (environment, output) = Setup();
		var runner = new CliRunner(environment, output, new StringWriter());

		var records = await RunAsync(
			runner,
			Arguments(),
			string.Join('\n', Seed(), """{"id":"1","type":"get_state"}""", """{"id":"2","type":"exit"}"""),
			output);

		var state = ResponseFor(records, "get_state")["data"]!;

		Assert.Equal("test-model", state["model"]!["id"]!.GetValue<string>());
		Assert.Equal("test", state["model"]!["provider"]!.GetValue<string>());
		Assert.Equal(["text"], state["model"]!["input"]!.AsArray().Select(static node => node!.GetValue<string>()));
		Assert.True(state["model"]!["cost"]!.AsObject().ContainsKey("cacheRead"));
		Assert.False(state["isStreaming"]!.GetValue<bool>());
		Assert.False(state["isCompacting"]!.GetValue<bool>());
		// pi's defaults, from settings-defaults.ts.
		Assert.Equal("one-at-a-time", state["steeringMode"]!.GetValue<string>());
		Assert.Equal("one-at-a-time", state["followUpMode"]!.GetValue<string>());
		Assert.Equal(0, state["pendingMessageCount"]!.GetValue<int>());
		// 两次提问两次回复；系统提示词是运行时注入的，不计入 messageCount（与 pi 一致）。
		Assert.Equal(4, state["messageCount"]!.GetValue<int>());
		Assert.NotNull(state["sessionFile"]!.GetValue<string>());
		Assert.Contains("read", state["tools"]!.AsArray().Select(static node => node!.GetValue<string>()));
	}

	[Fact]
	public async Task GetSessionStatsCountsMessagesAndTokens()
	{
		var (environment, output) = Setup();
		var runner = new CliRunner(environment, output, new StringWriter());

		var records = await RunAsync(
			runner,
			Arguments(),
			string.Join('\n', Seed(), """{"id":"1","type":"get_session_stats"}""", """{"id":"2","type":"exit"}"""),
			output);

		var stats = ResponseFor(records, "get_session_stats")["data"]!;

		Assert.Equal(2, stats["userMessages"]!.GetValue<int>());
		Assert.Equal(2, stats["assistantMessages"]!.GetValue<int>());
		Assert.Equal(0, stats["toolResults"]!.GetValue<int>());
		Assert.Equal(4, stats["totalMessages"]!.GetValue<int>());
		var tokens = stats["tokens"]!;
		Assert.Equal(20, tokens["input"]!.GetValue<long>());
		Assert.Equal(8, tokens["output"]!.GetValue<long>());
		Assert.Equal(0, tokens["cacheRead"]!.GetValue<long>());
		Assert.Equal(0, tokens["cacheWrite"]!.GetValue<long>());
		Assert.Equal(28, tokens["total"]!.GetValue<long>());
		Assert.True(stats["cost"]!.GetValue<double>() >= 0);

		var context = stats["contextUsage"]!;
		Assert.Equal(128_000, context["contextWindow"]!.GetValue<long>());
		Assert.True(context["percent"]!.GetValue<double>() > 0);
	}

	[Fact]
	public async Task EntriesSupportACursorAndRejectUnknownIds()
	{
		var (environment, output) = Setup();
		var runner = new CliRunner(environment, output, new StringWriter());

		var records = await RunAsync(
			runner,
			Arguments(),
			string.Join('\n', Seed(), """{"id":"1","type":"get_entries"}""", """{"id":"2","type":"exit"}"""),
			output);

		var entries = ResponseFor(records, "get_entries")["data"]!;
		var all = entries["entries"]!.AsArray();
		Assert.True(all.Count >= 4); // two user messages + two assistant messages（系统提示词不入库）
		Assert.Equal(all[^1]!["id"]!.GetValue<string>(), entries["leafId"]!.GetValue<string>());

		var firstId = all[0]!["id"]!.GetValue<string>();
		var secondId = all[1]!["id"]!.GetValue<string>();

		// The cursor returns only what came after it. The run continues the session that was just written.
		var (environment2, output2) = Setup();
		var runner2 = new CliRunner(environment2, output2, new StringWriter());
		var cursorRecords = await RunAsync(
			runner2,
			Arguments("--continue"),
			string.Join(
				'\n',
				Seed(),
				$$"""{"id":"1","type":"get_entries","since":"{{secondId}}"}""",
				"""{"id":"2","type":"exit"}"""),
			output2);

		var after = ResponseFor(cursorRecords, "get_entries")["data"]!["entries"]!.AsArray();
		Assert.DoesNotContain(firstId, after.Select(static node => node!["id"]!.GetValue<string>()));
		Assert.DoesNotContain(secondId, after.Select(static node => node!["id"]!.GetValue<string>()));

		// An unknown cursor is an error, as pi documents.
		var (environment3, output3) = Setup();
		var runner3 = new CliRunner(environment3, output3, new StringWriter());
		var badRecords = await RunAsync(
			runner3,
			Arguments(),
			string.Join('\n', Seed(), """{"id":"1","type":"get_entries","since":"nope"}""", """{"id":"2","type":"exit"}"""),
			output3);

		var bad = ResponseFor(badRecords, "get_entries");
		Assert.False(bad["success"]!.GetValue<bool>());
		Assert.Contains("nope", bad["error"]!.GetValue<string>());
	}

	[Fact]
	public async Task TreeReflectsTheActiveBranchAndLabels()
	{
		var (environment, output) = Setup();
		var runner = new CliRunner(environment, output, new StringWriter());

		var records = await RunAsync(
			runner,
			Arguments(),
			string.Join('\n', Seed(), """{"id":"1","type":"get_tree"}""", """{"id":"2","type":"exit"}"""),
			output);

		var data = ResponseFor(records, "get_tree")["data"]!;
		var tree = data["tree"]!.AsArray();

		var root = Assert.Single(tree);
		Assert.Null(root!["entry"]!["parentId"]);

		// A single chain: every node has at most one child, and the deep end is the leaf.
		var node = root;
		var depth = 1;
		while (node!["children"]!.AsArray().Count > 0)
		{
			node = Assert.Single(node["children"]!.AsArray());
			depth++;
		}

		Assert.Equal(node!["entry"]!["id"]!.GetValue<string>(), data["leafId"]!.GetValue<string>());
		Assert.True(depth >= 4);
	}

	[Fact]
	public async Task LastAssistantTextAndSessionName()
	{
		var (environment, output) = Setup();
		var runner = new CliRunner(environment, output, new StringWriter());

		var records = await RunAsync(
			runner,
			Arguments(),
			string.Join(
				'\n',
				Seed(),
				"""{"id":"1","type":"set_session_name","name":"my-feature-work"}""",
				"""{"id":"2","type":"get_last_assistant_text"}""",
				"""{"id":"3","type":"get_state"}""",
				"""{"id":"4","type":"exit"}"""),
			output);

		Assert.True(ResponseFor(records, "set_session_name")["success"]!.GetValue<bool>());
		Assert.Equal(
			"second answer",
			ResponseFor(records, "get_last_assistant_text")["data"]!["text"]!.GetValue<string>());
		Assert.Equal("my-feature-work", ResponseFor(records, "get_state")["data"]!["sessionName"]!.GetValue<string>());
	}

	[Fact]
	public async Task ForkMessagesForkAndCloneCreateNewSessionFiles()
	{
		var (environment, output) = Setup();
		var runner = new CliRunner(environment, output, new StringWriter());

		var records = await RunAsync(
			runner,
			Arguments(),
			string.Join(
				'\n',
				Seed(),
				"""{"id":"1","type":"get_fork_messages"}""",
				"""{"id":"2","type":"get_state"}""",
				"""{"id":"3","type":"exit"}"""),
			output);

		var forkable = ResponseFor(records, "get_fork_messages")["data"]!["messages"]!.AsArray();
		Assert.Equal(2, forkable.Count);
		Assert.Equal("first question", forkable[0]!["text"]!.GetValue<string>());

		var originalFile = ResponseFor(records, "get_state")["data"]!["sessionFile"]!.GetValue<string>();
		var forkEntry = forkable[0]!["entryId"]!.GetValue<string>();

		// Forking before the first message starts a fresh session that records the previous one as its parent.
		// The run continues the session the entry id came from.
		var (environment2, output2) = Setup();
		var runner2 = new CliRunner(environment2, output2, new StringWriter());
		var forkRecords = await RunAsync(
			runner2,
			Arguments("--continue"),
			string.Join(
				'\n',
				Seed(),
				$$"""{"id":"1","type":"fork","entryId":"{{forkEntry}}"}""",
				"""{"id":"2","type":"get_state"}""",
				"""{"id":"3","type":"exit"}"""),
			output2);

		var forked = ResponseFor(forkRecords, "fork");
		Assert.True(forked["success"]!.GetValue<bool>());
		Assert.Equal("first question", forked["data"]!["text"]!.GetValue<string>());
		Assert.False(forked["data"]!["cancelled"]!.GetValue<bool>());

		var forkedFile = ResponseFor(forkRecords, "get_state")["data"]!["sessionFile"]!.GetValue<string>();
		Assert.NotEqual(originalFile, forkedFile);
		Assert.True(File.Exists(forkedFile));

		// Cloning keeps the current branch: the copy has the same messages and names the original as its parent.
		var (environment3, output3) = Setup();
		var runner3 = new CliRunner(environment3, output3, new StringWriter());
		var cloneRecords = await RunAsync(
			runner3,
			Arguments(),
			string.Join(
				'\n',
				Seed(),
				"""{"id":"1","type":"clone"}""",
				"""{"id":"2","type":"get_messages"}""",
				"""{"id":"3","type":"exit"}"""),
			output3);

		Assert.True(ResponseFor(cloneRecords, "clone")["success"]!.GetValue<bool>());
		var cloned = ResponseFor(cloneRecords, "get_messages")["data"]!["messages"]!.AsArray();

		// 两条提问 + 两条回复；系统提示词不入库也不在消息列表里。
		Assert.Equal(4, cloned.Count);
	}

	[Fact]
	public async Task SwitchSessionLoadsAnotherFile()
	{
		// First run creates a session with two turns.
		var (environment, output) = Setup();
		var runner = new CliRunner(environment, output, new StringWriter());
		await RunAsync(
			runner,
			Arguments(),
			string.Join('\n', Seed(), """{"id":"1","type":"exit"}"""),
			output);

		var file = Assert.Single(Directory.EnumerateFiles(SessionsDir, "*.jsonl"));

		// A second run with an empty session switches to the first one and sees its messages.
		var (environment2, output2) = Setup();
		var runner2 = new CliRunner(environment2, output2, new StringWriter());
		var records = await RunAsync(
			runner2,
			Arguments(),
			string.Join(
				'\n',
				$$"""{"id":"1","type":"switch_session","sessionPath":{{CliTestJson.String(file)}}}""",
				"""{"id":"2","type":"get_messages"}""",
				"""{"id":"3","type":"exit"}"""),
			output2);

		Assert.True(ResponseFor(records, "switch_session")["success"]!.GetValue<bool>());
		Assert.False(ResponseFor(records, "switch_session")["data"]!["cancelled"]!.GetValue<bool>());
		// 两次提问两次回复（系统提示词不在会话消息里）。
		Assert.Equal(4, ResponseFor(records, "get_messages")["data"]!["messages"]!.AsArray().Count);

		// A missing file fails with a clear error rather than crashing.
		var (environment3, output3) = Setup();
		var runner3 = new CliRunner(environment3, output3, new StringWriter());
		var bad = await RunAsync(
			runner3,
			Arguments(),
			string.Join(
				'\n',
				"""{"id":"1","type":"switch_session","sessionPath":"/nope/missing.jsonl"}""",
				"""{"id":"2","type":"exit"}"""),
			output3);

		Assert.False(ResponseFor(bad, "switch_session")["success"]!.GetValue<bool>());
	}

	[Fact]
	public async Task CycleModelAndThinkingLevelMoveToTheNextOption()
	{
		var (environment, output) = Setup();
		var runner = new CliRunner(environment, output, new StringWriter());

		var records = await RunAsync(
			runner,
			Arguments(),
			string.Join(
				'\n',
				"""{"id":"1","type":"cycle_model"}""",
				"""{"id":"2","type":"cycle_thinking_level"}""",
				"""{"id":"3","type":"get_state"}""",
				"""{"id":"4","type":"exit"}"""),
			output);

		var cycled = ResponseFor(records, "cycle_model")["data"]!;
		Assert.NotEqual("test-model", cycled["model"]!["id"]!.GetValue<string>());
		Assert.False(cycled["isScoped"]!.GetValue<bool>());
		Assert.Equal(
			cycled["model"]!["id"]!.GetValue<string>(),
			ResponseFor(records, "get_state")["data"]!["model"]!["id"]!.GetValue<string>());

		// The model supports reasoning, so the level moves off "off".
		Assert.NotEqual("off", ResponseFor(records, "cycle_thinking_level")["data"]!["level"]!.GetValue<string>());
	}

	[Fact]
	public async Task GetCommandsListsPromptTemplatesAndSkills()
	{
		var promptDir = Path.Combine(_root, ".pi", "prompts");
		Directory.CreateDirectory(promptDir);
		File.WriteAllText(Path.Combine(promptDir, "fix-tests.md"), "Fix the failing tests in $ARGUMENTS");

		var skillDir = Path.Combine(_root, ".pi", "skills", "reviewer");
		Directory.CreateDirectory(skillDir);
		File.WriteAllText(
			Path.Combine(skillDir, "SKILL.md"),
			"---\nname: reviewer\ndescription: Review a diff\n---\nReview the diff.");

		var (environment, output) = Setup();
		var runner = new CliRunner(environment, output, new StringWriter());

		// Discovery is opt-in for tests, so these arguments do not pass the --no-* switches.
		var arguments = CliArguments.Parse(
		[
			"--mode", "rpc",
			"--model", "test/test-model",
			"--session-dir", SessionsDir,
			"--no-context-files",
			"--no-session",
		]).Arguments!;

		var records = await RunAsync(
			runner,
			arguments,
			string.Join('\n', """{"id":"1","type":"get_commands"}""", """{"id":"2","type":"exit"}"""),
			output);

		var commands = ResponseFor(records, "get_commands")["data"]!["commands"]!.AsArray();

		// Discovery also sees the real user-level resources on this machine, so match by name.
		var prompt = commands.Single(static node => node!["name"]!.GetValue<string>() == "fix-tests");
		Assert.Equal("prompt", prompt!["source"]!.GetValue<string>());
		Assert.Equal("project", prompt["sourceInfo"]!["scope"]!.GetValue<string>());
		Assert.EndsWith("fix-tests.md", prompt["sourceInfo"]!["path"]!.GetValue<string>());

		var skill = commands.Single(static node => node!["name"]!.GetValue<string>() == "skill:reviewer");
		Assert.Equal("skill", skill!["source"]!.GetValue<string>());
		Assert.Equal("Review a diff", skill["description"]!.GetValue<string>());
	}

	[Fact]
	public async Task ExportHtmlWritesASelfContainedFile()
	{
		var (environment, output) = Setup();
		var runner = new CliRunner(environment, output, new StringWriter());
		var target = Path.Combine(_root, "export", "session.html");

		var records = await RunAsync(
			runner,
			Arguments(),
			string.Join(
				'\n',
				Seed(),
				$$"""{"id":"1","type":"export_html","outputPath":{{CliTestJson.String(target)}}}""",
				"""{"id":"2","type":"exit"}"""),
			output);

		var path = ResponseFor(records, "export_html")["data"]!["path"]!.GetValue<string>();
		Assert.Equal(target, path);
		Assert.True(File.Exists(path));

		var html = await File.ReadAllTextAsync(path);
		Assert.StartsWith("<!DOCTYPE html>", html);
		Assert.Contains("second question", html);
		Assert.Contains("second answer", html);
		Assert.DoesNotContain("<script", html);
	}

	[Fact]
	public async Task RetryAndExtensionUiCommandsAreAccepted()
	{
		var (environment, output) = Setup();
		var runner = new CliRunner(environment, output, new StringWriter());

		var records = await RunAsync(
			runner,
			Arguments("--no-session"),
			string.Join(
				'\n',
				"""{"id":"1","type":"set_auto_retry","enabled":true}""",
				"""{"id":"2","type":"abort_retry"}""",
				"""{"type":"extension_ui_response","id":"ui-1","value":"ok"}""",
				"""{"id":"3","type":"exit"}"""),
			output);

		Assert.True(ResponseFor(records, "set_auto_retry")["success"]!.GetValue<bool>());
		Assert.True(ResponseFor(records, "abort_retry")["success"]!.GetValue<bool>());

		// The extension UI answer is accepted silently: no response is written for it.
		Assert.DoesNotContain(records, static node =>
			node["type"]?.GetValue<string>() == "response" &&
			node["command"]?.GetValue<string>() == "extension_ui_response");
	}
}

/// <summary>Escapes a string as a JSON literal. Written by hand so the test assembly stays trimming clean.</summary>
internal static class CliTestJson
{
	public static string String(string value) => JsonValue.Create(value)!.ToJsonString();
}
