using System.Text.Json.Nodes;
using PiWeft.Ai;
using PiWeft.CodingAgent;
using PiWeft.CodingAgent.Session;
using Xunit;

namespace PiWeft.CodingAgent.Tests;

/// 编辑重发不再为每次重发派生一份新会话文件（否则侧栏每改一次就多一条同名会话）：
/// 在同一份文件里把被放弃的分支改成墓碑。这里锁住三件事：会话 id 与文件不变、
/// 旧分支从投影与树里消失、之后追加的消息接在截断点上。
public class SessionTruncateForResendTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-truncate-" + Guid.NewGuid().ToString("N")[..8]);

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	private string SessionDir => Path.Combine(_root, "sessions");

	private static UserMessage User(string text, long timestamp) =>
		new() { Content = MessageContent.FromText(text), Timestamp = timestamp };

	private static AssistantMessage Assistant(string text, long timestamp) => new()
	{
		Api = "openai-completions",
		Provider = "fixture",
		Model = "fixture-model",
		Content = [new TextContent { Text = text }],
		StopReason = StopReason.Stop,
		Timestamp = timestamp,
	};

	private static string Describe(Message message) => message switch
	{
		UserMessage user => "u:" + (user.Content.Text ?? string.Empty),
		AssistantMessage assistant => "a:" + string.Concat(assistant.Content.OfType<TextContent>().Select(static block => block.Text)),
		_ => message.GetType().Name,
	};

	private static int CountNodes(IReadOnlyList<SessionTreeNode> roots) =>
		roots.Sum(static node => 1 + CountNodes(node.Children));

	private static (SessionManager Manager, string FirstUser, string FirstAnswer, string Second) BuildSession(string sessionDir)
	{
		var manager = SessionManager.Create("/tmp/p", sessionDir);
		var first = manager.AppendMessage(User("first", 1));
		var answer = manager.AppendMessage(Assistant("first answer", 2));
		var second = manager.AppendMessage(User("second", 3));
		manager.AppendMessage(Assistant("second answer", 4));
		return (manager, first, answer, second);
	}

	[Fact]
	public void TruncateForResendKeepsTheSessionFileAndRemovesTheAbandonedBranch()
	{
		var (manager, first, firstAnswer, second) = BuildSession(SessionDir);
		var file = manager.SessionFile!;
		var sessionId = manager.SessionId;

		var truncated = manager.TruncateForResend(second);

		Assert.Equal(2, truncated);
		// 会话身份不变：侧栏不会因为一次编辑多出一条会话。
		Assert.Equal(file, manager.SessionFile);
		Assert.Equal(sessionId, manager.SessionId);
		Assert.Equal([file], Directory.GetFiles(SessionDir, "*" + SessionFormat.Extension));

		Assert.Equal(["u:first", "a:first answer"], manager.BuildProjectedMessages().Select(Describe));
		// 叶子回到被截断提问的父级（上一轮回答），后续消息接在那里。
		Assert.Equal(firstAnswer, manager.LeafId);

		// 被放弃的条目在文件里改成墓碑：保留 id/parentId，原文不再作为消息存在。
		var tombstones = manager.Entries.OfType<DeletedEntry>().ToArray();
		Assert.Equal(2, tombstones.Length);
		Assert.All(tombstones, tombstone =>
		{
			Assert.Equal(tombstone.Id, tombstone.OriginalEntryId);
			Assert.Equal("resend-truncate", tombstone.Reason);
		});
		Assert.DoesNotContain(manager.Entries,
			entry => entry is SessionMessageEntry { Message: UserMessage user } && user.Content.Text == "second");

		// 树里也看不到死分支；重新打开文件后结论一致（墓碑不抢叶子）。
		var root = Assert.Single(manager.BuildTree());
		Assert.Equal(first, root.Entry.Id);
		Assert.Equal(2, CountNodes(manager.BuildTree()));

		var reopened = SessionManager.Open(file, SessionDir);
		Assert.Equal(firstAnswer, reopened.LeafId);
		Assert.Equal(["u:first", "a:first answer"], reopened.BuildProjectedMessages().Select(Describe));
		Assert.Equal(2, CountNodes(reopened.BuildTree()));
	}

	[Fact]
	public void AppendingAfterTruncateContinuesFromTheTruncatedEntry()
	{
		var (manager, first, firstAnswer, second) = BuildSession(SessionDir);

		manager.TruncateForResend(second);
		var edited = manager.AppendMessage(User("second (edited)", 5));

		Assert.Equal(firstAnswer, manager.GetEntry(edited)!.ParentId);
		Assert.Equal(
			["u:first", "a:first answer", "u:second (edited)"],
			manager.BuildProjectedMessages().Select(Describe));

		var reopened = SessionManager.Open(manager.SessionFile!, SessionDir);
		Assert.Equal(edited, reopened.LeafId);
		// 活分支只有三条：旧回答与旧提问都是墓碑，不再是叶子或候选分支。
		Assert.Equal(3, CountNodes(reopened.BuildTree()));
	}

	[Fact]
	public void TruncatingTheFirstMessageStartsTheEditedOneAsANewRoot()
	{
		var manager = SessionManager.Create("/tmp/p", SessionDir);
		var first = manager.AppendMessage(User("only", 1));
		manager.AppendMessage(Assistant("answer", 2));

		manager.TruncateForResend(first);

		Assert.Null(manager.LeafId);
		var edited = manager.AppendMessage(User("only (edited)", 3));
		Assert.Null(manager.GetEntry(edited)!.ParentId);
		Assert.Equal(["u:only (edited)"], manager.BuildProjectedMessages().Select(Describe));
	}

	[Fact]
	public void TruncatingAnEntryOutsideTheActiveBranchIsRejected()
	{
		var (manager, _, _, second) = BuildSession(SessionDir);
		// 把叶子移回第一条回答：第二条用户消息此时在死分支上，不能再被截断。
		manager.Branch(manager.Entries.OfType<SessionMessageEntry>().First().Id);

		Assert.Throws<InvalidOperationException>(() => manager.TruncateForResend(second));
		Assert.Throws<InvalidOperationException>(() => manager.TruncateForResend("missing"));
	}

	[Fact]
	public async Task RewindForResendSendsOnlyTheEditedTurnInTheSameSession()
	{
		var manager = SessionManager.Create(_root, SessionDir);
		var script = new ScriptedStreamFn();
		using var session = new CodingAgentSession(new CodingAgentSessionOptions
		{
			Cwd = _root,
			Model = ScriptedSessions.Model(),
			StreamFn = script.Invoke,
			Session = manager,
			Skills = [],
		});

		script.EnqueueText("first answer");
		await session.PromptAsync("first");
		script.EnqueueText("second answer");
		await session.PromptAsync("second");

		var file = manager.SessionFile!;
		var second = manager.Entries.OfType<SessionMessageEntry>().Last(static entry => entry.Message is UserMessage).Id;

		session.RewindForResend(second);

		Assert.Equal(file, manager.SessionFile);
		Assert.Equal([file], Directory.GetFiles(SessionDir, "*" + SessionFormat.Extension));
		Assert.DoesNotContain(session.Messages, message => message is UserMessage user && user.Content.Text == "second");

		script.EnqueueText("second answer (edited)");
		await session.PromptAsync("second (edited)");

		// 模型只看到「第一次问答 + 修改后的提问」：旧提问与旧回答都不在请求里。
		var request = script.Requests[^1];
		Assert.DoesNotContain(request, message => message is UserMessage user && user.Content.Text == "second");
		Assert.Contains(request, message => message is UserMessage user && user.Content.Text == "second (edited)");
		Assert.DoesNotContain(request, message => message is AssistantMessage assistant &&
			assistant.Content.OfType<TextContent>().Any(static block => block.Text == "second answer"));

		// 仍然是同一个会话文件、同一个会话 id。
		Assert.Equal(file, manager.SessionFile);
		Assert.Equal([file], Directory.GetFiles(SessionDir, "*" + SessionFormat.Extension));
	}

	[Fact]
	public void TombstoneRoundTripsThroughTheSessionFormat()
	{
		var original = new DeletedEntry
		{
			Id = "deadbeef",
			ParentId = "cafebabe",
			Timestamp = "2026-01-01T00:00:00.000Z",
			OriginalEntryId = "deadbeef",
			Reason = "resend-truncate",
		};

		var line = SessionJson.Serialize(original);
		var parsed = Assert.IsType<DeletedEntry>(SessionLines.Entry(SessionJson.ParseLine(line)));

		Assert.Equal(original.OriginalEntryId, parsed.OriginalEntryId);
		Assert.Equal(original.Reason, parsed.Reason);
		Assert.Equal(original.ParentId, parsed.ParentId);
		Assert.Equal(original.Timestamp, parsed.Timestamp);
		Assert.Null(parsed.Extra);
		Assert.True(JsonNode.DeepEquals(JsonNode.Parse(line), JsonNode.Parse(SessionJson.Serialize(parsed))));
	}
}
