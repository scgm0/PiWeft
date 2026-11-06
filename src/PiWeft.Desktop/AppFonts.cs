using System.Reflection;
using PiWeft.Cli;
using ZLogger;
using Aprillz.MewUI;

namespace PiWeft.Desktop;

/// 内嵌字体：界面用到的字体打进程序集，渲染不依赖机器上装了什么。
/// <list type="bullet">
/// <item>正文用 HarmonyOS Sans SC 官方静态字重（Regular / Bold）。可变字体插值后的笔画不均匀，已改回；
/// 界面只用两档，Medium 不嵌。</item>
/// <item>代码与终端用 Maple Mono NF CN（静态 Regular）。CN 版带 CJK，代码里的中文注释能对齐；
/// NF 版的 Nerd Font 图标供 powerline 提示符使用。</item>
/// </list>
/// Regular 最后注册（MewUI 注册表按字族只保留最后一个文件），并注册字族别名
/// <c>HarmonyOS_Sans_SC_Regular</c> 作全局默认，系统装了同名字族也落到内嵌文件。
/// 许可原文在 <c>Resources/fonts</c>；注册失败退回 <c>sans-serif</c>/<c>monospace</c>。
public static class AppFonts
{
	private const string ResourcePrefix = "PiWeft.Desktop.Resources.fonts.";

	public const string UiFamily = "HarmonyOS Sans SC";

	/// 默认正文字体族：Regular 文件的显式别名。默认样式、窗口与框架自绘对话框都用它，
	/// 避免系统同名可变字体抢占，也避免字重回退到 Bold。
	public const string DefaultUiFamily = "HarmonyOS_Sans_SC_Regular";

	public const string MonoFamily = "Maple Mono NF CN";

	internal const double ScrollWheelStepDip = 100;

	private static bool _registered;
	private static bool _uiLoaded;
	private static bool _regularLoaded;
	private static bool _monoLoaded;

	public static string Mono => _monoLoaded ? MonoFamily : "monospace";

	public static string Ui => _uiLoaded ? UiFamily : ThemeMetrics.SystemFontFamily;

	public static string DefaultUi => _regularLoaded ? DefaultUiFamily : ThemeMetrics.SystemFontFamily;

	public static string UiDisplayName => _regularLoaded ? $"{UiFamily} Regular" : Ui;

	public static bool HasUiFont => _uiLoaded;

	public static bool HasMonoFont => _monoLoaded;

	public static void Register()
	{
		if (_registered)
		{
			return;
		}

		_registered = true;

		// MewUI 源码 的字族注册表只保存最后一个文件，Regular 必须最后注册。
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
