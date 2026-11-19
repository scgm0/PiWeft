namespace PiWeft.Desktop;

internal static class ReadingPreferences
{
	public static double FontSize { get; private set; } = 16;
	public static double Width { get; private set; } = 1060;

	/// 正文行高倍率（1.6 / 1.75 / 1.9）。
	public static double LineHeight { get; private set; } = 1.6;
	public static event Action? Changed;

	public static void Initialize(DesktopPreference preference)
	{
		FontSize = double.IsFinite(preference.ChatFontSize) ? Math.Clamp(preference.ChatFontSize, 14, 22) : 16;
		Width = double.IsFinite(preference.ReadingWidth) ? Math.Clamp(preference.ReadingWidth, 760, 1360) : 1060;
		LineHeight = double.IsFinite(preference.ChatLineHeight) ? Math.Clamp(preference.ChatLineHeight, 1.6, 1.9) : 1.6;
	}

	public static void Apply(DesktopPreference preference)
	{
		var oldFont = FontSize;
		var oldWidth = Width;
		var oldLineHeight = LineHeight;
		Initialize(preference);
		if (oldFont != FontSize || oldWidth != Width || oldLineHeight != LineHeight) Changed?.Invoke();
	}
}
