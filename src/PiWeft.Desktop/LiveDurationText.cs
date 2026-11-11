using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

namespace PiWeft.Desktop;

/// 运行中步骤的秒表文本（PiDeck 的 <c>LiveDuration</c> 同构）：
/// 执行中每 100 ms 用「当前时刻 - 开始时刻」刷新一次，结束后停在固定耗时。
/// 节拍放在控件自己身上，不让宿主整帧重绘：一次 tick 只改这一枚 TextBlock，
/// 过程组的行布局、虚拟化列表和会话视口都不会因为计时器重新投影。
/// 与 <see cref="PendingReplyView"/> 一样，不可见时不写文本；但<b>不停表</b>：
/// MewUI 滚动只 InvalidateArrange，不重新测量，所以「下次测量再启动」永远等不到
/// （实测：计时停在离开视口那一刻）。空转一拍只做一次 <see cref="StreamPump.NeedsFrames"/> 向上遍历，
/// 10Hz 量级可不计；无窗口（自检/单测）时只显示一次初值，不建计时器。
/// 折叠的 Expander 内容例外：它不参与视觉树，停表后展开会重新测量，因此那里真正停表。
internal sealed class LiveDurationText : TextBlock
{
	private const int TickIntervalMs = 100;

	private long _startedAt;
	private long _endedAt;
	private DispatcherTimer? _clock;
	private string? _shown;
	private bool _disposed;

	public LiveDurationText(long startedAt, long? endedAt = null) => SetSpan(startedAt, endedAt);

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

	internal static string Format(long startedAt, long endedAt, long now)
	{
		if (startedAt <= 0) return string.Empty;
		var stop = endedAt > 0 && endedAt >= startedAt ? endedAt : now;
		return Strings.FormatDuration(Math.Max(0, stop - startedAt));
	}

	protected override Size MeasureContent(Size availableSize)
	{
		// 首次进入画面时启动心跳。之后它自己一直跳，不靠测量「复活」（见类注释）。
		Start();
		return base.MeasureContent(availableSize);
	}

	private void Start()
	{
		if (_startedAt <= 0 || _endedAt > 0 || _clock is not null || _disposed || !Application.IsRunning) return;
		// 折叠内容里的行可能被上层重新测量；真开始后下拍也会停，不如这里直接不建计时器。
		if (StreamPump.HiddenByCollapsedExpander(this)) return;
		_clock = new DispatcherTimer(TimeSpan.FromMilliseconds(TickIntervalMs));
		_clock.OnTick(Tick);
		_clock.Start();
	}

	private void Tick()
	{
		// 折叠的 Expander 内容不再参与视觉树；重新展开会重新测量，MeasureContent 会再 Start，
		// 所以这条路可以真正停表，免得收起执行过程后隐藏的步骤每秒白跳十次。
		if (StreamPump.HiddenByCollapsedExpander(this))
		{
			Stop();
			// 重新展开时做一次测量才能再 Start；不靠折叠动画或无关数据帧碰巧唤醒这一行。
			InvalidateMeasure();
			return;
		}

		// 其余不可见的拍只跳过写入，绝不停表：滚出视口再回来不会有测量来叫醒它，
		// 停了读数就冻在离开那一刻（切走的标签页同理）。自己被显式隐藏时也不必写，
		// 但要停表得由宿主收口跨度（见 AssistantMessageView）。
		if (!IsVisible || !StreamPump.NeedsFrames(this)) return;

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
