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
