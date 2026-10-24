using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
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
	public void BusyFlipsAndNewTurnsReuseTheProjectedPrefixWithoutFullPasses()
	{
		// 回合起止（busy 翻转）与新一轮用户消息只影响最后一轮的投影；长会话上一次
		// 全量要一百多毫秒，恰好落在 UI 线程的每回合起止帧上。
		var entries = new List<ChatEntry>();
		for (var i = 0; i < 120; i++)
		{
			entries.Add(User($"u{i}"));
			entries.Add(Answer($"a{i}", $"answer {i}"));
		}
		var projector = new ExecutionProcessProjector();
		AssertProjection(entries, false, projector);
		var fullAfterFirst = projector.FullPasses;
		Assert.Equal(1, fullAfterFirst);

		// 回合开始（busy 翻转）：只重投尾部。
		AssertProjection(entries, true, projector);
		Assert.Equal(fullAfterFirst, projector.FullPasses);

		// 流式尾部推进与新一轮用户消息：仍然只重投尾部。
		entries.Add(Answer("live", "partial"));
		AssertProjection(entries, true, projector);
		entries[^1] = Answer("live", "partial, more");
		AssertProjection(entries, true, projector);
		entries.Add(User("next"));
		entries.Add(Answer("live2", "second"));
		AssertProjection(entries, true, projector);
		// 回合结束：上一轮的运行态标记收敛为定稿，依旧只重投尾部。
		AssertProjection(entries, false, projector);
		Assert.Equal(fullAfterFirst, projector.FullPasses);
		Assert.True(projector.TailPasses >= 5);

		// 历史前缀被改写（分支/编辑）：缓存失效，必须回退全量。
		entries[1] = Answer("a0", "rewritten");
		AssertProjection(entries, false, projector);
		Assert.True(projector.FullPasses > fullAfterFirst);
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
		group.IsExpanded = true;
		ConversationPerformanceTests.Layout(timeline.View);
		// 组内步骤按视口虚拟化：展开后只有可见的摘要行，内容仍要等到行自身展开。
		var tool = Assert.Single(Find<Expander>(group.Content!), expander => HoverHints.Text(expander) == Strings.ToolExpandHint);
		Assert.Null(tool.Content);
		tool.IsExpanded = true;
		Assert.Contains(Find<MultiLineTextBox>(tool.Content!), text => text.Text == "output");
		run.IsExpanded = false;
		timeline.View.ScrollIntoView(rows.Length - 1);
		ConversationPerformanceTests.Layout(timeline.View);
		timeline.PruneRecycled();
		// 回到顶部：直接写偏移，避免虚拟化器在行高刚变化时丢掉一次 ScrollIntoView 请求。
		timeline.Scroll.SetScrollOffsets(0, 0);
		ConversationPerformanceTests.Layout(timeline.View);
		var restored = Assert.Single(Find<Expander>(timeline.View), expander => HoverHints.Text(expander) == Strings.ExecutionExpandHint);
		Assert.False(restored.IsExpanded);
		restored.IsExpanded = true;
		var restoredGroup = Assert.Single(Find<Expander>(restored.Content!), expander => HoverHints.Text(expander) == Strings.ExecutionGroupHint);
		Assert.True(restoredGroup.IsExpanded);
		ConversationPerformanceTests.Layout(timeline.View);
		Assert.True(Assert.Single(Find<Expander>(restoredGroup.Content!), expander => HoverHints.Text(expander) == Strings.ToolExpandHint).IsExpanded);
	}

	[Fact]
	public void UpdatingTheStreamingRunKeepsExpandedToolContentAndManualCollapse()
	{
		using var focus = new TextFocusScrollGuard(new ScrollViewer());
		ChatEntry Process(ChatEntry tool) => ExecutionProcess.Project([User("first"), tool, Answer("live", "partial") with { IsStreaming = true }], true)[1];
		var view = new ExecutionProcessView(Process(Tool("call", "output")), entry => entry.Role == ChatRole.Tool
			? new ToolMessageView(entry, focus).View : new AssistantMessageView(entry, _ => { }, focus).View);
		ConversationPerformanceTests.Layout(view.View);
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
		ConversationPerformanceTests.Layout(view.View);
		// 折叠时行被销毁，重新展开后按记住的步骤状态恢复展开。
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
	public void CollapsedProcessReleasesNestedScrollAnimations()
	{
		using var focus = new TextFocusScrollGuard(new ScrollViewer());
		var tools = Enumerable.Range(0, 40).Select(i => Tool($"call-{i}", $"output {i}")).ToArray();
		var process = ExecutionProcess.Project([User("first"), .. tools, Answer("final", "done")], false)[1];
		var view = new ExecutionProcessView(process, entry => new ToolMessageView(entry, focus).View);
		var run = Assert.Single(Find<Expander>(view.View), expander => HoverHints.Text(expander) == Strings.ExecutionExpandHint);
		run.IsExpanded = true;
		var group = Assert.Single(Find<Expander>(run.Content!), expander => HoverHints.Text(expander) == Strings.ExecutionGroupHint);
		group.IsExpanded = true;
		ConversationPerformanceTests.Layout(view.View);

		var viewport = Assert.Single(Find<ScrollViewer>(group.Content!));
		var animation = SmoothScroll.For(viewport);
		animation.ScrollBy(0, 2);
		Assert.True(animation.IsAnimating);
		// 折叠会销毁组体：滚动动画随控件释放一起停掉，不会在后台继续跑帧。
		group.IsExpanded = false;
		UiLifetime.Release(view.View);
		Assert.False(animation.IsAnimating);
	}

	[Fact]
	public void GroupStepsAreVirtualizedAndCollapsingReleasesThem()
	{
		using var focus = new TextFocusScrollGuard(new ScrollViewer());
		var tools = Enumerable.Range(0, 120).Select(i => Tool($"call-{i}", $"output {i}")).ToArray();
		var process = ExecutionProcess.Project([User("first"), .. tools, Answer("final", "done")], false)[1];
		var view = new ExecutionProcessView(process, entry => new ToolMessageView(entry, focus).View);
		ConversationPerformanceTests.Layout(view.View);

		var run = Assert.Single(Find<Expander>(view.View), expander => HoverHints.Text(expander) == Strings.ExecutionExpandHint);
		run.IsExpanded = true;
		var group = Assert.Single(Find<Expander>(run.Content!), expander => HoverHints.Text(expander) == Strings.ExecutionGroupHint);
		group.IsExpanded = true;
		ConversationPerformanceTests.Layout(view.View);

		// 步骤行按视口虚拟化：120 步只建可见的一小段，没有分页按钮。
		Assert.InRange(Find<Expander>(group.Content!).Count(expander => HoverHints.Text(expander) == Strings.ToolExpandHint), 1, 40);
		Assert.DoesNotContain(Find<Button>(group.Content!), button =>
			Find<TextBlock>(button).Any(text => text.Text.StartsWith("显示更早", StringComparison.Ordinal)));

		// 滚到组体顶部后最早的一步出现，行数仍然有界。
		var viewport = Assert.Single(Find<ScrollViewer>(group.Content!));
		viewport.SetScrollOffsets(0, 0);
		ConversationPerformanceTests.Layout(view.View);
		Assert.Contains(Find<Expander>(group.Content!), expander =>
			expander.Header is { } header && Find<TextBlock>(header).Any(text => text.Text == "read"));
		Assert.InRange(Find<Expander>(group.Content!).Count(expander => HoverHints.Text(expander) == Strings.ToolExpandHint), 1, 40);

		// 折叠即销毁：组体整块释放，不再保留不可见的步骤视图。
		group.IsExpanded = false;
		ConversationPerformanceTests.Layout(view.View);
		Assert.DoesNotContain(Find<Expander>(group.Content!), expander => HoverHints.Text(expander) == Strings.ToolExpandHint);
		group.IsExpanded = true;
		ConversationPerformanceTests.Layout(view.View);
		Assert.InRange(Find<Expander>(group.Content!).Count(expander => HoverHints.Text(expander) == Strings.ToolExpandHint), 1, 40);
		UiLifetime.Release(view.View);
	}

	[Fact]
	public void LargeGroupRealizesOnlyTheVisibleSteps()
	{
		using var focus = new TextFocusScrollGuard(new ScrollViewer());
		var tools = Enumerable.Range(0, 300).Select(i => Tool($"call-{i}", $"output {i}")).ToArray();
		var process = ExecutionProcess.Project([User("first"), .. tools, Answer("final", "done")], false)[1];
		var view = new ExecutionProcessView(process, entry => new ToolMessageView(entry, focus).View);
		ConversationPerformanceTests.Layout(view.View);

		var run = Assert.Single(Find<Expander>(view.View), expander => HoverHints.Text(expander) == Strings.ExecutionExpandHint);
		run.IsExpanded = true;
		var group = Assert.Single(Find<Expander>(run.Content!), expander => HoverHints.Text(expander) == Strings.ExecutionGroupHint);
		var started = System.Diagnostics.Stopwatch.StartNew();
		group.IsExpanded = true;
		ConversationPerformanceTests.Layout(view.View);
		started.Stop();

		Assert.InRange(Find<Expander>(group.Content!).Count(expander => HoverHints.Text(expander) == Strings.ToolExpandHint), 1, 40);
		// 展开 300 步的组体不能退化成整表建树。
		Assert.True(started.ElapsedMilliseconds < 500, $"expanding 300 steps took {started.ElapsedMilliseconds} ms");
		UiLifetime.Release(view.View);
	}

	[Fact]
	public void ExpandingAStepRowGrowsTheGroupScrollRange()
	{
		using var focus = new TextFocusScrollGuard(new ScrollViewer());
		var tools = Enumerable.Range(0, 40)
			.Select(i => Tool($"call-{i}", $"output {i}" + string.Concat(Enumerable.Repeat("\nline", 12)))).ToArray();
		var process = ExecutionProcess.Project([User("first"), .. tools, Answer("final", "done")], false)[1];
		var view = new ExecutionProcessView(process, entry => new ToolMessageView(entry, focus).View);
		var run = Assert.Single(Find<Expander>(view.View), expander => HoverHints.Text(expander) == Strings.ExecutionExpandHint);
		run.IsExpanded = true;
		var group = Assert.Single(Find<Expander>(run.Content!), expander => HoverHints.Text(expander) == Strings.ExecutionGroupHint);
		group.IsExpanded = true;
		ConversationPerformanceTests.Layout(view.View);

		var viewport = Assert.Single(Find<ScrollViewer>(group.Content!));
		var body = SmoothScroll.For(viewport);
		var before = body.MaxVerticalOffset;
		Assert.True(before > 0);

		// 展开第一行：行高变大，组体的可滚范围必须跟着长，否则新增内容滚不到。
		var tool = Find<Expander>(viewport).First(expander => HoverHints.Text(expander) == Strings.ToolExpandHint);
		tool.IsExpanded = true;
		ConversationPerformanceTests.Layout(view.View);

		Assert.True(body.MaxVerticalOffset > before,
			$"group scroll range did not grow after expanding a step: {before} -> {body.MaxVerticalOffset}");
		UiLifetime.Release(view.View);
	}

	[Fact]
	public void RestoringAnExpandedStepRowMeasuresItsFullHeight()
	{
		using var focus = new TextFocusScrollGuard(new ScrollViewer());
		var tools = Enumerable.Range(0, 40)
			.Select(i => Tool($"call-{i}", $"output {i}" + string.Concat(Enumerable.Repeat("\nline", 12)))).ToArray();
		var process = ExecutionProcess.Project([User("first"), .. tools, Answer("final", "done")], false)[1];
		var view = new ExecutionProcessView(process, entry => new ToolMessageView(entry, focus).View);
		var run = Assert.Single(Find<Expander>(view.View), expander => HoverHints.Text(expander) == Strings.ExecutionExpandHint);
		run.IsExpanded = true;
		var group = Assert.Single(Find<Expander>(run.Content!), expander => HoverHints.Text(expander) == Strings.ExecutionGroupHint);
		group.IsExpanded = true;
		ConversationPerformanceTests.Layout(view.View);

		var viewport = Assert.Single(Find<ScrollViewer>(group.Content!));
		var body = SmoothScroll.For(viewport);
		var expanded = Find<Expander>(viewport).First(expander => HoverHints.Text(expander) == Strings.ToolExpandHint);
		expanded.IsExpanded = true;
		ConversationPerformanceTests.Layout(view.View);
		var expandedMax = body.MaxVerticalOffset;
		Assert.True(expandedMax > 0);

		// 折叠会销毁组体；再次展开时行按记住的状态重建（恢复发生在虚拟化器的绑定/测量途中）。
		group.IsExpanded = false;
		ConversationPerformanceTests.Layout(view.View);
		group.IsExpanded = true;
		ConversationPerformanceTests.Layout(view.View);

		var restoredViewport = Assert.Single(Find<ScrollViewer>(group.Content!));
		var restoredBody = SmoothScroll.For(restoredViewport);
		var restored = Find<Expander>(restoredViewport).First(expander => HoverHints.Text(expander) == Strings.ToolExpandHint);
		Assert.True(restored.IsExpanded);
		var host = (FrameworkElement)restored.Parent!;
		// 行必须按展开后的内容测量，不能停在折叠时的一行摘要高度。
		Assert.True(host.Bounds.Height > 100, $"restored row kept the collapsed height: {host.Bounds.Height}");
		Assert.Equal(host.DesiredSize.Height, host.Bounds.Height, 1.0);
		// 恢复后的可滚范围仍然覆盖全部步骤（估算值随实测均值变化，只要求它没有塌回折叠态）。
		Assert.True(restoredBody.MaxVerticalOffset > 0);
		Assert.True(restoredBody.MaxVerticalOffset >= expandedMax * 0.5);
		UiLifetime.Release(view.View);
	}

	[Fact]
	public void TwoGroupsEachRefreshTheirOwnScrollRange()
	{
		using var focus = new TextFocusScrollGuard(new ScrollViewer());
		static ChatEntry Step(int index) => new()
		{
			Role = ChatRole.Tool, EntryId = $"step-{index}/result", ToolCallId = $"step-{index}", ToolName = "read",
			Text = $"output {index}" + string.Concat(Enumerable.Repeat("\nline", 10)),
		};
		// 中间回复把过程切成两组：每组的追帧状态必须各自独立。
		// 每组 12 步（超过 300 的组体高度）才能让「展开后滚动范围变大」这个断言成立。
		var process = ExecutionProcess.Project([
			User("first"), .. Enumerable.Range(0, 12).Select(Step), Answer("interim", "continue"),
			.. Enumerable.Range(12, 12).Select(Step), Answer("final", "done"),
		], false)[1];
		var view = new ExecutionProcessView(process, entry => new ToolMessageView(entry, focus).View);
		var run = Assert.Single(Find<Expander>(view.View), expander => HoverHints.Text(expander) == Strings.ExecutionExpandHint);
		run.IsExpanded = true;
		ConversationPerformanceTests.Layout(view.View);
		var groups = Find<Expander>(run.Content!).Where(expander => HoverHints.Text(expander) == Strings.ExecutionGroupHint).ToArray();
		Assert.Equal(2, groups.Length);
		foreach (var group in groups) group.IsExpanded = true;
		ConversationPerformanceTests.Layout(view.View);

		foreach (var group in groups)
		{
			var viewport = Assert.Single(Find<ScrollViewer>(group.Content!));
			var body = SmoothScroll.For(viewport);
			var before = body.MaxVerticalOffset;
			var row = Find<Expander>(viewport).First(expander => HoverHints.Text(expander) == Strings.ToolExpandHint);
			row.IsExpanded = true;
			ConversationPerformanceTests.Layout(view.View);
			Assert.True(body.MaxVerticalOffset > before,
				$"a group did not grow its scroll range after expanding a step: {before} -> {body.MaxVerticalOffset}");
		}
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
		ConversationPerformanceTests.Layout(view.View);
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
		// 连续竖线是组体的左列，与虚拟化列表同级。
		var line = Assert.Single(Find<Border>(view.View), border => border.Width == 1);
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
		// 组体是可滚视口：竖线覆盖可见区域，超出的步骤行会被裁剪，不再要求覆盖到最后一个图标。
		Assert.True(line.Bounds.Bottom >= viewport.Bounds.Bottom - 1);
		Assert.All(icons.Where(icon => viewport.Bounds.Contains(icon.Bounds.Position)),
			icon => Assert.True(icon.Bounds.Bottom <= line.Bounds.Bottom));
		UiLifetime.Release(view.View);
	}

	[Fact]
	public void GroupBodyOnlyConsumesTheWheelWhileItCanActuallyScroll()
	{
		using var focus = new TextFocusScrollGuard(new ScrollViewer());
		// 组体是嵌套视口：滚轮命中组体后先由内层消费，到边界才沿祖先链回到会话。
		// 步骤行虚拟化后，行数足够多才会让组体超出 300 的高度上限。
		(SmoothScroll Body, SmoothScroll Outer, ScrollViewer Viewport, ExecutionProcessView View) Fixture(int steps)
		{
			var tools = Enumerable.Range(0, steps).Select(i => Tool($"call-{i}", $"output {i}")).ToArray();
			var process = ExecutionProcess.Project([User("first"), .. tools, Answer("final", "done")], false)[1];
			var view = new ExecutionProcessView(process, entry => entry.Role == ChatRole.Tool
				? new ToolMessageView(entry, focus).View : new AssistantMessageView(entry, _ => { }, focus).View);
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
			return (SmoothScroll.For(viewport), SmoothScroll.For(conversation), viewport, view);
		}

		// 内容装得下：组体没有可滚的余量，滚轮交给会话（该穿透就穿透）。
		var small = Fixture(2);
		Assert.Equal(0, small.Body.MaxVerticalOffset);
		Raise(small.Viewport, Wheel(0, -1));
		Assert.True(small.Outer.IsAnimating, "a body with nothing to scroll must hand the wheel to the conversation");
		Assert.True(small.Outer.TargetVerticalOffset > 0);
		UiLifetime.Release(small.View.View);

		// 内容超高且不在边界：内层已经滚过，会话不动（不该穿透就不穿透）。
		var large = Fixture(60);
		Assert.True(large.Body.MaxVerticalOffset > 0);
		var outer = large.Outer;
		outer.JumpToEnd();
		var settled = outer.MaxVerticalOffset;
		large.Viewport.SetScrollOffsets(large.Viewport.HorizontalOffset, Math.Min(20, large.Body.MaxVerticalOffset));
		Raise(large.Viewport, Wheel(0, -1));
		Assert.True(large.Body.IsAnimating, "a body that really scrolled must keep the wheel");
		Assert.True(large.Body.TargetVerticalOffset > 20);
		Assert.False(outer.IsAnimating);
		Assert.Equal(settled, outer.TargetVerticalOffset);

		// 到顶部后继续向上：该方向没有余量，滚轮再次回到会话。
		large.Viewport.SetScrollOffsets(large.Viewport.HorizontalOffset, 0);
		Raise(large.Viewport, Wheel(0, 1));
		Assert.False(large.Body.IsAnimating);
		var upward = outer.TargetVerticalOffset;
		Assert.True(upward < settled, "the conversation must receive the wheel once the body sits at its top edge");
		// 到底部后继续向下同理。
		large.Viewport.SetScrollOffsets(large.Viewport.HorizontalOffset, large.Body.MaxVerticalOffset);
		Raise(large.Viewport, Wheel(0, -1));
		Assert.False(large.Body.IsAnimating);
		Assert.True(outer.TargetVerticalOffset > upward, "the conversation must receive the wheel once the body sits at its bottom edge");
		UiLifetime.Release(large.View.View);

		static MouseWheelEventArgs Wheel(double x, double y) =>
			new(new Point(0, 0), new Point(0, 0), new Vector(x, y));
		static void Raise(Element target, MouseWheelEventArgs args) =>
			typeof(UIElement).GetMethod("OnMouseWheel", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
				.Invoke(target, [args]);
	}

	[Fact]
	public void GroupBodyGrowsAndShrinksWithAStepExpansion()
	{
		using var focus = new TextFocusScrollGuard(new ScrollViewer());
		var tools = Enumerable.Range(0, 4)
			.Select(i => Tool($"call-{i}", $"output {i}\n" + string.Join('\n', Enumerable.Repeat("line", 12))))
			.ToArray();
		var process = ExecutionProcess.Project([User("first"), .. tools, Answer("final", "done")], false)[1];
		var view = new ExecutionProcessView(process, entry => new ToolMessageView(entry, focus).View);
		var run = Assert.Single(Find<Expander>(view.View), expander => HoverHints.Text(expander) == Strings.ExecutionExpandHint);
		run.IsExpanded = true;
		var group = Assert.Single(Find<Expander>(run.Content!), expander => HoverHints.Text(expander) == Strings.ExecutionGroupHint);
		group.IsExpanded = true;
		ConversationPerformanceTests.Layout(view.View);

		var viewport = Assert.Single(Find<ScrollViewer>(group.Content!));
		var collapsed = viewport.ViewportHeight;
		Assert.True(collapsed > 0);
		Assert.Equal(0, SmoothScroll.For(viewport).MaxVerticalOffset);

		var tool = Find<Expander>(viewport).First(expander => HoverHints.Text(expander) == Strings.ToolExpandHint);
		tool.IsExpanded = true;
		ConversationPerformanceTests.Layout(view.View);

		// 小组的组体由内容撑高：步骤展开后必须跟着长，而不是停在折叠时的几行高度。
		Assert.True(viewport.ViewportHeight > collapsed + 60,
			$"group body did not grow: {collapsed} -> {viewport.ViewportHeight}");
		Assert.True(viewport.ViewportHeight <= 302,
			$"group body exceeded its 300 DIP cap: {viewport.ViewportHeight}");
		Assert.True(SmoothScroll.For(viewport).MaxVerticalOffset > 0);

		// 收起后组体也要缩回内容高度，并把偏移夹进新的范围。
		tool.IsExpanded = false;
		ConversationPerformanceTests.Layout(view.View);
		Assert.True(Math.Abs(viewport.ViewportHeight - collapsed) <= 2,
			$"group body did not shrink back: {collapsed} -> {viewport.ViewportHeight}");
		Assert.Equal(0, SmoothScroll.For(viewport).MaxVerticalOffset);
		Assert.Equal(0, viewport.VerticalOffset);
		UiLifetime.Release(view.View);
	}

	[Fact]
	public void WheelOverTheGroupBodyStaysInsideUntilTheBodyCannotScroll()
	{
		using var focus = new TextFocusScrollGuard(new ScrollViewer());
		var tools = Enumerable.Range(0, 4)
			.Select(i => Tool($"call-{i}", $"output {i}\n" + string.Join('\n', Enumerable.Repeat("line", 12))))
			.ToArray();
		var process = ExecutionProcess.Project([User("first"), .. tools, Answer("final", "done")], false)[1];
		var view = new ExecutionProcessView(process, entry => new ToolMessageView(entry, focus).View);
		var conversation = new ScrollViewer()
			.Content(new StackPanel().Children(new Border().Height(500), view.View, new Border().Height(500)))
			.AutoVerticalScroll().NoHorizontalScroll().SmoothScrolling();
		ConversationPerformanceTests.Layout(conversation, 520, 380);
		var outer = SmoothScroll.For(conversation);
		conversation.SetScrollOffsets(0, 300);

		var run = Assert.Single(Find<Expander>(view.View), expander => HoverHints.Text(expander) == Strings.ExecutionExpandHint);
		run.IsExpanded = true;
		var group = Assert.Single(Find<Expander>(run.Content!), expander => HoverHints.Text(expander) == Strings.ExecutionGroupHint);
		group.IsExpanded = true;
		ConversationPerformanceTests.Layout(conversation, 520, 380);
		var viewport = Assert.Single(Find<ScrollViewer>(group.Content!));
		var body = SmoothScroll.For(viewport);
		var tool = Find<Expander>(viewport).First(expander => HoverHints.Text(expander) == Strings.ToolExpandHint);
		tool.IsExpanded = true;
		ConversationPerformanceTests.Layout(conversation, 520, 380);
		Assert.True(body.MaxVerticalOffset > 0);

		// 滚轮落在组体的空白/竖线区域（不是内层视口本身）时，也要先归组体。
		var bodyHost = (Element)group.Content!;
		outer.Stop();
		var outerBefore = outer.TargetVerticalOffset;
		RaiseWheel(bodyHost, 0, -1);
		Assert.True(body.IsAnimating, "the wheel over the group body leaked to the conversation");
		Assert.False(outer.IsAnimating);
		Assert.Equal(outerBefore, outer.TargetVerticalOffset);

		// 组体装不下任何内容时（步骤全部收起），同一位置的滚轮应交给会话。
		tool.IsExpanded = false;
		ConversationPerformanceTests.Layout(conversation, 520, 380);
		Assert.Equal(0, body.MaxVerticalOffset);
		body.Stop();
		var unhandled = RaiseWheel(bodyHost, 0, -1);
		// 不消费即表示这一格会继续沿祖先链交给会话，组体不会把滚轮锁住。
		Assert.False(unhandled.Handled);
		Assert.False(body.IsAnimating);
		UiLifetime.Release(view.View);
	}

	[Fact]
	public void GrowingPastTheStackLimitKeepsTheGroupBodyCapped()
	{
		using var focus = new TextFocusScrollGuard(new ScrollViewer());
		var first = Enumerable.Range(0, 24).Select(i => Tool($"call-{i}", $"output {i}")).ToArray();
		var process = ExecutionProcess.Project([User("first"), .. first, Answer("final", "done")], false)[1];
		var view = new ExecutionProcessView(process, entry => new ToolMessageView(entry, focus).View);
		ConversationPerformanceTests.Layout(view.View);
		var run = Assert.Single(Find<Expander>(view.View), expander => HoverHints.Text(expander) == Strings.ExecutionExpandHint);
		run.IsExpanded = true;
		var group = Assert.Single(Find<Expander>(run.Content!), expander => HoverHints.Text(expander) == Strings.ExecutionGroupHint);
		group.IsExpanded = true;
		ConversationPerformanceTests.Layout(view.View);
		var stackViewport = Assert.Single(Find<ScrollViewer>(group.Content!));
		var capped = stackViewport.ViewportHeight;
		Assert.InRange(capped, 0, 302);
		stackViewport.SetScrollOffsets(0, 50);
		ConversationPerformanceTests.Layout(view.View);
		Assert.Equal(50, stackViewport.VerticalOffset, 1);

		// 流式过程中步骤数越过栈上限：切换成虚拟化列表，组体仍保持限高，不出现高度跳变。
		var next = ExecutionProcess.Project([
			User("first"), .. Enumerable.Range(0, 25).Select(i => Tool($"call-{i}", $"output {i}")), Answer("final", "done"),
		], false)[1];
		view.Update(next);
		ConversationPerformanceTests.Layout(view.View);
		var switched = Assert.Single(Find<ScrollViewer>(group.Content!));
		Assert.InRange(switched.ViewportHeight, capped - 2, capped + 2);
		Assert.True(SmoothScroll.For(switched).MaxVerticalOffset > 0);
		// 换 presenter 不应把组体弹回顶部。
		Assert.Equal(50, switched.VerticalOffset, 1);
		UiLifetime.Release(view.View);
	}

	[Fact]
	public void ExpandingAStepAfterAStreamingRebindKeepsTheRowCallbackValid()
	{
		using var focus = new TextFocusScrollGuard(new ScrollViewer());
		var tools = Enumerable.Range(0, 4)
			.Select(i => Tool($"call-{i}", $"output {i}\n" + string.Join('\n', Enumerable.Repeat("line", 4))))
			.ToArray();
		var process = ExecutionProcess.Project([User("first"), .. tools, Answer("final", "done")], false)[1];
		var view = new ExecutionProcessView(process, entry => new ToolMessageView(entry, focus).View);
		var run = Assert.Single(Find<Expander>(view.View), expander => HoverHints.Text(expander) == Strings.ExecutionExpandHint);
		run.IsExpanded = true;
		var group = Assert.Single(Find<Expander>(run.Content!), expander => HoverHints.Text(expander) == Strings.ExecutionGroupHint);
		group.IsExpanded = true;
		ConversationPerformanceTests.Layout(view.View);
		var viewport = Assert.Single(Find<ScrollViewer>(group.Content!));

		// 流式更新会让栈布局重新绑定每一行：回调必须捕获对应的行，而不是循环结束后越界的索引。
		var changed = Enumerable.Range(0, 4)
			.Select(i => Tool($"call-{i}", $"changed {i}\n" + string.Join('\n', Enumerable.Repeat("line", 4))))
			.ToArray();
		view.Update(ExecutionProcess.Project([User("first"), .. changed, Answer("final", "done")], false)[1]);
		ConversationPerformanceTests.Layout(view.View);

		var tool = Find<Expander>(viewport).First(expander => HoverHints.Text(expander) == Strings.ToolExpandHint);
		tool.IsExpanded = true;
		ConversationPerformanceTests.Layout(view.View);
		Assert.True(tool.IsExpanded);
		UiLifetime.Release(view.View);
	}

	[Fact]
	public void ExpandingAStreamingThinkingStepFollowsTheGroupBodyTail()
	{
		using var focus = new TextFocusScrollGuard(new ScrollViewer());
		var thinkingText = string.Join('\n', Enumerable.Repeat("思考中的一行内容", 60));
		var process = ExecutionProcess.Project([
			User("first"),
			new ChatEntry { Role = ChatRole.Assistant, EntryId = "think", Text = string.Empty, Thinking = thinkingText, IsThinking = true, IsStreaming = true },
		], true)[1];
		var view = new ExecutionProcessView(process, entry => new AssistantMessageView(entry, _ => { }, focus).View);
		ConversationPerformanceTests.Layout(view.View);
		var run = Assert.Single(Find<Expander>(view.View), expander => HoverHints.Text(expander) == Strings.ExecutionExpandHint);
		run.IsExpanded = true;
		var group = Assert.Single(Find<Expander>(run.Content!), expander => HoverHints.Text(expander) == Strings.ExecutionGroupHint);
		group.IsExpanded = true;
		ConversationPerformanceTests.Layout(view.View);
		var viewport = Assert.Single(Find<ScrollViewer>(group.Content!));
		var body = SmoothScroll.For(viewport);
		var thinking = Assert.Single(Find<Expander>(viewport));

		thinking.IsExpanded = true;
		ConversationPerformanceTests.Layout(view.View);
		Assert.True(body.IsFollowingGrowth, "expanding streaming thinking should make the group body follow its tail");

		// 思考继续增长：组体保持在新的末尾，而不是停在展开时的位置。
		view.Update(ExecutionProcess.Project([
			User("first"),
			new ChatEntry { Role = ChatRole.Assistant, EntryId = "think", Text = string.Empty, Thinking = thinkingText + "\n" + thinkingText, IsThinking = true, IsStreaming = true },
		], true)[1]);
		ConversationPerformanceTests.Layout(view.View);
		Assert.True(body.IsFollowingGrowth);
		Assert.InRange(body.MaxVerticalOffset - viewport.VerticalOffset, 0, 1);

		thinking.IsExpanded = false;
		Assert.False(body.IsFollowingGrowth);
		UiLifetime.Release(view.View);
	}

	[Fact]
	public void ExpandingAStandaloneStreamingThinkingBlockFollowsTheConversation()
	{
		var host = new Border();
		var conversation = new ScrollViewer().Content(host)
			.AutoVerticalScroll().NoHorizontalScroll().SmoothScrolling();
		using var focus = new TextFocusScrollGuard(conversation);
		var followed = false;
		focus.FollowConversation = () => followed = true;
		var entry = new ChatEntry
		{
			Role = ChatRole.Assistant, EntryId = "live", Timestamp = 1, Text = string.Empty,
			Thinking = string.Join('\n', Enumerable.Repeat("思考内容", 40)), IsThinking = true, IsStreaming = true,
		};
		var view = new AssistantMessageView(entry, _ => { }, focus);
		host.Child(view.View);
		ConversationPerformanceTests.Layout(conversation, 520, 380);
		var thinking = Assert.Single(Find<Expander>(view.View));
		thinking.IsExpanded = true;
		Assert.True(followed, "expanding a streaming thinking block should ask the conversation to follow");
		Assert.True(thinking.IsExpanded);
		view.Dispose();
	}

	[Fact]
	public void ExpandingAStepKeepsTheReadingPositionAtItsPreviewRow()
	{
		using var focus = new TextFocusScrollGuard(new ScrollViewer());
		var tools = Enumerable.Range(0, 6)
			.Select(i => Tool($"call-{i}", $"output {i}\n" + string.Join('\n', Enumerable.Repeat("line", 12))))
			.ToArray();
		var process = ExecutionProcess.Project([User("first"), .. tools, Answer("final", "done")], false)[1];
		var view = new ExecutionProcessView(process, entry => new ToolMessageView(entry, focus).View);
		var run = Assert.Single(Find<Expander>(view.View), expander => HoverHints.Text(expander) == Strings.ExecutionExpandHint);
		run.IsExpanded = true;
		var group = Assert.Single(Find<Expander>(run.Content!), expander => HoverHints.Text(expander) == Strings.ExecutionGroupHint);
		group.IsExpanded = true;
		ConversationPerformanceTests.Layout(view.View);
		var viewport = Assert.Single(Find<ScrollViewer>(group.Content!));
		viewport.SetScrollOffsets(0, 0);
		ConversationPerformanceTests.Layout(view.View);

		var tool = Find<Expander>(viewport).First(expander => HoverHints.Text(expander) == Strings.ToolExpandHint);
		var previewTop = tool.Bounds.Y;
		tool.IsExpanded = true;
		ConversationPerformanceTests.Layout(view.View);

		// 展开的是第一行的预览；进度必须仍停在这一行，不能自动滚到工具输出的末尾。
		Assert.Equal(0, viewport.VerticalOffset, 1);
		Assert.InRange(tool.Bounds.Y, previewTop - 1, previewTop + 1);
		Assert.True(tool.Bounds.Y >= viewport.Bounds.Y - 1);
		Assert.True(SmoothScroll.For(viewport).MaxVerticalOffset > 0);
		UiLifetime.Release(view.View);
	}

	[Fact]
	public void ExpandingAStepInAVirtualizedGroupAlsoKeepsThePreviewRow()
	{
		using var focus = new TextFocusScrollGuard(new ScrollViewer());
		var tools = Enumerable.Range(0, 30)
			.Select(i => Tool($"call-{i}", $"output {i}\n" + string.Join('\n', Enumerable.Repeat("line", 12))))
			.ToArray();
		var process = ExecutionProcess.Project([User("first"), .. tools, Answer("final", "done")], false)[1];
		var view = new ExecutionProcessView(process, entry => new ToolMessageView(entry, focus).View);
		var run = Assert.Single(Find<Expander>(view.View), expander => HoverHints.Text(expander) == Strings.ExecutionExpandHint);
		run.IsExpanded = true;
		var group = Assert.Single(Find<Expander>(run.Content!), expander => HoverHints.Text(expander) == Strings.ExecutionGroupHint);
		group.IsExpanded = true;
		ConversationPerformanceTests.Layout(view.View);
		var viewport = Assert.Single(Find<ScrollViewer>(group.Content!));
		viewport.SetScrollOffsets(0, 0);
		ConversationPerformanceTests.Layout(view.View);

		var tool = Find<Expander>(viewport).First(expander => HoverHints.Text(expander) == Strings.ToolExpandHint);
		var previewTop = tool.Bounds.Y;
		tool.IsExpanded = true;
		ConversationPerformanceTests.Layout(view.View);

		// 虚拟化组也要停在被点击的预览行；不能因为行高缓存修正而滚到展开内容末尾。
		Assert.InRange(tool.Bounds.Y, previewTop - 1, previewTop + 1);
		Assert.True(tool.Bounds.Y >= viewport.Bounds.Y - 1);
		UiLifetime.Release(view.View);
	}



	[Fact]
	public void ExpandingAToolRowThatIsReboundByTheVirtualizerKeepsItsPreviewPinned()
	{
		// 展开步骤后，同一步骤在下一帧落盘：虚拟化器会 Replace 这一行并重新绑定宿主。
		// 预览行仍要停在原屏幕位置。只记行宿主引用的旧实现在宿主被换掉后立刻丢掉锚点，
		// 表现为偶发的「展开步骤时组体进度没钉在预览行」。
		using var focus = new TextFocusScrollGuard(new ScrollViewer());
		var tools = Enumerable.Range(0, 30).Select(i => Tool($"call-{i}", $"output {i}")).ToArray();
		var process = ExecutionProcess.Project([User("first"), .. tools], true)[1];
		var view = new ExecutionProcessView(process, entry => new ToolMessageView(entry, focus).View);
		ConversationPerformanceTests.Layout(view.View);

		var run = Assert.Single(Find<Expander>(view.View), expander => HoverHints.Text(expander) == Strings.ExecutionExpandHint);
		var group = Assert.Single(Find<Expander>(run.Content!), expander => HoverHints.Text(expander) == Strings.ExecutionGroupHint);
		var viewport = Assert.Single(Find<ScrollViewer>(group.Content!));
		// 离开末尾：这是读者的阅读位置，展开步骤不能把人拽到末尾。
		viewport.SetScrollOffsets(0, 240);
		ConversationPerformanceTests.Layout(view.View);

		var row = Find<ItemContainer>(viewport)
			.First(container => container.Bounds.Y >= viewport.Bounds.Y &&
				container.Bounds.Bottom <= viewport.Bounds.Bottom &&
				Find<Expander>(container).Any(expander => HoverHints.Text(expander) == Strings.ToolExpandHint));
		var position = row.Index;
		var tool = Find<Expander>(row).First(expander => HoverHints.Text(expander) == Strings.ToolExpandHint);
		var previewTop = row.Bounds.Y - viewport.Bounds.Y;
		tool.IsExpanded = true;
		ConversationPerformanceTests.Layout(view.View);
		Assert.InRange(row.Bounds.Y - viewport.Bounds.Y, previewTop - 1, previewTop + 1);

		// 该步骤的输出在这一帧到达：只 Replace 这一行，其余成员保持原对象。
		var members = process.ProcessEntries.ToArray();
		var entry = members[position];
		members[position] = entry with { Text = "输出已落盘\n" + string.Join('\n', Enumerable.Repeat("line", 8)) };
		view.Update(process with { ProcessEntries = [.. members] });
		ConversationPerformanceTests.Layout(view.View);
		view.SyncBodyMetrics();
		ConversationPerformanceTests.Layout(view.View);

		var rebound = Find<ItemContainer>(viewport).First(container => container.Index == position);
		Assert.True(Find<Expander>(rebound).Any(expander => expander.IsExpanded),
			$"row {position} must stay expanded: offset={viewport.VerticalOffset:0.#}");
		Assert.InRange(rebound.Bounds.Y - viewport.Bounds.Y, previewTop - 1, previewTop + 1);
		UiLifetime.Release(view.View);
	}

	[Fact]
	public void ExpandingAToolRowWhileParkedAtTheTailPinsItsPreviewRow()
	{
		// 组体滚动条正好停在末尾（几何上属于「跟随最新」）时点开一个靠上的工具步骤：
		// 阅读位置必须留在预览行。旧实现把「正在跟随」当成不钉锚的理由，展开的正文
		// 会把视口一路顶到新末尾，表现为偶发的「展开步骤行时组体进度没固定在步骤行预览」。
		using var focus = new TextFocusScrollGuard(new ScrollViewer());
		var tools = Enumerable.Range(0, 30)
			.Select(i => Tool($"call-{i}", $"output {i}\n" + string.Join('\n', Enumerable.Repeat("line", 12))))
			.ToArray();
		var process = ExecutionProcess.Project([User("first"), .. tools], true)[1];
		var view = new ExecutionProcessView(process, entry => new ToolMessageView(entry, focus).View);
		ConversationPerformanceTests.Layout(view.View);

		var run = Assert.Single(Find<Expander>(view.View), expander => HoverHints.Text(expander) == Strings.ExecutionExpandHint);
		var group = Assert.Single(Find<Expander>(run.Content!), expander => HoverHints.Text(expander) == Strings.ExecutionGroupHint);
		var viewport = Assert.Single(Find<ScrollViewer>(group.Content!));
		var smooth = SmoothScroll.For(viewport);
		viewport.SetScrollOffsets(0, smooth.MaxVerticalOffset);
		ConversationPerformanceTests.Layout(view.View);
		Assert.True(smooth.IsFollowingGrowth, "the body parked at its end follows growth");

		var row = Find<ItemContainer>(viewport)
			.Where(container => container.Bounds.Y > viewport.Bounds.Y &&
				container.Bounds.Bottom <= viewport.Bounds.Bottom &&
				Find<Expander>(container).Any(expander => HoverHints.Text(expander) == Strings.ToolExpandHint))
			.OrderBy(container => container.Bounds.Y)
			.First();
		var position = row.Index;
		var previewTop = row.Bounds.Y - viewport.Bounds.Y;
		var reading = viewport.VerticalOffset;
		Find<Expander>(row).First(expander => HoverHints.Text(expander) == Strings.ToolExpandHint).IsExpanded = true;
		ConversationPerformanceTests.Layout(view.View);

		Assert.False(smooth.IsFollowingGrowth, "expanding a tool step is a reading action");
		Assert.True(smooth.MaxVerticalOffset > reading + 20,
			$"the expanded body must have grown: {reading:0.#} -> {smooth.MaxVerticalOffset:0.#}");
		var expanded = Find<ItemContainer>(viewport).First(container => container.Index == position);
		Assert.InRange(expanded.Bounds.Y - viewport.Bounds.Y, previewTop - 1, previewTop + 1);
		Assert.True(viewport.VerticalOffset <= reading + 1,
			$"the reading position was dragged toward the tail: {reading:0.#} -> {viewport.VerticalOffset:0.#}");
		UiLifetime.Release(view.View);
	}

	[Fact]
	public void StreamingLatestGroupFollowsItsTailAndToolExpansionStopsIt()
	{
		using var focus = new TextFocusScrollGuard(new ScrollViewer());
		var tools = Enumerable.Range(0, 30).Select(i => Tool($"call-{i}", $"output {i}")).ToArray();
		var process = ExecutionProcess.Project([User("first"), .. tools], true)[1];
		var view = new ExecutionProcessView(process, entry => new ToolMessageView(entry, focus).View);
		ConversationPerformanceTests.Layout(view.View);

		var run = Assert.Single(Find<Expander>(view.View), expander => HoverHints.Text(expander) == Strings.ExecutionExpandHint);
		var group = Assert.Single(Find<Expander>(run.Content!), expander => HoverHints.Text(expander) == Strings.ExecutionGroupHint);
		var viewport = Assert.Single(Find<ScrollViewer>(group.Content!));
		var body = SmoothScroll.For(viewport);
		// 流式最新组自动展开时跟住最新步骤，和 PiDeck 的自动通道一致。
		Assert.True(body.IsFollowingGrowth);
		Assert.InRange(body.MaxVerticalOffset - viewport.VerticalOffset, 0, 1);

		// 用户展开一个工具步骤是阅读行为：停止跟随，进度留在预览行，不滚到输出末尾。
		var tool = Find<Expander>(viewport)
			.Last(expander => HoverHints.Text(expander) == Strings.ToolExpandHint &&
				expander.Bounds.Y >= viewport.Bounds.Y && expander.Bounds.Y < viewport.Bounds.Bottom);
		var previewTop = tool.Bounds.Y;
		tool.IsExpanded = true;
		ConversationPerformanceTests.Layout(view.View);
		Assert.False(body.IsFollowingGrowth);
		Assert.True(tool.Bounds.Y >= viewport.Bounds.Y - 1);
		Assert.InRange(tool.Bounds.Y, previewTop - 1, previewTop + 1);
		UiLifetime.Release(view.View);
	}

	[Fact]
	public void ExpandingStreamingThinkingInAGroupAlsoFollowsTheConversation()
	{
		var host = new Border();
		var conversation = new ScrollViewer().Content(host)
			.AutoVerticalScroll().NoHorizontalScroll().SmoothScrolling();
		using var focus = new TextFocusScrollGuard(conversation);
		var followed = false;
		focus.FollowConversation = () => followed = true;
		var thinkingText = string.Join('\n', Enumerable.Repeat("思考中的一行内容", 60));
		var process = ExecutionProcess.Project([
			User("first"),
			new ChatEntry { Role = ChatRole.Assistant, EntryId = "think", Text = string.Empty, Thinking = thinkingText, IsThinking = true, IsStreaming = true },
		], true)[1];
		var view = new ExecutionProcessView(process, entry => new AssistantMessageView(entry, _ => { }, focus).View);
		host.Child(view.View);
		ConversationPerformanceTests.Layout(conversation, 520, 380);

		var thinking = Find<Expander>(view.View).First(expander =>
			expander.Header is { } header && Find<TextBlock>(header).Any(text => text.Text == Strings.ThinkingActive));
		thinking.IsExpanded = true;
		Assert.True(followed, "expanding live thinking inside a group should also make the conversation follow");
		UiLifetime.Release(view.View);
	}

	[Fact]
	public void ReopeningAGroupRestoresExpandedThinkingWithoutYankingToTheTail()
	{
		using var focus = new TextFocusScrollGuard(new ScrollViewer());
		var thinkingText = string.Join('\n', Enumerable.Repeat("思考中的一行内容", 60));
		var process = ExecutionProcess.Project([
			User("first"),
			new ChatEntry { Role = ChatRole.Assistant, EntryId = "think", Text = string.Empty, Thinking = thinkingText, IsThinking = true, IsStreaming = true },
		], true)[1];
		var view = new ExecutionProcessView(process, entry => new AssistantMessageView(entry, _ => { }, focus).View);
		ConversationPerformanceTests.Layout(view.View);
		var run = Assert.Single(Find<Expander>(view.View), expander => HoverHints.Text(expander) == Strings.ExecutionExpandHint);
		var group = Assert.Single(Find<Expander>(run.Content!), expander => HoverHints.Text(expander) == Strings.ExecutionGroupHint);
		var viewport = Assert.Single(Find<ScrollViewer>(group.Content!));
		var thinking = Assert.Single(Find<Expander>(viewport));
		thinking.IsExpanded = true;
		ConversationPerformanceTests.Layout(view.View);
		Assert.True(SmoothScroll.For(viewport).IsFollowingGrowth);

		// 折叠组体销毁行；再展开时按记录恢复思考的展开状态。恢复不是用户展开，
		// 不能因为这个动作把读者从组体当前位置拽到思考末尾。
		group.IsExpanded = false;
		ConversationPerformanceTests.Layout(view.View);
		group.IsExpanded = true;
		ConversationPerformanceTests.Layout(view.View);
		var restoredViewport = Assert.Single(Find<ScrollViewer>(group.Content!));
		var restoredThinking = Assert.Single(Find<Expander>(restoredViewport));
		Assert.True(restoredThinking.IsExpanded);
		Assert.False(SmoothScroll.For(restoredViewport).IsFollowingGrowth);
		UiLifetime.Release(view.View);
	}

	[Fact]
	public void CollapsingTheLastStepDoesNotMoveTheOuterConversation()
	{
		var host = new Border();
		var conversation = new ScrollViewer().Content(host)
			.AutoVerticalScroll().NoHorizontalScroll().SmoothScrolling();
		using var conversationScroll = new ConversationScroll(conversation, _ => { });
		using var focus = new TextFocusScrollGuard(conversation);
		focus.PauseFollowing = conversationScroll.Browse;
		var tools = Enumerable.Range(0, 3)
			.Select(i => Tool($"call-{i}", $"output {i}\n" + string.Join('\n', Enumerable.Repeat("line", 12))))
			.ToArray();
		var process = ExecutionProcess.Project([User("first"), .. tools, Answer("final", "done")], false)[1];
		var view = new ExecutionProcessView(process, entry => new ToolMessageView(entry, focus).View);
		focus.LayoutInvalidated = fold => view.View.InvalidateMeasure();
		host.Child(new StackPanel().Children(new Border().Height(900), view.View, new Border().Height(600)));
		ConversationPerformanceTests.Layout(conversation, 700, 500);

		var run = Assert.Single(Find<Expander>(view.View), expander => HoverHints.Text(expander) == Strings.ExecutionExpandHint);
		run.IsExpanded = true;
		var group = Assert.Single(Find<Expander>(run.Content!), expander => HoverHints.Text(expander) == Strings.ExecutionGroupHint);
		group.IsExpanded = true;
		ConversationPerformanceTests.Layout(conversation, 700, 500);
		var viewport = Assert.Single(Find<ScrollViewer>(group.Content!));
		SmoothScroll.For(viewport).JumpToEnd();
		ConversationPerformanceTests.Layout(conversation, 700, 500);

		var tool = Find<Expander>(viewport).Last(expander => HoverHints.Text(expander) == Strings.ToolExpandHint);
		tool.IsExpanded = true;
		ConversationPerformanceTests.Layout(conversation, 700, 500);
		SmoothScroll.For(viewport).ScrollBy(0, 2);
		ConversationPerformanceTests.Layout(conversation, 700, 500);
		conversation.SetScrollOffsets(0, 900);
		ConversationPerformanceTests.Layout(conversation, 700, 500);

		var outer = conversation.VerticalOffset;
		var preview = tool.Bounds.Y;
		// 组内步骤的折叠只暂停外层跟随，不建立外层锚点：外部会话进度保持不变。
		focus.PauseFollowing!();
		tool.IsExpanded = false;
		ConversationPerformanceTests.Layout(conversation, 700, 500);

		Assert.Equal(outer, conversation.VerticalOffset, 1);
		Assert.InRange(tool.Bounds.Y, preview - 1, preview + 1);
		Assert.False(conversationScroll.Following);
		UiLifetime.Release(view.View);
	}

	private static MouseWheelEventArgs RaiseWheel(Element target, double horizontal, double vertical)
	{
		var args = new MouseWheelEventArgs(new Point(0, 0), new Point(0, 0), new Vector(horizontal, vertical));
		typeof(UIElement).GetMethod("OnMouseWheel", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
			.Invoke(target, [args]);
		return args;
	}

	[Fact]
	public void ComposerNoLongerContainsBalanceEntry()
	{
		using var session = ConversationPerformanceTests.CreateSession(1);
		using var tab = new SessionTab(session, [session.Model], "test");
		Assert.DoesNotContain(Find<Button>(tab.View), button => HoverHints.Text(button) == Strings.ProviderUsage);
	}
}
