using Aprillz.MewUI.Controls;
using PiWeft.CodingAgent;
using PiWeft.CodingAgent.Session;
using Xunit;
using static PiWeft.Desktop.Tests.VisualTree;

namespace PiWeft.Desktop.Tests;

/// <summary>运行中发送键右侧的 Split Send 菜单把草稿排入 steering，而不是中断当前回复。</summary>
[Collection("mewui-controls")]
public sealed class ComposerQueueTests
{
	[Fact]
	public async Task RunningComposerExposesSendMenuAndQueuesTheDraft()
	{
		var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var manager = SessionManager.InMemory(Environment.CurrentDirectory);
		var model = FakeStreams.Model();
		using var session = new CodingAgentSession(new CodingAgentSessionOptions
		{
			Cwd = manager.Cwd, Session = manager, Model = model, Tools = [], Skills = [], PromptTemplates = [], Persist = false,
			StreamFn = (streamModel, _, _) => FakeStreams.Text(streamModel, "final", gate.Task),
		});
		using var tab = new SessionTab(session, [model], "queue");
		tab.SetExtensionText("first");
		tab.SendFromShortcut();
		ConversationPerformanceTests.Layout(tab.View);
		var sendMenu = Find<Button>(tab.View).Single(button => HoverHints.Text(button) == Strings.SendOptions);
		Assert.True(sendMenu.IsVisible);
		await WaitForAsync(() => tab.ChatStreaming);

		tab.SetExtensionText("调整一下方向");
		tab.QueueDraft(steer: true);
		var (steering, followUp) = session.PendingQueues();
		Assert.Single(steering);
		Assert.Empty(followUp);

		gate.SetResult();
		await tab.PendingSend.WaitAsync(TimeSpan.FromSeconds(5));
	}

	private static async Task WaitForAsync(Func<bool> condition)
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
		while (!condition()) await Task.Delay(20, timeout.Token);
	}
}
