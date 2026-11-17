using Aprillz.MewUI.Controls;
using PiWeft.Agent;
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
	public async Task RetriedAttemptStopsReportingTheRetryOnceItsOutputArrives()
	{
		var manager = SessionManager.InMemory(Environment.CurrentDirectory);
		var model = FakeStreams.Model();
		var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var calls = 0;
		using var session = new CodingAgentSession(new CodingAgentSessionOptions
		{
			Cwd = manager.Cwd, Session = manager, Model = model, Tools = [], Skills = [], PromptTemplates = [], Persist = false,
			// 退避很短：状态条要看到的阶段是重试的这次尝试已经在出字，卡在流里为了断言。
			Settings = new CodingAgentSettings { Retry = new RetryPolicy(BaseDelayMs: 20) },
			StreamFn = (streamModel, _, _) => Interlocked.Increment(ref calls) == 1
				? FakeStreams.Error(streamModel, "Service Unavailable")
				: FakeStreams.Text(streamModel, "重试成功", gate.Task),
		});
		var outcomes = new List<bool>();
		using var subscription = session.Subscribe(@event =>
		{
			if (@event is AutoRetryEndEvent end)
			{
				outcomes.Add(end.Success);
			}

			return ValueTask.CompletedTask;
		});
		using var tab = new SessionTab(session, [model], "retry-live");
		tab.SetExtensionText("hi");
		tab.SendFromShortcut();

		// 等到重试的那次尝试已经在推流：第一次尝试失败后重试接手，正文正在长。
		var streaming = false;
		for (var attempt = 0; attempt < 500 && !streaming; attempt++)
		{
			await Task.Delay(10);
			streaming = tab.Chat.Entries().Any(entry => entry.Role == ChatRole.Assistant && entry.IsStreaming);
		}

		Assert.True(streaming);
		tab.RenderForTests();
		ConversationPerformanceTests.Layout(tab.View);

		// 屏幕上已经在出正文：重试这一行就该让位，不能一直挂到整轮结束。
		Assert.Null(tab.Chat.Retry);
		Assert.Equal(RespondingKind.Responding, tab.BusyStatus?.Kind);

		gate.SetResult();
		await tab.PendingSend.WaitAsync(TimeSpan.FromSeconds(5));

		// 重试确实以成功收口，状态条也跟着整条收起。
		Assert.True(Assert.Single(outcomes));
		tab.RenderForTests();
		Assert.Null(tab.BusyStatus);
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
