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
	public void BlocksAreClippedToTheViewAndTheirOwnSlot()
	{
		// 块的排布矩形由 MarkdownView 按测得的块高给出。块在 arrange 之后才长高（补实现新块、
		// 外层漏测）时，超出部分必须裁掉，否则会画到下一块或下一行上。
		var markdown = new MarkdownView
		{
			Source = string.Join("\n\n", Enumerable.Range(0, 6).Select(i => $"第 {i} 段 " + new string('文', 60))),
		};
		var scroll = Host(markdown);
		try
		{
			var blocks = Find<MarkdownParagraphView>(markdown).ToArray();
			Assert.NotEmpty(blocks);
			foreach (var block in blocks)
			{
				var clip = markdown.ChildClipForTest(block);
				Assert.NotNull(clip);
				var rect = clip.Value.Rect;
				// 纵向只允许 3 DIP 余量（行内代码底色/选区），再往外就会压到相邻块。
				Assert.True(rect.Top >= markdown.Bounds.Top - 3.5 && rect.Bottom <= markdown.Bounds.Bottom + 3.5,
					$"block clip {rect} leaves the view {markdown.Bounds}");
				Assert.True(rect.Top >= block.Bounds.Top - 3.5 && rect.Bottom <= block.Bounds.Bottom + 3.5,
					$"block clip {rect} leaves its own slot {block.Bounds}");
				Assert.True(rect.Contains(block.Bounds),
					$"block clip {rect} cuts the block it belongs to {block.Bounds}");
			}
		}
		finally
		{
			UiLifetime.Release(scroll);
		}
	}

	[Fact]
	public void ParagraphSkipsTheFrameWhenItsArrangedHeightIsStale()
	{
		// 外层漏测时（虚拟化行在自己的 arrange 里长高），段落版面是新文本的高度，Bounds 还是旧的。
		// 这一帧不能再画：画出去就压到下一块上，等下一次测量把行高补齐。
		var markdown = new MarkdownView { Source = "短" };
		var scroll = Host(markdown);
		try
		{
			var paragraph = Assert.Single(Find<MarkdownParagraphView>(markdown));
			var staleHeight = paragraph.Bounds.Height;
			markdown.Source = string.Join("", Enumerable.Repeat("把这一段写得足够长，长到在旧高度里放不下。", 6));
			// 只测量：版面换成新文本，排布高度仍是旧的那一版。
			scroll.Measure(new Size(640, 700));
			Assert.True(paragraph.DesiredSize.Height > staleHeight + 1,
				$"新文本没有长高：desired={paragraph.DesiredSize.Height:0.#} stale={staleHeight:0.#}");
			paragraph.Arrange(new Rect(paragraph.Bounds.X, paragraph.Bounds.Y, paragraph.Bounds.Width, staleHeight));
			Assert.False(paragraph.FitsArrangedHeight);

			Layout(scroll, 640);
			Assert.True(paragraph.FitsArrangedHeight);
		}
		finally
		{
			UiLifetime.Release(scroll);
		}
	}

	[Fact]
	public void StreamingTailIsNotClippedUntilItIsFinalized()
	{
		// 流式尾块每帧都在长高：不裁它，新露出的一行才不会先缺半截再补齐。
		// 其他块与定稿后的尾块仍按排布矩形裁剪，越界绘制压不到相邻内容。
		var markdown = new MarkdownView
		{
			IsStreaming = true,
			Source = "第一段\n\n第二段\n\n第三段正在写",
		};
		var scroll = Host(markdown);
		try
		{
			var blocks = Find<MarkdownParagraphView>(markdown).ToArray();
			Assert.True(blocks.Length >= 2);
			var tail = markdown.TailElementForTest;
			Assert.NotNull(tail);
			Assert.Null(markdown.ChildClipForTest(tail));
			var other = blocks.First(block => !ReferenceEquals(block, tail));
			Assert.NotNull(markdown.ChildClipForTest(other));

			markdown.IsStreaming = false;
			Layout(scroll, 640);
			Assert.NotNull(markdown.ChildClipForTest(tail));
		}
		finally
		{
			UiLifetime.Release(scroll);
		}
	}

	[Fact]
	public void StreamingParagraphKeepsDrawingWhenItsArrangedHeightIsStale()
	{
		// 同一份「测量晚一帧」的状态：流式期照画（尾块下面没有别的块），定稿后才吞掉这一帧。
		var markdown = new MarkdownView { Source = "短", IsStreaming = true };
		var scroll = Host(markdown);
		try
		{
			var paragraph = Assert.Single(Find<MarkdownParagraphView>(markdown));
			var staleHeight = paragraph.Bounds.Height;
			markdown.Source = string.Join("", Enumerable.Repeat("把这一段写得足够长，长到在旧高度里放不下。", 6));
			scroll.Measure(new Size(640, 700));
			paragraph.Arrange(new Rect(paragraph.Bounds.X, paragraph.Bounds.Y, paragraph.Bounds.Width, staleHeight));
			Assert.False(paragraph.FitsArrangedHeight);
			Assert.True(paragraph.ShouldDraw);

			markdown.IsStreaming = false;
			Assert.False(paragraph.ShouldDraw);
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
