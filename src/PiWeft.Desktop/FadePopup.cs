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

	private readonly Popup _popup = new();
	private Window? _window;
	private AnimationClock? _clock;
	private Panel? _ghostHost;
	private Window? _ghostWindow;
	private AnimationClock? _ghostClock;
	private double _surfaceOpacity = 1;
	private bool _disposing;
	private bool _suppressGhost;

	public FadePopup() => _popup.Closed += OnPopupClosed;

	public bool IsOpen => _popup.IsOpen;

	public UIElement? Content => _popup.Content;

	/// 原生弹层真正拆掉后触发；窗口级生命周期关闭不保证还会走到这里。
	public event EventHandler<PopupClosedEventArgs>? Closed;

	public void Show(UIElement content, UIElement anchor, Rect anchorInWindow, PopupAnchorSide side)
	{
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
		content.IsHitTestVisible = true;
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
			StartFade(surface, 0, 1, Easing.EaseOutQuint, null);
		}
		else
		{
			StartFade(content, 0, 1, Easing.EaseOutQuint, null);
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

	public void Dispose()
	{
		_disposing = true;
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
		var bounds = content?.Bounds ?? default;
		var opacity = _surfaceOpacity;
		var window = _window;
		_popup.Content = null;
		_window = null;

		if (content is { } released)
		{
			if (_disposing || _suppressGhost || e.Kind == PopupCloseKind.Lifecycle || !Application.IsRunning ||
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
		if (bounds.Width <= 0 || bounds.Height <= 0 || opacity <= 0.01)
		{
			UiLifetime.Release(content);
			return;
		}

		var host = new GhostLayer(bounds, content) { Opacity = opacity };
		_ghostHost = host;
		_ghostWindow = window;
		window.OverlayLayer.Add(host);

		if (!Application.IsRunning)
		{
			FinishGhost();
			return;
		}

		var clock = new AnimationClock(TimeSpan.FromMilliseconds(FadeOutMs), Easing.EaseOutCubic);
		clock.TickCallback = progress =>
		{
			if (host.Parent is null)
			{
				FinishGhost();
				return;
			}

			host.Opacity = opacity * (1 - progress);
		};
		clock.CompletedCallback = FinishGhost;
		_ghostClock = clock;
		clock.Start();
	}

	private void FinishGhost()
	{
		if (_ghostClock is { } clock)
		{
			clock.TickCallback = null;
			clock.CompletedCallback = null;
			clock.Stop();
			_ghostClock = null;
		}

		if (_ghostHost is { } host)
		{
			_ghostWindow?.OverlayLayer.Remove(host);
			_ghostHost = null;
			_ghostWindow = null;
			UiLifetime.Release(host);
		}
	}

	private void StartFade(UIElement surface, double from, double to, Func<double, double> easing, Action? completed)
	{
		StopFade();
		_surfaceOpacity = from;
		surface.Opacity = from;
		if (!Application.IsRunning)
		{
			surface.Opacity = to;
			_surfaceOpacity = to;
			completed?.Invoke();
			return;
		}

		var clock = new AnimationClock(TimeSpan.FromMilliseconds(FadeInMs), easing);
		clock.TickCallback = progress =>
		{
			var value = from + ((to - from) * progress);
			surface.Opacity = value;
			_surfaceOpacity = value;
		};
		clock.CompletedCallback = () =>
		{
			surface.Opacity = to;
			_surfaceOpacity = to;
			_clock = null;
			completed?.Invoke();
		};
		_clock = clock;
		clock.Start();
	}

	private void StopFade()
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
