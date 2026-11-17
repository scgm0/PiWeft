using Aprillz.MewUI.Controls;
using PiWeft.Ai;
using PiWeft.CodingAgent;
using PiWeft.CodingAgent.Session;
using PiWeft.CodingAgent.Settings;
using Xunit;
using static PiWeft.Desktop.Tests.VisualTree;

namespace PiWeft.Desktop.Tests;

[Collection("mewui-controls")]
public sealed class AutoRetryUiTests
{
	[Fact]
	public async Task BusyLineNamesTheRetryAttemptAndCountsDownToTheNextTry()
	{
		var manager = SessionManager.InMemory(Environment.CurrentDirectory);
		var model = FakeStreams.Model();
		using var session = new CodingAgentSession(new CodingAgentSessionOptions
		{
			Cwd = manager.Cwd, Session = manager, Model = model, Tools = [], Skills = [], PromptTemplates = [], Persist = false,
			// 退避时间给足：测试要在等待窗口里看到状态条，不让重试自然到点。
			Settings = new CodingAgentSettings { Retry = new RetryPolicy(BaseDelayMs: 5_000) },
			StreamFn = (streamModel, _, _) => FakeStreams.Error(streamModel, "Service Unavailable"),
		});
		using var tab = new SessionTab(session, [model], "retry");
		tab.SetExtensionText("hi");
		tab.SendFromShortcut();

		// 等退避状态出现在视图模型里，再手动渲染一帧（无窗口测试没有 DispatcherTimer）。
		for (var attempt = 0; attempt < 300 && tab.Chat.Retry is null; attempt++)
		{
			await Task.Delay(10);
		}

		Assert.NotNull(tab.Chat.Retry);
		tab.RenderForTests();
		ConversationPerformanceTests.Layout(tab.View);

		var busy = Find<TextBlock>(tab.View).Single(block => block.Text.Contains("正在自动重试", StringComparison.Ordinal));
		Assert.Contains("1/3", busy.Text, StringComparison.Ordinal);
		Assert.Contains("秒后重试", busy.Text, StringComparison.Ordinal);

		// 用户中止退避：状态条收口，不能把「中止」报成自动重试失败。
		tab.Chat.Abort();
		await tab.PendingSend.WaitAsync(TimeSpan.FromSeconds(5));
		tab.RenderForTests();

		Assert.Null(tab.Chat.Retry);
		Assert.Null(tab.Chat.TakeRetryNotice());
		Assert.Null(tab.Chat.Error);
	}

	[Fact]
	public async Task BackgroundTurnKeepsThePrimaryButtonOnAbortDuringBackoff()
	{
		var manager = SessionManager.InMemory(Environment.CurrentDirectory);
		var model = FakeStreams.Model();
		using var session = new CodingAgentSession(new CodingAgentSessionOptions
		{
			Cwd = manager.Cwd, Session = manager, Model = model, Tools = [], Skills = [], PromptTemplates = [], Persist = false,
			Settings = new CodingAgentSettings { Retry = new RetryPolicy(BaseDelayMs: 5_000) },
			StreamFn = (streamModel, _, _) => FakeStreams.Error(streamModel, "Service Unavailable"),
		});
		using var tab = new SessionTab(session, [model], "background-retry");

		// 后台注入的一轮（关联会话回报同路径）不经过标签的 _sending。
		var run = session.PromptAsync("hi");
		for (var attempt = 0; attempt < 300 && tab.Chat.Retry is null; attempt++)
		{
			await Task.Delay(10);
		}

		Assert.NotNull(tab.Chat.Retry);
		tab.RenderForTests();

		// 退避也是这一轮的一部分：主按钮仍在中断，状态条报重试倒计时，不假装空闲。
		Assert.Equal(Strings.Abort, tab.PrimaryLabelForTests);
		Assert.Equal(RespondingKind.Retrying, tab.BusyStatus?.Kind);

		// 点主按钮就中止退避，不能落到 Send（） 上再撞一次「已在运行」。
		tab.PrimaryActionForTests();
		await run.WaitAsync(TimeSpan.FromSeconds(5));
		tab.RenderForTests();
		Assert.Equal(Strings.Send, tab.PrimaryLabelForTests);
	}
}
