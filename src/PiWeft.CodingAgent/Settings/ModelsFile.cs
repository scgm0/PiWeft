using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using PiWeft.Ai;

namespace PiWeft.CodingAgent.Settings;

/// <summary><c>models.json</c> 中的一个 provider 条目。</summary>
public sealed record ModelsProviderEntry
{
	public string? Name { get; init; }

	public string? BaseUrl { get; init; }

	/// <summary>该端点使用的 api，例如 <c>openai-completions</c>。</summary>
	public string? Api { get; init; }

	/// <summary>字面量密钥，或 <c>$NAME</c> / <c>${NAME}</c> 形式的环境变量插值。</summary>
	public string? ApiKey { get; init; }

	public IReadOnlyDictionary<string, string>? Headers { get; init; }

	public JsonObject? Compat { get; init; }

	public bool? AuthHeader { get; init; }

	/// <summary>Radius 等 Provider 的 OAuth 登录方式。</summary>
	public string? OAuth { get; init; }

	/// <summary>在该 provider 上新增或替换的模型。</summary>
	public IReadOnlyList<JsonObject>? Models { get; init; }

	/// <summary>按模型 id 覆盖该 Provider 的内置模型。</summary>
	public IReadOnlyDictionary<string, ModelsOverrideEntry>? ModelOverrides { get; init; }
}

/// <summary>按模型 id 索引的元数据覆盖，作用于已存在的模型。</summary>
public sealed record ModelsOverrideEntry
{
	public string? Name { get; init; }

	public IReadOnlyList<string>? Input { get; init; }

	public bool? Reasoning { get; init; }

	public int? ContextWindow { get; init; }

	public int? MaxTokens { get; init; }

	public ModelCostPatch? Cost { get; init; }

	public IReadOnlyDictionary<string, string?>? ThinkingLevelMap { get; init; }

	public IReadOnlyDictionary<string, string>? Headers { get; init; }

	public JsonObject? Compat { get; init; }

	public JsonObject? InputLimits { get; init; }

	public Dictionary<string, int>? PromptCache { get; init; }

	public JsonObject? SamplingParams { get; init; }
}

/// <summary>modelOverrides 里只覆盖部分价格；未给出的字段保持原值。</summary>
public sealed record ModelCostPatch
{
	public double? Input { get; init; }

	public double? Output { get; init; }

	public double? CacheRead { get; init; }

	public double? CacheWrite { get; init; }
}

/// <summary><c>models.json</c> 文件：自定义 provider，以及对目录模型的覆盖。</summary>
public sealed record ModelsFile
{
	public IReadOnlyDictionary<string, ModelsProviderEntry>? Providers { get; init; }

	public IReadOnlyDictionary<string, ModelsOverrideEntry>? ModelOverrides { get; init; }
}

/// <summary>
/// 加载 <c>models.json</c> 并转换成目录条目。pi 就是靠它指向内置目录里没有的
/// OpenAI / Anthropic / Google 兼容端点（Ollama、LM Studio、vLLM、代理等）。
/// </summary>
public static partial class ModelsFileLoader
{
	/// <summary>用户级路径：<c>&lt;agentDir&gt;/models.json</c>。</summary>
	public static string UserPath(string? agentDir = null) =>
		Path.Combine(agentDir ?? Session.SessionPaths.GetAgentDir(), "models.json");

	/// <summary>项目级路径：<c>&lt;cwd&gt;/.pi/models.json</c>。</summary>
	public static string ProjectPath(string cwd) =>
		Path.Combine(Path.GetFullPath(cwd), Session.SessionPaths.ConfigDirName, "models.json");

	/// <summary>应用该文件后的结果：模型，以及 provider 在请求时需要的凭据与端点。</summary>
	public sealed record Applied
	{
		public IReadOnlyList<Model> Models { get; init; } = [];

		/// <summary>models.json 中声明的虚拟模型。</summary>
		public IReadOnlyList<VirtualModelDefinition> VirtualModels { get; init; } = [];

		/// <summary>provider id 到 API key 的映射，已完成变量插值。</summary>
		public IReadOnlyDictionary<string, string> ApiKeys { get; init; } = new Dictionary<string, string>();

		public int OverrideCount { get; init; }

		/// <summary>最终生效的 provider 条目，用于把 baseUrl/compat 作用于内置模型。</summary>
		public IReadOnlyDictionary<string, ModelsProviderEntry> ProviderEntries { get; init; } =
			new Dictionary<string, ModelsProviderEntry>();
	}

	/// <summary>
	/// 加载并应用两个文件；同名时项目条目替换用户条目。
	/// 项目级 <c>.pi/models.json</c> 能改写端点与请求头，因此不受信的项目必须传
	/// <paramref name="includeProject"/> = <c>false</c>（与 settings / 技能同一信任边界）。
	/// </summary>
	public static Applied Load(string cwd, string? agentDir = null, bool includeProject = true)
	{
		var user = Read(UserPath(agentDir));
		var project = includeProject ? Read(ProjectPath(cwd)) : null;
		return user is null && project is null ? new Applied() : Apply(user, project);
	}

	/// <summary>加载单个文件；缺失或不可读时返回 <c>null</c>。</summary>
	public static ModelsFile? Read(string path)
	{
		if (!File.Exists(path))
		{
			return null;
		}

		try
		{
			return JsonSerializer.Deserialize(File.ReadAllText(path), ModelsJsonContext.Default.ModelsFile);
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

	internal static Applied Apply(ModelsFile? user, ModelsFile? project)
	{
		var providers = new Dictionary<string, ModelsProviderEntry>(StringComparer.Ordinal);
		foreach (var (name, entry) in user?.Providers ?? new Dictionary<string, ModelsProviderEntry>())
		{
			providers[name] = entry;
		}

		foreach (var (name, entry) in project?.Providers ?? new Dictionary<string, ModelsProviderEntry>())
		{
			providers[name] = entry;
		}

		var models = new List<Model>();
		var virtualModels = new List<VirtualModelDefinition>();
		var apiKeys = new Dictionary<string, string>(StringComparer.Ordinal);

		foreach (var (providerId, entry) in providers)
		{
			if (entry.ApiKey is { Length: > 0 } key)
			{
				var resolved = ResolveValue(key);
				if (resolved is { Length: > 0 })
				{
					apiKeys[providerId] = resolved;
				}
			}

			foreach (var raw in entry.Models ?? [])
			{
				if (raw["virtual"] is JsonObject)
				{
					virtualModels.Add(ToVirtualDefinition(providerId, raw));
				}
				else
				{
					models.Add(ToModel(providerId, entry, raw));
				}
			}
		}

		var overrides = new Dictionary<string, ModelsOverrideEntry>(StringComparer.Ordinal);
		foreach (var (id, entry) in user?.ModelOverrides ?? new Dictionary<string, ModelsOverrideEntry>())
		{
			overrides[id] = entry;
		}

		foreach (var (id, entry) in project?.ModelOverrides ?? new Dictionary<string, ModelsOverrideEntry>())
		{
			overrides[id] = entry;
		}

		return new Applied
		{
			Models = models,
			VirtualModels = virtualModels,
			ApiKeys = apiKeys,
			OverrideCount = overrides.Count,
			ProviderEntries = providers,
		};
	}

	/// <summary>把 <c>modelOverrides</c> 应用到注册表中已有的模型。</summary>
	public static int ApplyOverrides(ModelRegistry registry, ModelsFile? user, ModelsFile? project)
	{
		var overrides = new Dictionary<string, ModelsOverrideEntry>(StringComparer.Ordinal);
		CollectOverrides(overrides, user);
		CollectOverrides(overrides, project);

		var applied = 0;
		foreach (var (id, entry) in overrides)
		{
			// 同一个 id 可能出现在多个 provider 上，因此每个匹配都会被覆盖；未知 id 按 pi 的做法忽略。
			foreach (var existing in registry.List().Where(model => string.Equals(model.Id, id, StringComparison.Ordinal)))
			{
				registry.Add(existing with
				{
					Name = entry.Name ?? existing.Name,
					Input = entry.Input ?? existing.Input,
					Reasoning = entry.Reasoning ?? existing.Reasoning,
					ContextWindow = entry.ContextWindow ?? existing.ContextWindow,
					MaxTokens = entry.MaxTokens ?? existing.MaxTokens,
					Cost = MergeCost(existing.Cost, entry.Cost),
					ThinkingLevelMap = entry.ThinkingLevelMap is { Count: > 0 } map
						? new Dictionary<string, string?>(map)
						: existing.ThinkingLevelMap,
					Headers = MergeHeaders(existing.Headers, entry.Headers),
					Compat = MergeNodes(existing.Compat, entry.Compat),
					InputLimits = entry.InputLimits ?? existing.InputLimits,
					PromptCache = entry.PromptCache ?? existing.PromptCache,
					SamplingParams = entry.SamplingParams ?? existing.SamplingParams,
				});

				applied++;
			}
		}

		return applied;
	}

	/// <summary>把 provider 级设置（baseUrl/compat）应用到已经加载的内置模型。</summary>
	public static int ApplyProviderSettings(ModelRegistry registry, ModelsFile? user, ModelsFile? project)
	{
		var providers = new Dictionary<string, ModelsProviderEntry>(StringComparer.Ordinal);
		foreach (var (name, entry) in user?.Providers ?? new Dictionary<string, ModelsProviderEntry>())
		{
			providers[name] = entry;
		}

		foreach (var (name, entry) in project?.Providers ?? new Dictionary<string, ModelsProviderEntry>())
		{
			providers[name] = entry;
		}

		var applied = 0;
		foreach (var (providerId, entry) in providers)
		{
			if (!string.IsNullOrEmpty(entry.BaseUrl) || entry.Compat is not null)
			{
				foreach (var existing in registry.List(providerId).ToList())
				{
					registry.Add(existing with
					{
						BaseUrl = string.IsNullOrEmpty(entry.BaseUrl) ? existing.BaseUrl : entry.BaseUrl,
						Compat = MergeNodes(entry.Compat, existing.Compat),
					});
					applied++;
				}
			}
		}

		return applied;
	}

	private static void CollectOverrides(Dictionary<string, ModelsOverrideEntry> overrides, ModelsFile? file)
	{
		foreach (var (id, entry) in file?.ModelOverrides ?? new Dictionary<string, ModelsOverrideEntry>())
		{
			overrides[id] = entry;
		}

		foreach (var (_, provider) in file?.Providers ?? new Dictionary<string, ModelsProviderEntry>())
		{
			foreach (var (id, entry) in provider.ModelOverrides ?? new Dictionary<string, ModelsOverrideEntry>())
			{
				overrides[id] = entry;
			}
		}
	}

	private static ModelCost MergeCost(ModelCost existing, ModelCostPatch? patch) =>
		patch is null
			? existing
			: new ModelCost
			{
				Input = patch.Input ?? existing.Input,
				Output = patch.Output ?? existing.Output,
				CacheRead = patch.CacheRead ?? existing.CacheRead,
				CacheWrite = patch.CacheWrite ?? existing.CacheWrite,
				Tiers = existing.Tiers,
			};

	private static JsonObject? MergeNodes(JsonObject? existing, JsonObject? patch)
	{
		if (patch is null)
		{
			return existing;
		}

		var merged = existing is null ? new JsonObject() : (JsonObject)existing.DeepClone();
		foreach (var (key, value) in patch)
		{
			merged[key] = value?.DeepClone();
		}

		return merged;
	}

	private static VirtualModelDefinition ToVirtualDefinition(string providerId, JsonObject raw)
	{
		var id = raw["id"]?.GetValue<string>()
			?? throw new InvalidOperationException($"A virtual model of provider '{providerId}' in models.json has no id");
		if (raw["virtual"] is not JsonObject settings)
		{
			throw new InvalidOperationException($"Virtual model '{providerId}/{id}' has no virtual settings");
		}

		var target = settings["model"]?.GetValue<string>();
		var (routeSource, routeTimeoutMs) = RouteSource(settings["route"]);
		if (target is null && routeSource is null)
		{
			throw new InvalidOperationException(
				$"Virtual model '{providerId}/{id}' must declare either a target model or a route script");
		}

		var thinking = settings["thinkingLevel"]?.GetValue<string>();
		var levels = StringArray(settings["thinkingLevels"]);
		var input = StringArray(settings["input"] ?? raw["input"]);

		return new VirtualModelDefinition
		{
			Provider = providerId,
			Id = id,
			Name = raw["name"]?.GetValue<string>() is { Length: > 0 } name ? name : id,
			ThinkingLevels = levels.Count > 0 ? levels : [ThinkingLevels.Off],
			Input = input.Count > 0 ? input : ["text", "image"],
			ContextWindow = raw["contextWindow"]?.GetValue<int>() ?? 0,
			MaxTokens = raw["maxTokens"]?.GetValue<int>() ?? 0,
			Route = routeSource is not null
				? VirtualModelScript.Create(providerId, id, routeSource, routeTimeoutMs)
				: VirtualModels.StaticRoute(target!, thinking),
		};
	}

	/// <summary>
	/// <c>virtual.route</c> 可以是源码字符串，或 <c>{ "code": "...", "timeoutMs": 2000 }</c>。
	/// </summary>
	private static (string? Source, int? TimeoutMs) RouteSource(JsonNode? node)
	{
		if (node is JsonValue value && value.TryGetValue<string>(out var source) && !string.IsNullOrWhiteSpace(source))
		{
			return (source, null);
		}

		if (node is JsonObject settings)
		{
			var code = settings["code"]?.GetValue<string>() ?? settings["source"]?.GetValue<string>();
			if (code is { Length: > 0 })
			{
				return (code, settings["timeoutMs"]?.GetValue<int>());
			}
		}

		return (null, null);
	}

	private static IReadOnlyList<string> StringArray(JsonNode? node)
	{
		if (node is not JsonArray array)
		{
			return [];
		}

		var values = new List<string>();
		foreach (var item in array)
		{
			if (item?.GetValue<string>() is { Length: > 0 } value)
			{
				values.Add(value);
			}
		}

		return values;
	}

	private static Model ToModel(string providerId, ModelsProviderEntry provider, JsonObject raw)
	{
		var id = raw["id"]?.GetValue<string>()
			?? throw new InvalidOperationException($"A model of provider '{providerId}' in models.json has no id");

		var model = JsonSerializer.Deserialize(raw, ModelsJsonContext.Default.Model) ?? new Model();

		return model with
		{
			Id = id,
			Provider = providerId,
			Api = FirstNonEmpty(model.Api, provider.Api) ?? "openai-completions",
			BaseUrl = FirstNonEmpty(model.BaseUrl, provider.BaseUrl) ?? string.Empty,
			Name = string.IsNullOrEmpty(model.Name) ? id : model.Name,
			Compat = MergeNodes(provider.Compat, model.Compat),
			Headers = MergeHeaders(provider.Headers is null
				? null
				: provider.Headers.ToDictionary(static pair => pair.Key, static pair => ResolveValue(pair.Value) ?? string.Empty), model.Headers),
		};
	}

	private static Dictionary<string, string>? MergeHeaders(
		Dictionary<string, string>? first,
		IReadOnlyDictionary<string, string>? second)
	{
		if (first is null && second is null)
		{
			return null;
		}

		var merged = first is null
			? new Dictionary<string, string>(StringComparer.Ordinal)
			: new Dictionary<string, string>(first, StringComparer.Ordinal);

		foreach (var (name, value) in second ?? new Dictionary<string, string>())
		{
			merged[name] = value;
		}

		return merged;
	}

	private static string? FirstNonEmpty(string? first, string? second) =>
		!string.IsNullOrEmpty(first) ? first : !string.IsNullOrEmpty(second) ? second : null;

	/// <summary>解析 models.json 中的值：<c>!command</c> 执行命令，其余做环境变量插值。</summary>
	internal static string? ResolveValue(string? value)
	{
		if (value is { Length: > 1 } && value[0] == '!')
		{
			var command = value[1..].Trim();
			return command.Length == 0 ? null : PiWeft.Ai.Auth.AuthStore.ExecuteCommandKey(command) ?? value;
		}

		return Interpolate(value);
	}

	/// <summary>
	/// 展开 <c>$NAME</c> 与 <c>${NAME}</c>。以 <c>!command</c> 开头的形式（pi 会在请求时执行 shell 命令）
	/// 在本端口中有意不执行。
	/// </summary>
	internal static string? Interpolate(string? value)
	{
		if (string.IsNullOrEmpty(value) || !value.Contains('$', StringComparison.Ordinal))
		{
			return value;
		}

		return InterpolationPattern().Replace(value, static match =>
			Environment.GetEnvironmentVariable(match.Groups["name"].Value) ?? string.Empty);
	}

	[System.Text.RegularExpressions.GeneratedRegex(@"\$\{?(?<name>[A-Za-z_][A-Za-z0-9_]*)\}?")]
	private static partial System.Text.RegularExpressions.Regex InterpolationPattern();
}

[JsonSourceGenerationOptions(
	PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
	DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
	WriteIndented = true,
	ReadCommentHandling = JsonCommentHandling.Skip,
	AllowTrailingCommas = true)]
[JsonSerializable(typeof(ModelsFile))]
[JsonSerializable(typeof(ModelsProviderEntry))]
[JsonSerializable(typeof(ModelsOverrideEntry))]
[JsonSerializable(typeof(Model))]
[JsonSerializable(typeof(ModelCost))]
internal partial class ModelsJsonContext : JsonSerializerContext
{
	// 保留源生成选项，只替换编码器。
	static ModelsJsonContext()
	{
		Default = new ModelsJsonContext(CreateOptions(Default));
	}

	private static JsonSerializerOptions CreateOptions(ModelsJsonContext defaultContext) =>
		new(defaultContext.GeneratedSerializerOptions!)
		{
			Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
		};
}
