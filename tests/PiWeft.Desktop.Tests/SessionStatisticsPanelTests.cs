using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using PiWeft.CodingAgent;
using Xunit;
using static PiWeft.Desktop.Tests.VisualTree;

namespace PiWeft.Desktop.Tests;

/// 会话统计是锚在入口上方的窗口内浮层：无后端时不能挂层，但内容仍要更新，
/// 也不能借挂层把卡片挂回窗口树。
[Collection("mewui-controls")]
public sealed class SessionStatisticsPanelTests
{
	[Fact]
	public void CardIsThePopupSurface()
	{
		using var panel = new SessionStatisticsPanel(static () => { });

		var card = Assert.IsType<Border>(panel.View);
		Assert.Equal(AppStyles.PopupCard, card.StyleName);
		Assert.False(panel.IsOpen);
	}

	[Fact]
	public void ActiveTabTakesFocusAfterSwitchingViews()
	{
		using var panel = new SessionStatisticsPanel(static () => { });
		var window = new Window();
		window.OverlayLayer.Add(panel.View);
		panel.Update(new SessionStatisticsDisplay(new SessionStatistics(), 128_000, 1_000_000, 5), false);
		var overview = Find<Button>(panel.View).Single(button =>
			button.Content is StackPanel content && Find<TextBlock>(content).Any(text => text.Text == Strings.StatisticsOverview));
		var details = Find<Button>(panel.View).Single(button =>
			button.Content is StackPanel content && Find<TextBlock>(content).Any(text => text.Text == Strings.StatisticsDetails));

		panel.FocusActiveTab();
		Assert.True(overview.IsFocused);
		Assert.False(details.IsFocused);

		panel.ShowDetails();
		panel.FocusActiveTab();
		Assert.True(details.IsFocused);
		Assert.False(overview.IsFocused);

		window.OverlayLayer.Remove(panel.View);
	}

	[Fact]
	public void PlacementKeepsTheCardAboveTheTriggerAndInsideTheWindow()
	{
		var (margin, maxHeight) = SessionStatisticsPanel.Placement(
			new Rect(700, 500, 40, 20),
			new Size(800, 600),
			new Thickness(8, 48, 8, 12),
			new Thickness(8, 4, 8, 12));

		// 卡片容器右缘贴锚点右缘，再从浮层可见区的右缘让出落影的一圈。
		Assert.Equal((800 - 8) - (740 + 8), margin.Right);
		// 卡片底边停在锚点上方 6，容器下缘再从可见区底边让出落影。
		Assert.Equal((600 - 12) - (500 - 6 + 12), margin.Bottom);
		// 卡片只能长到标题栏下方：锚点上方的空间扣掉让位量与落影上下两圈。
		Assert.Equal(500 - 6 - 48 - 4 + 16, maxHeight);
	}

	[Fact]
	public void ShowWithoutABackendUpdatesContentWithoutOpeningThePopup()
	{
		using var panel = new SessionStatisticsPanel(static () => { });
		var owner = new Border();
		var window = new Window().Content(owner);

		panel.Show(owner, new SessionStatisticsDisplay(new SessionStatistics { Input = 1_234 }, 128_000, 1_000_000, 2), false);

		Assert.False(panel.IsOpen);
		Assert.Null(panel.View.Parent);
		Assert.Equal(0, window.OverlayLayer.Count);
		Assert.Contains(Find<TextBlock>(panel.View), text => text.Text == "1.2K / 0");
	}
}
