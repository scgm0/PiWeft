using Aprillz.MewUI;

namespace PiWeft.Desktop;

/// <summary>
/// 一套配色。取自 PiDeck 的设计令牌（<c>foundation.css</c>），深色与浅色各一份，
/// 由 <see cref="AppTheme.For"/> 按当前主题解析 —— 样式 setter 与运行时取值共用同一份定义。
/// </summary>
public sealed record AppPalette
{
	// 背景层次
	public required Color WindowBackground { get; init; }

	public required Color SidebarBackground { get; init; }

	public required Color CardBackground { get; init; }

	public required Color CardBackgroundAlt { get; init; }

	public required Color HoverBackground { get; init; }

	public required Color CodeBackground { get; init; }

	public required Color CodeText { get; init; }

	// 文本
	public required Color TextPrimary { get; init; }

	public required Color TextMuted { get; init; }

	public required Color TextSubtle { get; init; }

	public required Color TextFaint { get; init; }

	public required Color TextOnAccent { get; init; }

	// 强调与状态
	public required Color Accent { get; init; }

	public required Color AccentSoft { get; init; }

	public required Color Border { get; init; }

	public required Color BorderSubtle { get; init; }

	public required Color Error { get; init; }

	public required Color Success { get; init; }

	public required Color Info { get; init; }

	public required Color Thinking { get; init; }

	public required Color Transparent { get; init; }

	/// <summary>深色配色。</summary>
	public static AppPalette Dark { get; } = new()
	{
		WindowBackground = new Color(0x1e, 0x1e, 0x1e),
		SidebarBackground = new Color(0x1b, 0x1b, 0x1b),
		CardBackground = new Color(0x24, 0x24, 0x24),
		CardBackgroundAlt = new Color(0x30, 0x30, 0x30),
		HoverBackground = new Color(0x32, 0x32, 0x32),
		CodeBackground = new Color(0x11, 0x14, 0x18),
		CodeText = new Color(0xd1, 0xd5, 0xdb),
		TextPrimary = new Color(0xec, 0xec, 0xe7),
		TextMuted = new Color(0xb8, 0xb8, 0xb2),
		TextSubtle = new Color(0x9a, 0x9a, 0x94),
		TextFaint = new Color(0xa0, 0xa0, 0x9a),
		TextOnAccent = new Color(0x12, 0x12, 0x12),
		Accent = new Color(0xfa, 0xfa, 0xfa),
		AccentSoft = new Color(0x2a, 0x2a, 0x2a),
		Border = new Color(0x38, 0x38, 0x38),
		BorderSubtle = new Color(0x2a, 0x2a, 0x2a),
		Error = new Color(0xf8, 0x71, 0x71),
		Success = new Color(0x4a, 0xde, 0x80),
		Info = new Color(0x60, 0xa5, 0xfa),
		Thinking = new Color(0x81, 0x8c, 0xf8),
		Transparent = new Color(0x00, 0x00, 0x00, 0x00),
	};

	/// <summary>浅色配色。</summary>
	public static AppPalette Light { get; } = new()
	{
		WindowBackground = new Color(0xff, 0xff, 0xff),
		SidebarBackground = new Color(0xff, 0xff, 0xff),
		CardBackground = new Color(0xff, 0xff, 0xff),
		CardBackgroundAlt = new Color(0xf4, 0xf4, 0xf5),
		HoverBackground = new Color(0xe5, 0xe7, 0xeb),
		CodeBackground = new Color(0x11, 0x14, 0x18),
		CodeText = new Color(0xd1, 0xd5, 0xdb),
		TextPrimary = new Color(0x20, 0x21, 0x24),
		TextMuted = new Color(0x4b, 0x55, 0x63),
		TextSubtle = new Color(0x63, 0x6b, 0x79),
		TextFaint = new Color(0x9f, 0xa3, 0xac),
		TextOnAccent = new Color(0xff, 0xff, 0xff),
		Accent = new Color(0x18, 0x18, 0x1b),
		AccentSoft = new Color(0xe5, 0xe7, 0xeb),
		Border = new Color(0xdf, 0xdf, 0xdf),
		BorderSubtle = new Color(0xe5, 0xe5, 0xe5),
		Error = new Color(0xb4, 0x23, 0x18),
		Success = new Color(0x14, 0x70, 0x3a),
		Info = new Color(0x1d, 0x4e, 0xd8),
		Thinking = new Color(0x63, 0x66, 0xf1),
		Transparent = new Color(0x00, 0x00, 0x00, 0x00),
	};
}
