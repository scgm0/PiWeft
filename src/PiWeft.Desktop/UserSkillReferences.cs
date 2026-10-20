using System.Text.RegularExpressions;

namespace PiWeft.Desktop;

/// <summary>
/// 解析草稿和用户消息里的 <c>/skill:名称</c> 技能调用，供展示层画成行内技能 chip。
/// 规则对齐 PiDeck：前置字符不能是路径分隔符或标识符字符，token 后不能紧跟 <c>/</c>，
/// 代码块、行内代码和 URL 里的同名文本保持纯文本。
/// </summary>
internal static partial class UserSkillReferences
{
	[GeneratedRegex("""(?<![:/.\w#!~])/skill:(?<name>[\p{L}\p{N}_-]+)""", RegexOptions.CultureInvariant)]
	private static partial Regex SkillToken();

	public static IReadOnlyList<UserSkillReference> Parse(string text)
	{
		if (text.Length == 0 || !text.Contains("/skill:", StringComparison.Ordinal)) return [];

		var protectedSpans = UserFileReferences.ProtectedSpans(text);
		var skills = new List<UserSkillReference>();
		foreach (Match match in SkillToken().Matches(text))
		{
			var start = match.Index;
			var end = start + match.Length;
			// /skill:x/y 更像路径片段，不当作技能调用。
			if (end < text.Length && text[end] == '/') continue;
			if (Overlaps(start, end, protectedSpans)) continue;
			skills.Add(new UserSkillReference(match.Groups["name"].Value, start, end));
		}
		return skills;
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
