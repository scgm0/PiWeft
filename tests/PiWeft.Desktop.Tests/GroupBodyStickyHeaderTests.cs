using System.Reflection;
using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Xunit;
using static PiWeft.Desktop.Tests.VisualTree;

namespace PiWeft.Desktop.Tests;

/// <summary>
/// 组体内展开项的标题吸顶（对齐 PiDeck 的 processEntryStickyPolicy）：
/// 只有「已展开 + 比组体视口更高」的步骤才会在跨过视口顶边时把标题留在那里，
/// 末尾越过视口后随内容退场；会话里的同名视图不吸顶。
/// </summary>
[Collection("mewui-controls")]
public sealed class GroupBodyStickyHeaderTests
{
	private static void Pump(UIElement view, double width = 560, double height = 420)
	{
		view.Measure(new Size(width, height));
		view.Arrange(new Rect(0, 0, width, height));
	}

	private static ChatEntry Tool(int index, int lines) => new()
	{
		Role = ChatRole.Tool,
		EntryId = $"tool/call-{index}",
		ToolName = "read",
		ToolCallId = $"call-{index}",
		ToolArguments = """{"path":"src/App.cs"}""",
		Text = string.Join('\n', Enumerable.Range(0, lines).Select(line => $"output {index} line {line}")),
		DurationMs = 12,
	};

	/// <summary>补足短步骤：收起长项之后组体仍然装不下一屏，才能验证「回到开头」的落位。</summary>
	private static ChatEntry[] Steps(params ChatEntry[] members) =>
		[.. members, .. Enumerable.Range(100, 12).Select(index => Tool(index, 1))];

	private sealed class Fixture
	{
		public required ExecutionProcessView View { get; init; }
		public required ScrollViewer Body { get; init; }
		public required SmoothScroll Smooth { get; init; }
		public required List<ChatEntry> Members { get; init; }
		public required ChatEntry Process { get; init; }

		public double ViewportTop => Body.Bounds.Y;

		public void Apply(bool streaming = true)
		{
			View.Update(Process with { ProcessEntries = [.. Members], IsStreaming = streaming });
			Settle();
		}

		/// <summary>窗口里这一步由 Render 优先级的回调完成；无窗口 fixture 手动驱动同一收敛。</summary>
		public void Settle()
		{
			Pump(View.View);
			View.SyncBodyMetrics();
			Pump(View.View);
			View.SyncBodyMetrics();
			Pump(View.View);
		}

		/// <summary>读者的阅读定位（不是程序化滚动）：同时解除跟底，流式增长不会再把位置拽回末尾。</summary>
		public void ScrollTo(double offset)
		{
			Smooth.RestoreReadingOffset(offset);
			Settle();
		}

		public void ScrollBody(double delta) => ScrollTo(Body.VerticalOffset + delta);

		/// <summary>组体停在末尾：几何上进入「置底即跟随」。</summary>
		public void FollowToEnd()
		{
			Smooth.JumpToEnd();
			Settle();
		}

		/// <summary>把这一项的顶部滚到视口顶边上方 <paramref name="distance"/> 处（距离越大 = 越过越多）。</summary>
		public void ScrollStepPastTop(StickyExpander step, double distance) =>
			ScrollBody(step.Bounds.Y - ViewportTop + distance);
	}

	private static Fixture Build(params ChatEntry[] members)
	{
		using var guard = new TextFocusScrollGuard(new ScrollViewer());
		var entries = members.ToList();
		var process = new ChatEntry
		{
			Role = ChatRole.Process, Text = string.Empty, EntryId = "process/sticky", IsStreaming = true,
			ProcessEntries = [.. entries],
		};
		var view = new ExecutionProcessView(process,
			entry => entry.Role == ChatRole.Tool
				? new ToolMessageView(entry, guard).View
				: new AssistantMessageView(entry, _ => { }, guard).View, guard);
		Pump(view.View);
		// 外层「执行过程」与本轮的过程组都要展开，组体才会真正建出来。
		foreach (var expander in Find<Expander>(view.View)
			.Where(expander => !expander.IsExpanded &&
				HoverHints.Text(expander) is Strings.ExecutionExpandHint or Strings.ExecutionGroupHint)
			.ToArray())
		{
			expander.IsExpanded = true;
		}
		Pump(view.View);

		var body = Find<ScrollViewer>(view.View).Single(viewport => viewport.IsVisible && viewport.Bounds.Height > 0 &&
			viewport.VerticalScroll != ScrollMode.Disabled);
		var fixture = new Fixture
		{
			View = view, Body = body, Smooth = SmoothScroll.For(body),
			Members = entries, Process = process,
		};
		fixture.Settle();
		return fixture;
	}

	private static UIElement HeaderOf(StickyExpander step) => Assert.IsAssignableFrom<UIElement>(step.Header);

	private static Button ReturnToStartOf(StickyExpander step) =>
		Find<Button>(HeaderOf(step)).Single(button => HoverHints.Text(button) == Strings.StepBackToStart);

	private static void Click(Button button) =>
		typeof(Button).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(button, null);

	[Fact]
	public void ExpandedStepTallerThanTheBodyPinsItsHeaderToTheBodyTop()
	{
		var fixture = Build(Steps(Tool(0, 2), Tool(1, 40), Tool(2, 30)));
		try
		{
			var steps = Find<StickyExpander>(fixture.Body).ToArray();
			var step = steps[1];
			step.IsExpanded = true;
			fixture.Settle();
			var header = HeaderOf(step);
			Assert.False(step.HeaderPinned, "the step sits at its natural position until it crosses the body top");
			Assert.True(header.Bounds.Y > fixture.ViewportTop + 1);

			// 让这一项跨过组体顶边：标题贴住视口顶边，正文继续滚。
			fixture.ScrollStepPastTop(step, 80);
			Assert.True(step.HeaderPinned, "a tall expanded step keeps its header at the body top");
			Assert.InRange(header.Bounds.Y, fixture.ViewportTop - 1, fixture.ViewportTop + 1);
			// 正文仍在标题下方滚动：标题压住的是它自己的内容，不是别处。
			Assert.True(step.Bounds.Y < header.Bounds.Y);
			Assert.True(step.Bounds.Bottom > header.Bounds.Bottom);

			// 下一项也是长项：本项末尾越过视口顶边后，标题随本项退场，改由下一项接棒。
			var next = steps[2];
			next.IsExpanded = true;
			fixture.Settle();
			fixture.ScrollBody(step.Bounds.Bottom - fixture.ViewportTop + 32);
			Assert.False(step.HeaderPinned, "the header leaves with the end of its step");
			Assert.True(header.Bounds.Y < fixture.ViewportTop - 1,
				$"the header must leave with the end of its step (header {header.Bounds.Y:0.#}, body {fixture.ViewportTop:0.#})");
			Assert.True(next.HeaderPinned, "the next tall step takes over the pin");
			Assert.InRange(HeaderOf(next).Bounds.Y, fixture.ViewportTop - 1, fixture.ViewportTop + 1);
		}
		finally
		{
			UiLifetime.Release(fixture.View.View);
		}
	}

	[Fact]
	public void ShortExpandedStepNeverPins()
	{
		// 展开后仍装得下一屏的步骤永远不会跨过视口顶边，不进入吸顶态。
		var fixture = Build(Steps(Tool(0, 1), Tool(1, 40)));
		try
		{
			var step = Find<StickyExpander>(fixture.Body).First();
			Find<StickyExpander>(fixture.Body).ElementAt(1).IsExpanded = true;
			fixture.Settle();
			// 从组体顶部开始：短步骤的标题这时还在视口里。
			fixture.ScrollTo(0);
			step.IsExpanded = true;
			fixture.Settle();
			var header = HeaderOf(step);
			var natural = header.Bounds.Y;
			Assert.False(step.HeaderPinned);

			fixture.ScrollStepPastTop(step, 40);
			Assert.False(step.HeaderPinned);
			Assert.False(step.HeaderDisplaced);
			Assert.True(header.Bounds.Y < fixture.ViewportTop - 1,
				$"a short step scrolls away like normal content (header {header.Bounds.Y:0.#}, body {fixture.ViewportTop:0.#})");
			Assert.True(header.Bounds.Y < natural);
		}
		finally
		{
			UiLifetime.Release(fixture.View.View);
		}
	}

	[Fact]
	public void PinnedHeaderOnlyOffersReturnToStartWhilePinned()
	{
		var fixture = Build(Steps(Tool(0, 2), Tool(1, 40)));
		try
		{
			var step = Find<StickyExpander>(fixture.Body).ElementAt(1);
			step.IsExpanded = true;
			fixture.Settle();
			var button = ReturnToStartOf(step);
			// 未吸顶时图标常驻占位但不可见、不可点：标题宽度不会因为图标出现而抖动。
			Assert.Equal(0, button.Opacity);
			Assert.False(button.IsHitTestVisible);

			fixture.ScrollStepPastTop(step, 80);
			Assert.True(step.HeaderPinned);
			Assert.Equal(1, button.Opacity);
			Assert.True(button.IsHitTestVisible);
		}
		finally
		{
			UiLifetime.Release(fixture.View.View);
		}
	}

	[Fact]
	public void ReturnToStartAlignsTheStepWithTheBodyTopAndStopsFollowing()
	{
		var fixture = Build(Steps(Tool(0, 2), Tool(1, 40)));
		try
		{
			// 组体停在末尾（流式中的常态）→ 几何上是跟随态。
			fixture.FollowToEnd();
			Assert.True(fixture.Smooth.IsFollowingGrowth);

			var step = Find<StickyExpander>(fixture.Body).ElementAt(1);
			step.IsExpanded = true;
			fixture.Settle();
			fixture.FollowToEnd();
			fixture.ScrollStepPastTop(step, 60);
			Assert.True(step.HeaderPinned);
			var start = fixture.Body.VerticalOffset + (step.Bounds.Y - fixture.ViewportTop);

			Click(ReturnToStartOf(step));
			fixture.Settle();
			Assert.InRange(fixture.Body.VerticalOffset, start - 1, start + 1);
			Assert.False(fixture.Smooth.IsFollowingGrowth, "returning to the step start is a reading move, not a follow request");
			Assert.False(step.HeaderPinned, "the header is back in place at its step start");
		}
		finally
		{
			UiLifetime.Release(fixture.View.View);
		}
	}

	[Fact]
	public void CollapsingAPinnedStepLeavesItsHeaderAtTheBodyTop()
	{
		var fixture = Build(Steps(Tool(0, 2), Tool(1, 40)));
		try
		{
			var steps = Find<StickyExpander>(fixture.Body).ToArray();
			var step = steps[1];
			step.IsExpanded = true;
			fixture.Settle();
			fixture.ScrollStepPastTop(step, 120);
			Assert.True(step.HeaderPinned);

			// 从吸顶标题收起：这一项的开头落回视口顶部，而不是留在已经滚走的行顶上（那样标题会直接消失）。
			step.IsExpanded = false;
			fixture.Settle();
			Assert.False(step.HeaderDisplaced);
			Assert.False(step.HeaderPinned);
			Assert.InRange(HeaderOf(step).Bounds.Y - fixture.ViewportTop, 0, 4);
		}
		finally
		{
			UiLifetime.Release(fixture.View.View);
		}
	}

	[Fact]
	public void ClickingThePinnedHeaderTogglesTheStepAndCollapsesToItsStart()
	{
		var fixture = Build(Steps(Tool(0, 2), Tool(1, 40)));
		try
		{
			var step = Find<StickyExpander>(fixture.Body).ElementAt(1);
			step.IsExpanded = true;
			fixture.Settle();
			fixture.ScrollStepPastTop(step, 120);
			Assert.True(step.HeaderPinned);

			// 吸顶位置必须真的能点到标题：标题命中优先于压在下面的正文。
			var header = HeaderOf(step);
			var headerHit = step.HitTest(new Point(step.Bounds.X + 60, fixture.ViewportTop + 6));
			Assert.NotNull(headerHit);
			Assert.True(headerHit!.IsDescendantOf(header), $"the pinned header must win hit testing: {headerHit.GetType().Name}");
			var bodyHit = step.HitTest(new Point(step.Bounds.X + 60, fixture.ViewportTop + 80));
			Assert.NotNull(bodyHit);
			Assert.False(bodyHit!.IsDescendantOf(header));

			// 标题在原位时基类会自己判定；吸顶后标题在视口顶部，必须按吸顶位置算一次开合。
			var args = new MouseEventArgs(new Point(120, 70), new Point(120, 70), MouseButton.Left);
			Invoke(HeaderOf(step), "OnMouseDown", args);
			Invoke(step, "OnMouseDown", args);
			Assert.True(args.Handled);
			Assert.False(step.IsExpanded, "clicking the pinned header collapses the step");

			fixture.Settle();
			Assert.InRange(HeaderOf(step).Bounds.Y - fixture.ViewportTop, 0, 4);

			// 点正文（不是标题）不开合。
			step.IsExpanded = true;
			fixture.Settle();
			fixture.ScrollStepPastTop(step, 120);
			var body = new MouseEventArgs(new Point(120, 200), new Point(120, 200), MouseButton.Left);
			Invoke(step, "OnMouseDown", body);
			Assert.True(step.IsExpanded);
		}
		finally
		{
			UiLifetime.Release(fixture.View.View);
		}
	}

	private static void Invoke(Element element, string method, MouseEventArgs args) =>
		element.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(element, [args]);

	[Fact]
	public void ReturnToStartClicksDoNotCollapseTheStep()
	{
		var fixture = Build(Steps(Tool(0, 2), Tool(1, 40)));
		try
		{
			var step = Find<StickyExpander>(fixture.Body).ElementAt(1);
			step.IsExpanded = true;
			fixture.Settle();
			fixture.ScrollStepPastTop(step, 60);
			Assert.True(step.HeaderPinned);

			// 点吸顶标题上的「回到开头」：按钮自己接管这次按下（Handled），折叠行不能顺手把这一项收起来。
			var button = ReturnToStartOf(step);
			var args = new MouseEventArgs(new Point(500, 70), new Point(500, 70), MouseButton.Left);
			Invoke(button, "OnMouseDown", args);
			Invoke(step, "OnMouseDown", args);
			Assert.True(step.IsExpanded);
			Click(button);
			fixture.Settle();
			Assert.True(step.IsExpanded);
			Assert.False(step.HeaderDisplaced);
		}
		finally
		{
			UiLifetime.Release(fixture.View.View);
		}
	}

	[Fact]
	public void StreamingThinkingStepAlsoPinsWhileItGrows()
	{
		var thinking = new ChatEntry
		{
			Role = ChatRole.Assistant, EntryId = "thinking/live/0", Timestamp = 1, Text = string.Empty,
			Thinking = string.Join("\n\n", Enumerable.Range(0, 40).Select(index => $"思考段落 {index}")),
			IsThinking = true, IsStreaming = true,
		};
		var fixture = Build(Steps(Tool(0, 2), thinking, Tool(2, 2)));
		try
		{
			var step = Find<StickyExpander>(fixture.Body).ElementAt(1);
			step.IsExpanded = true;
			fixture.Settle();
			// 从组体顶部开始读：这一项从自然位置被滚到视口上方。
			fixture.ScrollTo(0);
			fixture.ScrollStepPastTop(step, 80);
			Assert.True(step.HeaderPinned);

			// 思考继续变长：标题仍贴在组体顶部。
			fixture.Members[1] = thinking with { Thinking = thinking.Thinking + "\n\n追加的思考段落" };
			fixture.Apply();
			Assert.True(step.HeaderPinned);
			Assert.InRange(HeaderOf(step).Bounds.Y, fixture.ViewportTop - 1, fixture.ViewportTop + 1);
		}
		finally
		{
			UiLifetime.Release(fixture.View.View);
		}
	}

	[Fact]
	public void ThinkingOutsideAGroupBodyNeverPins()
	{
		// 会话里的同一套视图：没有组体视口可挂，滚动时标题原样滚走。
		var host = new Border();
		var conversation = new ScrollViewer().Content(host).AutoVerticalScroll().NoHorizontalScroll().SmoothScrolling();
		using var focus = new TextFocusScrollGuard(conversation);
		var thinking = new ChatEntry
		{
			Role = ChatRole.Assistant, EntryId = "live", Timestamp = 1, Text = string.Empty,
			Thinking = string.Join("\n\n", Enumerable.Range(0, 60).Select(index => $"思考中的一行 {index}")),
			IsThinking = true, IsStreaming = true,
		};
		var view = new AssistantMessageView(thinking, _ => { }, focus);
		host.Child(new StackPanel().Children(new Border().Height(600), view.View, new Border().Height(600)));
		Pump(conversation, 560, 420);
		var step = Assert.Single(Find<StickyExpander>(conversation));
		step.IsExpanded = true;
		Pump(conversation, 560, 420);
		var header = HeaderOf(step);
		var natural = header.Bounds.Y;
		conversation.SetScrollOffsets(0, SmoothScroll.For(conversation).MaxVerticalOffset);
		Pump(conversation, 560, 420);
		Pump(conversation, 560, 420);
		Assert.False(step.HeaderPinned);
		Assert.False(step.HeaderDisplaced);
		Assert.True(header.Bounds.Y < natural);
		view.Dispose();
	}
}
