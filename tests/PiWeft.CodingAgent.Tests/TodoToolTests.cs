using System.Text.Json.Nodes;
using PiWeft.Agent;
using PiWeft.Ai;
using PiWeft.CodingAgent.Session;
using PiWeft.CodingAgent.Tools;
using Xunit;

namespace PiWeft.CodingAgent.Tests;

/// <summary>C-01/D-01：原生 todo 工具与 PiDeck v3 快照的读写兼容。</summary>
public class TodoToolTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-todo-" + Guid.NewGuid().ToString("N")[..8]);

	public TodoToolTests() => Directory.CreateDirectory(_root);

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	private CodingAgentSession Session(SessionManager? manager = null, ScriptedStreamFn? script = null) =>
		new(new CodingAgentSessionOptions
		{
			Cwd = _root,
			Model = ScriptedSessions.Model(),
			StreamFn = (script ?? new ScriptedStreamFn()).Invoke,
			Session = manager ?? SessionManager.Create(_root, Path.Combine(_root, "sessions")),
			Skills = [],
			PromptTemplates = [],
		});

	private static AgentTool Todo(CodingAgentSession session) =>
		session.Tools.Single(static tool => tool.Name == TodoTool.Name);

	private static async Task<AgentToolResult> InvokeAsync(AgentTool tool, JsonObject arguments) =>
		await tool.Execute(new AgentToolInvocation
		{
			ToolCallId = "1",
			ToolName = tool.Name,
			Arguments = arguments,
		});

	private static JsonObject Args(string action) => new() { ["action"] = action };

	private static string SnapshotId(SessionManager manager) =>
		manager.Entries.OfType<CustomEntry>().Last(static entry => entry.CustomType == TodoTool.EntryType).Id;

	[Fact]
	public async Task Add_CreatesPlanAndPersistsPiDeckSnapshot()
	{
		using var session = Session();
		var tool = Todo(session);

		var result = await InvokeAsync(tool, new JsonObject { ["action"] = "add", ["text"] = "ship it" });

		Assert.False(result.IsError);
		var text = Assert.IsType<TextContent>(result.Content[0]).Text;
		Assert.Contains("Added todo #1: ship it", text);
		Assert.Contains("Todo plan #1: 0 completed, 0 in progress, 1 pending (1 items)", text);

		var snapshot = session.Session.Entries.OfType<CustomEntry>().Single();
		Assert.Equal(TodoTool.EntryType, snapshot.CustomType);

		var state = TodoTool.Decode(snapshot.Data)!;
		var plan = Assert.IsType<TodoPlan>(state.ActivePlan);
		Assert.Equal(1, plan.Id);
		Assert.Equal("ship it", Assert.Single(plan.Items).Text);
		Assert.Equal("pending", plan.Items[0].Status);
	}

	[Fact]
	public async Task Update_CompletesItemAndIsIdempotent()
	{
		using var session = Session();
		var tool = Todo(session);
		await InvokeAsync(tool, new JsonObject { ["action"] = "add", ["text"] = "one" });

		var updated = await InvokeAsync(tool, new JsonObject { ["action"] = "update", ["id"] = 1, ["status"] = "completed" });
		Assert.Contains("Updated todo #1 → completed", Assert.IsType<TextContent>(updated.Content[0]).Text);

		// 重复设置同一状态不写入新快照。
		var entriesBefore = session.Session.Entries.Count;
		var same = await InvokeAsync(tool, new JsonObject { ["action"] = "update", ["id"] = 1, ["status"] = "completed" });
		Assert.Contains("already completed", Assert.IsType<TextContent>(same.Content[0]).Text);
		Assert.Equal(entriesBefore, session.Session.Entries.Count);
	}

	[Fact]
	public async Task Delete_RemovesSingleItem()
	{
		using var session = Session();
		var tool = Todo(session);
		await InvokeAsync(tool, new JsonObject { ["action"] = "add", ["text"] = "one" });
		await InvokeAsync(tool, new JsonObject { ["action"] = "add", ["text"] = "two" });

		var result = await InvokeAsync(tool, new JsonObject { ["action"] = "delete", ["id"] = 1 });

		Assert.Contains("Deleted todo #1: one", Assert.IsType<TextContent>(result.Content[0]).Text);
		var state = TodoTool.Decode(session.Session.Entries.OfType<CustomEntry>().Last().Data)!;
		Assert.Equal("two", Assert.Single(state.ActivePlan!.Items).Text);
	}

	[Fact]
	public async Task ReplaceThenRestore_SwapsPlans()
	{
		using var session = Session();
		var tool = Todo(session);
		await InvokeAsync(tool, new JsonObject { ["action"] = "add", ["text"] = "old" });

		var replace = await InvokeAsync(tool, new JsonObject
		{
			["action"] = "replace",
			["items"] = new JsonArray(new JsonObject { ["text"] = "new", ["status"] = "in_progress" }),
		});
		Assert.Contains("Replaced the current plan with 1 todos", Assert.IsType<TextContent>(replace.Content[0]).Text);

		var replaced = TodoTool.Decode(session.Session.Entries.OfType<CustomEntry>().Last().Data)!;
		Assert.Equal("new", Assert.Single(replaced.ActivePlan!.Items).Text);
		Assert.Equal("old", Assert.Single(replaced.PreviousPlan!.Items).Text);

		await InvokeAsync(tool, Args("restore"));
		var restored = TodoTool.Decode(session.Session.Entries.OfType<CustomEntry>().Last().Data)!;
		Assert.Equal("old", Assert.Single(restored.ActivePlan!.Items).Text);
		Assert.Equal("new", Assert.Single(restored.PreviousPlan!.Items).Text);

		await InvokeAsync(tool, Args("clear"));
		var cleared = TodoTool.Decode(session.Session.Entries.OfType<CustomEntry>().Last().Data)!;
		Assert.Null(cleared.ActivePlan);
	}

	[Fact]
	public async Task UnknownId_ReportsAvailableIds()
	{
		using var session = Session();
		var tool = Todo(session);
		await InvokeAsync(tool, new JsonObject { ["action"] = "add", ["text"] = "one" });

		var result = await InvokeAsync(tool, new JsonObject { ["action"] = "update", ["id"] = 9, ["status"] = "completed" });

		Assert.True(result.IsError);
		Assert.Contains("#9 not found; current ids: 1", Assert.IsType<TextContent>(result.Content[0]).Text);
	}

	[Fact]
	public async Task List_DoesNotWriteASnapshot()
	{
		using var session = Session();
		var tool = Todo(session);
		await InvokeAsync(tool, new JsonObject { ["action"] = "add", ["text"] = "one" });
		var before = session.Session.Entries.Count;

		var listed = await InvokeAsync(tool, Args("list"));

		Assert.Equal(before, session.Session.Entries.Count);
		Assert.Contains("Todo plan #1", Assert.IsType<TextContent>(listed.Content[0]).Text);
	}

	[Fact]
	public async Task BranchSwitch_RestoresThatBranchPlan()
	{
		var manager = SessionManager.Create(_root, Path.Combine(_root, "sessions"));
		using var session = Session(manager);
		var tool = Todo(session);

		await InvokeAsync(tool, new JsonObject { ["action"] = "add", ["text"] = "branch a" });
		var forkPoint = SnapshotId(manager);

		// 在新分支上重建完全不同的计划。
		manager.Branch(forkPoint);
		await InvokeAsync(tool, new JsonObject { ["action"] = "replace", ["items"] = new JsonArray(new JsonObject { ["text"] = "branch b" }) });

		var onB = TodoTool.Decode(manager.Entries.OfType<CustomEntry>().Last().Data)!;
		Assert.Equal("branch b", Assert.Single(onB.ActivePlan!.Items).Text);

		// 回到最初的分支：应恢复该分支上的计划。
		manager.Branch(forkPoint);
		var listed = await InvokeAsync(tool, Args("list"));
		var text = Assert.IsType<TextContent>(listed.Content[0]).Text;
		Assert.Contains("branch a", text);
		Assert.DoesNotContain("branch b", text);
	}

	[Fact]
	public void Decode_RejectsLegacyAndMalformedSnapshots()
	{
		// 非 v3 版本视为无计划。
		Assert.Null(TodoTool.Decode(new JsonObject { ["version"] = 2, ["activePlan"] = new JsonObject { ["id"] = 1, ["todos"] = new JsonArray() } }));
		// 声明了 activePlan 但条目非法（缺 status）时整份快照不信任。
		Assert.Null(TodoTool.Decode(new JsonObject
		{
			["version"] = 3,
			["activePlan"] = new JsonObject
			{
				["id"] = 1,
				["todos"] = new JsonArray(new JsonObject { ["id"] = 1, ["text"] = "bad" }),
			},
			["nextPlanId"] = 2,
			["nextTodoId"] = 2,
		}));
		// 空计划合法。
		Assert.NotNull(TodoTool.Decode(new JsonObject { ["version"] = 3, ["nextPlanId"] = 1, ["nextTodoId"] = 1 }));
	}

	[Fact]
	public async Task CurrentTodo_TracksMutationsAndCachesByRevision()
	{
		using var session = Session();
		Assert.Null(session.CurrentTodo().ActivePlan);

		await InvokeAsync(Todo(session), new JsonObject { ["action"] = "add", ["text"] = "one" });

		var first = session.CurrentTodo();
		Assert.Equal("one", Assert.Single(first.ActivePlan!.Items).Text);
		// 未变更时复用同一实例（桌面渲染据此避免重建控件）。
		Assert.Same(first, session.CurrentTodo());
	}

	[Fact]
	public async Task AgentInvocation_PersistsSnapshotAndReloadsIt()
	{
		var script = new ScriptedStreamFn();
		var manager = SessionManager.Create(_root, Path.Combine(_root, "sessions"));
		using var session = Session(manager, script);

		script.EnqueueToolCall("call-1", TodoTool.Name, new JsonObject { ["action"] = "add", ["text"] = "from agent" });
		script.EnqueueText("done");

		await session.PromptAsync("please track this");

		var entry = manager.Entries.OfType<CustomEntry>().Single(static item => item.CustomType == TodoTool.EntryType);
		Assert.Equal("from agent", Assert.Single(TodoTool.Decode(entry.Data)!.ActivePlan!.Items).Text);

		// 重载后从会话文件重建，与 PiDeck 读取同一份数据。
		var reloaded = SessionManager.Open(manager.SessionFile!, Path.Combine(_root, "sessions"));
		Assert.Equal("from agent", Assert.Single(TodoTool.Current(reloaded).ActivePlan!.Items).Text);
	}

	[Fact]
	public void Encode_RoundTripsThroughDecode()
	{
		var state = new TodoState(
			new TodoPlan(2, [new TodoItem(3, "alpha", "in_progress"), new TodoItem(4, "beta", "completed")]),
			new TodoPlan(1, [new TodoItem(1, "old", "pending")]),
			3,
			5);

		var decoded = TodoTool.Decode(TodoTool.Encode(state))!;

		Assert.Equal(2, decoded.ActivePlan!.Id);
		Assert.Equal(2, decoded.ActivePlan.Items.Count);
		Assert.Equal("alpha", decoded.ActivePlan.Items[0].Text);
		Assert.Equal("in_progress", decoded.ActivePlan.Items[0].Status);
		Assert.Equal(1, decoded.PreviousPlan!.Id);
		Assert.Equal(3, decoded.NextPlanId);
		Assert.Equal(5, decoded.NextTodoId);
	}
}
