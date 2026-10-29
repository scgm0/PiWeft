using PiWeft.Agent;
using PiWeft.Ai;
using PiWeft.Ai.Providers;
using PiWeft.Ai.Providers.Anthropic;
using PiWeft.Ai.Providers.Bedrock;
using PiWeft.Ai.Providers.Google;
using PiWeft.Ai.Providers.Mistral;
using PiWeft.Ai.Providers.PiMessages;
using PiWeft.Ai.Providers.OpenAi;
using PiWeft.CodingAgent;
using PiWeft.CodingAgent.Models;
using PiWeft.CodingAgent.Resources;
using PiWeft.Mcp;
using PiWeft.CodingAgent.Session;
using PiWeft.CodingAgent.Settings;
using PiWeft.CodingAgent.Tools;

namespace PiWeft.Cli;

/// <summary>一次 CLI 运行所需的全部内容：模型目录、已注册的 provider 与解析后的设置。</summary>
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

	/// <summary>
	/// 项目是否受信任。为 false 时不合并项目设置、不连接项目 MCP，也不加载项目技能与提示词模板。
	/// 一次启动只解析一次，会话构造与 MCP 连接共用同一个决策。
	/// </summary>
	public bool ProjectTrusted { get; } = true;

	/// <summary><c>models.json</c> 中声明的 API key，按 provider id 索引。</summary>
	public IReadOnlyDictionary<string, string> ProviderApiKeys { get; }

	/// <summary>替换流式函数。测试以及自行掌控传输的嵌入方会用到。</summary>
	internal StreamFn? StreamFnOverride { get; init; }

	/// <summary>围绕调用方提供的流式函数构建环境。</summary>
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
		return new CliEnvironment(registry, gateway, images, classifiers, settings, model, tools, new Dictionary<string, string>(), projectTrusted: true)
		{
			StreamFnOverride = streamFn,
		};
	}

	public ModelRegistry Registry { get; }

	public ModelsGateway Gateway { get; }

	/// <summary>生图模型网关；目前注册 OpenRouter 方言。</summary>
	public ImagesGateway Images { get; }

	/// <summary>分类模型网关；目前注册 OpenAI Decisions 方言。</summary>
	public ClassifiersGateway Classifiers { get; }

	public CodingAgentSettings Settings { get; }

	public Model Model { get; }

	public IReadOnlyList<AgentTool> Tools { get; }

	private StreamFn? _streamFn;

	public StreamFn StreamFn => _streamFn ??= AgentGatewayStreamFn.For(Gateway);

	/// <summary>连接测试用的流式函数：测试注入优先，否则走真实网关。</summary>
	internal StreamFn ProbeStreamFn => StreamFnOverride ?? StreamFn;

	/// <summary>本端口实现的 provider API。</summary>
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

	/// <summary>构建环境：设置、目录、provider、模型与工具集。</summary>
	public static (CliEnvironment? Environment, string? Error) Create(CliArguments arguments, string cwd) =>
		Create(arguments, cwd, handler: null);

	/// <summary>构建环境，可让 provider 的 HTTP 请求走 <paramref name="handler"/>。</summary>
	internal static (CliEnvironment? Environment, string? Error) Create(
		CliArguments arguments,
		string cwd,
		HttpMessageHandler? handler)
	{
		// 信任决策只看**全局** settings.defaultProjectTrust（项目设置不能自我授权）。
		// 不受信时不合并项目设置，避免一个未受信项目通过 .pi/settings.json 影响行为。
		var defaultTrust = CodingAgentSettings.GlobalDefaultProjectTrust();
		var projectTrusted = ProjectTrust.Resolve(cwd, arguments.ProjectTrustOverride, defaultTrust);
		var settings = CodingAgentSettings.Load(cwd, agentDir: null, includeProject: projectTrusted);
		var registry = ModelRegistry.LoadDefault();

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

		var model = ResolveModel(registry, arguments, settings, out var modelError, customModels.ApiKeys);
		if (model is null)
		{
			return (null, modelError ?? "No usable model found.");
		}

		var tools = ResolveTools(arguments, cwd, settings);
		return (new CliEnvironment(registry, gateway, images, classifiers, settings, model, tools, customModels.ApiKeys, projectTrusted), null);
	}

	private static readonly Dictionary<string, string> DefaultModelPerProvider = new(StringComparer.Ordinal)
	{
		["amazon-bedrock"] = "us.anthropic.claude-opus-4-6-v1",
		["ant-ling"] = "Ring-2.6-1T",
		["anthropic"] = "claude-opus-4-8",
		["openai"] = "gpt-5.5",
		["azure"] = "gpt-5.4",
		["openai-codex"] = "gpt-6.1-sol",
		["radius"] = "balanced",
		["nvidia"] = "nvidia/nemotron-3.5-super-vl-120b-a12b",
		["deepseek"] = "deepseek-v4-pro",
		["google"] = "gemini-3.1-pro-preview",
		["google-vertex"] = "gemini-3.1-pro-preview",
		["github-copilot"] = "gpt-5.4",
		["openrouter"] = "moonshotai/kimi-k2.6",
		["vercel-ai-gateway"] = "zai/glm-5.1",
		["xai"] = "grok-4.7",
		["groq"] = "openai/gpt-oss-120b",
		["cerebras"] = "gpt-oss-120b",
		["zai"] = "glm-5.3",
		["zai-coding-cn"] = "glm-5.3",
		["mistral"] = "devstral-medium-latest",
		["minimax"] = "MiniMax-M2.7",
		["minimax-cn"] = "MiniMax-M2.7",
		["moonshotai"] = "kimi-k2.6",
		["moonshotai-cn"] = "kimi-k2.6",
		["huggingface"] = "moonshotai/Kimi-K2.6",
		["fireworks"] = "accounts/fireworks/models/kimi-k3",
		["together"] = "moonshotai/Kimi-K3",
		["baseten"] = "zai-org/GLM-5.2",
		["opencode"] = "kimi-k2.6",
		["opencode-go"] = "kimi-k3",
		["kimi-coding"] = "kimi-for-coding",
		["meta"] = "muse-spark-1.3",
		["cloudflare-workers-ai"] = "@cf/moonshotai/kimi-k2.6",
		["cloudflare-ai-gateway"] = "workers-ai/@cf/moonshotai/kimi-k2.6",
		["qwen-token-plan"] = "qwen3.7-max",
		["qwen-token-plan-cn"] = "qwen3.7-max",
		["qwen-token-plan-individual"] = "qwen3.8-max",
		["xiaomi"] = "mimo-v2.6-pro",
		["xiaomi-token-plan-cn"] = "mimo-v2.6-pro",
		["xiaomi-token-plan-ams"] = "mimo-v2.6-pro",
		["xiaomi-token-plan-sgp"] = "mimo-v2.6-pro",
	};

	/// <summary>依次从 <c>--model</c>、设置里的默认模型、第一个有密钥的 provider 解析模型。</summary>
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

			if (!SupportedApis.Contains(match.Api, StringComparer.Ordinal))
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

		// 完全没有密钥时，仍然以第一个受支持的对话模型启动，让桌面客户端能访问到 RPC 面并展示模型选择；
		// 之后发送提示会以 provider 自己的“缺少密钥”错误失败，这与 pi 在 RPC 模式下的体验一致。
		// 
		foreach (var candidate in registry.ChatModels())
		{
			if (SupportedApis.Contains(candidate.Api, StringComparer.Ordinal))
			{
				return candidate;
			}
		}

		error = "No model configured and no provider API key found. Pass --model, or set one in ~/.pi/agent/settings.json.";
		return null;
	}

	/// <summary>为目录里还没有的新模型构造 fallback 条目，使用该 Provider 首个模型的端点与能力。</summary>
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

	/// <summary>
	/// 按 <c>provider/id</c>、精确 id、glob 模式或子串查找模型。与 pi 1.3.0 一致：
	/// 同名 id 出现在多个 provider 时优先选择**有可用凭据**的那个，仍然无法区分时返回 null
	/// 由调用方报「模型未找到/有歧义」。
	/// </summary>
	public static Model? FindModel(
		ModelRegistry registry,
		string pattern,
		string? provider = null,
		IReadOnlyDictionary<string, string>? providerApiKeys = null,
		string? explicitApiKey = null)
	{
		if (pattern.Length == 0)
		{
			// 空模式（例如 `--model=`）会让后面的子串匹配 `Contains("")` 恒真，
			// 静默选中目录里的第一个模型；这里显式返回未命中，交给调用方报错。
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

		// 裸 id 命中多个 provider：优先有凭据者；不知道凭据情况（未传 key 信息）或仍然并列时
		// 维持「有歧义」的返回，让调用方报错而不是猜一个。
		var byId = registry.FindById(pattern)
			.Where(m => SupportedApis.Contains(m.Api, StringComparer.Ordinal))
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

	/// <summary>知道凭据情况时，把候选收窄到本地有可用凭据的 provider；不知道时原样返回。</summary>
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

	/// <summary>某个模型是否命中所给的 <c>--models</c> 模式：支持 glob 与子串匹配。</summary>
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

	/// <summary>列出本端口已实现 api 的对话模型，可按搜索串过滤。</summary>
	public IReadOnlyList<Model> ListModels(string? search = null) => ListModels(Registry, search);

	/// <summary>会话可选择的模型：受支持且有可用的本地认证；目录查询仍保留全量语义。</summary>
	public IReadOnlyList<Model> ListAvailableModels(string? explicitKey = null) =>
		ModelAvailability.Filter(ListModels(), ProviderApiKeys, explicitKey);

	/// <summary>列出目录模型，以及 <c>models.json</c> 中的自定义端点。</summary>
	public static IReadOnlyList<Model> ListModels(string cwd, string? search) =>
		ListModels(BuildRegistry(cwd), search);

	/// <summary>把 OAuth 凭据携带的端点（Copilot proxy、自定义 Radius gateway）应用到该 Provider 的全部模型。</summary>
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

	/// <summary>加载目录并叠加 <c>models.json</c>。</summary>
	public static ModelRegistry BuildRegistry(string cwd)
	{
		var registry = ModelRegistry.LoadDefault();
		var userModels = ModelsFileLoader.Read(ModelsFileLoader.UserPath());
		var projectModels = ModelsFileLoader.Read(ModelsFileLoader.ProjectPath(cwd));
		ModelsFileLoader.ApplyProviderSettings(registry, userModels, projectModels);
		ModelCatalogStore.LoadCached(registry);

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
		IEnumerable<Model> models = registry.ChatModels().Where(m => SupportedApis.Contains(m.Api, StringComparer.Ordinal));

		if (search is { Length: > 0 })
		{
			models = models.Where(m =>
				m.Id.Contains(search, StringComparison.OrdinalIgnoreCase) ||
				m.Provider.Contains(search, StringComparison.OrdinalIgnoreCase) ||
				$"{m.Provider}/{m.Id}".Contains(search, StringComparison.OrdinalIgnoreCase));
		}

		return [.. models.OrderBy(static m => m.Provider, StringComparer.Ordinal).ThenBy(static m => m.Id, StringComparer.Ordinal)];
	}

	/// <summary>应用工具相关参数：默认集、白名单、黑名单。</summary>
	public static IReadOnlyList<AgentTool> ResolveTools(CliArguments arguments, string cwd) =>
		ResolveTools(arguments, cwd, new CodingAgentSettings());

	/// <summary>
	/// 与 pi 相同的三态语义：默认集来自 <c>settings.defaultTools</c>（未配置时用
	/// <see cref="CodingAgentSettings.DefaultToolNames"/>）；<c>--tools</c> 的纯名字列表整体替换默认集，
	/// 只含 <c>+</c>/<c>-</c> 时在默认集上增删；<c>--exclude-tools</c> 最后应用并支持通配。
	/// 模式永远针对**完整**内置目录展开，因此默认关闭的 <c>grep</c>/<c>find</c>/<c>ls</c> 仍可显式选中。
	/// </summary>
	public static IReadOnlyList<AgentTool> ResolveTools(CliArguments arguments, string cwd, CodingAgentSettings settings)
	{
		if (arguments.NoTools)
		{
			return [];
		}

		// 目录含默认不启用的 codemode，这样 --tools/--exclude-tools/defaultTools 才能选中它。
		var catalog = arguments.NoBuiltinTools ? [] : BuiltinTools.CatalogTools(cwd);
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

	/// <summary>
	/// 按 <c>+name</c> / <c>-name</c> / 纯名字三态调整工具集。纯名字出现即整体替换
	/// <paramref name="baseTools"/>（<c>--tools</c> 额外支持通配），否则只在基线上增删。
	/// </summary>
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

	/// <summary>按会话相关参数解析出要使用的会话。</summary>
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
		Model? preferredModel = null)
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
		var resources = ResourceResolver.Resolve(cwd, agentDir: null, includeProject: projectTrusted);
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

		// --system-prompt / --append-system-prompt 既接受字面文本，也接受文件路径（与 pi 一致）。
		var customSystemPrompt = arguments.SystemPrompt is { Length: > 0 } systemPrompt
			? CliInput.ResolvePromptInput(systemPrompt, cwd)
			: null;
		var appendSystemPrompt = arguments.AppendSystemPrompt.Count == 0
			? null
			: string.Join(
				"\n\n",
				arguments.AppendSystemPrompt.Select(value => CliInput.ResolvePromptInput(value, cwd)));

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
			NoContextFiles = arguments.NoContextFiles,
			UiHost = uiHost,
			ProjectTrusted = projectTrusted,
		});
	}

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

	/// <summary>内置工具（按参数过滤）在前，MCP 工具在后；新建会话与服务器报告新工具后的
	/// 刷新都会用到。</summary>
	public IReadOnlyList<AgentTool> ComposeTools(CliArguments arguments, IReadOnlyList<AgentTool>? extraTools) =>
		arguments.NoTools ? [] : [.. Tools, .. FilterExtraTools(arguments, extraTools ?? [])];

	/// <summary>
	/// 应用 <c>mcp__</c> 工具模式。不以 <c>mcp__</c> 开头的条目永远不会移除 MCP 工具，
	/// 这是 pi 的规则。
	/// </summary>
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

	/// <summary>某个工具模式是否指向 MCP 工具（包含资源工具）。</summary>
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

	/// <summary>
	/// 打开或创建参数所要求的会话文件。<paramref name="defaultSessionDir"/> 来自
	/// <c>settings.sessionDir</c>，只有在没有 <c>--session-dir</c> 时才生效。
	/// </summary>
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

		// 与 pi 一致：--session-id 指向的会话已存在就继续用它，而不是每次都造新文件 ——
		// 否则同一条命令跑两次会得到两个同 id 会话，之后 --session 的命中就不确定了。
		if (arguments.SessionId is { Length: > 0 } sessionId &&
			FindSessionById(sessionId, cwd, sessionDir) is { } existing)
		{
			return SessionManager.Open(existing, sessionDir);
		}

		return SessionManager.Create(cwd, sessionDir, arguments.SessionId);
	}

	/// <summary>按会话 id 在会话目录里找最新的匹配文件（文件名包含 id）。</summary>
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

	/// <summary>在会话目录中解析会话文件路径或部分 UUID。</summary>
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
