using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using PiWeft.Markdown;
using Xunit;

namespace PiWeft.Desktop.Tests;

/// <summary>
/// 代码块行号槽：默认出现在代码区左侧、右对齐留出与代码的间隙；位数增长时列宽跟着变；
/// 主题可整体关闭。对齐本身靠绘制期的行 Y 映射，已由运行截图验证。
/// </summary>
[Collection("mewui-controls")]
public sealed class MarkdownCodeLineNumbersTests
{
	private const double Leading = 10;
	private const double GapToCode = 8;

	private static void Pump(UIElement view, double width = 520, double height = 400)
	{
		view.Measure(new Size(width, height));
		view.Arrange(new Rect(0, 0, width, height));
	}

	private static MarkdownView CodeView(string code)
	{
		var view = new MarkdownView { FontSize = 14 };
		view.Source = $"```csharp\n{code}\n```";
		Pump(view);
		return view;
	}

	private static (MarkdownCodeBlockView Block, MarkdownCodeGutter Gutter) GutterOf(string code)
	{
		var view = CodeView(code);
		var block = Assert.IsType<MarkdownCodeBlockView>(view.GetBlockElement(0));
		var gutter = Assert.IsType<MarkdownCodeGutter>(block.Gutter);
		return (block, gutter);
	}

	private static string Lines(int count, string prefix = "value") =>
		string.Join('\n', Enumerable.Range(1, count).Select(i => $"{prefix} {i}"));

	[Fact]
	public void GutterSitsBetweenCardAndCodeWithRoomForDigits()
	{
		var (block, gutter) = GutterOf(Lines(12));

		var text = block.TextView;
		// 数字右对齐在列右缘 - GapToCode 处，因此数字到代码的视觉间距 ≈ GapToCode + 像素取整。
		double numberRight = gutter.Bounds.Right - GapToCode;
		Assert.True(numberRight <= text.Bounds.X + 0.5,
			$"number right {numberRight} vs text left {text.Bounds.X}");
		Assert.InRange(text.Bounds.X - numberRight, GapToCode - 1, GapToCode + 2.5);
		var twoDigits = gutter.GetLayout("12").MeasuredSize.Width;
		Assert.InRange(gutter.Bounds.Width, Leading + twoDigits + GapToCode - 1, Leading + twoDigits + GapToCode + 1.5);
	}

	[Theory]
	[InlineData(9, "10")]
	[InlineData(99, "100")]
	public void ColumnWidthGrowsWhenLineNumberGainsADigit(int before, string afterMax)
	{
		var (_, first) = GutterOf(Lines(before));
		double narrow = first.Bounds.Width;

		var (block, grown) = GutterOf(Lines(before + 1));

		Assert.True(grown.Bounds.Width > narrow, $"width {grown.Bounds.Width} should exceed {narrow}");
		var expected = Leading + grown.GetLayout(afterMax).MeasuredSize.Width + GapToCode;
		Assert.InRange(grown.Bounds.Width, expected - 1, expected + 1.5);
		_ = block;
	}

	[Fact]
	public void StreamingAppendKeepsGutterBesideCode()
	{
		var (block, gutter) = GutterOf(Lines(5));

		// 流式追加走的正是 SetCode 的增量分支：行号列要跟着长，不许跑到代码下面。
		block.SetCode(Lines(105), "csharp");
		Pump(block);

		var text = block.TextView;
		double numberRight = gutter.Bounds.Right - GapToCode;
		Assert.InRange(text.Bounds.X - numberRight, GapToCode - 1, GapToCode + 2.5);
		var threeDigits = gutter.GetLayout("105").MeasuredSize.Width;
		Assert.InRange(gutter.Bounds.Width, Leading + threeDigits + GapToCode - 1, Leading + threeDigits + GapToCode + 1.5);
	}

	[Fact]
	public void ThemeCanHideLineNumbers()
	{
		var view = new MarkdownView
		{
			FontSize = 14,
			MarkdownTheme = new MarkdownTheme { ShowCodeLineNumbers = false },
		};
		view.Source = $"```csharp\n{Lines(12)}\n```";
		Pump(view);

		var block = Assert.IsType<MarkdownCodeBlockView>(view.GetBlockElement(0));
		Assert.Null(block.Gutter);
		// 关掉行号后代码区回到自带的四周内边距。
		Assert.Equal(12, VisualTree.Find<ScrollViewer>(block).Single().Padding.Left);
	}

	[Fact]
	public void SetCodeReuseDoesNotRecreateTheGutter()
	{
		var view = CodeView(Lines(5));
		var block = Assert.IsType<MarkdownCodeBlockView>(view.GetBlockElement(0));
		var gutter = block.Gutter;

		block.SetCode(Lines(6), "csharp");

		Assert.Same(gutter, block.Gutter);
	}
}
