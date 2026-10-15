using Aprillz.MewUI;
using Aprillz.MewUI.Text;
using PiWeft.Desktop;
using Xunit;

namespace PiWeft.Desktop.Tests;

/// <summary>代码块着色：逐行扫描关键字、字符串、注释与数字。分类器是纯函数，可以直接断言区间。</summary>
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
}
