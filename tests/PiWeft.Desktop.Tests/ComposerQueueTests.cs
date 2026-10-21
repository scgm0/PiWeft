using Aprillz.MewUI.Controls;
using PiWeft.Ai;
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

	[Fact]
	public async Task AbortFromSendButtonStopsTheRunAndAllowsTheNextMessage()
	{
		var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var manager = SessionManager.InMemory(Environment.CurrentDirectory);
		var model = FakeStreams.Model();
		var calls = 0;
		using var session = new CodingAgentSession(new CodingAgentSessionOptions
		{
			Cwd = manager.Cwd, Session = manager, Model = model, Tools = [], Skills = [], PromptTemplates = [], Persist = false,
			StreamFn = (streamModel, _, _) => Interlocked.Increment(ref calls) == 1
				? FakeStreams.Text(streamModel, "first", gate.Task)
				: FakeStreams.Text(streamModel, "second"),
		});
		using var tab = new SessionTab(session, [model], "abort");

		tab.SetExtensionText("first");
		tab.SendFromShortcut();
		await WaitForAsync(() => tab.ChatStreaming && Volatile.Read(ref calls) > 0);

		// 运行中同一个发送键变成中断；按一次必须把界面收回可发送状态。
		tab.SendFromShortcut();
		await WaitForAsync(() => !tab.IsStreaming);
		gate.SetResult();

		tab.SetExtensionText("second");
		tab.SendFromShortcut();
		await tab.PendingSend.WaitAsync(TimeSpan.FromSeconds(5));

		Assert.False(tab.IsStreaming);
		Assert.Equal("second", tab.Session.Session.LastAssistantText());

		// 中断的那次也必须以 aborted 条目落盘，而不是只停在内存里的半截流。
		Assert.Contains(tab.Session.Session.Entries, entry =>
			entry is SessionMessageEntry { Message: AssistantMessage { StopReason: StopReason.Aborted } });
	}

	private static async Task WaitForAsync(Func<bool> condition)
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
		while (!condition()) await Task.Delay(20, timeout.Token);
	}
}
