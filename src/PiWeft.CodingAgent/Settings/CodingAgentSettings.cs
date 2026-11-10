using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using PiWeft.Ai;
using PiWeft.Ai.Json;
using PiWeft.CodingAgent.Security;
using PiWeft.CodingAgent.Session;

namespace PiWeft.CodingAgent.Settings;

public sealed record ImagesSettings
{
	/// 对应 <c>settings.images.blockImages</c>：禁止任何图片进入模型上下文。
	public bool BlockImages { get; init; }
}

public sealed record CompactionModelOverride
{
	public int? ReserveTokens { get; init; }

	public int? KeepRecentTokens { get; init; }
}

public sealed record CompactionSettings
{
	public bool Enabled { get; init; } = true;

	public int ReserveTokens { get; init; } = 16384;

	public int KeepRecentTokens { get; init; } = 20000;

	/// 按 <c>provider/modelId</c> 覆盖压缩 token 预算。
	public IReadOnlyDictionary<string, CompactionModelOverride> ModelOverrides { get; init; } =
		new Dictionary<string, CompactionModelOverride>(StringComparer.Ordinal);

	public CompactionSettings ForModel(Model model)
	{
		if (!ModelOverrides.TryGetValue($"{model.Provider}/{model.Id}", out var value))
		{
			return this;
		}

		return this with
		{
			ReserveTokens = value.ReserveTokens ?? ReserveTokens,
			KeepRecentTokens = value.KeepRecentTokens ?? KeepRecentTokens,
		};
	}
}

public sealed record BranchSummarySettings
{
	public int ReserveTokens { get; init; } = 16384;

	public bool SkipPrompt { get; init; }
}

/// 本端口读取的 pi <c>settings.json</c> 子集。未知键忽略，完整的 pi 设置文件也能正常加载。
public sealed record CodingAgentSettings
{
	public string? DefaultProvider { get; init; }

	public string? DefaultModel { get; init; }

	public string? DefaultThinkingLevel { get; init; }

	public IReadOnlyDictionary<string, string> ModelThinkingLevels { get; init; } =
		new Dictionary<string, string>(StringComparer.Ordinal);

	public IReadOnlyList<string> EnabledModels { get; init; } = [];

	public string SteeringMode { get; init; } = "one-at-a-time";

	public string FollowUpMode { get; init; } = "one-at-a-time";

	public string CacheWarming { get; init; } = "idle";

	/// <c>settings.thinkingBudgets</c>：思考级别的自定义 token 预算。
	public IReadOnlyDictionary<string, int> ThinkingBudgets { get; init; } =
		new Dictionary<string, int>(StringComparer.Ordinal);

	public ImagesSettings Images { get; init; } = new();

	/// 偏好的传输方式：<c>auto</c> / <c>sse</c> / <c>websocket</c> / <c>websocket-cached</c>。
	/// 目前只有 Codex 的 responses 实现会读它（其它 provider 一律走 HTTP）。
	public string Transport { get; init; } = "auto";

	public CompactionSettings Compaction { get; init; } = new();

	public BranchSummarySettings BranchSummary { get; init; } = new();

	public RetryPolicy Retry { get; init; } = new();

	public CodemodeSettings Codemode { get; init; } = new();

	/// 工具调用安全门，对应 <c>settings.security</c>。默认 <c>off</c>，全部放行；
	/// 只从全局设置读取，见 <see cref="SecurityConfig.Load"/>。
	public SecurityConfig Security { get; init; } = SecurityConfig.Off;

	public bool EnableSkillCommands { get; init; } = true;

	public IReadOnlyList<PackageDeclaration> Packages { get; init; } = [];

	public IReadOnlyList<string> Skills { get; init; } = [];

	public IReadOnlyList<string> Prompts { get; init; } = [];

	public IReadOnlyList<string> Themes { get; init; } = [];

	public IReadOnlyList<string> Extensions { get; init; } = [];

	/// 项目信任的默认决策：<c>always</c>、<c>never</c> 或 <c>ask</c>。
	/// 默认 <c>ask</c>（与 pi 一致）：交互场景弹卡询问，RPC/print 等无 UI 场景按不受信处理。
	public string DefaultProjectTrust { get; init; } = "ask";

	public int? TimeoutMs { get; init; }

	public const int DefaultHttpIdleTimeoutMs = 300_000;

	/// 响应体空闲超时（毫秒），默认 300 秒；<c>0</c> 表示关闭。
	/// 连续这么久没有收到任何字节就让这一轮失败，交给自动重试。长流不受总时长限制。
	public int HttpIdleTimeoutMs { get; init; } = DefaultHttpIdleTimeoutMs;

	/// pi 的 <c>settings.httpProxy</c>：设置 <c>HTTP_PROXY</c>/<c>HTTPS_PROXY</c>。
	/// 只读全局设置：项目配置能把流量导到任意代理，等于让仓库劫持用户请求。
	public string? HttpProxy { get; init; }

	public string? ShellPath { get; init; }

	public string? ShellCommandPrefix { get; init; }

	public string? SessionDir { get; init; }

	public string SessionShareDestination { get; init; } = "auto";

	/// 默认启用的工具名（可带前缀 <c>+</c> / <c>-</c>），对应 pi 的 <c>defaultTools</c>。
	/// 为空时使用 <see cref="DefaultToolNames"/>。
	public IReadOnlyList<string> DefaultTools { get; init; } = [];

	public bool DefaultToolsConfigured { get; init; }

	/// 未配置 <c>defaultTools</c> 时的默认工具集，与 pi 一致（<c>read</c>/<c>bash</c>/<c>edit</c>/<c>write</c>），
	/// 另外保留 PiDeck 依赖的原生 <c>todo</c> 与 <c>ask_question</c>。
	/// pi 默认关闭的 <c>grep</c>/<c>find</c>/<c>ls</c> 需要 <c>--tools</c> 或 <c>defaultTools</c> 显式开启。
	public static readonly string[] DefaultToolNames = ["read", "bash", "edit", "write", "todo", "ask_question"];

	public static string GlobalPath(string? agentDir = null) =>
		Path.Combine(agentDir ?? SessionPaths.GetAgentDir(), "settings.json");

	public static string ProjectPath(string cwd) =>
		Path.Combine(Path.GetFullPath(cwd), SessionPaths.ConfigDirName, "settings.json");

	public static CodingAgentSettings Load(string cwd, string? agentDir = null, bool includeProject = true)
	{
		var resolvedAgentDir = agentDir ?? SessionPaths.GetAgentDir();
		var global = ReadFile(GlobalPath(resolvedAgentDir));
		// 不受信时只取全局设置，项目里的 .pi/settings.json 不参与。
		var project = includeProject ? ReadFile(ProjectPath(cwd)) : null;
		var merged = Merge(global, project);

		// extensions 的相对路径按声明作用域解析（pi 的 getBaseDirForScope）：用户级是 agentDir，
		// 项目级是 <cwd>/.pi。不能拿 cwd 当用户级路径的基准，不可信仓库里放个同名文件就能顶替全局扩展。
		var declared = project?.Extensions ?? global?.Extensions;
		var baseDirectory = project?.Extensions is not null
			? Path.GetDirectoryName(ProjectPath(cwd))!
			: resolvedAgentDir;

		// 安全门只认全局设置，而且要读原始 JSON：档位表与会话覆盖都不是扁平字段。
		return merged with
		{
			Extensions = ResolveExtensionPaths(declared, baseDirectory),
			Security = SecurityConfig.Load(resolvedAgentDir),
		};
	}

	private static IReadOnlyList<string> ResolveExtensionPaths(IReadOnlyList<string>? paths, string baseDirectory)
	{
		if (paths is not { Count: > 0 })
		{
			return [];
		}

		var resolved = new List<string>(paths.Count);
		foreach (var raw in paths)
		{
			var value = raw.Trim();
			if (value.Length == 0)
			{
				continue;
			}

			var expanded = UserPath.ExpandTilde(value);
			try
			{
				resolved.Add(Path.IsPathRooted(expanded)
					? Path.GetFullPath(expanded)
					: Path.GetFullPath(Path.Combine(baseDirectory, expanded)));
			}
			catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
			{
				// 坏路径原样保留：加载期报「不存在」，不让整个设置读取失败。
				resolved.Add(expanded);
			}
		}

		return resolved;
	}

	/// 只读**全局**设置里的 <c>httpProxy</c>。启动期就要把代理装进环境变量，而完整的
	/// <see cref="Load"/> 会连带读项目设置与安全门；这里只解析一次全局文件，省掉两次多余读取。
	public static string? GlobalHttpProxy(string? agentDir = null) =>
		NormalizeHttpProxy(ReadFile(GlobalPath(agentDir))?.HttpProxy);

	/// 只读**全局**设置里的 <c>defaultProjectTrust</c>。项目设置不能参与这个决策，
	/// 否则一个项目可以用自己的 settings.json 把自己标成可信。
	public static string GlobalDefaultProjectTrust(string? agentDir = null) =>
		ReadFile(GlobalPath(agentDir))?.DefaultProjectTrust ?? "ask";

	/// 把默认模型写进**全局**设置文件（<c>&lt;agentDir&gt;/settings.json</c>）。
	/// 只改 <c>defaultProvider</c> 与 <c>defaultModel</c> 两个键，其它字段（含本端口未建模的键）原样保留；
	/// 用户的配置文件不能被我们覆盖掉。
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

	/// 把某个模型的默认思考级别写进**全局**设置（<c>modelThinkingLevels</c>，键 <c>provider/modelId</c>）。
	/// pi 用同一张表在新建会话（<c>findInitialModel</c>）与切换模型时解析默认档位：桌面在用户点选档位时记账，
	/// 「本次的选择」就成了这个模型下一次的默认，不必再手改 settings.json。其它字段原样保留。
	public static void SetModelThinkingLevel(string provider, string model, string level, string? agentDir = null)
	{
		if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(level))
		{
			return;
		}

		var path = GlobalPath(agentDir);
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);

		var root = ReadExistingObject(path);
		// 损坏的 modelThinkingLevels（不是对象）直接换掉：保留它只会让整张表失效。
		var levels = root["modelThinkingLevels"] as JsonObject ?? new JsonObject();
		levels[$"{provider.Trim()}/{model.Trim()}"] = level.Trim();
		root["modelThinkingLevels"] = levels;

		File.WriteAllText(
			path,
			root.ToJsonString(new JsonSerializerOptions
			{
				WriteIndented = true,
				Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
			}));
	}

	/// 读取 settings 里的 <c>npmCommand</c>（默认 <c>["npm"]</c>）。该键只在包管理时使用，
	/// 因此不进入建模的设置字段，避免保存时把用户没动过的值写回文件。
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

	/// 把包来源加入某份 settings 的 <c>packages</c> 数组。保留文件里其它键，写入是原子的；
	/// 已经声明过时返回 <c>false</c>（不重复追加）。
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

	/// 把设置写入项目文件。**保留文件里本端口未建模的键**（例如 <c>$schema</c> 与上游新增项），
	/// 只覆盖认识的字段，否则一次保存就会抹掉用户的其它配置。
	public static void SaveProject(string cwd, CodingAgentSettings settings)
	{
		var path = ProjectPath(cwd);
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);

		var root = ReadExistingObject(path);
		// 加载后的 Extensions 是**按作用域解析过的绝对路径**（用户级根在 ~/.pi/agent）：
		// 原样写进项目的 .pi/settings.json 等于把 home 路径提交进仓库，还会让用户级声明失效。
		MergeInto(root, ToFile(settings) with { Extensions = null });

		File.WriteAllText(
			path,
			root.ToJsonString(new JsonSerializerOptions
			{
				WriteIndented = true,
				DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
				Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
			}));
	}

	private static JsonObject ReadExistingObject(string path)
	{
		if (!File.Exists(path))
		{
			return new JsonObject();
		}

		try
		{
			return JsonNode.Parse(PiJson.StripBom(File.ReadAllText(path))) as JsonObject ?? new JsonObject();
		}
		catch (Exception ex) when (ex is JsonException or IOException)
		{
			// 文件坏了就以它为基础重建，不让整个操作失败。
			return new JsonObject();
		}
	}

	private static void MergeInto(JsonObject root, SettingsFile file)
	{
		Write(root, "defaultProvider", file.DefaultProvider);
		Write(root, "defaultModel", file.DefaultModel);
		Write(root, "defaultThinkingLevel", file.DefaultThinkingLevel);
		Write(root, "modelThinkingLevels", file.ModelThinkingLevels);
		Write(root, "enabledModels", file.EnabledModels);
		Write(root, "steeringMode", file.SteeringMode);
		Write(root, "followUpMode", file.FollowUpMode);
		Write(root, "cacheWarming", file.CacheWarming);
		if (file.ThinkingBudgets is { Count: > 0 } thinkingBudgets)
		{
			var levels = new JsonObject();
			foreach (var (level, tokens) in thinkingBudgets)
			{
				levels[level] = tokens;
			}

			root["thinkingBudgets"] = levels;
		}

		if (file.Images is { BlockImages: true })
		{
			root["images"] = new JsonObject { ["blockImages"] = true };
		}
		else if (root["images"] is JsonObject images)
		{
			// 只移除本端口建模的键，图片主题等上游键保持不动。
			images.Remove("blockImages");
			if (images.Count == 0)
			{
				root.Remove("images");
			}
		}

		Write(root, "transport", file.Transport);
		if (file.SteeringMode is not null)
		{
			// 写入新键后旧键没有存在意义；留着会让下一次合并看到两份互相矛盾的档位。
			root.Remove("queueMode");
		}

		if (file.Transport is not null)
		{
			root.Remove("websockets");
		}
		Write(root, "enableSkillCommands", file.EnableSkillCommands);
		Write(root, "defaultProjectTrust", file.DefaultProjectTrust);
		Write(root, "timeoutMs", file.TimeoutMs);
		WriteTimeout(root, "httpIdleTimeoutMs", file.HttpIdleTimeoutMs);
		WritePackages(root, file.Packages);
		Write(root, "skills", file.Skills);
		Write(root, "prompts", file.Prompts);
		Write(root, "themes", file.Themes);
		Write(root, "extensions", file.Extensions);

		if (file.Compaction is { } compaction)
		{
			var target = root["compaction"] as JsonObject ?? new JsonObject();
			Write(target, "enabled", compaction.Enabled);
			Write(target, "reserveTokens", compaction.ReserveTokens);
			Write(target, "keepRecentTokens", compaction.KeepRecentTokens);
			if (compaction.ModelOverrides is { Count: > 0 } overrides)
			{
				var nodes = new JsonObject();
				foreach (var (modelId, value) in overrides)
				{
					var node = new JsonObject();
					Write(node, "reserveTokens", value.ReserveTokens);
					Write(node, "keepRecentTokens", value.KeepRecentTokens);
					nodes[modelId] = node;
				}

				target["modelOverrides"] = nodes;
			}

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

			if (retry.Provider is { } provider)
			{
				var providerTarget = target["provider"] as JsonObject ?? new JsonObject();
				Write(providerTarget, "timeoutMs", provider.TimeoutMs);
				Write(providerTarget, "maxRetries", provider.MaxRetries);
				Write(providerTarget, "maxRetryDelayMs", provider.MaxRetryDelayMs);
				target["provider"] = providerTarget;
			}

			root["retry"] = target;
		}

		if (file.Codemode is { } codemode)
		{
			var target = root["codemode"] as JsonObject ?? new JsonObject();
			Write(target, "mode", codemode.Mode);
			Write(target, "inlineBudget", codemode.InlineBudget);
			Write(target, "autoEnable", codemode.AutoEnable);
			root["codemode"] = target;
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

		// 用数组构造器，不用 JsonArray.Add<T>：泛型 Add 在 AOT 下会触发 IL2026/IL3050。
		var nodes = new JsonNode?[values.Count];
		for (var index = 0; index < values.Count; index++)
		{
			nodes[index] = JsonValue.Create(values[index]);
		}

		root[name] = new JsonArray(nodes);
	}

	private static int NormalizeIdleTimeout(int value) => value < 0 ? 300_000 : value;

	private static string NormalizeTransport(string? value) => value?.Trim().ToLowerInvariant() switch
	{
		"sse" => "sse",
		"websocket" => "websocket",
		"websocket-cached" => "websocket-cached",
		_ => "auto",
	};

	private static string? NormalizeHttpProxy(string? value) =>
		string.IsNullOrWhiteSpace(value) ? null : value.Trim();

	private static void WriteTimeout(JsonObject root, string name, int? value)
	{
		if (value is not { } timeout)
		{
			return;
		}

		// 0 按 pi 的写法落成 "disabled"：数字 0 在 pi 的 schema 里是合法值（等于关闭），
		// 但 "disabled" 更直白，且两边解析结果一致。
		root[name] = timeout == 0 ? JsonValue.Create("disabled") : JsonValue.Create(timeout);
	}

	internal static SettingsFile? ReadFile(string path)
	{
		if (!File.Exists(path))
		{
			return null;
		}

		try
		{
			return Normalize(JsonSerializer.Deserialize(PiJson.StripBom(File.ReadAllText(path)), CodingAgentJsonContext.Default.SettingsFile));
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

	/// 旧版设置键在内存里迁移到当前形状（pi 的 <c>SettingsManager.migrateSettings</c> 同义）：
	/// <c>queueMode</c> → <c>steeringMode</c>、<c>websockets</c> → <c>transport</c>、
	/// <c>retry.maxDelayMs</c> → <c>retry.provider.maxRetryDelayMs</c>。
	/// 只在加载时改内存，不主动重写文件；下一次保存会自然写成新形状。
	internal static SettingsFile? Normalize(SettingsFile? file)
	{
		if (file is null)
		{
			return null;
		}

		var steering = file.SteeringMode ?? file.QueueMode;
		var transport = file.Transport ?? (file.WebSockets is { } websockets ? (websockets ? "websocket" : "sse") : null);

		var retry = file.Retry;
		if (retry is not null && retry.MaxDelayMs is { } legacyDelay && retry.Provider?.MaxRetryDelayMs is null)
		{
			retry = retry with
			{
				MaxDelayMs = null,
				Provider = (retry.Provider ?? new RetryProviderFile()) with { MaxRetryDelayMs = legacyDelay },
			};
		}

		return file with
		{
			SteeringMode = steering,
			Transport = transport,
			QueueMode = null,
			WebSockets = null,
			Retry = retry,
		};
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
		CacheWarming = project?.CacheWarming ?? global?.CacheWarming ?? "idle",
		ThinkingBudgets = MergeIntDictionary(global?.ThinkingBudgets, project?.ThinkingBudgets),
		Images = new ImagesSettings
		{
			BlockImages = project?.Images?.BlockImages ?? global?.Images?.BlockImages ?? false,
		},
		Transport = NormalizeTransport(project?.Transport ?? global?.Transport),
		Compaction = new CompactionSettings
		{
			Enabled = project?.Compaction?.Enabled ?? global?.Compaction?.Enabled ?? true,
			ReserveTokens = project?.Compaction?.ReserveTokens ?? global?.Compaction?.ReserveTokens ?? 16384,
			KeepRecentTokens = project?.Compaction?.KeepRecentTokens ?? global?.Compaction?.KeepRecentTokens ?? 20000,
			ModelOverrides = MergeCompactionOverrides(global?.Compaction?.ModelOverrides, project?.Compaction?.ModelOverrides),
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
		// pi 的 deep merge 对数组是覆盖：项目声明了 extensions 就不再叠加用户级列表，
		// 否则「项目里关掉某扩展」无法通过空数组表达。
		Extensions = project?.Extensions ?? global?.Extensions ?? [],
		DefaultProjectTrust = project?.DefaultProjectTrust ?? global?.DefaultProjectTrust ?? "ask",
		// pi 把请求超时放在 retry.provider.timeoutMs；timeoutMs 是本端口早期写在根上的键，
		// 仍然读它以免已有配置失效，但新写入只写 pi 的形状。
		TimeoutMs = project?.TimeoutMs
			?? project?.Retry?.Provider?.TimeoutMs
			?? global?.TimeoutMs
			?? global?.Retry?.Provider?.TimeoutMs,
		HttpIdleTimeoutMs = NormalizeIdleTimeout(
			project?.HttpIdleTimeoutMs ?? global?.HttpIdleTimeoutMs ?? DefaultHttpIdleTimeoutMs),
		HttpProxy = NormalizeHttpProxy(global?.HttpProxy),
		ShellPath = project?.ShellPath ?? global?.ShellPath,
		ShellCommandPrefix = project?.ShellCommandPrefix ?? global?.ShellCommandPrefix,
		SessionDir = project?.SessionDir ?? global?.SessionDir,
		SessionShareDestination = project?.SessionShareDestination ?? global?.SessionShareDestination ?? "auto",
		DefaultTools = MergeTools(global?.DefaultTools, project?.DefaultTools),
		DefaultToolsConfigured = project?.DefaultTools is not null || global?.DefaultTools is not null,
		Codemode = new CodemodeSettings
		{
			Mode = NormalizeCodemodeMode(project?.Codemode?.Mode ?? global?.Codemode?.Mode),
			InlineBudget = NormalizeCodemodeBudget(project?.Codemode?.InlineBudget ?? global?.Codemode?.InlineBudget),
			AutoEnable = project?.Codemode?.AutoEnable ?? global?.Codemode?.AutoEnable ?? true,
		},
		// 安全门只认全局设置：项目里的 .pi/settings.json 可以随仓库一起分发，
		// 让它能改自己的安全等级等于让仓库关掉用户的防护。
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

	/// defaultTools 的跨层合并：项目只含 +/− 时叠加到用户列表；出现纯名字时整体替换。
	/// 空列表是合法的“全部禁用”，不能回退到默认值；两层都未配置时返回 <c>null</c>
	/// （表示使用 <see cref="DefaultToolNames"/>），设置界面据此区分「继承」与「显式空选择」。
	public static IReadOnlyList<string>? MergeDefaultToolEntries(IReadOnlyList<string>? global, IReadOnlyList<string>? project)
	{
		if (project is null)
		{
			return global is null ? null : [.. global];
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

	private static IReadOnlyList<string> MergeTools(List<string>? global, List<string>? project) =>
		MergeDefaultToolEntries(global, project) ?? [];

	private static IReadOnlyDictionary<string, int> MergeIntDictionary(
		Dictionary<string, int>? global,
		Dictionary<string, int>? project)
	{
		var result = new Dictionary<string, int>(StringComparer.Ordinal);
		foreach (var source in new[] { global, project })
		{
			if (source is null)
			{
				continue;
			}

			foreach (var (key, value) in source)
			{
				if (value > 0)
				{
					result[key] = value;
				}
			}
		}

		return result;
	}

	private static IReadOnlyDictionary<string, CompactionModelOverride> MergeCompactionOverrides(
		Dictionary<string, CompactionModelOverrideFile>? global,
		Dictionary<string, CompactionModelOverrideFile>? project)
	{
		var result = new Dictionary<string, CompactionModelOverride>(StringComparer.Ordinal);
		foreach (var source in new[] { global, project })
		{
			if (source is null)
			{
				continue;
			}

			foreach (var (modelId, value) in source)
			{
				result.TryGetValue(modelId, out var existing);
				result[modelId] = new CompactionModelOverride
				{
					ReserveTokens = value.ReserveTokens ?? existing?.ReserveTokens,
					KeepRecentTokens = value.KeepRecentTokens ?? existing?.KeepRecentTokens,
				};
			}
		}

		return result;
	}

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
		CacheWarming = settings.CacheWarming,
		ThinkingBudgets = settings.ThinkingBudgets.Count > 0
			? new Dictionary<string, int>(settings.ThinkingBudgets, StringComparer.Ordinal)
			: null,
		Images = settings.Images.BlockImages ? new ImagesFile { BlockImages = true } : null,
		// 只写用户真正改过的值：默认形状每次保存都追加 transport / httpIdleTimeoutMs，
		// 会把配置文件写成一份「本端口认为的默认值」清单，也让人 diff 不出自己改了什么。
		Transport = settings.Transport == "auto" ? null : settings.Transport,
		HttpIdleTimeoutMs = settings.HttpIdleTimeoutMs == DefaultHttpIdleTimeoutMs
			? null
			: settings.HttpIdleTimeoutMs,
		Compaction = new CompactionFile
		{
			Enabled = settings.Compaction.Enabled,
			ReserveTokens = settings.Compaction.ReserveTokens,
			KeepRecentTokens = settings.Compaction.KeepRecentTokens,
			ModelOverrides = settings.Compaction.ModelOverrides.Count > 0
				? settings.Compaction.ModelOverrides.ToDictionary(
					static pair => pair.Key,
					static pair => new CompactionModelOverrideFile
					{
						ReserveTokens = pair.Value.ReserveTokens,
						KeepRecentTokens = pair.Value.KeepRecentTokens,
					},
					StringComparer.Ordinal)
				: null,
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
		Extensions = List(settings.Extensions),
		DefaultProjectTrust = settings.DefaultProjectTrust,
		ShellPath = settings.ShellPath,
		ShellCommandPrefix = settings.ShellCommandPrefix,
		SessionDir = settings.SessionDir,
		SessionShareDestination = settings.SessionShareDestination,
		DefaultTools = settings.DefaultToolsConfigured ? [.. settings.DefaultTools] : null,
		Codemode = new CodemodeFile
		{
			Mode = settings.Codemode.Mode,
			InlineBudget = settings.Codemode.InlineBudget,
			AutoEnable = settings.Codemode.AutoEnable,
		},
		Retry = new RetryFile
		{
			Enabled = settings.Retry.Enabled,
			MaxRetries = settings.Retry.MaxRetries,
			BaseDelayMs = settings.Retry.BaseDelayMs,
			MaxAgentDelayMs = settings.Retry.MaxAgentDelayMs,
			Provider = settings.TimeoutMs is { } timeoutMs
				? new RetryProviderFile { TimeoutMs = timeoutMs }
				: null,
		},
	};

	private static List<string>? List(IReadOnlyList<string> values) => values.Count > 0 ? [.. values] : null;

	private static string NormalizeCodemodeMode(string? value) =>
		string.Equals(value, "only", StringComparison.OrdinalIgnoreCase) ? "only" : "on";

	private static int NormalizeCodemodeBudget(int? value) => value is >= 0 ? value.Value : CodemodeSettings.DefaultInlineBudget;



}

public sealed record CodemodeSettings
{
	public const int DefaultInlineBudget = 3000;

	public string Mode { get; init; } = "on";

	public int InlineBudget { get; init; } = DefaultInlineBudget;

	public bool AutoEnable { get; init; } = true;
}

public sealed record SettingsFile
{
	public string? DefaultProvider { get; init; }

	public string? DefaultModel { get; init; }

	public string? DefaultThinkingLevel { get; init; }

	public Dictionary<string, string>? ModelThinkingLevels { get; init; }

	public List<string>? EnabledModels { get; init; }

	public string? SteeringMode { get; init; }

	public string? FollowUpMode { get; init; }

	public string? CacheWarming { get; init; }

	public Dictionary<string, int>? ThinkingBudgets { get; init; }

	public ImagesFile? Images { get; init; }

	public string? Transport { get; init; }

	public string? QueueMode { get; init; }

	[JsonPropertyName("websockets")]
	public bool? WebSockets { get; init; }

	public CompactionFile? Compaction { get; init; }

	public BranchSummaryFile? BranchSummary { get; init; }

	public bool? EnableSkillCommands { get; init; }

	[JsonConverter(typeof(PackageDeclarationListJsonConverter))]
	public List<PackageDeclaration>? Packages { get; init; }

	[JsonConverter(typeof(FlexibleStringListJsonConverter))]
	public List<string>? Skills { get; init; }

	public List<string>? Prompts { get; init; }

	public List<string>? Themes { get; init; }

	public List<string>? Extensions { get; init; }

	public string? DefaultProjectTrust { get; init; }

	public int? TimeoutMs { get; init; }

	[JsonConverter(typeof(TimeoutMsJsonConverter))]
	public int? HttpIdleTimeoutMs { get; init; }

	public string? HttpProxy { get; init; }

	public string? ShellPath { get; init; }

	public string? ShellCommandPrefix { get; init; }

	public string? SessionDir { get; init; }

	public string SessionShareDestination { get; init; } = "auto";

	public List<string>? DefaultTools { get; init; }

	public RetryFile? Retry { get; init; }

	public CodemodeFile? Codemode { get; init; }
}


public sealed record CodemodeFile
{
	public string? Mode { get; init; }

	public int? InlineBudget { get; init; }

	public bool? AutoEnable { get; init; }
}

public sealed record RetryFile
{
	public bool? Enabled { get; init; }

	public int? MaxRetries { get; init; }

	public int? BaseDelayMs { get; init; }

	public int? MaxAgentDelayMs { get; init; }

	public int? MaxDelayMs { get; init; }

	public RetryProviderFile? Provider { get; init; }
}

public sealed record RetryProviderFile
{
	public int? TimeoutMs { get; init; }

	public int? MaxRetries { get; init; }

	public int? MaxRetryDelayMs { get; init; }
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

	public Dictionary<string, CompactionModelOverrideFile>? ModelOverrides { get; init; }
}

public sealed record ImagesFile
{
	public bool? BlockImages { get; init; }
}

public sealed record CompactionModelOverrideFile
{
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
