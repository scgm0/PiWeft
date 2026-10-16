using System.Diagnostics;
using System.Runtime.CompilerServices;
using Aprillz.MewUI;
using Aprillz.MewUI.Animation;
using Aprillz.MewUI.Controls;

namespace PiWeft.Desktop;

/// <summary>Frame-driven scrolling with accumulated wheel targets and immediate interruption by direct input.</summary>
internal sealed class SmoothScroll : IDisposable
{
	/// <summary>
	/// 滚轮事件间隔低于该值视为连续输入流（macOS 触控板/Magic Mouse 及其惯性阶段以
	/// 60-125Hz 发送分数增量）。对连续流逐事件重启 180ms 缓动会不断打断速度曲线，
	/// 视觉上卡顿（macOS 上所有可滚动区域滚轮卡顿、拖滚动条正常的根因）；此时改为
	/// 直接落位，与拖滚动条同一条渲染路径。离散滚轮刻度（Windows 鼠标）保留缓动。
	/// </summary>
	internal const double ContinuousWheelIntervalMs = 50;

	private static readonly ConditionalWeakTable<FrameworkElement, SmoothScroll> Controllers = new();
	private readonly FrameworkElement _view;
	private readonly ScrollViewer? _viewport;
	private readonly MultiLineTextBox? _text;
	private readonly ScrollBar? _verticalBar;
	private readonly ScrollBar? _horizontalBar;
	private readonly AnimationClock _clock = new(TimeSpan.FromMilliseconds(180), EaseOut);
	private readonly Stopwatch _wheelClock = Stopwatch.StartNew();
	private double _lastWheelMs = double.NegativeInfinity;
	private Point _from;
	private Point _target;
	private Point _lastOffset;
	private bool _toEnd;
	private bool _disposed;

	private SmoothScroll(FrameworkElement view)
	{
		_view = view;
		_viewport = view as ScrollViewer;
		_text = view as MultiLineTextBox;
		// Public scrollbar metrics include virtualized extents and DPI rounding.
		ScrollBar? verticalBar = null, horizontalBar = null;
		((IVisualTreeHost)view).VisitChildren(child =>
		{
			if (child is ScrollBar bar)
			{
				if (bar.Orientation == Orientation.Vertical) verticalBar = bar;
				else horizontalBar = bar;
			}
			return true;
		});
		_verticalBar = verticalBar;
		_horizontalBar = horizontalBar;
		_lastOffset = Offset;
		_clock.TickCallback = Advance;
		view.MouseWheel += RouteWheel;
		view.MouseDown += OnMouseDown;
		view.KeyDown += OnKeyDown;
		if (_viewport is not null) _viewport.ScrollChanged += OnScrollChanged;
		if (_text is not null) _text.ScrollOffsetChanged += OnTextScrollChanged;
	}

	public static SmoothScroll For(ScrollViewer view) => Controllers.GetValue(view, static view => new SmoothScroll(view));
	public static SmoothScroll For(MultiLineTextBox view) => Controllers.GetValue(view, static view => new SmoothScroll(view));
	internal static void Release(Element element)
	{
		if (element is FrameworkElement control && Controllers.TryGetValue(control, out var scroll)) scroll.Dispose();
	}

	public bool IsAnimating => _clock.IsRunning;
	public bool IsApplyingOffset { get; private set; }
	public double MaxVerticalOffset => Maximum(vertical: true);
	public double TargetVerticalOffset => IsAnimating ? (_toEnd ? MaxVerticalOffset : _target.Y) : Offset.Y;
	public event Action<double>? VerticalInput;
	private Point Offset => _text?.ScrollOffset ?? new Point(_viewport!.HorizontalOffset, _viewport.VerticalOffset);
	private bool CanScrollHorizontally => _text is not null ? !_text.Wrap : _viewport!.HorizontalScroll != ScrollMode.Disabled;

	// Virtualized content reports its logical extent independently of its viewport-sized DesiredSize.
	private double Maximum(bool vertical)
	{
		if (_text is not null)
		{
			if (!vertical && _text.Wrap) return 0;
			var bounds = _text.TextViewportBounds;
			return Math.Max(0, vertical ? _text.ExtentHeight - bounds.Height : _text.ExtentWidth - bounds.Width);
		}
		if ((vertical ? _viewport!.VerticalScroll : _viewport!.HorizontalScroll) == ScrollMode.Disabled) return 0;
		var bar = vertical ? _verticalBar : _horizontalBar;
		if (bar is { IsVisible: true }) return Math.Max(0, bar.Maximum);
		var extent = _viewport.Content?.DesiredSize ?? Size.Empty;
		return Math.Max(0, (vertical ? extent.Height : extent.Width) -
			(vertical ? _viewport.ViewportHeight : _viewport.ViewportWidth));
	}

	internal void RouteWheel(MouseWheelEventArgs args)
	{
		if (args.Handled) return;
		var horizontal = args.Delta.X;
		var vertical = args.Delta.Y;
		if (args.ShiftKey && horizontal == 0 && CanScrollHorizontally)
		{
			horizontal = vertical;
			vertical = 0;
		}
		var current = IsAnimating ? _target : Offset;
		var canX = horizontal != 0 && (horizontal > 0 ? current.X > 0 : current.X < Maximum(false));
		var canY = vertical != 0 && (vertical > 0 ? current.Y > 0 : current.Y < MaxVerticalOffset);
		if (!canX && !canY) return; // Let an enclosing viewport take over at the edge.
		HoverHints.Cancel(_view);
		ScrollBy(canX ? -horizontal : 0, canY ? -vertical : 0);
		args.Handled = true;
	}

	/// <summary>Wheel deltas are notch counts, including fractional trackpad movement.</summary>
	public void ScrollBy(double horizontal, double vertical)
	{
		if (_disposed || !double.IsFinite(horizontal) || !double.IsFinite(vertical)) return;
		var step = Application.IsRunning ? Application.Current.Theme.Metrics.ScrollWheelStep : ThemeManager.DefaultMetrics.ScrollWheelStep;
		// macOS 对所有滚动设备（含普通滚轮鼠标）做系统级平滑与惯性合成：物理刻度之后
		// 还会补发一串高频分数增量（Windows 的普通鼠标则一个刻度一个事件）。事件流连续
		// 时逐次重启缓动会打断速度曲线，视觉上卡顿；此时直接落位，与拖滚动条同一路径。
		var nowMs = _wheelClock.Elapsed.TotalMilliseconds;
		var continuous = nowMs - _lastWheelMs < ContinuousWheelIntervalMs;
		_lastWheelMs = nowMs;

		var current = Offset;
		var pending = IsAnimating ? new Point(_target.X, _toEnd ? MaxVerticalOffset : _target.Y) : current;
		// Reversing the wheel should respond now instead of consuming a backlog in the previous direction.
		var x = horizontal * (pending.X - current.X) < 0 ? current.X : pending.X;
		var y = vertical * (pending.Y - current.Y) < 0 ? current.Y : pending.Y;
		var target = new Point(Math.Clamp(x + horizontal * step, 0, Maximum(false)),
			Math.Clamp(y + vertical * step, 0, MaxVerticalOffset));

		if (continuous)
		{
			Stop();
			Apply(target);
		}
		else
		{
			Start(target, toEnd: false, milliseconds: 180);
		}

		if (vertical != 0) VerticalInput?.Invoke(vertical);
	}

	public void ScrollToEnd() => Start(new Point(Offset.X, MaxVerticalOffset), toEnd: true, milliseconds: 280);

	public void JumpToEnd()
	{
		Stop();
		Apply(new Point(Offset.X, MaxVerticalOffset));
	}

	private void Start(Point target, bool toEnd, double milliseconds)
	{
		if (_disposed) return;
		_from = Offset;
		_target = target;
		_toEnd = toEnd;
		if (Math.Abs(target.X - _from.X) < 0.5 && Math.Abs(target.Y - _from.Y) < 0.5)
		{
			Stop();
			Apply(target);
			return;
		}
		_clock.Duration = TimeSpan.FromMilliseconds(milliseconds);
		_clock.Start();
	}

	private void Advance(double progress)
	{
		// A hidden tab/popup must release its animation demand rather than keep all windows rendering.
		for (Element? element = _view; element is not null; element = element.Parent)
		{
			if (element is UIElement { IsVisible: false }) { Stop(); return; }
		}
		var targetY = _toEnd ? MaxVerticalOffset : Math.Min(_target.Y, MaxVerticalOffset);
		Apply(new Point(_from.X + (Math.Min(_target.X, Maximum(false)) - _from.X) * progress,
			_from.Y + (targetY - _from.Y) * progress));
	}

	private void Apply(Point offset)
	{
		IsApplyingOffset = true;
		try
		{
			if (_viewport is not null) _viewport.SetScrollOffsets(offset.X, offset.Y);
			else
			{
				if (_horizontalBar is not null) _horizontalBar.Value = offset.X;
				if (_verticalBar is not null) _verticalBar.Value = offset.Y;
			}
		}
		finally { IsApplyingOffset = false; _lastOffset = Offset; }
	}

	private void OnScrollChanged()
	{
		// Scrollbar dragging, focus navigation and explicit offsets take ownership from an active animation.
		if (!IsApplyingOffset && Offset != _lastOffset) Stop();
		_lastOffset = Offset;
	}

	private void OnTextScrollChanged(Aprillz.MewUI.Text.ITextViewHost _) => OnScrollChanged();
	private void OnMouseDown(MouseEventArgs args) => Stop();
	private void OnKeyDown(KeyEventArgs args) => Stop();
	public void Stop() { _clock.Stop(); _toEnd = false; }
	private static double EaseOut(double progress) => 1 - Math.Pow(1 - progress, 3);

	public void Dispose()
	{
		if (_disposed) return;
		_disposed = true;
		Stop();
		_clock.TickCallback = null;
		_view.MouseWheel -= RouteWheel;
		_view.MouseDown -= OnMouseDown;
		_view.KeyDown -= OnKeyDown;
		if (_viewport is not null) _viewport.ScrollChanged -= OnScrollChanged;
		if (_text is not null) _text.ScrollOffsetChanged -= OnTextScrollChanged;
		Controllers.Remove(_view);
	}
}

/// <summary>Installs one shared wheel controller per viewport.</summary>
internal static class SmoothScrollExtensions
{
	public static T SmoothScrolling<T>(this T view) where T : FrameworkElement
	{
		Attach(view);
		return view;
	}

	/// <summary>Attach at construction boundaries, including framework-owned tree and code viewports.</summary>
	private static void Attach(Element element)
	{
		if (element is ScrollViewer scroll) SmoothScroll.For(scroll);
		else if (element is MultiLineTextBox text) SmoothScroll.For(text);
		if (element is IVisualTreeHost tree)
			tree.VisitChildren(child => { Attach(child); return true; });
	}
}
