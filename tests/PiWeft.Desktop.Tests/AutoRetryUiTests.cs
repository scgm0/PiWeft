using Aprillz.MewUI.Controls;
using PiWeft.Ai;
using PiWeft.CodingAgent;
using PiWeft.CodingAgent.Session;
using PiWeft.CodingAgent.Settings;
using Xunit;
using static PiWeft.Desktop.Tests.VisualTree;

namespace PiWeft.Desktop.Tests;

/// <summary>自动重试退避期：状态条必须说明「正在重试第几次、几秒后」，而不是看起来已经失败。</summary>
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
			// 退避时间给足：测试要在等待窗口里看到状态条，而不是让重试自然到点。
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
}
