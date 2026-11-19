namespace PiWeft.Ai.Auth;

/// 在途的 OAuth 刷新任务。退出前等它们落盘：token 端点可能已经轮换了 refresh token，
/// 进程在响应到达与写入 auth.json 之间被信号打断，下次登录会拿到 invalid_grant。
public static class CredentialWrites
{
	private static readonly HashSet<Task> Pending = [];
	private static readonly Lock Gate = new();

	/// 跟踪一个刷新任务，任务结束（无论成败）后自动移出。
	public static void Track(Task task)
	{
		if (task.IsCompleted)
		{
			return;
		}

		lock (Gate)
		{
			Pending.Add(task);
		}

		_ = task.ContinueWith(
			static completed =>
			{
				lock (Gate)
				{
					Pending.Remove(completed);
				}
			},
			CancellationToken.None,
			TaskContinuationOptions.ExecuteSynchronously,
			TaskScheduler.Default);
	}

	/// 等在途刷新结束或超时。超时只放弃等待，不取消刷新：取消会丢掉刚换到的新令牌。
	public static Task WaitAsync(TimeSpan timeout)
	{
		Task[] pending;
		lock (Gate)
		{
			pending = [.. Pending];
		}

		return pending.Length == 0 ? Task.CompletedTask : Task.WhenAny(Task.WhenAll(pending), Task.Delay(timeout));
	}
}
