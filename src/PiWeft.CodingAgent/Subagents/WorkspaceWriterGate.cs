namespace PiWeft.CodingAgent.Subagents;

/// 进程级的写子代理名额：按工作目录计数，跨父会话生效。
/// 默认上限 0（不限），打开严格模式后才挡第二个写者。
/// 只算「正在跑」的写子代理；已结束的继续时重新申请（见 <see cref="SubagentHub"/>）。
internal static class WorkspaceWriterGate
{
	private static readonly Lock Gate = new();
	private static readonly Dictionary<string, int> Counts = new(StringComparer.Ordinal);

	/// 申请一个写者名额；<paramref name="max"/> 小于等于 0 表示不限，返回空租约。
	public static bool TryAcquire(string cwd, int max, out IDisposable? lease)
	{
		lease = null;
		if (max <= 0)
		{
			return true;
		}

		var key = Normalize(cwd);
		lock (Gate)
		{
			Counts.TryGetValue(key, out var count);
			if (count >= max)
			{
				return false;
			}

			Counts[key] = count + 1;
			lease = new Lease(key);
			return true;
		}
	}

	private static string Normalize(string cwd)
	{
		string key;
		try
		{
			key = Path.GetFullPath(cwd);
		}
		catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
		{
			key = cwd;
		}

		return OperatingSystem.IsWindows() ? key.ToUpperInvariant() : key;
	}

	private static void Release(string key)
	{
		lock (Gate)
		{
			if (!Counts.TryGetValue(key, out var count))
			{
				return;
			}

			if (count <= 1)
			{
				Counts.Remove(key);
			}
			else
			{
				Counts[key] = count - 1;
			}
		}
	}

	private sealed class Lease(string key) : IDisposable
	{
		private int _released;

		public void Dispose()
		{
			if (Interlocked.Exchange(ref _released, 1) == 0)
			{
				Release(key);
			}
		}
	}
}
