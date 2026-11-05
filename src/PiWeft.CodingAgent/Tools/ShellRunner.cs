using System.Diagnostics;
using System.Globalization;
using System.Text;
using PiWeft.CodingAgent.Session;

namespace PiWeft.CodingAgent.Tools;

public sealed record ShellResult(string Output, int? ExitCode, bool Cancelled)
{
	/// 超长命令的完整输出（落到临时文件）。未超过内存窗口时为 <c>null</c>，Output 本身就是全文。
	public string? FullOutputPath { get; init; }

	/// 整条命令实际输出的字节数与行数（含已滚出内存窗口的部分）。
	public long TotalBytes { get; init; }

	public int TotalLines { get; init; }

	/// Output 只是尾部窗口：完整内容见 FullOutputPath。
	/// 命令输出可以任意大（`yes`、`find /`、`grep -r`），不能拿全量字符串赌内存。
	public bool OutputTruncated { get; init; }

	/// 命令因超时被终止。调用方应带着已捕获的输出报错。
	public bool TimedOut { get; init; }

	/// 超时发生时 shell 本身已经先退出（后台进程仍占着管道）。
	/// 此时输出末尾属于后台进程，状态行需要同时报告 shell 的真实退出码。
	public bool ShellExitedBeforeTimeout { get; init; }

	public int? ShellExitCode { get; init; }

	/// shell 在超时前多久退出（秒，已按 0.1 秒取整）。
	public double? ShellExitSeconds { get; init; }
}

/// 执行中的输出快照。用于 tool_execution_update，不包含最终的状态行。
public sealed record ShellSnapshot(
	string Content,
	long TotalBytes,
	int TotalLines,
	bool OutputTruncated,
	string? FullOutputPath);

/// shell 调用的附加配置：显式 shell 路径、命令前缀与注入的环境变量。
/// 对应 pi 的 <c>shellPath</c>、<c>shellCommandPrefix</c> 与 <c>exposeSessionEnvironment</c>。
public sealed record ShellInvocation
{
	public string? ShellPath { get; init; }

	public string? CommandPrefix { get; init; }

	/// 注入子进程的环境变量；值为 null 表示从继承的环境中删除该变量。
	public IReadOnlyDictionary<string, string?>? Environment { get; init; }

	public bool PowerShell { get; init; }

	public string TempFilePrefix { get; init; } = "pi-bash";
}

public sealed record ShellCommandInfo(string FileName, string[] Arguments, bool CommandFromStdin);

public static class ShellRunner
{
	/// pi 的 MAX_TIMEOUT_MS。超过这个值不再接受，避免 TimeSpan.CancelAfter 溢出。
	private const double MaxTimeoutSeconds = 2_147_483.647;

	public static readonly string[] SessionEnvironmentNames =
	[
		"PI_SESSION_ID",
		"PI_SESSION_FILE",
		"PI_PROVIDER",
		"PI_MODEL",
		"PI_REASONING_LEVEL",
	];

	private const int UpdateThrottleMs = 100;

	/// 在 <paramref name="cwd"/> 执行 <paramref name="command"/>，按到达顺序合并 stdout 与 stderr。
	/// 取消/超时会杀掉整棵进程树，并把已经收到的输出带回结果。
	public static async Task<ShellResult> RunAsync(
		string command,
		string cwd,
		double? timeoutSeconds,
		Action<string>? onOutput,
		CancellationToken cancellationToken,
		ShellInvocation? invocation = null,
		Action<ShellSnapshot>? onSnapshot = null)
	{
		if (timeoutSeconds is { } seconds && (!double.IsFinite(seconds) || seconds <= 0))
		{
			throw new InvalidOperationException("Invalid timeout: must be a finite number of seconds");
		}

		if (timeoutSeconds is > MaxTimeoutSeconds)
		{
			throw new InvalidOperationException(
				$"Invalid timeout: maximum is {MaxTimeoutSeconds.ToString("0.###", CultureInfo.InvariantCulture)} seconds");
		}

		if (!Directory.Exists(cwd))
		{
			throw new InvalidOperationException(
				$"Working directory does not exist: {cwd}\nCannot execute {(invocation?.PowerShell == true ? "PowerShell" : "bash")} commands.");
		}

		var prefix = invocation?.CommandPrefix;
		if (prefix is { Length: > 0 })
		{
			// 与 pi 相同：前缀单独成行，用户的命令仍按原样执行。
			command = $"{prefix}\n{command}";
		}

		if (invocation?.PowerShell == true)
		{
			// pi 的 PowerShell 工具在命令前注入 UTF-8 输出编码，避免中文/emoji 走 OEM 代码页。
			command = "try { [Console]::OutputEncoding=[System.Text.Encoding]::UTF8 } catch {}\n" + command;
		}

		var shell = invocation?.PowerShell == true
			? PowerShellCommand()
			: ShellCommand(invocation?.ShellPath);
		var startInfo = new ProcessStartInfo
		{
			FileName = shell.FileName,
			WorkingDirectory = cwd,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			RedirectStandardInput = true,
			UseShellExecute = false,
		};

		foreach (var argument in shell.Arguments)
		{
			startInfo.ArgumentList.Add(argument);
		}

		if (!shell.CommandFromStdin)
		{
			startInfo.ArgumentList.Add(command);
		}

		foreach (var name in SessionEnvironmentNames)
		{
			startInfo.Environment.Remove(name);
		}

		if (invocation?.Environment is { Count: > 0 } environment)
		{
			foreach (var (name, value) in environment)
			{
				if (value is null)
				{
					startInfo.Environment.Remove(name);
				}
				else
				{
					startInfo.Environment[name] = value;
				}
			}
		}

		AddAgentBinToPath(startInfo);

		using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		if (timeoutSeconds is { } timeout)
		{
			cancellation.CancelAfter(TimeSpan.FromSeconds(timeout));
		}

		using var capture = new OutputCapture(invocation?.TempFilePrefix ?? "pi-bash");
		using var process = new Process { StartInfo = startInfo };
		if (!process.Start())
		{
			throw new InvalidOperationException($"Failed to start {shell.FileName}");
		}

		using var readCancellation = new CancellationTokenSource();
		var updateGate = new object();
		var stdout = PumpAsync(process.StandardOutput.BaseStream, capture, onOutput, onSnapshot, updateGate, readCancellation.Token);
		var stderr = PumpAsync(process.StandardError.BaseStream, capture, onOutput, onSnapshot, updateGate, readCancellation.Token);
		var pumps = Task.WhenAll(stdout, stderr);
		var startedAt = Stopwatch.GetTimestamp();

		try
		{
			try
			{
				if (shell.CommandFromStdin)
				{
					await process.StandardInput.WriteAsync(command.AsMemory(), CancellationToken.None).ConfigureAwait(false);
				}

				process.StandardInput.Close();
			}
			catch (Exception exception) when (exception is IOException or ObjectDisposedException)
			{
				// 命令可能已经退出，管道读端关了。与 pi 一样忽略 stdin 的写失败。
			}

			// WaitForExitAsync 只等 shell 自己退出，不等它留给后台进程的管道。
			await process.WaitForExitAsync(cancellation.Token).ConfigureAwait(false);

			// shell 退出后仍可能有后代在写（`npm run dev &`）。像 pi 的 waitForChildProcess
			// 一样按管道空闲收尾，但整个收尾仍受 timeout/abort 约束：否则 shell 先退出时，
			// 超时会被 2 秒固定 drain 拖成「命令成功」，后台进程也会被留在原地。
			await DrainAfterExitAsync(pumps, capture, readCancellation, cancellation.Token, timeoutSeconds is not null)
				.ConfigureAwait(false);

			return capture.Complete(process.ExitCode, cancelled: false, timedOut: false);
		}
		catch (OperationCanceledException)
		{
			var timedOut = !cancellationToken.IsCancellationRequested;
			var shellExit = DescribeShellExit(process, startedAt);
			TryKill(process);
			await DrainAfterCancellationAsync(pumps, readCancellation).ConfigureAwait(false);

			var result = capture.Complete(exitCode: null, cancelled: !timedOut, timedOut: timedOut);
			return shellExit is { } exit
				? result with
				{
					ShellExitedBeforeTimeout = true,
					ShellExitCode = exit.ExitCode,
					ShellExitSeconds = exit.Seconds,
				}
				: result;
		}
		catch
		{
			TryKill(process);
			readCancellation.Cancel();
			throw;
		}
	}

	/// <summary>shell 退出后等待两个管道排空的时间参数。</summary>
	private const int ExitIdleGraceMs = 100;

	/// <summary>后台进程一直有输出时，不无限等待；到上限就停止读取并用已有内容收尾。</summary>
	private const int ExitHardCapMs = 2_000;

	private const int PumpShutdownGraceMs = 2_000;

	private static async Task DrainAfterExitAsync(
		Task pumps,
		OutputCapture capture,
		CancellationTokenSource readCancellation,
		CancellationToken cancellationToken,
		bool hasTimeout)
	{
		var startedAt = Stopwatch.GetTimestamp();
		// pi 的 waitForChildProcess 在 exit 时重新启动 100ms 空闲计时器：
		// 命令静默很久后退出、后台进程紧接着写第一段输出时，不能因为「最后活动在 5 秒前」就立刻收尾。
		var idleSince = Math.Max(capture.LastActivityTicks, startedAt);
		while (!pumps.IsCompleted)
		{
			if (Stopwatch.GetElapsedTime(idleSince).TotalMilliseconds >= ExitIdleGraceMs)
			{
				break;
			}

			// 没有 timeout 时不能让一个永远写输出的后台进程把会话挂死；有 timeout 时
			// 交给 cancellation 收尾 —— 2 秒硬上限会在 timeout 之前把命令误报成成功。
			if (!hasTimeout && Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds >= ExitHardCapMs)
			{
				break;
			}

			// timeout/abort 必须能在后台进程仍写输出时打断收尾。
			await Task.WhenAny(pumps, Task.Delay(25, cancellationToken)).ConfigureAwait(false);
			cancellationToken.ThrowIfCancellationRequested();
			idleSince = Math.Max(idleSince, capture.LastActivityTicks);
		}

		readCancellation.Cancel();
		await AwaitPumpsAsync(pumps).ConfigureAwait(false);
	}

	private static async Task DrainAfterCancellationAsync(Task pumps, CancellationTokenSource readCancellation)
	{
		// 进程树刚被杀掉时，管道里可能还有最后一小段输出；只给很短的排空窗口，
		// 然后主动停止读取。被后台进程继承的管道不会因为 shell 退出而关闭，
		// 等它自然 EOF 会把 timeout 拖成 timeout+2s。
		await Task.WhenAny(pumps, Task.Delay(ExitIdleGraceMs)).ConfigureAwait(false);
		readCancellation.Cancel();
		await AwaitPumpsAsync(pumps).ConfigureAwait(false);
	}

	private static async Task AwaitPumpsAsync(Task pumps)
	{
		var completed = await Task.WhenAny(pumps, Task.Delay(PumpShutdownGraceMs)).ConfigureAwait(false);
		if (completed != pumps)
		{
			// 读端不响应取消时也不能把工具挂死；流最后由 using 作用域释放。
			_ = pumps.ContinueWith(
				static task => _ = task.Exception,
				TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
			return;
		}

		try
		{
			await pumps.ConfigureAwait(false);
		}
		catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException or IOException)
		{
		}
	}

	/// <summary>取消发生时 shell 是否已经退出；超时状态行需要同时报告这个事实。</summary>
	private static (int ExitCode, double Seconds)? DescribeShellExit(Process process, long startedAt)
	{
		try
		{
			if (!process.HasExited)
			{
				return null;
			}

			return (process.ExitCode, Math.Round(Stopwatch.GetElapsedTime(startedAt).TotalSeconds * 10) / 10);
		}
		catch (InvalidOperationException)
		{
			return null;
		}
	}

	private static async Task PumpAsync(
		Stream stream,
		OutputCapture capture,
		Action<string>? onOutput,
		Action<ShellSnapshot>? onSnapshot,
		object updateGate,
		CancellationToken cancellationToken)
	{
		var buffer = new byte[8192];
		var lastSnapshot = Stopwatch.GetTimestamp();
		try
		{
			while (!cancellationToken.IsCancellationRequested)
			{
				var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
				if (read <= 0)
				{
					break;
				}

				// onOutput 是行回调，必须在同一条锁里投递，否则 stdout/stderr 的行序会乱。
				lock (updateGate)
				{
					capture.Append(buffer.AsSpan(0, read), onOutput);
					if (onSnapshot is not null &&
						Stopwatch.GetElapsedTime(lastSnapshot).TotalMilliseconds >= UpdateThrottleMs)
					{
						lastSnapshot = Stopwatch.GetTimestamp();
						onSnapshot(capture.Snapshot());
					}
				}
			}
		}
		catch (OperationCanceledException)
		{
			// 收尾阶段主动停止读取，或进程被杀后管道未关闭。
		}
		catch (ObjectDisposedException)
		{
		}
		catch (IOException)
		{
		}
	}

	/// 读取完整输出文件的头尾窗口，供 codemode structuredContent 使用。
	public sealed record FullOutputRead(string Content, bool Truncated);

	public static FullOutputRead ReadFullOutput(string path, long maxBytes)
	{
		using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
		if (stream.Length <= maxBytes)
		{
			using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
			return new FullOutputRead(reader.ReadToEnd(), Truncated: false);
		}

		var headBytes = (int)Math.Min(int.MaxValue, Math.Max(0, maxBytes / 2));
		var tailBytes = (int)Math.Min(int.MaxValue, Math.Max(0, maxBytes - headBytes));
		var head = new byte[headBytes];
		var tail = new byte[tailBytes];
		stream.ReadExactly(head);
		stream.Seek(-tailBytes, SeekOrigin.End);
		stream.ReadExactly(tail);
		var headRead = head.Length;
		var tailRead = tail.Length;

		// 头部可能停在 UTF-8 多字节序列中间：不 flush 的 Decoder 会扣住未完成的码点，
		// 而不是像 GetString 那样留下一个替换字符。
		var headDecoder = Encoding.UTF8.GetDecoder();
		var headChars = new char[headDecoder.GetCharCount(head, 0, headRead)];
		headDecoder.GetChars(head, 0, headRead, headChars, 0);
		var headText = new string(headChars);

		// 尾部可能从 UTF-8 续字节中间开始；跳过它，避免产生一个替换字符。
		var tailStart = 0;
		while (tailStart < tailRead && (tail[tailStart] & 0xC0) == 0x80)
		{
			tailStart++;
		}

		var tailText = Encoding.UTF8.GetString(tail, tailStart, tailRead - tailStart);
		var omitted = stream.Length - headRead - (tailRead - tailStart);
		return new FullOutputRead(
			$"{headText}\n\n[... {omitted} bytes omitted ...]\n\n{tailText}",
			Truncated: true);
	}

	public static string WriteFullOutput(string output, string prefix = "pi-bash")
	{
		var path = Path.Combine(
			Path.GetTempPath(),
			$"{prefix}-{Guid.NewGuid().ToString("N")[..12]}.log");

		using var stream = CreatePrivateFile(path);
		var bytes = Encoding.UTF8.GetBytes(output);
		stream.Write(bytes, 0, bytes.Length);
		return path;
	}

	/// <summary>
	/// 完整输出可能包含命令打印的 token、路径或私密数据；Unix 上只给当前用户读写（pi 的 0600 语义）。
	/// </summary>
	internal static FileStream CreatePrivateFile(string path)
	{
		var options = new FileStreamOptions
		{
			Mode = FileMode.CreateNew,
			Access = FileAccess.Write,
			Share = FileShare.Read,
		};

		if (!OperatingSystem.IsWindows())
		{
			options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
		}

		return new FileStream(path, options);
	}

	public static ShellCommandInfo PowerShellCommand()
	{
		if (!OperatingSystem.IsWindows())
		{
			throw new InvalidOperationException("The powershell tool is only available on Windows.");
		}

		var shell = FindOnPath("pwsh.exe");
		if (shell is null &&
			Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) is { Length: > 0 } programFiles)
		{
			var pwsh7 = Path.Combine(programFiles, "PowerShell", "7", "pwsh.exe");
			if (File.Exists(pwsh7))
			{
				shell = pwsh7;
			}
		}

		shell ??= FindOnPath("powershell.exe");
		if (shell is null &&
			Environment.GetEnvironmentVariable("SystemRoot") is { Length: > 0 } systemRoot)
		{
			var windowsPowerShell = Path.Combine(systemRoot, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
			if (File.Exists(windowsPowerShell))
			{
				shell = windowsPowerShell;
			}
		}

		if (shell is null)
		{
			throw new InvalidOperationException(
				"No PowerShell executable found. Install PowerShell or add powershell.exe/pwsh.exe to PATH.");
		}

		return new ShellCommandInfo(
			shell,
			["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command"],
			CommandFromStdin: false);
	}

	public static ShellCommandInfo ShellCommand(string? shellPath = null)
	{
		if (OperatingSystem.IsWindows())
		{
			if (shellPath is { Length: > 0 })
			{
				if (!File.Exists(shellPath))
				{
					throw new InvalidOperationException($"Custom shell path not found: {shellPath}");
				}

				return BashCommandInfo(shellPath);
			}

			var candidates = new List<string>();
			if (Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) is { Length: > 0 } programFiles)
			{
				candidates.Add(Path.Combine(programFiles, "Git", "bin", "bash.exe"));
			}

			if (Environment.GetEnvironmentVariable("ProgramFiles(x86)") is { Length: > 0 } programFilesX86)
			{
				candidates.Add(Path.Combine(programFilesX86, "Git", "bin", "bash.exe"));
			}

			foreach (var candidate in candidates)
			{
				if (File.Exists(candidate))
				{
					return BashCommandInfo(candidate);
				}
			}

			var bashOnPath = FindOnPath("bash.exe");
			if (bashOnPath is not null)
			{
				return BashCommandInfo(bashOnPath);
			}

			throw new InvalidOperationException(
				$"No bash shell found. Options:\n" +
				$"  1. Install Git for Windows: https://git-scm.com/download/win\n" +
				$"  2. Add your bash to PATH (Cygwin, MSYS2, etc.)\n" +
				$"  3. Set shellPath in settings.json\n\n" +
				$"Searched Git Bash in:\n{string.Join("\n", candidates.Select(static path => $"  {path}"))}");
		}

		if (shellPath is { Length: > 0 })
		{
			if (!File.Exists(shellPath))
			{
				throw new InvalidOperationException($"Custom shell path not found: {shellPath}");
			}

			return new ShellCommandInfo(shellPath, ["-c"], CommandFromStdin: false);
		}

		if (File.Exists("/bin/bash"))
		{
			return new ShellCommandInfo("/bin/bash", ["-c"], CommandFromStdin: false);
		}

		var bash = FindOnPath("bash");
		return bash is not null
			? new ShellCommandInfo(bash, ["-c"], CommandFromStdin: false)
			: new ShellCommandInfo("sh", ["-c"], CommandFromStdin: false);
	}

	private static ShellCommandInfo BashCommandInfo(string shell)
	{
		// 旧版 WSL 的 System32\bash.exe 不把 -c 当命令参数，pi 通过 stdin 传脚本。
		var normalized = Path.GetFullPath(shell).ToLowerInvariant();
		var systemRoot = Environment.GetEnvironmentVariable("SystemRoot");
		var legacyWsl = systemRoot is { Length: > 0 }
			? IsLegacyWslPath(normalized, systemRoot)
			: normalized.EndsWith(@"\windows\system32\bash.exe", StringComparison.Ordinal) ||
				normalized.EndsWith(@"\windows\sysnative\bash.exe", StringComparison.Ordinal);

		return legacyWsl
			? new ShellCommandInfo(shell, ["-s"], CommandFromStdin: true)
			: new ShellCommandInfo(shell, ["-c"], CommandFromStdin: false);
	}

	private static bool IsLegacyWslPath(string normalizedPath, string systemRoot)
	{
		if (!Path.IsPathFullyQualified(systemRoot))
		{
			return false;
		}

		return Path.GetFullPath(Path.Combine(systemRoot, "System32", "bash.exe")).ToLowerInvariant() == normalizedPath ||
			Path.GetFullPath(Path.Combine(systemRoot, "Sysnative", "bash.exe")).ToLowerInvariant() == normalizedPath;
	}

	private static void AddAgentBinToPath(ProcessStartInfo startInfo)
	{
		var binDir = Path.Combine(SessionPaths.GetAgentDir(), "bin");
		var pathKey = startInfo.Environment.Keys.FirstOrDefault(
			static key => string.Equals(key, "PATH", StringComparison.OrdinalIgnoreCase)) ?? "PATH";
		var current = startInfo.Environment.TryGetValue(pathKey, out var value) ? value ?? string.Empty : string.Empty;
		var entries = current.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
		if (entries.Any(entry => string.Equals(entry, binDir, StringComparison.OrdinalIgnoreCase)))
		{
			return;
		}

		startInfo.Environment[pathKey] = string.Join(Path.PathSeparator, [binDir, .. entries]);
	}

	private static void TryKill(Process process)
	{
		try
		{
			if (!process.HasExited)
			{
				process.Kill(entireProcessTree: true);
			}
		}
		catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException or
			System.ComponentModel.Win32Exception or UnauthorizedAccessException or AggregateException)
		{
			// 进程刚好退出、没有权限杀某个后代，或平台不支持整树杀进程。
			// 清理是尽力而为：不能因为杀不掉后台进程把已经拿到的输出丢掉。
		}
	}

	private static string? FindOnPath(string executable)
	{
		var path = Environment.GetEnvironmentVariable("PATH");
		if (string.IsNullOrEmpty(path))
		{
			return null;
		}

		foreach (var directory in path.Split(Path.PathSeparator))
		{
			if (directory.Length == 0)
			{
				continue;
			}

			var candidate = Path.Combine(directory, executable);
			if (File.Exists(candidate))
			{
				return candidate;
			}
		}

		return null;
	}

	/// 只保留尾部窗口的累计器。超过窗口时先确保此前的内容已经写进临时文件，
	/// 再按行边界裁掉前半段；之后的输出同时写文件与窗口。
	/// 所有进入累计器的文本都先经过 <see cref="ShellTextSanitizer"/>，
	/// 这样模型上下文、会话文件和 UI 都不会收到 NUL/ANSI 这类会破坏协议或渲染的内容。
	private sealed class OutputCapture : IDisposable
	{
		/// pi 的 OutputAccumulator 用 2 * maxBytes 作为滚动窗口；单行超长时保留尾部片段。
		private const long MaxBytes = Truncate.DefaultMaxBytes;
		private const int MaxLines = Truncate.DefaultMaxLines;
		private const long MaxRollingBytes = MaxBytes * 2;

		private readonly string _prefix;
		private readonly Lock _gate = new();
		private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
		private readonly StringBuilder _tail = new();
		private readonly StringBuilder _line = new();
		private readonly List<byte[]> _pendingBytes = [];
		private FileStream? _file;
		private string? _path;
		private bool _fileUnavailable;
		private bool _closed;
		private bool _tailTruncated;
		private bool _tailStartsAtLineBoundary = true;
		private long _tailBytes;
		private long _totalTextBytes;
		private int _completedLines;
		private int _totalLines;
		private bool _hasOpenLine;
		private string _pendingAnsi = string.Empty;
		private long _lastActivityTicks = Stopwatch.GetTimestamp();
		private Action<string>? _onOutput;

		public OutputCapture(string prefix) => _prefix = prefix;

		/// <summary>最近一次收到管道数据的时间戳。shell 退出后用它判断后台进程是否还在写。</summary>
		public long LastActivityTicks => Interlocked.Read(ref _lastActivityTicks);

		public void Append(ReadOnlySpan<byte> data, Action<string>? onOutput)
		{
			if (data.Length == 0)
			{
				return;
			}

			lock (_gate)
			{
				if (_closed)
				{
					return;
				}

				if (onOutput is not null)
				{
					_onOutput = onOutput;
				}

				Interlocked.Exchange(ref _lastActivityTicks, Stopwatch.GetTimestamp());

				var text = DecodeAndSanitizeLocked(data.ToArray());
				if (text.Length > 0)
				{
					AppendTextLocked(text, onOutput);
				}

				if (ShouldUseTempFile())
				{
					EnsureFileLocked();
				}
			}
		}

		public ShellSnapshot Snapshot()
		{
			lock (_gate)
			{
				var content = SnapshotTextLocked();
				return new ShellSnapshot(
					content,
					_totalTextBytes,
					_totalLines,
					_tailTruncated,
					_file is not null && !_fileUnavailable ? _path : null);
			}
		}

		public ShellResult Complete(int? exitCode, bool cancelled, bool timedOut)
		{
			lock (_gate)
			{
				if (!_closed)
				{
					FlushDecoderLocked();
					FlushLineLocked();
					if (ShouldUseTempFile())
					{
						EnsureFileLocked();
					}
				}

				_closed = true;
				_file?.Dispose();
				_file = null;

				return new ShellResult(SnapshotTextLocked(), exitCode, cancelled)
				{
					FullOutputPath = !_fileUnavailable ? _path : null,
					TotalBytes = _totalTextBytes,
					TotalLines = _totalLines,
					OutputTruncated = _tailTruncated,
					TimedOut = timedOut,
				};
			}
		}

		private string SnapshotTextLocked()
		{
			var content = _tail.ToString();
			if (!_tailStartsAtLineBoundary)
			{
				var newline = content.IndexOf('\n');
				if (newline >= 0)
				{
					content = content[(newline + 1)..];
				}
			}

			return content;
		}

		private string DecodeAndSanitizeLocked(byte[] chunk)
		{
			var charCount = _decoder.GetCharCount(chunk, 0, chunk.Length);
			if (charCount == 0)
			{
				return string.Empty;
			}

			var chars = new char[charCount];
			_decoder.GetChars(chunk, 0, chunk.Length, chars, 0);
			return SanitizeWithPendingAnsiLocked(new string(chars));
		}

		private string SanitizeWithPendingAnsiLocked(string text)
		{
			if (text.Length == 0 && _pendingAnsi.Length == 0)
			{
				return string.Empty;
			}

			var combined = _pendingAnsi.Length == 0 ? text : _pendingAnsi + text;
			var (complete, pending) = ShellTextSanitizer.SplitIncompleteAnsiSuffix(combined);
			_pendingAnsi = pending;
			return ShellTextSanitizer.Sanitize(complete);
		}

		private void FlushDecoderLocked()
		{
			var chars = new char[4];
			var count = _decoder.GetChars(Array.Empty<byte>(), 0, 0, chars, 0, flush: true);
			var flushed = count > 0 ? new string(chars, 0, count) : string.Empty;
			var text = SanitizeWithPendingAnsiLocked(flushed);
			if (text.Length > 0)
			{
				AppendTextLocked(text, _onOutput);
			}

			// 输出在半个转义序列里结束：ESC 本身会被控制字符清洗掉，余下可见字符保留。
			if (_pendingAnsi.Length > 0)
			{
				var rest = ShellTextSanitizer.Sanitize(_pendingAnsi);
				_pendingAnsi = string.Empty;
				if (rest.Length > 0)
				{
					AppendTextLocked(rest, _onOutput);
				}
			}
		}

		private void FlushLineLocked()
		{
			if (_line.Length == 0)
			{
				return;
			}

				var line = _line.ToString();
			_line.Clear();
			_onOutput?.Invoke(line.EndsWith('\r') ? line[..^1] : line);
		}

		private void AppendTextLocked(string text, Action<string>? onOutput)
		{
			if (text.Length == 0)
			{
				return;
			}

			var bytes = Encoding.UTF8.GetBytes(text);
			_totalTextBytes += bytes.Length;
			if (_file is not null)
			{
				WriteToFileLocked(bytes);
			}
			else if (!_fileUnavailable)
			{
				// 文件不可写时只保留尾部窗口；继续攒 bytes 会把内存重新拖爆。
				_pendingBytes.Add(bytes);
			}

			_tail.Append(text);
			_tailBytes += bytes.Length;

			var newlines = 0;
			var lastNewline = -1;
			for (var index = 0; index < text.Length; index++)
			{
				if (text[index] == '\n')
				{
					newlines++;
					lastNewline = index;
				}
			}

			if (newlines == 0)
			{
				_hasOpenLine = true;
			}
			else
			{
				_completedLines += newlines;
				_hasOpenLine = lastNewline + 1 < text.Length;
			}

			_totalLines = _completedLines + (_hasOpenLine ? 1 : 0);

			if (_tailBytes > MaxRollingBytes * 2)
			{
				TrimTailLocked();
			}

			// 行回调只在同一条锁里投递，stdout/stderr 的行序不会被线程池打乱。
			if (onOutput is null)
			{
				return;
			}

			// 一次 chunk 可能有几千行；按段截取，避免每行 Remove(0, n) 造成二次方拷贝。
			var segmentStart = 0;
			for (var index = 0; index < text.Length; index++)
			{
				if (text[index] != '\n')
				{
					continue;
				}

				string line;
				if (_line.Length == 0)
				{
					line = text[segmentStart..index];
				}
				else
				{
					_line.Append(text, segmentStart, index - segmentStart);
					line = _line.ToString();
					_line.Clear();
				}

				segmentStart = index + 1;
				// 只给行回调去掉 CRLF 的尾巴；模型上下文保留原始 \r（与 pi 的 bash 工具一致）。
				onOutput.Invoke(line.EndsWith('\r') ? line[..^1] : line);
			}

			if (segmentStart < text.Length)
			{
				_line.Append(text, segmentStart, text.Length - segmentStart);
			}
		}

		private void TrimTailLocked()
		{
			var bytes = Encoding.UTF8.GetBytes(_tail.ToString());
			if (bytes.LongLength <= MaxRollingBytes)
			{
				_tailBytes = bytes.LongLength;
				return;
			}

			var start = (int)(bytes.LongLength - MaxRollingBytes);
			while (start < bytes.Length && (bytes[start] & 0xC0) == 0x80)
			{
				start++;
			}

			_tailStartsAtLineBoundary = start == 0 ? _tailStartsAtLineBoundary : bytes[start - 1] == (byte)'\n';
			var text = Encoding.UTF8.GetString(bytes, start, bytes.Length - start);
			_tail.Clear().Append(text);
			_tailBytes = Encoding.UTF8.GetByteCount(text);
			_tailTruncated = true;
		}

		private bool ShouldUseTempFile() => _totalTextBytes > MaxBytes || _totalLines > MaxLines;

		private void EnsureFileLocked()
		{
			if (_file is not null || _fileUnavailable)
			{
				return;
			}

			try
			{
				_path = Path.Combine(
					Path.GetTempPath(),
					$"{_prefix}-{Guid.NewGuid().ToString("N")[..12]}.log");
				_file = CreatePrivateFile(_path);
				foreach (var chunk in _pendingBytes)
				{
					_file.Write(chunk, 0, chunk.Length);
				}

				_pendingBytes.Clear();
				_file.Flush();
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				// 临时目录不可写：退化成只有尾部窗口，不让 shell 因为落盘失败而失败。
				_pendingBytes.Clear();
				_fileUnavailable = true;
				_file?.Dispose();
				_file = null;
				if (_path is not null)
				{
					try
					{
						File.Delete(_path);
					}
					catch (Exception cleaned) when (cleaned is IOException or UnauthorizedAccessException)
					{
					}

					_path = null;
				}
			}
		}

		private void WriteToFileLocked(byte[] chunk)
		{
			try
			{
				_file!.Write(chunk, 0, chunk.Length);
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				// 写到一半失败：完整输出已经不可信，删掉残留文件，之后只保留内存窗口。
				_pendingBytes.Clear();
				_fileUnavailable = true;
				_file?.Dispose();
				_file = null;
				if (_path is not null)
				{
					try
					{
						File.Delete(_path);
					}
					catch (Exception cleaned) when (cleaned is IOException or UnauthorizedAccessException)
					{
					}

					_path = null;
				}
			}
		}

		public void Dispose()
		{
			lock (_gate)
			{
				_closed = true;
				_file?.Dispose();
				_file = null;
			}
		}
	}

}
