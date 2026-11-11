using PiWeft.CodingAgent.Session;

namespace PiWeft.CodingAgent.Extensions;

public enum ExtensionLanguage
{
	Javascript,

	TypeScript,
}

/// 一个被发现到的扩展文件。<see cref="Supported"/> 为 <see langword="false"/> 的条目也进列表：
/// 界面要能告诉用户「这个扩展没加载，原因是 X」，静默跳过最难排查。
public sealed record ExtensionDescriptor
{
	public const string DirectoryName = "extensions";

	/// 扩展源文件的绝对路径。叫 <c>FilePath</c>，不叫 <c>Path</c>：
	/// 记录内的成员会把 <c>Path</c> 解析到这个属性，遮蔽 <c>System.IO.Path</c> 类型，
	/// 于是同一个文件里所有 <c>Path.GetFileName(...)</c> 都编不过（或更糟：解析到别处）。
	public required string FilePath { get; init; }

	public required string Origin { get; init; }

	public required bool Project { get; init; }

	public required ExtensionLanguage Language { get; init; }

	public bool Builtin { get; init; }

	public bool Missing { get; init; }

	/// 设置里的 <c>+</c>/<c>-</c> 规则是否允许加载。停用的扩展仍进入发现结果，
	/// 界面要能看到它并重新打开。
	public bool Enabled { get; init; } = true;

	/// 物化失败的原因（离线且无缓存、npm/git 命令失败）。为空时按常规规则判断。
	public string? Error { get; init; }

	public bool Supported => Language == ExtensionLanguage.Javascript && !Missing && Error is null;

	public string? UnsupportedReason => Error ?? (Missing
		? "扩展文件或目录不存在。"
		: Language == ExtensionLanguage.TypeScript
			? "当前扩展宿主只执行 JavaScript；TypeScript 扩展需要先在构建期转译成 JS（本端口不内置 TS 转译器，见 docs/01-parity.md 的 PI-01）。"
			: null);

	public static string GlobalDirectory(string? agentDir = null) =>
		Path.Combine(agentDir ?? SessionPaths.GetAgentDir(), DirectoryName);

	public static string ProjectDirectory(string cwd) =>
		Path.Combine(Path.GetFullPath(cwd), SessionPaths.ConfigDirName, DirectoryName);

	public static ExtensionLanguage LanguageOf(string path) =>
		Path.GetExtension(path).ToLowerInvariant() switch
		{
			".ts" or ".mts" or ".cts" or ".tsx" => ExtensionLanguage.TypeScript,
			_ => ExtensionLanguage.Javascript,
		};

	/// 发现全局与项目扩展。项目部分只在受信任时纳入：扩展能注册工具与命令并影响系统提示词，
	/// 让一个克隆下来的仓库自带扩展就生效，等于让仓库在用户机器上执行代码。
	/// <param name="cwd">会话工作目录。</param>
	/// <param name="trusted">项目是否受信任。</param>
	/// <param name="agentDir">覆盖 agent 目录（测试与多环境用）。</param>
	/// <param name="explicitPaths">pi 的 <c>-e/--extension</c> 与 <c>settings.extensions</c>。
	/// 显式路径不受项目信任门控：它是用户自己写下的，不是仓库自带的。</param>
	/// <param name="includeDiscovered">是否按约定目录发现。<c>-ne/--no-extensions</c> 关掉发现，
	/// 但仍加载显式路径（与 pi 一致）。</param>
	/// <param name="rules">settings 里的过滤规则；停用的条目仍然返回，只是 <see cref="Enabled"/> 为 false。</param>
	public static IReadOnlyList<ExtensionDescriptor> Discover(
		string cwd,
		bool trusted = true,
		string? agentDir = null,
		IReadOnlyList<string>? explicitPaths = null,
		bool includeDiscovered = true,
		ExtensionRules? rules = null)
	{
		var found = new List<ExtensionDescriptor>();
		if (includeDiscovered)
		{
			Collect(found, GlobalDirectory(agentDir), Origin: "全局", project: false);
			if (trusted)
			{
				Collect(found, ProjectDirectory(cwd), Origin: "项目", project: true);
			}
		}

		foreach (var path in explicitPaths ?? [])
		{
			CollectExplicit(found, path, cwd, agentDir);
		}

		var deduped = Dedupe(found);
		if (rules is null || rules.IsEmpty)
		{
			return deduped;
		}

		// 停用的条目保留在列表里，界面据此画开关；真正加载时由宿主跳过。
		return
		[
			.. deduped.Select(descriptor => descriptor with
			{
				Enabled = descriptor.Builtin
					? rules.IsBuiltinEnabled(descriptor.FilePath["builtin:".Length..])
					: rules.IsEnabled(descriptor.FilePath),
			}),
		];
	}

	/// 同一个文件既被约定目录发现、又被 <c>-e</c> 指到时只加载一次（pi 按解析后的路径去重）。
	/// 加载两份会得到两个 Jint 引擎、重复的工具与命令；工具表会静默留下第一份，
	/// 命令表却两份都在。
	private static List<ExtensionDescriptor> Dedupe(List<ExtensionDescriptor> found)
	{
		if (found.Count <= 1)
		{
			return found;
		}

		var seen = new HashSet<string>(
			OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
				? StringComparer.OrdinalIgnoreCase
				: StringComparer.Ordinal);
		var deduped = new List<ExtensionDescriptor>(found.Count);
		foreach (var descriptor in found)
		{
			var key = descriptor.Builtin ? descriptor.FilePath : FullPathOrRaw(descriptor.FilePath);
			if (seen.Add(key))
			{
				deduped.Add(descriptor);
			}
		}

		return deduped;
	}

	private static string FullPathOrRaw(string path)
	{
		try
		{
			return Path.GetFullPath(path);
		}
		catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
		{
			return path;
		}
	}

	private static void CollectExplicit(List<ExtensionDescriptor> into, string raw, string cwd, string? agentDir)
	{
		var value = raw.Trim();
		if (value.Length == 0)
		{
			return;
		}

		if (value.StartsWith("builtin:", StringComparison.Ordinal))
		{
			into.Add(new ExtensionDescriptor
			{
				FilePath = value,
				Origin = "内置",
				Project = false,
				Language = ExtensionLanguage.Javascript,
				Builtin = true,
			});
			return;
		}

		// pi 的 `-e npm:<pkg>[@ver]` / `-e git:<repo>@<ref>`：先物化到 agent 目录再按目录加载。
		if (ExtensionPackageCache.IsPackageSpec(value))
		{
			var packageRoot = ExtensionPackageCache.Resolve(value, agentDir, out var packageError);
			if (packageRoot is null)
			{
				into.Add(new ExtensionDescriptor
				{
					FilePath = value,
					Origin = "显式",
					Project = false,
					Language = ExtensionLanguage.Javascript,
					Error = packageError ?? "无法物化扩展包",
				});
				return;
			}

			Collect(into, packageRoot, Origin: "显式", project: false);
			return;
		}

		// pi 的 settings.extensions 把相对路径解析到各自作用域的根（用户级是 agentDir）；
		// 这里看两处再判定缺失：先 cwd（CLI 的 -e 语义），再 agentDir（用户级设置）。
		var expanded = UserPath.ExpandTilde(value);
		var path = Path.IsPathRooted(expanded)
			? expanded
			: new[] { Path.Combine(cwd, expanded), Path.Combine(agentDir ?? SessionPaths.GetAgentDir(), expanded) }
				.FirstOrDefault(candidate => File.Exists(candidate) || Directory.Exists(candidate))
				?? Path.Combine(cwd, expanded);

		if (Directory.Exists(path))
		{
			Collect(into, path, Origin: "显式", project: false);
			return;
		}

		if (File.Exists(path))
		{
			into.Add(new ExtensionDescriptor
			{
				FilePath = path,
				Origin = "显式",
				Project = false,
				Language = LanguageOf(path),
			});
			return;
		}

		into.Add(new ExtensionDescriptor
		{
			FilePath = path,
			Origin = "显式",
			Project = false,
			Language = LanguageOf(path),
			Missing = true,
		});
	}

	private static void Collect(List<ExtensionDescriptor> into, string directory, string Origin, bool project)
	{
		if (!Directory.Exists(directory))
		{
			return;
		}

		string[] files;
		try
		{
			// 只收扩展能写的几种后缀；目录里混着的 .d.ts、README 等不该变成「加载失败的扩展」。
			files = Directory.GetFiles(directory, "*.*", SearchOption.AllDirectories)
				.Where(IsExtensionFile)
				.ToArray();
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return;
		}

		foreach (var path in files.OrderBy(static path => path, StringComparer.Ordinal))
		{
			into.Add(new ExtensionDescriptor
			{
				FilePath = path,
				Origin = Origin,
				Project = project,
				Language = LanguageOf(path),
			});
		}
	}

	private static readonly HashSet<string> ExtensionExtensions = new(StringComparer.OrdinalIgnoreCase)
	{
		".js", ".mjs", ".cjs", ".ts", ".mts", ".cts", ".tsx",
	};

	/// 是不是一个扩展源文件。<c>.d.ts</c> 是类型声明、不是可执行扩展；目录里混着的
	/// README / 配置文件也不该被当成「加载失败的扩展」报给用户。
	private static bool IsExtensionFile(string file)
	{
		var name = Path.GetFileName(file);
		return !name.EndsWith(".d.ts", StringComparison.OrdinalIgnoreCase)
			&& ExtensionExtensions.Contains(Path.GetExtension(name));
	}
}
