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
	public void GroupHeadSummaryListsToolKindsAndCollapsesPastThree()
	{
		static ChatEntry Call(string name, int index) =>
			new() { Role = ChatRole.Tool, EntryId = $"tool/{index}", ToolName = name, Text = "ok" };

		Assert.Equal("执行了命令、已读取文件", ExecutionProcess.GroupSummary([Call("bash", 0), Call("read", 1)]));
		// 超过三个类别时收成「… 等」，组头不会跟着工具种类无限变长。
		Assert.Equal("执行了命令、已读取文件、修改了文件 等",
			ExecutionProcess.GroupSummary([Call("bash", 0), Call("read", 1), Call("edit", 2), Call("grep", 3)]));
		Assert.Equal(string.Empty, ExecutionProcess.GroupSummary([Answer("thinking", string.Empty, "plan")]));
	}

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
	public void IncrementalProjectionMatchesFullProjectionWhileTailChanges()
	{
		var entries = new List<ChatEntry>();
		for (var i = 0; i < 200; i++)
		{
			entries.Add(User($"u{i}"));
			entries.Add(Answer($"a{i}", $"answer {i}"));
		}
		var projector = new ExecutionProcessProjector();

		AssertProjection(entries, true, projector);
		for (var step = 1; step <= 8; step++)
		{
			entries[^1] = entries[^1] with { Text = new string('x', step * 64) };
			AssertProjection(entries, true, projector);
		}

		entries.Add(Tool("call", "output", pending: true));
		AssertProjection(entries, true, projector);
		entries[^1] = Tool("call", "output");
		AssertProjection(entries, true, projector);

		entries.Add(User("next"));
		AssertProjection(entries, true, projector);
		entries.Add(Answer("next-answer", "done"));
		AssertProjection(entries, false, projector);

		// A change before the last user turn invalidates the cached prefix and must fall back fully.
		entries[1] = Answer("a0", "rewritten answer");
		AssertProjection(entries, false, projector);
	}

	private static void AssertProjection(IReadOnlyList<ChatEntry> entries, bool busy, ExecutionProcessProjector projector)
	{
		var expected = ExecutionProcess.Project(entries, busy);
		var actual = projector.Project(entries, busy);
		Assert.Equal(expected.Count, actual.Count);
		for (var index = 0; index < expected.Count; index++)
		{
			Assert.True(ConversationTimeline.SameEntry(expected[index], actual[index]),
				$"row {index} differs: {expected[index].Role} vs {actual[index].Role}");
		}
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
	public void GroupShowsEarlierStepsBehindAFold()
	{
		using var focus = new TextFocusScrollGuard(new ScrollViewer());
		var tools = Enumerable.Range(0, 12).Select(i => Tool($"call-{i}", $"output {i}")).ToArray();
		var process = ExecutionProcess.Project([User("first"), .. tools, Answer("final", "done")], false)[1];
		var view = new ExecutionProcessView(process, entry => new ToolMessageView(entry, focus).View);
		ConversationPerformanceTests.Layout(view.View);

		var run = Assert.Single(Find<Expander>(view.View), expander => HoverHints.Text(expander) == Strings.ExecutionExpandHint);
		run.IsExpanded = true;
		var group = Assert.Single(Find<Expander>(run.Content!), expander => HoverHints.Text(expander) == Strings.ExecutionGroupHint);
		group.IsExpanded = true;
		Assert.Equal(8, Find<Expander>(group.Content!).Count(expander => HoverHints.Text(expander) == Strings.ToolExpandHint));
		var earlier = Find<Button>(group.Content!).Single(button => Find<TextBlock>(button).Any(text => text.Text == Strings.ExecutionEarlierSteps(4)));
		ImagePreviewTests.Click(earlier);
		Assert.Equal(12, Find<Expander>(group.Content!).Count(expander => HoverHints.Text(expander) == Strings.ToolExpandHint));
		UiLifetime.Release(view.View);
	}

	[Fact]
	public void StoppedTurnDoesNotLeaveAResultlessToolRunning()
	{
		var process = ExecutionProcess.Project([User("first"), Tool("orphan", "arguments", pending: true)], false)[1];
		var tool = Assert.Single(process.ProcessEntries);
		Assert.True(tool.IsStopped);
		Assert.False(tool.IsStreaming);
		using var focus = new TextFocusScrollGuard(new ScrollViewer());
		var view = new ToolMessageView(tool, focus);
		Assert.Contains(Find<TextBlock>(view.View), text => text.Text == Strings.ToolStopped);
		UiLifetime.Release(view.View);
	}

	[Fact]
	public void RunningProcessDoesNotClaimSuccessAndAnEarlierGroupIsNotStillRunning()
	{
		using var focus = new TextFocusScrollGuard(new ScrollViewer());
		var process = new ChatEntry
		{
			Role = ChatRole.Process, Text = "", IsStreaming = true,
			ProcessEntries = [Tool("one", "ok"), new() { Role = ChatRole.Error, Text = "retry needed" }],
		};
		var view = new ExecutionProcessView(process, entry => entry.Role == ChatRole.Tool
			? new ToolMessageView(entry, focus).View : new TextBlock().Text(entry.Text));
		Assert.False(Find<GlyphElement>(view.View).Single(glyph => glyph.Kind == GlyphKind.CheckMark).IsVisible);
		var group = Find<Expander>(view.View).Single(expander => HoverHints.Text(expander) == Strings.ExecutionGroupHint);
		Assert.Contains(Find<TextBlock>(group.Header!), text => text.Text == "已读取文件");
		Assert.DoesNotContain(Find<TextBlock>(group.Header!), text => text.Text == Strings.ProcessGroupRead);
		UiLifetime.Release(view.View);
	}

	[Fact]
	public void ExpandedGroupHasABoundedViewportAndToolHeadersHaveNoDuplicateChevrons()
	{
		using var focus = new TextFocusScrollGuard(new ScrollViewer());
		var process = ExecutionProcess.Project([User("first"), Tool("one", "output")], true)[1];
		var view = new ExecutionProcessView(process, entry => new ToolMessageView(entry, focus).View);
		var viewport = Find<ScrollViewer>(view.View).Single();
		Assert.Equal(300, viewport.MaxHeight);
		var tool = Find<Expander>(view.View).Single(expander => HoverHints.Text(expander) == Strings.ToolExpandHint);
		Assert.DoesNotContain(Find<GlyphElement>(tool.Header!), glyph => glyph.Kind is GlyphKind.ChevronRight or GlyphKind.ChevronDown);
		Assert.Null(tool.Content);
		UiLifetime.Release(view.View);
	}

	[Fact]
	public void ContinuousTimelineHasItsOwnColumnToTheLeftOfEveryStepIcon()
	{
		using var focus = new TextFocusScrollGuard(new ScrollViewer());
		var process = ExecutionProcess.Project([
			User("first"), Answer("plan", "", "check layout"), Tool("one", "output\nsecond line"), Tool("two", "ok"),
		], true)[1];
		var view = new ExecutionProcessView(process, entry => entry.Role == ChatRole.Tool
			? new ToolMessageView(entry, focus).View : new AssistantMessageView(entry, _ => { }, focus).View);
		ConversationPerformanceTests.Layout(view.View);
		var viewport = Assert.Single(Find<ScrollViewer>(view.View));
		var line = Assert.Single(Find<Border>(viewport), border => border.Width == 1);
		var icons = Find<Image>(viewport).Where(icon => icon.Width == 14 && icon.Margin == new Thickness(2, 3, 0, 0)).ToArray();
		Assert.Equal(3, icons.Length);
		Assert.True(line.Bounds.Height > 0);
		Assert.All(icons, icon => Assert.True(icon.Bounds.X - line.Bounds.Right >= 12,
			$"The step icon {icon.Bounds} overlaps the timeline {line.Bounds}."));

		// 展开工具输出之后仍是同一条连续竖线，不会在图标处截断或移进图标列。
		var tool = Find<Expander>(viewport).First(expander => HoverHints.Text(expander) == Strings.ToolExpandHint);
		tool.IsExpanded = true;
		ConversationPerformanceTests.Layout(view.View);
		Assert.All(icons, icon => Assert.True(icon.Bounds.X - line.Bounds.Right >= 12));
		Assert.True(line.Bounds.Y <= icons[0].Bounds.Y);
		Assert.True(line.Bounds.Bottom >= icons[^1].Bounds.Bottom);
		UiLifetime.Release(view.View);
	}

	[Fact]
	public void GroupBodyOnlyConsumesTheWheelWhileItCanActuallyScroll()
	{
		using var focus = new TextFocusScrollGuard(new ScrollViewer());
		var tools = Enumerable.Range(0, 12).Select(i => Tool($"call-{i}", $"output {i}")).ToArray();
		var process = ExecutionProcess.Project([User("first"), .. tools, Answer("final", "done")], false)[1];
		var view = new ExecutionProcessView(process, entry => entry.Role == ChatRole.Tool
			? new ToolMessageView(entry, focus).View : new AssistantMessageView(entry, _ => { }, focus).View);
		// 组体在真实会话里是嵌套视口：滚轮命中组体后先由内层消费，到边界才沿祖先链回到会话。
		var conversation = new ScrollViewer()
			.Content(new StackPanel().Children(new Border().Height(600), view.View, new Border().Height(600)))
			.AutoVerticalScroll().NoHorizontalScroll().SmoothScrolling();
		ConversationPerformanceTests.Layout(conversation, 500, 400);
		var run = Assert.Single(Find<Expander>(view.View), expander => HoverHints.Text(expander) == Strings.ExecutionExpandHint);
		run.IsExpanded = true;
		var group = Assert.Single(Find<Expander>(run.Content!), expander => HoverHints.Text(expander) == Strings.ExecutionGroupHint);
		group.IsExpanded = true;
		ConversationPerformanceTests.Layout(conversation, 500, 400);
		var viewport = Assert.Single(Find<ScrollViewer>(group.Content!));
		var body = SmoothScroll.For(viewport);
		var outer = SmoothScroll.For(conversation);

		// 内容装得下：组体没有可滚的余量，滚轮交给会话（该穿透就穿透）。
		Assert.Equal(0, body.MaxVerticalOffset);
		Raise(viewport, Wheel(0, -1));
		Assert.True(outer.IsAnimating, "a body with nothing to scroll must hand the wheel to the conversation");
		Assert.True(outer.TargetVerticalOffset > 0);
		outer.JumpToEnd();
		var settled = outer.MaxVerticalOffset;

		// 内容超高且不在边界：内层已经滚过，会话不动（不该穿透就不穿透）。
		var earlier = Find<Button>(group.Content!).Single(button => Find<TextBlock>(button)
			.Any(text => text.Text.StartsWith("显示更早", StringComparison.Ordinal)));
		ImagePreviewTests.Click(earlier);
		ConversationPerformanceTests.Layout(conversation, 500, 400);
		Assert.True(body.MaxVerticalOffset > 0);
		viewport.SetScrollOffsets(viewport.HorizontalOffset, Math.Min(20, body.MaxVerticalOffset));
		Raise(viewport, Wheel(0, -1));
		Assert.True(body.IsAnimating, "a body that really scrolled must keep the wheel");
		Assert.True(body.TargetVerticalOffset > 20);
		Assert.False(outer.IsAnimating);
		Assert.Equal(settled, outer.TargetVerticalOffset);

		// 到顶部后继续向上：该方向没有余量，滚轮再次回到会话。
		viewport.SetScrollOffsets(viewport.HorizontalOffset, 0);
		Raise(viewport, Wheel(0, 1));
		Assert.False(body.IsAnimating);
		var upward = outer.TargetVerticalOffset;
		Assert.True(upward < settled, "the conversation must receive the wheel once the body sits at its top edge");
		// 到底部后继续向下同理。
		viewport.SetScrollOffsets(viewport.HorizontalOffset, body.MaxVerticalOffset);
		Raise(viewport, Wheel(0, -1));
		Assert.False(body.IsAnimating);
		Assert.True(outer.TargetVerticalOffset > upward, "the conversation must receive the wheel once the body sits at its bottom edge");

		static MouseWheelEventArgs Wheel(double x, double y) =>
			new(new Point(0, 0), new Point(0, 0), new Vector(x, y));
		static void Raise(Element target, MouseWheelEventArgs args) =>
			typeof(UIElement).GetMethod("OnMouseWheel", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
				.Invoke(target, [args]);
		UiLifetime.Release(view.View);
	}

	[Fact]
	public void ComposerNoLongerContainsBalanceEntry()
	{
		using var session = ConversationPerformanceTests.CreateSession(1);
		using var tab = new SessionTab(session, [session.Model], "test");
		Assert.DoesNotContain(Find<Button>(tab.View), button => HoverHints.Text(button) == Strings.ProviderUsage);
	}
}
