using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Xunit;

namespace PiWeft.Desktop.Tests;

[Collection("mewui-controls")]
public sealed class HoverHintsTests
{
	[Fact]
	public void TooltipDelayIsLongEnoughToAvoidAccidentalHoverPopups()
	{
		Assert.Equal(TimeSpan.FromMilliseconds(350), HoverHints.Delay);
	}

	[Fact]
	public void TooltipsStopShowingOnceTheWindowIsMinimized()
	{
		// Popup 是独立的置顶原生窗口，窗口最小化后不会自己隐藏（X11 尤其明显）；
		// 和原生 ToolTip 一样，最小化时不能再出现、也不能继续停留在桌面上。
		using var window = new Window();
		Assert.True(HoverHints.CanShow(window));

		window.WindowState = WindowState.Minimized;
		Assert.False(HoverHints.CanShow(window));

		window.WindowState = WindowState.Normal;
		Assert.True(HoverHints.CanShow(window));
	}

	[Fact]
	public void ChipAnchorsAreRememberedSoTheHintSitsOnTheChip()
	{
		var owner = new Button().Width(40).Height(24);
		var anchor = new Rect(10, 20, 80, 20);
		HoverHints.Set(owner, "/tmp/a.cs", anchor);
		Assert.Equal("/tmp/a.cs", HoverHints.Text(owner));
		Assert.Equal(anchor, HoverHints.AnchorOf(owner));

		HoverHints.Set(owner, "/tmp/b.cs");
		Assert.Null(HoverHints.AnchorOf(owner));
	}

	[Fact]
	public void NestedHintsUseWindowCoordinatesAndLeaveRoomForPopupFlipping()
	{
		var owner = new Button().Width(40).Height(24).HorizontalAlignment(HorizontalAlignment.Left);
		var nested = new Border().Padding(new Thickness(30)).Child(new Border().Padding(new Thickness(20)).Child(owner));
		nested.Measure(new Size(600, 300));
		nested.Arrange(new Rect(100, 500, 600, 300));
		var anchor = HoverHints.Anchor(owner);
		Assert.Equal(owner.Bounds.X, anchor.X);
		Assert.Equal(owner.Bounds.Y - 6, anchor.Y);
		Assert.Equal(owner.Bounds.Bottom + 6, anchor.Bottom);
		Assert.Equal(owner.Bounds.Width, anchor.Width);
		Assert.Equal(150, anchor.X); // Adding each ancestor again would place the tooltip far away.
	}
}
