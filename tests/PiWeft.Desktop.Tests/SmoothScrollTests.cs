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
		AnimationFrames.Advance(45);
		Assert.InRange(viewport.VerticalOffset, 1, ThemeManager.DefaultMetrics.ScrollWheelStep - 1);
		AnimationFrames.Advance(200);

		var text = new MultiLineTextBox().Text(string.Join("\n", Enumerable.Range(0, 200).Select(i => $"Line {i}"))).SmoothScrolling();
		ConversationPerformanceTests.Layout(text, 240, 200);
		using var smoothText = SmoothScroll.For(text);
		var original = text.Text;
		smoothText.RouteWheel(new MouseWheelEventArgs(default, default, new Vector(0, -1), false, false, false, ModifierKeys.None));
		Assert.Equal(0, text.VerticalOffset);
		AnimationFrames.Advance(45);
		Assert.InRange(text.VerticalOffset, 1, ThemeManager.DefaultMetrics.ScrollWheelStep - 1);
		AnimationFrames.Advance(200);
		Assert.Equal(ThemeManager.DefaultMetrics.ScrollWheelStep, text.VerticalOffset);
		Assert.Equal(original, text.Text);
		text.ScrollToCaret();
		Assert.False(smoothText.IsAnimating);
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
		AnimationFrames.Advance(200);
		Assert.Equal(ThemeManager.DefaultMetrics.ScrollWheelStep, view.VerticalOffset);
		smooth.JumpToEnd();
		down = new MouseWheelEventArgs(default, default, new Vector(0, -1), false, false, false, ModifierKeys.None);
		smooth.RouteWheel(down);
		Assert.False(down.Handled);
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
		AnimationFrames.Advance(45);
		Assert.InRange(view.VerticalOffset, 1, step - 1);
		smooth.ScrollBy(0, 1);
		AnimationFrames.Advance(200);
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
		AnimationFrames.Advance(45);
		var turningPoint = view.VerticalOffset;
		smooth.ScrollBy(0, -1);
		AnimationFrames.Advance(200);
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
		AnimationFrames.Advance(200);
		var step = ThemeManager.DefaultMetrics.ScrollWheelStep;
		Assert.Equal(step * 0.4, view.HorizontalOffset);
		Assert.Equal(step * 0.2, view.VerticalOffset);
		args = new MouseWheelEventArgs(default, default, new Vector(0, -1), false, false, false, ModifierKeys.Shift);
		smooth.RouteWheel(args);
		AnimationFrames.Advance(200);
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
		AnimationFrames.Advance(200);
		Assert.Equal(smooth.MaxVerticalOffset, view.VerticalOffset);
		smooth.ScrollBy(0, -10);
		view.SetScrollOffsets(0, 300);
		Assert.False(smooth.IsAnimating);
		AnimationFrames.Advance(200);
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
		AnimationFrames.Advance(200);
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
		AnimationFrames.Advance(70);
		Assert.InRange(view.VerticalOffset, 101, SmoothScroll.For(view).MaxVerticalOffset - 1);
		((Border)view.Content!).Height(3000);
		Layout(view);
		AnimationFrames.Advance(300);
		Assert.Equal(SmoothScroll.For(view).MaxVerticalOffset, view.VerticalOffset);
		Assert.True(conversation.Following);
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
}
