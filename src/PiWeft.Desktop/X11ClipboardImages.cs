#if MEWUI_X11
using System.Diagnostics;
using PiWeft.Ai;

namespace PiWeft.Desktop;

/// <summary>
/// Linux（X11/XWayland）的图片剪贴板，经 xclip 读写 <c>image/png</c> 等 MIME。
/// MewUI 自身就跑在 X11/XWayland 上，因此图片与文本落在同一个剪贴板域里，不处理原生 Wayland 会话。
/// xsel 不支持图片类型，装了但没装 xclip 时就报告不可用，界面保留原有文本粘贴。
/// </summary>
internal sealed class X11ClipboardImages : IClipboardImages
{
	private const int TimeoutMs = 1500;
	private const string Clipboard = "clipboard";

	private readonly string _executable;
	private readonly Lock _gate = new();
	private Process? _owner;
	private bool _disposed;

	private X11ClipboardImages(string executable) => _executable = executable;

	public static X11ClipboardImages? Create()
	{
		if (!OperatingSystem.IsLinux()) return null;
		if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"))) return null;
		return Find("xclip") is { } xclip ? new X11ClipboardImages(xclip) : null;
	}

	private static IReadOnlyList<string> ListArguments => ["-quiet", "-selection", Clipboard, "-t", "TARGETS", "-o"];

	private static IReadOnlyList<string> ReadArguments(string mimeType) =>
		["-quiet", "-selection", Clipboard, "-t", mimeType, "-o"];

	private static IReadOnlyList<string> WriteArguments(string mimeType) =>
		["-quiet", "-selection", Clipboard, "-t", mimeType, "-i"];

	public bool HasImage()
	{
		if (_disposed) return false;
		var targets = Run(ListArguments);
		return targets is not null && ClipboardImages.Mimes.Any(mime => Contains(targets, mime));
	}

	public async Task<IReadOnlyList<ClipboardAttachment>> ReadAsync()
	{
		List<ClipboardAttachment> attachments = [];
		if (_disposed) return attachments;
		var targets = Run(ListArguments);
		if (targets is null) return attachments;

		foreach (var mimeType in ClipboardImages.Mimes)
		{
			if (!Contains(targets, mimeType)) continue;
			var bytes = await RunAsync(ReadArguments(mimeType)).ConfigureAwait(false);
			if (bytes is null || bytes.Length == 0 || bytes.Length > ClipboardImages.MaxBytes) return attachments;
			// owner 常常同时声明好几种类型：签名不符时继续试下一种，都不行才当作文本粘贴。
			if (!ClipboardImages.LooksLike(mimeType, bytes)) continue;
			attachments.Add(new ClipboardAttachment(ClipboardImages.PastedName(mimeType),
				new ImageContent { Data = Convert.ToBase64String(bytes), MimeType = mimeType }));
			return attachments;
		}

		return attachments;
	}

	public Task<bool> TrySetImageAsync(string mimeType, byte[] data) => Task.Run(() => TrySetImage(mimeType, data));

	private bool TrySetImage(string mimeType, byte[] data)
	{
		if (_disposed || data.Length == 0 || data.Length > ClipboardImages.MaxBytes) return false;
		Process? candidate = null;
		try
		{
			candidate = Start(WriteArguments(mimeType), input: true);
			if (candidate is null) return false;
			candidate.StandardInput.BaseStream.Write(data);
			candidate.StandardInput.Close();
			// X11 是选区语义：内容跟持有者同生共死，所以要把 xclip 进程留着当 owner。
			if (candidate.WaitForExit(50)) return candidate.ExitCode == 0;
			lock (_gate)
			{
				if (_disposed) return false;
				Stop(_owner);
				_owner = candidate;
				candidate = null;
			}
			return true;
		}
		catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
		{
			return false;
		}
		finally { Stop(candidate); }
	}

	/// <summary>列出剪贴板可用目标。按键处理里同步调用，因此只等待一个有界超时。</summary>
	private string[]? Run(IReadOnlyList<string> arguments)
	{
		Process? process = null;
		try
		{
			process = Start(arguments, input: false);
			if (process is null) return null;

			// 先等退出再读：这里的输出只是目标列表，远小于管道缓冲。反过来先读的话，
			// 剪贴板 owner 挂住不关 stdout 会让 ReadToEnd 永不返回 —— 而本方法在按键
			// 处理里同步调用，UI 会整个冻住，WaitForExit 的超时根本轮不到执行。
			if (!process.WaitForExit(TimeoutMs)) return null;
			var text = process.StandardOutput.ReadToEnd();
			return process.ExitCode == 0
				? text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
				: null;
		}
		catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
		{
			return null;
		}
		finally { Stop(process); }
	}

	private async Task<byte[]?> RunAsync(IReadOnlyList<string> arguments)
	{
		Process? process = null;
		try
		{
			process = Start(arguments, input: false);
			if (process is null) return null;
			using var content = new MemoryStream();
			var buffer = new byte[8192];
			using var timeout = new CancellationTokenSource(TimeoutMs);
			while (true)
			{
				var count = await process.StandardOutput.BaseStream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
				if (count == 0) break;
				if (content.Length + count > ClipboardImages.MaxBytes) return null;
				content.Write(buffer, 0, count);
			}
			await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
			return process.ExitCode == 0 ? content.ToArray() : null;
		}
		catch (Exception ex) when (ex is OperationCanceledException or IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
		{
			return null;
		}
		finally { Stop(process); }
	}

	private Process? Start(IReadOnlyList<string> arguments, bool input)
	{
		var process = new Process
		{
			StartInfo = new ProcessStartInfo(_executable)
			{
				UseShellExecute = false,
				CreateNoWindow = true,
				RedirectStandardInput = input,
				RedirectStandardOutput = !input,
				RedirectStandardError = true,
			},
		};
		foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
		try
		{
			if (!process.Start())
			{
				process.Dispose();
				return null;
			}
			// 诊断输出必须排空，否则管道写满会把子进程挂住。
			process.ErrorDataReceived += static (_, _) => { };
			process.BeginErrorReadLine();
			return process;
		}
		catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
		{
			process.Dispose();
			return null;
		}
	}

	private static void Stop(Process? process)
	{
		if (process is null) return;
		try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
		catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
		finally { process.Dispose(); }
	}

	private static bool Contains(IReadOnlyList<string> targets, string mimeType) =>
		targets.Any(target => string.Equals(target, mimeType, StringComparison.OrdinalIgnoreCase));

	private static string? Find(string name) => (Environment.GetEnvironmentVariable("PATH") ?? "")
		.Split(Path.PathSeparator).Where(static directory => directory.Length > 0)
		.Select(directory => Path.Combine(directory, name)).FirstOrDefault(File.Exists);

	public void Dispose()
	{
		lock (_gate)
		{
			if (_disposed) return;
			_disposed = true;
			Stop(_owner);
			_owner = null;
		}
	}
}
#endif
