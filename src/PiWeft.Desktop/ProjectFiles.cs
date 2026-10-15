namespace PiWeft.Desktop;

/// <summary>文件树里的一个条目。</summary>
public sealed record ProjectEntry(string Name, string RelativePath, bool IsDirectory, string Status);

/// <summary>
/// 项目文件访问：目录列举（跳过构建产物与版本库目录、条数有界）、文本读取（大小与二进制判定）
/// 与带冲突检测的写入。所有路径都限制在项目根目录内，<c>..</c> 逃逸会被拒绝。
/// </summary>
public static class ProjectFiles
{
	/// <summary>单次列举的最大条目数，避免超大目录拖垮界面。</summary>
	public const int MaxEntries = 2000;

	/// <summary>可编辑文本文件的大小上限。</summary>
	public const int MaxFileBytes = 1024 * 1024;

	/// <summary>这些目录不参与列举：版本库、依赖与构建产物。</summary>
	private static readonly HashSet<string> IgnoredDirectories = new(StringComparer.Ordinal)
	{
		".git", ".vs", ".idea", ".vscode", "node_modules", "bin", "obj", "dist", "build", "out",
		"target", ".venv", "venv", "__pycache__", ".next", ".nuxt", ".cache", ".gradle", "packages",
		"Pods", "DerivedData", ".terraform",
	};

	/// <summary>列举目录内容：先目录后文件，各按名称排序。<paramref name="relativeDir"/> 为空表示项目根。</summary>
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

	/// <summary>读取文本文件；过大或含 NUL 字节时返回错误而不返回内容。</summary>
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

	/// <summary>
	/// 写入文本文件。给定 <paramref name="expectedTicks"/> 时若磁盘上的时间戳已变化，拒绝覆盖并返回冲突。
	/// 写入采用同目录临时文件加原子替换。
	/// </summary>
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

	/// <summary>把相对路径解析成根目录内的绝对路径；越界或含绝对路径时返回 <c>null</c>。</summary>
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
