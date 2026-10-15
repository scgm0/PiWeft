namespace PiWeft.Desktop;

/// <summary>
/// 启动目录兜底。
///
/// 图形外壳启动的进程不继承终端的工作目录：macOS 的 launchd 把 cwd 设为 <c>/</c>，于是「项目」
/// 变成文件系统根目录 —— 窗口标题显示 <c>PiWeft Desktop — /</c>，会话写进按 <c>/</c> 编码的目录，
/// 文件浏览器、Git 面板与 MCP 子进程的工作目录也都落在根目录上。
///
/// 终端启动（或显式 <c>--cwd</c>）不做任何推断，保持原样。
/// </summary>
internal static class StartupDirectory
{
	/// <summary>
	/// 是否由图形外壳而非终端启动。macOS 的 Finder 会写入 <c>__CFBundleIdentifier</c>；
	/// 终端启动的进程 <c>XPC_SERVICE_NAME</c> 为 <c>0</c>。
	/// </summary>
	internal static bool LaunchedByShell => IsShellLaunch(
		OperatingSystem.IsMacOS(),
		Environment.GetEnvironmentVariable("__CFBundleIdentifier"),
		Environment.GetEnvironmentVariable("XPC_SERVICE_NAME"));

	/// <summary>图形外壳启动的判定，参数化以便测试（本机平台与 macOS 的启动标记无关）。</summary>
	internal static bool IsShellLaunch(bool isMacOS, string? bundleIdentifier, string? xpcServiceName) =>
		isMacOS && (bundleIdentifier is { Length: > 0 } || xpcServiceName is { Length: > 0 } and not "0");

	/// <summary>
	/// 选择启动目录：从二进制目录启动、或图形外壳把 cwd 丢成文件系统根时，
	/// 依次退回最近项目、用户主目录；其余情况一律原样返回。
	/// </summary>
	internal static string Resolve(
		string cwd,
		string baseDirectory,
		bool launchedByShell,
		IReadOnlyList<string> recentProjects,
		string homeDirectory)
	{
		var recent = recentProjects.FirstOrDefault(Directory.Exists);

		if (SameDirectory(cwd, baseDirectory))
		{
			return recent ?? cwd;
		}

		if (!launchedByShell || !IsFileSystemRoot(cwd))
		{
			return cwd;
		}

		return recent ?? (Directory.Exists(homeDirectory) ? homeDirectory : cwd);
	}

	/// <summary>cwd 是否就是文件系统根（<c>/</c>、<c>C:\</c>）。</summary>
	internal static bool IsFileSystemRoot(string cwd)
	{
		var full = Path.GetFullPath(cwd);
		return string.Equals(full, Path.GetPathRoot(full), StringComparison.Ordinal);
	}

	private static bool SameDirectory(string left, string right) =>
		string.Equals(
			Path.TrimEndingDirectorySeparator(left),
			Path.TrimEndingDirectorySeparator(right),
			StringComparison.Ordinal);
}
