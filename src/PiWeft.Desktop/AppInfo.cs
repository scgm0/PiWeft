namespace PiWeft.Desktop;

public static class AppInfo
{
	public const string Version = "0.1.0";

	/// 关于页「相关项目」的地址。文案与顺序在 AboutSection，这里只放站点。
	public const string PiDocsUrl = "https://pi.dev/docs/latest";

	public const string RepositoryUrl = "https://github.com/scgm0/PiWeft";

	/// PiWeft.Markdown 是本仓库的子项目，没有独立站点，指到源码目录。
	public const string MarkdownUrl = RepositoryUrl + "/tree/main/src/PiWeft.Markdown";

	public const string MewUiUrl = "https://github.com/aprillz/MewUI";

	public const string PiDeckUrl = "https://github.com/ayuayue/PiDeck";

	public const string LucideUrl = "https://lucide.dev";

	public const string DotNetUrl = "https://dotnet.microsoft.com";

	/// 桌面用它分组窗口、托盘图标与启动器条目；与 package.ps1 里的 CFBundleIdentifier 一致。
	public const string ApplicationId = "com.mystivaid.piweft";

	public const string Framework = "Aprillz.MewUI 实时源码（Core + X11 + MewVG + Svg）+ PiWeft.Markdown";

	public const string Icons = "Lucide（ISC）";
}
