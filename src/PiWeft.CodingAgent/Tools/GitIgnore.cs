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
		for (var index = chain.Count - 1; index >= 0; index--)
		{
			var current = chain[index];
			combined.AddRange(FromFile(Path.Combine(current, ".gitignore"), current));
			combined.AddRange(FromFile(Path.Combine(current, ".ignore"), current));
			combined.AddRange(FromFile(Path.Combine(current, ".rgignore"), current));
			if (gitRoot is not null &&
				string.Equals(Path.GetFullPath(current), Path.GetFullPath(gitRoot), StringComparison.Ordinal))
			{
				combined.AddRange(FromFile(Path.Combine(current, ".git", "info", "exclude"), current));
			}
		}

		return combined;
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
					var close = pattern.IndexOf(']', i + 1);
					if (close > i + 1)
					{
						builder.Append(pattern, i, close - i + 1);
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

	/// 是否是符号链接/重解析点。读不到属性时按「不进入」处理 —— 宁可少扫一点，也不要因为
	/// 自指软链接把 grep/find 拖进无限递归。
	private static bool IsReparsePoint(string path)
	{
		try
		{
			return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return true;
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
