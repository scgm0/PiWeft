namespace PiWeft.CodingAgent.Session;

/// <summary>
/// 用户给出的路径的规范化，对应 pi 的 <c>normalizePath</c>。
/// <para>
/// 重点是 <c>~</c> 展开：配置里写 <c>~/skills/pdf</c> 或 <c>PI_CODING_AGENT_DIR=~/pi-agent</c> 时，
/// <see cref="Path.GetFullPath(string)"/> 会把它当成相对路径拼到当前目录下（得到 <c>&lt;cwd&gt;/~/...</c>），
/// 于是路径永远不存在。pi 1.2.0 修的正是这一类问题。
/// </para>
/// </summary>
public static class UserPath
{
	/// <summary>展开开头的 <c>~</c>，其余原样返回。</summary>
	public static string ExpandTilde(string path, string? homeDirectory = null)
	{
		if (string.IsNullOrEmpty(path) || path[0] != '~')
		{
			return path;
		}

		var home = homeDirectory
			?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

		if (home.Length == 0)
		{
			return path;
		}

		if (path.Length == 1)
		{
			return home;
		}

		// 只处理 "~/" 与 "~\\"：~user 这种写法在跨平台下含义不一致，保持原样交给系统。
		if (path[1] is '/' or '\\')
		{
			return Path.Combine(home, path[2..]);
		}

		return path;
	}

	/// <summary>展开 <c>~</c> 后转成绝对路径。</summary>
	public static string Resolve(string path, string? homeDirectory = null) =>
		Path.GetFullPath(ExpandTilde(path, homeDirectory));
}
