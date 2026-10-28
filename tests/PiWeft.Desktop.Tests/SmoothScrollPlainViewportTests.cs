using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Xunit;
using static PiWeft.Desktop.Tests.VisualTree;

namespace PiWeft.Desktop.Tests;

/// <summary>
/// 非跟底视口（设置页这类普通 ScrollViewer）的滚轮动画同样会被布局校正打断：
/// 动画写的是分数偏移，arrange 每次吸附到像素网格都产生一次亚 DIP 的外部位移，
/// OnScrollChanged 的普通分支没有像 AutoFollowAtEnd 分支那样的校正容差，
/// 结果是每个滚轮刻度都被拦腰打断——快速滚动一顿一顿、半路丢距离。
/// </summary>
[Collection("mewui-controls")]
public sealed class SmoothScrollPlainViewportTests
{
	private sealed class Fixture
	{
		public required ScrollViewer Viewer { get; init; }
		public required SmoothScroll Smooth { get; init; }

		/// <summary>一个 16ms 帧：动画心跳 + 布局。dt 钉住，曲线不随机器负载抖。</summary>
		public void Frame()
		{
			AnimationFrames.Frame(Smooth);
			ConversationPerformanceTests.Layout(Viewer);
		}
	}

	private static Fixture Build()
	{
		var content = new StackPanel();
		for (var row = 0; row < 30; row++)
		{
			content.Children(new Border { Height = 40, Child = new TextBlock().Text($"row {row}") });
		}
		var viewer = new ScrollViewer().SmoothScrolling().Content(content).AutoVerticalScroll().NoHorizontalScroll();
		ConversationPerformanceTests.Layout(viewer);
		var smooth = SmoothScroll.For(viewer);
		Assert.True(smooth.MaxVerticalOffset > 400, "fixture should be scrollable");
		viewer.SetScrollOffsets(0, 0);
		ConversationPerformanceTests.Layout(viewer);
		return new Fixture { Viewer = viewer, Smooth = smooth };
	}

	[Fact]
	public void PixelSnapCorrectionMidGlideKeepsTheAnimationAlive()
	{
		var fixture = Build();
		try
		{
			var smooth = fixture.Smooth;
			smooth.ScrollBy(0, 1);
			smooth.FlushPendingWheel();
			fixture.Frame();
			Assert.True(smooth.IsAnimating, "the wheel glide should be running before the correction");

			// arrange 吸附式校正：extent 稳定的亚 DIP 外部位移，不是读者接管。
			fixture.Viewer.SetScrollOffsets(fixture.Viewer.HorizontalOffset, fixture.Viewer.VerticalOffset - 0.5);
			ConversationPerformanceTests.Layout(fixture.Viewer);
			Assert.True(smooth.IsAnimating,
				"a sub-DIP pixel-snap correction must not kill the reader's glide on a plain viewport");
		}
		finally
		{
			UiLifetime.Release(fixture.Viewer);
		}
	}

	[Fact]
	public void VisibleExternalDisplacementStillTakesOver()
	{
		var fixture = Build();
		try
		{
			var smooth = fixture.Smooth;
			smooth.ScrollBy(0, 1);
			smooth.FlushPendingWheel();
			fixture.Frame();
			Assert.True(smooth.IsAnimating);

			// 可见幅度的外部位移（焦点滚动、程序化定位）仍是接管：立即结束在途动画。
			fixture.Viewer.SetScrollOffsets(fixture.Viewer.HorizontalOffset, fixture.Viewer.VerticalOffset - 30);
			ConversationPerformanceTests.Layout(fixture.Viewer);
			Assert.False(smooth.IsAnimating, "a visible external displacement should stop the glide");
		}
		finally
		{
			UiLifetime.Release(fixture.Viewer);
		}
	}

	[Fact]
	public void NotchesAtTargetBoundaryAreSwallowedWhileTheGlideIsAlive()
	{
		var fixture = Build();
		try
		{
			var smooth = fixture.Smooth;
			// 快速连滚：目标先一步顶到末端，偏移还在半路（测试里无帧循环，动画不推进）。
			for (var notch = 0; notch < 12; notch++)
			{
				smooth.ScrollBy(0, 1);
			}
			Assert.True(smooth.IsAnimating, "the glide toward the accumulated target should be running");

			// 此刻再来一格：信息已被在途动画覆盖，必须吞掉。放行会让事件冒泡到本视口的
			// 内建 OnMouseWheel，ScrollBy 瞬间 +100 DIP，与动画抢同一个视口（一顿一顿）。
			var args = new MouseWheelEventArgs(
				new Point(0, 0), new Point(0, 0), new Vector(0, -1),
				leftButton: false, rightButton: false, middleButton: false, modifiers: ModifierKeys.None);
			smooth.RouteWheel(args);
			Assert.True(args.Handled, "a notch covered by the in-flight glide must be swallowed");
		}
		finally
		{
			UiLifetime.Release(fixture.Viewer);
		}
	}

	[Fact]
	public void NotchesAtARestingBoundaryStillCascadeOutward()
	{
		var fixture = Build();
		try
		{
			var smooth = fixture.Smooth;
			smooth.JumpToEnd();
			Assert.Equal(smooth.MaxVerticalOffset, fixture.Viewer.VerticalOffset, 1);

			// 偏移真正停在末端、没有在途动画：这一格要放行给外层视口（嵌套滚动）。
			var args = new MouseWheelEventArgs(
				new Point(0, 0), new Point(0, 0), new Vector(0, -1),
				leftButton: false, rightButton: false, middleButton: false, modifiers: ModifierKeys.None);
			smooth.RouteWheel(args);
			Assert.False(args.Handled, "a resting boundary must cascade the notch outward");
		}
		finally
		{
			UiLifetime.Release(fixture.Viewer);
		}
	}
}
