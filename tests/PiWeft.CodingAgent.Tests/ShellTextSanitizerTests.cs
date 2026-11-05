using PiWeft.CodingAgent.Tools;
using Xunit;

namespace PiWeft.CodingAgent.Tests;

/// bash 输出不是纯文本：`cat` 二进制、进度条、彩色日志都会带 NUL/ANSI/CR。
/// 这些字符一旦进入会话，会话就可能再也发不出去，所以进入上下文前必须先清洗。
public sealed class ShellTextSanitizerTests
{
	[Fact]
	public void StripsSgrAndOscSequences()
	{
		var input = "\u001b[31mred\u001b[0m \u001b]8;;https://example.com\u0007link\u001b]8;;\u0007";

		Assert.Equal("red link", ShellTextSanitizer.Sanitize(input).Trim());
	}

	[Fact]
	public void RemovesNulAndControlCharactersButKeepsTabsAndNewlines()
	{
		var input = "\u0000\u0001\u0008\tkeep\r\n\u000b\u000c\u000e\u001a\u001f\uFFF9text";

		Assert.Equal("\tkeep\r\ntext", ShellTextSanitizer.Sanitize(input));
	}

	[Fact]
	public void PreservesCarriageReturnsLikePi()
	{
		Assert.Equal("one\r\ntwo\r\n", ShellTextSanitizer.Sanitize("one\r\ntwo\r\n"));
	}

	[Fact]
	public void PreservesUnicodeAndEmoji()
	{
		const string input = "路径 /tmp/文件 😀\n";

		Assert.Equal(input, ShellTextSanitizer.Sanitize(input));
	}

	[Fact]
	public void HoldsBackAnIncompleteAnsiSequence()
	{
		var (complete, pending) = ShellTextSanitizer.SplitIncompleteAnsiSuffix("hello\u001b[38;5;");

		Assert.Equal("hello", complete);
		Assert.Equal("\u001b[38;5;", pending);
	}

	[Fact]
	public void DoesNotHoldBackACompleteAnsiSequence()
	{
		var (complete, pending) = ShellTextSanitizer.SplitIncompleteAnsiSuffix("hello\u001b[31mred");

		Assert.Equal("hello\u001b[31mred", complete);
		Assert.Equal(string.Empty, pending);
	}

	[Fact]
	public void PlainTextIsReturnedUnchanged()
	{
		var (complete, pending) = ShellTextSanitizer.SplitIncompleteAnsiSuffix("plain text");

		Assert.Equal("plain text", complete);
		Assert.Equal(string.Empty, pending);
	}
}
