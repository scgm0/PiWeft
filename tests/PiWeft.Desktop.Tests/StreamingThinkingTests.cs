using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using PiWeft.Markdown;
using Xunit;
using static PiWeft.Desktop.Tests.VisualTree;

namespace PiWeft.Desktop.Tests;

/// <summary>
/// 流式思考与步骤行展开后的布局收敛：两者都要求“行在 arrange 阶段变高后重新测高并发布 extent”。
/// </summary>
[Collection("mewui-controls")]
public sealed class StreamingThinkingTests
{
	private static void Pump(UIElement view, double width = 560, double height = 420)
	{
		view.Measure(new Size(width, height));
		view.Arrange(new Rect(0, 0, width, height));
	}

	/// <summary>逐层展开：外层过程折叠建出组，组折叠再建出步骤行（不动步骤自身的折叠）。</summary>
	private static void ExpandGroups(UIElement root, int passes = 3)
	{
		for (var pass = 0; pass < passes; pass++)
		{
			foreach (var expander in Find<Expander>(root)
				.Where(expander => !expander.IsExpanded &&
					HoverHints.Text(expander) is Strings.ExecutionExpandHint or Strings.ExecutionGroupHint)
				.ToArray())
			{
				expander.IsExpanded = true;
			}
			Pump(root);
		}
	}

	/// <summary>窗口里这一步由 Render 优先级的回调完成；无窗口 fixture 手动驱动同一收敛。</summary>
	private static void Settle(ExecutionProcessView group)
	{
		Pump(group.View);
		group.SyncBodyMetrics();
		Pump(group.View);
	}

	private static ChatEntry Thinking(string text) => new()
	{
		Role = ChatRole.Assistant,
		Text = string.Empty,
		Thinking = text,
		EntryId = "live",
		Timestamp = 1,
		IsStreaming = true,
		IsThinking = true,
	};

	private static ChatEntry Process(ChatEntry member, bool running = true) => new()
	{
		Role = ChatRole.Process,
		Text = string.Empty,
		EntryId = "process",
		IsStreaming = running,
		ProcessEntries = [member],
	};

	private static ChatEntry Tool(int index) => new()
	{
		Role = ChatRole.Tool,
		EntryId = $"tool/{index}",
		ToolName = "read",
		ToolCallId = $"call-{index}",
		Text = string.Join('\n', Enumerable.Range(0, 30).Select(line => $"output {index} line {line}")),
	};

	private static ScrollViewer BodyOf(ExecutionProcessView group) =>
		Find<ScrollViewer>(group.View).Single(view => view.IsVisible && view.Bounds.Height > 0 &&
			view.VerticalScroll != ScrollMode.Disabled);

	[Fact]
	public void ExpandingAToolRowAfterAQueuedStreamingFollowKeepsTheReadingPosition()
	{
		// 流式思考那一帧把「布局后继续跟底」排进了队列；同一帧里读者点开了另一个工具步骤。
		// 展开是阅读行为，必须取消还在排队的跟底动作，否则预览行会被拽到新的末尾 ——
		// 这就是「偶发：展开步骤行时组体进度没固定在步骤行预览」。
		using var guard = new TextFocusScrollGuard(new ScrollViewer());
		var members = Enumerable.Range(0, 30).Select(Tool).ToList();
		members[6] = Thinking("初始思考");
		var process = new ChatEntry
		{
			Role = ChatRole.Process, Text = string.Empty, EntryId = "process", IsStreaming = true,
			ProcessEntries = [.. members],
		};
		var view = new ExecutionProcessView(process, entry => entry.Role == ChatRole.Assistant
			? new AssistantMessageView(entry, _ => { }, guard).View
			: new ToolMessageView(entry, guard).View, guard);
		Pump(view.View);
		ExpandGroups(view.View);

		var body = BodyOf(view);
		var smooth = SmoothScroll.For(body);
		body.SetScrollOffsets(0, smooth.MaxVerticalOffset);
		Settle(view);
		Assert.True(smooth.IsFollowingGrowth, "the body parked at its end follows the streaming thinking");

		// 1) 思考又长了一截：SyncRows 记下跟随意图，把跟底动作排到布局之后。
		members[6] = Thinking(string.Join("\n\n", Enumerable.Range(0, 6).Select(index => $"思考段落 {index}")));
		view.Update(process with { ProcessEntries = [.. members] });
		Pump(view.View);

		// 2) 同一帧里读者点开一个靠上的工具步骤。
		var row = Find<ItemContainer>(body)
			.First(container => container.Bounds.Y > body.Bounds.Y &&
				container.Bounds.Bottom <= body.Bounds.Bottom &&
				Find<Expander>(container).Any(expander => HoverHints.Text(expander) == Strings.ToolExpandHint));
		var position = row.Index;
		var previewTop = row.Bounds.Y - body.Bounds.Y;
		Find<Expander>(row).First(expander => HoverHints.Text(expander) == Strings.ToolExpandHint).IsExpanded = true;

		view.SyncBodyMetrics();
		Pump(view.View);
		view.SyncBodyMetrics();
		Pump(view.View);

		Assert.False(smooth.IsFollowingGrowth, "expanding a tool step cancels the queued follow");
		var expanded = Find<ItemContainer>(body).First(container => container.Index == position);
		Assert.InRange(expanded.Bounds.Y - body.Bounds.Y, previewTop - 1, previewTop + 1);
		UiLifetime.Release(view.View);
	}

	[Fact]
	public void ExpandedStreamingThinkingUpdatesPerFrameAndRendersMarkdownWhenSettled()
	{
		using var guard = new TextFocusScrollGuard(new ScrollViewer());
		var entry = Thinking("step 0");
		var view = new AssistantMessageView(entry, _ => { }, guard);
		Pump(view.View);
		var thinking = Find<Expander>(view.View).Single();
		thinking.IsExpanded = true;
		Pump(view.View);

		// 流式阶段就用增量 Markdown 视图：只重排开放尾块，不再先纯文本再替换。
		var markdown = Assert.Single(Find<MarkdownView>(view.View));
		Assert.True(markdown.IsStreaming);
		Assert.Contains("step 0", markdown.Source, StringComparison.Ordinal);

		for (var step = 1; step <= 6; step++)
		{
			entry = Thinking(string.Join("\n\n", Enumerable.Range(0, step + 1).Select(i => $"reasoning paragraph {i}")));
			view.Update(entry);
			Pump(view.View);
			// 展开的思考必须跟着流式增量走，而不是停在展开时的那一版。
			Assert.Equal(entry.Thinking, markdown.Source);
		}

		// 思考结束：同一个视图只切换流式状态，不重建、不换控件。
		entry = entry with { IsThinking = false, IsStreaming = false };
		view.Update(entry);
		Pump(view.View);
		Assert.Same(markdown, Assert.Single(Find<MarkdownView>(view.View)));
		Assert.False(markdown.IsStreaming);
		Assert.Equal(entry.Thinking, markdown.Source);
		Assert.True(thinking.IsExpanded);
	}

	[Fact]
	public void StreamingThinkingInsideAProcessGroupGrowsWhileTheGroupStaysExpanded()
	{
		using var guard = new TextFocusScrollGuard(new ScrollViewer());
		var group = new ExecutionProcessView(Process(Thinking("step 0")),
			entry => new AssistantMessageView(entry, _ => { }, guard).View, guard);
		Pump(group.View);
		ExpandGroups(group.View);
		// 思考折叠由用户展开：展开后流式正文继续实时更新。
		var thinking = Find<Expander>(group.View).Single(expander => HoverHints.Text(expander) is null or "");
		thinking.IsExpanded = true;
		Settle(group);
		var markdown = Assert.Single(Find<MarkdownView>(group.View));
		var body = BodyOf(group);
		var smooth = SmoothScroll.For(body);
		var heightBefore = markdown.Bounds.Height;

		var entry = Thinking("step 0");
		for (var step = 1; step <= 8; step++)
		{
			entry = Thinking(string.Join("\n\n", Enumerable.Range(0, step * 2 + 1).Select(i => $"reasoning paragraph {i}")));
			group.Update(Process(entry));
			Pump(group.View);
			Assert.Equal(entry.Thinking, markdown.Source);
		}

		Settle(group);
		Assert.True(markdown.Bounds.Height > heightBefore + 20,
			$"expanded thinking did not grow on screen: {heightBefore:0.#} -> {markdown.Bounds.Height:0.#}");
		Assert.True(smooth.MaxVerticalOffset > 0, "the grown thinking row must be reachable inside the group body");
	}

	[Fact]
	public void ThinkingStaysExpandedWhenTheMessageSettles()
	{
		using var guard = new TextFocusScrollGuard(new ScrollViewer());
		var live = new ChatEntry
		{
			Role = ChatRole.Assistant, Text = string.Empty, EntryId = "live-assistant/1", Timestamp = 1,
			Thinking = string.Join("\n\n", Enumerable.Range(0, 20).Select(i => $"思考段落 {i}")),
			IsStreaming = true, IsThinking = true,
		};
		var process = ExecutionProcess.Project([
			new ChatEntry { Role = ChatRole.User, Text = "q", EntryId = "u" }, live,
		], true)[1];
		var view = new ExecutionProcessView(process, entry => new AssistantMessageView(entry, _ => { }, guard).View, guard);
		Pump(view.View);
		ExpandGroups(view.View);
		var thinking = Find<Expander>(view.View).First(expander => HoverHints.Text(expander) is null or "");
		thinking.IsExpanded = true;
		Pump(view.View);
		var settled = live with { EntryId = "session-entry-1", IsStreaming = false, IsThinking = false };
		var finalProcess = ExecutionProcess.Project([
			new ChatEntry { Role = ChatRole.User, Text = "q", EntryId = "u" }, settled,
		], false)[1];
		view.Update(finalProcess);
		Pump(view.View);
		var restored = Find<Expander>(view.View).FirstOrDefault(expander => HoverHints.Text(expander) is null or "");
		Assert.NotNull(restored);
		Assert.True(restored!.IsExpanded, "expanded thinking must survive message finalization");
		UiLifetime.Release(view.View);
	}

	[Fact]
	public void FinalMarkdownIsBuiltInTheSameFrameAsTheSettle()
	{
		using var guard = new TextFocusScrollGuard(new ScrollViewer());
		var live = new ChatEntry
		{
			Role = ChatRole.Assistant, Text = string.Empty, EntryId = "live", Timestamp = 1,
			Thinking = string.Join("\n\n", Enumerable.Range(0, 40).Select(index => $"思考段落 {index}")),
			IsStreaming = true, IsThinking = true,
		};
		var view = new AssistantMessageView(live, _ => { }, guard);
		Pump(view.View);
		var thinking = Find<Expander>(view.View).Single();
		thinking.IsExpanded = true;
		Pump(view.View);

		// 定稿：同一个视图只切状态，内容在流式期间已经排好，不留一帧空白/旧纯文本。
		var streaming = Assert.Single(Find<MarkdownView>(view.View));
		view.Update(live with { IsThinking = false, IsStreaming = false });
		Pump(view.View);
		var markdown = Assert.Single(Find<MarkdownView>(view.View));
		Assert.Same(streaming, markdown);
		Assert.False(markdown.IsStreaming);
		Assert.Equal(live.Thinking, markdown.Source);
		Assert.True(markdown.DesiredSize.Height > 0, "the markdown tree must exist in the same frame");
		UiLifetime.Release(view.View);
	}

	[Fact]
	public void SettlingAMessageKeepsStableProcessStepIds()
	{
		var user = new ChatEntry { Role = ChatRole.User, Text = "q", EntryId = "u" };
		var live = new ChatEntry
		{
			Role = ChatRole.Assistant, Text = string.Empty, EntryId = "live-assistant/1", Timestamp = 7,
			Thinking = "先分析一下", IsStreaming = true, IsThinking = true,
		};
		var call = new ChatEntry
		{
			Role = ChatRole.Tool, EntryId = "live/call", ToolCallId = "c1", ToolName = "read",
			ToolArguments = """{"path":"a.cs"}""", Text = "running", IsStreaming = true, Timestamp = 8,
		};
		var liveProcess = ExecutionProcess.Project([user, live, call], true)[1];
		var settledProcess = ExecutionProcess.Project([
			user,
			live with { EntryId = "session-entry", IsStreaming = false, IsThinking = false },
			call with { EntryId = "session-entry/tool/c1", Text = "done", IsStreaming = false, Timestamp = 9 },
		], false)[1];

		// 流式行落盘会换掉源 EntryId；过程组/步骤键必须仍稳定，否则展开的思考会在
		// 结束的瞬间整组重建并折叠。
		Assert.Equal(
			liveProcess.ProcessEntries.Select(entry => entry.EntryId),
			settledProcess.ProcessEntries.Select(entry => entry.EntryId));
	}

	[Fact]
	public void StreamingThinkingInAVirtualizedGroupDoesNotJitterTheGroupProgress()
	{
		using var guard = new TextFocusScrollGuard(new ScrollViewer());
		var tools = Enumerable.Range(0, 30).Select(Tool).ToArray();
		ChatEntry Thinking(string text, string entryId = "live", bool streaming = true, bool thinking = true) => new()
		{
			Role = ChatRole.Assistant,
			Text = string.Empty,
			Thinking = text,
			EntryId = entryId,
			Timestamp = 1,
			IsStreaming = streaming,
			IsThinking = thinking,
		};
		var process = new ChatEntry
		{
			Role = ChatRole.Process, Text = string.Empty, EntryId = "process", IsStreaming = true,
			ProcessEntries = [.. tools, Thinking("思考")],
		};
		var view = new ExecutionProcessView(process, entry => new AssistantMessageView(entry, _ => { }, guard).View, guard);
		Pump(view.View);
		ExpandGroups(view.View);

		var body = BodyOf(view);
		var smooth = SmoothScroll.For(body);
		var thinking = Find<Expander>(body).Last(expander => HoverHints.Text(expander) is null or "");
		thinking.IsExpanded = true;
		Settle(view);
		smooth.FollowGrowth();
		Pump(view.View);

		var changes = new List<ItemsChange>();
		var items = Find<ItemsControl>(view.View).Single();
		items.ItemsSource.Changed += changes.Add;
		var offsets = new List<double>();
		for (var step = 1; step <= 12; step++)
		{
			var text = string.Join("\n\n", Enumerable.Range(0, step * 2).Select(index => $"思考内容段落 {index}"));
			process = process with { ProcessEntries = [.. tools, Thinking(text)] };
			view.Update(process);
			Pump(view.View);
			view.SyncBodyMetrics();
			Pump(view.View);
			offsets.Add(body.VerticalOffset);
		}

		// 流式思考行只原位更新：不能每帧发 Replace 把该行高度打回估算值，
		// 否则组体的 extent 会先缩后涨，表现为进度/滚动位置跳变。
		Assert.Empty(changes);
		for (var index = 1; index < offsets.Count; index++)
		{
			Assert.True(offsets[index] >= offsets[index - 1] - 0.5,
				$"group progress went backwards at frame {index}: {offsets[index - 1]:0.#} -> {offsets[index]:0.#}");
		}
		Assert.Equal(smooth.MaxVerticalOffset, body.VerticalOffset, 1);

		// 落盘时流式行会换成会话条目 id：同一时间戳的最终思考仍走原位更新，
		// 不能在这一刻把已测行高打回估算值。
		process = process with { ProcessEntries = [.. tools, Thinking("最终思考内容", "entry-1", streaming: false, thinking: false)] };
		view.Update(process);
		Pump(view.View);
		view.SyncBodyMetrics();
		Pump(view.View);
		Assert.Empty(changes);
		UiLifetime.Release(view.View);
	}

	[Fact]
	public void ScrollingTheGroupBodyBackToAThinkingRowRendersItsMarkdownAtOnce()
	{
		// 思考行滚出组体视口后块会被回收；滚回来时必须**同一帧**就重新 realize 出正文。
		// 旧实现里 MarkdownView 带着回收前的缓存测量回来，RealizeRange 用过期视口窗口算出
		// 空范围，于是点击/滚动后偶发只剩空白，要再滚一下才恢复。
		using var guard = new TextFocusScrollGuard(new ScrollViewer());
		var thinkingText = string.Join("\n\n", Enumerable.Range(0, 40).Select(index =>
			$"思考段落 {index} " + string.Join(" ", Enumerable.Repeat("内容", 24))));
		ChatEntry Assistant(string text) => new()
		{
			Role = ChatRole.Assistant, Text = string.Empty, Thinking = text,
			EntryId = "live", Timestamp = 1, IsStreaming = false,
		};
		var process = new ChatEntry
		{
			Role = ChatRole.Process, Text = string.Empty, EntryId = "process",
			ProcessEntries = [.. Enumerable.Range(0, 30).Select(Tool), Assistant(thinkingText)],
		};
		var view = new ExecutionProcessView(process, entry => entry.Role == ChatRole.Assistant
			? new AssistantMessageView(entry, _ => { }, guard).View
			: new ToolMessageView(entry, guard).View, guard);
		using var window = new Window().Content(view.View);
		Pump(view.View);
		ExpandGroups(view.View);

		var body = BodyOf(view);
		var list = Find<ItemsControl>(view.View).Single();
		list.ScrollIntoView(list.ItemsSource.Count - 1);
		Pump(view.View);
		view.SyncBodyMetrics();
		Pump(view.View);

		var thinking = Find<Expander>(body).Last(expander => HoverHints.Text(expander) is null or "");
		thinking.IsExpanded = true;
		Settle(view);
		var markdown = Find<MarkdownView>(view.View).Single(candidate => candidate.Source == thinkingText);
		guard.Track(markdown);
		Assert.True(markdown.RealizedBlockCount > 0, "the thinking Markdown must render when expanded");

		// 滚到组体另一端，让思考行整行离开视口（块被回收），再一帧滚回来。
		body.SetScrollOffsets(0, 0);
		Pump(view.View);
		view.SyncBodyMetrics();
		Pump(view.View);
		body.SetScrollOffsets(0, double.MaxValue);
		Pump(view.View);
		view.SyncBodyMetrics();
		Pump(view.View);

		var paragraphs = Find<MarkdownParagraphView>(markdown).ToArray();
		Assert.NotEmpty(paragraphs);
		Assert.Contains(paragraphs, paragraph => paragraph.Bounds.Height > 0 &&
			paragraph.Bounds.Bottom > body.Bounds.Y && paragraph.Bounds.Y < body.Bounds.Bottom &&
			!string.IsNullOrEmpty(paragraph.Text));
		UiLifetime.Release(view.View);
	}

	[Fact]
	public void AppendingWhileStreamingInAVirtualizedGroupKeepsTheGroupProgressStable()
	{
		using var guard = new TextFocusScrollGuard(new ScrollViewer());
		ChatEntry Thinking(string text) => new()
		{
			Role = ChatRole.Assistant, Text = string.Empty, Thinking = text,
			EntryId = "live", Timestamp = 1, IsStreaming = true,
		};
		var tools = Enumerable.Range(0, 30).Select(Tool).ToList();
		var members = tools.Take(5).Append(Thinking("思考")).Concat(tools.Skip(5)).ToList();
		var process = new ChatEntry
		{
			Role = ChatRole.Process, Text = string.Empty, EntryId = "process", IsStreaming = true,
			ProcessEntries = [.. members],
		};
		var view = new ExecutionProcessView(process, entry => entry.Role == ChatRole.Assistant
			? new AssistantMessageView(entry, _ => { }, guard).View
			: new ToolMessageView(entry, guard).View, guard);
		Pump(view.View);
		ExpandGroups(view.View);
		var body = BodyOf(view);
		var smooth = SmoothScroll.For(body);
		body.SetScrollOffsets(0, 0);
		Pump(view.View);
		var thinking = Find<Expander>(body).First(expander => HoverHints.Text(expander) is null or "");
		thinking.IsExpanded = true;
		Settle(view);
		smooth.FollowGrowth();
		Pump(view.View);

		var changes = new List<ItemsChange>();
		var items = Find<ItemsControl>(view.View).Single();
		items.ItemsSource.Changed += changes.Add;
		var offsets = new List<double>();
		for (var step = 1; step <= 8; step++)
		{
			members[5] = Thinking(string.Join("\n\n", Enumerable.Range(0, step * 2).Select(i => $"思考 {i}")));
			members.Add(Tool(30 + step));
			view.Update(process with { ProcessEntries = [.. members] });
			Pump(view.View);
			view.SyncBodyMetrics();
			Pump(view.View);
			view.SyncBodyMetrics();
			Pump(view.View);
			offsets.Add(body.VerticalOffset);
			Assert.True(smooth.MaxVerticalOffset - body.VerticalOffset <= 1, $"step {step} follow={smooth.IsFollowingGrowth} max={smooth.MaxVerticalOffset:0.#} offset={body.VerticalOffset:0.#} viewport={body.ViewportHeight:0.#}");
		}

		// 同时“流式增长 + 追加新步骤”不能退化成整表 Reset，也不能被布局校正静默取消跟底。
		Assert.DoesNotContain(changes, change => change.Kind == ItemsChangeKind.Reset);
		Assert.True(smooth.IsFollowingGrowth, "layout extent corrections must not cancel group growth follow");
		for (var index = 1; index < offsets.Count; index++)
			Assert.True(offsets[index] >= offsets[index - 1] - 0.5,
				$"tail went backwards at frame {index}: {offsets[index - 1]:0.#} -> {offsets[index]:0.#}");
		UiLifetime.Release(view.View);
	}

	[Fact]
	public void GroupBodyFollowsNewStepsOnlyWhileItsScrollbarSitsAtTheBottom()
	{
		using var guard = new TextFocusScrollGuard(new ScrollViewer());
		var members = Enumerable.Range(0, 30).Select(Tool).ToList();
		var process = new ChatEntry
		{
			Role = ChatRole.Process, Text = string.Empty, EntryId = "process", IsStreaming = false,
			ProcessEntries = [.. members],
		};
		var view = new ExecutionProcessView(process, entry => new ToolMessageView(entry, guard).View, guard);
		Pump(view.View);
		ExpandGroups(view.View);
		view.SyncBodyMetrics();
		Pump(view.View);
		var body = BodyOf(view);
		var smooth = SmoothScroll.For(body);

		// 手动展开的组体从顶部开始，有可滚内容时不自动跟底。
		Assert.True(smooth.MaxVerticalOffset > 0);
		Assert.False(smooth.IsFollowingGrowth);

		// 滚到滚动条底部：开启跟底，后续新步骤自动跟随。
		body.SetScrollOffsets(0, smooth.MaxVerticalOffset);
		Pump(view.View);
		Assert.True(smooth.IsFollowingGrowth);
		AppendAndSettle(Tool(30));
		Assert.True(smooth.IsFollowingGrowth);
		Assert.InRange(smooth.MaxVerticalOffset - body.VerticalOffset, 0, 1);

		// 向上离开底部：暂停跟底，新增步骤不能把读者拽回末尾。
		body.SetScrollOffsets(0, Math.Max(0, smooth.MaxVerticalOffset - 120));
		Pump(view.View);
		Assert.False(smooth.IsFollowingGrowth);
		var readingOffset = body.VerticalOffset;
		AppendAndSettle(Tool(31));
		Assert.False(smooth.IsFollowingGrowth);
		Assert.InRange(body.VerticalOffset, readingOffset - 1, readingOffset + 1);

		// 回到底部：重新锁底，继续跟随。
		body.SetScrollOffsets(0, smooth.MaxVerticalOffset);
		Pump(view.View);
		Assert.True(smooth.IsFollowingGrowth);
		AppendAndSettle(Tool(32));
		Assert.True(smooth.IsFollowingGrowth);
		Assert.InRange(smooth.MaxVerticalOffset - body.VerticalOffset, 0, 1);
		UiLifetime.Release(view.View);

		void AppendAndSettle(ChatEntry step)
		{
			members.Add(step);
			view.Update(process with { ProcessEntries = [.. members] });
			Pump(view.View);
			view.SyncBodyMetrics();
			Pump(view.View);
			view.SyncBodyMetrics();
			Pump(view.View);
		}
	}

	[Fact]
	public void StreamingThinkingAboveTheViewportKeepsTheReadingRowStable()
	{
		using var guard = new TextFocusScrollGuard(new ScrollViewer());
		ChatEntry Thinking(string text) => new()
		{
			Role = ChatRole.Assistant, Text = string.Empty, Thinking = text,
			EntryId = "live", Timestamp = 1, IsStreaming = true,
		};
		var tools = Enumerable.Range(0, 30).Select(Tool).ToArray();
		var members = tools.Take(3).Append(Thinking(string.Join("\n\n", Enumerable.Range(0, 4).Select(i => $"思考 {i}")))).Concat(tools.Skip(3)).ToArray();
		var process = new ChatEntry
		{
			Role = ChatRole.Process, Text = string.Empty, EntryId = "process", IsStreaming = true,
			ProcessEntries = members,
		};
		var view = new ExecutionProcessView(process, entry => entry.Role == ChatRole.Assistant
			? new AssistantMessageView(entry, _ => { }, guard).View
			: new ToolMessageView(entry, guard).View, guard);
		Pump(view.View);
		ExpandGroups(view.View);
		var body = BodyOf(view);
		body.SetScrollOffsets(0, 0);
		Pump(view.View);
		var thinking = Find<Expander>(body).First(expander => HoverHints.Text(expander) is null or "");
		thinking.IsExpanded = true;
		Pump(view.View);
		body.SetScrollOffsets(0, 350);
		Pump(view.View);
		var anchor = Find<Expander>(body)
			.Where(expander => HoverHints.Text(expander) == Strings.ToolExpandHint)
			.Where(expander => expander.Bounds.Y >= body.Bounds.Y && expander.Bounds.Bottom <= body.Bounds.Bottom)
			.OrderBy(expander => expander.Bounds.Y)
			.Skip(1).First();
		var y = anchor.Bounds.Y;
		var offset = body.VerticalOffset;
		for (var step = 1; step <= 6; step++)
		{
			var text = string.Join("\n\n", Enumerable.Range(0, 4 + step * 3).Select(i => $"思考内容 {i}"));
			var next = members.ToArray();
			next[3] = Thinking(text);
			view.Update(process with { ProcessEntries = next });
			Pump(view.View);
			view.SyncBodyMetrics();
			Pump(view.View);
		}
		Assert.InRange(anchor.Bounds.Y, y - 1, y + 1);
		Assert.InRange(body.VerticalOffset, offset - 1, offset + 1);
		UiLifetime.Release(view.View);
	}

	[Fact]
	public void StreamingThinkingInTheTimelineGrowsTheConversationExtent()
	{
		ConversationScroll? conversation = null;
		TextFocusScrollGuard? guard = null;
		using var timeline = new ConversationTimeline(entry => entry.Role switch
		{
			ChatRole.Process => new ExecutionProcessView(entry, value => new AssistantMessageView(value, _ => { }, guard!).View, guard!).View,
			_ => new AssistantMessageView(entry, _ => { }, guard!).View,
		});
		guard = new TextFocusScrollGuard(timeline.Scroll, anchor => conversation!.PreserveAnchor(anchor));
		conversation = new ConversationScroll(timeline.Scroll, _ => { });
		using (guard)
		using (conversation)
		{
			var rows = new List<ChatEntry>
			{
				new() { Role = ChatRole.User, Text = "question", EntryId = "u" },
				Process(Thinking("step 0")),
				new() { Role = ChatRole.Assistant, Text = "answer", EntryId = "a" },
			};
			timeline.SetEntries(rows);
			Pump(timeline.View);
			ExpandGroups(timeline.View);
			var thinking = Find<Expander>(timeline.View)
				.First(expander => HoverHints.Text(expander) is null or "" && expander.IsVisible);
			thinking.IsExpanded = true;
			Pump(timeline.View);

			var smooth = SmoothScroll.For(timeline.Scroll);
			var before = smooth.MaxVerticalOffset;
			var entry = Thinking("step 0");
			for (var step = 1; step <= 10; step++)
			{
				entry = Thinking(string.Join("\n\n", Enumerable.Range(0, step * 3 + 1).Select(i => $"reasoning paragraph {i}")));
				rows[1] = Process(entry);
				timeline.SetEntries(rows);
				conversation.FollowContent();
				Pump(timeline.View);
				conversation.FollowAfterLayout();
			}

			conversation.SyncExtentAfterLayout();
			Pump(timeline.View);
			Assert.True(smooth.MaxVerticalOffset > before + 20,
				$"conversation extent did not follow the streaming thinking: {before:0.#} -> {smooth.MaxVerticalOffset:0.#}");
			Assert.True(conversation.Following);
		}
	}

	[Fact]
	public void ExpandingTheLastStepRowGrowsTheGroupBodyScrollRange()
	{
		using var guard = new TextFocusScrollGuard(new ScrollViewer());
		var group = new ExecutionProcessView(new ChatEntry
		{
			Role = ChatRole.Process, Text = string.Empty, EntryId = "process",
			ProcessEntries = [.. Enumerable.Range(0, 26).Select(Tool)],
		}, entry => new ToolMessageView(entry, guard).View, guard);
		Pump(group.View);
		ExpandGroups(group.View);

		var body = BodyOf(group);
		var smooth = SmoothScroll.For(body);
		var before = smooth.MaxVerticalOffset;
		Assert.True(before > 0, "a 26-step group must be scrollable");

		// 展开最后一条已实现行：它在视口下方，变高只能靠 arrange 重测行高发现。
		var tools = Find<Expander>(group.View)
			.Where(expander => HoverHints.Text(expander) == Strings.ToolExpandHint)
			.OrderBy(expander => expander.Bounds.Y)
			.ToArray();
		var lastRow = tools[^1];
		lastRow.IsExpanded = true;
		Settle(group);

		var after = smooth.MaxVerticalOffset;
		var rowHost = (UIElement)lastRow.Parent!.Parent!;
		Assert.True(after > before + 20,
			$"group body extent did not grow: {before} -> {after}; row h {rowHost.Bounds.Height:0.#} desired {rowHost.DesiredSize.Height:0.#} content {lastRow.Content?.DesiredSize.Height:0.#} open {lastRow.IsExpanded}");

		// 展开出来的正文真的建出来了，而且整个新范围都能滚到（滚轮拖到底不会停在旧末端）。
		var expandedContent = lastRow.Content as UIElement;
		Assert.NotNull(expandedContent);
		Assert.True(expandedContent!.Bounds.Height > 100, $"expanded content height {expandedContent.Bounds.Height:0.#}");
		body.SetScrollOffsets(0, after);
		Pump(group.View);
		Assert.Equal(smooth.MaxVerticalOffset, body.VerticalOffset, 1);
	}

	[Fact]
	public void ExpandingTheLastStepRowInTheStackLayoutKeepsItsContentReachable()
	{
		using var guard = new TextFocusScrollGuard(new ScrollViewer());
		var group = new ExecutionProcessView(new ChatEntry
		{
			Role = ChatRole.Process, Text = string.Empty, EntryId = "process",
			ProcessEntries = [.. Enumerable.Range(0, 6).Select(Tool)],
		}, entry => new ToolMessageView(entry, guard).View, guard);
		Pump(group.View);
		ExpandGroups(group.View);

		var body = BodyOf(group);
		var lastRow = Find<Expander>(group.View)
			.Where(expander => HoverHints.Text(expander) == Strings.ToolExpandHint)
			.OrderByDescending(expander => expander.Bounds.Y)
			.First();
		lastRow.IsExpanded = true;
		Settle(group);
		body.SetScrollOffsets(0, SmoothScroll.For(body).MaxVerticalOffset);
		Pump(group.View);
		Assert.True(lastRow.Bounds.Bottom <= body.Bounds.Bottom + 1,
			"the expanded last step must stay reachable inside the group body");
	}

	[Fact]
	public void ClickingLongExpandedThinkingDoesNotJumpTheGroupBodyOrLoseItsMarkdown()
	{
		using var guard = new TextFocusScrollGuard(new ScrollViewer());
		var thinkingText = string.Join("\n\n", Enumerable.Range(0, 50).Select(index =>
			$"思考段落 {index} " + string.Join(" ", Enumerable.Repeat("内容", 20)) + $" [链接](https://example.com/{index})"));
		ChatEntry Assistant(string text) => new()
		{
			Role = ChatRole.Assistant, Text = string.Empty, Thinking = text,
			EntryId = "live", Timestamp = 1, IsStreaming = true, IsThinking = true,
		};
		var tools = Enumerable.Range(0, 30).Select(Tool).ToArray();
		var process = new ChatEntry
		{
			Role = ChatRole.Process, Text = string.Empty, EntryId = "process", IsStreaming = true,
			ProcessEntries = [.. tools, Assistant(thinkingText)],
		};
		var view = new ExecutionProcessView(process, entry => entry.Role == ChatRole.Assistant
			? new AssistantMessageView(entry, _ => { }, guard).View
			: new ToolMessageView(entry, guard).View, guard);
		using var window = new Window().Content(view.View);
		Pump(view.View);
		ExpandGroups(view.View);
		var body = BodyOf(view);
		var smooth = SmoothScroll.For(body);
		var list = Find<ItemsControl>(view.View).Single();
		list.ScrollIntoView(list.ItemsSource.Count - 1);
		Pump(view.View);
		view.SyncBodyMetrics();
		Pump(view.View);
		var thinkingFold = Find<Expander>(body)
			.Last(expander => HoverHints.Text(expander) is null or "");
		thinkingFold.IsExpanded = true;
		Settle(view);
		var markdown = Assert.Single(Find<MarkdownView>(view.View), candidate => candidate.Source == thinkingText);
		guard.Track(markdown);

		// Put the middle of the long thinking Markdown in the middle of the group body.
		var desired = Math.Clamp(
			markdown.Bounds.Y - body.Bounds.Y + markdown.Bounds.Height / 2 - body.ViewportHeight / 2,
			0, smooth.MaxVerticalOffset);
		body.SetScrollOffsets(0, desired);
		Settle(view);
		var offset = body.VerticalOffset;
		var paragraph = Find<MarkdownParagraphView>(markdown)
			.First(candidate => candidate.Bounds.Bottom > body.Bounds.Y && candidate.Bounds.Y < body.Bounds.Bottom);
		Assert.True(markdown.BeginSelectionAtForTest(new Point(paragraph.Bounds.X + 2, paragraph.Bounds.Y + 4)));

		// Clicking a MarkdownView focuses the whole (viewport-taller) view. MewUI's focus-into-view
		// would scroll the group body to the view's top/bottom, jumping the progress and temporarily
		// releasing the virtualized step row. The focus guard must preserve every ancestor viewport.
		Assert.True(markdown.Focus());
		Pump(view.View);
		view.SyncBodyMetrics();
		Pump(view.View);

		Assert.InRange(body.VerticalOffset, offset - 1, offset + 1);
		Assert.True(markdown.RealizedBlockCount > 0, "clicking the thinking text must not blank the Markdown");
		Assert.Contains(Find<MarkdownParagraphView>(markdown),
			candidate => candidate.Bounds.Height > 0 && !string.IsNullOrEmpty(candidate.Text));

		// Link paragraphs are independent focus targets; they must preserve the same ancestor
		// viewports instead of letting focus-into-view move the group body.
		var linkParagraph = Find<MarkdownParagraphView>(markdown)
			.First(candidate => candidate.Focusable);
		Assert.True(linkParagraph.Focus());
		Pump(view.View);
		view.SyncBodyMetrics();
		Pump(view.View);
		Assert.InRange(body.VerticalOffset, offset - 1, offset + 1);
		Assert.True(markdown.RealizedBlockCount > 0);
		UiLifetime.Release(view.View);
	}

}
