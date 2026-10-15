using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Xunit;

namespace PiWeft.Desktop.Tests;

[Collection("mewui-controls")]
public sealed class PaletteOverlayTests
{
	[Fact]
	public void APanelOwnedPaletteAttachesToTheWindowAndDetachesOnClose()
	{
		var owner = new Border();
		var window = new Window().Content(new Grid().Children(new Border().Width(280), owner));
		var palette = new CommandPalette(() => []);
		var closed = 0;
		palette.Closed += () => closed++;

		palette.Show(owner);
		Assert.True(palette.IsOpen);
		Assert.Same(window, palette.View.Parent);
		Assert.True(window.OverlayLayer.Contains(palette.View));
		Assert.Equal(1, window.OverlayLayer.Count);
		palette.Show(owner);
		Assert.Equal(1, window.OverlayLayer.Count);

		palette.Hide();
		Assert.False(palette.IsOpen);
		Assert.Null(palette.View.Parent);
		Assert.Equal(0, window.OverlayLayer.Count);
		Assert.Equal(1, closed);
		palette.Hide();
		Assert.Equal(1, closed);
	}

	[Fact]
	public void OpeningAnotherPaletteClosesThePreviousWindowOverlay()
	{
		var window = new Window();
		var commands = new CommandPalette(() => []);
		var models = new CommandPalette(() => []);
		commands.Show(window);
		models.Show(window);
		Assert.False(commands.IsOpen);
		Assert.Null(commands.View.Parent);
		Assert.True(models.IsOpen);
		Assert.Same(window, models.View.Parent);
		Assert.Equal(1, window.OverlayLayer.Count);
		models.Hide();
		Assert.Equal(0, window.OverlayLayer.Count);
	}

	[Fact]
	public void DetachedOwnersDoNotOpenAnUnhostedPalette()
	{
		var palette = new CommandPalette(() => []);
		palette.Show(new Border());
		Assert.False(palette.IsOpen);
		Assert.Null(palette.View.Parent);
	}
}
