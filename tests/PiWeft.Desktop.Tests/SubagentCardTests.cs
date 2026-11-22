using PiWeft.Agent;
using PiWeft.Ai;
using PiWeft.CodingAgent;
using PiWeft.CodingAgent.Session;
using PiWeft.CodingAgent.Subagents;
using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using System.Text.Json.Nodes;
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

	[Fact]
	public void WheelOverALongListGlidesInsteadOfJumping()
	{
		// 卡片常驻在输入框上方：17 个子代理时一屏只看得到三行多，滚动得逐格落下而不是瞬移。
		var card = new SubagentCard(_ => { }, _ => true, _ => { });
		card.RenderRunsForSmoke(Enumerable.Range(0, 17).Select(index => new SubagentRun
		{
			Name = $"scout-{index}",
			SessionId = $"subagent-{index}",
			Agent = "scout",
			Label = $"任务 {index}",
			Model = "test/test-model",
			State = SubagentState.Idle,
			Turns = 1,
			Tokens = 1_000,
			Cost = 0.001,
			StartedAt = 1_000 + index,
			FinishedAt = 2_000 + index,
		}).ToArray());
		ConversationPerformanceTests.Layout(card.View, 800, 720);

		try
		{
			var scroll = Find<ScrollViewer>(card.View).Single(view => view.Content is StackPanel);
			var smooth = Assert.IsType<SmoothScroll>(SmoothScroll.Find(scroll));
			var step = ThemeManager.DefaultMetrics.ScrollWheelStep;
			Assert.True(smooth.MaxVerticalOffset > step, "17 行应该撑出可滚范围");

			var wheel = new MouseWheelEventArgs(
				new Point(0, 0), new Point(0, 0), new Vector(0, -1),
				leftButton: false, rightButton: false, middleButton: false, modifiers: ModifierKeys.None);
			smooth.RouteWheel(wheel);

			Assert.True(wheel.Handled);
			Assert.True(smooth.IsAnimating, "滚轮应交给平滑滚动，而不是由 ScrollViewer 瞬移一格");
			Assert.Equal(step, smooth.TargetVerticalOffset);
			Assert.True(scroll.VerticalOffset < step, "落位要经过动画，不能在一帧内跳到目标");
		}
		finally
		{
			UiLifetime.Release(card.View);
		}
	}

	[Fact]
	public void RowsShowTheTokenBudgetAndTheHostVerification()
	{
		var card = new SubagentCard(_ => { }, _ => true, _ => { });
		card.RenderRunsForSmoke(
		[
			new SubagentRun
			{
				Name = "worker-1",
				SessionId = "subagent-verified",
				Agent = "worker",
				Label = "改完跑测试",
				Model = "test/test-model",
				State = SubagentState.Idle,
				Turns = 1,
				Tokens = 12_000,
				MaxTokens = 50_000,
				StartedAt = 1_000,
				FinishedAt = 2_000,
				Verify = [new SubagentVerifyResult { Command = "dotnet test", ExitCode = 0, Output = "passed" }],
			},
			new SubagentRun
			{
				Name = "worker-2",
				SessionId = "subagent-unverified",
				Agent = "worker",
				Label = "改完没跑过",
				Model = "test/test-model",
				State = SubagentState.Failed,
				Turns = 1,
				Result = "都改好了。",
				StartedAt = 1_000,
				FinishedAt = 2_500,
				Verify = [new SubagentVerifyResult { Command = "dotnet test", ExitCode = 1, Output = "1 failed" }],
			},
			new SubagentRun
			{
				Name = "worker-3",
				SessionId = "subagent-asking",
				Agent = "worker",
				Label = "问父会话要不要改测试",
				Model = "test/test-model",
				State = SubagentState.Running,
				Turns = 1,
				Question = "要不要顺手改测试？",
				StartedAt = 1_000,
			},
			new SubagentRun
			{
				Name = "scout-1",
				SessionId = "subagent-truncated",
				Agent = "scout",
				Label = "写到一半撞上输出上限",
				Model = "test/test-model",
				State = SubagentState.Idle,
				Turns = 1,
				StopReason = "length",
				StartedAt = 1_000,
				FinishedAt = 3_000,
			},
		]);
		ConversationPerformanceTests.Layout(card.View, 800, 720);

		try
		{
			var blocks = Find<Aprillz.MewUI.Controls.TextBlock>(card.View).ToArray();
			// 预算与用量写在一起：只看 12k 不知道离上限还有多远。
			Assert.Contains(blocks, block => block.Text == "12k/50k tk");
			// 宿主自己跑过命令才算验证通过；未通过时收起状态行就直接报退出码。
			Assert.Contains(blocks, block => block.Text == "验证通过：dotnet test");
			Assert.Contains(blocks, block => block.Text == "验证失败：dotnet test（退出码 1）");
			Assert.Contains(blocks, block => block.Text == Strings.SubagentTruncated);
			// 停在 ask_parent 上的行写「问父会话」：父会话可能自己答，超时才转给用户。
			Assert.Contains(blocks, block => block.Text.StartsWith(Strings.SubagentQuestionState, StringComparison.Ordinal));

			// 展开验证过的行：命令、退出码与输出尾巴都在卡里，不必先切到子会话。
			card.ToggleExpandedForTests("worker-1");
			ConversationPerformanceTests.Layout(card.View, 800, 720);
			var expanded = Find<Aprillz.MewUI.Controls.TextBlock>(card.View).ToArray();
			Assert.Contains(expanded, block => block.Text == Strings.SubagentVerifySection);
			Assert.Contains(expanded, block => block.Text == "dotnet test · 退出码 0\npassed");
		}
		finally
		{
			UiLifetime.Release(card.View);
		}
	}

	[Fact]
	public void ExpandedQuestionRowShowsTheQuestionAndAnAnswerBox()
	{
		var card = new SubagentCard(_ => { }, _ => true, _ => { });
		card.RenderRunsForSmoke(
		[
			new SubagentRun
			{
				Name = "worker-1",
				SessionId = "subagent-asking",
				Agent = "worker",
				Label = "问父会话要不要改测试",
				Model = "test/test-model",
				State = SubagentState.Running,
				Turns = 1,
				Question = "要不要顺手改测试？",
				StartedAt = 1_000,
			},
		]);
		ConversationPerformanceTests.Layout(card.View, 800, 720);
		card.ToggleExpandedForTests("worker-1");
		ConversationPerformanceTests.Layout(card.View, 800, 720);

		try
		{
			var blocks = Find<Aprillz.MewUI.Controls.TextBlock>(card.View).ToArray();
			// 父会话可能不在跟前：问题与回答框都放在卡片里，不必等 20 秒摊到问询卡。
			Assert.Contains(blocks, block => block.Text == Strings.SubagentQuestion);
			Assert.Contains(blocks, block => block.Text == "要不要顺手改测试？");
			Assert.Single(Find<TextBox>(card.View));
		}
		finally
		{
			UiLifetime.Release(card.View);
		}
	}

	[Fact]
	public void ExpandedRowShowsTheStructuredOutput()
	{
		var card = new SubagentCard(_ => { }, _ => true, _ => { });
		card.RenderRunsForSmoke(
		[
			new SubagentRun
			{
				Name = "reviewer-1",
				SessionId = "subagent-structured",
				Agent = "reviewer",
				Label = "审查结果",
				Model = "test/test-model",
				State = SubagentState.Idle,
				Turns = 1,
				Result = "审查完成。",
				StructuredOutput = new JsonObject
				{
					["files"] = new JsonArray("src/A.cs"),
				},
				StartedAt = 1_000,
				FinishedAt = 2_000,
			},
		]);
		ConversationPerformanceTests.Layout(card.View, 800, 720);
		card.ToggleExpandedForTests("reviewer-1");
		ConversationPerformanceTests.Layout(card.View, 800, 720);

		try
		{
			var blocks = Find<Aprillz.MewUI.Controls.TextBlock>(card.View).ToArray();
			// 模型与 list_subagents 都拿得到结构化结果；卡片展开区不能漏。
			Assert.Contains(blocks, block => block.Text == Strings.SubagentStructured);
			Assert.Contains(blocks, block => block.Text.Contains("\"files\"", StringComparison.Ordinal));
		}
		finally
		{
			UiLifetime.Release(card.View);
		}
	}

	[Fact]
	public void RunningRowShowsHowLongAgoTheToolStarted()
	{
		var card = new SubagentCard(_ => { }, _ => true, _ => { });
		var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		card.RenderRunsForSmoke(
		[
			new SubagentRun
			{
				Name = "scout-1",
				SessionId = "subagent-long-tool",
				Agent = "scout",
				Label = "跟一次长命令",
				Model = "test/test-model",
				State = SubagentState.Running,
				Turns = 1,
				Activity = "bash dotnet test",
				LastActivityAt = now - 90_000,
				StartedAt = now - 300_000,
			},
		]);
		ConversationPerformanceTests.Layout(card.View, 800, 720);

		try
		{
			var blocks = Find<Aprillz.MewUI.Controls.TextBlock>(card.View).ToArray();
			// 当前工具名十分钟不变时，右侧那个总耗时看不出它是在跑还是卡住。
			Assert.Contains(blocks, block =>
				block.Text.StartsWith("bash dotnet test", StringComparison.Ordinal) &&
				block.Text.Contains("分钟前", StringComparison.Ordinal));
		}
		finally
		{
			UiLifetime.Release(card.View);
		}
	}

	[Fact]
	public void ExpandedRowShowsTheToolCallCount()
	{
		var card = new SubagentCard(_ => { }, _ => true, _ => { });
		card.RenderRunsForSmoke(
		[
			new SubagentRun
			{
				Name = "worker-1",
				SessionId = "subagent-tool-calls",
				Agent = "worker",
				Label = "改完跑测试",
				Model = "test/test-model",
				State = SubagentState.Idle,
				Turns = 1,
				ToolCalls = 12,
				StartedAt = 1_000,
				FinishedAt = 2_000,
			},
		]);
		ConversationPerformanceTests.Layout(card.View, 800, 720);
		card.ToggleExpandedForTests("worker-1");
		ConversationPerformanceTests.Layout(card.View, 800, 720);

		try
		{
			var blocks = Find<Aprillz.MewUI.Controls.TextBlock>(card.View).ToArray();
			// token 与工具数一起看，才分得清「多轮小步」与「一次长任务」。
			Assert.Contains(blocks, block => block.Text == "12 次工具调用");
		}
		finally
		{
			UiLifetime.Release(card.View);
		}
	}

	[Fact]
	public void ExpandedIdleRowOffersAFreeTextMessageBox()
	{
		var card = new SubagentCard(_ => { }, _ => true, _ => { });
		card.RenderRunsForSmoke(
		[
			new SubagentRun
			{
				Name = "worker-1",
				SessionId = "subagent-free-text",
				Agent = "worker",
				Label = "改完跑测试",
				Model = "test/test-model",
				State = SubagentState.Idle,
				Turns = 1,
				StartedAt = 1_000,
				FinishedAt = 2_000,
			},
		]);
		ConversationPerformanceTests.Layout(card.View, 800, 720);
		card.ToggleExpandedForTests("worker-1");
		ConversationPerformanceTests.Layout(card.View, 800, 720);

		try
		{
			// 不必先开标签才能追问；运行中这条消息会走 steer。
			var box = Assert.Single(Find<TextBox>(card.View));
			Assert.Equal(Strings.SubagentMessagePlaceholder, box.Placeholder);
		}
		finally
		{
			UiLifetime.Release(card.View);
		}
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
