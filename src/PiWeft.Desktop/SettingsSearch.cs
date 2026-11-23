namespace PiWeft.Desktop;

/// <summary>设置页可搜索项：一个分区入口，或分区内的一项设置。</summary>
/// <param name="Id">稳定标识，供结果列表做 key。</param>
/// <param name="Section">所属分区的标识，见 <see cref="SettingsSections"/>。</param>
/// <param name="Title">页面上显示的名称。</param>
/// <param name="Keywords">搜索别名：英文名、俗称、相邻概念。</param>
/// <param name="Anchor">跳到该设置时聚焦的控件键；为空表示只切到分区顶部。</param>
internal sealed record SettingsSearchEntry(
	string Id,
	string Section,
	string Title,
	IReadOnlyList<string> Keywords,
	string? Anchor = null)
{
	/// <summary>是否属于「配置管理」分段。</summary>
	public bool Config { get; } = SettingsSections.Find(Section)?.Config ?? false;

	/// <summary>分区名称，结果行右侧的面包屑。</summary>
	public string SectionTitle { get; } = SettingsSections.Title(Section);

	/// <summary>分区在导航里的顺序；建表时算一次，排序比较不再查字典。</summary>
	public int SectionOrder { get; } = SettingsSections.OrderOf(Section);
}

/// <summary>搜索命中：条目、总分与标题里命中的字符下标（升序、去重）。</summary>
internal readonly record struct SettingsSearchHit(
	SettingsSearchEntry Entry,
	int Score,
	IReadOnlyList<int> TitleIndices);

/// <summary>
/// 设置页搜索的纯打分内核。三条通道按确定性排序：标题子串最可信，其次是别名，
/// 最后是所属分区名（搜「外观」时把该页所有项都带出来）。
/// 多词查询按 AND 处理，每个词都要命中某一段文本。
/// </summary>
internal static class SettingsSearch
{
	private const int TitleExact = 1000;
	private const int TitlePrefix = 900;
	private const int TitleContains = 800;
	private const int KeywordExact = 650;
	private const int KeywordPrefix = 600;
	private const int KeywordContains = 550;
	private const int SectionExact = 300;
	private const int SectionPrefix = 280;
	private const int SectionContains = 260;

	/// <summary>全部可搜条目：每个分区一条，加上常用的具体设置项。</summary>
	public static IReadOnlyList<SettingsSearchEntry> Entries { get; } =
	[
		.. SettingsSections.Groups.SelectMany(static group => group.Sections.Select(section => new SettingsSearchEntry(
			"section:" + section.Id, section.Id, section.Title, [.. section.Keywords, group.Label]))),
		.. SettingsSections.Config.Select(static section => new SettingsSearchEntry(
			"section:" + section.Id, section.Id, section.Title, [.. section.Keywords, Strings.ConfigurationGroup])),
		new("item:overview.agentDir", "overview", Strings.SettingsAgentDir, ["agent", "目录", "路径", "agentDir"]),
		new("item:overview.cwd", "overview", Strings.SettingsCwd, ["工作目录", "当前目录", "cwd", "路径"]),
		new("item:overview.mcp", "overview", Strings.SettingsMcp, ["mcp", "服务器", "连接", "工具"]),
		new("appearance.theme", "appearance", Strings.ConfigTheme, ["主题", "深色", "浅色", "跟随系统", "theme"], "appearance.theme"),
		new("appearance.font", "appearance", Strings.ChatFontSize, ["字号", "字体", "大小", "阅读", "font"], "appearance.font"),
		new("appearance.width", "appearance", Strings.ReadingWidth, ["宽度", "行宽", "阅读", "width"], "appearance.width"),
		new("appearance.tips", "appearance", Strings.RespondingTipsSetting, ["提示", "轮播", "等待", "tips"], "appearance.tips"),
		new("appearance.close-to-tray", "appearance", Strings.CloseToTraySetting,
			["托盘", "关闭", "后台", "驻留", "窗口", "tray", "close"], "appearance.close-to-tray"),
		new("git.model", "git", Strings.GitSummaryModel, ["git", "提交", "摘要", "模型", "commit"], "git.model"),
		new("git.thinking", "git", Strings.GitSummaryThinking, ["git", "思考", "级别", "摘要"], "git.thinking"),
		new("git.prompt", "git", Strings.GitSummaryPrompt, ["git", "提示词", "模板", "prompt", "diff"], "git.prompt"),
		new("quickMessages.editor", "quickMessages", Strings.SettingsQuickMessagesEditor, ["快捷消息", "快捷", "常用语", "编辑"], "quickMessages.editor"),
		new("resources.skills", "resources", Strings.SettingsSkills, ["技能", "skill"]),
		new("resources.prompts", "resources", Strings.ResourcesPrompts, ["提示词", "模板", "prompt"]),
		new("resources.themes", "resources", Strings.ResourcesThemes, ["主题", "theme"]),
		new("resources.packages", "resources", Strings.ResourcesPackages, ["包", "package", "pi install"]),
		new("session.file", "session", Strings.SettingsSessionFile, ["会话", "文件", "路径", "session"]),
		new("session.id", "session", Strings.SettingsSessionId, ["会话", "id", "session"]),
		new("session.defaultModel", "session", Strings.MakeDefaultModel, ["默认模型", "模型", "model"], "session.defaultModel"),
		new("logs.level", "logs", Strings.LogLevel, ["日志", "级别", "level", "trace", "debug"], "logs.level"),
		new("diagnostics.toggle", "diagnostics", Strings.SettingsDiagnosticsToggle, ["诊断", "性能", "采样", "帧"], "diagnostics.toggle"),
		new("about.version", "about", Strings.AboutVersion, ["版本", "version"]),
		new("models.addProvider", "models", Strings.ConfigAddProvider, ["供应商", "provider", "添加"]),
		new("models.addModel", "models", Strings.ConfigAddModel, ["模型", "model", "添加"]),
		new("models.import", "models", Strings.ConfigImport, ["导入", "JSON"]),
		new("auth.keys", "auth", Strings.ConfigKey, ["API Key", "OAuth", "凭据", "认证", "auth"]),
		new("agent.tools", "agent", Strings.ConfigToolsTitle, ["工具", "tool", "默认工具"]),
		new("agent.codemode", "agent", Strings.ConfigCodemodeMode, ["codemode", "脚本", "工具"]),
		new("agent.subagents", "agent", Strings.ConfigSubagentsTitle, ["子代理", "subagent", "委派"]),
	];

	/// <summary>搜索设置项；空查询返回空数组，调用方据此切回正常内容。</summary>
	/// <param name="query">用户输入，按空白切成多个词，逐词 AND。</param>
	/// <param name="limit">结果上限；0 表示不截断。</param>
	public static IReadOnlyList<SettingsSearchHit> Match(string? query, int limit = 40)
	{
		var text = query?.Trim() ?? string.Empty;
		if (text.Length == 0) return [];
		var tokens = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
		if (tokens.Length == 0) return [];

		var hits = new List<SettingsSearchHit>(Entries.Count);
		foreach (var entry in Entries)
		{
			var total = 0;
			var matched = true;
			// 只有标题命中才需要记高亮下标；别名/分区命中不分配集合。
			SortedSet<int>? titleIndices = null;
			foreach (var token in tokens)
			{
				var result = ScoreEntry(entry, token);
				if (result.Score == 0)
				{
					matched = false;
					break;
				}

				total += result.Score;
				if (result.Length == 0) continue;
				titleIndices ??= [];
				for (var offset = 0; offset < result.Length; offset++)
				{
					titleIndices.Add(result.Start + offset);
				}
			}

			if (!matched) continue;
			IReadOnlyList<int> indices = titleIndices is null ? [] : [.. titleIndices];
			hits.Add(new SettingsSearchHit(entry, total, indices));
		}

		// 整串（或前缀）命中分区名时，把该分区自己抬到前面：搜「模型」先到模型页，
		// 搜「MCP」先到 MCP 服务器页，而不是先落到别名里带这个词的字段。
		var exact = text;
		for (var index = 0; index < hits.Count; index++)
		{
			var hit = hits[index];
			if (!hit.Entry.Id.StartsWith("section:", StringComparison.Ordinal)) continue;
			if (hit.Entry.Title.Equals(exact, StringComparison.OrdinalIgnoreCase))
			{
				hits[index] = hit with { Score = hit.Score + 20 };
			}
			else if (hit.Entry.Title.StartsWith(exact, StringComparison.OrdinalIgnoreCase))
			{
				hits[index] = hit with { Score = hit.Score + 10 };
			}
		}

		hits.Sort(CompareHits);
		return limit > 0 && hits.Count > limit ? [.. hits.Take(limit)] : hits;
	}

	/// <summary>把标题按命中下标切成「普通 / 命中」片段，供结果行加粗高亮。</summary>
	/// <param name="title">原始标题。</param>
	/// <param name="indices">升序、去重的命中下标，见 <see cref="SettingsSearchHit.TitleIndices"/>。</param>
	public static IReadOnlyList<(string Text, bool Hit)> Segments(string title, IReadOnlyList<int> indices)
	{
		if (indices.Count == 0) return [(title, false)];
		var segments = new List<(string Text, bool Hit)>(Math.Min(indices.Count * 2 + 1, title.Length + 1));
		var start = 0;
		var position = 0;
		while (position < indices.Count)
		{
			// 防御越界下标：内部调用方只传标题内的下标，这里保证坏的输入也不会抛。
			if (indices[position] >= title.Length)
			{
				position++;
				continue;
			}

			var from = Math.Clamp(indices[position], start, title.Length);
			var to = from;
			while (position + 1 < indices.Count && indices[position + 1] == to + 1)
			{
				position++;
				to++;
			}

			if (from > start) segments.Add((title[start..from], false));
			var end = Math.Min(to + 1, title.Length);
			if (from < end) segments.Add((title[from..end], true));
			start = to + 1;
			position++;
		}

		if (start < title.Length) segments.Add((title[start..], false));
		return segments;
	}

	private static int CompareHits(SettingsSearchHit left, SettingsSearchHit right)
	{
		var byScore = right.Score.CompareTo(left.Score);
		if (byScore != 0) return byScore;
		var bySection = left.Entry.SectionOrder.CompareTo(right.Entry.SectionOrder);
		if (bySection != 0) return bySection;
		var byLength = left.Entry.Title.Length.CompareTo(right.Entry.Title.Length);
		return byLength != 0 ? byLength : string.CompareOrdinal(left.Entry.Title, right.Entry.Title);
	}

	private static TextMatch ScoreEntry(SettingsSearchEntry entry, string token)
	{
		var best = ScoreText(entry.Title, token, TitleExact, TitlePrefix, TitleContains);
		foreach (var keyword in entry.Keywords)
		{
			var result = ScoreText(keyword, token, KeywordExact, KeywordPrefix, KeywordContains);
			if (result.Score > best.Score) best = result with { Start = 0, Length = 0 };
		}

		var section = ScoreText(entry.SectionTitle, token, SectionExact, SectionPrefix, SectionContains);
		if (section.Score > best.Score) best = section with { Start = 0, Length = 0 };
		return best;
	}

	private static TextMatch ScoreText(string text, string token, int exact, int prefix, int contains)
	{
		if (text.Length == 0 || token.Length == 0) return default;
		if (text.Equals(token, StringComparison.OrdinalIgnoreCase)) return new(exact, 0, token.Length);
		if (text.StartsWith(token, StringComparison.OrdinalIgnoreCase)) return new(prefix, 0, token.Length);
		var at = text.IndexOf(token, StringComparison.OrdinalIgnoreCase);
		return at >= 0 ? new TextMatch(contains, at, token.Length) : default;
	}

	/// <summary>一段文本的命中：分数与标题里的起始下标/长度；长度 0 表示不来自标题。</summary>
	private readonly record struct TextMatch(int Score, int Start, int Length);
}
