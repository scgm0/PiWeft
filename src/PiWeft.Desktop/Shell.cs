using System.Diagnostics;
using PiWeft.Cli;

namespace PiWeft.Desktop;

/// <summary>
/// 调用系统默认程序打开文件或链接。桌面端唯一的「外部动作」，因此集中在这里并统一处理平台差异。
/// </summary>
public static class Shell
{
	/// <summary>
	/// 在编辑器里打开文件：优先 <c>$VISUAL</c> / <c>$EDITOR</c>（与 git、crontab 等工具的约定一致），
	/// 没有配置时交给系统默认程序。返回是否成功启动。
	/// </summary>
	public static bool OpenInEditor(string path, int? line = null, int? column = null)
	{
		foreach (var variable in new[] { "VISUAL", "EDITOR" })
		{
			if (Environment.GetEnvironmentVariable(variable) is not { Length: > 0 } editor)
			{
				continue;
			}

			var location = line is { } number ? $"{path}:{number}:{column ?? 1}" : path;
			var name = Path.GetFileNameWithoutExtension(editor).ToLowerInvariant();
			if (Start(editor, name is "code" or "code-insiders" or "codium" or "cursor" ?
				(line is not null ? ["--goto", location] : [path]) : name is "subl" or "sublime_text" ? [location] : [path]))
			{
				return true;
			}
		}

		return Open(path);
	}

	public static bool OpenMessageLink(string url, string cwd, Action<string>? notice = null)
	{
		var target = MessageLinkTarget.Resolve(url, cwd);
		if (target is null) { notice?.Invoke(Strings.LinkUnsupported); return false; }
		if (!target.IsWeb && !File.Exists(target.Target) && !Directory.Exists(target.Target))
		{ notice?.Invoke(Strings.FileMissing(target.Target)); return false; }
		var started = target.IsWeb || Directory.Exists(target.Target) ? Open(target.Target) : OpenInEditor(target.Target, target.Line, target.Column);
		if (!started) notice?.Invoke(Strings.OpenFailed(target.Target));
		return started;
	}

	/// <summary>用默认程序打开一个文件或目录。失败时返回 false，不抛异常。</summary>
	public static bool Open(string path)
	{
		var (command, argument) = OperatingSystem.IsWindows()
			? ("explorer", path)
			: OperatingSystem.IsMacOS()
				? ("open", path)
				: ("xdg-open", path);

		return Start(command, [argument]);
	}

	/// <summary>启动一个外部命令；失败（未安装、权限等）返回 false。</summary>
	private static bool Start(string command, string[] arguments)
	{
		try
		{
			var info = new ProcessStartInfo(command)
			{
				UseShellExecute = false,
				RedirectStandardError = true,
			};
			foreach (var argument in arguments) info.ArgumentList.Add(argument);
			using var process = Process.Start(info);

			return process is not null;
		}
		catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
		{
			AppLog.Failure(AppLog.For<MainWindow>(), "Start external application", ex);
			// 没装 xdg-open / 编辑器之类的环境很常见，交给调用方提示即可。
			return false;
		}
	}
}
