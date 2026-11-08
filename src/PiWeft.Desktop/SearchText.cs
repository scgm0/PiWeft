using System.Buffers;

namespace PiWeft.Desktop;

/// 选择器搜索：查询分词与文本归一化。
/// 归一化只留字母与数字并转小写，搜 <c>gpt4o</c> 与 <c>gpt-4o</c> 等价。
/// 文本归一化在快照期算一次；按键路径只做子串比对，不分配临时数组。
internal static class SearchText
{
	// 空白集合交给 SearchValues 走向量化查找；制表符与换行来自粘贴的多行查询。
	private static readonly SearchValues<char> Whitespace = SearchValues.Create([' ', '\t', '\n', '\r', '\f', '\v']);

	/// 把查询切成归一化后的词。空查询返回空数组，调用方据此跳过逐条比对。
	public static string[] Words(string? query)
	{
		if (string.IsNullOrWhiteSpace(query))
		{
			return [];
		}

		// 绝大多数查询只有一两个词；容量按此给，避免 List 扩容。
		var words = new List<string>(2);
		var span = query.AsSpan().Trim();
		while (!span.IsEmpty)
		{
			var cut = span.IndexOfAny(Whitespace);
			var word = cut < 0 ? span : span[..cut];
			if (!word.IsEmpty)
			{
				var normalized = Normalize(word);
				if (normalized.Length > 0)
				{
					words.Add(normalized);
				}
			}

			span = cut < 0 ? default : span[(cut + 1)..];
		}

		return [.. words];
	}

	/// 归一化：只保留字母与数字，统一小写。短片段的临时缓冲放栈上，长文本才上堆。
	public static string Normalize(ReadOnlySpan<char> value)
	{
		if (value.IsEmpty)
		{
			return string.Empty;
		}

		Span<char> buffer = value.Length <= 256 ? stackalloc char[value.Length] : new char[value.Length];
		var length = 0;
		foreach (var c in value)
		{
			if (char.IsLetterOrDigit(c))
			{
				buffer[length++] = char.ToLowerInvariant(c);
			}
		}

		return new string(buffer[..length]);
	}

	/// 每个词都是 <paramref name="haystack"/> 的子串。
	/// <paramref name="haystack"/> 与 <paramref name="words"/> 都必须已经过 <see cref="Normalize"/>。
	public static bool ContainsAll(string haystack, ReadOnlySpan<string> words)
	{
		foreach (var word in words)
		{
			if (!haystack.Contains(word, StringComparison.Ordinal))
			{
				return false;
			}
		}

		return true;
	}
}
