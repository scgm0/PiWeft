namespace PiWeft.CodingAgent;

/// 模型正文里的 fenced code block 扫描：格式示例里出现的标记不算真标记。
/// 只按行判断，不解析 Markdown；```/~~~ 成对切换，未闭合的围栏之后全部视为代码。
/// 用 `ref struct` 枚举行，长回复不再为每一行分配字符串（`Split('\n')` 的旧实现）。
internal static class MarkdownScan
{
	/// 围栏行最多允许三个前导空格；再多是缩进代码里的内容，不是围栏。
	internal static bool IsFenceLine(ReadOnlySpan<char> line)
	{
		var indent = 0;
		while (indent < line.Length && line[indent] == ' ')
		{
			indent++;
		}

		if (indent > 3)
		{
			return false;
		}

		var rest = line[indent..];
		return rest.StartsWith("```", StringComparison.Ordinal) ||
			rest.StartsWith("~~~", StringComparison.Ordinal);
	}

	/// 逐行枚举围栏之外的行；围栏行本身不返回。
	public static LineEnumerator Lines(ReadOnlySpan<char> text) => new(text);

	/// 行枚举器。`Current` 是原文切片，调用方只在循环体内使用它。
	public ref struct LineEnumerator
	{
		private ReadOnlySpan<char> _remaining;
		private bool _inFence;

		public LineEnumerator(ReadOnlySpan<char> text)
		{
			_remaining = text;
			_inFence = false;
			Current = default;
		}

		public ReadOnlySpan<char> Current { get; private set; }

		public readonly LineEnumerator GetEnumerator() => this;

		public bool MoveNext()
		{
			while (!_remaining.IsEmpty)
			{
				var newline = _remaining.IndexOf('\n');
				var line = newline < 0 ? _remaining : _remaining[..newline];
				_remaining = newline < 0 ? default : _remaining[(newline + 1)..];

				if (IsFenceLine(line))
				{
					_inFence = !_inFence;
					continue;
				}

				if (!_inFence)
				{
					Current = line;
					return true;
				}
			}

			Current = default;
			return false;
		}
	}
}
