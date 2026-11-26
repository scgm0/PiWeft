namespace PiWeft.Desktop;

/// 一个可切换的仓库：面板的工作目录可以从会话目录切到它下面任意一个仓库。
/// <param name="Path">绝对路径（主工作区的 git 根或子仓库目录）。</param>
/// <param name="Relative">相对会话目录的路径；根仓库为空串。</param>
internal sealed record GitRepository(string Path, string Relative)
{
	/// 面板上显示的名字：根仓库用目录名，其余用相对路径。
	public string Label => Relative.Length > 0 ? Relative : System.IO.Path.GetFileName(Path);

	public bool IsRoot => Relative.Length == 0;
}

/// 扫描会话目录里的仓库（含 submodule 的 gitfile 形态）。PiDeck 的 SCM 也是这样在
/// monorepo 里并排管理多个仓库：一个会话一个 cwd，但 git 操作可以落到子仓库上。
/// 只扫目录结构，不起 git 子进程；跳过的目录与 PiDeck 的 gitRepoScope 一致。
internal static class GitRepositories
{
	/// 相对会话目录最多向下 4 层：覆盖 packages/* 与少量 monorepo 分组，不扫整盘。
	public const int MaxDepth = 4;

	/// 单次扫描的仓库上限，防止异常目录树拖垮面板。
	public const int MaxRepositories = 32;

	private static readonly HashSet<string> SkipDirectories = new(StringComparer.OrdinalIgnoreCase)
	{
		"node_modules", ".git", "dist", "build", "out", "out-tsc", "target", "vendor",
		"__pycache__", ".venv", "venv", "coverage", ".next", ".nuxt", ".cache", ".gradle",
		"bin", "obj", ".idea", ".vs",
	};

	/// <summary>扫描 <paramref name="cwd"/> 本身与它下面的仓库；根仓库排在首位。</summary>
	public static IReadOnlyList<GitRepository> Scan(string cwd, int maxDepth = MaxDepth)
	{
		var root = Normalize(cwd);
		if (root.Length == 0 || !Directory.Exists(root))
		{
			return [];
		}

		var found = new List<GitRepository>();
		// 会话目录可能是仓库子目录：包含它的仓库也要能选，否则 monorepo 里的子目录会话
		// 既没仓库 chip，切不到仓库根。
		if (FindContainingRepository(root) is { } containing)
		{
			found.Add(new GitRepository(containing, Relative(root, containing)));
		}

		Walk(root, root, depth: 0, maxDepth, found);
		found.Sort(static (left, right) =>
		{
			if (left.IsRoot != right.IsRoot)
			{
				return left.IsRoot ? -1 : 1;
			}

			return string.Compare(left.Relative, right.Relative, StringComparison.Ordinal);
		});
		return found;
	}

	private static void Walk(string root, string directory, int depth, int maxDepth, List<GitRepository> found)
	{
		if (found.Count >= MaxRepositories || depth >= maxDepth)
		{
			return;
		}

		string[] children;
		try
		{
			// 无权限的子目录直接跳过，不让一次拒绝访问中断整次扫描。
			children = [.. Directory.EnumerateDirectories(directory, "*",
				new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 })];
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return;
		}

		foreach (var child in children)
		{
			if (found.Count >= MaxRepositories)
			{
				return;
			}

			var name = Path.GetFileName(child);
			if (name.Length == 0 || name[0] == '.' || SkipDirectories.Contains(name))
			{
				continue;
			}

			try
			{
				// 符号链接与目录联接不跟随：可能指向仓库外，扫进去等于替用户翻别人的盘。
				if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
				{
					continue;
				}
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				continue;
			}

			if (IsRepository(child))
			{
				found.Add(new GitRepository(child, Relative(root, child)));
				continue;
			}

			Walk(root, child, depth + 1, maxDepth, found);
		}
	}

	/// <c>.git</c> 可能是目录（普通仓库），也可能是文件（submodule / worktree）。
	/// 从给定目录向上找最近的仓库根；调用方已经确认目录存在。
	private static string? FindContainingRepository(string start)
	{
		var directory = start;
		while (directory.Length > 0)
		{
			if (IsRepository(directory))
			{
				return directory;
			}

			var parent = Path.GetDirectoryName(directory);
			if (parent is null || string.Equals(parent, directory, StringComparison.Ordinal))
			{
				return null;
			}

			directory = parent;
		}

		return null;
	}

	private static bool IsRepository(string directory)
	{
		try
		{
			var dotGit = Path.Combine(directory, ".git");
			return Directory.Exists(dotGit) || File.Exists(dotGit);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return false;
		}
	}

	private static string Relative(string root, string path)
	{
		// GetRelativePath 对同一目录返回 "."，这里统一成空串（根仓库的标记）。
		if (string.Equals(root, path, StringComparison.Ordinal))
		{
			return string.Empty;
		}

		return Path.GetRelativePath(root, path).Replace('\\', '/');
	}

	private static string Normalize(string cwd)
	{
		try
		{
			return Path.GetFullPath(cwd);
		}
		catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
		{
			return string.Empty;
		}
	}
}
