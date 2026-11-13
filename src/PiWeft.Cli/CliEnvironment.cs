using System.Collections.Frozen;
using PiWeft.Agent;
using PiWeft.Ai;
using PiWeft.Ai.Providers;
using PiWeft.Ai.Providers.Anthropic;
using PiWeft.Ai.Providers.Bedrock;
using PiWeft.Ai.Providers.Classifiers;
using PiWeft.Ai.Providers.Google;
using PiWeft.Ai.Providers.Mistral;
using PiWeft.Ai.Providers.PiMessages;
using PiWeft.Ai.Providers.OpenAi;
using PiWeft.CodingAgent;
using PiWeft.CodingAgent.Extensions;
using PiWeft.CodingAgent.Models;
using PiWeft.CodingAgent.Resources;
using PiWeft.Mcp;
using PiWeft.CodingAgent.Session;
using PiWeft.CodingAgent.Settings;
using PiWeft.CodingAgent.Subagents;
using PiWeft.CodingAgent.Tools;

namespace PiWeft.Cli;

public sealed class CliEnvironment
{
	private CliEnvironment(
		ModelRegistry registry,
		ModelsGateway gateway,
		ImagesGateway images,
		ClassifiersGateway classifiers,
		CodingAgentSettings settings,
		Model model,
		IReadOnlyList<AgentTool> tools,
		IReadOnlyDictionary<string, string> providerApiKeys,
		bool projectTrusted)
	{
		Registry = registry;
		Gateway = gateway;
		Images = images;
		Classifiers = classifiers;
		Settings = settings;
		Model = model;
		Tools = tools;
		ProviderApiKeys = providerApiKeys;
		ProjectTrusted = projectTrusted;
	}

	/// 项目是否受信任。为 false 时不合并项目设置、不连接项目 MCP，也不加载项目技能与提示词模板。
	/// 一次启动只解析一次，会话构造与 MCP 连接共用同一个决策。
	public bool ProjectTrusted { get; } = true;

	public IReadOnlyDictionary<string, string> ProviderApiKeys { get; }

	internal StreamFn? StreamFnOverride { get; init; }

	internal static CliEnvironment ForTests(
		ModelRegistry registry,
		CodingAgentSettings settings,
		Model model,
		IReadOnlyList<AgentTool> tools,
		StreamFn streamFn)
	{
		var gateway = new ModelsGateway(registry);
		var images = new ImagesGateway();
		images.Register(new OpenRouterImagesProvider());
		var classifiers = new ClassifiersGateway();
		classifiers.Register(new OpenAiDecisionsProvider());
		classifiers.Register(new SystemOneClassifierProvider(cloudflare: false));
		classifiers.Register(new SystemOneClassifierProvider(cloudflare: true));
		return new CliEnvironment(registry, gateway, images, classifiers, settings, model, tools, new Dictionary<string, string>(), projectTrusted: true)
		{
			StreamFnOverride = streamFn,
		};
	}

	public ModelRegistry Registry { get; }

	public ModelsGateway Gateway { get; }

	public ImagesGateway Images { get; }

	public ClassifiersGateway Classifiers { get; }

	public CodingAgentSettings Settings { get; }

	public Model Model { get; }

	public IReadOnlyList<AgentTool> Tools { get; }

	private StreamFn? _streamFn;

	public StreamFn StreamFn => _streamFn ??= AgentGatewayStreamFn.For(Gateway);

	internal StreamFn ProbeStreamFn => StreamFnOverride ?? StreamFn;

	public static readonly string[] SupportedApis =
	[
		AnthropicProvider.ApiId,
		OpenAiCompletionsProvider.ApiId,
		OpenAiResponsesProvider.ApiId,
		GoogleGenerativeAiProvider.ApiId,
		GoogleVertexProvider.ApiId,
		AzureOpenAiResponsesProvider.ApiId,
		BedrockConverseProvider.ApiId,
		PiMessagesProvider.ApiId,
		MistralConversationsProvider.ApiId,
		OpenAiCodexResponsesProvider.ApiId,
		VirtualModels.Api,
	];

	private static readonly FrozenSet<string> SupportedApiSet = SupportedApis.ToFrozenSet(StringComparer.Ordinal);

	public static bool SupportsApi(string api) => SupportedApiSet.Contains(api);

	public static (CliEnvironment? Environment, string? Error) Create(CliArguments arguments, string cwd) =>
		Create(arguments, cwd, handler: null);

	internal static (CliEnvironment? Environment, string? Error) Create(
		CliArguments arguments,
		string cwd,
		HttpMessageHandler? handler)
	{
		// 信任决策只看全局 settings.defaultProjectTrust，项目设置不能自我授权。
		// 不受信时不合并项目设置，避免一个未受信项目通过 .pi/settings.json 影响行为。
		var defaultTrust = CodingAgentSettings.GlobalDefaultProjectTrust();
		var projectTrusted = ProjectTrust.Resolve(cwd, arguments.ProjectTrustOverride, defaultTrust);
		var settings = CodingAgentSettings.Load(cwd, agentDir: null, includeProject: projectTrusted);
		var registry = ModelCatalogOverlay.CreateRegistry();

		// models.json 会新增或替换目录条目，兼容端点就是靠它注册进来的。
		// 项目级文件能改写端点与请求头，必须跟 settings 用同一套信任门控。
		var userModels = ModelsFileLoader.Read(ModelsFileLoader.UserPath());
		var projectModels = projectTrusted ? ModelsFileLoader.Read(ModelsFileLoader.ProjectPath(cwd)) : null;
		var customModels = ModelsFileLoader.Load(cwd, agentDir: null, includeProject: projectTrusted);

		// 远程目录缓存覆盖内置条目；provider 级 baseUrl/compat 再统一作用于所有条目。
		ModelCatalogStore.LoadCached(registry);
		ModelsFileLoader.ApplyProviderSettings(registry, userModels, projectModels);
		ApplyOAuthProviderEndpoints(registry);
		foreach (var custom in customModels.Models)
		{
			registry.Add(custom);
		}

		foreach (var virtualModel in customModels.VirtualModels)
		{
			registry.AddVirtual(virtualModel);
		}

		if (customModels.OverrideCount > 0)
		{
			ModelsFileLoader.ApplyOverrides(registry, userModels, projectModels);
		}

		var gateway = new ModelsGateway(registry);

		// 共享客户端让连接池在进程内共用；覆盖 handler 则用于测试。
		var httpClient = handler is null ? null : new HttpClient(handler);
		gateway.Register(new AnthropicProvider(httpClient));
		gateway.Register(new OpenAiCompletionsProvider(httpClient));
		gateway.Register(new OpenAiResponsesProvider(httpClient));
		gateway.Register(new GoogleGenerativeAiProvider(httpClient));
		gateway.Register(new GoogleVertexProvider(httpClient));
		gateway.Register(new AzureOpenAiResponsesProvider(httpClient));
		gateway.Register(new BedrockConverseProvider(httpClient));
		gateway.Register(new PiMessagesProvider(httpClient));
		gateway.Register(new MistralConversationsProvider(httpClient));
		gateway.Register(new OpenAiCodexResponsesProvider(httpClient));

		var images = new ImagesGateway();
		images.Register(new OpenRouterImagesProvider(httpClient));
		var classifiers = new ClassifiersGateway();
		classifiers.Register(new OpenAiDecisionsProvider(httpClient));
		classifiers.Register(new SystemOneClassifierProvider(cloudflare: false, httpClient));
		classifiers.Register(new SystemOneClassifierProvider(cloudflare: true, httpClient));

		var model = ResolveModel(registry, arguments, settings, out var modelError, customModels.ApiKeys);
		if (model is null)
		{
			return (null, modelError ?? "No usable model found.");
		}

		var tools = ResolveTools(arguments, cwd, settings);
		return (new CliEnvironment(registry, gateway, images, classifiers, settings, model, tools, customModels.ApiKeys, projectTrusted), null);
	}

	/// 启动时按 provider 查一次；冻结字典让查找走预计算哈希，不再走可变字典的分支。
	private static readonly System.Collections.Frozen.FrozenDictionary<string, string> DefaultModelPerProvider =
		new Dictionary<string, string>(StringComparer.Ordinal)
		{
		["amazon-bedrock"] = "us.anthropic.claude-opus-5-5",
		["ant-ling"] = "Ring-2.6-1T",
		["anthropic"] = "claude-opus-5-5",
		["openai"] = "gpt-6.1-sol",
		["azure"] = "gpt-6.1-sol",
		["openai-codex"] = "gpt-6.1-sol",
		["radius"] = "balanced",
		["nvidia"] = "nvidia/nemotron-3.5-super-vl-120b-a12b",
		["deepseek"] = "deepseek-v4-pro",
		["google"] = "gemini-4-argon",
		["google-vertex"] = "gemini-4-argon",
		["github-copilot"] = "gpt-6.1-sol",
		["openrouter"] = "moonshotai/kimi-k2.7-code",
		["vercel-ai-gateway"] = "zai/glm-5.3",
		["xai"] = "grok-4.7",
		["groq"] = "openai/gpt-oss-120b",
		["cerebras"] = "gpt-oss-120b",
		["zai"] = "glm-5.3",
		["zai-coding-cn"] = "glm-5.3",
		["mistral"] = "devstral-medium-latest",
		["minimax"] = "MiniMax-M3",
		["minimax-cn"] = "MiniMax-M3",
		["moonshotai"] = "kimi-k2.7-code",
		["moonshotai-cn"] = "kimi-k2.7-code",
		["huggingface"] = "moonshotai/Kimi-K2.7-Code",
		["fireworks"] = "accounts/fireworks/models/kimi-k3",
		["together"] = "moonshotai/Kimi-K3",
		["baseten"] = "zai-org/GLM-5.3",
		["opencode"] = "kimi-k2.7-code",
		["opencode-go"] = "kimi-k3.1-pro",
		["kimi-coding"] = "kimi-for-coding",
		["meta"] = "muse-spark-1.3",
		["cloudflare-workers-ai"] = "@cf/moonshotai/kimi-k2.7-code",
		["cloudflare-ai-gateway"] = "workers-ai/@cf/moonshotai/kimi-k2.7-code",
		["qwen-token-plan"] = "qwen3.8-max",
		["qwen-token-plan-cn"] = "qwen3.8-max",
		["qwen-token-plan-individual"] = "qwen3.8-max",
		["qwen-token-plan-individual-cn"] = "qwen3.8-max",
		["xiaomi"] = "mimo-v2.6-pro",
		["xiaomi-token-plan-cn"] = "mimo-v2.6-pro",
		["xiaomi-token-plan-ams"] = "mimo-v2.6-pro",
		["xiaomi-token-plan-sgp"] = "mimo-v2.6-pro",
		}.ToFrozenDictionary(StringComparer.Ordinal);

	public static Model? ResolveModel(
		ModelRegistry registry,
		CliArguments arguments,
		CodingAgentSettings settings,
		out string? error,
		IReadOnlyDictionary<string, string>? providerApiKeys = null)
	{
		error = null;

		var requested = arguments.Model;
		var provider = arguments.Provider;

		if (requested is null && settings.DefaultModel is { Length: > 0 } defaultModel)
		{
			requested = defaultModel;
			provider ??= settings.DefaultProvider;
		}

		if (requested is not null)
		{
			var match = FindModel(registry, requested, provider, providerApiKeys, arguments.ApiKey);
			if (match is null)
			{
				if (TryBuildFallbackModel(registry, requested, provider, arguments.ThinkingLevel, out var fallback))
				{
					return fallback;
				}

				// 裸 id 同时命中多个 provider 且凭据无法区分时，提示用户用 --provider 消歧。
				error = registry.FindById(requested).Count > 1
					? $"Ambiguous model '{requested}': multiple providers share this id. Pass --provider to disambiguate."
					: $"Model not found: {requested}";
				return null;
			}

			if (!SupportsApi(match.Api))
			{
				error = $"Model '{match.Provider}/{match.Id}' uses api '{match.Api}', which this port does not implement yet. Supported: {string.Join(", ", SupportedApis)}.";
				return null;
			}

			return match;
		}

		// 每个 provider 只解析一次认证，避免无密钥时逐模型重复读取 auth.json。
		var available = ModelAvailability.Filter(registry.ChatModels(),
			providerApiKeys ?? new Dictionary<string, string>(), arguments.ApiKey);
		if (available.Count > 0)
		{
			foreach (var (providerId, modelId) in DefaultModelPerProvider)
			{
				if (available.FirstOrDefault(model =>
					string.Equals(model.Provider, providerId, StringComparison.Ordinal) &&
					string.Equals(model.Id, modelId, StringComparison.Ordinal)) is { } preferred)
				{
					return preferred;
				}
			}

			return available[0];
		}

		// 完全没有密钥时，仍以第一个受支持的对话模型启动，桌面客户端能访问 RPC 面并展示模型选择；
		// 发送提示会以 provider 自己的「缺少密钥」错误失败，与 pi 的 RPC 模式一致。
		foreach (var candidate in registry.ChatModels())
		{
			if (SupportsApi(candidate.Api))
			{
				return candidate;
			}
		}

		error = "No model configured and no provider API key found. Pass --model, or set one in ~/.pi/agent/settings.json.";
		return null;
	}

	private static bool TryBuildFallbackModel(
		ModelRegistry registry,
		string requested,
		string? provider,
		string? thinkingLevel,
		out Model model)
	{
		model = null!;
		var slash = requested.IndexOf('/');
		var prefix = slash > 0 ? requested[..slash] : null;
		var providerId = provider ?? prefix;
		var hasProviderPrefix = slash > 0 && (provider is null || string.Equals(prefix, provider, StringComparison.Ordinal));
		var modelId = hasProviderPrefix ? requested[(slash + 1)..] : requested;

		if (providerId is not { Length: > 0 })
		{
			return false;
		}

		var template = registry.List(providerId).FirstOrDefault();
		if (template is null)
		{
			return false;
		}

		model = template with
		{
			Id = modelId,
			Name = modelId,
			Reasoning = template.Reasoning || !string.IsNullOrEmpty(thinkingLevel) && thinkingLevel != ThinkingLevels.Off,
		};
		return true;
	}

	/// 按 <c>provider/id</c>、精确 id、glob 模式或子串查找模型。与 pi 1.3.0 一致：
	/// 同名 id 出现在多个 provider 时优先选有可用凭据的那个，仍无法区分时返回 null，
	/// 由调用方报「模型未找到/有歧义」。
	public static Model? FindModel(
		ModelRegistry registry,
		string pattern,
		string? provider = null,
		IReadOnlyDictionary<string, string>? providerApiKeys = null,
		string? explicitApiKey = null)
	{
		if (pattern.Length == 0)
		{
			// 空模式（例如 `--model=`）会让后面的子串匹配 `Contains("")` 恒真，静默选中第一个模型；
			// 这里显式返回未命中，交给调用方报错。
			return null;
		}

		if (provider is not null && registry.Get(provider, pattern) is { } direct)
		{
			return direct;
		}

		if (provider is null && registry.Resolve(pattern) is { } byKey)
		{
			return byKey;
		}

		// 裸 id 命中多个 provider：优先有凭据者；不知道凭据情况或仍然并列时返回「有歧义」，
		// 让调用方报错，不猜一个。
		var byId = registry.FindById(pattern)
			.Where(m => SupportsApi(m.Api))
			.Where(m => provider is null || string.Equals(m.Provider, provider, StringComparison.Ordinal))
			.ToList();
		if (byId.Count > 1)
		{
			var credentialed = PreferCredentialed(byId, providerApiKeys, explicitApiKey);
			return credentialed.Count == 1 ? credentialed[0] : null;
		}

		var candidates = registry.ChatModels(provider);
		var qualified = candidates.Select(model => (Model: model, Key: $"{model.Provider}/{model.Id}")).ToList();

		// glob 模式同时匹配“provider/id”与裸 id。
		if (pattern.IndexOfAny(['*', '?']) >= 0)
		{
			var regex = SearchTools.GlobToRegex(pattern);
			var matches = qualified
				.Where(entry => regex.IsMatch(entry.Key) || regex.IsMatch(entry.Model.Id))
				.Select(static entry => entry.Model)
				.ToList();

			if (matches.Count > 0)
			{
				return PreferCredentialed(matches, providerApiKeys, explicitApiKey) is { Count: > 0 } preferred
					? preferred[0]
					: null;
			}
		}

		var substring = qualified
			.Where(entry =>
				entry.Key.Contains(pattern, StringComparison.OrdinalIgnoreCase) ||
				entry.Model.Id.Contains(pattern, StringComparison.OrdinalIgnoreCase))
			.Select(static entry => entry.Model)
			.ToList();

		if (substring.Count > 0)
		{
			return PreferCredentialed(substring, providerApiKeys, explicitApiKey) is { Count: > 0 } preferred
				? preferred[0]
				: null;
		}

		return null;
	}

	private static IReadOnlyList<Model> PreferCredentialed(
		IReadOnlyList<Model> candidates,
		IReadOnlyDictionary<string, string>? providerApiKeys,
		string? explicitApiKey)
	{
		if (providerApiKeys is null || candidates.Count <= 1)
		{
			return candidates;
		}

		return ModelAvailability.Filter(candidates, providerApiKeys, explicitApiKey);
	}

	public static bool MatchesModelPattern(Model model, string pattern)
	{
		var qualified = $"{model.Provider}/{model.Id}";
		if (pattern.IndexOfAny(['*', '?']) >= 0)
		{
			var regex = SearchTools.GlobToRegex(pattern);
			return regex.IsMatch(qualified) || regex.IsMatch(model.Id);
		}

		return qualified.Contains(pattern, StringComparison.OrdinalIgnoreCase) ||
			model.Id.Contains(pattern, StringComparison.OrdinalIgnoreCase);
	}

	public IReadOnlyList<Model> ListModels(string? search = null) => ListModels(Registry, search);

	public IReadOnlyList<Model> ListAvailableModels(string? explicitKey = null) =>
		ModelAvailability.Filter(ListModels(), ProviderApiKeys, explicitKey);

	public static IReadOnlyList<Model> ListModels(string cwd, string? search) =>
		ListModels(BuildRegistry(cwd), search);

	private static void ApplyOAuthProviderEndpoints(ModelRegistry registry)
	{
		foreach (var provider in registry.Providers)
		{
			if (PiWeft.Ai.Auth.OAuthStore.Read(provider)?.BaseUrl is not { Length: > 0 } baseUrl)
			{
				continue;
			}

			foreach (var model in registry.List(provider).ToList())
			{
				registry.Add(model with { BaseUrl = baseUrl });
			}
		}
	}

	public static ModelRegistry BuildRegistry(string cwd)
	{
		var registry = ModelCatalogOverlay.CreateRegistry();
		var userModels = ModelsFileLoader.Read(ModelsFileLoader.UserPath());
		var projectModels = ModelsFileLoader.Read(ModelsFileLoader.ProjectPath(cwd));
		// 先叠加远程缓存与自定义模型，再应用 provider 级设置：否则缓存目录里已有 headers 的模型
		// 会漏掉 provider 级补头/端点覆盖（Copilot 代理、TokenDance 归因、自建网关等）。
		ModelCatalogStore.LoadCached(registry);
		// 内置 + 远程缓存模型都先套 provider 级设置；自定义模型在 ToModel 里已按「模型头优先」
		// 合并过 provider 设置，不能在这里再用 provider baseUrl 覆盖模型自己的端点。
		ModelsFileLoader.ApplyProviderSettings(registry, userModels, projectModels);
		// 与 CliEnvironment.Create 的装配顺序一致：Copilot proxy / 自定义 Radius 端点也要作用于缓存模型。
		ApplyOAuthProviderEndpoints(registry);

		var customModels = ModelsFileLoader.Load(cwd);
		foreach (var custom in customModels.Models)
		{
			registry.Add(custom);
		}

		foreach (var virtualModel in customModels.VirtualModels)
		{
			registry.AddVirtual(virtualModel);
		}

		ModelsFileLoader.ApplyOverrides(registry, userModels, projectModels);
		return registry;
	}

	private static IReadOnlyList<Model> ListModels(ModelRegistry registry, string? search)
	{
		IEnumerable<Model> models = registry.ChatModels().Where(m => SupportsApi(m.Api));

		if (search is { Length: > 0 })
		{
			models = models.Where(m =>
				m.Id.Contains(search, StringComparison.OrdinalIgnoreCase) ||
				m.Provider.Contains(search, StringComparison.OrdinalIgnoreCase) ||
				$"{m.Provider}/{m.Id}".Contains(search, StringComparison.OrdinalIgnoreCase));
		}

		return [.. models.OrderBy(static m => m.Provider, StringComparer.Ordinal).ThenBy(static m => m.Id, StringComparer.Ordinal)];
	}

	public static IReadOnlyList<AgentTool> ResolveTools(CliArguments arguments, string cwd) =>
		ResolveTools(arguments, cwd, new CodingAgentSettings());

	/// 与 pi 相同的三态语义：默认集来自 <c>settings.defaultTools</c>（未配置时用
	/// <see cref="CodingAgentSettings.DefaultToolNames"/>）；<c>--tools</c> 的纯名字列表整体替换默认集，
	/// 只含 <c>+</c>/<c>-</c> 时在默认集上增删；<c>--exclude-tools</c> 最后应用并支持通配。
	/// 模式永远针对**完整**内置目录展开，因此默认关闭的 <c>grep</c>/<c>find</c>/<c>ls</c> 仍可显式选中。
	public static IReadOnlyList<AgentTool> ResolveTools(CliArguments arguments, string cwd, CodingAgentSettings settings)
	{
		if (arguments.NoTools)
		{
			return [];
		}

		// 目录含默认不启用的 codemode，这样 --tools/--exclude-tools/defaultTools 才能选中它。
		var catalog = arguments.NoBuiltinTools ? [] : BuiltinTools.CatalogTools(cwd);
		// settings.extensions 关掉 builtin:codemode / builtin:tool-search 后，目录里也不该再有它：
		// 否则 defaultTools 写 +codemode 仍会把它带回来。
		if (!settings.IsBuiltinExtensionEnabled(ExtensionRules.CodemodeBuiltin))
		{
			catalog = [.. catalog.Where(tool => !string.Equals(tool.Name, CodeModeTool.Name, StringComparison.Ordinal))];
		}

		if (!settings.IsBuiltinExtensionEnabled(ExtensionRules.ToolSearchBuiltin))
		{
			catalog = [.. catalog.Where(tool => !string.Equals(tool.Name, ToolSearchTool.Name, StringComparison.Ordinal))];
		}

		if (catalog.Count == 0)
		{
			return [];
		}

		// 默认集：settings.defaultTools 的纯名字整体替换 pi 默认集，只有 +/- 时在默认集上增删。
		// 显式空列表是「禁用全部内置工具」，不能退回默认集（与 pi 的 resolveDefaultTools 一致）。
		var defaults = settings.DefaultToolsConfigured ? settings.DefaultTools : CodingAgentSettings.DefaultToolNames;
		var tools = settings.DefaultToolsConfigured && defaults.Count == 0
			? new List<AgentTool>()
			: ApplyToolEntries(
				[.. catalog.Where(tool => CodingAgentSettings.DefaultToolNames.Contains(tool.Name, StringComparer.Ordinal))],
				catalog,
				defaults,
				allowPatterns: false);

		if (arguments.Tools.Count > 0)
		{
			tools = ApplyToolEntries(tools, catalog, arguments.Tools, allowPatterns: true);
		}

		if (arguments.ExcludeTools.Count > 0)
		{
			var excluded = new HashSet<string>(ExpandPatterns(arguments.ExcludeTools, catalog), StringComparer.Ordinal);
			tools = [.. tools.Where(tool => !excluded.Contains(tool.Name))];
		}

		return tools;
	}

	/// 按 <c>+name</c> / <c>-name</c> / 纯名字三态调整工具集。纯名字出现即整体替换
	/// <paramref name="baseTools"/>（<c>--tools</c> 额外支持通配），否则只在基线上增删。
	private static List<AgentTool> ApplyToolEntries(
		IReadOnlyList<AgentTool> baseTools,
		IReadOnlyList<AgentTool> catalog,
		IReadOnlyList<string> entries,
		bool allowPatterns)
	{
		var adjustments = entries.Where(static entry => entry.StartsWith('+') || entry.StartsWith('-')).ToList();
		var replacements = entries.Where(static entry => !entry.StartsWith('+') && !entry.StartsWith('-')).ToList();
		var selected = new List<AgentTool>(baseTools);

		if (replacements.Count > 0)
		{
			var allow = new HashSet<string>(
				allowPatterns ? ExpandPatterns(replacements, catalog) : replacements,
				StringComparer.Ordinal);
			selected = [.. catalog.Where(tool => allow.Contains(tool.Name))];
		}

		foreach (var adjustment in adjustments)
		{
			var name = adjustment[1..];
			if (adjustment[0] == '+')
			{
				if (catalog.FirstOrDefault(tool => tool.Name == name) is { } tool &&
					selected.All(existing => existing.Name != name))
				{
					selected.Add(tool);
				}
			}
			else
			{
				selected.RemoveAll(tool => tool.Name == name);
			}
		}

		return selected;
	}

	/// <param name="preferredModel">
	/// 新会话（没有会话记录）的候选模型：仅当既没有显式 <c>--model</c>、也没有配置
	/// <c>settings.defaultModel</c> 时才会用到（桌面传「上次真正用过的模型」，对齐 PiDeck 的 lastUsedModel）。
	/// </param>
	public CodingAgentSession CreateSession(
		CliArguments arguments,
		string cwd,
		SessionManager? session = null,
		IReadOnlyList<AgentTool>? extraTools = null,
		bool recordInitialThinkingLevel = true,
		IUiHost? uiHost = null,
		Model? preferredModel = null,
		string? extraSystemPrompt = null,
		ILinkedSessionHost? linkedSessionHost = null,
		CodingAgentSession? linkedParent = null,
		string? linkedParentTitle = null,
		ISubagentHost? subagentHost = null,
		int subagentDepth = 0,
		SubagentCatalog? agents = null)
	{
		var manager = session ?? ResolveSession(arguments, cwd, Settings.SessionDir);

		// 新会话的模型：环境解析出的默认之后，才轮到「上次用过的模型」；
		// 显式 --model / settings.defaultModel 都优先于它（与 PiDeck 的 launchDefaults 同序）。
		var initialModel = Model;
		if (manager.Entries.Count == 0
			&& arguments.Model is null
			&& arguments.Provider is null
			&& Settings.DefaultModel is not { Length: > 0 }
			&& preferredModel is { } remembered)
		{
			initialModel = remembered;
		}

		// 环境启动时已解析好的决策；若本次调用显式带 --approve/--no-approve 则以它为准。
		var projectTrusted = arguments.ProjectTrustOverride ?? ProjectTrusted;
		var resources = ResourceResolver.Resolve(cwd, agentDir: null, includeProject: projectTrusted, settings: Settings);
		var explicitSkillRoots = ExplicitRoots(arguments.Skills, cwd);
		var explicitTemplateRoots = ExplicitRoots(arguments.PromptTemplates, cwd);
		var explicitThemeRoots = ExplicitRoots(arguments.Themes, cwd);

		var skills = MergeSkills(
			arguments.NoSkills ? [] : resources.Skills,
			explicitSkillRoots.Count > 0 ? CodingAgent.Skills.Skills.Discover(explicitSkillRoots) : []);
		var templates = MergeTemplates(
			arguments.NoPromptTemplates ? [] : resources.Templates,
			explicitTemplateRoots.Count > 0 ? CodingAgent.Prompts.PromptTemplates.Discover(explicitTemplateRoots) : []);
		var themes = arguments.NoThemes ? [] : resources.Themes;
		if (explicitThemeRoots.Count > 0)
		{
			var diagnostics = new List<string>();
			themes = [.. CodingAgent.Themes.ThemeCatalog.Discover(explicitThemeRoots, diagnostics), .. themes];
		}

		if (explicitSkillRoots.Count > 0 || arguments.NoSkills)
		{
			resources = resources with { Skills = skills };
		}

		if (explicitTemplateRoots.Count > 0 || arguments.NoPromptTemplates)
		{
			resources = resources with { Templates = templates };
		}

		resources = resources with { Themes = themes };

		var customSystemPrompt = arguments.SystemPrompt is { Length: > 0 } systemPrompt
			? CliInput.ResolvePromptInput(systemPrompt, cwd)
			: null;
		var appendSystemPrompt = arguments.AppendSystemPrompt.Count == 0
			? null
			: string.Join(
				"\n\n",
				arguments.AppendSystemPrompt.Select(value => CliInput.ResolvePromptInput(value, cwd)));
		if (extraSystemPrompt is { Length: > 0 } extra)
		{
			appendSystemPrompt = appendSystemPrompt is { Length: > 0 } existing
				? $"{existing}\n\n{extra}"
				: extra;
		}

		var stream = StreamFnOverride ?? StreamFn;
		// Resolve models.json credentials from the requested provider for every call, including
		// compaction and branch summaries. A startup key must never leak into another provider.
		StreamFn sessionStream = (model, messages, options) => stream(model, messages, options with
		{
			ApiKey = arguments.ApiKey ?? ProviderApiKeys.GetValueOrDefault(model.Provider),
		});

		return new CodingAgentSession(new CodingAgentSessionOptions
		{
			Cwd = cwd,
			Model = initialModel,
			// 没显式指定模型时，恢复会话要回到会话记录里的模型（pi 的 findInitialModel 语义）。
			RestoreRecordedModel = arguments.Model is null && arguments.Provider is null,
			StreamFn = sessionStream,
			Registry = Registry,
			ApiKeyForProvider = provider => arguments.ApiKey ?? ProviderApiKeys.GetValueOrDefault(provider),
			Images = Images,
			Classifiers = Classifiers,
			// null 表示「沿用会话里记录的级别」，恢复会话时因此能记住上次的选择；
			// 每模型默认值与全局默认值由会话层解析。
			ThinkingLevel = arguments.ThinkingLevel,
			// 工具组装由环境负责：内置工具（按参数过滤）加上 MCP 工具。
			Tools = ComposeTools(arguments, extraTools),
			Session = manager,
			Persist = !arguments.NoSession,
			RecordInitialThinkingLevel = recordInitialThinkingLevel,
			Settings = Settings,
			Options = new StreamOptions
			{
				// Only an explicit CLI override is provider-independent. Stored keys resolve per call above.
				ApiKey = arguments.ApiKey,
			},
			Skills = explicitSkillRoots.Count > 0 || arguments.NoSkills ? skills : null,
			PromptTemplates = explicitTemplateRoots.Count > 0 || arguments.NoPromptTemplates ? templates : null,
			Resources = resources,
			CustomSystemPrompt = customSystemPrompt,
			AppendSystemPrompt = appendSystemPrompt,
			LinkedSessionHost = linkedSessionHost,
			LinkedParent = linkedParent,
			LinkedParentTitle = linkedParentTitle,
			SubagentHost = subagentHost,
			SubagentDepth = subagentDepth,
			Agents = agents,
			NoContextFiles = arguments.NoContextFiles,
			// 显式路径来自 settings.extensions 与 -e；-ne 只关发现，显式路径仍然加载（与 pi 一致）。
			ExtensionPaths = [.. Settings.Extensions, .. arguments.Extensions],
			DiscoverExtensions = !arguments.NoExtensions,
			FlagValues = arguments.UnknownFlags,
			UiHost = uiHost,
			ProjectTrusted = projectTrusted,
		});
	}

	/// <summary>
	/// 建一个子代理会话：角色工具白名单、模型与思考级别、角色提示词加委派边界。
	/// 子会话不加载扩展、不带关联会话工具，避免递归开启子会话。
	/// </summary>
	public CodingAgentSession CreateChildSession(
		CliArguments arguments,
		string cwd,
		SessionManager session,
		SubagentStartContext context,
		SubagentCatalog catalog,
		ISubagentHost? subagentHost,
		IReadOnlyList<AgentTool>? extraTools,
		IUiHost? uiHost)
	{
		ArgumentNullException.ThrowIfNull(arguments);
		ArgumentNullException.ThrowIfNull(session);
		ArgumentNullException.ThrowIfNull(context);

		var definition = context.Agent;
		var model = ResolveChildModel(definition) ?? context.Parent.Model;
		var (tools, rejectedTools) = ChildTools(
			arguments,
			cwd,
			definition,
			extraTools,
			context.Parent.Tools,
			context.Parent.PlanModeEnabled);
		var boundary = SubagentHub.BuildBoundary(context);
		var appendSystemPrompt = definition.SystemPrompt.Length > 0
			? string.Concat(definition.SystemPrompt, "\n\n", boundary)
			: boundary;
		if (rejectedTools.Count > 0)
		{
			// 角色文件写了父会话没授予的工具：告诉子模型别调用，避免它按白名单猜。
			appendSystemPrompt = string.Concat(
				appendSystemPrompt,
				"\n\n工具限制：父会话没有授予 ",
				string.Join(", ", rejectedTools),
				"，不要调用它们。");
		}

		var stream = StreamFnOverride ?? StreamFn;
		StreamFn sessionStream = (requested, messages, options) => stream(requested, messages, options with
		{
			ApiKey = arguments.ApiKey ?? ProviderApiKeys.GetValueOrDefault(requested.Provider),
		});

		var projectTrusted = arguments.ProjectTrustOverride ?? ProjectTrusted;
		return new CodingAgentSession(new CodingAgentSessionOptions
		{
			Cwd = cwd,
			Model = model,
			RestoreRecordedModel = false,
			StreamFn = sessionStream,
			Registry = Registry,
			ApiKeyForProvider = provider => arguments.ApiKey ?? ProviderApiKeys.GetValueOrDefault(provider),
			Images = Images,
			Classifiers = Classifiers,
			// 角色没写级别时沿用父会话当前级别，而不是回到全局默认。
			ThinkingLevel = definition.Thinking ?? context.Parent.ThinkingLevel,
			Tools = tools,
			Session = session,
			Persist = true,
			RecordInitialThinkingLevel = false,
			InitialPlanMode = context.PlanMode || context.Parent.PlanModeEnabled,
			Settings = Settings,
			Options = new StreamOptions { ApiKey = arguments.ApiKey },
			// 子会话与父会话同 cwd：资源快照直接复用，免去每个子代理重扫技能与模板目录。
			Resources = context.Parent.Resources,
			AppendSystemPrompt = appendSystemPrompt,
			NoContextFiles = arguments.NoContextFiles,
			// 子代理不加载扩展：工具面保持角色白名单，也避免扩展在子会话里注册额外工具。
			LoadExtensions = false,
			DiscoverExtensions = false,
			SubagentHost = subagentHost,
			SubagentDepth = context.Depth,
			MaxSubagentDepth = 1,
			SubagentParent = context.Parent,
			Agents = catalog,
			UiHost = uiHost,
			ProjectTrusted = projectTrusted,
		});
	}

	private Model? ResolveChildModel(SubagentDefinition definition)
	{
		var spec = definition.Model?.Trim();
		return string.IsNullOrEmpty(spec) ? null : Registry.Resolve(spec);
	}

	private (IReadOnlyList<AgentTool> Tools, IReadOnlyList<string> Rejected) ChildTools(
		CliArguments arguments,
		string cwd,
		SubagentDefinition definition,
		IReadOnlyList<AgentTool>? extraTools,
		IReadOnlyList<AgentTool> inherited,
		bool planMode)
	{
		if (arguments.NoTools)
		{
			return ([], []);
		}

		var catalog = BuiltinTools.Catalog(cwd);
		var byName = new Dictionary<string, AgentTool>(catalog.Count, StringComparer.Ordinal);
		foreach (var builtin in catalog)
		{
			byName[builtin.Tool.Name] = builtin.Tool;
		}

		// MCP 工具也能进角色白名单；父会话的 mcp__ 过滤先生效。
		var extras = FilterExtraTools(arguments, extraTools ?? []);
		foreach (var tool in extras)
		{
			byName[tool.Name] = tool;
		}

		var inheritedNames = new HashSet<string>(inherited.Count, StringComparer.Ordinal);
		foreach (var tool in inherited)
		{
			if (!IsChildForbidden(tool.Name))
			{
				inheritedNames.Add(tool.Name);
			}
		}

		var rejected = new List<string>();
		var selected = new List<AgentTool>(definition.Tools?.Count ?? inherited.Count);
		if (definition.Tools is { Count: > 0 } allowed)
		{
			// 父会话用 --tools 给了纯名字列表时，角色白名单只能从这份列表里取；
			// --tools +grep 这类增删不改变可追加的只读发现工具。
			var hasExplicitAllow = arguments.Tools.Any(static entry =>
				entry.Length > 0 && !entry.StartsWith('+') && !entry.StartsWith('-') && !IsMcpPattern(entry));

			foreach (var rawName in allowed)
			{
				var name = rawName.Trim();
				if (name.Length == 0)
				{
					continue;
				}

				// 计划模式连角色的显式白名单也不能解锁 edit/write。
				if (IsChildForbidden(name) || (planMode && PlanMode.DisabledTools.Contains(name, StringComparer.Ordinal)))
				{
					Reject(name);
					continue;
				}

				if (arguments.ExcludeTools.Any(pattern => MatchesName(pattern, name)) ||
					arguments.Tools.Any(entry => entry.Length > 1 && entry[0] == '-' && MatchesName(entry[1..], name)))
				{
					Reject(name);
					continue;
				}

				var explicitlyAllowed = arguments.Tools.Any(entry =>
					entry.Length > 0 && !entry.StartsWith('+') && !entry.StartsWith('-') &&
					!IsMcpPattern(entry) && MatchesName(entry, name));
				if (hasExplicitAllow && !explicitlyAllowed)
				{
					Reject(name);
					continue;
				}

				// 白名单只做收窄，唯一例外是父会话默认没开、又完全只读的发现工具。
				// 内置 scout/reviewer/oracle 依赖这一点；写工具和 shell 不能这样加回来。
				var readOnlyAddition = name is "read" or "grep" or "find" or "ls";
				if (!inheritedNames.Contains(name) &&
					!(readOnlyAddition && !hasExplicitAllow && !arguments.NoBuiltinTools))
				{
					Reject(name);
					continue;
				}

				if (byName.TryGetValue(name, out var tool))
				{
					selected.Add(tool);
				}
				else
				{
					Reject(name);
				}
			}
		}
		else
		{
			// 继承父会话「当前可用」的工具名，而不是环境里的完整目录：--tools 与计划模式的
			// 过滤对子代理同样生效；工具实例仍按子会话重新绑定（PI_SESSION_ID 等不能串）。
			foreach (var builtin in catalog)
			{
				if (inheritedNames.Contains(builtin.Tool.Name))
				{
					selected.Add(builtin.Tool);
				}
			}

			foreach (var tool in extras)
			{
				if (inheritedNames.Contains(tool.Name) &&
					selected.All(existing => !string.Equals(existing.Name, tool.Name, StringComparison.Ordinal)))
				{
					selected.Add(tool);
				}
			}
		}

		foreach (var name in definition.ExcludeTools)
		{
			selected.RemoveAll(tool => string.Equals(tool.Name, name, StringComparison.Ordinal));
		}

		return (selected, rejected);

		void Reject(string name)
		{
			if (!rejected.Contains(name))
			{
				rejected.Add(name);
			}
		}
	}

	/// <summary>委派、关联会话与 codemode 不进子会话：递归与工具面都收在第一层。</summary>
	private static readonly FrozenSet<string> ChildForbiddenTools = FrozenSet.ToFrozenSet(
	[
		SubagentTools.DelegateName,
		SubagentTools.WaitName,
		SubagentTools.ListName,
		SubagentTools.ReadName,
		SubagentTools.SendName,
		SubagentTools.StopName,
		LinkedSessionsTool.StartName,
		LinkedSessionsTool.SendName,
		LinkedSessionsTool.ListName,
		CodeModeTool.Name,
	], StringComparer.Ordinal);

	private static bool IsChildForbidden(string name) => ChildForbiddenTools.Contains(name);

	private static IReadOnlyList<CodingAgent.Resources.ResourceRoot> ExplicitRoots(IReadOnlyList<string> paths, string cwd)
	{
		var roots = new List<CodingAgent.Resources.ResourceRoot>(paths.Count);
		foreach (var raw in paths)
		{
			var expanded = CodingAgent.Session.UserPath.ExpandTilde(raw.Trim());
			if (expanded.Length == 0) continue;
			var path = Path.IsPathRooted(expanded) ? expanded : Path.Combine(cwd, expanded);
			try
			{
				roots.Add(new CodingAgent.Resources.ResourceRoot(
					Path.GetFullPath(path),
					new CodingAgent.Resources.ResourceOrigin(CodingAgent.Resources.ResourceScope.User, cwd, 0)));
			}
			catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
			{
				// 无效路径跳过，单个坏参数不阻止启动。
			}
		}

		return roots;
	}

	private static IReadOnlyList<CodingAgent.Skills.Skill> MergeSkills(
		IReadOnlyList<CodingAgent.Skills.Skill> baseSkills,
		IReadOnlyList<CodingAgent.Skills.Skill> explicitSkills)
	{
		var merged = new List<CodingAgent.Skills.Skill>(explicitSkills.Count + baseSkills.Count);
		var names = new HashSet<string>(StringComparer.Ordinal);
		foreach (var skill in explicitSkills.Concat(baseSkills))
		{
			if (names.Add(skill.Name)) merged.Add(skill);
		}

		return merged;
	}

	private static IReadOnlyList<CodingAgent.Prompts.PromptTemplate> MergeTemplates(
		IReadOnlyList<CodingAgent.Prompts.PromptTemplate> baseTemplates,
		IReadOnlyList<CodingAgent.Prompts.PromptTemplate> explicitTemplates)
	{
		var merged = new List<CodingAgent.Prompts.PromptTemplate>(explicitTemplates.Count + baseTemplates.Count);
		var names = new HashSet<string>(StringComparer.Ordinal);
		foreach (var template in explicitTemplates.Concat(baseTemplates))
		{
			if (names.Add(template.Name)) merged.Add(template);
		}

		return merged;
	}

	/// 内置工具（按参数过滤）在前，MCP 工具在后；新建会话与服务器报告新工具后的
	/// 刷新都会用到。
	public IReadOnlyList<AgentTool> ComposeTools(CliArguments arguments, IReadOnlyList<AgentTool>? extraTools) =>
		arguments.NoTools ? [] : [.. Tools, .. FilterExtraTools(arguments, extraTools ?? [])];

	/// 应用 <c>mcp__</c> 工具模式。不以 <c>mcp__</c> 开头的条目永远不会移除 MCP 工具，
	/// 这是 pi 的规则。
	internal static IReadOnlyList<AgentTool> FilterExtraTools(CliArguments arguments, IReadOnlyList<AgentTool> tools)
	{
		var allow = arguments.Tools.Where(static pattern => IsMcpPattern(pattern) && !pattern.StartsWith('-')).ToList();
		var deny = arguments.ExcludeTools.Where(static pattern => IsMcpPattern(pattern))
			.Concat(arguments.Tools.Where(static pattern => pattern.StartsWith("-mcp__", StringComparison.Ordinal)).Select(static pattern => pattern[1..]))
			.ToList();

		var filtered = tools.AsEnumerable();
		if (allow.Count > 0)
		{
			filtered = filtered.Where(tool => allow.Any(pattern => MatchesName(pattern, tool.Name)));
		}

		if (deny.Count > 0)
		{
			filtered = filtered.Where(tool => !deny.Any(pattern => MatchesName(pattern, tool.Name)));
		}

		return [.. filtered];
	}

	private static bool IsMcpPattern(string pattern)
	{
		var bare = pattern.StartsWith('-') ? pattern[1..] : pattern;
		return bare.StartsWith("mcp__", StringComparison.Ordinal) || McpResourceTools.IsResourceTool(bare);
	}

	private static bool MatchesName(string pattern, string name)
	{
		if (pattern == name)
		{
			return true;
		}

		return pattern.IndexOfAny(['*', '?']) >= 0 && SearchTools.GlobToRegex(pattern).IsMatch(name);
	}

	/// 打开或创建参数所要求的会话文件。<paramref name="defaultSessionDir"/> 来自
	/// <c>settings.sessionDir</c>，只有在没有 <c>--session-dir</c> 时才生效。
	public static SessionManager ResolveSession(CliArguments arguments, string cwd, string? defaultSessionDir = null)
	{
		if (arguments.NoSession)
		{
			return SessionManager.InMemory(cwd, arguments.SessionId);
		}

		var sessionDir = arguments.SessionDir
			?? (defaultSessionDir is { Length: > 0 } configured ? PiWeft.CodingAgent.Session.UserPath.Resolve(configured) : null);

		if (arguments.Fork is { Length: > 0 } forkSource)
		{
			var source = ResolveSessionPath(forkSource, cwd, sessionDir)
				?? throw new InvalidOperationException($"Session to fork not found: {forkSource}");

			return SessionManager.ForkFrom(source, cwd, sessionDir);
		}

		if (arguments.Session is { Length: > 0 } sessionPath)
		{
			var resolved = ResolveSessionPath(sessionPath, cwd, sessionDir);
			return resolved is not null
				? SessionManager.Open(resolved, sessionDir)
				: throw new InvalidOperationException($"Session not found: {sessionPath}");
		}

		if (arguments.Continue || arguments.Resume)
		{
			return SessionManager.ContinueRecent(cwd, sessionDir);
		}

		// 与 pi 一致：--session-id 指向的会话已存在就继续用它，不每次造新文件；
		// 否则同一条命令跑两次会得到两个同 id 会话，之后 --session 的命中就不确定了。
		if (arguments.SessionId is { Length: > 0 } sessionId &&
			FindSessionById(sessionId, cwd, sessionDir) is { } existing)
		{
			return SessionManager.Open(existing, sessionDir);
		}

		return SessionManager.Create(cwd, sessionDir, arguments.SessionId);
	}

	private static string? FindSessionById(string id, string cwd, string? sessionDir)
	{
		var directory = sessionDir is { Length: > 0 } configured
			? PiWeft.CodingAgent.Session.UserPath.Resolve(configured)
			: SessionPaths.GetDefaultSessionDir(cwd);
		if (!Directory.Exists(directory))
		{
			return null;
		}

		return Directory
			.EnumerateFiles(directory, "*.jsonl")
			.Where(path => Path.GetFileName(path).Contains(id, StringComparison.OrdinalIgnoreCase))
			.OrderBy(static path => path, StringComparer.Ordinal)
			.LastOrDefault();
	}

	public static string? ResolveSessionPath(string pathOrId, string cwd, string? sessionDir)
	{
		if (File.Exists(pathOrId))
		{
			return Path.GetFullPath(pathOrId);
		}

		// 命令行给的目录同样展开 ~（与 pi 的 normalizePath 一致）。
		var directory = sessionDir is { Length: > 0 } configured
			? PiWeft.CodingAgent.Session.UserPath.Resolve(configured)
			: SessionPaths.GetDefaultSessionDir(cwd);
		if (!Directory.Exists(directory))
		{
			return null;
		}

		// 短 id（如 "12"）可能命中文件名里的时间戳等任意子串；枚举顺序又不保证，
		// 所以按文件名排序后取最新的一个，保证同样的输入总是解析到同一个会话。
		return Directory
			.EnumerateFiles(directory, "*.jsonl")
			.Where(path => Path.GetFileName(path).Contains(pathOrId, StringComparison.OrdinalIgnoreCase))
			.OrderBy(static path => path, StringComparer.Ordinal)
			.LastOrDefault();
	}

	private static IEnumerable<string> ExpandPatterns(IReadOnlyList<string> patterns, IReadOnlyList<AgentTool> tools)
	{
		foreach (var pattern in patterns)
		{
			if (pattern.IndexOfAny(['*', '?']) < 0)
			{
				yield return pattern;
				continue;
			}

			var regex = SearchTools.GlobToRegex(pattern);
			foreach (var tool in tools.Where(tool => regex.IsMatch(tool.Name)))
			{
				yield return tool.Name;
			}
		}
	}
}
