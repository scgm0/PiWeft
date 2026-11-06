using PiWeft.CodingAgent.Session;

namespace PiWeft.CodingAgent.Resources;

/// pi 资源目录布局：把散落的 <c>skills</c> / <c>prompts</c> / <c>themes</c> 目录名与
/// 各级根目录的解析集中到一处，供技能、提示词模板、主题与包共用。
public static class ResourcePaths
{
	public const string SkillsDirectoryName = "skills";
	public const string PromptsDirectoryName = "prompts";
	public const string ThemesDirectoryName = "themes";
	public const string ExtensionsDirectoryName = "extensions";
	public const string AgentsDirectoryName = ".agents";
	public const string PackageManifestName = "package.json";
	public const string NpmDirectoryName = "npm";

	public static string UserDirectory(string agentDir, string kind) =>
		Path.Combine(agentDir, kind);

	public static string ProjectDirectory(string directory, string kind) =>
		Path.Combine(directory, SessionPaths.ConfigDirName, kind);

	public static string UserAgentsSkillsDirectory() => Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
		AgentsDirectoryName,
		SkillsDirectoryName);

	public static string NpmInstallRoot(string agentDir) => Path.Combine(agentDir, NpmDirectoryName);

	/// 技能根目录，按发现顺序排列：先是工作目录逐级向上的项目根（<c>.pi/skills</c> 与
	/// <c>.agents/skills</c>），然后是用户根。同名时先发现者获胜，因此项目技能会覆盖用户技能。
	public static List<(string Root, ResourceOrigin Origin)> SkillRoots(
		string cwd,
		string? agentDir = null,
		bool includeProject = true)
	{
		var roots = new List<(string, ResourceOrigin)>();
		var resolvedAgentDir = agentDir ?? SessionPaths.GetAgentDir();

		// 不受信时只加载用户级资源，项目内的技能目录全部跳过。
		if (includeProject)
		{
			foreach (var directory in AncestorDirectories(cwd))
			{
				roots.Add((
					ProjectDirectory(directory, SkillsDirectoryName),
					ResourceOrigin.ProjectAuto.WithRoot(directory)));
				roots.Add((
					Path.Combine(directory, AgentsDirectoryName, SkillsDirectoryName),
					ResourceOrigin.ProjectAuto.WithRoot(directory)));
			}
		}

		roots.Add((UserDirectory(resolvedAgentDir, SkillsDirectoryName), ResourceOrigin.UserAuto));
		roots.Add((UserAgentsSkillsDirectory(), ResourceOrigin.UserAuto));
		return roots;
	}

	public static List<(string Root, ResourceOrigin Origin)> PromptRoots(
		string cwd,
		string? agentDir = null,
		bool includeProject = true)
	{
		var roots = new List<(string, ResourceOrigin)>();
		var resolvedAgentDir = agentDir ?? SessionPaths.GetAgentDir();

		if (includeProject)
		{
			var full = Path.GetFullPath(cwd);
			roots.Add((ProjectDirectory(full, PromptsDirectoryName), ResourceOrigin.ProjectAuto.WithRoot(full)));
		}

		roots.Add((UserDirectory(resolvedAgentDir, PromptsDirectoryName), ResourceOrigin.UserAuto));
		return roots;
	}

	public static List<(string Root, ResourceOrigin Origin)> ThemeRoots(
		string cwd,
		string? agentDir = null,
		bool includeProject = true)
	{
		var roots = new List<(string, ResourceOrigin)>();
		var resolvedAgentDir = agentDir ?? SessionPaths.GetAgentDir();

		if (includeProject)
		{
			var full = Path.GetFullPath(cwd);
			roots.Add((ProjectDirectory(full, ThemesDirectoryName), ResourceOrigin.ProjectAuto.WithRoot(full)));
		}

		roots.Add((UserDirectory(resolvedAgentDir, ThemesDirectoryName), ResourceOrigin.UserAuto));
		return roots;
	}

	public static IEnumerable<string> AncestorDirectories(string cwd)
	{
		var current = new DirectoryInfo(Path.GetFullPath(cwd));
		while (current is not null)
		{
			yield return current.FullName;

			// 遇到仓库根时终止项目内的发现。worktree 与 submodule 的 .git 是文件，不是目录，
			// 只判断目录会让发现一路扫到文件系统根，把仓库外的 .pi / .agents 也当成项目资源。
			if (File.Exists(Path.Combine(current.FullName, ".git")) || Directory.Exists(Path.Combine(current.FullName, ".git")))
			{
				yield break;
			}

			current = current.Parent;
		}
	}
}
