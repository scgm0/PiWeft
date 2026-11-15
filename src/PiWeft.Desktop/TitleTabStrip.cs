using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

namespace PiWeft.Desktop;

/// 标题栏页签条：页签超出可用宽度时横向滚动，普通滚轮直接落在横向轴上。
/// 页签始终居中在 40 DIP 的标题栏里，横向滚动条走自动隐藏的浮层，落在页签下方的空隙。
internal sealed class TitleTabStrip
{
	// 选中页签贴边时留一点余量：页签有圆角，贴着裁切边看起来像被切掉。
	private const double RevealMargin = 4;

	private readonly StackPanel _tabs = new();
	private readonly ScrollViewer _scroll;
	// 控制器与回调在构造时拿一次：滚轮每格都会走 OnWheel，滚入只在切换时用，
	// 但两者都不该每次重新查表或建闭包。
	private readonly SmoothScroll _smooth;
	private readonly Action _revealPending;
	private UIElement? _pendingReveal;
	private bool _revealQueued;

	public TitleTabStrip()
	{
		_tabs
			.Orientation(Orientation.Horizontal)
			.Spacing(2)
			.VerticalAlignment(VerticalAlignment.Center);

		_scroll = new ScrollViewer()
			.SmoothScrolling()
			.Content(_tabs)
			.AutoHorizontalScroll()
			.NoVerticalScroll()
			.AutoHideScrollBars(true)
			.Height(AppTheme.TitleBarHeight)
			.VerticalAlignment(VerticalAlignment.Center);
		_smooth = SmoothScroll.For(_scroll);
		_revealPending = RevealPending;
		_scroll.MouseWheel += OnWheel;
		View = _scroll;
	}

	public ScrollViewer View { get; }

	internal bool HasPendingReveal => _pendingReveal is not null;

	public void Clear()
	{
		// 已经排队的滚入跟着作废：页签都拆了，旧按钮的 Bounds 不再代表任何东西。
		_pendingReveal = null;
		_tabs.Clear();
	}

	public void Add(Button tab) => _tabs.Add(tab);

	/// 选中项切换后调用：布局落定再把页签滚进视口。多次调用只认最后一次。
	public void Reveal(UIElement? tab)
	{
		if (tab is null)
		{
			_pendingReveal = null;
			return;
		}

		_pendingReveal = tab;
		if (_revealQueued)
		{
			return;
		}

		var dispatcher = Application.IsRunning ? Application.Current.Dispatcher : null;
		if (dispatcher is null)
		{
			// 无窗口布局（测试、启动前的骨架）没有渲染帧；调用点自己决定何时 RevealNow。
			return;
		}

		_revealQueued = true;
		dispatcher.BeginInvoke(DispatcherPriority.Render, _revealPending);
	}

	private void RevealPending()
	{
		_revealQueued = false;
		var pending = _pendingReveal;
		_pendingReveal = null;
		if (pending is not null)
		{
			RevealNow(pending);
		}
	}

	/// 按当前布局把页签放进视口；布局没落定时 Bounds 为 0，直接跳过。
	internal void RevealNow(UIElement tab)
	{
		var viewport = _scroll.Bounds;
		var bounds = tab.Bounds;
		if (viewport.Width <= 0 || bounds.Width <= 0)
		{
			return;
		}

		var left = viewport.Left + RevealMargin;
		var right = viewport.Right - RevealMargin;
		var delta = bounds.Left < left ? bounds.Left - left
			: bounds.Right > right ? bounds.Right - right
			: 0;
		if (delta == 0)
		{
			return;
		}

		_scroll.SetScrollOffsets(_scroll.HorizontalOffset + delta, 0);
	}

	private void OnWheel(MouseWheelEventArgs args)
	{
		// 标题栏没有纵向内容：普通滚轮走横向，Shift 与触控板横滑仍由 SmoothScroll 处理。
		if (args.Handled || args.Delta.Y == 0 || args.Delta.X != 0)
		{
			return;
		}

		HoverHints.Cancel(_scroll);
		_smooth.ScrollBy(-args.Delta.Y, 0);
		args.Handled = true;
	}
}
