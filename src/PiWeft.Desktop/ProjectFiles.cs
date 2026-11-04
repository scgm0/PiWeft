using System.Collections.Frozen;

namespace PiWeft.Desktop;

public sealed record ProjectEntry(string Name, string RelativePath, bool IsDirectory, string Status);

/// 项目文件访问：目录列举（跳过构建产物与版本库目录、条数有界）、文本读取（大小与二进制判定）
/// 与带冲突检测的写入。所有路径都限制在项目根目录内，<c>..</c> 逃逸会被拒绝。
public static class ProjectFiles
{
	public const int MaxEntries = 2000;

	public const int MaxFileBytes = 1024 * 1024;

	/// 这些目录不参与列举：版本库、依赖与构建产物。
	/// 冻结集合把哈希预先算好并分段存放：目录列举要对每一项都查一次，普通字典的每次查找
	/// 都要重算字符串哈希（依赖替换、层级遍历）。
	private static readonly FrozenSet<string> IgnoredDirectories = new HashSet<string>(StringComparer.Ordinal)
	{
		".git", ".vs", ".idea", ".vscode", "node_modules", "bin", "obj", "dist", "build", "out",
		"target", ".venv", "venv", "__pycache__", ".next", ".nuxt", ".cache", ".gradle", "packages",
		"Pods", "DerivedData", ".terraform",
	}.ToFrozenSet(StringComparer.Ordinal);

	/// <param name="statuses">可选的 Git 状态标记，键为相对路径。</param>
	public static IReadOnlyList<ProjectEntry> List(string cwd, string relativeDir, IReadOnlyDictionary<string, string>? statuses = null)
	{
		var root = Path.GetFullPath(cwd);
		var directory = ResolveContained(root, relativeDir);
		if (directory is null || !Directory.Exists(directory))
		{
			return [];
		}

		var directories = new List<ProjectEntry>();
		var files = new List<ProjectEntry>();
		try
		{
			foreach (var child in Directory.EnumerateDirectories(directory))
			{
				var name = Path.GetFileName(child);
				if (name.StartsWith('.') || IgnoredDirectories.Contains(name))
				{
					continue;
				}

				directories.Add(new ProjectEntry(name, Relative(root, child), true, string.Empty));
				if (directories.Count + files.Count >= MaxEntries)
				{
					break;
				}
			}

			if (directories.Count < MaxEntries)
			{
				foreach (var child in Directory.EnumerateFiles(directory))
				{
					var name = Path.GetFileName(child);
					var relative = Relative(root, child);
					files.Add(new ProjectEntry(name, relative, false, statuses?.GetValueOrDefault(relative) ?? string.Empty));
					if (directories.Count + files.Count >= MaxEntries)
					{
						break;
					}
				}
			}
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			return [];
		}

		directories.Sort(static (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
		files.Sort(static (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
		directories.AddRange(files);
		return directories;
	}

	public static (string? Content, long LastWriteTicks, string? Error) Read(string cwd, string relativePath, int maxBytes = MaxFileBytes)
	{
		var root = Path.GetFullPath(cwd);
		var path = ResolveContained(root, relativePath);
		if (path is null || !File.Exists(path))
		{
			return (null, 0, Strings.FileBrowserMissing);
		}

		try
		{
			var info = new FileInfo(path);
			if (info.Length > maxBytes)
			{
				return (null, 0, Strings.FileBrowserTooLarge);
			}

			var bytes = File.ReadAllBytes(path);
			if (Array.IndexOf(bytes, (byte)0) >= 0)
			{
				return (null, 0, Strings.FileBrowserBinary);
			}

			var text = System.Text.Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
			return (text, info.LastWriteTimeUtc.Ticks, null);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			return (null, 0, ex.Message);
		}
	}

	/// 写入文本文件。给定 <paramref name="expectedTicks"/> 时若磁盘上的时间戳已变化，拒绝覆盖并返回冲突。
	/// 写入采用同目录临时文件加原子替换。
	public static string? Write(string cwd, string relativePath, string content, long? expectedTicks = null)
	{
		var root = Path.GetFullPath(cwd);
		var path = ResolveContained(root, relativePath);
		if (path is null)
		{
			return Strings.FileBrowserInvalidPath;
		}

		try
		{
			if (expectedTicks is { } expected && File.Exists(path) && File.GetLastWriteTimeUtc(path).Ticks != expected)
			{
				return Strings.FileBrowserConflict;
			}

			var directory = Path.GetDirectoryName(path);
			if (directory is { Length: > 0 })
			{
				Directory.CreateDirectory(directory);
			}

			var temporary = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
			File.WriteAllText(temporary, content, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
			File.Move(temporary, path, overwrite: true);
			return null;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			return ex.Message;
		}
	}

	internal static string? ResolveContained(string root, string relativePath)
	{
		if (Path.IsPathRooted(relativePath))
		{
			return null;
		}

		string full;
		try
		{
			full = Path.GetFullPath(Path.Combine(root, relativePath));
		}
		catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
		{
			return null;
		}

		if (string.Equals(full, root, StringComparison.Ordinal))
		{
			return full;
		}

		var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
		return full.StartsWith(prefix, StringComparison.Ordinal) ? full : null;
	}

	private static string Relative(string root, string path) =>
		Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
}
