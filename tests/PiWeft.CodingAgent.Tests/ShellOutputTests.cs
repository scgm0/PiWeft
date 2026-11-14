using System.Diagnostics;
using System.Text;
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
	public async Task ShellPathAndCommandPrefixAreApplied()
	{
		if (OperatingSystem.IsWindows())
		{
			return;
		}

		var result = await ShellRunner.RunAsync(
			"echo \"$PIWEFT_PREFIX_MARKER\"",
			_root,
			timeoutSeconds: null,
			onOutput: null,
			CancellationToken.None,
			new ShellInvocation
			{
				ShellPath = "/bin/bash",
				CommandPrefix = "PIWEFT_PREFIX_MARKER=from-prefix",
			});

		Assert.Equal(0, result.ExitCode);
		Assert.Equal("from-prefix\n", result.Output);
	}

	[Fact]
	public async Task FinalSnapshotIsEmittedEvenForShortCommands()
	{
		if (OperatingSystem.IsWindows())
		{
			return;
		}

		var snapshots = new List<ShellSnapshot>();
		var result = await ShellRunner.RunAsync(
			"printf 'hello\\n'",
			_root,
			timeoutSeconds: null,
			onOutput: null,
			CancellationToken.None,
			onSnapshot: snapshots.Add);

		Assert.Equal(0, result.ExitCode);
		Assert.NotEmpty(snapshots);
		Assert.Contains(snapshots, static snapshot => snapshot.Content.Contains("hello"));
	}

	[Fact]
	public async Task UserBashStripsCarriageReturnsAndStreamsChunks()
	{
		if (OperatingSystem.IsWindows())
		{
			return;
		}

		var chunks = new List<string>();
		var result = await ShellRunner.RunAsync(
			"printf 'a\\rb\\r\\nc'",
			_root,
			timeoutSeconds: null,
			chunks.Add,
			CancellationToken.None,
			new ShellInvocation { StripCarriageReturns = true });

		Assert.Equal("ab\nc", result.Output);
		Assert.Equal("ab\nc", string.Concat(chunks));
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
	public async Task PidVariableReachesTheShellUnchanged()
	{
		if (!OperatingSystem.IsLinux())
		{
			return; // 断言依赖 setsid（Linux 有，macOS 默认没有）
		}

		// systemd-run 的 scope 模式会还原 argv 里的 $$（见 LinuxCommandScope.EscapeCommand）。
		// 会话号与 $$ 相等只在 pid 原样到达 shell 时成立：少补偿得到 "$"，多补偿得到
		// 拼起来的假 pid，ps 查不到会话。
		var result = await ShellRunner.RunAsync(
			"echo \"$$:$(ps -o sid= -p $$ | tr -d ' ')\"",
			_root,
			timeoutSeconds: null,
			onOutput: null,
			CancellationToken.None);

		var parts = result.Output.Trim().Split(':');
		Assert.Equal(2, parts.Length);
		Assert.Equal(parts[0], parts[1]);
		Assert.True(int.TryParse(parts[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var pid) && pid > 0, $"output was {result.Output}");
	}

	[Theory]
	[InlineData("echo $$", true, "echo $$$$")]
	[InlineData("a$$b$$", true, "a$$$$b$$$$")]
	[InlineData("echo $$", false, "echo $$")]
	[InlineData("echo $HOME $1 $$", true, "echo $HOME $1 $$$$")]
	public void EscapeCommandDoublesDoubleDollarsOnlyWhenSystemdCollapsesThem(
		string command,
		bool collapsesDoubleDollar,
		string expected)
	{
		Assert.Equal(expected, LinuxCommandScope.EscapeCommand(command, collapsesDoubleDollar));
	}

	[Fact]
	public async Task LeftoverBackgroundProcessIsKilledWhenTheCommandSucceeds()
	{
		if (!OperatingSystem.IsLinux())
		{
			return; // 只看 Linux：macOS 没有 cgroup，ProcessIsAlive 也没 /proc 可读
		}

		var pidFile = Path.Combine(_root, "leftover.pid");
		var result = await ShellRunner.RunAsync(
			$"sleep 60 & echo $! > '{pidFile}'; echo ok",
			_root,
			timeoutSeconds: null,
			onOutput: null,
			CancellationToken.None);

		Assert.Equal(0, result.ExitCode);
		Assert.Contains("ok", result.Output);

		var pid = int.Parse(File.ReadAllText(pidFile).Trim(), System.Globalization.CultureInfo.InvariantCulture);
		Assert.True(
			await WaitForProcessDeathAsync(pid, TimeSpan.FromSeconds(3)),
			$"background process {pid} survived a successful command");
	}

	[Fact]
	public async Task ScopeKillsAProcessThatLeftTheProcessGroup()
	{
		if (!LinuxCommandScope.IsAvailable)
		{
			return; // 没有 user systemd 时只能退回进程组，进程自己 setsid 就跑掉了
		}

		var pidFile = Path.Combine(_root, "escaped.pid");
		var result = await ShellRunner.RunAsync(
			$"setsid bash -c 'echo $$ > {pidFile}; exec sleep 60' & " +
			$"for i in $(seq 1 250); do [ -s {pidFile} ] && break; sleep 0.02; done; echo started",
			_root,
			timeoutSeconds: null,
			onOutput: null,
			CancellationToken.None);

		Assert.Contains("started", result.Output);
		Assert.True(File.Exists(pidFile), "the escaped process never wrote its pid");

		var pid = int.Parse(File.ReadAllText(pidFile).Trim(), System.Globalization.CultureInfo.InvariantCulture);
		Assert.True(
			await WaitForProcessDeathAsync(pid, TimeSpan.FromSeconds(3)),
			$"process {pid} escaped the scope");
	}

	[Fact]
	public async Task ConcurrentCommandsKeepTheirOutputApart()
	{
		if (OperatingSystem.IsWindows())
		{
			return; // 依赖 POSIX sh 的重定向
		}

		var calls = Enumerable
			.Range(0, 6)
			.Select(index => ShellRunner.RunAsync(
				$"sleep 0.1; echo out{index}; echo err{index} >&2; exit {index}",
				_root,
				timeoutSeconds: 30,
				onOutput: null,
				CancellationToken.None))
			.ToArray();
		var results = await Task.WhenAll(calls);

		for (var index = 0; index < results.Length; index++)
		{
			Assert.Equal(index, results[index].ExitCode);
			Assert.Contains($"out{index}", results[index].Output);
			Assert.Contains($"err{index}", results[index].Output);
			Assert.DoesNotContain($"out{(index + 1) % results.Length}", results[index].Output);
		}
	}

	[Fact]
	public async Task CancellationKillsTheContainersLeftovers()
	{
		if (!LinuxCommandScope.IsAvailable)
		{
			return; // 无 cgroup 时 setsid 出去的后代不在进程组里，本就杀不到
		}

		var pidFile = Path.Combine(_root, "cancelled.pid");
		using var cancellation = new CancellationTokenSource();
		var run = ShellRunner.RunAsync(
			$"setsid bash -c 'echo $$ > {pidFile}; exec sleep 60' & " +
			$"for i in $(seq 1 250); do [ -s {pidFile} ] && break; sleep 0.02; done; exec sleep 60",
			_root,
			timeoutSeconds: null,
			onOutput: null,
			cancellation.Token);

		var deadline = DateTime.UtcNow.AddSeconds(5);
		while (DateTime.UtcNow < deadline && !File.Exists(pidFile))
		{
			await Task.Delay(20);
		}

		Assert.True(File.Exists(pidFile), "the background process never wrote its pid");
		var pid = int.Parse(
			File.ReadAllText(pidFile).Trim(),
			System.Globalization.CultureInfo.InvariantCulture);
		await cancellation.CancelAsync();
		var result = await run;

		Assert.True(result.Cancelled);
		Assert.True(
			await WaitForProcessDeathAsync(pid, TimeSpan.FromSeconds(3)),
			$"process {pid} survived cancellation");
	}

	[Fact]
	public async Task MultiByteOutputStaysValidAcrossTailTrimming()
	{
		if (OperatingSystem.IsWindows())
		{
			return; // cat 与多字节输出
		}

		var path = Path.Combine(_root, "multi-byte.txt");
		// 长度不是单元长度的整数倍：裁剪点会落在多字节字符中间，要验跳过续字节的切割。
		var content = "x" + string.Concat(Enumerable.Repeat("国际é", 60_000));
		File.WriteAllText(path, content);

		var result = await ShellRunner.RunAsync($"cat '{path}'", _root, 60, null, CancellationToken.None);

		Assert.Equal(0, result.ExitCode);
		Assert.True(result.OutputTruncated);
		Assert.Equal(Encoding.UTF8.GetByteCount(content), result.TotalBytes);
		Assert.DoesNotContain('\uFFFD', result.Output);
		Assert.EndsWith("国际é", result.Output);
	}

	[Fact]
	public async Task MultiByteCharacterSplitAcrossStreamsStaysIntact()
	{
		if (OperatingSystem.IsWindows())
		{
			return; // 用 POSIX printf 写出半个 UTF-8 序列
		}

		// C3 是 é 的首字节，A9 是续字节。首块只有一个不完整字符时 decoder 要把尾字节留到下一块；
		// stderr 插在中间时，两条流各用一个 decoder，不能把 stdout 的半个字符接上。
		var result = await ShellRunner.RunAsync(
			"printf '\\303'; sleep 0.2; printf 'stderr' >&2; sleep 0.2; printf '\\251\\n'",
			_root,
			timeoutSeconds: 30,
			onOutput: null,
			CancellationToken.None);

		Assert.Equal(0, result.ExitCode);
		Assert.DoesNotContain('\uFFFD', result.Output);
		Assert.Contains("stderr", result.Output);
		Assert.Contains("é", result.Output);
	}

	private static async Task<bool> WaitForProcessDeathAsync(int pid, TimeSpan timeout)
	{
		var deadline = DateTime.UtcNow + timeout;
		while (DateTime.UtcNow < deadline && ProcessIsAlive(pid))
		{
			await Task.Delay(25);
		}

		return !ProcessIsAlive(pid);
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
	public async Task TimeoutKillsBackgroundProcessWhenTheShellExitsFirst()
	{
		if (!OperatingSystem.IsLinux())
		{
			return; // 需要 setsid 与 /proc
		}

		var pidFile = Path.Combine(_root, "background.pid");
		var result = await ShellRunner.RunAsync(
			$"for i in $(seq 1 1200); do echo tick; sleep 0.01; done & echo $! > '{pidFile}'; exit 0",
			_root,
			timeoutSeconds: 2,
			onOutput: null,
			CancellationToken.None);

		Assert.True(result.TimedOut);
		Assert.True(result.ShellExitedBeforeTimeout);
		Assert.Equal(0, result.ShellExitCode);

		var pid = int.Parse(File.ReadAllText(pidFile).Trim(), System.Globalization.CultureInfo.InvariantCulture);
		var deadline = DateTime.UtcNow.AddSeconds(2);
		while (DateTime.UtcNow < deadline && ProcessIsAlive(pid))
		{
			await Task.Delay(25);
		}

		Assert.False(ProcessIsAlive(pid), $"background process {pid} survived the timeout");
	}

	private static bool ProcessIsAlive(int pid)
	{
		try
		{
			var stat = File.ReadAllText($"/proc/{pid}/stat");
			var close = stat.LastIndexOf(')');
			// 僵尸进程已经退出，只等 init 回收；测试把它视为死亡。
			return close < 0 || stat.Length <= close + 2 || stat[close + 2] != 'Z';
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return false;
		}
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
