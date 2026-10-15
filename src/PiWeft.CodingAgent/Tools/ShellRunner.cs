using System.Diagnostics;
using System.Text;

namespace PiWeft.CodingAgent.Tools;

/// <summary>shell 命令的原始结果，尚未截断或格式化。</summary>
public sealed record ShellResult(string Output, int? ExitCode, bool Cancelled);

/// <summary>为 <c>bash</c> 工具与 RPC 的 <c>bash</c> 命令执行 shell。</summary>
public static class ShellRunner
{
	/// <summary>
	/// 在 <paramref name="cwd"/> 执行 <paramref name="command"/>，按到达顺序合并 stdout 与 stderr。
	/// 取消会杀掉整棵进程树；超时则抛出 <see cref="InvalidOperationException"/>。
	/// </summary>
	public static async Task<ShellResult> RunAsync(
		string command,
		string cwd,
		double? timeoutSeconds,
		Action<string>? onOutput,
		CancellationToken cancellationToken)
	{
		if (timeoutSeconds is { } seconds && (!double.IsFinite(seconds) || seconds <= 0))
		{
			throw new InvalidOperationException("Invalid timeout: must be a finite number of seconds");
		}

		var (fileName, arguments) = ShellCommand(command);
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

		using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		if (timeoutSeconds is { } timeout)
		{
			cancellation.CancelAfter(TimeSpan.FromSeconds(timeout));
		}

		var output = new StringBuilder();
		var gate = new Lock();

		using var process = new Process { StartInfo = startInfo };
		process.OutputDataReceived += (_, args) => Append(output, gate, args.Data, onOutput);
		process.ErrorDataReceived += (_, args) => Append(output, gate, args.Data, onOutput);

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

			if (cancellationToken.IsCancellationRequested)
			{
				var partial = Snapshot(output, gate);
				return new ShellResult(partial, null, Cancelled: true);
			}

			throw new InvalidOperationException($"Command timed out after {timeoutSeconds} seconds");
		}

		return new ShellResult(Snapshot(output, gate), process.ExitCode, Cancelled: false);
	}

	/// <summary>把未截断的输出写到临时文件，便于模型之后读取完整内容。</summary>
	public static string WriteFullOutput(string output)
	{
		var path = Path.Combine(
			Path.GetTempPath(),
			$"pi-bash-{Guid.NewGuid().ToString("N")[..12]}.log");

		File.WriteAllText(path, output, new UTF8Encoding(false));
		return path;
	}

	/// <summary>执行命令所用的 shell 与参数，与 pi 的平台默认值一致。</summary>
	public static (string FileName, string[] Arguments) ShellCommand(string command)
	{
		if (OperatingSystem.IsWindows())
		{
			var powershell = FindOnPath("pwsh.exe") ?? FindOnPath("powershell.exe");
			return powershell is not null
				? (powershell, ["-NoProfile", "-NonInteractive", "-Command", command])
				: ("cmd.exe", ["/d", "/s", "/c", command]);
		}

		var shell = Environment.GetEnvironmentVariable("SHELL");
		if (string.IsNullOrEmpty(shell) || !File.Exists(shell))
		{
			shell = "/bin/bash";
		}

		return (shell, ["-l", "-c", command]);
	}

	private static void Append(StringBuilder output, Lock gate, string? line, Action<string>? onOutput)
	{
		if (line is null)
		{
			return;
		}

		lock (gate)
		{
			output.Append(line).Append('\n');
		}

		onOutput?.Invoke(line);
	}

	private static string Snapshot(StringBuilder output, Lock gate)
	{
		lock (gate)
		{
			return output.ToString();
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
