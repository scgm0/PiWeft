using PiWeft.Ai;
using PiWeft.CodingAgent.Session;
using PiWeft.CodingAgent;
using Xunit;

namespace PiWeft.CodingAgent.Tests;

/// <summary>Session forking, which backs the CLI <c>--fork</c> flag.</summary>
public class SessionForkTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-fork-" + Guid.NewGuid().ToString("N")[..8]);

	public SessionForkTests() => Directory.CreateDirectory(_root);

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	private string SessionsDir => Path.Combine(_root, "sessions");

	[Fact]
	public void ForkFromCopiesTheTranscriptAndRecordsTheParent()
	{
		var source = SessionManager.Create(_root, SessionsDir);
		source.AppendMessage(new UserMessage { Content = MessageContent.FromText("one"), Timestamp = 1 });
		source.AppendMessage(new AssistantMessage
		{
			Api = "test",
			Provider = "test",
			Model = "m",
			Content = [new TextContent { Text = "two" }],
			StopReason = StopReason.Stop,
			Timestamp = 2,
		});

		var forked = SessionManager.ForkFrom(source.SessionFile!, _root, SessionsDir);

		Assert.Equal(source.SessionFile, forked.Header!.ParentSession);
		Assert.NotEqual(source.SessionId, forked.SessionId);
		Assert.Equal(2, forked.Entries.Count);
		Assert.Equal(2, forked.BuildProjectedMessages().Count);
		Assert.Equal(forked.Entries[^1].Id, forked.LeafId);
		Assert.NotEqual(source.SessionFile, forked.SessionFile);

		// Appending continues from the copied leaf, so the fork is a real continuation.
		forked.AppendMessage(new UserMessage { Content = MessageContent.FromText("three"), Timestamp = 3 });
		Assert.Equal(forked.Entries[1].Id, forked.Entries[2].ParentId);

		// The source is untouched.
		Assert.Equal(2, SessionManager.Open(source.SessionFile!).Entries.Count);
	}

	[Fact]
	public void ForkFromWithoutAHeaderFails()
	{
		var path = Path.Combine(_root, "empty.jsonl");
		File.WriteAllText(path, string.Empty);

		Assert.Throws<InvalidOperationException>(() => SessionManager.ForkFrom(path, _root));
	}

	[Fact]
	public void NavigateToMovesTheLeafWithoutWritingASummary()
	{
		// 界面上的「跳到此处」：只移动叶子，不生成分支摘要、也不改写历史。
		var manager = SessionManager.Create(_root, SessionsDir);
		var firstUserId = manager.AppendMessage(new UserMessage { Content = MessageContent.FromText("first"), Timestamp = 1 });
		var firstAssistantId = manager.AppendMessage(new AssistantMessage
		{
			Api = "test",
			Provider = "test",
			Model = "m",
			Content = [new TextContent { Text = "answer one" }],
			StopReason = StopReason.Stop,
			Timestamp = 2,
		});
		manager.AppendMessage(new UserMessage { Content = MessageContent.FromText("second"), Timestamp = 3 });
		manager.AppendMessage(new AssistantMessage
		{
			Api = "test",
			Provider = "test",
			Model = "m",
			Content = [new TextContent { Text = "answer two" }],
			StopReason = StopReason.Stop,
			Timestamp = 4,
		});

		using var session = new CodingAgentSession(new CodingAgentSessionOptions
		{
			Cwd = _root,
			Model = ScriptedSessions.Model(),
			StreamFn = new ScriptedStreamFn().Invoke,
			Session = manager,
			Persist = false,
			Skills = [],
		});

		// 投影里含开头的 system 消息，这里只数真实对话消息。
		Assert.Equal(4, session.Messages.Count(static m => m is not SystemMessage));

		session.NavigateTo(firstAssistantId);

		Assert.Equal(firstAssistantId, manager.LeafId);
		Assert.Equal(2, session.Messages.Count(static m => m is not SystemMessage));
		Assert.Contains(session.Messages, static m => m is AssistantMessage assistant &&
			assistant.Content.OfType<TextContent>().Any(static block => block.Text == "answer one"));
		Assert.DoesNotContain(session.Messages, static m => m is UserMessage user && user.Content.Text == "second");

		// 没有写 branch_summary：历史条目数不变（4 条对话消息；打开时不写多余条目），只有指针移动。
		Assert.DoesNotContain(manager.Entries, static entry => entry is BranchSummaryEntry);
		Assert.Equal(4, manager.Entries.Count);

		// 跳回更早的条目后，追加会接在该处形成新分支。
		var branchUserId = manager.AppendMessage(new UserMessage { Content = MessageContent.FromText("retry"), Timestamp = 5 });
		Assert.Equal(firstAssistantId, manager.GetEntry(branchUserId)!.ParentId);
		Assert.Equal(5, manager.Entries.Count);
		Assert.NotNull(firstUserId);
	}

	[Fact]
	public void ForkedSessionIsIndependentlyResumable()
	{
		var source = SessionManager.Create(_root, SessionsDir);
		source.AppendMessage(new UserMessage { Content = MessageContent.FromText("shared"), Timestamp = 1 });

		var forked = SessionManager.ForkFrom(source.SessionFile!, _root, SessionsDir);
		forked.AppendMessage(new UserMessage { Content = MessageContent.FromText("only in fork"), Timestamp = 2 });

		var reopened = SessionManager.Open(forked.SessionFile!);
		var messages = reopened.BuildProjectedMessages();

		Assert.Equal(2, messages.Count);
		Assert.Equal("only in fork", Assert.IsType<UserMessage>(messages[1]).Content.Text);
	}
}
