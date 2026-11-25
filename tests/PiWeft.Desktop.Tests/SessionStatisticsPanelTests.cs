using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using PiWeft.CodingAgent;
using Xunit;
using static PiWeft.Desktop.Tests.VisualTree;

namespace PiWeft.Desktop.Tests;

/// 会话统计是锚在入口上方的弹层，不再是铺满窗口的浮层：
/// 无后端时不能开原生窗口，但内容仍要更新，且不能偷偷挂回窗口浮层。
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
