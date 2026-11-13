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
	public void LeavingGoalPausesTheRunningObjective()
	{
		using var tab = Tab();
		tab.Session.SetGoal("把构建修到全绿");
		Assert.True(tab.Session.Goal.Running);

		tab.SelectModeForTests(ComposerMode.Goal);
		Assert.True(tab.Session.Goal.Running);

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
		Assert.Equal(
			Strings.GoalProgress(Strings.GoalPhase(GoalState.Active), 0, GoalMode.DefaultMaxRounds),
			tab.GoalStripPhaseForTests);

		tab.Session.PauseGoal();
		tab.RefreshGoalStrip();
		Assert.True(tab.GoalStripVisible);
		Assert.StartsWith(Strings.GoalPhase(GoalState.Paused), tab.GoalStripPhaseForTests);

		tab.Session.ClearGoal();
		tab.RefreshGoalStrip();
		Assert.False(tab.GoalStripVisible);
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
