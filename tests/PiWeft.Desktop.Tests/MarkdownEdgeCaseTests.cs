using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using PiWeft.Markdown;
using Xunit;

namespace PiWeft.Desktop.Tests;

/// <summary>Edge cases of the incremental Markdown control: block shapes, markers and streaming.</summary>
[Collection("mewui-controls")]
public sealed class MarkdownEdgeCaseTests
{
	private static void Pump(UIElement view, double width = 520, double height = 20_000)
	{
		view.Measure(new Size(width, height));
		view.Arrange(new Rect(0, 0, width, height));
	}

	private static MarkdownView View(string source)
	{
		var view = new MarkdownView { FontSize = 14 };
		view.Source = source;
		Pump(view);
		return view;
	}

	[Fact]
	public void EmptyDocumentHasNoBlocksAndZeroHeight()
	{
		var view = View(string.Empty);
		Assert.Equal(0, view.BlockCount);
		Assert.Equal(0, view.TotalHeight);
		Assert.False(view.HasSelection);
		view.SelectAll();
		Assert.Equal(string.Empty, view.SelectedText);
	}

	[Fact]
	public void CarriageReturnsAndTrailingNewlinesKeepTheSameBlocks()
	{
		var lf = View("first\r\n\r\nsecond\n");
		var crlf = View("first\n\nsecond\n\n");
		Assert.Equal(2, lf.BlockCount);
		Assert.Equal("first\nsecond", lf.Text());
		Assert.Equal("first\nsecond", crlf.Text());
	}

	[Fact]
	public void HeadingsAreBoldAndScaledWithoutAffectingBodyText()
	{
		var view = View("# Title\n\nbody");
		var heading = Assert.IsType<MarkdownParagraphView>(view.GetBlockElement(0));
		var body = Assert.IsType<MarkdownParagraphView>(view.GetBlockElement(1));
		Assert.Equal(FontWeight.Bold, heading.FontWeight);
		Assert.Equal(FontWeight.Normal, body.FontWeight);
		Assert.True(view.GetBlockHeight(0) > view.GetBlockHeight(1),
			$"heading={view.GetBlockHeight(0)} body={view.GetBlockHeight(1)}");
	}

	[Fact]
	public void TableHeaderIsBoldAndCellsKeepTheirAlignment()
	{
		var view = View("| left | right |\n| :--- | ---: |\n| a | b |");
		var paragraphs = VisualTree.Find<MarkdownParagraphView>(view).ToArray();
		Assert.Equal(4, paragraphs.Length);
		Assert.Equal(FontWeight.Bold, paragraphs[0].FontWeight);
		Assert.Equal(FontWeight.Bold, paragraphs[1].FontWeight);
		Assert.Equal(FontWeight.Normal, paragraphs[2].FontWeight);
		Assert.Equal(TextAlignment.Left, paragraphs[2].Block.Alignment);
		Assert.Equal(TextAlignment.Right, paragraphs[3].Block.Alignment);
	}

	[Fact]
	public void NestedListIndentsItsContentAndKeepsBullets()
	{
		var view = View("- outer\n  - inner\n  - second");
		var list = Assert.IsType<MarkdownListPanel>(view.GetBlockElement(0));
		Assert.Equal("•", Assert.IsType<TextBlock>(list.MarkerAt(0)).Text);
		var inner = VisualTree.Find<MarkdownListPanel>(view).Single(panel => !ReferenceEquals(panel, list));
		Assert.NotSame(list, inner);
		Assert.True(inner.Bounds.X > list.Bounds.X, $"inner={inner.Bounds.X} outer={list.Bounds.X}");
		Assert.Equal("•", Assert.IsType<TextBlock>(inner.MarkerAt(0)).Text);

		view.SelectAll();
		Assert.Equal("•\touter\n•\tinner\n•\tsecond", view.SelectedText);
	}

	[Fact]
	public void TaskStateChangeUpdatesTheCheckboxInPlace()
	{
		var view = View("- [ ] pending\n- [x] done");
		var list = Assert.IsType<MarkdownListPanel>(view.GetBlockElement(0));
		Assert.False(Assert.IsType<CheckBox>(list.MarkerAt(0)).IsChecked);

		view.Source = "- [x] pending\n- [x] done";
		Pump(view);

		var updated = Assert.IsType<MarkdownListPanel>(view.GetBlockElement(0));
		Assert.Same(list, updated);
		Assert.True(Assert.IsType<CheckBox>(updated.MarkerAt(0)).IsChecked);
	}

	[Fact]
	public void TildeFenceAndInfoStringLanguageAreRecognized()
	{
		var view = View("~~~typescript title=app.ts\nconst x = 1;\n~~~");
		var code = Assert.IsType<MarkdownCodeBlockView>(view.GetBlockElement(0));
		Assert.Equal("const x = 1;", code.TextView.Text);
		Assert.Contains("typescript", VisualTree.Find<TextBlock>(view).Select(block => block.Text));
	}

	[Fact]
	public void StreamingAnUnclosedFenceKeepsTheSameCodeView()
	{
		var view = View("```csharp\nvar a = 1;\n");
		var code = Assert.IsType<MarkdownCodeBlockView>(view.GetBlockElement(0));
		for (var index = 0; index < 20; index++)
		{
			view.Source += $"var next{index} = {index};\n";
			Pump(view);
			Assert.Same(code, view.GetBlockElement(0));
		}
		Assert.Contains("var next19", code.TextView.Text);
	}

	[Fact]
	public void LinksInsideListItemsAndTableCellsOpen()
	{
		var opened = new List<string>();
		var view = View("- [file](src/a.cs)\n\n| [web](https://example.com) |\n| --- |");
		view.LinkRequested += args => opened.Add(args.Url);
		var links = VisualTree.Find<MarkdownParagraphView>(view).ToArray();
		Assert.All(links, paragraph => Assert.True(paragraph.LinkCount > 0));
		view.RaiseLinkForTest("src/a.cs");
		view.RaiseLinkForTest("https://example.com");
		Assert.Equal(new[] { "src/a.cs", "https://example.com" }, opened);
	}

	[Fact]
	public void SelectionAcrossAListAndFollowingParagraphCopiesMarkersOnce()
	{
		var view = View("- one\n- two\n\nafter");
		view.SelectAll();
		Assert.Equal("•\tone\n•\ttwo\nafter", view.SelectedText);
	}

	[Fact]
	public void EmojiAndCjkSurviveStreaming()
	{
		var view = View("emoji 😀 and 中文");
		view.Source += " 继续增长";
		Pump(view);
		view.SelectAll();
		Assert.Equal("emoji 😀 and 中文 继续增长", view.SelectedText);
	}

	[Fact]
	public void FinalizeKeepsTheSameElementsAndFullText()
	{
		var view = View("paragraph **bold**");
		var block = view.GetBlockElement(0);
		view.IsStreaming = false;
		Pump(view);
		Assert.Same(block, view.GetBlockElement(0));
		Assert.Equal("paragraph bold", Assert.IsType<MarkdownParagraphView>(block).Text);
	}

	[Fact]
	public void RemovingBlocksReleasesTheirElements()
	{
		var view = View("one\n\ntwo\n\nthree");
		var second = view.GetBlockElement(1);
		view.Source = "one";
		Pump(view);
		Assert.Equal(1, view.BlockCount);
		Assert.NotSame(second, view.GetBlockElement(0));
		Assert.Equal("one", view.Text());
	}

	[Fact]
	public void VeryLongSingleLineCodeBlockScrollsInsteadOfWrapping()
	{
		var view = View("```\n" + new string('x', 4000) + "\n```");
		var code = Assert.IsType<MarkdownCodeBlockView>(view.GetBlockElement(0));
		Assert.False(code.TextView.Wrap);
		Assert.True(view.TotalHeight < 200, $"height={view.TotalHeight}");
	}

	[Fact]
	public void RepeatedStreamingKeepsBlockCountAndTextConsistent()
	{
		var view = new MarkdownView { FontSize = 14 };
		var text = string.Empty;
		for (var index = 0; index < 60; index++)
		{
			text += index % 7 == 0 ? "\n\nparagraph " + index : " word" + index;
			view.Source = text;
			Pump(view);
		}
		view.SelectAll();
		Assert.DoesNotContain("", view.SelectedText.Split('\n').Where(line => line.Length == 0 && view.SelectedText.Length > 0));
		Assert.Contains("paragraph 56", view.SelectedText, StringComparison.Ordinal);
	}

	[Fact]
	public void EditingAnEarlierListParagraphKeepsElements()
	{
		var view = View("- one\n- two");
		var list = view.GetBlockElement(0);
		var one = VisualTree.Find<MarkdownParagraphView>(view).Single(paragraph => paragraph.Text == "one");

		view.Source = "- ONE\n- two";
		Pump(view);

		Assert.Same(list, view.GetBlockElement(0));
		Assert.Same(one, VisualTree.Find<MarkdownParagraphView>(view).Single(paragraph => paragraph.Text == "ONE"));
	}

	[Fact]
	public void EditingAnEarlierQuoteParagraphKeepsElements()
	{
		var view = View("> first\n>\n> second");
		var quote = view.GetBlockElement(0);
		var first = VisualTree.Find<MarkdownParagraphView>(view).Single(paragraph => paragraph.Text == "first");

		view.Source = "> FIRST\n>\n> second";
		Pump(view);

		Assert.Same(quote, view.GetBlockElement(0));
		Assert.Same(first, VisualTree.Find<MarkdownParagraphView>(view).Single(paragraph => paragraph.Text == "FIRST"));
	}

	[Fact]
	public void EditingAnEarlierTableCellKeepsElements()
	{
		var view = View("| a | b |\n| --- | --- |\n| c | d |");
		var table = view.GetBlockElement(0);
		var c = VisualTree.Find<MarkdownParagraphView>(view).Single(paragraph => paragraph.Text == "c");

		view.Source = "| a | b |\n| --- | --- |\n| C | d |";
		Pump(view);

		Assert.Same(table, view.GetBlockElement(0));
		Assert.Same(c, VisualTree.Find<MarkdownParagraphView>(view).Single(paragraph => paragraph.Text == "C"));
	}


	[Fact]
	public void SelectionSurvivesATailRewrite()
	{
		var view = View("before [link](https://example.com");
		view.SelectAll();

		view.Source = "before [link](https://example.com)";
		Pump(view);

		Assert.True(view.HasSelection);
		Assert.Equal("before link", view.SelectedText);
	}

	[Fact]
	public void SelectionSurvivesATaskToggle()
	{
		var view = View("- [ ] pending\n- [x] done");
		view.SelectAll();

		view.Source = "- [x] pending\n- [x] done";
		Pump(view);

		Assert.True(view.HasSelection);
		Assert.Contains("pending", view.SelectedText, StringComparison.Ordinal);
	}




	[Fact]
	public void RewritingCodeKeepsTheSameCodeView()
	{
		var view = View("```csharp\nvar a = 1;\n```");
		var code = Assert.IsType<MarkdownCodeBlockView>(view.GetBlockElement(0));

		view.Source = "```csharp\nvar b = 2;\n```";
		Pump(view);

		Assert.Same(code, view.GetBlockElement(0));
		Assert.Contains("var b = 2", code.TextView.Text);
		Assert.DoesNotContain("var a = 1", code.TextView.Text);
	}


	[Fact]
	public void HardLineBreaksKeepChunkedLayoutParity()
	{
		var source = "line one  \nline two  \nline three";
		var view = View(source);
		var full = View(source);
		Assert.InRange(view.TotalHeight, full.TotalHeight - 1, full.TotalHeight + 1);
		view.SelectAll();
		Assert.Equal("line one\nline two\nline three", view.SelectedText);
	}


	[Fact]
	public void FontChangeWithSourceUpdateRebuildsCodeView()
	{
		var view = new MarkdownView { FontSize = 14 };
		view.Source = "```\ncode\n```";
		Pump(view);
		var old = Assert.IsType<MarkdownCodeBlockView>(view.GetBlockElement(0));

		view.FontSize = 20;
		view.Source = "```\ncode\nextra\n```";
		Pump(view);

		var updated = Assert.IsType<MarkdownCodeBlockView>(view.GetBlockElement(0));
		Assert.NotSame(old, updated);
		Assert.Contains("extra", updated.TextView.Text);
		Assert.True(updated.TextView.FontSize > old.TextView.FontSize);
	}


	[Fact]
	public void ReusedLinkUpdatesItsSourceOffsets()
	{
		var view = new MarkdownView { FontSize = 14 };
		view.Source = "intro\n\n[link](https://example.com)\n\nlast";
		Pump(view);
		var paragraph = VisualTree.Find<MarkdownParagraphView>(view).Single(candidate => candidate.Text == "link");

		view.Source = "prepended\n\nintro\n\n[link](https://example.com)\n\nlast";
		Pump(view);

		var reused = VisualTree.Find<MarkdownParagraphView>(view).Single(candidate => candidate.Text == "link");
		Assert.Same(paragraph, reused);
		var run = reused.Runs.First(candidate => candidate.Url is not null);
		Assert.Equal("[link](https://example.com)", view.Source.Substring(run.SourceStart, run.SourceLength));
	}

}
