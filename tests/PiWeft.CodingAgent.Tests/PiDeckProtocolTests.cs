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

/// PiDeck 私有协议面的形状回归：名字与字段都按 PiDeck 0.10.0 的读取点固定。
/// 名字改错只会静默不显示，所以这里断言字面量；字段形状同理。
public sealed class PiDeckProtocolTests
{
	[Fact]
	public void WireNamesMatchPideck()
	{
		Assert.Equal("__piDeckBatchAsk", PiDeckProtocol.AskEnvelopeKey);
		Assert.Equal("__piDeckBatchAsk__", PiDeckProtocol.AskEnvelopePlaceholder);
		Assert.Equal("__PI_DECK_PLAN_MODE__", PiDeckProtocol.PlanModeMarker);
		Assert.Equal("__PI_DECK_GOAL_MODE__", PiDeckProtocol.GoalModeMarker);
		Assert.Equal("[PI_DECK_PLAN_NEXT]", PiDeckProtocol.PlanNextMarker);

		Assert.Equal("pi-deck-todo", PiDeckProtocol.TodoEntryType);
		Assert.Equal("pi-deck-todo-brief", PiDeckProtocol.TodoBriefEntryType);
		Assert.Equal("pi-deck-todo", PiDeckProtocol.TodoWidgetKey);
		Assert.Equal("[[pid:todo-plan:", PiDeckProtocol.TodoPlanMetadataPrefix);

		Assert.Equal("pi-deck-plan-mode", PiDeckProtocol.PlanEntryType);
		Assert.Equal("pi-deck-plan-todos", PiDeckProtocol.PlanWidgetKey);
		Assert.Equal("pi-deck-plan-execute", PiDeckProtocol.PlanExecuteMessageType);
		Assert.Equal("pi-deck-plan-complete", PiDeckProtocol.PlanCompleteMessageType);

		Assert.Equal("pi-deck-goal-mode", PiDeckProtocol.GoalEntryType);
		Assert.Equal("pi-deck-goal", PiDeckProtocol.GoalWidgetKey);

		// 纯内部名字不借用 PiDeck 前缀。
		Assert.Equal("piweft-goal-continue", PiWeftProtocol.GoalContinueRole);
	}

	[Fact]
	public void TodoSnapshotKeepsTheV3Shape()
	{
		var state = new TodoState(
			new TodoPlan(3, [new TodoItem(7, "ship it", "in_progress"), new TodoItem(8, "test it", "pending")]),
			new TodoPlan(2, [new TodoItem(5, "old", "completed")]),
			NextPlanId: 4,
			NextTodoId: 9);

		var json = TodoTool.Encode(state);

		Assert.Equal(3, json["version"]!.GetValue<int>());
		Assert.Equal(4, json["nextPlanId"]!.GetValue<int>());
		Assert.Equal(9, json["nextTodoId"]!.GetValue<int>());

		var active = Assert.IsType<JsonObject>(json["activePlan"]);
		Assert.Equal(3, active["id"]!.GetValue<int>());
		var item = Assert.IsType<JsonObject>(active["todos"]![0]);
		Assert.Equal(7, item["id"]!.GetValue<int>());
		Assert.Equal("ship it", item["text"]!.GetValue<string>());
		Assert.Equal("in_progress", item["status"]!.GetValue<string>());

		var previous = Assert.IsType<JsonObject>(json["previousPlan"]);
		Assert.Equal(2, previous["id"]!.GetValue<int>());
	}

	[Fact]
	public void TodoWidgetLinesKeepThePideckRowFormat()
	{
		var plan = new TodoPlan(1, [new TodoItem(1, "done", "completed"), new TodoItem(2, "doing", "in_progress"), new TodoItem(3, "todo", "pending")]);

		var lines = TodoTool.WidgetLines(plan, "leaf-1");

		Assert.Equal("[[pid:todo-plan:leaf-1:1]]", lines[0]);
		Assert.Equal("☑ #1 done", lines[1]);
		Assert.Equal("◐ #2 doing", lines[2]);
		Assert.Equal("☐ #3 todo", lines[3]);
	}

	[Fact]
	public void GoalSnapshotKeepsThePideckShape()
	{
		var state = new GoalState(true, GoalState.Blocked, "ship it", 5, 32, "missing key");

		var json = GoalMode.Encode(state);

		Assert.True(json["enabled"]!.GetValue<bool>());
		Assert.Equal("blocked", json["phase"]!.GetValue<string>());
		Assert.Equal("ship it", json["objective"]!.GetValue<string>());
		Assert.Equal(5, json["roundsStarted"]!.GetValue<int>());
		Assert.Equal(32, json["maxGoalRounds"]!.GetValue<int>());
		Assert.Equal("missing key", json["blockReason"]!.GetValue<string>());

		var decoded = GoalMode.Decode(json);
		// 跨重启只恢复为 paused：自动续跑等于绕过用户。阻塞原因保留给界面提示。
		Assert.Equal(GoalState.Paused, decoded.Phase);
		Assert.Equal("missing key", decoded.BlockReason);
		Assert.Equal(5, decoded.RoundsStarted);
	}

	[Fact]
	public void GoalSnapshotCanOmitTheUnchangedObjective()
	{
		var state = new GoalState(true, GoalState.Active, "ship it", 1, 32, null);

		var json = GoalMode.Encode(state, includeObjective: false);

		Assert.False(json.ContainsKey("objective"));
		Assert.False(json.ContainsKey("blockReason"));
		Assert.True(json["enabled"]!.GetValue<bool>());
	}
}

/// 会话实际写进分支的条目形状：PiDeck 打开同一份会话时要能读出来。
public sealed class PiDeckSessionEntryTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-pideck-" + Guid.NewGuid().ToString("N")[..8]);

	public PiDeckSessionEntryTests() => Directory.CreateDirectory(_root);

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	private sealed class NullUiHost : IUiHost
	{
		public bool HasUi => false;

		public Task<string?> InputAsync(UiInputRequest request, CancellationToken cancellationToken) =>
			Task.FromResult<string?>(null);
	}

	private CodingAgentSession Create()
	{
		var stream = new ScriptedStreamFn();
		var session = new CodingAgentSession(new CodingAgentSessionOptions
		{
			Cwd = _root,
			Model = ScriptedSessions.Model(),
			StreamFn = stream.Invoke,
			Session = SessionManager.InMemory(_root),
			Persist = false,
			Skills = [],
			PromptTemplates = [],
			UiHost = new NullUiHost(),
			Settings = new CodingAgentSettings(),
		});
		stream.EnqueueText("ok");
		return session;
	}

	private static JsonObject? LastEntry(CodingAgentSession session, string customType)
	{
		JsonObject? data = null;
		foreach (var entry in session.Session.GetBranch())
		{
			if (entry is CustomEntry custom && custom.CustomType == customType)
			{
				data = custom.Data as JsonObject;
			}
		}

		return data;
	}

	[Fact]
	public void PlanModeEntryCarriesEnabledTodosAndExecuting()
	{
		using var session = Create();
		session.SetPlanMode(true);

		var data = LastEntry(session, PiDeckProtocol.PlanEntryType);
		Assert.NotNull(data);
		Assert.True(data!["enabled"]!.GetValue<bool>());
		Assert.False(data["executing"]!.GetValue<bool>());
		Assert.IsType<JsonArray>(data["todos"]);
	}

	[Fact]
	public void GoalEntryCarriesThePideckFields()
	{
		using var session = Create();
		session.SetGoal("ship it");

		var data = LastEntry(session, PiDeckProtocol.GoalEntryType);
		Assert.NotNull(data);
		Assert.True(data!["enabled"]!.GetValue<bool>());
		Assert.Equal(GoalState.Active, data["phase"]!.GetValue<string>());
		Assert.Equal("ship it", data["objective"]!.GetValue<string>());
		Assert.Equal(0, data["roundsStarted"]!.GetValue<int>());
		Assert.Equal(GoalMode.DefaultMaxRounds, data["maxGoalRounds"]!.GetValue<int>());
	}
}
