using System.Collections.Concurrent;

namespace PiWeft.CodingAgent.Extensions;

/// 一个扩展的专用线程与任务队列。
/// Jint 的 <see cref="Jint.Engine"/> 不是线程安全的：扩展注册期（加载时）与运行期（事件回调、
/// 工具执行）都会调用它，而这些调用来自代理线程、工具线程与 UI 线程。所以每个扩展一个线程，
/// 所有进入该引擎的调用都在这里排队执行，这既是线程安全，也顺带得到「一个卡死的扩展
/// 不会拖住别的扩展与会话」的隔离性（超时后放弃等待并把该扩展标记为无响应）。
/// 队列是 FIFO 且**允许重入**：扩展的 <c>execute</c> 在 worker 上跑，期间宿主可能回调
/// <c>pi.ui.confirm</c>；那种回调如果排队等待自己就会死锁，所以已在 worker 线程时会直接内联执行。
internal sealed class ExtensionWorker : IDisposable
{
	private readonly ConcurrentQueue<Action> _queue = new();
	private readonly SemaphoreSlim _signal = new(0);
	private int _wakePending;
	private readonly Thread _thread;
	private volatile int _threadId;
	private volatile bool _stopping;

	/// worker 自身的故障。本程序集不依赖日志框架（宿主各自有 sink），所以只收集、由宿主读走并上报。
	/// 一个扩展的内部错误不能杀掉 worker 线程，否则后面所有扩展调用都会挂着。
	public ConcurrentQueue<Exception> Faults { get; } = new();

	public ExtensionWorker(string name)
	{
		_thread = new Thread(Run)
		{
			IsBackground = true,
			Name = "piweft-extension-" + name,
		};
		_thread.Start();
	}

	public bool OnWorkerThread => Environment.CurrentManagedThreadId == _threadId;

	public Task<T> RunAsync<T>(Func<T> work, TimeSpan timeout)
	{
		var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
		if (_stopping)
		{
			completion.SetException(new ObjectDisposedException(nameof(ExtensionWorker)));
			return completion.Task;
		}

		// 超时后调用方已经放弃：项还在队列里，跑掉会变成「当时没反应、过后才生效」。
		// 用一个共享格子记放弃状态，worker 取到它时直接落到已取消。
		int[] abandoned = [0];
		_queue.Enqueue(() =>
		{
			if (Volatile.Read(ref abandoned[0]) != 0)
			{
				completion.TrySetCanceled();
				return;
			}

			try
			{
				completion.TrySetResult(work());
			}
			catch (Exception exception)
			{
				completion.TrySetException(exception);
			}
		});
		Wake();

		return AwaitWithTimeoutAsync(completion.Task, timeout, abandoned);
	}

	private static async Task<T> AwaitWithTimeoutAsync<T>(Task<T> task, TimeSpan timeout, int[] abandoned)
	{
		try
		{
			return await task.WaitAsync(timeout).ConfigureAwait(false);
		}
		catch (TimeoutException)
		{
			Volatile.Write(ref abandoned[0], 1);
			throw;
		}
	}

	public void Post(Action work)
	{
		if (_stopping)
		{
			return;
		}

		_queue.Enqueue(work);
		Wake();
	}

	public void Dispose()
	{
		_stopping = true;
		Wake();
	}

	private void Run()
	{
		// 必须在 worker 线程上取：构造函数的线程 id 不是执行 Run 的那一个，
		// 比错会让扩展把「已在 worker 上」判成 false，回调自己排到自己后面再同步等，卡满超时。
		_threadId = Environment.CurrentManagedThreadId;
		while (!_stopping)
		{
			while (_queue.TryDequeue(out var action))
			{
				try
				{
					action();
				}
				catch (Exception exception)
				{
					Faults.Enqueue(exception);
				}
			}

			if (_stopping)
			{
				break;
			}

			// 一次入队只发一个信号；否则排 1000 条就要空转 1000 次 Wait/TryDequeue。
			_signal.Wait();
			Interlocked.Exchange(ref _wakePending, 0);
		}

		// 退出前排空剩余项，让等待方的 Task 都落到「已停止」，不挂着。
		while (_queue.TryDequeue(out var action))
		{
			try
			{
				action();
			}
			catch (ObjectDisposedException)
			{
				// 停止竞态下的正常结果。
			}
			catch (Exception exception)
			{
				Faults.Enqueue(exception);
			}
		}
	}

	private void Wake()
	{
		// 0→1 才 Release。消费端唤醒后把标志清回 0，避免排 N 条时信号计数堆到 N。
		if (Interlocked.Exchange(ref _wakePending, 1) == 0)
		{
			_signal.Release();
		}
	}
}
