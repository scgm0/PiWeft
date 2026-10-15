namespace PiWeft.Desktop;

/// <summary>Reading metrics are cached once, then published on explicit settings changes, never loaded per token.</summary>
internal static class ReadingPreferences
{
	public static double FontSize { get; private set; } = 16;
	public static double Width { get; private set; } = 1060;
	public static event Action? Changed;

	public static void Initialize(DesktopPreference preference)
	{
		FontSize = double.IsFinite(preference.ChatFontSize) ? Math.Clamp(preference.ChatFontSize, 14, 22) : 16;
		Width = double.IsFinite(preference.ReadingWidth) ? Math.Clamp(preference.ReadingWidth, 760, 1360) : 1060;
	}

	public static void Apply(DesktopPreference preference)
	{
		var oldFont = FontSize;
		var oldWidth = Width;
		Initialize(preference);
		if (oldFont != FontSize || oldWidth != Width) Changed?.Invoke();
	}
}
