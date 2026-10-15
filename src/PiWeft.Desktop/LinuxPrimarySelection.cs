using System.Diagnostics;
using System.Text;

namespace PiWeft.Desktop;

internal interface IPrimarySelection
{
	void Publish(string text);
	Task<string?> ReadAsync();
	Task PendingPublish { get; }
}

/// <summary>
/// PRIMARY 服务选择器：优先使用进程内原生 X11 实现（无外部程序），失败时回退到
/// xclip / xsel（X11）或 wl-copy / wl-paste（Wayland）。不含 CLIPBOARD 语义。
/// </summary>
internal sealed class LinuxPrimarySelection : IPrimarySelection, IDisposable
{
	// 原生实现按 ICCCM INCR 分块，单次传输上限可放宽到 8 MiB；外部程序回退路径沿用同一上限。
	internal const int MaxBytes = 8 * 1024 * 1024;
	internal const int MaxCharacters = MaxBytes / 2;
	private readonly IPrimarySelection _inner;

	private LinuxPrimarySelection(IPrimarySelection inner) => _inner = inner;

	internal LinuxPrimarySelection(SelectionCommand command) => _inner = new ProcessPrimarySelection(command);

	public static LinuxPrimarySelection? Create()
	{
		if (!OperatingSystem.IsLinux()) return null;
		// MewUI currently runs on X11/XWayland. Prefer that selection domain even on a Wayland desktop.
		if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")))
		{
			if (X11PrimarySelection.Create() is { } native) return new(native);
			if (Find("xclip") is { } xclip) return new(new SelectionCommand(xclip,
				["-quiet", "-selection", "primary", "-in", "-target", "UTF8_STRING"],
				["-selection", "primary", "-out"]));
			if (Find("xsel") is { } xsel) return new(new SelectionCommand(xsel,
				["--nodetach", "--primary", "--input"], ["--primary", "--output"]));
		}
		if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")) &&
			Find("wl-copy") is { } copy && Find("wl-paste") is { } paste)
			return new(new SelectionCommand(copy, ["--foreground", "--primary", "--type", "text/plain;charset=utf-8"],
				["--primary", "--no-newline"], paste));
		return null;
	}

	private static string? Find(string name) => (Environment.GetEnvironmentVariable("PATH") ?? "")
		.Split(Path.PathSeparator).Where(static directory => directory.Length > 0)
		.Select(directory => Path.Combine(directory, name)).FirstOrDefault(File.Exists);

	public void Publish(string text) => _inner.Publish(text);
	public Task<string?> ReadAsync() => _inner.ReadAsync();
	public Task PendingPublish => _inner.PendingPublish;
	public void Dispose() => (_inner as IDisposable)?.Dispose();
}

/// <summary>外部程序后端（xclip / xsel / wl-copy）：原生 X11 实现不可用时的回退。</summary>
internal sealed class ProcessPrimarySelection : IPrimarySelection, IDisposable
{
	private readonly SelectionCommand _command;
	private readonly Lock _gate = new();
	private readonly SemaphoreSlim _writer = new(1);
	private readonly CancellationTokenSource _shutdown = new();
	private CancellationTokenSource? _pending;
	private Process? _owner;
	private bool _disposed;

	public Task PendingPublish { get; private set; } = Task.CompletedTask;

	internal ProcessPrimarySelection(SelectionCommand command) => _command = command;

	public void Publish(string text)
	{
		lock (_gate)
		{
			if (_disposed) return;
			_pending?.Cancel();
			// Clearing a selection keeps the last PRIMARY, but cancels a delayed intermediate drag.
			if (text.Length == 0 || text.Length > LinuxPrimarySelection.MaxCharacters) return;
			var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
			_pending = cancellation;
			PendingPublish = PublishAsync(text, cancellation);
		}
	}

	private async Task PublishAsync(string text, CancellationTokenSource cancellation)
	{
		var acquired = false;
		Process? candidate = null;
		try
		{
			await Task.Delay(140, cancellation.Token).ConfigureAwait(false);
			await _writer.WaitAsync(cancellation.Token).ConfigureAwait(false);
			acquired = true;
			cancellation.Token.ThrowIfCancellationRequested();
			if (Encoding.UTF8.GetByteCount(text) > LinuxPrimarySelection.MaxBytes) return;
			candidate = Start(_command.WriteFile, _command.WriteArguments, input: true);
			if (candidate is null) return;
			using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
			timeout.CancelAfter(TimeSpan.FromSeconds(2));
			await candidate.StandardInput.WriteAsync(text.AsMemory(), timeout.Token).ConfigureAwait(false);
			candidate.StandardInput.Close();
			lock (_gate)
			{
				if (_disposed || cancellation.IsCancellationRequested) return;
				Stop(_owner);
				_owner = candidate;
				candidate = null;
			}
		}
		catch (Exception ex) when (ex is OperationCanceledException or IOException or System.ComponentModel.Win32Exception or InvalidOperationException) { }
		finally
		{
			Stop(candidate);
			if (acquired) _writer.Release();
			lock (_gate)
			{
				if (ReferenceEquals(_pending, cancellation)) _pending = null;
				cancellation.Dispose();
			}
		}
	}

	public async Task<string?> ReadAsync()
	{
		Task publish;
		lock (_gate) { if (_disposed) return null; publish = PendingPublish; }
		await publish.ConfigureAwait(false);
		Process? process = null;
		try
		{
			using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
			timeout.CancelAfter(TimeSpan.FromSeconds(2));
			process = Start(_command.ReadFile ?? _command.WriteFile, _command.ReadArguments, input: false);
			if (process is null) return null;
			using var content = new MemoryStream();
			var buffer = new byte[8192];
			while (true)
			{
				var count = await process.StandardOutput.BaseStream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
				if (count == 0) break;
				if (content.Length + count > LinuxPrimarySelection.MaxBytes) return null;
				content.Write(buffer, 0, count);
			}
			await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
			return process.ExitCode == 0 ? Encoding.UTF8.GetString(content.GetBuffer(), 0, (int)content.Length) : null;
		}
		catch (Exception ex) when (ex is OperationCanceledException or IOException or System.ComponentModel.Win32Exception or InvalidOperationException) { return null; }
		finally { Stop(process); }
	}

	private static Process? Start(string file, IReadOnlyList<string> arguments, bool input)
	{
		var process = new Process { StartInfo = new ProcessStartInfo(file)
		{
			UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = input,
			RedirectStandardOutput = !input, RedirectStandardError = true,
		} };
		if (input) process.StartInfo.StandardInputEncoding = new UTF8Encoding(false);
		foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
		try
		{
			if (!process.Start()) { process.Dispose(); return null; }
			// Drain diagnostics without storing clipboard text or allowing a pipe to fill.
			process.ErrorDataReceived += static (_, _) => { };
			process.BeginErrorReadLine();
			return process;
		}
		catch { process.Dispose(); throw; }
	}

	private static void Stop(Process? process)
	{
		if (process is null) return;
		try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
		catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
		finally { process.Dispose(); }
	}

	public void Dispose()
	{
		lock (_gate)
		{
			if (_disposed) return;
			_disposed = true;
			_shutdown.Cancel();
			Stop(_owner);
			_owner = null;
		}
	}
}

internal sealed record SelectionCommand(string WriteFile, string[] WriteArguments, string[] ReadArguments, string? ReadFile = null);
