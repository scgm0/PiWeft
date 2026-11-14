using Aprillz.MewUI.Controls;
using PiWeft.Markdown;
using PiWeft.Agent;
using PiWeft.Ai;
using PiWeft.CodingAgent;
using PiWeft.CodingAgent.Compatibility;
using PiWeft.CodingAgent.Session;
using PiWeft.CodingAgent.Subagents;
using System.Text.Json.Nodes;
using Xunit;
using static PiWeft.Desktop.Tests.VisualTree;

namespace PiWeft.Desktop.Tests;

/// 注入的回报：子代理/关联会话的消息不是用户写的，投影成 Report 行而不是用户气泡。
[Collection("mewui-controls")]
public sealed class InjectedReportTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-report-" + Guid.NewGuid().ToString("N")[..8]);

	public InjectedReportTests() => Directory.CreateDirectory(_root);

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	private (CodingAgentSession Session, ChatViewModel Chat) Create()
	{
		var model = FakeStreams.Model();
		var session = new CodingAgentSession(new CodingAgentSessionOptions
		{
			Cwd = _root,
			Model = model,
			StreamFn = (streamModel, _, _) => FakeStreams.Text(streamModel, "ok"),
			Session = SessionManager.InMemory(_root),
			Persist = false,
			Skills = [],
			PromptTemplates = [],
		});
		var chat = new ChatViewModel();
		chat.Attach(session);
		return (session, chat);
	}

	[Fact]
	public void SubagentResult_ProjectsAsAReportRowNotAUserBubble()
	{
		var (session, chat) = Create();
		using var _ = session;
		using var __ = chat;

		session.Session.AppendMessage(new UserMessage { Content = MessageContent.FromText("我自己问的。") });
		session.Session.AppendCustomMessage(
			SubagentHub.ResultMessageType,
			MessageContent.FromText("子代理「scout-1」完成了一轮。\nsession_id: abc。\n\n结论在这里。"),
			display: true);

		var rows = chat.Entries();
		Assert.Equal(ChatRole.User, rows[0].Role);
		var report = rows[1];
		Assert.Equal(ChatRole.Report, report.Role);
		Assert.Equal("子代理回报「scout-1」", report.Label);
		Assert.Contains("结论在这里。", report.Text);
	}

	[Fact]
	public void LinkedReport_AndLinkedMessage_AreReports()
	{
		var (session, chat) = Create();
		using var _ = session;
		using var __ = chat;

		session.Session.AppendCustomMessage(
			PiWeftProtocol.LinkedReportMessageType,
			MessageContent.FromText("子会话「修构建」结束了一轮。\n子会话把构建修好了。"));
		session.Session.AppendCustomMessage(
			PiWeftProtocol.LinkedMessageType,
			MessageContent.FromText("来自父会话「主」：\n再看一下测试。"));

		var rows = chat.Entries();
		Assert.All(rows, static row => Assert.Equal(ChatRole.Report, row.Role));
		Assert.Equal("子会话回报「修构建」", rows[0].Label);
		Assert.Equal("关联会话消息「主」", rows[1].Label);
	}

	[Fact]
	public void ParentSubagentMessage_IsLabeledFromTheChildSide()
	{
		var (session, chat) = Create();
		using var _ = session;
		using var __ = chat;

		session.Session.AppendCustomMessage(
			SubagentHub.MessageType,
			MessageContent.FromText("来自父会话「主」：\n再看一下测试。"));

		var row = Assert.Single(chat.Entries());
		Assert.Equal(ChatRole.Report, row.Role);
		// 子会话收到的这条是父会话发来的，标题不能从发件方写成「子代理消息」。
		Assert.Equal("父会话消息「主」", row.Label);
	}

	[Fact]
	public void ReportIsFoldedIntoTheProcessGroupAndCollapsedByDefault()
	{
		var (session, chat) = Create();
		using var _ = session;
		using var __ = chat;

		session.Session.AppendCustomMessage(
			SubagentHub.ResultMessageType,
			MessageContent.FromText("子代理「scout-1」完成了一轮。\nsession_id: abc。\n## 小标题\n\n- 一条结论"));
		using var tab = new SessionTab(session, [session.Model], "report-render");
		ConversationPerformanceTests.Layout(tab.View, 960, 720);

		// 不占独立行：报告是执行过程组里的第一步。
		var rows = ExecutionProcess.Project(chat.Entries(), busy: false);
		Assert.DoesNotContain(rows, static row => row.Role == ChatRole.Report);
		var group = Assert.Single(rows, static row => row.Role == ChatRole.Process);
		Assert.Equal(ChatRole.Report, group.ProcessEntries[0].Role);

		// 默认收起：时间线上只有组头一行，正文不建视图。
		Assert.Empty(Find<MarkdownView>(tab.View));
		Assert.DoesNotContain(Find<TextBlock>(tab.View), block => block.Text == Strings.MessageExpand);
	}

	[Fact]
	public void ReportPreviewSkipsTheEnvelopeLine()
	{
		// 首行是「子代理「x」完成了一轮。session_id: …」这层信封，预览要取后面的正文。
		Assert.Equal("只读排查已完成。", InjectedContext.Preview(
			"子代理「quick-scan」完成了一轮。 session_id: 01a1。\n只读排查已完成。",
			80));

		Assert.Equal("## 标题不该当预览", InjectedContext.Preview("## 标题不该当预览", 80));
		Assert.Equal(string.Empty, InjectedContext.Preview("子代理「a」完成了一轮。", 80));
		Assert.Equal("一二三…", InjectedContext.Preview("一二三四五", 3));
	}

	[Fact]
	public void ReportsAreNotForkPointsAndDoNotFeedRetry()
	{
		var (session, chat) = Create();
		using var _ = session;
		using var __ = chat;

		session.Session.AppendMessage(new UserMessage { Content = MessageContent.FromText("真正的提问。") });
		session.Session.AppendCustomMessage(
			SubagentHub.ResultMessageType,
			MessageContent.FromText("子代理「scout-1」完成了一轮。\n结论。"));

		var forkable = session.Session.GetUserMessagesForForking();
		var only = Assert.Single(forkable);
		Assert.Equal("真正的提问。", only.Text);
		// 重试取最后一条用户提问，不能取到回报。
		Assert.Equal("真正的提问。", chat.LastUserText());
	}

	[Fact]
	public void LegacyReportsWithoutACustomTypeStillRenderAsReports()
	{
		// 修 customType 落盘之前写出来的会话：文件里只是普通 user 消息。
		var (session, chat) = Create();
		using var _ = session;
		using var __ = chat;

		session.Session.AppendMessage(new UserMessage
		{
			Content = MessageContent.FromText("子代理「ui-review」完成了一轮。\nsession_id: abc。\n## 结论"),
		});

		var row = Assert.Single(chat.Entries());
		Assert.Equal(ChatRole.Report, row.Role);
		Assert.Equal("子代理回报「ui-review」", row.Label);
	}

	[Fact]
	public void WaitedSubagentResultsStillGetAReadOnlyReportCard()
	{
		var (session, chat) = Create();
		using var _ = session;
		using var __ = chat;

		session.Session.AppendMessage(new UserMessage { Content = MessageContent.FromText("测试子代理。") });
		session.Session.AppendMessage(new ToolResultMessage
		{
			ToolCallId = "call-1",
			ToolName = SubagentTools.WaitName,
			Timestamp = 1,
			Content = [new TextContent { Text = "wait_subagents\n- quick-test (scout) · idle · 1 turns" }],
			Details = new JsonObject
			{
				["timed_out"] = false,
				["subagents"] = new JsonArray(
				[
					new JsonObject
					{
						["name"] = "quick-test",
						["agent"] = "scout",
						["state"] = "idle",
						["delivered"] = true,
						["returned"] = true,
						["result"] = "顶层条目、测试与工具数量都对上了。",
					},
					// 没被 wait 取走的行没有正文：不该画出第二张卡。
					new JsonObject
					{
						["name"] = "still-running",
						["agent"] = "scout",
						["state"] = "running",
						["delivered"] = false,
						["returned"] = false,
					},
				]),
			},
		});

		var reports = chat.Entries().Where(static row => row.Role == ChatRole.Report).ToArray();
		var report = Assert.Single(reports);
		Assert.Equal("子代理回报「quick-test」", report.Label);
		Assert.Contains("顶层条目", report.Text);
	}

	[Fact]
	public void QueuedRowKeepsTheActionsAtTheirOwnWidth()
	{
		using var session = ConversationPerformanceTests.CreateSession(0);
		using var tab = new SessionTab(session, [session.Model], "queue-layout");
		tab.RefreshQueueForTests(
		[
			new ChatQueuedMessage(
				new UserMessage { Content = MessageContent.FromText("一条很长的排队消息，用来验证正文会占满而不是按钮被拉伸。") },
				"一条很长的排队消息，用来验证正文会占满而不是按钮被拉伸。", 0, false),
		]);
		ConversationPerformanceTests.Layout(tab.View, 960, 720);

		var retract = Find<Button>(tab.View).Single(candidate => HoverHints.Text(candidate) == Strings.QueuedRetract);
		var discard = Find<Button>(tab.View).Single(candidate => HoverHints.Text(candidate) == Strings.QueuedDiscard);
		// 图标按钮保持图标尺寸：LastChildFill 曾经把「撤回」拉成一条横条。
		Assert.InRange(retract.Bounds.Width, 0, 40);
		Assert.InRange(discard.Bounds.Width, 0, 40);

		// 徽标在正文左边，不是和动作挤在右侧。
		var badge = Find<TextBlock>(tab.View).Single(block => block.Text == Strings.QueuedFollowUp);
		Assert.True(badge.Bounds.X < discard.Bounds.X);
	}

	[Fact]
	public void ReportTitleIgnoresQuotedBracketsInTheBody()
	{
		Assert.Equal("子代理回报「scout-1」", InjectedContext.Title(
			Strings.ReportSubagentResult,
			"子代理「scout-1」完成了一轮。\n按「配置说明」第 3 节。"));
		Assert.Equal("子会话回报「修构建」", InjectedContext.Title(
			Strings.ReportLinkedChild,
			"子会话「修构建」结束了一轮。"));
	}

	[Fact]
	public void OrdinaryUserMessage_StaysAUserBubble()
	{
		var (session, chat) = Create();
		using var _ = session;
		using var __ = chat;

		session.Session.AppendMessage(new UserMessage { Content = MessageContent.FromText("普通提问。") });
		var row = Assert.Single(chat.Entries());
		Assert.Equal(ChatRole.User, row.Role);
		Assert.Null(row.Label);
	}

	[Fact]
	public void ReportJoinsTheCurrentTurnInsteadOfStartingASecondGroup()
	{
		var (session, chat) = Create();
		using var _ = session;
		using var __ = chat;

		session.Session.AppendMessage(new UserMessage { Content = MessageContent.FromText("问。") });
		session.Session.AppendMessage(new AssistantMessage
		{
			Content =
			[
				new TextContent { Text = "先查一下。" },
				new ToolCall { Id = "call-1", Name = "bash", Arguments = new System.Text.Json.Nodes.JsonObject { ["command"] = "ls" } },
			],
			StopReason = StopReason.ToolUse,
		});
		session.Session.AppendMessage(new ToolResultMessage
		{
			ToolCallId = "call-1",
			ToolName = "bash",
			Content = [new TextContent { Text = "a\nb" }],
		});
		session.Session.AppendCustomMessage(
			SubagentHub.ResultMessageType,
			MessageContent.FromText("子代理「scout-1」完成了一轮。\n结论。"));
		session.Session.AppendMessage(new AssistantMessage
		{
			Content = [new TextContent { Text = "收到回报。" }],
			StopReason = StopReason.Stop,
		});

		var rows = ExecutionProcess.Project(chat.Entries(), busy: false);

		// 一次提问只有一个执行过程组：回报不再另起一组。
		var groups = rows.Where(static row => row.Role == ChatRole.Process).ToArray();
		var group = Assert.Single(groups);
		Assert.Equal(ChatRole.Report, group.ProcessEntries[^1].Role);
		Assert.Contains(group.ProcessEntries, static entry => entry.ToolName == "bash");
		// 组头摘要把回报单独算一类，不会被「执行了命令」吞掉。
		Assert.Contains("收到子代理回报", ExecutionProcess.GroupSummary(group.ProcessEntries));
		Assert.Contains(rows, static row => row.Role == ChatRole.Assistant && row.Text == "收到回报。");
	}

	[Fact]
	public void InjectedReportInTheQueueHasNoRetractOrDiscard()
	{
		using var session = ConversationPerformanceTests.CreateSession(0);
		using var tab = new SessionTab(session, [session.Model], "queue");
		tab.RefreshQueueForTests(
		[
			new ChatQueuedMessage(
				new UserMessage { Content = MessageContent.FromText("用户写的。") }, "用户写的。", 0, false),
			new ChatQueuedMessage(
				new UserMessage
				{
					Content = MessageContent.FromText("子代理「scout-1」完成了一轮。"),
					CustomType = SubagentHub.ResultMessageType,
				}, "子代理「scout-1」完成了一轮。", 0, false),
		]);
		ConversationPerformanceTests.Layout(tab.View, 960, 720);

		Assert.Equal(2, tab.QueueCardRowCount);
		var retract = Find<Button>(tab.View).Count(candidate => HoverHints.Text(candidate) == Strings.QueuedRetract);
		var discard = Find<Button>(tab.View).Count(candidate => HoverHints.Text(candidate) == Strings.QueuedDiscard);
		// 只有用户写的那条带撤回/丢弃。
		Assert.Equal(1, retract);
		Assert.Equal(1, discard);
		Assert.Contains(Find<TextBlock>(tab.View), block => block.Text == Strings.QueuedInjected);
	}
}
