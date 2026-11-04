using System.Diagnostics;
using System.Text;

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
}

/// shell 调用的附加配置：显式 shell 路径、命令前缀与注入的环境变量。
/// 对应 pi 的 <c>shellPath</c>、<c>shellCommandPrefix</c> 与 <c>exposeSessionEnvironment</c>。
public sealed record ShellInvocation
{
	public string? ShellPath { get; init; }

	public string? CommandPrefix { get; init; }

	/// 注入子进程的环境变量；值为 null 表示从继承的环境中删除该变量。
	public IReadOnlyDictionary<string, string?>? Environment { get; init; }

	public bool PowerShell { get; init; }
}

public static class ShellRunner
{
	public static readonly string[] SessionEnvironmentNames =
	[
		"PI_SESSION_ID",
		"PI_SESSION_FILE",
		"PI_PROVIDER",
		"PI_MODEL",
		"PI_REASONING_LEVEL",
	];

	/// 在 <paramref name="cwd"/> 执行 <paramref name="command"/>，按到达顺序合并 stdout 与 stderr。
	/// 取消会杀掉整棵进程树；超时则抛出 <see cref="InvalidOperationException"/>。
	public static async Task<ShellResult> RunAsync(
		string command,
		string cwd,
		double? timeoutSeconds,
		Action<string>? onOutput,
		CancellationToken cancellationToken,
		ShellInvocation? invocation = null)
	{
		if (timeoutSeconds is { } seconds && (!double.IsFinite(seconds) || seconds <= 0))
		{
			throw new InvalidOperationException("Invalid timeout: must be a finite number of seconds");
		}

		var prefix = invocation?.CommandPrefix;
		if (prefix is { Length: > 0 })
		{
			// 与 pi 相同：前缀单独成行，用户的命令仍按原样执行。
			command = $"{prefix}\n{command}";
		}

		var (fileName, arguments) = invocation?.PowerShell == true
			? PowerShellCommand(command)
			: ShellCommand(command, invocation?.ShellPath);
		var startInfo = new ProcessStartInfo
		{
			FileName = fileName,
			WorkingDirectory = cwd,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			RedirectStandardInput = true,
			UseShellExecute = false,
		};

		foreach (var argument in arguments)
		{
			startInfo.ArgumentList.Add(argument);
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

		using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		if (timeoutSeconds is { } timeout)
		{
			cancellation.CancelAfter(TimeSpan.FromSeconds(timeout));
		}

		// 与 pi 的 OutputAccumulator 同义：内存里只留尾部窗口，超出部分落到临时文件。
		// 以前把整条命令的输出堆在 StringBuilder 里，`yes` 或 `cat 大文件` 会直接吃掉几 GB 内存。
		using var capture = new OutputCapture();

		// 两个流各自在 EOF 时用 null Data 通知一次；等它们排空后再快照，
		// 否则最后几行可能还在线程池队列里，被截断的输出就少了尾巴。
		var stdoutClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var stderrClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		using var process = new Process { StartInfo = startInfo };
		process.OutputDataReceived += (_, args) => OnLine(args.Data, capture, onOutput, stdoutClosed);
		process.ErrorDataReceived += (_, args) => OnLine(args.Data, capture, onOutput, stderrClosed);

		if (!process.Start())
		{
			throw new InvalidOperationException($"Failed to start {fileName}");
		}

		process.BeginOutputReadLine();
		process.BeginErrorReadLine();
		process.StandardInput.Close();

		try
		{
			await process.WaitForExitAsync(cancellation.Token).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			TryKill(process);
			await DrainOutputAsync(stdoutClosed.Task, stderrClosed.Task).ConfigureAwait(false);

			if (cancellationToken.IsCancellationRequested)
			{
				return capture.Complete(exitCode: null, cancelled: true);
			}

			throw new InvalidOperationException($"Command timed out after {timeoutSeconds} seconds");
		}

		await DrainOutputAsync(stdoutClosed.Task, stderrClosed.Task).ConfigureAwait(false);
		return capture.Complete(process.ExitCode, cancelled: false);
	}

	private static void OnLine(
		string? line,
		OutputCapture capture,
		Action<string>? onOutput,
		TaskCompletionSource closed)
	{
		if (line is null)
		{
			// BeginOutputReadLine 在流结束时用 null 回调一次。
			closed.TrySetResult();
			return;
		}

		capture.Append(line, onOutput);
	}

	/// 等两个流真正排空。被后台孙进程继承的管道可能永远不关闭（`npm run dev &`），
	/// 所以这里必须有界：超时后就用已经读到的内容收尾，不能把 shell 调用挂死。
	private static async Task DrainOutputAsync(Task stdoutClosed, Task stderrClosed)
	{
		await Task.WhenAny(Task.WhenAll(stdoutClosed, stderrClosed), Task.Delay(2_000)).ConfigureAwait(false);
	}

	public static string WriteFullOutput(string output)
	{
		var path = Path.Combine(
			Path.GetTempPath(),
			$"pi-bash-{Guid.NewGuid().ToString("N")[..12]}.log");

		File.WriteAllText(path, output, new UTF8Encoding(false));
		return path;
	}

	public static (string FileName, string[] Arguments) PowerShellCommand(string command)
	{
		var shell = FindOnPath("pwsh") ?? FindOnPath("pwsh.exe") ??
			FindOnPath("powershell") ?? FindOnPath("powershell.exe");
		return shell is null
			? throw new InvalidOperationException("PowerShell (pwsh or powershell) was not found on PATH")
			: (shell, ["-NoProfile", "-NonInteractive", "-Command", command]);
	}

	public static (string FileName, string[] Arguments) ShellCommand(string command, string? shellPath = null)
	{
		if (OperatingSystem.IsWindows())
		{
			var windowsShell = shellPath is { Length: > 0 } configured && File.Exists(configured)
				? configured
				: FindOnPath("pwsh.exe") ?? FindOnPath("powershell.exe");
			if (windowsShell is not null)
			{
				// 显式配置成 cmd.exe 时按 cmd 的参数走，其余按 PowerShell 处理。
				return Path.GetFileNameWithoutExtension(windowsShell).Equals("cmd", StringComparison.OrdinalIgnoreCase)
					? (windowsShell, ["/d", "/s", "/c", command])
					: (windowsShell, ["-NoProfile", "-NonInteractive", "-Command", command]);
			}

			return ("cmd.exe", ["/d", "/s", "/c", command]);
		}

		if (shellPath is { Length: > 0 } explicitShell && File.Exists(explicitShell))
		{
			return (explicitShell, ["-l", "-c", command]);
		}

		var shell = Environment.GetEnvironmentVariable("SHELL");
		if (string.IsNullOrEmpty(shell) || !File.Exists(shell))
		{
			shell = "/bin/bash";
		}

		return (shell, ["-l", "-c", command]);
	}

	/// 只保留尾部窗口的累计器。超过窗口时先确保此前的内容已经写进临时文件，
	/// 再按行边界裁掉前半段；之后的输出同时写文件与窗口。
	private sealed class OutputCapture : IDisposable
	{
		/// 内存里的上限（UTF-16 字符）。比模型可见的 50KB 大得多：
		/// 正常命令根本不会触发，尾部窗口也足够「最近 2000 行」使用。
		internal const int WindowChars = 1024 * 1024;

		private const int KeepChars = WindowChars / 2;

		private readonly StringBuilder _text = new();
		private readonly Lock _gate = new();
		private StreamWriter? _file;
		private string? _path;
		private bool _fileUnavailable;
		private bool _truncated;
		private bool _closed;
		private long _bytes;
		private int _lines;

		public void Append(string? line, Action<string>? onOutput)
		{
			if (line is null)
			{
				return;
			}

			lock (_gate)
			{
				// 收尾后才到的行（被孙进程继承的管道、线程池队列延迟）直接丢掉：
				// 快照已经交出去，再写只会制造一个没人引用的临时文件。
				if (_closed)
				{
					return;
				}

				_lines++;
				_bytes += Encoding.UTF8.GetByteCount(line) + 1;
				if (_file is not null)
				{
					WriteToFile(line);
				}

				_text.Append(line).Append('\n');
				if (_text.Length > WindowChars)
				{
					Trim();
				}
			}

			onOutput?.Invoke(line);
		}

		public ShellResult Complete(int? exitCode, bool cancelled)
		{
			lock (_gate)
			{
				_closed = true;
				_file?.Dispose();
				_file = null;

				// 文件只可能是「超窗后创建」的：没超窗时 Output 就是全文，不需要它。
				var path = _truncated && !_fileUnavailable ? _path : null;
				return new ShellResult(_text.ToString(), exitCode, cancelled)
				{
					FullOutputPath = path,
					TotalBytes = _bytes,
					TotalLines = _lines,
					OutputTruncated = _truncated,
				};
			}
		}

		private void Trim()
		{
			EnsureFile();

			var start = Math.Max(0, _text.Length - KeepChars);
			// 从行边界开始保留，尾部窗口的首行通常不是半行（模型看到的行数与全量行数仍能对上）。
			// 例外：单行本身就超过窗口（`base64 -w0` 之类）。这时保留它的尾部片段，
			// 不能把整行都丢掉 —— Truncate.Tail 的最后一行截断依赖窗口里仍有内容。
			var boundary = -1;
			for (var index = start; index < _text.Length - 1; index++)
			{
				if (_text[index] == '\n')
				{
					boundary = index + 1;
					break;
				}
			}

			if (boundary >= 0)
			{
				start = boundary;
			}

			if (start > 0)
			{
				_text.Remove(0, start);
			}

			_truncated = true;
		}

		private void EnsureFile()
		{
			if (_file is not null || _fileUnavailable)
			{
				return;
			}

			try
			{
				_path = Path.Combine(
					Path.GetTempPath(),
					$"pi-bash-{Guid.NewGuid().ToString("N")[..12]}.log");
				_file = new StreamWriter(
					new FileStream(_path, FileMode.CreateNew, FileAccess.Write, FileShare.Read),
					new UTF8Encoding(false))
				{
					AutoFlush = false,
				};
				// 这是第一次超窗，窗口里还是完整的前缀，先原样落盘。
				_file.Write(_text);
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				// 临时目录不可写：退化成只有尾部窗口，不让 bash 因为落盘失败而失败。
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

		private void WriteToFile(string line)
		{
			try
			{
				_file!.Write(line);
				_file.Write('\n');
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				// 写到一半失败：完整输出已经不可信，删掉残留文件，之后只保留内存窗口。
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
}
