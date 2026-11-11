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

	private static SubagentCatalog Builtins => SubagentCatalog.Discover(Path.GetTempPath(), "missing-agent-dir", includeProject: false);

	private CodingAgentSession CreateParent(
		ScriptedStreamFn stream,
		ISubagentHost? host,
		SubagentCatalog? catalog = null,
		int depth = 0,
		int maxActive = 4,
		int maxSpawns = 32,
		SessionManager? session = null) =>
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

		public Task<CodingAgentSession?> ReopenChildAsync(
			SubagentReopenContext context,
			CancellationToken cancellationToken)
		{
			ReopenCount++;
			if (!File.Exists(context.SessionFile))
			{
				return Task.FromResult<CodingAgentSession?>(null);
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
			return Task.FromResult<CodingAgentSession?>(child);
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
		Assert.Equal(SubagentState.Idle, restored.State);
		Assert.Equal(1, restored.Turns);
		Assert.Contains("结论", restored.Result);
		Assert.True(restored.Restored);
		Assert.False(string.IsNullOrEmpty(restored.SessionId));
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
		Assert.DoesNotContain("\n", run.Label);
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
