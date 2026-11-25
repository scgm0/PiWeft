using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

namespace PiWeft.Desktop;

internal sealed class TransientOverlay : IOverlayService
{
	private readonly OverlayInsetFollower _inset = new();
	private UIElement? _active;
	private Action? _close;

	public static void Open(Window window, UIElement view, Action close)
	{
		var host = window.OverlayLayer.GetOrCreateService(static _ => new TransientOverlay());
		if (!ReferenceEquals(host._active, view)) host._close?.Invoke();
		host._active = view;
		host._close = close;
		// 窗框标题栏属于窗口外壳：窗口级浮层从标题栏下方开始，避免遮住页签、面板键与窗口按钮。
		// 让位量跟窗口状态走：最大化时打开再还原，遮罩不能还按最大化时的边距铺到透明边上。
		if (view is FrameworkElement element) host._inset.Attach(window, element);
		else host._inset.Detach();
		if (!window.OverlayLayer.Contains(view)) window.OverlayLayer.Add(view);
	}

	public static void Remove(Window window, UIElement view)
	{
		window.OverlayLayer.Remove(view);
		if (window.OverlayLayer.GetService<TransientOverlay>() is { } host && ReferenceEquals(host._active, view))
		{
			host._active = null;
			host._close = null;
			host._inset.Detach();
		}
	}
}
