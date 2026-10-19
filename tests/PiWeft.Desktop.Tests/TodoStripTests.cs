using System.Text.Json.Nodes;
using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using PiWeft.Ai;
using PiWeft.Agent;
using PiWeft.CodingAgent;
using PiWeft.CodingAgent.Session;
using PiWeft.Desktop;
using Xunit;
using static PiWeft.Desktop.Tests.VisualTree;

namespace PiWeft.Desktop.Tests;

/// <summary>D-01：待办条只在存在计划时出现，并展示进度与条目。</summary>
[Collection("mewui-controls")]
public sealed class TodoStripTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "pi-todo-strip-" + Guid.NewGuid().ToString("N")[..8]);

	public TodoStripTests() => Directory.CreateDirectory(_root);

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	private CodingAgentSession Session() =>
		new(new CodingAgentSessionOptions
		{
			Cwd = _root,
			Model = FakeStreams.Model(),
			StreamFn = (model, _, _) => FakeStreams.Text(model, "ok"),
			Session = SessionManager.InMemory(_root),
			Persist = false,
			Skills = [],
			PromptTemplates = [],
		});

	private static Task<AgentToolResult> AddAsync(CodingAgentSession session, string text) =>
		TodoAsync(session, new JsonObject { ["action"] = "add", ["text"] = text });

	private static Task<AgentToolResult> TodoAsync(CodingAgentSession session, JsonObject arguments)
	{
		var todo = session.Tools.Single(static tool => tool.Name == "todo");
		return todo.Execute(new AgentToolInvocation
		{
			ToolCallId = "1",
			ToolName = "todo",
			Arguments = arguments,
		});
	}

	[Fact]
	public async Task TodoStripShowsTheActivePlan()
	{
		using var session = Session();
		await AddAsync(session, "写测试");

		using var tab = new SessionTab(session, [session.Model], "test");

		Assert.Contains(Find<TextBlock>(tab.View), block => block.Text == Strings.TodoPlanProgress(0, 0, 1));
		Assert.Contains(Find<TextBlock>(tab.View), block => block.Text == "写测试");
		Assert.Contains(Find<TodoStatusGlyph>(tab.View), glyph => glyph.Status == "pending");
	}

	[Fact]
	public async Task TodoCardMatchesTheComposerWidthAndShowsSegmentedProgress()
	{
		using var session = Session();
		await AddAsync(session, "收集素材");
		await AddAsync(session, "整理结构");
		await TodoAsync(session, new JsonObject { ["action"] = "update", ["id"] = 1, ["status"] = "completed" });
		await TodoAsync(session, new JsonObject { ["action"] = "update", ["id"] = 2, ["status"] = "in_progress" });
		await AddAsync(session, "写初稿");
		// 有消息时输入区贴底，卡片才和输入卡处于同一列布局。
		session.Session.AppendMessage(new UserMessage { Content = MessageContent.FromText("开始") });
		using var tab = new SessionTab(session, [session.Model], "test");
		ConversationPerformanceTests.Layout(tab.View, 960, 720);

		Assert.Contains(Find<TextBlock>(tab.View), block => block.Text == "1 完成\u2002·\u20021 进行中\u2002·\u20021 待处理");
		Assert.Contains(Find<TodoStatusGlyph>(tab.View), glyph => glyph.Status == "completed");
		Assert.Contains(Find<TodoStatusGlyph>(tab.View), glyph => glyph.Status == "in_progress");
		Assert.Contains(Find<TodoStatusGlyph>(tab.View), glyph => glyph.Status == "pending");

		var composer = Find<Border>(tab.View).Single(border => border.StyleName == AppStyles.Composer);
		var card = Find<Border>(tab.View).Single(border => border.StyleName == AppStyles.Card &&
			Find<TextBlock>(border).Any(text => text.Text == Strings.TodoPlanTitle));
		Assert.Equal(composer.Bounds.Width, card.Bounds.Width, 1);
		Assert.True(card.Bounds.Bottom <= composer.Bounds.Top);
		var header = Find<Button>(card).First();
		Assert.Equal(AppTheme.ControlHeightLg, header.ActualHeight, 1);

		// 展开/收起只隐藏列表，不重建会话或丢弃计划。
		var expandedHeight = card.ActualHeight;
		ImagePreviewTests.Click(header);
		ConversationPerformanceTests.Layout(tab.View, 960, 720);
		Assert.True(card.ActualHeight < expandedHeight);
		ImagePreviewTests.Click(header);
		ConversationPerformanceTests.Layout(tab.View, 960, 720);
		Assert.Equal(expandedHeight, card.ActualHeight, 1);
	}

	[Fact]
	public void TestConnectionButtonExists()
	{
		using var session = Session();
		using var tab = new SessionTab(session, [session.Model], "test");

		Assert.Contains(Find<Button>(tab.View), button => HoverHints.Text(button) == Strings.TestConnection);
	}

	[Fact]
	public void TerminalButtonTogglesTheBottomDock()
	{
		using var session = Session();
		using var tab = new SessionTab(session, [session.Model], "test");

		// 终端属于会话底部的纵向分栏，默认收起（不再占用右侧面板）。
		var split = Assert.IsType<SplitPanel>(tab.View);
		Assert.Equal(Orientation.Vertical, split.Orientation);
		Assert.False(tab.IsTerminalOpen);

		var pane = Find<Border>(tab.View).Single(border => border.StyleName == AppStyles.TerminalDock);
		Assert.False(pane.IsVisible);

		var button = Find<Button>(tab.View).Single(candidate => HoverHints.Text(candidate) == Strings.Terminal);
		ImagePreviewTests.Click(button);
		Assert.True(tab.IsTerminalOpen);
		Assert.True(pane.IsVisible);
		Assert.NotEmpty(Find<Border>(pane));

		ImagePreviewTests.Click(button);
		Assert.False(tab.IsTerminalOpen);
		Assert.False(pane.IsVisible);
	}

	[Fact]
	public void PlanModeButtonTogglesTheSession()
	{
		using var session = Session();
		using var tab = new SessionTab(session, [session.Model], "test");

		var button = Find<Button>(tab.View).Single(candidate => HoverHints.Text(candidate) == Strings.PlanModeHint);
		ImagePreviewTests.Click(button);

		Assert.True(session.PlanModeEnabled);
		Assert.Contains(Find<TextBlock>(tab.View), block => block.Text == Strings.PlanModeOn);

		ImagePreviewTests.Click(button);
		Assert.False(session.PlanModeEnabled);
	}

	[Fact]
	public void ExtensionWidgetsUseTheirOwnCardAndStatusesStayOnOneLine()
	{
		using var session = Session();
		session.Session.AppendMessage(new UserMessage { Content = MessageContent.FromText("开始") });
		using var tab = new SessionTab(session, [session.Model], "test");
		ConversationPerformanceTests.Layout(tab.View, 960, 720);

		tab.SetExtensionStatus("mcp", "MCP 3 个服务");
		tab.SetExtensionWidget("pi-deck-goal", ["阶段：active", "目标：完成重构"]);
		ConversationPerformanceTests.Layout(tab.View, 960, 720);

		Assert.Contains(Find<TextBlock>(tab.View), block => block.Text == Strings.ExtensionWidgetsTitle);
		Assert.Contains(Find<TextBlock>(tab.View), block => block.Text == "goal");
		Assert.Contains(Find<TextBlock>(tab.View), block => block.Text == "目标：完成重构");
		Assert.Contains(Find<TextBlock>(tab.View), block => block.Text == "MCP 3 个服务");
		var composer = Find<Border>(tab.View).Single(border => border.StyleName == AppStyles.Composer);
		var card = Find<Border>(tab.View).Single(border => border.StyleName == AppStyles.Card &&
			Find<TextBlock>(border).Any(text => text.Text == Strings.ExtensionWidgetsTitle));
		Assert.Equal(composer.Bounds.Width, card.Bounds.Width, 1);
		Assert.Equal(AppTheme.ControlHeightLg, Find<Button>(card).First().ActualHeight, 1);
		// widget 行不再并进状态行。
		Assert.DoesNotContain(Find<TextBlock>(tab.View), block => block.Text.Contains("阶段：active", StringComparison.Ordinal) && block.Text.Contains("MCP", StringComparison.Ordinal));

		tab.SetExtensionWidget("pi-deck-goal", null);
		Assert.DoesNotContain(Find<TextBlock>(tab.View), block => block.Text == "目标：完成重构");
	}

	[Fact]
	public void TodoStripIsHiddenWithoutAPlan()
	{
		using var session = Session();
		using var tab = new SessionTab(session, [session.Model], "test");

		Assert.False(tab.TodoCardVisible);
	}
}
