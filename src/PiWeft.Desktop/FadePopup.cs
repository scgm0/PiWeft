using Aprillz.MewUI;
using Aprillz.MewUI.Animation;
using Aprillz.MewUI.Controls;

namespace PiWeft.Desktop;

/// 原生 <see cref="Popup"/> 的进出场动效：进入 200ms ease-out-quint，退出 120ms ease-out-cubic。
/// 退出用 ease-out、时长也比 PiDeck 的 200ms 短：ease-in 开头几乎不动，关弹层像先卡一下。
/// 淡出留在弹层自己的原生窗口里做完，再关窗：窗口一藏，像素就只剩别的表面能画，
/// 而另一块表面的首帧最早也要下一帧才上屏——实测中间空 2–3 帧（≈50ms），
/// 菜单先整个消失、再以 81% 不透明度浮回来，比不做淡出更晃眼。框架自己关掉的路径
/// （外点、切窗、标签页收起）没有窗口可淡，直接收。
internal sealed class FadePopup : IDisposable
{
	private const int FadeInMs = 200;
	private const int FadeOutMs = 120;

	// 起点低于这个值就等于没画出来过，直接收，不再空跑一段动画。
	private const double HiddenThreshold = 0.01;

	private readonly Popup _popup = new();
	private readonly AnimationClock _clock;
	// 淡入淡出的目标：原生弹层是 chrome（连落影一起淡），表面内宿主时是内容自己。
	private UIElement? _surface;
	private double _from;
	private double _to;
	private bool _closing;
	private bool _disposed;

	public FadePopup()
	{
		_popup.Closed += OnPopupClosed;
		_clock = new AnimationClock(TimeSpan.FromMilliseconds(FadeInMs), Easing.EaseOutQuint)
		{
			TickCallback = OnFadeTick,
			CompletedCallback = OnFadeCompleted,
		};
	}

	/// 淡出期间算已关闭：调用方靠它决定「再按一次是收起还是重开」，
	/// 收尾的 120ms 不该吃掉下一次打开。
	public bool IsOpen => _popup.IsOpen && !_closing;

	/// 关闭时是否释放内容。菜单每次打开都重建，关掉即释放；常驻面板（会话统计）复用同一棵树，
	/// 关闭只卸载，内容由调用方自己的 Dispose 释放。
	public bool ReleaseContentOnClose { get; init; } = true;

	public UIElement? Content => _popup.Content;

	/// 原生弹层真正拆掉后触发；窗口级生命周期关闭不保证还会走到这里。
	public event EventHandler<PopupClosedEventArgs>? Closed;

	public void Show(UIElement content, UIElement anchor, Rect anchorInWindow, PopupAnchorSide side)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);

		// 淡出还没走完就要重开：先收干净，旧内容不该再留在窗口上。
		CloseInstantly();

		content.Opacity = 0;
		// 复用内容（ReleaseContentOnClose = false）时，上一次淡出把命中关了，重开必须恢复。
		content.IsHitTestVisible = true;
		_popup.Content = content;
		_popup.ShowAt(anchor, anchorInWindow, side);
		if (!_popup.IsOpen)
		{
			content.Opacity = 1;
			return;
		}

		var surface = _popup.Parent as UIElement;
		if (surface is not null and not Window && !ReferenceEquals(surface, content))
		{
			// chrome 的 opacity 连落影一起淡入；内容先恢复，避免两套透明度相乘。
			content.Opacity = 1;
			StartFade(surface, 0, 1, FadeInMs, Easing.EaseOutQuint);
		}
		else
		{
			StartFade(content, 0, 1, FadeInMs, Easing.EaseOutQuint);
		}
	}

	public void Close()
	{
		if (_closing)
		{
			return;
		}

		var surface = _surface;
		if (!Application.IsRunning || !_popup.IsOpen || surface is null || SurfaceHidden(surface))
		{
			CloseNow();
			return;
		}

		_closing = true;
		// 淡出的弹层不再接输入：点中的那一项已经执行过一次，再点一次只会重放一遍。
		if (_popup.Content is { } content)
		{
			content.IsHitTestVisible = false;
		}

		StartFade(surface, surface.Opacity, 0, FadeOutMs, Easing.EaseOutCubic);
	}

	/// 立即关掉，不做淡出。同一个 Popup 马上要再开一次，或会话切走、浮层跟随标签页一起消失时用：
	/// 那时淡出只会把旧菜单多留在窗口上 120ms。
	public void CloseInstantly() => CloseNow();

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}

		_disposed = true;
		CloseNow();
		_popup.Closed -= OnPopupClosed;
		_popup.Dispose();
	}

	private void OnPopupClosed(object? sender, PopupClosedEventArgs e)
	{
		// 框架自己关掉的路径（外点、切窗、生命周期）不会先经过 Close：停下淡出，别让
		// 收尾回调再去关一个已经拆掉的弹层。
		_clock.Stop();
		_closing = false;
		_surface = null;
		ReleaseContent();
		Closed?.Invoke(this, e);
	}

	private void StartFade(UIElement surface, double from, double to, int durationMs, Func<double, double> easing)
	{
		_clock.Stop();
		_surface = surface;
		_from = from;
		_to = to;
		surface.Opacity = from;
		if (!Application.IsRunning)
		{
			surface.Opacity = to;
			return;
		}

		_clock.Duration = TimeSpan.FromMilliseconds(durationMs);
		_clock.EasingFunction = easing;
		_clock.Start();
	}

	private void OnFadeTick(double progress)
	{
		if (_surface is { } surface)
		{
			surface.Opacity = _from + ((_to - _from) * progress);
		}
	}

	private void OnFadeCompleted()
	{
		if (_surface is { } surface)
		{
			surface.Opacity = _to;
		}

		if (_closing)
		{
			CloseNow();
		}
	}

	/// 拆窗本身是同步的：内容由 <see cref="OnPopupClosed"/> 交还给生命周期管理，
	/// 不在这里释放，免得同一次关闭里放两遍。
	private void CloseNow()
	{
		_clock.Stop();
		_closing = false;
		_surface = null;
		if (_popup.IsOpen)
		{
			_popup.Close();
			// 正常关闭：内容由 Closed 回调交还，不在这里再放一遍。
			if (!_popup.IsOpen)
			{
				return;
			}
		}

		// 窗口已经不在时 Close 什么都不做，也就没人来释放内容。
		ReleaseContent();
	}

	// 已经看不见的表面上再跑一段淡出等于什么都不画，直接收。
	private static bool SurfaceHidden(UIElement surface) =>
		!surface.IsVisible || surface.Opacity <= HiddenThreshold;

	private void ReleaseContent()
	{
		if (_popup.Content is not { } content)
		{
			return;
		}

		_popup.Content = null;
		if (ReleaseContentOnClose)
		{
			UiLifetime.Release(content);
		}
	}
}
