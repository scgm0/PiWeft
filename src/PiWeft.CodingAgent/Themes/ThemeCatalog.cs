using PiWeft.CodingAgent.Resources;

namespace PiWeft.CodingAgent.Themes;

public sealed record ThemeEntry
{
	public required string Name { get; init; }

	public required string FilePath { get; init; }

	public required ResourceOrigin Origin { get; init; }

	public PiTheme? Theme { get; init; }

	public string? Error { get; init; }

	public bool IsValid => Theme is not null;

	public string SourceLabel => Origin.Display;
}

/// 主题发现：扫描用户级与项目级 <c>themes</c> 目录，解析 pi 主题 JSON。
/// 同名先到先得（项目优先），冲突记入诊断。
public static class ThemeCatalog
{
	public const string SystemThemeName = "system";

	public const string DarkThemeName = "dark";

	public const string LightThemeName = "light";

	public static readonly IReadOnlySet<string> BuiltInNames = new HashSet<string>(StringComparer.Ordinal)
	{
		SystemThemeName,
		DarkThemeName,
		LightThemeName,
	};

	public static IReadOnlyList<ThemeEntry> Discover(string cwd, string? agentDir = null, bool includeProject = true)
	{
		// 便捷重载也要带上包资源：桌面主题选择器与设置页「资源」分区走的是这一条路径。
		var resolvedAgentDir = agentDir ?? Session.SessionPaths.GetAgentDir();
		var packages = Resources.PiPackageStore.Discover(cwd, resolvedAgentDir, includeProject);
		return Discover(ResourceResolver.ThemeRoots(cwd, resolvedAgentDir, includeProject, packages));
	}

	public static IReadOnlyList<ThemeEntry> Discover(
		IReadOnlyList<ResourceRoot> roots,
		List<string>? diagnostics = null)
	{
		var entries = new List<ThemeEntry>();
		var seenNames = new HashSet<string>(StringComparer.Ordinal);

		foreach (var (root, origin) in roots)
		{
			if (File.Exists(root))
			{
				AddEntry(root, origin);
				continue;
			}

			foreach (var file in EnumerateFiles(root))
			{
				AddEntry(file, origin);
			}
		}

		return entries;

		void AddEntry(string file, ResourceOrigin origin)
		{
			var fileName = Path.GetFileNameWithoutExtension(file);
			var theme = Parse(file, origin, out var error, out _);
			var name = theme?.Name ?? fileName;

			if (name.Equals(SystemThemeName, StringComparison.Ordinal))
			{
				// system 是保留名，自定义主题不能占用它。
				return;
			}

			if (!seenNames.Add(name))
			{
				diagnostics?.Add($"主题 {name} 重复，已忽略 {file}");
				return;
			}

			if (theme is null && error is not null)
			{
				diagnostics?.Add($"主题 {file} 无效：{error}");
			}

			entries.Add(new ThemeEntry
			{
				Name = name,
				FilePath = file,
				Origin = origin,
				Theme = theme,
				Error = error,
			});
		}
	}

	public static PiTheme? Load(string name, string cwd, string? agentDir = null, bool includeProject = true)
	{
		if (BuiltInNames.Contains(name))
		{
			return null;
		}

		foreach (var entry in Discover(cwd, agentDir, includeProject))
		{
			if (string.Equals(entry.Name, name, StringComparison.Ordinal))
			{
				return entry.Theme;
			}
		}

		return null;
	}

	public static PiTheme? Parse(string path, ResourceOrigin origin, out string? error, out IReadOnlyList<string> warnings)
	{
		string json;
		try
		{
			json = File.ReadAllText(path);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			error = $"无法读取主题文件：{exception.Message}";
			warnings = [];
			return null;
		}

		return PiTheme.Parse(json, path, origin, out error, out warnings);
	}

	private static IEnumerable<string> EnumerateFiles(string directory)
	{
		if (!Directory.Exists(directory))
		{
			yield break;
		}

		string[] files;
		try
		{
			// 物化到数组：EnumerateFiles 是惰性的，异常在遍历时才抛，只包住创建等于没有保护。
			files = [.. Directory.EnumerateFiles(directory, "*.json")];
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			yield break;
		}

		foreach (var file in files)
		{
			yield return Path.GetFullPath(file);
		}
	}
}
