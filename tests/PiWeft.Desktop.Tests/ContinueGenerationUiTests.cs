using Aprillz.MewUI.Controls;
using PiWeft.Ai;
using PiWeft.CodingAgent;
using PiWeft.CodingAgent.Session;
using PiWeft.CodingAgent.Settings;
using Xunit;
using static PiWeft.Desktop.Tests.VisualTree;

namespace PiWeft.Desktop.Tests;

/// 继续生成接管主按钮：失败一轮后发送键变成「继续生成」，草稿非空时回到「发送」。
[Collection("mewui-controls")]
public sealed class ContinueGenerationUiTests
{
	[Fact]
	public async Task FailedTurnTurnsThePrimaryButtonIntoContinue()
	{
		var calls = 0;
		var manager = SessionManager.InMemory(Environment.CurrentDirectory);
		var model = FakeStreams.Model();
		using var session = new CodingAgentSession(new CodingAgentSessionOptions
		{
			Cwd = manager.Cwd,
			Session = manager,
			Model = model,
			Tools = [],
			Skills = [],
			PromptTemplates = [],
			Persist = false,
			Settings = new CodingAgentSettings { Retry = new RetryPolicy(Enabled: false) },
			StreamFn = (streamModel, _, _) => Interlocked.Increment(ref calls) == 1
				? FakeStreams.Error(streamModel, "Service Unavailable")
				: FakeStreams.Text(streamModel, "continued answer"),
		});
		using var tab = new SessionTab(session, [model], "continue");
		tab.SetExtensionText("hi");
		tab.SendFromShortcut();
		await tab.PendingSend.WaitAsync(TimeSpan.FromSeconds(5));
		tab.RenderForTests();

		Assert.Equal(Strings.Continue, tab.PrimaryLabelForTests);

		// 有草稿时主按钮仍然是发送：继续生成不能抢走用户正在写的消息。
		tab.SetExtensionText("another question");
		Assert.Equal(Strings.Send, tab.PrimaryLabelForTests);
		tab.SetExtensionText(string.Empty);
		Assert.Equal(Strings.Continue, tab.PrimaryLabelForTests);

		tab.PrimaryActionForTests();
		await tab.PendingSend.WaitAsync(TimeSpan.FromSeconds(5));
		tab.RenderForTests();

		var rows = tab.Chat.Entries();
		Assert.Single(rows, static row => row.Role == ChatRole.User);
		Assert.Single(rows, static row => row.Role == ChatRole.Notice);
		Assert.Contains(rows, static row => row.Role == ChatRole.Assistant && row.Text == "continued answer");
		Assert.Equal(Strings.Send, tab.PrimaryLabelForTests);

		// 收据行真的画出来了：一行图标 + 文案，不带用户气泡。
		ConversationPerformanceTests.Layout(tab);
		Assert.Contains(Find<TextBlock>(tab.View), static block => block.Text == Strings.ContinueRequested);
	}
}
