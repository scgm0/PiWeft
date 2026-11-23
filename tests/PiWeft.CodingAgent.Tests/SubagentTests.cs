using System.Diagnostics;
using System.Text.Json.Nodes;
using PiWeft.Agent;
using PiWeft.Ai;
using PiWeft.CodingAgent;
using PiWeft.CodingAgent.Session;
using PiWeft.CodingAgent.Settings;
using PiWeft.CodingAgent.Subagents;
using Xunit;

namespace PiWeft.CodingAgent.Tests;

/// 子代理：角色发现、异步委派、结果投递、等待与容量。
public sealed class SubagentTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-subagent-" + Guid.NewGuid().ToString("N")[..8]);

	public SubagentTests() => Directory.CreateDirectory(_root);

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	private static JsonObject StatusSchema => new()
	{
		["type"] = "object",
		["properties"] = new JsonObject
		{
			["status"] = new JsonObject
			{
				["type"] = "string",
				["enum"] = new JsonArray("ok", "blocked"),
			},
		},
		["required"] = new JsonArray("status"),
		["additionalProperties"] = false,
	};

	private static SubagentCatalog Builtins => SubagentCatalog.Discover(Path.GetTempPath(), "missing-agent-dir", includeProject: false);

	private CodingAgentSession CreateParent(
		ScriptedStreamFn stream,
		ISubagentHost? host,
		SubagentCatalog? catalog = null,
		int depth = 0,
		int maxActive = 4,
		int maxSpawns = 32,
		int maxWriters = 0,
		bool team = false,
		SessionManager? session = null,
		TimeSpan? runTimeout = null) =>
		new(new CodingAgentSessionOptions
		{
			Cwd = _root,
			Model = ScriptedSessions.Model(),
			StreamFn = stream.Invoke,
			Session = session ?? SessionManager.InMemory(_root),
			Persist = false,
			Skills = [],
			PromptTemplates = [],
			Settings = new CodingAgentSettings(),
			SubagentHost = host,
			SubagentDepth = depth,
			SubagentLimits = new SubagentLimits
			{
				MaxActive = maxActive,
				MaxSpawns = maxSpawns,
				MaxWriters = maxWriters,
				Team = team,
				RunTimeout = runTimeout ?? TimeSpan.FromMinutes(30),
			},
			Agents = catalog ?? Builtins,
		});

	private sealed class FakeHost(
		string root,
		bool addReadTool = false,
		bool persistChildren = false,
		bool askQuestion = false,
		bool askParent = false) : ISubagentHost
	{
		private readonly IReadOnlyList<AgentTool>? _tools = addReadTool
			? [PiWeft.CodingAgent.Tools.FileTools.Read(root)]
			: null;

		private readonly BlockingUiHost _uiHost = new();

		/// 子会话里 ask_question 的答案；测试用它模拟用户拍板。
		public BlockingUiHost UiHost => _uiHost;

		public List<SubagentStartContext> Contexts { get; } = [];

		public Queue<ScriptedStreamFn> Streams { get; } = new();

		public List<CodingAgentSession> Children { get; } = [];

		public int ReopenCount { get; private set; }

		public List<SubagentReopenContext> ReopenContexts { get; } = [];

		/// 恢复前先等这个闸：并发 send 的测试用它把第一个恢复卡在 host 里。
		public TaskCompletionSource? ReopenGate { get; set; }

		/// 启动窗口测试用：把宿主建会话卡在这里，模拟「delegate 已返回但子会话还没建好」。
		public TaskCompletionSource? CreateGate { get; set; }

		public async Task<CodingAgentSession?> CreateChildAsync(
			SubagentStartContext context,
			CancellationToken cancellationToken)
		{
			Contexts.Add(context);
			if (CreateGate is { } gate)
			{
				await gate.Task.ConfigureAwait(false);
			}

			var stream = Streams.Count > 0 ? Streams.Dequeue() : new ScriptedStreamFn();
			// persistChildren 时给子会话真的落一个文件：冷恢复要按文件打开。
			var manager = persistChildren
				? SessionManager.Create(root, Path.Combine(root, "children"))
				: SessionManager.InMemory(root);
			CodingAgentSession? created = null;
			List<AgentTool>? tools = null;
			if (askQuestion)
			{
				tools = [PiWeft.CodingAgent.Tools.AskQuestionTool.Create(() => created).Tool];
			}

			if (askParent)
			{
				(tools ??= []).Add(SubagentTools.CreateAskParent(() => created));
			}

			tools ??= _tools is { } inherited ? [.. inherited] : null;
			created = new CodingAgentSession(new CodingAgentSessionOptions
			{
				Cwd = root,
				Model = ScriptedSessions.Model(),
				StreamFn = stream.Invoke,
				Session = manager,
				Persist = persistChildren,
				Skills = [],
				PromptTemplates = [],
				Settings = new CodingAgentSettings(),
				Tools = tools,
				UiHost = askQuestion ? _uiHost : null,
				SubagentParent = context.Parent,
			});
			Children.Add(created);
			return created;
		}

		public async Task<CodingAgentSession?> ReopenChildAsync(
			SubagentReopenContext context,
			CancellationToken cancellationToken)
		{
			ReopenCount++;
			ReopenContexts.Add(context);
			if (ReopenGate is { } gate)
			{
				await gate.Task.ConfigureAwait(false);
			}

			if (!File.Exists(context.SessionFile))
			{
				return null;
			}

			var manager = SessionManager.Open(context.SessionFile);
			var stream = Streams.Count > 0 ? Streams.Dequeue() : new ScriptedStreamFn();
			var child = new CodingAgentSession(new CodingAgentSessionOptions
			{
				Cwd = manager.Cwd,
				Model = ScriptedSessions.Model(),
				StreamFn = stream.Invoke,
				Session = manager,
				Persist = true,
				Skills = [],
				PromptTemplates = [],
				Settings = new CodingAgentSettings(),
				Tools = _tools,
				SubagentParent = context.Parent,
			});
			Children.Add(child);
			return child;
		}
	}

	[Fact]
	public async Task Delegate_ReturnsImmediatelyAndDeliversTheFinalReply()
	{
		var parentStream = new ScriptedStreamFn();
		parentStream.EnqueueText("父会话收到结论。");
		var childStream = new ScriptedStreamFn();
		childStream.EnqueueText("登录流程在三处被复制，入口是 AuthController.Login。");
		var host = new FakeHost(_root);
		host.Streams.Enqueue(childStream);

		using var parent = CreateParent(parentStream, host);
		var hub = Assert.IsType<SubagentHub>(parent.Subagents);

		var started = await hub.StartAsync(new SubagentStartRequest("scout", "找出登录流程的入口与重复实现。"));
		Assert.True(started.Ok, started.Text);
		Assert.Equal("scout-1", started.Name);
		Assert.False(string.IsNullOrEmpty(started.SessionId));

		var context = Assert.Single(host.Contexts);
		Assert.Equal("找出登录流程的入口与重复实现。", context.Task);
		Assert.Equal("scout-1", context.Name);
		Assert.Equal(1, context.Depth);
		Assert.Equal("scout", context.Agent.Name);

		await Eventually.IsTrue(
			() => hub.Runs.Single().State == SubagentState.Idle,
			"子代理应在一轮后回到 idle");
		await hub.DeliveryBarrier.WaitAsync(TimeSpan.FromSeconds(10));

		var run = hub.Runs.Single();
		Assert.Equal(1, run.Turns);
		Assert.Contains("AuthController.Login", run.Result!);

		var request = Assert.Single(parentStream.Requests);
		var message = Assert.IsType<UserMessage>(request.Single(item => item is UserMessage
		{
			CustomType: SubagentHub.ResultMessageType,
		}));
		Assert.Contains("子代理「scout-1」完成了一轮", message.Content.Text);
		Assert.Contains("AuthController.Login", message.Content.Text);
		Assert.Contains("不是用户指令", message.Content.Text);
		Assert.Contains(parent.Session.Entries, entry =>
			entry is CustomEntry { CustomType: SubagentHub.EntryType } custom &&
			custom.Data?["kind"]?.GetValue<string>() == "started");
	}

	[Fact]
	public async Task Usage_ExcludesCacheHitsToMatchTheSessionPanel()
	{
		var childStream = new ScriptedStreamFn();
		childStream.Enqueue(model => ScriptedSessions.Text(model, "带缓存的回复。", new Usage
		{
			Input = 10,
			Output = 5,
			CacheRead = 100,
			TotalTokens = 115,
		}));
		var host = new FakeHost(_root);
		host.Streams.Enqueue(childStream);
		using var parent = CreateParent(new ScriptedStreamFn(), host);
		var hub = Assert.IsType<SubagentHub>(parent.Subagents);

		var started = await hub.StartAsync(new SubagentStartRequest("scout", "查一下。"));
		Assert.True(started.Ok, started.Text);
		await Eventually.IsTrue(() => hub.Runs.Single().State == SubagentState.Idle, "第一轮结束");

		Assert.Equal(15, hub.Runs.Single().Tokens);
	}

	[Fact]
	public async Task ListFiltersByStateAndAgentAndExposesRoleCapabilities()
	{
		var childStream = new ScriptedStreamFn();
		childStream.EnqueueText("结论。");
		var host = new FakeHost(_root);
		host.Streams.Enqueue(childStream);
		using var parent = CreateParent(new ScriptedStreamFn(), host);
		var hub = Assert.IsType<SubagentHub>(parent.Subagents);

		var started = await hub.StartAsync(new SubagentStartRequest("scout", "查一下。"));
		Assert.True(started.Ok, started.Text);
		await Eventually.IsTrue(() => hub.Runs.Single().State == SubagentState.Idle, "第一轮结束");
		await hub.DeliveryBarrier.WaitAsync(TimeSpan.FromSeconds(10));

		hub.Describe(out var all);
		Assert.Equal(1, all["listed"]!.GetValue<int>());

		hub.Describe(out var completed, "completed", "scout");
		Assert.Equal(1, completed["listed"]!.GetValue<int>());

		hub.Describe(out var failed, "failed");
		Assert.Equal(0, failed["listed"]!.GetValue<int>());
		Assert.Contains("no subagent matched the filter", hub.Describe(out _, "failed"));

		hub.Describe(out var otherAgent, agent: "reviewer");
		Assert.Equal(0, otherAgent["listed"]!.GetValue<int>());

		var scout = all["available"]!.AsArray()
			.First(node => node!["name"]!.GetValue<string>() == "scout")!
			.AsObject();
		Assert.NotNull(scout["tools"]);
	}

	[Fact]
	public async Task CostBudgetStopsTheRun()
	{
		var childStream = new ScriptedStreamFn();
		childStream.EnqueueText("贵的一次。");
		var host = new FakeHost(_root);
		host.Streams.Enqueue(childStream);
		using var parent = CreateParent(new ScriptedStreamFn(), host);
		var hub = Assert.IsType<SubagentHub>(parent.Subagents);

		var started = await hub.StartAsync(new SubagentStartRequest("scout", "花点钱。")
		{
			MaxCost = 0.1,
		});
		Assert.True(started.Ok, started.Text);
		await Eventually.IsTrue(() => hub.Runs.Single().State == SubagentState.Failed, "费用超限应把状态落到 failed");
		Assert.Contains("cost", hub.Runs.Single().Result!);
		Assert.Contains("0.5", hub.Runs.Single().Result!);
		await hub.DeliveryBarrier.WaitAsync(TimeSpan.FromSeconds(10));
	}

	[Fact]
	public async Task ToolCallBudgetStopsTheRun()
	{
		File.WriteAllText(Path.Combine(_root, "a.txt"), "x");
		var childStream = new ScriptedStreamFn();
		childStream.EnqueueToolCall("read-1", "read", new JsonObject { ["path"] = "a.txt" });
		childStream.EnqueueToolCall("read-2", "read", new JsonObject { ["path"] = "a.txt" });
		var host = new FakeHost(_root, addReadTool: true);
		host.Streams.Enqueue(childStream);
		using var parent = CreateParent(new ScriptedStreamFn(), host);
		var hub = Assert.IsType<SubagentHub>(parent.Subagents);

		var started = await hub.StartAsync(new SubagentStartRequest("scout", "连续读。")
		{
			MaxToolCalls = 1,
		});
		Assert.True(started.Ok, started.Text);
		await Eventually.IsTrue(() => hub.Runs.Single().State == SubagentState.Failed, "工具超限应把状态落到 failed");
		Assert.Contains("tool-call budget", hub.Runs.Single().Result!);
		await hub.DeliveryBarrier.WaitAsync(TimeSpan.FromSeconds(10));
	}

	[Fact]
	public async Task ToolCallBudgetIsPerTurn()
	{
		File.WriteAllText(Path.Combine(_root, "a.txt"), "x");
		var parentStream = new ScriptedStreamFn();
		parentStream.EnqueueText("收到。");
		parentStream.EnqueueText("收到。");
		var childStream = new ScriptedStreamFn();
		childStream.EnqueueToolCall("read-1", "read", new JsonObject { ["path"] = "a.txt" });
		childStream.EnqueueText("第一轮。");
		childStream.EnqueueToolCall("read-2", "read", new JsonObject { ["path"] = "a.txt" });
		childStream.EnqueueText("第二轮。");
		var host = new FakeHost(_root, addReadTool: true);
		host.Streams.Enqueue(childStream);
		using var parent = CreateParent(parentStream, host);
		var hub = Assert.IsType<SubagentHub>(parent.Subagents);

		var started = await hub.StartAsync(new SubagentStartRequest("scout", "读两次。")
		{
			MaxToolCalls = 1,
		});
		Assert.True(started.Ok, started.Text);
		await Eventually.IsTrue(() => hub.Runs.Single().State == SubagentState.Idle, "第一轮结束");
		await hub.DeliveryBarrier.WaitAsync(TimeSpan.FromSeconds(10));

		var sent = await hub.SendAsync(started.Name!, "再读一次。");
		Assert.True(sent.Ok, sent.Text);
		await Eventually.IsTrue(() => hub.Runs.Single().State == SubagentState.Idle, "第二轮结束");
		Assert.Equal(2, hub.Runs.Single().Turns);
		Assert.Equal(SubagentState.Idle, hub.Runs.Single().State);
		await hub.DeliveryBarrier.WaitAsync(TimeSpan.FromSeconds(10));
	}

	[Fact]
	public async Task Usage_IsExposedInTheRunAndListDetails()
	{
		var childStream = new ScriptedStreamFn();
		childStream.EnqueueText("带用量的结论。");
		var host = new FakeHost(_root);
		host.Streams.Enqueue(childStream);
		using var parent = CreateParent(new ScriptedStreamFn(), host);
		var hub = Assert.IsType<SubagentHub>(parent.Subagents);

		var started = await hub.StartAsync(new SubagentStartRequest("scout", "查一下。"));
		Assert.True(started.Ok, started.Text);
		await Eventually.IsTrue(() => hub.Runs.Single().State == SubagentState.Idle, "第一轮结束");
		await hub.DeliveryBarrier.WaitAsync(TimeSpan.FromSeconds(10));

		var run = hub.Runs.Single();
		Assert.True(run.Tokens > 0, "子会话的 usage 应记到运行上");
		hub.Describe(out var details);
		var row = details["subagents"]!.AsArray()[0]!.AsObject();
		Assert.Equal(run.Tokens, row["tokens"]!.GetValue<long>());
		Assert.True(row["elapsed_ms"]!.GetValue<long>() >= 0);
	}

	[Fact]
	public async Task Read_CanIncludeToolCallsAndResults()
	{
		File.WriteAllText(Path.Combine(_root, "a.txt"), "x");
		var parentStream = new ScriptedStreamFn();
		parentStream.EnqueueText("父会话收到。");
		var childStream = new ScriptedStreamFn();
		childStream.EnqueueToolCall("read-1", "read", new JsonObject { ["path"] = "a.txt" });
		childStream.EnqueueText("读完了。");
		var host = new FakeHost(_root, addReadTool: true);
		host.Streams.Enqueue(childStream);
		using var parent = CreateParent(parentStream, host);
		var hub = Assert.IsType<SubagentHub>(parent.Subagents);

		var started = await hub.StartAsync(new SubagentStartRequest("scout", "读文件。"));
		Assert.True(started.Ok, started.Text);
		await Eventually.IsTrue(() => hub.Runs.Single().State == SubagentState.Idle, "子代理结束");

		var assistantOnly = hub.Read(started.Name!);
		Assert.True(assistantOnly.Ok, assistantOnly.Text);
		Assert.DoesNotContain("[tool", assistantOnly.Text);

		var transcript = hub.Read(started.Name!, includeTools: true);
		Assert.True(transcript.Ok, transcript.Text);
		Assert.Contains("[user]", transcript.Text);
		Assert.Contains("[assistant]", transcript.Text);
		Assert.Contains("[tool read]", transcript.Text);
		Assert.Contains("读完了", transcript.Text);
		Assert.True(transcript.Details!["include_tools"]!.GetValue<bool>());
		await hub.DeliveryBarrier.WaitAsync(TimeSpan.FromSeconds(10));
	}

	[Fact]
	public async Task Read_ReturnsTheFullTextWhenTheDeliveredResultWasClipped()
	{
		var fullText = new string('x', SubagentHub.MaxResultChars * 2) + "END";
		var parentStream = new ScriptedStreamFn();
		parentStream.EnqueueText("父会话收到。");
		var childStream = new ScriptedStreamFn();
		childStream.EnqueueText(fullText);
		var host = new FakeHost(_root);
		host.Streams.Enqueue(childStream);
		using var parent = CreateParent(parentStream, host);
		var hub = Assert.IsType<SubagentHub>(parent.Subagents);

		var started = await hub.StartAsync(new SubagentStartRequest("scout", "写一份长报告。"));
		Assert.True(started.Ok, started.Text);
		await Eventually.IsTrue(() => hub.Runs.Single().State == SubagentState.Idle, "第一轮结束");
		await hub.DeliveryBarrier.WaitAsync(TimeSpan.FromSeconds(10));

		var read = hub.Read(started.Name!, SubagentHub.MaxReadChars);
		Assert.True(read.Ok, read.Text);
		Assert.Equal(fullText.Length, read.Details!["chars"]!.GetValue<int>());
		Assert.False(read.Details!["truncated"]!.GetValue<bool>());
		Assert.Contains("END", read.Text);
		Assert.True(read.Text.Length > SubagentHub.MaxResultChars);

		var clipped = hub.Read(started.Name!, 1_000);
		Assert.True(clipped.Ok, clipped.Text);
		Assert.True(clipped.Details!["truncated"]!.GetValue<bool>());
		Assert.Contains("已截断", clipped.Text);
	}

	[Fact]
	public async Task StructuredOutput_IsValidatedAndDelivered()
	{
		var parentStream = new ScriptedStreamFn();
		parentStream.EnqueueText("父会话收到。");
		var childStream = new ScriptedStreamFn();
		childStream.EnqueueText("```json\n{\"status\":\"ok\"}\n```");
		var host = new FakeHost(_root);
		host.Streams.Enqueue(childStream);
		using var parent = CreateParent(parentStream, host);
		var hub = Assert.IsType<SubagentHub>(parent.Subagents);

		var started = await hub.StartAsync(new SubagentStartRequest("reviewer", "检查这次改动。")
		{
			OutputSchema = StatusSchema,
		});
		Assert.True(started.Ok, started.Text);
		await Eventually.IsTrue(() => hub.Runs.Single().State == SubagentState.Idle, "结构化结果应通过校验");
		await hub.DeliveryBarrier.WaitAsync(TimeSpan.FromSeconds(10));

		var run = hub.Runs.Single();
		Assert.Equal("ok", run.StructuredOutput!["status"]!.GetValue<string>());
		var message = Assert.Single(parentStream.Requests).OfType<UserMessage>()
			.Single(item => item.CustomType == SubagentHub.ResultMessageType);
		Assert.Contains("structured_output", message.Content.Text);
		Assert.Contains("\"status\"", message.Content.Text);
		Assert.Contains("\"ok\"", message.Content.Text);

		hub.Describe(out var details);
		var row = details["subagents"]!.AsArray()[0]!.AsObject();
		Assert.Equal("ok", row["structured_output"]!["status"]!.GetValue<string>());
	}

	[Fact]
	public async Task StructuredOutput_InvalidMarksTheRunFailed()
	{
		var parentStream = new ScriptedStreamFn();
		parentStream.EnqueueText("父会话收到。");
		var childStream = new ScriptedStreamFn();
		childStream.EnqueueText("看完了，没问题。");
		var host = new FakeHost(_root);
		host.Streams.Enqueue(childStream);
		using var parent = CreateParent(parentStream, host);
		var hub = Assert.IsType<SubagentHub>(parent.Subagents);

		var started = await hub.StartAsync(new SubagentStartRequest("reviewer", "检查这次改动。")
		{
			OutputSchema = StatusSchema,
		});
		Assert.True(started.Ok, started.Text);
		await Eventually.IsTrue(() => hub.Runs.Single().State == SubagentState.Failed, "没有 JSON 应判失败");
		await hub.DeliveryBarrier.WaitAsync(TimeSpan.FromSeconds(10));

		var run = hub.Runs.Single();
		Assert.Null(run.StructuredOutput);
		Assert.Contains("structured output", run.Result!);
		var message = Assert.Single(parentStream.Requests).OfType<UserMessage>()
			.Single(item => item.CustomType == SubagentHub.ResultMessageType);
		Assert.Contains("这一轮失败", message.Content.Text);
	}

	[Fact]
	public async Task Delegate_RejectsAnUnsupportedOutputSchema()
	{
		var host = new FakeHost(_root);
		using var parent = CreateParent(new ScriptedStreamFn(), host);
		var hub = Assert.IsType<SubagentHub>(parent.Subagents);
		var started = await hub.StartAsync(new SubagentStartRequest("reviewer", "检查。")
		{
			OutputSchema = new JsonObject
			{
				["type"] = "object",
				["properties"] = new JsonObject
				{
					["name"] = new JsonObject
					{
						["type"] = "string",
						["pattern"] = "^a",
					},
				},
			},
		});

		Assert.False(started.Ok);
		Assert.Contains("output_schema", started.Text);
		Assert.Empty(host.Contexts);
	}

	[Fact]
	public async Task Restart_RestoresTheOutputSchema()
	{
		var host = new FakeHost(_root);
		var childStream = new ScriptedStreamFn();
		childStream.EnqueueText("""{"status":"ok"}""");
		host.Streams.Enqueue(childStream);
		var manager = SessionManager.Create(_root, Path.Combine(_root, "sessions"));
		var file = manager.SessionFile;
		using (var first = CreateParent(new ScriptedStreamFn(), host, session: manager))
		{
			var started = await first.Subagents!.StartAsync(new SubagentStartRequest("reviewer", "检查。")
			{
				OutputSchema = StatusSchema,
			});
			Assert.True(started.Ok, started.Text);
			await Eventually.IsTrue(() => first.Subagents.Runs.Single().State == SubagentState.Idle, "第一轮结束");
			await first.Subagents.DeliveryBarrier.WaitAsync(TimeSpan.FromSeconds(10));
		}

		foreach (var child in host.Children)
		{
			child.Dispose();
		}

		Assert.NotNull(file);
		using var second = CreateParent(new ScriptedStreamFn(), new FakeHost(_root), session: SessionManager.Open(file));
		var restored = Assert.Single(second.Subagents!.Runs);
		Assert.Equal("ok", restored.StructuredOutput!["status"]!.GetValue<string>());
	}

	[Fact]
	public async Task Wait_KeepsResultsThatDoNotFitTheBudget()
	{
		// 父会话占着一轮：子代理结果先攒着，wait 拿到的正文不受消息投递干扰。
		var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var parentStream = new ScriptedStreamFn();
		parentStream.Enqueue(model => GatedText(model, "父会话这一轮结束了。", gate.Task));
		var host = new FakeHost(_root);
		var reports = new[]
		{
			new string('a', SubagentHub.MaxResultChars),
			new string('b', SubagentHub.MaxResultChars),
			new string('c', SubagentHub.MaxResultChars),
		};
		foreach (var report in reports)
		{
			var childStream = new ScriptedStreamFn();
			childStream.EnqueueText(report);
			host.Streams.Enqueue(childStream);
		}

		using var parent = CreateParent(parentStream, host);
		var hub = Assert.IsType<SubagentHub>(parent.Subagents);
		var running = parent.PromptAsync("先跑一轮。");
		await Eventually.IsTrue(() => parent.IsRunning, "父会话应进入运行态");

		var names = new List<string>();
		for (var index = 0; index < reports.Length; index++)
		{
			var started = await hub.StartAsync(new SubagentStartRequest("scout", $"报告 {index}"));
			Assert.True(started.Ok, started.Text);
			names.Add(started.Name!);
		}

		await Eventually.IsTrue(
			() => hub.Runs.Count == reports.Length && hub.Runs.All(static run => run.State == SubagentState.Idle),
			"三个子代理都应结束第一轮");

		var waited = await hub.WaitAsync(names, TimeSpan.FromSeconds(10));
		Assert.True(waited.Ok, waited.Text);
		var rows = waited.Details!["subagents"]!.AsArray();
		Assert.True(rows[0]!["returned"]!.GetValue<bool>());
		Assert.True(rows[1]!["returned"]!.GetValue<bool>());
		// 24k 预算装得下两份 12k 正文，第三份必须留在未投递状态。
		Assert.False(rows[2]!["returned"]!.GetValue<bool>());
		Assert.False(rows[2]!["delivered"]!.GetValue<bool>());
		Assert.Contains("result omitted", waited.Text);

		var again = await hub.WaitAsync([names[2]], TimeSpan.FromSeconds(5));
		Assert.True(again.Ok, again.Text);
		Assert.Contains(reports[2], again.Text);
		var retried = again.Details!["subagents"]!.AsArray()[0]!.AsObject();
		Assert.True(retried["returned"]!.GetValue<bool>());

		gate.SetResult();
		await running;
		await hub.DeliveryBarrier.WaitAsync(TimeSpan.FromSeconds(5));

		// 三份正文都由 wait 取走：父会话只跑了自己那一轮，不再多一轮回报。
		Assert.Single(parentStream.Requests);
	}

	[Fact]
	public async Task SendWhileWaitingDoesNotReplaceTheTurnWaiter()
	{
		var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var parentStream = new ScriptedStreamFn();
		parentStream.EnqueueText("父会话收到结论。");
		var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var childStream = new ScriptedStreamFn();
		childStream.Enqueue(model =>
		{
			firstStarted.TrySetResult();
			return GatedText(model, "第一轮结论。", gate.Task);
		});
		childStream.EnqueueText("第二轮结论。");
		var host = new FakeHost(_root);
		host.Streams.Enqueue(childStream);

		using var parent = CreateParent(parentStream, host);
		var hub = Assert.IsType<SubagentHub>(parent.Subagents);
		var started = await hub.StartAsync(new SubagentStartRequest("worker", "先做第一轮。"));
		Assert.True(started.Ok, started.Text);
		await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
		await Eventually.IsTrue(() => hub.Runs.Single().State == SubagentState.Running, "子代理应进入运行态");

		var wait = hub.WaitAsync([started.Name!], TimeSpan.FromSeconds(3));
		var sent = await hub.SendAsync(started.Name!, "补一条后续。");
		Assert.True(sent.Ok, sent.Text);

		// send 不能换掉正在等待的 TCS：这一轮收尾时 wait 必须拿到结果。
		// 运行中的 follow-up 会并进同一轮运行，最终回复是合并后的最后一条正文。
		gate.SetResult();
		var waited = await wait;
		Assert.True(waited.Ok, waited.Text);
		Assert.False(waited.Details!["timed_out"]!.GetValue<bool>());
		Assert.Contains("第二轮结论", waited.Text);

		// 运行中的 follow-up 不另起一轮：turns 计的是这一轮运行，正文是合并后的最终回复。
		Assert.Equal(1, hub.Runs.Single().Turns);
		Assert.Contains("第二轮结论", hub.Runs.Single().Result!);
		await hub.DeliveryBarrier.WaitAsync(TimeSpan.FromSeconds(5));
	}

	[Fact]
	public async Task Wait_ReturnsTheResultAndSuppressesTheSeparateDelivery()
	{
		var parentStream = new ScriptedStreamFn();
		var childStream = new ScriptedStreamFn();
		childStream.EnqueueText("结论：没有发现阻塞项。");
		var host = new FakeHost(_root);
		host.Streams.Enqueue(childStream);

		using var parent = CreateParent(parentStream, host);
		var hub = Assert.IsType<SubagentHub>(parent.Subagents);

		var started = await hub.StartAsync(new SubagentStartRequest("reviewer", "检查这次改动。"));
		Assert.True(started.Ok, started.Text);

		var waited = await hub.WaitAsync([started.Name!], TimeSpan.FromSeconds(10));
		Assert.True(waited.Ok, waited.Text);
		// 给模型的正文是给人读的，不是 JSON；结构化数据走 Details。
		Assert.Contains("- reviewer-1 (reviewer) · idle · 1 turns", waited.Text);
		Assert.Contains("没有发现阻塞项", waited.Text);
		Assert.DoesNotContain("\\u", waited.Text);
		var row = Assert.Single(waited.Details!["subagents"]!.AsArray())!.AsObject();
		Assert.Equal("idle", row["state"]!.GetValue<string>());
		// 正文随本次工具结果返回：桌面据此补一张只读回报卡。
		Assert.True(row["returned"]!.GetValue<bool>());
		Assert.Contains("没有发现阻塞项", row["result"]!.GetValue<string>());

		await hub.DeliveryBarrier.WaitAsync(TimeSpan.FromSeconds(10));
		Assert.Empty(parentStream.Requests);
	}

	[Fact]
	public async Task Fork_SeedsTheChildWithTheLastCompletedTurn()
	{
		var parentStream = new ScriptedStreamFn();
		parentStream.EnqueueText("第一轮结论。");
		var host = new FakeHost(_root);
		host.Streams.Enqueue(new ScriptedStreamFn());

		var manager = SessionManager.Create(_root, Path.Combine(_root, "sessions"));
		using var parent = CreateParent(parentStream, host, session: manager);
		await parent.PromptAsync("第一个问题。");

		var hub = Assert.IsType<SubagentHub>(parent.Subagents);
		var started = await hub.StartAsync(new SubagentStartRequest("scout", "接着看。", Context: "fork"));
		Assert.True(started.Ok, started.Text);

		var context = Assert.Single(host.Contexts);
		Assert.NotNull(context.Seed);
		Assert.Equal(2, context.Seed!.Count);
		var question = Assert.IsType<UserMessage>(context.Seed[0]);
		Assert.Contains("第一个问题", question.Content.Text);
		var answer = Assert.IsType<AssistantMessage>(context.Seed[1]);
		Assert.Contains("第一轮结论", MessageText.Of(answer));
		// fork 的父历史只是上下文：父会话花掉的 token 与时长不能重复计进子代理，
		// 否则子代理卡和全局用量都会把 fork 之前的花费再算一遍。
		Assert.Equal(0, answer.Usage.TotalTokens);
		Assert.Null(answer.DurationMs);
		var parentAnswer = parent.Session.BuildProjectedMessages().OfType<AssistantMessage>().Last();
		Assert.True(parentAnswer.Usage.TotalTokens > 0, "父会话自己的用量不能被改写");
	}

	[Fact]
	public async Task DelegatePassesPerCallModelAndThinkingToTheHost()
	{
		var host = new FakeHost(_root);
		var childStream = new ScriptedStreamFn();
		childStream.EnqueueText("结论。");
		host.Streams.Enqueue(childStream);
		using var parent = CreateParent(new ScriptedStreamFn(), host);
		var hub = Assert.IsType<SubagentHub>(parent.Subagents);

		var started = await hub.StartAsync(new SubagentStartRequest("scout", "查一下。")
		{
			Model = "other/provider-model",
			Thinking = "high",
		});
		Assert.True(started.Ok, started.Text);
		var context = Assert.Single(host.Contexts);
		Assert.Equal("other/provider-model", context.Model);
		Assert.Equal("high", context.Thinking);

		var tooLong = await hub.StartAsync(new SubagentStartRequest("scout", "又一个。")
		{
			Model = new string('a', 201),
		});
		Assert.False(tooLong.Ok);
		Assert.Contains("model must be", tooLong.Text);
		await hub.DeliveryBarrier.WaitAsync(TimeSpan.FromSeconds(10));
	}

	[Fact]
	public async Task DelegateCwdStaysInsideTheParentWorkspace()
	{
		Directory.CreateDirectory(Path.Combine(_root, "work"));
		var host = new FakeHost(_root);
		var childStream = new ScriptedStreamFn();
		childStream.EnqueueText("结论。");
		host.Streams.Enqueue(childStream);
		using var parent = CreateParent(new ScriptedStreamFn(), host);
		var hub = Assert.IsType<SubagentHub>(parent.Subagents);

		var started = await hub.StartAsync(new SubagentStartRequest("scout", "查子目录。")
		{
			Cwd = "work",
		});
		Assert.True(started.Ok, started.Text);
		Assert.Equal(Path.Combine(_root, "work"), Assert.Single(host.Contexts).Cwd);

		var missing = await hub.StartAsync(new SubagentStartRequest("scout", "查不存在的目录。")
		{
			Cwd = "missing-dir",
		});
		Assert.False(missing.Ok);
		Assert.Contains("does not exist", missing.Text);

		var outside = await hub.StartAsync(new SubagentStartRequest("scout", "查父目录。")
		{
			Cwd = "..",
		});
		Assert.False(outside.Ok);
		Assert.Contains("inside the parent workspace", outside.Text);

		var both = await hub.StartAsync(new SubagentStartRequest("worker", "查隔离副本。")
		{
			Cwd = "work",
			Worktree = true,
		});
		Assert.False(both.Ok);
		Assert.Contains("cannot be combined with worktree", both.Text);
		await hub.DeliveryBarrier.WaitAsync(TimeSpan.FromSeconds(10));
	}

	[Fact]
	public async Task SteerPendingClearsWhenTheChildReadsTheMessage()
	{
		File.WriteAllText(Path.Combine(_root, "a.txt"), "x");
		var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var parentStream = new ScriptedStreamFn();
		parentStream.EnqueueText("父会话收到。");
		var childStream = new ScriptedStreamFn();
		childStream.Enqueue(model => GatedToolCall(model, "read-1", "read",
			new JsonObject { ["path"] = "a.txt" }, gate.Task));
		childStream.EnqueueText("读完了。");
		var host = new FakeHost(_root, addReadTool: true);
		host.Streams.Enqueue(childStream);

		using var parent = CreateParent(parentStream, host);
		var hub = Assert.IsType<SubagentHub>(parent.Subagents);
		var started = await hub.StartAsync(new SubagentStartRequest("scout", "读文件。"));
		Assert.True(started.Ok, started.Text);
		await Eventually.IsTrue(() => hub.Runs.Single().State == SubagentState.Running, "子代理应开始运行");

		var sent = await hub.SendAsync(started.Name!, "补充一条。", "steer");
		Assert.True(sent.Ok, sent.Text);
		Assert.True(hub.Runs.Single().SteerPending, "steer 入队后应标待送达");

		gate.SetResult();
		await Eventually.IsTrue(() => !hub.Runs.Single().SteerPending, "工具轮后的队列更新应清除待送达");
		await Eventually.IsTrue(() => hub.Runs.Single().State == SubagentState.Idle, "第二轮结束");
		await hub.DeliveryBarrier.WaitAsync(TimeSpan.FromSeconds(10));
	}

	[Fact]
	public async Task SendWhileTheChildSessionIsStartingWaitsInsteadOfFailing()
	{
		var host = new FakeHost(_root)
		{
			CreateGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
		};
		var childStream = new ScriptedStreamFn();
		childStream.EnqueueText("首轮结论。");
		childStream.EnqueueText("第二轮结论。");
		host.Streams.Enqueue(childStream);

		using var parent = CreateParent(new ScriptedStreamFn(), host);
		var hub = Assert.IsType<SubagentHub>(parent.Subagents);

		var start = hub.StartAsync(new SubagentStartRequest("scout", "查一下。"));
		await Eventually.IsTrue(() => host.Contexts.Count == 1, "宿主已开始建会话");

		// delegate 已经返回名字，宿主还没建完会话：这条消息不能按「历史项」冷恢复失败。
		var send = hub.SendAsync("scout-1", "补充一句。");
		Assert.False(send.IsCompleted, "启动窗口内的 send 应等会话建好");

		host.CreateGate.SetResult();
		var started = await start;
		Assert.True(started.Ok, started.Text);
		var sent = await send;
		Assert.True(sent.Ok, sent.Text);

		await Eventually.IsTrue(
			() => childStream.Requests.Any(request => request.Any(message =>
				message is UserMessage user &&
				user.Content.Text?.Contains("补充一句", StringComparison.Ordinal) == true)),
			"启动窗口内排队的消息应进入子会话");
		await hub.DeliveryBarrier.WaitAsync(TimeSpan.FromSeconds(10));
	}

	[Fact]
	public async Task Fork_DropsOldHistoryWhenTheSeedIsTooLarge()
	{
		var model = ScriptedSessions.Model();
		var manager = SessionManager.Create(_root, Path.Combine(_root, "sessions"));
		var host = new FakeHost(_root);
		var childStream = new ScriptedStreamFn();
		childStream.EnqueueText("fork 结论。");
		host.Streams.Enqueue(childStream);
		using var parent = CreateParent(new ScriptedStreamFn(), host, session: manager);

		parent.Session.AppendMessage(new UserMessage
		{
			Content = new string('a', 60_000),
			Timestamp = 1,
		});
		parent.Session.AppendMessage(ScriptedSessions.Blank(model) with
		{
			Content = [new TextContent { Text = "旧回复" }],
			StopReason = StopReason.Stop,
			Timestamp = 2,
		});
		parent.Session.AppendMessage(new UserMessage
		{
			Content = new string('b', 60_000),
			Timestamp = 3,
		});
		parent.Session.AppendMessage(ScriptedSessions.Blank(model) with
		{
			Content = [new TextContent { Text = "新回复" }],
			StopReason = StopReason.Stop,
			Timestamp = 4,
		});

		var hub = Assert.IsType<SubagentHub>(parent.Subagents);
		var started = await hub.StartAsync(new SubagentStartRequest("scout", "带着历史查一下。", Name: null, Context: "fork"));
		Assert.True(started.Ok, started.Text);
		await Eventually.IsTrue(() => hub.Runs.Single().State == SubagentState.Idle, "fork 子代理第一轮结束");

		var seed = host.Contexts.Single().Seed;
		Assert.NotNull(seed);
		Assert.True(seed.Count >= 2);
		Assert.IsType<UserMessage>(seed[0]);
		Assert.DoesNotContain(seed, message =>
			message is UserMessage user &&
			user.Content.Text is { } text &&
			text.StartsWith("aaaa", StringComparison.Ordinal));
		Assert.Contains(seed, message =>
			message is UserMessage user &&
			user.Content.Text == new string('b', 60_000));
	}

	[Fact]
	public async Task Fork_WithoutACompletedTurnFails()
	{
		var manager = SessionManager.Create(_root, Path.Combine(_root, "sessions"));
		using var parent = CreateParent(new ScriptedStreamFn(), new FakeHost(_root), session: manager);
		var hub = Assert.IsType<SubagentHub>(parent.Subagents);

		var started = await hub.StartAsync(new SubagentStartRequest("scout", "接着看。", Context: "fork"));
		Assert.False(started.Ok);
		Assert.Contains("fork needs a completed turn", started.Text);
		Assert.Empty(hub.Runs);
	}

	[Fact]
	public async Task ResultIsHeldWhileTheParentRunsSoWaitAndDeliveryDoNotBothFire()
	{
		// 父会话阻塞在 wait_subagents 上：子代理完成时结果必须攒着，
		// 不能先排成 follow-up 再被 wait 取走 —— 那会让同一段正文进两次上下文。
		var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var parentStream = new ScriptedStreamFn();
		parentStream.Enqueue(model => GatedText(model, "父会话这一轮结束了。", gate.Task));
		var childGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var childStream = new ScriptedStreamFn();
		childStream.Enqueue(model => GatedText(model, "子代理结论。", childGate.Task));
		var host = new FakeHost(_root);
		host.Streams.Enqueue(childStream);

		using var parent = CreateParent(parentStream, host);
		var hub = Assert.IsType<SubagentHub>(parent.Subagents);

		var running = parent.PromptAsync("先跑一轮。");
		await Eventually.IsTrue(() => parent.IsRunning, "父会话应进入运行态");

		var started = await hub.StartAsync(new SubagentStartRequest("scout", "查一下。"));
		Assert.True(started.Ok, started.Text);
		await Eventually.IsTrue(() => hub.Runs.Single().State == SubagentState.Running, "子代理应开始运行");

		// 真的进入 wait，而不是让子代理先跑完再补一个 wait。
		var waiting = hub.WaitAsync([started.Name!], TimeSpan.FromSeconds(5));
		childGate.SetResult();

		var waited = await waiting;
		Assert.True(waited.Ok, waited.Text);
		Assert.Contains("子代理结论", waited.Text);

		// 父会话还在跑：结果不能提前投递（只会补跑一轮请求，不会多出「回报」那一轮）。
		var requestsWhileRunning = parentStream.Requests.Count;
		await Task.Delay(400);
		Assert.Equal(requestsWhileRunning, parentStream.Requests.Count);

		gate.SetResult();
		await running;
		await hub.DeliveryBarrier.WaitAsync(TimeSpan.FromSeconds(5));

		// wait 取走过之后，回合结束时也不该再投一次。
		Assert.DoesNotContain(parentStream.Requests, request => request.Any(message =>
			message is UserMessage { CustomType: SubagentHub.ResultMessageType }));
	}

	[Fact]
	public async Task Restart_RestoresFinishedRunsFromTheSessionFile()
	{
		var host = new FakeHost(_root);
		var childStream = new ScriptedStreamFn();
		childStream.EnqueueText("结论。");
		host.Streams.Enqueue(childStream);

		var manager = SessionManager.Create(_root, Path.Combine(_root, "sessions"));
		var file = manager.SessionFile;
		using (var first = CreateParent(new ScriptedStreamFn(), host, session: manager))
		{
			var hub = Assert.IsType<SubagentHub>(first.Subagents);
			var started = await hub.StartAsync(new SubagentStartRequest("scout", "查一下。"));
			Assert.True(started.Ok, started.Text);
			await Eventually.IsTrue(() => hub.Runs.Single().State == SubagentState.Idle, "第一轮结束");
			await hub.DeliveryBarrier.WaitAsync(TimeSpan.FromSeconds(10));
		}

		foreach (var child in host.Children)
		{
			child.Dispose();
		}

		Assert.NotNull(file);
		var reopened = SessionManager.Open(file);
		Assert.Contains(reopened.Entries, entry => entry is CustomEntry { CustomType: SubagentHub.EntryType });
		using var second = CreateParent(new ScriptedStreamFn(), new FakeHost(_root), session: reopened);
		var restored = Assert.Single(second.Subagents!.Runs);
		Assert.Equal("scout-1", restored.Name);
		Assert.Equal("scout", restored.Agent);
		Assert.Equal("查一下。", restored.Brief);
		Assert.Equal("test/test-model", restored.Model);
		Assert.Equal(SubagentState.Idle, restored.State);
		Assert.Equal(1, restored.Turns);
		Assert.Contains("结论", restored.Result);
		Assert.True(restored.Tokens > 0, "恢复后用量应从条目读回");
		Assert.True(restored.StartedAt > 0, "startedAt 应随条目落盘");
		Assert.True(restored.FinishedAt >= restored.StartedAt, "finishedAt 应随条目落盘");
		Assert.True(restored.Restored);
		Assert.False(string.IsNullOrEmpty(restored.SessionId));
	}

	[Fact]
	public void Restart_FillsMissingTimestampsForV2Entries()
	{
		var manager = SessionManager.Create(_root, Path.Combine(_root, "sessions"));
		var childManager = SessionManager.Create(
			_root,
			Path.Combine(_root, "children"),
			parentSession: manager.SessionFile,
			origin: "subagent");
		childManager.AppendMessage(new UserMessage
		{
			Content = "任务。",
			Timestamp = 1000,
		});
		childManager.AppendMessage(ScriptedSessions.Blank(ScriptedSessions.Model()) with
		{
			Content = [new TextContent { Text = "结论。" }],
			StopReason = StopReason.Stop,
			Timestamp = 2000,
		});
		manager.AppendCustom(SubagentHub.EntryType, new JsonObject
		{
			["kind"] = "start",
			["name"] = "scout-1",
			["agent"] = "scout",
			["session"] = childManager.SessionId,
			["file"] = childManager.SessionFile,
			["depth"] = 1,
			["brief"] = "查一下。",
		});
		manager.AppendCustom(SubagentHub.EntryType, new JsonObject
		{
			["kind"] = "idle",
			["name"] = "scout-1",
			["agent"] = "scout",
			["session"] = childManager.SessionId,
			["file"] = childManager.SessionFile,
			["depth"] = 1,
			["tokensVersion"] = 2,
			["turns"] = 1,
			["result"] = "结论。",
		});

		Assert.NotNull(manager.SessionFile);
		using var parent = CreateParent(
			new ScriptedStreamFn(),
			new FakeHost(_root),
			session: SessionManager.Open(manager.SessionFile));

		var restored = Assert.Single(parent.Subagents!.Runs);
		Assert.True(restored.StartedAt > 0);
		Assert.True(restored.FinishedAt >= restored.StartedAt);
	}

	[Fact]
	public void DelegateDescriptionOnlyListsAdvertisedRoles()
	{
		var agents = Path.Combine(_root, ".pi", "agents");
		Directory.CreateDirectory(agents);
		File.WriteAllText(Path.Combine(agents, "hidden.md"),
			"""
			---
			name: hidden
			description: hidden description
			---
			Hidden role.
			""");
		File.WriteAllText(Path.Combine(agents, "shown.md"),
			"""
			---
			name: shown
			description: shown description
			advertise: true
			---
			Shown role.
			""");

		var catalog = SubagentCatalog.Discover(_root, "missing-agent-dir", includeProject: true);
		Assert.False(catalog.Get("hidden")!.Advertise);
		Assert.True(catalog.Get("shown")!.Advertise);
		Assert.True(catalog.Get("scout")!.Advertise);

		using var parent = CreateParent(new ScriptedStreamFn(), new FakeHost(_root), catalog: catalog);
		var tool = parent.Tools.Single(item => item.Name == SubagentTools.DelegateName);
		Assert.Contains("shown", tool.Description);
		Assert.DoesNotContain("hidden", tool.Description);
		Assert.Contains("+1 more", tool.Description);

		var hub = Assert.IsType<SubagentHub>(parent.Subagents);
		var listing = hub.Describe(out var details);
		Assert.Contains("hidden: hidden description (not advertised)", listing);
		Assert.Contains(details["available"]!.AsArray(), static row =>
			row!["name"]!.GetValue<string>() == "hidden" &&
			row["advertise"]!.GetValue<bool>() == false);
	}

	[Fact]
	public void OverridingABuiltinKeepsItsAdvertisedDefaultUnlessSpecified()
	{
		var agents = Path.Combine(_root, ".pi", "agents");
		Directory.CreateDirectory(agents);
		var path = Path.Combine(agents, "scout.md");
		File.WriteAllText(path,
			"""
			---
			name: scout
			description: custom scout
			---
			Custom scout.
			""");
		Assert.True(SubagentCatalog.Discover(_root, "missing-agent-dir", includeProject: true).Get("scout")!.Advertise);

		File.WriteAllText(path,
			"""
			---
			name: scout
			description: custom scout
			advertise: false
			---
			Custom scout.
			""");
		Assert.False(SubagentCatalog.Discover(_root, "missing-agent-dir", includeProject: true).Get("scout")!.Advertise);
	}

	[Fact]
	public void DelegateGuidelinesDescribeWhenToDelegate()
	{
		using var parent = CreateParent(new ScriptedStreamFn(), new FakeHost(_root));
		var tools = SubagentTools.Create(Assert.IsType<SubagentHub>(parent.Subagents));
		var delegateTool = tools.Single(tool => tool.Tool.Name == SubagentTools.DelegateName);
		Assert.Contains(delegateTool.Guidelines, static guideline => guideline.Contains("Direct work is the default", StringComparison.Ordinal));
		Assert.Contains(delegateTool.Guidelines, static guideline => guideline.Contains("Use scout before planning", StringComparison.Ordinal));
	}

	[Fact]
	public void BuiltinSubagentPromptTemplatesFollowTheHost()
	{
		using var withHost = new CodingAgentSession(new CodingAgentSessionOptions
		{
			Cwd = _root,
			Model = ScriptedSessions.Model(),
			StreamFn = new ScriptedStreamFn().Invoke,
			Session = SessionManager.InMemory(_root),
			Persist = false,
			Skills = [],
			Settings = new CodingAgentSettings(),
			SubagentHost = new FakeHost(_root),
			Agents = Builtins,
		});
		Assert.Contains(withHost.PromptTemplates, static template =>
			template.Name == "scout" && template.Origin.Scope == PiWeft.CodingAgent.Resources.ResourceScope.Builtin);

		using var withoutHost = new CodingAgentSession(new CodingAgentSessionOptions
		{
			Cwd = _root,
			Model = ScriptedSessions.Model(),
			StreamFn = new ScriptedStreamFn().Invoke,
			Session = SessionManager.InMemory(_root),
			Persist = false,
			Skills = [],
			Settings = new CodingAgentSettings(),
		});
		Assert.DoesNotContain(withoutHost.PromptTemplates, static template =>
			template.Origin.Scope == PiWeft.CodingAgent.Resources.ResourceScope.Builtin);
	}

	[Fact]
	public void DelegateDescriptionCapsTheAgentCatalog()
	{
		var agents = Path.Combine(_root, ".pi", "agents");
		Directory.CreateDirectory(agents);
		for (var index = 1; index <= 30; index++)
		{
			File.WriteAllText(Path.Combine(agents, $"agent-{index}.md"),
				$"""
				---
				name: agent-{index}
				description: {new string('x', 300)}
				advertise: true
				---
				Do the work.
				""");
		}

		var catalog = SubagentCatalog.Discover(_root, "missing-agent-dir", includeProject: true);
		using var parent = CreateParent(new ScriptedStreamFn(), new FakeHost(_root), catalog: catalog);
		var tool = parent.Tools.Single(item => item.Name == SubagentTools.DelegateName);
		Assert.Contains("+10 more", tool.Description);
		Assert.DoesNotContain(new string('x', 201), tool.Description);
	}

	[Fact]
	public void Restart_RecomputesLegacyTokenCounts()
	{
		var manager = SessionManager.Create(_root, Path.Combine(_root, "sessions"));
		var childManager = SessionManager.Create(
			_root,
			Path.Combine(_root, "children"),
			parentSession: manager.SessionFile,
			origin: "subagent");
		childManager.AppendMessage(new UserMessage
		{
			Content = "任务。",
			Timestamp = 1,
		});
		childManager.AppendMessage(ScriptedSessions.Blank(ScriptedSessions.Model()) with
		{
			Content = [new TextContent { Text = "结论。" }],
			StopReason = StopReason.Stop,
			Usage = new Usage { Input = 10, Output = 5, CacheRead = 100, TotalTokens = 115 },
			Timestamp = 2,
		});
		manager.AppendCustom(SubagentHub.EntryType, new JsonObject
		{
			["kind"] = "start",
			["name"] = "scout-1",
			["agent"] = "scout",
			["session"] = childManager.SessionId,
			["file"] = childManager.SessionFile,
			["depth"] = 1,
		});
		manager.AppendCustom(SubagentHub.EntryType, new JsonObject
		{
			["kind"] = "idle",
			["name"] = "scout-1",
			["agent"] = "scout",
			["session"] = childManager.SessionId,
			["file"] = childManager.SessionFile,
			["depth"] = 1,
			["turns"] = 1,
			["tokens"] = 115,
			["result"] = "旧结果。",
		});

		Assert.NotNull(manager.SessionFile);
		using var parent = CreateParent(
			new ScriptedStreamFn(),
			new FakeHost(_root),
			session: SessionManager.Open(manager.SessionFile));

		var restored = Assert.Single(parent.Subagents!.Runs);
		Assert.Equal(15, restored.Tokens);
		Assert.True(restored.StartedAt > 0);
		Assert.True(restored.FinishedAt >= restored.StartedAt);
	}

	[Fact]
	public async Task SendReopensARestoredRunFromItsSessionFile()
	{
		var host = new FakeHost(_root, persistChildren: true);
		var first = new ScriptedStreamFn();
		first.EnqueueText("第一轮结论。");
		host.Streams.Enqueue(first);

		var manager = SessionManager.Create(_root, Path.Combine(_root, "sessions"));
		var file = manager.SessionFile;
		using (var initial = CreateParent(new ScriptedStreamFn(), host, session: manager))
		{
			var hub = Assert.IsType<SubagentHub>(initial.Subagents);
			var started = await hub.StartAsync(new SubagentStartRequest("scout", "查一下。"));
			Assert.True(started.Ok, started.Text);
			await Eventually.IsTrue(() => hub.Runs.Single().State == SubagentState.Idle, "第一轮结束");
			await hub.DeliveryBarrier.WaitAsync(TimeSpan.FromSeconds(10));
		}

		foreach (var child in host.Children)
		{
			child.Dispose();
		}

		Assert.NotNull(file);

		// 「重启」：新进程从会话文件恢复出历史运行，此时没有活会话。
		var reopenedHost = new FakeHost(_root, persistChildren: true);
		var followUp = new ScriptedStreamFn();
		followUp.EnqueueText("补充后的结论。");
		reopenedHost.Streams.Enqueue(followUp);

		var reopenedParent = SessionManager.Open(file);
		using var second = CreateParent(new ScriptedStreamFn(), reopenedHost, session: reopenedParent);
		var restored = Assert.Single(second.Subagents!.Runs);
		Assert.True(restored.Restored);
		Assert.NotNull(restored.SessionFile);

		var sent = await second.Subagents.SendAsync(restored.Name, "再补一条。");
		Assert.True(sent.Ok, sent.Text);
		Assert.Equal(1, reopenedHost.ReopenCount);

		await Eventually.IsTrue(
			() => second.Subagents.Runs.Single().Turns == 2,
			"冷恢复后应能继续跑一轮");
		var run = second.Subagents.Runs.Single();
		Assert.False(run.Restored);
		Assert.Contains("补充后的结论", run.Result!);
	}

	[Fact]
	public async Task ConcurrentSendReopensARestoredRunOnlyOnce()
	{
		var host = new FakeHost(_root, persistChildren: true);
		var first = new ScriptedStreamFn();
		first.EnqueueText("第一轮结论。");
		host.Streams.Enqueue(first);

		var manager = SessionManager.Create(_root, Path.Combine(_root, "sessions"));
		var file = manager.SessionFile;
		using (var initial = CreateParent(new ScriptedStreamFn(), host, session: manager))
		{
			var hub = Assert.IsType<SubagentHub>(initial.Subagents);
			var started = await hub.StartAsync(new SubagentStartRequest("scout", "查一下。"));
			Assert.True(started.Ok, started.Text);
			await Eventually.IsTrue(() => hub.Runs.Single().State == SubagentState.Idle, "第一轮结束");
			await hub.DeliveryBarrier.WaitAsync(TimeSpan.FromSeconds(10));
		}

		foreach (var child in host.Children)
		{
			child.Dispose();
		}

		Assert.NotNull(file);
		var reopenedHost = new FakeHost(_root, persistChildren: true);
		var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		reopenedHost.ReopenGate = gate;
		var followUp = new ScriptedStreamFn();
		followUp.EnqueueText("补充结论一。");
		followUp.EnqueueText("补充结论二。");
		reopenedHost.Streams.Enqueue(followUp);

		using var second = CreateParent(new ScriptedStreamFn(), reopenedHost, session: SessionManager.Open(file));
		var restored = Assert.Single(second.Subagents!.Runs);
		Assert.True(restored.Restored);

		var firstSend = second.Subagents.SendAsync(restored.Name, "第一条补充。");
		var secondSend = second.Subagents.SendAsync(restored.Name, "第二条补充。");
		await Eventually.IsTrue(() => reopenedHost.ReopenCount == 1, "只允许一个调用方进入恢复");
		gate.SetResult();

		var outcomes = await Task.WhenAll(firstSend, secondSend);
		Assert.All(outcomes, outcome => Assert.True(outcome.Ok, outcome.Text));
		Assert.Equal(1, reopenedHost.ReopenCount);
		await Eventually.IsTrue(
			() => second.Subagents.Runs.Single().State == SubagentState.Idle,
			"两条消息最终都收尾");
	}

	[Fact]
	public async Task SendWithoutAReopenableSessionFails()
	{
		var host = new FakeHost(_root, persistChildren: true);
		var first = new ScriptedStreamFn();
		first.EnqueueText("第一轮结论。");
		host.Streams.Enqueue(first);

		var manager = SessionManager.Create(_root, Path.Combine(_root, "sessions"));
		var file = manager.SessionFile!;
		using (var initial = CreateParent(new ScriptedStreamFn(), host, session: manager))
		{
			var hub = Assert.IsType<SubagentHub>(initial.Subagents);
			var started = await hub.StartAsync(new SubagentStartRequest("scout", "查一下。"));
			Assert.True(started.Ok, started.Text);
			await Eventually.IsTrue(() => hub.Runs.Single().State == SubagentState.Idle, "第一轮结束");
			await hub.DeliveryBarrier.WaitAsync(TimeSpan.FromSeconds(10));
		}

		foreach (var child in host.Children)
		{
			child.Dispose();
		}

		var reopenedParent = SessionManager.Open(file);
		using var second = CreateParent(new ScriptedStreamFn(), new FakeHost(_root, persistChildren: true), session: reopenedParent);
		var restored = Assert.Single(second.Subagents!.Runs);

		// 子会话文件被删掉：历史项还在，但打不开，只能报错。
		Assert.NotNull(restored.SessionFile);
		File.Delete(restored.SessionFile!);

		var sent = await second.Subagents.SendAsync(restored.Name, "再补一条。");
		Assert.False(sent.Ok);
		Assert.Contains("cannot be reopened", sent.Text);
	}

	[Fact]
	public async Task SendReopensARunWhoseChildTabWasClosed()
	{
		var host = new FakeHost(_root, persistChildren: true);
		var first = new ScriptedStreamFn();
		first.EnqueueText("第一轮结论。");
		host.Streams.Enqueue(first);
		var followUp = new ScriptedStreamFn();
		followUp.EnqueueText("第二轮结论。");
		host.Streams.Enqueue(followUp);

		using var parent = CreateParent(new ScriptedStreamFn(), host);
		var hub = Assert.IsType<SubagentHub>(parent.Subagents);
		var started = await hub.StartAsync(new SubagentStartRequest("scout", "查一下。"));
		Assert.True(started.Ok, started.Text);
		await Eventually.IsTrue(() => hub.Runs.Single().State == SubagentState.Idle, "第一轮结束");

		// 关掉子会话标签：状态落到 stopped，但关系还在。
		Assert.Single(host.Children).Dispose();
		Assert.Equal(SubagentState.Stopped, hub.Runs.Single().State);

		var sent = await hub.SendAsync(started.Name!, "接着看。");
		Assert.True(sent.Ok, sent.Text);
		Assert.Equal(1, host.ReopenCount);
		await Eventually.IsTrue(() => hub.Runs.Single().Turns == 2, "重开后应能继续跑");
	}

	[Fact]
	public async Task StopThenSendStillDeliversTheNextTurn()
	{
		// 停过一回合之后再发消息：Stopping 不能一直挂着，否则下一轮的结论会被当成「已停掉」吞掉。
		var childStream = new ScriptedStreamFn();
		childStream.EnqueueText("第一轮结论。");
		childStream.EnqueueText("第二轮结论。");
		var host = new FakeHost(_root, persistChildren: true);
		host.Streams.Enqueue(childStream);

		using var parent = CreateParent(new ScriptedStreamFn(), host);
		var hub = Assert.IsType<SubagentHub>(parent.Subagents);
		var started = await hub.StartAsync(new SubagentStartRequest("scout", "先跑一轮。"));
		Assert.True(started.Ok, started.Text);
		await Eventually.IsTrue(() => hub.Runs.Single().State == SubagentState.Idle, "第一轮结束");

		var stopped = hub.Stop(started.Name!);
		Assert.True(stopped.Ok, stopped.Text);
		Assert.Equal(SubagentState.Stopped, hub.Runs.Single().State);

		var sent = await hub.SendAsync(started.Name!, "接着做。");
		Assert.True(sent.Ok, sent.Text);

		await Eventually.IsTrue(() => hub.Runs.Single().Turns == 2, "第二轮应正常收尾");
		Assert.Equal(SubagentState.Idle, hub.Runs.Single().State);
		Assert.Contains("第二轮结论", hub.Runs.Single().Result!);
	}

	[Fact]
	public async Task Send_RejectsWhileTheStoppedRunIsStillAborting()
	{
		var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var childStream = new ScriptedStreamFn();
		childStream.Enqueue(model =>
		{
			firstStarted.TrySetResult();
			return GatedText(model, "慢慢来。", gate.Task);
		});
		var host = new FakeHost(_root);
		host.Streams.Enqueue(childStream);
		var parent = CreateParent(new ScriptedStreamFn(), host);
		try
		{
			var hub = Assert.IsType<SubagentHub>(parent.Subagents);
			var started = await hub.StartAsync(new SubagentStartRequest("scout", "慢任务。"));
			Assert.True(started.Ok, started.Text);
			await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
			await Eventually.IsTrue(() => hub.Runs.Single().State == SubagentState.Running, "子代理进入运行态");

			hub.Stop(started.Name!);
			var sent = await hub.SendAsync(started.Name!, "接着做。");
			Assert.False(sent.Ok);
			Assert.Contains("still stopping", sent.Text);

			gate.SetResult();
			await Eventually.IsTrue(() => !host.Children[0].IsRunning, "子代理收尾");
		}
		finally
		{
			gate.TrySetResult();
			parent.Dispose();
			foreach (var child in host.Children)
			{
				child.Dispose();
			}
		}
	}

	[Fact]
	public async Task StopDuringStartDoesNotLaunchTheFirstTurn()
	{
		// 建子会话是慢活：这期间停下，不能再起首轮。
		var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var host = new SlowHost(_root, gate.Task);
		using var parent = CreateParent(new ScriptedStreamFn(), host);
		var hub = Assert.IsType<SubagentHub>(parent.Subagents);

		var starting = hub.StartAsync(new SubagentStartRequest("scout", "慢启动。"));
		await Eventually.IsTrue(() => hub.Runs.Count == 1, "先登记");

		var stopped = hub.Stop("scout-1");
		Assert.True(stopped.Ok, stopped.Text);

		gate.SetResult();
		var outcome = await starting;
		Assert.False(outcome.Ok);
		Assert.Contains("stopped before its first turn", outcome.Text);
		Assert.Equal(SubagentState.Stopped, hub.Runs.Single().State);
		// 会话建出来了但没有跑首轮：没有消息写进它的会话文件。
		Assert.Empty(host.Children[0].Session.Entries.OfType<SessionMessageEntry>());
	}

	[Fact]
	public async Task ClosingTheChildSessionMarksTheRunStopped()
	{
		var childStream = new ScriptedStreamFn();
		childStream.EnqueueText("结论。");
		var host = new FakeHost(_root);
		host.Streams.Enqueue(childStream);
		using var parent = CreateParent(new ScriptedStreamFn(), host);
		var hub = Assert.IsType<SubagentHub>(parent.Subagents);

		var started = await hub.StartAsync(new SubagentStartRequest("scout", "查一下。"));
		Assert.True(started.Ok, started.Text);
		await Eventually.IsTrue(() => hub.Runs.Single().State == SubagentState.Idle, "第一轮结束");

		Assert.Single(host.Children).Dispose();

		Assert.Equal(SubagentState.Stopped, hub.Runs.Single().State);
	}

	[Fact]
	public async Task ClosingTheParentStopsRunningChildren()
	{
		var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var childStream = new ScriptedStreamFn();
		childStream.Enqueue(model =>
		{
			firstStarted.TrySetResult();
			return GatedText(model, "慢慢来。", gate.Task);
		});
		var host = new FakeHost(_root);
		host.Streams.Enqueue(childStream);
		var parent = CreateParent(new ScriptedStreamFn(), host);
		try
		{
			var hub = Assert.IsType<SubagentHub>(parent.Subagents);
			var started = await hub.StartAsync(new SubagentStartRequest("scout", "慢任务。"));
			Assert.True(started.Ok, started.Text);
			await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
			await Eventually.IsTrue(() => hub.Runs.Single().State == SubagentState.Running, "子代理进入运行态");

			parent.Dispose();

			Assert.Equal(SubagentState.Stopped, hub.Runs.Single().State);
			gate.SetResult();
			await Eventually.IsTrue(
				() => host.Children.Count == 1 && !host.Children[0].IsRunning,
				"关父会话后子代理应停止");
		}
		finally
		{
			gate.TrySetResult();
			parent.Dispose();
			foreach (var child in host.Children)
			{
				child.Dispose();
			}
		}
	}

	[Fact]
	public async Task ActivityTracksTheToolTheChildIsRunningAndClearsWhenTheTurnEnds()
	{
		File.WriteAllText(Path.Combine(_root, "Auth.cs"), "class Auth {}");
		var childStream = new ScriptedStreamFn();
		childStream.EnqueueToolCall("call-1", "read", new JsonObject { ["path"] = "Auth.cs" });
		childStream.EnqueueText("读完了。");
		var host = new FakeHost(_root, addReadTool: true);
		host.Streams.Enqueue(childStream);

		using var parent = CreateParent(new ScriptedStreamFn(), host);
		var hub = Assert.IsType<SubagentHub>(parent.Subagents);

		// read 是微秒级工具：轮询抓 Activity 的窗口几乎不存在，改成记录每次状态推送里看到的值。
		var seen = new List<string?>();
		hub.Changed += () =>
		{
			foreach (var run in hub.Runs)
			{
				lock (seen)
				{
					seen.Add(run.Activity);
				}
			}
		};

		var started = await hub.StartAsync(new SubagentStartRequest("scout", "读一下。"));
		Assert.True(started.Ok, started.Text);
		await Eventually.IsTrue(() => hub.Runs.Single().State == SubagentState.Idle, "子代理应结束");

		lock (seen)
		{
			Assert.Contains(seen, activity => activity is { Length: > 0 } && activity.StartsWith("read Auth.cs", StringComparison.Ordinal));
		}

		Assert.Null(hub.Runs.Single().Activity);
	}

	[Fact]
	public async Task ProgressShowsTheLastAssistantLineWhileTheTurnContinues()
	{
		File.WriteAllText(Path.Combine(_root, "Auth.cs"), "class Auth {}");
		var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var childStream = new ScriptedStreamFn();
		childStream.Enqueue(model => TextThenToolCall(model, "先看一眼。", "call-1", "read", new JsonObject { ["path"] = "Auth.cs" }));
		childStream.Enqueue(model => GatedText(model, "读完了。", gate.Task));
		var host = new FakeHost(_root, addReadTool: true);
		host.Streams.Enqueue(childStream);

		using var parent = CreateParent(new ScriptedStreamFn(), host);
		var hub = Assert.IsType<SubagentHub>(parent.Subagents);
		var started = await hub.StartAsync(new SubagentStartRequest("scout", "读一下。"));
		Assert.True(started.Ok, started.Text);

		await Eventually.IsTrue(
			() => hub.Runs.Single().Progress == "先看一眼。",
			"工具轮之后的助手正文应成为进度");
		await Eventually.IsTrue(
			() => hub.Runs.Single().Activity is null,
			"工具结束后卡片不该还停在 read");

		gate.SetResult();
		await Eventually.IsTrue(() => hub.Runs.Single().State == SubagentState.Idle, "子代理结束");
		Assert.Null(hub.Runs.Single().Progress);
	}

	[Fact]
	public async Task ClearWorktreeDropsThePathFromTheRun()
	{
		var host = new FakeHost(_root);
		host.Streams.Enqueue(new ScriptedStreamFn());
		using var parent = CreateParent(new ScriptedStreamFn(), host);
		var hub = Assert.IsType<SubagentHub>(parent.Subagents);
		var started = await hub.StartAsync(new SubagentStartRequest("worker", "写一下。") { Worktree = true });
		Assert.True(started.Ok, started.Text);
		Assert.NotNull(hub.Runs.Single().WorktreePath);

		Assert.True(hub.ClearWorktree(started.Name!));
		var run = hub.Runs.Single();
		Assert.Null(run.WorktreePath);
		Assert.Null(run.WorktreeBranch);
	}

	[Fact]
	public void TeamBoardClaimsCompletesAndRejectsStaleRevisions()
	{
		using var parent = CreateParent(new ScriptedStreamFn(), new FakeHost(_root), team: true);
		var board = Assert.IsType<TeamBoard>(Assert.IsType<SubagentHub>(parent.Subagents).Team);

		var created = board.Create("lead", "写登录流程", "改 Auth.cs 并跑测试。", null, ["src/Auth.cs"]);
		Assert.True(created.Ok, created.Text);
		var task = Assert.Single(board.Tasks);
		Assert.Equal("task-1", task.Id);
		Assert.Equal(TeamTaskStatus.Pending, task.Status);

		var claimed = board.Update("worker-1", task.Id, task.Revision, "claim");
		Assert.True(claimed.Ok, claimed.Text);
		var stale = board.Update("lead", task.Id, task.Revision, "complete");
		Assert.False(stale.Ok);
		Assert.Contains("stale", stale.Text);

		var completed = board.Update("worker-1", task.Id, task.Revision + 1, "complete");
		Assert.True(completed.Ok, completed.Text);
		Assert.Equal(TeamTaskStatus.Completed, board.Get(task.Id).Details!["task"]!["status"]!.GetValue<string>());
	}

	[Fact]
	public void TeamBoardBlocksUnreadyDependenciesAndDetectsCycles()
	{
		using var parent = CreateParent(new ScriptedStreamFn(), new FakeHost(_root), team: true);
		var board = Assert.IsType<TeamBoard>(Assert.IsType<SubagentHub>(parent.Subagents).Team);

		Assert.True(board.Create("lead", "先做 A", "A 的说明。", null, null).Ok);
		Assert.True(board.Create("lead", "再做 B", "B 的说明。", ["task-1"], null).Ok);

		var blocked = board.Update("worker-1", "task-2", 1, "claim");
		Assert.False(blocked.Ok);
		Assert.Contains("dependencies", blocked.Text);

		// task-1 依赖 task-2，而 task-2 已依赖 task-1：成环。
		var cycle = board.Update("lead", "task-1", 1, "set_dependencies", blockedBy: ["task-2"]);
		Assert.False(cycle.Ok);
		Assert.Contains("cycle", cycle.Text);

		Assert.True(board.Update("lead", "task-1", 1, "complete").Ok);
		Assert.True(board.Update("worker-1", "task-2", 1, "claim").Ok);
	}

	[Fact]
	public void TeamBoardRestoresFromTheParentSessionLog()
	{
		using var parent = CreateParent(new ScriptedStreamFn(), new FakeHost(_root), team: true);
		var hub = Assert.IsType<SubagentHub>(parent.Subagents);
		var board = Assert.IsType<TeamBoard>(hub.Team);
		Assert.True(board.Create("lead", "持久化", "重启后还要在。", null, ["src/A.cs"]).Ok);

		using var reopened = new SubagentHub(
			parent,
			new FakeHost(_root),
			hub.Catalog,
			0,
			new SubagentLimits { Team = true });
		var restored = Assert.IsType<TeamBoard>(reopened.Team);
		var task = Assert.Single(restored.Tasks);
		Assert.Equal("task-1", task.Id);
		Assert.Equal("持久化", task.Subject);
		Assert.Equal(["src/A.cs"], task.WriteScopes);
	}

	[Fact]
	public void TeamToolsAreRegisteredForTheParentWhenTeamIsOn()
	{
		using var parent = CreateParent(new ScriptedStreamFn(), new FakeHost(_root), team: true);
		var names = parent.Tools.Select(static tool => tool.Name).ToHashSet(StringComparer.Ordinal);
		Assert.Contains(TeamTools.CreateName, names);
		Assert.Contains(TeamTools.ListName, names);
		Assert.Contains(TeamTools.GetName, names);
		Assert.Contains(TeamTools.UpdateName, names);

		using var plain = CreateParent(new ScriptedStreamFn(), new FakeHost(_root));
		Assert.DoesNotContain(TeamTools.CreateName, plain.Tools.Select(static tool => tool.Name));
	}

	[Fact]
	public async Task WorktreeRequiresAWritingRole()
	{
		using var parent = CreateParent(new ScriptedStreamFn(), new FakeHost(_root));
		var hub = Assert.IsType<SubagentHub>(parent.Subagents);

		var rejected = await hub.StartAsync(new SubagentStartRequest("scout", "查一下。") { Worktree = true });
		Assert.False(rejected.Ok);
		Assert.Contains("worktree", rejected.Text);
	}

	[Fact]
	public async Task WorktreeStartsAWriterWithABranchAndPassesItToTheHost()
	{
		var host = new FakeHost(_root);
		host.Streams.Enqueue(new ScriptedStreamFn());
		var parentStream = new ScriptedStreamFn();
		parentStream.EnqueueText("父会话收到。");
		using var parent = CreateParent(parentStream, host);
		var hub = Assert.IsType<SubagentHub>(parent.Subagents);

		var started = await hub.StartAsync(new SubagentStartRequest("worker", "写一下。") { Worktree = true });
		Assert.True(started.Ok, started.Text);
		var run = hub.Runs.Single();
		Assert.StartsWith("piweft/", run.WorktreeBranch, StringComparison.Ordinal);
		Assert.NotNull(run.WorktreePath);
		var context = Assert.Single(host.Contexts);
		Assert.True(context.Worktree);
		Assert.Equal(run.WorktreeBranch, context.WorktreeBranch);
	}

	[Fact]
	public async Task WriterLimit_RejectsASecondWritingSubagent()
	{
		var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var firstStream = new ScriptedStreamFn();
		firstStream.Enqueue(model => GatedText(model, "第一轮。", gate.Task));
		var scoutStream = new ScriptedStreamFn();
		scoutStream.EnqueueText("查完了。");
		var thirdStream = new ScriptedStreamFn();
		thirdStream.EnqueueText("第三轮。");
		var host = new FakeHost(_root);
		host.Streams.Enqueue(firstStream);
		host.Streams.Enqueue(scoutStream);
		host.Streams.Enqueue(thirdStream);

		var parentStream = new ScriptedStreamFn();
		for (var index = 0; index < 4; index++)
		{
			parentStream.EnqueueText("父会话收到。");
		}

		using var parent = CreateParent(parentStream, host, maxWriters: 1);
		var hub = Assert.IsType<SubagentHub>(parent.Subagents);

		var first = await hub.StartAsync(new SubagentStartRequest("worker", "先写一个。"));
		Assert.True(first.Ok, first.Text);
		await Eventually.IsTrue(() => hub.Runs.Single(run => run.Name == first.Name).State == SubagentState.Running, "第一个写者应占住名额");

		var second = await hub.StartAsync(new SubagentStartRequest("worker", "同时再写一个。"));
		Assert.False(second.Ok);
		Assert.Contains("writer limit", second.Text);

		// 只读子代理不占写者名额。
		var scout = await hub.StartAsync(new SubagentStartRequest("scout", "只读查一下。"));
		Assert.True(scout.Ok, scout.Text);

		gate.SetResult();
		await Eventually.IsTrue(() => hub.Runs.Single(run => run.Name == first.Name).State == SubagentState.Idle, "第一个写者结束");
		var third = await hub.StartAsync(new SubagentStartRequest("worker", "现在可以写了。"));
		Assert.True(third.Ok, third.Text);
	}

	[Fact]
	public async Task WriterLimit_AppliesWhenAnIdleWriterContinues()
	{
		var firstGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var secondGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var firstStream = new ScriptedStreamFn();
		firstStream.Enqueue(model => GatedText(model, "第一轮。", firstGate.Task));
		firstStream.Enqueue(model => GatedText(model, "第二轮。", secondGate.Task));
		var secondStream = new ScriptedStreamFn();
		secondStream.EnqueueText("另一个。");
		var host = new FakeHost(_root);
		host.Streams.Enqueue(firstStream);
		host.Streams.Enqueue(secondStream);

		var parentStream = new ScriptedStreamFn();
		for (var index = 0; index < 4; index++)
		{
			parentStream.EnqueueText("父会话收到。");
		}

		using var parent = CreateParent(parentStream, host, maxWriters: 1);
		var hub = Assert.IsType<SubagentHub>(parent.Subagents);
		var first = await hub.StartAsync(new SubagentStartRequest("worker", "先写。"));
		Assert.True(first.Ok, first.Text);
		firstGate.SetResult();
		await Eventually.IsTrue(() => hub.Runs.Single().State == SubagentState.Idle, "第一轮结束");
		await hub.DeliveryBarrier.WaitAsync(TimeSpan.FromSeconds(10));

		var sent = await hub.SendAsync(first.Name!, "再写一轮。");
		Assert.True(sent.Ok, sent.Text);
		await Eventually.IsTrue(() => hub.Runs.Single().State == SubagentState.Running, "继续的一轮应重新占名额");

		var second = await hub.StartAsync(new SubagentStartRequest("worker", "同时写。"));
		Assert.False(second.Ok);
		Assert.Contains("writer limit", second.Text);
		secondGate.SetResult();
	}

	[Fact]
	public async Task ChangedFilesAreRecordedForWriteTools()
	{
		var childStream = new ScriptedStreamFn();
		childStream.Enqueue(model => TextThenToolCall(
			model,
			"先写一个文件。",
			"call-1",
			"write",
			new JsonObject { ["path"] = "changed.txt", ["content"] = "hello" }));
		childStream.EnqueueText("写完了。");
		var host = new FakeHost(_root);
		host.Streams.Enqueue(childStream);

		using var parent = CreateParent(new ScriptedStreamFn(), host);
		var hub = Assert.IsType<SubagentHub>(parent.Subagents);
		var started = await hub.StartAsync(new SubagentStartRequest("worker", "改个东西。"));
		Assert.True(started.Ok, started.Text);

		await Eventually.IsTrue(() => hub.Runs.Single().ChangedFiles.Count == 1, "写工具的路径应记进改动文件");
		var run = hub.Runs.Single();
		Assert.Contains("changed.txt", run.ChangedFiles[0], StringComparison.Ordinal);
		Assert.False(run.MayWrite);
		await Eventually.IsTrue(() => hub.Runs.Single().State == SubagentState.Idle, "子代理结束");
	}

	[Fact]
	public async Task ShellToolsMarkTheRunAsPossibleWriter()
	{
		var childStream = new ScriptedStreamFn();
		childStream.Enqueue(model => TextThenToolCall(
			model,
			"跑一条命令。",
			"call-1",
			"bash",
			new JsonObject { ["command"] = "true" }));
		childStream.EnqueueText("跑完了。");
		var host = new FakeHost(_root);
		host.Streams.Enqueue(childStream);

		using var parent = CreateParent(new ScriptedStreamFn(), host);
		var hub = Assert.IsType<SubagentHub>(parent.Subagents);
		var started = await hub.StartAsync(new SubagentStartRequest("worker", "跑一下。"));
		Assert.True(started.Ok, started.Text);

		await Eventually.IsTrue(() => hub.Runs.Single().MayWrite, "shell 工具应标可能写入");
		Assert.Empty(hub.Runs.Single().ChangedFiles);
	}

	[Fact]
	public void BuiltinCatalogIncludesTheAdvisoryOracle()
	{
		var oracle = Builtins.Get("oracle");
		Assert.NotNull(oracle);
		Assert.Contains("read", oracle.Tools!);
		Assert.DoesNotContain("edit", oracle.Tools!);
		Assert.Equal(4, Builtins.All.Count);
	}

	[Fact]
	public async Task LabelIsTheFirstLineOfTheBrief()
	{
		var host = new FakeHost(_root);
		host.Streams.Enqueue(new ScriptedStreamFn());
		using var parent = CreateParent(new ScriptedStreamFn(), host);
		var hub = Assert.IsType<SubagentHub>(parent.Subagents);

		var started = await hub.StartAsync(new SubagentStartRequest("scout", "第一行任务。\n第二行细节。"));
		Assert.True(started.Ok, started.Text);
		var run = hub.Runs.Single();
		Assert.Equal("第一行任务。", run.Label);
		Assert.Equal("第一行任务。\n第二行细节。", run.Brief);
		Assert.Equal("test/test-model", run.Model);
		Assert.Equal("off", run.Thinking);
		Assert.DoesNotContain("\n", run.Label);
	}

	[Fact]
	public async Task ExplicitLabelOverridesTheBriefFirstLine()
	{
		var host = new FakeHost(_root);
		host.Streams.Enqueue(new ScriptedStreamFn());
		using var parent = CreateParent(new ScriptedStreamFn(), host);
		var hub = Assert.IsType<SubagentHub>(parent.Subagents);

		var started = await hub.StartAsync(new SubagentStartRequest("scout", "查一下登录入口。")
		{
			Label = "登录入口",
		});
		Assert.True(started.Ok, started.Text);
		Assert.Equal("登录入口", hub.Runs.Single().Label);
	}

	[Fact]
	public async Task NoteChildAwaitingTracksTheQuestionAndShowsInDetails()
	{
		var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var childStream = new ScriptedStreamFn();
		childStream.Enqueue(model => GatedText(model, "等你决定。", gate.Task));
		var host = new FakeHost(_root);
		host.Streams.Enqueue(childStream);

		using var parent = CreateParent(new ScriptedStreamFn(), host);
		var hub = Assert.IsType<SubagentHub>(parent.Subagents);
		var started = await hub.StartAsync(new SubagentStartRequest("worker", "先想清楚再动手。"));
		Assert.True(started.Ok, started.Text);
		await Eventually.IsTrue(() => hub.Runs.Single().State == SubagentState.Running, "子代理进入运行态");

		Assert.NotNull(hub.NoteChildAwaiting(started.SessionId!, true));
		Assert.True(hub.Runs.Single().AwaitingUser);
		Assert.True(hub.HasAwaitingUser);
		// 同一状态再报一次不算变化，不重复触发界面刷新与 toast。
		Assert.Null(hub.NoteChildAwaiting(started.SessionId!, true));
		hub.Describe(out var details);
		Assert.True(details["subagents"]!.AsArray()[0]!["awaiting_user"]!.GetValue<bool>());

		var cleared = hub.NoteChildAwaiting(started.SessionId!, false);
		Assert.NotNull(cleared);
		Assert.False(hub.Runs.Single().AwaitingUser);
		Assert.False(hub.HasAwaitingUser);

		gate.SetResult();
		await Eventually.IsTrue(() => hub.Runs.Single().State == SubagentState.Idle, "子代理结束");
	}

	[Fact]
	public async Task WaitAnyReturnsAsSoonAsOneTargetIsFinished()
	{
		var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var quick = new ScriptedStreamFn();
		quick.EnqueueText("先完成。");
		var slow = new ScriptedStreamFn();
		slow.Enqueue(model => GatedText(model, "后完成。", gate.Task));
		var host = new FakeHost(_root);
		host.Streams.Enqueue(quick);
		host.Streams.Enqueue(slow);

		using var parent = CreateParent(new ScriptedStreamFn(), host);
		var hub = Assert.IsType<SubagentHub>(parent.Subagents);
		var first = await hub.StartAsync(new SubagentStartRequest("scout", "先查。"));
		var second = await hub.StartAsync(new SubagentStartRequest("scout", "慢查。"), default);
		Assert.True(first.Ok, first.Text);
		Assert.True(second.Ok, second.Text);
		await Eventually.IsTrue(
			() => hub.Runs.Single(run => run.Name == first.Name).State == SubagentState.Idle,
			"第一个子代理先结束");

		var waited = await hub.WaitAsync([first.Name!, second.Name!], TimeSpan.FromSeconds(5), any: true);
		Assert.True(waited.Ok, waited.Text);
		Assert.False(waited.Details!["timed_out"]!.GetValue<bool>());
		Assert.True(waited.Details!["any"]!.GetValue<bool>());
		Assert.Equal(1, waited.Details!["still_running"]!.GetValue<int>());
		Assert.Contains("returned early", waited.Text);
		Assert.Contains("先完成", waited.Text);
		Assert.DoesNotContain("后完成", waited.Text);

		gate.SetResult();
		await Eventually.IsTrue(
			() => hub.Runs.All(static run => run.State == SubagentState.Idle),
			"两个子代理都结束");
	}

	[Fact]
	public async Task WaitPublishesProgressAndHintsWhenItTimesOut()
	{
		var childStream = new ScriptedStreamFn();
		childStream.Enqueue(model => GatedText(model, "慢慢来。", new TaskCompletionSource().Task));
		var host = new FakeHost(_root);
		host.Streams.Enqueue(childStream);

		using var parent = CreateParent(new ScriptedStreamFn(), host);
		var hub = Assert.IsType<SubagentHub>(parent.Subagents);

		var started = await hub.StartAsync(new SubagentStartRequest("scout", "慢任务。"));
		Assert.True(started.Ok, started.Text);

		var updates = new List<string>();
		var outcome = await hub.WaitAsync([started.Name!], TimeSpan.FromMilliseconds(300), default,
			text => { lock (updates) updates.Add(text); });

		Assert.True(outcome.Ok, outcome.Text);
		Assert.Contains("timed out", outcome.Text);
		// 超时后要告诉模型下一步怎么做，不然它会再等一轮。
		Assert.Contains("End your turn", outcome.Text);
		lock (updates)
		{
			Assert.NotEmpty(updates);
			Assert.Contains(updates, text => text.Contains("scout-1", StringComparison.Ordinal));
		}
	}

	[Fact]
	public async Task UnknownAgent_ListsTheCatalog()
	{
		using var parent = CreateParent(new ScriptedStreamFn(), new FakeHost(_root));
		var hub = Assert.IsType<SubagentHub>(parent.Subagents);

		var started = await hub.StartAsync(new SubagentStartRequest("nope", "做点什么。"));
		Assert.False(started.Ok);
		Assert.Contains("unknown agent", started.Text);
		Assert.Contains("scout", started.Text);
		Assert.Empty(hub.Runs);
	}

	[Fact]
	public async Task Capacity_RejectsWhenActiveChildrenReachTheLimit()
	{
		var blocking = new BlockingHost();
		using var parent = CreateParent(new ScriptedStreamFn(), blocking, maxActive: 1);
		var hub = Assert.IsType<SubagentHub>(parent.Subagents);

		using var cancellation = new CancellationTokenSource();
		var first = hub.StartAsync(new SubagentStartRequest("scout", "第一个任务。"), cancellation.Token);
		await Eventually.IsTrue(() => hub.Runs.Count == 1, "第一个子代理先登记");

		var overflow = await hub.StartAsync(new SubagentStartRequest("scout", "超出上限。"));
		Assert.False(overflow.Ok);
		Assert.Contains("already active", overflow.Text);

		await cancellation.CancelAsync();
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
	}

	[Fact]
	public async Task SpawnBudget_StopsChainedDelegationUntilTheNextUserPrompt()
	{
		var host = new FakeHost(_root);
		host.Streams.Enqueue(new ScriptedStreamFn());
		using var parent = CreateParent(new ScriptedStreamFn(), host, maxSpawns: 1);
		var hub = Assert.IsType<SubagentHub>(parent.Subagents);

		var first = await hub.StartAsync(new SubagentStartRequest("scout", "第一个任务。"));
		Assert.True(first.Ok, first.Text);

		var second = await hub.StartAsync(new SubagentStartRequest("scout", "第二个任务。"));
		Assert.False(second.Ok);
		Assert.Contains("this user turn already started 1", second.Text);

		hub.NoteUserPrompt();
		var third = await hub.StartAsync(new SubagentStartRequest("scout", "用户发话后的任务。"));
		Assert.True(third.Ok, third.Text);
	}

	[Fact]
	public async Task Forget_DropsAFinishedRunFromTheList()
	{
		var parentStream = new ScriptedStreamFn();
		parentStream.EnqueueText("父会话收到。");
		var childStream = new ScriptedStreamFn();
		childStream.EnqueueText("结论。");
		var host = new FakeHost(_root);
		host.Streams.Enqueue(childStream);
		using var parent = CreateParent(parentStream, host);
		var hub = Assert.IsType<SubagentHub>(parent.Subagents);

		var started = await hub.StartAsync(new SubagentStartRequest("scout", "查一下。"));
		Assert.True(started.Ok, started.Text);
		await Eventually.IsTrue(() => hub.Runs.Single().State == SubagentState.Idle, "子代理结束");
		await hub.DeliveryBarrier.WaitAsync(TimeSpan.FromSeconds(10));

		Assert.True(hub.Forget(started.Name!));
		Assert.Empty(hub.Runs);
		hub.Describe(out var details);
		Assert.Empty(details["subagents"]!.AsArray());
		Assert.False(hub.Forget(started.Name!));
	}

	[Fact]
	public async Task ReadingTheFirstTurnDoesNotSwallowTheSameTextInTheSecondTurn()
	{
		var parentStream = new ScriptedStreamFn();
		parentStream.EnqueueText("父会话收到第一轮。");
		parentStream.EnqueueText("父会话收到第二轮。");
		var childStream = new ScriptedStreamFn();
		childStream.EnqueueText("检查通过。");
		childStream.EnqueueText("检查通过。");
		var host = new FakeHost(_root);
		host.Streams.Enqueue(childStream);
		using var parent = CreateParent(parentStream, host);
		var hub = Assert.IsType<SubagentHub>(parent.Subagents);

		var started = await hub.StartAsync(new SubagentStartRequest("scout", "查一下。"));
		Assert.True(started.Ok, started.Text);
		await Eventually.IsTrue(() => hub.Runs.Single().State == SubagentState.Idle, "第一轮结束");
		await hub.DeliveryBarrier.WaitAsync(TimeSpan.FromSeconds(10));

		// 第一轮读一遍；第二轮正文完全相同，也不能被上一轮的已读记录扣下。
		Assert.True(hub.Read(started.Name!).Ok);
		var sent = await hub.SendAsync(started.Name!, "再查一遍。");
		Assert.True(sent.Ok, sent.Text);
		await Eventually.IsTrue(() => hub.Runs.Single().Turns == 2, "第二轮结束");
		await hub.DeliveryBarrier.WaitAsync(TimeSpan.FromSeconds(10));

		var last = parentStream.Requests[^1];
		Assert.Equal(
			2,
			last.OfType<UserMessage>().Count(static message => message.CustomType == SubagentHub.ResultMessageType));
	}

	[Fact]
	public async Task Forget_ClearsAQueuedDelivery()
	{
		var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var parentStream = new ScriptedStreamFn();
		parentStream.Enqueue(model =>
		{
			started.TrySetResult();
			return GatedText(model, "父会话第一轮。", gate.Task);
		});
		parentStream.EnqueueText("父会话第二轮。");

		var childStream = new ScriptedStreamFn();
		childStream.EnqueueText("子代理报告。");
		var host = new FakeHost(_root);
		host.Streams.Enqueue(childStream);
		using var parent = CreateParent(parentStream, host);
		var hub = Assert.IsType<SubagentHub>(parent.Subagents);

		var parentTurn = parent.PromptAsync("先做这个。");
		await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

		var child = await hub.StartAsync(new SubagentStartRequest("scout", "查一下。"));
		Assert.True(child.Ok, child.Text);
		await Eventually.IsTrue(() => hub.Runs.Single().State == SubagentState.Idle, "子代理结束");

		// 父会话还在跑：结果留在队列里；删掉这条运行后，队列里的正文也要一起清。
		Assert.True(hub.Forget(child.Name!));
		Assert.Empty(hub.Runs);

		parent.FollowUp(parent.PrepareInput("排队的问题。"));
		gate.SetResult();
		await parentTurn.WaitAsync(TimeSpan.FromSeconds(10));
		await hub.DeliveryBarrier.WaitAsync(TimeSpan.FromSeconds(10));

		Assert.DoesNotContain(
			parentStream.Requests.SelectMany(static request => request).OfType<UserMessage>(),
			static message => message.CustomType == SubagentHub.ResultMessageType);
	}

	[Fact]
	public async Task Send_QueuesASecondTurnAndDeliversItsResult()
	{
		var parentStream = new ScriptedStreamFn();
		parentStream.EnqueueText("父会话处理第二轮。");
		parentStream.EnqueueText("父会话处理第二轮。");
		var childStream = new ScriptedStreamFn();
		childStream.EnqueueText("第一轮结论。");
		childStream.EnqueueText("补充后的结论。");
		var host = new FakeHost(_root);
		host.Streams.Enqueue(childStream);

		using var parent = CreateParent(parentStream, host);
		var hub = Assert.IsType<SubagentHub>(parent.Subagents);

		var started = await hub.StartAsync(new SubagentStartRequest("worker", "改掉空指针。"));
		Assert.True(started.Ok, started.Text);
		await Eventually.IsTrue(() => hub.Runs.Single().State == SubagentState.Idle, "第一轮结束");

		var sent = await hub.SendAsync(started.Name!, "顺便补一条回归测试。");
		Assert.True(sent.Ok, sent.Text);
		await Eventually.IsTrue(() => hub.Runs.Single().Turns == 2, "第二轮结束");
		await hub.DeliveryBarrier.WaitAsync(TimeSpan.FromSeconds(10));

		Assert.Contains("补充后的结论", hub.Runs.Single().Result!);
	}

	[Fact]
	public async Task Send_SteerInjectsIntoTheRunningTurn()
	{
		var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var parentStream = new ScriptedStreamFn();
		parentStream.EnqueueText("父会话收到。");
		var childStream = new ScriptedStreamFn();
		childStream.Enqueue(model =>
		{
			firstStarted.TrySetResult();
			return GatedText(model, "第一轮结论。", gate.Task);
		});
		childStream.EnqueueText("采纳提醒后的结论。");
		var host = new FakeHost(_root);
		host.Streams.Enqueue(childStream);

		using var parent = CreateParent(parentStream, host);
		var hub = Assert.IsType<SubagentHub>(parent.Subagents);
		var started = await hub.StartAsync(new SubagentStartRequest("worker", "先做第一轮。"));
		Assert.True(started.Ok, started.Text);
		await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

		var steered = await hub.SendAsync(started.Name!, "别忘了检查 null。", "steer");
		Assert.True(steered.Ok, steered.Text);
		Assert.Contains("steered", steered.Text);

		gate.SetResult();
		await Eventually.IsTrue(
			() => hub.Runs.Single().State == SubagentState.Idle && hub.Runs.Single().Turns == 1,
			"steer 后这一轮运行结束");

		// steering 在下一个请求里以 user 消息出现，不和 follow-up 混成新的运行。
		Assert.Contains(childStream.Requests, request => request.Any(message =>
			message is UserMessage user &&
			user.Content.Text is { } text &&
			text.Contains("别忘了检查 null", StringComparison.Ordinal)));

		var late = await hub.SendAsync(started.Name!, "已经结束了。", "steer");
		Assert.False(late.Ok);
		Assert.Contains("not running", late.Text);
	}

	[Fact]
	public async Task QueuedUserPromptDoesNotDeliverAFinishedReportTwice()
	{
		var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var parentStream = new ScriptedStreamFn();
		parentStream.Enqueue(model =>
		{
			started.TrySetResult();
			return GatedText(model, "父会话第一轮。", gate.Task);
		});
		parentStream.EnqueueText("父会话第二轮。");

		var childStream = new ScriptedStreamFn();
		childStream.EnqueueText("子代理报告。");
		var host = new FakeHost(_root);
		host.Streams.Enqueue(childStream);

		using var parent = CreateParent(parentStream, host);
		var hub = Assert.IsType<SubagentHub>(parent.Subagents);

		var parentTurn = parent.PromptAsync("先做这个。");
		await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

		var child = await hub.StartAsync(new SubagentStartRequest("scout", "查一下。"));
		Assert.True(child.Ok, child.Text);
		await Eventually.IsTrue(() => hub.Runs.Single().State == SubagentState.Idle, "子代理结束");

		// 用户在父会话运行中排队一条消息：PrepareInput 会触发 NoteUserPrompt → flush。
		var queued = parent.PrepareInput("排队的问题。");
		parent.FollowUp(queued);

		gate.SetResult();
		await parentTurn.WaitAsync(TimeSpan.FromSeconds(10));
		// 投递有自己的合并窗口：等它真的落地，再读父会话的上下文。
		await hub.DeliveryBarrier.WaitAsync(TimeSpan.FromSeconds(10));
		await Eventually.IsTrue(() => !parent.IsRunning, "父会话两轮都结束");

		// 最后一次请求就是模型看到的上下文：回报只该有一条。
		var last = parentStream.Requests[^1];
		var reports = last
			.OfType<UserMessage>()
			.Count(static message => message.CustomType == SubagentHub.ResultMessageType);
		Assert.Equal(1, reports);
	}

	[Fact]
	public async Task ReadingAFinishedResultSuppressesItsPendingDelivery()
	{
		var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var parentStream = new ScriptedStreamFn();
		parentStream.Enqueue(model =>
		{
			started.TrySetResult();
			return GatedText(model, "父会话第一轮。", gate.Task);
		});
		parentStream.EnqueueText("父会话第二轮。");

		var childStream = new ScriptedStreamFn();
		childStream.EnqueueText("子代理报告。");
		var host = new FakeHost(_root);
		host.Streams.Enqueue(childStream);

		using var parent = CreateParent(parentStream, host);
		var hub = Assert.IsType<SubagentHub>(parent.Subagents);

		var parentTurn = parent.PromptAsync("先做这个。");
		await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

		var child = await hub.StartAsync(new SubagentStartRequest("scout", "查一下。"));
		Assert.True(child.Ok, child.Text);
		await Eventually.IsTrue(() => hub.Runs.Single().State == SubagentState.Idle, "子代理结束");
		Assert.Equal(SubagentState.Idle, hub.Runs.Single().State);

		// 父会话还在跑：结果留在 _pending；父会话先读一遍正文。
		var read = hub.Read(child.Name!);
		Assert.True(read.Ok, read.Text);
		Assert.Contains("子代理报告", read.Text);

		// 用户排队一条消息：会触发 NoteUserPrompt；正文已经读过，不再补投。
		parent.FollowUp(parent.PrepareInput("排队的问题。"));
		gate.SetResult();
		await parentTurn.WaitAsync(TimeSpan.FromSeconds(10));
		await Eventually.IsTrue(() => !parent.IsRunning, "父会话两轮都结束");

		Assert.DoesNotContain(
			parentStream.Requests.SelectMany(static request => request).OfType<UserMessage>(),
			static message => message.CustomType == SubagentHub.ResultMessageType);
	}

	[Fact]
	public async Task TruncatedReadDoesNotSuppressTheFullDelivery()
	{
		var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var parentStream = new ScriptedStreamFn();
		parentStream.Enqueue(model =>
		{
			started.TrySetResult();
			return GatedText(model, "父会话第一轮。", gate.Task);
		});
		parentStream.EnqueueText("父会话第二轮。");

		var childStream = new ScriptedStreamFn();
		childStream.EnqueueText("完整的子代理报告。");
		var host = new FakeHost(_root);
		host.Streams.Enqueue(childStream);

		using var parent = CreateParent(parentStream, host);
		var hub = Assert.IsType<SubagentHub>(parent.Subagents);

		var parentTurn = parent.PromptAsync("先做这个。");
		await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

		var child = await hub.StartAsync(new SubagentStartRequest("scout", "查一下。"));
		Assert.True(child.Ok, child.Text);
		await Eventually.IsTrue(() => hub.Runs.Single().State == SubagentState.Idle, "子代理结束");

		// 只看一个字符：不能因为「读过」就把完整结果扣下。
		var read = hub.Read(child.Name!, 1);
		Assert.True(read.Ok, read.Text);

		parent.FollowUp(parent.PrepareInput("排队的问题。"));
		gate.SetResult();
		await parentTurn.WaitAsync(TimeSpan.FromSeconds(10));
		await hub.DeliveryBarrier.WaitAsync(TimeSpan.FromSeconds(10));
		await Eventually.IsTrue(() => !parent.IsRunning, "父会话两轮都结束");

		var last = parentStream.Requests[^1];
		Assert.Equal(
			1,
			last.OfType<UserMessage>().Count(static message => message.CustomType == SubagentHub.ResultMessageType));
	}

	[Fact]
	public async Task Send_RejectsANewTurnWhenTheActiveLimitIsFull()
	{
		var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var parentStream = new ScriptedStreamFn();
		parentStream.EnqueueText("父会话收到。");
		parentStream.EnqueueText("父会话收到。");
		var firstStream = new ScriptedStreamFn();
		firstStream.EnqueueText("第一份报告。");
		var secondStream = new ScriptedStreamFn();
		secondStream.Enqueue(model => GatedText(model, "第二份报告。", gate.Task));
		var host = new FakeHost(_root);
		host.Streams.Enqueue(firstStream);
		host.Streams.Enqueue(secondStream);

		using var parent = CreateParent(parentStream, host, maxActive: 1);
		var hub = Assert.IsType<SubagentHub>(parent.Subagents);
		var first = await hub.StartAsync(new SubagentStartRequest("scout", "先查一下。"));
		Assert.True(first.Ok, first.Text);
		await Eventually.IsTrue(() => hub.Runs.Single().State == SubagentState.Idle, "第一个子代理结束");
		await hub.DeliveryBarrier.WaitAsync(TimeSpan.FromSeconds(5));

		var second = await hub.StartAsync(new SubagentStartRequest("scout", "再查一下。"));
		Assert.True(second.Ok, second.Text);
		await Eventually.IsTrue(
			() => hub.Runs.Single(run => run.Name == second.Name).State == SubagentState.Running,
			"第二个子代理应占住唯一的并发名额");

		var rejected = await hub.SendAsync(first.Name!, "补充一条。");
		Assert.False(rejected.Ok);
		Assert.Contains("already active", rejected.Text);
		Assert.Equal(SubagentState.Idle, hub.Runs.Single(run => run.Name == first.Name).State);

		gate.SetResult();
		hub.Stop(second.Name!);
		var sent = await hub.SendAsync(first.Name!, "补充一条。");
		Assert.True(sent.Ok, sent.Text);
	}

	[Fact]
	public async Task Delivery_StopsAfterSixConsecutiveParentTurns()
	{
		var parentStream = new ScriptedStreamFn();
		for (var index = 0; index < SubagentHub.MaxAutoContinue; index++)
		{
			parentStream.EnqueueText("父会话收到。");
		}

		var childStream = new ScriptedStreamFn();
		for (var index = 0; index < SubagentHub.MaxAutoContinue + 1; index++)
		{
			childStream.EnqueueText($"报告 {index + 1}。");
		}

		var host = new FakeHost(_root);
		host.Streams.Enqueue(childStream);
		using var parent = CreateParent(parentStream, host);
		var hub = Assert.IsType<SubagentHub>(parent.Subagents);

		var started = await hub.StartAsync(new SubagentStartRequest("scout", "第一轮。"));
		Assert.True(started.Ok, started.Text);
		for (var turn = 1; turn <= SubagentHub.MaxAutoContinue + 1; turn++)
		{
			await Eventually.IsTrue(
				() => hub.Runs.Single().Turns == turn && hub.Runs.Single().State == SubagentState.Idle,
				$"第 {turn} 轮结束");
			await hub.DeliveryBarrier.WaitAsync(TimeSpan.FromSeconds(5));
			if (turn == SubagentHub.MaxAutoContinue + 1)
			{
				break;
			}

			var sent = await hub.SendAsync(started.Name!, $"继续第 {turn + 1} 轮。");
			Assert.True(sent.Ok, sent.Text);
		}

		await Eventually.IsTrue(() => !parent.IsRunning, "父会话回报轮次收尾");
		Assert.Equal(SubagentHub.MaxAutoContinue, parentStream.Requests.Count);

		// 第 7 轮没有自动投递：卡片与 list 要标出来，正文没丢。
		await Eventually.IsTrue(() => hub.Runs.Single().DeliverySuppressed, "停投后应标记未投递");
		var waited = await hub.WaitAsync([started.Name!], TimeSpan.FromSeconds(5));
		Assert.True(waited.Ok, waited.Text);
		Assert.Contains($"报告 {SubagentHub.MaxAutoContinue + 1}。", waited.Text);
		Assert.False(hub.Runs.Single().DeliverySuppressed, "wait 取走后不应再标未投递");
	}

	[Fact]
	public async Task RunTimeout_MarksTheRunFailedAndDeliversAResult()
	{
		var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var parentStream = new ScriptedStreamFn();
		parentStream.EnqueueText("父会话收到超时。");
		var childStream = new ScriptedStreamFn();
		childStream.Enqueue(model => GatedText(model, "做不完。", gate.Task));
		var host = new FakeHost(_root);
		host.Streams.Enqueue(childStream);

		using var parent = CreateParent(parentStream, host, runTimeout: TimeSpan.FromMilliseconds(100));
		var hub = Assert.IsType<SubagentHub>(parent.Subagents);
		var started = await hub.StartAsync(new SubagentStartRequest("worker", "一个卡住的长任务。"));
		Assert.True(started.Ok, started.Text);

		await Eventually.IsTrue(() => hub.Runs.Single().State == SubagentState.Failed, "超时应把状态落到 failed");
		Assert.Contains("timed out", hub.Runs.Single().Result!);
		Assert.Equal(1, hub.Runs.Single().Turns);

		// 迟到的 AgentEnd 不能把失败改回 idle。
		gate.SetResult();
		await hub.DeliveryBarrier.WaitAsync(TimeSpan.FromSeconds(10));
		Assert.Equal(SubagentState.Failed, hub.Runs.Single().State);
		Assert.Contains(parentStream.Requests, request => request.Any(message =>
			message is UserMessage { CustomType: SubagentHub.ResultMessageType } user &&
			user.Content.Text is { } text &&
			text.Contains("timed out", StringComparison.Ordinal)));
	}

	[Fact]
	public async Task Stop_MarksTheRunStopped()
	{
		var parentStream = new ScriptedStreamFn();
		var childStream = new ScriptedStreamFn();
		childStream.EnqueueText("一轮。");
		var host = new FakeHost(_root);
		host.Streams.Enqueue(childStream);

		using var parent = CreateParent(parentStream, host);
		var hub = Assert.IsType<SubagentHub>(parent.Subagents);
		var started = await hub.StartAsync(new SubagentStartRequest("scout", "查一下。"));
		Assert.True(started.Ok, started.Text);

		var stopped = hub.Stop(started.Name!);
		Assert.True(stopped.Ok, stopped.Text);
		Assert.Equal(SubagentState.Stopped, hub.Runs.Single().State);
	}

	[Fact]
	public void Tools_AreRegisteredOnlyWithAHostAndBelowTheDepthLimit()
	{
		using var withHost = CreateParent(new ScriptedStreamFn(), new FakeHost(_root));
		Assert.Contains(withHost.Tools, tool => tool.Name == SubagentTools.DelegateName);
		Assert.Contains(withHost.Tools, tool => tool.Name == SubagentTools.WaitName);

		using var withoutHost = CreateParent(new ScriptedStreamFn(), host: null);
		Assert.DoesNotContain(withoutHost.Tools, tool => SubagentTools.IsName(tool.Name));

		using var childDepth = CreateParent(new ScriptedStreamFn(), new FakeHost(_root), depth: 1);
		Assert.DoesNotContain(childDepth.Tools, tool => SubagentTools.IsName(tool.Name));
	}

	[Fact]
	public void Parse_ReadsScalarAndBlockLists()
	{
		var definition = SubagentCatalog.Parse(
			"""
			---
			name: api-reviewer
			description: Review HTTP handlers
			tools:
			  - read
			  - grep
			excludeTools: edit, write
			model: anthropic/claude-sonnet-4
			thinking: high
			---
			只读审查 API 层。
			""",
			"fallback",
			"project",
			"/tmp/api-reviewer.md");

		Assert.NotNull(definition);
		Assert.Equal("api-reviewer", definition!.Name);
		Assert.Equal("Review HTTP handlers", definition.Description);
		Assert.Equal(["read", "grep"], definition.Tools);
		Assert.Equal(["edit", "write"], definition.ExcludeTools);
		Assert.Equal("anthropic/claude-sonnet-4", definition.Model);
		Assert.Equal("high", definition.Thinking);
		Assert.Equal("只读审查 API 层。", definition.SystemPrompt);
		Assert.Equal("project", definition.Source);
	}

	[Fact]
	public void Parse_RejectsAnInvalidName()
	{
		var definition = SubagentCatalog.Parse(
			"---\nname: Bad Name\ndescription: x\n---\nbody",
			"fallback",
			"user",
			null);
		Assert.Null(definition);
	}

	[Fact]
	public void Discover_ProjectOverridesUserOverridesBuiltin()
	{
		var agentDir = Path.Combine(_root, "agent");
		var project = Path.Combine(_root, ".pi", "agents");
		Directory.CreateDirectory(Path.Combine(agentDir, "agents"));
		Directory.CreateDirectory(project);

		File.WriteAllText(Path.Combine(agentDir, "agents", "scout.md"), "---\nname: scout\ndescription: user scout\n---\nuser");
		File.WriteAllText(Path.Combine(project, "scout.md"), "---\nname: scout\ndescription: project scout\n---\nproject");
		File.WriteAllText(Path.Combine(project, "auditor.md"), "---\nname: auditor\ndescription: local auditor\ntools: read\n---\naudit");

		var catalog = SubagentCatalog.Discover(_root, agentDir, includeProject: true);
		Assert.Equal("project scout", catalog.Get("scout")!.Description);
		Assert.Equal("local auditor", catalog.Get("auditor")!.Description);
		Assert.NotNull(catalog.Get("reviewer"));

		var userOnly = SubagentCatalog.Discover(_root, agentDir, includeProject: false);
		Assert.Equal("user scout", userOnly.Get("scout")!.Description);
		Assert.Null(userOnly.Get("auditor"));
	}

	[Fact]
	public async Task DelegateTool_ReportsMissingArgumentsAndStartsAChild()
	{
		var host = new FakeHost(_root);
		using var parent = CreateParent(new ScriptedStreamFn(), host);
		var tool = parent.Tools.Single(item => item.Name == SubagentTools.DelegateName);

		var missing = await tool.Execute(new AgentToolInvocation
		{
			ToolCallId = "call-1",
			ToolName = SubagentTools.DelegateName,
			Arguments = new JsonObject { ["agent"] = "scout" },
		});
		Assert.True(missing.IsError);
		Assert.Contains("task is required", ((TextContent)missing.Content[0]).Text);

		var started = await tool.Execute(new AgentToolInvocation
		{
			ToolCallId = "call-2",
			ToolName = SubagentTools.DelegateName,
			Arguments = new JsonObject
			{
				["agent"] = "scout",
				["task"] = "看下登录流程。",
			},
		});
		Assert.False(started.IsError);
		Assert.Equal("started", started.Details?["status"]?.GetValue<string>());
		Assert.Equal("scout-1", started.Details?["name"]?.GetValue<string>());
		Assert.Single(host.Contexts);

		var badSchema = await tool.Execute(new AgentToolInvocation
		{
			ToolCallId = "call-3",
			ToolName = SubagentTools.DelegateName,
			Arguments = new JsonObject
			{
				["agent"] = "scout",
				["task"] = "再看一次。",
				["output_schema"] = "not an object",
			},
		});
		Assert.True(badSchema.IsError);
		Assert.Contains("output_schema", ((TextContent)badSchema.Content[0]).Text);
		Assert.Single(host.Contexts);
	}

	[Fact]
	public void Tools_DescribeTheAvailableAgents()
	{
		using var parent = CreateParent(new ScriptedStreamFn(), new FakeHost(_root));
		var tool = parent.Tools.Single(item => item.Name == SubagentTools.DelegateName);
		Assert.Contains("scout", tool.Description);
		Assert.Contains("reviewer", tool.Description);
		Assert.Contains("worker", tool.Description);
		Assert.Contains("fresh", tool.Parameters["properties"]!["context"]!["enum"]!.ToJsonString());
		Assert.IsType<JsonObject>(tool.Parameters["properties"]!["output_schema"]);
		Assert.IsType<JsonObject>(tool.Parameters["properties"]!["verify"]);
		Assert.IsType<JsonObject>(tool.Parameters["properties"]!["max_tokens"]);
		Assert.IsType<JsonObject>(tool.Parameters["properties"]!["timeout_ms"]);
	}

	[Fact]
	public async Task AwaitingUser_PausesTheRunDeadlineUntilTheAnswerArrives()
	{
		var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var childStream = new ScriptedStreamFn();
		childStream.Enqueue(model => GatedText(model, "等你拍板。", gate.Task));
		var host = new FakeHost(_root);
		host.Streams.Enqueue(childStream);

		using var parent = CreateParent(new ScriptedStreamFn(), host, runTimeout: TimeSpan.FromMilliseconds(2_000));
		var hub = Assert.IsType<SubagentHub>(parent.Subagents);
		var started = await hub.StartAsync(new SubagentStartRequest("worker", "改完再问我。"));
		Assert.True(started.Ok, started.Text);
		await Eventually.IsTrue(() => hub.Runs.Single().State == SubagentState.Running, "子会话应进入运行态");

		var run = Assert.IsType<SubagentRun>(hub.NoteChildAwaiting(started.SessionId!, awaiting: true));
		Assert.True(run.AwaitingUser);

		// 预算是 2 秒，等 3 秒早就该超时了；暂停后必须还活着。
		await Task.Delay(TimeSpan.FromSeconds(3));
		Assert.Equal(SubagentState.Running, hub.Runs.Single().State);

		var resumedAt = Stopwatch.GetTimestamp();
		var answered = Assert.IsType<SubagentRun>(hub.NoteChildAwaiting(started.SessionId!, awaiting: false));
		Assert.False(answered.AwaitingUser);

		// 恢复后至少再给半份预算，然后按新表超时；不能刚恢复就判死。
		await Eventually.IsTrue(
			() => hub.Runs.Single().State == SubagentState.Failed,
			"恢复后截止时间应重新生效",
			TimeSpan.FromSeconds(10));
		Assert.True(
			Stopwatch.GetElapsedTime(resumedAt) >= TimeSpan.FromMilliseconds(700),
			$"resume window too short: {Stopwatch.GetElapsedTime(resumedAt)}");
		Assert.Contains("timed out", hub.Runs.Single().Result!);
		gate.SetResult();
	}

	[Fact]
	public async Task Timeout_KeepsTheTextTheChildAlreadyWrote()
	{
		var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var childStream = new ScriptedStreamFn();
		childStream.EnqueueText("审阅到一半：AuthController 缺校验。");
		childStream.Enqueue(model => GatedText(model, "还在写。", gate.Task));
		var host = new FakeHost(_root);
		host.Streams.Enqueue(childStream);

		using var parent = CreateParent(new ScriptedStreamFn(), host, runTimeout: TimeSpan.FromSeconds(1.5));
		var hub = Assert.IsType<SubagentHub>(parent.Subagents);
		var started = await hub.StartAsync(new SubagentStartRequest("reviewer", "审一半。"));
		Assert.True(started.Ok, started.Text);
		await Eventually.IsTrue(() => hub.Runs.Single().State == SubagentState.Idle, "第一轮应结束");

		var sent = await hub.SendAsync(started.Name!, "接着看。");
		Assert.True(sent.Ok, sent.Text);
		await Eventually.IsTrue(
			() => hub.Runs.Single().State == SubagentState.Failed,
			"第二轮应在时限后超时",
			TimeSpan.FromSeconds(10));

		var result = hub.Runs.Single().Result!;
		Assert.Contains("timed out", result);
		Assert.Contains("AuthController 缺校验", result);
		gate.SetResult();
	}

	[Fact]
	public async Task TokenBudget_StopsTheRunAndReportsTheCap()
	{
		var childStream = new ScriptedStreamFn();
		// 第一条消息就带上 15k 输入：预算设成 1000，它一开口就超。
		childStream.Enqueue(model => ScriptedSessions.Text(
			model,
			"收到。",
			new Usage { Input = 15_000, Output = 200, TotalTokens = 15_200 }));
		var host = new FakeHost(_root);
		host.Streams.Enqueue(childStream);

		using var parent = CreateParent(new ScriptedStreamFn(), host);
		var hub = Assert.IsType<SubagentHub>(parent.Subagents);
		var started = await hub.StartAsync(new SubagentStartRequest("scout", "先看一眼。") { MaxTokens = 1_000 });
		Assert.True(started.Ok, started.Text);

		await Eventually.IsTrue(
			() => hub.Runs.Single().State == SubagentState.Failed,
			"超预算应停掉这一轮",
			TimeSpan.FromSeconds(10));

		var run = hub.Runs.Single();
		Assert.Equal(1_000, run.MaxTokens);
		Assert.Contains("token budget", run.Result!);
	}

	[Fact]
	public async Task Verify_RunsTheCommandOnTheHostAndMarksAFailure()
	{
		if (OperatingSystem.IsWindows())
		{
			return;
		}

		var childStream = new ScriptedStreamFn();
		childStream.EnqueueText("改完了，测试都过了。");
		var host = new FakeHost(_root);
		host.Streams.Enqueue(childStream);

		using var parent = CreateParent(new ScriptedStreamFn(), host);
		var hub = Assert.IsType<SubagentHub>(parent.Subagents);
		var started = await hub.StartAsync(new SubagentStartRequest("worker", "改个东西。")
		{
			Verify = ["printf 'boom' && exit 3"],
		});
		Assert.True(started.Ok, started.Text);

		await Eventually.IsTrue(
			() => hub.Runs.Single().State == SubagentState.Failed,
			"验证没通过就该判 failed",
			TimeSpan.FromSeconds(15));

		var run = hub.Runs.Single();
		var verify = Assert.Single(run.Verify);
		Assert.Equal(3, verify.ExitCode);
		Assert.False(verify.Passed);
		Assert.Contains("boom", verify.Output);
		Assert.Contains("改完了", run.Result!);
		Assert.Contains("verify", run.Result!, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task Verify_PassingCommandKeepsTheRunSuccessful()
	{
		if (OperatingSystem.IsWindows())
		{
			return;
		}

		var childStream = new ScriptedStreamFn();
		childStream.EnqueueText("改完了。");
		var host = new FakeHost(_root);
		host.Streams.Enqueue(childStream);

		using var parent = CreateParent(new ScriptedStreamFn(), host);
		var hub = Assert.IsType<SubagentHub>(parent.Subagents);
		var started = await hub.StartAsync(new SubagentStartRequest("worker", "改个东西。")
		{
			Verify = ["echo ok"],
		});
		Assert.True(started.Ok, started.Text);

		await Eventually.IsTrue(
			() => hub.Runs.Single().State == SubagentState.Idle,
			"验证通过后应落到 idle",
			TimeSpan.FromSeconds(15));

		var verify = Assert.Single(hub.Runs.Single().Verify);
		Assert.True(verify.Passed);
		Assert.Equal(0, verify.ExitCode);
	}

	[Fact]
	public async Task StopReason_LengthIsReportedInTheDeliveredNotice()
	{
		var parentStream = new ScriptedStreamFn();
		parentStream.EnqueueText("父会话收到。");
		var childStream = new ScriptedStreamFn();
		childStream.Enqueue(model => TruncatedText(model, "被截断的结论。"));
		var host = new FakeHost(_root);
		host.Streams.Enqueue(childStream);

		using var parent = CreateParent(parentStream, host);
		var hub = Assert.IsType<SubagentHub>(parent.Subagents);
		var started = await hub.StartAsync(new SubagentStartRequest("scout", "查一下。"));
		Assert.True(started.Ok, started.Text);

		await Eventually.IsTrue(() => hub.Runs.Single().State == SubagentState.Idle, "一轮后应结束");
		await hub.DeliveryBarrier.WaitAsync(TimeSpan.FromSeconds(10));

		Assert.Equal("length", hub.Runs.Single().StopReason);
		var notice = Assert.Single(
			parentStream.Requests.SelectMany(static request => request).OfType<UserMessage>(),
			static message => message.CustomType == SubagentHub.ResultMessageType);
		Assert.Contains("max tokens", notice.Content.Text!);
	}

	[Fact]
	public async Task Restart_RequeuesAResultThatNeverReachedTheParent()
	{
		var sessions = Path.Combine(_root, "restart");
		Directory.CreateDirectory(sessions);
		var childStream = new ScriptedStreamFn();
		childStream.EnqueueText("重启前写好的结论。");
		var host = new FakeHost(_root, persistChildren: true);
		host.Streams.Enqueue(childStream);

		string parentFile = string.Empty;
		{
			var parentGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			var parentStream = new ScriptedStreamFn();
			parentStream.Enqueue(model => GatedText(model, "父会话还在忙。", parentGate.Task));
			var manager = SessionManager.Create(_root, sessions);
			parentFile = manager.SessionFile!;
			var parent = CreateParent(parentStream, host, session: manager);
			try
			{
				var hub = Assert.IsType<SubagentHub>(parent.Subagents);
				_ = parent.PromptAsync("先聊着。");
				await Eventually.IsTrue(() => parent.IsRunning, "父会话应进入运行态");

				var started = await hub.StartAsync(new SubagentStartRequest("scout", "查一下。"));
				Assert.True(started.Ok, started.Text);
				await Eventually.IsTrue(() => hub.Runs.Single().State == SubagentState.Idle, "一轮后应结束");
				await WaitForDescriptorAsync(parentFile, "idle");

				// 父会话一直在跑，投递停在队列里：这时关掉会话，等于正文没送达。
				Assert.True(hub.Runs.Single().IsTerminal);
			}
			finally
			{
				// 回合还没结束就退出：投递队列随 hub 一起丢掉。
				parent.Dispose();
			}
		}

		// 重开同一个会话文件：未投递的结果应重新排队，跟着下一次父会话轮次送出。
		var reopenedManager = SessionManager.Open(parentFile);
		var reopenedStream = new ScriptedStreamFn();
		reopenedStream.EnqueueText("父会话接着讲。");
		using var reopened = CreateParent(reopenedStream, host, session: reopenedManager);
		var reopenedHub = Assert.IsType<SubagentHub>(reopened.Subagents);
		var restored = Assert.Single(reopenedHub.Runs);
		Assert.Equal(SubagentState.Idle, restored.State);
		Assert.Equal("重启前写好的结论。", restored.Result);
		Assert.Equal(1, reopenedHub.PendingCount);

		reopenedHub.NoteUserPrompt();
		await reopenedHub.DeliveryBarrier.WaitAsync(TimeSpan.FromSeconds(10));
		Assert.Equal(0, reopenedHub.PendingCount);
		await Eventually.IsTrue(() => reopenedStream.Requests.Count > 0, "补投应拉起一轮父会话", TimeSpan.FromSeconds(10));

		Assert.Contains(reopenedStream.Requests, request => request.Any(message =>
			message is UserMessage { CustomType: SubagentHub.ResultMessageType } user &&
			user.Content.Text is { } text &&
			text.Contains("重启前写好的结论", StringComparison.Ordinal)));

		// 投递成功会补一条 delivered：第二次重启不该再推同一段正文。
		var reopenedAgain = SessionManager.Open(parentFile);
		using var third = CreateParent(new ScriptedStreamFn(), host, session: reopenedAgain);
		Assert.Equal(0, Assert.IsType<SubagentHub>(third.Subagents).PendingCount);
	}

	[Fact]
	public async Task Restart_DoesNotRequeueAResultTheWaitAlreadyTook()
	{
		var sessions = Path.Combine(_root, "wait-taken");
		Directory.CreateDirectory(sessions);
		var childGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var childStream = new ScriptedStreamFn();
		childStream.Enqueue(model => GatedText(model, "wait 取走的结论。", childGate.Task));
		var host = new FakeHost(_root, persistChildren: true);
		host.Streams.Enqueue(childStream);

		string parentFile;
		{
			var parentGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			var parentStream = new ScriptedStreamFn();
			parentStream.Enqueue(model => GatedText(model, "父会话忙着。", parentGate.Task));
			var manager = SessionManager.Create(_root, sessions);
			parentFile = manager.SessionFile!;
			var parent = CreateParent(parentStream, host, session: manager);
			try
			{
				var hub = Assert.IsType<SubagentHub>(parent.Subagents);
				_ = parent.PromptAsync("先聊着。");
				await Eventually.IsTrue(() => parent.IsRunning, "父会话应进入运行态");

				var started = await hub.StartAsync(new SubagentStartRequest("scout", "查一下。"));
				Assert.True(started.Ok, started.Text);
				var waiting = hub.WaitAsync(null, TimeSpan.FromSeconds(10));
				await Eventually.IsTrue(
					() => hub.Runs.Single().State == SubagentState.Running,
					"子会话应进入运行态",
					TimeSpan.FromSeconds(10));
				childGate.SetResult();
				var waited = await waiting;
				Assert.True(waited.Ok, waited.Text);
				Assert.Contains("wait 取走的结论", waited.Text);
				await WaitForDescriptorAsync(parentFile, "delivered");
			}
			finally
			{
				parent.Dispose();
			}
		}

		var reopenedManager = SessionManager.Open(parentFile);
		using var reopened = CreateParent(new ScriptedStreamFn(), host, session: reopenedManager);
		var hubAgain = Assert.IsType<SubagentHub>(reopened.Subagents);
		Assert.Single(hubAgain.Runs);
		Assert.Equal(0, hubAgain.PendingCount);
	}

	[Fact]
	public void CheckpointLead_GivesLongRunsTimeToWrapUp()
	{
		Assert.Equal(TimeSpan.Zero, SubagentHub.CheckpointLeadForTests(TimeSpan.FromMinutes(2)));
		Assert.Equal(TimeSpan.FromMinutes(1), SubagentHub.CheckpointLeadForTests(TimeSpan.FromMinutes(10)));
		Assert.Equal(TimeSpan.FromMinutes(2), SubagentHub.CheckpointLeadForTests(TimeSpan.FromMinutes(30)));
	}

	[Fact]
	public void Parse_ReadsTheAgentLimitsAndVerifyCommands()
	{
		var definition = Assert.IsType<SubagentDefinition>(SubagentCatalog.Parse(
			"""
			---
			name: api-reviewer
			description: Review HTTP handlers.
			timeoutMinutes: 45
			maxTokens: 80000
			verify:
			  - dotnet test tests/Api
			  - dotnet format --verify-no-changes
			---
			You review HTTP handlers.
			""",
			"api-reviewer",
			"user",
			null));

		Assert.Equal(45, definition.TimeoutMinutes);
		Assert.Equal(80_000, definition.MaxTokens);
		Assert.Equal(2, definition.Verify!.Count);
		Assert.Equal("dotnet test tests/Api", definition.Verify[0]);
	}

	[Fact]
	public void Settings_ReadTheSubagentLimits()
	{
		File.WriteAllText(Path.Combine(_root, "settings.json"), """
			{ "subagents": { "maxActive": 8, "maxSpawns": 64, "runTimeoutMinutes": 45, "maxTokens": 250000, "verifyTimeoutSeconds": 120, "openTabs": true } }
			""");

		var settings = CodingAgentSettings.Load(_root, agentDir: _root, includeProject: false);

		Assert.Equal(8, settings.Subagents.MaxActive);
		Assert.Equal(64, settings.Subagents.MaxSpawns);
		Assert.Equal(45, settings.Subagents.RunTimeoutMinutes);
		Assert.Equal(250_000, settings.Subagents.MaxTokens);
		Assert.Equal(120, settings.Subagents.VerifyTimeoutSeconds);
		// 默认不开标签；写了才回到旧行为。
		Assert.True(settings.Subagents.OpenTabs);

		var limits = CodingAgentSettings.ToLimits(settings.Subagents);
		Assert.Equal(TimeSpan.FromMinutes(45), limits.RunTimeout);
		Assert.Equal(250_000, limits.MaxTokens);
		Assert.Equal(TimeSpan.FromSeconds(120), limits.VerifyTimeout);
	}

	[Fact]
	public void Settings_FallBackToDefaultsWhenTheSubagentLimitsAreOutOfRange()
	{
		File.WriteAllText(Path.Combine(_root, "settings.json"), """
			{ "subagents": { "maxActive": 0, "maxSpawns": 100000, "runTimeoutMinutes": -5, "maxTokens": -1 } }
			""");

		var settings = CodingAgentSettings.Load(_root, agentDir: _root, includeProject: false);

		// 超范围的值不是「尽量靠边」，而是回到默认：一个 0 并发会让 delegation 完全不可用。
		Assert.Equal(4, settings.Subagents.MaxActive);
		Assert.Equal(32, settings.Subagents.MaxSpawns);
		Assert.Equal(30, settings.Subagents.RunTimeoutMinutes);
		Assert.Equal(0, settings.Subagents.MaxTokens);
	}

	[Fact]
	public async Task StopDuringVerificationKeepsTheRunStopped()
	{
		if (OperatingSystem.IsWindows())
		{
			return;
		}

		var childStream = new ScriptedStreamFn();
		childStream.EnqueueText("改完了。");
		var host = new FakeHost(_root);
		host.Streams.Enqueue(childStream);

		using var parent = CreateParent(new ScriptedStreamFn(), host);
		var hub = Assert.IsType<SubagentHub>(parent.Subagents);
		var started = await hub.StartAsync(new SubagentStartRequest("worker", "改个东西。")
		{
			Verify = ["sleep 5 && echo ok"],
		});
		Assert.True(started.Ok, started.Text);

		await Eventually.IsTrue(
			() => hub.Runs.Single().Activity?.StartsWith("verify:", StringComparison.Ordinal) == true,
			"验证应该真的开始跑",
			TimeSpan.FromSeconds(10));

		var stopped = hub.Stop(started.Name!);
		Assert.True(stopped.Ok, stopped.Text);
		Assert.Equal(SubagentState.Stopped, hub.Runs.Single().State);
		// 停掉的那一轮不该把验证结果写到关系上；再等一会儿也不能自己变回 idle。
		await Task.Delay(TimeSpan.FromMilliseconds(500));
		Assert.Equal(SubagentState.Stopped, hub.Runs.Single().State);
		Assert.Empty(hub.Runs.Single().Verify);
	}

	[Fact]
	public async Task PlanModeRejectsAVerificationThatWrites()
	{
		using var parent = CreateParent(new ScriptedStreamFn(), new FakeHost(_root));
		parent.SetPlanMode(true);
		var hub = Assert.IsType<SubagentHub>(parent.Subagents);

		var rejected = await hub.StartAsync(new SubagentStartRequest("worker", "改个东西。")
		{
			Verify = ["dotnet test"],
		});
		Assert.False(rejected.Ok);
		Assert.Contains("read-only", rejected.Text);

		// 只读命令照常放行。
		var allowed = await hub.StartAsync(new SubagentStartRequest("scout", "看一眼。")
		{
			Verify = ["git status"],
		});
		Assert.True(allowed.Ok, allowed.Text);
		hub.Stop(allowed.Name!);
	}

	[Fact]
	public async Task Restart_KeepsTheRunLimitsAndVerificationResults()
	{
		if (OperatingSystem.IsWindows())
		{
			return;
		}

		var sessions = Path.Combine(_root, "limits");
		Directory.CreateDirectory(sessions);
		var childStream = new ScriptedStreamFn();
		childStream.EnqueueText("改完了。");
		var host = new FakeHost(_root, persistChildren: true);
		host.Streams.Enqueue(childStream);

		string parentFile;
		{
			var manager = SessionManager.Create(_root, sessions);
			parentFile = manager.SessionFile!;
			using var parent = CreateParent(new ScriptedStreamFn(), host, session: manager);
			var hub = Assert.IsType<SubagentHub>(parent.Subagents);
			var started = await hub.StartAsync(new SubagentStartRequest("worker", "改个东西。")
			{
				MaxTokens = 40_000,
				Verify = ["echo ok"],
			});
			Assert.True(started.Ok, started.Text);
			await Eventually.IsTrue(() => hub.Runs.Single().State == SubagentState.Idle, "一轮后应结束");
			// 内存状态先落、条目后写：等它落盘再重开，否则读到的是半截关系。
			await WaitForDescriptorAsync(parentFile, "idle");
		}

		await WaitForDescriptorAsync(parentFile, "idle");
		var reopenedManager = SessionManager.Open(parentFile);
		using var reopened = CreateParent(new ScriptedStreamFn(), host, session: reopenedManager);
		var reopenedHub = Assert.IsType<SubagentHub>(reopened.Subagents);
		var restored = Assert.Single(reopenedHub.Runs);
		Assert.Equal(40_000, restored.MaxTokens);
		Assert.Equal("stop", restored.StopReason);
		var verify = Assert.Single(restored.Verify);
		Assert.Equal("echo ok", verify.Command);
		Assert.True(verify.Passed);

		// 冷恢复要继续带同一套验证命令：子会话的提示词得照样写明结论会被复核。
		host.Streams.Enqueue(new ScriptedStreamFn());
		var sent = await reopenedHub.SendAsync(restored.Name, "接着改。");
		Assert.True(sent.Ok, sent.Text);
		var reopen = Assert.Single(host.ReopenContexts);
		Assert.Equal("echo ok", Assert.Single(reopen.Verify));
	}

	/// 子会话的 ask_question 用这个宿主把问题挂住，等测试放行。
	private sealed class BlockingUiHost : IUiHost
	{
		public bool HasUi => true;

		public TaskCompletionSource<string?> Answer { get; } =
			new(TaskCreationOptions.RunContinuationsAsynchronously);

		public Task<string?> InputAsync(UiInputRequest request, CancellationToken cancellationToken) => Answer.Task;
	}

	[Fact]
	public async Task AwaitingChangedFiresOnceWhenAChildStartsAsking()
	{
		var childStream = new ScriptedStreamFn();
		childStream.EnqueueToolCall("call-1", "ask_question", new JsonObject
		{
			["questions"] = new JsonArray(new JsonObject
			{
				["id"] = "q1",
				["type"] = "input",
				["question"] = "要不要改配置？",
			}),
		});
		childStream.EnqueueText("按你的选择做完了。");
		var host = new FakeHost(_root, askQuestion: true);
		host.Streams.Enqueue(childStream);

		using var parent = CreateParent(new ScriptedStreamFn(), host);
		var hub = Assert.IsType<SubagentHub>(parent.Subagents);
		var seen = new List<string>();
		hub.AwaitingChanged += run => seen.Add(run.Name);

		Assert.True((await hub.StartAsync(new SubagentStartRequest("worker", "改个东西。"))).Ok);
		await Eventually.IsTrue(
			() => hub.Runs.Single().AwaitingUser,
			"ask_question 应把运行标成等待回答",
			TimeSpan.FromSeconds(10));

		// 桌面的展示同步再报一次同一个状态不算新的「开始等」。
		hub.NoteChildAwaiting(hub.Runs.Single().SessionId, awaiting: true);
		Assert.Equal(["worker-1"], seen);

		host.UiHost.Answer.SetResult("改默认值。");
		await Eventually.IsTrue(
			() => hub.Runs.Single().State == SubagentState.Idle,
			"回答后应正常结束",
			TimeSpan.FromSeconds(10));
		Assert.Single(seen);
	}

	[Fact]
	public async Task ChildQuestionPausesTheDeadlineWithoutTheDesktopNudgingIt()
	{
		var childStream = new ScriptedStreamFn();
		childStream.EnqueueToolCall("call-1", "ask_question", new JsonObject
		{
			["questions"] = new JsonArray(new JsonObject
			{
				["id"] = "q1",
				["type"] = "input",
				["question"] = "要不要改配置？",
			}),
		});
		childStream.EnqueueText("按你的选择做完了。");
		var host = new FakeHost(_root, askQuestion: true);
		host.Streams.Enqueue(childStream);

		using var parent = CreateParent(new ScriptedStreamFn(), host, runTimeout: TimeSpan.FromMilliseconds(1_500));
		var hub = Assert.IsType<SubagentHub>(parent.Subagents);
		var started = await hub.StartAsync(new SubagentStartRequest("worker", "改个东西，拿不准就问我。"));
		Assert.True(started.Ok, started.Text);

		// 子代理自己的工具事件就要把截止时间停住：父标签没开、没人调 NoteChildAwaiting 也一样。
		await Eventually.IsTrue(
			() => hub.Runs.Single().AwaitingUser,
			"ask_question 应把运行标成等待回答",
			TimeSpan.FromSeconds(10));

		await Task.Delay(TimeSpan.FromSeconds(2.5));
		Assert.Equal(SubagentState.Running, hub.Runs.Single().State);
		Assert.True(hub.Runs.Single().AwaitingUser);

		// 回答之后计时恢复，回合照常收尾。
		host.UiHost.Answer.SetResult("改默认值。");
		await Eventually.IsTrue(
			() => hub.Runs.Single().State == SubagentState.Idle,
			"回答后应正常结束",
			TimeSpan.FromSeconds(10));
		Assert.False(hub.Runs.Single().AwaitingUser);
		Assert.Contains("按你的选择做完了", hub.Runs.Single().Result!);
	}

	[Fact]
	public async Task WaitReturnsEarlyWhenAChildAsksItsParentAndSendAnswersIt()
	{
		var childStream = new ScriptedStreamFn();
		childStream.EnqueueToolCall("call-1", SubagentTools.AskParentName, new JsonObject
		{
			["question"] = "要不要顺手改测试？",
		});
		childStream.EnqueueText("按父会话说的做了。");
		var host = new FakeHost(_root, askParent: true);
		host.Streams.Enqueue(childStream);

		using var parent = CreateParent(new ScriptedStreamFn(), host);
		var hub = Assert.IsType<SubagentHub>(parent.Subagents);
		var started = await hub.StartAsync(new SubagentStartRequest("worker", "改个东西。"));
		Assert.True(started.Ok, started.Text);
		await Eventually.IsTrue(
			() => hub.Runs.Single().Question is { Length: > 0 },
			"子代理应把问题交回父会话",
			TimeSpan.FromSeconds(10));

		// wait 不再只等结束与超时：有人提问就提前返回，并带上问题本身。
		var waited = await hub.WaitAsync(null, TimeSpan.FromSeconds(10));
		Assert.True(waited.Ok, waited.Text);
		Assert.Contains("waiting for your answer", waited.Text);
		Assert.Contains("要不要顺手改测试", waited.Text);
		Assert.Equal("question", waited.Details!["reason"]!.GetValue<string>());
		var question = Assert.Single(waited.Details!["questions"]!.AsArray());
		Assert.Equal("worker-1", question!["name"]!.GetValue<string>());
		Assert.Equal("要不要顺手改测试？", question["question"]!.GetValue<string>());

		// 回答走 send_to_subagent：当作答案投递给工具调用，不另起一轮。
		var answered = await hub.SendAsync(started.Name!, "改，别动别的测试。");
		Assert.True(answered.Ok, answered.Text);
		Assert.Contains("answered", answered.Text);

		await Eventually.IsTrue(
			() => hub.Runs.Single().State == SubagentState.Idle,
			"回答后子代理应继续并结束",
			TimeSpan.FromSeconds(10));
		Assert.Contains("按父会话说的做了", hub.Runs.Single().Result!);
		Assert.Null(hub.Runs.Single().Question);
		Assert.Contains(
			childStream.Requests,
			request => request.Any(message =>
				message is ToolResultMessage result &&
				result.Content.OfType<TextContent>().Any(text =>
					text.Text.Contains("改，别动别的测试", StringComparison.Ordinal))));
	}

	[Fact]
	public async Task AskParentTimesOutAndKeepsTheRunAlive()
	{
		var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var childStream = new ScriptedStreamFn();
		childStream.Enqueue(model => GatedText(model, "做完了。", gate.Task));
		var host = new FakeHost(_root);
		host.Streams.Enqueue(childStream);

		using var parent = CreateParent(new ScriptedStreamFn(), host, runTimeout: TimeSpan.FromSeconds(1.5));
		var hub = Assert.IsType<SubagentHub>(parent.Subagents);
		var started = await hub.StartAsync(new SubagentStartRequest("worker", "改个东西。"));
		Assert.True(started.Ok, started.Text);
		await Eventually.IsTrue(() => hub.Runs.Single().State == SubagentState.Running, "子代理应进入运行态");
		var sessionId = hub.Runs.Single().SessionId;

		var asked = hub.AskParentAsync(sessionId, "要不要改测试？", ["要", "不要"], CancellationToken.None);
		await Eventually.IsTrue(() => hub.PendingQuestion(sessionId) is not null, "问题应挂起");

		// 等待不计入运行时限：预算只有 1.5 秒，挂 2.2 秒后还是 Running。
		await Task.Delay(TimeSpan.FromSeconds(2.2));
		Assert.Equal(SubagentState.Running, hub.Runs.Single().State);

		Assert.True(hub.AnswerQuestion(sessionId, "要"));
		Assert.Equal("要", await asked);
		Assert.Null(hub.PendingQuestion(sessionId));

		gate.SetResult();
		await Eventually.IsTrue(() => hub.Runs.Single().State == SubagentState.Idle, "子代理应正常结束");
	}

	[Fact]
	public async Task AskParentStopsAfterTheCapOfParentAnswers()
	{
		var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var childStream = new ScriptedStreamFn();
		childStream.Enqueue(model => GatedText(model, "做完了。", gate.Task));
		var host = new FakeHost(_root);
		host.Streams.Enqueue(childStream);

		using var parent = CreateParent(new ScriptedStreamFn(), host, runTimeout: TimeSpan.FromMinutes(5));
		var hub = Assert.IsType<SubagentHub>(parent.Subagents);
		var started = await hub.StartAsync(new SubagentStartRequest("worker", "改个东西。"));
		Assert.True(started.Ok, started.Text);
		await Eventually.IsTrue(() => hub.Runs.Single().State == SubagentState.Running, "子代理应进入运行态");
		var sessionId = hub.Runs.Single().SessionId;

		for (var index = 0; index < SubagentHub.MaxParentQuestions; index++)
		{
			var asked = hub.AskParentAsync(sessionId, $"问题 {index}", null, CancellationToken.None);
			await Eventually.IsTrue(() => hub.PendingQuestion(sessionId) is not null, "问题应挂起");
			Assert.True((await hub.SendAsync(started.Name!, $"答案 {index}")).Ok);
			Assert.Equal($"答案 {index}", await asked);
		}

		// 到上限：父会话不再能回答，子代理应转为问用户。
		Assert.Null(await hub.AskParentAsync(sessionId, "还有吗？", null, CancellationToken.None));
		gate.SetResult();
	}

	[Fact]
	public async Task UserAnswersDoNotConsumeTheParentQuestionBudget()
	{
		var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var childStream = new ScriptedStreamFn();
		childStream.Enqueue(model => GatedText(model, "做完了。", gate.Task));
		var host = new FakeHost(_root);
		host.Streams.Enqueue(childStream);

		using var parent = CreateParent(new ScriptedStreamFn(), host, runTimeout: TimeSpan.FromMinutes(5));
		var hub = Assert.IsType<SubagentHub>(parent.Subagents);
		var started = await hub.StartAsync(new SubagentStartRequest("worker", "改个东西。"));
		Assert.True(started.Ok, started.Text);
		await Eventually.IsTrue(() => hub.Runs.Single().State == SubagentState.Running, "子代理应进入运行态");
		var sessionId = hub.Runs.Single().SessionId;

		// 用户答三次：每次都要真正挂起再回答，证明这条路径不碰父会话的额度。
		for (var index = 0; index < SubagentHub.MaxParentQuestions; index++)
		{
			var asked = hub.AskParentAsync(sessionId, $"用户问题 {index}", null, CancellationToken.None);
			await Eventually.IsTrue(() => hub.PendingQuestion(sessionId) is not null, "问题应挂起");
			Assert.True(hub.AnswerQuestion(sessionId, $"用户答案 {index}"));
			Assert.Equal($"用户答案 {index}", await asked);
		}

		var parentAsk = hub.AskParentAsync(sessionId, "父会话还能答吗？", null, CancellationToken.None);
		await Eventually.IsTrue(() => hub.PendingQuestion(sessionId) is not null, "问题应挂起");
		Assert.True((await hub.SendAsync(started.Name!, "能。")).Ok);
		Assert.Equal("能。", await parentAsk);
		gate.SetResult();
	}

	[Fact]
	public async Task StopResolvesAPendingQuestionAsUnanswered()
	{
		var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var childStream = new ScriptedStreamFn();
		childStream.Enqueue(model => GatedText(model, "做完了。", gate.Task));
		var host = new FakeHost(_root);
		host.Streams.Enqueue(childStream);

		using var parent = CreateParent(new ScriptedStreamFn(), host);
		var hub = Assert.IsType<SubagentHub>(parent.Subagents);
		var started = await hub.StartAsync(new SubagentStartRequest("worker", "改个东西。"));
		Assert.True(started.Ok, started.Text);
		await Eventually.IsTrue(() => hub.Runs.Single().State == SubagentState.Running, "子代理应进入运行态");
		var sessionId = hub.Runs.Single().SessionId;

		var asked = hub.AskParentAsync(sessionId, "要不要改测试？", null, CancellationToken.None, TimeSpan.FromMinutes(5));
		await Eventually.IsTrue(() => hub.PendingQuestion(sessionId) is not null, "问题应挂起");
		Assert.True(hub.Stop(started.Name!).Ok);

		Assert.Null(await asked);
		Assert.Null(hub.PendingQuestion(sessionId));
		Assert.Equal(SubagentState.Stopped, hub.Runs.Single().State);
		gate.SetResult();
	}

	[Fact]
	public async Task AskParentTimesOutWhenNobodyAnswersAndKeepsTheRun()
	{
		var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var childStream = new ScriptedStreamFn();
		childStream.Enqueue(model => GatedText(model, "做完了。", gate.Task));
		var host = new FakeHost(_root);
		host.Streams.Enqueue(childStream);

		using var parent = CreateParent(new ScriptedStreamFn(), host, runTimeout: TimeSpan.FromMinutes(5));
		var hub = Assert.IsType<SubagentHub>(parent.Subagents);
		var started = await hub.StartAsync(new SubagentStartRequest("worker", "改个东西。"));
		Assert.True(started.Ok, started.Text);
		await Eventually.IsTrue(() => hub.Runs.Single().State == SubagentState.Running, "子代理应进入运行态");
		var sessionId = hub.Runs.Single().SessionId;

		var asked = hub.AskParentAsync(sessionId, "要不要改测试？", null, CancellationToken.None, TimeSpan.FromMilliseconds(150));
		await Eventually.IsTrue(() => hub.PendingQuestion(sessionId) is not null, "问题应挂起");
		Assert.Null(await asked);
		Assert.Null(hub.PendingQuestion(sessionId));

		// 到点只解开问题：run 还在跑，子代理自己决定或转问用户。
		Assert.Equal(SubagentState.Running, hub.Runs.Single().State);
		Assert.Null(hub.Runs.Single().Question);

		Assert.True(hub.Stop(started.Name!).Ok);
		gate.SetResult();
	}

	[Fact]
	public async Task IdleEvictionReleasesTheSessionAndKeepsTheRun()
	{
		var childStream = new ScriptedStreamFn();
		childStream.EnqueueText("做完了。");
		var host = new FakeHost(_root, persistChildren: true);
		host.Streams.Enqueue(childStream);

		using var parent = CreateParent(new ScriptedStreamFn(), host);
		var hub = Assert.IsType<SubagentHub>(parent.Subagents);
		var started = await hub.StartAsync(new SubagentStartRequest("worker", "改个东西。"));
		Assert.True(started.Ok, started.Text);
		await Eventually.IsTrue(() => hub.Runs.Single().State == SubagentState.Idle, "一轮后应结束");

		// 一回合做完就是「空闲」：没有挂起的问题就不再需要活会话。
		Assert.True(hub.IsIdle(started.SessionId));

		Assert.True(hub.UnloadSession(started.SessionId));
		Assert.True(host.Children[0].IsDisposed);

		// 条目、状态与结果都留着，只是活会话没了。
		var run = hub.Runs.Single();
		Assert.Equal(SubagentState.Idle, run.State);
		Assert.Equal(1, run.Turns);
		Assert.Contains("做完了", run.Result!);
		Assert.False(hub.UnloadSession(started.SessionId));

		// 打开转录：冷恢复出活会话，而不是另开一份新的。
		var reopened = await hub.ReopenForViewAsync(started.SessionId);
		Assert.NotNull(reopened);
		Assert.False(reopened!.IsDisposed);
		Assert.Same(reopened, await hub.ReopenForViewAsync(started.SessionId));

		// 回收过的会话再发消息：照常冷恢复并跑新一轮。
		Assert.True(hub.UnloadSession(started.SessionId));
		var second = new ScriptedStreamFn();
		second.EnqueueText("第二轮。");
		host.Streams.Enqueue(second);
		Assert.True((await hub.SendAsync(started.Name!, "再来一次。")).Ok);
		await Eventually.IsTrue(
			() => hub.Runs.Single().Turns == 2,
			"回收后再发消息应冷恢复并跑完",
			TimeSpan.FromSeconds(10));
	}

	[Fact]
	public async Task IdleEvictionLeavesRunningAndAskingSubagentsAlone()
	{
		var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var childStream = new ScriptedStreamFn();
		childStream.Enqueue(model => GatedText(model, "做完了。", gate.Task));
		var host = new FakeHost(_root);
		host.Streams.Enqueue(childStream);

		using var parent = CreateParent(new ScriptedStreamFn(), host);
		var hub = Assert.IsType<SubagentHub>(parent.Subagents);
		var started = await hub.StartAsync(new SubagentStartRequest("worker", "改个东西。"));
		Assert.True(started.Ok, started.Text);
		await Eventually.IsTrue(() => hub.Runs.Single().State == SubagentState.Running, "子代理应进入运行态");

		// 运行中：不算空闲，也释放不了。
		Assert.False(hub.IsIdle(started.SessionId));
		Assert.False(hub.UnloadSession(started.SessionId));

		// 停在问题上：也不释放，否则问题就没人接了。
		var asked = hub.AskParentAsync(started.SessionId, "要不要改测试？", null, CancellationToken.None);
		await Eventually.IsTrue(() => hub.PendingQuestion(started.SessionId) is not null, "问题应挂起");
		Assert.False(hub.UnloadSession(started.SessionId));
		Assert.True(hub.AnswerQuestion(started.SessionId, "要"));
		Assert.Equal("要", await asked);

		gate.SetResult();
		await Eventually.IsTrue(() => hub.Runs.Single().State == SubagentState.Idle, "子代理应正常结束");
	}

	/// 描述符与内存状态是两条路径：重开会话前先等这一轮的条目真的落盘。
	private static async Task WaitForDescriptorAsync(string sessionFile, string kind)
	{
		var needle = string.Concat("\"kind\":\"", kind, "\"");
		await Eventually.IsTrue(
			() =>
			{
				try
				{
					return File.Exists(sessionFile) &&
						File.ReadAllText(sessionFile).Contains(needle, StringComparison.Ordinal);
				}
				catch (IOException)
				{
					return false;
				}
			},
			$"等待 {kind} 条目落盘",
			TimeSpan.FromSeconds(10));
	}

	/// 一条被 max tokens 截断的助手消息：收尾通知应把它标出来。
	private static AssistantMessageEventStream TruncatedText(Model model, string text)
	{
		var stream = new AssistantMessageEventStream();
		var blank = ScriptedSessions.Blank(model);
		var message = blank with
		{
			Content = [new TextContent { Text = text }],
			StopReason = StopReason.Length,
			// 输出打满模型上限：不然会话会把 Length 当成可恢复的溢出并重试。
			Usage = new Usage { Input = 10, Output = 4096, TotalTokens = 4106 },
		};
		stream.Push(new StartEvent { Partial = blank });
		stream.Push(new TextStartEvent { ContentIndex = 0, Partial = message });
		stream.Push(new TextDeltaEvent { ContentIndex = 0, Delta = text, Partial = message });
		stream.Push(new TextEndEvent { ContentIndex = 0, Content = text, Partial = message });
		stream.Push(new DoneEvent { Reason = StopReason.Length, Partial = message });
		return stream;
	}

	/// 推完开头就等 gate：这一轮会一直挂到测试放行，父会话于是真的处于运行态。
	private static AssistantMessageEventStream GatedText(Model model, string text, Task gate, long input = 10, long output = 5)
	{
		var stream = new AssistantMessageEventStream();
		_ = Task.Run(async () =>
		{
			var blank = ScriptedSessions.Blank(model);
			var message = blank with
			{
				Content = [new TextContent { Text = text }],
				StopReason = StopReason.Stop,
				Usage = new Usage { Input = input, Output = output, TotalTokens = input + output },
			};

			stream.Push(new StartEvent { Partial = blank });
			stream.Push(new TextStartEvent { ContentIndex = 0, Partial = message });
			await gate.ConfigureAwait(false);
			stream.Push(new TextDeltaEvent { ContentIndex = 0, Delta = text, Partial = message });
			stream.Push(new TextEndEvent { ContentIndex = 0, Content = text, Partial = message });
			stream.Push(new DoneEvent { Reason = StopReason.Stop, Partial = message });
		});

		return stream;
	}

	/// 与 GatedText 同源，但在 gate 放行后才结束一个工具调用：
	/// 用来在「子代理正在跑」的窗口里排 steer，再观察队列更新。
	private static AssistantMessageEventStream GatedToolCall(
		Model model,
		string id,
		string name,
		JsonObject arguments,
		Task gate)
	{
		var stream = new AssistantMessageEventStream();
		_ = Task.Run(async () =>
		{
			var blank = ScriptedSessions.Blank(model);
			var call = new ToolCall { Id = id, Name = name, Arguments = arguments };
			var message = blank with { Content = [call], StopReason = StopReason.ToolUse };

			stream.Push(new StartEvent { Partial = blank });
			stream.Push(new ToolCallStartEvent { ContentIndex = 0, Partial = message });
			stream.Push(new ToolCallEndEvent { ContentIndex = 0, ToolCall = call, Partial = message });
			await gate.ConfigureAwait(false);
			stream.Push(new DoneEvent { Reason = StopReason.ToolUse, Partial = message });
		});

		return stream;
	}

	/// 一条带文字的助手消息加一个工具调用：用来验证工具轮后的进度摘要。
	private static AssistantMessageEventStream TextThenToolCall(
		Model model,
		string text,
		string id,
		string name,
		JsonObject arguments)
	{
		var stream = new AssistantMessageEventStream();
		var blank = ScriptedSessions.Blank(model);
		var call = new ToolCall { Id = id, Name = name, Arguments = arguments };
		var message = blank with
		{
			Content = [new TextContent { Text = text }, call],
			StopReason = StopReason.ToolUse,
		};

		stream.Push(new StartEvent { Partial = blank });
		stream.Push(new TextStartEvent { ContentIndex = 0, Partial = message });
		stream.Push(new TextDeltaEvent { ContentIndex = 0, Delta = text, Partial = message });
		stream.Push(new TextEndEvent { ContentIndex = 0, Content = text, Partial = message });
		stream.Push(new ToolCallStartEvent { ContentIndex = 1, Partial = message });
		stream.Push(new ToolCallEndEvent { ContentIndex = 1, ToolCall = call, Partial = message });
		stream.Push(new DoneEvent { Reason = StopReason.ToolUse, Partial = message });
		return stream;
	}

	/// 建会话很慢的宿主：gate 打开后才真的返回一个子会话。
	private sealed class SlowHost(string root, Task gate) : ISubagentHost
	{
		public List<CodingAgentSession> Children { get; } = [];

		public async Task<CodingAgentSession?> CreateChildAsync(
			SubagentStartContext context,
			CancellationToken cancellationToken)
		{
			await gate.WaitAsync(cancellationToken);
			var child = new CodingAgentSession(new CodingAgentSessionOptions
			{
				Cwd = root,
				Model = ScriptedSessions.Model(),
				StreamFn = new ScriptedStreamFn().Invoke,
				Session = SessionManager.InMemory(root),
				Persist = false,
				Skills = [],
				PromptTemplates = [],
				Settings = new CodingAgentSettings(),
			});
			Children.Add(child);
			return child;
		}
	}

	private sealed class BlockingHost : ISubagentHost
	{
		public async Task<CodingAgentSession?> CreateChildAsync(
			SubagentStartContext context,
			CancellationToken cancellationToken)
		{
			await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
			return null;
		}
	}
}
