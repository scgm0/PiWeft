using Aprillz.MewUI;

namespace PiWeft.Desktop;

/// <summary>
/// 一套配色。「织机」设计语言（见仓库根 DESIGN.md）：暖墨色底 + 黄铜单强调色，
/// 深色与浅色各一份，由 <see cref="AppTheme.For"/> 按当前主题解析 ——
/// 样式 setter 与运行时取值共用同一份定义。
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

	/// <summary>深色配色：中性石墨分层，白色主按钮 —— 高级感来自克制的冷黑与高对比白字。</summary>
	public static AppPalette Dark { get; } = new()
	{
		WindowBackground = new Color(0x14, 0x14, 0x14),
		SidebarBackground = new Color(0x0f, 0x0f, 0x0f),
		CardBackground = new Color(0x1d, 0x1d, 0x1d),
		CardBackgroundAlt = new Color(0x25, 0x25, 0x25),
		HoverBackground = new Color(0x2b, 0x2b, 0x2b),
		CodeBackground = new Color(0x0f, 0x0f, 0x0f),
		CodeText = new Color(0xe6, 0xe6, 0xe6),
		TextPrimary = new Color(0xf5, 0xf5, 0xf5),
		TextMuted = new Color(0xa6, 0xa6, 0xa6),
		TextSubtle = new Color(0x7d, 0x7d, 0x7d),
		TextFaint = new Color(0x54, 0x54, 0x54),
		TextOnAccent = new Color(0x14, 0x14, 0x14),
		Accent = new Color(0xfa, 0xfa, 0xfa),
		AccentSoft = new Color(0x2f, 0x2f, 0x2f),
		Border = new Color(0x2c, 0x2c, 0x2c),
		BorderSubtle = new Color(0x20, 0x20, 0x20),
		Error = new Color(0xf8, 0x71, 0x71),
		Success = new Color(0x4a, 0xde, 0x80),
		Info = new Color(0x60, 0xa5, 0xfa),
		Thinking = new Color(0xa7, 0x8b, 0xfa),
		Transparent = new Color(0x00, 0x00, 0x00, 0x00),
	};

	/// <summary>浅色配色：中性灰阶 + 黑色主按钮，纯白卡片。</summary>
	public static AppPalette Light { get; } = new()
	{
		WindowBackground = new Color(0xfa, 0xfa, 0xfa),
		SidebarBackground = new Color(0xf2, 0xf2, 0xf2),
		CardBackground = new Color(0xff, 0xff, 0xff),
		CardBackgroundAlt = new Color(0xf0, 0xf0, 0xf0),
		HoverBackground = new Color(0xe5, 0xe5, 0xe5),
		CodeBackground = new Color(0x17, 0x17, 0x17),
		CodeText = new Color(0xe6, 0xe6, 0xe6),
		TextPrimary = new Color(0x17, 0x17, 0x17),
		TextMuted = new Color(0x52, 0x52, 0x52),
		TextSubtle = new Color(0x73, 0x73, 0x73),
		TextFaint = new Color(0xa3, 0xa3, 0xa3),
		TextOnAccent = new Color(0xfa, 0xfa, 0xfa),
		Accent = new Color(0x17, 0x17, 0x17),
		AccentSoft = new Color(0xe8, 0xe8, 0xe8),
		Border = new Color(0xe0, 0xe0, 0xe0),
		BorderSubtle = new Color(0xeb, 0xeb, 0xeb),
		Error = new Color(0xb4, 0x23, 0x18),
		Success = new Color(0x14, 0x70, 0x3a),
		Info = new Color(0x1d, 0x4e, 0xd8),
		Thinking = new Color(0x6d, 0x28, 0xd9),
		Transparent = new Color(0x00, 0x00, 0x00, 0x00),
	};
}
