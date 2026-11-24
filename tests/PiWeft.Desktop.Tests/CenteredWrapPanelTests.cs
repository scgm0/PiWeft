using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Xunit;

namespace PiWeft.Desktop.Tests;

[Collection("mewui-controls")]
public sealed class CenteredWrapPanelTests
{
	[Fact]
	public void EveryLineIsCenteredWithinThePanel()
	{
		var panel = new CenteredWrapPanel { Spacing = 8 };
		var first = new Border().Width(100).Height(20);
		var second = new Border().Width(100).Height(20);
		var third = new Border().Width(100).Height(20);
		panel.Children(first, second, third);

		// 100 + 8 + 100 = 208 一行，第三块换行；两行都各自居中。
		panel.Measure(new Size(240, 200));
		panel.Arrange(new Rect(0, 0, 240, 200));

		Assert.Equal(16, first.Bounds.X, 0.5);
		Assert.Equal(124, second.Bounds.X, 0.5);
		Assert.Equal(70, third.Bounds.X, 0.5);
		Assert.Equal(28, third.Bounds.Y, 0.5);
		Assert.Equal(208, panel.DesiredSize.Width, 0.5);
	}
}
