using Aprillz.MewUI.Controls;
using PiWeft.Ai;
using PiWeft.CodingAgent;
using PiWeft.CodingAgent.Session;
using Xunit;
using static PiWeft.Desktop.Tests.VisualTree;

namespace PiWeft.Desktop.Tests;

/// <summary>
/// 运行中再发的消息不再只弹一次 toast：输入框上方的「排队中」卡要列出每一条，
/// 并标明它会进本轮（steer）还是下一轮（follow-up），撤回能连内容一起回到输入框。
/// </summary>
[Collection("mewui-controls")]
public sealed class ComposerQueueStripTests
{
	[Fact]
	public async Task QueuedDraftsShowUpInTheStripAndCanBeRetractedBackToTheComposer()
	{
		var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		// 第一次模型请求已经开始（流被 gate 卡在中间），此时排入的 steering 才不会被
		// 「请求前抽取」提前送达 —— 那是另一条既有语义，不是排队条的显示范围。
		var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var manager = SessionManager.InMemory(Environment.CurrentDirectory);
		var model = FakeStreams.Model();
		using var session = new CodingAgentSession(new CodingAgentSessionOptions
		{
			Cwd = manager.Cwd, Session = manager, Model = model, Tools = [], Skills = [], PromptTemplates = [], Persist = false,
			StreamFn = (streamModel, _, _) =>
			{
				started.TrySetResult();
				return FakeStreams.Text(streamModel, "final", gate.Task);
			},
		});
		using var tab = new SessionTab(session, [model], "queue-strip");
		tab.SetExtensionText("first");
		tab.SendFromShortcut();
		await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
		await WaitForAsync(() => tab.ChatStreaming);
		await Task.Delay(50);

		Assert.False(tab.QueueCardVisible);

		// 两条：一条进本轮，一条排下一轮。队列快照按 steering → follow-up 排序。
		tab.SetExtensionText("调整一下方向");
		tab.QueueDraft(steer: true);
		tab.SetExtensionText("下一轮再看日志");
		tab.QueueDraft(steer: false);
		tab.RenderForTests();
		ConversationPerformanceTests.Layout(tab.View);

		var chatQueue = tab.Chat.QueuedMessages();
		Assert.Equal(2, chatQueue.Count);
		Assert.True(chatQueue[0].Steer);
		Assert.False(chatQueue[1].Steer);
		Assert.Equal("调整一下方向", chatQueue[0].Text);
		Assert.True(tab.QueueCardVisible);

		// 卡片里能读到两条正文与各自的投递时机。
		var texts = Find<TextBlock>(tab.View).Select(block => block.Text).ToArray();
		Assert.Contains(texts, text => text.Contains("排队中", StringComparison.Ordinal));
		Assert.Contains(texts, text => text.Contains("调整一下方向", StringComparison.Ordinal));
		Assert.Contains(texts, text => text.Contains("下一轮再看日志", StringComparison.Ordinal));
		Assert.Contains(texts, text => text.Contains(Strings.QueuedSteer, StringComparison.Ordinal));
		Assert.Contains(texts, text => text.Contains(Strings.QueuedFollowUp, StringComparison.Ordinal));

		// 撤回第一条：内容回到输入框，队列里只剩一条。
		tab.RetractQueued(chatQueue[0]);
		Assert.Equal("调整一下方向", tab.ComposerText);
		Assert.Single(session.PendingQueues().FollowUp);
		Assert.Empty(session.PendingQueues().Steering);

		tab.RenderForTests();
		Assert.True(tab.QueueCardVisible);

		// 收回最后一条后整卡消失，不再占输入区上方的高度。
		var last = Assert.Single(tab.Chat.QueuedMessages());
		tab.DiscardQueued(last);
		tab.RenderForTests();
		Assert.False(tab.QueueCardVisible);

		gate.SetResult();
		await tab.PendingSend.WaitAsync(TimeSpan.FromSeconds(5));
	}

	[Fact]
	public async Task RetractingKeepsTheDraftTheUserWasAlreadyWriting()
	{
		var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var manager = SessionManager.InMemory(Environment.CurrentDirectory);
		var model = FakeStreams.Model();
		using var session = new CodingAgentSession(new CodingAgentSessionOptions
		{
			Cwd = manager.Cwd, Session = manager, Model = model, Tools = [], Skills = [], PromptTemplates = [], Persist = false,
			StreamFn = (streamModel, _, _) => FakeStreams.Text(streamModel, "final", gate.Task),
		});
		using var tab = new SessionTab(session, [model], "queue-draft");
		tab.SetExtensionText("first");
		tab.SendFromShortcut();
		await WaitForAsync(() => tab.ChatStreaming);

		tab.SetExtensionText("被排队的消息");
		tab.QueueDraft(steer: false);
		tab.SetExtensionText("我正在写的新草稿");

		// 撤回的文本排在已有草稿前面（PiDeck 同语义），不能把用户刚写的那半句吞掉。
		tab.RetractQueued(Assert.Single(tab.Chat.QueuedMessages()));
		Assert.StartsWith("被排队的消息", tab.ComposerText, StringComparison.Ordinal);
		Assert.EndsWith("我正在写的新草稿", tab.ComposerText, StringComparison.Ordinal);
		Assert.Contains("\n\n", tab.ComposerText, StringComparison.Ordinal);

		gate.SetResult();
		await tab.PendingSend.WaitAsync(TimeSpan.FromSeconds(5));
	}

	[Fact]
	public async Task RetractingAMessageThatAlreadyReachedTheModelIsRefused()
	{
		var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var manager = SessionManager.InMemory(Environment.CurrentDirectory);
		var model = FakeStreams.Model();
		using var session = new CodingAgentSession(new CodingAgentSessionOptions
		{
			Cwd = manager.Cwd, Session = manager, Model = model, Tools = [], Skills = [], PromptTemplates = [], Persist = false,
			StreamFn = (streamModel, _, _) => FakeStreams.Text(streamModel, "final", gate.Task),
		});
		using var tab = new SessionTab(session, [model], "queue-retract");
		tab.SetExtensionText("first");
		tab.SendFromShortcut();
		await WaitForAsync(() => tab.ChatStreaming);

		tab.SetExtensionText("这条会先被投递");
		tab.QueueDraft(steer: true);
		var queued = Assert.Single(tab.Chat.QueuedMessages());

		// 模型已经把这条抽走了（队列里不再有它）：撤回必须拒绝，而不是假装成功。
		session.ClearQueue();
		Assert.False(tab.Chat.RetractQueued(queued));

		gate.SetResult();
		await tab.PendingSend.WaitAsync(TimeSpan.FromSeconds(5));
	}

	private static async Task WaitForAsync(Func<bool> condition)
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
		while (!condition()) await Task.Delay(20, timeout.Token);
	}
}
