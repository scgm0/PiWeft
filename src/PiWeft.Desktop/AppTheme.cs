using Aprillz.MewUI;

namespace PiWeft.Desktop;

/// <summary>
/// 桌面端的视觉常量：颜色转发到 <see cref="AppPalette"/>（按当前主题解析），尺寸是固定值。
/// </summary>
/// <remarks>
/// 颜色在这里读取是**即时**的：界面里长期存在的容器应该用 <see cref="AppStyles"/> 的命名样式，
/// 让 MewUI 在主题变化时重新解析（不必重建界面）；这里的属性适合构建瞬间就要定色的内容，
/// 例如消息气泡、图标与语法高亮。
/// </remarks>
public static class AppTheme
{
	/// <summary>
	/// 是否使用浅色配色。应用运行期间读取当前生效的主题（因此运行时切换主题后重新取色即可跟随）；
	/// 启动之前只有偏好可用，此时退回 <see cref="ThemeManager.Default"/>。
	/// </summary>
	private static bool IsLight => Application.IsRunning
		? !Application.Current.Theme.IsDark
		: ThemeManager.Default == ThemeVariant.Light;

	/// <summary>当前是否浅色配色（供诊断输出与语法配色使用）。</summary>
	public static bool IsLightTheme => IsLight;

	/// <summary>用户自定义主题的配色覆盖；为 null 时使用内建配色。</summary>
	private static AppPalette? _customLight;
	private static AppPalette? _customDark;

	/// <summary>
	/// 应用自定义主题配色（两个方向都可为空）。传 null 恢复内建深浅配色。
	/// 命名样式通过 <see cref="For"/> 解析颜色，因此换肤不需要重建界面。
	/// </summary>
	public static void UseCustomPalette(AppPalette? light, AppPalette? dark)
	{
		_customLight = light;
		_customDark = dark;
	}

	/// <summary>按主题解析配色；样式 setter 用它，因此换肤无需重建界面。</summary>
	public static AppPalette For(Theme theme) => theme.IsDark
		? _customDark ?? AppPalette.Dark
		: _customLight ?? AppPalette.Light;

	/// <summary>Use the application colors for framework controls, menus and selection surfaces.</summary>
	public static ThemeSeed Seed(AppPalette palette) => new()
	{
		WindowBackground = palette.WindowBackground,
		WindowText = palette.TextPrimary,
		ControlBackground = palette.CardBackground,
		ButtonFace = palette.CardBackgroundAlt,
		ButtonDisabledBackground = palette.HoverBackground,
		Error = palette.Error,
	};

	/// <summary>当前生效的配色。</summary>
	public static AppPalette Current => IsLight
		? _customLight ?? AppPalette.Light
		: _customDark ?? AppPalette.Dark;

	// ------------------------------------------------------------ 颜色转发
	public static Color WindowBackground => Current.WindowBackground;

	public static Color SidebarBackground => Current.SidebarBackground;

	public static Color CardBackground => Current.CardBackground;

	public static Color CardBackgroundAlt => Current.CardBackgroundAlt;

	public static Color HoverBackground => Current.HoverBackground;

	public static Color CodeBackground => Current.CodeBackground;

	public static Color CodeText => Current.CodeText;

	public static Color TerminalBackground => Current.TerminalBackground;

	public static Color TerminalForeground => Current.TerminalForeground;

	public static Color TerminalBorder => Current.TerminalBorder;

	public static Color TerminalSelection => Current.TerminalSelection;

	public static Color TerminalCursor => Current.TerminalCursor;

	public static Color TerminalCursorUnfocused => Current.TerminalCursorUnfocused;

	public static Color TerminalLink => Current.TerminalLink;

	public static Color TextPrimary => Current.TextPrimary;

	public static Color TextMuted => Current.TextMuted;

	public static Color TextSubtle => Current.TextSubtle;

	public static Color TextFaint => Current.TextFaint;

	public static Color TextOnAccent => Current.TextOnAccent;

	public static Color Accent => Current.Accent;

	public static Color AccentSoft => Current.AccentSoft;

	public static Color Border => Current.Border;

	public static Color BorderSubtle => Current.BorderSubtle;

	public static Color Error => Current.Error;

	public static Color Success => Current.Success;

	public static Color Info => Current.Info;

	public static Color Thinking => Current.Thinking;

	public static Color Transparent => Current.Transparent;

	// -------------------------------------------------------------------- 尺寸
	// 「织机」形制（DESIGN.md）：小圆角，表面靠色阶与发丝线区分，最大圆角 10。
	public const double RadiusXs = 4;
	public const double RadiusSm = 6;
	public const double RadiusMd = 8;
	public const double RadiusLg = 10;
	public const double RadiusXl = 10;

	/// <summary>签名细节「纬线」的宽度：活动会话、活动标签、用户消息左缘的强调短线。</summary>
	public const double ThreadWidth = 2;

	public const double Space1 = 4;
	public const double Space2 = 8;
	public const double Space3 = 12;
	public const double Space4 = 16;
	public const double Space6 = 24;

	public const double ControlHeightSm = 28;
	public const double ControlHeightMd = 32;
	public const double ControlHeightLg = 36;

	public const double SidebarWidth = 280;
	public const double ComposerHeight = 64;

	/// <summary>
	/// 右侧功能面板的默认宽度与可拖拽范围：面板页签与窗口按钮共用标题栏那一行，
	/// 所以下限要留出「三段页签 + 窗口按钮」的宽度；对话区保底见下方。
	/// </summary>
	public const double RightPanelWidth = 420;
	public const double RightPanelMinWidth = 360;
	public const double RightPanelMaxWidth = 720;

	/// <summary>拖拽右侧面板时主区必须保留的最小宽度（PiDeck 的 chat 下限是 360）。</summary>
	public const double MinChatWidth = 400;

	/// <summary>自定义窗框标题栏的高度（DIP）。Windows/macOS 由系统原生按钮，X11 由应用绘制。</summary>
	public const double TitleBarHeight = 40;

	/// <summary>标题栏窗口按钮的宽度：与标题栏等高、三键等宽、贴右缘。</summary>
	public const double TitleBarButtonWidth = 46;
	public static double MessageMaxWidth => ReadingPreferences.Width;

	// 正文对齐 PiDeck 16px；辅助信息提高一档，避免大屏过小。
	public const double FontSizeMicro = 12;
	public const double FontSizeCaption = 13;
	public const double FontSizeControl = 14;
	public static double FontSizeChat => ReadingPreferences.FontSize;
	public const double FontSizeTitle = 17;
	public const double FontSizeBrand = 18;

	/// <summary>等宽字体族：优先内嵌的 Maple Mono，注册失败时退回系统等宽字体。</summary>
	public static string MonoFont => AppFonts.Mono;
}
