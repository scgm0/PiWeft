using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using PiWeft.Desktop;
using Xunit;

namespace PiWeft.Desktop.Tests;

/// <summary>可变高度虚拟化时间线的行为回归：跟随意图、已测高度、跳转与侧栏列表。</summary>
[Collection("mewui-controls")]
public sealed class VirtualizationAuditTests
{
	private static ChatEntry User(int index) => new()
	{
		Role = ChatRole.User,
		Text = $"history {index}",
		EntryId = $"h{index}",
	};

	private static (ConversationTimeline Timeline, ConversationScroll Conversation, TextFocusScrollGuard Guard) Build()
	{
		ConversationScroll? conversation = null;
		TextFocusScrollGuard? guard = null;
		var timeline = new ConversationTimeline(entry =>
			new Border().Height(entry.Text.Length * 7 + 40).Child(new TextBlock().Text(entry.Text)));
		guard = new TextFocusScrollGuard(timeline.Scroll, anchor => conversation!.PreserveAnchor(anchor));
		conversation = new ConversationScroll(timeline.Scroll, _ => { });
		return (timeline, conversation, guard);
	}

	private static void Pump(UIElement view, double width = 500, double height = 400)
	{
		view.Measure(new Size(width, height));
		view.Arrange(new Rect(0, 0, width, height));
	}

	private static MouseWheelEventArgs Wheel(double notches) =>
		new(default, default, new Vector(0, notches), false, false, false, ModifierKeys.None);

	private static ContentControl HostOf(ConversationTimeline timeline, string entryId) =>
		VisualTree.Find<ContentControl>(timeline.View)
			.First(host => host.Tag is ChatEntry entry && entry.EntryId == entryId);

	[Fact]
	public void TinyUpwardTrackpadJitterKeepsFollowing()
	{
		var (timeline, conversation, guard) = Build();
		using (timeline)
		using (conversation)
		using (guard)
		{
			timeline.SetEntries(Enumerable.Range(0, 40).Select(User).ToArray());
			Pump(timeline.View);
			var smooth = SmoothScroll.For(timeline.Scroll);
			smooth.JumpToEnd();
			Pump(timeline.View);
			Assert.True(conversation.Following);

			// 触控板小刻度上滑：处在近底带内，不应被当成“退出跟随”；
			// 高精度输入会同步落位并触发 ScrollChanged，几何路径也不能把它当成读者意图。
			smooth.RouteWheel(Wheel(0.01));
			Assert.True(conversation.Following);
			smooth.RouteWheel(Wheel(0.05));
			Assert.True(conversation.Following);
			smooth.RouteWheel(Wheel(0.05));
			Assert.True(conversation.Following);
		}
	}

	[Fact]
	public void ClearUpwardIntentEscapesFollowing()
	{
		var (timeline, conversation, guard) = Build();
		using (timeline)
		using (conversation)
		using (guard)
		{
			timeline.SetEntries(Enumerable.Range(0, 40).Select(User).ToArray());
			Pump(timeline.View);
			var smooth = SmoothScroll.For(timeline.Scroll);
			smooth.JumpToEnd();
			Pump(timeline.View);
			Assert.True(conversation.Following);

			smooth.RouteWheel(Wheel(1));
			Assert.False(conversation.Following);
		}
	}

	[Fact]
	public void RewritingOneRowKeepsMeasuredHeightsForTheRest()
	{
		using var timeline = new ConversationTimeline(entry =>
			new Border().Height(entry.Timestamp % 3 == 0 ? 220 : 60).Child(new TextBlock().Text(entry.Text)));
		using var guard = new TextFocusScrollGuard(timeline.Scroll);
		using var conversation = new ConversationScroll(timeline.Scroll, _ => { });
		var rows = Enumerable.Range(0, 60).Select(i => new ChatEntry
		{
			Role = ChatRole.User,
			Text = $"row {i}",
			Timestamp = i,
			EntryId = $"r{i}",
		}).ToList();
		timeline.SetEntries(rows);
		Pump(timeline.View);
		// 走完整段历史，让每一行都进入过视口并留下已测高度。
		for (var offset = 0.0; offset < 7000; offset += 250)
		{
			timeline.Scroll.SetScrollOffsets(0, offset);
			Pump(timeline.View);
		}

		timeline.Scroll.SetScrollOffsets(0, 1000);
		Pump(timeline.View);
		var before = SmoothScroll.For(timeline.Scroll).MaxVerticalOffset;

		// 只改不参与高度的元数据：其它行的已测高度不应被整段作废成估算值。
		rows[30] = rows[30] with { ToolArguments = "metadata-only" };
		timeline.SetEntries(rows);
		Pump(timeline.View);
		var after = SmoothScroll.For(timeline.Scroll).MaxVerticalOffset;
		Assert.Equal(before, after, 1);
	}

	[Fact]
	public void ChangingOneRowReplacesOnlyThatRow()
	{
		using var timeline = new ConversationTimeline(entry =>
			new Border().Height(80).Child(new TextBlock().Text(entry.Text)));
		var rows = Enumerable.Range(0, 80).Select(User).ToList();
		timeline.SetEntries(rows);
		Pump(timeline.View);
		var changes = new List<ItemsChange>();
		timeline.View.ItemsSource.Changed += changes.Add;

		rows[20] = rows[20] with { ToolArguments = "metadata-only" };
		timeline.SetEntries(rows);

		var replace = Assert.Single(changes);
		Assert.Equal(ItemsChangeKind.Replace, replace.Kind);
		Assert.Equal(20, replace.Index);
		Assert.Equal(1, replace.Count);
	}

	[Fact]
	public void GrowingProcessRowAboveTheTailKeepsFollowing()
	{
		ConversationScroll? conversation = null;
		TextFocusScrollGuard? guard = null;
		using var timeline = new ConversationTimeline(entry => entry.Role == ChatRole.Process
			? new Border().Height(120 + entry.ProcessEntries.Count * 80)
			: new Border().Height(entry.Role == ChatRole.Assistant ? 400 : 70).Child(new TextBlock().Text(entry.Text)));
		guard = new TextFocusScrollGuard(timeline.Scroll, anchor => conversation!.PreserveAnchor(anchor));
		conversation = new ConversationScroll(timeline.Scroll, _ => { });
		using (timeline)
		using (conversation)
		using (guard)
		{
			var rows = Enumerable.Range(0, 20).Select(User).ToList();
			rows.Add(new ChatEntry
			{
				Role = ChatRole.Process, Text = string.Empty, EntryId = "process",
				ProcessEntries = [new ChatEntry { Role = ChatRole.Tool, Text = "step", EntryId = "step-1" }],
			});
			rows.Add(new ChatEntry { Role = ChatRole.Assistant, Text = "streaming", EntryId = "live", IsStreaming = true, Timestamp = 1 });
			timeline.SetEntries(rows);
			Pump(timeline.View);
			conversation.ScrollToLatest();
			SmoothScroll.For(timeline.Scroll).Stop();
			for (var pass = 0; pass < 10; pass++)
			{
				Pump(timeline.View);
				conversation.FollowAfterLayout();
			}
			Assert.True(conversation.Following);
			Assert.Equal(SmoothScroll.For(timeline.Scroll).MaxVerticalOffset, timeline.Scroll.VerticalOffset, 1);

			// 过程组在尾部上方长高：布局锚点校正不能把跟底状态当成读者上滚。
			var process = rows[^2];
			rows[^2] = process with
			{
				ProcessEntries = [.. process.ProcessEntries,
					new ChatEntry { Role = ChatRole.Tool, Text = "step", EntryId = "step-2" }],
			};
			timeline.SetEntries(rows);
			conversation.FollowContent();
			for (var pass = 0; pass < 10; pass++)
			{
				Pump(timeline.View);
				conversation.FollowAfterLayout();
			}
			Assert.True(conversation.Following);
			Assert.Equal(SmoothScroll.For(timeline.Scroll).MaxVerticalOffset, timeline.Scroll.VerticalOffset, 1);
		}
	}

	[Fact]
	public void GrowingAnInPlaceProcessRowAboveKeepsTheReadingRow()
	{
		using var timeline = new ConversationTimeline(entry => entry.Role == ChatRole.Process
			? new Border().Height(120 + entry.ProcessEntries.Count * 80)
			: new Border().Height(100).Child(new TextBlock().Text(entry.Text)));
		using var guard = new TextFocusScrollGuard(timeline.Scroll);
		using var conversation = new ConversationScroll(timeline.Scroll, _ => { });
		var rows = Enumerable.Range(0, 30).Select(User).ToList();
		rows.Insert(12, new ChatEntry
		{
			Role = ChatRole.Process, Text = string.Empty, EntryId = "process",
			ProcessEntries = [new ChatEntry { Role = ChatRole.Tool, Text = "step", EntryId = "step-1" }],
		});
		timeline.SetEntries(rows);
		Pump(timeline.View);
		timeline.Scroll.SetScrollOffsets(0, 900);
		Pump(timeline.View);
		Pump(timeline.View);
		conversation.Browse();

		var anchor = VisualTree.Find<ContentControl>(timeline.View)
			.Where(host => host.Tag is ChatEntry && host.Bounds.Bottom > timeline.Scroll.Bounds.Top)
			.OrderBy(host => host.Bounds.Y).Skip(3).First();
		var anchorId = ((ChatEntry)anchor.Tag!).EntryId!;
		var originalY = anchor.Bounds.Y;
		for (var step = 0; step < 6; step++)
		{
			var process = rows[12];
			rows[12] = process with
			{
				ProcessEntries = [.. process.ProcessEntries,
					new ChatEntry { Role = ChatRole.Tool, Text = "step", EntryId = $"step-{step + 2}" }],
			};
			timeline.SetEntries(rows);
			Pump(timeline.View);
			Pump(timeline.View);
		}
		Assert.False(conversation.Following);
		var moved = HostOf(timeline, anchorId).Bounds.Y - originalY;
		Assert.True(Math.Abs(moved) <= 2, $"reading row drifted {moved} DIP over 6 in-place updates above");
	}




	[Fact]
	public void ReadingAnchorCapturesTheDepthInsideATallRow()
	{
		using var timeline = new ConversationTimeline(entry => new Border().Height(2000)
			.Child(new TextBlock().Text(entry.Text)));
		using var guard = new TextFocusScrollGuard(timeline.Scroll);
		timeline.SetEntries([User(0)]);
		Pump(timeline.View);
		timeline.Scroll.SetScrollOffsets(0, 700);
		Pump(timeline.View);
		var anchor = timeline.CurrentReadingAnchor();
		Assert.Equal("h0", anchor.EntryId);
		Assert.InRange(anchor.WithinRow, 650, 750);
	}

	[Fact]
	public void FilteringJumpsToTheFirstMatchAndClearingRestoresTheReadingRow()
	{
		using var session = ConversationPerformanceTests.CreateSession(80);
		using var tab = new SessionTab(session, [session.Model], "filter");
		ConversationPerformanceTests.Layout(tab);
		var scroll = VisualTree.Find<ScrollViewer>(tab.View).First();

		scroll.SetScrollOffsets(0, 2400);
		ConversationPerformanceTests.Layout(tab);
		Assert.False(tab.FollowingFromTests);
		var anchor = tab.TimelineFromTests.CurrentReadingAnchor().EntryId;
		Assert.NotNull(anchor);
		Assert.True(scroll.VerticalOffset > 0);

		tab.SetFilterFromTests("answer 70");
		ConversationPerformanceTests.Layout(tab);
		Assert.False(tab.FollowingFromTests);
		Assert.Contains(VisualTree.Find<PiWeft.Markdown.MarkdownView>(tab.View),
			view => view.Source.Contains("answer 70", StringComparison.Ordinal));
		Assert.Equal(0, scroll.VerticalOffset, 1);

		tab.SetFilterFromTests(string.Empty);
		ConversationPerformanceTests.Layout(tab);
		// 清空搜索回到原来的阅读行（窗口运行时 JumpToRow 会把它对齐到顶部；
		// 无窗口 fixture 没有 dispatcher，至少要求它重新进入视口且不落在底部）。
		var restored = VisualTree.Find<ContentControl>(tab.View)
			.FirstOrDefault(host => host.Tag is ChatEntry entry && entry.EntryId == anchor);
		Assert.NotNull(restored);
		Assert.True(restored!.Bounds.Bottom > scroll.Bounds.Top && restored.Bounds.Top < scroll.Bounds.Bottom,
			$"anchor {anchor} not in viewport; offset {scroll.VerticalOffset}, max {SmoothScroll.For(scroll).MaxVerticalOffset}");
		Assert.True(scroll.VerticalOffset < SmoothScroll.For(scroll).MaxVerticalOffset - 1);
	}

	[Fact]
	public void FilteringFromTheTailReturnsToTheTailAfterClearing()
	{
		using var session = ConversationPerformanceTests.CreateSession(60);
		using var tab = new SessionTab(session, [session.Model], "filter-tail");
		ConversationPerformanceTests.Layout(tab);
		Assert.True(tab.FollowingFromTests);

		tab.SetFilterFromTests("answer 30");
		ConversationPerformanceTests.Layout(tab);
		tab.SetFilterFromTests(string.Empty);
		ConversationPerformanceTests.Layout(tab);
		Assert.True(tab.FollowingFromTests);
	}


	[Fact]
	public void FilteringFindsToolsInsideAProcessGroup()
	{
		var process = new ChatEntry
		{
			Role = ChatRole.Process,
			Text = string.Empty,
			EntryId = "process",
			ProcessEntries =
			[
				new ChatEntry { Role = ChatRole.Tool, ToolName = "bash", Text = "dotnet build", EntryId = "step" },
			],
		};
		Assert.True(SessionTab.Matches(process, "bash"));
		Assert.True(SessionTab.Matches(process, "dotnet build"));
		Assert.False(SessionTab.Matches(process, "missing"));
	}

	[Fact]
	public void ScrollIntoViewReachesFarUnmeasuredRows()
	{
		using var timeline = new ConversationTimeline(entry =>
			new Border().Height(40 + entry.Text.Length * 9).Child(new TextBlock().Text(entry.Text)));
		using var guard = new TextFocusScrollGuard(timeline.Scroll);
		using var conversation = new ConversationScroll(timeline.Scroll, _ => { });
		var rows = Enumerable.Range(0, 200).Select(i => new ChatEntry
		{
			Role = ChatRole.User,
			Text = new string('x', 10 + i % 30),
			EntryId = $"r{i}",
		}).ToArray();
		timeline.SetEntries(rows);
		Pump(timeline.View);
		timeline.View.ScrollIntoView(0);
		for (var pass = 0; pass < 6; pass++) Pump(timeline.View);
		Assert.Contains(VisualTree.Find<ContentControl>(timeline.View), host => host.Tag is ChatEntry { EntryId: "r0" });

		timeline.View.ScrollIntoView(150);
		for (var pass = 0; pass < 6; pass++) Pump(timeline.View);
		Assert.Contains(VisualTree.Find<ContentControl>(timeline.View), host => host.Tag is ChatEntry { EntryId: "r150" });

		timeline.View.ScrollIntoView(20);
		for (var pass = 0; pass < 6; pass++) Pump(timeline.View);
		Assert.Contains(VisualTree.Find<ContentControl>(timeline.View), host => host.Tag is ChatEntry { EntryId: "r20" });
	}

	[Fact]
	public void RefreshingTheSessionTreeKeepsItsScrollOffset()
	{
		var tree = new TreeView().ItemHeight(36);
		tree.ItemsSource(Enumerable.Range(0, 60).Select(i => new TreeViewNode($"session {i}")).ToArray());
		Pump(tree);
		var scroll = VisualTree.Find<ScrollViewer>(tree).First();
		scroll.SetScrollOffsets(0, 300);
		Pump(tree);
		Assert.Equal(300, scroll.VerticalOffset, 0.5);

		tree.ItemsSource(Enumerable.Range(0, 61).Select(i => new TreeViewNode($"session {i}")).ToArray());
		Pump(tree);
		Assert.Equal(300, scroll.VerticalOffset, 0.5);
	}

	[Fact]
	public void EntryEqualityIncludesTheChangedFilesSummary()
	{
		var entry = new ChatEntry { Role = ChatRole.Assistant, Text = "answer", EntryId = "a" };
		Assert.False(ConversationTimeline.SameEntry(entry, entry with { ChangedFiles = ["a.cs"] }));
	}
}
