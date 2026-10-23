using PiWeft.Markdown;
using Xunit;
using Xunit.Abstractions;

namespace PiWeft.Desktop.Tests;

/// <summary>
/// Differential test: parsing every streaming prefix incrementally must produce the same block
/// model and selectable text as parsing that prefix from scratch.
/// </summary>
public sealed class MarkdownIncrementalParserTests(ITestOutputHelper output)
{
	private static readonly string[] Documents =
	[
		"# Title\n\nintro **bold** and `code`\n\n- one\n- two\n  - nested\n  - nested2\n\n| a | b |\n| --- | --- |\n| c | d |\n\n> quote\n>\n> more\n\n```csharp\nvar a = 1;\nvar b = 2;\n```\n",
		"setext\n=====\n\nparagraph\n\n1. ordered\n2. list\n\n* star\n* bullets\n",
		"before [link](https://example.com) after\n\nnext paragraph\n\n## Another heading\n\nlast\n",
		"~~~\nlong fence\nwith **markdown** inside\n~~~\n\nafter fence\n",
		"- [ ] pending\n- [x] done\n- [ ] more\n",
		"| left | center | right |\n| :--- | :---: | ---: |\n| a | b | c |\n| d | e | f |\n",
		"> first\n>\n> second\n>\n> - nested quote list\n>   - item\n",
		"# Same\n\n# Same\n\ntext\n",
		"first\r\n\r\nsecond\r\n\r\n- item\r\n- other\r\n",
		"中文 😀 emoji\n\n段落 😀 继续\n\n- 项目 😀\n",
		"<div>\nraw <b>html</b>\n</div>\n\ntext with <span>inline</span> html\n",
	];

	[Theory]
	[MemberData(nameof(DocumentIndices))]
	public void EveryStreamingPrefixMatchesAFullParse(int docIndex)
	{
		string source = Documents[docIndex];
		MarkdownDocument? previous = null;
		for (int length = 1; length <= source.Length; length += 5)
		{
			string prefix = source[..length];
			previous = MarkdownParser.Parse(prefix, options: null, previous);
			MarkdownDocument full = MarkdownParser.Parse(prefix);

			Assert.Equal(
				full.Blocks.Select(static block => block.Hash),
				previous.Blocks.Select(static block => block.Hash));
			Assert.Equal(full.ToPlainText(), previous.ToPlainText());
			Assert.Equal(
				full.TextUnits.Select(static unit => unit.Text),
				previous.TextUnits.Select(static unit => unit.Text));
		}
		output.WriteLine($"doc {docIndex}: {source.Length} chars, {source.Length / 5} prefixes");
	}

	public static TheoryData<int> DocumentIndices()
	{
		var data = new TheoryData<int>();
		for (int index = 0; index < Documents.Length; index++)
		{
			data.Add(index);
		}
		return data;
	}

	[Fact]
	public void LinkReferenceDefinitionsDisableIncrementalParsingButStayCorrect()
	{
		string source = "[ref][id]\n\n[id]: https://example.com\n";
		MarkdownDocument first = MarkdownParser.Parse(source);
		MarkdownDocument incremental = MarkdownParser.Parse(source, null, first);
		Assert.Equal(
			first.Blocks.Select(static block => block.Hash),
			incremental.Blocks.Select(static block => block.Hash));
		Assert.Contains(incremental.Blocks, block => block.Runs.Any(run => run.Url == "https://example.com"));
	}

	[Fact]
	public void IncrementalTailSourceOffsetsPointIntoTheFullSource()
	{
		var previous = MarkdownParser.Parse("# head\n\nintro\n\n");
		var source = "# head\n\nintro\n\nsecond [link](https://example.com)\n";
		var incremental = MarkdownParser.Parse(source, null, previous);

		var block = incremental.Blocks[^1];
		Assert.Equal("second link", block.Text);
		Assert.Equal(source.IndexOf("second", StringComparison.Ordinal), block.SourceStart);
		Assert.StartsWith("second", source[block.SourceStart..], StringComparison.Ordinal);
		var run = block.Runs.First(static run => run.Url is not null);
		Assert.Equal("[link](https://example.com)", source.Substring(run.SourceStart, run.SourceLength));
	}

}
