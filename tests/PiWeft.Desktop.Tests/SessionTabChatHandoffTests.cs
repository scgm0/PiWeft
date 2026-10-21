using System.Diagnostics;
using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using PiWeft.Ai;
using PiWeft.CodingAgent;
using PiWeft.CodingAgent.Session;
using Xunit;
using Xunit.Abstractions;
using static PiWeft.Desktop.Tests.VisualTree;

namespace PiWeft.Desktop.Tests;

/// <summary>
/// 长会话的聊天记录可以在后台线程预投影，标签页直接接手；
/// 交接必须真正复用同一个模型，否则 UI 线程还得再投影一遍（本项要省掉 200+ 毫秒）。
/// </summary>
[Collection("mewui-controls")]
public sealed class SessionTabChatHandoffTests(ITestOutputHelper output)
{
	[Fact]
	public void PrebuiltChatIsReusedInsteadOfReprojectingOnTheUIThread()
	{
		using var session = ConversationPerformanceTests.CreateSession(40);
		var chat = new ChatViewModel();

		// 模拟宿主的后台预建：Attach 与首次投影都发生在其它线程上。
		Exception? failure = null;
		var worker = new Thread(() =>
		{
			try
			{
				chat.Attach(session);
				chat.Entries();
			}
			catch (Exception ex)
			{
				failure = ex;
			}
		});
		worker.Start();
		Assert.True(worker.Join(TimeSpan.FromSeconds(30)), "后台投影卡住了");
		Assert.Null(failure);

		var projected = chat.Entries();
		Assert.Equal(80, projected.Count);

		using var tab = new SessionTab(session, [session.Model], "history", availableModels: null, chat);
		var started = Stopwatch.StartNew();
		var rows = tab.Chat.Entries();
		started.Stop();

		Assert.True(ReferenceEquals(tab.Chat, chat), "标签页应接手预建的聊天记录模型");
		Assert.True(ReferenceEquals(projected, rows), "接手后不能重新投影，快照必须是同一个实例");
		Assert.True(ReferenceEquals(tab.Chat.Session, session), "聊天记录仍指向同一个会话");
		output.WriteLine($"Entries() after handoff: {started.Elapsed.TotalMilliseconds:0.###} ms");

		// 视图确实渲染出了后台投影的内容（打开时停在最新一条用户消息附近）。
		ConversationPerformanceTests.Layout(tab);
		Assert.Contains(Find<MultiLineTextBox>(tab.View),
			view => view.Text.StartsWith("question", StringComparison.Ordinal));
	}

	[Fact]
	public void TabWithoutAPrebuiltChatStillAttachesItself()
	{
		using var session = ConversationPerformanceTests.CreateSession(2);
		using var tab = new SessionTab(session, [session.Model], "history");
		ConversationPerformanceTests.Layout(tab);

		Assert.True(ReferenceEquals(tab.Chat.Session, session));
		Assert.Equal(4, tab.Chat.Entries().Count);
	}

	[Fact]
	public void OpeningADuplicateTabDoesNotReattachTheHandedChat()
	{
		using var session = ConversationPerformanceTests.CreateSession(5);
		var chat = new ChatViewModel();
		chat.Attach(session);
		var before = chat.Entries();
		using var tab = new SessionTab(session, [session.Model], "history", null, chat);

		// 会话在标签页存活期间继续写入：接手过的模型照常收到增量。
		session.Session.AppendMessage(new UserMessage { Content = MessageContent.FromText("later") });
		var after = tab.Chat.Entries();

		Assert.NotSame(before, after);
		Assert.Equal(before.Count + 1, after.Count);
	}
}
