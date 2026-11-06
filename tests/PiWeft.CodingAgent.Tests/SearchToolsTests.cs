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

	private void MakeRepository(string relativePath) =>
		Directory.CreateDirectory(Path.Combine(_root, relativePath, ".git"));

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
	public async Task Grep_UsesOnlyLineFeedAsLineBreak()
	{
		Write("cr.txt", "a\rb\rc\r\nmatch\r\n");

		var text = Text(await SearchTools.Grep(_root).Execute(
			Invoke("grep", new JsonObject { ["pattern"] = "match", ["path"] = "cr.txt" })));

		// rg 只按 '\n' 切行：孤立 CR 是正文，匹配在第二行。
		Assert.Contains("cr.txt:2: match", text);
	}

	[Fact]
	public async Task Grep_ReadsUtf16BomFiles()
	{
		var bytes = new byte[] { 0xFF, 0xFE }
			.Concat(System.Text.Encoding.Unicode.GetBytes("hello match\n"))
			.ToArray();
		File.WriteAllBytes(Path.Combine(_root, "u16.txt"), bytes);

		var text = Text(await SearchTools.Grep(_root).Execute(
			Invoke("grep", new JsonObject { ["pattern"] = "match", ["path"] = "u16.txt" })));

		Assert.Contains("u16.txt:1: hello match", text);
	}

	[Fact]
	public async Task Grep_FindsTextInBinaryFiles()
	{
		File.WriteAllBytes(Path.Combine(_root, "bin.dat"), [.. "abc\0def\nok\n"u8]);

		var text = Text(await SearchTools.Grep(_root).Execute(
			Invoke("grep", new JsonObject { ["pattern"] = "ok", ["path"] = "bin.dat" })));

		// rg --json 会报告二进制文件中的文本行，不整份跳过。
		Assert.Contains("bin.dat:2: ok", text);
	}

	[Fact]
	public async Task Grep_EmitsContextWithoutDuplicates()
	{
		Write("ctx.txt", "l1\nl2\nm1\nl3\nl4\nm2\nl5\n");

		var text = Text(await SearchTools.Grep(_root).Execute(
			Invoke("grep", new JsonObject { ["pattern"] = "m", ["path"] = "ctx.txt", ["context"] = 1 })));

		Assert.Equal("ctx.txt-2- l2\nctx.txt:3: m1\nctx.txt-4- l3\nctx.txt-5- l4\nctx.txt:6: m2\nctx.txt-7- l5", text);
	}

	[Fact]
	public async Task Grep_OutsideRepository_DoesNotApplyGitIgnore()
	{
		Write(".gitignore", "*.log\n");
		Write("a.log", "needle\n");
		Write("b.txt", "needle\n");

		var text = Text(await SearchTools.Grep(_root).Execute(
			Invoke("grep", new JsonObject { ["pattern"] = "needle" })));

		// ripgrep 默认只在仓库里读 .gitignore。
		Assert.Contains("a.log", text);
		Assert.Contains("b.txt", text);
	}

	[Fact]
	public async Task Find_OutsideRepository_AppliesGitIgnoreLikeFdNoRequireGit()
	{
		Write(".gitignore", "*.log\n");
		Write("a.log", "x");
		Write("b.txt", "x");

		var text = Text(await SearchTools.Find(_root).Execute(
			Invoke("find", new JsonObject { ["pattern"] = "*" })));

		Assert.DoesNotContain("a.log", text);
		Assert.Contains("b.txt", text);
	}

	[Fact]
	public async Task NestedRepository_ResetsAncestorGitIgnore()
	{
		MakeRepository("nest");
		MakeRepository("nest/inner");
		Write("nest/.gitignore", "*.log\n");
		Write("nest/outer.log", "needle\n");
		Write("nest/inner/inner.log", "needle\n");

		var find = Text(await SearchTools.Find(Path.Combine(_root, "nest")).Execute(
			Invoke("find", new JsonObject { ["pattern"] = "*.log" })));
		Assert.Contains("inner/inner.log", find);
		Assert.DoesNotContain("outer.log", find);

		var grep = Text(await SearchTools.Grep(Path.Combine(_root, "nest")).Execute(
			Invoke("grep", new JsonObject { ["pattern"] = "needle" })));
		Assert.Contains("inner/inner.log", grep);
		Assert.DoesNotContain("outer.log", grep);
	}

	[Fact]
	public async Task Find_ReadsFdIgnoreAndGrepDoesNot()
	{
		Write(".fdignore", "foo.txt\n");
		Write("foo.txt", "needle\n");
		Write("bar.txt", "needle\n");

		var find = Text(await SearchTools.Find(_root).Execute(
			Invoke("find", new JsonObject { ["pattern"] = "*" })));
		Assert.DoesNotContain("foo.txt", find);

		var grep = Text(await SearchTools.Grep(_root).Execute(
			Invoke("grep", new JsonObject { ["pattern"] = "needle" })));
		Assert.Contains("foo.txt", grep);
	}

	[Fact]
	public async Task Grep_ReadsRgIgnoreAndFindDoesNot()
	{
		Write(".rgignore", "foo.txt\n");
		Write("foo.txt", "needle\n");
		Write("bar.txt", "needle\n");

		var grep = Text(await SearchTools.Grep(_root).Execute(
			Invoke("grep", new JsonObject { ["pattern"] = "needle" })));
		Assert.DoesNotContain("foo.txt", grep);

		var find = Text(await SearchTools.Find(_root).Execute(
			Invoke("find", new JsonObject { ["pattern"] = "*" })));
		Assert.Contains("foo.txt", find);
	}

	[Fact]
	public async Task RepositoryRoot_DoesNotInheritParentGitIgnore()
	{
		Write("above/.gitignore", "*.log\n");
		MakeRepository("above/repo");
		Write("above/repo/a.log", "needle\n");
		Write("above/repo/b.txt", "needle\n");

		var find = Text(await SearchTools.Find(Path.Combine(_root, "above/repo")).Execute(
			Invoke("find", new JsonObject { ["pattern"] = "*" })));
		Assert.Contains("a.log", find);

		var grep = Text(await SearchTools.Grep(Path.Combine(_root, "above/repo")).Execute(
			Invoke("grep", new JsonObject { ["pattern"] = "needle" })));
		Assert.Contains("a.log", grep);
	}

	[Fact]
	public async Task NonVcsIgnoreAboveRepositoryRoot_StillApplies()
	{
		Write("nonvcs/.ignore", "*.log\n");
		MakeRepository("nonvcs/repo");
		Write("nonvcs/repo/a.log", "needle\n");
		Write("nonvcs/repo/b.txt", "needle\n");

		var find = Text(await SearchTools.Find(Path.Combine(_root, "nonvcs/repo")).Execute(
			Invoke("find", new JsonObject { ["pattern"] = "*" })));
		Assert.DoesNotContain("a.log", find);

		var grep = Text(await SearchTools.Grep(Path.Combine(_root, "nonvcs/repo")).Execute(
			Invoke("grep", new JsonObject { ["pattern"] = "needle" })));
		Assert.DoesNotContain("a.log", grep);
	}

	[Fact]
	public async Task Grep_NonRepoDirectoryAfterARepoDoesNotInheritGitIgnore()
	{
		MakeRepository("a");
		Write("a/x.log", "needle\n");
		// b 不是仓库：ripgrep 不读它的 .gitignore，哪怕前面刚走过一个仓库。
		Write("b/.gitignore", "*.log\n");
		Write("b/y.log", "needle\n");

		var text = Text(await SearchTools.Grep(_root).Execute(
			Invoke("grep", new JsonObject { ["pattern"] = "needle" })));

		Assert.Contains("a/x.log", text);
		Assert.Contains("b/y.log", text);
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
	public async Task FdGlobalIgnoreAppliesToFindButNotGrep()
	{
		Directory.CreateDirectory(Path.Combine(_root, "config", "fd"));
		File.WriteAllText(Path.Combine(_root, "config", "fd", "ignore"), "fd-ignored/\n");
		Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(_root, "config"));

		var work = Path.Combine(_root, "work-fd");
		Directory.CreateDirectory(Path.Combine(work, "fd-ignored"));
		File.WriteAllText(Path.Combine(work, "fd-ignored", "x.txt"), "x");
		File.WriteAllText(Path.Combine(work, "kept.txt"), "x");

		var find = await SearchTools.Find(work).Execute(new AgentToolInvocation
		{
			ToolCallId = "call-1",
			ToolName = "find",
			Arguments = new JsonObject { ["pattern"] = "*.txt" },
		});
		var findText = string.Concat(find.Content.OfType<TextContent>().Select(static block => block.Text));
		Assert.Contains("kept.txt", findText);
		Assert.DoesNotContain("fd-ignored", findText);

		var grep = await SearchTools.Grep(work).Execute(new AgentToolInvocation
		{
			ToolCallId = "call-2",
			ToolName = "grep",
			Arguments = new JsonObject { ["pattern"] = "x" },
		});
		var grepText = string.Concat(grep.Content.OfType<TextContent>().Select(static block => block.Text));
		Assert.Contains("fd-ignored/x.txt", grepText);
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

	[Fact]
	public void RelativeTo_RequiresASeparatorBoundary()
	{
		var root = Path.Combine(Path.GetTempPath(), "piweft-rel");
		// 前缀相同但不是子目录：不能切出错误相对路径。
		var sibling = root + "x/file.ts";
		Assert.Equal(Path.GetRelativePath(root, sibling).Replace('\\', '/'), ToolPaths.RelativeTo(sibling, root));

		Assert.Equal("a/b.ts", ToolPaths.RelativeTo(Path.Combine(root, "a", "b.ts"), root));
		Assert.Equal("b.ts", ToolPaths.RelativeTo(Path.Combine(root, "b.ts"), root));
	}

}