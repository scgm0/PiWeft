using System.Reflection;
using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Xunit;
using static PiWeft.Desktop.Tests.VisualTree;

namespace PiWeft.Desktop.Tests;

[Collection("mewui-controls")]
public sealed class ImagePreviewTests
{
	private const string Png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+j7XcAAAAASUVORK5CYII=";

	[Fact]
	public void ThumbnailClickOpensAWindowOverlayUsingTheSameSourceAndClosingReleasesIt()
	{
		var preview = new ImagePreview();
		var owner = new Border();
		var window = new Window().Content(owner);
		var thumbnail = Assert.IsType<Button>(SessionTab.ImageView(new ChatImage(Png, "image/png"), source => preview.Show(owner, source)));
		var image = Find<Image>(thumbnail).Single();
		Click(thumbnail);
		Assert.True(preview.IsOpen);
		Assert.Same(image.Source, PreviewImage(preview).Source);
		Assert.True(window.OverlayLayer.Contains(preview.View));
		Click(Find<Button>(preview.View).Single(button => HoverHints.Text(button) == Strings.ImagePreviewClose));
		Assert.False(preview.IsOpen);
		Assert.Null(preview.View.Parent);
		Assert.Null(PreviewImage(preview).Source);
		Assert.NotNull(image.Source);
	}

	[Fact]
	public void EscapeAndBackdropCloseWhileClickingTheImageKeepsThePreviewOpen()
	{
		var preview = new ImagePreview();
		var window = new Window();
		preview.Show(window, ChatImages.Source(Png));
		var click = new MouseEventArgs(default, default, MouseButton.Left);
		typeof(UIElement).GetMethod("OnMouseDown", BindingFlags.Instance | BindingFlags.NonPublic)!
			.Invoke(PreviewImage(preview), [click]);
		Assert.True(click.Handled);
		Assert.True(preview.IsOpen);
		Click(Find<Button>(preview.View).Single(button => HoverHints.Text(button) is null));
		Assert.False(preview.IsOpen);
		preview.Show(window, ChatImages.Source(Png));
		var key = new KeyEventArgs(Key.Escape, 0);
		typeof(UIElement).GetMethod("OnKeyDown", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(preview.View, [key]);
		Assert.True(key.Handled);
		Assert.False(preview.IsOpen);
		Assert.Equal(0, window.OverlayLayer.Count);
	}

	[Theory]
	[InlineData(960, 720)]
	[InlineData(500, 800)]
	public void LetterboxSpaceHitsTheBackdropInsteadOfTheImage(double width, double height)
	{
		var preview = new ImagePreview();
		var window = new Window();
		preview.Show(window, ChatImages.Source(Png));
		ConversationPerformanceTests.Layout(preview.View, width, height);
		var image = PreviewImage(preview);
		var center = new Point(image.Bounds.X + image.Bounds.Width / 2, image.Bounds.Y + image.Bounds.Height / 2);
		Assert.Same(image, preview.View.HitTest(center));
		var blank = width > height ? new Point(60, height / 2) : new Point(width / 2, 60);
		Assert.False(image.Bounds.Contains(blank));
		var backdrop = Assert.IsType<Button>(preview.View.HitTest(blank));
		Click(backdrop);
		Assert.False(preview.IsOpen);
		Assert.Null(image.Source);
		Assert.Equal(0, window.OverlayLayer.Count);
	}

	[Fact]
	public void RepeatedPreviewDoesNotAddLayersAndCommandPaletteReplacesIt()
	{
		var window = new Window();
		var preview = new ImagePreview();
		preview.Show(window, ChatImages.Source(Png));
		preview.Show(window, ChatImages.Source(Png));
		Assert.Equal(1, window.OverlayLayer.Count);
		var palette = new CommandPalette(() => []);
		palette.Show(window);
		Assert.False(preview.IsOpen);
		Assert.Null(PreviewImage(preview).Source);
		Assert.Equal(1, window.OverlayLayer.Count);
		palette.Hide();
	}

	[Fact]
	public void DetachedOwnersDoNotKeepAnUnhostedPreviewOrSource()
	{
		var preview = new ImagePreview();
		preview.Show(new Border(), ChatImages.Source(Png));
		Assert.False(preview.IsOpen);
		Assert.Null(PreviewImage(preview).Source);
	}

	[Fact]
	public void RebuildingThumbnailsReusesLiveSourcesAndInvalidBase64HasNoPreviewAction()
	{
		var image = new ChatImage(Png, "image/png");
		var first = Assert.IsType<Image>(SessionTab.ImageView(image));
		var second = Assert.IsType<Image>(SessionTab.ImageView(image));
		Assert.Same(first.Source, second.Source);
		Assert.IsType<TextBlock>(SessionTab.ImageView(new ChatImage("not base64!", "image/png"), _ => throw new InvalidOperationException()));
	}

	private static Image PreviewImage(ImagePreview preview) => Find<Image>(preview.View).Single(image => double.IsNaN(image.Width));

	internal static void Click(Button button) => typeof(Button).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(button, null);
}
