using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using PiWeft.Markdown;
using Xunit;
using Xunit.Abstractions;
using static PiWeft.Desktop.Tests.VisualTree;

namespace PiWeft.Desktop.Tests;

/// Markdown 排布一致性：块的行高必须与它内部真正排出来的文本高度一致，否则下一块会压在它上面
/// （读者看到的就是「偶发的正文错位重叠」）。这里用「父级给的高度 ≥ 子元素排版高度」作为
/// 不相交的必要条件，覆盖宽度往返与流式追加两种最容易出现缓存/测量不一致的场景。
[Collection("mewui-controls")]
public sealed class MarkdownLayoutConsistencyTests(ITestOutputHelper output)
{
	private static void Layout(UIElement view, double width, double height = 700)
	{
		for (var pass = 0; pass < 3; pass++)
		{
			view.Measure(new Size(width, height));
			view.Arrange(new Rect(0, 0, width, height));
		}
	}

	private static ScrollViewer Host(MarkdownView markdown)
	{
		var scroll = new ScrollViewer().Content(markdown).AutoVerticalScroll().NoHorizontalScroll();
		Layout(scroll, 640);
		return scroll;
	}

	private static void AssertNoOverflow(MarkdownView markdown, string where)
	{
		var realized = Find<MarkdownParagraphView>(markdown).ToArray();
		Assert.NotEmpty(realized);
		foreach (var paragraph in realized)
		{
			Assert.False(double.IsNaN(paragraph.LayoutWidthForTest), $"{where}: 已实现段落没有版面");
			// 正文宽度：父级给的 arrange 宽度；_wrap 打开时段落会按它排版。
			Assert.True(Math.Abs(paragraph.Bounds.Width - paragraph.LayoutWidthForTest) <= 0.5 ||
				paragraph.LayoutWidthForTest >= 1_000_000,
				$"{where}: 段落版面宽度 {paragraph.LayoutWidthForTest:0.#} ≠ 排布宽度 {paragraph.Bounds.Width:0.#}");
			Assert.True(paragraph.Bounds.Height + 0.5 >= paragraph.DesiredSize.Height,
				$"{where}: 段落被压扁 height={paragraph.Bounds.Height:0.#} < text={paragraph.DesiredSize.Height:0.#}（文字会溢出到下一块）");
		}
	}

	[Fact]
	public void WidthRoundTripRemeasuresRealizedParagraphs()
	{
		// 交叉覆盖「滚动条出现/消失、右侧面板开关」这类宽度往返：高度缓存命中时不能跳过
		// 已经附着元素的重新测量，否则父级按旧宽度分配的高度装不下新宽度下的文本。
		var markdown = new MarkdownView
		{
			Source = string.Join("\n\n", Enumerable.Range(0, 12).Select(i =>
				$"## 标题 {i}\n\n第 {i} 段正文 " + string.Join("", Enumerable.Repeat("内容", 40)))),
			IsStreaming = false,
		};
		var scroll = Host(markdown);
		try
		{
			AssertNoOverflow(markdown, "初始宽度");
			Layout(scroll, 520);
			AssertNoOverflow(markdown, "变窄");
			Layout(scroll, 640);
			AssertNoOverflow(markdown, "回到原宽度");
			Layout(scroll, 360);
			AssertNoOverflow(markdown, "更窄");
			Layout(scroll, 640);
			AssertNoOverflow(markdown, "再次回到原宽度");
			output.WriteLine($"blocks realized: {Find<MarkdownParagraphView>(markdown).Count()}");
		}
		finally
		{
			UiLifetime.Release(scroll);
		}
	}

	[Fact]
	public void StreamedParagraphGrowthKeepsFollowingBlocksBelowIt()
	{
		// 流式追加：尾块每帧都长高。父级必须每帧重新测出它的高度，
		// 否则尾块在自己的高度之外继续画字，叠到紧随其后的块上。
		var markdown = new MarkdownView { IsStreaming = true, Source = "开头\n\n第二块正文" };
		var scroll = Host(markdown);
		try
		{
			var text = "开头段落";
			for (var frame = 0; frame < 40; frame++)
			{
				text += $"\n\n追加的第 {frame} 段，带一点长度让行高变化。";
				markdown.Append($"\n\n追加的第 {frame} 段，带一点长度让行高变化。");
				Layout(scroll, 640);
				AssertNoOverflow(markdown, $"流式第 {frame} 帧");
			}
			markdown.IsStreaming = false;
			Layout(scroll, 640);
			AssertNoOverflow(markdown, "定稿");
		}
		finally
		{
			UiLifetime.Release(scroll);
		}
	}

	[Fact]
	public void StreamedTableFinalizeKeepsTheTableDirectlyBelowTheList()
	{
		// 表格行会多次停在半个 `|` 上。增量解析曾把那一刻多出的空段落留在列表与表格之间，
		// 每帧再叠一个，最后表现为列表和表格之间一大段空白。
		const string source = "开头\n\n- 一项\n- 二项\n\n| a | b |\n| --- | --- |\n| c | d |\n| e | f |";
		var streamed = new MarkdownView { IsStreaming = true, FontSize = 16 };
		var scroll = Host(streamed);
		try
		{
			for (int index = 0; index < source.Length; index++)
			{
				streamed.Append(source[index].ToString());
				Layout(scroll, 640);
			}
			streamed.IsStreaming = false;
			Layout(scroll, 640);

			var full = new MarkdownView { FontSize = 16, Source = source };
			var fullScroll = Host(full);
			try
			{
				Assert.Equal(full.BlockCount, streamed.BlockCount);
				Assert.InRange(streamed.TotalHeight, full.TotalHeight - 1, full.TotalHeight + 1);
			}
			finally
			{
				UiLifetime.Release(fullScroll);
			}
		}
		finally
		{
			UiLifetime.Release(scroll);
		}
	}

	[Fact]
	public void TableCellsDoNotRemeasureWhenTheGridSnapsTracksToDevicePixels()
	{
		// 4 列星号轨道的表格在非整数可用宽度上会得到 215.5 这样的轨道：measure 用未取整的宽度，
		// arrange 把轨道端点对齐到设备像素（216）。段落曾经把这个一像素差当成版面失效，每轮
		// arrange 都重新标脏 measure，表格所在的窗口于是陷入 measure → arrange → InvalidateMeasure
		// → measure 的死循环（打开带表格的会话即 100% CPU 卡死）。
		// 必须挂在 Window 下：Grid 只对 ILayoutRoundingHost（窗口）做像素对齐，孤立控件直接 Pump
		// 不会出现这个差值，这也是它以前没被抓到的原因。
		var table = "| 部分 | 精简前 | 精简后 | 变化 |\n|---|---:|---:|---:|\n" +
			"| README + DESIGN + docs | 1,950 行 | 782 行 | **-60%** |\n" +
			"| 源码注释/XML 文档 | 10,774 行 | 5,696 行 | **-47%** |\n" +
			"| 测试代码 | 54,792 行 / 2,277 项 | 44,466 行 / 1,925 项 | **-18.8% / -15.5%** |\n" +
			"| scripts | 123 行 | 113 行 | **-8%** |";
		foreach (var width in new double[] { 950, 1060 })
		{
			var markdown = new MarkdownView { Source = table };
			var scroll = new ScrollViewer().Content(markdown).AutoVerticalScroll().NoHorizontalScroll();
			var window = new Window().Content(scroll);
			try
			{
				for (var pass = 0; pass < 3; pass++)
				{
					scroll.Measure(new Size(width, 700));
					scroll.Arrange(new Rect(0, 0, width, 700));
				}
				var cells = Find<MarkdownParagraphView>(markdown).ToArray();
				Assert.NotEmpty(cells);
				Assert.All(cells, cell => Assert.False(cell.IsMeasureDirty,
					$"w={width}: 单元格正文排布后又标脏 measure，布局不会收敛"));
			}
			finally
			{
				UiLifetime.Release(scroll);
				window.Dispose();
			}
		}
	}
}
