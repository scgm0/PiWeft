using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using PiWeft.Markdown;
using Xunit;
using Xunit.Abstractions;

namespace PiWeft.Desktop.Tests;

/// <summary>
/// Virtualization sweep: after every scroll position, every block intersecting the viewport must
/// be attached, measured, and — for paragraphs — carry a built text layout. A miss here is
/// exactly the "contiguous blank band between two rendered blocks" readers see as disappearing
/// content. Runs against a cold cache, a warm cache (historical re-entry) and after a
/// jump-to-bottom, mirroring the reported repro.
/// </summary>
[Collection("mewui-controls")]
public sealed class MarkdownVirtualizationSweepTests
{
	private readonly ITestOutputHelper _output;

	public MarkdownVirtualizationSweepTests(ITestOutputHelper output) => _output = output;

	private const double ViewportHeight = 720;
	private const double Width = 900;

	private static string Document()
	{
		var builder = new System.Text.StringBuilder();
		for (var section = 0; section < 24; section++)
		{
			builder.Append("## Section ").Append(section).Append(": 主流框架怎么做\n\n");
			builder.Append(" intro paragraph with **bold**, `inline code` and a [link](https://example.com/")
				.Append(section).Append(") long enough to wrap inside a reading column several times over.\n\n");
			builder.Append("1. 块级控件树 + 每块 Measure/Arrange\n");
			builder.Append("2. 非虚拟化时整篇一次性建树\n");
			builder.Append("3. 块滚出视口后释放，滚回来重新建\n\n");
			builder.Append("| 实现 | TextBlock | 高度 | 预热后 | 分配 |\n| --- | --- | --- | --- | --- |\n");
			builder.Append("| MarkdownPresenter | 74 | 3,634 DIP | ~50 ms | ~2.8 MB |\n");
			builder.Append("| MarkdownViewer | 25 | 视口内 | ~23 ms | ~1.33 MB |\n\n");
			builder.Append("> [!NOTE]\n> quote line one\n> quote line two\n\n");
			builder.Append("```csharp\nvar rendered = Render(section: ").Append(section).Append(");\n```\n\n");
		}
		return builder.ToString();
	}

	private static (ScrollViewer Scroll, MarkdownView View) Host(string source)
	{
		var view = new MarkdownView { FontSize = 14 };
		view.Source = source;
		var scroll = new ScrollViewer { Content = view, Width = Width, Height = ViewportHeight };
		scroll.Measure(new Size(Width, ViewportHeight));
		scroll.Arrange(new Rect(0, 0, Width, ViewportHeight));
		return (scroll, view);
	}

	private static void Pump(ScrollViewer scroll)
	{
		// Mirror the window layout loop: an arrange pass requests a measure for the newly visible
		// range, the next pass realizes it.
		scroll.Measure(new Size(Width, ViewportHeight));
		scroll.Arrange(new Rect(0, 0, Width, ViewportHeight));
		scroll.Measure(new Size(Width, ViewportHeight));
		scroll.Arrange(new Rect(0, 0, Width, ViewportHeight));
	}

	private void AssertViewportIsComplete(MarkdownView view, double offset)
	{
		int blocks = view.BlockCount;
		int checkedBlocks = 0;
		for (int index = 0; index < blocks; index++)
		{
			double top = view.GetBlockTopForTest(index);
			double height = view.GetBlockHeight(index);
			if (double.IsNaN(top) || double.IsNaN(height))
			{
				continue;
			}
			if (top + height <= offset || top >= offset + ViewportHeight)
			{
				continue;
			}
			checkedBlocks++;
			Assert.True(view.IsBlockAttachedForTest(index),
				$"offset={offset:F0} block {index} [{top:F0}..{top + height:F0}] intersects the viewport but is not attached");
			if (view.GetBlockElement(index) is MarkdownParagraphView paragraph)
			{
				Assert.NotEqual(double.NaN, paragraph.LayoutWidthForTest);
			}
		}
		Assert.True(checkedBlocks > 0, $"offset={offset:F0}: no block intersects the viewport");
	}

	private void Sweep(MarkdownView view, ScrollViewer scroll, double totalHeight)
	{
		int failures = 0;
		for (double offset = 0; offset < totalHeight; offset += 160)
		{
			scroll.SetScrollOffsets(0, offset);
			Pump(scroll);
			try
			{
				AssertViewportIsComplete(view, scroll.VerticalOffset);
			}
			catch (Xunit.Sdk.XunitException error)
			{
				failures++;
				if (failures <= 4)
				{
					_output.WriteLine(error.Message);
				}
			}
		}
		Assert.Equal(0, failures);
	}

	[Fact]
	public void SweepFindsNoBlankViewportRanges()
	{
		MarkdownView.ClearHeightCacheForTest();
		string source = Document();
		var (scroll, view) = Host(source);
		Pump(scroll);
		double totalHeight = view.TotalHeight;
		_output.WriteLine($"blocks={view.BlockCount} totalHeight={totalHeight:F0}");

		Sweep(view, scroll, totalHeight);
	}

	private const string RealDocumentPath = "/tmp/blank-repro.md";

	[Fact]
	public void RealSessionDocumentSweepsCleanAndListRowsRender()
	{
		MarkdownView.ClearHeightCacheForTest();
		if (!File.Exists(RealDocumentPath))
		{
			return; // fixture captured from the reported session; regenerate when available
		}
		string source = File.ReadAllText(RealDocumentPath);
		var (scroll, view) = Host(source);
		Pump(scroll);

		// Deep assertion: every visible list row has a positive height and every visible
		// paragraph (including the ones nested in list rows) carries a text layout.
		var listRows = new List<(int Index, double Top, double Height, int Rows)>();
		for (int index = 0; index < view.BlockCount; index++)
		{
			if (view.GetBlockElement(index) is MarkdownListPanel panel)
			{
				listRows.Add((index, view.GetBlockTopForTest(index), view.GetBlockHeight(index), panel.RowCount));
			}
		}
		_output.WriteLine($"blocks={view.BlockCount} lists={listRows.Count} rows=[{string.Join(',', listRows.Select(static row => row.Rows))}]");

		Sweep(view, scroll, view.TotalHeight);

		// After the sweep, every list row must still measure positive and every nested paragraph
		// must have a layout — a zero-height or unmeasured row paints as a hole.
		foreach (var (index, top, height, rows) in listRows)
		{
			Assert.True(height > 0, $"list block {index} collapsed to zero height");
			if (view.GetBlockElement(index) is MarkdownListPanel panel)
			{
				for (int row = 0; row < rows; row++)
				{
					if (panel.ContentAt(row) is StackPanel content)
					{
						foreach (var paragraph in global::PiWeft.Desktop.Tests.VisualTree.Find<MarkdownParagraphView>(content))
						{
							Assert.NotEqual(double.NaN, paragraph.LayoutWidthForTest);
						}
					}
				}
			}
		}
	}

	[Fact]
	public void RecycledRowSweepFindsNoBlankViewportRanges()
	{
		MarkdownView.ClearHeightCacheForTest();
		if (!File.Exists(RealDocumentPath))
		{
			return;
		}
		string source = File.ReadAllText(RealDocumentPath);
		var view = new MarkdownView { FontSize = 14 };
		view.Source = source;
		var scroll = new ScrollViewer { Content = view, Width = Width, Height = ViewportHeight };

		// Simulate the outer row virtualizer: detach the whole row, reattach it at another
		// scroll position, and sweep. The reattach must re-measure (RestoreAfterReattach).
		for (var round = 0; round < 3; round++)
		{
			scroll.Measure(new Size(Width, ViewportHeight));
			scroll.Arrange(new Rect(0, 0, Width, ViewportHeight));
			scroll.Content = null;
			scroll.Content = view;
			scroll.SetScrollOffsets(0, 600 + round * 350);
			Pump(scroll);
			AssertViewportIsComplete(view, scroll.VerticalOffset);
		}

		// Width jitter (scrollbar appearing/disappearing) must not leave stale layouts behind.
		for (var round = 0; round < 2; round++)
		{
			foreach (double width in new[] { Width - 12, Width })
			{
				scroll.Width = width;
				scroll.Measure(new Size(width, ViewportHeight));
				scroll.Arrange(new Rect(0, 0, width, ViewportHeight));
				scroll.Measure(new Size(width, ViewportHeight));
				scroll.Arrange(new Rect(0, 0, width, ViewportHeight));
				AssertViewportIsComplete(view, scroll.VerticalOffset);
			}
		}
	}

	[Fact]
	public void SweepWithWarmCacheFindsNoBlankViewportRanges()
	{
		MarkdownView.ClearHeightCacheForTest();
		string source = Document();

		// First pass warms the cross-instance cache and detaches/disposes far blocks.
		var (warmScroll, warmView) = Host(source);
		Pump(warmScroll);
		warmScroll.SetScrollOffsets(0, double.MaxValue);
		Pump(warmScroll);
		Pump(warmScroll);

		// Second view = re-entering the historical message with a warm cache.
		var (scroll, view) = Host(source);
		Pump(scroll);
		double totalHeight = view.TotalHeight;
		_output.WriteLine($"warm re-entry: blocks={view.BlockCount} totalHeight={totalHeight:F0} cache={MarkdownView.HeightCacheEntryCountForTest}");

		// Sweep up from the bottom first, then from the top: the reported blanks happened while
		// scrolling around a long message after jumps.
		scroll.SetScrollOffsets(0, double.MaxValue);
		Pump(scroll);
		Pump(scroll);
		for (double offset = Math.Max(0, scroll.VerticalOffset); offset >= 0; offset -= 160)
		{
			scroll.SetScrollOffsets(0, offset);
			Pump(scroll);
			AssertViewportIsComplete(view, scroll.VerticalOffset);
		}
		Sweep(view, scroll, totalHeight);
	}
}
