using System.Text.Json.Nodes;
using PiWeft.Agent;
using PiWeft.Ai;
using PiWeft.CodingAgent.Settings;
using PiWeft.CodingAgent.Session;
using Xunit;

namespace PiWeft.CodingAgent.Tests;

internal static class ScriptedSessions
{
	public static Model Model(string id = "test-model") => new()
	{
		Id = id,
		Provider = "test",
		Api = "test",
		BaseUrl = "https://example.invalid",
		MaxTokens = 4096,
		ContextWindow = 128_000,
		Reasoning = true,
		Cost = new ModelCost { Input = 1, Output = 2 },
	};

	public static AssistantMessage Blank(Model model) => new()
	{
		Api = model.Api,
		Provider = model.Provider,
		Model = model.Id,
		Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
	};

	public static AssistantMessageEventStream Text(Model model, string text)
	{
		var stream = new AssistantMessageEventStream();
		var blank = Blank(model);
		var message = blank with
		{
			Content = [new TextContent { Text = text }],
			StopReason = StopReason.Stop,
			Usage = new Usage { Input = 10, Output = 5, TotalTokens = 15, Cost = new UsageCost { Total = 0.5 } },
		};

		stream.Push(new StartEvent { Partial = blank });
		stream.Push(new TextStartEvent { ContentIndex = 0, Partial = message });
		stream.Push(new TextDeltaEvent { ContentIndex = 0, Delta = text, Partial = message });
		stream.Push(new TextEndEvent { ContentIndex = 0, Content = text, Partial = message });
		stream.Push(new DoneEvent { Reason = StopReason.Stop, Partial = message });
		return stream;
	}

	/// <summary>一次失败的回复（可重试错误的测试用）。</summary>
	public static AssistantMessageEventStream Error(Model model, string message)
	{
		var stream = new AssistantMessageEventStream();
		var blank = Blank(model);
		var failed = blank with { StopReason = StopReason.Error, ErrorMessage = message };

		stream.Push(new StartEvent { Partial = blank });
		stream.Push(new ErrorEvent { Reason = StopReason.Error, Partial = failed });
		return stream;
	}

	public static AssistantMessageEventStream ToolCall(Model model, string id, string name, JsonObject arguments)
	{
		var stream = new AssistantMessageEventStream();
		var blank = Blank(model);
		var call = new ToolCall { Id = id, Name = name, Arguments = arguments };
		var message = blank with { Content = [call], StopReason = StopReason.ToolUse };

		stream.Push(new StartEvent { Partial = blank });
		stream.Push(new ToolCallStartEvent { ContentIndex = 0, Partial = message });
		stream.Push(new ToolCallEndEvent { ContentIndex = 0, ToolCall = call, Partial = message });
		stream.Push(new DoneEvent { Reason = StopReason.ToolUse, Partial = message });
		return stream;
	}
}

internal sealed class ScriptedStreamFn
{
	private readonly Queue<Func<Model, AssistantMessageEventStream>> _responses = new();

	public List<IReadOnlyList<Message>> Requests { get; } = [];

	/// <summary>最近一次请求的选项，便于断言探测参数。</summary>
	public StreamOptions? LastOptions { get; private set; }

	/// <summary>最近一次请求实际收到的模型。</summary>
	public Model? LastModel { get; private set; }

	public void EnqueueText(string text) => _responses.Enqueue(model => ScriptedSessions.Text(model, text));

	public void EnqueueError(string message) => _responses.Enqueue(model => ScriptedSessions.Error(model, message));

	public void EnqueueToolCall(string id, string name, JsonObject arguments) =>
		_responses.Enqueue(model => ScriptedSessions.ToolCall(model, id, name, arguments));

	public AssistantMessageEventStream Invoke(Model model, IReadOnlyList<Message> messages, StreamOptions options)
	{
		// The loop appends to the live transcript, so snapshot what this request actually saw.
		Requests.Add([.. messages]);
		LastOptions = options;
		LastModel = model;
		if (_responses.Count == 0)
		{
			throw new InvalidOperationException("ScriptedStreamFn ran out of responses");
		}

		return _responses.Dequeue()(model);
	}
}

public class CodingAgentSessionTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-session-" + Guid.NewGuid().ToString("N")[..8]);

	public CodingAgentSessionTests() => Directory.CreateDirectory(_root);

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	private string SessionsDir => Path.Combine(_root, "sessions");

	private CodingAgentSession CreateSession(ScriptedStreamFn script, SessionManager? session = null) =>
		new(new CodingAgentSessionOptions
		{
			Cwd = _root,
			Model = ScriptedSessions.Model(),
			StreamFn = script.Invoke,
			Session = session ?? SessionManager.Create(_root, SessionsDir),
			Skills = [],
		});

	[Fact]
	public async Task VirtualModelRoutesToPhysicalModelAndKeepsVirtualSelection()
	{
		var physical = ScriptedSessions.Model("physical");
		var registry = new ModelRegistry();
		registry.Add(physical);
		var definition = new VirtualModelDefinition
		{
			Provider = "router",
			Id = "auto",
			Name = "Auto",
			ThinkingLevels = [ThinkingLevels.Off, ThinkingLevels.High],
			Route = (request, _) =>
			{
				Assert.Equal(VirtualRouteReason.User, request.Reason);
				return Task.FromResult(new VirtualModelRoute
				{
					Model = physical,
					ThinkingLevel = ThinkingLevels.High,
				});
			},
		};
		registry.AddVirtual(definition);
		var virtualModel = registry.Get("router", "auto")!;
		var script = new ScriptedStreamFn();
		script.EnqueueText("routed");
		using var session = new CodingAgentSession(new CodingAgentSessionOptions
		{
			Cwd = _root,
			Model = virtualModel,
			StreamFn = script.Invoke,
			Registry = registry,
			Session = SessionManager.Create(_root, SessionsDir),
			Skills = [],
		});

		await session.PromptAsync("hello");

		Assert.Equal("physical", script.LastModel!.Id);
		Assert.Equal(ThinkingLevels.High, script.LastOptions!.ThinkingLevel);
		var assistant = Assert.IsType<AssistantMessage>(session.Messages.Last(message => message is AssistantMessage));
		Assert.Equal("test", assistant.Provider);
		Assert.Equal("physical", assistant.Model);
		Assert.Equal(ThinkingLevels.High, assistant.ThinkingLevel);
		Assert.Equal("router", session.Model.Provider);
		Assert.Equal("auto", session.Model.Id);
	}

	[Fact]
	public void SetTools_RebuildsThePromptTheModelSeesButKeepsTheSessionFile()
	{
		using var session = CreateSession(new ScriptedStreamFn());

		var original = PiWeft.Ai.Transcript.GetText(Assert.IsType<SystemMessage>(session.ProjectedMessages[0]));

		Assert.DoesNotContain("late_tool", original);

		// A tool that appeared during the session, as an MCP server announcing a list change would produce.
		var late = new AgentTool
		{
			Name = "late_tool",
			Label = "late_tool",
			Description = "A tool that appeared later",
			Parameters = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
			Execute = static _ => Task.FromResult(new AgentToolResult { Content = [new TextContent { Text = "ok" }] }),
		};

		session.SetTools([.. session.Tools, late]);

		Assert.Contains(session.Tools, static tool => tool.Name == "late_tool");

		var projected = PiWeft.Ai.Transcript.GetText(Assert.IsType<SystemMessage>(session.ProjectedMessages[0]));
		Assert.Contains("late_tool", projected);
		Assert.Contains("A tool that appeared later", projected);

		// The built-in tool entries keep the wording they were declared with.
		Assert.Contains("- read: Read file contents", projected);
		Assert.Contains("Use read to examine files instead of cat or sed.", projected);
		Assert.Contains(SystemPrompt.Preamble, projected);

		// 会话文件里不存系统提示词（与 pi 一致），所以文件不因换工具而变化 —— 变化的只有投影。
		Assert.DoesNotContain(
			session.Session.Entries,
			entry => entry is SessionMessageEntry { Message: SystemMessage });

		// The declaration sent to the provider carries the new tool too.
		var toolsAdded = Assert.IsType<SystemMessage>(session.ProjectedMessages[0]).ToolsAdded!;
		Assert.Contains(toolsAdded, static tool => tool.Name == "late_tool");
	}

	[Fact]
	public void Constructor_SeedsTheSectionedSystemPrompt()
	{
		using var session = CreateSession(new ScriptedStreamFn());

		// 系统提示词是运行时构造的：投影里有，会话文件里没有（与 pi 一致，
		// 否则真实 pi 读这个会话时会把 role:system 的消息当成历史）。
		var system = Assert.IsType<SystemMessage>(session.ProjectedMessages[0]);

		Assert.Equal(
			["preamble", "tools", "rules", "cwd"],
			system.Sections!.Keys.ToList());
		Assert.NotNull(system.ToolsAdded);
		Assert.Equal(9, system.ToolsAdded!.Count);
		Assert.Equal(9, session.Tools.Count);
		Assert.Contains(session.Tools, static tool => tool.Name == "todo");
		Assert.Contains(session.Tools, static tool => tool.Name == "ask_question");

		Assert.DoesNotContain(
			session.Session.Entries,
			entry => entry is SessionMessageEntry { Message: SystemMessage });
	}

	[Fact]
	public async Task Prompt_PersistsEveryMessage()
	{
		var script = new ScriptedStreamFn();
		script.EnqueueText("hello there");

		using var session = CreateSession(script);
		await session.PromptAsync("hi");

		var kinds = session.Session.Entries.Select(entry => entry.Type).ToList();

		// 只写真实往来消息：打开时不写多余条目，系统提示词也不入库。
		Assert.Equal(["message", "message"], kinds);

		var messages = session.Session.Entries.OfType<SessionMessageEntry>().Select(entry => entry.Message).ToList();
		Assert.IsType<UserMessage>(messages[0]);
		Assert.IsType<AssistantMessage>(messages[1]);

		// 投影 = 系统提示 + 对话（系统提示不入库）。
		Assert.Equal(3, session.Messages.Count);
		Assert.IsType<SystemMessage>(session.Messages[0]);
		Assert.IsType<UserMessage>(session.Messages[1]);
		Assert.IsType<AssistantMessage>(session.Messages[2]);
	}

	[Fact]
	public async Task ToolCallsAndResultsArePersistedAndSentBack()
	{
		File.WriteAllText(Path.Combine(_root, "sample.txt"), "file body\n");

		var script = new ScriptedStreamFn();
		script.EnqueueToolCall("c1", "read", new JsonObject { ["path"] = "sample.txt" });
		script.EnqueueText("done");

		using var session = CreateSession(script);
		await session.PromptAsync("read it");

		// 会话里只有真实对话消息（系统提示词不入库）：用户、工具调用、工具结果、最终回复。
		var messages = session.Session.Entries.OfType<SessionMessageEntry>().Select(entry => entry.Message).ToList();
		Assert.Equal(4, messages.Count);
		Assert.IsType<UserMessage>(messages[0]);
		Assert.IsType<AssistantMessage>(messages[1]);
		var toolResult = Assert.IsType<ToolResultMessage>(messages[2]);
		Assert.Equal("c1", toolResult.ToolCallId);
		Assert.Contains("file body", Assert.IsType<TextContent>(toolResult.Content[0]).Text);

		// The follow-up request sees the assistant tool call and its result.
		Assert.Equal(2, script.Requests.Count);
		Assert.Contains(script.Requests[1], message => message is ToolResultMessage);
	}

	[Fact]
	public async Task ResumeKeepsTheTranscriptAndDoesNotAddASecondSystemPrompt()
	{
		var script = new ScriptedStreamFn();
		script.EnqueueText("first answer");
		using (var first = CreateSession(script))
		{
			await first.PromptAsync("hi");
		}

		var file = Directory.EnumerateFiles(SessionsDir, "*.jsonl").Single();
		var reopened = SessionManager.Open(file);
		var entriesBefore = reopened.Entries.Count;

		var second = new ScriptedStreamFn();
		using var resumed = CreateSession(second, reopened);

		// 级别没变时恢复不会写任何东西（只是打开看一眼不该改动文件），也不会加第二份系统提示词。
		Assert.Equal(entriesBefore, resumed.Session.Entries.Count);
		Assert.DoesNotContain(
			resumed.Session.Entries,
			entry => entry is SessionMessageEntry { Message: SystemMessage });

		// 投影里始终只有一份系统提示词。
		Assert.Single(resumed.Messages.OfType<SystemMessage>());
		Assert.Equal(3, resumed.Messages.Count);
		Assert.Equal("first answer", Assert.IsType<TextContent>(
			Assert.IsType<AssistantMessage>(resumed.Messages[2]).Content[0]).Text);
	}

	[Fact]
	public async Task SetModelAndThinkingLevelRecordChangeEntries()
	{
		using var session = CreateSession(new ScriptedStreamFn());

		session.SetModel(ScriptedSessions.Model("other-model"));
		session.SetThinkingLevel(ThinkingLevels.High);

		Assert.Equal("other-model", session.Model.Id);
		Assert.Equal(ThinkingLevels.High, session.ThinkingLevel);

		// 打开时不写多余条目，所以这里只有这次的模型变更与级别变更两条。
		var modelChange = Assert.IsType<ModelChangeEntry>(session.Session.Entries[0]);
		Assert.Equal("test", modelChange.Provider);
		Assert.Equal("other-model", modelChange.ModelId);
		Assert.IsType<ThinkingLevelChangeEntry>(session.Session.Entries[1]);

		// The settings resolved from the branch follow the newest change entries.
		var context = session.Session.BuildSessionContext();
		Assert.Equal(("test", "other-model"), context.Model);
		Assert.Equal(ThinkingLevels.High, context.ThinkingLevel);
	}

	[Fact]
	public async Task UsageTotalsSumPersistedAssistantMessages()
	{
		var script = new ScriptedStreamFn();
		script.EnqueueText("one");

		using var session = CreateSession(script);
		await session.PromptAsync("hi");

		var (tokens, cost) = session.UsageTotals();
		Assert.Equal(15, tokens);
		Assert.Equal(0.5, cost, 6);
	}

	[Fact]
	public async Task WorkingDirectoryIsTheSessionScopeForTools()
	{
		File.WriteAllText(Path.Combine(_root, "here.txt"), "local\n");

		var script = new ScriptedStreamFn();
		script.EnqueueToolCall("c1", "ls", new JsonObject());
		script.EnqueueText("listed");

		using var session = CreateSession(script);
		await session.PromptAsync("list");

		var toolResult = session.Session.Entries
			.OfType<SessionMessageEntry>()
			.Select(entry => entry.Message)
			.OfType<ToolResultMessage>()
			.Single();

		Assert.Contains("here.txt", Assert.IsType<TextContent>(toolResult.Content[0]).Text);
	}

	[Fact]
	public async Task DisposedSessionRejectsPrompts()
	{
		var session = CreateSession(new ScriptedStreamFn());
		session.Dispose();

		await Assert.ThrowsAsync<ObjectDisposedException>(() => session.PromptAsync("hi"));
	}
}

public class CodingMessagesTests
{
	[Fact]
	public void BashExecutionBecomesAUserMessage()
	{
		var message = CodingMessages.BashExecution(
			"ls",
			"a\nb",
			exitCode: 0,
			cancelled: false,
			truncated: true,
			fullOutputPath: "/tmp/full.txt",
			excludeFromContext: false,
			timestamp: 1);

		var converted = Assert.IsType<UserMessage>(
			Assert.Single(CodingMessages.ConvertToLlm([message], CancellationToken.None)));

		var text = converted.Content.Text!;
		Assert.Contains("Ran `ls`", text);
		Assert.Contains("```\na\nb\n```", text);
		Assert.Contains("[Output truncated. Full output: /tmp/full.txt]", text);
	}

	[Fact]
	public void ExcludedBashExecutionIsDropped()
	{
		var message = CodingMessages.BashExecution(
			"ls",
			"a",
			exitCode: 0,
			cancelled: false,
			truncated: false,
			fullOutputPath: null,
			excludeFromContext: true,
			timestamp: 1);

		Assert.Empty(CodingMessages.ConvertToLlm([message], CancellationToken.None));
	}

	[Fact]
	public void SummaryRolesBecomePrefixedUserMessages()
	{
		var compaction = new CustomMessage
		{
			CustomRole = "compactionSummary",
			Timestamp = 1,
			Raw = new JsonObject { ["role"] = "compactionSummary", ["summary"] = "we talked about X" },
		};
		var branch = new CustomMessage
		{
			CustomRole = "branchSummary",
			Timestamp = 2,
			Raw = new JsonObject { ["role"] = "branchSummary", ["summary"] = "approach A" },
		};

		var converted = CodingMessages.ConvertToLlm([compaction, branch], CancellationToken.None);

		Assert.Equal(2, converted.Count);
		var first = Assert.IsType<UserMessage>(converted[0]);
		Assert.Contains("The conversation history before this point was compacted", first.Content.Text);
		Assert.Contains("we talked about X", first.Content.Text);
		Assert.Contains("</summary>", first.Content.Text);

		var second = Assert.IsType<UserMessage>(converted[1]);
		Assert.Contains("The following is a summary of a branch", second.Content.Text);
		Assert.Contains("approach A", second.Content.Text);
	}

	[Fact]
	public void EstimateTokensGrowsWithContent()
	{
		var small = CodingMessages.EstimateTokens([new UserMessage { Content = MessageContent.FromText("hi") }]);
		var large = CodingMessages.EstimateTokens(
			[new UserMessage { Content = MessageContent.FromText(new string('x', 4000)) }]);

		Assert.True(large > small);
		Assert.True(large >= 1000);
	}
}

public class SettingsTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-settings-" + Guid.NewGuid().ToString("N")[..8]);

	public SettingsTests() => Directory.CreateDirectory(_root);

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	[Fact]
	public void Load_MergesProjectOverGlobal()
	{
		var agentDir = Path.Combine(_root, "agent");
		Directory.CreateDirectory(agentDir);
		Directory.CreateDirectory(Path.Combine(_root, "project", ".pi"));

		File.WriteAllText(
			Path.Combine(agentDir, "settings.json"),
			"""
			{
			  "defaultProvider": "anthropic",
			  "defaultModel": "claude-sonnet-4-5",
			  "compaction": { "enabled": true, "reserveTokens": 8192, "keepRecentTokens": 10000 },
			  "unknownFutureKey": true
			}
			""");

		File.WriteAllText(
			Path.Combine(_root, "project", ".pi", "settings.json"),
			"""{"defaultThinkingLevel":"high","compaction":{"reserveTokens":4096}}""");

		var settings = CodingAgentSettings.Load(Path.Combine(_root, "project"), agentDir);

		Assert.Equal("anthropic", settings.DefaultProvider);
		Assert.Equal("claude-sonnet-4-5", settings.DefaultModel);
		Assert.Equal("high", settings.DefaultThinkingLevel);
		// Project values win per key; the global value survives for keys the project omits.
		Assert.Equal(4096, settings.Compaction.ReserveTokens);
		Assert.Equal(10000, settings.Compaction.KeepRecentTokens);
		Assert.True(settings.Compaction.Enabled);
	}

	[Fact]
	public void Load_WithoutFiles_UsesDefaults()
	{
		var settings = CodingAgentSettings.Load(_root, Path.Combine(_root, "missing"));

		Assert.True(settings.Compaction.Enabled);
		Assert.Equal(16384, settings.Compaction.ReserveTokens);
		Assert.Equal(20000, settings.Compaction.KeepRecentTokens);
		Assert.Null(settings.DefaultModel);
	}

	[Fact]
	public void SaveProject_RoundTrips()
	{
		var settings = new CodingAgentSettings
		{
			DefaultProvider = "openai",
			DefaultModel = "gpt-5",
			DefaultThinkingLevel = ThinkingLevels.Low,
			TimeoutMs = 30_000,
			Compaction = new CompactionSettings { Enabled = false, ReserveTokens = 1000, KeepRecentTokens = 2000 },
		};

		CodingAgentSettings.SaveProject(_root, settings);
		var loaded = CodingAgentSettings.Load(_root, Path.Combine(_root, "missing"));

		Assert.Equal("openai", loaded.DefaultProvider);
		Assert.Equal("gpt-5", loaded.DefaultModel);
		Assert.Equal(ThinkingLevels.Low, loaded.DefaultThinkingLevel);
		Assert.Equal(30_000, loaded.TimeoutMs);
		Assert.False(loaded.Compaction.Enabled);
		Assert.Equal(1000, loaded.Compaction.ReserveTokens);
	}

	[Fact]
	public void DefaultPathsFollowPi()
	{
		if (OperatingSystem.IsWindows())
		{
			return;
		}

		Assert.EndsWith("/settings.json", CodingAgentSettings.GlobalPath("/home/u/.pi/agent"));
		Assert.Equal("/home/u/.pi/agent/settings.json", CodingAgentSettings.GlobalPath("/home/u/.pi/agent"));
		Assert.Equal("/tmp/proj/.pi/settings.json", CodingAgentSettings.ProjectPath("/tmp/proj"));
	}
}

/// <summary>
/// 自动重试：可重试的失败会退避后重跑，失败的那次尝试从模型投影里移除（文件里保留），
/// 并发出 <c>auto_retry_start</c> / <c>auto_retry_end</c> 事件 —— 与 pi 的行为一致。
/// </summary>
public class AutoRetryTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "pi-retry-" + Guid.NewGuid().ToString("N")[..8]);

	public AutoRetryTests() => Directory.CreateDirectory(_root);

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	private CodingAgentSession CreateSession(
		ScriptedStreamFn script,
		RetryPolicy? retry = null,
		bool? autoRetry = null)
	{
		var session = new CodingAgentSession(new CodingAgentSessionOptions
		{
			Cwd = _root,
			Model = ScriptedSessions.Model(),
			StreamFn = script.Invoke,
			Session = SessionManager.Create(_root, Path.Combine(_root, "sessions")),
			Skills = [],
			Settings = new CodingAgentSettings
			{
				// 测试里把退避压到最小，别真等两秒。
				Retry = retry ?? new RetryPolicy(BaseDelayMs: 1),
			},
		});

		if (autoRetry is { } enabled)
		{
			session.SetAutoRetryEnabled(enabled);
		}

		return session;
	}

	[Fact]
	public async Task RetryableFailureIsRetriedAndTheRecoveredRunIsReported()
	{
		var script = new ScriptedStreamFn();
		script.EnqueueError("The upstream provider returned an error");
		script.EnqueueText("这次成功了");

		using var session = CreateSession(script);

		var events = new List<Piece>();
		using var subscription = session.Subscribe(@event =>
		{
			switch (@event)
			{
				case AutoRetryStartEvent start:
					events.Add(new Piece("start", start.Attempt, start.MaxAttempts, start.DelayMs, start.ErrorMessage));
					break;
				case AutoRetryEndEvent end:
					events.Add(new Piece("end", end.Attempt, 0, 0, end.FinalError ?? string.Empty));
					break;
			}

			return ValueTask.CompletedTask;
		});

		var messages = await session.PromptAsync("hi");

		// 两次请求：第一次失败，第二次成功。
		Assert.Equal(2, script.Requests.Count);

		var start = Assert.Single(events, piece => piece.Kind == "start");
		Assert.Equal(1, start.Attempt);
		Assert.Equal(3, start.MaxAttempts);

		var end = Assert.Single(events, piece => piece.Kind == "end");
		Assert.Equal(1, end.Attempt);
		Assert.Equal(string.Empty, end.Error);

		// 最终回复来自第二次尝试。
		var final = Assert.IsType<AssistantMessage>(messages[^1]);
		Assert.Equal("这次成功了", Assert.IsType<TextContent>(final.Content[0]).Text);

		// 失败的那次尝试仍在文件里（作为 message 条目），但被 context_edit 从投影中排除。
		Assert.Contains(
			session.Session.Entries,
			entry => entry is SessionMessageEntry { Message: AssistantMessage { StopReason: StopReason.Error } });
		Assert.Contains(session.Session.Entries, entry => entry is ContextEditEntry);

		// 重试请求里没有那次失败：模型只看到用户消息。
		var retryRequest = script.Requests[1];
		Assert.DoesNotContain(retryRequest, message => message is AssistantMessage { StopReason: StopReason.Error });
		Assert.Contains(retryRequest, message => message is UserMessage);

		// 会话投影同样不含失败尝试。
		Assert.DoesNotContain(session.Messages, message => message is AssistantMessage { StopReason: StopReason.Error });
	}

	[Fact]
	public async Task NonRetryableFailureIsReportedImmediately()
	{
		var script = new ScriptedStreamFn();
		script.EnqueueError("insufficient_quota: you exceeded your current quota");

		using var session = CreateSession(script);

		var events = new List<string>();
		using var subscription = session.Subscribe(@event =>
		{
			if (@event is AutoRetryStartEvent or AutoRetryEndEvent)
			{
				events.Add(@event.GetType().Name);
			}

			return ValueTask.CompletedTask;
		});

		await session.PromptAsync("hi");

		Assert.Single(script.Requests);
		Assert.Empty(events);
	}

	[Fact]
	public async Task RetriesStopAfterTheConfiguredLimit()
	{
		var script = new ScriptedStreamFn();
		for (var i = 0; i < 10; i++)
		{
			script.EnqueueError("Service Unavailable");
		}

		using var session = CreateSession(script, new RetryPolicy(BaseDelayMs: 1, MaxRetries: 2));

		await session.PromptAsync("hi");

		// 首次调用 + 2 次重试。
		Assert.Equal(3, script.Requests.Count);
	}

	[Fact]
	public async Task DisablingAutoRetrySkipsTheRetryLoop()
	{
		var script = new ScriptedStreamFn();
		script.EnqueueError("Service Unavailable");
		script.EnqueueText("不该被用到");

		using var session = CreateSession(script, autoRetry: false);

		await session.PromptAsync("hi");

		Assert.Single(script.Requests);
		Assert.False(session.AutoRetryEnabled);
		Assert.Equal(0, session.RetryAttempt);
	}

	[Fact]
	public async Task AbortDuringRetryBackoffStopsTheRun()
	{
		var script = new ScriptedStreamFn();
		script.EnqueueError("Service Unavailable");
		script.EnqueueText("should not run");

		// 退避时间给足，确保取消只能来自 Abort() 而不是自然到点。
		using var session = CreateSession(script, new RetryPolicy(BaseDelayMs: 5_000, MaxRetries: 3));
		var retryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		using var subscription = session.Subscribe(@event =>
		{
			if (@event is AutoRetryStartEvent)
			{
				retryStarted.TrySetResult();
			}

			return ValueTask.CompletedTask;
		});

		var prompt = session.PromptAsync("hi");
		await retryStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.True(session.IsRetrying);

		session.Abort();
		var messages = await prompt.WaitAsync(TimeSpan.FromSeconds(2));

		Assert.Single(script.Requests);
		Assert.False(session.IsRetrying);
		Assert.False(session.IsRunning);
		Assert.IsType<AssistantMessage>(messages[^1]);
	}

	/// <summary>事件断言用的记录。</summary>
	private sealed record Piece(string Kind, int Attempt, int MaxAttempts, int DelayMs, string Error);
}

/// <summary>
/// 写项目设置时必须保留本端口未建模的键（例如 <c>$schema</c> 与上游新增项），
/// 否则一次保存就会把用户的其它配置抹掉。
/// </summary>
public class SettingsWriteTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "pi-settings-" + Guid.NewGuid().ToString("N")[..8]);

	public SettingsWriteTests() => Directory.CreateDirectory(_root);

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	[Fact]
	public void UnknownKeysSurviveASave()
	{
		var path = Path.Combine(_root, ".pi", "settings.json");
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, """
			{
			  "$schema": "https://pi.dev/schemas/settings.schema.json",
			  "someFutureKey": { "nested": true },
			  "retry": { "provider": { "maxRetryDelayMs": 1234 } }
			}
			""");

		CodingAgentSettings.SaveProject(_root, new CodingAgentSettings
		{
			DefaultModel = "claude-sonnet-4-5",
			EnableSkillCommands = false,
			Retry = new RetryPolicy(BaseDelayMs: 500),
		});

		var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject();

		Assert.Equal("claude-sonnet-4-5", root["defaultModel"]!.GetValue<string>());
		Assert.False(root["enableSkillCommands"]!.GetValue<bool>());
		Assert.Equal(500, root["retry"]!["baseDelayMs"]!.GetValue<int>());

		// 未建模的键都还在（包括 retry 下面我们不认识的子键）。
		Assert.Equal("https://pi.dev/schemas/settings.schema.json", root["$schema"]!.GetValue<string>());
		Assert.True(root["someFutureKey"]!["nested"]!.GetValue<bool>());
		Assert.Equal(1234, root["retry"]!["provider"]!["maxRetryDelayMs"]!.GetValue<int>());
	}

	[Fact]
	public void RetrySettingsRoundTripThroughTheFile()
	{
		CodingAgentSettings.SaveProject(_root, new CodingAgentSettings
		{
			Retry = new RetryPolicy(Enabled: false, MaxRetries: 7, BaseDelayMs: 250, MaxAgentDelayMs: 5000),
		});

		var loaded = CodingAgentSettings.Load(_root, Path.Combine(_root, "agent"));

		Assert.False(loaded.Retry.Enabled);
		Assert.Equal(7, loaded.Retry.MaxRetries);
		Assert.Equal(250, loaded.Retry.BaseDelayMs);
		Assert.Equal(5000, loaded.Retry.MaxAgentDelayMs);
	}
}

/// <summary>
/// 桌面浏览时不写文件；开始对话时记录实际级别，CLI 可显式启用上游启动记录。
/// </summary>
public class OpenSessionWriteTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "pi-openwrite-" + Guid.NewGuid().ToString("N")[..8]);

	public OpenSessionWriteTests() => Directory.CreateDirectory(_root);

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	private CodingAgentSession Open(string? thinkingLevel)
	{
		var dir = Path.Combine(_root, "sessions");
		var manager = SessionManager.Create(_root, dir, id: "fixed");
		var file = manager.SessionFile!;

		manager.AppendMessage(new UserMessage { Content = MessageContent.FromText("你好") });
		if (thinkingLevel is not null)
		{
			manager.AppendThinkingLevelChange(thinkingLevel);
		}

		var reopened = SessionManager.Open(file, _root);
		return new CodingAgentSession(new CodingAgentSessionOptions
		{
			Cwd = _root,
			Model = ScriptedSessions.Model(),
			StreamFn = new ScriptedStreamFn().Invoke,
			Session = reopened,
			Skills = [],
			ThinkingLevel = thinkingLevel,
		});
	}

	[Fact]
	public void OpeningWithoutAChangeDoesNotTouchTheFile()
	{
		using var session = Open("off");
		var file = session.Session.SessionFile!;

		// 头部 + 用户消息 + 打开时记录的一条；没有第二次写入。
		Assert.Equal(3, File.ReadAllLines(file).Length);
		Assert.Equal("off", session.ThinkingLevel);
	}

	[Fact]
	public async Task OpeningWithADifferentLevelDefersWritingUntilAPrompt()
	{
		var file = Path.Combine(_root, "sessions");
		var manager = SessionManager.Create(_root, file, id: "changing");
		manager.AppendThinkingLevelChange("off");
		manager.AppendMessage(new UserMessage { Content = MessageContent.FromText("你好") });

		var reopened = SessionManager.Open(manager.SessionFile!, _root);
		var before = File.ReadAllBytes(manager.SessionFile!);
		var modifiedAt = File.GetLastWriteTimeUtc(manager.SessionFile!);
		var script = new ScriptedStreamFn();
		script.EnqueueText("done");
		using var session = new CodingAgentSession(new CodingAgentSessionOptions
		{
			Cwd = _root,
			Model = ScriptedSessions.Model(),
			StreamFn = script.Invoke,
			Session = reopened,
			Skills = [],
			ThinkingLevel = "high",
		});

		Assert.Equal("high", session.ThinkingLevel);
		Assert.Equal(before, File.ReadAllBytes(manager.SessionFile!));
		Assert.Equal(modifiedAt, File.GetLastWriteTimeUtc(manager.SessionFile!));
		Assert.DoesNotContain(session.Session.Entries, entry => entry is ThinkingLevelChangeEntry { ThinkingLevel: "high" });

		await session.PromptAsync("继续");

		// 真正开始对话时记录级别，且只有一条。
		Assert.Single(session.Session.Entries, entry => entry is ThinkingLevelChangeEntry { ThinkingLevel: "high" });
	}
}

public sealed class CacheWarmerTests
{
	private static IReadOnlyList<Message> WarmMessages(Model model) =>
	[
		new UserMessage { Content = MessageContent.FromText("hi") },
		new AssistantMessage
		{
			Provider = model.Provider,
			Model = model.Id,
			Api = model.Api,
			Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
			StopReason = StopReason.Stop,
			Usage = new Usage { Input = 1_000_000, TotalTokens = 1_000_000 },
		},
	];

	[Fact]
	public void StreamingWarmingStopsWhenAgentSettles()
	{
		var model = ScriptedSessions.Model() with { PromptCache = new Dictionary<string, int> { ["short"] = 60 } };
		using var warmer = new PiWeft.CodingAgent.Compaction.CacheWarmer(
			static (_, _, _) => ScriptedSessions.Text(ScriptedSessions.Model(), "warm"),
			static () => "streaming",
			static _ => { });

		warmer.Start(model, WarmMessages(model), new StreamOptions { CacheRetention = CacheRetentions.Short });
		Assert.Equal("scheduled", warmer.Status.State);
		warmer.AgentSettled();

		Assert.Equal("inactive", warmer.Status.State);
		Assert.Contains("settled", warmer.Status.Reason);
	}

	[Fact]
	public void IdleWarmingSurvivesAgentSettled()
	{
		var model = ScriptedSessions.Model() with { PromptCache = new Dictionary<string, int> { ["short"] = 60 } };
		using var warmer = new PiWeft.CodingAgent.Compaction.CacheWarmer(
			static (_, _, _) => ScriptedSessions.Text(ScriptedSessions.Model(), "warm"),
			static () => "idle",
			static _ => { });

		warmer.Start(model, WarmMessages(model), new StreamOptions { CacheRetention = CacheRetentions.Short });
		warmer.AgentSettled();

		Assert.NotEqual("inactive", warmer.Status.State);
	}

	[Fact]
	public void CheapRequestDoesNotWarm()
	{
		var model = ScriptedSessions.Model() with
		{
			Cost = new ModelCost { Input = 0.001, Output = 0.001 },
			PromptCache = new Dictionary<string, int> { ["short"] = 60 },
		};
		using var warmer = new PiWeft.CodingAgent.Compaction.CacheWarmer(
			static (_, _, _) => ScriptedSessions.Text(ScriptedSessions.Model(), "warm"),
			static () => "streaming",
			static _ => { });

		warmer.Start(model, WarmMessages(model), new StreamOptions { CacheRetention = CacheRetentions.Short });

		Assert.Equal("inactive", warmer.Status.State);
		Assert.Equal("stop", warmer.Status.Decision!.Action);
	}

	[Fact]
	public async Task ExtensionDecisionCanStopARefresh()
	{
		var events = new List<PiWeft.CodingAgent.Compaction.CacheWarmingDecisionEvent>();
		var script = new ScriptedStreamFn();
		var model = ScriptedSessions.Model() with { PromptCache = new Dictionary<string, int> { ["short"] = 11 } };
		using var warmer = new PiWeft.CodingAgent.Compaction.CacheWarmer(
			script.Invoke,
			static () => "streaming",
			static _ => { },
			(decisionEvent, _) =>
			{
				events.Add(decisionEvent);
				return Task.FromResult<string?>("stop");
			});

		warmer.Start(model, WarmMessages(model), new StreamOptions { CacheRetention = CacheRetentions.Short });

		// 等到扩展真的把动作改成 stop 并收口；不靠固定睡眠。
		await Eventually.IsTrue(
			() => warmer.Status is { State: "inactive", Reason: "stopped by extension" },
			"扩展的 stop 决策没有收口保温状态");

		Assert.Single(events);
		Assert.Equal("cache_warming_decision", events[0].Type);
		Assert.Equal("inactive", warmer.Status.State);
		Assert.Contains("stopped by extension", warmer.Status.Reason);
		Assert.True(warmer.Status.ExtensionOverride);
		Assert.Empty(script.Requests);
	}

	[Fact]
	public async Task ExtensionDecisionCanOverrideACheapStopWithWarm()
	{
		var script = new ScriptedStreamFn();
		script.EnqueueText("warm");
		var model = ScriptedSessions.Model() with
		{
			Cost = new ModelCost { Input = 0.001, Output = 0.001 },
			PromptCache = new Dictionary<string, int> { ["short"] = 11 },
		};
		using var warmer = new PiWeft.CodingAgent.Compaction.CacheWarmer(
			script.Invoke,
			static () => "streaming",
			static _ => { },
			static (_, _) => Task.FromResult<string?>("warm"));

		warmer.Start(model, WarmMessages(model), new StreamOptions { CacheRetention = CacheRetentions.Short });

		// Start 时状态就是 scheduled，但 ExtensionOverride 为 false；这条只在扩展
		// 真实介入并完成一次刷新之后才成立。
		await Eventually.IsTrue(
			() => warmer.Status is { State: "scheduled", ExtensionOverride: true },
			"扩展的 warm 覆盖没有完成一次保温刷新");

		Assert.Single(script.Requests);
		Assert.Equal("scheduled", warmer.Status.State);
		Assert.True(warmer.Status.ExtensionOverride);
	}

	[Fact]
	public async Task WarmsWithOneTokenAndRecordsUsage()
	{
		var script = new ScriptedStreamFn();
		script.EnqueueText("warm");
		var usages = 0;
		var scheduledTransitions = 0;
		PiWeft.CodingAgent.Compaction.CacheWarmingStatus? afterFirstRefresh = null;

		// 刷新间隔只有 1 秒（缓存寿命 11 秒、预留 10 秒余量），所以在「第几次刷新」这个维度上
		// 轮询必然踩到竞态：刚看到 usages==1，第二次刷新就已经把 Requests 加了第二条。
		// 改成在状态回调里数 scheduled 的次数——Start 先置一次，第二次就代表第一次刷新收尾，
		// 那一刻把状态快照下来，之后所有断言都基于这份快照。
		var firstRefreshDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		using var warmer = new PiWeft.CodingAgent.Compaction.CacheWarmer(
			script.Invoke,
			static () => "streaming",
			_ => Interlocked.Increment(ref usages),
			statusChanged: status =>
			{
				if (status.State == "scheduled" && Interlocked.Increment(ref scheduledTransitions) == 2)
				{
					afterFirstRefresh = status;
					firstRefreshDone.TrySetResult();
				}
			});
		var model = ScriptedSessions.Model() with { PromptCache = new Dictionary<string, int> { ["short"] = 11 } };

		warmer.Start(model,
		[
			new UserMessage { Content = MessageContent.FromText("hi") },
			new AssistantMessage
			{
				Provider = model.Provider,
				Model = model.Id,
				Api = model.Api,
				Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
				StopReason = StopReason.Stop,
				Usage = new Usage { Input = 1_000_000, TotalTokens = 1_000_000 },
			},
		], new StreamOptions
		{
			CacheRetention = CacheRetentions.Short,
		});

		await firstRefreshDone.Task.WaitAsync(TimeSpan.FromSeconds(20));
		warmer.Cancel();

		Assert.Equal(1, Volatile.Read(ref usages));
		Assert.Equal(1, script.LastOptions!.MaxTokens);
		Assert.NotNull(afterFirstRefresh);
		Assert.Equal("scheduled", afterFirstRefresh!.State);
		Assert.Equal("warm", afterFirstRefresh.Decision!.Action);
	}
}
