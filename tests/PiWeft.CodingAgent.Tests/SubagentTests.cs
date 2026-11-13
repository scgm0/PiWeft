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
			MaxActiveSubagents = maxActive,
			MaxSubagentSpawns = maxSpawns,
			SubagentRunTimeout = runTimeout ?? TimeSpan.FromMinutes(30),
			Agents = catalog ?? Builtins,
		});

	private sealed class FakeHost(string root, bool addReadTool = false, bool persistChildren = false) : ISubagentHost
	{
		private readonly IReadOnlyList<AgentTool>? _tools = addReadTool
			? [PiWeft.CodingAgent.Tools.FileTools.Read(root)]
			: null;

		public List<SubagentStartContext> Contexts { get; } = [];

		public Queue<ScriptedStreamFn> Streams { get; } = new();

		public List<CodingAgentSession> Children { get; } = [];

		public int ReopenCount { get; private set; }

		/// 恢复前先等这个闸：并发 send 的测试用它把第一个恢复卡在 host 里。
		public TaskCompletionSource? ReopenGate { get; set; }

		public Task<CodingAgentSession?> CreateChildAsync(
			SubagentStartContext context,
			CancellationToken cancellationToken)
		{
			Contexts.Add(context);
			var stream = Streams.Count > 0 ? Streams.Dequeue() : new ScriptedStreamFn();
			// persistChildren 时给子会话真的落一个文件：冷恢复要按文件打开。
			var manager = persistChildren
				? SessionManager.Create(root, Path.Combine(root, "children"))
				: SessionManager.InMemory(root);
			var child = new CodingAgentSession(new CodingAgentSessionOptions
			{
				Cwd = root,
				Model = ScriptedSessions.Model(),
				StreamFn = stream.Invoke,
				Session = manager,
				Persist = persistChildren,
				Skills = [],
				PromptTemplates = [],
				Settings = new CodingAgentSettings(),
				Tools = _tools,
				SubagentParent = context.Parent,
			});
			Children.Add(child);
			return Task.FromResult<CodingAgentSession?>(child);
		}

		public async Task<CodingAgentSession?> ReopenChildAsync(
			SubagentReopenContext context,
			CancellationToken cancellationToken)
		{
			ReopenCount++;
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
		// 父会话正在跑一轮（模拟它阻塞在 wait_subagents 上）：子代理先完成，
		// 结果必须攒着，不能先排成 follow-up 再被 wait 取走 —— 那会让同一段正文进两次上下文。
		var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var parentStream = new ScriptedStreamFn();
		parentStream.Enqueue(model => GatedText(model, "父会话这一轮结束了。", gate.Task));
		var childStream = new ScriptedStreamFn();
		childStream.EnqueueText("子代理结论。");
		var host = new FakeHost(_root);
		host.Streams.Enqueue(childStream);

		using var parent = CreateParent(parentStream, host);
		var hub = Assert.IsType<SubagentHub>(parent.Subagents);

		var running = parent.PromptAsync("先跑一轮。");
		await Eventually.IsTrue(() => parent.IsRunning, "父会话应进入运行态");

		var started = await hub.StartAsync(new SubagentStartRequest("scout", "查一下。"));
		Assert.True(started.Ok, started.Text);
		await Eventually.IsTrue(() => hub.Runs.Single().State == SubagentState.Idle, "子代理第一轮结束");

		// 父会话还在跑：结果不能提前投递（只会补跑一轮请求，不会多出「回报」那一轮）。
		var requestsWhileRunning = parentStream.Requests.Count;
		await Task.Delay(400);
		Assert.Equal(requestsWhileRunning, parentStream.Requests.Count);

		var waited = await hub.WaitAsync([started.Name!], TimeSpan.FromSeconds(5));
		Assert.True(waited.Ok, waited.Text);
		Assert.Contains("子代理结论", waited.Text);

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

		var started = await hub.StartAsync(new SubagentStartRequest("scout", "读一下。"));
		Assert.True(started.Ok, started.Text);
		await Eventually.IsTrue(
			() => hub.Runs.Single().Activity is { Length: > 0 },
			"运行中应报出当前工具");
		Assert.StartsWith("read Auth.cs", hub.Runs.Single().Activity!, StringComparison.Ordinal);

		await Eventually.IsTrue(() => hub.Runs.Single().State == SubagentState.Idle, "子代理应结束");
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

		// 第 7 轮没有自动投递，但正文没丢：wait 仍能取回。
		var waited = await hub.WaitAsync([started.Name!], TimeSpan.FromSeconds(5));
		Assert.True(waited.Ok, waited.Text);
		Assert.Contains($"报告 {SubagentHub.MaxAutoContinue + 1}。", waited.Text);
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
	}

	/// 推完开头就等 gate：这一轮会一直挂到测试放行，父会话于是真的处于运行态。
	private static AssistantMessageEventStream GatedText(Model model, string text, Task gate)
	{
		var stream = new AssistantMessageEventStream();
		_ = Task.Run(async () =>
		{
			var blank = ScriptedSessions.Blank(model);
			var message = blank with
			{
				Content = [new TextContent { Text = text }],
				StopReason = StopReason.Stop,
				Usage = new Usage { Input = 10, Output = 5, TotalTokens = 15 },
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
