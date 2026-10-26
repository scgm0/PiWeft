namespace PiWeft.CodingAgent.Tests;

/// <summary>
/// 等一个随时间收敛的观察值，而不是睡死一个固定毫秒数。
/// <para>
/// 保温器的最小缓存寿命是 11 秒（ttl≤10 直接不启用），所以它必然先等约 1 秒才动作。
/// 旧测试写 <c>await Task.Delay(1300)</c>：并行跑整套测试时线程池一忙，1 秒的定时器就会
/// 晚于 1300ms 触发，断言随机变红。这里改成「轮询到条件成立，最长等 20 秒」——结果与
/// 机器负载无关，只在真的不收敛时才失败，失败时给出明确原因。
/// </para>
/// </summary>
internal static class Eventually
{
	private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);

	public static async Task IsTrue(Func<bool> condition, string because)
	{
		var deadline = Environment.TickCount64 + (long)DefaultTimeout.TotalMilliseconds;
		while (Environment.TickCount64 < deadline)
		{
			if (condition())
			{
				return;
			}

			await Task.Delay(10).ConfigureAwait(false);
		}

		throw new TimeoutException($"{because}（等了 {DefaultTimeout.TotalSeconds:0} 秒仍未成立）");
	}
}
