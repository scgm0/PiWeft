using System.Text;
using PiWeft.Agent;
using PiWeft.Ai;
using PiWeft.CodingAgent.Tools;

namespace PiWeft.CodingAgent;

/// <summary>编码系统提示词的输入。</summary>
public sealed record SystemPromptOptions
{
	public required string Cwd { get; init; }

	/// <summary>各工具对提示词的贡献，按声明顺序。</summary>
	public IReadOnlyList<BuiltinTool> Tools { get; init; } = [];

	/// <summary>追加在工具准则之后的准则（pi 的基础规则）。</summary>
	public IReadOnlyList<string> ExtraGuidelines { get; init; } = [];

	/// <summary>
	/// 自定义前缀提示词，对应 pi 的 <c>--system-prompt</c>。设置后取代默认 preamble，
	/// 并且不再生成 tools/rules/docs 章节（与 pi 的 <c>customPrompt</c> 一致）。
	/// </summary>
	public string? CustomPrompt { get; init; }

	/// <summary>
	/// 追加在提示词末尾的 <c>addendum</c> 章节，对应 pi 的 <c>--append-system-prompt</c>。
	/// </summary>
	public string? Addendum { get; init; }

	/// <summary>由 AGENTS.md / CLAUDE.md 渲染出的 <c>&lt;project_context&gt;</c> 章节。</summary>
	public string? ProjectContext { get; init; }

	/// <summary>渲染出的 <c>&lt;docs&gt;</c> 章节。</summary>
	public string? Docs { get; init; }

	/// <summary>由已发现技能渲染出的 <c>&lt;available_skills&gt;</c> 章节。</summary>
	public string? Skills { get; init; }

	/// <summary>按名称覆盖的提示词章节；值为 <c>null</c> 表示删除该章节。</summary>
	public IReadOnlyDictionary<string, string?>? SectionOverrides { get; init; }
}

/// <summary>
/// 组装编码系统提示词。pi 把提示词作为具名章节挂在 system 消息上，
/// 因此提示词的更新可以从会话文件里回放。
/// </summary>
public static class SystemPrompt
{
	public const string Preamble =
		"You are an expert coding assistant operating inside pi, a coding agent harness. You help users by reading files, executing commands, editing code, and writing new files.";

	/// <summary>每个会话都会带上的基础规则，追加在工具准则之后。</summary>
	public static readonly string[] BaselineGuidelines =
	[
		"Be concise in your responses",
		"Show file paths clearly when working with files",
	];

	public static SystemMessage Build(SystemPromptOptions options)
	{
		// 自定义提示词取代默认 preamble，并且不再生成 tools/rules/docs 章节（与 pi 的 customPrompt 一致）；
		// project_context、skills、addendum 与 cwd 仍然附加，因为它们是上下文而不是前缀。
		var custom = options.CustomPrompt is { Length: > 0 };

		var sections = new Dictionary<string, string?>(StringComparer.Ordinal)
		{
			["preamble"] = custom ? options.CustomPrompt! : Preamble,
		};

		if (!custom)
		{
			sections["tools"] = RenderTools(options.Tools);

			var rules = RenderRules(options.Tools, options.ExtraGuidelines);
			if (rules is not null)
			{
				sections["rules"] = rules;
			}

			if (options.Docs is { Length: > 0 } docs)
			{
				sections["docs"] = docs;
			}
		}

		if (options.Addendum is { Length: > 0 } addendum)
		{
			sections["addendum"] = addendum;
		}

		if (options.ProjectContext is { Length: > 0 } projectContext)
		{
			sections["project_context"] = projectContext;
		}

		if (options.Skills is { Length: > 0 } skills)
		{
			sections["skills"] = skills;
		}

		sections["cwd"] = $"<cwd>\n{options.Cwd}\n</cwd>";

		if (options.SectionOverrides is { } overrides)
		{
			foreach (var (name, value) in overrides)
			{
				if (value is null)
				{
					sections.Remove(name);
				}
				else
				{
					sections[name] = value;
				}
			}
		}

		return new SystemMessage
		{
			Content = MessageContent.Empty,
			Sections = sections,
			ToolsAdded = options.Tools.Count > 0 ? [.. options.Tools.Select(static tool => tool.Tool.ToDeclaration())] : null,
			Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
		};
	}

	public static string RenderTools(IReadOnlyList<BuiltinTool> tools)
	{
		var builder = new StringBuilder("<tools>\n");
		foreach (var tool in tools)
		{
			builder.Append("- ").Append(tool.Tool.Name).Append(": ").Append(tool.Snippet).Append('\n');
		}

		builder.Append("\nIn addition to the tools above, you may have access to other custom tools depending on the project.\n</tools>");
		return builder.ToString();
	}

	public static string? RenderRules(IReadOnlyList<BuiltinTool> tools, IReadOnlyList<string> extraGuidelines)
	{
		var guidelines = new List<string>();
		foreach (var tool in tools)
		{
			guidelines.AddRange(tool.Guidelines);
		}

		guidelines.AddRange(extraGuidelines);
		if (guidelines.Count == 0)
		{
			return null;
		}

		var builder = new StringBuilder("<rules>\n");
		foreach (var guideline in guidelines)
		{
			builder.Append("- ").Append(guideline).Append('\n');
		}

		builder.Append("</rules>");
		return builder.ToString();
	}
}

/// <summary>
/// 自动加载 SYSTEM.md / APPEND_SYSTEM.md。受信任项目的 <c>.pi</c> 文件覆盖 agent 目录的对应文件。
/// </summary>
public static class SystemPromptFiles
{
	public const string SystemFileName = "SYSTEM.md";
	public const string AppendFileName = "APPEND_SYSTEM.md";

	/// <summary>返回替换默认 preamble 的文本；没有时返回 null。</summary>
	public static string? LoadSystem(string cwd, bool projectTrusted, string? agentDir = null) =>
		Load(cwd, projectTrusted, SystemFileName, agentDir);

	/// <summary>返回追加到提示词末尾的文本；没有时返回 null。</summary>
	public static string? LoadAppend(string cwd, bool projectTrusted, string? agentDir = null) =>
		Load(cwd, projectTrusted, AppendFileName, agentDir);

	private static string? Load(string cwd, bool projectTrusted, string fileName, string? agentDir)
	{
		if (projectTrusted)
		{
			var project = Path.Combine(Path.GetFullPath(cwd), Session.SessionPaths.ConfigDirName, fileName);
			if (Read(project) is { Length: > 0 } projectText)
			{
				return projectText;
			}
		}

		var directory = agentDir ?? Session.SessionPaths.GetAgentDir();
		return Read(Path.Combine(directory, fileName));
	}

	private static string? Read(string path)
	{
		if (!File.Exists(path))
		{
			return null;
		}

		try
		{
			var text = File.ReadAllText(path);
			return string.IsNullOrWhiteSpace(text) ? null : text;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return null;
		}
	}
}

/// <summary>
/// 从工作目录向上加载项目指令文件（AGENTS.md、CLAUDE.md 等），对应 pi 的
/// <c>loadProjectContextFiles</c>：
/// <list type="bullet">
/// <item>先加载 agent 目录里的全局文件，再按「最外层 → 最近」追加祖先目录的文件；</item>
/// <item>每级目录只取一个文件（按 <see cref="FileNames"/> 顺序）；</item>
/// <item>按**真实路径**去重：把 AGENTS.md 符号链接到 CLAUDE.md 之类的写法不会重复注入；</item>
/// <item>嵌套 git worktree 里符号链接到主仓库的那一份要跳过 —— 否则同一份说明会出现两次。</item>
/// </list>
/// </summary>
public static class ContextFiles
{
	public const string SectionName = "project_context";

	/// <summary>pi 会查找的指令文件名，按优先级排列。</summary>
	public static readonly string[] FileNames =
	["AGENTS.override.md", "AGENTS.md", "AGENTS.MD", "CLAUDE.md", "CLAUDE.MD", "CODEBUDDY.md"];

	/// <summary>
	/// 为 <paramref name="cwd"/> 渲染 <c>project_context</c> 章节；没有内容时返回 <c>null</c>。
	/// </summary>
	/// <param name="cwd">工作目录。</param>
	/// <param name="agentDir">agent 目录（放全局指令文件）；默认取 <c>$PI_CODING_AGENT_DIR</c>。</param>
	public static string? Load(string cwd, string? agentDir = null)
	{
		var resolvedCwd = Path.GetFullPath(cwd);
		var configuredAgentDir = Environment.GetEnvironmentVariable("PI_CODING_AGENT_DIR");
		var resolvedAgentDir = Path.GetFullPath(
			agentDir
			?? (configuredAgentDir is { Length: > 0 }
				? configuredAgentDir
				: Path.Combine(
					Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
					".pi",
					"agent")));

		var found = new List<(string Path, string Content)>();
		var seen = new HashSet<string>(StringComparer.Ordinal);

		// 全局指令文件排在最前（与 pi 相同）。
		if (LoadFromDirectory(resolvedAgentDir) is { } global && seen.Add(Canonicalize(global.Path)))
		{
			found.Add(global);
		}

		var shadowed = FindShadowedContextFile(resolvedCwd);

		// 从最外层往内走，让外层文件排在内层之前（内层更具体，读起来更顺）。
		var ancestors = new List<string>();
		var current = new DirectoryInfo(resolvedCwd);
		while (current is not null)
		{
			ancestors.Add(current.FullName);
			current = current.Parent;
		}

		ancestors.Reverse();

		foreach (var directory in ancestors)
		{
			if (LoadFromDirectory(directory) is not { } file)
			{
				continue;
			}

			// 比较**目录**的真实路径加文件名，而不是文件本身的真实路径：
			// worktree 里的 AGENTS.md 若符号链接到主仓库的文件，它解析出来正是被遮蔽的那条路径。
			if (shadowed is not null &&
				string.Equals(Path.Combine(Canonicalize(directory), Path.GetFileName(file.Path)), shadowed, StringComparison.Ordinal))
			{
				continue;
			}

			if (seen.Add(Canonicalize(file.Path)))
			{
				found.Add(file);
			}
		}

		if (found.Count == 0)
		{
			return null;
		}

		var builder = new StringBuilder("<project_context>\nProject-specific instructions and guidelines:\n");
		foreach (var (path, content) in found)
		{
			builder.Append("\n<project_instructions path=\"").Append(path).Append("\">\n");
			builder.Append(content.TrimEnd()).Append('\n');
			builder.Append("</project_instructions>\n");
		}

		builder.Append("</project_context>");
		return builder.ToString();
	}

	/// <summary>某个目录下的指令文件：按 <see cref="FileNames"/> 取第一个存在的。</summary>
	private static (string Path, string Content)? LoadFromDirectory(string directory)
	{
		foreach (var name in FileNames)
		{
			var path = Path.Combine(directory, name);
			if (!File.Exists(path))
			{
				continue;
			}

			try
			{
				return (path, File.ReadAllText(path));
			}
			catch (IOException)
			{
				// 读不出来就当没有：上下文文件不该让会话起不来。
				return null;
			}
		}

		return null;
	}

	/// <summary>解析符号链接后的路径（不存在时退回绝对路径）。</summary>
	internal static string Canonicalize(string path)
	{
		try
		{
			FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
			return info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? Path.GetFullPath(path);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			return Path.GetFullPath(path);
		}
	}

	/// <summary>
	/// 嵌套 git worktree 里被主仓库遮蔽的指令文件路径；不适用时返回 <c>null</c>。
	/// 只在「worktree 位于主仓库内部」且主仓库确实检出在同一处时才算（裸仓库与子模块不算）。
	/// </summary>
	internal static string? FindShadowedContextFile(string cwd)
	{
		if (FindGitPaths(cwd) is not var (repoDir, commonGitDir))
		{
			return null;
		}

		var canonicalCommon = Canonicalize(commonGitDir);
		var mainRepoRoot = Path.GetDirectoryName(canonicalCommon);
		if (mainRepoRoot is null || !IsInside(Canonicalize(repoDir), mainRepoRoot))
		{
			// 普通仓库（两者同处）与同级 worktree（git worktree add ../feat）都走到这里。
			return null;
		}

		// 只有当 common git dir 的父目录本身也是同一仓库的检出时才算主仓库根；
		// 裸仓库布局（proj/.bare + proj/main）与子模块（.git/modules/...）会被这一条排除。
		if (!string.Equals(Canonicalize(Path.Combine(mainRepoRoot, ".git")), canonicalCommon, StringComparison.Ordinal))
		{
			return null;
		}

		return LoadFromDirectory(Canonicalize(repoDir)) is { } file
			? Path.Combine(mainRepoRoot, Path.GetFileName(file.Path))
			: null;
	}

	/// <summary>向父目录查找 <c>.git</c>，返回（worktree 根目录, 公共 git 目录）。</summary>
	private static (string RepoDir, string CommonGitDir)? FindGitPaths(string start)
	{
		var current = new DirectoryInfo(Path.GetFullPath(start));

		while (current is not null)
		{
			var dotGit = Path.Combine(current.FullName, ".git");

			if (Directory.Exists(dotGit))
			{
				// 普通检出：两者相同。
				return (current.FullName, dotGit);
			}

			if (File.Exists(dotGit))
			{
				// worktree / 子模块：.git 是「gitdir: <path>」的指针文件。
				var gitDir = ReadGitDirPointer(dotGit);
				if (gitDir is null)
				{
					return null;
				}

				var common = Path.Combine(gitDir, "commondir");
				if (!File.Exists(common))
				{
					// 子模块的 gitdir 没有 commondir，落在 .git/modules 下。
					return null;
				}

				var pointer = File.ReadAllText(common).Trim();
				var resolvedCommon = Path.IsPathRooted(pointer) ? pointer : Path.Combine(gitDir, pointer);
				return (current.FullName, Path.GetFullPath(resolvedCommon));
			}

			current = current.Parent;
		}

		return null;
	}

	/// <summary>读取 <c>.git</c> 指针文件里的 <c>gitdir:</c> 路径。</summary>
	private static string? ReadGitDirPointer(string path)
	{
		try
		{
			foreach (var line in File.ReadLines(path))
			{
				if (!line.StartsWith("gitdir:", StringComparison.Ordinal))
				{
					continue;
				}

				var value = line["gitdir:".Length..].Trim();
				return value.Length == 0 ? null : Path.GetFullPath(value);
			}
		}
		catch (IOException)
		{
			return null;
		}

		return null;
	}

	private static bool IsInside(string path, string parent)
	{
		var normalizedParent = parent.EndsWith(Path.DirectorySeparatorChar)
			? parent
			: parent + Path.DirectorySeparatorChar;

		return path.StartsWith(normalizedParent, StringComparison.Ordinal);
	}
}
