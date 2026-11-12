using PiWeft.Ai;
using PiWeft.CodingAgent.Session;
using Xunit;

namespace PiWeft.CodingAgent.Tests;

/// 追加与读取是多线程的（agent 循环在后台线程，界面在主线程）：
/// 条目链必须是直的，读标签/条目不能撞上「Collection was modified」。
public class SessionManagerConcurrencyTests
{
	private const int Writers = 4;
	private const int AppendsPerWriter = 150;

	[Fact]
	public async Task ConcurrentAppendsKeepTheEntryChainLinear()
	{
		var manager = SessionManager.InMemory(Path.GetTempPath());
		using var start = new ManualResetEventSlim(false);

		var writers = new Task[Writers];
		for (var index = 0; index < Writers; index++)
		{
			var writer = index;
			writers[index] = Task.Run(() =>
			{
				start.Wait();
				for (var i = 0; i < AppendsPerWriter; i++)
				{
					manager.AppendMessage(new UserMessage
					{
						Content = MessageContent.FromText($"w{writer}-{i}"),
					});
				}
			});
		}

		start.Set();
		await Task.WhenAll(writers);

		var entries = manager.Entries;
		Assert.Equal(Writers * AppendsPerWriter, entries.Count);
		for (var index = 1; index < entries.Count; index++)
		{
			Assert.Equal(entries[index - 1].Id, entries[index].ParentId);
		}
	}

	[Fact]
	public async Task ConcurrentLabelAppendsAndReadsDoNotThrow()
	{
		var manager = SessionManager.InMemory(Path.GetTempPath());
		var targets = new string[200];
		for (var index = 0; index < targets.Length; index++)
		{
			targets[index] = manager.AppendMessage(new UserMessage { Content = MessageContent.FromText($"target-{index}") });
		}

		using var stop = new CancellationTokenSource();

		var failure = default(Exception);
		var readers = new Task[2];
		for (var index = 0; index < readers.Length; index++)
		{
			readers[index] = Task.Run(() =>
			{
				try
				{
					while (!stop.IsCancellationRequested)
					{
						// 枚举是真实的消费方式：线上字典被并发写入时这里会抛。
						foreach (var pair in manager.Labels)
						{
							_ = pair.Value.Length;
						}

						_ = manager.EntryCount;
						_ = manager.LeafId;
						_ = manager.Header;
						foreach (var entry in manager.Entries)
						{
							_ = entry.Id;
						}
					}
				}
				catch (Exception exception)
				{
					Interlocked.CompareExchange(ref failure, exception, null);
				}
			});
		}

		try
		{
			for (var round = 0; round < 20; round++)
			{
				for (var index = 0; index < targets.Length; index++)
				{
					manager.AppendLabelChange(targets[index], $"label-{round}-{index}");
				}

				// 清除标签也是写路径。
				for (var index = 0; index < targets.Length; index += 2)
				{
					manager.AppendLabelChange(targets[index], null);
				}
			}
		}
		finally
		{
			stop.Cancel();
			await Task.WhenAll(readers);
		}

		Assert.Null(failure);
	}
}
