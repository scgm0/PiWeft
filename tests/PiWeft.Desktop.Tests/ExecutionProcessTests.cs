using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Aprillz.MewUI.Markdown;
using Xunit;
using static PiWeft.Desktop.Tests.VisualTree;

namespace PiWeft.Desktop.Tests;

[Collection("mewui-controls")]
public sealed class ExecutionProcessTests
{
	private static ChatEntry User(string id) => new() { Role = ChatRole.User, Text = id, EntryId = id };
	private static ChatEntry Answer(string id, string text, string thinking = "") =>
		new() { Role = ChatRole.Assistant, EntryId = id, Text = text, Thinking = thinking };
	private static ChatEntry Tool(string id, string text, bool pending = false) =>
		new() { Role = ChatRole.Tool, EntryId = $"{id}/{(pending ? "call" : "result")}", ToolCallId = id, ToolName = "read", Text = text, IsStreaming = pending };

	[Fact]
	public void OneFoldPerTurnKeepsFinalAnswerVisibleAndCountsEachToolOnce()
	{
		var rows = ExecutionProcess.Project([
			User("first"), Answer("interim", "checking", "plan"), Tool("call", "arguments", true), Tool("call", "output"),
			Answer("final", "done", "conclusion"), User("second"), Answer("second-final", "hello"),
		], false);
		Assert.Equal(new[] { ChatRole.User, ChatRole.Process, ChatRole.Assistant, ChatRole.User, ChatRole.Assistant }, rows.Select(row => row.Role));
		var process = rows[1];
		Assert.Equal("process/first", process.EntryId);
		Assert.Equal("done", rows[2].Text);
		Assert.Empty(rows[2].Thinking);
		var tool = Assert.Single(process.ProcessEntries, row => row.Role == ChatRole.Tool);
		Assert.Equal("output", tool.Text);
		Assert.Equal("arguments", tool.ToolArguments);
		Assert.Equal(new[] { "", "checking", "output", "" }, process.ProcessEntries.Select(row => row.Text));
		Assert.Contains("工具 1", ExecutionProcess.Summary(process.ProcessEntries));
		Assert.Contains("思考 2", ExecutionProcess.Summary(process.ProcessEntries));
		Assert.Contains("中间回复 1", ExecutionProcess.Summary(process.ProcessEntries));
	}

	[Fact]
	public void ToolTailDoesNotPromoteAnInterimAnswerAndErrorsAreKeptInOrder()
	{
		var error = new ChatEntry { Role = ChatRole.Error, Text = "failed" };
		var rows = ExecutionProcess.Project([User("first"), Answer("interim", "checking"), Tool("call", "output"), error], false);
		Assert.Equal(2, rows.Count);
		Assert.Equal(new[] { ChatRole.Assistant, ChatRole.Tool, ChatRole.Error }, rows[1].ProcessEntries.Select(row => row.Role));
		Assert.Contains("失败 1", ExecutionProcess.Summary(rows[1].ProcessEntries));
	}

	[Fact]
	public void StreamingTextAndImageOnlyFinalAnswerRemainOutsideTheFold()
	{
		var image = new ChatImage("data", "image/png");
		var rows = ExecutionProcess.Project([User("first"), Tool("call", "output"),
			Answer("live", "partial", "reasoning") with { IsStreaming = true }], true);
		Assert.True(rows[1].IsStreaming);
		Assert.True(rows[^1].IsStreaming);
		Assert.Equal("partial", rows[^1].Text);
		var completed = ExecutionProcess.Project([User("first"), Tool("call", "output"),
			Answer("final", "") with { Images = [image] }], false);
		Assert.Equal(image, Assert.Single(completed[^1].Images));
		Assert.DoesNotContain(completed[1].ProcessEntries, entry => entry.Images.Count > 0);
	}

	[Fact]
	public void ProcessAndGroupsAreLazyAndNestedStateSurvivesRecycling()
	{
		using var focus = new TextFocusScrollGuard(new ScrollViewer());
		var process = ExecutionProcess.Project([User("first"), Tool("call", "output"), Answer("final", "done")], false)[1];
		UIElement Create(ChatEntry entry) => entry.Role == ChatRole.Process
			? new ExecutionProcessView(entry, Create).View
			: entry.Role == ChatRole.Tool ? new ToolMessageView(entry, focus).View : new AssistantMessageView(entry, _ => { }, focus).View;
		using var timeline = new ConversationTimeline(Create);
		var rows = new[] { process }.Concat(Enumerable.Range(0, 200).Select(i => Answer($"row-{i}", $"answer {i}"))).ToArray();
		timeline.SetEntries(rows);
		timeline.View.ScrollIntoView(0);
		ConversationPerformanceTests.Layout(timeline.View);
		var run = Assert.Single(Find<Expander>(timeline.View), expander => HoverHints.Text(expander) == Strings.ExecutionExpandHint);
		Assert.Null(run.Content);
		run.IsExpanded = true;
		var group = Assert.Single(Find<Expander>(run.Content!));
		Assert.Null(group.Content);
		group.IsExpanded = true;
		var tool = Assert.Single(Find<Expander>(group.Content!));
		Assert.Null(tool.Content);
		tool.IsExpanded = true;
		Assert.Contains(Find<MultiLineTextBox>(tool.Content!), text => text.Text == "output");
		run.IsExpanded = false;
		timeline.View.ScrollIntoView(rows.Length - 1);
		ConversationPerformanceTests.Layout(timeline.View);
		timeline.PruneRecycled();
		timeline.View.ScrollIntoView(0);
		ConversationPerformanceTests.Layout(timeline.View);
		var restored = Assert.Single(Find<Expander>(timeline.View), expander => HoverHints.Text(expander) == Strings.ExecutionExpandHint);
		Assert.False(restored.IsExpanded);
		restored.IsExpanded = true;
		var restoredGroup = Assert.Single(Find<Expander>(restored.Content!), expander => HoverHints.Text(expander) == Strings.ExecutionGroupHint);
		Assert.True(restoredGroup.IsExpanded);
		Assert.True(Assert.Single(Find<Expander>(restoredGroup.Content!)).IsExpanded);
	}

	[Fact]
	public void UpdatingTheStreamingRunKeepsExpandedToolContentAndManualCollapse()
	{
		using var focus = new TextFocusScrollGuard(new ScrollViewer());
		ChatEntry Process(ChatEntry tool) => ExecutionProcess.Project([User("first"), tool, Answer("live", "partial") with { IsStreaming = true }], true)[1];
		var view = new ExecutionProcessView(Process(Tool("call", "output")), entry => entry.Role == ChatRole.Tool
			? new ToolMessageView(entry, focus).View : new AssistantMessageView(entry, _ => { }, focus).View);
		var toolFold = Find<Expander>(view.View).Single(expander => HoverHints.Text(expander) == Strings.ToolExpandHint);
		toolFold.IsExpanded = true;
		var content = toolFold.Content;
		view.Update(Process(Tool("call", "output")));
		Assert.Same(content, toolFold.Content);
		var run = Find<Expander>(view.View).First();
		run.IsExpanded = false;
		view.Update(Process(Tool("call", "new output")) with { IsStreaming = false });
		Assert.False(run.IsExpanded);
		run.IsExpanded = true;
		Assert.True(Find<Expander>(view.View).Single(expander => HoverHints.Text(expander) == Strings.ToolExpandHint).IsExpanded);
		UiLifetime.Release(view.View);
	}

	[Fact]
	public void LiveGroupsAdvanceWithoutClosingManuallyOpenedGroups()
	{
		using var focus = new TextFocusScrollGuard(new ScrollViewer());
		ChatEntry Process(params ChatEntry[] content) => ExecutionProcess.Project([User("first"), .. content], true)[1];
		var view = new ExecutionProcessView(Process(Tool("one", "output")), entry => entry.Role == ChatRole.Tool
			? new ToolMessageView(entry, focus).View : new AssistantMessageView(entry, _ => { }, focus).View);
		var first = Find<Expander>(view.View).Single(expander => HoverHints.Text(expander) == Strings.ExecutionGroupHint);
		Assert.True(first.IsExpanded);
		first.IsExpanded = false;
		first.IsExpanded = true;
		view.Update(Process(Tool("one", "output"), Answer("interim", "continue"), Tool("two", "output")));
		var groups = Find<Expander>(view.View).Where(expander => HoverHints.Text(expander) == Strings.ExecutionGroupHint).ToArray();
		Assert.Equal(2, groups.Length);
		Assert.All(groups, group => Assert.True(group.IsExpanded));
		UiLifetime.Release(view.View);
	}

	[Fact]
	public void CollapsedProcessReleasesNestedTextAnimations()
	{
		using var focus = new TextFocusScrollGuard(new ScrollViewer());
		var process = ExecutionProcess.Project([User("first"), Tool("one", "output"), Answer("final", "done")], false)[1];
		var view = new ExecutionProcessView(process, entry => new ToolMessageView(entry, focus).View);
		for (var depth = 0; depth < 3; depth++)
			foreach (var fold in Find<Expander>(view.View).ToArray()) fold.IsExpanded = true;
		var text = Find<MultiLineTextBox>(view.View).Single();
		var animation = SmoothScroll.For(text);
		animation.ScrollBy(0, 1);
		Assert.True(animation.IsAnimating);
		Find<Expander>(view.View).First().IsExpanded = false;
		UiLifetime.Release(view.View);
		Assert.False(animation.IsAnimating);
	}

	[Fact]
	public void ComposerNoLongerContainsBalanceEntry()
	{
		using var session = ConversationPerformanceTests.CreateSession(1);
		using var tab = new SessionTab(session, [session.Model], "test");
		Assert.DoesNotContain(Find<Button>(tab.View), button => HoverHints.Text(button) == Strings.ProviderUsage);
	}
}
