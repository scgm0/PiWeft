using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using PiWeft.CodingAgent.Resources;
using PiWeft.Desktop;
using Xunit;
using static PiWeft.Desktop.Tests.VisualTree;

namespace PiWeft.Desktop.Tests;

/// <summary>
/// 滚轮事件会从命中元素沿祖先链冒泡，链上任何一个没有控制器的 ScrollViewer 都会用
/// MewUI 内建的 ScrollBy 直跳接管（无动画、整格 +100 DIP），与平滑滚动交替出现，
/// 表现为快速滚动一顿一顿。设置页的可视树里不允许存在无控制器的滚动视口。
/// </summary>
[Collection("mewui-controls")]
public sealed class SmoothScrollCoverageTests
{
	[Theory]
	[InlineData("resources")]
	[InlineData("overview")]
	[InlineData("appearance")]
	public void EveryScrollViewerInTheSettingsSectionHasAController(string section)
	{
		var view = new SettingsView(static () => new SettingsSnapshot
		{
			AgentDir = "/tmp",
			Cwd = "/tmp",
			ModelCount = 1,
			Tools = ["read"],
			Skills = ["mewui"],
			McpServers = [],
			Resources = ResourceSnapshot.Empty,
		});
		view.SelectSectionAsync(section).GetAwaiter().GetResult();
		ConversationPerformanceTests.Layout(view.View);

		var viewers = Find<ScrollViewer>(view.View);
		Assert.NotEmpty(viewers);
		var missing = viewers.Where(static scroll => SmoothScroll.Find(scroll) is null).ToList();
		Assert.True(missing.Count == 0,
			$"[{section}] uncontrolled ScrollViewers: {string.Join(", ", missing.Select(static scroll => scroll.GetType().Name + "@" + scroll.Bounds))}");
	}
}
