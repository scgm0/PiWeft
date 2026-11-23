using Xunit;

namespace PiWeft.Desktop.Tests;

public class TrayBehaviorTests
{
	[Theory]
	[InlineData(true, true, true)]
	[InlineData(true, false, false)]
	[InlineData(false, true, false)]
	[InlineData(false, false, false)]
	public void HidesOnlyWhenEnabledAndSupported(bool closeToTray, bool traySupported, bool expected)
	{
		Assert.Equal(expected, TrayBehavior.ShouldHide(closeToTray, traySupported));
	}

	[Theory]
	[InlineData(true, false, true)]
	[InlineData(false, true, true)]
	[InlineData(true, true, true)]
	[InlineData(false, false, false)]
	public void MenuOffersShowOnlyWhileTheWindowIsOffScreen(bool hiddenToTray, bool minimized, bool expected)
	{
		Assert.Equal(expected, TrayBehavior.MenuShowsWindow(hiddenToTray, minimized));
	}
}
