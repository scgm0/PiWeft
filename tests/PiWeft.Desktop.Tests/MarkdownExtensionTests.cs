using Markdig;
using Markdig.Extensions.Footnotes;
using Markdig.Extensions.Mathematics;
using PiWeft.Markdown;
using Xunit;
using Xunit.Abstractions;

namespace PiWeft.Desktop.Tests;

/// <summary>
/// Extensibility: the inline HTML formatting whitelist, ConfigurePipeline (mathematics,
/// footnotes), custom block renderers keyed by the Markdig node type name, and the honest
/// source-span fallback for unknown inline nodes.
/// </summary>
public sealed class MarkdownExtensionTests(ITestOutputHelper output)
{
	private static readonly MarkdownOptions Mathematics = new()
	{
		UseHtmlFormatting = false,
		ConfigurePipeline = static builder => builder.UseMathematics(),
	};

	private static MarkdownRunStyle StyleOf(MarkdownBlock block, string text)
		=> block.Runs.First(run => run.Text == text).Style;

	[Fact]
	public void InlineHtmlWhitelistMapsToRunStyles()
	{
		var block = MarkdownParser.Parse("a <b>x</b> and <i>y</i>", null, null, streaming: false).Blocks[0];
		Assert.Equal("a x and y", block.Text);
		Assert.Equal(MarkdownRunStyle.Bold, StyleOf(block, "x") & MarkdownRunStyle.Bold);
		Assert.Equal(MarkdownRunStyle.Italic, StyleOf(block, "y") & MarkdownRunStyle.Italic);
		Assert.DoesNotContain(block.Runs, static run => run.Text.Contains('<'));

		var nested = MarkdownParser.Parse("<b><i>both</i></b>").Blocks[0];
		var nestedStyle = StyleOf(nested, "both");
		Assert.Equal(MarkdownRunStyle.Bold | MarkdownRunStyle.Italic, nestedStyle & (MarkdownRunStyle.Bold | MarkdownRunStyle.Italic));

		var mixed = MarkdownParser.Parse("<u>u</u><s>s</s><del>d</del><mark>m</mark><ins>i</ins><code>c</code><sub>lo</sub><sup>hi</sup>").Blocks[0];
		Assert.NotEqual(MarkdownRunStyle.None, StyleOf(mixed, "u") & MarkdownRunStyle.Underline);
		Assert.NotEqual(MarkdownRunStyle.None, StyleOf(mixed, "s") & MarkdownRunStyle.Strike);
		Assert.NotEqual(MarkdownRunStyle.None, StyleOf(mixed, "d") & MarkdownRunStyle.Strike);
		Assert.NotEqual(MarkdownRunStyle.None, StyleOf(mixed, "m") & MarkdownRunStyle.Marked);
		Assert.NotEqual(MarkdownRunStyle.None, StyleOf(mixed, "i") & MarkdownRunStyle.Inserted);
		Assert.NotEqual(MarkdownRunStyle.None, StyleOf(mixed, "c") & MarkdownRunStyle.Code);
		Assert.NotEqual(MarkdownRunStyle.None, StyleOf(mixed, "lo") & MarkdownRunStyle.Script);
		Assert.NotEqual(MarkdownRunStyle.None, StyleOf(mixed, "hi") & MarkdownRunStyle.Script);
	}

	[Fact]
	public void LineBreakTagBecomesAHardBreak()
	{
		var block = MarkdownParser.Parse("one<br/>two<br>three").Blocks[0];
		Assert.Equal("one\ntwo\nthree", block.Text);
	}

	[Fact]
	public void CommentsAreHiddenAndTransparentTagsPassThrough()
	{
		var block = MarkdownParser.Parse("a <!-- secret --> b").Blocks[0];
		Assert.Equal("a  b", block.Text);
		Assert.DoesNotContain(block.Runs, static run => run.Text.Contains("secret", StringComparison.Ordinal));

		var span = MarkdownParser.Parse("<span style=\"color:red\">kept</span> <font size=5>too</font>").Blocks[0];
		Assert.Equal("kept too", span.Text);
		Assert.All(span.Runs, static run => Assert.Equal(MarkdownRunStyle.None, run.Style));

		// Attributes are never interpreted; only the tag itself is consumed.
		var bold = MarkdownParser.Parse("<b class=\"q\">bold</b>").Blocks[0];
		Assert.Equal("bold", bold.Text);
		Assert.NotEqual(MarkdownRunStyle.None, StyleOf(bold, "bold") & MarkdownRunStyle.Bold);
	}

	[Fact]
	public void UnknownTagsAndStructuralHtmlStayLiteral()
	{
		var block = MarkdownParser.Parse("x <div>y</div> z").Blocks[0];
		Assert.Equal("x <div>y</div> z", block.Text);

		var htmlBlock = MarkdownParser.Parse("<div>\nraw <b>html</b>\n</div>").Blocks[0];
		Assert.Contains("raw <b>html</b>", htmlBlock.Text, StringComparison.Ordinal);
	}

	[Fact]
	public void UnclosedFormattingTagRunsToTheEndOfTheParagraph()
	{
		var block = MarkdownParser.Parse("plain <b>rest").Blocks[0];
		Assert.Equal("plain rest", block.Text);
		Assert.NotEqual(MarkdownRunStyle.None, StyleOf(block, "rest") & MarkdownRunStyle.Bold);
	}

	[Fact]
	public void HtmlFormattingCanBeDisabled()
	{
		var options = new MarkdownOptions { UseHtmlFormatting = false };
		var block = MarkdownParser.Parse("a <b>x</b> b", options).Blocks[0];
		Assert.Equal("a <b>x</b> b", block.Text);
	}

	[Fact]
	public void ConfigurePipelineProducesCustomBlocksAndHonestInlineText()
	{
		var document = MarkdownParser.Parse("$$\nx^2\n$$\n\ntext $x+1$ end", Mathematics);

		var math = document.Blocks[0];
		Assert.Equal(MarkdownBlockKind.Custom, math.Kind);
		Assert.Equal("MathBlock", math.CustomKind);
		Assert.Contains("x^2", math.Text, StringComparison.Ordinal);

		// The inline fallback uses the source span: MathInline.ToString() is the type name.
		var inline = document.Blocks[1].Runs.Single(static run => run.Text.Contains("x+1", StringComparison.Ordinal));
		Assert.Equal("$x+1$", inline.Text);
	}

	[Fact]
	public void WithoutTheExtensionMathStaysLiteralText()
	{
		var document = MarkdownParser.Parse("$$\nx^2\n$$");
		Assert.All(document.Blocks, static block => Assert.NotEqual(MarkdownBlockKind.Custom, block.Kind));
		Assert.Contains("$$", document.Blocks[0].Text, StringComparison.Ordinal);
	}

	[Fact]
	public void FootnoteDefinitionsAppearOnceViaTheFootnoteGroup()
	{
		var options = new MarkdownOptions { ConfigurePipeline = static builder => builder.UseFootnotes() };
		var document = MarkdownParser.Parse("note[^1] here\n\n[^1]: the note\n", options);

		// The LinkReferenceDefinitionGroup copy is skipped; the footnote group keeps the content
		// nested one level down.
		Assert.Equal(2, document.Blocks.Count);
		string plain = document.ToPlainText();
		Assert.Equal(1, plain.Split("the note", StringSplitOptions.None).Length - 1);
		var reference = document.Blocks[0].Runs.FirstOrDefault(static run => run.Text.Contains("[^1]", StringComparison.Ordinal));
		Assert.Equal("[^1]", reference.Text);
	}

	[Theory]
	[InlineData("NOTE")]
	[InlineData("TIP")]
	[InlineData("IMPORTANT")]
	[InlineData("WARNING")]
	[InlineData("CAUTION")]
	public void GitHubAlertsStripTheMarkerAndTagTheQuote(string kind)
	{
		var document = MarkdownParser.Parse($"> [!{kind}]\n> content line\n\nafter");
		var quote = document.Blocks[0];
		Assert.Equal(MarkdownBlockKind.Quote, quote.Kind);
		Assert.Equal(kind, quote.Alert);
		Assert.Single(quote.Children);
		Assert.Equal("content line", quote.Children[0].Text);
		Assert.Equal("after", document.Blocks[1].Text);
	}

	[Fact]
	public void LowercaseAlertMarkersAreRecognized()
	{
		var document = MarkdownParser.Parse("> [!caution] be careful now");
		Assert.Equal("CAUTION", document.Blocks[0].Alert);
		Assert.Equal("be careful now", document.Blocks[0].Children[0].Text);
	}

	[Fact]
	public void PlainQuotesHaveNoAlert()
	{
		var document = MarkdownParser.Parse("> quoted text\n> more");
		Assert.Null(document.Blocks[0].Alert);
		Assert.Equal("quoted text more", document.Blocks[0].Children[0].Text);
	}

	[Fact]
	public void MultiParagraphAlertsKeepTheFollowingParagraphs()
	{
		var document = MarkdownParser.Parse("> [!WARNING]\n> first\n>\n> second");
		var quote = document.Blocks[0];
		Assert.Equal("WARNING", quote.Alert);
		Assert.Equal(2, quote.Children.Count);
		Assert.Equal("first", quote.Children[0].Text);
		Assert.Equal("second", quote.Children[1].Text);
	}

	[Fact]
	public void FrontMatterIsHiddenWhenTheYamlExtensionIsEnabled()
	{
		var options = new MarkdownOptions { ConfigurePipeline = static builder => builder.UseYamlFrontMatter() };
		var document = MarkdownParser.Parse("---\ntitle: x\n---\n\nbody text\n", options);
		Assert.Single(document.Blocks);
		Assert.Equal("body text", document.Blocks[0].Text);

		// Without the extension the dashes stay thematic breaks / setext syntax.
		var plain = MarkdownParser.Parse("---\ntitle: x\n---\n\nbody text\n");
		Assert.True(plain.Blocks.Count >= 2);
	}

	[Fact]
	public void EmojiShortcodesRenderTheCharacter()
	{
		var options = new MarkdownOptions { ConfigurePipeline = static builder => builder.UseEmojiAndSmiley() };
		var document = MarkdownParser.Parse("hello :smile: end", options);
		Assert.Equal("hello 😄 end", document.Blocks[0].Text);
	}

	[Fact]
	public void LinkSchemesGateLinksImagesAndAutolinks()
	{
		var options = new MarkdownOptions
		{
			LinkSchemes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "http", "https" },
		};
		var document = MarkdownParser.Parse(
			"[good](https://a.com) [bad](javascript:alert(1)) ![i](ftp://b.com/i.png) [rel](doc.md) [anchor](#x)", options);
		var runs = document.Blocks[0].Runs.Where(static run => run.Url is not null).ToList();
		Assert.Equal(["https://a.com", "doc.md", "#x"], runs.Select(static run => run.Url));
		// The disallowed image never reaches a resolver: its run carries no URL.
		Assert.DoesNotContain(document.Blocks[0].Runs, static run => run.Image && run.Url is not null);

		var autolink = MarkdownParser.Parse("<ftp://files.example.com/a.txt>", options);
		Assert.DoesNotContain(autolink.Blocks[0].Runs, static run => run.Url is not null);
		Assert.Equal("ftp://files.example.com/a.txt", autolink.Blocks[0].Text);

		// Unset options keep today's behavior: the host decides on activation.
		var unfiltered = MarkdownParser.Parse("[x](javascript:alert(1))");
		Assert.Equal("javascript:alert(1)", unfiltered.Blocks[0].Runs.First(static run => run.Url is not null).Url);
	}

	[Fact]
	public void CustomBlocksHashStablyAcrossRevisions()
	{
		string source = "$$\nx^2\n$$\n\ntail";
		var first = MarkdownParser.Parse(source, Mathematics);
		var second = MarkdownParser.Parse(source, Mathematics, first, streaming: false);
		Assert.Equal(
			first.Blocks.Select(static block => block.Hash),
			second.Blocks.Select(static block => block.Hash));

		// Appending after a custom block reuses the prefix hashes incrementally.
		var grown = MarkdownParser.Parse(source + "\n\nmore", Mathematics, first, streaming: false);
		Assert.Equal(first.Blocks[0].Hash, grown.Blocks[0].Hash);
		Assert.Equal(3, grown.Blocks.Count);
	}

	[Fact]
	public void StreamingDifferentialHoldsWithHtmlAndCustomBlocks()
	{
		string source = "a <b>bo</b>ld `x`\n\n$$\ny^2\n$$\n\n<i>it</i> [link](https://example.com)\n";
		MarkdownDocument? previous = null;
		for (int length = 1; length <= source.Length; length += 4)
		{
			string prefix = source[..length];
			previous = MarkdownParser.Parse(prefix, Mathematics, previous, streaming: true);
			// Streaming frames carry the tail repair, so the exact model is a full parse of the
			// repaired source (see MarkdownStreamRepairTests).
			string suffix = MarkdownStreamRepair.ComputeSuffix(prefix);
			MarkdownDocument expected = MarkdownParser.Parse(prefix + suffix, Mathematics);
			Assert.Equal(
				expected.Blocks.Select(static block => block.Hash),
				previous.Blocks.Select(static block => block.Hash));
		}
	}
}
