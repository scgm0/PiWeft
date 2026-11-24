using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

namespace PiWeft.Desktop;

internal enum ToastKind
{
	Info,
	Success,
	Error,
}

internal sealed record ToastItem(string Message, ToastKind Kind);

/// 窗口级 toast 的统一入口：交给 <see cref="ToastHost"/> 呈现（顶部横幅、最多三条、点穿、自动淡出）。
/// 没有窗口或应用未运行时静默忽略，因此无窗口的布局测试与退出阶段调用是安全的。
internal static class Toasts
{
	private const int RecentCapacity = 8;

	private static readonly List<ToastItem> RecentItems = [];

	public static void Show(Element? origin, string message, ToastKind kind = ToastKind.Info)
	{
		if (string.IsNullOrWhiteSpace(message)) return;
		var text = message.Trim();
		RecentItems.Add(new ToastItem(text, kind));
		while (RecentItems.Count > RecentCapacity) RecentItems.RemoveAt(0);
		if (!Application.IsRunning) return;
		if (origin?.FindVisualRoot() is not Window window) return;
		window.OverlayLayer.GetOrCreateService(layer => new ToastHost(window)).Show(text, kind);
	}

	public static void Success(Element? origin, string message) => Show(origin, message, ToastKind.Success);

	public static void Error(Element? origin, string message) => Show(origin, message, ToastKind.Error);

	internal static IReadOnlyList<ToastItem> Recent => RecentItems;

	internal static void ClearRecent() => RecentItems.Clear();
}
