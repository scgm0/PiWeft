using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Xunit;

namespace PiWeft.Desktop.Tests;

/// 运行中步骤的秒表要在标签页来回切换后继续走：TabControl 把未选中内容摘出视觉树，
/// 切回来不重新测量，秒表不能只靠测量复活。
[Collection("mewui-controls")]
public sealed class LiveDurationTextTests
{
	[Fact]
	public void StopwatchRestartsWhenItsTabIsSelectedAgain()
	{
		var startedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 1_500;
		var timer = new LiveDurationText(startedAt);
		var tabs = new TabControl();
		tabs.AddTab(new TabItem().Header("a").Content(new Border().Child(timer)));
		tabs.AddTab(new TabItem().Header("b").Content(new Border().Child(new TextBlock().Text("b"))));
		var window = new Window().Content(tabs);
		try
		{
			ConversationPerformanceTests.Layout(tabs, 480, 320);
			Assert.True(timer.IsTicking, "运行中的秒表挂到窗口后要走");

			tabs.SelectedIndex = 1;
			Assert.False(timer.IsTicking, "切走的标签页内容已摘出视觉树，秒表应停");

			tabs.SelectedIndex = 0;
			// 布局缓存仍有效，切回不一定重测；秒表必须在重挂时自己回来。
			Assert.True(timer.IsTicking, "切回标签页后秒表要重新走");
		}
		finally
		{
			UiLifetime.Release(window);
		}
	}
}
