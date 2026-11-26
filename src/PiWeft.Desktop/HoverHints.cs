using System.Runtime.CompilerServices;
using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

namespace PiWeft.Desktop;

internal sealed class HoverHints : IOverlayService, IDisposable
{
	internal static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(350);

	// 浮层是独立的原生窗口：X11 上它压住指针就夺走指针，主窗口收到 LeaveNotify 清掉整条悬停链，
	// 浮层随即被自己的 MouseLeave 取消，指针不动就再也弹不出来。锚点与指针之间留这道缝，
	// 窗口自带的阴影内边距（上 4、下 8）还要再吃掉几个像素。
	private const double PointerGap = 12;

	// 锚点矩形贴着指针取一段小高度：Popup 按下沿置顶、上沿置底，两侧都留出 PointerGap。
	private const double PointerHalfWidth = 6;

	private static readonly ConditionalWeakTable<FrameworkElement, Hint> Hints = new();
	private readonly Window _window;
	private readonly DispatcherTimer _timer = new(Delay);
	private FrameworkElement? _owner;
	private Rect? _anchor;
	private Point? _pointer;
	private Popup? _popup;

	private HoverHints(Window window)
	{
		_window = window;
		_timer.Tick += Show;
		window.Deactivated += Cancel;
		// Popup 是独立的原生窗口，owner 最小化时它不会跟着隐藏（X11 尤其明显）。
		// 原生 ToolTip 由窗口的 popup 管理器在失焦时收起，这里补上窗口状态这一路。
		window.WindowStateChanged += OnWindowStateChanged;
		window.Closed += Dispose;
		window.MouseMove += OnMouseMove;
		window.MouseDown += OnMouseDown;
		window.MouseWheel += OnWheel;
		window.KeyDown += OnKeyDown;
	}

	/// <summary>
	/// 给元素挂悬停提示。锚点写元素布局坐标（与 <see cref="Element.Bounds"/> 同一套），
	/// 不给就落在指针旁边。
	/// </summary>
	public static T Set<T>(T element, string? text, Rect? anchor = null) where T : FrameworkElement
	{
		var hint = Hints.GetValue(element, static owner => new Hint(owner));
		if (hint.Text == text && hint.Anchor == anchor) return element;
		hint.Text = text;
		hint.Anchor = anchor;
		// 浮层已经为这个元素开着时就地换字：状态行那类秒级刷新的提示每改一次都重计时，
		// 永远不会到点；行走 chip 时也能直接换到下一枚的锚点上。
		if (Refresh(element)) return element;
		CancelOwner(element);
		// 行内 chip 的提示是在鼠标移动中换文本的；鼠标已经在元素上时直接开始计时，
		// 否则要等下一次 MouseEnter 才出现，行内悬浮永远看不到。
		if (!string.IsNullOrEmpty(text) && element.IsMouseOver) Begin(element);
		return element;
	}

	internal static string? Text(FrameworkElement element) => Hints.TryGetValue(element, out var hint) ? hint.Text : null;

	internal static Rect? AnchorOf(FrameworkElement element) => Hints.TryGetValue(element, out var hint) ? hint.Anchor : null;

	internal static bool IsOpen(Window window) => window.OverlayLayer.GetService<HoverHints>()?._popup?.IsOpen == true;

	private static void Begin(FrameworkElement owner)
	{
		if (!Application.IsRunning || string.IsNullOrEmpty(Text(owner)) || owner.FindVisualRoot() is not Window window) return;
		if (!CanShow(window, requireFocus: true)) return;
		var service = window.OverlayLayer.GetOrCreateService(_ => new HoverHints(window));
		service.Cancel();
		service._owner = owner;
		service._anchor = AnchorOf(owner);
		service._timer.Start();
	}

	public static void Cancel(Element element)
	{
		if (element.FindVisualRoot() is Window window) window.OverlayLayer.GetService<HoverHints>()?.Cancel();
	}

	private static void CancelOwner(Element owner)
	{
		if (owner.FindVisualRoot() is Window window && window.OverlayLayer.GetService<HoverHints>() is { } service &&
			ReferenceEquals(service._owner, owner)) service.Cancel();
	}

	/// 提示已为这个元素打开且仍停在它身上时，直接把文本与锚点换到浮层上。
	private static bool Refresh(FrameworkElement owner)
	{
		if (string.IsNullOrEmpty(Text(owner)) || owner.FindVisualRoot() is not Window window) return false;
		if (window.OverlayLayer.GetService<HoverHints>() is not { _popup: { IsOpen: true } popup } service) return false;
		if (!ReferenceEquals(service._owner, owner)) return false;
		service._anchor = AnchorOf(owner);
		FillContent(popup, owner);
		var anchor = service.AnchorInWindow(owner);
		popup.MoveTo(anchor, service.SideFor(anchor));
		return true;
	}

	/// 窗口最小化后不应再有悬停提示：此时 owner 没有可视客户区，计时器到点或
	/// 残留的 IsMouseOver 都会把置顶的 popup 留在桌面上。
	/// <param name="requireFocus">
	/// 悬停提示要求窗口处于激活态：Linux 下未聚焦窗口仍会收到指针进入/移动，
	/// 否则切到别的应用后桌面顶层仍会冒出 tooltip。
	/// </param>
	internal static bool CanShow(Window window, bool requireFocus = false) =>
		window.WindowState != WindowState.Minimized && (!requireFocus || window.IsActive);

	private void OnWindowStateChanged(WindowState state)
	{
		if (state == WindowState.Minimized) Cancel();
	}

	private void OnMouseMove(MouseEventArgs args) => _pointer = args.GetPosition(_window);

	private void Show()
	{
		_timer.Stop();
		if (!CanShow(_window, requireFocus: true) || _owner is not { IsMouseOver: true } owner ||
			!ReferenceEquals(owner.FindVisualRoot(), _window) || string.IsNullOrEmpty(Text(owner)))
		{ Cancel(); return; }
		for (Element? ancestor = owner; ancestor is not null; ancestor = ancestor.Parent)
			if (ancestor is UIElement { IsVisible: false }) { Cancel(); return; }

		var popup = _popup ??= new Popup().IsHitTestVisible(false);
		FillContent(popup, owner);
		var anchor = AnchorInWindow(owner);
		// 窗口下半部分向上弹，免得盖住输入框底部的工具条；Popup 自己还会按屏幕边界翻转。
		popup.ShowAt(owner, anchor, SideFor(anchor));
	}

	// 浮层内容每次重造：文本可能一行变三行，旧 Border 留着只会带着旧尺寸。
	private static void FillContent(Popup popup, FrameworkElement owner)
	{
		if (popup.Content is { } oldContent) UiLifetime.Release(oldContent);
		popup.Content = new Border().StyleName(AppStyles.Card).CornerRadius(AppTheme.RadiusMd)
			.MaxWidth(420).Padding(new Thickness(10, 6, 10, 6)).IsHitTestVisible(false)
			.Child(new TextBlock().Text(Text(owner) ?? string.Empty).FontSize(AppTheme.FontSizeCaption)
				.PrimaryText().TextWrapping(TextWrapping.Wrap));
	}

	private PopupAnchorSide SideFor(Rect anchor) =>
		anchor.Y + (anchor.Height / 2) > _window.ClientSize.Height / 2 ? PopupAnchorSide.Above : PopupAnchorSide.Below;

	/// 浮层的锚点。默认贴着指针：按元素整块摆放时，宽行宽列的中点离指针很远，浮层还会
	/// 落到指针下面（见 <see cref="PointerGap"/>）；Viewbox 里的格子更是整块偏移。
	private Rect AnchorInWindow(FrameworkElement owner)
	{
		if (_anchor is { } anchor)
		{
			// 调用方给的锚点与 Bounds 同一套坐标，换成元素本地坐标再补窗口坐标；
			// 上下各留 PointerGap，浮层不会压住 chip 上的指针。
			var local = new Rect(anchor.X - owner.Bounds.X, anchor.Y - owner.Bounds.Y - PointerGap,
				anchor.Width, anchor.Height + (PointerGap * 2));
			return DrawnRect(owner, local);
		}

		var point = _pointer ?? new Point(owner.Bounds.X + (owner.Bounds.Width / 2), owner.Bounds.Y + (owner.Bounds.Height / 2));
		return new Rect(point.X - PointerHalfWidth, point.Y - PointerGap, PointerHalfWidth * 2, PointerGap * 2);
	}

	/// 元素本地矩形换成窗口坐标。MewUI 按窗口坐标安排元素，Viewbox 这类祖先只在绘制与命中时
	/// 套变换，Bounds 不带；少了这一段，锚点会落在变换前的位置上。
	private static Rect DrawnRect(Element element, Rect localRect) =>
		element.FindVisualRoot() is Window window ? element.TranslateRect(localRect, window) : localRect;

	/// 菜单类弹层贴着元素摆放时的锚点，上下各留 6px，交给 Popup 做边界翻转。
	internal static Rect Anchor(Element owner) =>
		DrawnRect(owner, new Rect(0, -6, owner.Bounds.Width, owner.Bounds.Height + 12));

	private void Cancel()
	{
		_timer.Stop();
		_owner = null;
		_anchor = null;
		_popup?.Close();
	}

	private void OnMouseDown(MouseEventArgs _) => Cancel();
	private void OnWheel(MouseWheelEventArgs _) => Cancel();
	private void OnKeyDown(KeyEventArgs _) => Cancel();

	public void Dispose()
	{
		Cancel();
		if (_popup?.Content is { } content) UiLifetime.Release(content);
		_popup?.Dispose();
		_popup = null;
		_timer.Dispose();
		_window.Deactivated -= Cancel;
		_window.WindowStateChanged -= OnWindowStateChanged;
		_window.Closed -= Dispose;
		_window.MouseMove -= OnMouseMove;
		_window.MouseDown -= OnMouseDown;
		_window.MouseWheel -= OnWheel;
		_window.KeyDown -= OnKeyDown;
	}

	private sealed class Hint
	{
		public string? Text;
		public Rect? Anchor;
		public Hint(FrameworkElement owner)
		{
			owner.MouseEnter += () => Begin(owner);
			owner.MouseLeave += () => CancelOwner(owner);
			owner.MouseDown += _ => Cancel(owner);
			owner.MouseWheel += _ => Cancel(owner);
			owner.KeyDown += _ => Cancel(owner);
		}
	}
}

internal static class HoverHintExtensions
{
	public static T HoverTip<T>(this T element, string? text) where T : FrameworkElement => HoverHints.Set(element, text);
}
