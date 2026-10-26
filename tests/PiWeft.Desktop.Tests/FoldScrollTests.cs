using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using System.Reflection;
using Xunit;

namespace PiWeft.Desktop.Tests;

[Collection("mewui-controls")]
public sealed class FoldScrollTests
{
	// 指针折叠的锚点保持在虚拟化时间线上由 VirtualizedProcessDoesNotJumpToTheAnswerWhenExpandedAtTheBottom 覆盖；
	// 这里只保留键盘（Space）路径。
	[Fact]
	public void KeyboardFoldStopsEndFollowingAndKeepsItsHeaderAtTheSameReadingPosition()
	{
		var fold = new Expander().Header(new TextBlock().Text("process")).IsExpanded(false)
			.Content(new Border().Height(800));
		var body = new StackPanel().Children(new Border().Height(1000), fold, new Border().Height(100));
		var scroll = new ScrollViewer().Content(body).AutoVerticalScroll().NoHorizontalScroll();
		using var guard = new TextFocusScrollGuard(scroll);
		using var conversation = new ConversationScroll(scroll, _ => { });
		guard.TrackFold(fold);
		ConversationPerformanceTests.Layout(scroll, 500, 400);
		Assert.True(conversation.Following);
		var y = fold.Bounds.Y;
		// Simulate the reading-intent callback immediately before the native toggle.
		using var inputGuard = new TextFocusScrollGuard(scroll, conversation.PreserveAnchor);
		inputGuard.TrackFold(fold);
		Toggle(fold, keyboard: true);
		ConversationPerformanceTests.Layout(scroll, 500, 400);
		conversation.RestoreAnchor();
		ConversationPerformanceTests.Layout(scroll, 500, 400);
		Assert.True(fold.IsExpanded);
		Assert.False(conversation.Following);
		Assert.InRange(Math.Abs(fold.Bounds.Y - y), 0, 1);
		Toggle(fold, keyboard: true);
		ConversationPerformanceTests.Layout(scroll, 500, 400);
		conversation.RestoreAnchor();
		ConversationPerformanceTests.Layout(scroll, 500, 400);
		Assert.False(fold.IsExpanded);
		Assert.False(conversation.Following);
		Assert.InRange(Math.Abs(fold.Bounds.Y - y), 0, 1);
		UiLifetime.Release(scroll);
	}

	[Fact]
	public void ScrollToLatestFromFarUpReachesTheMeasuredEnd()
	{
		using var session = ConversationPerformanceTests.CreateSession(300);
		using var tab = new SessionTab(session, [session.Model], "latest");
		ConversationPerformanceTests.Layout(tab);
		var scroll = VisualTree.Find<ScrollViewer>(tab.View).First();
		using var conversation = new ConversationScroll(scroll, _ => { });
		var smooth = SmoothScroll.For(scroll);

		// 停在很早的位置：远端行没被测量，末端只是估算。
		scroll.SetScrollOffsets(0, 0);
		ConversationPerformanceTests.Layout(tab);
		Assert.True(smooth.MaxVerticalOffset > 1000);

		conversation.ScrollToLatest();
		// 收敛判据是「动画停下且真的到末尾」，不是「跑够 N 帧就算完」：跟底每帧都会重测行高，
		// 目标 MaxVerticalOffset 会被不断推后，动画随之延长；而 16ms 只是虚拟时间的一帧，
		// 并行跑整套测试时每帧推进的行数会变。固定 40 帧在满负载下不够（实测会稳定变红），
		// 所以按虚拟时间给足预算，收敛即退出。
		for (var frame = 0; frame < 400; frame++)
		{
			Thread.Sleep(2);
			AnimationFrames.Advance(16);
			ConversationPerformanceTests.Layout(tab);
			// 应用里这一步由 Render 优先级的回调完成；测试里手动驱动同样的收敛。
			conversation.FollowAfterLayout();
			if (!smooth.IsAnimating && Math.Abs(smooth.MaxVerticalOffset - scroll.VerticalOffset) <= 1) break;
		}

		Assert.True(conversation.Following);
		// 跟底状态本身用 4 DIP 判定「在末尾」；增量 Markdown 的最后一行在动画收尾后
		// 仍可能再测出 1–2 DIP 的高度差，不应把它当成没有到达末尾。
		Assert.Equal(smooth.MaxVerticalOffset, scroll.VerticalOffset, 4.0);
	}

	[Fact]
	public void VirtualizedProcessDoesNotJumpToTheAnswerWhenExpandedAtTheBottom()
	{
		ConversationScroll? conversation = null;
		TextFocusScrollGuard? guard = null;
		using var timeline = new ConversationTimeline(entry =>
		{
			if (entry.Role == ChatRole.Process)
				return new ExecutionProcessView(entry, _ => new Border().Height(100), guard).View;
			return new Border().Height(100).Child(new TextBlock().Text(entry.Text));
		});
		guard = new TextFocusScrollGuard(timeline.Scroll, anchor => conversation!.PreserveAnchor(anchor));
		conversation = new ConversationScroll(timeline.Scroll, _ => { });
		using (guard)
		using (conversation)
		{
			var rows = Enumerable.Range(0, 50).Select(i => new ChatEntry { Role = ChatRole.User, Text = $"history {i}", EntryId = $"history-{i}" }).ToList();
			rows.Add(new ChatEntry { Role = ChatRole.Process, Text = "", EntryId = "process", ProcessEntries = Enumerable.Range(0, 8)
				.Select(i => new ChatEntry { Role = ChatRole.Assistant, Text = $"step {i}", EntryId = $"step-{i}" }).ToArray() });
			rows.Add(new ChatEntry { Role = ChatRole.Assistant, Text = "answer", EntryId = "answer" });
			timeline.SetEntries(rows);
			ConversationPerformanceTests.Layout(timeline.View, 500, 400);
			var fold = VisualTree.Find<Expander>(timeline.View).Single();
			var y = fold.Bounds.Y;
			var offset = timeline.Scroll.VerticalOffset;
			conversation.PreserveAnchor(fold);
			Toggle(fold, false);
			ConversationPerformanceTests.Layout(timeline.View, 500, 400);
			conversation.RestoreAnchor();
			ConversationPerformanceTests.Layout(timeline.View, 500, 400);
			Assert.True(Math.Abs(fold.Bounds.Y - y) <= 1,
				$"Header moved from {y} to {fold.Bounds.Y}; offset {offset} -> {timeline.Scroll.VerticalOffset}; parent {fold.Parent?.GetType().Name}; visible folds {VisualTree.Find<Expander>(timeline.View).Count()}");
			Assert.False(conversation.Following);
		}
	}

	[Fact]
	public void WheelOverGroupTextScrollsTheGroupBodyBeforeTheConversation()
	{
		// 组体里的思考文本/工具输出是可选择的文字控件：它们曾把滚轮直接转给会话，
		// 于是组体永远滚不动、整段会话跟着动（滚动穿透）。
		var text = MessageText.Create(string.Join('\n', Enumerable.Range(0, 60).Select(i => $"step {i}")));
		var group = new ScrollViewer().Content(new StackPanel().Children(text))
			.AutoVerticalScroll().NoHorizontalScroll().MaxHeight(160).SmoothScrolling();
		var conversation = new ScrollViewer()
			.Content(new StackPanel().Children(new Border().Height(600), group, new Border().Height(600)))
			.AutoVerticalScroll().NoHorizontalScroll().SmoothScrolling();
		ConversationPerformanceTests.Layout(conversation, 500, 300);
		using var guard = new TextFocusScrollGuard(conversation);
		guard.Track(text);
		var body = SmoothScroll.For(group);
		var outer = SmoothScroll.For(conversation);
		var step = ThemeManager.DefaultMetrics.ScrollWheelStep;
		Assert.True(body.MaxVerticalOffset > 0);

		var wheel = new MouseWheelEventArgs(default, default, new Vector(0, -1), false, false, false, ModifierKeys.None);
		guard.RouteWheel(text, wheel);
		Assert.True(wheel.Handled);
		Assert.Equal(step, body.TargetVerticalOffset);
		Assert.False(outer.IsAnimating);

		// 组体到底后，滚轮才沿祖先链回到会话。
		body.JumpToEnd();
		wheel = new MouseWheelEventArgs(default, default, new Vector(0, -1), false, false, false, ModifierKeys.None);
		guard.RouteWheel(text, wheel);
		Assert.True(wheel.Handled);
		Assert.Equal(step, outer.TargetVerticalOffset);
	}

	[Fact]
	public void CollapsingALongProcessGroupRestoresTheRowsBelow()
	{
		ConversationScroll? conversation = null;
		TextFocusScrollGuard? guard = null;
		using var timeline = new ConversationTimeline(entry =>
		{
			if (entry.Role == ChatRole.Process)
				return new ExecutionProcessView(entry, e => new AssistantMessageView(e, _ => { }, guard!).View, guard).View;
			return new AssistantMessageView(entry, _ => { }, guard!).View;
		});
		guard = new TextFocusScrollGuard(timeline.Scroll, anchor => conversation!.PreserveAnchor(anchor));
		conversation = new ConversationScroll(timeline.Scroll, _ => { });
		using (guard)
		using (conversation)
		{
			var longText = string.Join("\n\n", Enumerable.Range(0, 40).Select(i => $"## 段落 {i}\n\n这是一段用于撑高过程组体的正文，第 {i} 段。"));
			var rows = Enumerable.Range(0, 24)
				.Select(i => new ChatEntry { Role = ChatRole.User, Text = $"history {i}", EntryId = $"h{i}" }).ToList();
			rows.Add(new ChatEntry { Role = ChatRole.Process, Text = "", EntryId = "p", ProcessEntries = Enumerable.Range(0, 3)
				.Select(i => new ChatEntry { Role = ChatRole.Assistant, Thinking = "先梳理一下", Text = longText, EntryId = $"s{i}" }).ToArray() });
			rows.Add(new ChatEntry { Role = ChatRole.Assistant, Text = "answer", EntryId = "a" });
			timeline.SetEntries(rows);
			Pump(timeline.View);
			// 用户是在底部（跟流）展开的：展开后仍贴底。
			SmoothScroll.For(timeline.Scroll).JumpToEnd();
			Pump(timeline.View);

			var run = VisualTree.Find<Expander>(timeline.View).Single(expander => HoverHints.Text(expander) == Strings.ExecutionExpandHint);
			Toggle(run, keyboard: true);
			Pump(timeline.View);
			var group = VisualTree.Find<Expander>(run.Content!).Single(expander => HoverHints.Text(expander) == Strings.ExecutionGroupHint);
			Toggle(group, keyboard: true);
			Pump(timeline.View);
			SmoothScroll.For(timeline.Scroll).JumpToEnd();
			Pump(timeline.View);

			Toggle(run, keyboard: true);
			Pump(timeline.View);
			var offset = timeline.Scroll.VerticalOffset;
			var maximum = SmoothScroll.For(timeline.Scroll).MaxVerticalOffset;
			// 收起后内容变短：偏移必须跟着新范围收回，否则视口停在内容之外，表现为一片空白。
			Assert.True(offset <= maximum + 0.5,
				$"offset {offset} left beyond the shrunken extent {maximum}");
			var real = timeline.VisibleRows.OfType<UIElement>()
				.Count(row => row.Bounds.Bottom > timeline.Scroll.Bounds.Y && row.Bounds.Y < timeline.Scroll.Bounds.Bottom);
			Assert.True(real > 0, $"the viewport is blank after collapsing; offset {offset}, max {maximum}");
		}
	}

	[Fact]
	public void RebuildingWithFewerMessagesNeverLeavesABlankRow()
	{
		using var timeline = new ConversationTimeline(entry => new Border().Height(100).Child(new TextBlock().Text(entry.Text)));
		var rows = Enumerable.Range(0, 8)
			.Select(i => new ChatEntry { Role = ChatRole.User, Text = $"m{i}", EntryId = $"e{i}" }).ToList();
		timeline.SetEntries(rows);
		Pump(timeline.View);

		timeline.SetEntries(rows.Take(3).ToList(), rebuild: true);
		Pump(timeline.View);

		// 已实现且落在视口里的行不能是「有位置、没内容」的空壳。
		var blank = VisualTree.Find<ContentControl>(timeline.View)
			.Where(host => host.Content is null && host.Bounds.Height > 0 &&
				host.Bounds.Bottom > timeline.Scroll.Bounds.Y && host.Bounds.Y < timeline.Scroll.Bounds.Bottom)
			.ToArray();
		Assert.Empty(blank);
	}

	[Fact]
	public void FoldTogglesAskTheHostToRefreshTheTimeline()
	{
		using var scroll = new ScrollViewer();
		using var guard = new TextFocusScrollGuard(scroll);
		var refreshes = 0;
		FrameworkElement? source = null;
		guard.LayoutInvalidated = element => { refreshes++; source = element; };
		var fold = new Expander().IsExpanded(false).Content(new Border().Height(200));
		guard.TrackFold(fold);

		fold.IsExpanded = true;
		Assert.Equal(1, refreshes);
		// 时间线据此只重排受影响的那一行（折叠行不一定把失效传到行根）。
		Assert.Same(fold, source);
		fold.IsExpanded = false;
		Assert.Equal(2, refreshes);
		Assert.Same(fold, source);
	}

	[Fact]
	public void CollapsingAHugeFoldMidScrollLeavesRealizedRowsInTheViewport()
	{
		// 中途滚动时收起一个很高的折叠行：视口必须仍落在内容上，
		// 否则看起来就是「一片空白，滚动一下才恢复」。
		Expander? fold = null;
		using var timeline = new ConversationTimeline(entry =>
		{
			if (entry.EntryId != "u") return new Border().Height(100).Child(new TextBlock().Text(entry.Text));
			fold = new Expander().IsExpanded(false).Content(new Border().Height(4000));
			return fold;
		});
		timeline.SetEntries([
			new() { Role = ChatRole.User, Text = "q", EntryId = "u" },
			new() { Role = ChatRole.Assistant, Text = "answer", EntryId = "a" },
		]);
		Pump(timeline.View);
		fold!.IsExpanded = true;
		Pump(timeline.View);
		Pump(timeline.View);
		timeline.Scroll.SetScrollOffsets(0, 1500);
		Pump(timeline.View);

		fold.IsExpanded = false;
		Pump(timeline.View);
		timeline.RefreshLayout();
		Pump(timeline.View);

		var visible = timeline.VisibleRows.OfType<UIElement>()
			.Where(row => row.Bounds.Bottom > timeline.Scroll.Bounds.Y && row.Bounds.Y < timeline.Scroll.Bounds.Bottom)
			.ToArray();
		Assert.NotEmpty(visible);
		Assert.All(visible, row => Assert.True(row.Bounds.Height > 0));
	}

	/// <summary>只跑一次真实的 measure/arrange：不手动清洗子树缓存，才能暴露漏掉的失效。</summary>
	private static void Pump(UIElement view, double width = 500, double height = 400)
	{
		view.Measure(new Size(width, height));
		view.Arrange(new Rect(0, 0, width, height));
	}

	private static UIElement RowHost(ConversationTimeline timeline, string id) =>
		VisualTree.Find<ContentControl>(timeline.View)
			.Single(host => host.Tag is ChatEntry { EntryId: var entryId } && entryId == id);

	private static void Toggle(Expander fold, bool keyboard)
	{
		if (keyboard)
			typeof(UIElement).GetMethod("OnKeyUp", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(fold, [new KeyEventArgs(Key.Space, 0)]);
		else
		{
			var point = new Point(fold.Bounds.X + 10, fold.Bounds.Y + 10);
			typeof(UIElement).GetMethod("OnMouseDown", BindingFlags.Instance | BindingFlags.NonPublic)!
				.Invoke(fold, [new MouseEventArgs(point, point, MouseButton.Left)]);
		}
	}
}
