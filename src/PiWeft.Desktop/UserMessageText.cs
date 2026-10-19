using System.Text.RegularExpressions;

namespace PiWeft.Desktop;

/// <summary>用户消息里的技能上下文块：展示成 chip，正文仍完整发送给模型。</summary>
internal sealed record UserSkillChip(string Name, string Location);

/// <summary>
/// 把用户消息拆成「技能 chip + 文件引用 chip + 正文」。技能命令展开后会插入一整段
/// <c>&lt;skill name="..."&gt;...&lt;/skill&gt;</c>，那是模型上下文，不是用户写的话；
/// 直接铺在气泡里会把一条普通提问变成几百行 XML。文件引用则原样保留在正文里，
/// 由 <see cref="UserReferenceRichText"/> 的投影层在行内显示成 chip。
/// </summary>
internal static partial class UserMessageText
{
	private const int CollapsedLines = 8;

	// 技能块由 Skills.ExpandCommand 生成；name/location 一定带双引号，正文可能包含任意 XML/代码。
	[GeneratedRegex("<skill\\s+name=\"(?<name>[^\"]*)\"(?:\\s+location=\"(?<path>[^\"]*)\")?\\s*>[\\s\\S]*?</skill>", RegexOptions.IgnoreCase)]
	private static partial Regex SkillBlock();

	[GeneratedRegex("\\n{3,}")]
	private static partial Regex BlankRuns();

	public static (IReadOnlyList<UserSkillChip> Skills, string Body) Parse(string text)
	{
		if (text.Length == 0) return ([], string.Empty);

		var chips = new List<UserSkillChip>();
		foreach (Match match in SkillBlock().Matches(text))
		{
			var name = match.Groups["name"].Value.Trim();
			if (name.Length == 0) continue;
			chips.Add(new UserSkillChip(name, match.Groups["path"].Value.Trim()));
		}

		var body = chips.Count == 0
			? text
			: BlankRuns().Replace(SkillBlock().Replace(text, "\n\n"), "\n\n").Trim();
		// 正文保留原始 @"path"：行内 chip 由 UserReferenceRichText 的投影层隐藏路径，
		// 发送、复制和编辑仍然使用完整原文。
		return (chips, body);
	}

	/// <summary>
	/// 折叠长正文：只按源码行数截断（PiDeck 用 CSS line-clamp，MewUI 没有同等的文本行裁剪）。
	/// 被截断时保留一个省略号，展开按钮和完整原文都在气泡里。
	/// </summary>
	public static (string Text, bool Collapsible) Collapse(string body)
	{
		if (body.Length == 0) return (body, false);
		var lines = body.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
		if (lines.Length <= CollapsedLines) return (body, false);
		return (string.Join('\n', lines.Take(CollapsedLines)) + "\n…", true);
	}
}
