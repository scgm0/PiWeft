using System.Text.Json.Nodes;
using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
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

	private static Task<AgentToolResult> AddAsync(CodingAgentSession session, string text)
	{
		var todo = session.Tools.Single(static tool => tool.Name == "todo");
		return todo.Execute(new AgentToolInvocation
		{
			ToolCallId = "1",
			ToolName = "todo",
			Arguments = new JsonObject { ["action"] = "add", ["text"] = text },
		});
	}

	[Fact]
	public async Task TodoStripShowsTheActivePlan()
	{
		using var session = Session();
		await AddAsync(session, "写测试");

		using var tab = new SessionTab(session, [session.Model], "test");

		Assert.Contains(Find<TextBlock>(tab.View), block => block.Text == Strings.TodoPlanProgress(0, 1));
		Assert.Contains(Find<TextBlock>(tab.View), block => block.Text.Contains("#1 写测试", StringComparison.Ordinal));
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
	public void TodoStripIsHiddenWithoutAPlan()
	{
		using var session = Session();
		using var tab = new SessionTab(session, [session.Model], "test");

		Assert.DoesNotContain(Find<TextBlock>(tab.View), block => block.Text.StartsWith(Strings.TodoPlanTitle, StringComparison.Ordinal));
	}
}
