namespace PiWeft.CodingAgent.Resources;

public sealed record PackageSource
{
	public required string Value { get; init; }

	public required ResourceScope Scope { get; init; }

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

/// 一个已安装的 pi 包：包根目录、<c>package.json</c> 的 <c>pi</c> 清单，以及它声明的资源路径。
public sealed record PiPackage
{
	public required string Name { get; init; }

	public required string Source { get; init; }

	public required string Root { get; init; }

	public required ResourceScope Scope { get; init; }

	public PiPackageManifest Manifest { get; init; } = new();

	public PackageSource? Filters { get; init; }

	public ResourceOrigin Origin => new(ResourceScope.Package, Root, ResourceOrigin.PackageRank, Name);

	/// 展开某类资源的具体路径（文件或目录）。清单声明了该类型时用声明值（支持 glob），
	/// 否则回退到包的约定目录 <c>&lt;root&gt;/&lt;kind&gt;</c>。
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
