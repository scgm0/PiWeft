using System.Text;
using System.Text.RegularExpressions;

namespace PiWeft.CodingAgent.Tools;

/// glob 语法里两处需要「编译成正则」的地方（<c>find</c>/<c>grep --glob</c> 与 <c>.gitignore</c>）：
/// 两者都跟着 ripgrep/fd 背后的 globset/gitignore 语法走，因此共用同一份字符类规则，
/// 免得一边支持 <c>[!a]</c> 另一边把 <c>!</c> 当普通字符。
internal static class GlobSyntax
{
	/// 把 <c>[...]</c> 的内容（<paramref name="start"/> 是 '[' 之后，<paramref name="end"/> 是 ']' 的位置）
	/// 追加成 .NET 正则字符类。
	/// <para>
	/// gitignore 与 globset 都用 fnmatch 的写法：<c>!</c> 开头表示取反（<c>^</c> 也常见于手写规则）。
	/// .NET 正则的取反是 <c>^</c>，而且路径分隔符永远不该被字符类匹配到，所以取反类要补一个 <c>/</c>。
	/// </para>
	public static void AppendClass(StringBuilder builder, string pattern, int start, int end)
	{
		var index = start;
		var negated = false;
		if (index < end && (pattern[index] == '!' || pattern[index] == '^'))
		{
			negated = true;
			index++;
		}

		if (index >= end)
		{
			// 空类（"[]" / "[!]"）：按字面处理，避免生成非法正则。
			builder.Append(negated ? "\\[!" : "\\[");
			return;
		}

		builder.Append(negated ? "[^/" : "[");
		for (; index < end; index++)
		{
			var c = pattern[index];
			switch (c)
			{
				case '\\' when index + 1 < end:
					builder.Append('\\').Append(pattern[++index]);
					break;
				case '\\':
					builder.Append("\\\\");
					break;
				case '[':
				case ']':
					builder.Append('\\').Append(c);
					break;
				default:
					builder.Append(c);
					break;
			}
		}

		if (negated)
		{
			builder.Append('/');
		}

		builder.Append(']');
	}

	/// glob 的字符类结束位置；找不到时返回 -1（调用方按字面 '<c>[</c>' 处理）。
	public static int FindClassEnd(string pattern, int start, int end)
	{
		for (var index = start; index < end; index++)
		{
			if (pattern[index] == '\\')
			{
				index++;
				continue;
			}

			if (pattern[index] == ']')
			{
				return index;
			}
		}

		return -1;
	}

	/// 顶层 <c>{...}</c> 的结束位置（支持嵌套）；找不到时返回 -1。
	public static int FindBraceEnd(string pattern, int start, int end)
	{
		var depth = 1;
		for (var index = start; index < end; index++)
		{
			switch (pattern[index])
			{
				case '\\':
					index++;
					break;
				case '{':
					depth++;
					break;
				case '}':
					depth--;
					if (depth == 0)
					{
						return index;
					}

					break;
			}
		}

		return -1;
	}

	/// 按顶层逗号切分 <c>{a,b,c}</c> 的分支；没有逗号时只有一段。
	public static List<(int Start, int End)> SplitBraceAlternatives(string pattern, int start, int end)
	{
		var alternatives = new List<(int, int)>();
		var depth = 0;
		var segment = start;
		for (var index = start; index < end; index++)
		{
			switch (pattern[index])
			{
				case '\\':
					index++;
					break;
				case '{':
					depth++;
					break;
				case '}':
					depth--;
					break;
				case ',' when depth == 0:
					alternatives.Add((segment, index));
					segment = index + 1;
					break;
			}
		}

		alternatives.Add((segment, end));
		return alternatives;
	}

	/// 追加 <c>{a,b}</c> 的分支；每一支都按 glob 语法再编译一次（支持嵌套）。
	public static void AppendBraceAlternatives(
		StringBuilder builder,
		string pattern,
		int start,
		int end,
		Action<StringBuilder, string, int, int> appendGlob)
	{
		builder.Append("(?:");
		var alternatives = SplitBraceAlternatives(pattern, start, end);
		for (var index = 0; index < alternatives.Count; index++)
		{
			if (index > 0)
			{
				builder.Append('|');
			}

			appendGlob(builder, pattern, alternatives[index].Start, alternatives[index].End);
		}

		builder.Append(')');
	}

	/// fd 的 <c>--glob</c> 是 smart-case：pattern 里没有大写字母就大小写不敏感。
	/// ripgrep 的 <c>--glob</c> 则是大小写敏感，所以只有 find 用这个判定。
	public static bool IsSmartCaseInsensitive(string pattern) => !pattern.Any(char.IsUpper);

	/// Windows 上 fd 用 <c>[/\\]</c> 匹配两种分隔符；进程内的路径统一先换成 '/'，
	/// 因此这里保持一致即可：正则只认 '/'。
	public static string NormalizeSlashes(string path) => path.Replace('\\', '/');

	/// 供调用方构造超时正则：glob 展开后可能有嵌套分支，仍然给一个上限。
	public static Regex Compile(StringBuilder builder, bool ignoreCase)
	{
		// 生成的 glob 正则没有反向引用与环视，NonBacktracking 不会改变语义，
		// 但能挡住畸形 pattern 的指数回溯。
		var options = RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;
		if (ignoreCase)
		{
			options |= RegexOptions.IgnoreCase;
		}

		try
		{
			return new Regex(builder.ToString(), options, TimeSpan.FromSeconds(2));
		}
		catch (ArgumentException exception)
		{
			throw new InvalidOperationException($"Invalid glob pattern: {exception.Message}", exception);
		}
	}
}
