using System.Text.Json.Nodes;
using PiWeft.Agent;
using PiWeft.Ai;
using PiWeft.CodingAgent;
using PiWeft.CodingAgent.Compatibility;
using PiWeft.CodingAgent.Session;
using PiWeft.CodingAgent.Settings;
using PiWeft.CodingAgent.Tools;
using Xunit;

namespace PiWeft.CodingAgent.Tests;

/// 关联会话：子会话回合结束回报父会话、自动续跑额度与互相发消息。
public sealed class LinkedSessionTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-linked-" + Guid.NewGuid().ToString("N")[..8]);

	public LinkedSessionTests() => Directory.CreateDirectory(_root);

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	private CodingAgentSession CreateSession(ScriptedStreamFn stream, ILinkedSessionHost? host = null) =>
		new(new CodingAgentSessionOptions
		{
			Cwd = _root,
			Model = ScriptedSessions.Model(),
			StreamFn = stream.Invoke,
			Session = SessionManager.InMemory(_root),
			Persist = false,
			Skills = [],
			PromptTemplates = [],
			Settings = new CodingAgentSettings(),
			LinkedSessionHost = host,
		});

	[Fact]
	public async Task StartSession_ReportsTheFinalReplyAndStartsAParentTurn()
	{
		var parentStream = new ScriptedStreamFn();
		parentStream.EnqueueText("父会话收到回报。");
		var childStream = new ScriptedStreamFn();
		childStream.EnqueueText("子会话把构建修好了。");
		var host = new FakeHost(_root);
		host.Streams.Enqueue(childStream);

		using var parent = CreateSession(parentStream, host);
		var hub = Assert.IsType<LinkedSessionHub>(parent.Linked);

		var started = await hub.StartAsync("修构建", "把构建修到全绿。");
		Assert.True(started.Ok, started.Text);

		var child = Assert.Single(host.Children);
		Assert.Equal("把构建修到全绿。", host.Contexts[0].Prompt);
		Assert.Contains("关联子会话", host.Contexts[0].SystemBrief);

		await WaitForReportsAsync(hub, 1);
		await hub.DeliveryBarrier.WaitAsync(TimeSpan.FromSeconds(5));

		var link = Assert.Single(hub.Children);
		Assert.Equal("修构建", link.Title);
		Assert.Equal(LinkedSessionLink.Idle, link.State);
		Assert.Equal(1, link.TurnCount);
		Assert.Contains("子会话把构建修好了。", link.LastReport);

		// 回报自动拉起父会话的一轮；正文带上「这是子会话自述」的限定。
		var request = Assert.Single(parentStream.Requests);
		var report = Assert.IsType<UserMessage>(request.Single(message => message is UserMessage
		{
			CustomType: PiWeftProtocol.LinkedReportMessageType,
		}));
		Assert.Contains("子会话「修构建」结束了一轮。", report.Content.Text);
		Assert.Contains("这是子会话的自述", report.Content.Text);
		Assert.True(report.Display);

		child.Dispose();
	}

	[Fact]
	public async Task AutoContinue_StopsAtTheQuotaAndResetsOnUserPrompt()
	{
		var parentStream = new ScriptedStreamFn();
		for (var index = 0; index < LinkedSessionHub.MaxAutoContinue + 1; index++)
		{
			parentStream.EnqueueText($"父会话第 {index + 1} 次继续。");
		}

		var childStream = new ScriptedStreamFn();
		childStream.EnqueueText("第一轮完成。");
		for (var index = 0; index < LinkedSessionHub.MaxAutoContinue + 1; index++)
		{
			childStream.EnqueueText($"追加一轮 {index + 1}。");
		}

		var host = new FakeHost(_root);
		host.Streams.Enqueue(childStream);

		using var parent = CreateSession(parentStream, host);
		var hub = Assert.IsType<LinkedSessionHub>(parent.Linked);
		Assert.True((await hub.StartAsync("跑长任务", "分多轮做完。")).Ok);
		var childId = Assert.Single(hub.Children).ChildSessionId;
		await WaitForReportsAsync(hub, 1);
		await hub.DeliveryBarrier.WaitAsync(TimeSpan.FromSeconds(5));

		// 连续回报：前 6 次各拉起父会话一轮，第 7 次只记在关系表里。
		for (var turn = 2; turn <= LinkedSessionHub.MaxAutoContinue + 1; turn++)
		{
			var sent = await hub.SendAsync(childId, $"继续第 {turn} 轮。");
			Assert.True(sent.Ok, sent.Text);
			await WaitForReportsAsync(hub, turn);
			await hub.DeliveryBarrier.WaitAsync(TimeSpan.FromSeconds(5));
		}

		Assert.Equal(LinkedSessionHub.MaxAutoContinue, parentStream.Requests.Count);
		Assert.Equal(LinkedSessionHub.MaxAutoContinue + 1, Assert.Single(hub.Children).TurnCount);

		// 用户一说话额度清零：下一份回报又能拉起父会话。
		parent.PrepareInput("我来接手一下");
		var next = await hub.SendAsync(childId, "再跑一轮。");
		Assert.True(next.Ok, next.Text);
		await WaitForReportsAsync(hub, LinkedSessionHub.MaxAutoContinue + 2);
		await hub.DeliveryBarrier.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.Equal(LinkedSessionHub.MaxAutoContinue + 1, parentStream.Requests.Count);

		foreach (var child in host.Children)
		{
			child.Dispose();
		}
	}

	[Fact]
	public async Task SendToSession_DeliversToTheChildWithTheParentLabel()
	{
		var parentStream = new ScriptedStreamFn();
		parentStream.EnqueueText("父会话收到回报。");
		var childStream = new ScriptedStreamFn();
		childStream.EnqueueText("第一轮完成。");
		childStream.EnqueueText("补充要求也做完了。");
		var host = new FakeHost(_root);
		host.Streams.Enqueue(childStream);

		using var parent = CreateSession(parentStream, host);
		var hub = Assert.IsType<LinkedSessionHub>(parent.Linked);
		Assert.True((await hub.StartAsync("修构建", "把构建修到全绿。")).Ok);
		var childId = Assert.Single(hub.Children).ChildSessionId;
		await WaitForReportsAsync(hub, 1);
		await hub.DeliveryBarrier.WaitAsync(TimeSpan.FromSeconds(5));

		Assert.True((await hub.SendAsync(childId, "顺便补一下文档。")).Ok);
		await WaitForReportsAsync(hub, 2);
		await hub.DeliveryBarrier.WaitAsync(TimeSpan.FromSeconds(5));

		var request = childStream.Requests[^1];
		var message = Assert.IsType<UserMessage>(request.Last(item => item is UserMessage));
		Assert.Contains("来自父会话", message.Content.Text);
		Assert.Contains("顺便补一下文档。", message.Content.Text);

		var listed = hub.List();
		var children = Assert.IsType<JsonArray>(listed["children"]);
		Assert.Equal(childId, children[0]!["session_id"]!.GetValue<string>());

		foreach (var child in host.Children)
		{
			child.Dispose();
		}
	}

	[Fact]
	public async Task ReportClipsLongRepliesAndCollapsesBlankLines()
	{
		var parentStream = new ScriptedStreamFn();
		parentStream.EnqueueText("父会话收到回报。");
		var childStream = new ScriptedStreamFn();
		childStream.EnqueueText("第一段。\r\n\r\n\r\n第二段。" + new string('长', 600));
		var host = new FakeHost(_root);
		host.Streams.Enqueue(childStream);

		using var parent = CreateSession(parentStream, host);
		var hub = Assert.IsType<LinkedSessionHub>(parent.Linked);
		Assert.True((await hub.StartAsync("长回复", "写一份长报告。")).Ok);
		await WaitForReportsAsync(hub, 1);
		await hub.DeliveryBarrier.WaitAsync(TimeSpan.FromSeconds(5));

		var report = Assert.Single(hub.Children).LastReport;
		Assert.NotNull(report);
		Assert.StartsWith("第一段。\n\n第二段。", report);
		Assert.Equal(LinkedSessionHub.ReportMaxChars + 1, report.Length);
		Assert.EndsWith("…", report);

		foreach (var child in host.Children)
		{
			child.Dispose();
		}
	}

	[Fact]
	public async Task ChildCanSendToItsParent()
	{
		var parentStream = new ScriptedStreamFn();
		parentStream.EnqueueText("父会话收到回报。");
		parentStream.EnqueueText("父会话收到子会话的消息。");
		var childStream = new ScriptedStreamFn();
		childStream.EnqueueText("第一轮完成。");
		var host = new FakeHost(_root);
		host.Streams.Enqueue(childStream);

		using var parent = CreateSession(parentStream, host);
		var hub = Assert.IsType<LinkedSessionHub>(parent.Linked);
		Assert.True((await hub.StartAsync("修构建", "把构建修到全绿。")).Ok);
		await WaitForReportsAsync(hub, 1);
		await hub.DeliveryBarrier.WaitAsync(TimeSpan.FromSeconds(5));

		var child = Assert.Single(host.Children);
		var childHub = Assert.IsType<LinkedSessionHub>(child.Linked);
		var parentEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		using var subscription = parent.Subscribe(@event =>
		{
			if (@event is AgentEndEvent { WillRetry: false })
			{
				parentEnded.TrySetResult();
			}

			return ValueTask.CompletedTask;
		});

		var sent = await childHub.SendAsync(parent.Session.SessionId, "这一步需要你拍板。");
		Assert.True(sent.Ok, sent.Text);
		await parentEnded.Task.WaitAsync(TimeSpan.FromSeconds(5));

		var request = parentStream.Requests[^1];
		Assert.Contains(request, static message => message is UserMessage user &&
			(user.Content.Text ?? string.Empty).Contains("来自子会话", StringComparison.Ordinal) &&
			(user.Content.Text ?? string.Empty).Contains("这一步需要你拍板。", StringComparison.Ordinal));

		child.Dispose();
	}

	[Fact]
	public async Task OwnInitiativeRegistersACardAndNewSessionStartsTheChild()
	{
		var parentStream = new ScriptedStreamFn();
		parentStream.EnqueueText("父会话收到回报。");
		var childStream = new ScriptedStreamFn();
		childStream.EnqueueText("后续任务完成。");
		var host = new FakeHost(_root);
		host.Streams.Enqueue(childStream);

		using var parent = CreateSession(parentStream, host);
		var hub = Assert.IsType<LinkedSessionHub>(parent.Linked);
		var proposed = await hub.StartAsync("补测试", "给关联会话补一组测试。", LinkedSessionProposal.OwnInitiative);
		Assert.True(proposed.Ok, proposed.Text);
		Assert.Contains("\"status\":\"proposed\"", proposed.Text);
		Assert.Empty(host.Children);
		var proposal = Assert.Single(hub.Proposals);
		Assert.Equal(LinkedSessionProposal.Pending, proposal.Status);
		Assert.Equal(LinkedSessionProposal.OwnInitiative, proposal.Initiation);
		var listed = Assert.IsType<JsonArray>(hub.List()["proposals"]);
		Assert.Equal(proposal.Id, listed[0]!["proposal_id"]!.GetValue<string>());

		var answered = await hub.AnswerProposalAsync(proposal.Id, LinkedSessionAnswers.New);
		Assert.True(answered.Ok, answered.Text);
		var child = Assert.Single(host.Children);
		Assert.Equal(LinkedSessionProposal.Started, Assert.Single(hub.Proposals).Status);
		Assert.Equal(child.Session.SessionId, Assert.Single(hub.Children).ChildSessionId);

		await WaitForReportsAsync(hub, 1);
		await hub.DeliveryBarrier.WaitAsync(TimeSpan.FromSeconds(5));
		child.Dispose();
	}

	[Fact]
	public async Task OfferWaitsForTheCardAndReturnsTheAnswerStatus()
	{
		var parentStream = new ScriptedStreamFn();
		parentStream.EnqueueText("父会话收到回报。");
		var childStream = new ScriptedStreamFn();
		childStream.EnqueueText("批准的会话做完了。");
		var host = new FakeHost(_root);
		host.Streams.Enqueue(childStream);

		using var parent = CreateSession(parentStream, host);
		var hub = Assert.IsType<LinkedSessionHub>(parent.Linked);

		// 先拒绝一个 offer。
		var declinedRun = hub.StartAsync("不要这个", "这件事先不做。", LinkedSessionProposal.Offer);
		var declined = await WaitForPendingProposalAsync(hub, 1);
		Assert.True((await hub.AnswerProposalAsync(declined.Id, LinkedSessionAnswers.Dismiss)).Ok);
		var declinedResult = await declinedRun.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.True(declinedResult.Ok, declinedResult.Text);
		Assert.Contains("\"status\":\"declined\"", declinedResult.Text);
		Assert.Empty(host.Children);

		// 再批准一个 offer：工具调用在用户点「新会话」后返回 started。
		var acceptedRun = hub.StartAsync("批准这个", "这件事值得开。", LinkedSessionProposal.Offer);
		var accepted = await WaitForPendingProposalAsync(hub, 1);
		Assert.True((await hub.AnswerProposalAsync(accepted.Id, LinkedSessionAnswers.New)).Ok);
		var acceptedResult = await acceptedRun.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.True(acceptedResult.Ok, acceptedResult.Text);
		Assert.Contains("\"status\":\"started\"", acceptedResult.Text);
		var child = Assert.Single(host.Children);
		Assert.Equal(LinkedSessionProposal.Started, hub.Proposals.Single(p => p.Id == accepted.Id).Status);

		await WaitForReportsAsync(hub, 1);
		await hub.DeliveryBarrier.WaitAsync(TimeSpan.FromSeconds(5));
		child.Dispose();
	}

	[Fact]
	public async Task OwnInitiativeHereSendsTheBriefBackToTheParent()
	{
		var parentStream = new ScriptedStreamFn();
		parentStream.EnqueueText("父会话接手做。");
		var host = new FakeHost(_root);
		using var parent = CreateSession(parentStream, host);
		var hub = Assert.IsType<LinkedSessionHub>(parent.Linked);

		Assert.True((await hub.StartAsync("补文档", "补一段桌面文档。", LinkedSessionProposal.OwnInitiative)).Ok);
		var proposal = Assert.Single(hub.Proposals);
		var answered = await hub.AnswerProposalAsync(proposal.Id, LinkedSessionAnswers.Here);
		Assert.True(answered.Ok, answered.Text);
		Assert.Equal(LinkedSessionProposal.Here, hub.Proposals.Single().Status);

		var request = Assert.Single(parentStream.Requests);
		Assert.Contains(request, static message => message is UserMessage user &&
			(user.Content.Text ?? string.Empty).Contains("在这里做：补文档", StringComparison.Ordinal) &&
			(user.Content.Text ?? string.Empty).Contains("补一段桌面文档。", StringComparison.Ordinal));
	}

	[Fact]
	public async Task OfferIsExpiredWhenTheRunIsCancelled()
	{
		var host = new FakeHost(_root);
		using var parent = CreateSession(new ScriptedStreamFn(), host);
		var hub = Assert.IsType<LinkedSessionHub>(parent.Linked);
		using var cancel = new CancellationTokenSource();
		cancel.CancelAfter(TimeSpan.FromMilliseconds(50));

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
			hub.StartAsync("等不到回答", "没人会点这张卡。", LinkedSessionProposal.Offer, cancel.Token));
		Assert.Equal(LinkedSessionProposal.Expired, Assert.Single(hub.Proposals).Status);
		Assert.Empty(host.Children);
	}

	private static async Task<LinkedSessionProposal> WaitForPendingProposalAsync(LinkedSessionHub hub, int count)
	{
		for (var attempt = 0; attempt < 400; attempt++)
		{
			var pending = hub.Proposals.Where(static proposal => proposal.Status == LinkedSessionProposal.Pending).ToArray();
			if (pending.Length >= count)
			{
				return pending[^1];
			}

			await Task.Delay(10);
		}

		throw new TimeoutException("pending proposal did not appear");
	}

	[Fact]
	public void WithoutAHostTheLinkedToolsAreNotRegistered()
	{
		using var session = CreateSession(new ScriptedStreamFn());

		Assert.Null(session.Linked);
		Assert.DoesNotContain(session.Tools, tool => LinkedSessionsTool.IsName(tool.Name));
	}

	[Fact]
	public void WithAHostTheLinkedToolsAreRegistered()
	{
		var host = new FakeHost(_root);
		using var session = CreateSession(new ScriptedStreamFn(), host);

		Assert.NotNull(session.Linked);
		Assert.Contains(session.Tools, static tool => tool.Name == LinkedSessionsTool.StartName);
		Assert.Contains(session.Tools, static tool => tool.Name == LinkedSessionsTool.SendName);
		Assert.Contains(session.Tools, static tool => tool.Name == LinkedSessionsTool.ListName);
	}

	private static async Task WaitForReportsAsync(LinkedSessionHub hub, int turns)
	{
		if (hub.Children.Any(link => link.TurnCount >= turns))
		{
			return;
		}

		var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		void Check()
		{
			if (hub.Children.Any(link => link.TurnCount >= turns))
			{
				completion.TrySetResult();
			}
		}

		hub.Changed += Check;
		try
		{
			Check();
			await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
		}
		finally
		{
			hub.Changed -= Check;
		}
	}

	private sealed class FakeHost(string root) : ILinkedSessionHost
	{
		public Queue<ScriptedStreamFn> Streams { get; } = new();

		public List<CodingAgentSession> Children { get; } = [];

		public List<LinkedSessionStartContext> Contexts { get; } = [];

		public Task<CodingAgentSession?> CreateChildAsync(
			LinkedSessionStartContext context,
			CancellationToken cancellationToken)
		{
			if (Streams.Count == 0)
			{
				throw new InvalidOperationException("no scripted child stream");
			}

			var stream = Streams.Dequeue();
			var child = new CodingAgentSession(new CodingAgentSessionOptions
			{
				Cwd = root,
				Model = ScriptedSessions.Model(),
				StreamFn = stream.Invoke,
				Session = SessionManager.InMemory(root),
				Persist = false,
				Skills = [],
				PromptTemplates = [],
				Settings = new CodingAgentSettings(),
				LinkedSessionHost = this,
				LinkedParent = context.Parent,
				LinkedParentTitle = context.ParentTitle,
			});
			Children.Add(child);
			Contexts.Add(context);
			return Task.FromResult<CodingAgentSession?>(child);
		}
	}
}
