using Aprillz.MewUI;
using Aprillz.MewUI.Animation;
using Aprillz.MewUI.Controls;

namespace PiWeft.Desktop;

/// 窗口浮层的整块淡入淡出：进入 200ms ease-out-quint，退出 120ms ease-out-cubic。
/// 退出用 ease-out、时长也比 PiDeck 的 200ms 短：ease-in 开头几乎不动，关闭看起来会先卡一下。
/// 时钟与回调都是字段：Duration 与 EasingFunction 本来就可写，每次开关重造时钟只是给 GC 添活。
internal sealed class OverlayFade
{
	private const int FadeInMs = 200;
	private const int FadeOutMs = 120;

	// 起点低于这个值就当作已经看不见：直接收尾，不再空跑一帧。
	private const double HiddenThreshold = 0.01;

	private readonly FrameworkElement _view;
	// 与主浮层同生共死的附属元素（例如最大化时补的标题栏压暗条）：单独一层，不跟着主浮层淡出就会硬切。
	private readonly FrameworkElement? _companion;
	private readonly AnimationClock _clock;
	private double _from;
	private double _to;
	private Action? _onClosed;
	private bool _closing;

	public OverlayFade(FrameworkElement view, FrameworkElement? companion = null)
	{
		_view = view;
		_companion = companion;
		_clock = new AnimationClock(TimeSpan.FromMilliseconds(FadeInMs), Easing.EaseOutQuint)
		{
			TickCallback = OnTick,
			CompletedCallback = OnCompleted,
		};
	}

	public bool IsClosing => _closing;

	public void Open()
	{
		_closing = false;
		_onClosed = null;
		Start(0, 1, FadeInMs, Easing.EaseOutQuint);
	}

	/// 淡出途中又要显示同一个浮层：从当前不透明度接回 1，避免闪一下。
	public void Reopen()
	{
		var from = Math.Clamp(_view.Opacity, 0, 1);
		_closing = false;
		_onClosed = null;
		Start(from, 1, FadeInMs, Easing.EaseOutQuint);
	}

	/// 立即放弃正在进行的淡出/淡入，不触发关闭回调；调用方随后自己做同步收尾。
	public void Cancel()
	{
		_closing = false;
		_onClosed = null;
		Stop();
		SetOpacity(1);
	}

	public void Close(Action closed)
	{
		if (_closing)
		{
			return;
		}

		if (!Application.IsRunning)
		{
			SetOpacity(1);
			closed();
			return;
		}

		_closing = true;
		_onClosed = closed;
		var from = Math.Clamp(_view.Opacity, 0, 1);
		if (from <= HiddenThreshold)
		{
			FinishClose();
			return;
		}

		Start(from, 0, FadeOutMs, Easing.EaseOutCubic);
	}

	private void Start(double from, double to, int durationMs, Func<double, double> easing)
	{
		Stop();
		_from = from;
		_to = to;
		SetOpacity(from);
		if (!Application.IsRunning)
		{
			SetOpacity(to);
			return;
		}

		_clock.Duration = TimeSpan.FromMilliseconds(durationMs);
		_clock.EasingFunction = easing;
		_clock.Start();
	}

	// 每帧只读一个父指针就够判断「还在不在窗口里」：OverlayLayer 摘层、窗口关闭都会把它置空。
	private void OnTick(double progress)
	{
		if (_view.Parent is null)
		{
			if (_closing)
			{
				FinishClose();
			}
			else
			{
				Stop();
			}

			return;
		}

		SetOpacity(_from + ((_to - _from) * progress));
	}

	private void OnCompleted()
	{
		SetOpacity(_to);
		if (_closing)
		{
			FinishClose();
		}
	}

	private void FinishClose()
	{
		Stop();
		_closing = false;
		var closed = _onClosed;
		_onClosed = null;
		SetOpacity(1);
		closed?.Invoke();
	}

	private void SetOpacity(double opacity)
	{
		_view.Opacity = opacity;
		if (_companion is not null)
		{
			_companion.Opacity = opacity;
		}
	}

	// Stop 自带「没在跑就返回」，这里不再重复判断：时钟在完成回调里已经自行停掉。
	private void Stop() => _clock.Stop();
}
