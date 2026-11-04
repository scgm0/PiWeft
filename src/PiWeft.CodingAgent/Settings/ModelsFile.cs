using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using PiWeft.Ai;

namespace PiWeft.CodingAgent.Settings;

public sealed record ModelsProviderEntry
{
	public string? Name { get; init; }

	public string? BaseUrl { get; init; }

	public string? Api { get; init; }

	public string? ApiKey { get; init; }

	public IReadOnlyDictionary<string, string>? Headers { get; init; }

	public JsonObject? Compat { get; init; }

	public bool? AuthHeader { get; init; }

	public string? OAuth { get; init; }

	public IReadOnlyList<JsonObject>? Models { get; init; }

	public IReadOnlyDictionary<string, ModelsOverrideEntry>? ModelOverrides { get; init; }
}

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

public sealed record ModelCostPatch
{
	public double? Input { get; init; }

	public double? Output { get; init; }

	public double? CacheRead { get; init; }

	public double? CacheWrite { get; init; }
}

public sealed record ModelsFile
{
	public IReadOnlyDictionary<string, ModelsProviderEntry>? Providers { get; init; }

	public IReadOnlyDictionary<string, ModelsOverrideEntry>? ModelOverrides { get; init; }
}

/// 加载 <c>models.json</c> 并转换成目录条目。pi 就是靠它指向内置目录里没有的
/// OpenAI / Anthropic / Google 兼容端点（Ollama、LM Studio、vLLM、代理等）。
public static partial class ModelsFileLoader
{
	public static string UserPath(string? agentDir = null) =>
		Path.Combine(agentDir ?? Session.SessionPaths.GetAgentDir(), "models.json");

	public static string ProjectPath(string cwd) =>
		Path.Combine(Path.GetFullPath(cwd), Session.SessionPaths.ConfigDirName, "models.json");

	public sealed record Applied
	{
		public IReadOnlyList<Model> Models { get; init; } = [];

		public IReadOnlyList<VirtualModelDefinition> VirtualModels { get; init; } = [];

		public IReadOnlyDictionary<string, string> ApiKeys { get; init; } = new Dictionary<string, string>();

		public int OverrideCount { get; init; }

		public IReadOnlyDictionary<string, ModelsProviderEntry> ProviderEntries { get; init; } =
			new Dictionary<string, ModelsProviderEntry>();
	}

	/// 加载并应用两个文件；同名时项目条目替换用户条目。
	/// 项目级 <c>.pi/models.json</c> 能改写端点与请求头，因此不受信的项目必须传
	/// <paramref name="includeProject"/> = <c>false</c>（与 settings / 技能同一信任边界）。
	public static Applied Load(string cwd, string? agentDir = null, bool includeProject = true)
	{
		var user = Read(UserPath(agentDir));
		var project = includeProject ? Read(ProjectPath(cwd)) : null;
		return user is null && project is null ? new Applied() : Apply(user, project);
	}

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
			if (!string.IsNullOrEmpty(entry.BaseUrl) || entry.Compat is not null || entry.Headers is { Count: > 0 } || entry.AuthHeader is not null)
			{
				// provider 级 headers 也必须叠到内置模型上：Copilot 代理、TokenDance 归因、
				// 自建网关的额外鉴权头都靠它；模型自己的同名头仍然优先。
				var providerHeaders = entry.Headers is { Count: > 0 }
					? entry.Headers.ToDictionary(static pair => pair.Key, static pair => ResolveValue(pair.Value) ?? string.Empty)
					: null;
				foreach (var existing in registry.List(providerId).ToList())
				{
					registry.Add(existing with
					{
						BaseUrl = string.IsNullOrEmpty(entry.BaseUrl) ? existing.BaseUrl : entry.BaseUrl,
						Compat = MergeNodes(entry.Compat, existing.Compat),
						Headers = MergeHeaders(providerHeaders, existing.Headers),
						AuthHeader = entry.AuthHeader ?? existing.AuthHeader,
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

	/// <c>virtual.route</c> 可以是源码字符串，或 <c>{ "code": "...", "timeoutMs": 2000 }</c>。
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
			AuthHeader = provider.AuthHeader ?? false,
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

	internal static string? ResolveValue(string? value)
	{
		if (value is { Length: > 1 } && value[0] == '!')
		{
			var command = value[1..].Trim();
			return command.Length == 0 ? null : PiWeft.Ai.Auth.AuthStore.ExecuteCommandKey(command) ?? value;
		}

		return Interpolate(value);
	}

	/// 展开 <c>$NAME</c> 与 <c>${NAME}</c>。以 <c>!command</c> 开头的形式（pi 会在请求时执行 shell 命令）
	/// 在本端口中有意不执行。
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
