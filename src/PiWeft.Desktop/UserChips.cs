namespace PiWeft.Desktop;

/// <summary>行内 chip 的种类：文件引用画成 Info 蓝，技能调用画成 Thinking 紫。</summary>
internal enum UserChipKind
{
	File,
	Skill,
}

/// <summary>
/// 正文里可以画成行内 chip 的一段原文（文件引用或技能调用）。
/// 投影层只关心字符区间，渲染层再按 <see cref="Kind"/> 决定图标、配色与悬浮提示。
/// </summary>
internal interface IUserChip
{
	UserChipKind Kind { get; }

	/// <summary>原始值：文件路径或技能名。</summary>
	string Value { get; }

	/// <summary>chip 上的展示文本（文件带 @，技能只留名字）。</summary>
	string Display { get; }

	bool IsDirectory { get; }

	int Start { get; }

	int End { get; }
}

/// <summary>
/// 用户消息 / 草稿里的技能调用：pi 的 wire token 是 <c>/skill:名称</c>。
/// 和文件引用一样只做展示层识别，发送给模型的原文不变。
/// </summary>
internal sealed record UserSkillReference(string Name, int Start, int End) : IUserChip
{
	public UserChipKind Kind => UserChipKind.Skill;

	public string Value => Name;

	/// <summary>技能 chip 只显示技能名；<c>/skill:</c> 是 wire 细节，交给 sparkles 图标表达。</summary>
	public string Display => Name;

	public bool IsDirectory => false;

	/// <summary>悬浮提示用回到原文的完整命令。</summary>
	public string Command => "/skill:" + Name;
}

/// <summary>把正文里的文件引用和技能调用合成一份按位置排序、互不重叠的 chip 列表。</summary>
internal static class UserChips
{
	public static IReadOnlyList<IUserChip> Parse(string text)
	{
		if (text.Length == 0) return [];
		var files = UserFileReferences.Parse(text);
		var skills = UserSkillReferences.Parse(text);
		if (files.Count == 0) return skills.Count == 0 ? [] : [.. skills];
		if (skills.Count == 0) return [.. files];

		var chips = new List<IUserChip>(files.Count + skills.Count);
		chips.AddRange(files);
		chips.AddRange(skills);
		chips.Sort(static (left, right) => left.Start.CompareTo(right.Start));
		var merged = new List<IUserChip>(chips.Count);
		var coverEnd = -1;
		foreach (var chip in chips)
		{
			// 文件路径可能把后面的 /skill:名称 一起吞进 token，先出现者胜出。
			if (chip.Start < coverEnd) continue;
			merged.Add(chip);
			coverEnd = chip.End;
		}
		return merged;
	}
}
