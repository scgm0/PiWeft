using System.Text.Encodings.Web;
using System.Text.Json;
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

	public CompactionSettings Compaction { get; init; } = new();

	/// <summary>自动重试策略，对应 pi 的 <c>settings.retry</c>。</summary>
	public RetryPolicy Retry { get; init; } = new();

	public bool EnableSkillCommands { get; init; } = true;

	/// <summary>
	/// 项目信任的默认决策：<c>always</c>、<c>never</c> 或 <c>ask</c>。
	/// 本端口默认 <c>always</c>（不改变既有行为）；显式设置或 <c>--no-approve</c> 才会收紧。
	/// </summary>
	public string DefaultProjectTrust { get; init; } = "always";

	/// <summary>provider 请求超时，单位毫秒。</summary>
	public int? TimeoutMs { get; init; }

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
		ReadFile(GlobalPath(agentDir))?.DefaultProjectTrust ?? "always";

	/// <summary>
	/// 把默认模型写进**全局**设置文件（<c>&lt;agentDir&gt;/settings.json</c>）。
	/// 只改动 <c>defaultProvider</c> 与 <c>defaultModel</c> 两个键，其它字段（包括本端口未建模的键）原样保留 ——
	/// 用户的配置文件不能被我们覆盖掉。
	/// </summary>
	public static void SetDefaultModel(string provider, string model, string? agentDir = null)
	{
		var path = GlobalPath(agentDir);
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);

		System.Text.Json.Nodes.JsonObject root;
		try
		{
			root = File.Exists(path)
				? System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path)) as System.Text.Json.Nodes.JsonObject
					?? new System.Text.Json.Nodes.JsonObject()
				: new System.Text.Json.Nodes.JsonObject();
		}
		catch (System.Text.Json.JsonException)
		{
			// 文件坏了就以它为基础重建这两个键，而不是让整个操作失败。
			root = new System.Text.Json.Nodes.JsonObject();
		}

		root["defaultProvider"] = provider;
		root["defaultModel"] = model;

		File.WriteAllText(
			path,
			root.ToJsonString(new System.Text.Json.JsonSerializerOptions
			{
				WriteIndented = true,
				Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
			}));
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
				Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
			}));
	}

	/// <summary>读出现有设置文件（不存在或坏掉时给一个空对象）。</summary>
	private static System.Text.Json.Nodes.JsonObject ReadExistingObject(string path)
	{
		if (!File.Exists(path))
		{
			return new System.Text.Json.Nodes.JsonObject();
		}

		try
		{
			return System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))
				as System.Text.Json.Nodes.JsonObject ?? new System.Text.Json.Nodes.JsonObject();
		}
		catch (Exception ex) when (ex is JsonException or IOException)
		{
			return new System.Text.Json.Nodes.JsonObject();
		}
	}

	/// <summary>把已知字段写进 JSON 对象，其余键原样保留。</summary>
	private static void MergeInto(System.Text.Json.Nodes.JsonObject root, SettingsFile file)
	{
		Write(root, "defaultProvider", file.DefaultProvider);
		Write(root, "defaultModel", file.DefaultModel);
		Write(root, "defaultThinkingLevel", file.DefaultThinkingLevel);
		Write(root, "enableSkillCommands", file.EnableSkillCommands);
		Write(root, "defaultProjectTrust", file.DefaultProjectTrust);
		Write(root, "timeoutMs", file.TimeoutMs);

		if (file.Compaction is { } compaction)
		{
			var target = root["compaction"] as System.Text.Json.Nodes.JsonObject ?? new System.Text.Json.Nodes.JsonObject();
			Write(target, "enabled", compaction.Enabled);
			Write(target, "reserveTokens", compaction.ReserveTokens);
			Write(target, "keepRecentTokens", compaction.KeepRecentTokens);
			root["compaction"] = target;
		}

		if (file.Retry is { } retry)
		{
			// 只写本端口建模的子键，retry.provider 之类的上游键保持不动。
			var target = root["retry"] as System.Text.Json.Nodes.JsonObject ?? new System.Text.Json.Nodes.JsonObject();
			Write(target, "enabled", retry.Enabled);
			Write(target, "maxRetries", retry.MaxRetries);
			Write(target, "baseDelayMs", retry.BaseDelayMs);
			Write(target, "maxAgentDelayMs", retry.MaxAgentDelayMs);
			root["retry"] = target;
		}
	}

	// 用具体类型的重载写值：泛型的 JsonValue.Create<T> 在 AOT 下需要运行时代码生成。
	private static void Write(System.Text.Json.Nodes.JsonObject root, string name, string? value)
	{
		if (value is not null)
		{
			root[name] = System.Text.Json.Nodes.JsonValue.Create(value);
		}
	}

	private static void Write(System.Text.Json.Nodes.JsonObject root, string name, bool? value)
	{
		if (value is not null)
		{
			root[name] = System.Text.Json.Nodes.JsonValue.Create(value.Value);
		}
	}

	private static void Write(System.Text.Json.Nodes.JsonObject root, string name, int? value)
	{
		if (value is not null)
		{
			root[name] = System.Text.Json.Nodes.JsonValue.Create(value.Value);
		}
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
		Compaction = new CompactionSettings
		{
			Enabled = project?.Compaction?.Enabled ?? global?.Compaction?.Enabled ?? true,
			ReserveTokens = project?.Compaction?.ReserveTokens ?? global?.Compaction?.ReserveTokens ?? 16384,
			KeepRecentTokens = project?.Compaction?.KeepRecentTokens ?? global?.Compaction?.KeepRecentTokens ?? 20000,
		},
		EnableSkillCommands = project?.EnableSkillCommands ?? global?.EnableSkillCommands ?? true,
		DefaultProjectTrust = project?.DefaultProjectTrust ?? global?.DefaultProjectTrust ?? "always",
		TimeoutMs = project?.TimeoutMs ?? global?.TimeoutMs,
		Retry = new RetryPolicy(
			Enabled: project?.Retry?.Enabled ?? global?.Retry?.Enabled ?? true,
			MaxRetries: project?.Retry?.MaxRetries ?? global?.Retry?.MaxRetries ?? 3,
			BaseDelayMs: project?.Retry?.BaseDelayMs ?? global?.Retry?.BaseDelayMs ?? 2000,
			MaxAgentDelayMs: project?.Retry?.MaxAgentDelayMs ?? global?.Retry?.MaxAgentDelayMs
				?? RetryClassifier.DefaultMaxAgentDelayMs),
	};

	internal static SettingsFile ToFile(CodingAgentSettings settings) => new()
	{
		DefaultProvider = settings.DefaultProvider,
		DefaultModel = settings.DefaultModel,
		DefaultThinkingLevel = settings.DefaultThinkingLevel,
		Compaction = new CompactionFile
		{
			Enabled = settings.Compaction.Enabled,
			ReserveTokens = settings.Compaction.ReserveTokens,
			KeepRecentTokens = settings.Compaction.KeepRecentTokens,
		},
		EnableSkillCommands = settings.EnableSkillCommands,
		DefaultProjectTrust = settings.DefaultProjectTrust,
		TimeoutMs = settings.TimeoutMs,
		Retry = new RetryFile
		{
			Enabled = settings.Retry.Enabled,
			MaxRetries = settings.Retry.MaxRetries,
			BaseDelayMs = settings.Retry.BaseDelayMs,
			MaxAgentDelayMs = settings.Retry.MaxAgentDelayMs,
		},
	};
}

/// <summary>设置文件的磁盘形状。所有字段都是可选的，未知键会被忽略。</summary>
public sealed record SettingsFile
{
	public string? DefaultProvider { get; init; }

	public string? DefaultModel { get; init; }

	public string? DefaultThinkingLevel { get; init; }

	public CompactionFile? Compaction { get; init; }

	public bool? EnableSkillCommands { get; init; }

	public string? DefaultProjectTrust { get; init; }

	public int? TimeoutMs { get; init; }

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
