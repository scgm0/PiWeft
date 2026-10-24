using System.Text;
using PiWeft.Markdown;
using Xunit;
using Xunit.Abstractions;

namespace PiWeft.Desktop.Tests;

/// <summary>
/// Streaming tail repair: provisional closers for unfinished syntax, the streaming parse
/// differential against a full parse of the repaired source, finalize correction, and the
/// append-only builder API.
/// </summary>
public sealed class MarkdownStreamRepairTests(ITestOutputHelper output)
{
	[Theory]
	[InlineData("hello **wor", "**")]
	[InlineData("hello *wor", "*")]
	[InlineData("`code", "`")]
	[InlineData("``code", "``")]
	[InlineData("see [link](https://ex.com", ")")]
	[InlineData("see [link](https://ex.com/x?y=1&z=2", ")")]
	[InlineData("see [link](https://ex.com a", "")]
	[InlineData("a * b", "")]
	[InlineData("2*3*4", "")]
	[InlineData("word_with_underscores", "")]
	[InlineData("a \\* b", "")]
	[InlineData("~~strike", "~~")]
	[InlineData("==mark", "==")]
	[InlineData("++ins", "++")]
	[InlineData("***bi", "***")]
	[InlineData("**bold** done", "")]
	[InlineData("a `b` c", "")]
	[InlineData("text <!-- note", "")]
	[InlineData("text <!-- note -->", "")]
	[InlineData("text <!-- a ** b --> tail", "")]
	[InlineData("```csharp\ncode with ` and ** inside", "")]
	[InlineData("```\nstill open", "")]
	[InlineData("    indented * code", "")]
	[InlineData("para\n\n    indented * code", "")]
	[InlineData("use [ref] only", "")]
	[InlineData("- [ ] task", "")]
	[InlineData("[x](y(c", "))")]
	[InlineData("Title\n===", "")]
	[InlineData("**a\n\nb", "")]
	[InlineData("**a\n- item", "")]
	[InlineData("_em_", "")]
	public void SuffixClosesOpenConstructs(string tail, string expected)
	{
		Assert.Equal(expected, MarkdownStreamRepair.ComputeSuffix(tail));
	}

	private static readonly string[] StreamingDocuments =
	[
		"# Title\n\nintro **bold** and `code`\n\n- one\n- two\n\n> quote\n\n```csharp\nvar a = 1;\n```\n",
		"text with **bold, then *italic, then `code and [link](https://example.com/path more\n\nnext **para**\n",
		"para one\n\npara two ~~strike and ==mark and ++ins\n\n- item **one\n- item two\n",
		"a [bracket, an ![img](https://example.com/i.png, an <!-- comment\n\ntail **bold\n",
		"```python\ncode **not** repaired `x\n",
		"    indented code * stays\n\nnormal **text\n",
		"<!-- html comment\n\nspans blank **lines\n",
		"1. ordered **one\n2. ordered two\n\nsetext\n====\ntail `code\n",
		"[ref][id]\n\n[id]: https://example.com\n\nsee **ref now",
		"first\r\n\r\n**second\r\n\r\n- item `x\r\n",
	];

	public static TheoryData<int> StreamingIndices()
	{
		var data = new TheoryData<int>();
		for (int index = 0; index < StreamingDocuments.Length; index++)
		{
			data.Add(index);
		}
		return data;
	}

	/// <summary>
	/// For every streamed prefix: the streaming parse must equal a full parse of the repaired
	/// source, blocks before the last must equal the unrepaired parse, and the repair flag must
	/// track whether a suffix was applied.
	/// </summary>
	[Theory]
	[MemberData(nameof(StreamingIndices))]
	public void StreamingPrefixDifferentialRepairsOnlyTheLastBlock(int docIndex)
	{
		string source = StreamingDocuments[docIndex];
		MarkdownDocument? previous = null;
		int repairedFrames = 0;
		for (int length = 1; length <= source.Length; length += 3)
		{
			string prefix = source[..length];
			previous = MarkdownParser.Parse(prefix, null, previous, streaming: true);
			string suffix = MarkdownStreamRepair.ComputeSuffix(prefix);
			MarkdownDocument expected = MarkdownParser.Parse(prefix + suffix);
			MarkdownDocument fresh = MarkdownParser.Parse(prefix);

			Assert.Equal(
				expected.Blocks.Select(static block => block.Hash),
				previous.Blocks.Select(static block => block.Hash));
			Assert.Equal(fresh.Blocks.Count, previous.Blocks.Count);
			Assert.Equal(
				fresh.Blocks.Take(Math.Max(0, fresh.Blocks.Count - 1)).Select(static block => block.Hash),
				previous.Blocks.Take(Math.Max(0, previous.Blocks.Count - 1)).Select(static block => block.Hash));
			Assert.Equal(suffix.Length > 0, previous.TailRepaired);
			repairedFrames += suffix.Length > 0 ? 1 : 0;
		}
		output.WriteLine($"doc {docIndex}: {source.Length} chars, {repairedFrames} repaired frames");
	}

	[Fact]
	public void FinalizeReparseCorrectsTheRepairedTail()
	{
		string source = "hello **wor";
		MarkdownDocument streamed = MarkdownParser.Parse(source, null, null, streaming: true);
		Assert.True(streamed.TailRepaired);
		Assert.Contains(streamed.Blocks[^1].Runs,
			static run => (run.Style & MarkdownRunStyle.Bold) != 0 && run.Text == "wor");
		Assert.Equal("hello wor", streamed.Blocks[^1].Text);

		// Same source with streaming off: the repaired tail re-parses to the exact model.
		MarkdownDocument final = MarkdownParser.Parse(source, null, streamed, streaming: false);
		Assert.False(final.TailRepaired);
		MarkdownDocument fresh = MarkdownParser.Parse(source);
		Assert.Equal(fresh.Blocks.Select(static block => block.Hash), final.Blocks.Select(static block => block.Hash));
		Assert.Equal("hello **wor", final.Blocks[^1].Text);

		// A further finalize frame with the same source is a no-op.
		Assert.Same(final, MarkdownParser.Parse(source, null, final, streaming: false));
	}

	[Fact]
	public void StreamingContinuesAcrossRepairedFramesAndFinalizes()
	{
		string[] frames =
		[
			"hello **bo",
			"hello **bold",
			"hello **bold** and `co",
			"hello **bold** and `code` done",
		];
		MarkdownDocument? previous = null;
		foreach (string frame in frames)
		{
			previous = MarkdownParser.Parse(frame, null, previous, streaming: true);
		}
		string source = frames[^1];
		MarkdownDocument final = MarkdownParser.Parse(source, null, previous, streaming: false);
		Assert.Equal(
			MarkdownParser.Parse(source).Blocks.Select(static block => block.Hash),
			final.Blocks.Select(static block => block.Hash));
	}

	[Fact]
	public void BuilderAppendMatchesFullParseAndKeepsTheLazySource()
	{
		string source = "# Title\n\nfirst **para**\n\nsecond `tail` grows\n";
		var builder = new StringBuilder();
		MarkdownDocument? previous = null;
		for (int offset = 0; offset < source.Length; offset += 3)
		{
			builder.Append(source.AsSpan(offset, Math.Min(3, source.Length - offset)));
			previous = MarkdownParser.Parse(builder, null, previous, streaming: true);

			Assert.Same(builder, previous.SourceBuilder);
			Assert.Equal(builder.Length, previous.SourceLength);
			string text = builder.ToString();
			MarkdownDocument expected = MarkdownParser.Parse(text + MarkdownStreamRepair.ComputeSuffix(text));
			Assert.Equal(
				expected.Blocks.Select(static block => block.Hash),
				previous.Blocks.Select(static block => block.Hash));
		}

		// Reading Source materializes the accumulated builder exactly once per read.
		Assert.Equal(source, previous.Source);
		Assert.Equal(source, previous.Source);

		MarkdownDocument final = MarkdownParser.Parse(builder, null, previous, streaming: false);
		Assert.Equal(
			MarkdownParser.Parse(source).Blocks.Select(static block => block.Hash),
			final.Blocks.Select(static block => block.Hash));
	}

	[Fact]
	public void BuilderAppendWithoutRepairStillMatchesFullParse()
	{
		string source = "# Head\n\nintro\n\nsecond [link](https://example.com/x)\n";
		var builder = new StringBuilder();
		MarkdownDocument? previous = null;
		for (int offset = 0; offset < source.Length; offset += 7)
		{
			builder.Append(source.AsSpan(offset, Math.Min(7, source.Length - offset)));
			previous = MarkdownParser.Parse(builder, null, previous);
			MarkdownDocument expected = MarkdownParser.Parse(builder.ToString());
			Assert.Equal(
				expected.Blocks.Select(static block => block.Hash),
				previous.Blocks.Select(static block => block.Hash));
		}
		Assert.Equal(source, previous.Source);
	}
}
