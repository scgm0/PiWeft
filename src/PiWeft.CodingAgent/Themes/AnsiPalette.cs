namespace PiWeft.CodingAgent.Themes;

/// <summary>
/// ANSI 256 色索引表（xterm 默认调色板）。pi 的主题可以写 <c>"39"</c> 这样的索引色，
/// 终端自己渲染它；桌面需要具体 RGB，因此这里内置同一套 xterm 取值。
/// </summary>
internal static class AnsiPalette
{
	/// <summary>6×6×6 立方体各级分量。</summary>
	private static readonly int[] CubeLevels = [0, 95, 135, 175, 215, 255];

	private static readonly ThemeColor[] Colors = Build();

	public static bool TryGet(int index, out ThemeColor color)
	{
		if ((uint)index < (uint)Colors.Length)
		{
			color = Colors[index];
			return true;
		}

		color = default;
		return false;
	}

	private static ThemeColor[] Build()
	{
		var table = new ThemeColor[256];

		// 基础 16 色（xterm 默认）。
		ReadOnlySpan<uint> basic =
		[
			0x000000, 0xcd0000, 0x00cd00, 0xcdcd00, 0x0000ee, 0xcd00cd, 0x00cdcd, 0xe5e5e5,
			0x7f7f7f, 0xff0000, 0x00ff00, 0xffff00, 0x5c5cff, 0xff00ff, 0x00ffff, 0xffffff,
		];

		for (var index = 0; index < basic.Length; index++)
		{
			table[index] = FromRgb(basic[index]);
		}

		// 16-231：6×6×6 色彩立方。
		var slot = 16;
		foreach (var r in CubeLevels)
		{
			foreach (var g in CubeLevels)
			{
				foreach (var b in CubeLevels)
				{
					table[slot++] = new ThemeColor((byte)r, (byte)g, (byte)b);
				}
			}
		}

		// 232-255：24 级灰阶。
		for (var index = 0; index < 24; index++)
		{
			var value = (byte)(8 + (10 * index));
			table[232 + index] = new ThemeColor(value, value, value);
		}

		return table;
	}

	private static ThemeColor FromRgb(uint value) =>
		new((byte)(value >> 16), (byte)(value >> 8), (byte)value);
}
