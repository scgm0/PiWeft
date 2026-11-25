using Aprillz.MewUI;
using Aprillz.MewUI.Animation;
using Aprillz.MewUI.Controls;

namespace PiWeft.Desktop;

/// 窗口浮层的整块淡入淡出。铺满主窗口的大面积浮层默认 220ms / 160ms ease-out-quad：
/// quint 在慢帧下会把第一帧直接推到五六十个百分点，整屏亮度跳一下。淡入前先以近乎透明的
/// 状态铺一帧，把布局、字体缓存和透明层的首帧成本留在动画之前。
/// 退出比进入短：关闭要利落；ease-in 开头几乎不动，看起来会先卡一下。
/// 小面积的窗口浮层（toast）用 <see cref="PopupLike"/> 跟原生弹层同一档时长与曲线。
/// 时钟与回调都是字段：Duration 与 EasingFunction 本来就可写，每次开关重造时钟只是给 GC 添活。
internal sealed class OverlayFade
{
	private const int DefaultFadeInMs = 220;
	private const int DefaultFadeOutMs = 160;

	// 起点低于这个值就当作已经看不见：直接收尾，不再空跑一帧。
	private const double HiddenThreshold = 0.01;

	// 预热帧用：大于 0 才会建透明层，小于 HiddenThreshold 所以看不见。
	private const double PrewarmOpacity = 0.005;

	private static readonly Func<double, double> OverlayEase = Easing.EaseOutQuad;

	private readonly FrameworkElement _view;
	// 与主浮层同生共死的附属元素（例如最大化时补的标题栏压暗条）：单独一层，不跟着主浮层淡出就会硬切。
	private readonly FrameworkElement? _companion;
	private readonly int _fadeInMs;
	private readonly int _fadeOutMs;
	private readonly Func<double, double> _fadeInEase;
	private readonly Func<double, double> _fadeOutEase;
	private readonly AnimationClock _clock;
	private double _from;
	private double _to;
	private Action? _onClosed;
	private bool _closing;
	private Window? _prewarmWindow;

	public OverlayFade(
		FrameworkElement view,
		FrameworkElement? companion = null,
		int fadeInMs = DefaultFadeInMs,
		int fadeOutMs = DefaultFadeOutMs,
		Func<double, double>? fadeInEase = null,
		Func<double, double>? fadeOutEase = null)
	{
		_view = view;
		_companion = companion;
		_fadeInMs = fadeInMs;
		_fadeOutMs = fadeOutMs;
		_fadeInEase = fadeInEase ?? OverlayEase;
		_fadeOutEase = fadeOutEase ?? OverlayEase;
		_clock = new AnimationClock(TimeSpan.FromMilliseconds(fadeInMs), _fadeInEase)
		{
			TickCallback = OnTick,
			CompletedCallback = OnCompleted,
		};
	}

	/// 与原生弹层同一档时长与曲线：toast 这类小面积浮层用这一档，弹层怎么淡它就怎么淡。
	public static OverlayFade PopupLike(FrameworkElement view) =>
		new(view, fadeInMs: FadePopup.FadeInMs, fadeOutMs: FadePopup.FadeOutMs,
			fadeInEase: FadePopup.FadeInEasing, fadeOutEase: FadePopup.FadeOutEasing);

	public bool IsClosing => _closing;

	public void Open()
	{
		_closing = false;
		_onClosed = null;
		Stop();
		UnsubscribePrewarm();
		if (!Application.IsRunning || _view.FindVisualRoot() is not Window window)
		{
			Start(0, 1, _fadeInMs, _fadeInEase);
			return;
		}

		_prewarmWindow = window;
		SetOpacity(PrewarmOpacity);
		window.FrameRendered += OnPrewarmRendered;
		_view.InvalidateVisual();
	}

	private void OnPrewarmRendered()
	{
		if (_prewarmWindow is not { } window) return;
		UnsubscribePrewarm();
		if (_view.Parent is null) return;
		// 从预热透明度接着走：先落到 0 会让那一帧把已建好的透明层丢掉，下一帧又要重建。
		Start(PrewarmOpacity, 1, _fadeInMs, _fadeInEase);
	}

	private void UnsubscribePrewarm()
	{
		if (_prewarmWindow is { } window)
		{
			window.FrameRendered -= OnPrewarmRendered;
			_prewarmWindow = null;
		}
	}

	/// 淡出途中又要显示同一个浮层：从当前不透明度接回 1，避免闪一下。
	public void Reopen()
	{
		UnsubscribePrewarm();
		var from = Math.Clamp(_view.Opacity, 0, 1);
		_closing = false;
		_onClosed = null;
		Start(from, 1, _fadeInMs, _fadeInEase);
	}

	/// 立即放弃正在进行的淡出/淡入，不触发关闭回调；调用方随后自己做同步收尾。
	public void Cancel()
	{
		UnsubscribePrewarm();
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

		UnsubscribePrewarm();
		if (!Application.IsRunning)
		{
			SetOpacity(1);
			closed();
			return;
		}

		_closing = true;
		_onClosed = closed;
		_to = 0;
		var from = Math.Clamp(_view.Opacity, 0, 1);
		if (from <= HiddenThreshold)
		{
			FinishClose();
			return;
		}

		Start(from, 0, _fadeOutMs, _fadeOutEase);
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
		// 先收视图再还原不透明度：回调里若把移除推后一拍，先还原就会多画一帧满不透明的旧浮层。
		// 回调里又开了新的一段（Reopen/Open）时不能覆盖回去，那个时钟正在跑。
		closed?.Invoke();
		if (!_clock.IsRunning)
		{
			SetOpacity(1);
		}
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
