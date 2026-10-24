using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Aprillz.MewUI.Markdown;
using PiWeft.Markdown;
using Xunit;
using Xunit.Abstractions;

namespace PiWeft.Desktop.Tests;

/// <summary>
/// Incremental Markdown control: model mapping, block diff/reuse, streaming paragraph chunks,
/// selection copy and outer-viewport block recycling.
/// </summary>
[Collection("mewui-controls")]
public sealed class MarkdownViewTests
{
	private readonly ITestOutputHelper _output;

	public MarkdownViewTests(ITestOutputHelper output) => _output = output;

	private static void Pump(UIElement view, double width = 520, double height = 400)
	{
		view.Measure(new Size(width, height));
		view.Arrange(new Rect(0, 0, width, height));
	}

	private static string Paragraphs(int count, int linesEach = 8)
	{
		var builder = new System.Text.StringBuilder();
		for (var index = 0; index < count; index++)
		{
			builder.Append("## Section ").Append(index).Append('\n').Append('\n');
			for (var line = 0; line < linesEach; line++)
			{
				builder.Append("Paragraph ").Append(index).Append(" line ").Append(line)
					.Append(" has **bold**, `code` and a [link](https://example.com/").Append(index)
					.Append(") with enough words to wrap in a reading column.\n\n");
			}
		}
		return builder.ToString();
	}

	[Fact]
	public void ParserMapsCommonBlocksAndStableHashes()
	{
		var document = MarkdownParser.Parse("# Title\n\nhello **world**\n\n- one\n- two\n\n```csharp\nvar x = 1;\n```");
		Assert.Equal(4, document.Blocks.Count);
		Assert.Equal(MarkdownBlockKind.Heading, document.Blocks[0].Kind);
		Assert.Equal("Title", document.Blocks[0].Text);
		Assert.Equal(MarkdownBlockKind.Paragraph, document.Blocks[1].Kind);
		Assert.Equal("hello world", document.Blocks[1].Text);
		Assert.Equal(MarkdownBlockKind.List, document.Blocks[2].Kind);
		Assert.Equal(MarkdownBlockKind.Code, document.Blocks[3].Kind);
		Assert.Equal("csharp", document.Blocks[3].CodeLanguage);

		var again = MarkdownParser.Parse("# Title\n\nhello **world**\n\n- one\n- two\n\n```csharp\nvar x = 1;\n```");
		Assert.Equal(document.Blocks.Select(static block => block.Hash), again.Blocks.Select(static block => block.Hash));

		foreach (var syntax in new[] { "-", "*", "+" })
		{
			var unordered = MarkdownParser.Parse($"{syntax} a\n{syntax} b");
			Assert.Equal("•\ta\n•\tb", unordered.ToPlainText());
		}
		Assert.Equal("1.\ta\n2.\tb", MarkdownParser.Parse("1. a\n2. b").ToPlainText());
		Assert.Equal("3.\ta\n4.\tb", MarkdownParser.Parse("3. a\n4. b").ToPlainText());
	}

	[Fact]
	public void StreamingAppendReusesTheFirstParagraphElement()
	{
		var view = new MarkdownView { FontSize = 14 };
		view.Source = "first paragraph\n\nsecond";
		Pump(view);
		var first = view.GetBlockElement(0);
		Assert.NotNull(first);
		Assert.Equal(2, view.BlockCount);

		view.Source = "first paragraph\n\nsecond paragraph grows";
		Pump(view);

		Assert.Equal(2, view.BlockCount);
		Assert.Same(first, view.GetBlockElement(0));
		Assert.Contains("grows", Assert.IsType<MarkdownParagraphView>(view.GetBlockElement(1)).Text);
		Assert.True(view.GetBlockElement(1) is MarkdownParagraphView paragraph && paragraph.ChunkCount >= 1);
	}

	[Fact]
	public void LongParagraphIsSplitIntoBoundedChunks()
	{
		var builder = new System.Text.StringBuilder();
		while (builder.Length < 12_000)
		{
			builder.Append("A long streamed paragraph with **bold** and `code` that keeps wrapping. ");
		}
		var view = new MarkdownView { FontSize = 14 };
		view.Source = builder.ToString();
		Pump(view);

		var paragraph = Assert.IsType<MarkdownParagraphView>(view.GetBlockElement(0));
		_output.WriteLine($"source={builder.Length}, blockText={paragraph.Text.Length}, chunks={paragraph.ChunkCount}, height={view.TotalHeight:F2}, blocks={view.BlockCount}");
		Assert.True(paragraph.ChunkCount > 1, "a 12 K paragraph must be frozen into chunks");
		Assert.True(paragraph.ChunkCount < 24, "chunk count must stay bounded");
	}

	[Fact]
	public void SelectAllCopiesAllBlocksIncludingUnrealizedOnes()
	{
		var view = new MarkdownView { FontSize = 14 };
		view.Source = "# Title\n\nfirst\n\nsecond";
		Pump(view);
		view.SelectAll();

		Assert.Equal("Title\nfirst\nsecond", view.SelectedText);
		Assert.True(view.HasSelection);
	}

	[Fact]
	public void OuterViewportKeepsOnlyNearbyBlocksRealized()
	{
		var view = new MarkdownView { FontSize = 14 };
		view.Source = Paragraphs(10);
		var scroll = new ScrollViewer { Content = view };
		scroll.Measure(new Size(520, 300));
		scroll.Arrange(new Rect(0, 0, 520, 300));

		_output.WriteLine($"blocks={view.BlockCount}, realized={view.RealizedBlockCount}");
		Assert.True(view.BlockCount > 50, $"blocks={view.BlockCount}");
		Assert.InRange(view.RealizedBlockCount, 1, 30);

		scroll.SetScrollOffsets(0, double.MaxValue);
		scroll.Measure(new Size(520, 300));
		scroll.Arrange(new Rect(0, 0, 520, 300));
		// The arrange pass requests a measure for the newly visible range; the next layout
		// pass realizes it, exactly as the window's layout loop does.
		scroll.Measure(new Size(520, 300));
		scroll.Arrange(new Rect(0, 0, 520, 300));

		Assert.NotNull(view.GetBlockElement(view.BlockCount - 1));
		Assert.True(view.RealizedBlockCount <= 30, $"realized after scroll: {view.RealizedBlockCount}");

		view.SelectAll();
		Assert.Contains("Section 9", view.SelectedText, StringComparison.Ordinal);
	}


	[Fact]
	public void CodeBlockUsesACardHeaderAndPaddedBody()
	{
		var view = new MarkdownView { FontSize = 14 };
		view.Source = "```csharp\nvar a = 1;\n```";
		Pump(view);

		var code = Assert.IsType<MarkdownCodeBlockView>(view.GetBlockElement(0));
		Assert.Equal(1, code.Chrome.BorderThickness);
		Assert.Equal(10, code.Chrome.CornerRadius);
		Assert.True(code.Chrome.ClipToBounds);
		Assert.Contains("csharp", VisualTree.Find<TextBlock>(code).Select(block => block.Text));
		var copy = VisualTree.Find<Button>(code).Single();
		Assert.Equal("Copy code", Assert.IsType<TextBlock>(copy.ToolTip).Text);
		Assert.NotNull(copy.Content);
		var header = VisualTree.Find<Border>(code).First(border => border.Height == 34);
		Assert.Equal(34, header.DesiredSize.Height, 1);
		var body = VisualTree.Find<ScrollViewer>(code).Single();
		// 左侧留白由行号列提供（ShowCodeLineNumbers=false 时才回到 12），右侧仍是内边距。
		Assert.Equal(0, body.Padding.Left);
		Assert.Equal(12, body.Padding.Right);
	}

	[Fact]
	public void CodeCopyActionIsCenteredInTheButton()
	{
		// 宿主给的图标常常只设了纵向居中：固定宽度下水平默认是 Stretch，图标就会贴在按钮左侧。
		var host = new Border { Width = 14, Height = 14, VerticalAlignment = VerticalAlignment.Center };
		var view = new MarkdownView { FontSize = 14 };
		view.MarkdownTheme = new PiWeft.Markdown.MarkdownTheme { CodeCopyIcon = _ => host };
		view.Source = "```csharp\nvar a = 1;\n```";
		Pump(view);

		var code = Assert.IsType<MarkdownCodeBlockView>(view.GetBlockElement(0));
		var copy = VisualTree.Find<Button>(code).Single();
		Assert.Equal(14, host.RenderSize.Width, 1);
		Assert.Equal(14, host.RenderSize.Height, 1);
		Assert.Equal(
			Math.Round(copy.Bounds.X + copy.Bounds.Width / 2, 1),
			Math.Round(host.Bounds.X + host.Bounds.Width / 2, 1), 0.6);
		Assert.Equal(
			Math.Round(copy.Bounds.Y + copy.Bounds.Height / 2, 1),
			Math.Round(host.Bounds.Y + host.Bounds.Height / 2, 1), 0.6);
	}

	[Fact]
	public void CodeCopyButtonFallsBackToItsTextLabelWithoutAHostIcon()
	{
		var view = new MarkdownView { FontSize = 14 };
		view.MarkdownTheme = new PiWeft.Markdown.MarkdownTheme { CodeCopyLabel = "复制", CodeCopyTooltip = "复制代码" };
		view.Source = "```csharp\nvar a = 1;\n```";
		Pump(view);

		var code = Assert.IsType<MarkdownCodeBlockView>(view.GetBlockElement(0));
		var copy = VisualTree.Find<Button>(code).Single();
		Assert.Equal("复制代码", Assert.IsType<TextBlock>(copy.ToolTip).Text);
		// 没有宿主图标时退回本地化文本；控件库自己绝不画图标。
		Assert.Equal("复制", Assert.IsType<TextBlock>(copy.Content).Text);
	}

	[Fact]
	public void CodeCopyButtonUsesTheIconTheHostSupplies()
	{
		var idle = new Border { Width = 14, Height = 14 };
		var confirmed = new Border { Width = 14, Height = 14 };
		var idleRequests = 0;
		var confirmedRequests = 0;
		var view = new MarkdownView { FontSize = 14 };
		view.MarkdownTheme = new PiWeft.Markdown.MarkdownTheme
		{
			CodeCopyLabel = "复制",
			CodeCopyTooltip = "复制代码",
			CodeCopyIcon = size => { idleRequests++; Assert.Equal(14, size); return idle; },
			CodeCopiedIcon = size => { confirmedRequests++; return confirmed; },
		};
		view.Source = "```csharp\nvar a = 1;\n```";
		Pump(view);

		var code = Assert.IsType<MarkdownCodeBlockView>(view.GetBlockElement(0));
		var copy = VisualTree.Find<Button>(code).Single();
		Assert.Same(idle, copy.Content);
		Assert.Equal(1, idleRequests);
		Assert.DoesNotContain("复制", VisualTree.Find<TextBlock>(code).Select(block => block.Text));
		// 确认图标只在真的复制成功那一刻才要（无剪贴板的测试运行里不会触发）。
		Assert.Equal(0, confirmedRequests);
		Assert.True(copy.Width <= 30, $"icon button width={copy.Width}");

		var header = VisualTree.Find<Border>(code).Single(border => border.Height == 34);
		Assert.True(header.Bounds.Height > 0);
	}

	[Fact]
	public void TableUsesACardHeaderZebraAndContentWeightedColumns()
	{
		var view = new MarkdownView { FontSize = 14 };
		view.Source = "| scenario | middle | p90 |\n| --- | ---: | ---: |\n| a much longer scenario label | ~6 ms | ~14 ms |\n| short | ~1 ms | ~2 ms |\n| other | ~3 ms | ~4 ms |";
		Pump(view, 520, 20_000);

		var table = Assert.IsType<Border>(view.GetBlockElement(0));
		Assert.Equal(1, table.BorderThickness);
		Assert.Equal(10, table.CornerRadius);
		Assert.True(table.ClipToBounds);
		var grid = Assert.IsType<Grid>(table.Child);
		Assert.True(grid.ColumnDefinitions[0].Width.Value > grid.ColumnDefinitions[2].Width.Value,
			$"columns={string.Join(",", grid.ColumnDefinitions.Select(column => column.Width.Value))}");

		var cells = grid.Children.OfType<Border>().ToArray();
		Assert.Equal(new Thickness(10, 7, 10, 7), cells[0].Padding);
		var header = cells.First(cell => Grid.GetRow(cell) == 0);
		var alternate = cells.First(cell => Grid.GetRow(cell) == 2);
		var last = cells.Last();
		Assert.True(header.Background.A > 0, "the header needs a tint");
		Assert.True(alternate.Background.A > 0, "the second body row needs a zebra tint");
		var cellText = VisualTree.Find<MarkdownParagraphView>(table).First();
		Assert.InRange(cellText.FontSize, 12, 13);
		Assert.Equal(0, last.NonUniformBorderThickness.Bottom);
		Assert.Equal(1, cells.First(cell => Grid.GetRow(cell) == 1).NonUniformBorderThickness.Bottom);
	}


	[Fact]
	public void InlineCodeHasOuterLayoutSpacingAroundTheChip()
	{
		var view = new MarkdownView { FontSize = 14 };
		view.Source = "前`代码`后";
		Pump(view);

		var paragraph = Assert.IsType<MarkdownParagraphView>(view.GetBlockElement(0));
		var runs = paragraph.Runs;
		var code = runs.Select((run, index) => (run, index))
			.Single(item => (item.run.Style & MarkdownRunStyle.Code) != 0);
		Assert.Equal("代码", code.run.Text);
		Assert.Equal("\u2009", runs[code.index - 1].Text);
		Assert.Equal("\u2009", runs[code.index + 1].Text);
		Assert.Equal("前", runs[code.index - 2].Text);
		Assert.Equal("后", runs[code.index + 2].Text);
		view.SelectAll();
		Assert.Equal("前代码后", view.SelectedText);
	}

	[Fact]
	public void StreamingCodeBlockUpdatesTheSameView()
	{
		var oldText = "```csharp\nvar a = 1;\n";
		var newText = "```csharp\nvar a = 1;\nvar b = 2;\n";
		var view = new MarkdownView { FontSize = 14 };
		view.Source = oldText;
		Pump(view);
		var code = Assert.IsType<MarkdownCodeBlockView>(view.GetBlockElement(0));
		Assert.Contains("var a = 1", code.TextView.Text);

		view.Source = newText;
		Pump(view);

		Assert.Same(code, view.GetBlockElement(0));
		Assert.Contains("var b = 2", code.TextView.Text);

		view.Source = "```csharp\nvar a = 1;\nvar b = 2;\n```";
		Pump(view);
		Assert.Same(code, view.GetBlockElement(0));
	}

	[Fact]
	public void ChunkedParagraphHeightMatchesAFullLayout()
	{
		var text = string.Join(" ", Enumerable.Range(0, 900).Select(index =>
			$"sentence {index} has enough plain words to wrap several lines in a reading column"));
		var view = new MarkdownView { FontSize = 14 };
		// MewUI 的 MarkdownPresenter 没有行距开关，拿它当“一次成型”的参照时，只能把本控件的
		// 默认行距关掉（行距本身由 MarkdownLineSpacingTests 负责）：这里要验的是分块排版与
		// 整体排版高度一致，不是两个控件的字距节奏一致。
		view.MarkdownTheme = new PiWeft.Markdown.MarkdownTheme { LineHeightScale = 0, HeadingLineHeightScale = 0 };
		view.Source = text;
		Pump(view, 520, 20_000);

		using var reference = new MarkdownPresenter { FontSize = 14, FontFamily = view.FontFamily, Markdown = text };
		reference.Measure(new Size(520, double.PositiveInfinity));

		_output.WriteLine($"view={view.TotalHeight:F2}, reference={reference.DesiredSize.Height:F2}");
		Assert.InRange(view.TotalHeight, reference.DesiredSize.Height - 1.5, reference.DesiredSize.Height + 1.5);
	}

	[Fact]
	public void ClosingEmphasisRestylesFrozenChunks()
	{
		var body = string.Concat(Enumerable.Repeat("bold words that keep wrapping across lines ", 80)).TrimEnd();
		var view = new MarkdownView { FontSize = 14 };
		view.Source = "**" + body;
		Pump(view, 520, 20_000);
		var paragraph = Assert.IsType<MarkdownParagraphView>(view.GetBlockElement(0));
		Assert.True(paragraph.ChunkCount > 1);
		Assert.Contains("**", paragraph.Text, StringComparison.Ordinal);

		view.Source = "**" + body + "**";
		Pump(view, 520, 20_000);

		// Closing the emphasis removes the literal delimiters from the display text and restyles
		// every line; the block is rebuilt instead of painting the old literal text.
		var updated = Assert.IsType<MarkdownParagraphView>(view.GetBlockElement(0));
		Assert.Equal(body, updated.Text);
		Assert.True(updated.ChunkCount > 1);
		Assert.Contains(updated.Runs, run => (run.Style & MarkdownRunStyle.Bold) != 0 && run.Text.Contains("bold words", StringComparison.Ordinal));
	}

	[Fact]
	public void StreamingAppendCostStaysBounded()
	{
		var builder = new System.Text.StringBuilder();
		for (var index = 0; index < 180; index++)
		{
			builder.Append("## Section ").Append(index).Append('\n').Append('\n');
			builder.Append("Paragraph ").Append(index).Append(" has **bold**, `code` and a [link](https://example.com/").Append(index)
				.Append(") with enough words to wrap in a reading column.\n\n");
		}
		var text = builder.ToString();
		var view = new MarkdownView { FontSize = 14 };
		view.Source = text;
		Pump(view, 520, 20_000);

		var times = new List<double>();
		var allocations = new List<long>();
		var current = text;
		for (var index = 0; index < 40; index++)
		{
			current += "\n\nstreamed tail";
			var before = GC.GetAllocatedBytesForCurrentThread();
			var watch = System.Diagnostics.Stopwatch.StartNew();
			view.Source = current;
			Pump(view, 520, 20_000);
			times.Add(watch.Elapsed.TotalMilliseconds);
			allocations.Add(GC.GetAllocatedBytesForCurrentThread() - before);
		}
		times.Sort();
		allocations.Sort();
		_output.WriteLine($"append median={times[times.Count / 2]:F2} ms p90={times[(int)(times.Count * 0.9)]:F2} ms max={times[^1]:F2} ms alloc={allocations[allocations.Count / 2] / 1024:F0} KiB");
		Assert.True(times[times.Count / 2] < 40, $"append median {times[times.Count / 2]:F2} ms");
	}

	[Fact]
	public void SingleLongParagraphStreamsWithoutFullRelayout()
	{
		var builder = new System.Text.StringBuilder();
		while (builder.Length < 24_000)
		{
			builder.Append("A streamed paragraph with **bold** and `code` that keeps wrapping. ");
		}
		var text = builder.ToString();
		var view = new MarkdownView { FontSize = 14 };
		view.Source = string.Empty;
		Pump(view, 520, 20_000);

		var times = new List<double>();
		var allocations = new List<long>();
		var appended = new System.Text.StringBuilder();
		for (var offset = 0; offset < text.Length; offset += 96)
		{
			appended.Append(text.AsSpan(offset, Math.Min(96, text.Length - offset)));
			var before = GC.GetAllocatedBytesForCurrentThread();
			var watch = System.Diagnostics.Stopwatch.StartNew();
			view.Source = appended.ToString();
			Pump(view, 520, 20_000);
			times.Add(watch.Elapsed.TotalMilliseconds);
			allocations.Add(GC.GetAllocatedBytesForCurrentThread() - before);
		}
		var paragraph = Assert.IsType<MarkdownParagraphView>(view.GetBlockElement(0));
		Assert.True(paragraph.ChunkCount > 1);
		times.Sort();
		allocations.Sort();
		_output.WriteLine($"paragraph append median={times[times.Count / 2]:F2} ms p90={times[(int)(times.Count * 0.9)]:F2} ms max={times[^1]:F2} ms alloc={allocations[allocations.Count / 2] / 1024:F0} KiB chunks={paragraph.ChunkCount}");
		Assert.True(times[times.Count / 2] < 40, $"paragraph append median {times[times.Count / 2]:F2} ms");

		// Compare against a fresh instance of the same control. This keeps DPI, theme and font
		// resolution identical while validating that the chunked streamed layout has the same
		// total height as a layout built from the final text in one pass.
		var full = new MarkdownView { FontSize = 14 };
		full.Source = text;
		Pump(full, 520, 20_000);
		_output.WriteLine($"streamed height={view.TotalHeight:F2}, full height={full.TotalHeight:F2}");
		Assert.InRange(view.TotalHeight, full.TotalHeight - 1, full.TotalHeight + 1);

		var beforeFinalize = paragraph;
		var watchFinalize = System.Diagnostics.Stopwatch.StartNew();
		view.IsStreaming = false;
		Pump(view, 520, 20_000);
		watchFinalize.Stop();
		_output.WriteLine($"finalize={watchFinalize.Elapsed.TotalMilliseconds:F2} ms");
		Assert.Same(beforeFinalize, view.GetBlockElement(0));
		Assert.True(watchFinalize.Elapsed.TotalMilliseconds < 20, $"finalize {watchFinalize.Elapsed.TotalMilliseconds:F2} ms");
	}

	[Fact]
	public void ListTableAndQuoteTextParticipatesInSelection()
	{
		var view = new MarkdownView { FontSize = 14 };
		view.Source = "1. one\n2. two\n\n> quote\n\n| a | b |\n| --- | --- |\n| c | d |";
		Pump(view, 520, 20_000);

		var paragraphs = VisualTree.Find<MarkdownParagraphView>(view).ToArray();
		Assert.True(paragraphs.Length >= 7, $"paragraphs={paragraphs.Length}");
		Assert.All(paragraphs, paragraph => Assert.True(paragraph.TextUnit >= 0,
			$"paragraph [{paragraph.Text}] unit={paragraph.TextUnit}"));

		view.SelectAll();
		Assert.All(paragraphs, paragraph => Assert.True(
			paragraph.SelectionRange.End > paragraph.SelectionRange.Start,
			$"paragraph [{paragraph.Text}] range={paragraph.SelectionRange}"));

		Assert.Contains("one", view.SelectedText, StringComparison.Ordinal);
		Assert.Contains("two", view.SelectedText, StringComparison.Ordinal);
		Assert.Contains("quote", view.SelectedText, StringComparison.Ordinal);
		Assert.Contains("a\tb", view.SelectedText, StringComparison.Ordinal);
		Assert.Contains("c\td", view.SelectedText, StringComparison.Ordinal);
	}


	[Fact]
	public void MouseSelectionWorksInsideListAndTable()
	{
		var view = new MarkdownView { FontSize = 14 };
		view.Source = "1. one\n2. two\n\n| a | b |\n| --- | --- |\n| c | d |";
		Pump(view, 520, 20_000);

		var paragraphs = VisualTree.Find<MarkdownParagraphView>(view).ToArray();
		var first = paragraphs.First(paragraph => paragraph.Text == "one");
		var last = paragraphs.First(paragraph => paragraph.Text == "d");
		var start = new Point(first.Bounds.X + 2, first.Bounds.Y + 4);
		var end = new Point(last.Bounds.Right - 2, last.Bounds.Y + 4);

		Assert.True(view.BeginSelectionAtForTest(start), "the list item must be a selection target");
		view.ExtendSelectionToForTest(end);

		Assert.True(view.HasSelection,
			$"anchor={view.SelectionAnchorForTest} focus={view.SelectionFocusForTest} start={start} end={end} first={first.Bounds} last={last.Bounds} view={view.Bounds}");
		Assert.Contains("one", view.SelectedText, StringComparison.Ordinal);
		Assert.Contains("c\td", view.SelectedText, StringComparison.Ordinal);
	}


	[Fact]
	public void ReusedContainerRefreshesNestedTextUnits()
	{
		var view = new MarkdownView { FontSize = 14 };
		view.Source = "1. one\n2. two\n\n| a | b |\n| --- | --- |\n| c | d |";
		Pump(view, 520, 20_000);
		var listElement = view.GetBlockElement(0);
		var tableElement = view.GetBlockElement(1);

		// Prepending a paragraph shifts every following text unit by one. The list and table
		// blocks are reused by hash, so their nested paragraphs must be renumbered.
		view.Source = "intro\n\n1. one\n2. two\n\n| a | b |\n| --- | --- |\n| c | d |";
		Pump(view, 520, 20_000);

		Assert.Same(listElement, view.GetBlockElement(1));
		Assert.Same(tableElement, view.GetBlockElement(2));
		var one = VisualTree.Find<MarkdownParagraphView>(view).Single(paragraph => paragraph.Text == "one");
		var d = VisualTree.Find<MarkdownParagraphView>(view).Single(paragraph => paragraph.Text == "d");
		Assert.Equal(1, one.TextUnit);
		Assert.True(d.TextUnit > one.TextUnit, $"d unit={d.TextUnit}, one unit={one.TextUnit}");

		view.SelectAll();
		Assert.Contains("intro", view.SelectedText, StringComparison.Ordinal);
		Assert.Contains("c\td", view.SelectedText, StringComparison.Ordinal);
	}


	[Fact]
	public void StreamingParagraphTailRewriteKeepsTheSameElement()
	{
		var view = new MarkdownView { FontSize = 14 };
		view.Source = "before [link](https://example.com";
		Pump(view);
		var paragraph = view.GetBlockElement(0);

		view.Source = "before [link](https://example.com)";
		Pump(view);

		Assert.Same(paragraph, view.GetBlockElement(0));
		Assert.Equal("before link", Assert.IsType<MarkdownParagraphView>(paragraph).Text);
	}


	[Fact]
	public void FocusableParagraphsAreExposedToTheHost()
	{
		var view = new MarkdownView { FontSize = 14 };
		var created = new List<FrameworkElement>();
		view.FocusableElementCreated = created.Add;
		view.Source = "plain\n\n[link](https://example.com)";
		Pump(view);

		Assert.Contains(created, element =>
			element is MarkdownParagraphView { Focusable: true } paragraph && paragraph.LinkCount == 1);
	}

	[Fact]
	public void StreamingListKeepsTheListAndLastItemElements()
	{
		var view = new MarkdownView { FontSize = 14 };
		view.Source = "- first\n- second";
		Pump(view);
		var list = view.GetBlockElement(0);
		var second = VisualTree.Find<MarkdownParagraphView>(view).Single(paragraph => paragraph.Text == "second");

		view.Source = "- first\n- second grows";
		Pump(view);

		Assert.Same(list, view.GetBlockElement(0));
		var updated = VisualTree.Find<MarkdownParagraphView>(view).Single(paragraph => paragraph.Text == "second grows");
		Assert.Same(second, updated);
	}

	[Fact]
	public void StreamingQuoteKeepsTheQuoteAndLastParagraphElements()
	{
		var view = new MarkdownView { FontSize = 14 };
		view.Source = "> first\n>\n> second";
		Pump(view);
		var quote = view.GetBlockElement(0);
		var second = VisualTree.Find<MarkdownParagraphView>(view).Single(paragraph => paragraph.Text == "second");

		view.Source = "> first\n>\n> second grows";
		Pump(view);

		Assert.Same(quote, view.GetBlockElement(0));
		var updated = VisualTree.Find<MarkdownParagraphView>(view).Single(paragraph => paragraph.Text == "second grows");
		Assert.Same(second, updated);
	}



	[Fact]
	public void ReattachingARealizedViewRequestsANewMeasureAndKeepsBlocksVisible()
	{
		var view = new MarkdownView { FontSize = 14 };
		view.Source = Paragraphs(30);
		Assert.True(view.Source.Length > 0);
		var scroll = new ScrollViewer { Content = view };
		scroll.Measure(new Size(520, 300));
		scroll.Arrange(new Rect(0, 0, 520, 300));
		scroll.Measure(new Size(520, 300));
		scroll.Arrange(new Rect(0, 0, 520, 300));
		Assert.False(view.IsMeasureDirty);

		// Move the view out of the viewport, then detach/attach it the way a recycled
		// virtualized row does. The cached measure must not suppress the realize pass.
		scroll.SetScrollOffsets(0, 2000);
		scroll.Measure(new Size(520, 300));
		scroll.Arrange(new Rect(0, 0, 520, 300));
		scroll.Measure(new Size(520, 300));
		scroll.Arrange(new Rect(0, 0, 520, 300));
		Assert.True(view.BlockCount > 256, $"the test must exercise recycling, blocks={view.BlockCount}");
		Assert.True(view.RealizedBlockCount < view.BlockCount, $"blocks={view.BlockCount} realized={view.RealizedBlockCount}");
		scroll.Content = null;
		scroll.Content = view;

		Assert.True(view.IsMeasureDirty, "reattaching a virtualized Markdown view must invalidate measure");
		scroll.Measure(new Size(520, 300));
		scroll.Arrange(new Rect(0, 0, 520, 300));
		scroll.Measure(new Size(520, 300));
		scroll.Arrange(new Rect(0, 0, 520, 300));
		Assert.True(view.RealizedBlockCount > 0, "reattached Markdown must realize visible blocks");
		// 回收期间被 detach 的块必须重新挂回来并落在视口里，否则正文只剩空白，要等下一次滚动才恢复。
		var viewport = scroll.Bounds;
		Assert.Contains(Enumerable.Range(0, view.BlockCount).Select(view.GetBlockElement).OfType<UIElement>(),
			element => element.Bounds.Bottom > viewport.Y && element.Bounds.Y < viewport.Bottom &&
				element.Bounds.Height > 0);
	}

	[Fact]
	public void StreamingTableKeepsTheTableAndLastCellElements()
	{
		var view = new MarkdownView { FontSize = 14 };
		view.Source = "| a | b |\n| --- | --- |\n| c | d";
		Pump(view);
		var table = view.GetBlockElement(0);
		var lastCell = VisualTree.Find<MarkdownParagraphView>(view).Single(paragraph => paragraph.Text == "d");

		view.Source = "| a | b |\n| --- | --- |\n| c | d grows";
		Pump(view);

		Assert.Same(table, view.GetBlockElement(0));
		var updated = VisualTree.Find<MarkdownParagraphView>(view).Single(paragraph => paragraph.Text == "d grows");
		Assert.Same(lastCell, updated);
	}

	[Fact]
	public void StreamingNestedListKeepsNestedGridAndParagraphElements()
	{
		var view = new MarkdownView { FontSize = 14 };
		view.Source = "- outer\n  - inner";
		Pump(view);
		var outer = view.GetBlockElement(0);
		var inner = VisualTree.Find<MarkdownParagraphView>(view).Single(paragraph => paragraph.Text == "inner");

		view.Source = "- outer\n  - inner grows";
		Pump(view);

		Assert.Same(outer, view.GetBlockElement(0));
		var updated = VisualTree.Find<MarkdownParagraphView>(view).Single(paragraph => paragraph.Text == "inner grows");
		Assert.Same(inner, updated);
	}


	[Fact]
	public void NewListItemAppendsThenStreamsInPlace()
	{
		var view = new MarkdownView { FontSize = 14 };
		view.Source = "- first\n- second";
		Pump(view);
		var list = view.GetBlockElement(0);

		view.Source = "- first\n- second\n- third";
		Pump(view);
		Assert.Same(list, view.GetBlockElement(0));
		var third = VisualTree.Find<MarkdownParagraphView>(view).Single(paragraph => paragraph.Text == "third");

		view.Source = "- first\n- second\n- third grows";
		Pump(view);

		Assert.Same(list, view.GetBlockElement(0));
		var updated = VisualTree.Find<MarkdownParagraphView>(view).Single(paragraph => paragraph.Text == "third grows");
		Assert.Same(third, updated);
	}


	[Fact]
	public void DuplicateBlocksKeepDistinctTextUnitsWhenRecycled()
	{
		var view = new MarkdownView { FontSize = 14 };
		view.Source = string.Join("\n\n", Enumerable.Repeat("same", 20));
		var scroll = new ScrollViewer { Content = view };
		scroll.Measure(new Size(520, 40));
		scroll.Arrange(new Rect(0, 0, 520, 40));

		scroll.SetScrollOffsets(0, double.MaxValue);
		for (var pass = 0; pass < 2; pass++)
		{
			scroll.Measure(new Size(520, 40));
			scroll.Arrange(new Rect(0, 0, 520, 40));
		}

		var last = Assert.IsType<MarkdownParagraphView>(view.GetBlockElement(view.BlockCount - 1));
		Assert.Equal(19, last.TextUnit);
		view.SelectAll();
		Assert.Equal(20, view.SelectedText.Split("same", StringSplitOptions.None).Length - 1);
	}


	[Fact]
	public void StreamingLargeListCostStaysBounded()
	{
		var builder = new System.Text.StringBuilder();
		for (var index = 0; index < 200; index++)
		{
			builder.Append("- item ").Append(index).Append('\n');
		}
		var text = builder.ToString().TrimEnd();
		var view = new MarkdownView { FontSize = 14 };
		view.Source = text;
		var initialWatch = System.Diagnostics.Stopwatch.StartNew();
		Pump(view, 520, 20_000);
		initialWatch.Stop();
		var list = view.GetBlockElement(0);
		_output.WriteLine($"large list initial={initialWatch.Elapsed.TotalMilliseconds:F2} ms");

		var times = new List<double>();
		var same = 0;
		var current = text;
		for (var index = 0; index < 30; index++)
		{
			current += " grow";
			var watch = System.Diagnostics.Stopwatch.StartNew();
			view.Source = current;
			Pump(view, 520, 20_000);
			times.Add(watch.Elapsed.TotalMilliseconds);
			if (ReferenceEquals(list, view.GetBlockElement(0))) same++;
		}
		_output.WriteLine($"same list={same}/30");
		times.Sort();
		_output.WriteLine($"large list append median={times[times.Count / 2]:F2} ms p90={times[(int)(times.Count * 0.9)]:F2} ms max={times[^1]:F2} ms");
		Assert.True(times[times.Count / 2] < 40, $"large list append median {times[times.Count / 2]:F2} ms");
	}


	[Fact]
	public void StreamingListRowHeightCacheTracksTheGrowingItem()
	{
		var shortText = "- first\n- second";
		var longText = "- first\n- second " + string.Join(" ", Enumerable.Repeat("with enough words to wrap across several lines", 12));
		var view = new MarkdownView { FontSize = 14 };
		view.Source = shortText;
		Pump(view, 520, 20_000);
		var list = view.GetBlockElement(0);

		view.Source = longText;
		Pump(view, 520, 20_000);

		Assert.Same(list, view.GetBlockElement(0));
		var full = new MarkdownView { FontSize = 14 };
		full.Source = longText;
		Pump(full, 520, 20_000);
		_output.WriteLine($"list streamed={view.TotalHeight:F2}, full={full.TotalHeight:F2}");
		Assert.InRange(view.TotalHeight, full.TotalHeight - 1, full.TotalHeight + 1);
	}


	[Fact]
	public void AppendingListItemsKeepsTheListElementAndRows()
	{
		var view = new MarkdownView { FontSize = 14 };
		view.Source = "- item 0\n- item 1";
		Pump(view, 520, 20_000);
		var list = Assert.IsType<MarkdownListPanel>(view.GetBlockElement(0));

		for (var index = 2; index < 12; index++)
		{
			view.Source += "\n- item " + index;
			Pump(view, 520, 20_000);
			Assert.Same(list, view.GetBlockElement(0));
		}

		Assert.Equal(12, list.RowCount);
		view.SelectAll();
		Assert.Contains("item 11", view.SelectedText, StringComparison.Ordinal);
	}


	[Fact]
	public void AppendingQuoteParagraphsKeepsTheQuoteElement()
	{
		var view = new MarkdownView { FontSize = 14 };
		view.Source = "> first";
		Pump(view);
		var quote = view.GetBlockElement(0);

		view.Source = "> first\n>\n> second";
		Pump(view);
		Assert.Same(quote, view.GetBlockElement(0));
		var second = VisualTree.Find<MarkdownParagraphView>(view).Single(paragraph => paragraph.Text == "second");

		view.Source = "> first\n>\n> second grows";
		Pump(view);
		Assert.Same(quote, view.GetBlockElement(0));
		var updated = VisualTree.Find<MarkdownParagraphView>(view).Single(paragraph => paragraph.Text == "second grows");
		Assert.Same(second, updated);
	}


	[Fact]
	public void AppendingTableRowsKeepsTheTableElement()
	{
		var view = new MarkdownView { FontSize = 14 };
		view.Source = "| a | b |\n| --- | --- |\n| c | d";
		Pump(view);
		var table = view.GetBlockElement(0);
		var d = VisualTree.Find<MarkdownParagraphView>(view).Single(paragraph => paragraph.Text == "d");

		view.Source += "\n| e | f";
		Pump(view);
		Assert.Same(table, view.GetBlockElement(0));
		Assert.Contains(VisualTree.Find<MarkdownParagraphView>(view), paragraph => paragraph.Text == "e");
		var f = VisualTree.Find<MarkdownParagraphView>(view).Single(paragraph => paragraph.Text == "f");

		view.Source += " grows";
		Pump(view);
		Assert.Same(table, view.GetBlockElement(0));
		var updated = VisualTree.Find<MarkdownParagraphView>(view).Single(paragraph => paragraph.Text == "f grows");
		Assert.Same(f, updated);
		Assert.Same(d, VisualTree.Find<MarkdownParagraphView>(view).Single(paragraph => paragraph.Text == "d"));
	}


	[Fact]
	public void UnorderedListUsesBulletMarkerNotCheckbox()
	{
		foreach (var syntax in new[] { "-", "*", "+" })
		{
			var view = new MarkdownView { FontSize = 14 };
			view.Source = $"{syntax} one\n{syntax} two";
			Pump(view);

			var list = Assert.IsType<MarkdownListPanel>(view.GetBlockElement(0));
			var marker = Assert.IsType<TextBlock>(list.MarkerAt(0));
			Assert.Equal("•", marker.Text);

			view.SelectAll();
			Assert.StartsWith("•\tone", view.SelectedText, StringComparison.Ordinal);
		}
	}

	[Fact]
	public void TaskListUsesCheckboxMarker()
	{
		var view = new MarkdownView { FontSize = 14 };
		view.Source = "- [ ] todo\n- [x] done";
		Pump(view);

		var list = Assert.IsType<MarkdownListPanel>(view.GetBlockElement(0));
		Assert.IsType<CheckBox>(list.MarkerAt(0));
		Assert.IsType<CheckBox>(list.MarkerAt(1));

		view.SelectAll();
		Assert.Contains("todo", view.SelectedText, StringComparison.Ordinal);
		Assert.Contains("done", view.SelectedText, StringComparison.Ordinal);
	}

}
