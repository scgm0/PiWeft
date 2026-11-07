using System.Text;
using System.Text.Json.Nodes;
using PiWeft.Agent;
using PiWeft.Ai;
using PiWeft.CodingAgent.Tools;
using Xunit;

namespace PiWeft.CodingAgent.Tests;

public class ToolTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-tools-" + Guid.NewGuid().ToString("N")[..8]);

	public ToolTests() => Directory.CreateDirectory(_root);

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	private AgentToolInvocation Invoke(string tool, JsonObject arguments) => new()
	{
		ToolCallId = "c1",
		ToolName = tool,
		Arguments = arguments,
	};

	private static string Text(AgentToolResult result) =>
		string.Concat(result.Content.OfType<TextContent>().Select(static block => block.Text));

	private string Write(string relativePath, string content)
	{
		var path = Path.Combine(_root, relativePath);
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, content);
		return path;
	}


	[Fact]
	public async Task Read_ReturnsFileContent()
	{
		Write("a.txt", "one\ntwo\nthree\n");
		var result = await FileTools.Read(_root).Execute(Invoke("read", new JsonObject { ["path"] = "a.txt" }));

		Assert.Contains("one", Text(result));
		Assert.Contains("three", Text(result));
	}

	[Fact]
	public async Task Read_OffsetAndLimit_AddsContinuationNotice()
	{
		Write("a.txt", string.Join("\n", Enumerable.Range(1, 10)) + "\n");
		var result = await FileTools.Read(_root).Execute(
			Invoke("read", new JsonObject { ["path"] = "a.txt", ["offset"] = 2, ["limit"] = 3 }));

		var text = Text(result);
		Assert.Contains("2\n3\n4", text);
		Assert.Contains("7 more lines in file. Use offset=5 to continue.", text);
	}

	[Fact]
	public async Task Read_OffsetBeyondEnd_Throws()
	{
		Write("a.txt", "one\n");
		var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
			FileTools.Read(_root).Execute(Invoke("read", new JsonObject { ["path"] = "a.txt", ["offset"] = 99 })));

		Assert.Contains("beyond end of file", error.Message);
	}

	[Fact]
	public async Task Read_MissingFile_Throws()
	{
		await Assert.ThrowsAsync<FileNotFoundException>(() =>
			FileTools.Read(_root).Execute(Invoke("read", new JsonObject { ["path"] = "nope.txt" })));
	}

	[Fact]
	public async Task Read_TruncatesAtLineLimit()
	{
		Write("big.txt", string.Join("\n", Enumerable.Range(1, 2500)) + "\n");
		var result = await FileTools.Read(_root).Execute(Invoke("read", new JsonObject { ["path"] = "big.txt" }));

		var text = Text(result);
		Assert.Contains("Showing lines 1-2000 of 2501", text);
		Assert.Contains("Use offset=2001 to continue.", text);
	}

	[Fact]
	public async Task Read_DoesNotSplitOnLoneCarriageReturn()
	{
		// pi 的 read 只在 '\n' 上切行；孤立 CR 是正文（与 grep/rg 的差异被单独处理）。
		Write("cr.txt", "a\rb\nmatch\n");
		var text = Text(await FileTools.Read(_root).Execute(Invoke("read", new JsonObject { ["path"] = "cr.txt" })));

		Assert.Equal("a\rb\nmatch\n", text);
	}

	[Fact]
	public async Task Read_FirstLineOverByteLimit_PointsAtTheBashFallback()
	{
		var longLine = new string('x', (int)Truncate.DefaultMaxBytes + 1024);
		Write("long.txt", longLine + "\nsecond\n");

		var text = Text(await FileTools.Read(_root).Execute(Invoke("read", new JsonObject { ["path"] = "long.txt" })));

		Assert.Contains("exceeds 50.0KB limit", text);
		Assert.Contains("head -c 51200", text);

		// offset 跳到后面的正常行仍然可读。
		var second = Text(await FileTools.Read(_root).Execute(
			Invoke("read", new JsonObject { ["path"] = "long.txt", ["offset"] = 2 })));
		Assert.Contains("second", second);
	}


	[Theory]
	[InlineData("")]
	[InlineData("plain ascii")]
	[InlineData("汉字与 emoji 😀 混排")]
	[InlineData("\uD800")]
	public async Task Write_ProducesTheSameUtf8BytesAsTheEncoder(string content)
	{
		// 分块编码写入必须与一次性 GetBytes 逐字节相同，包括边界与非法代理项。
		var name = $"exact-{content.Length}.txt";
		await FileTools.Write(_root).Execute(Invoke("write", new JsonObject
		{
			["path"] = name,
			["content"] = content,
		}));

		var expected = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(content);
		Assert.Equal(expected, File.ReadAllBytes(Path.Combine(_root, name)));
	}

	[Fact]
	public async Task Write_SplitsSurrogatePairsAcrossChunkBoundaries()
	{
		// 16 KiB 分块：把 emoji（代理对）放在 16384 字符边界两侧，验证不会被拆成两个替换字符。
		var content = new string('a', 16383) + "😀" + "😀" + new string('汉', 10) + "\uD800" + new string('b', 20);
		await FileTools.Write(_root).Execute(Invoke("write", new JsonObject
		{
			["path"] = "boundary.txt",
			["content"] = content,
		}));

		var expected = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(content);
		Assert.Equal(expected, File.ReadAllBytes(Path.Combine(_root, "boundary.txt")));
	}

	[Fact]
	public async Task Write_TruncatesExistingLongerContent()
	{
		Write("shrink.txt", new string('x', 5000));
		await FileTools.Write(_root).Execute(Invoke("write", new JsonObject
		{
			["path"] = "shrink.txt",
			["content"] = "short",
		}));

		Assert.Equal("short", File.ReadAllText(Path.Combine(_root, "shrink.txt")));
	}

	[Fact]
	public async Task Write_CreatesParentDirectories()
	{
		await FileTools.Write(_root).Execute(Invoke("write", new JsonObject
		{
			["path"] = "nested/deep/out.txt",
			["content"] = "hello",
		}));

		Assert.Equal("hello", File.ReadAllText(Path.Combine(_root, "nested/deep/out.txt")));
	}


	[Fact]
	public async Task Edit_AppliesMultipleDisjointEdits()
	{
		Write("a.txt", "alpha\nbeta\ngamma\n");
		var result = await FileTools.Edit(_root).Execute(Invoke("edit", new JsonObject
		{
			["path"] = "a.txt",
			["edits"] = new JsonArray(
				new JsonObject { ["oldText"] = "alpha", ["newText"] = "ALPHA" },
				new JsonObject { ["oldText"] = "gamma", ["newText"] = "GAMMA" }),
		}));

		Assert.Equal("ALPHA\nbeta\nGAMMA\n", File.ReadAllText(Path.Combine(_root, "a.txt")));
		Assert.Contains("--- a.txt", result.Details!["patch"]!.GetValue<string>());
		Assert.Contains("+1 ALPHA", result.Details["diff"]!.GetValue<string>());
		Assert.Equal(1, result.Details["firstChangedLine"]!.GetValue<int>());
	}

	[Fact]
	public async Task Edit_PreservesCrlfAndBom()
	{
		var path = Path.Combine(_root, "crlf.txt");
		File.WriteAllText(path, "\uFEFFone\r\ntwo\r\n", new UTF8Encoding(false));

		await FileTools.Edit(_root).Execute(Invoke("edit", new JsonObject
		{
			["path"] = "crlf.txt",
			["edits"] = new JsonArray(new JsonObject { ["oldText"] = "two", ["newText"] = "TWO" }),
		}));

		var bytes = File.ReadAllBytes(path);
		Assert.Equal(0xEF, bytes[0]);
		Assert.Equal(0xBB, bytes[1]);
		Assert.Equal(0xBF, bytes[2]);

		// Decode the bytes directly: File.ReadAllText would strip the BOM again.
		Assert.Equal("\uFEFFone\r\nTWO\r\n", Encoding.UTF8.GetString(bytes));
	}

	[Fact]
	public async Task Edit_RejectsAmbiguousAndMissingText()
	{
		Write("a.txt", "dup\ndup\n");

		var ambiguous = await Assert.ThrowsAsync<InvalidOperationException>(() =>
			FileTools.Edit(_root).Execute(Invoke("edit", new JsonObject
			{
				["path"] = "a.txt",
				["edits"] = new JsonArray(new JsonObject { ["oldText"] = "dup", ["newText"] = "x" }),
			})));
		Assert.Contains("must be unique", ambiguous.Message);

		var missing = await Assert.ThrowsAsync<InvalidOperationException>(() =>
			FileTools.Edit(_root).Execute(Invoke("edit", new JsonObject
			{
				["path"] = "a.txt",
				["edits"] = new JsonArray(new JsonObject { ["oldText"] = "absent", ["newText"] = "x" }),
			})));
		Assert.Contains("Could not find the exact text", missing.Message);
	}

	[Fact]
	public async Task Edit_RejectsOverlappingEdits()
	{
		Write("a.txt", "abcdef\n");
		var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
			FileTools.Edit(_root).Execute(Invoke("edit", new JsonObject
			{
				["path"] = "a.txt",
				["edits"] = new JsonArray(
					new JsonObject { ["oldText"] = "abc", ["newText"] = "x" },
					new JsonObject { ["oldText"] = "bcd", ["newText"] = "y" }),
			})));

		Assert.Contains("overlap", error.Message);
	}

	[Fact]
	public async Task Edit_ToleratesWhitespaceDifferences()
	{
		Write("a.txt", "  indented line   \nnext\n");
		await FileTools.Edit(_root).Execute(Invoke("edit", new JsonObject
		{
			["path"] = "a.txt",
			["edits"] = new JsonArray(new JsonObject { ["oldText"] = "  indented line\n", ["newText"] = "replaced\n" }),
		}));

		Assert.Equal("replaced\nnext\n", File.ReadAllText(Path.Combine(_root, "a.txt")));
	}

	[Fact]
	public async Task Edit_LargeFileProducesLocalPatch()
	{
		// 回归：曾经用全表 LCS 算 diff，2 万行两侧要 1.6 GB、几十秒。
		var lines = Enumerable.Range(0, 20_000).Select(static i => $"line {i} content").ToArray();
		Write("big.txt", string.Join('\n', lines) + "\n");

		var result = await FileTools.Edit(_root).Execute(Invoke("edit", new JsonObject
		{
			["path"] = "big.txt",
			["edits"] = new JsonArray(new JsonObject { ["oldText"] = "line 10000 content", ["newText"] = "line 10000 changed" }),
		}));

		var patch = result.Details!["patch"]!.GetValue<string>();
		Assert.Contains("line 10000 changed", patch);
		Assert.True(patch.Split('\n').Length < 30, $"patch 只应包含改动附近的行，实际 {patch.Split('\n').Length} 行");
		Assert.Equal(10_001, result.Details["firstChangedLine"]!.GetValue<int>());
		Assert.Contains("line 10000 changed", File.ReadAllText(Path.Combine(_root, "big.txt")));
	}

	[Fact]
	public async Task Edit_DiffFallsBackForWhollyRewrittenText()
	{
		// 两侧没有共用行时拿不到锚点，退化为整段删加，但仍要给出能读的 hunk。
		Write("a.txt", "aaa\nbbb\nccc\nddd\neee\nfff\n");
		var result = await FileTools.Edit(_root).Execute(Invoke("edit", new JsonObject
		{
			["path"] = "a.txt",
			["edits"] = new JsonArray(new JsonObject { ["oldText"] = "bbb\nccc\nddd", ["newText"] = "111\n222\n333" }),
		}));

		var diff = result.Details!["diff"]!.GetValue<string>();
		Assert.Contains("-2 bbb", diff);
		Assert.Contains("+2 111", diff);
		Assert.Equal("aaa\n111\n222\n333\neee\nfff\n", File.ReadAllText(Path.Combine(_root, "a.txt")));
	}


	[Fact]
	public async Task Ls_ListsDirectoriesWithSlash()
	{
		Directory.CreateDirectory(Path.Combine(_root, "src"));
		Write("file.txt", "x");

		var result = await SearchTools.Ls(_root).Execute(Invoke("ls", new JsonObject()));
		var lines = Text(result).Split('\n');

		Assert.Contains("src/", lines);
		Assert.Contains("file.txt", lines);
	}

	[Fact]
	public async Task Ls_EmptyDirectory_ReportsIt()
	{
		var empty = Path.Combine(_root, "empty");
		Directory.CreateDirectory(empty);

		var result = await SearchTools.Ls(_root).Execute(Invoke("ls", new JsonObject { ["path"] = "empty" }));
		Assert.Equal("(empty directory)", Text(result));
	}

	[Fact]
	public async Task Find_MatchesGlobAndRespectsGitIgnore()
	{
		Write(".gitignore", "ignored/\n");
		Write("src/a.ts", "x");
		Write("src/b.js", "x");
		Write("ignored/c.ts", "x");

		var result = await SearchTools.Find(_root).Execute(Invoke("find", new JsonObject { ["pattern"] = "**/*.ts" }));
		var lines = Text(result).Split('\n');

		Assert.Contains("src/a.ts", lines);
		Assert.DoesNotContain("ignored/c.ts", lines);
		Assert.DoesNotContain("src/b.js", lines);
	}

	[Fact]
	public async Task Find_NoMatches_ReportsIt()
	{
		Write("a.txt", "x");
		var result = await SearchTools.Find(_root).Execute(Invoke("find", new JsonObject { ["pattern"] = "*.rs" }));
		Assert.Equal("No files found matching pattern", Text(result));
	}


	[Fact]
	public async Task Grep_ReportsMatchesWithContextAndLineNumbers()
	{
		Write("a.txt", "one\ntwo\nthree\nfour\n");
		var result = await SearchTools.Grep(_root).Execute(Invoke("grep", new JsonObject
		{
			["pattern"] = "three",
			["context"] = 1,
		}));

		var text = Text(result);
		Assert.Contains("a.txt:3: three", text);
		Assert.Contains("a.txt-2- two", text);
		Assert.Contains("a.txt-4- four", text);
	}

	[Fact]
	public async Task Grep_RespectsGitIgnoreAndSupportsLiteralAndIgnoreCase()
	{
		Directory.CreateDirectory(Path.Combine(_root, ".git"));
		Write(".gitignore", "vendor/\n");
		Write("src/a.txt", "Needle\n");
		Write("vendor/b.txt", "Needle\n");

		var result = await SearchTools.Grep(_root).Execute(Invoke("grep", new JsonObject
		{
			["pattern"] = "needle",
			["ignoreCase"] = true,
			["literal"] = true,
		}));

		var text = Text(result);
		Assert.Contains("src/a.txt:1: Needle", text);
		Assert.DoesNotContain("vendor", text);
	}

	[Fact]
	public async Task Grep_NoMatches_ReportsIt()
	{
		Write("a.txt", "x");
		var result = await SearchTools.Grep(_root).Execute(Invoke("grep", new JsonObject { ["pattern"] = "zzz" }));
		Assert.Equal("No matches found", Text(result));
	}


	[Fact]
	public async Task Bash_CapturesOutput()
	{
		if (OperatingSystem.IsWindows())
		{
			return;
		}

		var result = await BashTool.Create(_root).Execute(Invoke("bash", new JsonObject
		{
			["command"] = "printf 'hello\\n'",
		}));

		Assert.Equal("hello\n", Text(result));
		Assert.Equal(0, result.StructuredContent!["exit_code"]!.GetValue<int>());
	}

	[Fact]
	public async Task Bash_NonZeroExitIsReported()
	{
		if (OperatingSystem.IsWindows())
		{
			return;
		}

		var result = await BashTool.Create(_root).Execute(Invoke("bash", new JsonObject
		{
			["command"] = "echo boom >&2; exit 3",
		}));

		var text = Text(result);
		Assert.Contains("boom", text);
		Assert.Contains("Command exited with code 3", text);
	}

	[Fact]
	public async Task Bash_TimeoutFails()
	{
		if (OperatingSystem.IsWindows())
		{
			return;
		}

		var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
			BashTool.Create(_root).Execute(Invoke("bash", new JsonObject
			{
				["command"] = "sleep 5",
				["timeout"] = 1,
			})));

		Assert.Contains("timed out", error.Message);
	}

	[Fact]
	public async Task Bash_RejectsNonPositiveTimeout()
	{
		var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
			BashTool.Create(_root).Execute(Invoke("bash", new JsonObject
			{
				["command"] = "true",
				["timeout"] = 0,
			})));

		Assert.Contains("Invalid timeout", error.Message);
	}
}
