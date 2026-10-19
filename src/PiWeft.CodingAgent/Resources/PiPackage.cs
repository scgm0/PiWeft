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

	public bool Autoload { get; init; } = true;

	public IReadOnlyList<string>? Extensions { get; init; }

	public IReadOnlyList<string>? Skills { get; init; }

	public IReadOnlyList<string>? Prompts { get; init; }

	public IReadOnlyList<string>? Themes { get; init; }

	public IReadOnlyList<string>? FiltersFor(string kind) => kind switch
	{
		ResourcePaths.ExtensionsDirectoryName => Extensions,
		ResourcePaths.SkillsDirectoryName => Skills,
		ResourcePaths.PromptsDirectoryName => Prompts,
		ResourcePaths.ThemesDirectoryName => Themes,
		_ => null,
	};
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

	/// <summary>settings 对象形式的资源过滤；null 表示完整加载。</summary>
	public PackageSource? Filters { get; init; }

	/// <summary>本包资源的来源标签。</summary>
	public ResourceOrigin Origin => new(ResourceScope.Package, Root, ResourceOrigin.PackageRank, Name);

	/// <summary>
	/// 展开某类资源的具体路径（文件或目录）。清单声明了该类型时用声明值（支持 glob），
	/// 否则回退到包的约定目录 <c>&lt;root&gt;/&lt;kind&gt;</c>。
	/// </summary>
	public IReadOnlyList<string> ResolveResourcePaths(string kind)
	{
		var autoLoad = Filters?.Autoload ?? true;
		var declared = kind switch
		{
			ResourcePaths.SkillsDirectoryName => Manifest.Skills,
			ResourcePaths.PromptsDirectoryName => Manifest.Prompts,
			ResourcePaths.ThemesDirectoryName => Manifest.Themes,
			ResourcePaths.ExtensionsDirectoryName => Manifest.Extensions,
			_ => [],
		};

		var filters = Filters?.FiltersFor(kind);
		if (!autoLoad && filters is null)
		{
			return [];
		}

		var resolved = new List<string>();
		if (declared.Count == 0)
		{
			var conventional = Path.Combine(Root, kind);
			if (Directory.Exists(conventional))
			{
				resolved.Add(conventional);
			}
		}
		else
		{
			foreach (var pattern in declared)
			{
				resolved.AddRange(PathGlob.Expand(Root, pattern));
			}
		}

		if (filters is not null)
		{
			resolved = [.. resolved.Where(path => MatchesFilters(path, filters))];
		}

		return resolved;
	}

	/// <summary>按包过滤规则匹配一个已解析资源路径。过滤器只能收窄，不能暴露清单未声明的资源。</summary>
	private bool MatchesFilters(string path, IReadOnlyList<string> filters)
	{
		var relative = Path.GetRelativePath(Root, path).Replace('\\', '/');
		var included = false;

		foreach (var raw in filters)
		{
			var pattern = raw.Trim().Replace('\\', '/');
			if (pattern.Length == 0) continue;

			if (pattern.StartsWith('!') || pattern.StartsWith('-'))
			{
				if (GlobMatches(relative, pattern[1..])) return false;
				continue;
			}

			var exact = pattern.StartsWith('+');
			var body = exact ? pattern[1..] : pattern;
			if (GlobMatches(relative, body)) included = true;
		}

		// 只有排除规则时默认包含；出现任何包含规则后必须命中至少一个。
		var hasInclude = filters.Any(static value =>
			value.Length > 0 && value[0] is not ('!' or '-'));
		return hasInclude ? included : true;
	}

	private static bool GlobMatches(string value, string pattern)
	{
		var regex = "^" + string.Concat(pattern.Select(static c => c switch
		{
			'*' => ".*",
			'?' => ".",
			_ => System.Text.RegularExpressions.Regex.Escape(c.ToString()),
		})) + "$";
		return System.Text.RegularExpressions.Regex.IsMatch(
			value, regex, System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
	}

}
