using PiWeft.Ai;
using PiWeft.CodingAgent;
using PiWeft.CodingAgent.Session;
using Xunit;

namespace PiWeft.Desktop.Tests;

/// 输入区模式的接线：目标与计划互斥、模式 chip 跟随、目标标记只影响发送形态。
[Collection("mewui-controls")]
public sealed class ComposerModeTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-modes-" + Guid.NewGuid().ToString("N")[..8]);
	private readonly string? _previousAgentDir = Environment.GetEnvironmentVariable("PI_CODING_AGENT_DIR");

	public ComposerModeTests()
	{
		Directory.CreateDirectory(_root);
		// 关联会话的关系表落在 agent 目录：测试指到临时目录，不写用户的 ~/.pi/agent。
		Environment.SetEnvironmentVariable("PI_CODING_AGENT_DIR", Path.Combine(_root, "agent"));
	}

	public void Dispose()
	{
		Environment.SetEnvironmentVariable("PI_CODING_AGENT_DIR", _previousAgentDir);
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	private SessionTab Tab(string reply = "done", ILinkedSessionHost? linkedHost = null)
	{
		var model = FakeStreams.Model();
		var session = new CodingAgentSession(new CodingAgentSessionOptions
		{
			Cwd = _root,
			Model = model,
			Session = SessionManager.InMemory(_root),
			Persist = false,
			Skills = [],
			PromptTemplates = [],
			LinkedSessionHost = linkedHost,
			StreamFn = (streamModel, _, _) => FakeStreams.Text(streamModel, reply),
		});
		return new SessionTab(session, [model], "modes");
	}

	private sealed class NoopLinkedHost : ILinkedSessionHost
	{
		public Task<CodingAgentSession?> CreateChildAsync(LinkedSessionStartContext context, CancellationToken cancellationToken) =>
			Task.FromResult<CodingAgentSession?>(null);
	}

	[Fact]
	public void GoalAndPlanAreExclusiveAndTheChipFollowsTheMode()
	{
		using var tab = Tab();
		Assert.Equal(ComposerMode.Normal, tab.Mode);
		Assert.Null(tab.ModeChipTextForTests);

		tab.SelectModeForTests(ComposerMode.Plan);
		Assert.Equal(ComposerMode.Plan, tab.Mode);
		Assert.True(tab.Session.PlanModeEnabled);
		Assert.Equal(Strings.ModePlan, tab.ModeChipTextForTests);

		tab.SelectModeForTests(ComposerMode.Goal);
		Assert.Equal(ComposerMode.Goal, tab.Mode);
		Assert.False(tab.Session.PlanModeEnabled);
		Assert.Equal(Strings.ModeGoal, tab.ModeChipTextForTests);

		// 再选当前模式 = 回普通。
		tab.SelectModeForTests(ComposerMode.Goal);
		Assert.Equal(ComposerMode.Normal, tab.Mode);
		Assert.Null(tab.ModeChipTextForTests);
	}

	[Fact]
	public void ModeSwitchQueuesUntilTheTurnEnds()
	{
		using var tab = Tab();

		tab.QueueModeForTests(ComposerMode.Plan);

		// 会话状态不动，chip 先给出「本轮后」的交代。
		Assert.Equal(ComposerMode.Normal, tab.Mode);
		Assert.Equal(ComposerMode.Plan, tab.PendingModeForTests);
		Assert.Equal(Strings.ModePlan + Strings.ModePendingSuffix, tab.ModeChipTextForTests);

		tab.ApplyPendingModeForTests();

		Assert.Null(tab.PendingModeForTests);
		Assert.Equal(ComposerMode.Plan, tab.Mode);
		Assert.True(tab.Session.PlanModeEnabled);
	}

	[Fact]
	public void PendingExitKeepsTheChipVisible()
	{
		using var tab = Tab();
		tab.SelectModeForTests(ComposerMode.Plan);

		tab.QueueModeForTests(ComposerMode.Normal);

		Assert.Equal(ComposerMode.Plan, tab.Mode);
		Assert.Equal(Strings.ModePlan + Strings.ModePendingExitSuffix, tab.ModeChipTextForTests);

		tab.ApplyPendingModeForTests();

		Assert.Equal(ComposerMode.Normal, tab.Mode);
		Assert.Null(tab.ModeChipTextForTests);
	}

	[Fact]
	public void LeavingGoalPausesTheRunningObjective()
	{
		using var tab = Tab();
		tab.Session.SetGoal("把构建修到全绿");
		Assert.True(tab.Session.Goal.Running);

		// 目标已在跑：模式 chip 已经是目标（不再需要从菜单进入一次）。
		Assert.Equal(ComposerMode.Goal, tab.Mode);

		tab.SelectModeForTests(ComposerMode.Plan);
		Assert.False(tab.Session.Goal.Running);
		Assert.Equal(GoalState.Paused, tab.Session.Goal.Phase);
		Assert.True(tab.Session.PlanModeEnabled);
	}

	[Fact]
	public async Task GoalModeSendsTheMarkerButKeepsTheVisibleMessagePlain()
	{
		using var tab = Tab("把构建修好了。\nGOAL_COMPLETE");
		tab.SelectModeForTests(ComposerMode.Goal);

		await tab.SendAsync("ship it");

		Assert.Equal("ship it", tab.Session.Goal.Objective);
		Assert.Equal(GoalState.Complete, tab.Session.Goal.Phase);
		var message = Assert.IsType<UserMessage>(tab.Session.Session.BuildSessionContext().Messages.First(item => item is UserMessage));
		Assert.Equal("ship it", message.Content.Text);
		Assert.DoesNotContain(GoalMode.Marker, message.Content.Text);

		// 完成态的目标条不占位（PiDeck 同义）。
		tab.RefreshGoalStrip();
		Assert.False(tab.GoalStripVisible);
	}

	[Fact]
	public void GoalStripFollowsTheGoalState()
	{
		using var tab = Tab();
		Assert.False(tab.GoalStripVisible);

		tab.Session.SetGoal("把构建修到全绿");
		tab.RefreshGoalStrip();
		Assert.True(tab.GoalStripVisible);
		Assert.Equal("进行中 · 0/32", tab.GoalStripPhaseForTests);

		tab.Session.PauseGoal();
		tab.RefreshGoalStrip();
		Assert.True(tab.GoalStripVisible);
		Assert.StartsWith(Strings.GoalPhase(GoalState.Paused), tab.GoalStripPhaseForTests);

		tab.Session.ClearGoal();
		tab.RefreshGoalStrip();
		Assert.False(tab.GoalStripVisible);
	}

	[Fact]
	public void GoalStripStartsCollapsedAndExpandsOnDemand()
	{
		var card = new GoalStripCard(() => { }, () => { }, () => { }, _ => { });
		card.Render(new GoalState(true, GoalState.Active, "把构建修到全绿，并补上回归测试。", 0, 32, null));
		Assert.True(card.IsShown);
		Assert.False(card.IsExpanded);
		Assert.DoesNotContain("把构建", card.Summary);

		card.ToggleExpandedForTests();
		Assert.True(card.IsExpanded);
		card.ToggleExpandedForTests();
		Assert.False(card.IsExpanded);
	}

	[Fact]
	public void GoalStripCollapsesMultilineObjectiveToSingleLinePreview()
	{
		var card = new GoalStripCard(() => { }, () => { }, () => { }, _ => { });
		const string objective = "请参考 @\"/tmp/a\" 优化 UI 细节\n\nUI 改动需要截图验证\n  也要保证无功能倒退。";
		card.Render(new GoalState(true, GoalState.Active, objective, 0, 32, null));

		// 收起态只给一行：换行与缩进折成空格，宽度交给省略号。
		Assert.False(card.IsExpanded);
		Assert.Equal(
			"请参考 @\"/tmp/a\" 优化 UI 细节 UI 改动需要截图验证 也要保证无功能倒退。",
			card.ObjectiveTextForTests);
		Assert.Equal(objective, card.DetailsTextForTests);
	}

	[Fact]
	public void GoalStripShowsTheBlockReasonInsteadOfTheObjective()
	{
		var card = new GoalStripCard(() => { }, () => { }, () => { }, _ => { });

		card.Render(new GoalState(
			true, GoalState.Blocked, "把构建修到全绿", 3, 32, "request failed", "request-failed"));

		// 阻塞时目标位置让给原因；目标全文留在 tooltip。
		Assert.True(card.IsShown);
		Assert.Equal("request failed", card.ObjectiveTextForTests);
	}

	[Fact]
	public void GoalStripShowsTheTokenBudgetOnlyWhenConfigured()
	{
		var card = new GoalStripCard(() => { }, () => { }, () => { }, _ => { });
		card.Render(new GoalState(
			true, GoalState.Active, "把构建修到全绿", 2, 32, null,
			MaxTokens: 1_000, TokensAtStart: 0), usedTokens: 250);

		Assert.Contains("250/1.0k tk", card.PhaseTextForTests);

		card.Render(new GoalState(true, GoalState.Active, "把构建修到全绿", 3, 32, null));
		Assert.DoesNotContain("tk", card.PhaseTextForTests);
	}

	[Fact]
	public void GoalTokenBudgetFollowsUsedTokensWithinTheSameGoal()
	{
		var card = new GoalStripCard(() => { }, () => { }, () => { }, _ => { });
		var goal = new GoalState(true, GoalState.Active, "把构建修到全绿", 2, 32, null,
			MaxTokens: 1_000, TokensAtStart: 0);
		card.Render(goal, usedTokens: 100);
		Assert.Contains("100/1.0k tk", card.PhaseTextForTests);

		// 同一份 GoalState 实例在整轮里不会换；用量变化也必须刷新。
		card.Render(goal, usedTokens: 250);
		Assert.Contains("250/1.0k tk", card.PhaseTextForTests);
	}

	[Fact]
	public void GoalEditorKeepsMultipleLinesAndFileReferences()
	{
		string? edited = null;
		var card = new GoalStripCard(() => { }, () => { }, () => { }, value => edited = value);
		card.Render(new GoalState(true, GoalState.Active, "旧目标", 0, 32, null));

		card.BeginEditForTests();
		card.EditorForTests.Text = "第一行\n第二行";
		card.EditorForTests.Select(card.EditorForTests.Text.Length, 0);
		card.InsertReferencesForTests([Path.Combine(_root, "a b.txt")]);
		Assert.Equal(1, UserReferenceRichText.Count(card.EditorForTests.Text));

		card.CommitEditForTests();

		// 多行保留换行，引用仍是原始完整路径（chip 只是显示投影）。
		Assert.NotNull(edited);
		Assert.StartsWith("第一行\n第二行", edited);
		Assert.Contains("a b.txt", edited);
		Assert.Contains("@\"", edited);
	}

	[Fact]
	public void RunningModeMarkerFollowsTheGoal()
	{
		using var tab = Tab();
		Assert.False(tab.HasRunningMode);
		Assert.Equal(tab.Title, MainWindow.EntryTitle(tab));
		Assert.Null(MainWindow.EntryStatus(tab));
		Assert.Equal(tab.Title, MainWindow.EntryTooltip(tab));

		tab.Session.SetGoal("把构建修到全绿");

		Assert.True(tab.HasRunningMode);
		Assert.Equal(tab.Title, MainWindow.EntryTitle(tab));
		Assert.Equal(Strings.TabGoalShort(0, 32), MainWindow.EntryStatus(tab)?.Text);
		// 状态点之外，悬停要说清是目标而不是计划。
		Assert.Equal($"{tab.Title}\n{Strings.TabGoalRunning(0, 32)}", MainWindow.EntryTooltip(tab));

		tab.Session.PauseGoal();

		Assert.False(tab.HasRunningMode);
		Assert.Equal(tab.Title, MainWindow.EntryTitle(tab));
	}

	[Fact]
	public void GoalProgressRefreshesTheTabWithoutAModeSwitch()
	{
		using var tab = Tab();
		tab.Session.SetGoal("把构建修到全绿", maxRounds: 4);
		// 先落一帧：真实的推送路径（SetExtensionWidget）会在目标建立时就刷过一次。
		tab.RefreshGoalStrip();
		var progress = 0;
		var mode = 0;
		tab.GoalProgressChanged += _ => progress++;
		tab.ModeChanged += _ => mode++;

		// 轮次上限变了、模式没翻转：widget 推送到标签（SetExtensionWidget → RefreshGoalStrip）后，
		// 页签上的「目标 0/4」必须自己跟到 0/8，不能等点到别的页签时那条重建路径顺带刷一次。
		tab.Session.ExtendGoalRounds(4);
		tab.RefreshGoalStrip();

		Assert.Equal(1, progress);
		Assert.Equal(0, mode);
		Assert.Equal(Strings.TabGoalShort(0, 8), MainWindow.EntryStatus(tab)?.Text);
	}

	[Fact]
	public void ModeFollowsARunningGoalWithoutTheLocalSwitch()
	{
		using var tab = Tab();
		// 恢复出来或直接创建的目标没有经过「+」菜单：_goalMode 仍是 false，chip 也要显示目标。
		tab.Session.SetGoal("把构建修到全绿");

		Assert.Equal(ComposerMode.Goal, tab.Mode);

		tab.Session.PauseGoal();

		Assert.Equal(ComposerMode.Normal, tab.Mode);
	}

	[Fact]
	public void SlashCommandsIncludeTheBuiltInModes()
	{
		using var tab = Tab();

		var commands = tab.SlashCommands();

		// 发送路径已经接管这些命令，补全栏也得能发现它们。
		Assert.Contains("plan", commands);
		Assert.Contains("goal", commands);
		Assert.Contains("reload", commands);
	}

	[Fact]
	public async Task SlashCommandsAreHandledWithoutAModelTurn()
	{
		using var tab = Tab();
		tab.SelectModeForTests(ComposerMode.Plan);
		Assert.True(tab.Session.PlanModeEnabled);

		await tab.SendAsync("/plan off");

		// 命中本地命令：不改模式就说明没进模型。
		Assert.False(tab.Session.PlanModeEnabled);
		Assert.False(tab.Session.IsRunning);

		tab.Session.SetGoal("把构建修到全绿");
		await tab.SendAsync("/goal pause");

		Assert.Equal(GoalState.Paused, tab.Session.Goal.Phase);
		// 命令命中后不能又作为正文发给模型。
		Assert.DoesNotContain(
			tab.Session.ProjectedMessages.OfType<UserMessage>(),
			static message => message.Content.Text?.Contains("/goal pause") == true);
	}

	[Fact]
	public void GoalEditKeepsThePhaseAndUpdatesTheStrip()
	{
		using var tab = Tab();
		tab.Session.SetGoal("旧目标", maxRounds: 10);
		tab.Session.PauseGoal();
		tab.RefreshGoalStrip();
		Assert.Equal("旧目标", tab.GoalStripObjectiveForTests);

		tab.Session.EditGoal("新目标");
		tab.RefreshGoalStrip();

		Assert.Equal("新目标", tab.GoalStripObjectiveForTests);
		Assert.StartsWith(Strings.GoalPhase(GoalState.Paused), tab.GoalStripPhaseForTests);
		Assert.Equal(10, tab.Session.Goal.MaxRounds);
	}

	[Fact]
	public void PlanWidgetUsesItsOwnCard()
	{
		using var tab = Tab();

		tab.SetExtensionWidget(PlanSteps.WidgetKey, ["计划草案 2 步", "☐ 1. First step", "☐ 2. Second step"]);

		Assert.True(tab.PlanCardVisible);
		Assert.Equal("计划草案 2 步", tab.PlanCardMetaForTests);
		// 计划有专用卡，不能再以原始行落进「扩展」卡。
		Assert.False(tab.ExtensionWidgetCardVisible);

		tab.SetExtensionWidget(PlanSteps.WidgetKey, null);

		Assert.False(tab.PlanCardVisible);
	}

	[Fact]
	public async Task LinkedCardShowsPendingProposals()
	{
		using var tab = Tab(linkedHost: new NoopLinkedHost());
		var hub = Assert.IsType<LinkedSessionHub>(tab.Session.Linked);
		var result = await hub.StartAsync("补测试", "补一组桌面测试。", LinkedSessionProposal.OwnInitiative);
		Assert.True(result.Ok, result.Text);

		tab.RefreshLinkedSessions();
		Assert.True(tab.LinkedCardVisible);
		Assert.Equal(1, tab.LinkedCardRowCount);
	}

	[Fact]
	public void ComposeLeavesSlashCommandsAloneAndAddsTheGoalMarkerOtherwise()
	{
		Assert.Equal("fix", ComposerPrompt.Compose("fix", ComposerMode.Normal));
		Assert.Equal("fix", ComposerPrompt.Compose("fix", ComposerMode.Plan));
		Assert.Equal("/skill:deploy", ComposerPrompt.Compose("/skill:deploy", ComposerMode.Goal));
		Assert.Equal("  /skill:deploy", ComposerPrompt.Compose("  /skill:deploy", ComposerMode.Goal));
		Assert.Equal($"{GoalMode.Marker}\nfix", ComposerPrompt.Compose("fix", ComposerMode.Goal));
		Assert.Equal($"{GoalMode.Marker}\n{Strings.GoalContinueInstruction}", ComposerPrompt.Compose(string.Empty, ComposerMode.Goal));
	}
}
