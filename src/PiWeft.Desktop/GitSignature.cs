namespace PiWeft.Desktop;

/// 一份仓库签名的两部分：引用状态（HEAD、分支与远端跟踪分支、合并中途的标记）与索引。
/// 引用变了说明提交图也可能变了，索引变了只影响改动列表——分开看，暂存一次就不必重读 `git log`。
/// 两个字段都是 64 位累加哈希：签名每两秒算一次，不能为它保留引用目录的完整字符串。
internal readonly record struct GitSignatureValue(long Refs, long Full);

/// 仓库的廉价签名：只 stat 少量小文件，不启动 git 子进程。
/// Git 面板可见时按它轮询，签名变了才真的重读状态——子代理或在终端里提交、
/// 切分支、fetch 之后，面板能在两秒内跟上，而空转时只花几次 stat。
/// 不包含工作区文件改动：那需要遍历整个目录，代价与 <c>git status</c> 同量级；
/// 工作区的变化由回合结束、窗口重新激活与手动刷新兜住。
internal static class GitSignature
{
	// 引用文件可能上千（大仓库的分支与远端跟踪分支），给统计一个上界：
	// 超出后剩下的按「有变化」处理（签名里带上标记），宁可多刷一次也不漏。
	private const int MaxRefFiles = 2048;

	/// 返回当前签名；不是仓库或读不到 git 目录时返回 null（调用方据此停掉轮询）。
	public static GitSignatureValue? Read(string cwd)
	{
		var gitDir = ResolveGitDir(cwd);
		if (gitDir is null)
		{
			return null;
		}

		var commonDir = ResolveCommonDir(gitDir);
		var refs = 0L;
		AccumulateFile(ref refs, Path.Combine(gitDir, "HEAD"));
		AccumulateFile(ref refs, Path.Combine(gitDir, "MERGE_HEAD"));
		AccumulateFile(ref refs, Path.Combine(gitDir, "CHERRY_PICK_HEAD"));
		AccumulateFile(ref refs, Path.Combine(gitDir, "REVERT_HEAD"));
		AccumulateFile(ref refs, Path.Combine(gitDir, "rebase-merge"));
		AccumulateFile(ref refs, Path.Combine(gitDir, "rebase-apply"));
		AccumulateFile(ref refs, Path.Combine(commonDir, "packed-refs"));
		AccumulateRefs(ref refs, Path.Combine(gitDir, "refs"));
		if (!string.Equals(gitDir, commonDir, StringComparison.Ordinal))
		{
			// 工作树里的 HEAD 是私有的，分支与远端引用都在公共仓库目录下。
			AccumulateRefs(ref refs, Path.Combine(commonDir, "refs"));
		}

		// 索引在引用之外：只暂存不改提交图。
		var index = 0L;
		AccumulateFile(ref index, Path.Combine(gitDir, "index"));

		return new GitSignatureValue(refs, Combine(refs, index));
	}

	private static void AccumulateRefs(ref long accumulator, string refs)
	{
		try
		{
			if (!Directory.Exists(refs))
			{
				return;
			}

			var count = 0;
			var files = 0;
			// 枚举顺序不保证稳定，所以逐个混进哈希再相加（加法与顺序无关），不排序也不留字符串。
			foreach (var file in Directory.EnumerateFiles(refs, "*", SearchOption.AllDirectories))
			{
				files++;
				if (files > MaxRefFiles)
				{
					accumulator = Mix(accumulator, count, -1L);
					return;
				}

				AccumulateFile(ref accumulator, file);
				count++;
			}

			accumulator = Mix(accumulator, count, 0L);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			// 引用目录读不到只说明签名不完整：加一个标记，下一次读到完整目录时会算出不同签名。
			accumulator = Mix(accumulator, -1L, -1L);
		}
	}

	private static void AccumulateFile(ref long accumulator, string path)
	{
		try
		{
			var info = new FileInfo(path);
			if (info.Exists)
			{
				accumulator = Mix(accumulator, info.LastWriteTimeUtc.Ticks, info.Length, path);
				return;
			}

			// 交互式 rebase 的 rebase-merge / rebase-apply 是目录：FileInfo 永远不存在，
			// 只看文件会把“正在 rebase”漏掉。
			var directory = new DirectoryInfo(path);
			if (directory.Exists)
			{
				accumulator = Mix(accumulator, directory.LastWriteTimeUtc.Ticks, 0, path);
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			accumulator = Mix(accumulator, -1L, -1L);
		}
	}

	/// 把两部分累加哈希拼成一份：两边可能共享同样的项，直接相加会在两边同步变化时抵消，
	/// 过一遍 FNV 混淆再拼。
	private static long Combine(long left, long right)
	{
		const ulong prime = 1099511628211UL;
		var hash = 14695981039346656037UL;
		hash = (hash ^ (ulong)left) * prime;
		hash = (hash ^ (ulong)right) * prime;
		return unchecked((long)hash);
	}

	/// 64 位 FNV-1a 混淆后相加。加法（不是异或）让同一路径出现两次时不会互相抵消。
	private static long Mix(long accumulator, long ticks, long length, string path = "")
	{
		const ulong prime = 1099511628211UL;
		var hash = 14695981039346656037UL;
		foreach (var character in path)
		{
			hash = (hash ^ character) * prime;
		}

		hash = (hash ^ (ulong)ticks) * prime;
		hash = (hash ^ (ulong)length) * prime;
		return unchecked(accumulator + (long)hash);
	}

	/// <c>.git</c> 可能是目录（普通仓库），也可能是文件（工作树 / 子模块），内容形如
	/// <c>gitdir: ../.git/worktrees/xxx</c>。直接拼 <c>.git/HEAD</c> 在工作树里不成立。
	private static string? ResolveGitDir(string cwd)
	{
		try
		{
			// 会话目录可能是仓库子目录：git status 会向上找，签名也必须向上找，
			// 否则子目录会话的自动刷新与仓库列表都是空的。
			var directory = Path.GetFullPath(cwd);
			while (directory.Length > 0)
			{
				var dotGit = Path.Combine(directory, ".git");
				if (Directory.Exists(dotGit))
				{
					return dotGit;
				}

				if (File.Exists(dotGit))
				{
					foreach (var raw in File.ReadLines(dotGit))
					{
						var line = raw.Trim();
						if (!line.StartsWith("gitdir:", StringComparison.Ordinal))
						{
							continue;
						}

						var target = line["gitdir:".Length..].Trim();
						return target.Length == 0
							? null
							: Path.GetFullPath(Path.IsPathRooted(target) ? target : Path.Combine(directory, target));
					}
				}

				var parent = Path.GetDirectoryName(directory);
				if (parent is null || string.Equals(parent, directory, StringComparison.Ordinal))
				{
					return null;
				}

				directory = parent;
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
		{
			// 读不到就按「不是仓库」处理：轮询会停，面板仍可手动刷新。
		}

		return null;
	}

	private static string ResolveCommonDir(string gitDir)
	{
		try
		{
			var file = Path.Combine(gitDir, "commondir");
			if (File.Exists(file))
			{
				var target = File.ReadAllText(file).Trim();
				if (target.Length > 0)
				{
					return Path.GetFullPath(Path.IsPathRooted(target) ? target : Path.Combine(gitDir, target));
				}
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
		{
			// 工作树指针读不到时退回自己的 gitDir：签名不完整但不会崩。
		}

		return gitDir;
	}
}
