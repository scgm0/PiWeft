using PiWeft.Cli;
using Xunit;

namespace PiWeft.Cli.Tests;

/// 日志出口的脱敏与单行化：导出的隐私边界和 <c>/share --safe</c> 是同一套规则。
public sealed class LogRedactorTests
{
	private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
	private static readonly string AgentDir = Path.Combine(Home, ".pi", "agent");
	private static readonly string Cwd = Path.Combine(Home, "projects", "demo");

	private static LogRedactor Redactor() => new(Cwd, AgentDir);

	[Fact]
	public void AgentDirectoryComesBeforeHome()
	{
		var text = Redactor().Text(Path.Combine(AgentDir, "logs", "piweft-cli-1.log"));
		Assert.Equal(Path.Combine("<agent>", "logs", "piweft-cli-1.log"), text);
	}

	[Fact]
	public void WorkspaceAndHomeBecomePlaceholders()
	{
		var text = Redactor().Text($"{Path.Combine(Cwd, "src", "App.cs")} and {Path.Combine(Home, "notes.md")}");
		Assert.Equal($"{Path.Combine("<workspace>", "src", "App.cs")} and {Path.Combine("~", "notes.md")}", text);
	}

	[Fact]
	public void OnlyCompletePathPrefixesAreReplaced()
	{
		// demo2 不是 demo 目录内的文件：整段前缀替换会把它错写成 <workspace>2。
		var sibling = Cwd + "2";
		Assert.Equal(Path.Combine("~", "projects", "demo2", "file"), Redactor().Text(Path.Combine(sibling, "file")));
	}

	[Theory]
	[InlineData("Authorization: Bearer abcdefghijklmnop")]
	[InlineData("api_key=sk-abcdefghijklmnopqrstuvwxyz")]
	[InlineData("https://user:pass@example.com/path")]
	[InlineData("token=ghp_abcdefghijklmnopqrstuvwxyz0123")]
	public void CredentialsAreMasked(string input)
	{
		var text = Redactor().Text(input);
		Assert.DoesNotContain("abcdefghijklmnop", text, StringComparison.Ordinal);
		Assert.DoesNotContain("ghp_", text, StringComparison.Ordinal);
	}

	[Fact]
	public void PrivateKeyBodyIsDropped()
	{
		var text = Redactor().Text("-----BEGIN RSA PRIVATE KEY-----\nMIIEow\n-----END RSA PRIVATE KEY-----");
		Assert.DoesNotContain("MIIEow", text, StringComparison.Ordinal);
	}

	[Fact]
	public void LineCollapsesNewlinesAndKeepsContent()
	{
		Assert.Equal("a b c", Redactor().Line("a\n b \r\nc"));
		Assert.Equal("a b", Redactor().Line("a\r\n\r\n  b  "));
		Assert.Equal(string.Empty, Redactor().Line(" \r\n "));
	}

	[Fact]
	public void TruncatedLineKeepsTheOvershootCount()
	{
		var text = Redactor().Line(new string('x', 40) + "\n" + new string('y', 40), maxChars: 50);
		Assert.StartsWith(new string('x', 40), text, StringComparison.Ordinal);
		// 压成一行后是 40 + 1 + 40 = 81 个字符，超出上限 31 个。
		Assert.EndsWith("…(+31)", text, StringComparison.Ordinal);
	}
}
