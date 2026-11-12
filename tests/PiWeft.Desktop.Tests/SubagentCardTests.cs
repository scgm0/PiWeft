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

	private sealed class FakeHost(string root, Model model) : ISubagentHost
	{
		public Task<CodingAgentSession?> CreateChildAsync(SubagentStartContext context, CancellationToken cancellationToken)
		{
			var child = new CodingAgentSession(new CodingAgentSessionOptions
			{
				Cwd = root,
				Model = model,
				StreamFn = (streamModel, _, _) => FakeStreams.Text(streamModel, "结论。"),
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

	private static async Task WaitForIdleAsync(SubagentHub hub, string name)
	{
		var deadline = Environment.TickCount64 + 10_000;
		while (Environment.TickCount64 < deadline)
		{
			if (hub.Runs.SingleOrDefault(run => run.Name == name)?.State == SubagentState.Idle)
			{
				return;
			}

			await Task.Delay(10);
		}

		throw new TimeoutException("子代理没有在 10 秒内结束第一轮");
	}
}
