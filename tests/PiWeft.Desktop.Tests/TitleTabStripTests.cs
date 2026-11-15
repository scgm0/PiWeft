using System.Reflection;
using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Xunit;
using static PiWeft.Desktop.Tests.VisualTree;

namespace PiWeft.Desktop.Tests;

/// 标题栏页签条：页签超过可用宽度时横向滚动，选中的页签要能滚回视口。
[Collection("mewui-controls")]
public sealed class TitleTabStripTests
{
	private static TitleTabStrip Build(int count, double width = 500)
	{
		var strip = new TitleTabStrip();
		for (var index = 0; index < count; index++)
		{
			strip.Add(new Button
			{
				Width = 120,
				Height = 28,
				Content = new TextBlock().Text($"tab {index}"),
			});
		}

		ConversationPerformanceTests.Layout(strip.View, width, AppTheme.TitleBarHeight);
		return strip;
	}

	[Fact]
	public void SelectedTabBeyondTheViewportIsScrolledBackIntoView()
	{
		var strip = Build(12);
		try
		{
			var tabs = Find<Button>(strip.View).ToArray();
			Assert.Equal(12, tabs.Length);
			Assert.Equal(0, strip.View.HorizontalOffset);

			strip.RevealNow(tabs[9]);
			ConversationPerformanceTests.Layout(strip.View, 500, AppTheme.TitleBarHeight);

			var viewport = strip.View.Bounds;
			Assert.True(strip.View.HorizontalOffset > 0, "第九个页签超出视口，应该把它滚进来");
			Assert.InRange(tabs[9].Bounds.Left, viewport.Left, viewport.Right);
			Assert.InRange(tabs[9].Bounds.Right, viewport.Left, viewport.Right);
		}
		finally
		{
			UiLifetime.Release(strip.View);
		}
	}

	[Fact]
	public void RevealingTheFirstTabScrollsBackToTheStart()
	{
		var strip = Build(12);
		try
		{
			var tabs = Find<Button>(strip.View).ToArray();
			strip.RevealNow(tabs[9]);
			ConversationPerformanceTests.Layout(strip.View, 500, AppTheme.TitleBarHeight);
			Assert.True(strip.View.HorizontalOffset > 0);

			strip.RevealNow(tabs[0]);
			ConversationPerformanceTests.Layout(strip.View, 500, AppTheme.TitleBarHeight);
			Assert.Equal(0, strip.View.HorizontalOffset);
		}
		finally
		{
			UiLifetime.Release(strip.View);
		}
	}

	[Fact]
	public void RebuildingTheTabsKeepsTheReadingOffset()
	{
		var strip = Build(12);
		try
		{
			var tabs = Find<Button>(strip.View).ToArray();
			strip.RevealNow(tabs[9]);
			ConversationPerformanceTests.Layout(strip.View, 500, AppTheme.TitleBarHeight);
			var reading = strip.View.HorizontalOffset;
			Assert.True(reading > 0);

			// 标题变化会整条重建；没有换选中项时不能把读者手动滚到的位置抢回去。
			strip.Clear();
			for (var index = 0; index < 12; index++)
			{
				strip.Add(new Button
				{
					Width = 120,
					Height = 28,
					Content = new TextBlock().Text($"renamed {index}"),
				});
			}

			ConversationPerformanceTests.Layout(strip.View, 500, AppTheme.TitleBarHeight);
			Assert.Equal(reading, strip.View.HorizontalOffset, 1);
		}
		finally
		{
			UiLifetime.Release(strip.View);
		}
	}

	[Fact]
	public void WheelOverTheStripGlidesHorizontally()
	{
		var strip = Build(12);
		try
		{
			var smooth = Assert.IsType<SmoothScroll>(SmoothScroll.Find(strip.View));
			var wheel = new MouseWheelEventArgs(
				new Point(0, 0), new Point(0, 0), new Vector(0, -1),
				leftButton: false, rightButton: false, middleButton: false, modifiers: ModifierKeys.None);
			typeof(UIElement).GetMethod("OnMouseWheel", BindingFlags.Instance | BindingFlags.NonPublic)!
				.Invoke(strip.View, [wheel]);

			// 标题栏没有纵向内容：普通滚轮应当转成横向滚动，而不是被吞掉或留给别的视口。
			Assert.True(wheel.Handled);
			Assert.True(smooth.IsAnimating, "滚轮应走平滑滚动，而不是瞬间跳一格");

			for (var frame = 0; frame < 60; frame++)
			{
				AnimationFrames.Frame(smooth);
				ConversationPerformanceTests.Layout(strip.View, 500, AppTheme.TitleBarHeight);
			}

			Assert.True(strip.View.HorizontalOffset > 0, "滚轮之后页签条应该向右滚");
		}
		finally
		{
			UiLifetime.Release(strip.View);
		}
	}
}
