namespace PiWeft.CodingAgent.Tools;

/// 同一文件的写操作串行化（pi 的 <c>withFileMutationQueue</c> 同义）。
/// 并行工具调用里两个 <c>edit</c> 打同一个文件时，各自的「读—改—写」可能交错：后写的那次
/// 拿着旧内容，前一次的修改就被静默覆盖。锁按规范化路径分组，不同文件互不阻塞。
/// 用固定数量的条带，不用「每路径一个锁对象」：会话可以碰到任意多的路径，
/// 一个只增不减的字典就是一处没有上界的泄漏；路径碰撞只会多等几微秒。
internal static class FileMutationQueue
{
	private static readonly Lock[] Stripes = CreateStripes();

	public static T Mutate<T>(string path, Func<T> action)
	{
		lock (LockFor(path))
		{
			return action();
		}
	}

	public static void Mutate(string path, Action action) =>
		Mutate(path, () =>
		{
			action();
			return true;
		});

	private static Lock[] CreateStripes()
	{
		var stripes = new Lock[64];
		for (var index = 0; index < stripes.Length; index++)
		{
			stripes[index] = new Lock();
		}

		return stripes;
	}

	private static Lock LockFor(string path)
	{
		var key = Canonical(path);
		var hash = (uint)StringComparer.Ordinal.GetHashCode(key);
		return Stripes[hash % (uint)Stripes.Length];
	}

	/// 解析到真实路径：<c>./a</c>、符号链接与目标文件必须落在同一把锁上，
	/// 否则换个写法就能绕过串行化。文件还不存在时用绝对路径当键。
	private static string Canonical(string path)
	{
		string full;
		try
		{
			full = Path.GetFullPath(path);
		}
		catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
		{
			return path;
		}

		try
		{
			if (File.Exists(full))
			{
				var target = File.ResolveLinkTarget(full, returnFinalTarget: true);
				if (target is not null)
				{
					return Path.GetFullPath(target.FullName);
				}
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			// 解析失败就用绝对路径：宁可多串行一点，也不要出现两条路径各写各的。
		}

		// Windows 文件系统不区分大小写：Foo.txt 与 foo.txt 必须落在同一把锁上。
		// macOS 默认也不区分，但存在区分大小写的卷，按路径原样处理（realpath 已经归一化了已存在文件）。
		return OperatingSystem.IsWindows() ? full.ToUpperInvariant() : full;
	}
}
