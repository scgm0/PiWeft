using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

namespace PiWeft.Desktop;

/// 窗口级浮层的让位量跟随窗口状态。
/// 普通窗口有透明阴影边与标题栏，最大化/全屏没有；只按打开时算一次，窗口还原后
/// 遮罩仍按最大化时的边距铺，暗色会越过圆角、盖到透明边上。
internal sealed class OverlayInsetFollower
{
	private readonly Func<Window, Thickness> _inset;
	private Window? _window;
	private FrameworkElement? _view;

	internal OverlayInsetFollower(Func<Window, Thickness>? inset = null) =>
		_inset = inset ?? InsetFor;

	/// 让 view 的外边距从此刻起跟随 window 的窗口状态；换成别的窗口或视图时先解除旧订阅。
	internal void Attach(Window window, FrameworkElement view)
	{
		if (ReferenceEquals(_window, window) && ReferenceEquals(_view, view))
		{
			Apply();
			return;
		}

		Detach();
		_window = window;
		_view = view;
		window.WindowStateChanged += OnWindowStateChanged;
		Apply();
	}

	internal void Detach()
	{
		if (_window is { } window) window.WindowStateChanged -= OnWindowStateChanged;
		_window = null;
		_view = null;
	}

	private void OnWindowStateChanged(WindowState _) => Apply();

	private void Apply()
	{
		if (_window is { } window && _view is { } view) view.Margin = _inset(window);
	}

	/// 窗口级浮层在窗口里的可见区域：跳过标题栏；X11 普通窗口再跳过透明阴影边。
	internal static Thickness InsetFor(Window window) =>
		window is MainWindow main
			? MainWindow.OverlayInsetFor(main.WindowState)
			: new Thickness(0, AppTheme.TitleBarHeight, 0, 0);
}
