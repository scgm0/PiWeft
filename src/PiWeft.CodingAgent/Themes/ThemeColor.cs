namespace PiWeft.CodingAgent.Themes;

/// 一个不透明的 sRGB 颜色。pi 的主题颜色没有 alpha 通道，<see cref="IsTerminalDefault"/>
/// 对应 pi 的空字符串（<c>""</c>）：「用终端自己的默认前景/背景色」，需要具体值时由调用方回退。
public readonly record struct ThemeColor(byte R, byte G, byte B, bool IsTerminalDefault = false)
{
	public static readonly ThemeColor TerminalDefault = new(0, 0, 0, true);

	public uint ToArgb() => 0xFF000000u | ((uint)R << 16) | ((uint)G << 8) | B;

	public string ToHex() => $"#{R:x2}{G:x2}{B:x2}";
}
