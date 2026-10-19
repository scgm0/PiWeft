using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using PiWeft.Ai;
using PiWeft.CodingAgent.Session;

namespace PiWeft.CodingAgent.Settings;

/// <summary>压缩调优参数，对应 pi 设置里的 <c>compaction</c> 段。</summary>
public sealed record CompactionSettings
{
	public bool Enabled { get; init; } = true;

	/// <summary>为下一次响应预留的令牌数。</summary>
	public int ReserveTokens { get; init; } = 16384;

	/// <summary>压缩时始终原样保留的近期令牌数。</summary>
	public int KeepRecentTokens { get; init; } = 20000;
}

/// <summary>分支摘要参数，对应 pi 的 <c>settings.branchSummary</c>。</summary>
public sealed record BranchSummarySettings
{
	/// <summary>摘要请求为上下文窗口预留的令牌数。</summary>
	public int ReserveTokens { get; init; } = 16384;

	/// <summary>跳过分支摘要提示，直接导航。</summary>
	public bool SkipPrompt { get; init; }
}

/// <summary>
/// 本端口读取的 pi <c>settings.json</c> 子集。未知键会被忽略，因此完整的 pi 设置文件也能
/// 正常加载而不报错。
/// </summary>
public sealed record CodingAgentSettings
{
	public string? DefaultProvider { get; init; }

	public string? DefaultModel { get; init; }

	/// <summary>取值 <c>off</c>、<c>minimal</c>…<c>max</c>。</summary>
	public string? DefaultThinkingLevel { get; init; }

	/// <summary>按 <c>provider/modelId</c> 覆盖启动思考级别。</summary>
	public IReadOnlyDictionary<string, string> ModelThinkingLevels { get; init; } =
		new Dictionary<string, string>(StringComparer.Ordinal);

	/// <summary>启动与轮换使用的模型模式；空表示全部可用模型。</summary>
	public IReadOnlyList<string> EnabledModels { get; init; } = [];

	/// <summary><c>all</c> 或 <c>one-at-a-time</c>。</summary>
	public string SteeringMode { get; init; } = "one-at-a-time";

	public string FollowUpMode { get; init; } = "one-at-a-time";

	public CompactionSettings Compaction { get; init; } = new();

	public BranchSummarySettings BranchSummary { get; init; } = new();

	/// <summary>自动重试策略，对应 pi 的 <c>settings.retry</c>。</summary>
	public RetryPolicy Retry { get; init; } = new();

	public bool EnableSkillCommands { get; init; } = true;

	/// <summary>包来源（npm / git / 本地路径），对应 pi 的 <c>settings.packages</c>。</summary>
	public IReadOnlyList<PackageDeclaration> Packages { get; init; } = [];

	/// <summary>额外的技能文件或目录，对应 pi 的 <c>settings.skills</c>。</summary>
	public IReadOnlyList<string> Skills { get; init; } = [];

	/// <summary>额外的提示词模板文件或目录，对应 pi 的 <c>settings.prompts</c>。</summary>
	public IReadOnlyList<string> Prompts { get; init; } = [];

	/// <summary>额外的主题文件或目录，对应 pi 的 <c>settings.themes</c>。</summary>
	public IReadOnlyList<string> Themes { get; init; } = [];

	/// <summary>
	/// 项目信任的默认决策：<c>always</c>、<c>never</c> 或 <c>ask</c>。
	/// 默认 <c>ask</c>（与 pi 一致）：交互场景弹卡询问，RPC/print 等无 UI 场景按不受信处理。
	/// </summary>
	public string DefaultProjectTrust { get; init; } = "ask";

	/// <summary>provider 请求超时，单位毫秒。</summary>
	public int? TimeoutMs { get; init; }

	/// <summary>bash 工具使用的 shell 路径，对应 pi 的 <c>shellPath</c>；为空时按平台默认值。</summary>
	public string? ShellPath { get; init; }

	/// <summary>每条 bash 命令前追加的初始化脚本，对应 pi 的 <c>shellCommandPrefix</c>。</summary>
	public string? ShellCommandPrefix { get; init; }

	/// <summary>会话目录，对应 pi 的 <c>sessionDir</c>；<c>--session-dir</c> 优先。</summary>
	public string? SessionDir { get; init; }

	/// <summary><c>/share</c> 的目标：<c>auto</c>、<c>gist</c> 或 <c>radius</c>。</summary>
	public string SessionShareDestination { get; init; } = "auto";

	/// <summary>
	/// 默认启用的工具名（可带前缀 <c>+</c> / <c>-</c>），对应 pi 的 <c>defaultTools</c>。
	/// 为空时使用 <see cref="DefaultToolNames"/>。
	/// </summary>
	public IReadOnlyList<string> DefaultTools { get; init; } = [];

	/// <summary>settings 里是否显式写了 defaultTools（空的列表表示禁用全部内置工具）。</summary>
	public bool DefaultToolsConfigured { get; init; }

	/// <summary>
	/// 未配置 <c>defaultTools</c> 时的默认工具集，与 pi 一致（<c>read</c>/<c>bash</c>/<c>edit</c>/<c>write</c>），
	/// 另外保留 PiDeck 依赖的原生 <c>todo</c> 与 <c>ask_question</c>。
	/// pi 默认关闭的 <c>grep</c>/<c>find</c>/<c>ls</c> 需要 <c>--tools</c> 或 <c>defaultTools</c> 显式开启。
	/// </summary>
	public static readonly string[] DefaultToolNames = ["read", "bash", "edit", "write", "todo", "ask_question"];

	/// <summary>全局设置路径：<c>&lt;agentDir&gt;/settings.json</c>。</summary>
	public static string GlobalPath(string? agentDir = null) =>
		Path.Combine(agentDir ?? SessionPaths.GetAgentDir(), "settings.json");

	/// <summary>项目设置路径：<c>&lt;cwd&gt;/.pi/settings.json</c>。</summary>
	public static string ProjectPath(string cwd) =>
		Path.Combine(Path.GetFullPath(cwd), SessionPaths.ConfigDirName, "settings.json");

	/// <summary>加载并合并全局与项目设置，项目配置优先。</summary>
	public static CodingAgentSettings Load(string cwd, string? agentDir = null, bool includeProject = true)
	{
		var global = ReadFile(GlobalPath(agentDir));
		// 不受信时只取全局设置，项目里的 .pi/settings.json 不参与。
		var project = includeProject ? ReadFile(ProjectPath(cwd)) : null;
		return Merge(global, project);
	}

	/// <summary>
	/// 只读**全局**设置里的 <c>defaultProjectTrust</c>。项目设置不能参与这个决策，
	/// 否则一个项目可以用自己的 settings.json 把自己标成可信。
	/// </summary>
	public static string GlobalDefaultProjectTrust(string? agentDir = null) =>
		ReadFile(GlobalPath(agentDir))?.DefaultProjectTrust ?? "ask";

	/// <summary>
	/// 把默认模型写进**全局**设置文件（<c>&lt;agentDir&gt;/settings.json</c>）。
	/// 只改动 <c>defaultProvider</c> 与 <c>defaultModel</c> 两个键，其它字段（包括本端口未建模的键）原样保留 ——
	/// 用户的配置文件不能被我们覆盖掉。
	/// </summary>
	public static void SetDefaultModel(string provider, string model, string? agentDir = null)
	{
		var path = GlobalPath(agentDir);
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);

		var root = ReadExistingObject(path);
		root["defaultProvider"] = provider;
		root["defaultModel"] = model;

		File.WriteAllText(
			path,
			root.ToJsonString(new JsonSerializerOptions
			{
				WriteIndented = true,
				Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
			}));
	}

	/// <summary>
	/// 读取 settings 里的 <c>npmCommand</c>（默认 <c>["npm"]</c>）。该键只在包管理时使用，
	/// 因此不进入建模的设置字段，避免保存时把用户没动过的值写回文件。
	/// </summary>
	public static IReadOnlyList<string> NpmCommand(string settingsPath)
	{
		var root = File.Exists(settingsPath) ? ReadExistingObject(settingsPath) : null;
		if (root?["npmCommand"] is not JsonArray array || array.Count == 0)
		{
			return ["npm"];
		}

		var command = new List<string>(array.Count);
		foreach (var node in array)
		{
			if (node is JsonValue value && value.GetValueKind() == JsonValueKind.String && value.GetValue<string>() is { Length: > 0 } text)
			{
				command.Add(text);
			}
		}

		return command.Count > 0 ? command : ["npm"];
	}

	/// <summary>
	/// 把包来源加入某份 settings 的 <c>packages</c> 数组。保留文件里其它键，写入是原子的；
	/// 已经声明过时返回 <c>false</c>（不重复追加）。
	/// </summary>
	public static bool AddPackage(string settingsPath, string source)
	{
		var root = ReadExistingObject(settingsPath);
		var existing = root["packages"] as JsonArray;
		if (existing is not null)
		{
			foreach (var node in existing)
			{
				if (PackageSourceOf(node) is { } declared &&
					string.Equals(declared, source, StringComparison.Ordinal))
				{
					return false;
				}
			}
		}

		var count = existing?.Count ?? 0;
		var items = new JsonNode?[count + 1];
		for (var index = 0; index < count; index++)
		{
			items[index] = existing![index]?.DeepClone();
		}

		items[count] = JsonValue.Create(source);
		root["packages"] = new JsonArray(items);
		WriteAtomically(settingsPath, root);
		return true;
	}

	/// <summary>从某份 settings 的 <c>packages</c> 数组移除一个来源；未声明时返回 <c>false</c>。</summary>
	public static bool RemovePackage(string settingsPath, string source)
	{
		if (!File.Exists(settingsPath))
		{
			return false;
		}

		var root = ReadExistingObject(settingsPath);
		if (root["packages"] is not JsonArray existing)
		{
			return false;
		}

		var kept = new List<JsonNode?>(existing.Count);
		var removed = false;
		foreach (var node in existing)
		{
			if (PackageSourceOf(node) is { } declared &&
				string.Equals(declared, source, StringComparison.Ordinal))
			{
				removed = true;
				continue;
			}

			kept.Add(node?.DeepClone());
		}

		if (!removed)
		{
			return false;
		}

		if (kept.Count == 0)
		{
			root.Remove("packages");
		}
		else
		{
			root["packages"] = new JsonArray([.. kept]);
		}

		WriteAtomically(settingsPath, root);
		return true;
	}

	/// <summary>同目录临时文件 + 原子替换；Unix 上新文件仅用户可读写。</summary>
	private static void WriteAtomically(string path, JsonObject root)
	{
		var directory = Path.GetDirectoryName(path);
		if (directory is { Length: > 0 })
		{
			Directory.CreateDirectory(directory);
		}

		var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
		var json = root.ToJsonString(new JsonSerializerOptions
		{
			WriteIndented = true,
			Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
		});

		try
		{
			File.WriteAllText(temporary, json);
			if (!OperatingSystem.IsWindows())
			{
				File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
			}

			File.Move(temporary, path, overwrite: true);
		}
		finally
		{
			// 失败时不要留下 .tmp 垃圾；成功时 Move 已经把它搬走了。
			if (File.Exists(temporary))
			{
				try
				{
					File.Delete(temporary);
				}
				catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
				{
					// 清理失败不影响主流程的错误上报。
				}
			}
		}
	}

	/// <summary>
	/// 把设置写入项目文件。**保留文件里本端口未建模的键**（例如 <c>$schema</c> 与上游新增项），
	/// 只覆盖我们认识的字段 —— 否则一次保存就会把用户的其它配置抹掉。
	/// </summary>
	public static void SaveProject(string cwd, CodingAgentSettings settings)
	{
		var path = ProjectPath(cwd);
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);

		var root = ReadExistingObject(path);
		MergeInto(root, ToFile(settings));

		File.WriteAllText(
			path,
			root.ToJsonString(new JsonSerializerOptions
			{
				WriteIndented = true,
				DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
				Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
			}));
	}

	/// <summary>读出现有设置文件（不存在或坏掉时给一个空对象）。</summary>
	private static JsonObject ReadExistingObject(string path)
	{
		if (!File.Exists(path))
		{
			return new JsonObject();
		}

		try
		{
			return JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject();
		}
		catch (Exception ex) when (ex is JsonException or IOException)
		{
			// 文件坏了就以它为基础重建，而不是让整个操作失败。
			return new JsonObject();
		}
	}

	/// <summary>把已知字段写进 JSON 对象，其余键原样保留。</summary>
	private static void MergeInto(JsonObject root, SettingsFile file)
	{
		Write(root, "defaultProvider", file.DefaultProvider);
		Write(root, "defaultModel", file.DefaultModel);
		Write(root, "defaultThinkingLevel", file.DefaultThinkingLevel);
		Write(root, "modelThinkingLevels", file.ModelThinkingLevels);
		Write(root, "enabledModels", file.EnabledModels);
		Write(root, "steeringMode", file.SteeringMode);
		Write(root, "followUpMode", file.FollowUpMode);
		Write(root, "enableSkillCommands", file.EnableSkillCommands);
		Write(root, "defaultProjectTrust", file.DefaultProjectTrust);
		Write(root, "timeoutMs", file.TimeoutMs);
		WritePackages(root, file.Packages);
		Write(root, "skills", file.Skills);
		Write(root, "prompts", file.Prompts);
		Write(root, "themes", file.Themes);

		if (file.Compaction is { } compaction)
		{
			var target = root["compaction"] as JsonObject ?? new JsonObject();
			Write(target, "enabled", compaction.Enabled);
			Write(target, "reserveTokens", compaction.ReserveTokens);
			Write(target, "keepRecentTokens", compaction.KeepRecentTokens);
			root["compaction"] = target;
		}

		if (file.BranchSummary is { } branchSummary)
		{
			var target = root["branchSummary"] as JsonObject ?? new JsonObject();
			Write(target, "reserveTokens", branchSummary.ReserveTokens);
			Write(target, "skipPrompt", branchSummary.SkipPrompt);
			root["branchSummary"] = target;
		}

		if (file.Retry is { } retry)
		{
			// 只写本端口建模的子键，retry.provider 之类的上游键保持不动。
			var target = root["retry"] as JsonObject ?? new JsonObject();
			Write(target, "enabled", retry.Enabled);
			Write(target, "maxRetries", retry.MaxRetries);
			Write(target, "baseDelayMs", retry.BaseDelayMs);
			Write(target, "maxAgentDelayMs", retry.MaxAgentDelayMs);
			root["retry"] = target;
		}
	}

	// 用具体类型的重载写值：泛型的 JsonValue.Create<T> 在 AOT 下需要运行时代码生成。
	private static void Write(JsonObject root, string name, string? value)
	{
		if (value is not null)
		{
			root[name] = JsonValue.Create(value);
		}
	}

	private static void Write(JsonObject root, string name, bool? value)
	{
		if (value is not null)
		{
			root[name] = JsonValue.Create(value.Value);
		}
	}

	private static void Write(JsonObject root, string name, IReadOnlyDictionary<string, string>? values)
	{
		if (values is null)
		{
			return;
		}

		var node = new JsonObject();
		foreach (var (key, value) in values)
		{
			node[key] = value;
		}

		root[name] = node;
	}

	private static void Write(JsonObject root, string name, int? value)
	{
		if (value is not null)
		{
			root[name] = JsonValue.Create(value.Value);
		}
	}

	private static void Write(JsonObject root, string name, List<string>? values)
	{
		if (values is null)
		{
			return;
		}

		// 用数组构造器而不是 JsonArray.Add<T>：泛型 Add 在 AOT 下会触发 IL2026/IL3050。
		var nodes = new JsonNode?[values.Count];
		for (var index = 0; index < values.Count; index++)
		{
			nodes[index] = JsonValue.Create(values[index]);
		}

		root[name] = new JsonArray(nodes);
	}

	internal static SettingsFile? ReadFile(string path)
	{
		if (!File.Exists(path))
		{
			return null;
		}

		try
		{
			return JsonSerializer.Deserialize(File.ReadAllText(path), CodingAgentJsonContext.Default.SettingsFile);
		}
		catch (JsonException)
		{
			return null;
		}
		catch (IOException)
		{
			return null;
		}
	}

	internal static CodingAgentSettings Merge(SettingsFile? global, SettingsFile? project) => new()
	{
		DefaultProvider = project?.DefaultProvider ?? global?.DefaultProvider,
		DefaultModel = project?.DefaultModel ?? global?.DefaultModel,
		DefaultThinkingLevel = project?.DefaultThinkingLevel ?? global?.DefaultThinkingLevel,
		ModelThinkingLevels = MergeDictionary(global?.ModelThinkingLevels, project?.ModelThinkingLevels),
		EnabledModels = project?.EnabledModels is { Count: > 0 } projectEnabled
			? [.. projectEnabled]
			: global?.EnabledModels is { Count: > 0 } globalEnabled ? [.. globalEnabled] : [],
		SteeringMode = project?.SteeringMode ?? global?.SteeringMode ?? "one-at-a-time",
		FollowUpMode = project?.FollowUpMode ?? global?.FollowUpMode ?? "one-at-a-time",
		Compaction = new CompactionSettings
		{
			Enabled = project?.Compaction?.Enabled ?? global?.Compaction?.Enabled ?? true,
			ReserveTokens = project?.Compaction?.ReserveTokens ?? global?.Compaction?.ReserveTokens ?? 16384,
			KeepRecentTokens = project?.Compaction?.KeepRecentTokens ?? global?.Compaction?.KeepRecentTokens ?? 20000,
		},
		BranchSummary = new BranchSummarySettings
		{
			ReserveTokens = project?.BranchSummary?.ReserveTokens ?? global?.BranchSummary?.ReserveTokens ?? 16384,
			SkipPrompt = project?.BranchSummary?.SkipPrompt ?? global?.BranchSummary?.SkipPrompt ?? false,
		},
		EnableSkillCommands = project?.EnableSkillCommands ?? global?.EnableSkillCommands ?? true,
		Packages = MergePackages(global?.Packages, project?.Packages),
		Skills = MergeLists(global?.Skills, project?.Skills),
		Prompts = MergeLists(global?.Prompts, project?.Prompts),
		Themes = MergeLists(global?.Themes, project?.Themes),
		DefaultProjectTrust = project?.DefaultProjectTrust ?? global?.DefaultProjectTrust ?? "ask",
		TimeoutMs = project?.TimeoutMs ?? global?.TimeoutMs,
		ShellPath = project?.ShellPath ?? global?.ShellPath,
		ShellCommandPrefix = project?.ShellCommandPrefix ?? global?.ShellCommandPrefix,
		SessionDir = project?.SessionDir ?? global?.SessionDir,
		SessionShareDestination = project?.SessionShareDestination ?? global?.SessionShareDestination ?? "auto",
		DefaultTools = MergeTools(global?.DefaultTools, project?.DefaultTools),
		DefaultToolsConfigured = project?.DefaultTools is not null || global?.DefaultTools is not null,
		Retry = new RetryPolicy(
			Enabled: project?.Retry?.Enabled ?? global?.Retry?.Enabled ?? true,
			MaxRetries: project?.Retry?.MaxRetries ?? global?.Retry?.MaxRetries ?? 3,
			BaseDelayMs: project?.Retry?.BaseDelayMs ?? global?.Retry?.BaseDelayMs ?? 2000,
			MaxAgentDelayMs: project?.Retry?.MaxAgentDelayMs ?? global?.Retry?.MaxAgentDelayMs
				?? RetryClassifier.DefaultMaxAgentDelayMs),
	};

	private static IReadOnlyDictionary<string, string> MergeDictionary(
		IReadOnlyDictionary<string, string>? global,
		IReadOnlyDictionary<string, string>? project)
	{
		var merged = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (var (key, value) in global ?? new Dictionary<string, string>())
		{
			merged[key] = value;
		}

		foreach (var (key, value) in project ?? new Dictionary<string, string>())
		{
			merged[key] = value;
		}

		return merged;
	}

	/// <summary>
	/// defaultTools 的项目继承：项目只含 +/− 时叠加到用户列表；出现纯名字时整体替换。
	/// 空列表是合法的“全部禁用”，不能回退到默认值。
	/// </summary>
	private static IReadOnlyList<string> MergeTools(List<string>? global, List<string>? project)
	{
		if (project is null)
		{
			return global is null ? [] : [.. global];
		}

		if (project.Count == 0)
		{
			return [];
		}

		var hasPlain = project.Any(static entry => entry.Length > 0 && entry[0] is not ('+' or '-'));
		if (hasPlain)
		{
			return [.. project];
		}

		var merged = new List<string>(global?.Count ?? 0 + project.Count);
		if (global is not null)
		{
			merged.AddRange(global);
		}

		merged.AddRange(project);
		return merged;
	}

	/// <summary>合并 packages：项目条目按来源替换用户条目，新条目追加在后面。</summary>
	private static IReadOnlyList<PackageDeclaration> MergePackages(
		IReadOnlyList<PackageDeclaration>? global,
		IReadOnlyList<PackageDeclaration>? project)
	{
		var merged = new List<PackageDeclaration>();
		var indexBySource = new Dictionary<string, int>(StringComparer.Ordinal);

		void Add(PackageDeclaration package)
		{
			if (indexBySource.TryGetValue(package.Source, out var index))
			{
				merged[index] = package;
			}
			else
			{
				indexBySource[package.Source] = merged.Count;
				merged.Add(package);
			}
		}

		foreach (var package in global ?? [])
		{
			Add(package);
		}

		foreach (var package in project ?? [])
		{
			Add(package);
		}

		return merged;
	}

	/// <summary>把 packages 写成 pi 支持的字符串/对象数组，保留过滤字段。</summary>
	private static void WritePackages(JsonObject root, List<PackageDeclaration>? packages)
	{
		if (packages is null)
		{
			return;
		}

		var nodes = new JsonNode?[packages.Count];
		for (var index = 0; index < packages.Count; index++)
		{
			var package = packages[index];
			if (package.Autoload && package.Extensions is null && package.Skills is null &&
				package.Prompts is null && package.Themes is null)
			{
				nodes[index] = JsonValue.Create(package.Source);
				continue;
			}

			var node = new JsonObject { ["source"] = package.Source };
			if (!package.Autoload) node["autoload"] = false;
			AddList(node, "extensions", package.Extensions);
			AddList(node, "skills", package.Skills);
			AddList(node, "prompts", package.Prompts);
			AddList(node, "themes", package.Themes);
			nodes[index] = node;
		}

		root["packages"] = new JsonArray(nodes);
	}

	private static void AddList(JsonObject node, string name, List<string>? values)
	{
		if (values is null) return;
		var items = new JsonNode?[values.Count];
		for (var index = 0; index < values.Count; index++) items[index] = JsonValue.Create(values[index]);
		node[name] = new JsonArray(items);
	}

	private static string? PackageSourceOf(JsonNode? node) => node switch
	{
		JsonValue value when value.GetValueKind() == JsonValueKind.String => value.GetValue<string>(),
		JsonObject obj when obj["source"] is JsonValue source && source.GetValueKind() == JsonValueKind.String =>
			source.GetValue<string>(),
		_ => null,
	};

	/// <summary>拼接全局与项目的资源列表并去重（先出现者保留）。</summary>
	private static IReadOnlyList<string> MergeLists(List<string>? global, List<string>? project)
	{
		if (global is null or { Count: 0 })
		{
			return project is null or { Count: 0 } ? [] : [.. project];
		}

		if (project is null or { Count: 0 })
		{
			return [.. global];
		}

		var merged = new List<string>(global.Count + project.Count);
		var seen = new HashSet<string>(StringComparer.Ordinal);
		foreach (var item in global)
		{
			if (seen.Add(item))
			{
				merged.Add(item);
			}
		}

		foreach (var item in project)
		{
			if (seen.Add(item))
			{
				merged.Add(item);
			}
		}

		return merged;
	}

	internal static SettingsFile ToFile(CodingAgentSettings settings) => new()
	{
		DefaultProvider = settings.DefaultProvider,
		DefaultModel = settings.DefaultModel,
		DefaultThinkingLevel = settings.DefaultThinkingLevel,
		ModelThinkingLevels = settings.ModelThinkingLevels.Count > 0
			? new Dictionary<string, string>(settings.ModelThinkingLevels, StringComparer.Ordinal)
			: null,
		EnabledModels = List(settings.EnabledModels),
		SteeringMode = settings.SteeringMode,
		FollowUpMode = settings.FollowUpMode,
		Compaction = new CompactionFile
		{
			Enabled = settings.Compaction.Enabled,
			ReserveTokens = settings.Compaction.ReserveTokens,
			KeepRecentTokens = settings.Compaction.KeepRecentTokens,
		},
		BranchSummary = new BranchSummaryFile
		{
			ReserveTokens = settings.BranchSummary.ReserveTokens,
			SkipPrompt = settings.BranchSummary.SkipPrompt,
		},
		EnableSkillCommands = settings.EnableSkillCommands,
		Packages = settings.Packages.Count > 0 ? [.. settings.Packages] : null,
		Skills = List(settings.Skills),
		Prompts = List(settings.Prompts),
		Themes = List(settings.Themes),
		DefaultProjectTrust = settings.DefaultProjectTrust,
		TimeoutMs = settings.TimeoutMs,
		ShellPath = settings.ShellPath,
		ShellCommandPrefix = settings.ShellCommandPrefix,
		SessionDir = settings.SessionDir,
		SessionShareDestination = settings.SessionShareDestination,
		DefaultTools = settings.DefaultToolsConfigured ? [.. settings.DefaultTools] : null,
		Retry = new RetryFile
		{
			Enabled = settings.Retry.Enabled,
			MaxRetries = settings.Retry.MaxRetries,
			BaseDelayMs = settings.Retry.BaseDelayMs,
			MaxAgentDelayMs = settings.Retry.MaxAgentDelayMs,
		},
	};

	/// <summary>空列表写成 <c>null</c>，保存时不会覆盖文件里已有的条目。</summary>
	private static List<string>? List(IReadOnlyList<string> values) => values.Count > 0 ? [.. values] : null;
}

/// <summary>设置文件的磁盘形状。所有字段都是可选的，未知键会被忽略。</summary>
public sealed record SettingsFile
{
	public string? DefaultProvider { get; init; }

	public string? DefaultModel { get; init; }

	public string? DefaultThinkingLevel { get; init; }

	/// <summary>按 <c>provider/modelId</c> 覆盖启动思考级别。</summary>
	public Dictionary<string, string>? ModelThinkingLevels { get; init; }

	/// <summary>启动与轮换使用的模型模式；空表示全部可用模型。</summary>
	public List<string>? EnabledModels { get; init; }

	/// <summary><c>all</c> 或 <c>one-at-a-time</c>。</summary>
	public string? SteeringMode { get; init; }

	public string? FollowUpMode { get; init; }

	public CompactionFile? Compaction { get; init; }

	public BranchSummaryFile? BranchSummary { get; init; }

	public bool? EnableSkillCommands { get; init; }

	[JsonConverter(typeof(PackageDeclarationListJsonConverter))]
	public List<PackageDeclaration>? Packages { get; init; }

	[JsonConverter(typeof(FlexibleStringListJsonConverter))]
	public List<string>? Skills { get; init; }

	public List<string>? Prompts { get; init; }

	public List<string>? Themes { get; init; }

	public string? DefaultProjectTrust { get; init; }

	public int? TimeoutMs { get; init; }

	public string? ShellPath { get; init; }

	public string? ShellCommandPrefix { get; init; }

	public string? SessionDir { get; init; }

	/// <summary><c>/share</c> 的目标：<c>auto</c>、<c>gist</c> 或 <c>radius</c>。</summary>
	public string SessionShareDestination { get; init; } = "auto";

	public List<string>? DefaultTools { get; init; }

	public RetryFile? Retry { get; init; }
}

/// <summary><c>settings.retry</c> 的磁盘形状。</summary>
public sealed record RetryFile
{
	public bool? Enabled { get; init; }

	public int? MaxRetries { get; init; }

	public int? BaseDelayMs { get; init; }

	public int? MaxAgentDelayMs { get; init; }
}

public sealed record BranchSummaryFile
{
	public int? ReserveTokens { get; init; }

	public bool? SkipPrompt { get; init; }
}

public sealed record CompactionFile
{
	public bool? Enabled { get; init; }

	public int? ReserveTokens { get; init; }

	public int? KeepRecentTokens { get; init; }
}

[JsonSourceGenerationOptions(
	PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
	DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
	WriteIndented = true,
	ReadCommentHandling = JsonCommentHandling.Skip,
	AllowTrailingCommas = true)]
[JsonSerializable(typeof(SettingsFile))]
internal partial class CodingAgentJsonContext : JsonSerializerContext
{
	// 保留源生成选项，只替换编码器。
	static CodingAgentJsonContext()
	{
		Default = new CodingAgentJsonContext(CreateOptions(Default));
	}

	private static JsonSerializerOptions CreateOptions(CodingAgentJsonContext defaultContext) =>
		new(defaultContext.GeneratedSerializerOptions!)
		{
			Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
		};
}
