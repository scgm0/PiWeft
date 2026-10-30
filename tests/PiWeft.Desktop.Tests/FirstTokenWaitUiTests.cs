using Aprillz.MewUI.Controls;
using PiWeft.Ai;
using PiWeft.CodingAgent;
using PiWeft.CodingAgent.Session;
using PiWeft.Desktop;
using Xunit;
using static PiWeft.Desktop.Tests.VisualTree;

namespace PiWeft.Desktop.Tests;

/// <summary>
/// 首轮等待的体验：发出第一条消息到首 token 落地之间，屏幕上没有正文、没有思考、也没有工具，
/// 这段以前什么提示都没有。现在状态条常驻、时间线里落一行占位，字一到两者各归其位。
/// </summary>
[Collection("mewui-controls")]
public sealed class FirstTokenWaitUiTests
{
	[Fact]
	public async Task WaitingForTheFirstTokenKeepsTheStatusBarAlive()
	{
		var firstToken = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
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
			StreamFn = (target, _, _) => GatedText(target, firstToken.Task, finish.Task, "收到，我在"),
		});
		using var tab = new SessionTab(session, [model], "wait");
		tab.SetExtensionText("第一条消息");
		tab.SendFromShortcut();

		// 还没收到任何 delta：判成预热，状态条那句话说的是后台真的在走的路。
		Assert.Equal(RespondingKind.Starting, tab.BusyStatus?.Kind);
		var waitingPhrase = tab.BusyStatus?.Phrase;
		Assert.False(string.IsNullOrWhiteSpace(waitingPhrase));

		// 占位行跟在真实内容后面：会话还空着的时候那是欢迎卡的地盘，不抢。
		for (var attempt = 0; attempt < 300 && tab.Chat.Entries().Count == 0; attempt++)
		{
			await Task.Delay(10);
		}

		Assert.NotEmpty(tab.Chat.Entries());
		tab.RenderForTests();
		ConversationPerformanceTests.Layout(tab.View);

		var line = Find<TextBlock>(tab.View).Where(text => text.Text == waitingPhrase).ToArray();
		Assert.NotEmpty(line);
		// 时间线里那一行占位：首 token 一到就被真的流式行顶掉，所以此时还没有助手正文。
		Assert.Single(Find<PendingReplyView>(tab.View));
		Assert.StartsWith(Strings.PendingReplyTitle, Find<PendingReplyView>(tab.View).Single().TitleText, StringComparison.Ordinal);

		firstToken.SetResult();
		for (var attempt = 0; attempt < 300 && !HasStreamingBody(tab); attempt++)
		{
			await Task.Delay(10);
		}

		Assert.True(HasStreamingBody(tab), "首 token 到达后应该有正在推流的正文行。");
		tab.RenderForTests();
		ConversationPerformanceTests.Layout(tab.View);

		Assert.Equal(RespondingKind.Responding, tab.BusyStatus?.Kind);
		Assert.Empty(Find<PendingReplyView>(tab.View));

		finish.SetResult();
		await tab.PendingSend.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.Null(tab.BusyStatus);
	}

	private static bool HasStreamingBody(SessionTab tab)
	{
		var rows = tab.Chat.Entries();
		for (var index = rows.Count - 1; index >= 0; index--)
		{
			if (rows[index] is { IsStreaming: true } row && row.TextLength > 0) return true;
		}

		return false;
	}

	/// <summary>
	/// 一段可分两次放行的假回复：先卡在首 token 之前（屏幕上什么都没有），
	/// 放行者推第一段，最后再收口。真实的等待长度不可控，测试里必须能自己按住。
	/// </summary>
	private static AssistantMessageEventStream GatedText(Model model, Task firstToken, Task finish, string text)
	{
		var stream = new AssistantMessageEventStream();
		_ = Task.Run(async () =>
		{
			var blank = new AssistantMessage
			{
				Api = model.Api,
				Provider = model.Provider,
				Model = model.Id,
				Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
			};
			var partial = blank with
			{
				Content = [new TextContent { Text = text }],
				StopReason = StopReason.Stop,
				Usage = new Usage { Input = 10, Output = 5, TotalTokens = 15, Cost = new UsageCost { Total = 0.5 } },
			};

			await firstToken.ConfigureAwait(false);
			stream.Push(new StartEvent { Partial = blank });
			stream.Push(new TextStartEvent { ContentIndex = 0, Partial = blank });
			stream.Push(new TextDeltaEvent { ContentIndex = 0, Delta = text, Partial = partial });

			await finish.ConfigureAwait(false);
			stream.Push(new TextEndEvent { ContentIndex = 0, Content = text, Partial = partial });
			stream.Push(new DoneEvent { Reason = StopReason.Stop, Partial = partial });
		});

		return stream;
	}
}
