using PiWeft.Agent;
using PiWeft.Ai;
using PiWeft.CodingAgent;
using PiWeft.CodingAgent.Session;
using PiWeft.CodingAgent.Subagents;
using Aprillz.MewUI.Controls;
using Xunit;
using static PiWeft.Desktop.Tests.VisualTree;

namespace PiWeft.Desktop.Tests;

/// 子代理条：有子代理时显示行，点行打开标签，运行中的可以停。
[Collection("mewui-controls")]
public sealed class SubagentCardTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-subagent-card-" + Guid.NewGuid().ToString("N")[..8]);

	public SubagentCardTests() => Directory.CreateDirectory(_root);

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	private sealed class FakeHost(string root, Model model, Func<Model, AssistantMessageEventStream>? stream = null) : ISubagentHost
	{
		public Task<CodingAgentSession?> CreateChildAsync(SubagentStartContext context, CancellationToken cancellationToken)
		{
			var child = new CodingAgentSession(new CodingAgentSessionOptions
			{
				Cwd = root,
				Model = model,
				StreamFn = (streamModel, _, _) => (stream ?? (static m => FakeStreams.Text(m, "结论。")))(streamModel),
				Session = SessionManager.InMemory(root),
				Persist = false,
				Skills = [],
				PromptTemplates = [],
			});
			return Task.FromResult<CodingAgentSession?>(child);
		}
	}

	[Fact]
	public async Task FinishedSubagentShowsOneRowAndTheSkipWhenThereAreNone()
	{
		var model = FakeStreams.Model();
		using var session = new CodingAgentSession(new CodingAgentSessionOptions
		{
			Cwd = _root,
			Model = model,
			StreamFn = (streamModel, _, _) => FakeStreams.Text(streamModel, "完成。"),
			Session = SessionManager.InMemory(_root),
			Persist = false,
			Skills = [],
			PromptTemplates = [],
			SubagentHost = new FakeHost(_root, model),
			Agents = SubagentCatalog.Discover(_root, "missing-agent-dir", includeProject: false),
		});

		var hub = Assert.IsType<SubagentHub>(session.Subagents);
		var started = await hub.StartAsync(new SubagentStartRequest("scout", "看下登录流程。"));
		Assert.True(started.Ok, started.Text);
		await WaitForIdleAsync(hub, "scout-1");

		using var tab = new SessionTab(session, [model], "subagents");
		ConversationPerformanceTests.Layout(tab.View, 960, 720);

		Assert.True(tab.SubagentCardVisible);
		Assert.Equal(1, tab.SubagentCardRowCount);
		// 完成一轮是「已完成」，不是「等待中」：Idle 表示还能继续发消息，不是在等什么。
		Assert.Contains(Find<Aprillz.MewUI.Controls.TextBlock>(tab.View), block => block.Text.StartsWith("已完成 · 1 轮", StringComparison.Ordinal));
	}

	[Fact]
	public async Task ExpandingARowShowsTaskResultAndModelWithoutOpeningTheTab()
	{
		var model = FakeStreams.Model();
		using var session = new CodingAgentSession(new CodingAgentSessionOptions
		{
			Cwd = _root,
			Model = model,
			StreamFn = (streamModel, _, _) => FakeStreams.Text(streamModel, "完成。"),
			Session = SessionManager.InMemory(_root),
			Persist = false,
			Skills = [],
			PromptTemplates = [],
			SubagentHost = new FakeHost(_root, model),
			Agents = SubagentCatalog.Discover(_root, "missing-agent-dir", includeProject: false),
		});

		var hub = Assert.IsType<SubagentHub>(session.Subagents);
		var started = await hub.StartAsync(new SubagentStartRequest("scout", "看下登录流程。"));
		Assert.True(started.Ok, started.Text);
		await WaitForIdleAsync(hub, "scout-1");

		using var tab = new SessionTab(session, [model], "subagents-expanded");
		ConversationPerformanceTests.Layout(tab.View, 960, 720);
		tab.SubagentCardForTests.ToggleExpandedForTests("scout-1");
		ConversationPerformanceTests.Layout(tab.View, 960, 720);

		var blocks = Find<Aprillz.MewUI.Controls.TextBlock>(tab.View);
		Assert.Contains(blocks, block => block.Text == Strings.SubagentBrief);
		Assert.Contains(blocks, block => block.Text == "看下登录流程。");
		Assert.Contains(blocks, block => block.Text == Strings.SubagentResult);
		Assert.Contains(blocks, block => block.Text == "结论。");
		Assert.Contains(blocks, block => block.Text.StartsWith("scout · test/test-model", StringComparison.Ordinal));
	}

	[Fact]
	public async Task RefreshWithoutChangesDoesNotReformatTheRows()
	{
		var model = FakeStreams.Model();
		using var session = new CodingAgentSession(new CodingAgentSessionOptions
		{
			Cwd = _root,
			Model = model,
			StreamFn = (streamModel, _, _) => FakeStreams.Text(streamModel, "完成。"),
			Session = SessionManager.InMemory(_root),
			Persist = false,
			Skills = [],
			PromptTemplates = [],
			SubagentHost = new FakeHost(_root, model),
			Agents = SubagentCatalog.Discover(_root, "missing-agent-dir", includeProject: false),
		});

		var hub = Assert.IsType<SubagentHub>(session.Subagents);
		var started = await hub.StartAsync(new SubagentStartRequest("scout", "看下登录流程。"));
		Assert.True(started.Ok, started.Text);
		await WaitForIdleAsync(hub, "scout-1");

		using var tab = new SessionTab(session, [model], "subagents-alloc");
		ConversationPerformanceTests.Layout(tab.View, 960, 720);
		for (var index = 0; index < 8; index++)
		{
			tab.RefreshSubagents();
		}

		const int iterations = 100;
		var before = GC.GetAllocatedBytesForCurrentThread();
		for (var index = 0; index < iterations; index++)
		{
			tab.RefreshSubagents();
		}

		var perRefresh = (GC.GetAllocatedBytesForCurrentThread() - before) / (double)iterations;
		// 稳定期只剩 hub 快照数组（调用方独占，无法复用）。再每次拼状态字、身份行就会退回几百字节。
		Assert.True(perRefresh < 256, $"每次刷新分配 {perRefresh:0.0} B，超过只复制快照的量");
	}

	[Fact]
	public async Task FailedSubagentShowsTheReasonWithoutExpanding()
	{
		var model = FakeStreams.Model();
		using var session = new CodingAgentSession(new CodingAgentSessionOptions
		{
			Cwd = _root,
			Model = model,
			StreamFn = (streamModel, _, _) => FakeStreams.Text(streamModel, "完成。"),
			Session = SessionManager.InMemory(_root),
			Persist = false,
			Skills = [],
			PromptTemplates = [],
			SubagentHost = new FakeHost(_root, model, static m => FakeStreams.Error(m, "Request aborted")),
			Agents = SubagentCatalog.Discover(_root, "missing-agent-dir", includeProject: false),
		});

		var hub = Assert.IsType<SubagentHub>(session.Subagents);
		var started = await hub.StartAsync(new SubagentStartRequest("scout", "看下登录流程。"));
		Assert.True(started.Ok, started.Text);
		await WaitForStateAsync(hub, "scout-1", SubagentState.Failed);

		using var tab = new SessionTab(session, [model], "subagents-failed");
		ConversationPerformanceTests.Layout(tab.View, 960, 720);

		var blocks = Find<Aprillz.MewUI.Controls.TextBlock>(tab.View).ToArray();
		// 收起时也要看得到失败原因，不必先展开再翻「最近回复」。
		var reason = Assert.Single(blocks, block => block.Text == "Request aborted");
		Assert.True(reason.IsVisible);
		Assert.Equal(AppStyles.RoleError, reason.Tag);

		var status = Assert.Single(blocks, block => block.Text.StartsWith("失败 · 1 轮", StringComparison.Ordinal));
		Assert.Equal(AppStyles.RoleError, status.Tag);

		// 耗时只在行右侧走一次秒表；不再有第二份「总耗时」把同一件事说两遍。
		Assert.DoesNotContain(blocks, block => block.Text.StartsWith("总耗时", StringComparison.Ordinal));
	}

	[Fact]
	public void SessionWithoutAHostHidesTheCard()
	{
		var model = FakeStreams.Model();
		using var session = new CodingAgentSession(new CodingAgentSessionOptions
		{
			Cwd = _root,
			Model = model,
			StreamFn = (streamModel, _, _) => FakeStreams.Text(streamModel, "完成。"),
			Session = SessionManager.InMemory(_root),
			Persist = false,
			Skills = [],
			PromptTemplates = [],
		});

		using var tab = new SessionTab(session, [model], "no-subagents");
		ConversationPerformanceTests.Layout(tab.View, 960, 720);

		Assert.False(tab.SubagentCardVisible);
		Assert.Equal(0, tab.SubagentCardRowCount);
	}

	private static async Task WaitForIdleAsync(SubagentHub hub, string name) =>
		await WaitForStateAsync(hub, name, SubagentState.Idle);

	private static async Task WaitForStateAsync(SubagentHub hub, string name, SubagentState state)
	{
		var deadline = Environment.TickCount64 + 10_000;
		while (Environment.TickCount64 < deadline)
		{
			if (hub.Runs.SingleOrDefault(run => run.Name == name)?.State == state)
			{
				return;
			}

			await Task.Delay(10);
		}

		throw new TimeoutException($"子代理没有在 10 秒内进入 {state}");
	}
}
