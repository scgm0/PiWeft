using Aprillz.MewUI;
using PiWeft.Markdown;
using Xunit;

namespace PiWeft.Desktop.Tests;

/// <summary>
/// 默认行距：正文 1.6 倍、标题 1.3 倍（与 PiDeck 的 --line-height-chat / .markdown-body 标题一致），
/// 按字号换算成 MewUI 的附加行间距，并且保证不把行压到字体自然行高以下。
/// </summary>
[Collection("mewui-controls")]
public sealed class MarkdownLineSpacingTests
{
	private const string FourLines = "aaaa\nbbbb\ncccc\ndddd";

	/// <summary>关掉行距的主题：用来量字体自然行高，作为行距断言的基准。</summary>
	private static readonly MarkdownTheme TightTheme = new() { LineHeightScale = 0, HeadingLineHeightScale = 0 };

	private static double Height(double fontSize, MarkdownTheme theme, MarkdownBlockKind kind, string text)
	{
		AppFonts.Register();
		var block = new MarkdownBlock { Kind = kind, Text = text, Runs = [new MarkdownRun(text)] };
		var paragraph = new MarkdownParagraphView { FontSize = fontSize, FontFamily = AppFonts.DefaultUi };
		paragraph.SetBlock(block, theme, null, null, plainCode: false, wrap: false, fontScale: 1);
		paragraph.Measure(new Size(100_000, 100_000));
		return paragraph.DesiredSize.Height;
	}

	/// <summary>行距（相邻两行行首的间距），单位 DIP。</summary>
	private static double Pitch(double fontSize, MarkdownTheme theme, MarkdownBlockKind kind)
	{
		// 4 行文本有 3 个行间空隙：高度差 / 3 就是加上的行间距，加上自然行高即行距。
		double natural = Height(fontSize, new MarkdownTheme { LineHeightScale = 0, HeadingLineHeightScale = 0 }, kind, FourLines);
		double spaced = Height(fontSize, theme, kind, FourLines);
		return natural / 4 + (spaced - natural) / 3;
	}

	[Fact]
	public void DefaultThemeGivesBodyTextPitchOf1Point6TimesFontSize()
	{
		Assert.Equal(1.6, new MarkdownTheme().LineHeightScale, 3);
		Assert.Equal(16 * 1.6, Pitch(16, new MarkdownTheme(), MarkdownBlockKind.Paragraph), 1);
	}

	[Fact]
	public void BodyPitchScalesWithTheFontSize()
	{
		// 字号档位（14–22）都要跟着走，否则放大字号只会让行挤在一起。
		foreach (var size in new[] { 14.0, 18.0, 22.0 })
		{
			Assert.Equal(size * 1.6, Pitch(size, new MarkdownTheme(), MarkdownBlockKind.Paragraph), 1);
		}
	}

	[Fact]
	public void HeadingsKeepTighterLeadingThanBody()
	{
		Assert.Equal(1.3, new MarkdownTheme().HeadingLineHeightScale, 3);
		Assert.Equal(16 * 1.3, Pitch(16, new MarkdownTheme(), MarkdownBlockKind.Heading), 1);
	}

	[Fact]
	public void RequestedScaleBelowNaturalHeightNeverCompressesLines()
	{
		// 1.0 倍小于字体的自然行高：宁可维持自然行高，也不能让上下行咬在一起。
		double natural = Height(16, new MarkdownTheme { LineHeightScale = 0 }, MarkdownBlockKind.Paragraph, FourLines);
		Assert.Equal(natural, Height(16, new MarkdownTheme { LineHeightScale = 1 }, MarkdownBlockKind.Paragraph, FourLines), 1);
	}

	[Fact]
	public void DisabledLineHeightFallsBackToTheFontMetrics()
	{
		double off = Height(16, new MarkdownTheme { LineHeightScale = 0 }, MarkdownBlockKind.Paragraph, FourLines);
		double negative = Height(16, new MarkdownTheme { LineHeightScale = -1 }, MarkdownBlockKind.Paragraph, FourLines);
		Assert.Equal(off, negative, 1);
		Assert.True(off < Height(16, new MarkdownTheme(), MarkdownBlockKind.Paragraph, FourLines));
	}

	[Fact]
	public void ChangingTheThemeRelayoutsTheDocument()
	{
		// 主题里的行距倍率必须进 MeasureKey，否则改了设置文档高度还是旧值。
		const string paragraph =
			"alpha bravo charlie delta echo foxtrot golf hotel india juliet kilo lima mike november " +
			"oscar papa quebec romeo sierra tango uniform victor whiskey x-ray yankee zulu";
		var view = new MarkdownView { FontSize = 16, Source = paragraph };
		view.Measure(new Size(420, 600));
		double loosened = view.DesiredSize.Height;
		view.MarkdownTheme = TightTheme;
		view.Measure(new Size(420, 600));
		Assert.True(loosened > view.DesiredSize.Height + 3,
			$"expected the body line spacing to grow the document: {view.DesiredSize.Height} -> {loosened}");
	}

	[Fact]
	public void SingleLineListItemsTakeTheWholeLinePitch()
	{
		// 单行条目里没有「行与行之间」，行距得由列表行补齐：3 个条目 == 3 个 1.6em 的行框。
		// 不补的话，同一个字号下列表会比正文挤（19 DIP vs 25.6 DIP）。
		double natural = Height(16, TightTheme, MarkdownBlockKind.Paragraph, "aaaa");
		double loosened = ViewHeight(new MarkdownTheme(), "- alpha\n- bravo\n- charlie", 520);
		double tightened = ViewHeight(TightTheme, "- alpha\n- bravo\n- charlie", 520);

		// 控件把高度向上取整到设备像素，所以这里用区间而不是小数位比较。
		Assert.InRange(loosened, 3 * 16 * 1.6 - 1, 3 * 16 * 1.6 + 1);
		Assert.InRange(tightened, 3 * natural - 1, 3 * natural + 1);
	}

	[Fact]
	public void WrappingListItemKeepsOnePitchPerLine()
	{
		// 多行条目：正文部分已经自带了行与行之间的间距，条目只能再补一份，不能按行重复加。
		const string item = "alpha bravo charlie delta echo foxtrot golf hotel india juliet kilo lima mike november";
		double natural = Height(16, TightTheme, MarkdownBlockKind.Paragraph, "aaaa");
		double tightened = ViewHeight(TightTheme, $"- {item}", 260);
		double loosened = ViewHeight(new MarkdownTheme(), $"- {item}", 260);
		int lines = (int)Math.Round(tightened / natural);
		Assert.True(lines >= 2, $"fixture must wrap, lines={lines}");
		Assert.InRange(loosened, lines * 16 * 1.6 - 1, lines * 16 * 1.6 + 1);
	}

	[Fact]
	public void ListMarkerSitsOnTheSameLineBoxAsItsItem()
	{
		double leading = 16 * 1.6 - Height(16, TightTheme, MarkdownBlockKind.Paragraph, "aaaa");

		// 行间距上下均分：标记与条目文字一起下滞半个行距，弹头不会停在行框顶上。
		Assert.InRange(MarkerOffset(new MarkdownTheme()) - MarkerOffset(TightTheme), leading / 2 - 0.5, leading / 2 + 0.5);

		double MarkerOffset(MarkdownTheme theme)
		{
			var view = new MarkdownView { FontSize = 16, MarkdownTheme = theme, Source = "- alpha\n- bravo" };
			view.Measure(new Size(520, 600));
			view.Arrange(new Rect(0, 0, 520, 600));
			var list = Assert.IsType<MarkdownListPanel>(view.GetBlockElement(0));
			return list.MarkerAt(0)!.Bounds.Y - list.Bounds.Y;
		}
	}

	[Fact]
	public void TaskListMarkersFollowTheSameLeading()
	{
		double leading = 16 * 1.6 - Height(16, TightTheme, MarkdownBlockKind.Paragraph, "aaaa");

		// 任务条目的复选框也要跟着行框走，否则行距一大就挂在条目左上角。
		double Offset(MarkdownTheme theme)
		{
			var view = new MarkdownView { FontSize = 16, MarkdownTheme = theme, Source = "- [ ] alpha\n- [x] bravo" };
			view.Measure(new Size(520, 600));
			view.Arrange(new Rect(0, 0, 520, 600));
			var list = Assert.IsType<MarkdownListPanel>(view.GetBlockElement(0));
			Assert.IsType<Aprillz.MewUI.Controls.CheckBox>(list.MarkerAt(0));
			return list.MarkerAt(0)!.Bounds.Y - list.Bounds.Y;
		}

		Assert.InRange(Offset(new MarkdownTheme()) - Offset(TightTheme), leading / 2 - 0.5, leading / 2 + 0.5);
	}

	private static double ViewHeight(MarkdownTheme theme, string source, double width)
	{
		var view = new MarkdownView { FontSize = 16, MarkdownTheme = theme, Source = source };
		view.Measure(new Size(width, 2_000));
		return view.TotalHeight;
	}

	[Fact]
	public void SelectionOfAWrappedParagraphHasNoGapsBetweenLines()
	{
		// 行间距拉开后，按字形带涂的选区会变成一条条横纹（浏览器/PiDeck 把半行距也涂进去）。
		// 这里直接量控件自己算出的选区矩形：逐行首尾相接，每行正好一个行框高。
		AppFonts.Register();
		const string text =
			"aaaa aaaa aaaa aaaa aaaa aaaa aaaa aaaa aaaa aaaa aaaa aaaa aaaa aaaa aaaa aaaa";
		var wrapped = new MarkdownBlock
		{
			Kind = MarkdownBlockKind.Paragraph,
			Text = text,
			Runs = [new MarkdownRun(text)],
		};
		var paragraph = new MarkdownParagraphView { FontSize = 16, FontFamily = AppFonts.DefaultUi };
		paragraph.SetBlock(wrapped, new MarkdownTheme(), null, null, plainCode: false, wrap: true, fontScale: 1);
		paragraph.Measure(new Size(200, 1000));
		paragraph.SetSelection(0, text.Length);

		IReadOnlyList<Rect> rects = paragraph.SelectionRectsForTest();
		Assert.True(rects.Count >= 2, $"fixture must wrap, rects={rects.Count}");
		double pitch = 16 * 1.6;
		foreach (Rect rect in rects)
		{
			Assert.InRange(rect.Height, pitch - 1, pitch + 1);
		}
		for (int index = 1; index < rects.Count; index++)
		{
			Assert.True(Math.Abs(rects[index].Y - rects[index - 1].Bottom) < 0.5,
				$"line {index}: {rects[index - 1].Bottom} vs {rects[index].Y}");
		}

		// 关掉行距时不扩：保持改动前的逐行贴满观感。
		var tight = new MarkdownParagraphView { FontSize = 16, FontFamily = AppFonts.DefaultUi };
		tight.SetBlock(wrapped, TightTheme, null, null, plainCode: false, wrap: true, fontScale: 1);
		tight.Measure(new Size(200, 1000));
		tight.SetSelection(0, text.Length);
		IReadOnlyList<Rect> tightRects = tight.SelectionRectsForTest();
		Assert.Equal(tightRects[0].Height + tightRects[0].Y, tightRects[1].Y, 1);
		Assert.True(tightRects[0].Height < pitch - 3, $"tight line should stay at the natural height: {tightRects[0].Height}");
	}

	[Fact]
	public void SelectingInListItemLeadingStillHitsThatItem()
	{
		// 条目上下各滞半个行距，那片空白里点击不能变成「选不中」：控件的就近归属必须把事件
		// 交给紧邻的条目（选区/拖选靠它），否则列表行距一大就会发现空白处点不动。
		var view = new MarkdownView { FontSize = 16, Source = "- alpha one\n- bravo two" };
		view.Measure(new Size(520, 600));
		view.Arrange(new Rect(0, 0, 520, 600));
		var list = Assert.IsType<MarkdownListPanel>(view.GetBlockElement(0));
		var second = list.ContentAt(1)!;
		var point = new Point(second.Bounds.X + 4, second.Bounds.Y - 1);

		Assert.True(view.BeginSelectionAtForTest(point, 2));
		// 命中的是第二个条目（选区文本带条目标记前缀），而不是上一个条目的。
		Assert.Contains("bravo", view.SelectedText);
		Assert.DoesNotContain("alpha", view.SelectedText);
	}

	[Fact]
	public void SwitchingThemeOnARealizedListUpdatesItemSpacing()
	{
		// 行间距写进了列表行的缓存（ListRow.Leading），靠「换主题强制重建」才能刷新；
		// 这里把这条链钉住：同一个已 realize 的视图改主题，列表行高要跟着变。
		var view = new MarkdownView { FontSize = 16, Source = "- alpha\n- bravo\n- charlie" };
		view.Measure(new Size(520, 600));
		double loosened = view.TotalHeight;
		view.MarkdownTheme = TightTheme;
		view.Measure(new Size(520, 600));
		Assert.True(loosened - view.TotalHeight > 3,
			$"list rows must follow the theme's line height: {view.TotalHeight} -> {loosened}");
	}
}
