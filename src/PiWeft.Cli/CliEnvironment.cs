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
		CodingAgentSettings settings,
		Model model,
		IReadOnlyList<AgentTool> tools,
		IReadOnlyDictionary<string, string> providerApiKeys,
		bool projectTrusted)
	{
		Registry = registry;
		Gateway = gateway;
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
		return new CliEnvironment(registry, gateway, settings, model, tools, new Dictionary<string, string>(), projectTrusted: true)
		{
			StreamFnOverride = streamFn,
		};
	}

	public ModelRegistry Registry { get; }

	public ModelsGateway Gateway { get; }

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
		var customModels = ModelsFileLoader.Load(cwd);
		foreach (var custom in customModels.Models)
		{
			registry.Add(custom);
		}

		if (customModels.OverrideCount > 0)
		{
			ModelsFileLoader.ApplyOverrides(
				registry,
				ModelsFileLoader.Read(ModelsFileLoader.UserPath()),
				ModelsFileLoader.Read(ModelsFileLoader.ProjectPath(cwd)));
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

		var model = ResolveModel(registry, arguments, settings, out var modelError, customModels.ApiKeys);
		if (model is null)
		{
			return (null, modelError ?? "No usable model found.");
		}

		var tools = ResolveTools(arguments, cwd);
		return (new CliEnvironment(registry, gateway, settings, model, tools, customModels.ApiKeys, projectTrusted), null);
	}

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
			var match = FindModel(registry, requested, provider);
			if (match is null)
			{
				error = $"Model not found: {requested}";
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
		if (available.Count > 0) return available[0];

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

	/// <summary>按 <c>provider/id</c>、精确 id、glob 模式或子串查找模型。</summary>
	public static Model? FindModel(ModelRegistry registry, string pattern, string? provider = null)
	{
		if (provider is not null && registry.Get(provider, pattern) is { } direct)
		{
			return direct;
		}

		if (registry.Resolve(pattern) is { } byKey)
		{
			return byKey;
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
				return matches[0];
			}
		}

		var substring = qualified
			.Where(entry =>
				entry.Key.Contains(pattern, StringComparison.OrdinalIgnoreCase) ||
				entry.Model.Id.Contains(pattern, StringComparison.OrdinalIgnoreCase))
			.Select(static entry => entry.Model)
			.ToList();

		return substring.Count > 0 ? substring[0] : null;
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

	/// <summary>加载目录并叠加 <c>models.json</c>。</summary>
	public static ModelRegistry BuildRegistry(string cwd)
	{
		var registry = ModelRegistry.LoadDefault();

		foreach (var custom in ModelsFileLoader.Load(cwd).Models)
		{
			registry.Add(custom);
		}

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

	/// <summary>应用工具相关参数：内置集、白名单、黑名单。</summary>
	public static IReadOnlyList<AgentTool> ResolveTools(CliArguments arguments, string cwd)
	{
		if (arguments.NoTools)
		{
			return [];
		}

		var tools = arguments.NoBuiltinTools
			? new List<AgentTool>()
			: [.. BuiltinTools.Tools(cwd)];

		if (arguments.Tools.Count > 0)
		{
			var adjustments = arguments.Tools.Where(static t => t.StartsWith('+') || t.StartsWith('-')).ToList();
			var replacements = arguments.Tools.Where(static t => !t.StartsWith('+') && !t.StartsWith('-')).ToList();

			if (replacements.Count > 0)
			{
				var allow = new HashSet<string>(ExpandPatterns(replacements, tools), StringComparer.Ordinal);
				tools = [.. tools.Where(tool => allow.Contains(tool.Name))];
			}

			foreach (var adjustment in adjustments)
			{
				var name = adjustment[1..];
				tools = adjustment[0] == '+'
					? [.. tools, .. BuiltinTools.Tools(cwd).Where(tool => tool.Name == name && tools.All(existing => existing.Name != name))]
					: [.. tools.Where(tool => tool.Name != name)];
			}
		}

		if (arguments.ExcludeTools.Count > 0)
		{
			var excluded = new HashSet<string>(ExpandPatterns(arguments.ExcludeTools, tools), StringComparer.Ordinal);
			tools = [.. tools.Where(tool => !excluded.Contains(tool.Name))];
		}

		return tools;
	}

	/// <summary>按会话相关参数解析出要使用的会话。</summary>
	public CodingAgentSession CreateSession(
		CliArguments arguments,
		string cwd,
		SessionManager? session = null,
		IReadOnlyList<AgentTool>? extraTools = null,
		bool recordInitialThinkingLevel = true,
		IUiHost? uiHost = null)
	{
		var manager = session ?? ResolveSession(arguments, cwd);

		IReadOnlyList<CodingAgent.Skills.Skill>? skills = arguments.NoSkills ? [] : null;
		IReadOnlyList<CodingAgent.Prompts.PromptTemplate>? templates = arguments.NoPromptTemplates ? [] : null;

		// --system-prompt / --append-system-prompt 既接受字面文本，也接受文件路径（与 pi 一致）。
		var customSystemPrompt = arguments.SystemPrompt is { Length: > 0 } systemPrompt
			? CliInput.ResolvePromptInput(systemPrompt, cwd)
			: null;
		var appendSystemPrompt = arguments.AppendSystemPrompt.Count == 0
			? null
			: string.Join(
				"\n\n",
				arguments.AppendSystemPrompt.Select(value => CliInput.ResolvePromptInput(value, cwd)));

		// 环境启动时已解析好的决策；若本次调用显式带 --approve/--no-approve 则以它为准。
		var projectTrusted = arguments.ProjectTrustOverride ?? ProjectTrusted;

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
			Model = Model,
			StreamFn = sessionStream,
			// null 表示「沿用会话里记录的级别」，恢复会话时因此能记住上次的选择。
			ThinkingLevel = arguments.ThinkingLevel ?? Settings.DefaultThinkingLevel,
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
			Skills = skills,
			PromptTemplates = templates,
			CustomSystemPrompt = customSystemPrompt,
			AppendSystemPrompt = appendSystemPrompt,
			NoContextFiles = arguments.NoContextFiles,
			UiHost = uiHost,
			ProjectTrusted = projectTrusted,
		});
	}

	/// <summary>内置工具（按参数过滤）在前，MCP 工具在后；新建会话与服务器报告新工具后的
	/// 刷新都会用到。</summary>
	public IReadOnlyList<AgentTool> ComposeTools(CliArguments arguments, IReadOnlyList<AgentTool>? extraTools) =>
		[.. Tools, .. FilterExtraTools(arguments, extraTools ?? [])];

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

	/// <summary>打开或创建参数所要求的会话文件。</summary>
	public static SessionManager ResolveSession(CliArguments arguments, string cwd)
	{
		if (arguments.NoSession)
		{
			return SessionManager.InMemory(cwd, arguments.SessionId);
		}

		var sessionDir = arguments.SessionDir;

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

		return SessionManager.Create(cwd, sessionDir, arguments.SessionId);
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

		return Directory
			.EnumerateFiles(directory, "*.jsonl")
			.FirstOrDefault(path => Path.GetFileName(path).Contains(pathOrId, StringComparison.OrdinalIgnoreCase));
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
