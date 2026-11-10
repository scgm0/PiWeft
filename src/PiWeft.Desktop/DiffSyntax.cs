namespace PiWeft.Desktop;

/// 统一 diff 的行分类：元信息行与 <c>@@ -a,b +c,d @@</c> 头。
/// 判定按 <see cref="ReadOnlySpan{T}"/> 做，逐行调用也不分配：一个几千行的补丁在展开 diff 时
/// 要过一遍所有行，用 <c>Regex.Match</c> 每行都会产出一个 Match 与两个 Capture（实测 6.7k 行
/// 约 2.1 MB/5 轮、2.7 ms，改成手写 span 解析是 0 B、1.1 ms）。字符串实参经 C# 14 的
/// 一等 span 转换直接传入，调用点不需要写 <c>.AsSpan()</c>。
internal static class DiffSyntax
{
	public static bool IsMetadata(ReadOnlySpan<char> line) =>
		line.StartsWith("+++") || line.StartsWith("---") || line.StartsWith("diff ") ||
		line.StartsWith("index ") || (!line.IsEmpty && line[0] == '\\');

	/// 解析 hunk 头两侧的行数（头部省略 <c>,数量</c> 时为 1）；认不出来时返回 (0, 0)。
	/// 行数用来按 hunk 消费内容行：内容行本身以 <c>--</c> / <c>++</c> 开头时，
	/// 只看前缀无法与文件头区分。
	public static (int OldCount, int NewCount) HunkLengths(ReadOnlySpan<char> line)
	{
		if (!line.StartsWith("@@ -")) return (0, 0);
		var index = 4;
		if (!TryReadNumber(line, ref index, out _)) return (0, 0);
		var oldCount = 1;
		if (index < line.Length && line[index] == ',')
		{
			index++;
			if (!TryReadNumber(line, ref index, out oldCount)) return (0, 0);
		}

		if (index + 1 >= line.Length || line[index] != ' ' || line[index + 1] != '+') return (0, 0);
		index += 2;
		if (!TryReadNumber(line, ref index, out _)) return (0, 0);
		var newCount = 1;
		if (index < line.Length && line[index] == ',')
		{
			index++;
			if (!TryReadNumber(line, ref index, out newCount)) return (0, 0);
		}

		return (oldCount, newCount);
	}

	/// 解析 hunk 头的两侧起始行号；认不出来时返回兜底值（行号列最多显示得不准，不会崩）。
	/// 形状：<c>@@ -旧[,数] +新[,数] @@</c>，尾部可能还有上下文（函数名等）。
	public static (int OldLine, int NewLine) ParseHunkHeader(ReadOnlySpan<char> line, int fallbackOld, int fallbackNew)
	{
		if (!TryParseHunkHeader(line, out var oldLine, out var newLine))
		{
			return (fallbackOld, fallbackNew);
		}

		return (oldLine, newLine);
	}

	private static bool TryParseHunkHeader(ReadOnlySpan<char> line, out int oldLine, out int newLine)
	{
		oldLine = 0;
		newLine = 0;
		if (!line.StartsWith("@@ -"))
		{
			return false;
		}

		var index = 4;
		if (!TryReadNumber(line, ref index, out oldLine))
		{
			return false;
		}

		// 行数只在 @@ -a,b 里出现，值的绝对值与本视图无关（行号从起点逐行累加），读掉即可。
		if (index < line.Length && line[index] == ',')
		{
			index++;
			if (!TryReadNumber(line, ref index, out _))
			{
				return false;
			}
		}

		if (index + 1 >= line.Length || line[index] != ' ' || line[index + 1] != '+')
		{
			return false;
		}

		index += 2;
		if (!TryReadNumber(line, ref index, out newLine))
		{
			return false;
		}

		if (index < line.Length && line[index] == ',')
		{
			index++;
			if (!TryReadNumber(line, ref index, out _))
			{
				return false;
			}
		}

		return index + 2 < line.Length && line[index] == ' ' && line[index + 1] == '@' && line[index + 2] == '@';
	}

	private static bool TryReadNumber(ReadOnlySpan<char> line, ref int index, out int value)
	{
		value = 0;
		var start = index;
		while (index < line.Length && char.IsAsciiDigit(line[index]))
		{
			var digit = line[index] - '0';
			// 越界就当作认不出来：补丁里的行号不会到这个量级，坏了也不能溢出成负数。
			if (value > (int.MaxValue - digit) / 10)
			{
				index = start;
				return false;
			}

			value = value * 10 + digit;
			index++;
		}

		return index > start;
	}
}
