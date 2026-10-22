using System.Globalization;

namespace PiWeft.Desktop;

/// <summary>
/// 终端里「一个码点占几格」的判定。终端缓冲区是按显示格寻址的，而输出流是 Unicode 文本：
/// 组合符号、变体选择符、ZWJ 链、肤色修饰、Hangul 中/终声都不该各占一格，否则列号会越算越偏。
/// <para>
/// 宽度口径对齐 wcwidth（东亚宽字符 2 格、组合符号 0 格）：应用侧（readline/zsh）也用 wcwidth 算光标，
/// 两边口径一致才不会有整行错位。emoji 变体选择符（U+FE0F）把窄字符画成两格宽的情况刻意不升级宽度，
/// 那属于字体呈现与 wcwidth 的固有分歧，硬改反而会和 shell 的列号打架。
/// </para>
/// </summary>
internal static class TerminalText
{
	/// <summary>零宽连接符：它把后面的字符并进同一格。</summary>
	internal const int Zwj = 0x200D;

	/// <summary>码点占几格：0 = 零宽续接，1 = 窄，2 = 宽。</summary>
	internal static int RuneWidth(int codepoint)
	{
		if (codepoint < 0x20 || codepoint == 0x7F)
		{
			return 0;
		}

		if (codepoint < 0x7F)
		{
			return 1;
		}

		// C1 控制字符与软连字符一类：不可见。
		if (codepoint < 0xA0)
		{
			return 0;
		}

		if (IsExtend(codepoint))
		{
			return 0;
		}

		return IsWide(codepoint) ? 2 : 1;
	}

	/// <summary>
	/// 零宽续接码元：它属于前一格那个文本元素，自己不开新格。
	/// 组合符号（é、越南语声调、阿拉伯音符）、变体选择符、ZWJ、肤色修饰、Hangul 中/终声、
	/// 标签字符（细分旗帜）、键帽组合都属于这一类。
	/// </summary>
	internal static bool IsExtend(int codepoint)
	{
		// 快速通道：U+0300 以下只有零宽空格/连接符这一小段是零宽，其余一律不是续接码元。
		if (codepoint < 0x300)
		{
			return codepoint is >= 0x200B and <= 0x200F;
		}

		if (codepoint == Zwj)
		{
			return true;
		}

		// U+200B–U+200F：零宽空格/连接符/方向标记；U+2060 词连接符。
		if (codepoint is >= 0x200B and <= 0x200F or 0x2060)
		{
			return true;
		}

		// U+FE00–U+FE0F 变体选择符，U+E0100–U+E01EF 补充变体选择符。
		if (codepoint is >= 0xFE00 and <= 0xFE0F or >= 0xE0100 and <= 0xE01EF)
		{
			return true;
		}

		// U+1F3FB–U+1F3FF 肤色修饰。
		if (codepoint is >= 0x1F3FB and <= 0x1F3FF)
		{
			return true;
		}

		// U+E0020–U+E007F 标签字符：细分旗帜（🏴 + 标签序列）。
		if (codepoint is >= 0xE0020 and <= 0xE007F)
		{
			return true;
		}

		// U+20E3 键帽组合。
		if (codepoint == 0x20E3)
		{
			return true;
		}

		// U+1160–U+11FF、U+D7B0–U+D7FF：Hangul 中声/终声，与前面的初声合成一个音节。
		if (codepoint is >= 0x1160 and <= 0x11FF or >= 0xD7B0 and <= 0xD7FF)
		{
			return true;
		}

		return CharUnicodeInfo.GetUnicodeCategory(codepoint) is
			UnicodeCategory.NonSpacingMark or
			UnicodeCategory.SpacingCombiningMark or
			UnicodeCategory.EnclosingMark;
	}

	/// <summary>区域指示符（U+1F1E6–U+1F1FF）：成对出现才是一个国旗。</summary>
	internal static bool IsRegionalIndicator(int codepoint) => codepoint is >= 0x1F1E6 and <= 0x1F1FF;

	/// <summary>东亚宽字符与 emoji：占两格。</summary>
	internal static bool IsWide(int codepoint) => codepoint switch
	{
		>= 0x1100 and <= 0x115F => true,
		>= 0x2E80 and <= 0xA4CF => true,
		>= 0xA960 and <= 0xA97F => true,
		>= 0xAC00 and <= 0xD7A3 => true,
		>= 0xF900 and <= 0xFAFF => true,
		>= 0xFE10 and <= 0xFE19 => true,
		>= 0xFE30 and <= 0xFE6F => true,
		>= 0xFF00 and <= 0xFF60 => true,
		>= 0xFFE0 and <= 0xFFE6 => true,
		>= 0x1F000 and <= 0x1F02F => true,
		>= 0x1F0A0 and <= 0x1F0FF => true,
		>= 0x1F100 and <= 0x1F1FF => true,
		>= 0x1F200 and <= 0x1F2FF => true,
		>= 0x1F300 and <= 0x1F5FF => true,
		>= 0x1F600 and <= 0x1F64F => true,
		>= 0x1F680 and <= 0x1F6FF => true,
		>= 0x1F7E0 and <= 0x1F7EB => true,
		>= 0x1F900 and <= 0x1F9FF => true,
		>= 0x1FA70 and <= 0x1FAFF => true,
		>= 0x20000 and <= 0x3FFFD => true,
		_ => false,
	};

	/// <summary>把一个簇（一个文本元素）按格数折回宽度；孤立组合符号按一格算，别让它凭空消失。</summary>
	internal static int ClusterWidth(ReadOnlySpan<char> cluster)
	{
		if (cluster.IsEmpty)
		{
			return 0;
		}

		var width = 0;
		var index = 0;
		while (index < cluster.Length)
		{
			index = NextCodepoint(cluster, index, out var codepoint);
			width += RuneWidth(codepoint);
		}

		return width > 2 ? 2 : width;
	}

	/// <summary>读出 <paramref name="index"/> 处的码点；孤立代理按 U+FFFD 处理，返回下一个位置。</summary>
	internal static int NextCodepoint(ReadOnlySpan<char> text, int index, out int codepoint)
	{
		var unit = text[index++];
		if (!char.IsHighSurrogate(unit))
		{
			codepoint = char.IsLowSurrogate(unit) ? 0xFFFD : unit;
			return index;
		}

		if (index < text.Length && char.IsLowSurrogate(text[index]))
		{
			codepoint = char.ConvertToUtf32(unit, text[index]);
			return index + 1;
		}

		codepoint = 0xFFFD;
		return index;
	}
}
