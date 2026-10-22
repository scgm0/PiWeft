using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Xunit;

namespace PiWeft.Desktop.Tests;

[Collection("mewui-controls")]
public sealed class SmoothScrollTests
{
	private static ScrollViewer View() => new ScrollViewer().Content(new Border().Width(1200).Height(2000))
		.AutoVerticalScroll().AutoHorizontalScroll();

	private static void Layout(ScrollViewer view)
	{
		view.Measure(new Size(200, 200));
		view.InvalidateArrange();
		view.Arrange(new Rect(0, 0, 200, 200));
	}

	/// <summary>
	/// 推进一帧：新的衰减物理按真实经过时间计算，假时钟只负责触发 Tick，
	/// 所以帧间隔必须真的存在（否则 dt≈0，画面不动）。
	/// </summary>
	private static void Frame(int milliseconds = 16)
	{
		Thread.Sleep(milliseconds);
		AnimationFrames.Advance(milliseconds);
	}

	/// <summary>持续推进到落位：指数衰减每帧只走完剩余距离的一部分。</summary>
	private static void Settle(SmoothScroll smooth, int frames = 60)
	{
		for (var index = 0; index < frames && smooth.IsAnimating; index++)
		{
			Frame();
		}
	}

	private static void KeyDown(Element view, KeyEventArgs args) =>
		typeof(UIElement).GetMethod("OnKeyDown", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
			.Invoke(view, [args]);

	[Fact]
	public void TreeViewportAndTextEditorsAnimateWithoutReplacingTheirContent()
	{
		var tree = new TreeView().ItemsSource(Enumerable.Range(0, 200).Select(i => new TreeViewNode($"Session {i}")).ToArray());
		tree.SmoothScrolling();
		ConversationPerformanceTests.Layout(tree, 240, 200);
		var viewport = VisualTree.Find<ScrollViewer>(tree).Single();
		using var smoothTree = SmoothScroll.For(viewport);
		smoothTree.RouteWheel(new MouseWheelEventArgs(default, default, new Vector(0, -1), false, false, false, ModifierKeys.None));
		Assert.Equal(0, viewport.VerticalOffset);
		Frame(45);
		Assert.InRange(viewport.VerticalOffset, 1, ThemeManager.DefaultMetrics.ScrollWheelStep - 1);
		Settle(smoothTree);

		var text = new MultiLineTextBox().Text(string.Join("\n", Enumerable.Range(0, 200).Select(i => $"Line {i}"))).SmoothScrolling();
		ConversationPerformanceTests.Layout(text, 240, 200);
		using var smoothText = SmoothScroll.For(text);
		var original = text.Text;
		smoothText.RouteWheel(new MouseWheelEventArgs(default, default, new Vector(0, -1), false, false, false, ModifierKeys.None));
		Assert.Equal(0, text.VerticalOffset);
		Frame(45);
		Assert.InRange(text.VerticalOffset, 1, ThemeManager.DefaultMetrics.ScrollWheelStep - 1);
		Settle(smoothText);
		// 文本视口的末端带小数（行高累积），落位允许亚像素误差。
		Assert.Equal(ThemeManager.DefaultMetrics.ScrollWheelStep, text.VerticalOffset, 0.5);
		Assert.Equal(original, text.Text);
		text.ScrollToCaret();
		Assert.False(smoothText.IsAnimating);
	}

	[Fact]
	public void TrackpadDeltasApplyImmediatelyWhileWheelNotchesGlide()
	{
		var view = View();
		using var smooth = SmoothScroll.For(view);
		Layout(view);

		// 触控板（高精度小数刻度）：与浏览器一致，直接 1:1 落位，不排队动画。
		smooth.ScrollBy(0, 0.2);
		Assert.False(smooth.IsAnimating);
		Assert.True(view.VerticalOffset > 0, "a fractional trackpad delta must move the viewport immediately");

		// 鼠标滚轮（整格）：走 Chromium 曲线，一帧只走一部分，最终精确落位。
		var start = view.VerticalOffset;
		var step = ThemeManager.DefaultMetrics.ScrollWheelStep;
		smooth.ScrollBy(0, 1);
		Assert.True(smooth.IsAnimating);
		Frame(16);
		Assert.InRange(view.VerticalOffset - start, 0.5, step - 0.5);
		Settle(smooth);
		Assert.Equal(start + step, view.VerticalOffset, 0.5);
	}

	[Fact]
	public void NestedViewportsReleaseWheelAtTheirEdgesAndInstallationIsIdempotent()
	{
		var view = View().SmoothScrolling().SmoothScrolling();
		Layout(view);
		using var smooth = SmoothScroll.For(view);
		var up = new MouseWheelEventArgs(default, default, new Vector(0, 1), false, false, false, ModifierKeys.None);
		smooth.RouteWheel(up);
		Assert.False(up.Handled);
		var down = new MouseWheelEventArgs(default, default, new Vector(0, -1), false, false, false, ModifierKeys.None);
		smooth.RouteWheel(down);
		Assert.True(down.Handled);
		Settle(smooth);
		Assert.Equal(ThemeManager.DefaultMetrics.ScrollWheelStep, view.VerticalOffset);
		smooth.JumpToEnd();
		down = new MouseWheelEventArgs(default, default, new Vector(0, -1), false, false, false, ModifierKeys.None);
		smooth.RouteWheel(down);
		Assert.False(down.Handled);
	}

	[Fact]
	public void NestedViewportHandsTheWheelToTheOuterViewportAtItsEdge()
	{
		var inner = new ScrollViewer().Content(new Border().Height(1200))
			.AutoVerticalScroll().NoHorizontalScroll().MaxHeight(100).SmoothScrolling();
		var outer = new ScrollViewer()
			.Content(new StackPanel().Children(new Border().Height(400), inner, new Border().Height(400)))
			.AutoVerticalScroll().NoHorizontalScroll().SmoothScrolling();
		ConversationPerformanceTests.Layout(outer, 200, 200);
		using var innerSmooth = SmoothScroll.For(inner);
		using var outerSmooth = SmoothScroll.For(outer);
		var step = ThemeManager.DefaultMetrics.ScrollWheelStep;
		Assert.True(innerSmooth.MaxVerticalOffset > 0);

		var down = new MouseWheelEventArgs(default, default, new Vector(0, -1), false, false, false, ModifierKeys.None);
		innerSmooth.RouteWheel(down);
		Assert.True(down.Handled);
		Assert.Equal(step, innerSmooth.TargetVerticalOffset);
		Assert.False(outerSmooth.IsAnimating);

		// 内层到底：Viewport 自带的滚轮处理会在原地标记 Handled，所以必须由内层先交接给外层。
		innerSmooth.JumpToEnd();
		down = new MouseWheelEventArgs(default, default, new Vector(0, -1), false, false, false, ModifierKeys.None);
		innerSmooth.RouteWheel(down);
		Assert.True(down.Handled);
		Assert.Equal(step, outerSmooth.TargetVerticalOffset);
	}

	[Fact]
	public void WheelMovesOverMultipleFramesAndAccumulatesConsecutiveNotches()
	{
		var view = View();
		Layout(view);
		using var smooth = SmoothScroll.For(view);
		var step = ThemeManager.DefaultMetrics.ScrollWheelStep;
		smooth.ScrollBy(0, 1);
		Assert.Equal(0, view.VerticalOffset);
		Frame(45);
		Assert.InRange(view.VerticalOffset, 1, step - 1);
		smooth.ScrollBy(0, 1);
		Settle(smooth);
		Assert.Equal(step * 2, view.VerticalOffset);
		Assert.False(smooth.IsAnimating);
	}

	[Fact]
	public void ReversingWheelRespondsImmediatelyAndDoesNotConsumePendingForwardMotion()
	{
		var view = View();
		Layout(view);
		view.SetScrollOffsets(0, 500);
		using var smooth = SmoothScroll.For(view);
		smooth.ScrollBy(0, 5);
		Frame(45);
		var turningPoint = view.VerticalOffset;
		smooth.ScrollBy(0, -1);
		Settle(smooth);
		Assert.InRange(view.VerticalOffset, turningPoint - ThemeManager.DefaultMetrics.ScrollWheelStep - 1,
			turningPoint - ThemeManager.DefaultMetrics.ScrollWheelStep + 1);
	}

	[Fact]
	public void FractionalTrackpadInputAndShiftWheelPreserveTheirAxes()
	{
		var view = View();
		Layout(view);
		using var smooth = SmoothScroll.For(view);
		var args = new MouseWheelEventArgs(default, default, new Vector(-0.4, -0.2), false, false, false, ModifierKeys.None);
		smooth.RouteWheel(args);
		Assert.True(args.Handled);
		Settle(smooth);
		var step = ThemeManager.DefaultMetrics.ScrollWheelStep;
		Assert.Equal(step * 0.4, view.HorizontalOffset);
		Assert.Equal(step * 0.2, view.VerticalOffset);
		args = new MouseWheelEventArgs(default, default, new Vector(0, -1), false, false, false, ModifierKeys.Shift);
		smooth.RouteWheel(args);
		Settle(smooth);
		Assert.Equal(step * 1.4, view.HorizontalOffset);
		Assert.Equal(step * 0.2, view.VerticalOffset);
	}

	[Fact]
	public void ScrollTargetsAreClampedAndDirectOffsetsCancelAnimation()
	{
		var view = View();
		Layout(view);
		using var smooth = SmoothScroll.For(view);
		smooth.ScrollBy(0, 1000);
		Settle(smooth);
		Assert.Equal(smooth.MaxVerticalOffset, view.VerticalOffset);
		smooth.ScrollBy(0, -10);
		view.SetScrollOffsets(0, 300);
		Assert.False(smooth.IsAnimating);
		Frame(50);
		Assert.Equal(300, view.VerticalOffset);
	}

	[Fact]
	public void HiddenSurfacesAndDisposedControllersReleaseAnimations()
	{
		var view = View();
		Layout(view);
		using var smooth = SmoothScroll.For(view);
		smooth.ScrollBy(0, 1);
		view.IsVisible(false);
		AnimationFrames.Advance(50);
		Assert.False(smooth.IsAnimating);
		view.IsVisible(true);
		smooth.ScrollBy(0, 1);
		Assert.True(smooth.IsAnimating);
		smooth.Dispose();
		Assert.False(smooth.IsAnimating);
	}

	[Fact]
	public void WheelStreamAccumulatesIntoOneContinuousAnimation()
	{
		// 高频分数增量（触控板/惯性）与离散刻度共用同一条指数衰减曲线：
		// 连续输入只累加目标、动画不重启，所以速度是连续的（不从头再来一次缓动）。
		var view = View();
		Layout(view);
		using var smooth = SmoothScroll.For(view);
		var step = ThemeManager.DefaultMetrics.ScrollWheelStep;

		smooth.ScrollBy(0, 1);
		Assert.True(smooth.IsAnimating);
		Assert.Equal(step, smooth.TargetVerticalOffset);

		Frame(20);
		var midway = view.VerticalOffset;
		Assert.InRange(midway, 1, step - 1);

		// 动画进行中继续输入：目标累加，位置不回退也不重开缓动。
		smooth.ScrollBy(0, 1);
		smooth.ScrollBy(0, 1);
		Assert.True(smooth.IsAnimating);
		Assert.Equal(step * 3, smooth.TargetVerticalOffset);
		Frame(20);
		Assert.True(view.VerticalOffset > midway);

		Settle(smooth);
		Assert.Equal(step * 3, view.VerticalOffset);
		Assert.False(smooth.IsAnimating);

		// 反向输入立即从当前位置起算：待追赶的前向量不参与。
		var offset = view.VerticalOffset;
		smooth.ScrollBy(0, -1);
		Assert.True(smooth.IsAnimating);
		Assert.Equal(offset - step, smooth.TargetVerticalOffset);
		Settle(smooth);
		Assert.Equal(offset - step, view.VerticalOffset);
	}

	[Fact]
	public void ContentGrowthFollowsEndWhileUpwardIntentPinsTheReaderBeforeTheFirstFrame()
	{
		var view = View();
		var showLatest = false;
		using var conversation = new ConversationScroll(view, visible => showLatest = visible);
		Layout(view);
		var smooth = SmoothScroll.For(view);
		Assert.Equal(smooth.MaxVerticalOffset, view.VerticalOffset);
		Assert.True(conversation.Following);
		((Border)view.Content!).Height(2500);
		Layout(view);
		Assert.True(conversation.Following);
		Assert.Equal(smooth.MaxVerticalOffset, view.VerticalOffset);
		smooth.ScrollBy(0, -1);
		Assert.False(conversation.Following);
		conversation.FollowContent();
		Assert.True(smooth.IsAnimating);
		Settle(smooth);
		var readingOffset = view.VerticalOffset;
		Assert.True(showLatest);
		((Border)view.Content!).Height(3000);
		Layout(view);
		conversation.FollowContent();
		Assert.Equal(readingOffset, view.VerticalOffset);
		Assert.False(conversation.Following);
	}

	[Fact]
	public void ReturnToLatestIsAnimatedAndTracksGrowthDuringTheAnimation()
	{
		var view = View();
		using var conversation = new ConversationScroll(view, _ => { });
		Layout(view);
		view.SetScrollOffsets(0, 100);
		Assert.False(conversation.Following);
		conversation.ScrollToLatest();
		Assert.Equal(100, view.VerticalOffset);
		Frame(70);
		Assert.InRange(view.VerticalOffset, 101, SmoothScroll.For(view).MaxVerticalOffset - 1);
		((Border)view.Content!).Height(3000);
		Layout(view);
		Settle(SmoothScroll.For(view));
		Assert.Equal(SmoothScroll.For(view).MaxVerticalOffset, view.VerticalOffset);
		Assert.True(conversation.Following);
	}

	[Fact]
	public void ReachingTheEndWhileTheExtentGrowsResumesFollowing()
	{
		var view = View();
		using var conversation = new ConversationScroll(view, _ => { });
		Layout(view);
		view.SetScrollOffsets(0, 400);
		Layout(view);
		Assert.False(conversation.Following);

		// Grow the virtualized content, then drag to the new end in the same notification:
		// the maximum changes, but reaching the end is still the reader's follow instruction.
		((Border)view.Content!).Height(2600);
		Layout(view);
		// A direct offset write is what a scrollbar drag produces (not SmoothScroll's own apply).
		view.SetScrollOffsets(0, 1_000_000);
		Layout(view);
		Assert.True(conversation.Following);

		((Border)view.Content!).Height(3200);
		Layout(view);
		conversation.FollowContent();
		Layout(view);
		Assert.Equal(SmoothScroll.For(view).MaxVerticalOffset, view.VerticalOffset);
	}

	[Fact]
	public void FollowContentSteersARunningWheelAnimationToTheNewEnd()
	{
		var view = View();
		using var conversation = new ConversationScroll(view, _ => { });
		Layout(view);
		var smooth = SmoothScroll.For(view);
		smooth.JumpToEnd();
		view.SetScrollOffsets(0, smooth.MaxVerticalOffset - 10);
		Layout(view);

		// A downward wheel near the end keeps following and starts a short animation.
		smooth.ScrollBy(0, 1);
		Assert.True(conversation.Following);
		Assert.True(smooth.IsAnimating);

		// The reply grows while that animation runs; the follow request must not be dropped
		// just because an animation owns the offset.
		((Border)view.Content!).Height(2600);
		Layout(view);
		conversation.FollowContent();
		Settle(smooth);
		Assert.Equal(smooth.MaxVerticalOffset, view.VerticalOffset);
	}

	[Fact]
	public void FollowEndSteersAnAlreadyRunningAnimation()
	{
		var view = View();
		Layout(view);
		using var smooth = SmoothScroll.For(view);
		smooth.ScrollBy(0, 1);
		Assert.True(smooth.IsAnimating);
		((Border)view.Content!).Height(2800);
		Layout(view);
		smooth.FollowEnd();
		Settle(smooth);
		Assert.Equal(smooth.MaxVerticalOffset, view.VerticalOffset);
	}

	[Fact]
	public void ContentShrinkDoesNotResumeFollowingWhileBrowsing()
	{
		var view = View();
		using var conversation = new ConversationScroll(view, _ => { });
		Layout(view);
		view.SetScrollOffsets(0, 500);
		((Border)view.Content!).Height(1500);
		Layout(view);
		Assert.Equal(500, view.VerticalOffset);
		Assert.False(conversation.Following);
		((Border)view.Content!).Height(300);
		Layout(view);
		Assert.False(conversation.Following);
		var clamped = view.VerticalOffset;
		((Border)view.Content!).Height(2000);
		Layout(view);
		Assert.Equal(clamped, view.VerticalOffset);
	}

	[Fact]
	public void RepeatedKeyPressesStepImmediatelyWithoutRestartingTheCurve()
	{
		// 长按方向键时平台会重复发 KeyDown。每次重复都从零速重开曲线，会让位移只剩
		// 单次曲线的第一小段；重复键直接落位，速度才与浏览器一致。
		var view = View();
		Layout(view);
		using var smooth = SmoothScroll.For(view);

		KeyDown(view, new KeyEventArgs(Key.Down, 0));
		Assert.True(smooth.IsAnimating);
		Frame(20);
		var before = view.VerticalOffset;

		KeyDown(view, new KeyEventArgs(Key.Down, 0, ModifierKeys.None, isRepeat: true));
		Assert.False(smooth.IsAnimating);
		Assert.InRange(view.VerticalOffset - before, 39.5, 40.5);

		var after = view.VerticalOffset;
		KeyDown(view, new KeyEventArgs(Key.Down, 0, ModifierKeys.None, isRepeat: true));
		Assert.False(smooth.IsAnimating);
		Assert.InRange(view.VerticalOffset - after, 39.5, 40.5);
	}

	[Fact]
	public void WheelOverTheScrollBarUsesTheViewportStepAndAnimation()
	{
		// 滚动条自己处理滚轮时会按 SmallChange（24 DIP）立即跳；悬停在右缘不应该
		// 让速度和缓动都变成另一套。
		var view = View();
		Layout(view);
		using var smooth = SmoothScroll.For(view);
		var bar = VisualTree.Find<ScrollBar>(view).Single(candidate => candidate.Orientation == Orientation.Vertical);
		var wheel = new MouseWheelEventArgs(default, default, new Vector(0, -1), false, false, false, ModifierKeys.None);
		var method = typeof(ScrollBar).GetMethod("OnMouseWheel", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

		method.Invoke(bar, [wheel]);

		Assert.True(wheel.Handled);
		Assert.True(smooth.IsAnimating);
		Assert.Equal(ThemeManager.DefaultMetrics.ScrollWheelStep, smooth.TargetVerticalOffset, 1);
		Settle(smooth);
		Assert.Equal(ThemeManager.DefaultMetrics.ScrollWheelStep, view.VerticalOffset, 1);
	}

	[Fact]
	public void WheelBurstsAreAppliedOncePerFrame()
	{
		var view = View();
		Layout(view);
		using var smooth = SmoothScroll.For(view);
		var step = ThemeManager.DefaultMetrics.ScrollWheelStep;

		// 同一帧内的多个刻度只写一次偏移；目标先累计，视口不动。
		smooth.EnqueueWheelPixels(0, step);
		smooth.EnqueueWheelPixels(0, step);
		Assert.Equal(0, view.VerticalOffset);
		Assert.Equal(step * 2, smooth.TargetVerticalOffset, 1);

		smooth.FlushPendingWheel();
		Assert.True(smooth.IsAnimating);
		Settle(smooth);
		Assert.Equal(step * 2, view.VerticalOffset, 1);

		// 分数增量合并后仍走 1:1 落位，不产生补间；两次输入只发一次 ScrollChanged。
		var changes = 0;
		view.ScrollChanged += () => changes++;
		smooth.EnqueueWheelPixels(0, step * 0.2);
		smooth.EnqueueWheelPixels(0, step * 0.2);
		Assert.Equal(0, changes);
		smooth.FlushPendingWheel();
		Assert.False(smooth.IsAnimating);
		Assert.Equal(step * 2.4, view.VerticalOffset, 1);
		Assert.Equal(1, changes);
	}

	[Fact]
	public void FastTrackpadEventsStayImmediateUntilAWholeNotchArrives()
	{
		var view = View();
		Layout(view);
		using var smooth = SmoothScroll.For(view);
		var step = ThemeManager.DefaultMetrics.ScrollWheelStep;

		// 分数增量建立连续流；紧随其后的 0.7 格仍属于同一次触控板手势，不能重启缓动。
		smooth.ScrollBy(0, 0.2);
		Assert.False(smooth.IsAnimating);
		var before = view.VerticalOffset;
		smooth.ScrollBy(0, 0.7);
		Assert.False(smooth.IsAnimating);
		Assert.InRange(view.VerticalOffset - before, step * 0.7 - 0.5, step * 0.7 + 0.5);

		// 整格滚轮即使紧跟触控板，也必须回到缓动路径。
		smooth.ScrollBy(0, 1);
		Assert.True(smooth.IsAnimating);
		Assert.Equal(step * 1.9, smooth.TargetVerticalOffset, 1);
	}

	[Fact]
	public void KeyboardScrollingMovesTheViewportAndLeavesHandledKeysAlone()
	{
		var view = View();
		Layout(view);
		using var smooth = SmoothScroll.For(view);
		var pageDown = new KeyEventArgs(Key.PageDown, 0);
		typeof(UIElement).GetMethod("OnKeyDown", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
			.Invoke(view, [pageDown]);
		Assert.True(pageDown.Handled);
		Assert.True(smooth.IsAnimating);
		Settle(smooth);
		Assert.True(view.VerticalOffset > 0);

		var end = new KeyEventArgs(Key.End, 0);
		typeof(UIElement).GetMethod("OnKeyDown", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
			.Invoke(view, [end]);
		Assert.True(end.Handled);
		Settle(smooth);
		Assert.Equal(smooth.MaxVerticalOffset, view.VerticalOffset, 1);

		var before = view.VerticalOffset;
		var handled = new KeyEventArgs(Key.Up, 0) { Handled = true };
		typeof(UIElement).GetMethod("OnKeyDown", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
			.Invoke(view, [handled]);
		Assert.Equal(before, view.VerticalOffset);
	}

	[Fact]
	public void KeyboardScrollingUpdatesTheConversationFollowIntent()
	{
		var view = View();
		using var conversation = new ConversationScroll(view, _ => { });
		Layout(view);
		var smooth = SmoothScroll.For(view);
		smooth.JumpToEnd();
		Assert.True(conversation.Following);

		var pageUp = new KeyEventArgs(Key.PageUp, 0);
		typeof(UIElement).GetMethod("OnKeyDown", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
			.Invoke(view, [pageUp]);
		Assert.False(conversation.Following);

		var end = new KeyEventArgs(Key.End, 0);
		typeof(UIElement).GetMethod("OnKeyDown", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
			.Invoke(view, [end]);
		Assert.True(conversation.Following);
	}
}
