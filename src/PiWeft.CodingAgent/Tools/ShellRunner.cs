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

		try
		{
			if (shell.CommandFromStdin)
			{
				await process.StandardInput.WriteAsync(command.AsMemory(), CancellationToken.None).ConfigureAwait(false);
			}

			process.StandardInput.Close();
			await process.WaitForExitAsync(cancellation.Token).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			TryKill(process);
			await DrainAsync(stdout, stderr, readCancellation).ConfigureAwait(false);

			var timedOut = !cancellationToken.IsCancellationRequested;
			return capture.Complete(exitCode: null, cancelled: !timedOut, timedOut: timedOut);
		}
		catch
		{
			TryKill(process);
			readCancellation.Cancel();
			throw;
		}

		await DrainAsync(stdout, stderr, readCancellation).ConfigureAwait(false);
		return capture.Complete(process.ExitCode, cancelled: false, timedOut: false);
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
			// 进程退出后仍有孙进程持有管道：由 DrainAsync 统一收尾。
		}
		catch (ObjectDisposedException)
		{
		}
		catch (IOException)
		{
		}
	}

	/// 等两个流真正排空。被后台孙进程继承的管道可能永远不关闭（`npm run dev &`），
	/// 所以这里必须有界：超时后取消读取，用已经读到的内容收尾。
	private static async Task DrainAsync(Task stdout, Task stderr, CancellationTokenSource readCancellation)
	{
		var all = Task.WhenAll(stdout, stderr);
		if (await Task.WhenAny(all, Task.Delay(2_000)).ConfigureAwait(false) != all)
		{
			readCancellation.Cancel();
		}

		try
		{
			await all.ConfigureAwait(false);
		}
		catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException or IOException)
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

		var headBytes = (int)Math.Min(int.MaxValue, maxBytes / 2);
		var tailBytes = (int)Math.Min(int.MaxValue, maxBytes - headBytes);
		var head = new byte[headBytes];
		var tail = new byte[tailBytes];
		stream.ReadExactly(head);
		stream.Seek(-tailBytes, SeekOrigin.End);
		stream.ReadExactly(tail);
		var headRead = head.Length;
		var tailRead = tail.Length;

		// 尾部可能从 UTF-8 续字节中间开始；跳过它，避免产生一个替换字符。
		var tailStart = 0;
		while (tailStart < tailRead && (tail[tailStart] & 0xC0) == 0x80)
		{
			tailStart++;
		}

		var headText = Encoding.UTF8.GetString(head, 0, headRead);
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

		File.WriteAllText(path, output, new UTF8Encoding(false));
		return path;
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
		catch (InvalidOperationException)
		{
			// 进程已经退出。
		}
		catch (NotSupportedException)
		{
			// 该平台不支持整树杀进程。
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
		private int _lineScan;
		private readonly List<byte[]> _raw = [];
		private FileStream? _file;
		private string? _path;
		private bool _fileUnavailable;
		private bool _closed;
		private bool _tailTruncated;
		private bool _tailStartsAtLineBoundary = true;
		private long _tailBytes;
		private long _totalRawBytes;
		private long _totalDecodedBytes;
		private int _completedLines;
		private int _totalLines;
		private long _currentLineBytes;
		private bool _hasOpenLine;
		private Action<string>? _onOutput;

		public OutputCapture(string prefix) => _prefix = prefix;

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

				_onOutput = onOutput;
				_totalRawBytes += data.Length;
				var chunk = data.ToArray();
				if (_file is not null)
				{
					WriteToFileLocked(chunk);
				}
				else
				{
					_raw.Add(chunk);
				}

				var charCount = _decoder.GetCharCount(chunk, 0, chunk.Length);
				if (charCount > 0)
				{
					var chars = new char[charCount];
					_decoder.GetChars(chunk, 0, chunk.Length, chars, 0);
					AppendTextLocked(new string(chars), onOutput);
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
				var content = _tail.ToString();
				if (!_tailStartsAtLineBoundary)
				{
					var newline = content.IndexOf('\n');
					if (newline >= 0)
					{
						content = content[(newline + 1)..];
					}
				}

				return new ShellSnapshot(
					content,
					_totalDecodedBytes,
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

				var content = _tail.ToString();
				if (!_tailStartsAtLineBoundary)
				{
					var newline = content.IndexOf('\n');
					if (newline >= 0)
					{
						content = content[(newline + 1)..];
					}
				}

				return new ShellResult(content, exitCode, cancelled)
				{
					FullOutputPath = !_fileUnavailable ? _path : null,
					TotalBytes = _totalDecodedBytes,
					TotalLines = _totalLines,
					OutputTruncated = _tailTruncated,
					TimedOut = timedOut,
				};
			}
		}

		private void FlushDecoderLocked()
		{
			var chars = new char[4];
			var count = _decoder.GetChars(Array.Empty<byte>(), 0, 0, chars, 0, flush: true);
			if (count > 0)
			{
				AppendTextLocked(new string(chars, 0, count), _onOutput);
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
			_lineScan = 0;
			if (line.EndsWith('\r'))
			{
				line = line[..^1];
			}

			_onOutput?.Invoke(line);
		}

		private void AppendTextLocked(string text, Action<string>? onOutput)
		{
			if (text.Length == 0)
			{
				return;
			}

			var bytes = Encoding.UTF8.GetByteCount(text);
			_totalDecodedBytes += bytes;
			_tail.Append(text);
			_tailBytes += bytes;

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
				_currentLineBytes += bytes;
				_hasOpenLine = true;
			}
			else
			{
				_completedLines += newlines;
				var tail = text[(lastNewline + 1)..];
				_currentLineBytes = Encoding.UTF8.GetByteCount(tail);
				_hasOpenLine = tail.Length > 0;
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

			var scanFrom = _lineScan;
			_line.Append(text);
			_lineScan = _line.Length;
			for (var index = scanFrom; index < _line.Length; index++)
			{
				if (_line[index] != '\n')
				{
					continue;
				}

				var line = _line.ToString(0, index);
				_line.Remove(0, index + 1);
				_lineScan = 0;
				index = -1;
				if (line.EndsWith('\r'))
				{
					line = line[..^1];
				}

				onOutput.Invoke(line);
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

		private bool ShouldUseTempFile() =>
			_totalRawBytes > MaxBytes || _totalDecodedBytes > MaxBytes || _totalLines > MaxLines;

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
				_file = new FileStream(_path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
				foreach (var chunk in _raw)
				{
					_file.Write(chunk, 0, chunk.Length);
				}

				_raw.Clear();
				_file.Flush();
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				// 临时目录不可写：退化成只有尾部窗口，不让 shell 因为落盘失败而失败。
				_raw.Clear();
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
				_raw.Clear();
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
