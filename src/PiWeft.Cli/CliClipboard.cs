using System.Diagnostics;

namespace PiWeft.Cli;

/// <summary>
/// CLI 侧的系统剪贴板写入：按平台选择 <c>wl-copy</c>/<c>xclip</c>/<c>pbcopy</c>/<c>clip</c>。
/// 没有可用工具（常见于无图形环境的 SSH 会话）时返回 false，由调用方给出提示。
/// </summary>
internal static class CliClipboard
{
	public static bool TryCopy(string text)
	{
		if (text.Length == 0)
		{
			return false;
		}

		try
		{
			if (OperatingSystem.IsMacOS())
			{
				return Run("pbcopy", [], text);
			}

			if (OperatingSystem.IsWindows())
			{
				return Run("clip", [], text);
			}

			if (OperatingSystem.IsLinux())
			{
				// Wayland 优先；X11 会话再退回 xclip。两个都不在 PATH 里时报告失败。
				if (Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") is { Length: > 0 } &&
					Run("wl-copy", [], text))
				{
					return true;
				}

				return Run("xclip", ["-selection", "clipboard"], text);
			}

			return false;
		}
		catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
		{
			return false;
		}
	}

	private static bool Run(string fileName, string[] arguments, string input)
	{
		var start = new ProcessStartInfo(fileName)
		{
			RedirectStandardInput = true,
			RedirectStandardError = true,
			RedirectStandardOutput = true,
			UseShellExecute = false,
			CreateNoWindow = true,
		};
		foreach (var argument in arguments)
		{
			start.ArgumentList.Add(argument);
		}

		using var process = Process.Start(start);
		if (process is null)
		{
			return false;
		}

		process.StandardInput.Write(input);
		process.StandardInput.Close();
		return process.WaitForExit(2_000) && process.ExitCode == 0;
	}
}
