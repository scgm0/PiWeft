using System.Text.Json.Nodes;
using PiWeft.CodingAgent.Session;
using PiWeft.CodingAgent.Settings;

namespace PiWeft.CodingAgent.Resources;

/// <summary>
/// pi 包的来源解析与发现：把 settings 里的 <c>packages</c> 条目（npm / git / 本地路径）
/// 映射到磁盘上的包根目录，并读取其 <c>package.json</c> 清单。
/// </summary>
public static class PiPackageStore
{
	/// <summary>settings 里声明的包来源，去重后按「项目优先」排序。</summary>
	public static IReadOnlyList<PackageSource> DeclaredSources(
		string cwd,
		string? agentDir = null,
		bool includeProject = true)
	{
		var resolvedAgentDir = agentDir ?? SessionPaths.GetAgentDir();
		var sources = new List<PackageSource>();
		// 去重键带作用域：同一个包可以同时声明在项目与用户 settings 里，
		// 只用来源去重会让用户作用域的那条消失，pi update（用户）就永远轮不到它。
		var seen = new HashSet<(ResourceScope Scope, string Value)>();

		// 项目设置里的来源排在前面，与资源的「项目优先」一致。
		if (includeProject)
		{
			Add(
				CodingAgentSettings.ProjectPath(cwd),
				ResourceScope.Project,
				Path.Combine(Path.GetFullPath(cwd), SessionPaths.ConfigDirName));
		}

		Add(CodingAgentSettings.GlobalPath(resolvedAgentDir), ResourceScope.User, resolvedAgentDir);
		return sources;

		void Add(string settingsPath, ResourceScope scope, string scopeRoot)
		{
			var declared = CodingAgentSettings.ReadFile(settingsPath)?.Packages;
			if (declared is null)
			{
				return;
			}

			foreach (var declaration in declared)
			{
				var trimmed = declaration.Source.Trim();
				if (trimmed.Length == 0 || !seen.Add((scope, trimmed)))
				{
					continue;
				}

				sources.Add(new PackageSource
				{
					Value = trimmed,
					Scope = scope,
					ScopeRoot = scopeRoot,
					Autoload = declaration.Autoload,
					Extensions = declaration.Extensions,
					Skills = declaration.Skills,
					Prompts = declaration.Prompts,
					Themes = declaration.Themes,
				});
			}
		}
	}

	/// <summary>发现已安装的包；包根不存在（未安装）时跳过。</summary>
	public static IReadOnlyList<PiPackage> Discover(
		string cwd,
		string? agentDir = null,
		bool includeProject = true)
	{
		var packages = new List<PiPackage>();
		foreach (var source in DeclaredSources(cwd, agentDir, includeProject))
		{
			var root = ResolveRoot(source);
			if (root is null || !Directory.Exists(root))
			{
				continue;
			}

			packages.Add(Load(source, root));
		}

		return packages;
	}

	/// <summary>读取一个已安装包的清单。</summary>
	public static PiPackage Load(PackageSource source, string root) => new()
	{
		Name = ReadName(root) ?? Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, '/')),
		Source = source.Value,
		Root = root,
		Scope = source.Scope,
		Manifest = PiPackageManifest.Load(root),
		Filters = source.FiltersFor(ResourcePaths.SkillsDirectoryName) is null &&
			source.FiltersFor(ResourcePaths.PromptsDirectoryName) is null &&
			source.FiltersFor(ResourcePaths.ThemesDirectoryName) is null &&
			source.FiltersFor(ResourcePaths.ExtensionsDirectoryName) is null &&
			source.Autoload
				? null
				: source,
	};

	/// <summary>
	/// 把来源字符串解析成包根目录的绝对路径；无法识别时返回 <c>null</c>。
	/// 仅做路径推导，不执行安装动作。
	/// </summary>
	public static string? ResolveRoot(PackageSource source)
	{
		var value = source.Value;

		if (value.StartsWith("npm:", StringComparison.Ordinal))
		{
			var name = NpmPackageName(value[4..]);
			return name is null
				? null
				: Path.Combine(source.ScopeRoot, ResourcePaths.NpmDirectoryName, "node_modules", name);
		}

		if (value.StartsWith("git:", StringComparison.Ordinal) ||
			value.StartsWith("http://", StringComparison.Ordinal) ||
			value.StartsWith("https://", StringComparison.Ordinal))
		{
			var slug = GitSlug(value);
			return slug is null ? null : Path.Combine(source.ScopeRoot, "git", slug);
		}

		// 本地路径：绝对路径原样，相对路径从作用域根解析。
		var local = Path.IsPathRooted(value) ? value : Path.Combine(source.ScopeRoot, value);
		try
		{
			return Path.GetFullPath(local);
		}
		catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
		{
			return null;
		}
	}

	/// <summary>从 <c>npm:</c> 来源里取出包名（保留 scope，去掉版本）。</summary>
	public static string? NpmPackageName(string spec)
	{
		var trimmed = spec.Trim();
		if (trimmed.Length == 0)
		{
			return null;
		}

		// scoped 包（@scope/name）里的第一个 '@' 不是版本分隔符。
		var separator = trimmed.StartsWith('@')
			? trimmed.IndexOf('@', 1)
			: trimmed.IndexOf('@');
		var name = separator > 0 ? trimmed[..separator] : trimmed;
		return name.Length == 0 ? null : name;
	}

	/// <summary>git 来源在磁盘上的目录名：去掉协议与 ref，并把分隔符换成 '-'。</summary>
	public static string? GitSlug(string spec)
	{
		var value = spec;
		if (value.StartsWith("git:", StringComparison.Ordinal))
		{
			value = value[4..];
		}

		value = value.Replace("https://", string.Empty, StringComparison.Ordinal)
			.Replace("http://", string.Empty, StringComparison.Ordinal)
			.Replace("git@", string.Empty, StringComparison.Ordinal);

		// 去掉 @ref 后缀（host/path@ref）。
		var at = value.LastIndexOf('@');
		if (at > 0 && !value[..at].Contains('/'))
		{
			at = -1;
		}

		if (at > 0)
		{
			value = value[..at];
		}

		var slug = value.TrimEnd('/').Replace(':', '-').Replace('/', '-');
		return slug.Length == 0 ? null : slug;
	}

	private static string? ReadName(string root)
	{
		var path = Path.Combine(root, ResourcePaths.PackageManifestName);
		if (!File.Exists(path))
		{
			return null;
		}

		try
		{
			return (JsonNode.Parse(File.ReadAllText(path)) as JsonObject)?["name"] is JsonValue value &&
				value.GetValueKind() == System.Text.Json.JsonValueKind.String
				? value.GetValue<string>()
				: null;
		}
		catch (Exception exception) when (exception is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
		{
			return null;
		}
	}
}
