namespace PiWeft.Ai.Auth;

/// 交互登录时是否跳过自动打开浏览器。CLI 的 <c>--no-browser</c> 在进入登录流程前设置它，
/// 设备码与授权码两种流程都仍然把 URL 打印到终端。
public static class OAuthBrowser
{
	public static bool Suppress { get; set; }
}
