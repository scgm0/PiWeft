using System.Text.Json.Nodes;
using PiWeft.Agent;
using PiWeft.Ai;
using PiWeft.CodingAgent.Tools;
using Xunit;

namespace PiWeft.CodingAgent.Tests;

/// grep / find 的 glob、gitignore 与 limit 语义。pi 把这些交给 ripgrep / fd（globset +
/// ignore crate）；进程内实现至少要覆盖它们真正影响结果的规则：smart-case、{a,b} 分支、
/// fnmatch 的 <c>[!a]</c> 取反、路径型 pattern 的任意深度匹配，以及全局忽略。
public sealed class SearchToolsTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-search-" + Guid.NewGuid().ToString("N")[..8]);

	public SearchToolsTests() => Directory.CreateDirectory(_root);

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	private static AgentToolInvocation Invoke(string tool, JsonObject arguments) => new()
	{
		ToolCallId = "call-1",
		ToolName = tool,
		Arguments = arguments,
	};

	private static string Text(AgentToolResult result) =>
		string.Concat(result.Content.OfType<TextContent>().Select(static block => block.Text));

	private void Write(string relativePath, string content)
	{
		var path = Path.Combine(_root, relativePath);
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, content);
	}

	[Fact]
	public async Task Find_ExpandsBraceAlternativesLikeFd()
	{
		Write("a.ts", "x");
		Write("b.js", "x");
		Write("c.md", "x");

		var result = await SearchTools.Find(_root).Execute(
			Invoke("find", new JsonObject { ["pattern"] = "*.{ts,js}" }));
		var lines = Text(result).Split('\n');

		Assert.Contains("a.ts", lines);
		Assert.Contains("b.js", lines);
		Assert.DoesNotContain("c.md", lines);
	}

	[Fact]
	public async Task Find_IsSmartCaseLikeFd()
	{
		Write("lower.txt", "x");
		Write("UPPER.TXT", "x");

		// 全小写 pattern：fd 默认视为大小写不敏感。
		var insensitive = Text(await SearchTools.Find(_root).Execute(
			Invoke("find", new JsonObject { ["pattern"] = "*.txt" })));
		Assert.Contains("lower.txt", insensitive);
		Assert.Contains("UPPER.TXT", insensitive);

		// 含大写：变成大小写敏感。
		var sensitive = Text(await SearchTools.Find(_root).Execute(
			Invoke("find", new JsonObject { ["pattern"] = "*.TXT" })));
		Assert.Contains("UPPER.TXT", sensitive);
		Assert.DoesNotContain("lower.txt", sensitive);
	}

	[Fact]
	public async Task Find_MatchesPathPatternsAtAnyDepthLikeFullPath()
	{
		Write("src/a.ts", "x");
		Write("deep/nested/src/b.ts", "x");

		var lines = Text(await SearchTools.Find(_root).Execute(
			Invoke("find", new JsonObject { ["pattern"] = "src/*.ts" }))).Split('\n');

		// pi 会给路径型 pattern 补 `**/`（fd --full-path），因此深层同名目录也命中。
		Assert.Contains("src/a.ts", lines);
		Assert.Contains("deep/nested/src/b.ts", lines);
	}

	[Fact]
	public async Task Find_ReportsTheLimitOnlyWhenAnExtraResultExists()
	{
		Write("a.txt", "x");
		Write("b.txt", "x");

		var exact = Text(await SearchTools.Find(_root).Execute(
			Invoke("find", new JsonObject { ["pattern"] = "*.txt", ["limit"] = 2 })));
		Assert.DoesNotContain("results limit reached", exact);

		var exceeded = Text(await SearchTools.Find(_root).Execute(
			Invoke("find", new JsonObject { ["pattern"] = "*.txt", ["limit"] = 1 })));
		Assert.Contains("results limit reached", exceeded);
	}

	[Fact]
	public async Task GitIgnore_NegatedCharacterClassIgnoresTheOtherNames()
	{
		Write(".gitignore", "[!a].txt\n");
		Write("a.txt", "x");
		Write("b.txt", "x");

		var lines = Text(await SearchTools.Find(_root).Execute(
			Invoke("find", new JsonObject { ["pattern"] = "*.txt" }))).Split('\n');

		Assert.Contains("a.txt", lines);
		Assert.DoesNotContain("b.txt", lines);
	}

	[Fact]
	public async Task Find_DoesNotDescendIntoLinkedDirectories()
	{
		if (OperatingSystem.IsWindows())
		{
			return; // 创建符号链接要额外权限，Windows 上不作为回归用例。
		}

		Write("real/a.txt", "x");
		Directory.CreateSymbolicLink(Path.Combine(_root, "linked"), Path.Combine(_root, "real"));

		var lines = Text(await SearchTools.Find(_root).Execute(
			Invoke("find", new JsonObject { ["pattern"] = "*.txt" }))).Split('\n');

		// 链接本身作为目录项可以出现，但不能跟进去（否则自指链接会让遍历无限循环）。
		Assert.Contains("real/a.txt", lines);
		Assert.DoesNotContain("linked/a.txt", lines);
	}

	[Fact]
	public async Task Grep_GlobIsCaseSensitiveLikeRipgrep()
	{
		Write("a.txt", "needle\n");
		Write("B.TXT", "needle\n");

		var text = Text(await SearchTools.Grep(_root).Execute(Invoke("grep", new JsonObject
		{
			["pattern"] = "needle",
			["glob"] = "*.TXT",
		})));

		// rg 的 --glob 不做 smart-case：全小写 pattern 也只匹配全小写文件名。
		Assert.Contains("B.TXT", text);
		Assert.DoesNotContain("a.txt", text);
	}

	[Fact]
	public async Task Grep_ReportsTheMatchLimitOnlyWhenAnExtraMatchExists()
	{
		Write("a.txt", "needle\nneedle\n");

		var exact = Text(await SearchTools.Grep(_root).Execute(Invoke("grep", new JsonObject
		{
			["pattern"] = "needle",
			["limit"] = 2,
		})));
		Assert.DoesNotContain("matches limit reached", exact);

		var exceeded = Text(await SearchTools.Grep(_root).Execute(Invoke("grep", new JsonObject
		{
			["pattern"] = "needle",
			["limit"] = 1,
		})));
		Assert.Contains("matches limit reached", exceeded);
	}

	[Fact]
	public async Task Grep_GlobSupportsRipgrepStyleNegation()
	{
		Write("keep/a.txt", "needle\n");
		Write("skip/b.txt", "needle\n");

		var text = Text(await SearchTools.Grep(_root).Execute(Invoke("grep", new JsonObject
		{
			["pattern"] = "needle",
			["glob"] = "!skip/**",
		})));

		Assert.Contains("keep/a.txt", text);
		Assert.DoesNotContain("skip/b.txt", text);
	}
}

/// 全局忽略（<c>$XDG_CONFIG_HOME/git/ignore</c> 与 <c>core.excludesFile</c>）是 ripgrep/fd 的
/// 默认行为之一。改进程环境变量，因此单独放进串行集合。
[Collection("process-environment")]
public sealed class GlobalGitIgnoreTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-globalignore-" + Guid.NewGuid().ToString("N")[..8]);
	private readonly string? _previousXdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");

	public GlobalGitIgnoreTests()
	{
		Directory.CreateDirectory(Path.Combine(_root, "work"));
		Directory.CreateDirectory(Path.Combine(_root, "config", "git"));
	}

	public void Dispose()
	{
		Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _previousXdg);
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	[Fact]
	public async Task GlobalIgnoreFileIsHonouredLikeRipgrepAndFd()
	{
		File.WriteAllText(Path.Combine(_root, "config", "git", "ignore"), "global-ignored/\n");
		Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(_root, "config"));

		var work = Path.Combine(_root, "work");
		Directory.CreateDirectory(Path.Combine(work, "global-ignored"));
		File.WriteAllText(Path.Combine(work, "global-ignored", "x.txt"), "x");
		File.WriteAllText(Path.Combine(work, "kept.txt"), "x");

		var result = await SearchTools.Find(work).Execute(new AgentToolInvocation
		{
			ToolCallId = "call-1",
			ToolName = "find",
			Arguments = new JsonObject { ["pattern"] = "*.txt" },
		});

		var text = string.Concat(result.Content.OfType<TextContent>().Select(static block => block.Text));
		Assert.Contains("kept.txt", text);
		Assert.DoesNotContain("global-ignored", text);
	}
}