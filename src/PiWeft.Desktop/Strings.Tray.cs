namespace PiWeft.Desktop;

public static partial class Strings
{
	// 托盘
	public const string TrayShowWindow = "显示主窗口";
	public const string TrayQuit = "退出 PiWeft";

	// 关闭行为
	public const string WindowSettings = "窗口";
	public const string CloseToTraySetting = "关闭窗口时隐藏到托盘";
	public const string CloseToTrayHint = "任务继续在后台运行。点托盘图标恢复窗口，退出在托盘菜单里。";
	public const string CloseToTrayUnavailable = "当前桌面没有系统托盘，关闭窗口会直接退出。";
	public const string CloseToTrayGnomeHint = "GNOME 可安装 AppIndicator 扩展后重试。";
}
