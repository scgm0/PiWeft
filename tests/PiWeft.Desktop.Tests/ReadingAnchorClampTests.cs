using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Xunit;
using Xunit.Abstractions;
using static PiWeft.Desktop.Tests.VisualTree;

namespace PiWeft.Desktop.Tests;

/// 虚拟化高度估算收缩时，ScrollViewer 会把当前偏移夹到新的末尾。夹取本身是布局，
/// 不应改变读者的阅读位置：ConversationScroll 要把顶部行 + 行内偏移映射回新坐标系。
/// 这里用一个“锚点行上方的一行变矮”的确定场景覆盖夹取路径，不依赖运行时估算的具体数值。
[Collection("mewui-controls")]
public sealed class ReadingAnchorClampTests(ITestOutputHelper output)
{
	[Fact]
	public void ExtentShrinkClampKeepsTheReadingRow()
	{
		var metricsBefore = ThemeManager.DefaultMetrics;
		ThemeManager.DefaultMetrics = metricsBefore with { ScrollWheelStep = 100 };
		try
		{
			var heights = new double[30];
			for (var index = 0; index < 28; index++) heights[index] = 100;
			heights[28] = 500;
			heights[29] = 600;
			var views = new Border[heights.Length];
			var rows = Enumerable.Range(0, heights.Length).Select(index => new ChatEntry
			{
				Role = ChatRole.User,
				Text = $"row {index}",
				EntryId = $"r{index}",
				Timestamp = index,
			}).ToArray();
			using var timeline = new ConversationTimeline(entry =>
			{
				var index = int.Parse(entry.EntryId![1..]);
				return views[index] = new Border().Height(heights[index]).Child(new TextBlock().Text(entry.Text));
			});
			using var conversation = new ConversationScroll(timeline.Scroll, _ => { }, timeline);
			var smooth = SmoothScroll.For(timeline.Scroll);
			AnimationFrames.Pin(smooth);
			timeline.SetEntries(rows);
			Pump(timeline.View);
			// 打开会话：落到末尾并让尾部行量出真实高度。
			conversation.ScrollToLatest();
			for (var pass = 0; pass < 10; pass++)
			{
				AnimationFrames.Advance();
				Pump(timeline.View);
				conversation.FollowAfterLayout();
			}
			smooth.Stop();
			Pump(timeline.View);

			// 上滚一格离开末尾；此时顶部是 r29，r28 仍在预测量范围内。
			smooth.ScrollBy(0, -1);
			smooth.FlushPendingWheel();
			for (var frame = 0; frame < 40 && smooth.IsAnimating; frame++)
			{
				AnimationFrames.Advance();
				Pump(timeline.View);
			}
			smooth.Stop();
			Pump(timeline.View);
			var start = timeline.CurrentReadingAnchor();
			var maximumBefore = smooth.MaxVerticalOffset;
			output.WriteLine($"before: off={timeline.Scroll.VerticalOffset:F0} max={maximumBefore:F0} point={start}");
			Assert.Equal("r29", start.EntryId);
			Assert.False(conversation.Following);

			// 锚点上方的一行变矮：extent 收缩，当前 offset 超过新 max，触发夹取。
			heights[28] = 300;
			views[28].Height = 300;
			timeline.RefreshLayout(views[28]);
			Pump(timeline.View);

			var end = timeline.CurrentReadingAnchor();
			output.WriteLine($"after: off={timeline.Scroll.VerticalOffset:F0} max={smooth.MaxVerticalOffset:F0} point={end}");
			Assert.True(smooth.MaxVerticalOffset < maximumBefore - 150, "the shrink must reach the published extent");
			Assert.Equal(start.EntryId, end.EntryId);
			Assert.InRange(Math.Abs(end.WithinRow - start.WithinRow), 0, 3);
			Assert.True(timeline.Scroll.VerticalOffset < smooth.MaxVerticalOffset - 1,
				"a layout clamp must not snap the reader to the end");
			Assert.False(conversation.Following);
		}
		finally
		{
			ThemeManager.DefaultMetrics = metricsBefore;
		}
	}

	private static void Pump(UIElement view) => ConversationPerformanceTests.Layout(view, 700, 500);
}
