using System.Diagnostics;

namespace PiWeft.Mcp;

/// <summary>
/// stdio MCP 服务器的启动环境。
///
/// 图形外壳启动的进程只继承最小环境：macOS 的 Finder/launchd 给的 PATH 通常只有
/// <c>/usr/bin:/bin:/usr/sbin:/sbin</c>，Homebrew、nvm、fnm 等安装的 <c>node</c> 不在其中，
/// 于是 <c>mcp.json</c> 里的 <c>"command": "node"</c> 会以 ENOENT 失败，报错形如
/// <c>An error occurred trying to start process 'node' with working directory '/'. No such file or directory</c>
/// —— 报错里的工作目录 <c>/</c> 也来自同一次图标启动。
///
/// 这里按 PiDeck <c>PiLocator</c> 的顺序组装子进程 PATH：**env PATH → 登录 shell PATH →
/// 包管理器/版本管理器目录 → 平台兜底目录**。顺序即优先级，已有 PATH 永远占先；新增目录
/// 只能追加到同类之后，否则会静默改掉用户原本启动的可执行文件。
///
/// 探测登录 shell 必须异步：同步读会在登录脚本慢或卡住时冻结调用线程（PiDeck 的 M1 根因）。
/// 因此 <see cref="EffectivePath"/> 只读缓存、绝不阻塞，<see cref="WarmAsync"/> 负责预热。
/// </summary>
internal static class McpLaunchEnvironment
{
	/// <summary>登录 shell 探测超时，与 PiDeck 的 3000 ms 一致。</summary>
	internal static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

	private static readonly Lock Gate = new();
	private static string? _shellPath;
	private static bool _probed;
	private static Task<string>? _probe;

	/// <summary>
	/// 子进程 PATH：env PATH、已预热到的登录 shell PATH、常见二进制目录合并去重。
	/// 只读缓存，不触发探测；未预热时只含 env PATH 与常见目录。
	/// </summary>
	internal static string EffectivePath => MergePath(Parts());

	private static IEnumerable<string?> Parts()
	{
		yield return Environment.GetEnvironmentVariable("PATH");
		yield return CachedLoginShellPath();
		foreach (var directory in WellKnownDirectories())
		{
			yield return directory;
		}
	}

	/// <summary>解析可执行文件并返回子进程 PATH；解析失败时给出含搜索路径的错误。</summary>
	internal static async Task<(string FileName, string Path)> PrepareAsync(string command)
	{
		await WarmAsync().ConfigureAwait(false);

		var path = EffectivePath;
		if (ResolveCommand(command, path) is { } resolved)
		{
			return (resolved, path);
		}

		// Windows 不改写命令：图形外壳已带系统合并 PATH，CreateProcess 另有查找规则，
		// 保持既有行为比统一语义更重要。
		if (OperatingSystem.IsWindows())
		{
			return (command, path);
		}

		throw new InvalidOperationException(NotFoundMessage(command, path));
	}

	/// <summary>命令解析失败时的说明：列出搜索过的 PATH，并给出可操作的下一步。</summary>
	internal static string NotFoundMessage(string command, string path) =>
		$"command '{command}' was not found. PATH searched: {path}. " +
		(OperatingSystem.IsMacOS()
			? "An app started from Finder only inherits the system PATH: use an absolute command path or set \"env\" for this server."
			: "Use an absolute command path or add its directory to PATH.");

	/// <summary>
	/// 异步预热登录 shell 的 PATH。并发调用共享同一次探测，失败或超时按空串负缓存。
	/// Windows 没有对应方式，图形外壳已拿到系统合并 PATH。
	/// </summary>
	internal static Task WarmAsync()
	{
		if (OperatingSystem.IsWindows())
		{
			return Task.CompletedTask;
		}

		lock (Gate)
		{
			if (_probed)
			{
				return Task.CompletedTask;
			}

			return _probe ??= ProbeAsync();
		}
	}

	private static async Task<string> ProbeAsync()
	{
		var path = await ReadLoginShellPathAsync().ConfigureAwait(false);
		lock (Gate)
		{
			_shellPath = path;
			_probed = true;
		}

		return path;
	}

	private static string? CachedLoginShellPath()
	{
		lock (Gate)
		{
			return _probed ? _shellPath : null;
		}
	}

	/// <summary>
	/// 读取登录 shell 的 PATH：<c>/bin/sh -lc 'printf %s "$PATH"'</c>。
	/// 超时、无法启动或非零退出都返回空串，调用方只退化到 env PATH。
	/// </summary>
	internal static async Task<string> ReadLoginShellPathAsync()
	{
		try
		{
			var startInfo = new ProcessStartInfo("/bin/sh")
			{
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
			};
			startInfo.ArgumentList.Add("-lc");
			startInfo.ArgumentList.Add("printf %s \"$PATH\"");

			using var process = Process.Start(startInfo);
			if (process is null)
			{
				return string.Empty;
			}

			// 两个管道都要读走，否则话多的登录脚本会把子进程堵死。
			var stdout = process.StandardOutput.ReadToEndAsync();
			var stderr = process.StandardError.ReadToEndAsync();

			using var timeout = new CancellationTokenSource(ProbeTimeout);
			try
			{
				await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				Kill(process);
				return string.Empty;
			}

			var text = await stdout.ConfigureAwait(false);
			_ = await stderr.ConfigureAwait(false);
			return process.ExitCode == 0 ? text.Trim() : string.Empty;
		}
		catch (Exception)
		{
			// 探测是尽力而为：任何失败都只意味着本次没有登录 shell PATH。
			return string.Empty;
		}
	}

	private static void Kill(Process process)
	{
		try
		{
			if (!process.HasExited)
			{
				process.Kill(entireProcessTree: true);
			}
		}
		catch (Exception)
		{
			// 进程已经退出。
		}
	}

	/// <summary>
	/// 把不含目录的命令按 <paramref name="path"/> 逐目录解析成绝对路径。
	/// 含目录分隔符的命令原样返回：相对路径的基准是子进程的工作目录，
	/// 在这里改写成绝对路径会破坏该语义。
	/// </summary>
	internal static string? ResolveCommand(string command, string path)
	{
		if (string.IsNullOrWhiteSpace(command))
		{
			return null;
		}

		if (HasDirectory(command))
		{
			return command;
		}

		foreach (var directory in Split(path))
		{
			var candidate = Path.Combine(directory, command);
			if (File.Exists(candidate))
			{
				return candidate;
			}

			if (OperatingSystem.IsWindows() && File.Exists(candidate + ".exe"))
			{
				return candidate + ".exe";
			}
		}

		return null;
	}

	/// <summary>命令是否已带目录（绝对路径或相对路径），这类命令不做 PATH 查找。</summary>
	internal static bool HasDirectory(string command) => command.Contains('/') || command.Contains('\\');

	/// <summary>按顺序合并多个 PATH 片段：去重，保留首次出现的顺序。</summary>
	internal static string MergePath(IEnumerable<string?> sources)
	{
		var seen = new HashSet<string>(StringComparer.Ordinal);
		var directories = new List<string>();
		foreach (var source in sources)
		{
			foreach (var directory in Split(source))
			{
				if (seen.Add(directory))
				{
					directories.Add(directory);
				}
			}
		}

		return string.Join(Path.PathSeparator, directories);
	}

	private static IEnumerable<string> Split(string? path) =>
		string.IsNullOrEmpty(path)
			? []
			: path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

	/// <summary>
	/// 常见可执行文件目录：图形外壳启动时往往缺这些目录，而 MCP 服务器多用 node/npx/uv 启动。
	/// 只追加到 PATH 之后，不代表这些目录比用户 PATH 优先。
	/// </summary>
	internal static IEnumerable<string> WellKnownDirectories()
	{
		var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		var entries = new List<string>();

		if (OperatingSystem.IsWindows())
		{
			entries.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"));
			entries.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "pnpm"));
			entries.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Volta", "bin"));
			entries.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Yarn", "bin"));
			entries.Add(Path.Combine(home, "scoop", "shims"));
			return Absolutes(entries);
		}

		// 版本管理器：每个 node 版本各有一个 bin 目录，只靠 <root>/current 会漏掉未切软链的机器。
		// 顺序按版本号从新到旧：目录名字典序会把 v12 排在 v24 前面，而 PATH 查找按顺序取
		// 第一个命中——GUI 启动就会解析到最老的 node，跑不了新语法（如 ??），服务器直接
		// SyntaxError 退出（Node 12 + `??` 的真实案例）。nvm 的 default 别名最贴近用户
		// 终端里的选择，解析得到就放在最前。
		entries.Add(Path.Combine(home, ".nvm", "current", "bin"));
		var nvmVersions = ChildDirectories(Path.Combine(home, ".nvm", "versions", "node"));
		if (NvmDefaultBin(home) is { Length: > 0 } nvmDefault)
		{
			entries.Add(nvmDefault);
		}

		entries.AddRange(OrderVersionsNewestFirst(nvmVersions)
			.Select(version => Path.Combine(version, "bin")));

		var fnmRoots = new List<string>();
		if (Environment.GetEnvironmentVariable("FNM_DIR") is { Length: > 0 } fnmDir)
		{
			fnmRoots.Add(fnmDir);
		}

		fnmRoots.Add(Path.Combine(home, ".fnm"));
		fnmRoots.Add(Path.Combine(XdgDataHome(home), "fnm"));
		if (OperatingSystem.IsMacOS())
		{
			fnmRoots.Add(Path.Combine(home, "Library", "Application Support", "fnm"));
		}

		foreach (var root in fnmRoots)
		{
			entries.Add(Path.Combine(root, "current", "bin"));
			entries.AddRange(OrderVersionsNewestFirst(ChildDirectories(Path.Combine(root, "node-versions")))
				.Select(version => Path.Combine(version, "installation", "bin")));
		}

		var pnpmHome = Environment.GetEnvironmentVariable("PNPM_HOME");
		if (string.IsNullOrEmpty(pnpmHome))
		{
			pnpmHome = OperatingSystem.IsMacOS()
				? Path.Combine(home, "Library", "pnpm")
				: Path.Combine(home, ".local", "share", "pnpm");
		}

		entries.Add(pnpmHome);

		// 全局安装与独立工具的落点；.asdf/.mise 的 shims 是它们的统一入口。
		entries.Add(Path.Combine(home, ".npm-global", "bin"));
		entries.Add(Path.Combine(home, ".volta", "bin"));
		entries.Add(Path.Combine(home, ".asdf", "shims"));
		entries.Add(Path.Combine(home, ".local", "share", "mise", "shims"));
		entries.Add(Path.Combine(home, ".bun", "bin"));
		entries.Add(Path.Combine(home, ".deno", "bin"));
		entries.Add(Path.Combine(home, ".local", "bin"));
		entries.Add(Path.Combine(home, "bin"));

		// 平台兜底：macOS 图形外壳拿不到 Homebrew，Apple Silicon 与 Intel 的默认前缀都补上。
		if (OperatingSystem.IsMacOS())
		{
			entries.Add("/opt/homebrew/bin");
			entries.Add("/opt/homebrew/sbin");
			entries.Add("/usr/local/bin");
			entries.Add("/usr/local/sbin");
		}
		else if (OperatingSystem.IsLinux())
		{
			entries.Add("/usr/local/bin");
			entries.Add("/usr/local/sbin");
			entries.Add("/usr/bin");
		}

		return Absolutes(entries);
	}

	/// <summary>
	/// 丢掉空值与相对路径：主目录取不到时 <see cref="Path.Combine(string, string)"/> 会生成
	/// <c>.nvm/current/bin</c> 这类相对目录，放进 PATH 会按当前工作目录解析，等于引入未定义行为。
	/// </summary>
	private static IEnumerable<string> Absolutes(IEnumerable<string> entries) =>
		entries.Where(static entry => entry.Length > 0 && Path.IsPathRooted(entry));

	private static string XdgDataHome(string home) =>
		Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } xdg
			? xdg
			: Path.Combine(home, ".local", "share");

	private static IEnumerable<string> ChildDirectories(string parent)
	{
		try
		{
			return Directory.Exists(parent)
				? Directory.GetDirectories(parent)
				: [];
		}
		catch (Exception)
		{
			// 目录不可读时按不存在处理。
			return [];
		}
	}

	/// <summary>
	/// 版本目录按版本号从新到旧排序。"v12.22.12" 这类名字的字典序与版本号无关（v12 排在
	/// v24 前面），而 PATH 查找按顺序取第一个命中，排序错了 GUI 启动就会拿到最老的 node。
	/// 版本号解析不出的目录排在最后，彼此仍按名字典序。
	/// </summary>
	internal static IEnumerable<string> OrderVersionsNewestFirst(IEnumerable<string> directories) =>
		directories
			.Select(directory => (Directory: directory, Key: VersionKey(directory)))
			.OrderByDescending(entry => entry.Key, VersionKeyComparer.Instance)
			.Select(entry => entry.Directory);

	private static (ulong Major, ulong Minor, ulong Patch, bool HasVersion, string Name) VersionKey(string directory)
	{
		var name = Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
		// nvm/fnm 是 "v12.22.12"，mise 是 "20.15.0"：跳过可选的 "v" 后逐段解析数字。
		var digits = name.StartsWith('v') ? name[1..] : name;
		var major = 0UL;
		var minor = 0UL;
		var patch = 0UL;
		var hasVersion = false;

		foreach (var (index, value) in Segments(digits))
		{
			switch (index)
			{
				case 0 when TryParseUlong(value, out var parsed):
					major = parsed;
					hasVersion = true;
					break;
				case 1 when TryParseUlong(value, out var parsed):
					minor = parsed;
					break;
				case 2 when TryParseUlong(value, out var parsed):
					patch = parsed;
					break;
				default:
					break; // 只认三段数字，之后的段（如 "-rc.1" 的残余）不参与比较。
			}
		}

		return (major, minor, patch, hasVersion, name);

		static IEnumerable<(int Index, string Value)> Segments(string text)
		{
			var parts = text.Split('.');
			for (var index = 0; index < parts.Length && index < 3; index++)
			{
				yield return (index, parts[index]);
			}
		}
	}

	private static bool TryParseUlong(string text, out ulong value)
	{
		value = 0;
		if (text.Length == 0)
		{
			return false;
		}

		foreach (var character in text)
		{
			if (character is < '0' or > '9')
			{
				return false;
			}

			value = checked(value * 10 + (ulong)(character - '0'));
		}

		return true;
	}

	private sealed class VersionKeyComparer : IComparer<(ulong Major, ulong Minor, ulong Patch, bool HasVersion, string Name)>
	{
		internal static readonly VersionKeyComparer Instance = new();

		public int Compare(
			(ulong Major, ulong Minor, ulong Patch, bool HasVersion, string Name) left,
			(ulong Major, ulong Minor, ulong Patch, bool HasVersion, string Name) right)
		{
			// 自然升序（版本小在前），方向由 OrderByDescending 反转：有版本号的排前面，
			// 版本从新到旧；版本相同时按名字字典序，保证结果确定。
			var byHasVersion = left.HasVersion.CompareTo(right.HasVersion);
			if (byHasVersion != 0)
			{
				return byHasVersion;
			}

			var byMajor = left.Major.CompareTo(right.Major);
			if (byMajor != 0)
			{
				return byMajor;
			}

			var byMinor = left.Minor.CompareTo(right.Minor);
			if (byMinor != 0)
			{
				return byMinor;
			}

			var byPatch = left.Patch.CompareTo(right.Patch);
			return byPatch != 0 ? byPatch : string.CompareOrdinal(left.Name, right.Name);
		}
	}

	/// <summary>
	/// 解析 nvm 的 default 别名指向的 bin 目录（最贴近用户终端里 <c>node</c> 的版本）：
	/// <c>alias/default</c> → 版本号或再一层别名 → <c>versions/node/&lt;版本&gt;</c>，
	/// 别名只写到部分版本号（如 <c>24.15</c>）时取匹配的最高已装版本。
	/// 解析不出来（无别名、lts 名、格式不符）返回 null，退回“从新到旧”的排序。
	/// </summary>
	internal static string? NvmDefaultBin(string home)
	{
		if (string.IsNullOrEmpty(home))
		{
			return null;
		}

		try
		{
			var aliasDir = Path.Combine(home, ".nvm", "alias");
			var target = ReadAlias(aliasDir, "default");
			if (target.Length == 0 || target.Contains('/'))
			{
				// 无别名，或 lts 代号（"lts/hydrogen"）这类不带版本号的形态。
				return null;
			}

			var versionsRoot = Path.Combine(home, ".nvm", "versions", "node");
			for (var hop = 0; hop < 2; hop++)
			{
				var exact = Path.Combine(versionsRoot, target);
				if (Directory.Exists(exact))
				{
					return Path.Combine(exact, "bin");
				}

				var next = ReadAlias(aliasDir, target);
				if (next.Length == 0)
				{
					break;
				}

				target = next;
			}

			// 别名只写了部分版本号（"24.15" → v24.15.0）：取匹配的最高版本。
			var prefix = target.StartsWith('v') ? target[1..] : target;
			return OrderVersionsNewestFirst(Directory.GetDirectories(versionsRoot)
					.Where(directory => Path.GetFileName(directory).TrimStart('v').StartsWith(prefix, StringComparison.Ordinal)))
				.Select(directory => Path.Combine(directory, "bin"))
				.FirstOrDefault();
		}
		catch (Exception)
		{
			// 别名解析是尽力而为：任何失败都只意味着走“从新到旧”的排序。
			return null;
		}
	}

	private static string ReadAlias(string aliasDir, string name)
	{
		try
		{
			var path = Path.Combine(aliasDir, name);
			return File.Exists(path) ? File.ReadAllText(path).Trim() : string.Empty;
		}
		catch (Exception)
		{
			return string.Empty;
		}
	}
}
