using System.Text;
using System.Text.RegularExpressions;

namespace PiWeft.CodingAgent.Tools;

/// <summary>
/// 供 <c>grep</c> 与 <c>find</c> 遍历时使用的 <c>.gitignore</c> 匹配器。pi 把忽略规则交给 ripgrep/fd，
/// 本端口在进程内实现常用子集，因而不需要外部可执行文件。
/// </summary>
public sealed class GitIgnore
{
	private readonly List<IgnoreRule> _rules = [];

	public int Count => _rules.Count;

	public static GitIgnore Empty { get; } = new();

	/// <summary>解析 <c>.gitignore</c> 文件（若存在），并以它所在目录作为模式基准。</summary>
	public static GitIgnore FromDirectory(string directory)
	{
		var path = Path.Combine(directory, ".gitignore");
		if (!File.Exists(path))
		{
			return Empty;
		}

		try
		{
			return Parse(File.ReadAllLines(path), directory);
		}
		catch (IOException)
		{
			return Empty;
		}
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

	/// <summary>最后一条命中的规则是忽略该路径时为 true。</summary>
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
				default:
					builder.Append(Regex.Escape(c.ToString()));
					break;
			}
		}

		builder.Append('$');

		try
		{
			return new Regex(
				builder.ToString(),
				RegexOptions.CultureInvariant | RegexOptions.Compiled,
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

/// <summary>深度优先的文件遍历器，遵循各级 <c>.gitignore</c>，并始终跳过 <c>.git</c>。</summary>
public static class FileWalker
{
	/// <summary>按稳定顺序枚举 <paramref name="root"/> 下的文件。</summary>
	public static IEnumerable<string> EnumerateFiles(string root, bool skipHidden = false)
	{
		if (!Directory.Exists(root))
		{
			yield break;
		}

		var stack = new List<GitIgnore>();
		foreach (var file in WalkFiles(root, stack, skipHidden))
		{
			yield return file;
		}
	}

	/// <summary>深度优先枚举 <paramref name="root"/> 下的所有条目（文件与目录）。</summary>
	public static IEnumerable<string> EnumerateEntries(string root)
	{
		if (!Directory.Exists(root))
		{
			yield break;
		}

		var stack = new List<GitIgnore>();
		foreach (var entry in WalkEntries(root, stack))
		{
			yield return entry;
		}
	}

	private static IEnumerable<string> WalkFiles(string directory, List<GitIgnore> stack, bool skipHidden)
	{
		var pushed = false;
		var local = GitIgnore.FromDirectory(directory);
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
				if (string.Equals(name, ".git", StringComparison.Ordinal))
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

				foreach (var file in WalkFiles(child, stack, skipHidden))
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

	private static IEnumerable<string> WalkEntries(string directory, List<GitIgnore> stack)
	{
		var pushed = false;
		var local = GitIgnore.FromDirectory(directory);
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
				if (string.Equals(name, ".git", StringComparison.Ordinal))
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

				foreach (var entry in WalkEntries(child, stack))
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
