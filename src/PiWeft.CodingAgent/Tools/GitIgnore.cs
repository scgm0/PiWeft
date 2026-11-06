using System.Text;
using System.Text.RegularExpressions;

namespace PiWeft.CodingAgent.Tools;

/// 供 <c>grep</c> 与 <c>find</c> 遍历时使用的 <c>.gitignore</c> 匹配器。pi 把忽略规则交给 ripgrep/fd，
/// 本端口在进程内实现常用子集，因而不需要外部可执行文件。
public sealed class GitIgnore
{
	private readonly List<IgnoreRule> _rules = [];

	public int Count => _rules.Count;

	public static GitIgnore Empty { get; } = new();

	public static GitIgnore FromDirectory(string directory) =>
		FromFile(Path.Combine(directory, ".gitignore"), directory);

	public static GitIgnore FromFile(string path, string baseDirectory)
	{
		if (!File.Exists(path))
		{
			// 不能返回共享的 Empty：调用方会用 AddRange 把 .ignore/.rgignore 叠加进来。
			return new GitIgnore();
		}

		try
		{
			return Parse(File.ReadAllLines(path), baseDirectory);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return new GitIgnore();
		}
	}

	/// 搜索起点在子目录时，ripgrep/fd 仍会应用父目录的 ignore 文件。这里把 git 仓库根
	/// （没有仓库则到文件系统根）到搜索目录父级的规则合并成一份，Walker 再叠加各层本地规则。
	public static GitIgnore ForSearchRoot(string root)
	{
		var directory = Directory.Exists(root)
			? Path.GetFullPath(root)
			: Path.GetDirectoryName(Path.GetFullPath(root)) ?? Path.GetFullPath(root);
		if (directory.Length == 0)
		{
			return Empty;
		}

		var gitRoot = FindGitRoot(directory);
		var stop = gitRoot ?? Path.GetPathRoot(directory) ?? directory;
		var chain = new List<string>();
		for (var current = directory; current is { Length: > 0 }; current = Path.GetDirectoryName(current))
		{
			if (string.Equals(Path.GetFullPath(current), Path.GetFullPath(directory), StringComparison.Ordinal))
			{
				continue;
			}

			chain.Add(current);
			if (string.Equals(Path.GetFullPath(current), Path.GetFullPath(stop), StringComparison.Ordinal))
			{
				break;
			}
		}

		var combined = new GitIgnore();
		// 优先级从低到高：全局忽略（core.excludesFile / XDG ignore）→ .git/info/exclude →
		// 逐层 .gitignore/.ignore/.rgignore。规则是「后出现的覆盖先出现的」，顺序错了
		// 就会出现「仓库根的 .gitignore 被 info/exclude 反过来压住」这种和 git 不一样的结果。
		var baseDirectory = gitRoot ?? directory;
		combined.AddRange(Global(baseDirectory));
		if (gitRoot is not null)
		{
			combined.AddRange(FromFile(Path.Combine(gitRoot, ".git", "info", "exclude"), gitRoot));
		}

		for (var index = chain.Count - 1; index >= 0; index--)
		{
			var current = chain[index];
			combined.AddRange(FromFile(Path.Combine(current, ".gitignore"), current));
			combined.AddRange(FromFile(Path.Combine(current, ".ignore"), current));
			combined.AddRange(FromFile(Path.Combine(current, ".rgignore"), current));
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
			combined.AddRange(FromFile(path, baseDirectory));
		}

		return combined;
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

	private static string? FindGitRoot(string directory)
	{
		for (var current = directory; current is { Length: > 0 }; current = Path.GetDirectoryName(current))
		{
			if (Directory.Exists(Path.Combine(current, ".git")))
			{
				return current;
			}
		}

		return null;
	}

	public static GitIgnore Parse(IEnumerable<string> lines, string baseDirectory)
	{
		var ignore = new GitIgnore();
		foreach (var raw in lines)
		{
			var rule = Compile(raw, baseDirectory);
			if (rule is not null)
			{
				ignore._rules.Add(rule);
			}
		}

		return ignore;
	}

	public void AddRange(GitIgnore other)
	{
		if (!ReferenceEquals(other, this))
		{
			_rules.AddRange(other._rules);
		}
	}

	public bool IsIgnored(string absolutePath, bool isDirectory)
	{
		var ignored = false;
		foreach (var rule in _rules)
		{
			if (rule.DirectoryOnly && !isDirectory)
			{
				continue;
			}

			if (rule.IsMatch(absolutePath))
			{
				ignored = !rule.Negated;
			}
		}

		return ignored;
	}

	private static IgnoreRule? Compile(string raw, string baseDirectory)
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
		var regex = BuildRegex(pattern, anchored);
		return regex is null ? null : new IgnoreRule(baseDirectory, regex, negated, directoryOnly);
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
			var options = RegexOptions.CultureInvariant | RegexOptions.Compiled;
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

	private sealed record IgnoreRule(string BaseDirectory, Regex Regex, bool Negated, bool DirectoryOnly)
	{
		public bool IsMatch(string absolutePath)
		{
			var relative = Path.GetRelativePath(BaseDirectory, absolutePath);
			if (relative.StartsWith("..", StringComparison.Ordinal))
			{
				return false;
			}

			return Regex.IsMatch(relative.Replace('\\', '/'));
		}
	}
}

public static class FileWalker
{
	public static IEnumerable<string> EnumerateFiles(string root, bool skipHidden = false, bool includeGitDirectory = false)
	{
		if (!Directory.Exists(root))
		{
			yield break;
		}

		var stack = new List<GitIgnore>();
		var inherited = GitIgnore.ForSearchRoot(root);
		if (inherited.Count > 0)
		{
			stack.Add(inherited);
		}

		foreach (var file in WalkFiles(root, stack, skipHidden, includeGitDirectory))
		{
			yield return file;
		}
	}

	public static IEnumerable<string> EnumerateEntries(string root, bool includeGitDirectory = false)
	{
		if (!Directory.Exists(root))
		{
			yield break;
		}

		var stack = new List<GitIgnore>();
		var inherited = GitIgnore.ForSearchRoot(root);
		if (inherited.Count > 0)
		{
			stack.Add(inherited);
		}

		foreach (var entry in WalkEntries(root, stack, includeGitDirectory))
		{
			yield return entry;
		}
	}

	private static IEnumerable<string> WalkFiles(string directory, List<GitIgnore> stack, bool skipHidden, bool includeGitDirectory)
	{
		var pushed = false;
		var local = GitIgnore.FromDirectory(directory);
		local.AddRange(GitIgnore.FromFile(Path.Combine(directory, ".ignore"), directory));
		local.AddRange(GitIgnore.FromFile(Path.Combine(directory, ".rgignore"), directory));
		if (local.Count > 0)
		{
			stack.Add(local);
			pushed = true;
		}

		try
		{
			List<string> files;
			List<string> directories;
			try
			{
				files = [.. Directory.EnumerateFiles(directory).OrderBy(static p => p, StringComparer.Ordinal)];
				directories = [.. Directory.EnumerateDirectories(directory).OrderBy(static p => p, StringComparer.Ordinal)];
			}
			catch (UnauthorizedAccessException)
			{
				yield break;
			}
			catch (IOException)
			{
				yield break;
			}

			foreach (var file in files)
			{
				if (IsIgnored(stack, file, isDirectory: false))
				{
					continue;
				}

				if (skipHidden && Path.GetFileName(file).StartsWith('.'))
				{
					continue;
				}

				yield return file;
			}

			foreach (var child in directories)
			{
				var name = Path.GetFileName(child);
				if (!includeGitDirectory && string.Equals(name, ".git", StringComparison.Ordinal))
				{
					continue;
				}

				// 不跟随符号链接目录：指向祖先的软链接会让遍历无限递归（ripgrep 默认也是这个语义）。
				if (IsReparsePoint(child))
				{
					continue;
				}

				if (IsIgnored(stack, child, isDirectory: true))
				{
					continue;
				}

				if (skipHidden && name.StartsWith('.'))
				{
					continue;
				}

				foreach (var file in WalkFiles(child, stack, skipHidden, includeGitDirectory))
				{
					yield return file;
				}
			}
		}
		finally
		{
			if (pushed)
			{
				stack.RemoveAt(stack.Count - 1);
			}
		}
	}

	private static IEnumerable<string> WalkEntries(string directory, List<GitIgnore> stack, bool includeGitDirectory)
	{
		var pushed = false;
		var local = GitIgnore.FromDirectory(directory);
		local.AddRange(GitIgnore.FromFile(Path.Combine(directory, ".ignore"), directory));
		local.AddRange(GitIgnore.FromFile(Path.Combine(directory, ".rgignore"), directory));
		if (local.Count > 0)
		{
			stack.Add(local);
			pushed = true;
		}

		try
		{
			List<string> files;
			List<string> directories;
			try
			{
				files = [.. Directory.EnumerateFiles(directory).OrderBy(static p => p, StringComparer.Ordinal)];
				directories = [.. Directory.EnumerateDirectories(directory).OrderBy(static p => p, StringComparer.Ordinal)];
			}
			catch (UnauthorizedAccessException)
			{
				yield break;
			}
			catch (IOException)
			{
				yield break;
			}

			foreach (var child in directories.Concat(files))
			{
				var isDirectory = Directory.Exists(child);
				var name = Path.GetFileName(child);
				if (!includeGitDirectory && string.Equals(name, ".git", StringComparison.Ordinal))
				{
					continue;
				}

				if (IsIgnored(stack, child, isDirectory))
				{
					continue;
				}

				yield return child;
			}

			foreach (var child in directories)
			{
				var name = Path.GetFileName(child);
				if (string.Equals(name, ".git", StringComparison.Ordinal) || IsIgnored(stack, child, isDirectory: true))
				{
					continue;
				}

				// 与 WalkFiles 同样的理由：符号链接目录不跟随，避免自指软链接导致无限递归。
				if (IsReparsePoint(child))
				{
					continue;
				}

				foreach (var entry in WalkEntries(child, stack, includeGitDirectory))
				{
					yield return entry;
				}
			}
		}
		finally
		{
			if (pushed)
			{
				stack.RemoveAt(stack.Count - 1);
			}
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

	private static bool IsIgnored(List<GitIgnore> stack, string path, bool isDirectory)
	{
		foreach (var ignore in stack)
		{
			if (ignore.IsIgnored(path, isDirectory))
			{
				return true;
			}
		}

		return false;
	}
}
