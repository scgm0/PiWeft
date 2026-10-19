using System.Text.Json.Nodes;
using PiWeft.Agent;
using PiWeft.Ai;
using PiWeft.Cli;
using PiWeft.CodingAgent;
using PiWeft.CodingAgent.Session;
using PiWeft.CodingAgent.Settings;
using PiWeft.CodingAgent.Tools;
using Xunit;

namespace PiWeft.Cli.Tests;

public class AgentEventWriterTests
{
	private static AssistantMessage Assistant(params ContentBlock[] content) => new()
	{
		Api = "anthropic-messages",
		Provider = "anthropic",
		Model = "m",
		Content = content,
		StopReason = StopReason.Stop,
		Usage = new Usage { Input = 3, Output = 1, TotalTokens = 4 },
		Timestamp = 42,
	};

	[Fact]
	public void AgentTurnAndMessageEventsMatchPiWireShape()
	{
		Assert.Equal("agent_start", JsonNode.Parse(AgentEventWriter.Write(new AgentStartEvent()))!["type"]!.GetValue<string>());
		Assert.Equal("turn_start", JsonNode.Parse(AgentEventWriter.Write(new TurnStartEvent()))!["type"]!.GetValue<string>());

		var end = JsonNode.Parse(AgentEventWriter.Write(new AgentEndEvent
		{
			Messages = [Assistant(new TextContent { Text = "done" })],
		}))!;

		Assert.Equal("agent_end", end["type"]!.GetValue<string>());
		Assert.False(end["willRetry"]!.GetValue<bool>());
		Assert.Equal("assistant", end["messages"]![0]!["role"]!.GetValue<string>());

		var turnEnd = JsonNode.Parse(AgentEventWriter.Write(new TurnEndEvent
		{
			Message = Assistant(new TextContent { Text = "done" }),
			ToolResults = [],
		}))!;

		Assert.Equal("turn_end", turnEnd["type"]!.GetValue<string>());
		Assert.Empty(turnEnd["toolResults"]!.AsArray());

		var partial = Assistant(new TextContent { Text = "hello" });
		var update = JsonNode.Parse(AgentEventWriter.Write(new MessageUpdateEvent
		{
			Message = partial,
			AssistantMessageEvent = new TextDeltaEvent { ContentIndex = 0, Delta = "hello", Partial = partial },
		}))!;

		Assert.Equal("message_update", update["type"]!.GetValue<string>());
		// The cumulative message is not on the wire; usage and the nested delta are.
		Assert.False(update.AsObject().ContainsKey("message"));
		Assert.Equal(4, update["usage"]!["totalTokens"]!.GetValue<int>());

		var nested = update["assistantMessageEvent"]!;
		Assert.Equal("text_delta", nested["type"]!.GetValue<string>());
		Assert.Equal(0, nested["contentIndex"]!.GetValue<int>());
		Assert.Equal("hello", nested["delta"]!.GetValue<string>());
		Assert.False(nested.AsObject().ContainsKey("partial"));

		string Write(AssistantMessageEvent @event) => AgentEventWriter.Write(new MessageUpdateEvent
		{
			Message = partial,
			AssistantMessageEvent = @event,
		});

		Assert.Equal("start", JsonNode.Parse(Write(new StartEvent { Partial = partial }))!["assistantMessageEvent"]!["type"]!.GetValue<string>());

		var toolCall = new ToolCall { Id = "c1", Name = "read", Arguments = new JsonObject { ["path"] = "a" } };
		var toolEnd = JsonNode.Parse(Write(new ToolCallEndEvent
		{
			ContentIndex = 1,
			ToolCall = toolCall,
			Partial = partial,
		}))!["assistantMessageEvent"]!;

		Assert.Equal("toolcall_end", toolEnd["type"]!.GetValue<string>());
		Assert.Equal(1, toolEnd["contentIndex"]!.GetValue<int>());
		Assert.Equal("read", toolEnd["toolCall"]!["name"]!.GetValue<string>());
		Assert.Equal("a", toolEnd["toolCall"]!["arguments"]!["path"]!.GetValue<string>());

		Assert.Equal("thinking_end", JsonNode.Parse(Write(new ThinkingEndEvent
		{
			ContentIndex = 0,
			Content = "thought",
			Partial = partial,
		}))!["assistantMessageEvent"]!["type"]!.GetValue<string>());

		var messageEnd = JsonNode.Parse(AgentEventWriter.Write(new MessageEndEvent
		{
			Message = new ToolResultMessage
			{
				ToolCallId = "c1",
				ToolName = "read",
				Content = [new TextContent { Text = "contents" }],
				Timestamp = 7,
			},
		}))!;

		Assert.Equal("message_end", messageEnd["type"]!.GetValue<string>());
		Assert.Equal("toolResult", messageEnd["message"]!["role"]!.GetValue<string>());
		Assert.Equal("c1", messageEnd["message"]!["toolCallId"]!.GetValue<string>());
	}

	[Theory]
	[InlineData("tool_execution_start")]
	[InlineData("message_end")]
	[InlineData("toolcall_end")]
	public void ToolArgumentsKeepReadableChineseAndJsonQuoteEscapes(string eventType)
	{
		const string command = "echo \"你好 <tag> & 'quote'\"";
		var call = new ToolCall { Id = "c1", Name = "bash", Arguments = new JsonObject { ["command"] = command } };
		var message = Assistant(call);
		AgentEvent @event = eventType switch
		{
			"tool_execution_start" => new ToolExecutionStartEvent { ToolCallId = call.Id, ToolName = call.Name, Arguments = call.Arguments },
			"message_end" => new MessageEndEvent { Message = message },
			_ => new MessageUpdateEvent
			{
				Message = message,
				AssistantMessageEvent = new ToolCallEndEvent { ContentIndex = 0, ToolCall = call, Partial = message },
			},
		};

		var json = AgentEventWriter.Write(@event);

		Assert.Contains("你好", json);
		Assert.Contains("\\\"你好", json);
		Assert.DoesNotContain("\\u", json);
		var record = JsonNode.Parse(json)!;
		var arguments = eventType switch
		{
			"tool_execution_start" => record["args"],
			"message_end" => record["message"]!["content"]![0]!["arguments"],
			_ => record["assistantMessageEvent"]!["toolCall"]!["arguments"],
		};
		Assert.Equal(command, arguments!["command"]!.GetValue<string>());
	}

}

public class CliRunnerTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-cli-" + Guid.NewGuid().ToString("N")[..8]);

	public CliRunnerTests() => Directory.CreateDirectory(_root);

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	private (CliEnvironment Environment, ScriptedStreamFn Script) Environment()
	{
		var script = new ScriptedStreamFn();
		var model = TestModels.Model();
		var registry = new ModelRegistry();
		registry.Add(model);

		var environment = CliEnvironment.ForTests(
			registry,
			new CodingAgentSettings(),
			model,
			[.. BuiltinTools.Tools(_root)],
			script.Invoke);

		return (environment, script);
	}

	private (CliRunner Runner, StringWriter Output, StringWriter Error) RunnerFor(CliEnvironment environment)
	{
		var output = new StringWriter();
		var error = new StringWriter();
		return (new CliRunner(environment, output, error), output, error);
	}

	private CliArguments Arguments(params string[] argv) => CliArguments.Parse(argv).Arguments!;

	[Fact]
	public void ExtensionUiFireAndForgetMatchesPiWireShape()
	{
		var (environment, _) = Environment();
		var (runner, output, _) = RunnerFor(environment);
		var host = (IUiHost)runner;

		host.SetStatus(new UiStatusRequest("build", "compiling"));
		host.SetStatus(new UiStatusRequest("build", null));
		host.SetWidget(new UiWidgetRequest("plan", ["step 1", "step 2"], "aboveEditor"));
		host.SetTitle(new UiTitleRequest("release"));
		host.SetEditorText("draft");

		var records = output.ToString()
			.Split('\n', StringSplitOptions.RemoveEmptyEntries)
			.Select(static line => JsonNode.Parse(line)!)
			.ToArray();
		Assert.Equal(5, records.Length);

		Assert.Equal("extension_ui_request", records[0]["type"]!.GetValue<string>());
		Assert.Equal("setStatus", records[0]["method"]!.GetValue<string>());
		Assert.Equal("build", records[0]["statusKey"]!.GetValue<string>());
		Assert.Equal("compiling", records[0]["statusText"]!.GetValue<string>());

		// 清除状态时不带 statusText。
		Assert.Equal("setStatus", records[1]["method"]!.GetValue<string>());
		Assert.False(records[1].AsObject().ContainsKey("statusText"));

		Assert.Equal("setWidget", records[2]["method"]!.GetValue<string>());
		Assert.Equal("plan", records[2]["widgetKey"]!.GetValue<string>());
		Assert.Equal(["step 1", "step 2"], records[2]["widgetLines"]!.AsArray().Select(static line => line!.GetValue<string>()));
		Assert.Equal("aboveEditor", records[2]["widgetPlacement"]!.GetValue<string>());

		Assert.Equal("setTitle", records[3]["method"]!.GetValue<string>());
		Assert.Equal("release", records[3]["title"]!.GetValue<string>());

		Assert.Equal("set_editor_text", records[4]["method"]!.GetValue<string>());
		Assert.Equal("draft", records[4]["text"]!.GetValue<string>());
	}

	[Theory]
	[InlineData("off")]
	[InlineData("high")]
	public async Task DesktopOpeningPreservesTheFileAndHistoricalResponseDuration(string requestedLevel)
	{
		var (environment, script) = Environment();
		var manager = SessionManager.Create(_root, Path.Combine(_root, "sessions"));
		manager.AppendThinkingLevelChange("off");
		manager.AppendMessage(new UserMessage { Content = MessageContent.FromText("你好") });
		manager.AppendMessage(new AssistantMessage
		{
			Api = environment.Model.Api,
			Provider = environment.Model.Provider,
			Model = environment.Model.Id,
			Content = [new ThinkingContent { Thinking = "检查历史耗时" }, new TextContent { Text = "完成" }],
			DurationMs = 2345.5,
			Timestamp = 100,
		});
		var path = manager.SessionFile!;
		var before = File.ReadAllBytes(path);
		var modifiedAt = File.GetLastWriteTimeUtc(path);
		using var session = environment.CreateSession(
			Arguments("--thinking", requestedLevel, "--no-skills", "--no-prompt-templates"),
			_root,
			session: SessionManager.Open(path, _root),
			recordInitialThinkingLevel: false);

		Assert.Equal(before, File.ReadAllBytes(path));
		Assert.Equal(modifiedAt, File.GetLastWriteTimeUtc(path));
		Assert.Equal(2345.5, Assert.IsType<AssistantMessage>(session.Session.BuildProjectedMessages().Last()).DurationMs);

		script.Enqueue("继续完成");
		await session.PromptAsync("继续");

		var reopened = SessionManager.Open(path, _root);
		Assert.Equal(requestedLevel, reopened.BuildSessionContext().ThinkingLevel);
		Assert.Equal(2345.5, reopened.BuildProjectedMessages().OfType<AssistantMessage>().First().DurationMs);
		Assert.Equal(requestedLevel == "off" ? 1 : 2, reopened.Entries.OfType<ThinkingLevelChangeEntry>().Count());
	}

	[Fact]
	public async Task PrintMode_WritesTheAssistantText()
	{
		var (environment, script) = Environment();
		script.Enqueue("Hello from print mode");
		var (runner, output, _) = RunnerFor(environment);

		var exitCode = await runner.RunAsync(Arguments("--print", "--no-session", "hi"), _root, new StringReader(""), CancellationToken.None);

		Assert.Equal(0, exitCode);
		Assert.Contains("Hello from print mode", output.ToString());
	}

	[Fact]
	public async Task JsonMode_WritesAHeaderThenEventsThenSettled()
	{
		var (environment, script) = Environment();
		script.Enqueue("json answer");
		var (runner, output, _) = RunnerFor(environment);

		await runner.RunAsync(Arguments("--mode", "json", "--no-session", "hi"), _root, new StringReader(""), CancellationToken.None);

		var records = output.ToString()
			.Split('\n', StringSplitOptions.RemoveEmptyEntries)
			.Select(static line => JsonNode.Parse(line)!)
			.ToList();

		Assert.Equal("session", records[0]!["type"]!.GetValue<string>());
		Assert.Equal(3, records[0]!["version"]!.GetValue<int>());
		Assert.Equal(_root, records[0]!["cwd"]!.GetValue<string>());

		var types = records.Select(static record => record["type"]!.GetValue<string>()).ToList();
		Assert.Contains("agent_start", types);
		Assert.Contains("message_end", types);
		Assert.Equal("agent_settled", types[^1]);

		var messageEnd = records.Last(static record => record["type"]!.GetValue<string>() == "message_end");
		Assert.Equal("json answer", messageEnd["message"]!["content"]![0]!["text"]!.GetValue<string>());
	}

	[Fact]
	public async Task RpcMode_HandlesCommandsAndStreamsEvents()
	{
		var (environment, script) = Environment();
		script.Enqueue("rpc answer");
		var (runner, output, _) = RunnerFor(environment);

		var commands = string.Join(
			'\n',
			"""{"id":"1","type":"get_state"}""",
			"""{"id":"2","type":"prompt","message":"hello"}""",
			"""{"id":"3","type":"get_available_thinking_levels"}""",
			"""{"id":"4","type":"bogus"}""",
			"not json",
			"""{"id":"5","type":"exit"}""");

		await runner.RunAsync(Arguments("--mode", "rpc", "--no-session"), _root, new StringReader(commands), CancellationToken.None);

		var records = await WaitForAsync(
			output,
			static text => text.Contains("\"type\":\"agent_end\""));

		var responses = records
			.Where(static node => node["type"]!.GetValue<string>() == "response")
			.ToList();

		var state = responses.Single(static r => r["command"]!.GetValue<string>() == "get_state");
		Assert.True(state["success"]!.GetValue<bool>());
		Assert.Equal("test-model", state["data"]!["model"]!["id"]!.GetValue<string>());

		var prompt = responses.Single(static r => r["command"]!.GetValue<string>() == "prompt");
		Assert.True(prompt["success"]!.GetValue<bool>());
		Assert.Equal("started", prompt["data"]!["disposition"]!.GetValue<string>());

		var thinking = responses.Single(static r => r["command"]!.GetValue<string>() == "get_available_thinking_levels");
		Assert.Contains("high", thinking["data"]!["levels"]!.AsArray().Select(static level => level!.GetValue<string>()));

		var unknown = responses.Single(static r => r["command"]!.GetValue<string>() == "bogus");
		Assert.False(unknown["success"]!.GetValue<bool>());
		Assert.Contains("Unsupported command", unknown["error"]!.GetValue<string>());

		var invalid = responses.Single(static r => r["command"]!.GetValue<string>() == "unknown");
		Assert.False(invalid["success"]!.GetValue<bool>());

		// The run streamed its message events into the same stdout stream.
		var types = records.Select(static record => record["type"]!.GetValue<string>()).ToList();
		Assert.Contains("agent_start", types);
		Assert.Contains("message_end", types);
		Assert.Contains("agent_end", types);

		var messageEnd = records.Last(static record => record["type"]!.GetValue<string>() == "message_end");
		Assert.Equal("rpc answer", messageEnd["message"]!["content"]![0]!["text"]!.GetValue<string>());
	}

	[Fact]
	public async Task RpcMode_RejectsPromptsWhileStreaming()
	{
		var (environment, script) = Environment();
		script.Enqueue("first");
		var (runner, output, _) = RunnerFor(environment);

		var commands = string.Join(
			'\n',
			"""{"id":"1","type":"prompt","message":"one"}""",
			"""{"id":"2","type":"prompt","message":"two"}""",
			"""{"id":"3","type":"exit"}""");

		await runner.RunAsync(Arguments("--mode", "rpc", "--no-session"), _root, new StringReader(commands), CancellationToken.None);
		await WaitForAsync(output, static text => text.Contains("\"id\":\"2\""));

		var records = Parse(output);
		var second = records.Single(static node =>
			node["type"]!.GetValue<string>() == "response" && node["id"]?.GetValue<string>() == "2");

		// Whether the first run already finished is timing dependent; either a queue is required or it started.
		Assert.True(second["success"]!.GetValue<bool>() || second["error"]!.GetValue<string>().Contains("streaming"));
	}

	[Fact]
	public async Task RpcMode_SetThinkingLevelAndCompact()
	{
		var (environment, script) = Environment();
		script.Enqueue("compaction summary");
		var (runner, output, _) = RunnerFor(environment);

		var commands = string.Join(
			'\n',
			"""{"id":"1","type":"set_thinking_level","thinkingLevel":"high"}""",
			"""{"id":"2","type":"compact"}""",
			"""{"id":"3","type":"exit"}""");

		await runner.RunAsync(Arguments("--mode", "rpc", "--no-session"), _root, new StringReader(commands), CancellationToken.None);

		var responses = Parse(output).Where(static node => node["type"]!.GetValue<string>() == "response").ToList();
		Assert.Equal("high", responses[0]!["data"]!["thinkingLevel"]!.GetValue<string>());
		Assert.True(responses[1]!["success"]!.GetValue<bool>());
	}

	/// <summary>Builds an environment whose tool set comes from the arguments, like the real startup path.</summary>
	private CliEnvironment EnvironmentWithTools(CliArguments arguments)
	{
		var model = TestModels.Model();
		var registry = new ModelRegistry();
		registry.Add(model);

		return CliEnvironment.ForTests(
			registry,
			new CodingAgentSettings(),
			model,
			CliEnvironment.ResolveTools(arguments, _root),
			new ScriptedStreamFn().Invoke);
	}

	[Fact]
	public void CreateSession_AppendsExtraToolsAndHonorsMcpPatterns()
	{
		var extra = new List<AgentTool>
		{
			new()
			{
				Name = "mcp__fixture__echo",
				Label = "mcp__fixture__echo",
				Description = "echo",
				Execute = _ => Task.FromResult(AgentToolResult.Text("ok")),
			},
		};

		var all = Arguments("--no-session");
		using var session = EnvironmentWithTools(all).CreateSession(all, _root, extraTools: extra);

		Assert.Contains(session.Tools, static tool => tool.Name == "read");
		Assert.Contains(session.Tools, static tool => tool.Name == "mcp__fixture__echo");

		// A --tools entry that does not start with mcp__ must not remove MCP tools.
		var readOnly = Arguments("--no-session", "-t", "read");
		using var filtered = EnvironmentWithTools(readOnly).CreateSession(readOnly, _root, extraTools: extra);

		Assert.Equal(["read"], filtered.Tools.Where(static t => !t.Name.StartsWith("mcp__", StringComparison.Ordinal)).Select(static t => t.Name));
		Assert.Contains(filtered.Tools, static tool => tool.Name == "mcp__fixture__echo");

		// An explicit mcp__ pattern does select them.
		var mcpOnly = Arguments("--no-session", "-t", "mcp__fixture__*");
		using var selected = EnvironmentWithTools(mcpOnly).CreateSession(mcpOnly, _root, extraTools: extra);

		Assert.Equal(["mcp__fixture__echo"], selected.Tools.Select(static t => t.Name));

		// And an mcp__ denylist removes them.
		var noMcp = Arguments("--no-session", "-xt", "mcp__fixture__*");
		using var denied = EnvironmentWithTools(noMcp).CreateSession(noMcp, _root, extraTools: extra);

		Assert.DoesNotContain(denied.Tools, static tool => tool.Name.StartsWith("mcp__", StringComparison.Ordinal));
		Assert.Contains(denied.Tools, static tool => tool.Name == "read");
	}

	[Fact]
	public async Task PrintMode_UsesTheSystemPromptOverride()
	{
		var (environment, script) = Environment();
		script.Enqueue("ok");
		var (runner, _, _) = RunnerFor(environment);

		await runner.RunAsync(
			Arguments("--print", "--no-session", "--system-prompt", "You are terse", "hi"),
			_root,
			new StringReader(string.Empty),
			CancellationToken.None);

		var system = script.Requests[0].OfType<SystemMessage>().First();
		Assert.Equal("You are terse", system.Sections!["preamble"]);
	}

	[Fact]
	public async Task PrintMode_ExpandsFileArgumentsIntoThePrompt()
	{
		var file = Path.Combine(_root, "notes.md");
		File.WriteAllText(file, "IMPORTANT CONTEXT");
		var (environment, script) = Environment();
		script.Enqueue("ok");
		var (runner, _, _) = RunnerFor(environment);

		var exitCode = await runner.RunAsync(
			Arguments("--print", "--no-session", "@" + file, "explain"),
			_root,
			new StringReader(string.Empty),
			CancellationToken.None);

		Assert.Equal(0, exitCode);
		var user = script.Requests[0].OfType<UserMessage>().First();
		Assert.Contains($"<file name=\"{file}\">", user.Content.Text!);
		Assert.Contains("IMPORTANT CONTEXT", user.Content.Text!);
		Assert.EndsWith("explain", user.Content.Text!);
	}

	[Fact]
	public async Task RpcMode_PreservesImagesOnIdlePrompt()
	{
		var (environment, script) = Environment();
		script.Enqueue("seen");
		var (runner, output, _) = RunnerFor(environment);

		var commands = string.Join(
			'\n',
			"""{"id":"1","type":"prompt","message":"describe","images":[{"data":"QUJD","mimeType":"image/png"}]}""",
			"""{"id":"2","type":"exit"}""");

		await runner.RunAsync(Arguments("--mode", "rpc", "--no-session"), _root, new StringReader(commands), CancellationToken.None);
		await WaitForAsync(output, static text => text.Contains("\"type\":\"agent_end\""));

		var user = script.Requests
			.SelectMany(static request => request)
			.OfType<UserMessage>()
			.First(static message => message.Content.Blocks is { Count: > 0 });

		Assert.Equal("describe", Assert.IsType<TextContent>(user.Content.Blocks![0]).Text);
		Assert.Equal("QUJD", Assert.IsType<ImageContent>(user.Content.Blocks![1]).Data);
	}

	[Fact]
	public async Task RpcMode_RejectsFileArguments()
	{
		var (environment, _) = Environment();
		var (runner, _, error) = RunnerFor(environment);

		var exitCode = await runner.RunAsync(
			Arguments("--mode", "rpc", "--no-session", "@x.md"),
			_root,
			new StringReader(string.Empty),
			CancellationToken.None);

		Assert.Equal(1, exitCode);
		Assert.Contains("not supported in RPC mode", error.ToString());
	}

	[Fact]
	public async Task PrintMode_ExitsNonZeroWhenTheProviderFails()
	{
		var (environment, script) = Environment();
		script.EnqueueError("upstream is down");
		var (runner, _, error) = RunnerFor(environment);

		var exitCode = await runner.RunAsync(
			Arguments("--print", "--no-session", "hi"),
			_root,
			new StringReader(string.Empty),
			CancellationToken.None);

		Assert.Equal(1, exitCode);
		Assert.Contains("upstream is down", error.ToString());
	}

	[Fact]
	public async Task ExportMode_WritesSelfContainedHtml()
	{
		var (environment, _) = Environment();
		var manager = SessionManager.Create(_root, Path.Combine(_root, "sessions"));
		manager.AppendMessage(new UserMessage { Content = MessageContent.FromText("hello export"), Timestamp = 1 });
		manager.AppendMessage(new AssistantMessage
		{
			Api = environment.Model.Api,
			Provider = environment.Model.Provider,
			Model = environment.Model.Id,
			Content = [new TextContent { Text = "exported answer" }],
			StopReason = StopReason.Stop,
			Timestamp = 2,
		});

		var outputPath = Path.Combine(_root, "export.html");
		var (runner, output, _) = RunnerFor(environment);

		var exitCode = await runner.RunAsync(
			Arguments("--export", manager.SessionFile!, outputPath),
			_root,
			new StringReader(string.Empty),
			CancellationToken.None);

		Assert.Equal(0, exitCode);
		Assert.True(File.Exists(outputPath));
		Assert.Contains("exported answer", File.ReadAllText(outputPath));
		Assert.Contains("Exported to:", output.ToString());
	}

	[Fact]
	public async Task ExportMode_ReportsMissingSessionWithoutCrashing()
	{
		var (environment, _) = Environment();
		var (runner, _, error) = RunnerFor(environment);

		var exitCode = await runner.RunAsync(
			Arguments("--export", "/nonexistent/session.jsonl"),
			_root,
			new StringReader(string.Empty),
			CancellationToken.None);

		Assert.Equal(1, exitCode);
		Assert.Contains("Session not found", error.ToString());
	}

	[Fact]
	public async Task RpcMode_AnswersAskQuestionThroughExtensionUiProtocol()
	{
		var (environment, script) = Environment();
		script.EnqueueToolCall("call-1", "ask_question", new JsonObject
		{
			["questions"] = new JsonArray(new JsonObject
			{
				["question"] = "选哪个？",
				["options"] = new JsonArray("A", "B"),
			}),
		});
		script.Enqueue("done");
		var (runner, output, _) = RunnerFor(environment);

		using var reader = new ChannelTextReader();
		var run = runner.RunAsync(Arguments("--mode", "rpc", "--no-session"), _root, reader, CancellationToken.None);

		reader.Write("""{"id":"1","type":"prompt","message":"ask me"}""");

		// 等待工具发出 extension_ui_request，再用 extension_ui_response 回答。
		string? requestId = null;
		for (var attempt = 0; attempt < 500 && requestId is null; attempt++)
		{
			var request = Parse(output).FirstOrDefault(static node =>
				node["type"]?.GetValue<string>() == "extension_ui_request" &&
				node["method"]?.GetValue<string>() == "input");
			if (request is not null)
			{
				Assert.Equal(AskQuestions.EnvelopePlaceholder, request["placeholder"]!.GetValue<string>());
				Assert.NotNull(AskQuestions.ParseEnvelope(request["title"]!.GetValue<string>()));
				requestId = request["id"]!.GetValue<string>();
				break;
			}

			await Task.Delay(10);
		}

		Assert.NotNull(requestId);
		var response = AskQuestions.EncodeResponse(
			[new AskAnswer("q1", "select", JsonValue.Create("A"), "A", false)],
			cancelled: false);
		reader.Write(new JsonObject
		{
			["type"] = "extension_ui_response",
			["id"] = requestId,
			["value"] = response,
		}.ToJsonString());
		reader.Write("""{"id":"2","type":"exit"}""");
		reader.Complete();

		var exitCode = await run;
		Assert.Equal(0, exitCode);

		var toolResult = Parse(output).Last(static node =>
			node["type"]?.GetValue<string>() == "message_end" &&
			node["message"]?["role"]?.GetValue<string>() == "toolResult");
		Assert.Contains("q1: A", toolResult["message"]!["content"]![0]!["text"]!.GetValue<string>());
	}

	[Fact]
	public void CreateSession_ProjectTrustGatesProjectSkills()
	{
		var skillDir = Path.Combine(_root, ".pi", "skills", "project-skill");
		Directory.CreateDirectory(skillDir);
		File.WriteAllText(
			Path.Combine(skillDir, "SKILL.md"),
			"---\nname: project-skill\ndescription: project scoped\n---\n\nbody\n");

		var (environment, _) = Environment();

		using var trusted = environment.CreateSession(Arguments("--no-session", "--approve"), _root);
		Assert.True(trusted.ProjectTrusted);
		Assert.Contains(trusted.DiscoveredSkills, static skill => skill.Name == "project-skill");

		using var untrusted = environment.CreateSession(Arguments("--no-session", "--no-approve"), _root);
		Assert.False(untrusted.ProjectTrusted);
		Assert.DoesNotContain(untrusted.DiscoveredSkills, static skill => skill.Name == "project-skill");
	}

	[Fact]
	public async Task RpcMode_TogglesPlanMode()
	{
		var (environment, script) = Environment();
		script.Enqueue("ok");
		var (runner, output, _) = RunnerFor(environment);

		var commands = string.Join(
			'\n',
			"""{"id":"1","type":"set_plan_mode","enabled":true}""",
			"""{"id":"2","type":"get_state"}""",
			"""{"id":"3","type":"set_plan_mode","enabled":false}""",
			"""{"id":"4","type":"exit"}""");

		var exitCode = await runner.RunAsync(Arguments("--mode", "rpc", "--no-session"), _root, new StringReader(commands), CancellationToken.None);
		Assert.Equal(0, exitCode);

		var responses = Parse(output).Where(static node => node["type"]!.GetValue<string>() == "response").ToList();
		Assert.True(responses.Single(static r => r["id"]!.GetValue<string>() == "1")["data"]!["enabled"]!.GetValue<bool>());
		Assert.True(responses.Single(static r => r["command"]!.GetValue<string>() == "get_state")["data"]!["planMode"]!.GetValue<bool>());
		Assert.False(responses.Single(static r => r["id"]!.GetValue<string>() == "3")["data"]!["enabled"]!.GetValue<bool>());
	}

	[Fact]
	public async Task RpcMode_TestsModelConnection()
	{
		var (environment, script) = Environment();
		script.Enqueue("pong");
		var (runner, output, _) = RunnerFor(environment);

		var commands = string.Join(
			'\n',
			"""{"id":"1","type":"test_model","modelId":"test-model"}""",
			"""{"id":"2","type":"exit"}""");

		await runner.RunAsync(Arguments("--mode", "rpc", "--no-session"), _root, new StringReader(commands), CancellationToken.None);

		var response = Parse(output).Single(static node =>
			node["type"]!.GetValue<string>() == "response" && node["id"]?.GetValue<string>() == "1");

		Assert.True(response["success"]!.GetValue<bool>());
		Assert.True(response["data"]!["ok"]!.GetValue<bool>());
		Assert.NotNull(response["data"]!["usage"]);
	}

	[Fact]
	public async Task RpcMode_FetchRemoteModelsReportsUnsupportedApi()
	{
		var (environment, _) = Environment();
		var (runner, output, _) = RunnerFor(environment);

		var commands = string.Join(
			'\n',
			"""{"id":"1","type":"fetch_remote_models","modelId":"test-model","api":"bedrock-converse-stream"}""",
			"""{"id":"2","type":"exit"}""");

		await runner.RunAsync(Arguments("--mode", "rpc", "--no-session"), _root, new StringReader(commands), CancellationToken.None);

		var response = Parse(output).Single(static node =>
			node["type"]!.GetValue<string>() == "response" && node["id"]?.GetValue<string>() == "1");

		Assert.True(response["success"]!.GetValue<bool>());
		Assert.False(response["data"]!["ok"]!.GetValue<bool>());
		Assert.Contains("不支持", response["data"]!["error"]!.GetValue<string>());
	}

	[Fact]
	public async Task RpcMode_ListsAndReconnectsMcpServers()
	{
		// 整台 hidden 的服务器不会启动进程，但会出现在状态里，用来验证重连路径不需真实子进程。
		var project = Path.Combine(_root, ".pi");
		Directory.CreateDirectory(project);
		File.WriteAllText(
			Path.Combine(project, "mcp.json"),
			"""{ "mcpServers": { "quiet": { "command": "/nonexistent/server", "exposure": "hidden" } } }""");

		var (environment, _) = Environment();
		var (runner, output, _) = RunnerFor(environment);

		var commands = string.Join(
			'\n',
			"""{"id":"1","type":"list_mcp"}""",
			"""{"id":"2","type":"reconnect_mcp"}""",
			"""{"id":"3","type":"exit"}""");

		await runner.RunAsync(Arguments("--mode", "rpc", "--no-session"), _root, new StringReader(commands), CancellationToken.None);

		var parsed = Parse(output);
		foreach (var id in new[] { "1", "2" })
		{
			var response = parsed.Single(node =>
				node["type"]!.GetValue<string>() == "response" && node["id"]?.GetValue<string>() == id);
			Assert.True(response["success"]!.GetValue<bool>());
			Assert.True(response["data"]!["connected"]!.GetValue<bool>());
			Assert.Empty(response["data"]!["errors"]!.AsArray());

			var server = Assert.Single(response["data"]!["servers"]!.AsArray());
			Assert.Equal("quiet", server!["name"]!.GetValue<string>());
			Assert.False(server["connected"]!.GetValue<bool>());
			Assert.Equal("hidden", server["error"]!.GetValue<string>());
		}
	}

	[Fact]
	public async Task RpcMode_ReportsCrossSessionUsageStats()
	{
		// 会话目录布局与生产一致：<root>/<project>/<file>.jsonl。
		var sessions = Path.Combine(_root, "sessions");
		var project = Path.Combine(sessions, "--work-alpha--");
		Directory.CreateDirectory(project);
		File.WriteAllLines(Path.Combine(project, "2026-01-02T00-00-00-000Z_s1.jsonl"),
		[
			"""{"type":"session","version":3,"id":"s1","timestamp":"2026-01-02T00:00:00.000Z","cwd":"/work/alpha"}""",
			"""{"type":"message","id":"m1","timestamp":"2026-01-02T00:00:01.000Z","message":{"role":"assistant","provider":"test","model":"test-model","usage":{"input":100,"output":10,"cacheRead":5,"cacheWrite":0,"cost":{"total":0.25}}}}""",
		]);

		var (environment, _) = Environment();
		var (runner, output, _) = RunnerFor(environment);

		var commands = string.Join(
			'\n',
			"""{"id":"1","type":"get_usage_stats","weeks":4}""",
			"""{"id":"2","type":"exit"}""");

		await runner.RunAsync(
			Arguments("--mode", "rpc", "--no-session", "--session-dir", sessions),
			_root,
			new StringReader(commands),
			CancellationToken.None);

		var response = Parse(output).Single(static node =>
			node["type"]!.GetValue<string>() == "response" && node["id"]?.GetValue<string>() == "1");
		Assert.True(response["success"]!.GetValue<bool>());

		var data = response["data"]!;
		Assert.Equal(115, data["totals"]!["tokens"]!.GetValue<long>());
		Assert.Equal(1, data["totals"]!["messages"]!.GetValue<long>());
		Assert.Equal(0.25, data["totals"]!["cost"]!.GetValue<double>(), 6);
		Assert.Equal("2026-01-02", Assert.Single(data["days"]!.AsArray())!["date"]!.GetValue<string>());
		Assert.Equal("test-model", Assert.Single(data["models"]!.AsArray())!["model"]!.GetValue<string>());
		Assert.Equal("/work/alpha", Assert.Single(data["projects"]!.AsArray())!["directory"]!.GetValue<string>());
		Assert.Equal(28, data["heatmap"]!.AsArray().Count);
	}

	private sealed class ChannelTextReader : TextReader
	{
		private readonly System.Threading.Channels.Channel<string> _lines =
			System.Threading.Channels.Channel.CreateUnbounded<string>();

		public void Write(string line) => _lines.Writer.TryWrite(line);

		public void Complete() => _lines.Writer.TryComplete();

		public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
		{
			try
			{
				return await _lines.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
			}
			catch (System.Threading.Channels.ChannelClosedException)
			{
				return null;
			}
		}
	}

	private static SystemMessage SystemPromptMessage(CodingAgentSession session) =>
		Assert.IsType<SystemMessage>(session.ProjectedMessages[0]);

	private static Dictionary<string, string?> SystemSections(CodingAgentSession session) =>
		SystemPromptMessage(session).Sections!;

	[Fact]
	public async Task InteractiveMode_ReadsPromptsUntilExit()
	{
		var (environment, script) = Environment();
		script.Enqueue("first answer");
		script.Enqueue("second answer");
		var (runner, output, error) = RunnerFor(environment);

		var input = new StringReader("one\n/model\n/exit\n");
		var exitCode = await runner.RunAsync(Arguments("--no-session"), _root, input, CancellationToken.None);

		Assert.Equal(0, exitCode);
		Assert.Contains("first answer", output.ToString());
		Assert.Contains("test/test-model", error.ToString());
	}

	private static List<JsonNode> Parse(StringWriter output) => output.ToString()
		.Split('\n', StringSplitOptions.RemoveEmptyEntries)
		.Where(static line => line.StartsWith('{'))
		.Select(static line => JsonNode.Parse(line)!)
		.ToList();

	private static async Task<List<JsonNode>> WaitForAsync(StringWriter output, Func<string, bool> condition)
	{
		for (var attempt = 0; attempt < 200; attempt++)
		{
			if (condition(output.ToString()))
			{
				break;
			}

			await Task.Delay(10);
		}

		return Parse(output);
	}
}
