using System.Reflection;
using PiWeft.Cli;
using ZLogger;
using Aprillz.MewUI;

namespace PiWeft.Desktop;

/// <summary>
/// 内嵌字体：把界面用到的字体打进程序集，渲染效果不再取决于机器上装了什么。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>界面正文用 <b>HarmonyOS Sans SC</b> 的官方静态字重（Regular / Bold）。早前用 fontTools
/// 把三个字重合成过一个可变字体，但插值后的笔画粗细在原字重之间不够均匀，因此改回官方文件；
/// 界面只用两档字重，Medium 没有引用，就没有嵌进来。</item>
/// <item>代码块、工具输出等等宽文本用 <b>Maple Mono NF CN</b>（静态 Regular）。Maple Mono 的可变字体只有拉丁版，
/// 没有 CJK；用 CN 版是为了让代码里的中文注释也能等宽对齐，NF 版额外的 Nerd Font 图标供终端里的 powerline 提示符使用。</item>
/// </list>
/// 使用官方静态字重资源；MewUI 0.22.1 的注册表按字族只保留最后一个文件，
/// 因此 Regular 最后注册，Normal 使用原版正文笔画，Bold 请求由各平台后端处理。
/// 另外给 Regular 注册一个显式的字族别名 <c>HarmonyOS_Sans_SC_Regular</c>，并把它设为
/// 全局默认字体：即使系统里也装了同名字族，默认正文也一定落到内嵌的 Regular 文件。
/// 许可原文随包放在 <c>Resources/fonts</c>；注册失败时退回字体栈里的 <c>sans-serif</c>/<c>monospace</c>。
/// </remarks>
public static class AppFonts
{
	private const string ResourcePrefix = "PiWeft.Desktop.Resources.fonts.";

	/// <summary>界面正文字体族（友好的字族名，供显式设置字体的文本使用）。</summary>
	public const string UiFamily = "HarmonyOS Sans SC";

	/// <summary>
	/// 默认正文字体族：Regular 文件的显式别名。默认样式、窗口与框架自绘对话框都用它，
	/// 避免系统同名可变字体抢占，也避免字重回退到 Bold。
	/// </summary>
	public const string DefaultUiFamily = "HarmonyOS_Sans_SC_Regular";

	/// <summary>等宽字体族（Nerd Font 版，含 powerline 图标）。</summary>
	public const string MonoFamily = "Maple Mono NF CN";

	/// <summary>一格滚轮的位移（DIP）：与 Chromium/Electron 的 ~100px 对齐。</summary>
	internal const double ScrollWheelStepDip = 100;

	private static bool _registered;
	private static bool _uiLoaded;
	private static bool _regularLoaded;
	private static bool _monoLoaded;

	/// <summary>代码与工具输出使用的字体族；注册失败时退回系统等宽字体。</summary>
	public static string Mono => _monoLoaded ? MonoFamily : "monospace";

	/// <summary>界面正文使用的字体族；注册失败时交给平台默认字体。</summary>
	public static string Ui => _uiLoaded ? UiFamily : ThemeMetrics.SystemFontFamily;

	/// <summary>全局默认正文字体族；Regular 注册失败时交给平台默认字体。</summary>
	public static string DefaultUi => _regularLoaded ? DefaultUiFamily : ThemeMetrics.SystemFontFamily;

	/// <summary>界面字体的展示名：默认是官方 Regular 文件。</summary>
	public static string UiDisplayName => _regularLoaded ? $"{UiFamily} Regular" : Ui;

	/// <summary>是否成功内嵌了正文字体。</summary>
	public static bool HasUiFont => _uiLoaded;

	/// <summary>是否成功内嵌了等宽字体。</summary>
	public static bool HasMonoFont => _monoLoaded;

	/// <summary>注册内嵌字体。必须在创建任何控件之前调用一次。</summary>
	public static void Register()
	{
		if (_registered)
		{
			return;
		}

		_registered = true;

		// MewUI 0.22.1 的字族注册表只保存最后一个文件，Regular 必须最后注册。
		// 否则 Linux 的 Normal 也会拿到 Bold；粗体由后端按请求字重处理。
		// 同时把 Regular 注册到显式别名，供 ThemeMetrics 的默认字体使用。
		var boldLoaded = RegisterFont("HarmonyOS_Sans_SC_Bold.ttf");
		_regularLoaded = RegisterFont("HarmonyOS_Sans_SC_Regular.ttf", DefaultUiFamily);
		_uiLoaded = boldLoaded || _regularLoaded;

		_monoLoaded = RegisterFont("MapleMono-NF-CN-Regular.ttf");

		// 正文是全局默认字体：由 ThemeMetrics 下发到每个控件，MewUI 自己画的对话框也跟着变。
		// 用 Regular 的显式别名，确保默认字重拿到官方 Regular 文件。
		// 滚轮步长同时对齐浏览器（Chromium/Electron 一格约 100 DIP）：MewUI 默认 50 DIP 偏小，
		// 同样的手势要走两倍格数，手感会显得「拖」。
		var metrics = ThemeMetrics.Default with { ScrollWheelStep = ScrollWheelStepDip };
		if (_regularLoaded)
		{
			metrics = metrics with
			{
				FontFamily = DefaultUiFamily,
				FontSize = AppTheme.FontSizeControl,
				FontWeight = FontWeight.Normal,
			};
		}
		ThemeManager.DefaultMetrics = metrics;
	}

	private static bool RegisterFont(string fileName, string? nameHint = null)
	{
		var assembly = Assembly.GetExecutingAssembly();
		using var stream = assembly.GetManifestResourceStream(ResourcePrefix + fileName);
		if (stream is null)
		{
			AppLog.For<MainWindow>().ZLogWarning($"Embedded font missing: file={fileName}");
			return false;
		}

		try
		{
			FontResources.Register(stream, Path.GetExtension(fileName), nameHint);
			return true;
		}
		catch (Exception ex)
		{
			AppLog.Failure(AppLog.For<MainWindow>(), "Register embedded font", ex);
			return false;
		}
	}
}
