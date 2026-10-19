namespace PiWeft.Desktop;

/// <summary>终端可选择的一个 shell。</summary>
internal sealed record TerminalShellOption(string Path, string Name);

/// <summary>
/// 命令 Dock 的 shell 发现与解析：读取 <c>$SHELL</c>、<c>/etc/shells</c> 与常见路径，
/// 也接受用户在 <c>desktop.json</c> 里写下的可执行文件路径。只影响终端面板，不改系统登录 shell。
/// </summary>
internal static class TerminalShells
{
	/// <summary>这些 shell 接受 <c>-i</c>；其它（nu、csh 派生等）直接启动，参数由 shell 自己决定。</summary>
	private static readonly string[] InteractiveFlagShells = ["bash", "sh", "zsh", "ksh", "dash", "fish"];

	private static readonly string[] CommonUnixShells =
	[
		"/bin/bash", "/bin/zsh", "/bin/fish", "/bin/sh", "/usr/bin/fish", "/usr/local/bin/fish",
	];

	/// <summary>当前可用的 shell 列表（按路径去重），自动项不包含在内。</summary>
	public static IReadOnlyList<TerminalShellOption> Detect()
	{
		var options = new List<TerminalShellOption>();
		var seen = new HashSet<string>(StringComparer.Ordinal);

		void Add(string? path, string? name = null)
		{
			if (string.IsNullOrWhiteSpace(path))
			{
				return;
			}

			var full = Expand(path.Trim());
			if (!File.Exists(full))
			{
				return;
			}

			// /bin 与 /usr/bin 在 usrmerge 后是同一批文件，按真实路径去重，
			// 否则菜单里会出现两组 bash/sh/zsh。
			if (!seen.Add(RealPath(full)))
			{
				return;
			}

			options.Add(new TerminalShellOption(full, name ?? System.IO.Path.GetFileName(full)));
		}

		if (OperatingSystem.IsWindows())
		{
			Add(Environment.GetEnvironmentVariable("ComSpec"));
			foreach (var name in new[] { "pwsh.exe", "powershell.exe" })
			{
				Add(FindOnPath(name), name);
			}

			return options;
		}

		Add(Environment.GetEnvironmentVariable("SHELL"));
		foreach (var path in ReadEtcShells())
		{
			Add(path);
		}

		foreach (var path in CommonUnixShells)
		{
			Add(path);
		}

		return options;
	}

	/// <summary>把偏好里的值解析成可执行文件路径；空或无效时回退系统默认。</summary>
	public static string Resolve(string configured)
	{
		if (!string.IsNullOrWhiteSpace(configured))
		{
			var expanded = Expand(configured.Trim());
			if (File.Exists(expanded))
			{
				return expanded;
			}

			if (FindOnPath(configured.Trim()) is { } found)
			{
				return found;
			}
		}

		if (OperatingSystem.IsWindows())
		{
			return Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
		}

		if (Environment.GetEnvironmentVariable("SHELL") is { Length: > 0 } shell && File.Exists(shell))
		{
			return shell;
		}

		foreach (var candidate in new[] { "/bin/bash", "/bin/zsh", "/bin/sh" })
		{
			if (File.Exists(candidate))
			{
				return candidate;
			}
		}

		return "/bin/sh";
	}

	/// <summary>状态栏与按钮上显示的名字：自动时显示解析到的名字。</summary>
	public static string DisplayName(string configured) =>
		string.IsNullOrWhiteSpace(configured) ? System.IO.Path.GetFileName(Resolve("")) : System.IO.Path.GetFileName(Resolve(configured));

	/// <summary>配置值是否能在本机解析成可执行文件（用于自定义输入的校验）。</summary>
	public static bool IsExecutable(string configured)
	{
		if (string.IsNullOrWhiteSpace(configured))
		{
			return false;
		}

		var expanded = Expand(configured.Trim());
		return File.Exists(expanded) || FindOnPath(configured.Trim()) is not null;
	}

	/// <summary>启动参数：POSIX 交互 shell 用 <c>-i</c>，其它保持空。</summary>
	public static IReadOnlyList<string> Arguments(string shell)
	{
		if (OperatingSystem.IsWindows())
		{
			return [];
		}

		var name = System.IO.Path.GetFileNameWithoutExtension(shell);
		return InteractiveFlagShells.Contains(name, StringComparer.OrdinalIgnoreCase) ? ["-i"] : [];
	}

	/// <summary>符号链接解析后的真实路径；解析失败时返回原路径。</summary>
	internal static string RealPath(string path)
	{
		try
		{
			return File.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName ?? path;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			return path;
		}
	}

	internal static string Expand(string path)
	{
		if (path == "~")
		{
			return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		}

		if (path.StartsWith("~/", StringComparison.Ordinal))
		{
			return System.IO.Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
				path[2..]);
		}

		return path;
	}

	private static string? FindOnPath(string name)
	{
		if (name.Contains('/') || name.Contains('\\'))
		{
			return null;
		}

		foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
			.Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
		{
			var candidate = System.IO.Path.Combine(directory, name);
			if (File.Exists(candidate))
			{
				return candidate;
			}
		}

		return null;
	}

	private static IEnumerable<string> ReadEtcShells()
	{
		try
		{
			if (!File.Exists("/etc/shells"))
			{
				return [];
			}

			return File.ReadLines("/etc/shells")
				.Select(static line => line.Trim())
				.Where(static line => line.Length > 0 && !line.StartsWith('#'))
				.ToArray();
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			return [];
		}
	}
}
