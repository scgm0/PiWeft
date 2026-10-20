using System.Text;
using System.Text.RegularExpressions;

namespace PiWeft.Desktop;

/// <summary>用户消息 / 草稿里的一个文件引用：展示短文件名，完整路径留在原文与 tooltip 里。</summary>
internal sealed record UserFileReference(string Path, string Label, bool IsDirectory, int Start, int End) : IUserChip
{
	/// <summary>展示用前缀；文件引用保留 @，和 PiDeck 的 file chip 一致。</summary>
	public string Display => "@" + Label;

	public UserChipKind Kind => UserChipKind.File;

	public string Value => Path;
}

/// <summary>
/// 解析草稿和用户消息里的 <c>@"path"</c> / <c>@path</c> 引用。
/// 只做展示层识别，不改变发送给模型的原文；代码块、行内代码和 URL 里的 @ 保持纯文本。
/// </summary>
internal static partial class UserFileReferences
{
	// 文件 chip 只认两种写法：
	// 1. 拖拽/粘贴插入的 @"path with spaces"（ComposerDrop.Reference 会转义 \ 和 "）；
	// 2. 用户手打的 @relative/path 或 @/absolute/path。
	// 前面的字符排除 URL 分隔符、邮箱/句柄和引号，避免把代码里的 "@scope/pkg" 当成文件。
	[GeneratedRegex("""(?<![A-Za-z0-9_:/.\\#!~"'=])@(?:"(?<quoted>(?:\\.|[^"\\])*)"|(?<plain>[^\s@"<>]+))""", RegexOptions.CultureInvariant)]
	private static partial Regex ReferenceToken();

	[GeneratedRegex("https?://\\S+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
	private static partial Regex Url();

	private static readonly char[] TrailingPunctuation =
		['.', ',', ';', ':', '!', '?', '，', '。', '；', '：', '！', '？'];

	public static IReadOnlyList<UserFileReference> Parse(string text)
	{
		if (text.Length == 0 || !text.Contains('@')) return [];

		var protectedSpans = ProtectedSpans(text);
		var references = new List<UserFileReference>();
		foreach (Match match in ReferenceToken().Matches(text))
		{
			var start = match.Index;
			var end = start + match.Length;
			if (Overlaps(start, end, protectedSpans)) continue;

			string path;
			if (match.Groups["quoted"].Success)
			{
				path = UnescapeQuoted(match.Groups["quoted"].Value);
			}
			else
			{
				var plain = match.Groups["plain"].Value;
				var trimmed = plain.TrimEnd(TrailingPunctuation);
				if (!LooksLikePath(trimmed)) continue;
				path = trimmed;
				end = start + 1 + trimmed.Length;
			}

			if (path.Length == 0) continue;
			var isDirectory = path.EndsWith('/') || path.EndsWith('\\');
			references.Add(new UserFileReference(path, DisplayLabel(path, isDirectory), isDirectory, start, end));
		}

		return references;
	}

	private static string DisplayLabel(string path, bool isDirectory)
	{
		var normalized = path.Replace('\\', '/');
		var trimmed = normalized.TrimEnd('/');
		if (trimmed.Length == 0) return "/";
		var slash = trimmed.LastIndexOf('/');
		var name = slash >= 0 ? trimmed[(slash + 1)..] : trimmed;
		if (name.Length == 0) name = trimmed;
		return isDirectory ? name + "/" : name;
	}

	private static bool LooksLikePath(string path)
	{
		if (path.Length == 0) return false;
		if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
			path.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return false;
		if (path.EndsWith('/') || path.EndsWith('\\')) return true;
		var absolute = path.StartsWith('/') || path.StartsWith("~/", StringComparison.Ordinal) ||
			path.StartsWith("./", StringComparison.Ordinal) || path.StartsWith("../", StringComparison.Ordinal) ||
			(path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && (path[2] == '/' || path[2] == '\\'));
		// @scope/pkg 这类代码标识符没有点也没有绝对前缀，不当作文件；@README.md 和 @src/App.cs 则命中。
		return absolute || path.Contains('.');
	}

	private static string UnescapeQuoted(string value)
	{
		if (!value.Contains('\\')) return value;
		var builder = new StringBuilder(value.Length);
		for (var index = 0; index < value.Length; index++)
		{
			var current = value[index];
			if (current == '\\' && index + 1 < value.Length && (value[index + 1] == '\\' || value[index + 1] == '"'))
			{
				builder.Append(value[++index]);
				continue;
			}
			builder.Append(current);
		}
		return builder.ToString();
	}

	private static bool Overlaps(int start, int end, IReadOnlyList<(int Start, int End)> spans)
	{
		foreach (var span in spans)
		{
			if (start < span.End && end > span.Start) return true;
		}
		return false;
	}

	/// <summary>URL、围栏代码块和行内代码里的 @ 不参与引用识别。</summary>
	internal static List<(int Start, int End)> ProtectedSpans(string text)
	{
		var spans = new List<(int Start, int End)>();
		foreach (Match match in Url().Matches(text))
		{
			spans.Add((match.Index, match.Index + match.Length));
		}

		var fenced = new List<(int Start, int End)>();
		var lineStart = 0;
		while (lineStart < text.Length)
		{
			var lineEnd = text.IndexOf('\n', lineStart);
			if (lineEnd < 0) lineEnd = text.Length;
			var line = text.AsSpan(lineStart, lineEnd - lineStart);
			var trimmed = line.TrimStart([' ', '\t']);
			if (trimmed.Length >= 3 && (trimmed[0] == '`' || trimmed[0] == '~'))
			{
				var fence = trimmed[0];
				var count = 0;
				while (count < trimmed.Length && trimmed[count] == fence) count++;
				if (count >= 3)
				{
					var close = text.Length;
					var scan = lineEnd < text.Length ? lineEnd + 1 : text.Length;
					while (scan <= text.Length)
					{
						var nextEnd = text.IndexOf('\n', scan);
						if (nextEnd < 0) nextEnd = text.Length;
						var candidate = text.AsSpan(scan, nextEnd - scan).TrimStart([' ', '\t']);
						var run = 0;
						while (run < candidate.Length && candidate[run] == fence) run++;
						if (run >= count)
						{
							close = nextEnd;
							break;
						}
						if (nextEnd == text.Length) break;
						scan = nextEnd + 1;
					}
					fenced.Add((lineStart, close));
					spans.Add((lineStart, close));
					lineStart = close < text.Length ? close + 1 : text.Length;
					continue;
				}
			}
			lineStart = lineEnd < text.Length ? lineEnd + 1 : text.Length;
		}

		// 行内代码：成对的同长度反引号。围栏块内部已经整体跳过。
		for (var index = 0; index < text.Length;)
		{
			if (text[index] != '`' || Inside(fenced, index))
			{
				index++;
				continue;
			}
			var run = 0;
			while (index + run < text.Length && text[index + run] == '`') run++;
			var close = text.IndexOf(new string('`', run), index + run, StringComparison.Ordinal);
			if (close < 0)
			{
				index += run;
				continue;
			}
			spans.Add((index, close + run));
			index = close + run;
		}

		spans.Sort(static (left, right) => left.Start.CompareTo(right.Start));
		return spans;
	}

	private static bool Inside(IReadOnlyList<(int Start, int End)> spans, int index)
	{
		foreach (var span in spans)
		{
			if (index >= span.Start && index < span.End) return true;
		}
		return false;
	}
}
