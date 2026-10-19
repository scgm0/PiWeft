using System.Diagnostics;
using System.Runtime.CompilerServices;
using Aprillz.MewUI;
using Aprillz.MewUI.Animation;
using Aprillz.MewUI.Controls;

namespace PiWeft.Desktop;

/// <summary>Frame-driven scrolling with exponential decay physics, natively supporting both high-frequency trackpads and discrete mouse wheels.</summary>
internal sealed class SmoothScroll : IDisposable
{
	private static readonly ConditionalWeakTable<FrameworkElement, SmoothScroll> Controllers = new();
	private readonly FrameworkElement _view;
	private readonly ScrollViewer? _viewport;
	private readonly MultiLineTextBox? _text;
	private readonly ScrollBar? _verticalBar;
	private readonly ScrollBar? _horizontalBar;

	// 使用极长的持续时间作为纯帧循环触发器，目标到达时主动终止以释放资源
	private readonly AnimationClock _clock = new(TimeSpan.FromDays(1), LinearEase);
	private readonly Stopwatch _renderClock = new();

	private Point _target;
	private Point _lastOffset;
	private bool _toEnd;
	private bool _disposed;

	// 阻尼系数：控制追赶 _target 的敏捷度（约 12-18，值越大越敏捷，15 接近 Chromium/iOS 体感）
	private const double DecayRate = 15.0;

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

		// Virtualized content can grow between arrangements. Pull its current extent into the
		// viewport before clamping the target, or a stale bar maximum caps the wheel short of the
		// real end (the reader then cannot reach newly appended rows).
		SyncMetrics();
		var current = Offset;
		var pending = IsAnimating ? new Point(_target.X, _toEnd ? MaxVerticalOffset : _target.Y) : current;

		// Reversing the wheel should respond now instead of consuming a backlog in the previous direction.
		var x = horizontal * (pending.X - current.X) < 0 ? current.X : pending.X;
		var y = vertical * (pending.Y - current.Y) < 0 ? current.Y : pending.Y;

		var target = new Point(Math.Clamp(x + horizontal * step, 0, Maximum(false)),
			Math.Clamp(y + vertical * step, 0, MaxVerticalOffset));

		Start(target, toEnd: false);

		if (vertical != 0) VerticalInput?.Invoke(vertical);
	}

	public void ScrollToEnd()
	{
		SyncMetrics();
		Start(new Point(Offset.X, MaxVerticalOffset), toEnd: true);
	}

	public void JumpToEnd()
	{
		Stop();
		SyncMetrics();
		Apply(new Point(Offset.X, MaxVerticalOffset));
	}

	/// <summary>
	/// Keeps an already running animation pointed at the end. A wheel animation started a notch
	/// short of the end would otherwise settle there while the reply keeps growing, with no further
	/// scroll notification left to resume following.
	/// </summary>
	public void FollowEnd()
	{
		if (IsAnimating) ScrollToEnd();
		else JumpToEnd();
	}

	/// <summary>
	/// Refreshes the viewport's extent metrics from its scroll content. <see cref="ScrollViewer"/>
	/// only republishes a virtualized content extent during measure; a row that grew after that
	/// leaves the bar maximum stale until the next offset write.
	/// </summary>
	private void SyncMetrics()
	{
		if (_viewport is null) return;
		IsApplyingOffset = true;
		try { _viewport.SetScrollOffsets(_viewport.HorizontalOffset, _viewport.VerticalOffset); }
		finally { IsApplyingOffset = false; _lastOffset = Offset; }
	}

	private void Start(Point target, bool toEnd)
	{
		if (_disposed) return;
		_target = target;
		_toEnd = toEnd;

		var current = Offset;
		if (Math.Abs(target.X - current.X) < 0.5 && Math.Abs(target.Y - current.Y) < 0.5)
		{
			Stop();
			Apply(target);
			return;
		}

		if (!_clock.IsRunning)
		{
			_renderClock.Restart();
			_clock.Start();
		}
	}

	private void Advance(double progress)
	{
		// A hidden tab/popup must release its animation demand rather than keep all windows rendering.
		for (Element? element = _view; element is not null; element = element.Parent)
		{
			if (element is UIElement { IsVisible: false }) { Stop(); return; }
		}

		// 根据自上一帧流逝的真实时间计算指数衰减系数
		double dt = _renderClock.Elapsed.TotalSeconds;
		_renderClock.Restart();

		// 防御性拦截：如果主线程卡顿引起 dt 过大，限制单帧最大平滑跳跃步长以防抖
		if (dt > 0.05) dt = 0.05;

		var current = Offset;
		var targetX = Math.Min(_target.X, Maximum(false));
		var targetY = _toEnd ? MaxVerticalOffset : Math.Min(_target.Y, MaxVerticalOffset);

		// 指数平滑方程 (Exponential Smoothing)：完美衔接低频的大段离散滚轮与高频细小的触控板滚轮
		double factor = 1 - Math.Exp(-DecayRate * dt);
		double nextX = current.X + (targetX - current.X) * factor;
		double nextY = current.Y + (targetY - current.Y) * factor;

		// 检查衰减极限，到达阈值直接落位
		if (Math.Abs(targetX - nextX) < 0.5 && Math.Abs(targetY - nextY) < 0.5)
		{
			Apply(new Point(targetX, targetY));
			Stop();
			return;
		}

		Apply(new Point(nextX, nextY));
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

	// 对于以时间为基准的物理循环，内置 Easing 提供恒等映射即可
	private static double LinearEase(double progress) => progress;

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
