using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Xunit;

namespace PiWeft.Desktop.Tests;

[Collection("mewui-controls")]
public sealed class IconButtonLayoutTests
{
	[Theory]
	[InlineData(true, "\u65b0\u5efa", 28)]
	[InlineData(true, "\u9009\u62e9\u6a21\u578b", 36)]
	[InlineData(true, "\u4fdd\u5b58", 48)]
	[InlineData(false, "Settings", 28)]
	[InlineData(false, "\u914d\u7f6e\u7ba1\u7406", 36)]
	public void IconAndLabelShareTheButtonVerticalCenter(bool primary, string text, double height)
	{
		var button = primary
			? AppIcons.PrimaryButton("plus", text, () => { })
			: AppIcons.TextButton("settings", text, () => { });
		var row = Assert.IsType<StackPanel>(button.Content);
		var image = Assert.IsType<Image>(row.Children[0]);
		var label = Assert.IsType<TextBlock>(row.Children[1]);
		Assert.Equal(VerticalAlignment.Center, row.VerticalAlignment);
		Assert.Equal(VerticalAlignment.Center, image.VerticalAlignment);
		Assert.Equal(VerticalAlignment.Center, label.VerticalAlignment);

		// Model a text line box so layout assertions do not need a graphics backend.
		var lineBox = new Border().Width(text.Length * AppTheme.FontSizeControl).Height(22)
			.VerticalAlignment(label.VerticalAlignment);
		row.Clear();
		row.Children(image, lineBox);
		button.Measure(new Size(240, height));
		button.Arrange(new Rect(10, 20, 240, height));
		Assert.Equal(14, image.Bounds.Width);
		Assert.Equal(14, image.Bounds.Height);
		Assert.InRange(Math.Abs(CenterY(image) - CenterY(lineBox)), 0, 1);
		Assert.InRange(Math.Abs(CenterY(image) - CenterY(button)), 0, 1);
		Assert.InRange(lineBox.Bounds.Left - image.Bounds.Right, 5, 7);
	}

	[Theory]
	[InlineData(14, 28)]
	[InlineData(16, 36)]
	[InlineData(20, 48)]
	public void IconOnlyContentIsCenteredWithoutChangingItsSize(double size, double height)
	{
		var button = AppIcons.Button("folder-open", "Open", () => { }, size: size);
		var image = Assert.IsType<Image>(button.Content);
		button.Measure(new Size(48, height));
		button.Arrange(new Rect(10, 20, 48, height));
		Assert.Equal(size, image.Bounds.Width);
		Assert.Equal(size, image.Bounds.Height);
		Assert.InRange(Math.Abs(CenterY(image) - CenterY(button)), 0, 1);
		Assert.InRange(Math.Abs(image.Bounds.X + size / 2 - (button.Bounds.X + button.Bounds.Width / 2)), 0, 1);
	}

	private static double CenterY(Element element) => element.Bounds.Y + element.Bounds.Height / 2;
}
