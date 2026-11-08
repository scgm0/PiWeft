using Aprillz.MewUI;
using Aprillz.MewUI.Animation;
using Aprillz.MewUI.Controls;
using Aprillz.MewUI.Rendering;

namespace PiWeft.Desktop;

/// 原生 <see cref="Popup"/> 的进出场动效：进入 200ms ease-out-quint，退出 120ms ease-out-cubic。
/// 退出用 ease-out、时长也比 PiDeck 的 200ms 短：ease-in 开头几乎不动，关弹层像先卡一下。
/// 打开时直接淡入弹层的 chrome（连同一圈落影）；关闭由框架同步拆窗口，来不及淡出，
/// 所以把内容重挂到窗口 OverlayLayer 上做一段幽灵淡出，再释放内容。
internal sealed class FadePopup : IDisposable
{
	private const int FadeInMs = 200;
	private const int FadeOutMs = 120;

	// 幽灵起点低于这个值就等于没画出来过，直接释放内容，不再空跑一段动画。
	private const double GhostMinOpacity = 0.01;

	private readonly Popup _popup = new();
	private readonly AnimationClock _fadeClock;
	private readonly AnimationClock _ghostClock;
	private Window? _window;
	private Window? _ghostWindow;
	private Panel? _ghostHost;
	private UIElement? _fadeSurface;
	private double _fadeFrom;
	private double _fadeTo;
	private double _ghostFrom;
	private double _surfaceOpacity = 1;
	private bool _disposed;
	private bool _suppressGhost;

	public FadePopup()
	{
		_popup.Closed += OnPopupClosed;
		_fadeClock = new AnimationClock(TimeSpan.FromMilliseconds(FadeInMs), Easing.EaseOutQuint)
		{
			TickCallback = OnFadeTick,
			CompletedCallback = OnFadeCompleted,
		};
		_ghostClock = new AnimationClock(TimeSpan.FromMilliseconds(FadeOutMs), Easing.EaseOutCubic)
		{
			TickCallback = OnGhostTick,
			CompletedCallback = FinishGhost,
		};
	}

	public bool IsOpen => _popup.IsOpen;

	public UIElement? Content => _popup.Content;

	/// 原生弹层真正拆掉后触发；窗口级生命周期关闭不保证还会走到这里。
	public event EventHandler<PopupClosedEventArgs>? Closed;

	public void Show(UIElement content, UIElement anchor, Rect anchorInWindow, PopupAnchorSide side)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);

		FinishGhost();
		if (_popup.IsOpen)
		{
			// 重新打开同一个弹层：旧内容不该再留下一层幽灵。
			_suppressGhost = true;
			_popup.Close();
			_suppressGhost = false;
		}

		ReleaseContent();
		StopFade();
		_window = anchor.FindVisualRoot() as Window;
		content.Opacity = 0;
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
			StartFade(surface);
		}
		else
		{
			StartFade(content);
		}
	}

	public void Close()
	{
		if (_popup.IsOpen)
		{
			_popup.Close();
			return;
		}

		FinishGhost();
		ReleaseContent();
	}

	/// 立即关掉，不做淡出。同一个 Popup 马上要再开一次，或会话切走、浮层跟随标签页一起消失时用：
	/// 那时淡出只会把旧菜单多留在窗口上 120ms，还白白往 OverlayLayer 挂一层幽灵。
	public void CloseInstantly()
	{
		if (_popup.IsOpen)
		{
			_suppressGhost = true;
			_popup.Close();
			_suppressGhost = false;
			return;
		}

		FinishGhost();
		ReleaseContent();
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}

		_disposed = true;
		FinishGhost();
		StopFade();
		_suppressGhost = true;
		_popup.Close();
		_suppressGhost = false;
		ReleaseContent();
		_popup.Closed -= OnPopupClosed;
		_popup.Dispose();
	}

	private void OnPopupClosed(object? sender, PopupClosedEventArgs e)
	{
		StopFade();
		var content = _popup.Content;
		// 先取边界：内容一旦摘下父级，后续布局不再保证它停在原位。
		var bounds = content?.Bounds ?? default;
		var opacity = _surfaceOpacity;
		var window = _window;
		_popup.Content = null;
		_window = null;

		if (content is { } released)
		{
			if (_suppressGhost || e.Kind == PopupCloseKind.Lifecycle || !Application.IsRunning ||
				window is null || !window.IsVisible)
			{
				UiLifetime.Release(released);
			}
			else
			{
				ShowGhost(window, released, bounds, opacity);
			}
		}

		Closed?.Invoke(this, e);
	}

	private void ShowGhost(Window window, UIElement content, Rect bounds, double opacity)
	{
		FinishGhost();
		if (bounds.Width <= 0 || bounds.Height <= 0 || opacity <= GhostMinOpacity)
		{
			UiLifetime.Release(content);
			return;
		}

		var host = new GhostLayer(bounds, content) { Opacity = opacity };
		_ghostHost = host;
		_ghostWindow = window;
		_ghostFrom = opacity;
		window.OverlayLayer.Add(host);
		_ghostClock.Start();
	}

	private void OnGhostTick(double progress)
	{
		if (_ghostHost is not { } host)
		{
			_ghostClock.Stop();
			return;
		}

		// 浮层被提前摘掉（窗口关闭、OverlayLayer 释放）就不用再画，直接收尾。
		if (host.Parent is null)
		{
			FinishGhost();
			return;
		}

		host.Opacity = _ghostFrom * (1 - progress);
	}

	private void FinishGhost()
	{
		_ghostClock.Stop();
		_ghostFrom = 0;
		if (_ghostHost is not { } host)
		{
			return;
		}

		_ghostHost = null;
		var window = _ghostWindow;
		_ghostWindow = null;
		window?.OverlayLayer.Remove(host);
		UiLifetime.Release(host);
	}

	private void StartFade(UIElement surface)
	{
		StopFade();
		_fadeSurface = surface;
		_fadeFrom = 0;
		_fadeTo = 1;
		_surfaceOpacity = 0;
		surface.Opacity = 0;
		if (!Application.IsRunning)
		{
			surface.Opacity = 1;
			_surfaceOpacity = 1;
			_fadeSurface = null;
			return;
		}

		_fadeClock.Start();
	}

	private void OnFadeTick(double progress)
	{
		var value = _fadeFrom + ((_fadeTo - _fadeFrom) * progress);
		_surfaceOpacity = value;
		if (_fadeSurface is { } surface)
		{
			surface.Opacity = value;
		}
	}

	private void OnFadeCompleted()
	{
		_surfaceOpacity = _fadeTo;
		if (_fadeSurface is { } surface)
		{
			surface.Opacity = _fadeTo;
			_fadeSurface = null;
		}
	}

	private void StopFade()
	{
		_fadeClock.Stop();
		_fadeSurface = null;
	}

	private void ReleaseContent()
	{
		if (_popup.Content is not { } content)
		{
			return;
		}

		_popup.Content = null;
		UiLifetime.Release(content);
	}

	/// 幽灵只负责画面：铺满客户区、按原内容边界摆位，命中一律穿透。
	/// 落影的几何与 alpha 照抄 MewUI 的 PopupChrome；换成 ShadowDecorator 时圆角与底部范围不同，
	/// 关闭瞬间会看到阴影变重一下。
	private sealed class GhostLayer : Panel
	{
		private const double ShadowBlurRadius = 8;
		private const double ShadowOffsetY = 4;

		private readonly Rect _bounds;
		private readonly UIElement _child;

		public GhostLayer(Rect bounds, UIElement child)
		{
			_bounds = bounds;
			_child = child;
			Add(child);
		}

		protected override Size MeasureContent(Size availableSize)
		{
			_child.Measure(new Size(_bounds.Width, _bounds.Height));
			return Size.Empty;
		}

		protected override void ArrangeContent(Rect arranged) => _child.Arrange(_bounds);

		protected override void OnRender(IGraphicsContext context)
		{
			var cb = _child.Bounds;
			if (cb.Width <= 0 || cb.Height <= 0) return;

			double dpiScale = GetDpi() / 96.0;
			double radius = LayoutRounding.RoundToPixel(Theme.Metrics.ControlCornerRadius, dpiScale);
			byte alpha = (byte)(Theme.IsDark ? 128 : 64);
			var shadowBounds = new Rect(cb.X, cb.Y + ShadowOffsetY, cb.Width, cb.Height - ShadowOffsetY);
			context.DrawBoxShadow(shadowBounds, radius, ShadowBlurRadius, Color.FromArgb(alpha, 0, 0, 0));
		}

		protected override UIElement? OnHitTest(Point point) => null;
	}
}
