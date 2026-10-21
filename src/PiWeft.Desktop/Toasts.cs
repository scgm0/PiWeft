using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

namespace PiWeft.Desktop;

/// <summary>通知语气：只用于调用方语义与诊断记录，呈现由框架的 ToastService 负责。</summary>
internal enum ToastKind
{
	Info,
	Success,
	Error,
}

/// <summary>一条已发出的通知。</summary>
internal sealed record ToastItem(string Message, ToastKind Kind);

/// <summary>
/// 窗口级 toast 的统一入口：交给框架的 <see cref="ToastService"/> 呈现
/// （阴影、滑入滑出、随主题换肤、自动消失都由它管理，并纳入窗口的合成渲染）。
/// 没有窗口或应用未运行时静默忽略，因此无窗口的布局测试与退出阶段调用是安全的。
/// </summary>
internal static class Toasts
{
	private const int RecentCapacity = 8;

	/// <summary>最近发出的通知：无窗口时也会记录，供交互诊断与无窗口测试断言。</summary>
	private static readonly List<ToastItem> RecentItems = [];

	public static void Show(Element? origin, string message, ToastKind kind = ToastKind.Info)
	{
		if (string.IsNullOrWhiteSpace(message)) return;
		var text = message.Trim();
		RecentItems.Add(new ToastItem(text, kind));
		while (RecentItems.Count > RecentCapacity) RecentItems.RemoveAt(0);
		if (!Application.IsRunning) return;
		if (origin?.FindVisualRoot() is not Window window) return;
		window.ShowToast(text);
	}

	public static void Success(Element? origin, string message) => Show(origin, message, ToastKind.Success);

	public static void Error(Element? origin, string message) => Show(origin, message, ToastKind.Error);

	/// <summary>最近发出的通知，最多 <see cref="RecentCapacity"/> 条（诊断与无窗口测试用）。</summary>
	internal static IReadOnlyList<ToastItem> Recent => RecentItems;

	/// <summary>清空诊断记录，避免上一个用例的提示让断言误通过。</summary>
	internal static void ClearRecent() => RecentItems.Clear();
}
