using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Aprillz.MewUI.Text;
using Aprillz.MewUI.Text.Editing;
using PiWeft.Desktop;
using Xunit;

namespace PiWeft.Desktop.Tests;

public class CodeHighlightingTests
{
	private static IReadOnlyList<TextPaintSpan> Classify(string? language, string line)
	{
		var classifier = new KeywordClassifier(language);
		var output = new List<TextPaintSpan>();
		var text = line.AsMemory();

		classifier.Classify(
			new TextClassificationContext(new LogicalTextLine(0, 0, text.Length, text.Length), text, IdentityTextOffsetMap.Instance),
			output);

		return output;
	}

	private static bool Has(IReadOnlyList<TextPaintSpan> spans, int start, int length, Color expected) =>
		spans.Any(span => span.Range.Start == start && span.Range.Length == length && span.Foreground == expected);

	[Fact]
	public void CSharpLineHighlightsKeywordStringAndComment()
	{
		//           0123456789...
		var line = "var x = \"hi\"; // note";
		var spans = Classify("csharp", line);

		Assert.True(Has(spans, 0, 3, SyntaxPalette.Keyword), "var 应作为关键字着色");
		Assert.True(Has(spans, 8, 4, SyntaxPalette.String), "\"hi\" 应作为字符串着色");
		Assert.True(Has(spans, 14, 7, SyntaxPalette.Comment), "行尾注释应整体着色");
	}

	[Fact]
	public void EscapedQuoteDoesNotEndTheString()
	{
		var line = "print(\"a \\\" b\")";
		var spans = Classify("python", line);

		// 字符串从第 6 个字符一直延伸到右括号之前，中途的转义引号不算结束。
		Assert.True(Has(spans, 6, line.Length - 7, SyntaxPalette.String), $"字符串区间错误：{line}");
		Assert.True(Has(spans, 0, 5, SyntaxPalette.Keyword), "print 应作为关键字着色");
	}

	[Fact]
	public void HashIsACommentOnlyInShellFamilyLanguages()
	{
		Assert.True(Has(Classify("python", "x = 1  # note"), 7, 6, SyntaxPalette.Comment));
		Assert.True(Has(Classify("bash", "# comment"), 0, 9, SyntaxPalette.Comment));

		// C# 的 #region 不是注释，不应被着色。
		Assert.DoesNotContain(Classify("csharp", "#region Demo"), span => span.Foreground == SyntaxPalette.Comment);
	}

	[Fact]
	public void NumbersAreHighlightedOnlyAtIdentifierBoundaries()
	{
		var spans = Classify("json", "{\"count\": 42, \"items2\": 7}");

		Assert.True(Has(spans, 10, 2, SyntaxPalette.Number), "42 应作为数字着色");
		Assert.True(Has(spans, 24, 1, SyntaxPalette.Number), "7 应作为数字着色");

		// items2 里的 2 位于字符串内部，不应单独成为一个数字区间。
		Assert.DoesNotContain(spans, span => span.Range.Start == 20 && span.Foreground == SyntaxPalette.Number);
	}

	[Fact]
	public void LanguageMarkersFromMarkdownAreNormalized()
	{
		// Markdown 里常见的写法都能落到同一套关键字表上。
		Assert.True(Has(Classify("csharp title=Foo.cs", "return 1;"), 0, 6, SyntaxPalette.Keyword));
		Assert.True(Has(Classify(".js", "const a = 1"), 0, 5, SyntaxPalette.Keyword));
		Assert.True(Has(Classify("CS", "class A {}"), 0, 5, SyntaxPalette.Keyword));
		Assert.True(Has(Classify("{.js}", "const a = 1"), 0, 5, SyntaxPalette.Keyword));
		Assert.True(Has(Classify("sql", "SELECT name FROM items"), 0, 6, SyntaxPalette.Keyword));
		Assert.True(Has(Classify("csharp", "field = value;"), 0, 5, SyntaxPalette.Keyword));
	}

	[Fact]
	public void UnknownLanguageOnlyGetsLexicalHighlighting()
	{
		var spans = Classify("brainfuck", "hello \"world\" 123");

		Assert.True(Has(spans, 6, 7, SyntaxPalette.String));
		Assert.True(Has(spans, 14, 3, SyntaxPalette.Number));
		Assert.DoesNotContain(spans, span => span.Foreground == SyntaxPalette.Keyword);
	}

	[Fact]
	public void SliceFromTheMiddleOfALineKeepsTheWholeLineHighlights()
	{
		var line = "const value = \"a long string with 123 inside\"; // trailing note";
		var document = new EditableTextDocument(line);

		// 从字符串中间切一段：片段里看不到开引号，逐段扫描会把结尾的引号当成开头。
		const int sliceStart = 20;
		const int sliceLength = 25;
		var slice = line.Substring(sliceStart, sliceLength);
		var spans = new List<TextPaintSpan>();
		new KeywordClassifier("typescript", document).Classify(
			new TextClassificationContext(new LogicalTextLine(0, sliceStart, sliceLength, sliceLength),
				slice.AsMemory(), IdentityTextOffsetMap.Instance), spans);

		var whole = Classify("typescript", line);
		var expected = whole
			.Select(span => (
				Start: Math.Max(span.Range.Start, sliceStart),
				End: Math.Min(span.Range.Start + span.Range.Length, sliceStart + sliceLength),
				span.Foreground))
			.Where(span => span.End > span.Start)
			.Select(span => (Range: new TextRange(span.Start - sliceStart, span.End - span.Start), span.Foreground));

		Assert.Equal(expected, spans.Select(span => (span.Range, span.Foreground)));
	}

	[Theory]
	[InlineData("csharp", "var x = \"hi\"; // note")]
	[InlineData("json", "{\"count\": 42, \"items2\": 7}")]
	[InlineData("python", "x = 1  # note")]
	public void WholeLineClassificationIsUnchangedWithADocument(string language, string line)
	{
		// 没有切片时，带文档的分类器必须与纯文本路径逐个区间一致。
		var document = new EditableTextDocument(line);
		var withDocument = ClassifySlice(new KeywordClassifier(language, document), document, 0, line.Length);
		var withoutDocument = Classify(language, line);

		Assert.Equal(withoutDocument.Select(span => (span.Range, span.Foreground)),
			withDocument.Select(span => (span.Range, span.Foreground)));
	}

	[Fact]
	public void DocumentEditsInvalidateTheCachedLine()
	{
		var document = new EditableTextDocument("var x = 1;");
		var classifier = new KeywordClassifier("csharp", document);
		Assert.Contains(ClassifySlice(classifier, document, 0, 10), span => span.Foreground == SyntaxPalette.Keyword);

		// 把关键字改成普通标识符：文档版本变了，不能还拿旧的整行缓存。
		document.Replace(0, 3, "xyz");
		Assert.DoesNotContain(ClassifySlice(classifier, document, 0, 10), span => span.Foreground == SyntaxPalette.Keyword);
	}

	private static List<TextPaintSpan> ClassifySlice(KeywordClassifier classifier, IReadOnlyTextDocument document,
		int start, int length)
	{
		var output = new List<TextPaintSpan>();
		classifier.Classify(new TextClassificationContext(
			new LogicalTextLine(0, start, length, length), document.GetText(start, length).AsMemory(),
			IdentityTextOffsetMap.Instance), output);
		return output;
	}
}

[Collection("mewui-controls")]
public sealed class CodeHighlightingInteractionTests
{
	[Fact]
	public void LongCodeLineKeepsItsCommentColorAfterHorizontalScroll()
	{
		// 整行都是注释，切片从行中间开始时看不到 //。
		var code = "// " + new string('x', 1200);
		var text = MessageText.Create(code, mono: true, wrap: false, fontSize: AppTheme.FontSizeMicro);
		using var focus = new TextFocusScrollGuard(new ScrollViewer());
		AssistantMessageView.ConfigureCodeText(text, "csharp", focus, muted: false);
		ConversationPerformanceTests.Layout(text, 400, 120);
		Assert.Contains(SyntaxPalette.Comment, PaintedColors(text));

		var viewport = text.TextViewportBounds;
		text.MakeVisible(new Rect(2000, 0, viewport.Width, viewport.Height));
		ConversationPerformanceTests.Layout(text, 400, 120);
		Assert.True(text.HorizontalOffset > 0);
		Assert.Contains(SyntaxPalette.Comment, PaintedColors(text));
		UiLifetime.Release(text);
	}

	private static Color[] PaintedColors(MultiLineTextBox text) =>
		text.VisibleTextLines.SelectMany(line => line.PaintSpans)
			.Select(span => span.Foreground)
			.OfType<Color>()
			.Distinct()
			.ToArray();
}
