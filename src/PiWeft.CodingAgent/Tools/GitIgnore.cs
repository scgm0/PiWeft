using System.Text;
using System.Text.RegularExpressions;

namespace PiWeft.CodingAgent.Tools;

/// 忽略规则的来源。嵌套仓库只丢掉祖先仓库的本地 VCS 规则；全局 git 忽略与非 VCS 规则保留。
internal enum IgnoreSource
{
	LocalGit,
	GlobalGit,
	Always,
}

/// grep 跟 ripgrep 的 ignore 语义，find 跟 fd 的。
internal enum IgnoreFlavor
{
	Ripgrep,
	Fd,
}

/// 遍历参数：ignore 方言决定读哪些 ignore 文件，也决定嵌套仓库边界怎么处理。
internal sealed record FileWalkOptions
{
	public IgnoreFlavor Flavor { get; init; } = IgnoreFlavor.Fd;

	public bool SkipHidden { get; init; }

	public bool IncludeGitDirectory { get; init; }

	public string FlavorIgnoreFileName => Flavor == IgnoreFlavor.Fd ? ".fdignore" : ".rgignore";
}

/// 供 <c>grep</c> 与 <c>find</c> 遍历时使用的 <c>.gitignore</c> 匹配器。pi 把忽略规则交给 ripgrep/fd，
/// 本端口在进程内实现常用子集，因而不需要外部可执行文件。
public sealed class GitIgnore
{
	private readonly List<IgnoreRule> _rules = [];

	/// 所有规则共用的基准目录（逐目录层都是这种情况）。合并层可能是 null，
	/// 那时按规则自带的基准分组算相对路径。
	private string? _singleBase;

	public int Count => _rules.Count;

	public static GitIgnore Empty { get; } = new();

	public static GitIgnore FromDirectory(string directory) =>
		FromFile(Path.Combine(directory, ".gitignore"), directory);

	public static GitIgnore FromFile(string path, string baseDirectory) =>
		FromFile(path, baseDirectory, IgnoreSource.LocalGit);

	internal static GitIgnore FromFile(string path, string baseDirectory, IgnoreSource source)
	{
		if (!File.Exists(path))
		{
			// 不能返回共享的 Empty：调用方会用 AddRange 把 .ignore/.rgignore 叠加进来。
			return new GitIgnore();
		}

		try
		{
			return Parse(File.ReadAllLines(path), baseDirectory, source);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return new GitIgnore();
		}
	}

	/// 搜索起点在子目录时，ripgrep/fd 仍会应用父目录的 ignore 文件。
	/// VCS 规则按仓库根截断（仓库根之上的 .gitignore 不生效），非 VCS 规则
	/// （.ignore / .rgignore / .fdignore）不受仓库边界限制；fd 的 --no-require-git
	/// 还会在非仓库目录里读父级 .gitignore 与全局 git 忽略。
	internal static GitIgnore ForWalkRoot(string root, FileWalkOptions options)
	{
		var directory = Directory.Exists(root)
			? Path.GetFullPath(root)
			: Path.GetDirectoryName(Path.GetFullPath(root)) ?? Path.GetFullPath(root);
		if (directory.Length == 0)
		{
			return Empty;
		}

		var combined = new GitIgnore();
		// 非 VCS 规则从文件系统根一路读下来；顺序由低到高，子目录规则后读。
		for (var current = Path.GetDirectoryName(directory); current is { Length: > 0 }; current = Path.GetDirectoryName(current))
		{
			combined.AddRange(FromFile(Path.Combine(current, ".ignore"), current, IgnoreSource.Always));
			combined.AddRange(FromFile(Path.Combine(current, options.FlavorIgnoreFileName), current, IgnoreSource.Always));
		}

		if (options.Flavor == IgnoreFlavor.Fd)
		{
			combined.AddRange(FdGlobal(directory));
		}

		var gitRoot = FindGitRoot(directory);
		// fd 用 --no-require-git：不在仓库里也读 .gitignore / 全局 git 忽略，且不按仓库边界重置。
		var vcsActive = gitRoot is not null || options.Flavor == IgnoreFlavor.Fd;
		if (!vcsActive)
		{
			return combined;
		}

		var baseDirectory = gitRoot ?? directory;
		combined.AddRange(Global(baseDirectory));

		if (gitRoot is not null)
		{
			// 仓库根之上的 .gitignore 不生效；仓库根自己的规则留给 Walker 读。
			var chain = new List<string>();
			if (!SamePath(directory, gitRoot))
			{
				for (var current = Path.GetDirectoryName(directory); current is { Length: > 0 }; current = Path.GetDirectoryName(current))
				{
					chain.Add(current);
					if (SamePath(current, gitRoot))
					{
						break;
					}
				}
			}

			if (chain.Count > 0)
			{
				combined.AddRange(FromFile(Path.Combine(gitRoot, ".git", "info", "exclude"), gitRoot, IgnoreSource.LocalGit));
				for (var index = chain.Count - 1; index >= 0; index--)
				{
					combined.AddRange(FromFile(Path.Combine(chain[index], ".gitignore"), chain[index], IgnoreSource.LocalGit));
				}
			}
		}
		else
		{
			for (var current = Path.GetDirectoryName(directory); current is { Length: > 0 }; current = Path.GetDirectoryName(current))
			{
				combined.AddRange(FromFile(Path.Combine(current, ".gitignore"), current, IgnoreSource.LocalGit));
			}
		}

		return combined;
	}

	/// git 的全局忽略源：<c>$XDG_CONFIG_HOME/git/ignore</c>（缺省 <c>~/.config/git/ignore</c>）
	/// 与 <c>core.excludesFile</c> 指到的文件。ripgrep 与 fd 都读它们，进程内实现也要读，
	/// 否则「本机早就忽略掉的构建产物」会重新出现在 grep/find 的结果里。
	public static GitIgnore Global(string baseDirectory)
	{
		var combined = new GitIgnore();
		foreach (var path in GlobalFiles())
		{
			combined.AddRange(FromFile(path, baseDirectory, IgnoreSource.GlobalGit));
		}

		return combined;
	}

	/// fd 自己的全局忽略文件：<c>$XDG_CONFIG_HOME/fd/ignore</c>（缺省 <c>~/.config/fd/ignore</c>）。
	internal static GitIgnore FdGlobal(string baseDirectory)
	{
		var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		if (home.Length == 0)
		{
			return Empty;
		}

		var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
		var xdg = string.IsNullOrEmpty(configHome) ? Path.Combine(home, ".config") : configHome;
		return FromFile(Path.Combine(xdg, "fd", "ignore"), baseDirectory, IgnoreSource.Always);
	}

	/// 嵌套仓库边界：丢掉从祖先仓库继承的本地 VCS 规则，保留全局 git 忽略与非 VCS 规则。
	internal GitIgnore WithoutLocalGit()
	{
		if (!_rules.Any(static rule => rule.Source == IgnoreSource.LocalGit))
		{
			return this;
		}

		var copy = new GitIgnore();
		copy._rules.AddRange(_rules.Where(static rule => rule.Source != IgnoreSource.LocalGit));
		copy._singleBase = copy._rules.Count > 0 && copy._rules.All(rule => string.Equals(rule.BaseDirectory, copy._rules[0].BaseDirectory, StringComparison.Ordinal))
			? copy._rules[0].BaseDirectory
			: null;
		return copy;
	}

	private static IEnumerable<string> GlobalFiles()
	{
		var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		if (home.Length == 0)
		{
			yield break;
		}

		var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
		var xdg = string.IsNullOrEmpty(configHome) ? Path.Combine(home, ".config") : configHome;
		yield return Path.Combine(xdg, "git", "ignore");

		foreach (var configFile in new[] { Path.Combine(xdg, "git", "config"), Path.Combine(home, ".gitconfig") })
		{
			if (ReadCoreExcludesFile(configFile) is { } excludes)
			{
				yield return excludes;
			}
		}
	}

	/// 只读 <c>[core] excludesfile</c> 一行：不必为了一个路径去实现整套 git 配置语法。
	private static string? ReadCoreExcludesFile(string configFile)
	{
		if (!File.Exists(configFile))
		{
			return null;
		}

		try
		{
			var section = string.Empty;
			foreach (var raw in File.ReadLines(configFile))
			{
				var line = raw.Trim();
				if (line.Length == 0 || line[0] is '#' or ';')
				{
					continue;
				}

				if (line[0] == '[' && line[^1] == ']')
				{
					section = line[1..^1].Trim();
					continue;
				}

				var separator = line.IndexOf('=');
				if (separator <= 0 || !string.Equals(section, "core", StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}

				var key = line[..separator].Trim();
				if (!string.Equals(key, "excludesfile", StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}

				var value = line[(separator + 1)..].Trim().Trim('"');
				if (value.StartsWith('~'))
				{
					var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
					value = value.Length > 1 && value[1] == '/'
						? Path.Combine(home, value[2..])
						: home;
				}

				// 相对路径的语义随 cwd 变，这里只接受绝对路径（git 文档也不推荐相对路径）。
				return Path.IsPathRooted(value) ? value : null;
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			// 读不到全局配置就当没有：忽略规则缺失只会多列文件，不能因此让工具失败。
		}

		return null;
	}

	internal static string? FindGitRoot(string directory)
	{
		for (var current = directory; current is { Length: > 0 }; current = Path.GetDirectoryName(current))
		{
			if (HasGitMarker(current))
			{
				return current;
			}
		}

		return null;
	}

	/// 仓库标记既可能是目录（普通仓库），也可能是文件（worktree / submodule）。
	internal static bool HasGitMarker(string directory)
	{
		var marker = Path.Combine(directory, ".git");
		return Directory.Exists(marker) || File.Exists(marker);
	}

	internal static bool SamePath(string left, string right) =>
		string.Equals(Path.GetFullPath(left), Path.GetFullPath(right),
			OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

	public static GitIgnore Parse(IEnumerable<string> lines, string baseDirectory) =>
		Parse(lines, baseDirectory, IgnoreSource.LocalGit);

	internal static GitIgnore Parse(IEnumerable<string> lines, string baseDirectory, IgnoreSource source)
	{
		var ignore = new GitIgnore();
		foreach (var raw in lines)
		{
			var rule = Compile(raw, baseDirectory, source);
			if (rule is not null)
			{
				ignore._rules.Add(rule);
			}
		}

		if (ignore._rules.Count > 0)
		{
			ignore._singleBase = baseDirectory;
		}

		return ignore;
	}

	public void AddRange(GitIgnore other)
	{
		if (ReferenceEquals(other, this) || other._rules.Count == 0)
		{
			return;
		}

		_rules.AddRange(other._rules);
		_singleBase = _rules.Count == other._rules.Count
			? other._singleBase
			: _rules[0].BaseDirectory;
		if (_singleBase is not null && _rules.Any(rule => !string.Equals(rule.BaseDirectory, _singleBase, StringComparison.Ordinal)))
		{
			_singleBase = null;
		}
	}

	public bool IsIgnored(string absolutePath, bool isDirectory) => Apply(absolutePath, isDirectory, ignored: false);

	/// 把本层规则叠加到已有判定上（跨层时后读的规则覆盖先读的，不能每层各自返回 true 后短路）。
	/// 相对路径按基准目录算一次：规则通常几十条，逐条 `GetRelativePath` 是遍历的主要成本之一。
	internal bool Apply(string absolutePath, bool isDirectory, bool ignored)
	{
		if (_rules.Count == 0)
		{
			return ignored;
		}

		if (_singleBase is { } single)
		{
			var relative = RelativeTo(single, absolutePath);
			if (relative is null)
			{
				return ignored;
			}

			foreach (var rule in _rules)
			{
				if (rule.DirectoryOnly && !isDirectory)
				{
					continue;
				}

				if (rule.Matches(relative))
				{
					ignored = !rule.Negated;
				}
			}

			return ignored;
		}

		// 合并层：按规则自己的基准分组，避免同一基准重复算相对路径。
		Dictionary<string, string?>? relatives = null;
		foreach (var rule in _rules)
		{
			if (rule.DirectoryOnly && !isDirectory)
			{
				continue;
			}

			relatives ??= new Dictionary<string, string?>(StringComparer.Ordinal);
			if (!relatives.TryGetValue(rule.BaseDirectory, out var relative))
			{
				relative = RelativeTo(rule.BaseDirectory, absolutePath);
				relatives[rule.BaseDirectory] = relative;
			}

			if (relative is not null && rule.Matches(relative))
			{
				ignored = !rule.Negated;
			}
		}

		return ignored;
	}

	/// 基准目录之外的路径返回 null（与 `GetRelativePath` 以 ".." 开头同义）。
	private static string? RelativeTo(string baseDirectory, string absolutePath)
	{
		var relative = Path.GetRelativePath(baseDirectory, absolutePath);
		if (relative.StartsWith("..", StringComparison.Ordinal))
		{
			return null;
		}

		return OperatingSystem.IsWindows() ? relative.Replace('\\', '/') : relative;
	}

	private static IgnoreRule? Compile(string raw, string baseDirectory, IgnoreSource source)
	{
		var line = raw;
		if (line.Length == 0 || line[0] == '#')
		{
			return null;
		}

		// 除非被转义，否则忽略行尾空格。
		var end = line.Length;
		while (end > 0 && line[end - 1] == ' ' && (end < 2 || line[end - 2] != '\\'))
		{
			end--;
		}

		line = line[..end];
		if (line.Length == 0)
		{
			return null;
		}

		var negated = line[0] == '!';
		if (negated)
		{
			line = line[1..];
		}

		if (line.StartsWith("\\#", StringComparison.Ordinal) || line.StartsWith("\\!", StringComparison.Ordinal))
		{
			line = line[1..];
		}

		var directoryOnly = line.EndsWith('/');
		line = line.TrimEnd('/');

		if (line.Length == 0)
		{
			return null;
		}

		var anchored = line.StartsWith('/') || line.Contains('/');
		var pattern = line.TrimStart('/');
		// 绝大多数规则是 `name`、`dir/`、`*.ext`：直接比文件名，别为每个条目跑一次正则。
		if (!anchored && TryFastPath(pattern, out var literal, out var suffix))
		{
			return new IgnoreRule(
				baseDirectory,
				Regex: null,
				Negated: negated,
				DirectoryOnly: directoryOnly,
				Source: source,
				Literal: literal,
				Suffix: suffix);
		}

		var regex = BuildRegex(pattern, anchored);
		return regex is null
			? null
			: new IgnoreRule(baseDirectory, regex, negated, directoryOnly, source, Literal: null, Suffix: null);
	}

	/// 无通配的普通名字，或只有前缀 `*.` 的扩展名规则。返回 true 时 <paramref name="literal"/>
	/// 或 <paramref name="suffix"/> 恰好有一个非空。
	private static readonly System.Buffers.SearchValues<char> Wildcards =
		System.Buffers.SearchValues.Create("*?[\\");

	private static bool TryFastPath(string pattern, out string? literal, out string? suffix)
	{
		literal = null;
		suffix = null;
		if (pattern.Length == 0 || pattern.Contains('/'))
		{
			return false;
		}

		if (pattern[0] == '*' && pattern.Length > 1 && pattern[1] == '.')
		{
			var rest = pattern[1..];
			if (rest.AsSpan().IndexOfAny(Wildcards) < 0)
			{
				suffix = rest;
				return true;
			}

			return false;
		}

		if (pattern.AsSpan().IndexOfAny(Wildcards) < 0)
		{
			literal = pattern;
			return true;
		}

		return false;
	}

	private static Regex? BuildRegex(string pattern, bool anchored)
	{
		var builder = new StringBuilder(pattern.Length * 2 + 16);
		builder.Append(anchored ? "^" : "(^|/)");

		for (var i = 0; i < pattern.Length; i++)
		{
			var c = pattern[i];
			switch (c)
			{
				case '*':
					if (i + 1 < pattern.Length && pattern[i + 1] == '*')
					{
						// "**/" 与 "/**" 都会跨目录匹配。
						i++;
						if (i + 1 < pattern.Length && pattern[i + 1] == '/')
						{
							i++;
							builder.Append("(.*/)?");
						}
						else
						{
							builder.Append(".*");
						}
					}
					else
					{
						builder.Append("[^/]*");
					}

					break;
				case '?':
					builder.Append("[^/]");
					break;
				case '[':
				{
					// gitignore 用 fnmatch 的字符类：'[!a]' 是取反，.NET 里要写成 '[^/a]'。
					// 照原样抄进去会让 '!' 变成普通字符，整条规则的意思反过来。
					var close = GlobSyntax.FindClassEnd(pattern, i + 1, pattern.Length);
					if (close > i + 1)
					{
						GlobSyntax.AppendClass(builder, pattern, i + 1, close);
						i = close;
					}
					else
					{
						builder.Append("\\[");
					}

					break;
				}
				case '\\':
					if (i + 1 < pattern.Length)
					{
						builder.Append(Regex.Escape(pattern[++i].ToString()));
					}
					else
					{
						builder.Append("\\\\");
					}

					break;
				default:
					builder.Append(Regex.Escape(c.ToString()));
					break;
			}
		}

		builder.Append('$');

		try
		{
			// NonBacktracking：gitignore 的正则由本方法生成，没有反向引用/环视；
			// 用户仓库里的怪规则也不会把遍历拖进指数回溯。
			var options = RegexOptions.CultureInvariant | RegexOptions.Compiled | RegexOptions.NonBacktracking;
			if (OperatingSystem.IsWindows())
			{
				options |= RegexOptions.IgnoreCase;
			}

			return new Regex(
				builder.ToString(),
				options,
				TimeSpan.FromSeconds(2));
		}
		catch (ArgumentException)
		{
			return null;
		}
	}

	private sealed record IgnoreRule(
		string BaseDirectory,
		Regex? Regex,
		bool Negated,
		bool DirectoryOnly,
		IgnoreSource Source,
		string? Literal,
		string? Suffix)
	{
		private static readonly StringComparison Comparison = OperatingSystem.IsWindows()
			? StringComparison.OrdinalIgnoreCase
			: StringComparison.Ordinal;

		/// <param name="relative">已按基准目录算好、并统一成 '/' 分隔的相对路径。</param>
		public bool Matches(string relative)
		{
			if (Regex is { } regex)
			{
				return regex.IsMatch(relative);
			}

			var slash = relative.LastIndexOf('/');
			var name = slash < 0 ? relative.AsSpan() : relative.AsSpan(slash + 1);
			if (Literal is { } literal)
			{
				return name.Equals(literal, Comparison);
			}

			return Suffix is { } suffix && name.EndsWith(suffix, Comparison);
		}
	}
}

internal static class FileWalker
{
	public static IEnumerable<string> EnumerateFiles(string root, FileWalkOptions options)
	{
		if (!Directory.Exists(root))
		{
			yield break;
		}

		var state = CreateState(root, options);
		foreach (var file in WalkFiles(root, state))
		{
			yield return file;
		}
	}

	public static IEnumerable<string> EnumerateEntries(string root, FileWalkOptions options)
	{
		if (!Directory.Exists(root))
		{
			yield break;
		}

		var state = CreateState(root, options);
		foreach (var entry in WalkEntries(root, state))
		{
			yield return entry;
		}
	}

	private sealed class WalkState
	{
		public required string Root { get; init; }

		public required FileWalkOptions Options { get; init; }

		public List<GitIgnore> Stack { get; } = [];

		/// 当前是否处于某个 git 仓库内（grep 在进入仓库前不读 .gitignore）。
		public bool RepositoryActive { get; set; }

		/// 进入嵌套仓库时是否丢掉祖先仓库的本地规则。
		public bool ResetAtNestedRepository { get; set; }

		public bool GlobalGitLoaded { get; set; }
	}

	private static WalkState CreateState(string root, FileWalkOptions options)
	{
		var full = Path.GetFullPath(root);
		var gitRoot = GitIgnore.FindGitRoot(full);
		var state = new WalkState
		{
			Root = full,
			Options = options,
			// fd 的 --no-require-git：不在仓库里也按仓库规则读 .gitignore 与全局忽略。
			RepositoryActive = gitRoot is not null || options.Flavor == IgnoreFlavor.Fd,
			// 从非仓库目录开始搜索时，ripgrep 遇到仓库才启用 VCS 规则，且之后仍按仓库边界重置。
			ResetAtNestedRepository = gitRoot is not null || options.Flavor == IgnoreFlavor.Ripgrep,
			GlobalGitLoaded = gitRoot is not null || options.Flavor == IgnoreFlavor.Fd,
		};

		var inherited = GitIgnore.ForWalkRoot(full, options);
		if (inherited.Count > 0)
		{
			state.Stack.Add(inherited);
		}

		return state;
	}

	private static IEnumerable<string> WalkFiles(string directory, WalkState state)
	{
		var previousRepositoryActive = state.RepositoryActive;
		EnterRepository(directory, state);
		// 深度要在 EnterRepository 之后取：进入嵌套仓库会丢掉祖先的本地规则，
		// 用之前的深度会让本目录这一层永远弹不出去，规则一直留到兄弟目录。
		var depth = state.Stack.Count;
		PushLocalLayer(directory, state);

		try
		{
			string[] files;
			string[] directories;
			try
			{
				// GetFiles/GetDirectories 能利用 readdir 的 d_type，不必给每个条目额外 stat。
				files = Directory.GetFiles(directory);
				directories = Directory.GetDirectories(directory);
				Array.Sort(files, StringComparer.Ordinal);
				Array.Sort(directories, StringComparer.Ordinal);
			}
			catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
			{
				yield break;
			}

			foreach (var entry in files)
			{
				if (IsIgnored(state, entry, isDirectory: false))
				{
					continue;
				}

				if (state.Options.SkipHidden && Path.GetFileName(entry).StartsWith('.'))
				{
					continue;
				}

				yield return entry;
			}

			foreach (var child in directories)
			{
				var name = Path.GetFileName(child);
				if (!state.Options.IncludeGitDirectory && string.Equals(name, ".git", StringComparison.Ordinal))
				{
					continue;
				}

				// 不跟随符号链接目录：指向祖先的软链接会让遍历无限递归（ripgrep 默认也是这个语义）。
				if (IsReparsePoint(child))
				{
					continue;
				}

				if (IsIgnored(state, child, isDirectory: true))
				{
					continue;
				}

				if (state.Options.SkipHidden && name.StartsWith('.'))
				{
					continue;
				}

				foreach (var file in WalkFiles(child, state))
				{
					yield return file;
				}
			}
		}
		finally
		{
			while (state.Stack.Count > depth)
			{
				state.Stack.RemoveAt(state.Stack.Count - 1);
			}

			// 离开仓库后 VCS 规则不再生效（ripgrep 按文件所在仓库判断）。
			state.RepositoryActive = previousRepositoryActive;
		}
	}

	private static IEnumerable<string> WalkEntries(string directory, WalkState state)
	{
		var previousRepositoryActive = state.RepositoryActive;
		EnterRepository(directory, state);
		var depth = state.Stack.Count;
		PushLocalLayer(directory, state);

		try
		{
			string[] files;
			string[] directories;
			try
			{
				files = Directory.GetFiles(directory);
				directories = Directory.GetDirectories(directory);
				Array.Sort(files, StringComparer.Ordinal);
				Array.Sort(directories, StringComparer.Ordinal);
			}
			catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
			{
				yield break;
			}

			// 目录在前、文件在后（与 fd 的顺序一致）；两边各自保持字典序。
			foreach (var child in directories)
			{
				var name = Path.GetFileName(child);
				if (!state.Options.IncludeGitDirectory && string.Equals(name, ".git", StringComparison.Ordinal))
				{
					continue;
				}

				if (IsIgnored(state, child, isDirectory: true))
				{
					continue;
				}

				yield return child;
			}

			foreach (var child in files)
			{
				if (IsIgnored(state, child, isDirectory: false))
				{
					continue;
				}

				yield return child;
			}

			foreach (var child in directories)
			{
				var name = Path.GetFileName(child);
				if (string.Equals(name, ".git", StringComparison.Ordinal) || IsIgnored(state, child, isDirectory: true))
				{
					continue;
				}

				// 与 WalkFiles 同样的理由：符号链接目录不跟随，避免自指软链接导致无限递归。
				if (IsReparsePoint(child))
				{
					continue;
				}

				foreach (var entry in WalkEntries(child, state))
				{
					yield return entry;
				}
			}
		}
		finally
		{
			while (state.Stack.Count > depth)
			{
				state.Stack.RemoveAt(state.Stack.Count - 1);
			}

			state.RepositoryActive = previousRepositoryActive;
		}
	}

	/// 进入一个仓库根：嵌套仓库先丢掉祖先仓库的本地规则，再按自身规则重新加载。
	private static void EnterRepository(string directory, WalkState state)
	{
		if (!GitIgnore.HasGitMarker(directory))
		{
			return;
		}

		if (state.ResetAtNestedRepository && state.RepositoryActive && !GitIgnore.SamePath(directory, state.Root))
		{
			for (var index = 0; index < state.Stack.Count; index++)
			{
				state.Stack[index] = state.Stack[index].WithoutLocalGit();
			}

			state.Stack.RemoveAll(static layer => layer.Count == 0);
			state.RepositoryActive = false;
		}

		state.RepositoryActive = true;

		// ripgrep 从非仓库目录进入仓库后才开始读全局 git 忽略；fd 在起点已经读过。
		if (!state.GlobalGitLoaded)
		{
			var global = GitIgnore.Global(directory);
			if (global.Count > 0)
			{
				state.Stack.Add(global);
			}

			state.GlobalGitLoaded = true;
		}
	}

	/// 当前目录自己的规则：info/exclude → .gitignore → .ignore → 方言 ignore 文件（后者覆盖前者）。
	private static void PushLocalLayer(string directory, WalkState state)
	{
		var local = new GitIgnore();
		if (state.RepositoryActive)
		{
			local.AddRange(GitIgnore.FromFile(
				Path.Combine(directory, ".git", "info", "exclude"), directory, IgnoreSource.LocalGit));
			local.AddRange(GitIgnore.FromFile(
				Path.Combine(directory, ".gitignore"), directory, IgnoreSource.LocalGit));
		}

		local.AddRange(GitIgnore.FromFile(
			Path.Combine(directory, ".ignore"), directory, IgnoreSource.Always));
		local.AddRange(GitIgnore.FromFile(
			Path.Combine(directory, state.Options.FlavorIgnoreFileName), directory, IgnoreSource.Always));
		if (local.Count > 0)
		{
			state.Stack.Add(local);
		}
	}

	/// 是否是符号链接/重解析点。读不到属性时按「不进入」处理：宁可少扫一点，也不让
	/// 自指软链接把 grep/find 拖进无限递归。
	/// 目录本身是「链接」才不跟随：符号链接与目录联接（junction）。
	/// Windows 上的可重解析点不只有链接：OneDrive 等云占位符也是，而它们是普通目录，
	/// 按「凡是 reparse point 都跳过」会把整片同步目录悄悄漏掉。LinkTarget 只对真正的链接返回非空。
	private static bool IsReparsePoint(string path)
	{
		try
		{
			if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0)
			{
				return false;
			}

			// 拿不到链接目标（不是链接、或不是我们能解析的重解析点）就照常遍历。
			return new DirectoryInfo(path).LinkTarget is not null;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return false;
		}
	}

	private static bool IsIgnored(WalkState state, string path, bool isDirectory)
	{
		var ignored = false;
		foreach (var layer in state.Stack)
		{
			ignored = layer.Apply(path, isDirectory, ignored);
		}

		return ignored;
	}
}
