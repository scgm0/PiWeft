namespace PiWeft.Desktop;

/// 关闭主窗口时隐藏到托盘，还是真正关闭进程。
internal static class TrayBehavior
{
	/// 关闭驻留要求设置开启且托盘真的可用：托盘不可用时隐藏会让窗口失联，只能真关。
	/// 退出请求在调用方先短路，避免为不求值的可用性查询付 500 ms。
	public static bool ShouldHide(bool closeToTray, bool traySupported)
		=> closeToTray && traySupported;
}
