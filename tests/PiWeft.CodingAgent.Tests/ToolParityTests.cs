using System.Text;
using System.Text.Json.Nodes;
using PiWeft.Agent;
using PiWeft.Ai;
using PiWeft.CodingAgent.Tools;
using Xunit;

namespace PiWeft.CodingAgent.Tests;

/// 针对与 pi / PiDeck 行为对齐的回归用例。工具名、参数与真实 CLI 调用一致。
public sealed class ToolParityTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-parity-" + Guid.NewGuid().ToString("N")[..8]);

	public ToolParityTests() => Directory.CreateDirectory(_root);

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	private static AgentToolInvocation Invoke(string tool, JsonObject arguments, CancellationToken cancellationToken = default) => new()
	{
		ToolCallId = "call-1",
		ToolName = tool,
		Arguments = arguments,
		CancellationToken = cancellationToken,
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
	public async Task Edit_UsesTheFirstNewlineStyleInsteadOfTurningMixedFilesIntoCrlf()
	{
		var path = Write("mixed.txt", "a\nb\r\nc\n");

		await FileTools.Edit(_root).Execute(Invoke("edit", new JsonObject
		{
			["path"] = "mixed.txt",
			["edits"] = new JsonArray(new JsonObject { ["oldText"] = "b", ["newText"] = "B" }),
		}));

		Assert.Equal("a\nB\nc\n", File.ReadAllText(path));
	}

	[Theory]
	[InlineData("""[{"oldText":"hello","newText":"hi"}]""")]
	[InlineData("""{"oldText":"hello","newText":"hi"}""")]
	public async Task Edit_AcceptsPiArgumentVariants(string edits)
	{
		var path = Write("coerce.txt", "hello world");
		var arguments = new JsonObject
		{
			["path"] = "coerce.txt",
			["edits"] = edits,
		};

		await FileTools.Edit(_root).Execute(Invoke("edit", arguments));

		Assert.Equal("hi world", File.ReadAllText(path));
	}

	[Fact]
	public async Task Edit_AcceptsLegacyTopLevelOldTextAndNewText()
	{
		var path = Write("legacy.txt", "hello world");

		await FileTools.Edit(_root).Execute(Invoke("edit", new JsonObject
		{
			["path"] = "legacy.txt",
			["oldText"] = "hello",
			["newText"] = "hi",
		}));

		Assert.Equal("hi world", File.ReadAllText(path));
	}

	[Fact]
	public async Task Write_DoesNotReplaceAReadOnlyFileOnUnix()
	{
		if (OperatingSystem.IsWindows())
		{
			return;
		}

		var path = Write("readonly.txt", "old");
		File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

		await Assert.ThrowsAsync<InvalidOperationException>(() =>
			FileTools.Write(_root).Execute(Invoke("write", new JsonObject
			{
				["path"] = "readonly.txt",
				["content"] = "new",
			})));

		Assert.Equal("old", File.ReadAllText(path));
	}

	[Fact]
	public async Task Read_AcceptsFileUrlsAndAtPrefixedPaths()
	{
		var path = Write("url target.txt", "hello");
		var uri = new Uri(path).AbsoluteUri;

		var byUrl = await FileTools.Read(_root).Execute(Invoke("read", new JsonObject { ["path"] = uri }));
		Assert.Equal("hello", Text(byUrl));

		var byAt = await FileTools.Read(_root).Execute(Invoke("read", new JsonObject { ["path"] = "@" + path }));
		Assert.Equal("hello", Text(byAt));
	}

	[Fact]
	public async Task Read_DetectsImagesByMagicBytesNotByExtension()
	{
		Write("fake.png", "this is plain text");
		var textResult = await FileTools.Read(_root).Execute(Invoke("read", new JsonObject { ["path"] = "fake.png" }));
		Assert.DoesNotContain(textResult.Content, static block => block is ImageContent);

		// 最小 PNG 头：签名 + 13 字节 IHDR；不需要完整像素数据，只验证嗅探。
		var png = new byte[]
		{
			0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
			0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
			0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
			0x08, 0x06, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
		};
		var pngPath = Path.Combine(_root, "image.txt");
		File.WriteAllBytes(pngPath, png);

		var imageResult = await FileTools.Read(_root).Execute(Invoke("read", new JsonObject { ["path"] = "image.txt" }));
		Assert.Contains(imageResult.Content, static block => block is ImageContent);
		Assert.Contains("Read image file [image/png]", Text(imageResult));
	}

	[Fact]
	public async Task Read_AddsTheNonVisionNoteWhenTheBoundModelHasNoImageInput()
	{
		var png = new byte[]
		{
			0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
			0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
			0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
			0x08, 0x06, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
		};
		File.WriteAllBytes(Path.Combine(_root, "image.png"), png);
		var tool = FileTools.Read(_root, static () => ScriptedSessions.Model() with { Input = ["text"] });

		var result = await tool.Execute(Invoke("read", new JsonObject { ["path"] = "image.png" }));

		Assert.Contains("[Current model does not support images.", Text(result));
	}

	[Fact]
	public async Task Bash_NonZeroExitIsAnErrorAndCarriesStructuredContent()
	{
		if (OperatingSystem.IsWindows())
		{
			return;
		}

		var result = await BashTool.Create(_root).Execute(Invoke("bash", new JsonObject
		{
			["command"] = "printf 'boom'; exit 3",
		}));

		Assert.True(result.IsError);
		Assert.Contains("Command exited with code 3", Text(result));
		Assert.Equal(3, result.StructuredContent!["exit_code"]!.GetValue<int>());
	}

	[Fact]
	public async Task Bash_PreservesExactOutputAndReportsEmptyOutputLikePi()
	{
		if (OperatingSystem.IsWindows())
		{
			return;
		}

		var exact = await BashTool.Create(_root).Execute(Invoke("bash", new JsonObject
		{
			["command"] = "printf 'hello'",
		}));
		Assert.Equal("hello", Text(exact));

		var carriageReturn = await BashTool.Create(_root).Execute(Invoke("bash", new JsonObject
		{
			["command"] = "printf 'a\\rb\\r\\nc'",
		}));
		Assert.Equal("a\rb\r\nc", Text(carriageReturn));

		var empty = await BashTool.Create(_root).Execute(Invoke("bash", new JsonObject
		{
			["command"] = "true",
		}));
		Assert.Equal("(no output)", Text(empty));
	}

	[Fact]
	public async Task Bash_CancellationKeepsPartialOutput()
	{
		if (OperatingSystem.IsWindows())
		{
			return;
		}

		using var cancellation = new CancellationTokenSource(300);
		var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
			BashTool.Create(_root).Execute(Invoke(
				"bash",
				new JsonObject { ["command"] = "printf 'partial'; sleep 5" },
				cancellation.Token)));

		Assert.Contains("partial", error.Message);
		Assert.Contains("Command aborted", error.Message);
	}

	[Fact]
	public async Task Find_GlobWithoutSlashMatchesBasenamesRecursively()
	{
		Write("a.ts", "x");
		Write("sub/b.ts", "x");
		Write("sub/c.js", "x");

		var result = await SearchTools.Find(_root).Execute(Invoke("find", new JsonObject
		{
			["pattern"] = "*.ts",
		}));

		var text = Text(result);
		Assert.Contains("a.ts", text);
		Assert.Contains("sub/b.ts", text);
		Assert.DoesNotContain("c.js", text);
	}

	[Fact]
	public async Task Find_And_Grep_HonorParentGitIgnoreWhenSearchingASubdirectory()
	{
		Write(".gitignore", "ignored.txt\n");
		Write("sub/ignored.txt", "needle");
		Write("sub/keep.txt", "needle");

		var grep = await SearchTools.Grep(_root).Execute(Invoke("grep", new JsonObject
		{
			["pattern"] = "needle",
			["path"] = "sub",
		}));
		Assert.DoesNotContain("ignored.txt", Text(grep));
		Assert.Contains("keep.txt", Text(grep));

		var find = await SearchTools.Find(_root).Execute(Invoke("find", new JsonObject
		{
			["pattern"] = "*.txt",
			["path"] = "sub",
		}));
		Assert.DoesNotContain("ignored.txt", Text(find));
		Assert.Contains("keep.txt", Text(find));
	}

	[Fact]
	public async Task Grep_ClampsLimitsAndContextAndCapsTheOutput()
	{
		Write("g.txt", "x\na\ny\n");

		var zeroLimit = await SearchTools.Grep(_root).Execute(Invoke("grep", new JsonObject
		{
			["pattern"] = "a",
			["limit"] = 0,
		}));
		Assert.Contains("g.txt:2: a", Text(zeroLimit));

		var negativeContext = await SearchTools.Grep(_root).Execute(Invoke("grep", new JsonObject
		{
			["pattern"] = "a",
			["context"] = -1,
		}));
		Assert.Contains("g.txt:2: a", Text(negativeContext));

		var big = string.Join("\n", Enumerable.Range(1, 100).Select(static _ => new string('z', 500) + " MATCH"));
		Write("big.txt", big);
		var capped = await SearchTools.Grep(_root).Execute(Invoke("grep", new JsonObject
		{
			["pattern"] = "MATCH",
			["path"] = "big.txt",
		}));
		Assert.Contains("50.0KB limit reached", Text(capped));
		Assert.True(capped.Details!["truncation"] is JsonObject);
	}

	[Fact]
	public async Task Grep_LiteralPatternKeepsSpaces()
	{
		Write("lit.txt", "foo bar\nfoobar\n");

		var result = await SearchTools.Grep(_root).Execute(Invoke("grep", new JsonObject
		{
			["pattern"] = "foo bar",
			["literal"] = true,
		}));

		Assert.Contains("lit.txt:1: foo bar", Text(result));
		Assert.DoesNotContain("lit.txt:2", Text(result));
	}

	[Fact]
	public async Task Grep_RejectsLookaroundLikeRipgrepRatherThanBacktracking()
	{
		Write("re.txt", "xa");

		await Assert.ThrowsAsync<InvalidOperationException>(() =>
			SearchTools.Grep(_root).Execute(Invoke("grep", new JsonObject
			{
				["pattern"] = "(?<=x)a",
			})));
	}

	[Fact]
	public async Task Ls_SortsCaseInsensitivelyAndClampsZeroLimit()
	{
		Write("ls/B.txt", "x");
		Write("ls/a.txt", "x");
		Write("ls/C.txt", "x");

		var all = await SearchTools.Ls(_root).Execute(Invoke("ls", new JsonObject { ["path"] = "ls" }));
		var lines = Text(all).Split('\n');
		Assert.Equal(["a.txt", "B.txt", "C.txt"], lines);

		var one = await SearchTools.Ls(_root).Execute(Invoke("ls", new JsonObject
		{
			["path"] = "ls",
			["limit"] = 0,
		}));
		Assert.StartsWith("a.txt", Text(one));
		Assert.Contains("1 entries limit reached", Text(one));
	}

	[Fact]
	public void AskQuestion_DetailsKeepOptionsAndAnswerLabelsForPiDeckReplay()
	{
		var questions = new List<AskQuestion>
		{
			new("q1", "select", "pick", [new AskOption("A", "a", "first")], "placeholder", "prefill"),
		};
		var answers = new List<AskAnswer> { new("q1", "select", JsonValue.Create("a"), "A", WasCustom: false) };

		var details = AskQuestions.ResultDetails(questions, answers, cancelled: false);

		var question = details["questions"]![0]!.AsObject();
		Assert.Equal("a", question["options"]![0]!["value"]!.GetValue<string>());
		Assert.True(question["allowOther"]!.GetValue<bool>());
		Assert.Equal("placeholder", question["placeholder"]!.GetValue<string>());
		Assert.Equal("prefill", question["prefill"]!.GetValue<string>());
		Assert.Equal("A", details["answers"]![0]!["label"]!.GetValue<string>());
	}

	[Fact]
	public void AskQuestion_LegacySingleQuestionUsesDefaultId()
	{
		var questions = AskQuestions.FromToolArguments(new JsonObject { ["question"] = "继续吗？" });

		Assert.Equal("default", Assert.Single(questions).Id);
	}

	[Fact]
	public async Task CoreToolsDeclareTheSameStructuredOutputsAsPi()
	{
		Assert.NotNull(FileTools.Read(_root).OutputSchema);
		Assert.NotNull(BashTool.Create(_root).OutputSchema);
		Assert.Null(FileTools.Write(_root).OutputSchema);
		Assert.Null(FileTools.Edit(_root).OutputSchema);

		var read = await FileTools.Read(_root).Execute(Invoke("read", new JsonObject { ["path"] = Write("r.txt", "hello") }));
		Assert.Equal("hello", read.StructuredContent!.GetValue<string>());

		if (!OperatingSystem.IsWindows())
		{
			var bash = await BashTool.Create(_root).Execute(Invoke("bash", new JsonObject
			{
				["command"] = "printf 'hi'",
			}));
			Assert.Equal("hi", bash.StructuredContent!["output"]!.GetValue<string>());
		}
	}
}
