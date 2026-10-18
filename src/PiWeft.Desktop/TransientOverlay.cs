using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

namespace PiWeft.Desktop;

/// <summary>模型、命令和图片浮层共用窗口级互斥与清理，避免跨标签叠加。</summary>
internal sealed class TransientOverlay : IOverlayService
{
	private UIElement? _active;
	private Action? _close;

	public static void Open(Window window, UIElement view, Action close)
	{
		var host = window.OverlayLayer.GetOrCreateService(static _ => new TransientOverlay());
		if (!ReferenceEquals(host._active, view)) host._close?.Invoke();
		host._active = view;
		host._close = close;
		// 窗框标题栏属于窗口外壳：窗口级浮层从标题栏下方开始，避免遮住页签、面板键与窗口按钮。
		if (view is FrameworkElement element)
		{
			element.Margin = window is MainWindow main
				? MainWindow.OverlayInsetFor(main.WindowState)
				: new Thickness(0, AppTheme.TitleBarHeight, 0, 0);
		}
		if (!window.OverlayLayer.Contains(view)) window.OverlayLayer.Add(view);
	}

	public static void Remove(Window window, UIElement view)
	{
		window.OverlayLayer.Remove(view);
		if (window.OverlayLayer.GetService<TransientOverlay>() is { } host && ReferenceEquals(host._active, view))
		{
			host._active = null;
			host._close = null;
		}
	}
}
