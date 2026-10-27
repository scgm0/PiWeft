using PiWeft.CodingAgent.Session;

namespace PiWeft.CodingAgent.Extensions;

/// <summary>扩展源文件的语言类别。</summary>
public enum ExtensionLanguage
{
	/// <summary>纯 JavaScript（<c>.js</c> / <c>.mjs</c> / <c>.cjs</c>），宿主直接执行。</summary>
	Javascript,

	/// <summary>TypeScript（<c>.ts</c> / <c>.mts</c> / <c>.cts</c> / <c>.tsx</c>）：需要先转译，当前宿主不支持。</summary>
	TypeScript,
}

/// <summary>
/// 一个被发现到的扩展文件。<see cref="Supported"/> 为 <see langword="false"/> 的条目仍然会进列表 ——
/// 界面上要能告诉用户「这个扩展没加载，原因是 X」，静默跳过是最难排查的行为。
/// </summary>
public sealed record ExtensionDescriptor
{
	/// <summary>pi 约定的扩展目录名。</summary>
	public const string DirectoryName = "extensions";

	/// <summary>扩展源文件的绝对路径。叫 <c>FilePath</c> 而不是 <c>Path</c>：
	/// 记录内的成员会把 <c>Path</c> 解析到这个属性，遮蔽 <c>System.IO.Path</c> 类型，
	/// 于是同一个文件里所有 <c>Path.GetFileName(...)</c> 都编不过（或更糟：解析到别处）。</summary>
	public required string FilePath { get; init; }

	/// <summary>来源标签：全局 / 项目 / 包名。</summary>
	public required string Origin { get; init; }

	/// <summary>作用域。项目扩展参与 <c>trust.json</c> 门控：不受信时不加载。</summary>
	public required bool Project { get; init; }

	public required ExtensionLanguage Language { get; init; }

	public bool Supported => Language == ExtensionLanguage.Javascript;

	/// <summary>不支持时给用户的说明；支持时为 <see langword="null"/>。</summary>
	public string? UnsupportedReason => Language == ExtensionLanguage.TypeScript
		? "当前扩展宿主只执行 JavaScript；TypeScript 扩展需要先在构建期转译成 JS（本端口不内置 TS 转译器，见 docs/01-parity.md 的 PI-01）。"
		: null;

	/// <summary>全局扩展目录：<c>&lt;agentDir&gt;/extensions</c>。</summary>
	public static string GlobalDirectory(string? agentDir = null) =>
		Path.Combine(agentDir ?? SessionPaths.GetAgentDir(), DirectoryName);

	/// <summary>项目扩展目录：<c>&lt;cwd&gt;/.pi/extensions</c>。</summary>
	public static string ProjectDirectory(string cwd) =>
		Path.Combine(Path.GetFullPath(cwd), SessionPaths.ConfigDirName, DirectoryName);

	public static ExtensionLanguage LanguageOf(string path) =>
		Path.GetExtension(path).ToLowerInvariant() switch
		{
			".ts" or ".mts" or ".cts" or ".tsx" => ExtensionLanguage.TypeScript,
			_ => ExtensionLanguage.Javascript,
		};

	/// <summary>
	/// 发现全局 + 项目扩展。项目部分只在受信任时纳入 —— 扩展能注册工具与命令并影响系统提示词，
	/// 让一个克隆下来的仓库自带扩展就生效，等于让仓库在用户机器上执行代码。
	/// </summary>
	/// <param name="cwd">会话工作目录。</param>
	/// <param name="trusted">项目是否受信任。</param>
	/// <param name="agentDir">覆盖 agent 目录（测试与多环境用）。</param>
	public static IReadOnlyList<ExtensionDescriptor> Discover(
		string cwd,
		bool trusted = true,
		string? agentDir = null)
	{
		var found = new List<ExtensionDescriptor>();
		Collect(found, GlobalDirectory(agentDir), Origin: "全局", project: false);
		if (trusted)
		{
			Collect(found, ProjectDirectory(cwd), Origin: "项目", project: true);
		}

		return found;
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

	/// <summary>可加载的扩展后缀（含不支持、但要能点名解释原因的 TypeScript）。</summary>
	private static readonly HashSet<string> ExtensionExtensions = new(StringComparer.OrdinalIgnoreCase)
	{
		".js", ".mjs", ".cjs", ".ts", ".mts", ".cts", ".tsx",
	};

	/// <summary>
	/// 是不是一个扩展源文件。<c>.d.ts</c> 是类型声明、不是可执行扩展；目录里混着的
	/// README / 配置文件也不该被当成「加载失败的扩展」报给用户。
	/// </summary>
	private static bool IsExtensionFile(string file)
	{
		var name = Path.GetFileName(file);
		return !name.EndsWith(".d.ts", StringComparison.OrdinalIgnoreCase)
			&& ExtensionExtensions.Contains(Path.GetExtension(name));
	}
}
