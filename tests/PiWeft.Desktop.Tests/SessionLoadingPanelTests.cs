using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Xunit;
using static PiWeft.Desktop.Tests.VisualTree;

namespace PiWeft.Desktop.Tests;

/// <summary>
/// 准备阶段会话面板里的加载段落：进度就地显示、没有中止按钮、
/// 错误停在同一段落里直到用户关闭，状态变化必须通知宿主换回欢迎文案。
/// </summary>
[Collection("mewui-controls")]
public sealed class SessionLoadingPanelTests
{
	[Fact]
	public void PanelIsASectionNotAFloatingCardAndHasNoAbortButton()
	{
		var panel = new SessionLoadingPanel();

		// 它是准备阶段面板 StackPanel 里的一段，不是带 Card 样式的浮层。
		Assert.IsType<StackPanel>(panel.View);
		Assert.DoesNotContain(AppStyles.Card, Describe(panel.View));
		Assert.False(panel.Showing);

		panel.Begin("正在打开会话「big」…");

		Assert.True(panel.Showing);
		Assert.Equal("正在打开会话「big」…", panel.TitleText);
		// 只有错误态的「关闭」，没有中止/取消入口。
		var buttons = Find<Button>(panel.View).ToList();
		Assert.Single(buttons);
		Assert.DoesNotContain("中止", string.Join("|", buttons.Select(ButtonLabel)));
		Assert.False(panel.DismissVisible);
	}

	[Fact]
	public void UpdateSwitchesBetweenIndeterminateAndPercent()
	{
		var panel = new SessionLoadingPanel();
		panel.Begin("loading");

		// 长度未知：不定进度 + 行数文案。
		panel.Update("正在读取 8960 行…", null);
		Assert.True(panel.ProgressIndeterminate);
		Assert.Equal("正在读取 8960 行…", panel.DetailText);

		// 已知长度：切换成百分比进度条。
		panel.Update("正在读取「big」50%（13500 行）", 0.5);
		Assert.False(panel.ProgressIndeterminate);
		Assert.Equal(50, panel.ProgressValue);
	}

	[Fact]
	public void CompleteEndsOnlyWhenTheLastOperationEnds()
	{
		var panel = new SessionLoadingPanel();
		panel.Begin("first");
		panel.Begin("second");

		panel.Complete();
		Assert.True(panel.Showing);

		panel.Complete();
		Assert.False(panel.Showing);
	}

	[Fact]
	public void FailKeepsTheErrorInPlaceUntilItIsDismissed()
	{
		var panel = new SessionLoadingPanel();
		panel.Begin("loading");
		panel.Fail("打开会话失败", "权限不足");

		Assert.True(panel.Showing);
		Assert.Equal("打开会话失败", panel.TitleText);
		Assert.Equal("权限不足", panel.DetailText);
		// 错误态收掉进度轨道，否则看着像一条分割线。
		Assert.False(panel.ProgressVisible);
		Assert.True(panel.DismissVisible);

		// 别的操作收尾（小文件没进加载段落，因此只调用了 Complete）不能顺手抹掉错误。
		panel.Complete();
		Assert.True(panel.Showing);
		Assert.Equal("权限不足", panel.DetailText);

		ImagePreviewTests.Click(Find<Button>(panel.View).Single());
		Assert.False(panel.Showing);
	}

	[Fact]
	public void NextOperationClearsThePreviousError()
	{
		var panel = new SessionLoadingPanel();
		panel.Begin("loading");
		panel.Fail("打开会话失败", "boom");
		panel.Begin("again");

		Assert.Equal("again", panel.TitleText);
		Assert.Equal(string.Empty, panel.DetailText);
		Assert.True(panel.ProgressVisible);
		Assert.False(panel.DismissVisible);
	}

	[Fact]
	public void EveryStateChangeNotifiesTheHost()
	{
		// 宿主靠这个回调在「欢迎文案」与「加载段落」之间互斥切换，漏一次就会两层同时显示。
		var signals = 0;
		var panel = new SessionLoadingPanel { Changed = () => signals++ };
		panel.Begin("loading");
		panel.Update("50%", 0.5);
		panel.Fail("打开会话失败", "boom");
		ImagePreviewTests.Click(Find<Button>(panel.View).Single());
		panel.Complete();

		Assert.Equal(5, signals);
		Assert.False(panel.Showing);
	}

	private static string Describe(UIElement element) => $"{element.GetType().Name}:{element}";

	private static string ButtonLabel(Button button) =>
		(button.Content as TextBlock)?.Text ?? button.Content?.ToString() ?? string.Empty;
}
