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
	private double _lastMaximum;
	private bool _toEnd;
	private bool _disposed;
	// 展开正在流式输出的内容（例如思考）后持续贴住内容末尾；读者自己滚动或显式结束即解除。
	private bool _followGrowth;
	private bool _growthFollowQueued;
	// 置底自动跟随：组体这类嵌套视口只要滚动条停在底部就跟随最新内容，
	// 由 ExecutionProcessView 显式启用；外层会话仍用 ConversationScroll 的状态机。
	private const double AutoFollowTolerance = 4;
	internal bool AutoFollowAtEnd { get; set; }
	// 读者明确上滚过（滚轮/键盘/滚动条/恢复历史位置）。只有这种意图能挡住「置底即跟底」；
	// 布局测高、presenter 切换、展开折叠引起的位移都不算，否则跟底会被静默关掉且再不恢复。
	private bool _readerEscaped;

	/// <summary>恢复历史状态后的首个组体：保持阅读位置，等读者滚到底或显式展开再重新锁底。</summary>
	internal void SuppressAutoFollow()
	{
		_readerEscaped = true;
		_followGrowth = false;
		Stop();
	}

	// 跟底（内容增长）用指数衰减：目标每帧都在变，衰减不会因为重排而反复重启。
	private const double DecayRate = 18.0;
	private const double MaxGlideMilliseconds = 260;
	private double _glideMilliseconds;

	// 输入驱动的滚动复刻 Chromium 的 ScrollOffsetAnimationCurve：固定起步时长 + 距离项，
	// 配 CSS ease（cubic-bezier(0.25, 0.1, 0.25, 1)）——正是 PiDeck（Electron/Chromium）
	// 滚轮的手感来源：一次滚动在 ~300ms 内到位，没有指数衰减的长尾。
	private const double BaseDurationSeconds = 0.15;
	private const double DurationPerDip = 0.0012;
	private const double MinDurationSeconds = 0.10;
	private const double MaxDurationSeconds = 0.30;
	// CSS ease 的两个控制点横坐标（纵坐标见 EaseY1/EaseY2）。
	private const double EaseX1 = 0.25;
	private const double EaseX2 = 0.25;
	private const double EaseY1 = 0.1;
	private const double EaseY2 = 1.0;
	private Point _from;
	private double _durationSeconds;
	private double _elapsedSeconds;

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
		_lastMaximum = MaxVerticalOffset;
		_clock.TickCallback = Advance;
		view.MouseWheel += RouteWheel;
		view.MouseDown += OnMouseDown;
		view.KeyDown += OnKeyDown;
		if (_viewport is not null) _viewport.ScrollChanged += OnScrollChanged;
		if (_text is not null) _text.ScrollOffsetChanged += OnTextScrollChanged;
		if (_verticalBar is not null) _verticalBar.MouseDown += OnScrollBarDown;
	}

	public static SmoothScroll For(ScrollViewer view) => Controllers.GetValue(view, static view => new SmoothScroll(view));
	public static SmoothScroll For(MultiLineTextBox view) => Controllers.GetValue(view, static view => new SmoothScroll(view));
	/// <summary>已安装的控制器；不新建，供滚轮沿祖先链交接时判断谁接管。</summary>
	internal static SmoothScroll? Find(FrameworkElement view) =>
		Controllers.TryGetValue(view, out var scroll) ? scroll : null;
	internal static void Release(Element element)
	{
		if (element is FrameworkElement control && Controllers.TryGetValue(control, out var scroll)) scroll.Dispose();
	}

	public bool IsAnimating => _clock.IsRunning;
	public bool IsApplyingOffset { get; private set; }
	/// <summary>是否正在跟随内容增长（展开流式内容后保持贴底）。</summary>
	public bool IsFollowingGrowth => _followGrowth;
	public double MaxVerticalOffset => Maximum(vertical: true);
	public double TargetVerticalOffset => IsAnimating ? (_toEnd ? MaxVerticalOffset : _target.Y) : Offset.Y;
	/// <summary>Wheel/keyboard scroll input in DIPs (positive = toward the end).</summary>
	public event Action<double>? VerticalInput;
	/// <summary>The scrollbar owned by this viewport, if any; its pointer gestures are reader intent.</summary>
	internal ScrollBar? VerticalBar => _verticalBar;
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
		// 优先用滚动条的范围：虚拟化内容（如过程组里的步骤列表）把 DesiredSize 报成可用高度，
		// 拿它算余量会把「内容装得下」误判成还能滚。隐藏的滚动条范围保持 0，正是想要的答案。
		var bar = vertical ? _verticalBar : _horizontalBar;
		if (bar is not null) return bar.IsVisible ? Math.Max(0, bar.Maximum) : 0;
		var extent = _viewport.Content?.DesiredSize ?? Size.Empty;
		return Math.Max(0, (vertical ? extent.Height : extent.Width) -
			(vertical ? _viewport.ViewportHeight : _viewport.ViewportWidth));
	}

	/// <summary>
	/// 按滚轮方向返回本视口还能走的偏移增量；已经到边界（或内容装得下）的方向为零。
	/// </summary>
	internal Point WheelDelta(double horizontal, double vertical)
	{
		var current = IsAnimating ? _target : Offset;
		var x = horizontal != 0 && (horizontal > 0 ? current.X > 0.5 : current.X < Maximum(vertical: false) - 0.5)
			? -horizontal : 0;
		var y = vertical != 0 && (vertical > 0 ? current.Y > 0.5 : current.Y < MaxVerticalOffset - 0.5)
			? -vertical : 0;
		return new Point(x, y);
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
		var delta = WheelDelta(horizontal, vertical);
		if (delta.X == 0 && delta.Y == 0)
		{
			// 到边界：把这一格交给外层视口（组体 → 会话）。不能只是不处理——ScrollViewer 自带的
			// OnMouseWheel 会在原地把未处理的事件标记为已处理，外层再也接不到。
			if (ScrollCascade.Route(_view, horizontal, vertical)) args.Handled = true;
			return;
		}
		HoverHints.Cancel(_view);
		ScrollBy(delta.X, delta.Y);
		args.Handled = true;
	}

	// 低于这个刻度视为触控板/高精度输入：浏览器对它不做动画，直接 1:1 落位。
	private const double TrackpadNotchThreshold = 0.5;

	/// <summary>
	/// Wheel deltas are notch counts, including fractional trackpad movement.
	/// </summary>
	public void ScrollBy(double horizontal, double vertical)
	{
		var step = Application.IsRunning ? Application.Current.Theme.Metrics.ScrollWheelStep : ThemeManager.DefaultMetrics.ScrollWheelStep;
		ScrollByPixels(horizontal * step, vertical * step);
	}

	/// <summary>Scrolls by DIPs; used by the wheel after converting notches and by keyboard scrolling.</summary>
	private void ScrollByPixels(double horizontal, double vertical)
	{
		if (_disposed || !double.IsFinite(horizontal) || !double.IsFinite(vertical)) return;
		if (horizontal == 0 && vertical == 0) return;
		// 读者滚动会接管滚动位置：立即结束对内容增长的跟随，下一帧不再把他拉回末尾。
		// 只有向上才是「离开末尾」的意图；向下滚回底部由几何判定重新锁底。
		_followGrowth = false;
		if (vertical < 0) _readerEscaped = true;
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

		var target = new Point(Math.Clamp(x + horizontal, 0, Maximum(false)),
			Math.Clamp(y + vertical, 0, MaxVerticalOffset));

		// 触控板：增量本身就是高频连续的，浏览器（Chromium）对它 1:1 直接滚动、不做补间动画；
		// 给每一小步都套 100ms+ 的缓动只会让手指和画面之间多出一段黏滞感。
		if (Math.Max(Math.Abs(horizontal), Math.Abs(vertical)) < TrackpadNotchThreshold * step)
		{
			Stop();
			Apply(new Point(Math.Clamp(current.X + horizontal, 0, Maximum(false)),
				Math.Clamp(current.Y + vertical, 0, MaxVerticalOffset)));
		}
		else
		{
			Start(target, toEnd: false);
		}

		if (vertical != 0) VerticalInput?.Invoke(vertical);
	}

	public void ScrollToEnd()
	{
		_readerEscaped = false;
		SyncMetrics();
		Start(new Point(Offset.X, MaxVerticalOffset), toEnd: true);
	}

	public void JumpToEnd()
	{
		_readerEscaped = false;
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
	/// 展开正在流式输出的内容后，让视口持续贴住内容末尾：立即露出当前末尾，之后每次内容变高
	/// （思考的 Markdown 解析、行高变化）都在布局落定后再落位。与 <see cref="FollowEnd"/> 不同，
	/// 它不依赖动画，所以长内容每帧增长时也能跟上。
	/// </summary>
	public void FollowGrowth()
	{
		if (_disposed) return;
		// 显式跟随（自动展开最新组、展开流式思考）优先于恢复态的抑制。
		_readerEscaped = false;
		// 立即贴底，不能被上一次滚轮动画拉回旧目标。
		Stop();
		_followGrowth = true;
		SyncMetrics();
		Apply(new Point(Offset.X, MaxVerticalOffset));
	}

	/// <summary>停止跟随内容增长，视口停在当前位置（折叠流式内容或读者接管时）。</summary>
	public void StopFollowingGrowth() => _followGrowth = false;

	/// <summary>
	/// Republishes the virtualized extent to the scroll owner. Rows measured during arrange update
	/// the presenter's extent after the owner already published its metrics, so the bar maximum
	/// (and therefore the reachable end) would otherwise stay at the pre-growth value.
	/// </summary>
	internal void SyncExtent() => SyncMetrics();

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
		finally
		{
			IsApplyingOffset = false;
			_lastOffset = Offset;
			_lastMaximum = MaxVerticalOffset;
		}
	}

	private void Start(Point target, bool toEnd)
	{
		if (_disposed) return;
		_target = target;
		_toEnd = toEnd;
		_glideMilliseconds = 0;

		var current = Offset;
		if (Math.Abs(target.X - current.X) < 0.5 && Math.Abs(target.Y - current.Y) < 0.5)
		{
			Stop();
			Apply(target);
			return;
		}

		// 从当前位置重新计时（连续滚轮 = 一次新的曲线，而不是叠加的长尾）。
		_from = current;
		_elapsedSeconds = 0;
		_durationSeconds = Math.Clamp(
			BaseDurationSeconds + Math.Max(Math.Abs(target.X - current.X), Math.Abs(target.Y - current.Y)) * DurationPerDip,
			MinDurationSeconds, MaxDurationSeconds);

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

		if (!_toEnd)
		{
			// 输入驱动：按时间走 ease-out 曲线，到点精确落位（Chromium 的滚动曲线）。
			_elapsedSeconds += dt;
			double elapsed01 = _durationSeconds <= 0 ? 1 : Math.Clamp(_elapsedSeconds / _durationSeconds, 0, 1);
			double eased = ChromiumEase(elapsed01);
			var next = new Point(
				_from.X + (targetX - _from.X) * eased,
				_from.Y + (targetY - _from.Y) * eased);
			if (elapsed01 >= 1)
			{
				Apply(new Point(targetX, targetY));
				Stop();
				return;
			}
			Apply(next);
			return;
		}

		// 指数平滑方程 (Exponential Smoothing)：完美衔接低频的大段离散滚轮与高频细小的触控板滚轮
		double factor = 1 - Math.Exp(-DecayRate * dt);
		double nextX = current.X + (targetX - current.X) * factor;
		double nextY = current.Y + (targetY - current.Y) * factor;

		_glideMilliseconds += dt * 1000;

		// 检查衰减极限，到达阈值或超过滑行上限时直接落位
		if ((Math.Abs(targetX - nextX) < 0.5 && Math.Abs(targetY - nextY) < 0.5) || _glideMilliseconds >= MaxGlideMilliseconds)
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
		finally
		{
			IsApplyingOffset = false;
			_lastOffset = Offset;
			_lastMaximum = MaxVerticalOffset;
		}
	}

	private void OnScrollChanged()
	{
		if (_toEnd)
		{
			// 跟底动画期间，布局可能把偏移夹回当前末端（行还没测完）；那不是读者接管滚动，
			// 动画要继续重读末端并追下去。
			_lastOffset = Offset;
			_lastMaximum = MaxVerticalOffset;
			return;
		}
		var offset = Offset;
		var maximum = MaxVerticalOffset;
		var extentChanged = Math.Abs(maximum - _lastMaximum) > 0.5;
		if (AutoFollowAtEnd)
		{
			// 跟随态由几何决定：滚动条停在底部就跟随，读者明确上滚才离开。
			// 这样 presenter 切换、行高重测、展开/折叠都不会把跟随永久关掉；
			// 「展开步骤后停在预览行」靠 StopFollowingGrowth 清掉本次跟随，再由几何保持。
			var distance = maximum - offset.Y;
			if (maximum > AutoFollowTolerance)
			{
				if (distance <= AutoFollowTolerance)
				{
					_readerEscaped = false;
					_followGrowth = true;
				}
				else if (_readerEscaped)
				{
					_followGrowth = false;
				}
				else if (!extentChanged)
				{
					// extent 稳定却被外部移动（滚动条拖动、焦点、程序化定位）：离开末尾。
					_followGrowth = false;
					if (!IsApplyingOffset) Stop();
				}
				// extent 增长且读者没上滚：保持现状，不把布局校正误判成离开。
			}
		}
		else if (!IsApplyingOffset && offset != _lastOffset)
		{
			// 虚拟化行在 arrange 阶段被重新测高时，presenter 会请求一次锚点校正并把远端
			// extent 一起改大。这属于布局，不是读者意图；若把它当成上滚，跟底会在第一帧
			// 被静默取消，组体进度随即停在半路。只有 extent 没变的手工位移才算接管。
			if (!_followGrowth || !extentChanged)
			{
				_followGrowth = false;
				Stop();
			}
		}
		_lastOffset = offset;
		_lastMaximum = maximum;
		// 内容还在变高：布局期间不能改偏移，等这一帧发布完 extent 再落位。
		if (_followGrowth && !IsApplyingOffset && maximum - offset.Y > 0.5) QueueGrowthFollow();
	}

	private void OnScrollBarDown(MouseEventArgs _)
	{
		// 拖动/点击滚动条是显式阅读意图：先退出跟底，之后停到底部再由几何重新锁底。
		_readerEscaped = true;
		StopFollowingGrowth();
	}

	/// <summary>把跟随增长的重夹排到布局之后；一帧里只排一次。</summary>
	private void QueueGrowthFollow()
	{
		if (_growthFollowQueued) return;
		if (!Application.IsRunning || Application.Current.Dispatcher is not { } dispatcher)
		{
			SyncMetrics();
			Apply(new Point(Offset.X, MaxVerticalOffset));
			return;
		}
		_growthFollowQueued = true;
		dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
		{
			_growthFollowQueued = false;
			if (_disposed || !_followGrowth) return;
			SyncMetrics();
			Apply(new Point(Offset.X, MaxVerticalOffset));
		});
	}

	private void OnTextScrollChanged(Aprillz.MewUI.Text.ITextViewHost _) => OnScrollChanged();
	private void OnMouseDown(MouseEventArgs args) { _followGrowth = false; Stop(); }
	private void OnKeyDown(KeyEventArgs args)
	{
		// 向上翻页/回到开头是离开末尾的意图；向下按键交给几何判定是否回到底部。
		if (args.Key is Key.Up or Key.PageUp or Key.Home) _readerEscaped = true;
		_followGrowth = false;
		Stop();
		// Ctrl+PageUp/PageDown are tab-navigation chords; a focused editor leaves them unhandled
		// on purpose, so the viewport must not consume them either.
		if (args.Handled || args.ControlKey) return;
		// Only keys the focused control left unhandled reach the viewport: those really scroll it.
		var viewportHeight = _viewport?.ViewportHeight ?? 0;
		var line = 40.0;
		var page = Math.Max(line, viewportHeight - line);
		var target = args.Key switch
		{
			Key.Up => Offset.Y - line,
			Key.Down => Offset.Y + line,
			Key.PageUp => Offset.Y - page,
			Key.PageDown => Offset.Y + page,
			Key.Home => 0,
			Key.End => MaxVerticalOffset,
			_ => double.NaN,
		};
		if (double.IsNaN(target)) return;
		var delta = target - Offset.Y;
		if (Math.Abs(delta) < 0.5)
		{
			// Already at that edge. Still report the reader's direction so an explicit End/PageDown
			// can re-lock following at the tail; leave the key itself for an ancestor viewport.
			VerticalInput?.Invoke(args.Key is Key.Up or Key.PageUp or Key.Home ? -line : line);
			return;
		}
		ScrollByPixels(0, delta);
		args.Handled = true;
	}
	public void Stop() { _clock.Stop(); _toEnd = false; }

	// 对于以时间为基准的物理循环，内置 Easing 提供恒等映射即可
	private static double LinearEase(double progress) => progress;

	/// <summary>Chromium 滚动曲线的 CSS ease：cubic-bezier(0.25, 0.1, 0.25, 1)。</summary>
	private static double ChromiumEase(double x)
	{
		x = Math.Clamp(x, 0, 1);
		// 先用牛顿迭代把时间参数 t 从横坐标解出来，再取纵坐标。
		var t = x;
		for (var iteration = 0; iteration < 6; iteration++)
		{
			double error = Bezier(t, EaseX1, EaseX2) - x;
			if (Math.Abs(error) < 1e-5) break;
			double slope = BezierSlope(t, EaseX1, EaseX2);
			if (Math.Abs(slope) < 1e-6) break;
			t = Math.Clamp(t - error / slope, 0, 1);
		}
		return Bezier(t, EaseY1, EaseY2);
	}

	private static double Bezier(double t, double p1, double p2)
	{
		double u = 1 - t;
		return 3 * u * u * t * p1 + 3 * u * t * t * p2 + t * t * t;
	}

	private static double BezierSlope(double t, double p1, double p2)
	{
		double u = 1 - t;
		return 3 * u * u * p1 + 6 * u * t * (p2 - p1) + 3 * t * t * (1 - p2);
	}

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
		if (_verticalBar is not null) _verticalBar.MouseDown -= OnScrollBarDown;
		Controllers.Remove(_view);
	}
}

/// <summary>
/// 嵌套滚动链：内层视口在边界放行后，滚轮沿祖先链找到下一个真正滚得动的视口。
/// </summary>
internal static class ScrollCascade
{
	/// <summary>
	/// 从 <paramref name="origin"/> 的父级向上找最近的可滚动祖先视口并接管这次滚轮。
	/// </summary>
	/// <param name="origin">起点元素，从它的父级开始查找，本身不会被滚动。</param>
	/// <param name="horizontal">滚轮横向刻度（+ 表示向左）。</param>
	/// <param name="vertical">滚轮纵向刻度（+ 表示向上）。</param>
	/// <returns>接管了滚轮则为 true；整条祖先链都到边界时为 false，事件保持未处理。</returns>
	public static bool Route(Element origin, double horizontal, double vertical)
	{
		if (horizontal == 0 && vertical == 0) return false;
		for (var parent = origin.Parent; parent is not null; parent = parent.Parent)
		{
			if (parent is not ScrollViewer viewport || !viewport.IsVisible) continue;
			// 只接管已经平滑滚动的视口：不在滚轮路径上顺手给无关容器装控制器。
			if (SmoothScroll.Find(viewport) is not { } smooth) continue;
			var delta = smooth.WheelDelta(horizontal, vertical);
			if (delta.X == 0 && delta.Y == 0) continue;
			HoverHints.Cancel(viewport);
			smooth.ScrollBy(delta.X, delta.Y);
			return true;
		}
		return false;
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
