using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Xunit;
using static PiWeft.Desktop.Tests.VisualTree;

namespace PiWeft.Desktop.Tests;

/// <summary>
/// 组体「滚动条置底 ⇒ 跟随最新步骤」。真实对话里思考与 bash 步骤交替、中途还会展开/折叠，
/// 这里按同样的顺序驱动，钉住跟随不会被 presenter 切换、行高重测或展开动作永久打断。
/// </summary>
[Collection("mewui-controls")]
public sealed class GroupBodyFollowTests
{
	private static void Pump(UIElement view, double width = 560, double height = 420)
	{
		view.Measure(new Size(width, height));
		view.Arrange(new Rect(0, 0, width, height));
	}

	private static ChatEntry Thinking(string text, bool thinking = true) => new()
	{
		Role = ChatRole.Assistant,
		Text = string.Empty,
		Thinking = text,
		EntryId = "thinking/live/0",
		Timestamp = 1,
		IsStreaming = thinking,
		IsThinking = thinking,
	};

	private static ChatEntry Bash(int index, bool running = false) => new()
	{
		Role = ChatRole.Tool,
		EntryId = $"tool/call-{index}",
		ToolName = "bash",
		ToolCallId = $"call-{index}",
		ToolArguments = """{"command":"ls -la"}""",
		Text = running ? string.Empty : string.Join('\n', Enumerable.Range(0, 6).Select(line => $"output {index} line {line}")),
		IsStreaming = running,
		DurationMs = running ? null : 12,
	};

	private sealed class Fixture
	{
		public required ExecutionProcessView View { get; init; }
		public required ScrollViewer Body { get; init; }
		public required SmoothScroll Smooth { get; init; }
		public required List<ChatEntry> Members { get; init; }
		public required ChatEntry Process { get; init; }

		public void Apply(bool streaming = true)
		{
			View.Update(Process with { ProcessEntries = [.. Members], IsStreaming = streaming });
			Pump(View.View);
			View.SyncBodyMetrics();
			Pump(View.View);
			View.SyncBodyMetrics();
			Pump(View.View);
		}

		public void ScrollToEnd()
		{
			Body.SetScrollOffsets(0, Smooth.MaxVerticalOffset);
			Pump(View.View);
			View.SyncBodyMetrics();
			Pump(View.View);
		}

		public void ScrollToStart()
		{
			Body.SetScrollOffsets(0, 0);
			Pump(View.View);
			View.SyncBodyMetrics();
			Pump(View.View);
		}

		/// <summary>
		/// 读者的滚轮/滚动条位移。直接落位而不跑补间动画：动画由真实帧时钟驱动，
		/// 测试宿主里没有消息循环，结果不稳定；这里只验证「位移之后跟随态如何判定」。
		/// </summary>
		public void ReaderScroll(double delta)
		{
			Body.SetScrollOffsets(0, Math.Clamp(Body.VerticalOffset + delta, 0, Smooth.MaxVerticalOffset));
			Pump(View.View);
			View.SyncBodyMetrics();
			Pump(View.View);
		}

		public double DistanceFromEnd => Smooth.MaxVerticalOffset - Body.VerticalOffset;
	}

	private static Fixture Build(IEnumerable<ChatEntry> initial, string title = "q")
	{
		using var guard = new TextFocusScrollGuard(new ScrollViewer());
		var members = initial.ToList();
		var process = new ChatEntry
		{
			Role = ChatRole.Process, Text = string.Empty, EntryId = $"process/{title}", IsStreaming = true,
			ProcessEntries = [.. members],
		};
		var view = new ExecutionProcessView(process, entry => entry.Role == ChatRole.Tool
			? new ToolMessageView(entry, guard).View
			: new AssistantMessageView(entry, _ => { }, guard).View, guard);
		Pump(view.View);
		foreach (var expander in Find<Expander>(view.View)
			.Where(expander => !expander.IsExpanded &&
				HoverHints.Text(expander) is Strings.ExecutionExpandHint or Strings.ExecutionGroupHint)
			.ToArray())
		{
			expander.IsExpanded = true;
		}
		Pump(view.View);
		view.SyncBodyMetrics();
		Pump(view.View);

		var body = Find<ScrollViewer>(view.View).Single(viewport => viewport.IsVisible && viewport.Bounds.Height > 0 &&
			viewport.VerticalScroll != ScrollMode.Disabled);
		return new Fixture
		{
			View = view,
			Body = body,
			Smooth = SmoothScroll.For(body),
			Members = members,
			Process = process,
		};
	}

	[Fact]
	public void BottomFollowSurvivesTheStackToVirtualSwitchAndNewSteps()
	{
		// 24 步时组体还是栈布局，第 25 步会切到虚拟化列表并重建组体。
		var fixture = Build(Enumerable.Range(0, 24).Select(index => Bash(index)));
		try
		{
			fixture.ScrollToEnd();
			Assert.True(fixture.Smooth.IsFollowingGrowth, "scrolling to the group end must lock growth follow");

			fixture.Members.Add(Bash(24, running: true));
			fixture.Apply();
			Assert.True(fixture.Smooth.IsFollowingGrowth, "switching to the virtualized body must keep following");
			Assert.InRange(fixture.DistanceFromEnd, 0, 1);

			for (var index = 25; index < 30; index++)
			{
				fixture.Members.Add(Bash(index, running: true));
				fixture.Apply();
				Assert.True(fixture.Smooth.IsFollowingGrowth, $"step {index} must keep following");
				Assert.InRange(fixture.DistanceFromEnd, 0, 1);
			}
		}
		finally
		{
			UiLifetime.Release(fixture.View.View);
		}
	}

	[Fact]
	public void BottomFollowKeepsUpWithAStreamingThinkingStep()
	{
		var fixture = Build([.. Enumerable.Range(0, 20).Select(index => Bash(index)), Thinking("初始思考")]);
		try
		{
			fixture.ScrollToEnd();
			Assert.True(fixture.Smooth.IsFollowingGrowth);

			// 思考是组内最后一步：持续增量时组体应当一直贴底。
			for (var step = 1; step <= 8; step++)
			{
				var text = string.Join("\n\n", Enumerable.Range(0, step * 3).Select(index => $"思考段落 {index}"));
				fixture.Members[^1] = Thinking(text);
				fixture.Apply();
				Assert.True(fixture.Smooth.IsFollowingGrowth, $"thinking delta {step} must keep following");
				Assert.InRange(fixture.DistanceFromEnd, 0, 1);
			}

			// 新的 bash 步骤接在思考之后：仍然跟随。
			fixture.Members.Add(Bash(100, running: true));
			fixture.Apply();
			Assert.True(fixture.Smooth.IsFollowingGrowth);
			Assert.InRange(fixture.DistanceFromEnd, 0, 1);
		}
		finally
		{
			UiLifetime.Release(fixture.View.View);
		}
	}

	[Fact]
	public void ExpandingAndCollapsingThinkingAboveTheViewportDoesNotPoisonFollowing()
	{
		var fixture = Build([Thinking("初始思考"), .. Enumerable.Range(0, 24).Select(index => Bash(index))]);
		try
		{
			// 读者滚到顶部去展开思考：几何上离开末尾，跟随暂停。
			fixture.ScrollToStart();
			Assert.False(fixture.Smooth.IsFollowingGrowth, "reading at the top must not follow the tail");

			var thinking = Find<Expander>(fixture.Body).Single(expander => HoverHints.Text(expander) is null or "");
			thinking.IsExpanded = true;
			fixture.Apply();
			Assert.False(fixture.Smooth.IsFollowingGrowth, "expanding thinking while browsing must not yank to the tail");

			thinking = Find<Expander>(fixture.Body).Single(expander => HoverHints.Text(expander) is null or "");
			thinking.IsExpanded = false;
			fixture.Apply();
			Assert.False(fixture.Smooth.IsFollowingGrowth);

			// 回到组体底部后必须重新锁底，并继续跟住后续步骤。
			fixture.ScrollToEnd();
			Assert.True(fixture.Smooth.IsFollowingGrowth, "returning to the bottom must resume following");

			fixture.Members.Add(Bash(300, running: true));
			fixture.Apply();
			Assert.True(fixture.Smooth.IsFollowingGrowth);
			Assert.InRange(fixture.DistanceFromEnd, 0, 1);
		}
		finally
		{
			UiLifetime.Release(fixture.View.View);
		}
	}

	[Fact]
	public void ExpandingAToolStepPausesFollowUntilTheReaderReturnsToTheBottom()
	{
		var fixture = Build(Enumerable.Range(0, 26).Select(index => Bash(index)), "tool");
		try
		{
			fixture.ScrollToEnd();
			Assert.True(fixture.Smooth.IsFollowingGrowth);

			// 展开一个 bash 步骤：阅读行为，跟随暂停、预览行留在原位。
			var tool = Find<Expander>(fixture.Body)
				.Last(expander => HoverHints.Text(expander) == Strings.ToolExpandHint);
			var previewTop = tool.Bounds.Y;
			tool.IsExpanded = true;
			fixture.Apply();
			Assert.False(fixture.Smooth.IsFollowingGrowth, "expanding a tool step must pause following");
			Assert.InRange(tool.Bounds.Y, previewTop - 1, previewTop + 1);

			// 暂停期间新步骤到达：不能把读者拽回末尾。
			var reading = fixture.Body.VerticalOffset;
			fixture.Members.Add(Bash(201, running: true));
			fixture.Apply();
			Assert.False(fixture.Smooth.IsFollowingGrowth);
			Assert.InRange(fixture.Body.VerticalOffset, reading - 1, reading + 1);

			// 读者滚回底部：跟随恢复。
			fixture.ScrollToEnd();
			Assert.True(fixture.Smooth.IsFollowingGrowth, "returning to the bottom must resume following");
			fixture.Members.Add(Bash(202, running: true));
			fixture.Apply();
			Assert.InRange(fixture.DistanceFromEnd, 0, 1);
		}
		finally
		{
			UiLifetime.Release(fixture.View.View);
		}
	}

	[Fact]
	public void RebuildingTheBodyAfterStepRowsAreRecycledStillFollows()
	{
		// 回归：曾经用「有步骤状态记录」判断恢复态，而虚拟化滚动本身就会写入记录，
		// 于是组体重建（栈→虚拟化、回收后重建）会误判成恢复并永久关闭跟底。
		var fixture = Build(Enumerable.Range(0, 40).Select(index => Bash(index)), "rebuild");
		try
		{
			fixture.ScrollToEnd();
			Assert.True(fixture.Smooth.IsFollowingGrowth);

			// 来回滚几次，让虚拟化行被回收并写入步骤状态。
			for (var pass = 0; pass < 3; pass++)
			{
				fixture.ScrollToStart();
				fixture.ScrollToEnd();
			}
			Assert.True(fixture.Smooth.IsFollowingGrowth, "scrolling back to the end must keep following");

			// 折叠再展开整组：组体被销毁重建，之后仍应跟随。
			var run = Find<Expander>(fixture.View.View).Single(expander => HoverHints.Text(expander) == Strings.ExecutionExpandHint);
			var group = Find<Expander>(run.Content!).Single(expander => HoverHints.Text(expander) == Strings.ExecutionGroupHint);
			group.IsExpanded = false;
			Pump(fixture.View.View);
			group.IsExpanded = true;
			Pump(fixture.View.View);
			fixture.View.SyncBodyMetrics();
			Pump(fixture.View.View);

			fixture.ScrollToEnd();
			Assert.True(fixture.Smooth.IsFollowingGrowth, "a rebuilt body must still follow when it sits at the end");
			fixture.Members.Add(Bash(900, running: true));
			fixture.Apply();
			Assert.InRange(fixture.DistanceFromEnd, 0, 1);
		}
		finally
		{
			UiLifetime.Release(fixture.View.View);
		}
	}

	[Fact]
	public void UpwardWheelEscapesAndReturningToTheBottomRelocks()
	{
		var fixture = Build(Enumerable.Range(0, 30).Select(index => Bash(index)), "wheel");
		try
		{
			fixture.ScrollToEnd();
			Assert.True(fixture.Smooth.IsFollowingGrowth);

			// 读者上滚：离开组体底部。
			fixture.ReaderScroll(-120);
			Assert.False(fixture.Smooth.IsFollowingGrowth, "an upward scroll must escape bottom follow");

			var reading = fixture.Body.VerticalOffset;
			fixture.Members.Add(Bash(203, running: true));
			fixture.Apply();
			Assert.False(fixture.Smooth.IsFollowingGrowth);
			Assert.InRange(fixture.Body.VerticalOffset, reading - 1, reading + 1);

			// 向下滚回底部：重新锁底并继续跟随。
			fixture.ReaderScroll(400);
			Assert.InRange(fixture.DistanceFromEnd, 0, 1);
			Assert.True(fixture.Smooth.IsFollowingGrowth, "scrolling back to the bottom must re-lock following");
			fixture.Members.Add(Bash(204, running: true));
			fixture.Apply();
			Assert.InRange(fixture.DistanceFromEnd, 0, 1);
		}
		finally
		{
			UiLifetime.Release(fixture.View.View);
		}
	}
}
