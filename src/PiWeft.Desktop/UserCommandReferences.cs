using System.Text.RegularExpressions;

namespace PiWeft.Desktop;

/// 解析草稿和用户消息里的提示词/斜杠命令调用，供展示层画成行内命令 chip。
/// 规则对齐 PiDeck：命令后不能紧跟 <c>/</c>（路径）或 <c>:</c>（<c>/skill:名称</c> 由技能解析器接管），
/// 前面不能是路径分隔符或标识符字符；代码块、行内代码和 URL 里的同名文本保持纯文本。
internal static partial class UserCommandReferences
{
	[GeneratedRegex("""(?<![:/.\w#!~])/(?<name>[\p{L}][\p{L}\p{N}_-]*)""", RegexOptions.CultureInvariant)]
	private static partial Regex CommandToken();

	public static IReadOnlyList<UserCommandReference> Parse(string text, IReadOnlySet<string>? commandNames)
	{
		if (commandNames is not { Count: > 0 } || !text.Contains('/')) return [];

		var protectedSpans = UserFileReferences.ProtectedSpans(text);
		var commands = new List<UserCommandReference>();
		foreach (Match match in CommandToken().Matches(text))
		{
			var name = match.Groups["name"].Value;
			if (!commandNames.Contains(name)) continue;

			var start = match.Index;
			var end = start + match.Length;
			// /cmd/x 是路径；/skill:名称 留给技能解析器，避免同一段文本出现两种 chip。
			if (end < text.Length && text[end] is '/' or ':') continue;
			if (Overlaps(start, end, protectedSpans)) continue;
			commands.Add(new UserCommandReference(name, start, end));
		}

		return commands;
	}

	private static bool Overlaps(int start, int end, IReadOnlyList<(int Start, int End)> spans)
	{
		foreach (var span in spans)
		{
			if (start < span.End && end > span.Start) return true;
		}

		return false;
	}
}
