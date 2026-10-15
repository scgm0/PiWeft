using System.Diagnostics;
using System.Threading.Channels;

namespace PiWeft.Ai;

/// <summary>
/// 基于推送的异步事件流，并带一个最终结果。消费方用 <c>await foreach</c> 迭代，
/// 生产方用 <see cref="Push"/> 推送、用 <see cref="End"/> 结束。
/// </summary>
public class EventStream<T, TResult> : IAsyncEnumerable<T>
{
	private readonly Channel<T> _channel = Channel.CreateUnbounded<T>(new UnboundedChannelOptions
	{
		SingleReader = true,
		SingleWriter = false,
		AllowSynchronousContinuations = false,
	});

	private readonly TaskCompletionSource<TResult> _result =
		new(TaskCreationOptions.RunContinuationsAsynchronously);

	private readonly Func<T, bool> _isComplete;
	private readonly Func<T, TResult> _extractResult;
	private int _done;

	public EventStream(Func<T, bool> isComplete, Func<T, TResult> extractResult)
	{
		_isComplete = isComplete;
		_extractResult = extractResult;
	}

	/// <summary>最终结果，由终止事件或 <see cref="End"/> 结算。</summary>
	public Task<TResult> Result => _result.Task;

	public bool IsDone => Volatile.Read(ref _done) != 0;

	public void Push(T @event)
	{
		if (IsDone)
		{
			return;
		}

		if (_isComplete(@event))
		{
			Interlocked.Exchange(ref _done, 1);
			_result.TrySetResult(_extractResult(@event));
			_channel.Writer.TryWrite(@event);
			_channel.Writer.TryComplete();
			return;
		}

		_channel.Writer.TryWrite(@event);
	}

	/// <summary>在没有终止事件的情况下结束流。</summary>
	public void End()
	{
		if (Interlocked.Exchange(ref _done, 1) != 0)
		{
			return;
		}

		_channel.Writer.TryComplete();
	}

	/// <summary>结束流并显式给出结果。</summary>
	public void End(TResult result)
	{
		if (Interlocked.Exchange(ref _done, 1) == 0)
		{
			_channel.Writer.TryComplete();
		}

		_result.TrySetResult(result);
	}

	/// <summary>让流失败；已经推送的事件仍会被消费完。</summary>
	public void Fail(Exception exception)
	{
		if (Interlocked.Exchange(ref _done, 1) == 0)
		{
			_channel.Writer.TryComplete(exception);
		}

		_result.TrySetException(exception);
	}

	public async IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
	{
		await foreach (var item in _channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
		{
			yield return item;
		}
	}
}

/// <summary>
/// 一次助手回复的事件流，同时负责计时：最终消息会带上 <c>durationMs</c>，
/// 用单调时钟从流创建时刻起算；若消息已自带该字段，或它的时间戳早于流创建，则不改动。
/// </summary>
public sealed class AssistantMessageEventStream : EventStream<AssistantMessageEvent, AssistantMessage>
{
	private readonly long _startedAtWallClock = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
	private readonly long _startedAtMonotonic = Stopwatch.GetTimestamp();
	private long _firstDelta;
	private long _firstText;

	public AssistantMessageEventStream()
		: base(
			static e => e is DoneEvent or ErrorEvent,
			static e => e switch
			{
				DoneEvent done => done.Message,
				ErrorEvent error => error.Error,
				_ => throw new InvalidOperationException("Unexpected event type for final result"),
			})
	{
	}

	public new void Push(AssistantMessageEvent @event)
	{
		if (IsDone) return;
		switch (@event)
		{
			case TextDeltaEvent { Delta.Length: > 0 }:
				_firstText = _firstText == 0 ? Stopwatch.GetTimestamp() : _firstText;
				_firstDelta = _firstDelta == 0 ? _firstText : _firstDelta;
				break;
			case ThinkingDeltaEvent { Delta.Length: > 0 }:
				_firstDelta = _firstDelta == 0 ? Stopwatch.GetTimestamp() : _firstDelta;
				break;
			case DoneEvent done:
				Time(done.Message);
				break;
			case ErrorEvent error:
				Time(error.Error);
				break;
		}

		base.Push(@event);
	}

	public new void End(AssistantMessage result)
	{
		if (!IsDone) Time(result);
		base.End(result);
	}

	private void Time(AssistantMessage message)
	{
		// Historical/scripted results keep their original timing, including missing fields.
		if (message.DurationMs is not null || message.Timestamp < _startedAtWallClock)
		{
			return;
		}

		var ended = Stopwatch.GetTimestamp();
		message.DurationMs = Stopwatch.GetElapsedTime(_startedAtMonotonic, ended).TotalMilliseconds;
		var firstContent = _firstText != 0 ? _firstText : _firstDelta;
		if (firstContent != 0)
		{
			message.FirstTokenMs ??= Stopwatch.GetElapsedTime(_startedAtMonotonic, firstContent).TotalMilliseconds;
			message.StreamingDurationMs ??= Stopwatch.GetElapsedTime(_firstDelta, ended).TotalMilliseconds;
		}
	}
}
