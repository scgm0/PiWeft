using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

namespace PiWeft.Desktop;

/// 子代理当前工具加「多久以前」。运行中的行可能几分钟没有新事件，
/// 只靠工具名分不出在跑还是卡住；这一枚文本自己每秒走一次，不等 hub 的 Changed。
/// 与 <see cref="LiveDurationText"/> 同一套生命周期：挂到窗口才建表，摘掉即停。
internal sealed class LiveActivityText : TextBlock
{
	private const int TickIntervalMs = 1_000;
	// 超过这个时长才提示「没有新动作」：跑测试与读大文件几分钟很正常，太早报会变成噪音。
	private const long StaleAfterMs = 120_000;

	private string _activity = string.Empty;
	private long _lastActivityAt;
	private bool _running;
	private DispatcherTimer? _clock;
	private string? _shown;
	private bool _disposed;

	/// 每次工具起止或状态边沿调用；运行中才建表。
	internal void SetActivity(string? activity, long lastActivityAt, bool running)
	{
		if (_disposed)
		{
			return;
		}

		_activity = activity ?? string.Empty;
		_lastActivityAt = lastActivityAt;
		// 活动名在工具边界会短暂为空；不要把计时器跟着停掉，否则每个工具调用都重建一个。
		_running = running;
		UpdateText(NowMs());
		if (_running)
		{
			Start();
		}
		else
		{
			Stop();
		}
	}

	protected override Size MeasureContent(Size availableSize)
	{
		// 首次进画面时补一次启动：构建时还没挂到窗口，SetActivity 建不了表。
		if (_running) Start();
		return base.MeasureContent(availableSize);
	}

	protected override void OnDispose()
	{
		_disposed = true;
		Stop();
		base.OnDispose();
	}

	private void Start()
	{
		if (_disposed || _clock is not null || !Application.IsRunning || FindVisualRoot() is not Window) return;
		_clock = new DispatcherTimer(TimeSpan.FromMilliseconds(TickIntervalMs));
		_clock.OnTick(Tick);
		_clock.Start();
	}

	private void Tick()
	{
		// 摘掉、折叠或释放后不再写；重新挂回会重新测量，MeasureContent 会再启动。
		if (_disposed || FindVisualRoot() is not Window || StreamPump.HiddenByCollapsedExpander(this))
		{
			Stop();
			return;
		}

		// 不可见的拍只跳过写入，不停表：滚出视口或切到别的标签后不会有测量来叫醒它。
		if (!IsVisible || !StreamPump.NeedsFrames(this))
		{
			return;
		}

		UpdateText(NowMs());
	}

	private void UpdateText(long now)
	{
		var text = _activity.Length == 0
			? string.Empty
			: _lastActivityAt <= 0
				? _activity
				: Strings.SubagentActivity(_activity, Math.Max(0, now - _lastActivityAt), StaleAfterMs);
		if (string.Equals(_shown, text, StringComparison.Ordinal)) return;
		_shown = text;
		Text = text;
		var visible = text.Length > 0;
		if (IsVisible != visible) IsVisible = visible;
	}

	private void Stop()
	{
		if (_clock is not { } clock) return;
		_clock = null;
		clock.Stop();
		clock.Dispose();
	}

	private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
