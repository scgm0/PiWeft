using System.Collections.Concurrent;

namespace PiWeft.CodingAgent.Extensions;

/// <summary>
/// 一个扩展的专用线程与任务队列。
/// <para>
/// Jint 的 <see cref="Jint.Engine"/> 不是线程安全的：扩展注册期（加载时）与运行期（事件回调、
/// 工具执行）都会调用它，而这些调用来自代理线程、工具线程与 UI 线程。所以每个扩展一个线程，
/// 所有进入该引擎的调用都在这里排队执行 —— 这既是线程安全，也顺带得到「一个卡死的扩展
/// 不会拖住别的扩展与会话」的隔离性（超时后放弃等待并把该扩展标记为无响应）。
/// </para>
/// <para>
/// 队列是 FIFO 且**允许重入**：扩展的 <c>execute</c> 在 worker 上跑，期间宿主可能回调
/// <c>pi.ui.confirm</c>；那种回调如果排队等待自己就会死锁，所以已在 worker 线程时会直接内联执行。
/// </para>
/// </summary>
internal sealed class ExtensionWorker : IDisposable
{
	private readonly ConcurrentQueue<Action> _queue = new();
	private readonly SemaphoreSlim _signal = new(0);
	private readonly Thread _thread;
	private readonly int _threadId;
	private volatile bool _stopping;

	/// <summary>
	/// worker 自身的故障。本程序集不依赖日志框架（宿主各自有 sink），所以只收集、由宿主读走并上报。
	/// 一个扩展的内部错误绝不能杀掉 worker 线程 —— 那会让后面所有扩展调用永远挂着。
	/// </summary>
	public ConcurrentQueue<Exception> Faults { get; } = new();

	public ExtensionWorker(string name)
	{
		_thread = new Thread(Run)
		{
			IsBackground = true,
			Name = "piweft-extension-" + name,
		};
		_threadId = Environment.CurrentManagedThreadId;
		_thread.Start();
	}

	/// <summary>当前是否就跑在本扩展的线程上（决定内联执行还是排队）。</summary>
	public bool OnWorkerThread => Environment.CurrentManagedThreadId == _threadId;

	/// <summary>在扩展线程上执行并等待结果。已停止时立刻失败，不排队到永远。</summary>
	public Task<T> RunAsync<T>(Func<T> work, TimeSpan timeout)
	{
		var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
		if (_stopping)
		{
			completion.SetException(new ObjectDisposedException(nameof(ExtensionWorker)));
			return completion.Task;
		}

		_queue.Enqueue(() =>
		{
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

		return completion.Task.WaitAsync(timeout);
	}

	/// <summary>投递一段不等待完成的工作（fire-and-forget 的 UI 调用走这里）。</summary>
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

			_signal.Wait(TimeSpan.FromMilliseconds(200));
		}

		// 退出前排空剩余项，让等待方的 Task 都能落到「已停止」而不是永远挂着。
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
		try
		{
			_signal.Release();
		}
		catch (SemaphoreFullException)
		{
			// 已有唤醒信号在排队。
		}
	}
}
