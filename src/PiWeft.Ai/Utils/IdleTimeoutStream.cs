using System.Diagnostics;

namespace PiWeft.Ai;

/// <summary>
/// 给响应体加「空闲超时」：连续 <see cref="_timeout"/> 没有读到任何字节就失败。
/// <para>
/// 为什么不是总时长：模型思考几分钟仍在产出 delta 是合法长流，用总时长会在正常路径上切断它。
/// 反过来，供应商把连接挂着不发也不关时，<see cref="HttpClient"/> 的 <c>Timeout = Infinite</c>
/// 会让这一轮永远等下去 —— pi 用 <c>settings.httpIdleTimeoutMs</c>（默认 300 秒）兜住这种情况。
/// </para>
/// <para>
/// 超时抛 <see cref="IOException"/> 而不是 <see cref="OperationCanceledException"/>：
/// 用户没按中断，把它报成「已中止」会误导；IOException 会走自动重试的分类器（文案里带 timeout）。
/// </para>
/// </summary>
internal sealed class IdleTimeoutStream : Stream
{
	private static readonly TimeSpan MinimumCheckInterval = TimeSpan.FromMilliseconds(100);
	private static readonly TimeSpan MaximumCheckInterval = TimeSpan.FromSeconds(1);

	private readonly Stream _inner;
	private readonly TimeSpan _timeout;
	private readonly CancellationToken _requestToken;
	private readonly CancellationTokenSource _idle = new();
	private readonly CancellationTokenSource _linked;
	private readonly CancellationTokenSource _stopWatchdog = new();
	private readonly Task _watchdog;
	private long _lastActivity;
	private volatile bool _timedOut;
	private bool _disposed;

	public IdleTimeoutStream(Stream inner, TimeSpan timeout, CancellationToken requestToken)
	{
		ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);

		_inner = inner;
		_timeout = timeout;
		_requestToken = requestToken;
		_lastActivity = Stopwatch.GetTimestamp();
		_linked = CancellationTokenSource.CreateLinkedTokenSource(requestToken, _idle.Token);
		_watchdog = WatchAsync();
	}

	private async Task WatchAsync()
	{
		var interval = TimeSpan.FromMilliseconds(
			Math.Clamp(_timeout.TotalMilliseconds / 4, MinimumCheckInterval.TotalMilliseconds, MaximumCheckInterval.TotalMilliseconds));
		try
		{
			while (true)
			{
				await Task.Delay(interval, _stopWatchdog.Token).ConfigureAwait(false);
				if (Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastActivity)) < _timeout)
				{
					continue;
				}

				_timedOut = true;
				_idle.Cancel();
				return;
			}
		}
		catch (OperationCanceledException)
		{
			// Dispose 或超时后退出。
		}
	}

	public override bool CanRead => _inner.CanRead;

	public override bool CanSeek => _inner.CanSeek;

	public override bool CanWrite => _inner.CanWrite;

	public override long Length => _inner.Length;

	public override long Position
	{
		get => _inner.Position;
		set => _inner.Position = value;
	}

	public override void Flush() => _inner.Flush();

	public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

	public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

	public override void SetLength(long value) => _inner.SetLength(value);

	public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

	public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
		ReadCoreAsync(
			(stream, token) => stream.ReadAsync(buffer, offset, count, token),
			cancellationToken);

	public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
	{
		var (linked, perCall) = Link(cancellationToken);
		return ReadMemoryAsync(buffer, linked, perCall);
	}

	private async ValueTask<int> ReadMemoryAsync(
		Memory<byte> buffer,
		CancellationToken linked,
		CancellationTokenSource? perCall)
	{
		try
		{
			var read = await _inner.ReadAsync(buffer, linked).ConfigureAwait(false);
			Touch();
			return read;
		}
		catch (OperationCanceledException) when (TimedOut)
		{
			throw IdleTimeout();
		}
		finally
		{
			perCall?.Dispose();
		}
	}

	private async Task<int> ReadCoreAsync(
		Func<Stream, CancellationToken, Task<int>> read,
		CancellationToken cancellationToken)
	{
		var (linked, perCall) = Link(cancellationToken);
		try
		{
			var result = await read(_inner, linked).ConfigureAwait(false);
			Touch();
			return result;
		}
		catch (OperationCanceledException) when (TimedOut)
		{
			throw IdleTimeout();
		}
		finally
		{
			perCall?.Dispose();
		}
	}

	/// <summary>调用方自己的令牌（通常就是请求令牌）直接复用共享的 linked 源，只有不同的令牌才另建。</summary>
	private (CancellationToken Token, CancellationTokenSource? PerCall) Link(CancellationToken cancellationToken)
	{
		if (cancellationToken.CanBeCanceled && cancellationToken != _requestToken)
		{
			var perCall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _linked.Token);
			return (perCall.Token, perCall);
		}

		return (_linked.Token, null);
	}

	private bool TimedOut => _timedOut && !_requestToken.IsCancellationRequested;

	private void Touch() => Interlocked.Exchange(ref _lastActivity, Stopwatch.GetTimestamp());

	private IOException IdleTimeout() =>
		new($"Provider response stream idle timeout: no data for {_timeout.TotalSeconds:0.###} seconds");

	protected override void Dispose(bool disposing)
	{
		if (disposing && !_disposed)
		{
			_disposed = true;
			_stopWatchdog.Cancel();
			_idle.Cancel();
			_linked.Dispose();
			_idle.Dispose();
			_stopWatchdog.Dispose();
			_inner.Dispose();
		}

		base.Dispose(disposing);
	}

	public override async ValueTask DisposeAsync()
	{
		if (!_disposed)
		{
			_disposed = true;
			_stopWatchdog.Cancel();
			_idle.Cancel();
			_linked.Dispose();
			_idle.Dispose();
			_stopWatchdog.Dispose();
			await _inner.DisposeAsync().ConfigureAwait(false);
		}

		await base.DisposeAsync().ConfigureAwait(false);
	}
}
