namespace PiWeft.CodingAgent.Resources;

/// <summary>一条已声明的包来源（来自 settings 的 <c>packages</c>）。</summary>
public sealed record PackageSource
{
	/// <summary>原始来源字符串，例如 <c>npm:@scope/pkg@1.0.0</c> 或 <c>./local-pkg</c>。</summary>
	public required string Value { get; init; }

	/// <summary>声明来源的作用域；决定相对路径从哪里解析。</summary>
	public required ResourceScope Scope { get; init; }

	/// <summary>相对路径的基准目录：用户级为 agent 目录，项目级为项目 <c>.pi</c>。</summary>
	public required string ScopeRoot { get; init; }
}

/// <summary>
/// 一个已安装的 pi 包：包根目录、<c>package.json</c> 的 <c>pi</c> 清单，以及它声明的资源路径。
/// </summary>
public sealed record PiPackage
{
	/// <summary>包名（<c>package.json</c> 的 <c>name</c>，缺失时用目录名）。</summary>
	public required string Name { get; init; }

	/// <summary>原始来源字符串。</summary>
	public required string Source { get; init; }

	/// <summary>包根目录（绝对路径）。</summary>
	public required string Root { get; init; }

	/// <summary>包安装位置对应的作用域。</summary>
	public required ResourceScope Scope { get; init; }

	public PiPackageManifest Manifest { get; init; } = new();

	/// <summary>本包资源的来源标签。</summary>
	public ResourceOrigin Origin => new(ResourceScope.Package, Root, ResourceOrigin.PackageRank, Name);

	/// <summary>
	/// 展开某类资源的具体路径（文件或目录）。清单声明了该类型时用声明值（支持 glob），
	/// 否则回退到包的约定目录 <c>&lt;root&gt;/&lt;kind&gt;</c>。
	/// </summary>
	public IReadOnlyList<string> ResolveResourcePaths(string kind)
	{
		var declared = kind switch
		{
			ResourcePaths.SkillsDirectoryName => Manifest.Skills,
			ResourcePaths.PromptsDirectoryName => Manifest.Prompts,
			ResourcePaths.ThemesDirectoryName => Manifest.Themes,
			ResourcePaths.ExtensionsDirectoryName => Manifest.Extensions,
			_ => [],
		};

		if (declared.Count == 0)
		{
			var conventional = Path.Combine(Root, kind);
			return Directory.Exists(conventional) ? [conventional] : [];
		}

		var resolved = new List<string>(declared.Count);
		foreach (var pattern in declared)
		{
			resolved.AddRange(PathGlob.Expand(Root, pattern));
		}

		return resolved;
	}
}
