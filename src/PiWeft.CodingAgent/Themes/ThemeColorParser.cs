using System.Globalization;
using System.Text.RegularExpressions;

namespace PiWeft.CodingAgent.Themes;

/// <summary>
/// 解析 pi 主题里的颜色写法，六种形式与 <c>docs/themes.md</c> 一致：
/// 十六进制、<c>oklch(...)</c>、<c>okhsl(...)</c>、256 色索引、<c>vars</c> 变量引用、空字符串（终端默认）。
/// </summary>
public static partial class ThemeColorParser
{
	/// <summary>变量引用的最大解析深度，用于截断循环引用。</summary>
	private const int MaxDepth = 16;

	/// <summary>解析一个颜色值。空字符串表示「终端默认」，同样返回 <c>true</c>。</summary>
	public static bool TryParse(
		string? value,
		IReadOnlyDictionary<string, string>? variables,
		out ThemeColor color,
		out string? error) =>
		TryParse(value, variables, 0, out color, out error);

	private static bool TryParse(
		string? value,
		IReadOnlyDictionary<string, string>? variables,
		int depth,
		out ThemeColor color,
		out string? error)
	{
		color = default;
		error = null;

		if (depth > MaxDepth)
		{
			error = $"颜色变量引用的层级超过 {MaxDepth} 层（可能存在循环引用）";
			return false;
		}

		var text = value?.Trim() ?? string.Empty;
		if (text.Length == 0)
		{
			// pi 的 "" 表示用终端默认前景/背景色；这里用显式标志表示。
			color = ThemeColor.TerminalDefault;
			return true;
		}

		if (text[0] == '#')
		{
			return TryParseHex(text, out color, out error);
		}

		if (text.Length <= 3 && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var index))
		{
			if (!AnsiPalette.TryGet(index, out color))
			{
				error = $"ANSI 颜色索引必须在 0 到 255 之间：{text}";
				return false;
			}

			return true;
		}

		var oklch = OklchRegex().Match(text);
		if (oklch.Success)
		{
			return TryParseOklch(oklch, out color, out error);
		}

		var okhsl = OkhslRegex().Match(text);
		if (okhsl.Success)
		{
			return TryParseOkhsl(okhsl, out color, out error);
		}

		// 其余一律当作 vars 引用；未定义即主题无效（与 pi 一致）。
		if (variables is not null && variables.TryGetValue(text, out var referenced))
		{
			return TryParse(referenced, variables, depth + 1, out color, out error);
		}

		error = variables is null
			? $"无法解析的颜色值：{text}"
			: $"颜色或变量未定义：{text}";
		return false;
	}

	private static bool TryParseHex(string text, out ThemeColor color, out string? error)
	{
		color = default;
		error = null;
		var digits = text.AsSpan(1);

		// 与 pi 一致：只接受 3 位或 6 位十六进制。
		if (digits.Length is not (3 or 6))
		{
			error = $"十六进制颜色必须是 3 位或 6 位：{text}";
			return false;
		}

		Span<char> expanded = stackalloc char[6];
		if (digits.Length == 3)
		{
			for (var index = 0; index < 3; index++)
			{
				expanded[index * 2] = digits[index];
				expanded[(index * 2) + 1] = digits[index];
			}
		}
		else
		{
			digits.CopyTo(expanded);
		}

		if (!TryHexByte(expanded[..2], out var r) ||
			!TryHexByte(expanded[2..4], out var g) ||
			!TryHexByte(expanded[4..6], out var b))
		{
			error = $"十六进制颜色包含非十六进制字符：{text}";
			return false;
		}

		color = new ThemeColor(r, g, b);
		return true;
	}

	private static bool TryHexByte(ReadOnlySpan<char> digits, out byte value)
	{
		value = 0;
		foreach (var digit in digits)
		{
			var nibble = digit switch
			{
				>= '0' and <= '9' => digit - '0',
				>= 'a' and <= 'f' => digit - 'a' + 10,
				>= 'A' and <= 'F' => digit - 'A' + 10,
				_ => -1,
			};

			if (nibble < 0)
			{
				return false;
			}

			value = (byte)((value << 4) | nibble);
		}

		return true;
	}

	private static bool TryParseOklch(Match match, out ThemeColor color, out string? error)
	{
		color = default;
		error = null;

		var lightness = Number(match, "l");
		if (match.Groups["lpct"].Success)
		{
			lightness /= 100.0;
		}

		var chroma = Number(match, "c");
		var hue = Number(match, "h");

		if (lightness is < 0 or > 1)
		{
			error = $"oklch 的亮度必须在 0 到 1（或 0% 到 100%）之间：{match.Value}";
			return false;
		}

		if (chroma < 0)
		{
			error = $"oklch 的色度不能为负：{match.Value}";
			return false;
		}

		color = ColorSpaces.OklchToColor(lightness, chroma, hue);
		return true;
	}

	private static bool TryParseOkhsl(Match match, out ThemeColor color, out string? error)
	{
		color = default;
		error = null;

		var hue = Number(match, "h");
		var saturation = Number(match, "s");
		var lightness = Number(match, "l");

		if (match.Groups["spct"].Success)
		{
			saturation /= 100.0;
		}

		if (match.Groups["lpct"].Success)
		{
			lightness /= 100.0;
		}

		if (saturation is < 0 or > 1 || lightness is < 0 or > 1)
		{
			error = $"okhsl 的饱和度与亮度必须在 0 到 1（或 0% 到 100%）之间：{match.Value}";
			return false;
		}

		color = ColorSpaces.OkhslToColor(hue, saturation, lightness);
		return true;
	}

	private static double Number(Match match, string group) =>
		double.Parse(match.Groups[group].Value, NumberStyles.Float, CultureInfo.InvariantCulture);

	[GeneratedRegex(
		@"\Aoklch\(\s*(?<l>[+-]?(?:\d+(?:\.\d*)?|\.\d+)(?:e[+-]?\d+)?)(?<lpct>%)?\s+(?<c>[+-]?(?:\d+(?:\.\d*)?|\.\d+)(?:e[+-]?\d+)?)\s+(?<h>[+-]?(?:\d+(?:\.\d*)?|\.\d+)(?:e[+-]?\d+)?)(?:deg)?\s*\)\z",
		RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
	private static partial Regex OklchRegex();

	[GeneratedRegex(
		@"\Aokhsl\(\s*(?<h>[+-]?(?:\d+(?:\.\d*)?|\.\d+)(?:e[+-]?\d+)?)(?:deg)?\s+(?<s>[+-]?(?:\d+(?:\.\d*)?|\.\d+)(?:e[+-]?\d+)?)(?<spct>%)?\s+(?<l>[+-]?(?:\d+(?:\.\d*)?|\.\d+)(?:e[+-]?\d+)?)(?<lpct>%)?\s*\)\z",
		RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
	private static partial Regex OkhslRegex();
}
