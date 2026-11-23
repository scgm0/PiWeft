using PiWeft.CodingAgent.Extensions;
using Xunit;

namespace PiWeft.CodingAgent.Tests;

/// 扩展 worker 的线程身份：宿主据此决定「已经在 worker 上就内联执行」，
/// 判错会把回调排到自己后面再同步等待，卡满超时。
public sealed class ExtensionWorkerTests
{
	[Fact]
	public async Task WorkRunsOnTheWorkerThreadAndCallersKnowIt()
	{
		using var worker = new ExtensionWorker("thread-id");
		var inside = await worker.RunAsync(() => worker.OnWorkerThread, TimeSpan.FromSeconds(5));
		Assert.True(inside, "worker 上的代码要认出自己在 worker 线程");
		Assert.False(worker.OnWorkerThread, "调用线程不是 worker");
	}

	[Fact]
	public async Task WorkThatTimedOutDoesNotRunLater()
	{
		using var worker = new ExtensionWorker("abandon");
		using var gate = new ManualResetEventSlim(false);
		// 先占住唯一的 worker 线程：后面的项只能排队。
		var blocker = worker.RunAsync(() => {
			gate.Wait();
			return true;
		}, TimeSpan.FromSeconds(10));

		var ran = false;
		var timedOut = worker.RunAsync(() => {
			ran = true;
			return true;
		}, TimeSpan.FromMilliseconds(50));

		await Assert.ThrowsAsync<TimeoutException>(() => timedOut);
		gate.Set();
		Assert.True(await blocker);

		// 哨兵项排在超时项之后：它完成时，超时项已经被 worker 取过。
		await worker.RunAsync(() => true, TimeSpan.FromSeconds(5));
		Assert.False(ran, "超时后放弃的命令不该在后台补跑");
	}
}
