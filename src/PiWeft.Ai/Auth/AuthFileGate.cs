namespace PiWeft.Ai.Auth;

/// auth.json 的跨进程写入门闩。OAuthStore / AuthStore / CodexAuth 各自做「读整份 → 改一个 provider → 写整份」，
/// 并发刷新不同 provider 时没有共享锁会互相覆盖（后写者把前者的新令牌丢掉）。
/// 进程内用 <see cref="Gate"/>，进程间用同目录的 <c>auth.json.lock</c>。
internal static class AuthFileGate
{
	public static readonly Lock Gate = new();

	/// 取得跨进程文件锁。调用方仍要先持有 <see cref="Gate"/>，避免同进程两个线程争同一把文件锁。
	public static IDisposable LockFile(string? agentDir = null)
	{
		var path = AuthStore.Path(agentDir) + ".lock";
		var directory = System.IO.Path.GetDirectoryName(path);
		if (directory is { Length: > 0 })
		{
			Directory.CreateDirectory(directory);
		}

		return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
	}
}
