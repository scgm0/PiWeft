using PiWeft.Cli;
using Xunit;

namespace PiWeft.Cli.Tests;

/// 导出 HTML 的 ANSI 转换：pi 1.5.2+ 把终端颜色与 OSC 8 超链接转成内联样式，
/// 其余转义序列丢弃；否则工具输出在导出文件里会露出 `[38;2;…m`。
public sealed class AnsiHtmlTests
{
	[Fact]
	public void SgrColorsAndStylesBecomeInlineCss()
	{
		var html = AnsiHtml.ToHtml("\u001b[31mred\u001b[0m plain");

		Assert.Contains("<span style=\"color:#800000\">red</span>", html);
		Assert.EndsWith(" plain", html);
	}

	[Fact]
	public void TrueColorAnd256ColorsAreSupported()
	{
		Assert.Contains("color:rgb(1,2,3)", AnsiHtml.ToHtml("\u001b[38;2;1;2;3mX\u001b[0m"));
		Assert.Contains("background-color:rgb(4,5,6)", AnsiHtml.ToHtml("\u001b[48;2;4;5;6mX\u001b[0m"));
		Assert.Contains("color:#878700", AnsiHtml.ToHtml("\u001b[38;5;100mX\u001b[0m"));
		Assert.Contains("color:#080808", AnsiHtml.ToHtml("\u001b[38;5;232mX\u001b[0m"));
	}

	[Fact]
	public void Osc8LinksBecomeAnchorsAndOnlySafeSchemesPass()
	{
		var link = AnsiHtml.ToHtml("\u001b]8;;https://example.com\u0007click\u001b]8;;\u0007");
		Assert.Equal("<a href=\"https://example.com\">click</a>", link);

		var mail = AnsiHtml.ToHtml("\u001b]8;;mailto:a@b.c\u001b\\mail\u001b]8;;\u001b\\");
		Assert.Contains("<a href=\"mailto:a@b.c\">mail</a>", mail);

		// javascript: 不放行，但链接文字保留。
		var unsafeLink = AnsiHtml.ToHtml("\u001b]8;;javascript:alert(1)\u0007text\u001b]8;;\u0007");
		Assert.DoesNotContain("<a", unsafeLink);
		Assert.Equal("text", unsafeLink);
	}

	[Fact]
	public void OtherSequencesAndStrayEscapesAreDropped()
	{
		Assert.Equal("abc", AnsiHtml.ToHtml("a\u001b[2Kb\u001b[?25lc"));
		Assert.Equal("plain", AnsiHtml.ToHtml("\u001b]0;title\u0007plain"));
		// 孤立 ESC（没有后续序列）也去掉，不留在导出文件里。
		Assert.Equal("xy", AnsiHtml.StripEscapes("x\u001b[31my"));
		Assert.Equal("xy", AnsiHtml.StripEscapes("x\u001by"));
	}

	[Fact]
	public void HtmlIsEscapedAroundSequences()
	{
		var html = AnsiHtml.ToHtml("<b>\u001b[1mbold\u001b[0m & </b>");

		Assert.StartsWith("&lt;b&gt;<span style=\"font-weight:bold\">bold</span>", html);
		Assert.Contains("&amp; &lt;/b&gt;", html);
	}
}
