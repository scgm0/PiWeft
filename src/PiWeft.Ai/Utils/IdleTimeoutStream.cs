using System.Diagnostics;

namespace PiWeft.Ai;

/// 响应体的空闲超时：连续 <see cref="_timeout"/> 收不到字节就失败。
/// 按空闲而非总时长计时：模型思考几分钟但仍在产出 delta，属于合法长流。
/// 供应商挂住连接不发也不关时，<see cref="HttpClient"/>（<c>Timeout = Infinite</c>）会无限等待，
/// 对应 pi 的 <c>settings.httpIdleTimeoutMs</c>，默认 300 秒。
/// 每读到数据就 <see cref="CancellationTokenSource.CancelAfter(TimeSpan)"/> 续期，一个流只有一个定时器。
/// 超时抛 <see cref="IOException"/> 而不抛 <see cref="OperationCanceledException"/>：
/// 用户没按中断，报成「已中止」会误导；IOException 还会进自动重试的分类器（文案带 timeout）。
internal sealed class IdleTimeoutStream : Stream
{
	private static readonly TimeSpan MinimumRenewInterval = TimeSpan.FromMilliseconds(50);

	private readonly Stream _inner;
	private readonly TimeSpan _timeout;
	private readonly CancellationToken _requestToken;
	private readonly CancellationTokenSource _idle;
	private readonly CancellationTokenSource _linked;
	private long _lastRenew;
	private bool _disposed;

	public IdleTimeoutStream(Stream inner, TimeSpan timeout, CancellationToken requestToken)
	{
		ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);

		_inner = inner;
		_timeout = timeout;
		_requestToken = requestToken;
		_idle = new CancellationTokenSource();
		_linked = CancellationTokenSource.CreateLinkedTokenSource(requestToken, _idle.Token);
		_lastRenew = Stopwatch.GetTimestamp();
		ArmTimeout();
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

	// 走 Memory 重载：数组重载会退回 BeginRead/EndRead，只实现异步读的内层流等于退回同步路径。
	public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
		ReadAsync(new Memory<byte>(buffer, offset, count), cancellationToken).AsTask();

	public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
	{
		// 调用方令牌通常就是请求令牌，直接复用 linked 源；长流上万次读，每次建一个 CTS 是白费分配。
		var (token, perCall) = Link(cancellationToken);
		try
		{
			var read = await _inner.ReadAsync(buffer, token).ConfigureAwait(false);
			Renew();
			return read;
		}
		catch (OperationCanceledException) when (IsIdleTimeout())
		{
			throw new IOException(
				$"Provider response stream idle timeout: no data for {_timeout.TotalSeconds:0.###} seconds");
		}
		finally
		{
			perCall?.Dispose();
		}
	}

	private (CancellationToken Token, CancellationTokenSource? PerCall) Link(CancellationToken cancellationToken)
	{
		if (cancellationToken.CanBeCanceled && cancellationToken != _requestToken)
		{
			var perCall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _linked.Token);
			return (perCall.Token, perCall);
		}

		return (_linked.Token, null);
	}

	/// 空闲到期，不是用户中止或提前释放：请求令牌没取消，本流也没在释放。
	/// <see cref="Release"/> 会取消计时令牌，那之后到来的取消不算超时。
	private bool IsIdleTimeout() =>
		_idle.IsCancellationRequested && !_requestToken.IsCancellationRequested && !_disposed;

	/// 到期时刻推到「现在 + 超时」。数据密集时每 50 ms 最多动一次定时器，
	/// 空闲判定因此可能提前 50 ms；相对默认 300 秒可忽略，换来一秒几百个 SSE 分片也不重置定时器。
	private void Renew()
	{
		var now = Stopwatch.GetTimestamp();
		if (Stopwatch.GetElapsedTime(_lastRenew) < MinimumRenewInterval)
		{
			return;
		}

		_lastRenew = now;
		ArmTimeout();
	}

	private void ArmTimeout()
	{
		if (_disposed || _idle.IsCancellationRequested)
		{
			return;
		}

		try
		{
			_idle.CancelAfter(_timeout);
		}
		catch (ObjectDisposedException)
		{
			// 与并发释放竞争：流已经不读了，超时不再有意义。
		}
	}

	protected override void Dispose(bool disposing)
	{
		if (disposing)
		{
			ReleaseCore();
			_inner.Dispose();
			ReleaseTokens();
		}

		base.Dispose(disposing);
	}

	public override async ValueTask DisposeAsync()
	{
		ReleaseCore();
		await _inner.DisposeAsync().ConfigureAwait(false);
		ReleaseTokens();
		await base.DisposeAsync().ConfigureAwait(false);
	}

	/// 释放顺序：标记已释放 → 取消计时 → 关内层流 → 释放令牌源。
	/// 内层可能还有登记了取消的读；先关流，等注册走完再 dispose 令牌源，才不会撞上「已释放」。
	private void ReleaseCore()
	{
		if (_disposed)
		{
			return;
		}

		_disposed = true;
		_idle.Cancel();
	}

	private void ReleaseTokens()
	{
		try
		{
			_linked.Dispose();
			_idle.Dispose();
		}
		catch (ObjectDisposedException)
		{
			// 重复释放：_disposed 已经挡住了正常路径，这里只兜底。
		}
	}
}
