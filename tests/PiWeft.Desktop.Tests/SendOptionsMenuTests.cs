using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using PiWeft.Ai;
using PiWeft.CodingAgent;
using PiWeft.CodingAgent.Session;
using Xunit;
using static PiWeft.Desktop.Tests.VisualTree;

namespace PiWeft.Desktop.Tests;

/// <summary>
/// 发送弹窗（PiDeck 的 Split Send 同款）：投递方式是两行项——标题写动作、
/// 第二行写协议名与生效时机。点击项即执行对应动作（弹窗自身的开关由框
/// 架的原生 Popup 负责，无头测试里不涉及）。
/// </summary>
[Collection("mewui-controls")]
public sealed class SendOptionsMenuTests
{
	[Fact]
	public void MenuShowsTitleAndTimingForEachModeAndRunsTheChosenOne()
	{
		var selected = new List<string>();
		var content = new SendOptionsMenu().BuildForTests(
		[
			new SendOptionsMenu.Item("arrow-up", Strings.SteerCurrentTurn, Strings.SteerCurrentTurnHint, () => selected.Add("steer")),
			new SendOptionsMenu.Item("list-ordered", Strings.QueueNextTurn, Strings.QueueNextTurnHint, () => selected.Add("followUp")),
		]);

		var texts = Find<TextBlock>(content).Select(block => block.Text).ToArray();
		// 每一行都是「动作标题 + 生效时机」，而不是把协议名塞进标题尾巴。
		Assert.Contains(Strings.SteerCurrentTurn, texts);
		Assert.Contains(Strings.SteerCurrentTurnHint, texts);
		Assert.Contains(Strings.QueueNextTurn, texts);
		Assert.Contains(Strings.QueueNextTurnHint, texts);

		// 两项各一行按钮；点第二项跑 follow-up 那一条，不串到第一项。
		var rows = Find<Button>(content).ToArray();
		Assert.Equal(2, rows.Length);
		ImagePreviewTests.Click(rows[1]);
		Assert.Equal(["followUp"], selected);
	}

	[Fact]
	public void RowsKeepTheIconOffTheTextAndGroupAbortBehindASeparator()
	{
		var content = Assert.IsType<Border>(new SendOptionsMenu().BuildForTests(
		[
			new SendOptionsMenu.Item("arrow-up", Strings.SteerCurrentTurn, Strings.SteerCurrentTurnHint, static () => { }),
			new SendOptionsMenu.Item("list-ordered", Strings.QueueNextTurn, Strings.QueueNextTurnHint, static () => { }),
			new SendOptionsMenu.Item("x", Strings.Abort, Strings.AbortHint, static () => { }, AppStyles.RoleError, SeparatorBefore: true),
		]));
		content.IsVisible = true;
		content.Measure(new Size(320, 400));
		content.Arrange(new Rect(0, 0, 320, 400));

		// 图标与标题之间必须留出间距：贴在一起时读者第一眼看到的是「×加入当前回合」。
		var icons = Find<Image>(content).ToArray();
		var title = Find<TextBlock>(content).First(text => text.Text == Strings.SteerCurrentTurn);
		Assert.Equal(3, icons.Length);
		Assert.All(icons, icon => Assert.True(icon.Bounds.Right < title.Bounds.X,
			$"图标右缘 {icon.Bounds.Right} 应与标题左缘 {title.Bounds.X} 分开"));

		// 中断前有一条发丝线，把破坏性动作与两种投递方式分开。
		var separators = Find<Border>(content)
			.Where(border => border.ActualHeight is > 0 and < 2 && border.ActualWidth > 100)
			.ToArray();
		Assert.Single(separators);
	}

	[Fact]
	public void MenuCarriesAnIconPerModeSoTheThreeRowsReadApart()
	{
		var content = new SendOptionsMenu().BuildForTests(
		[
			new SendOptionsMenu.Item("arrow-up", Strings.SteerCurrentTurn, Strings.SteerCurrentTurnHint, static () => { }),
			new SendOptionsMenu.Item("list-ordered", Strings.QueueNextTurn, Strings.QueueNextTurnHint, static () => { }),
			new SendOptionsMenu.Item("x", Strings.Abort, Strings.AbortHint, static () => { }),
		]);

		// 图标是图片元素；菜单项之间的区分不能只靠文字缩进。
		var icons = Find<Image>(content).ToArray();
		Assert.Equal(3, icons.Length);
		Assert.All(icons, icon => Assert.NotNull(icon.Source));
	}

	[Fact]
	public async Task SendCaretOpensNothingWithoutABackendInsteadOfThrowing()
	{
		var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var manager = SessionManager.InMemory(Environment.CurrentDirectory);
		var model = FakeStreams.Model();
		using var session = new CodingAgentSession(new CodingAgentSessionOptions
		{
			Cwd = manager.Cwd, Session = manager, Model = model, Tools = [], Skills = [], PromptTemplates = [], Persist = false,
			StreamFn = (streamModel, _, _) => FakeStreams.Text(streamModel, "final", gate.Task),
		});
		using var tab = new SessionTab(session, [model], "send-menu");
		tab.SetExtensionText("first");
		tab.SendFromShortcut();
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
		while (!tab.ChatStreaming) await Task.Delay(20, timeout.Token);

		// 无头环境没有原生弹窗后端：入口必须静默降级，不能抛异常打断会话。
		tab.ShowSendMenuForTests();
		Assert.False(tab.SendMenuForTests.IsOpen);
		Assert.Null(tab.SendMenuForTests.ContentForTests);

		gate.SetResult();
		await tab.PendingSend.WaitAsync(TimeSpan.FromSeconds(5));
	}
}
