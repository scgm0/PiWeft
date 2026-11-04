using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

namespace PiWeft.Desktop;

/// <summary>
/// 运行中步骤的秒表文本（PiDeck 的 <c>LiveDuration</c> 同构）：
/// 执行中每 100 ms 用「当前时刻 - 开始时刻」刷新一次，结束后停在固定耗时。
/// </summary>
/// <remarks>
/// 节拍放在控件自己身上，而不是让宿主整帧重绘：一次 tick 只改这一枚 TextBlock，
/// 过程组的行布局、虚拟化列表和会话视口都不会因为计时器重新投影。
/// 与 <see cref="PendingReplyView"/> 一样，不可见就停表（折叠、滚出视口、切走的标签页），
/// 下一次测量再启动；无窗口（自检/单测）时只显示一次初值，不建计时器。
/// </remarks>
internal sealed class LiveDurationText : TextBlock
{
	/// <summary>与 <see cref="Strings.FormatDuration"/> 的十分位精度对齐；1 秒一跳会像坏掉的秒表。</summary>
	private const int TickIntervalMs = 100;

	private long _startedAt;
	private long _endedAt;
	private DispatcherTimer? _clock;
	private string? _shown;
	private bool _disposed;

	public LiveDurationText(long startedAt, long? endedAt = null) => SetSpan(startedAt, endedAt);

	/// <summary>换一段起止（思考段会随流式分段重建）；同一段重复调用不会重置计时器。</summary>
	internal void SetSpan(long startedAt, long? endedAt)
	{
		var start = startedAt > 0 ? startedAt : 0;
		var end = endedAt is { } value && value >= start ? value : 0;
		if (start == _startedAt && end == _endedAt) return;
		_startedAt = start;
		_endedAt = end;
		_shown = null;
		UpdateText(NowMs());
		if (end > 0 || start <= 0) Stop();
	}

	/// <summary>秒表读数；<paramref name="endedAt"/> 为 0 时读 <paramref name="now"/>。</summary>
	internal static string Format(long startedAt, long endedAt, long now)
	{
		if (startedAt <= 0) return string.Empty;
		var stop = endedAt > 0 && endedAt >= startedAt ? endedAt : now;
		return Strings.FormatDuration(Math.Max(0, stop - startedAt));
	}

	protected override Size MeasureContent(Size availableSize)
	{
		// 从隐藏里回到画面时补回心跳：文本每拍都在变，测量每次都会走到这里。
		Start();
		return base.MeasureContent(availableSize);
	}

	private void Start()
	{
		if (_startedAt <= 0 || _endedAt > 0 || _clock is not null || _disposed || !Application.IsRunning) return;
		_clock = new DispatcherTimer(TimeSpan.FromMilliseconds(TickIntervalMs));
		_clock.OnTick(Tick);
		_clock.Start();
	}

	private void Tick()
	{
		if (!StreamPump.NeedsFrames(this))
		{
			// 折叠、切走的标签页、滚出视口、整行被回收：停下来，下次测量再起来。
			Stop();
			return;
		}

		UpdateText(NowMs());
	}

	private void UpdateText(long now)
	{
		var text = Format(_startedAt, _endedAt, now);
		if (string.Equals(_shown, text, StringComparison.Ordinal)) return;
		_shown = text;
		Text = text;
	}

	private void Stop()
	{
		if (_clock is not { } clock) return;
		_clock = null;
		clock.Stop();
		clock.Dispose();
	}

	protected override void OnDispose()
	{
		_disposed = true;
		Stop();
		base.OnDispose();
	}

	private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
