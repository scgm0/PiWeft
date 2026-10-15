using System.Text;
using System.Text.RegularExpressions;
using PiWeft.CodingAgent.Resources;

namespace PiWeft.CodingAgent.Skills;

/// <summary>已发现的技能：一个含 <c>SKILL.md</c>（带类 YAML 前置元数据）的目录。</summary>
public sealed record Skill
{
	public required string Name { get; init; }

	public required string Description { get; init; }

	/// <summary>技能文件的绝对路径。</summary>
	public required string FilePath { get; init; }

	/// <summary>技能内相对路径的解析基准目录。</summary>
	public required string BaseDirectory { get; init; }

	/// <summary>该技能只能通过显式命令触发时为 true。</summary>
	public bool DisableModelInvocation { get; init; }

	/// <summary>前置元数据之后的 Markdown 正文。</summary>
	public string Body { get; init; } = string.Empty;

	/// <summary>技能来源（用户、项目或包）。</summary>
	public ResourceOrigin Origin { get; init; }
}

/// <summary>
/// 从用户与项目位置发现 <c>SKILL.md</c>，并渲染 <c>&lt;available_skills&gt;</c>
/// 提示词章节。
/// </summary>
public static partial class Skills
{
	/// <summary>不作为技能元数据暴露的前置元数据字段。</summary>
	private static readonly HashSet<string> MetadataKeys = new(StringComparer.Ordinal)
	{
		"name",
		"description",
		"disable-model-invocation",
	};

	/// <summary>
	/// 发现技能：约定目录（项目优先）加 settings 显式路径与已安装包。
	/// 同名技能先发现者获胜，因此项目技能会覆盖用户技能。
	/// </summary>
	public static IReadOnlyList<Skill> Discover(string cwd, string? agentDir = null, bool includeProject = true)
	{
		// 这条便捷重载也要带上包资源，否则桌面/测试看到的技能集合会比会话快照少。
		var resolvedAgentDir = agentDir ?? Session.SessionPaths.GetAgentDir();
		var packages = PiPackageStore.Discover(cwd, resolvedAgentDir, includeProject);
		return Discover(ResourceResolver.SkillRoots(cwd, resolvedAgentDir, includeProject, packages));
	}

	/// <summary>从已解析的根目录发现技能。</summary>
	public static IReadOnlyList<Skill> Discover(IReadOnlyList<ResourceRoot> roots)
	{
		var found = new List<Skill>();
		var seenNames = new HashSet<string>(StringComparer.Ordinal);
		var seenPaths = new HashSet<string>(StringComparer.Ordinal);

		foreach (var (root, origin) in roots)
		{
			// settings 里可以直接声明一个 SKILL.md 文件；只按目录处理会把它静默丢掉。
			if (File.Exists(root))
			{
				Add(root, origin, found, seenNames, seenPaths);
				continue;
			}

			if (!Directory.Exists(root))
			{
				continue;
			}

			Collect(root, origin, found, seenNames, seenPaths);
		}

		return found;
	}

	/// <summary>按发现顺序排列的技能目录（仅路径）。</summary>
	public static IReadOnlyList<string> SearchRoots(string cwd, string? agentDir = null, bool includeProject = true)
	{
		var roots = ResourceResolver.SkillRoots(cwd, agentDir, includeProject);
		var paths = new List<string>(roots.Count);
		foreach (var root in roots)
		{
			paths.Add(root.Path);
		}

		return paths;
	}

	/// <summary>从 <paramref name="cwd"/> 向上直到（含）仓库根或文件系统根的各级目录。</summary>
	internal static IEnumerable<string> AncestorDirectories(string cwd) => ResourcePaths.AncestorDirectories(cwd);

	/// <summary>
	/// 展开 <c>/skill:name args</c> 命令为技能正文（带 <c>&lt;skill&gt;</c> 包裹），
	/// 对应 pi 的 <c>_expandSkillCommand</c>：未知技能或非技能命令原样返回。
	/// 正文取自已发现的技能快照，不重新读盘。
	/// </summary>
	public static string ExpandCommand(string text, IReadOnlyList<Skill> skills)
	{
		if (!text.StartsWith("/skill:", StringComparison.Ordinal))
		{
			return text;
		}

		var spaceIndex = text.IndexOf(' ', StringComparison.Ordinal);
		var name = spaceIndex < 0 ? text[7..] : text[7..spaceIndex];
		var args = spaceIndex < 0 ? string.Empty : text[(spaceIndex + 1)..].Trim();

		foreach (var skill in skills)
		{
			if (!string.Equals(skill.Name, name, StringComparison.Ordinal))
			{
				continue;
			}

			var body = skill.Body.Trim();
			var block = $"<skill name=\"{skill.Name}\" location=\"{skill.FilePath}\">\nReferences are relative to {skill.BaseDirectory}.\n\n{body}\n</skill>";
			return args.Length > 0 ? $"{block}\n\n{args}" : block;
		}

		return text;
	}

	/// <summary>渲染技能的提示词章节；没有技能时返回空字符串。</summary>
	public static string FormatForPrompt(IReadOnlyList<Skill> skills, string fileReadTool = "read")
	{
		var visible = skills.Where(static skill => !skill.DisableModelInvocation).ToList();
		if (visible.Count == 0)
		{
			return string.Empty;
		}

		var builder = new StringBuilder();
		builder.Append("\n\nThe following skills provide specialized instructions for specific tasks.\n");
		builder.Append(fileReadTool switch
		{
			"read" => "Use the read tool to load a skill's file when the task matches its description.\n",
			"bash" => "Use bash to load a skill's file when the task matches its description.\n",
			_ => "Load a skill's file when the task matches its description.\n",
		});
		builder.Append("When a skill file references a relative path, resolve it against the skill directory (parent of SKILL.md / dirname of the path) and use that absolute path in tool commands.\n\n");
		builder.Append("<available_skills>\n");

		foreach (var skill in visible)
		{
			builder.Append("  <skill>\n");
			builder.Append("    <name>").Append(EscapeXml(skill.Name)).Append("</name>\n");
			builder.Append("    <description>").Append(EscapeXml(skill.Description)).Append("</description>\n");
			builder.Append("    <location>").Append(EscapeXml(skill.FilePath)).Append("</location>\n");
			builder.Append("  </skill>\n");
		}

		builder.Append("</available_skills>");
		return builder.ToString();
	}

	/// <summary>解析一个技能文件。前置元数据里没有可用的名称与描述时返回 <c>null</c>。</summary>
	public static Skill? ParseFile(string path)
	{
		string text;
		try
		{
			text = File.ReadAllText(path);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return null;
		}

		var (fields, body) = ParseFrontmatter(text);
		if (fields is null)
		{
			return null;
		}

		var directory = Path.GetDirectoryName(path) ?? string.Empty;
		var fallbackName = Path.GetFileName(directory);

		var name = fields.TryGetValue("name", out var declaredName) && declaredName.Length > 0
			? declaredName
			: fallbackName;

		if (!fields.TryGetValue("description", out var description) || description.Length == 0)
		{
			return null; // 没有描述的技能不会被加载
		}

		if (name.Length == 0 || description.Length == 0)
		{
			return null;
		}

		return new Skill
		{
			Name = name,
			Description = description,
			FilePath = path,
			BaseDirectory = directory,
			DisableModelInvocation = fields.TryGetValue("disable-model-invocation", out var disabled) &&
				disabled.Equals("true", StringComparison.OrdinalIgnoreCase),
			Body = body,
		};
	}

	/// <summary>解析可选的 <c>---</c> 边界前置元数据，返回字段与剩余正文。</summary>
	public static (Dictionary<string, string>? Fields, string Body) ParseFrontmatter(string text)
	{
		var match = FrontmatterRegex().Match(text);
		if (!match.Success)
		{
			return (null, text);
		}

		var fields = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (var line in match.Groups["body"].Value.Split('\n'))
		{
			var trimmed = line.Trim();
			if (trimmed.Length == 0 || trimmed.StartsWith('#'))
			{
				continue;
			}

			var separator = trimmed.IndexOf(':');
			if (separator <= 0)
			{
				continue;
			}

			var key = trimmed[..separator].Trim();
			var value = trimmed[(separator + 1)..].Trim().Trim('"', '\'');
			if (key.Length > 0)
			{
				fields[key] = value;
			}
		}

		return (fields, text[match.Length..]);
	}

	private static void Collect(
		string root,
		ResourceOrigin origin,
		List<Skill> found,
		HashSet<string> seenNames,
		HashSet<string> seenPaths)
	{
		// 含 SKILL.md 的目录就是技能根，不再向内递归。
		var direct = Path.Combine(root, "SKILL.md");
		if (File.Exists(direct))
		{
			Add(direct, origin, found, seenNames, seenPaths);
			return;
		}

		foreach (var file in EnumerateSkillFiles(root))
		{
			Add(file, origin, found, seenNames, seenPaths);
		}
	}

	private static void Add(
		string path,
		ResourceOrigin origin,
		List<Skill> found,
		HashSet<string> seenNames,
		HashSet<string> seenPaths)
	{
		var full = Path.GetFullPath(path);
		if (!seenPaths.Add(full))
		{
			return;
		}

		var skill = ParseFile(full);
		if (skill is null)
		{
			return;
		}

		// 名称冲突时保留先发现的技能。
		if (!seenNames.Add(skill.Name))
		{
			return;
		}

		found.Add(skill with { Origin = origin });
	}

	private static IEnumerable<string> EnumerateSkillFiles(string root)
	{
		string[] children;
		try
		{
			// 物化到数组：EnumerateDirectories 是惰性的，异常在遍历时才抛，只包住创建等于没有保护。
			children = [.. Directory.EnumerateDirectories(root)];
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			yield break;
		}

		foreach (var child in children.OrderBy(static d => d, StringComparer.Ordinal))
		{
			var skillFile = Path.Combine(child, "SKILL.md");
			if (File.Exists(skillFile))
			{
				yield return skillFile;
				continue;
			}

			foreach (var nested in EnumerateSkillFiles(child))
			{
				yield return nested;
			}
		}
	}

	private static string EscapeXml(string value) => value
		.Replace("&", "&amp;", StringComparison.Ordinal)
		.Replace("<", "&lt;", StringComparison.Ordinal)
		.Replace(">", "&gt;", StringComparison.Ordinal)
		.Replace("\"", "&quot;", StringComparison.Ordinal)
		.Replace("'", "&apos;", StringComparison.Ordinal);

	[GeneratedRegex(@"\A---\r?\n(?<body>.*?)\r?\n---\r?\n?", RegexOptions.Singleline)]
	private static partial Regex FrontmatterRegex();
}
