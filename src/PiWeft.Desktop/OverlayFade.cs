using Aprillz.MewUI;
using Aprillz.MewUI.Animation;
using Aprillz.MewUI.Controls;

namespace PiWeft.Desktop;

/// 窗口浮层的整块淡入淡出：进入 200ms ease-out-quint，退出 120ms ease-out-cubic。
/// 退出用 ease-out、时长也比 PiDeck 的 200ms 短：ease-in 开头几乎不动，关闭看起来会先卡一下。
internal sealed class OverlayFade
{
	private const int FadeInMs = 200;
	private const int FadeOutMs = 120;

	private readonly FrameworkElement _view;
	private AnimationClock? _clock;
	private Action? _completed;
	private bool _closing;

	public OverlayFade(FrameworkElement view) => _view = view;

	public bool IsClosing => _closing;

	public void Open()
	{
		_closing = false;
		_completed = null;
		Stop();
		_view.IsHitTestVisible = true;
		Start(0, 1, FadeInMs, Easing.EaseOutQuint, null);
	}

	/// 淡出途中又要显示同一个浮层：从当前不透明度接回 1，避免闪一下。
	public void Reopen()
	{
		var from = Math.Clamp(_view.Opacity, 0, 1);
		_closing = false;
		_completed = null;
		Stop();
		_view.IsHitTestVisible = true;
		Start(from, 1, FadeInMs, Easing.EaseOutQuint, null);
	}

	/// 立即放弃正在进行的淡出/淡入，不触发关闭回调；调用方随后自己做同步收尾。
	public void Cancel()
	{
		_closing = false;
		_completed = null;
		Stop();
		_view.Opacity = 1;
		_view.IsHitTestVisible = true;
	}

	public void Close(Action completed)
	{
		if (_closing)
		{
			return;
		}

		if (!Application.IsRunning)
		{
			// 无头环境没有帧脉冲，直接收尾。
			_view.Opacity = 1;
			completed();
			return;
		}

		_closing = true;
		_completed = completed;
		_view.IsHitTestVisible = false;
		var from = Math.Clamp(_view.Opacity, 0, 1);
		if (from <= 0.01)
		{
			FinishClose();
			return;
		}

		Start(from, 0, FadeOutMs, Easing.EaseOutCubic, FinishClose);
	}

	private void Start(double from, double to, int durationMs, Func<double, double> easing, Action? completed)
	{
		_view.Opacity = from;
		if (!Application.IsRunning)
		{
			_view.Opacity = to;
			completed?.Invoke();
			return;
		}

		var clock = new AnimationClock(TimeSpan.FromMilliseconds(durationMs), easing);
		clock.TickCallback = progress =>
		{
			// 窗口/浮层在动画途中被拆掉时不要再碰已离树的元素。
			if (_view.FindVisualRoot() is null)
			{
				Stop();
				return;
			}

			_view.Opacity = from + ((to - from) * progress);
		};
		clock.CompletedCallback = () =>
		{
			_view.Opacity = to;
			_clock = null;
			completed?.Invoke();
		};
		_clock = clock;
		clock.Start();
	}

	private void FinishClose()
	{
		Stop();
		_closing = false;
		var completed = _completed;
		_completed = null;
		_view.Opacity = 1;
		completed?.Invoke();
	}

	private void Stop()
	{
		if (_clock is not { } clock)
		{
			return;
		}

		clock.TickCallback = null;
		clock.CompletedCallback = null;
		clock.Stop();
		_clock = null;
	}
}
