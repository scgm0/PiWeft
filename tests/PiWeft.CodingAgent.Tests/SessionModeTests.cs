using System.Text.Json.Nodes;
using PiWeft.Agent;
using PiWeft.Ai;
using PiWeft.CodingAgent;
using PiWeft.CodingAgent.Compatibility;
using PiWeft.CodingAgent.Session;
using PiWeft.CodingAgent.Settings;
using PiWeft.CodingAgent.Tools;
using Xunit;

namespace PiWeft.CodingAgent.Tests;

/// 计划步骤与目标标记的纯逻辑：与 PiDeck 的 <c>pi-deck-plan-mode.ts</c> /
/// <c>pi-deck-goal-mode.ts</c> 同一套解析与文案。
public sealed class PlanStepParsingTests
{
	[Fact]
	public void Extract_FindsNumberedStepsUnderThePlanHeading()
	{
		const string reply = """
			I looked at the module. Two files need work.

			**Plan:**
			1. Add the parser
			2) Wire it into the session
			3、 Write tests
			4: Update docs
			""";

		var steps = PlanSteps.Extract(reply);

		Assert.Equal([1, 2, 3, 4], steps.Select(static step => step.Step));
		Assert.Equal("Add the parser", steps[0].Text);
		Assert.Equal("Wire it into the session", steps[1].Text);
		Assert.All(steps, static step => Assert.False(step.Completed));
	}

	[Fact]
	public void Extract_AcceptsChineseHeadingAndCleansMarkdown()
	{
		const string reply = """
			先说明现状。

			计划：
			1.  **修复** `read` 的边界
			2.  /compact 不动
			3.  ok
			""";

		var steps = PlanSteps.Extract(reply);

		// 「ok」只有两个字符，与 PiDeck 一样被丢弃；斜杠开头的行不算步骤。
		var step = Assert.Single(steps);
		Assert.Equal("修复 read 的边界", step.Text);
	}

	[Fact]
	public void Extract_ReturnsNothingWithoutAPlanHeading()
	{
		Assert.Empty(PlanSteps.Extract("Just a normal reply.\n1. not a plan"));
	}

	[Fact]
	public void MarkCompleted_AppliesDoneMarkersOnce()
	{
		var steps = PlanSteps.Extract("Plan:\n1. First step\n2. Second step").ToList();

		Assert.Equal(2, PlanSteps.MarkCompleted("did it [DONE:1] and [DONE:2]", steps));
		Assert.All(steps, static step => Assert.True(step.Completed));

		// 重复标记不再计数。
		Assert.Equal(0, PlanSteps.MarkCompleted("[DONE:1]", steps));
	}

	[Fact]
	public void WidgetLines_UseThePideckProtocol()
	{
		var steps = PlanSteps.Extract("Plan:\n1. First step\n2. Second step").ToList();
		PlanSteps.MarkCompleted("[DONE:1]", steps);

		var draft = PlanSteps.WidgetLines(steps, executing: false);
		Assert.Equal("计划草案 2 步", draft[0]);
		Assert.Equal("☑ 1. First step", draft[1]);
		Assert.Equal("☐ 2. Second step", draft[2]);

		var executing = PlanSteps.WidgetLines(steps, executing: true);
		Assert.Equal("计划进度 1/2", executing[0]);
	}

	[Fact]
	public void NextTitle_CarriesTheDesktopMarkerAndOptions()
	{
		var steps = PlanSteps.Extract("Plan:\n1. First step").ToList();
		var title = PlanSteps.NextTitle(steps);

		Assert.StartsWith("[PI_DECK_PLAN_NEXT]", title);
		Assert.Contains("计划草案已就绪（1 步）", title);
		Assert.Contains("1. First step", title);
	}
}

public sealed class GoalModeTests
{
	[Fact]
	public void Marker_SplitsTheObjectiveFromTheVisibleText()
	{
		Assert.True(GoalMode.TryStripMarker("__PI_DECK_GOAL_MODE__\n把构建修到全绿", out var objective));
		Assert.Equal("把构建修到全绿", objective);

		Assert.False(GoalMode.TryStripMarker("普通消息", out var plain));
		Assert.Equal("普通消息", plain);
	}

	[Theory]
	[InlineData("All done.\nGOAL_COMPLETE", "complete", null)]
	[InlineData("I cannot continue.\nGOAL_BLOCKED: missing credentials", "blocked", "missing credentials")]
	public void DetectTerminal_FindsTheCompletionTokens(string text, string phase, string? reason)
	{
		var terminal = GoalMode.DetectTerminal(text);

		Assert.NotNull(terminal);
		Assert.Equal(phase, terminal!.Value.Phase);
		Assert.Equal(reason, terminal.Value.Reason);
	}

	[Fact]
	public void DetectTerminal_ReturnsNullForOngoingWork()
	{
		Assert.Null(GoalMode.DetectTerminal("Still working on it."));
	}

	[Fact]
	public void Decode_RestoresActiveGoalsAsPaused()
	{
		var state = GoalMode.Decode(new JsonObject
		{
			["enabled"] = true,
			["phase"] = "active",
			["objective"] = "ship it",
			["roundsStarted"] = 3,
			["maxGoalRounds"] = 32,
		});

		Assert.Equal(GoalState.Paused, state.Phase);
		Assert.Equal("ship it", state.Objective);
		Assert.Equal(3, state.RoundsStarted);
		Assert.False(state.Running);
	}

	[Fact]
	public void Decode_ScansBackForTheObjectiveAndStopsAtEnabledFalse()
	{
		// 目标只在变化时落盘：后续轮次的条目没有 objective。
		var branch = new JsonNode?[]
		{
			GoalMode.Encode(new GoalState(true, GoalState.Active, "ship it", 0, 32, null)),
			GoalMode.Encode(new GoalState(true, GoalState.Active, "ship it", 1, 32, null), includeObjective: false),
			GoalMode.Encode(new GoalState(true, GoalState.Blocked, "ship it", 2, 32, "missing key"), includeObjective: false),
		};

		var state = GoalMode.Decode(branch);

		Assert.Equal("ship it", state.Objective);
		Assert.Equal(2, state.RoundsStarted);
		Assert.Equal("missing key", state.BlockReason);
		Assert.Equal(GoalState.Paused, state.Phase);

		// 清除/完成写在尾部：回溯必须在这里截断，否则旧目标重启后复活。
		branch = [.. branch, GoalMode.Encode(new GoalState(false, GoalState.Paused, string.Empty, 0, 32, null))];
		Assert.Equal(GoalState.Initial, GoalMode.Decode(branch));
	}

	[Fact]
	public void WidgetLines_MatchThePideckShapeAndHideWhenComplete()
	{
		var active = GoalMode.WidgetLines(new GoalState(true, GoalState.Active, "ship it", 3, 32, null));
		Assert.Equal(["active · 3/32", "ship it"], active);

		var blocked = GoalMode.WidgetLines(new GoalState(true, GoalState.Blocked, "ship it", 3, 32, "missing key"));
		Assert.Equal(["blocked · 3/32", "ship it", "missing key"], blocked);

		// PiDeck 的 SessionGoalStrip 完成时不渲染。
		Assert.Empty(GoalMode.WidgetLines(new GoalState(false, GoalState.Complete, "ship it", 3, 32, null)));
	}
}

/// 会话侧的计划/目标编排：需要脚本化模型与可编程的 UI 宿主。
public sealed class SessionModeTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-modes-" + Guid.NewGuid().ToString("N")[..8]);

	public SessionModeTests() => Directory.CreateDirectory(_root);

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	private sealed class ModeUiHost : IUiHost
	{
		public Queue<string?> Selects { get; } = new();

		public List<UiSelectRequest> SelectRequests { get; } = [];

		public List<UiWidgetRequest> Widgets { get; } = [];

		public List<UiNotifyRequest> Notifications { get; } = [];

		public bool HasUi => true;

		public Task<string?> InputAsync(UiInputRequest request, CancellationToken cancellationToken) =>
			Task.FromResult<string?>(null);

		public Task<string?> SelectAsync(UiSelectRequest request, CancellationToken cancellationToken)
		{
			SelectRequests.Add(request);
			return Task.FromResult(Selects.Count > 0 ? Selects.Dequeue() : null);
		}

		public void Notify(UiNotifyRequest request) => Notifications.Add(request);

		public void SetWidget(UiWidgetRequest request) => Widgets.Add(request);

		public IReadOnlyList<string>? LastWidget(string key) =>
			Widgets.LastOrDefault(widget => widget.Key == key)?.Lines;
	}

	/// 计划确认选单一直挂着：用来验证「停止」能打断界面等待。
	private sealed class BlockingUiHost : IUiHost
	{
		public TaskCompletionSource<bool> SelectStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public bool HasUi => true;

		public Task<string?> InputAsync(UiInputRequest request, CancellationToken cancellationToken) =>
			Task.FromResult<string?>(null);

		public async Task<string?> SelectAsync(UiSelectRequest request, CancellationToken cancellationToken)
		{
			SelectStarted.TrySetResult(true);
			await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
			return null;
		}
	}

	private (CodingAgentSession Session, ScriptedStreamFn Stream, ModeUiHost Ui) Create(
		CodingAgentSettings? settings = null,
		SessionManager? manager = null)
	{
		var stream = new ScriptedStreamFn();
		var ui = new ModeUiHost();
		var session = new CodingAgentSession(new CodingAgentSessionOptions
		{
			Cwd = _root,
			Model = ScriptedSessions.Model(),
			StreamFn = stream.Invoke,
			Session = manager ?? SessionManager.InMemory(_root),
			Persist = false,
			Skills = [],
			PromptTemplates = [],
			UiHost = ui,
			Settings = settings ?? new CodingAgentSettings(),
		});
		return (session, stream, ui);
	}

	[Fact]
	public async Task PlanDraft_ExtractsStepsAndStartsExecutionOnApproval()
	{
		var (session, stream, ui) = Create();
		using var _ = session;
		ui.Selects.Enqueue("开始执行|恢复写权限，按步骤改代码并勾进度");

		stream.EnqueueText("Plan:\n1. First concrete step\n2. Second concrete step");
		stream.EnqueueText("Executed step 1 [DONE:1] and step 2 [DONE:2]");

		await session.PromptAsync($"{PlanMode.Marker}\n请先做只读分析");

		// 选单标题带桌面端识别前缀，widget 先推草案。
		var request = Assert.Single(ui.SelectRequests);
		Assert.StartsWith(PlanSteps.NextMarker, request.Title);
		Assert.Contains(
			ui.Widgets,
			static widget => widget.Key == PlanSteps.WidgetKey && widget.Lines is { Count: 3 } lines &&
				lines[0] == "计划草案 2 步");

		// 选「开始执行」后写权限恢复，并且真的跑了一轮执行请求。
		Assert.False(session.PlanModeEnabled);
		Assert.Contains(session.Tools, static tool => tool.Name == "write");
		Assert.Equal(2, stream.Requests.Count);
		Assert.Contains(
			stream.Requests[1],
			static message => message is UserMessage { CustomType: "pi-deck-plan-execute" });

		// 两个 [DONE:n] 让计划收尾：完成消息落进会话，进度清空。
		Assert.False(session.PlanExecutionEnabled);
		Assert.Empty(session.ActivePlanSteps);
		Assert.Contains(
			session.Session.Entries,
			static entry => entry is CustomMessageEntry { CustomType: "pi-deck-plan-complete" });
	}

	[Fact]
	public async Task PlanDraft_StaysReadOnlyWhenTheUserContinuesPlanning()
	{
		var (session, stream, ui) = Create();
		using var _ = session;
		ui.Selects.Enqueue("先不执行|结束本轮，保持只读；再发消息后 AI 才继续");

		stream.EnqueueText("Plan:\n1. First concrete step");

		await session.PromptAsync($"{PlanMode.Marker}\n请先做只读分析");

		Assert.True(session.PlanModeEnabled);
		Assert.False(session.PlanExecutionEnabled);
		Assert.DoesNotContain(session.Tools, static tool => tool.Name == "write");
		Assert.Single(stream.Requests);
		Assert.Equal("计划草案 1 步", ui.LastWidget(PlanSteps.WidgetKey)![0]);
	}

	[Fact]
	public async Task PlanExecution_TracksProgressFromDoneMarkers()
	{
		var (session, stream, ui) = Create();
		using var _ = session;

		stream.EnqueueText("Plan:\n1. First concrete step\n2. Second concrete step");
		// 只完成第一步：执行态保持，进度 1/2。
		stream.EnqueueText("Finished the first part [DONE:1]");
		ui.Selects.Enqueue("开始执行|恢复写权限");

		await session.PromptAsync($"{PlanMode.Marker}\n请先做只读分析");

		Assert.True(session.PlanExecutionEnabled);
		Assert.Equal(2, session.ActivePlanSteps.Count);
		Assert.True(session.ActivePlanSteps[0].Completed);
		Assert.False(session.ActivePlanSteps[1].Completed);
		Assert.Equal("计划进度 1/2", ui.LastWidget(PlanSteps.WidgetKey)![0]);
	}

	[Fact]
	public async Task GoalMode_ContinuesRoundsUntilTheCompletionToken()
	{
		var (session, stream, ui) = Create();
		using var _ = session;

		stream.EnqueueText("First round done.");
		stream.EnqueueText("All requirements met.\nGOAL_COMPLETE");

		await session.PromptAsync($"{GoalMode.Marker}\n把构建修到全绿");

		// 两轮都跑了：第一轮结束后自动 follow-up，第二轮给出完成标记。
		Assert.Equal(2, stream.Requests.Count);
		Assert.Equal(GoalState.Complete, session.Goal.Phase);
		Assert.False(session.Goal.Running);
		// 完成标记出现在第二轮，那一轮不计入已用轮次。
		Assert.Equal(1, session.Goal.RoundsStarted);
		Assert.Contains(ui.Notifications, static notify => notify.Message.Contains("已完成"));
	}

	[Fact]
	public async Task GoalMode_PausesOnTheNextUnmarkedMessage()
	{
		var (session, stream, _) = Create();
		using var _ = session;

		session.SetGoal("把构建修到全绿");
		Assert.True(session.Goal.Running);

		stream.EnqueueText("Normal question answered.");
		await session.PromptAsync("先停一下，回答我一个问题");

		Assert.Equal(GoalState.Paused, session.Goal.Phase);
		Assert.False(session.Goal.Running);
		Assert.Single(stream.Requests);
	}

	[Fact]
	public async Task GoalMode_KeepsTheObjectiveWhenAPausedGoalGetsAMessage()
	{
		var (session, stream, _) = Create();
		using var _ = session;

		session.SetGoal("把构建修到全绿");
		session.PauseGoal();

		stream.EnqueueText("换一条思路继续。\nGOAL_COMPLETE");
		await session.PromptAsync($"{GoalMode.Marker}\n换一条思路试试");

		// 暂停 ≠ 完成：正文只是这一轮的指令，目标不能被替换。
		Assert.Equal("把构建修到全绿", session.Goal.Objective);
		Assert.Equal(GoalState.Complete, session.Goal.Phase);
	}

	[Fact]
	public async Task GoalMode_ResumeRunsARound()
	{
		var (session, stream, _) = Create();
		using var _ = session;

		session.SetGoal("把构建修到全绿");
		session.PauseGoal();
		Assert.False(session.Goal.Running);

		stream.EnqueueText("Resumed and done.\nGOAL_COMPLETE");
		Assert.True(await session.ResumeGoalAsync());

		// 恢复不能只把 phase 标回 active：空闲会话要真的再跑一轮。
		Assert.Single(stream.Requests);
		Assert.Equal(GoalState.Complete, session.Goal.Phase);
		Assert.Contains(
			stream.Requests[0],
			static message => message is UserMessage { CustomType: PiWeftProtocol.GoalContinueRole });
	}

	[Fact]
	public async Task GoalMode_WritesTheObjectiveOncePerGoal()
	{
		var (session, stream, _) = Create();
		using var _ = session;

		stream.EnqueueText("First round.");
		stream.EnqueueText("Finally done.\nGOAL_COMPLETE");
		await session.PromptAsync($"{GoalMode.Marker}\n把构建修到全绿");

		var goalEntries = session.Session.GetBranch()
			.OfType<CustomEntry>()
			.Where(static entry => entry.CustomType == GoalMode.EntryType)
			.Select(static entry => Assert.IsType<JsonObject>(entry.Data))
			.ToList();

		// 两轮各写一份状态，但目标全文只写一次；完成条目不写旧目标。
		Assert.True(goalEntries.Count >= 2);
		Assert.Single(goalEntries, static entry => entry.ContainsKey("objective"));
		Assert.Equal(GoalState.Complete, goalEntries[^1]["phase"]!.GetValue<string>());
	}

	[Fact]
	public async Task GoalMode_PausingDuringTheRoundDoesNotAdvanceOrContinue()
	{
		var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var requests = new List<IReadOnlyList<Message>>();
		var session = new CodingAgentSession(new CodingAgentSessionOptions
		{
			Cwd = _root,
			Model = ScriptedSessions.Model(),
			Session = SessionManager.InMemory(_root),
			Persist = false,
			Skills = [],
			PromptTemplates = [],
			Settings = new CodingAgentSettings(),
			StreamFn = (model, messages, options) =>
			{
				requests.Add([.. messages]);
				var stream = new AssistantMessageEventStream();
				var message = ScriptedSessions.Blank(model) with
				{
					Content = [new TextContent { Text = "working" }],
					StopReason = StopReason.Stop,
				};
				_ = Task.Run(async () =>
				{
					stream.Push(new StartEvent { Partial = message with { Content = [] } });
					stream.Push(new TextStartEvent { ContentIndex = 0, Partial = message });
					stream.Push(new TextDeltaEvent { ContentIndex = 0, Delta = "working", Partial = message });
					started.TrySetResult();
					await gate.Task;
					stream.Push(new DoneEvent { Reason = StopReason.Stop, Partial = message });
				});
				return stream;
			},
		});
		using var scope = session;

		session.SetGoal("把构建修到全绿");
		var run = session.PromptAsync("开始");
		await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

		// 这一轮还没收尾就暂停：轮次不推进，也不再排续轮。
		session.PauseGoal();
		gate.SetResult();
		await run.WaitAsync(TimeSpan.FromSeconds(5));

		Assert.Equal(GoalState.Paused, session.Goal.Phase);
		Assert.Equal(0, session.Goal.RoundsStarted);
		Assert.False(session.Goal.Running);
		Assert.Single(requests);
	}

	[Fact]
	public async Task GoalMode_BlocksAtTheRoundLimit()
	{
		var (session, stream, ui) = Create(new CodingAgentSettings());
		using var _ = session;

		// 上限 1 轮：第一轮结束就阻塞，不再续轮。
		session.SetGoal("never finishes", maxRounds: 1);
		stream.EnqueueText("still going");
		await session.PromptAsync($"{GoalMode.Marker}\nnever finishes");

		Assert.Single(stream.Requests);
		Assert.Equal(GoalState.Blocked, session.Goal.Phase);
		Assert.Contains("reached max rounds", session.Goal.BlockReason);
		Assert.Contains(ui.Notifications, static notify => notify.Message.Contains("轮次上限"));
	}
	[Fact]
	public void SelectText_StripsMarkersAndSplitsLabels()
	{
		Assert.Equal("计划草案已就绪（2 步）", UiSelectText.DisplayTitle("[PI_DECK_PLAN_NEXT] 计划草案已就绪（2 步）"));
		Assert.Equal("普通标题", UiSelectText.DisplayTitle("普通标题"));

		var (label, description) = UiSelectText.SplitOption("开始执行|恢复写权限，按步骤改代码并勾进度");
		Assert.Equal("开始执行", label);
		Assert.Equal("恢复写权限，按步骤改代码并勾进度", description);
		Assert.Equal(("没有说明", (string?)null), UiSelectText.SplitOption("没有说明"));
	}

	[Fact]
	public async Task PlanSelect_IsCancelledByAbort()
	{
		var stream = new ScriptedStreamFn();
		var ui = new BlockingUiHost();
		using var session = new CodingAgentSession(new CodingAgentSessionOptions
		{
			Cwd = _root,
			Model = ScriptedSessions.Model(),
			StreamFn = stream.Invoke,
			Session = SessionManager.InMemory(_root),
			Persist = false,
			Skills = [],
			PromptTemplates = [],
			UiHost = ui,
			Settings = new CodingAgentSettings(),
		});

		stream.EnqueueText("Plan:\n1. First concrete step");
		var run = session.PromptAsync($"{PlanMode.Marker}\n请先做只读分析");
		await ui.SelectStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

		session.Abort();

		// 选单必须随停止立刻收掉；否则整次运行卡在等界面。
		await run.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.True(session.PlanModeEnabled);
	}

	[Fact]
	public async Task GoalMode_CountsRoundsAtAgentEndNotPerAssistantMessage()
	{
		var (session, stream, _) = Create();
		using var _ = session;

		// 一次 run 里有工具调用：turn_end 会出现两次，但只算一轮。
		stream.EnqueueToolCall("t1", "read", new JsonObject { ["path"] = "a.txt" });
		stream.EnqueueText("First round done.");
		stream.EnqueueText("All requirements met.\nGOAL_COMPLETE");

		await session.PromptAsync($"{GoalMode.Marker}\n把构建修到全绿");

		Assert.Equal(3, stream.Requests.Count);
		Assert.Equal(GoalState.Complete, session.Goal.Phase);
		Assert.Equal(1, session.Goal.RoundsStarted);

		// 工具结果之后的那次请求里不能夹着续轮指令：它属于上一轮的中段。
		Assert.DoesNotContain(
			stream.Requests[1],
			static message => message is UserMessage { CustomType: PiWeftProtocol.GoalContinueRole });
	}

	[Fact]
	public async Task GoalMode_RunsMoreThanTwoRounds()
	{
		var (session, stream, _) = Create();
		using var _ = session;

		stream.EnqueueText("Round one.");
		stream.EnqueueText("Round two.");
		stream.EnqueueText("Round three.");
		stream.EnqueueText("Finally done.\nGOAL_COMPLETE");

		await session.PromptAsync($"{GoalMode.Marker}\n把构建修到全绿");

		// 三轮续跑都必须真的发生：第二轮结束时排上的第三轮不能被丢掉。
		Assert.Equal(4, stream.Requests.Count);
		Assert.Equal(GoalState.Complete, session.Goal.Phase);
		Assert.Equal(3, session.Goal.RoundsStarted);
	}

}
