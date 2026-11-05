using System.Diagnostics;
using PiWeft.Ai;
using PiWeft.CodingAgent.Tools;
using Xunit;

namespace PiWeft.CodingAgent.Tests;

/// shell 输出可以任意大：内存里只能留尾部窗口，完整内容必须落到临时文件。
/// 历史缺陷是把整条命令的输出堆在 StringBuilder 里，`yes`、`cat 大文件`、`grep -r` 会吃掉几 GB 内存。
public sealed class ShellOutputTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-shell-" + Guid.NewGuid().ToString("N")[..8]);

	public ShellOutputTests() => Directory.CreateDirectory(_root);

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	[Fact]
	public async Task LongSingleLineOutputKeepsATailWindowAndPreservesTheFullOutput()
	{
		if (OperatingSystem.IsWindows())
		{
			return; // 用 POSIX 工具生成 3 MB 单行输出
		}

		var result = await ShellRunner.RunAsync(
			"head -c 3000000 /dev/zero | tr '\\0' a",
			_root,
			timeoutSeconds: 60,
			onOutput: null,
			CancellationToken.None);

		Assert.Equal(0, result.ExitCode);
		Assert.True(result.OutputTruncated);
		Assert.Equal(1, result.TotalLines);
		Assert.InRange(result.TotalBytes, 3_000_000, 3_000_100);
		Assert.NotNull(result.FullOutputPath);
		Assert.False(string.IsNullOrEmpty(result.Output));

		// 窗口只保留尾部一段，不再是整条 3 MB 字符串。
		Assert.True(result.Output.Length <= 1_200_000, $"window was {result.Output.Length} chars");
		Assert.EndsWith("a", result.Output.TrimEnd('\n'));

		var file = new FileInfo(result.FullOutputPath!);
		Assert.Equal(result.TotalBytes, file.Length);

		// 模型可见的尾部截断仍有内容，并且能指出这是第 1 行的末尾。
		var truncation = Truncate.Tail(result.Output) with
		{
			TotalLines = result.TotalLines,
			TotalBytes = result.TotalBytes,
		};
		Assert.True(truncation.Truncated);
		Assert.True(truncation.LastLinePartial);
		Assert.Equal(1, truncation.TotalLines);

		File.Delete(result.FullOutputPath!);
	}

	[Fact]
	public async Task ManyLinesKeepTheLastLineRangeAndTheFullFile()
	{
		if (OperatingSystem.IsWindows())
		{
			return;
		}

		var result = await ShellRunner.RunAsync(
			"seq 1 300000",
			_root,
			timeoutSeconds: 60,
			onOutput: null,
			CancellationToken.None);

		Assert.Equal(0, result.ExitCode);
		Assert.True(result.OutputTruncated);
		Assert.Equal(300_000, result.TotalLines);
		Assert.NotNull(result.FullOutputPath);
		Assert.True(result.Output.Length <= 1_200_000);

		// 尾部窗口的末行就是命令的最后一行。
		var lines = result.Output.TrimEnd('\n').Split('\n');
		Assert.Equal("300000", lines[^1]);

		// 完整文件末尾也是最后一行。
		var fullLines = File.ReadLines(result.FullOutputPath!).ToArray();
		Assert.Equal(300_000, fullLines.Length);
		Assert.Equal("300000", fullLines[^1]);

		File.Delete(result.FullOutputPath!);
	}

	[Fact]
	public async Task SmallOutputStaysInMemoryWithoutATempFile()
	{
		if (OperatingSystem.IsWindows())
		{
			return;
		}

		var result = await ShellRunner.RunAsync("printf 'hello\\nworld\\n'", _root, null, null, CancellationToken.None);

		Assert.Equal(0, result.ExitCode);
		Assert.False(result.OutputTruncated);
		Assert.Null(result.FullOutputPath);
		Assert.Equal("hello\nworld\n", result.Output);
		Assert.Equal(2, result.TotalLines);
	}

	[Fact]
	public async Task BinaryAndAnsiOutputIsSanitizedBeforeItReachesTheModel()
	{
		if (OperatingSystem.IsWindows())
		{
			return;
		}

		var result = await ShellRunner.RunAsync(
			"printf '\\033[31mred\\033[0m\\000\\001\\r\\n'",
			_root,
			timeoutSeconds: null,
			onOutput: null,
			CancellationToken.None);

		Assert.Equal(0, result.ExitCode);
		Assert.Equal("red\r\n", result.Output);
		Assert.Equal(5, result.TotalBytes);
	}

	[Fact]
	public async Task AnsiSequenceSplitAcrossReadsIsStillStripped()
	{
		if (OperatingSystem.IsWindows())
		{
			return;
		}

		// printf 的两次写入落在不同的管道 read 里，前半段 ESC[ 必须扣到下一块再拼起来。
		var result = await ShellRunner.RunAsync(
			"printf '\\033['; sleep 0.15; printf '31mred\\033[0m\\n'",
			_root,
			timeoutSeconds: null,
			onOutput: null,
			CancellationToken.None);

		Assert.Equal(0, result.ExitCode);
		Assert.Equal("red\n", result.Output);
	}

	[Fact]
	public async Task BackgroundChildHoldingThePipeDoesNotBlockTheTool()
	{
		if (OperatingSystem.IsWindows())
		{
			return;
		}

		var startedAt = Stopwatch.GetTimestamp();
		var result = await ShellRunner.RunAsync(
			"sleep 2 & echo hi",
			_root,
			timeoutSeconds: null,
			onOutput: null,
			CancellationToken.None);
		var elapsed = Stopwatch.GetElapsedTime(startedAt);

		Assert.Equal(0, result.ExitCode);
		Assert.Contains("hi", result.Output);
		Assert.True(elapsed < TimeSpan.FromSeconds(1.5), $"tool waited {elapsed} for a background child");
	}

	[Fact]
	public async Task TimeoutStillAppliesWhenTheShellExitedAndABackgroundProcessKeepsWriting()
	{
		if (OperatingSystem.IsWindows())
		{
			return;
		}

		var startedAt = Stopwatch.GetTimestamp();
		var result = await ShellRunner.RunAsync(
			"for i in $(seq 1 1200); do echo tick; sleep 0.01; done & echo started; exit 3",
			_root,
			timeoutSeconds: 5,
			onOutput: null,
			CancellationToken.None);
		var elapsed = Stopwatch.GetElapsedTime(startedAt);

		Assert.True(
			result.TimedOut,
			$"ShellExit={result.ShellExitCode} out={result.Output[..Math.Min(120, result.Output.Length)]}");
		Assert.True(
			result.ShellExitedBeforeTimeout,
			$"TimedOut={result.TimedOut} ShellExit={result.ShellExitCode} out={result.Output[..Math.Min(120, result.Output.Length)]}");
		Assert.Equal(3, result.ShellExitCode);
		Assert.Contains("started", result.Output);
		Assert.Contains("tick", result.Output);
		Assert.True(elapsed < TimeSpan.FromSeconds(8), $"timed out tool took {elapsed}");
	}

	[Fact]
	public void TimeoutStatusAlsoReportsTheShellExitCode()
	{
		var result = new ShellResult("out", ExitCode: null, Cancelled: false)
		{
			TimedOut = true,
			ShellExitedBeforeTimeout = true,
			ShellExitCode = 3,
			ShellExitSeconds = 0.1,
		};

		var message = BashTool.TimeoutStatus(result, timeoutSeconds: 5, text: "out");

		Assert.Contains("timed out after 5 seconds", message);
		Assert.Contains("shell exited with code 3 after 0.1 seconds", message);
		Assert.Contains("background processes", message);
	}

	[Fact]
	public void TimeoutStatusOmitsTheShellSentenceWhenTheShellWasStillRunning()
	{
		var result = new ShellResult("out", ExitCode: null, Cancelled: false) { TimedOut = true };

		var message = BashTool.TimeoutStatus(result, timeoutSeconds: null, text: "out");

		Assert.Contains("timed out after timeout seconds", message);
		Assert.DoesNotContain("shell exited", message);
	}

	[Fact]
	public void ReadFullOutputDoesNotSplitUtf8CharactersAtTheWindowEdge()
	{
		var path = Path.Combine(_root, "full.log");
		File.WriteAllText(path, new string('é', 100) + new string('x', 200));

		var full = ShellRunner.ReadFullOutput(path, 11);

		Assert.True(full.Truncated);
		Assert.DoesNotContain('\uFFFD', full.Content);
	}

	[Fact]
	public void ConvertToLlmSanitizesOldPoisonedToolResults()
	{
		var message = new ToolResultMessage
		{
			ToolCallId = "c1",
			ToolName = "bash",
			Content = [new TextContent { Text = "\u0000\u001b[31mred\u001b[0m\u001a" }],
		};

		var converted = CodingMessages.ConvertToLlm([message], CancellationToken.None);
		var result = Assert.IsType<ToolResultMessage>(Assert.Single(converted));
		var text = Assert.IsType<TextContent>(Assert.Single(result.Content));

		Assert.Equal("red", text.Text);
	}

	[Fact]
	public void ConvertToLlmSanitizesOldBashExecutionOutput()
	{
		var message = CodingMessages.BashExecution(
			command: "echo hi",
			output: "\u0000\u001b[31mout\u001b[0m\u001a",
			exitCode: 0,
			cancelled: false,
			truncated: false,
			fullOutputPath: null,
			excludeFromContext: false,
			timestamp: 1);

		var converted = CodingMessages.ConvertToLlm([message], CancellationToken.None);
		var user = Assert.IsType<UserMessage>(Assert.Single(converted));

		Assert.Equal("Ran `echo hi`\n```\nout\n```", user.Content.Text);
	}

	[Fact]
	public async Task FullOutputFileIsOnlyReadableByTheCurrentUser()
	{
		if (OperatingSystem.IsWindows())
		{
			return;
		}

		var path = ShellRunner.WriteFullOutput(new string('a', 64 * 1024));
		try
		{
			Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
		}
		finally
		{
			File.Delete(path);
		}
	}
}
