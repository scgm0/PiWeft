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
/// 计时用 <see cref="CancellationTokenSource.CancelAfter(TimeSpan)"/> 反复续期，不轮询：
/// 每读到数据就把到期时刻推到「现在 + 超时」，一个流从头到尾只有一个定时器。
/// </para>
/// <para>
/// 超时抛 <see cref="IOException"/> 而不是 <see cref="OperationCanceledException"/>：
/// 用户没按中断，把它报成「已中止」会误导；IOException 会走自动重试的分类器（文案里带 timeout）。
/// </para>
/// </summary>
internal sealed class IdleTimeoutStream : Stream
{
	/// <summary>最短续期间隔：数据密集时不必为每个字节都动一次定时器。</summary>
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

	// 走 Memory 重载，而不是基组的数组重载：后者会退回 BeginRead/EndRead，
	// 对只实现了异步读的内层流（以及很多测试替身）等于把读退回同步路径。
	public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
		ReadAsync(new Memory<byte>(buffer, offset, count), cancellationToken).AsTask();

	public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
	{
		// 调用方的令牌通常就是请求令牌：这时直接用共享的 linked 源，不为每次读建一个 CTS。
		// 一次长流有上万个读，每个都分配再释放 CancellationTokenSource 是白扔的对象。
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

	/// <summary>
	/// 是「空闲到期」而不是用户中止或提前释放：请求令牌没被取消、本流也没在释放
	/// （<see cref="Release"/> 会取消计时令牌，那之后到来的取消不该被说成超时）。
	/// </summary>
	private bool IsIdleTimeout() =>
		_idle.IsCancellationRequested && !_requestToken.IsCancellationRequested && !_disposed;

	/// <summary>把到期时刻推到「现在 + 超时」。数据密集时最多每 50 ms 动一次定时器：
	/// 于是空闲窗口可能被提前至多 50 ms 判定 —— 相对默认的 300 秒可以忽略，
	/// 但换来的是「一秒几百个 SSE 分片也不反复重置定时器」。
	/// </summary>
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

	/// <summary>
	/// 释放顺序：标记已释放 → 取消计时 → 释放内层流 → 才释放令牌源。
	/// 内层可能还有一个挂着注册的读在等取消；先把流关掉，那个注册一定已经走完，
	/// 之后 dispose 才不会撞上「令牌源已释放」。
	/// </summary>
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
