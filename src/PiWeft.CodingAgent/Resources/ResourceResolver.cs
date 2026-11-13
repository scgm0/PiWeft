using PiWeft.CodingAgent.Prompts;
using PiWeft.CodingAgent.Session;
using PiWeft.CodingAgent.Settings;
using PiWeft.CodingAgent.Skills;
using PiWeft.CodingAgent.Themes;

namespace PiWeft.CodingAgent.Resources;

public sealed record ResourceRoot(string Path, ResourceOrigin Origin);

/// 资源页显示的一个扩展条目。<see cref="Note"/> 承载「为什么没生效」；
/// 加载失败或 TypeScript 未转译的扩展如果只字不提，用户会以为它在跑。
/// <see cref="Enabled"/> 是 settings 过滤规则的投影：停用的条目仍列出，方便重新打开。
public sealed record ExtensionEntry(
	string Name,
	string Path,
	string Origin,
	bool Loaded,
	string Note,
	bool Enabled = true);

public sealed record ResourceSnapshot
{
	public IReadOnlyList<Skill> Skills { get; init; } = [];

	public IReadOnlyList<PromptTemplate> Templates { get; init; } = [];

	public IReadOnlyList<ThemeEntry> Themes { get; init; } = [];

	public IReadOnlyList<PiPackage> Packages { get; init; } = [];

	public IReadOnlyList<ExtensionEntry> Extensions { get; init; } = [];

	public IReadOnlyList<string> Diagnostics { get; init; } = [];

	public static ResourceSnapshot Empty { get; } = new();
}

/// 统一的资源发现入口：把「约定目录 + settings 显式路径 + 已安装包」合成
/// 技能、提示词模板与主题的根目录列表，优先级与 pi 的 <c>resourcePrecedenceRank</c> 对齐。
/// CLI、RPC 与桌面共用这一条路径，保证三者看到相同的资源集合。
public static class ResourceResolver
{
	public static ResourceSnapshot Resolve(
		string cwd,
		string? agentDir = null,
		bool includeProject = true,
		CodingAgentSettings? settings = null)
	{
		var resolvedAgentDir = agentDir ?? SessionPaths.GetAgentDir();
		var diagnostics = new List<string>();
		var packages = PiPackageStore.Discover(cwd, resolvedAgentDir, includeProject);
		settings ??= CodingAgentSettings.Load(cwd, resolvedAgentDir, includeProject);

		return new ResourceSnapshot
		{
			Skills = Skills.Skills.Discover(SkillRoots(cwd, resolvedAgentDir, includeProject, packages)),
			Templates = Prompts.PromptTemplates.WithSubagentBuiltins(
				Prompts.PromptTemplates.Discover(PromptRoots(cwd, resolvedAgentDir, includeProject, packages))),
			Themes = ThemeCatalog.Discover(ThemeRoots(cwd, resolvedAgentDir, includeProject, packages), diagnostics),
			Packages = packages,
			// 只发现、不执行：资源快照要在会话建立之前就能列出扩展并说明状态，
			// 而执行扩展代码属于宿主的事（ExtensionHost）。settings 的显式路径由宿主加载，
			// 这里只跟约定目录和过滤规则，避免打开设置时顺手物化远端包。
			Extensions = [.. PiWeft.CodingAgent.Extensions.ExtensionDescriptor
				.Discover(cwd, includeProject, resolvedAgentDir, rules: settings.ExtensionRules)
				.Select(static descriptor => new ExtensionEntry(
					Path.GetFileNameWithoutExtension(descriptor.FilePath),
					descriptor.FilePath,
					descriptor.Origin,
					Loaded: false,
					Note: descriptor.Supported ? string.Empty : descriptor.UnsupportedReason ?? "未加载",
					Enabled: descriptor.Enabled))],
			Diagnostics = diagnostics,
		};
	}

	public static IReadOnlyList<ResourceRoot> SkillRoots(
		string cwd,
		string? agentDir = null,
		bool includeProject = true,
		IReadOnlyList<PiPackage>? packages = null)
	{
		var resolvedAgentDir = agentDir ?? SessionPaths.GetAgentDir();
		var roots = new List<ResourceRoot>();

		// rank 0/2：settings 里显式声明的技能路径。
		AddDeclared(roots, cwd, resolvedAgentDir, includeProject, static file => file.Skills, ResourcePaths.SkillsDirectoryName);

		if (includeProject)
		{
			// rank 1：项目自动发现（逐级向上，遇到仓库根停止）。
			foreach (var directory in ResourcePaths.AncestorDirectories(cwd))
			{
				var origin = ResourceOrigin.ProjectAuto.WithRoot(directory);
				roots.Add(new ResourceRoot(ResourcePaths.ProjectDirectory(directory, ResourcePaths.SkillsDirectoryName), origin));
				roots.Add(new ResourceRoot(Path.Combine(directory, ResourcePaths.AgentsDirectoryName, ResourcePaths.SkillsDirectoryName), origin));
			}
		}

		// rank 3：用户级约定目录。
		roots.Add(new ResourceRoot(
			ResourcePaths.UserDirectory(resolvedAgentDir, ResourcePaths.SkillsDirectoryName),
			ResourceOrigin.UserAuto));
		roots.Add(new ResourceRoot(ResourcePaths.UserAgentsSkillsDirectory(), ResourceOrigin.UserAuto));

		AddPackageRoots(roots, packages, ResourcePaths.SkillsDirectoryName);

		// 按 rank 稳定排序：settings 里用户声明的资源（rank 2）必须排在项目自动发现（rank 1）之后，
		// 否则「先发现者获胜」会让用户级资源压过项目级同名资源。
		return [.. roots.OrderBy(static root => root.Origin.Rank)];
	}

	public static IReadOnlyList<ResourceRoot> PromptRoots(
		string cwd,
		string? agentDir = null,
		bool includeProject = true,
		IReadOnlyList<PiPackage>? packages = null)
	{
		var resolvedAgentDir = agentDir ?? SessionPaths.GetAgentDir();
		var roots = new List<ResourceRoot>();

		AddDeclared(roots, cwd, resolvedAgentDir, includeProject, static file => file.Prompts, ResourcePaths.PromptsDirectoryName);

		if (includeProject)
		{
			var full = Path.GetFullPath(cwd);
			roots.Add(new ResourceRoot(
				ResourcePaths.ProjectDirectory(full, ResourcePaths.PromptsDirectoryName),
				ResourceOrigin.ProjectAuto.WithRoot(full)));
		}

		roots.Add(new ResourceRoot(
			ResourcePaths.UserDirectory(resolvedAgentDir, ResourcePaths.PromptsDirectoryName),
			ResourceOrigin.UserAuto));

		AddPackageRoots(roots, packages, ResourcePaths.PromptsDirectoryName);

		// 同上：用户声明的模板不能压过项目 `.pi/prompts` 里的同名模板。
		return [.. roots.OrderBy(static root => root.Origin.Rank)];
	}

	public static IReadOnlyList<ResourceRoot> ThemeRoots(
		string cwd,
		string? agentDir = null,
		bool includeProject = true,
		IReadOnlyList<PiPackage>? packages = null)
	{
		var resolvedAgentDir = agentDir ?? SessionPaths.GetAgentDir();
		var roots = new List<ResourceRoot>();

		AddDeclared(roots, cwd, resolvedAgentDir, includeProject, static file => file.Themes, ResourcePaths.ThemesDirectoryName);

		if (includeProject)
		{
			var full = Path.GetFullPath(cwd);
			roots.Add(new ResourceRoot(
				ResourcePaths.ProjectDirectory(full, ResourcePaths.ThemesDirectoryName),
				ResourceOrigin.ProjectAuto.WithRoot(full)));
		}

		roots.Add(new ResourceRoot(
			ResourcePaths.UserDirectory(resolvedAgentDir, ResourcePaths.ThemesDirectoryName),
			ResourceOrigin.UserAuto));

		AddPackageRoots(roots, packages, ResourcePaths.ThemesDirectoryName);

		// 同上：用户声明的主题不能压过项目 `.pi/themes` 里的同名主题。
		return [.. roots.OrderBy(static root => root.Origin.Rank)];
	}

	private static void AddPackageRoots(List<ResourceRoot> roots, IReadOnlyList<PiPackage>? packages, string kind)
	{
		if (packages is null)
		{
			return;
		}

		foreach (var package in packages)
		{
			foreach (var path in package.ResolveResourcePaths(kind))
			{
				roots.Add(new ResourceRoot(path, package.Origin));
			}
		}
	}

	/// 把 settings 里显式声明的资源路径展开成根目录。项目设置里的相对路径相对项目 <c>.pi</c>，
	/// 用户设置里的相对路径相对 agent 目录（与 pi 一致）。
	private static void AddDeclared(
		List<ResourceRoot> roots,
		string cwd,
		string agentDir,
		bool includeProject,
		Func<SettingsFile, List<string>?> selector,
		string kind)
	{
		// 项目（rank 0）在前，用户（rank 2）在后。
		if (includeProject)
		{
			Declare(
				CodingAgentSettings.ProjectPath(cwd),
				Path.Combine(Path.GetFullPath(cwd), SessionPaths.ConfigDirName),
				new ResourceOrigin(ResourceScope.Project, Path.GetFullPath(cwd), 0));
		}

		Declare(
			CodingAgentSettings.GlobalPath(agentDir),
			agentDir,
			new ResourceOrigin(ResourceScope.User, agentDir, 2));

		void Declare(string settingsPath, string baseDirectory, ResourceOrigin origin)
		{
			var file = CodingAgentSettings.ReadFile(settingsPath);
			var declared = file is null ? null : selector(file);
			if (declared is null)
			{
				return;
			}

			foreach (var value in declared)
			{
				var trimmed = value.Trim();
				if (trimmed.Length == 0)
				{
					continue;
				}

				var expanded = UserPath.ExpandTilde(trimmed);
				var path = Path.IsPathRooted(expanded) ? expanded : Path.Combine(baseDirectory, expanded);
				try
				{
					roots.Add(new ResourceRoot(Path.GetFullPath(path), origin));
				}
				catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
				{
					// 无效路径直接跳过：单个坏条目不应让整个会话创建失败。
				}
			}
		}
	}
}
