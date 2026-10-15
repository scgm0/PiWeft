using System.Diagnostics;
using PiWeft.Cli;

namespace PiWeft.Desktop;

/// <summary>在独立项目窗口打开目录；原窗口的会话与运行不受影响，单实例入口处理重复项目。</summary>
internal static class ProjectLauncher
{
	public static bool Open(string cwd)
	{
		try
		{
			if (!Directory.Exists(cwd) || Environment.ProcessPath is not { } executable)
			{
				return false;
			}

			var info = new ProcessStartInfo(executable)
			{
				UseShellExecute = false,
				WorkingDirectory = Path.GetFullPath(cwd),
			};
			if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
			{
				info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "piweft-desktop.dll"));
			}

			info.ArgumentList.Add("--cwd");
			info.ArgumentList.Add(info.WorkingDirectory);
			using var process = Process.Start(info);
			return process is not null;
		}
		catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
		{
			AppLog.Failure(AppLog.For<MainWindow>(), "Launch project window", ex);
			return false;
		}
	}
}
