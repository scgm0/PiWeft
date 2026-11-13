using System.Collections.Frozen;

namespace PiWeft.CodingAgent.Subagents;

/// <summary>一个子代理角色：markdown 文件里的 frontmatter 加正文提示词。</summary>
public sealed record SubagentDefinition
{
	/// <summary>角色名，同时是 <c>delegate</c> 的 <c>agent</c> 参数。</summary>
	public required string Name { get; init; }

	/// <summary>一句话用途，进入工具描述与 <c>list_subagents</c>。</summary>
	public required string Description { get; init; }

	/// <summary>追加到子会话系统提示词后面的角色指令。</summary>
	public string SystemPrompt { get; init; } = string.Empty;

	/// <summary>子会话工具白名单；省略时沿用父会话可用的工具。</summary>
	public IReadOnlyList<string>? Tools { get; init; }

	/// <summary>在白名单或父会话工具集之上再去掉的工具。</summary>
	public IReadOnlyList<string> ExcludeTools { get; init; } = [];

	/// <summary>子会话模型，<c>provider/id</c> 或唯一 id。</summary>
	public string? Model { get; init; }

	/// <summary>子会话思考级别。</summary>
	public string? Thinking { get; init; }

	/// <summary>
	/// 是否把角色名与描述放进 <c>delegate</c> 的模型可见目录。null 表示跟随同名角色的既有值；
	/// 新角色未写时为 false。完整列表始终可由 <c>list_subagents</c> 查到。
	/// </summary>
	public bool? Advertise { get; init; }

	/// <summary>来源目录：<c>builtin</c>、<c>user</c> 或 <c>project</c>。</summary>
	public string Source { get; init; } = "builtin";

	/// <summary>定义文件路径；内置角色为 null。</summary>
	public string? FilePath { get; init; }
}

/// <summary>
/// 子代理角色目录。发现顺序是内置、用户、项目，同名后发现的覆盖先发现的。
/// 一次会话构造只建一份，查找走 <see cref="FrozenDictionary{TKey,TValue}"/>。
/// </summary>
public sealed class SubagentCatalog
{
	private readonly FrozenDictionary<string, SubagentDefinition> _byName;

	private SubagentCatalog(SubagentDefinition[] all)
	{
		_all = all;
		_byName = all.ToFrozenDictionary(static definition => definition.Name, StringComparer.Ordinal);
	}

	private readonly SubagentDefinition[] _all;

	/// <summary>没有角色的空目录。</summary>
	public static SubagentCatalog Empty { get; } = new([]);

	/// <summary>全部角色，按名字排序。</summary>
	public IReadOnlyList<SubagentDefinition> All => _all;

	/// <summary>按名字取角色；找不到返回 null。</summary>
	public SubagentDefinition? Get(string name) => _byName.GetValueOrDefault(name);

	/// <summary>角色名是否合法：小写字母开头，只含小写字母、数字与连字符，最长 32。</summary>
	public static bool IsValidName(string name)
	{
		if (name.Length is 0 or > 32 || !char.IsAsciiLetterLower(name[0]))
		{
			return false;
		}

		foreach (var character in name.AsSpan(1))
		{
			if (!char.IsAsciiLetterLower(character) && !char.IsAsciiDigit(character) && character != '-')
			{
				return false;
			}
		}

		return !string.Equals(name, "lead", StringComparison.Ordinal);
	}

	/// <summary>
	/// 发现角色：内置角色，然后是 <c>&lt;agentDir&gt;/agents</c> 与项目的
	/// <c>.pi/agents</c>，递归读 <c>*.md</c>。
	/// </summary>
	public static SubagentCatalog Discover(
		string cwd,
		string? agentDir = null,
		bool includeProject = true,
		List<string>? diagnostics = null)
	{
		var resolvedAgentDir = agentDir ?? Session.SessionPaths.GetAgentDir();
		var byName = new Dictionary<string, SubagentDefinition>(StringComparer.Ordinal);
		foreach (var builtin in SubagentCatalog.Builtin)
		{
			byName[builtin.Name] = builtin;
		}

		Collect(Path.Combine(resolvedAgentDir, "agents"), "user", byName, diagnostics);
		if (includeProject)
		{
			Collect(Path.Combine(Path.GetFullPath(cwd), ".pi", "agents"), "project", byName, diagnostics);
		}

		var all = new SubagentDefinition[byName.Count];
		var index = 0;
		foreach (var pair in byName)
		{
			all[index++] = pair.Value;
		}

		Array.Sort(all, static (left, right) => string.CompareOrdinal(left.Name, right.Name));
		return new SubagentCatalog(all);
	}

	private static void Collect(
		string root,
		string source,
		Dictionary<string, SubagentDefinition> byName,
		List<string>? diagnostics)
	{
		if (!Directory.Exists(root))
		{
			return;
		}

		var options = new EnumerationOptions
		{
			RecurseSubdirectories = true,
			IgnoreInaccessible = true,
			MatchCasing = MatchCasing.CaseInsensitive,
			MaxRecursionDepth = 4,
		};

		foreach (var path in Directory.EnumerateFiles(root, "*.md", options))
		{
			var definition = ParseFile(path, source);
			if (definition is null)
			{
				diagnostics?.Add($"子代理定义不可读或缺少名字：{path}");
				continue;
			}

			// 覆盖同名内置角色时，不写 advertise 就沿用内置的可见性；新角色默认不进目录。
			var advertise = definition.Advertise ?? byName.GetValueOrDefault(definition.Name)?.Advertise ?? false;
			byName[definition.Name] = definition with { Advertise = advertise };
		}
	}

	/// <summary>解析一个角色定义文件；没有 frontmatter 或名字非法时返回 null。</summary>
	public static SubagentDefinition? ParseFile(string path, string source = "user")
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

		return Parse(text, Path.GetFileNameWithoutExtension(path), source, path);
	}

	/// <summary>解析角色定义正文。<paramref name="fallbackName"/> 用于省略 name 的文件。</summary>
	public static SubagentDefinition? Parse(string text, string fallbackName, string source, string? path)
	{
		var span = text.AsSpan();
		if (span.StartsWith("\uFEFF"))
		{
			span = span[1..];
		}

		if (!span.StartsWith("---"))
		{
			return null;
		}

		// 第一行是 ---，随后找下一个 --- 行。frontmatter 只按行读，正文保持原样。
		var firstBreak = span.IndexOf('\n');
		if (firstBreak < 0)
		{
			return null;
		}

		var bodyStart = -1;
		var lineStart = firstBreak + 1;
		while (lineStart < span.Length)
		{
			var lineEnd = span[lineStart..].IndexOf('\n');
			var line = (lineEnd < 0 ? span[lineStart..] : span.Slice(lineStart, lineEnd)).TrimEnd('\r');
			if (line.Trim().SequenceEqual("---"))
			{
				bodyStart = lineEnd < 0 ? span.Length : lineStart + lineEnd + 1;
				break;
			}

			if (lineEnd < 0)
			{
				return null;
			}

			lineStart += lineEnd + 1;
		}

		if (bodyStart < 0)
		{
			return null;
		}

		var fields = ParseFields(span.Slice(firstBreak + 1, lineStart - firstBreak - 1));
		if (fields.Count == 0)
		{
			return null;
		}

		var name = fields.GetValueOrDefault("name");
		if (string.IsNullOrWhiteSpace(name))
		{
			name = fallbackName;
		}

		name = name.Trim();
		if (!IsValidName(name))
		{
			return null;
		}

		var body = text[bodyStart..].Trim();
		var description = fields.GetValueOrDefault("description")?.Trim() ?? string.Empty;
		bool? advertise = bool.TryParse(fields.GetValueOrDefault("advertise"), out var configuredAdvertise)
			? configuredAdvertise
			: null;

		return new SubagentDefinition
		{
			Name = name,
			Description = description,
			SystemPrompt = body,
			Tools = SplitList(fields.GetValueOrDefault("tools")),
			ExcludeTools = SplitList(fields.GetValueOrDefault("excludeTools")) ?? [],
			Model = Trimmed(fields.GetValueOrDefault("model")),
			Thinking = Trimmed(fields.GetValueOrDefault("thinking")),
			Advertise = advertise,
			Source = source,
			FilePath = path,
		};
	}

	private static Dictionary<string, string> ParseFields(ReadOnlySpan<char> frontmatter)
	{
		var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		string? listKey = null;
		foreach (var rawLine in frontmatter.EnumerateLines())
		{
			var line = rawLine.Trim();
			if (line.IsEmpty || line[0] == '#')
			{
				continue;
			}

			if (line[0] == '-' && line.Length > 1 && (line[1] == ' ' || line[1] == '\t') && listKey is not null)
			{
				var item = line[1..].Trim();
				if (item.IsEmpty)
				{
					continue;
				}

				var existing = fields[listKey];
				fields[listKey] = existing.Length == 0 ? item.ToString() : string.Concat(existing, ",", item);
				continue;
			}

			var separator = line.IndexOf(':');
			if (separator <= 0)
			{
				continue;
			}

			var key = line[..separator].Trim().ToString();
			var value = line[(separator + 1)..].Trim().Trim("\"'").ToString();
			if (key.Length == 0)
			{
				continue;
			}

			fields[key] = value;
			listKey = value.Length == 0 ? key : null;
		}

		return fields;
	}

	private static string? Trimmed(string? value) =>
		string.IsNullOrWhiteSpace(value) ? null : value.Trim();

	private static IReadOnlyList<string>? SplitList(string? value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return null;
		}

		var items = new List<string>(4);
		foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			if (part.Length > 0)
			{
				items.Add(part);
			}
		}

		return items.Count == 0 ? null : items;
	}

	private static readonly string[] ScoutTools = ["read", "grep", "find", "ls"];

	private static readonly string[] WorkerTools = ["read", "grep", "find", "ls", "edit", "write", "bash", "todo"];

	/// <summary>随 CLI 分发的四个角色。用户或项目同名文件覆盖它们。</summary>
	public static IReadOnlyList<SubagentDefinition> Builtin { get; } =
	[
		new SubagentDefinition
		{
			Name = "scout",
			Advertise = true,
			Description = "Fast codebase recon: relevant files, entry points, data flow, risks.",
			Tools = ScoutTools,
			SystemPrompt =
				"You investigate a codebase and report findings. You do not edit files. " +
				"Locate the relevant files, entry points, data flow, and risks. " +
				"Read what you need, then answer with concrete file paths and line references. " +
				"When the task is ambiguous, state the assumption you used instead of asking.",
		},
		new SubagentDefinition
		{
			Name = "reviewer",
			Advertise = true,
			Description = "Read-only review against the task, tests, edge cases, and simplicity.",
			Tools = ScoutTools,
			SystemPrompt =
				"You review a change without editing files. Check it against the task, tests, edge cases, " +
				"and unnecessary complexity. Report concrete findings with file and line references, ordered " +
				"by severity. If the change looks fine, say so in one line. Do not restate the diff.",
		},
		new SubagentDefinition
		{
			Name = "oracle",
			Advertise = true,
			Description = "A second opinion before acting: challenges assumptions and recommends the safest next step.",
			Tools = ScoutTools,
			SystemPrompt =
				"You are the advisory reviewer. Read the code and the request, then challenge the assumptions behind the " +
				"proposed direction. Point out what could break, what is missing, and the safest next move. Do not edit files. " +
				"Answer with concrete file references and a short recommended execution prompt.",
		},
		new SubagentDefinition
		{
			Name = "worker",
			Advertise = true,
			Description = "Implementation work: edits files, validates, reports blockers instead of guessing.",
			Tools = WorkerTools,
			SystemPrompt =
				"You implement the assigned change. Read the surrounding code first, make the smallest correct " +
				"edit, then verify with the project's build or tests. Do not touch files outside the task. " +
				"When a decision needs the user, stop and write the blocker and the options in your final reply; " +
				"you cannot ask questions.",
		},
	];
}
