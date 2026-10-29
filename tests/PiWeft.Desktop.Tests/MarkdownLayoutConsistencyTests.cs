using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using PiWeft.Markdown;
using Xunit;
using Xunit.Abstractions;
using static PiWeft.Desktop.Tests.VisualTree;

namespace PiWeft.Desktop.Tests;

/// <summary>
/// Markdown 排布一致性：块的行高必须与它内部真正排出来的文本高度一致，否则下一块会压在它上面
/// （读者看到的就是「偶发的正文错位重叠」）。这里用「父级给的高度 ≥ 子元素排版高度」作为
/// 不相交的必要条件，覆盖宽度往返与流式追加两种最容易出现缓存/测量不一致的场景。
/// </summary>
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

	/// <summary>每个已实现段落都必须落在父级给它的高度里（超出就会叠到下一块上）。</summary>
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
}
