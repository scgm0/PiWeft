using System.Text;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiWeft.Agent;
using PiWeft.Ai;
using PiWeft.Imaging;
using PiWeft.Ai.Json;
using PiWeft.Ai.Utils;
using PiWeft.CodingAgent.Compatibility;
using PiWeft.CodingAgent.Compaction;
using PiWeft.CodingAgent.Prompts;
using PiWeft.CodingAgent.Resources;
using PiWeft.CodingAgent.Security;
using PiWeft.CodingAgent.Extensions;
using PiWeft.CodingAgent.Models;
using PiWeft.CodingAgent.Session;
using PiWeft.CodingAgent.Settings;
using PiWeft.CodingAgent.Tools;
using PiWeft.JavaScript;

// 本类型上也有 `Tools` 属性，因此给 shell 运行器起别名。
using ShellRunner = PiWeft.CodingAgent.Tools.ShellRunner;

// `PiWeft.Agent` 同时是命名空间，因此给运行时类型起别名。
using AgentRuntime = PiWeft.Agent.Agent;

// 会话上也有 `Tools` 属性，因此给 todo 工具工厂起别名。
using TodoToolFactory = PiWeft.CodingAgent.Tools.TodoTool;
using AskQuestionToolFactory = PiWeft.CodingAgent.Tools.AskQuestionTool;

// `PiWeft.JavaScript` 里也有 CodeModeTool（沙箱侧类型），这里只用工具工厂。
using CodeModeToolFactory = PiWeft.CodingAgent.Tools.CodeModeTool;

namespace PiWeft.CodingAgent;

public sealed record CodingAgentSessionOptions
{
	/// 是否按 pi 约定发现并加载扩展（全局 + 受信任项目的 <c>extensions/</c>）。
	/// 默认开：没有扩展目录时开销只是几次目录存在性检查。
	public bool LoadExtensions { get; init; } = true;

	public ExtensionHost? Extensions { get; init; }

	public IReadOnlyList<string> ExtensionPaths { get; init; } = [];

	public bool DiscoverExtensions { get; init; } = true;

	public IReadOnlyDictionary<string, string?> FlagValues { get; init; } =
		new Dictionary<string, string?>(StringComparer.Ordinal);

	public required string Cwd { get; init; }

	public required Model Model { get; init; }

	public required StreamFn StreamFn { get; init; }

	public ModelRegistry? Registry { get; init; }

	public Func<string, string?>? ApiKeyForProvider { get; init; }

	public ImagesGateway? Images { get; init; }

	public ClassifiersGateway? Classifiers { get; init; }

	/// 会话文件记录了 <c>model_change</c> 时优先恢复它（pi 的 <c>findInitialModel</c> 同义）；
	/// 显式 <c>--model</c> 由调用方关掉这个开关，模型已不存在时自动回退到 <see cref="Model"/>。
	public bool RestoreRecordedModel { get; init; }

	/// 思考级别。为 <c>null</c> 时沿用会话记录（恢复会话时就是上次的），没有记录则用
	/// <see cref="ThinkingLevels.Off"/>。与 pi 一致：显式指定 > 会话记录 > 默认值。
	public string? ThinkingLevel { get; init; }

	public IReadOnlyList<AgentTool>? Tools { get; init; }

	public IReadOnlyList<AgentTool> ExtraTools { get; init; } = [];

	public SessionManager? Session { get; init; }

	public bool Persist { get; init; } = true;

	public bool RecordInitialThinkingLevel { get; init; }

	public CodingAgentSettings Settings { get; init; } = new();

	public IReadOnlyList<string>? ExtraGuidelines { get; init; }

	public string? CustomSystemPrompt { get; init; }

	public string? AppendSystemPrompt { get; init; }

	public bool NoContextFiles { get; init; }

	public IUiHost? UiHost { get; init; }

	/// 宿主提供的关联会话能力；为 null 时不注册 start_session / send_to_session / list_linked_sessions。
	public ILinkedSessionHost? LinkedSessionHost { get; init; }

	/// 本会话是关联子会话时，指向开出它的父会话。
	public CodingAgentSession? LinkedParent { get; init; }

	/// 父会话的展示标题（子会话的系统提示用）。
	public string? LinkedParentTitle { get; init; }

	/// 项目是否受信任。为 false 时不加载项目级技能与提示词模板（<c>&lt;cwd&gt;/.pi/skills</c>、
	/// <c>.agents/skills</c> 等），用户级目录照常加载。上下文文件不受影响，与 pi 一致。
	public bool ProjectTrusted { get; init; } = true;

	public StreamOptions Options { get; init; } = new();

	public IReadOnlyList<Skills.Skill>? Skills { get; init; }

	public IReadOnlyList<PromptTemplate>? PromptTemplates { get; init; }

	public ResourceSnapshot? Resources { get; init; }

	public Func<BeforeToolCallContext, CancellationToken, ValueTask<BeforeToolCallResult?>>? BeforeToolCall { get; init; }

	public Func<AfterToolCallContext, CancellationToken, ValueTask<AfterToolCallResult?>>? AfterToolCall { get; init; }

	/// 扩展的 <c>cache_warming_decision</c> 处理器：返回 <c>warm</c>/<c>stop</c> 覆盖 pi 的决策，
	/// <c>null</c> 或抛异常时沿用原决策。
	public Func<CacheWarmingDecisionEvent, CancellationToken, Task<string?>>? DecideCacheWarming { get; init; }
}

/// 一个编码代理会话：分章节的系统提示词、内置工具、仅追加的会话文件与代理循环。
/// 已落盘的消息是权威记录，每次请求发送的都是当前分支的投影。
public sealed partial class CodingAgentSession : IDisposable
{
	private readonly AgentRuntime _agent;
	private CacheWarmer? _cacheWarmer;

	private IReadOnlyList<BuiltinTool> _promptTools = [];

	private SystemPromptOptions _systemPromptSections = null!;

	private SystemMessage? _refreshedSystemPrompt;

	/// 扩展 <c>before_agent_start</c> 为本轮改写的提示词。只在本轮生效：下一轮仍从
	/// <see cref="_refreshedSystemPrompt"/> 重新算，否则每轮都在上一轮结果上追加，提示词会无限变长。
	private SystemMessage? _extensionSystemPrompt;
	private IReadOnlyList<Message>? _projectedCache;
	private long _projectedCacheRevision = -1;
	private SystemMessage? _projectedCachePrompt;
	private readonly IDisposable _persistence;
	private readonly IDisposable _cacheWarmLifecycle;
	private readonly IDisposable _modeLifecycle;
	private readonly IDisposable _extensionLifecycle;

	private readonly List<Func<AgentEvent, ValueTask>> _listeners = [];

	private int _retryAttempt;
	private AssistantMessage? _routeFailed;

	private CancellationTokenSource? _retryAbort;

	private readonly Lock _abortGate = new();
	private CancellationTokenSource? _runAbort;

	private bool? _autoRetryOverride;
	private bool _overflowRecoveryAttempted;
	private readonly StreamFn _streamFn;
	private readonly StreamFn _routedStreamFn;
	private readonly StreamFn _directStreamFn;
	private static readonly Lazy<ModelRegistry> FallbackModelRegistry = new(() => ModelCatalogOverlay.CreateRegistry());

	private readonly ModelRegistry? _registry;
	private readonly Func<string, string?>? _apiKeyForProvider;
	private readonly ImagesGateway? _images;
	private readonly ClassifiersGateway? _classifiers;
	private readonly StreamOptions _streamOptions;
	private bool _disposed;
	private readonly Lock _statisticsGate = new();

	private readonly IReadOnlyList<Skills.Skill>? _explicitSkills;
	private readonly IReadOnlyList<PromptTemplate>? _explicitTemplates;
	private readonly SessionStatisticsCache _statistics = new();
	private long _todoRevision = -1;
	private TodoState _todo = TodoState.Empty;

	private bool _planModeFromMarker;
	private bool _todoWidgetPublished;
	private IReadOnlyList<Message>? _contextMessages;
	private IReadOnlyList<Message>? _imageCountSource;
	private int _imageCount;
	private (long Tokens, int? LastUsageIndex) _contextTokens;
	private bool _contextAnchorValid = true;
	private long _contextAnchorRevision = -1;
	// 逐条估算的增量缓存：上下文估算在长会话里要 100+ 毫秒，而投影几乎只是往后追加。
	private IReadOnlyList<Message>? _estimateSource;
	private long[] _estimateValues = [];
	private long _estimateTotal;
	private int _estimateCount;

	private IReadOnlyList<BuiltinTool> _fullTools = [];

	private readonly Lazy<AgentTool> _toolSearch;

	private const string ToolLoadEntryType = "tool-load";

	private readonly Func<BeforeToolCallContext, CancellationToken, ValueTask<BeforeToolCallResult?>>? _userBeforeToolCall;

	private readonly Func<AfterToolCallContext, CancellationToken, ValueTask<AfterToolCallResult?>>? _userAfterToolCall;

	/// 进程内 JS 扩展宿主。默认按 pi 约定发现全局与项目扩展；传 <c>extensions: null</c> 关闭，
	/// 测试与一次性会话用。
	public ExtensionHost? Extensions { get; private set; }

	public IReadOnlyDictionary<string, ExtensionFlag> ExtensionFlags =>
		Extensions?.Flags ?? new Dictionary<string, ExtensionFlag>();

	public IReadOnlyList<string> UnknownExtensionFlags => Extensions?.UnknownFlags ?? [];

	public IReadOnlyList<string> ExtensionFlagErrors => Extensions?.FlagErrors ?? [];

	private bool _ownsExtensions = true;

	private IReadOnlyList<BuiltinTool> _builtinTools = [];

	private CodingAgentSessionOptions? _sessionOptions;

	public CodingAgentSession(CodingAgentSessionOptions options)
	{
		ArgumentNullException.ThrowIfNull(options);

		Cwd = Path.GetFullPath(options.Cwd);
		Settings = options.Settings;
		// 安全门跟着会话走：档位可以按会话文件路径覆盖，界面改档也直接落在这里。
		Security = options.Settings.Security;
		UiHost = options.UiHost;
		ProjectTrusted = options.ProjectTrusted;
		_images = options.Images;
		_classifiers = options.Classifiers;
		Session = options.Session
			?? (options.Persist ? SessionManager.Create(Cwd) : SessionManager.InMemory(Cwd));
		Linked = options.LinkedSessionHost is { } linkedHost
			? new LinkedSessionHub(this, linkedHost, options.LinkedParent, options.LinkedParentTitle)
			: null;

		_explicitSkills = options.Skills;
		_explicitTemplates = options.PromptTemplates;
		Resources = options.Resources ?? ResourceResolver.Resolve(Cwd, agentDir: null, includeProject: options.ProjectTrusted);
		if (options.Skills is not null || options.PromptTemplates is not null)
		{
			// CLI 的 --no-skills / --no-prompt-templates 以显式空列表表达，优先于自动发现。
			Resources = Resources with
			{
				Skills = options.Skills ?? Resources.Skills,
				Templates = options.PromptTemplates ?? Resources.Templates,
			};
		}

		// 内置工具的提示词元数据以名称索引：命令行/桌面会传入已经过滤的 AgentTool 列表，
		// 这里按名称把短 snippet 与 guidelines 重新挂回去，保证提示词与 pi/PiDeck 一致。
		var defaults = BuiltinTools.Create(Cwd, () => _agent?.Model);
		var defaultsByName = new Dictionary<string, BuiltinTool>(defaults.Count, StringComparer.Ordinal);
		foreach (var tool in defaults)
		{
			defaultsByName[tool.Tool.Name] = tool;
		}

		List<BuiltinTool> builtins;
		if (options.Tools is null)
		{
			// 与 pi 一致：没显式给工具列表时只启用 read/bash/edit/write（外加原生 todo/ask_question）。
			// 目录里的 grep/find/ls/powershell/codemode 需要 --tools 或 defaultTools 显式开启。
			var presets = new HashSet<string>(PiWeft.CodingAgent.Settings.CodingAgentSettings.DefaultToolNames, StringComparer.Ordinal);
			builtins =
			[
				.. defaults.Where(tool => presets.Contains(tool.Tool.Name)),
				.. options.ExtraTools.Select(static tool => new BuiltinTool(tool, tool.Description, [])),
			];
		}
		else
		{
			builtins = new List<BuiltinTool>(options.Tools.Count + options.ExtraTools.Count);
			foreach (var tool in options.Tools)
			{
				builtins.Add(defaultsByName.TryGetValue(tool.Name, out var described)
					? described with { Tool = tool }
					: new BuiltinTool(tool, tool.Description, []));
			}

			foreach (var tool in options.ExtraTools)
			{
				builtins.Add(new BuiltinTool(tool, tool.Description, []));
			}
		}

		// 把占位的 todo 工具换成绑定到本会话的版本，使其能读写当前分支的 PiDeck 兼容快照。
		// 切换会话（SwitchSession/ForkAt）时闭包会读到新的 Session，无需重建工具。
		for (var index = 0; index < builtins.Count; index++)
		{
			if (builtins[index].Tool.Name == TodoToolFactory.Name)
			{
				builtins[index] = TodoToolFactory.Create(() => Session, PublishTodoWidget);
			}
			else if (builtins[index].Tool.Name == AskQuestionToolFactory.Name)
			{
				builtins[index] = AskQuestionToolFactory.Create(() => this);
			}
			else if (LinkedSessionsTool.IsName(builtins[index].Tool.Name))
			{
				builtins[index] = LinkedSessionsTool.Bind(builtins[index].Tool.Name, () => this);
			}
			else if (builtins[index].Tool.Name == "bash")
			{
				// bash 需要会话上下文来注入 PI_* 环境变量与 settings 的 shell 配置。
				builtins[index] = builtins[index] with { Tool = PiWeft.CodingAgent.Tools.BashTool.Create(Cwd, BashEnvironment) };
			}
			else if (builtins[index].Tool.Name == "powershell")
			{
				// PowerShell 与 bash 一样要注入会话上下文（PI_*）与 shell 配置。
				builtins[index] = builtins[index] with { Tool = PowerShellTool.Create(Cwd, BashEnvironment) };
			}
			else if (builtins[index].Tool.Name == "read")
			{
				// read 需要模型能力来判断是否给出非 vision 提示；图片识别本身按 magic bytes。
				builtins[index] = builtins[index] with { Tool = FileTools.Read(Cwd, () => _agent?.Model) };
			}
		}

		// 关联会话工具跟宿主能力走，不属于 pi 的内置工具集（与 PiDeck 的扩展工具同义）：
		// 有宿主时无论 --tools 怎么配置都补齐，避免用户自定义 defaultTools 时关联会话静默消失。
		if (Linked is not null)
		{
			foreach (var name in LinkedSessionsTool.Names)
			{
				if (!builtins.Any(tool => string.Equals(tool.Tool.Name, name, StringComparison.Ordinal)))
				{
					builtins.Add(LinkedSessionsTool.Bind(name, () => this));
				}
			}
		}

		// 系统提示词只在运行时构造，不写进会话文件：真实 pi 读回时会把 role:system 当成历史，污染上下文。
		_systemPromptSections = new SystemPromptOptions
		{
			Cwd = Cwd,
			ExtraGuidelines = options.ExtraGuidelines ?? SystemPrompt.BaselineGuidelines,
			CustomPrompt = options.CustomSystemPrompt ?? SystemPromptFiles.LoadSystem(Cwd, options.ProjectTrusted),
			Addendum = CombineAddendum(
				SystemPromptFiles.LoadAppend(Cwd, options.ProjectTrusted),
				options.AppendSystemPrompt),
			Skills = Skills.Skills.FormatForPrompt(DiscoveredSkills),
			ProjectContext = options.NoContextFiles ? null : ContextFiles.Load(Cwd),
		};

		// 有 exposure: codemode 的工具时自动启用 codemode（pi 的 autoEnableCodemode 行为）。
		if (Settings.Codemode.AutoEnable
			&& builtins.Any(static tool => string.Equals(tool.Tool.Exposure, ToolExposure.Codemode, StringComparison.Ordinal))
			&& builtins.TrueForAll(static tool => !string.Equals(tool.Tool.Name, CodeModeToolFactory.Name, StringComparison.Ordinal)))
		{
			builtins.Add(CodeModeToolFactory.CreatePlaceholder());
		}

		_toolSearch = new Lazy<AgentTool>(CreateToolSearchTool);

		// 扩展工具并表要在内置工具集定稿之后、代理建立之前：早了会被 _fullTools 覆盖，
		// 晚了 ApplyTools 还没有 _agent 可用。
		_sessionOptions = options;
		_builtinTools = builtins;
		_fullTools = builtins;
		LoadExtensions(options);
		// 提示词按并表后的工具集构造：扩展注册的工具也要出现在给模型的声明里，
		// 否则工具能被执行、模型却不知道它存在。
		_refreshedSystemPrompt = SystemPrompt.Build(SectionsFor(_fullTools));
		_userBeforeToolCall = options.BeforeToolCall;
		_userAfterToolCall = options.AfterToolCall;

		_streamFn = options.StreamFn;
		_registry = options.Registry;
		_apiKeyForProvider = options.ApiKeyForProvider;
		_routedStreamFn = StreamWithVirtualRouting;
		_directStreamFn = StreamDirectWithVirtualRouting;
		_streamOptions = options.Options with
		{
			TimeoutMs = options.Options.TimeoutMs ?? options.Settings.TimeoutMs,
			IdleTimeoutMs = options.Options.IdleTimeoutMs ??
				(options.Settings.HttpIdleTimeoutMs > 0 ? options.Settings.HttpIdleTimeoutMs : null),
			Transport = options.Options.Transport ?? options.Settings.Transport,
			ThinkingBudgets = options.Options.ThinkingBudgets ??
				(options.Settings.ThinkingBudgets.Count > 0 ? options.Settings.ThinkingBudgets : null),
		};

		_cacheWarmer = new CacheWarmer(
			_directStreamFn,
			() => Settings.CacheWarming,
			usage => Session.AppendUsage("cache_warm", _agent!.Model.Provider, _agent.Model.Id, usage, "prompt cache warm"),
			options.DecideCacheWarming,
			PushCacheWarmingStatus);

		// 恢复会话时，会话文件里记录的模型与思考级别优先于默认值（显式传入的仍然最优先）。
		// 模型这一条对齐 pi 的 findInitialModel：宿主的默认模型只是没有记录时的兜底。
		// BuildSessionContext() 会走一遍分支并投影消息，这里只调一次：
		// 长会话开场时多走一遍分支是看得见的时间（分支部建要遍历整条链）。
		var recorded = Session.Entries.Count > 0 ? Session.BuildSessionContext() : null;
		var initialModel = options.Model;
		if (options.RestoreRecordedModel && recorded?.Model is { } recordedModel &&
			options.Registry?.Get(recordedModel.Provider, recordedModel.ModelId) is { } known)
		{
			// 模型可能已被删除或改名：存在性校验不过就回退到宿主解析出的模型。
			initialModel = known;
		}

		var requestedThinkingLevel = options.ThinkingLevel
			?? recorded?.ThinkingLevel
			?? options.Settings.ModelThinkingLevels.GetValueOrDefault($"{initialModel.Provider}/{initialModel.Id}")
			?? options.Settings.DefaultThinkingLevel
			?? ThinkingLevels.Off;

		_agent = new AgentRuntime(new AgentOptions
		{
			Model = initialModel,
			ThinkingLevel = ThinkingLevelResolver.Clamp(initialModel, requestedThinkingLevel),
			StreamFn = _routedStreamFn,
			// 用 _fullTools，不用局部的 builtins：扩展工具在 LoadExtensions 里并入了 _fullTools，
			// 用局部变量会丢掉它们，工具能执行但不出现在声明里。
			Tools = [.. _fullTools.Select(static builtin => builtin.Tool)],
			ConvertToLlm = CodingMessages.ConvertToLlm,
			TransformContext = TransformContextAsync,
			BeforeToolCall = GuardedBeforeToolCallAsync,
			// 工具结果的图片也要过一遍限制（MCP/扩展/截图工具）；用户钩子先跑，再归一化它的输出。
			AfterToolCall = NormalizeToolResultImagesAsync,
			PrepareNextTurn = PrepareNextTurnAsync,
			WillRetry = ShouldRetryAssistant,
			NormalizeQueuedMessage = NormalizeQueuedMessageAsync,
			Options = _streamOptions,
		});

		// codemode 的描述要列出会话当前可调用的工具，因此等代理建好后再绑定并重建提示词。
		// model-only 只声明给模型，脚本碰不到，别在描述里当可调用工具列出来。
		RebindCodeMode([.. builtins
			.Select(static builtin => builtin.Tool)
			.Where(static tool => tool.Exposure != ToolExposure.ModelOnly)]);
		// 扩展工具在 LoadExtensions 里并入 _fullTools；初始的 AgentOptions.Tools 还没应用
		// exposure/default 过滤，统一走一次 ApplyTools。
		ApplyTools();

		_agent.SteeringMode = ParseQueueMode(options.Settings.SteeringMode);
		_agent.FollowUpMode = ParseQueueMode(options.Settings.FollowUpMode);

		// 恢复会话：代理从分支投影出发，使其自身状态与文件一致。
		_agent.SetMessages(Session.BuildProjectedMessages());

		// CLI 保留上游启动行为；桌面仅浏览时不追加元数据。
		if (options.RecordInitialThinkingLevel)
		{
			Session.AppendThinkingLevelChange(_agent.ThinkingLevel);
		}

		_persistence = _agent.Subscribe(PersistAsync);
		_cacheWarmLifecycle = _agent.Subscribe(CacheWarmLifecycleAsync);
		_modeLifecycle = _agent.Subscribe(ModeLifecycleAsync);
		_extensionLifecycle = _agent.Subscribe(ExtensionLifecycleAsync);
		PublishTodoWidget(CurrentTodo());
		// 计划/目标模式的状态跟着分支走：执行态续接进度，目标只恢复为暂停。
		RestoreModeState();
		PublishPlanWidget();
		PublishGoalWidget();
		DispatchSessionLifecycle("session_start");
	}

	public string Cwd { get; }

	public CodingAgentSettings Settings { get; }

	/// 当前生效的安全门配置。启动时来自 <c>settings.security</c>（只读全局），
	/// 用户在界面上改档位时就地替换，换档不重建会话，也不要求重启。
	public SecurityConfig Security { get; set; } = SecurityConfig.Off;

	public string SecurityLevelId => Security.Resolve(Session.SessionFile).Level;

	private readonly HashSet<string> _securityApprovedTools = new(StringComparer.Ordinal);

	public IUiHost? UiHost { get; }

	public bool ProjectTrusted { get; }

	public SessionManager Session { get; private set; }

	/// 关联会话：开出子会话、互相转发消息、把子会话的回合结果回报到本会话。
	/// 只在宿主提供了 <see cref="ILinkedSessionHost"/> 时存在；纯 CLI 为 null。
	public LinkedSessionHub? Linked { get; }

	public AgentRuntime Agent => _agent;

	public Model Model => _agent.Model;

	public Compaction.CacheWarmingStatus? CacheWarmingStatus => _cacheWarmer?.Status;

	public string ThinkingLevel => _agent.ThinkingLevel;

	public IReadOnlyList<AgentTool> Tools => _agent.Tools;

	public IReadOnlyList<AgentTool> DeclaredTools => _agent.DeclaredTools;

	/// 当前生效的完整系统提示词。pi 的 <c>/share</c> 把它写进 <c>pi.share</c> 尾条目，
	/// 查看器据此渲染工具说明；会话文件本身不存系统提示词。
	public string CurrentSystemPrompt =>
		// 系统提示词按具名章节存放（preamble/tools/rules/cwd…），发给模型前才拼成文本；
		// /share 要的是模型实际看到的那一份，所以走同一个渲染函数。
		Transcript.GetText(_extensionSystemPrompt ?? (_refreshedSystemPrompt ??= SystemPrompt.Build(SectionsFor(_promptTools))));

	public IReadOnlyList<Message> Messages => Project();

	/// 下一次请求将携带的消息：分支投影；若会话中途工具集发生变化，
	/// 其中的系统提示词会被重建。
	public IReadOnlyList<Message> ProjectedMessages => Project();

	public bool IsCompacting { get; private set; }

	public int PendingMessageCount => _agent.PendingMessageCount;

	public string? SessionName => Session.CurrentName();

	public ResourceSnapshot Resources { get; private set; }

	public IReadOnlyList<Skills.Skill> DiscoveredSkills => Resources.Skills;

	public IReadOnlyList<PromptTemplate> PromptTemplates => Resources.Templates;

	/// 重新解析技能、提示词模板与主题，对应 pi 的 <c>/reload</c>：不重建会话，
	/// 只刷新提示词里的技能章节，使后续请求看到新的资源集合。
	public void ReloadResources()
	{
		ThrowIfDisposed();

		var resolved = ResourceResolver.Resolve(Cwd, agentDir: null, includeProject: ProjectTrusted);
		Resources = resolved with
		{
			// 显式传入的列表（--no-skills 等）在重载后继续生效。
			Skills = _explicitSkills ?? resolved.Skills,
			Templates = _explicitTemplates ?? resolved.Templates,
		};

		_systemPromptSections = _systemPromptSections with
		{
			Skills = Skills.Skills.FormatForPrompt(Resources.Skills),
		};

		// 扩展也要重载：改完扩展文件还要重启应用才生效，等于没有热更新。
		// 只有宿主自己加载的才重建。注入进来的（options.Extensions）生命周期归宿主，
		// 这里擅自 Dispose 别人的宿主是越界。
		if (_ownsExtensions)
		{
			Extensions?.Dispose();
			Extensions = null;
			// -e npm:/git: 的解析结果也要重查：改了包版本后 /reload 就该拿到新代码。
			ExtensionPackageCache.Invalidate();
			LoadExtensions(_sessionOptions! with { Extensions = null });
		}

		ApplyTools();
	}

	public string ExpandPromptTemplate(string text) => Prompts.PromptTemplates.Expand(text, PromptTemplates);

	/// 展开一条用户输入：先展开 <c>/skill:name</c> 技能命令，再展开提示词模板，顺序与 pi 一致。
	public string ExpandInput(string text)
	{
		if (text.Length == 0)
		{
			return text;
		}

		// 大多数输入既不是技能命令也不是模板（不以 '/' 开头），先快速返回避免任何额外分配。
		if (text[0] != '/')
		{
			return text;
		}

		return Prompts.PromptTemplates.Expand(
			Skills.Skills.ExpandCommand(text, DiscoveredSkills),
			PromptTemplates);
	}

	/// 把一条文本输入（可选图片）组装成待发送的用户消息，展开技能命令与提示词模板。
	/// CLI、RPC 与桌面共用这条路径，保证三者的输入语义一致。
	public UserMessage PrepareInput(string text, IReadOnlyList<ImageContent>? images = null)
	{
		// 用户发消息：关联会话的自动续跑额度清零（PiDeck 同义）。
		// 子会话回报不走这里，所以不会被自己触发的父会话轮次清零。
		Linked?.NoteUserPrompt();

		// 计划模式：PiDeck 的 Plan 开关会在消息首行注入隐藏标记。带标记就开启，
		// 随后第一条无标记消息（来自同一开关流）自动退出；/plan 手动的源不自动退出。
		if (PlanMode.TryStripMarker(text, out var withoutMarker))
		{
			_planModeFromMarker = true;
			SetPlanMode(true);
			text = withoutMarker;
		}
		else if (PlanModeEnabled && _planModeFromMarker)
		{
			_planModeFromMarker = false;
			SetPlanMode(false);
		}

		// 目标模式：带标记进入/推进目标；切回普通模式的下一条无标记消息只暂停，不清除。
		if (GoalMode.TryStripMarker(text, out var objective))
		{
			text = objective;
			if (_goal.Objective.Length == 0 || _goal.Phase == GoalState.Complete)
			{
				SetGoal(objective);
			}
			else if (_goal.Phase is GoalState.Paused or GoalState.Blocked)
			{
				// 暂停/阻塞 ≠ 完成：正文只是这一轮的指令，目标保持不变（PiDeck 同义）。
				SetGoal(_goal.Objective, resetRounds: false);
			}
			else
			{
				lock (_goalGate)
				{
					_goal = _goal with { Enabled = true, Phase = GoalState.Active };
					PersistGoalState();
				}
				PublishGoalWidget();
				RefreshSystemPrompt();
			}
		}
		else if (_goal.Running)
		{
			PauseGoal();
		}

		var expanded = ExpandInput(text);
		var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

		if (images is not { Count: > 0 })
		{
			return new UserMessage { Content = MessageContent.FromText(expanded), Timestamp = timestamp };
		}

		var blocks = new List<ContentBlock>(images.Count + 1) { new TextContent { Text = expanded } };
		blocks.AddRange(images);
		return new UserMessage { Content = MessageContent.FromBlocks(blocks), Timestamp = timestamp };
	}

	/// 附件图片按当前模型的 <c>inputLimits.images.resize</c> 归一化：限制内原样保留
	/// （动画 GIF/WebP 也不动），超限才解码、应用 EXIF 方向、缩放并重编码；失败的那张
	/// 改成一句括注。与 pi 的 <c>_normalizePromptImages</c> 同义。
	/// <para>
	/// 归一化放在要进上下文的时刻，不放在 <see cref="PrepareInput"/>：
	/// 解码 + 重采样是 CPU 重活（12MP 照片数百毫秒），<see cref="PrepareInput"/> 在桌面端
	/// 是 UI 线程上的同步调用；排队消息也可能在等待期间换了模型。
	/// </para>
	private UserMessage NormalizeMessage(UserMessage message)
	{
		if (message.Content.Blocks is not { Count: > 0 } blocks ||
			!blocks.Any(static block => block is ImageContent))
		{
			return message;
		}

		if (Settings.Images.BlockImages)
		{
			return message with { Content = MessageContent.FromBlocks(ReplaceImages(blocks, ImageBlockedNote)) };
		}

		var options = ImageResizeOptions.FromInputLimits(Model.InputLimits);
		var normalized = new List<ContentBlock>(blocks.Count);
		var hints = new List<string>();
		var changed = false;
		var imageCount = 0;

		foreach (var block in blocks)
		{
			if (block is not ImageContent image)
			{
				normalized.Add(block);
				continue;
			}

			if (++imageCount > options.MaxPerMessage)
			{
				hints.Add($"[Image omitted: maxPerMessage={options.MaxPerMessage}.]");
				changed = true;
				continue;
			}

			if (!TryDecodeBase64(image.Data, out var bytes))
			{
				hints.Add("[Image omitted: data is not valid base64.]");
				changed = true;
				continue;
			}

			var processed = ImagePipeline.ProcessBytes(bytes, image.MimeType, options);
			if (!processed.Ok)
			{
				hints.Add(processed.Error ?? "[Image omitted.]");
				changed = true;
				continue;
			}

			// 未重编码时沿用原始 base64 字符串：省一次大字符串的分配与拷贝。
			// 判据用「原样返回同一个数组」，不比较 mime：扩展名与内容不符时 mime 会变，
			// 但字节没变，仍可复用调用方手里的 base64。
			var unchanged = ReferenceEquals(processed.Bytes, bytes);
			normalized.Add(unchanged
				? image
				: new ImageContent { Data = Convert.ToBase64String(processed.Bytes), MimeType = processed.MimeType });
			changed |= !unchanged;
			hints.AddRange(processed.Hints);
		}

		if (!changed && hints.Count == 0)
		{
			return message;
		}

		if (hints.Count > 0)
		{
			AppendHints(normalized, hints);
		}

		return message with { Content = MessageContent.FromBlocks(normalized) };
	}

	/// 扩展的 <c>before_agent_start</c>：当前只应用返回的 <c>systemPrompt</c>。
	/// 返回的 <c>message</c> 还没有注入会话，避免与 PromptAsync 的追加顺序冲突。
	private void DispatchBeforeAgentStart(Message message)
	{
		if (Extensions is not { } extensions || !extensions.ListensTo("before_agent_start"))
		{
			return;
		}

		// 始终以基础提示词为输入：扩展看到的是「没有它自己上一轮改动」的版本。
		var basePrompt = _refreshedSystemPrompt ??= SystemPrompt.Build(SectionsFor(_promptTools));
		var payload = new JsonObject
		{
			["messageType"] = message is UserMessage ? "user" : "custom",
			["systemPrompt"] = Transcript.GetText(basePrompt),
		};
		if (message is UserMessage { Content.IsText: true } user)
		{
			payload["prompt"] = user.Content.Text;
		}
		else if (message is UserMessage { Content.Blocks: { } blocks })
		{
			payload["images"] = PiJson.SerializeBlocks(
				[.. blocks.OfType<ImageContent>()]);
		}

		var updated = extensions.Dispatch("before_agent_start", payload);
		_extensionSystemPrompt = updated["systemPrompt"] is JsonValue value &&
			value.TryGetValue<string>(out var systemPrompt) &&
			!string.IsNullOrWhiteSpace(systemPrompt)
				? new SystemMessage
				{
					Content = MessageContent.FromText(systemPrompt),
					Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
				}
				: null;
	}

	/// 扩展的 <c>tool_result</c>：先让扩展改写结果，再跑用户钩子与图片归一化。
	private AfterToolCallResult? DispatchToolResult(AfterToolCallContext context)
	{
		if (Extensions is not { } extensions || !extensions.ListensTo("tool_result"))
		{
			return null;
		}

		var payload = new JsonObject
		{
			["toolName"] = context.ToolCall.Name,
			["toolCallId"] = context.ToolCall.Id,
			["input"] = context.Arguments.DeepClone(),
			["content"] = PiJson.SerializeBlocks(context.Result.Content),
			["isError"] = context.IsError,
		};
		if (context.Result.Details is { } details)
		{
			payload["details"] = details.DeepClone();
		}
		if (context.Result.StructuredContent is { } structured)
		{
			payload["structuredContent"] = structured.DeepClone();
		}

		var updated = extensions.Dispatch("tool_result", payload);
		var content = ReadContent(updated["content"]);
		var hasDetails = updated.ContainsKey("details");
		var hasStructured = updated.ContainsKey("structuredContent");
		bool? isError = updated["isError"] is JsonValue errorValue && errorValue.TryGetValue<bool>(out var parsedError)
			? parsedError
			: null;
		if (content is null && !hasDetails && !hasStructured && isError is null)
		{
			return null;
		}

		return new AfterToolCallResult
		{
			Content = content,
			Details = hasDetails ? updated["details"]?.DeepClone() : null,
			HasDetails = hasDetails,
			StructuredContent = hasStructured ? updated["structuredContent"]?.DeepClone() : null,
			HasStructuredContent = hasStructured,
			IsError = isError,
		};
	}

	private static IReadOnlyList<ContentBlock>? ReadContent(JsonNode? node) =>
		node is JsonArray ? PiJson.DeserializeBlocks(node.ToJsonString()) : null;

	/// 扩展的 <c>agent_end</c>：本轮结束、自动重试与后续队列尚未决定时触发。
	private void DispatchAgentEnd(AgentEndEvent end)
	{
		var messages = new JsonArray();
		foreach (var message in end.Messages)
		{
			messages.Add(JsonNode.Parse(PiJson.SerializeMessage(message)));
		}

		DispatchExtension("agent_end", new JsonObject
		{
			["messages"] = messages,
			["willRetry"] = end.WillRetry,
		});
	}

	/// 工具结果的图片同样会进入历史与后续每个请求（MCP、扩展、截图工具），
	/// 与 pi 的 <c>normalizeToolResultImages</c> 同义：先跑扩展与用户钩子，再把结果里的图片按当前模型
	/// 限制缩放/转码；括注作为额外的文本块追加在后面。与 <c>read</c> 的差别是失败时**保留原图块**：
	/// 工具已经产出了它，删掉等于改变工具行为。
	private async ValueTask<AfterToolCallResult?> NormalizeToolResultImagesAsync(
		AfterToolCallContext context,
		CancellationToken cancellationToken)
	{
		var extensionHook = DispatchToolResult(context);
		if (extensionHook is not null)
		{
			context = context with
			{
				Result = context.Result with
				{
					Content = extensionHook.Content ?? context.Result.Content,
					Details = extensionHook.HasDetails ? extensionHook.Details : context.Result.Details,
					StructuredContent = extensionHook.HasStructuredContent ? extensionHook.StructuredContent : context.Result.StructuredContent,
					IsError = extensionHook.IsError ?? context.Result.IsError,
				},
				IsError = extensionHook.IsError ?? context.IsError,
			};
		}

		var userHook = _userAfterToolCall is { } hook
			? await hook(context, cancellationToken).ConfigureAwait(false)
			: null;
		var merged = MergeAfterToolCallHooks(extensionHook, userHook);

		var content = merged?.Content ?? context.Result.Content;
		if (!content.Any(static block => block is ImageContent))
		{
			return merged;
		}

		var normalized = await Task.Run(() => NormalizeImageBlocks(content), cancellationToken).ConfigureAwait(false);
		if (ReferenceEquals(normalized, content))
		{
			return merged;
		}

		return new AfterToolCallResult
		{
			Content = normalized,
			Details = merged?.Details,
			HasDetails = merged?.HasDetails ?? false,
			StructuredContent = merged?.StructuredContent,
			HasStructuredContent = merged?.HasStructuredContent ?? false,
			IsError = merged?.IsError,
			Usage = merged?.Usage,
			Terminate = merged?.Terminate,
		};
	}

	private const string ImageBlockedNote = "(image omitted: image input is disabled)";

	private static List<ContentBlock> ReplaceImages(IReadOnlyList<ContentBlock> blocks, string note)
	{
		var replaced = new List<ContentBlock>(blocks.Count);
		var previousWasNote = false;
		foreach (var block in blocks)
		{
			if (block is ImageContent)
			{
				if (!previousWasNote)
				{
					replaced.Add(new TextContent { Text = note });
				}

				previousWasNote = true;
				continue;
			}

			replaced.Add(block);
			previousWasNote = block is TextContent text &&
				string.Equals(text.Text, note, StringComparison.Ordinal);
		}

		return replaced;
	}

	private static AfterToolCallResult? MergeAfterToolCallHooks(
		AfterToolCallResult? extensionHook,
		AfterToolCallResult? userHook)
	{
		if (extensionHook is null)
		{
			return userHook;
		}

		if (userHook is null)
		{
			return extensionHook;
		}

		return new AfterToolCallResult
		{
			Content = userHook.Content ?? extensionHook.Content,
			Details = userHook.HasDetails ? userHook.Details : extensionHook.Details,
			HasDetails = userHook.HasDetails || extensionHook.HasDetails,
			StructuredContent = userHook.HasStructuredContent ? userHook.StructuredContent : extensionHook.StructuredContent,
			HasStructuredContent = userHook.HasStructuredContent || extensionHook.HasStructuredContent,
			IsError = userHook.IsError ?? extensionHook.IsError,
			Usage = userHook.Usage ?? extensionHook.Usage,
			Terminate = userHook.Terminate ?? extensionHook.Terminate,
		};
	}

	/// 逐块处理图片；没有任何改动时返回原列表（调用方据此跳过重写）。
	private List<ContentBlock>? NormalizeImageBlocks(IReadOnlyList<ContentBlock> content)
	{
		if (Settings.Images.BlockImages)
		{
			return ReplaceImages(content, ImageBlockedNote);
		}

		var options = ImageResizeOptions.FromInputLimits(Model.InputLimits);
		List<ContentBlock>? normalized = null;
		for (var index = 0; index < content.Count; index++)
		{
			if (content[index] is not ImageContent image)
			{
				normalized?.Add(content[index]);
				continue;
			}

			// 解不开/处理失败：保留原图块（与 pi 一致），不删工具产出。
			if (!TryDecodeBase64(image.Data, out var bytes))
			{
				normalized?.Add(image);
				continue;
			}

			var processed = ImagePipeline.ProcessBytes(bytes, image.MimeType, options);
			if (!processed.Ok)
			{
				normalized?.Add(image);
				continue;
			}

			var bytesUnchanged = ReferenceEquals(processed.Bytes, bytes);
			if (bytesUnchanged && processed.Hints.Count == 0)
			{
				normalized?.Add(image);
				continue;
			}

			normalized ??= [.. content.Take(index)];
			normalized.Add(bytesUnchanged
				? image
				: new ImageContent { Data = Convert.ToBase64String(processed.Bytes), MimeType = processed.MimeType });
			if (processed.Hints.Count > 0)
			{
				normalized.Add(new TextContent { Text = string.Join("\n", processed.Hints) });
			}
		}

		return normalized;
	}

	/// 把括注并进第一个文本块（没有就补一个），与 pi 把 hints 拼进 prompt 的位置一致。
	private static void AppendHints(List<ContentBlock> blocks, IReadOnlyList<string> hints)
	{
		var text = string.Join("\n", hints);
		for (var index = 0; index < blocks.Count; index++)
		{
			if (blocks[index] is TextContent existing)
			{
				blocks[index] = new TextContent
				{
					Text = existing.Text.Length > 0 ? $"{existing.Text}\n\n{text}" : text,
				};
				return;
			}
		}

		blocks.Insert(0, new TextContent { Text = text });
	}

	private static bool TryDecodeBase64(string data, out byte[] bytes)
	{
		try
		{
			bytes = Convert.FromBase64String(data);
			return true;
		}
		catch (FormatException)
		{
			bytes = [];
			return false;
		}
	}

	private ValueTask<Message> NormalizeQueuedMessageAsync(Message message, CancellationToken cancellationToken) =>
		new(NormalizeMessageAsync(message, cancellationToken));

	/// 归一化是 CPU 密集的：放到线程池，别占住桌面 UI 线程或 RPC 事件循环。
	private Task<Message> NormalizeMessageAsync(Message message, CancellationToken cancellationToken)
	{
		if (message is not UserMessage user ||
			user.Content.Blocks is not { Count: > 0 } blocks ||
			!blocks.Any(static block => block is ImageContent))
		{
			return Task.FromResult(message);
		}

		return Task.Run<Message>(() =>
		{
			cancellationToken.ThrowIfCancellationRequested();
			return NormalizeMessage(user);
		}, cancellationToken);
	}

	/// 把叶子移动到 <paramref name="targetId"/>，并先把被放弃的路径摘要成一条
	/// <c>branch_summary</c> 条目。
	public async Task<BranchSummaryResult?> NavigateWithSummaryAsync(
		string targetId,
		string? customInstructions = null,
		CancellationToken cancellationToken = default)
	{
		ThrowIfDisposed();
		if (_agent.IsStreaming)
		{
			throw new InvalidOperationException("Cannot navigate while the session is streaming");
		}

		var result = await BranchSummarizer.NavigateWithSummaryAsync(
			Session,
			_agent.Model,
			_directStreamFn,
			_streamOptions,
			Settings.Compaction.ForModel(_agent.Model),
			Settings.BranchSummary,
			targetId,
			customInstructions,
			cancellationToken,
			thinkingLevel: _agent.ThinkingLevel).ConfigureAwait(false);

		_agent.SetMessages(Session.BuildProjectedMessages());
		return result;
	}

	/// 直接把叶子移到某个条目并重载消息，不生成分支摘要。界面上的「跳到此处」用它，
	/// 只做回退不做摘要生成，因此是即时操作。
	public void NavigateTo(string entryId)
	{
		ThrowIfDisposed();

		if (_agent.IsStreaming)
		{
			throw new InvalidOperationException("运行期间不能切换分支");
		}

		if (!Session.Contains(entryId))
		{
			throw new InvalidOperationException($"找不到条目 {entryId}");
		}

		Session.Branch(entryId);
		_agent.SetMessages(Session.BuildProjectedMessages());
	}

	/// 替换当前会话，<c>switch_session</c>、<c>fork</c>、<c>clone</c> 都需要它。代理保留自己的模型、
	/// 工具与设置，会话记录则按新会话的分支重建。
	public void SwitchSession(SessionManager session)
	{
		ThrowIfDisposed();
		ArgumentNullException.ThrowIfNull(session);

		if (_agent.IsStreaming)
		{
			throw new InvalidOperationException("Cannot switch sessions while streaming");
		}

		Session = session;
		_agent.SetMessages(Session.BuildProjectedMessages());

		// 恢复会话记录的思考级别（模型仍由宿主当前选择决定）。
		if (Session.Entries.Count > 0)
		{
			var recorded = Session.BuildSessionContext().ThinkingLevel;
			if (recorded is { Length: > 0 })
			{
				_agent.SetThinkingLevel(ThinkingLevelResolver.Clamp(_agent.Model, recorded));
			}
		}

		PublishTodoWidget(CurrentTodo());
	}

	/// 在当前会话的 <paramref name="entryId"/> 处派生，并切换到新的会话文件。
	/// <paramref name="position"/> 取 <c>before</c>（在用户消息之前派生，RPC 的默认值）或 <c>at</c>
	/// （保留该条目本身，也就是 <see cref="CloneAsync"/> 的行为）。
	/// <returns>被派生那条用户消息的文本；在条目处派生时返回 <c>null</c>。</returns>
	public string? ForkAt(string entryId, string position = "before")
	{
		ThrowIfDisposed();

		if (_agent.IsStreaming)
		{
			throw new InvalidOperationException("Cannot fork while streaming");
		}

		var selected = Session.GetEntry(entryId)
			?? throw new InvalidOperationException("Invalid entry ID for forking");

		string? targetLeafId;
		string? selectedText = null;

		if (position == "at")
		{
			targetLeafId = selected.Id;
		}
		else
		{
			if (selected is not SessionMessageEntry { Message: UserMessage user })
			{
				throw new InvalidOperationException("Invalid entry ID for forking");
			}

			targetLeafId = selected.ParentId;
			selectedText = user.Content.Text ?? string.Empty;
		}

		if (!Session.IsPersisted)
		{
			// 内存会话无法再产生第二个文件，改为移动叶子。
			if (targetLeafId is null)
			{
				Session.StartNew();
			}
			else
			{
				Session.Branch(targetLeafId);
			}

			_agent.SetMessages(Session.BuildProjectedMessages());
			PublishTodoWidget(CurrentTodo());
			return selectedText;
		}

		var sourceFile = Session.SessionFile;
		if (targetLeafId is null)
		{
			Session.StartNew(parentSession: sourceFile);
		}
		else
		{
			Session.CreateBranchedSession(targetLeafId);
		}

		_agent.SetMessages(Session.BuildProjectedMessages());
		PublishTodoWidget(CurrentTodo());
		return selectedText;
	}

	/// 「编辑重发」：在当前会话文件里把 <paramref name="entryId"/> 及其全部后继改成墓碑，
	/// 再把代理消息重置为截断后的投影。会话 id 与文件不变，侧栏不会多出一条会话；
	/// 旧回答的工具调用链也不会作为平行分支残留。
	/// <returns>被截断的条目数。</returns>
	public int RewindForResend(string entryId)
	{
		ThrowIfDisposed();

		if (_agent.IsStreaming)
		{
			throw new InvalidOperationException("Cannot rewind while streaming");
		}

		var truncated = Session.TruncateForResend(entryId);
		_agent.SetMessages(Session.BuildProjectedMessages());
		PublishTodoWidget(CurrentTodo());
		return truncated;
	}

	public void CloneAsync()
	{
		ThrowIfDisposed();
		var leaf = Session.LeafId ?? throw new InvalidOperationException("Cannot clone session: no current entry selected");
		ForkAt(leaf, "at");
	}

	public Task<BranchSummaryResult?> SummarizeBranchAsync(
		string targetId,
		string? customInstructions = null,
		CancellationToken cancellationToken = default)
	{
		ThrowIfDisposed();
		return BranchSummarizer.SummarizeBranchAsync(
			Session,
			_agent.Model,
			_directStreamFn,
			_streamOptions,
			Settings.Compaction.ForModel(_agent.Model),
			Settings.BranchSummary,
			targetId,
			customInstructions,
			cancellationToken,
			thinkingLevel: _agent.ThinkingLevel);
	}

	public (long Tokens, int? LastUsageIndex) ContextTokens()
	{
		lock (_statisticsGate)
		{
			var messages = Session.BuildProjectedMessages();
			var anchorValid = UsageAnchorValid(Session.Revision);
			if (!ReferenceEquals(messages, _contextMessages) || anchorValid != _contextAnchorValid)
			{
				_contextTokens = EstimateContextIncrementally(messages, anchorValid);
				_contextMessages = messages;
				_contextAnchorValid = anchorValid;
			}
			return _contextTokens;
		}
	}

	/// <summary>
	/// 与 pi 的 estimateProjectedContextTokens 一致：最后一次 usage 之后如果发生过
	/// compaction / context_edit，那条 usage 的 input 已不能代表当前投影，退回全量估算。
	/// 否则压缩完的大会话仍会显示/判断成 600K+，小窗口模型会反复触发压缩。
	/// </summary>
	private bool UsageAnchorValid(long revision)
	{
		if (_contextAnchorRevision == revision)
		{
			return _contextAnchorValid;
		}

		_contextAnchorRevision = revision;
		var branch = Session.GetBranch();
		var lastInvalidating = -1;
		var lastUsage = -1;
		for (var index = 0; index < branch.Count; index++)
		{
			switch (branch[index])
			{
				case CompactionEntry or ContextEditEntry:
					lastInvalidating = index;
					break;
				case SessionMessageEntry { Message: AssistantMessage assistant } when HasUsableUsage(assistant):
					lastUsage = index;
					break;
			}
		}

		_contextAnchorValid = lastUsage > lastInvalidating;
		return _contextAnchorValid;
	}

	private static bool HasUsableUsage(AssistantMessage message) =>
		message.StopReason is not (StopReason.Aborted or StopReason.Error) &&
		Compaction.ContextTokens.CalculateContextTokens(message.Usage) > 0;

	/// 与 <see cref="Compaction.ContextTokens.EstimateContextTokens"/> 同语义，但按消息引用复用已估算的
	/// 前缀：追加一条消息只重新估算那一条，不重算整段历史。26,983 条的真实会话里，这一步从
	/// 约 124 ms（UI 线程）降到不足 1 ms；分支导航或上下文编辑改写了前缀时，退回到第一个不一致处重算。
	private (long Tokens, int? LastUsageIndex) EstimateContextIncrementally(
		IReadOnlyList<Message> messages,
		bool useUsageAnchor)
	{
		var reusable = 0;
		if (ReferenceEquals(_estimateSource, messages))
		{
			reusable = Math.Min(_estimateCount, messages.Count);
		}
		else if (_estimateSource is { } previous)
		{
			var limit = Math.Min(Math.Min(previous.Count, messages.Count), _estimateCount);
			while (reusable < limit && ReferenceEquals(previous[reusable], messages[reusable])) reusable++;
		}

		if (_estimateValues.Length < messages.Count)
		{
			// 追加式增长：按倍增扩容，避免每条新消息都重分配整段数组。
			Array.Resize(ref _estimateValues, Math.Max(messages.Count, _estimateValues.Length * 2 + 64));
		}

		for (var i = reusable; i < messages.Count; i++)
		{
			_estimateValues[i] = Compaction.ContextTokens.EstimateTokens(messages[i]);
		}

		_estimateSource = messages;
		_estimateCount = messages.Count;

		long total = 0;
		for (var i = 0; i < messages.Count; i++)
		{
			total += _estimateValues[i];
		}

		_estimateTotal = total;

		if (!useUsageAnchor)
		{
			return (total, null);
		}

		var anchor = Compaction.ContextTokens.LastAssistantUsage(messages);
		if (anchor is null)
		{
			return (total, null);
		}

		// 有可用用量时：锚点及其之前按真实用量，之后按估算累加。
		long before = 0;
		for (var i = 0; i <= anchor.Value.Index; i++)
		{
			before += _estimateValues[i];
		}

		return (Compaction.ContextTokens.CalculateContextTokens(anchor.Value.Usage) + (total - before), anchor.Value.Index);
	}

	public bool ShouldCompact() =>
		Compaction.ContextTokens.ShouldCompact(ContextTokens().Tokens, _agent.Model.ContextWindow, Settings.Compaction.ForModel(_agent.Model));

	/// 摘要较旧的条目并记录一条 <c>compaction</c> 条目。没有可压缩内容时返回 <c>null</c>。
	/// 完成后会从会话刷新代理的会话记录。
	/// <param name="reason">压缩原因（<c>manual</c> / <c>threshold</c> / <c>overflow</c>），随事件发给客户端。</param>
	public async Task<CompactionResult?> CompactAsync(
		string? customInstructions = null,
		CancellationToken cancellationToken = default,
		string reason = CompactionReasons.Manual,
		bool willRetry = false)
	{
		ThrowIfDisposed();
		if (_agent.IsStreaming)
		{
			throw new InvalidOperationException("Cannot compact while the session is streaming");
		}

		_cacheWarmer?.Cancel();
		IsCompacting = true;
		Emit(new CompactionStartEvent { Reason = reason });
		CompactionResult? result;
		try
		{
			result = await RunSummarizationWithRetryAsync(
				() => Compactor.CompactAsync(
					Session,
					_agent.Model,
					_directStreamFn,
					_streamOptions,
					Settings.Compaction.ForModel(_agent.Model),
					customInstructions,
					cancellationToken,
					thinkingLevel: _agent.ThinkingLevel),
				"compaction",
				reason,
				cancellationToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			IsCompacting = false;
			Emit(new CompactionEndEvent { Reason = reason, Aborted = true, WillRetry = willRetry });
			throw;
		}
		catch (Exception exception)
		{
			IsCompacting = false;
			Emit(new CompactionEndEvent
			{
				Reason = reason,
				ErrorMessage = exception.Message,
				WillRetry = willRetry,
			});
			throw;
		}
		finally
		{
			IsCompacting = false;
		}

		if (result is not null)
		{
			AppendTodoBriefAfterCompaction();
			_agent.SetMessages(Session.BuildProjectedMessages());
		}

		Emit(new CompactionEndEvent
		{
			Reason = reason,
			Result = result,
			EstimatedTokensAfter = result is null ? null : ContextTokens().Tokens,
			WillRetry = willRetry && result is not null,
		});

		return result;
	}

	public bool IsStreaming => _agent.IsStreaming;

	/// 会话已释放：关联会话的回报/转发不该再拉起它。
	public bool IsDisposed => _disposed;

	public AgentQueueMode SteeringMode
	{
		get => _agent.SteeringMode;
		set => _agent.SteeringMode = value;
	}

	public AgentQueueMode FollowUpMode
	{
		get => _agent.FollowUpMode;
		set => _agent.FollowUpMode = value;
	}

	public bool AutoCompactionEnabled { get; set; } = true;

	public (IReadOnlyList<Message> Steering, IReadOnlyList<Message> FollowUp) ClearQueue()
	{
		var cleared = _agent.ClearQueue();
		EmitQueueUpdate();
		return cleared;
	}

	public (IReadOnlyList<Message> Steering, IReadOnlyList<Message> FollowUp) PendingQueues() => _agent.PendingQueues();

	/// 撤回一条尚未投递的排队消息（界面的「撤回输入框 / 丢弃」）。
	/// 已投递给模型的那条不在队列里，会返回 false。
	public bool RemoveQueued(Message message)
	{
		var removed = _agent.RemoveQueued(message);
		if (removed)
		{
			EmitQueueUpdate();
		}

		return removed;
	}

	private void EmitQueueUpdate()
	{
		var (steering, followUp) = _agent.PendingQueues();
		Emit(new QueueUpdateEvent { Steering = steering, FollowUp = followUp });
	}

	public string? ErrorMessage => _agent.ErrorMessage;

	public IDisposable Subscribe(Func<AgentEvent, ValueTask> listener)
	{
		var subscription = _agent.Subscribe(listener);
		_listeners.Add(listener);

		return new ListenerHandle(subscription, _listeners, listener);
	}

	private void Emit(AgentEvent @event)
	{
		foreach (var listener in _listeners.ToArray())
		{
			listener(@event).AsTask().GetAwaiter().GetResult();
		}
	}

	private sealed class ListenerHandle(
		IDisposable inner,
		List<Func<AgentEvent, ValueTask>> listeners,
		Func<AgentEvent, ValueTask> listener) : IDisposable
	{
		private bool _disposed;

		public void Dispose()
		{
			if (_disposed)
			{
				return;
			}

			_disposed = true;
			inner.Dispose();
			listeners.Remove(listener);
		}
	}

	public void Abort()
	{
		_agent.Abort();
		CancelQuietly(_retryAbort);

		CancellationTokenSource? run;
		lock (_abortGate)
		{
			run = _runAbort;
		}

		// 自动压缩与重试退避期间 agent 本身并未流式运行，只取消 agent 不会停下这些阶段。
		CancelQuietly(run);
	}

	public async Task AbortAsync(CancellationToken cancellationToken = default)
	{
		Abort();
		while (IsRunning && !cancellationToken.IsCancellationRequested)
		{
			await Task.Delay(10, cancellationToken).ConfigureAwait(false);
		}
	}

	public bool IsRunning
	{
		get
		{
			lock (_abortGate)
			{
				return _runAbort is not null;
			}
		}
	}

	private CancellationTokenSource BeginRun(CancellationToken cancellationToken)
	{
		var run = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		lock (_abortGate)
		{
			_runAbort = run;
		}

		return run;
	}

	private void EndRun(CancellationTokenSource run)
	{
		lock (_abortGate)
		{
			if (ReferenceEquals(_runAbort, run))
			{
				_runAbort = null;
			}
		}
	}

	/// 外部调用方的取消令牌被取消时，即使本轮已经落定 aborted 消息并收尾，也把取消继续向上传递：
	/// CLI 需要据此返回 130，桌面会用同一信号清掉发送状态。会话内部的 <see cref="Abort"/>（例如 RPC
	/// 的 abort 命令）不会取消调用方令牌，因此仍按正常收尾返回。
	private static void ThrowIfCallerCancelled(CancellationToken cancellationToken)
	{
		if (cancellationToken.IsCancellationRequested)
		{
			throw new OperationCanceledException(cancellationToken);
		}
	}

	private static void CancelQuietly(CancellationTokenSource? cancellation)
	{
		if (cancellation is null)
		{
			return;
		}

		try
		{
			cancellation.Cancel();
		}
		catch (ObjectDisposedException)
		{
			// 运行刚好收尾并释放了 CTS。
		}
	}

	public SessionStatistics Statistics()
	{
		lock (_statisticsGate) return _statistics.Get(Session);
	}

	public bool PlanModeEnabled { get; private set; }

	public void SetPlanMode(bool enabled)
	{
		if (PlanModeEnabled == enabled)
		{
			return;
		}

		PlanModeEnabled = enabled;
		// 退出计划模式同时结束执行态；草案步骤一并清掉，避免下一轮把旧计划当当前计划。
		_planExecution = false;
		_planSteps.Clear();
		ApplyTools();
		PublishPlanWidget();
		PersistPlanState();
	}

	private void ApplyTools()
	{
		var effective = _fullTools;
		if (PlanModeEnabled)
		{
			effective = [.. _fullTools.Where(static tool => !PlanMode.DisabledTools.Contains(tool.Tool.Name, StringComparer.Ordinal))];
		}

		var loaded = LoadedToolNames();
		var hasDiscovery = effective.Any(static tool => IsDiscoveryExposure(tool.Tool.Exposure));
		var toolSearch = hasDiscovery ? _toolSearch.Value : null;

		// 声明给模型：direct 工具 + tool_search 加载过的非 direct 工具。
		var declared = new List<BuiltinTool>(effective.Count + 1);
		foreach (var tool in effective)
		{
			var exposure = tool.Tool.Exposure;
			if (string.Equals(exposure, ToolExposure.Direct, StringComparison.Ordinal)
				|| string.Equals(exposure, ToolExposure.ModelOnly, StringComparison.Ordinal)
				|| (!string.Equals(exposure, ToolExposure.Hidden, StringComparison.Ordinal) && loaded.Contains(tool.Tool.Name)))
			{
				declared.Add(tool);
			}
		}

		if (toolSearch is not null)
		{
			declared.Add(new BuiltinTool(toolSearch, "Search for tools that are not loaded yet and load the matches", []));
		}

		var codemodeActive = effective.Any(static tool => tool.Tool.Name == CodeModeToolFactory.Name);
		// codemode 的 mode=on：直接声明给模型的工具补上从脚本调用的说明。
		if (string.Equals(Settings.Codemode.Mode, "on", StringComparison.Ordinal) && codemodeActive)
		{
			declared = [.. declared.Select(static tool => tool.Tool.Name is CodeModeToolFactory.Name or ToolSearchName
				|| tool.Tool.Exposure == ToolExposure.ModelOnly
				? tool
				: tool with { Tool = WithCodemodeHint(tool.Tool) })];
		}
		// mode=only：直接工具只给脚本，不再声明给模型（codemode/tool_search 自身除外）。
		else if (string.Equals(Settings.Codemode.Mode, "only", StringComparison.Ordinal) && codemodeActive)
		{
			declared = [.. declared.Where(static tool =>
				!string.Equals(tool.Tool.Exposure, ToolExposure.Direct, StringComparison.Ordinal)
				|| tool.Tool.Name is CodeModeToolFactory.Name or ToolSearchName)];
		}

		// model-only 只声明给模型，不能被 codemode/嵌套调用碰到（与 pi 的 exposure 语义一致）。
		List<AgentTool> callableTools = [.. effective
			.Select(static tool => tool.Tool)
			.Where(static tool => tool.Exposure != ToolExposure.ModelOnly)];
		if (toolSearch is not null)
		{
			callableTools.Add(toolSearch);
		}
		_agent.SetTools(callableTools, declared.Select(static tool => tool.Tool).ToList());
		_promptTools = declared;
		_refreshedSystemPrompt = SystemPrompt.Build(SectionsFor(declared));
	}

	/// 只改模式章节、不动工具集时用：计划执行态与目标态都要让下一轮请求看到新章节。
	private void RefreshSystemPrompt() =>
		_refreshedSystemPrompt = SystemPrompt.Build(SectionsFor(_promptTools));

	private const string ToolSearchName = "tool_search";

	private static bool IsDiscoveryExposure(string exposure) =>
		string.Equals(exposure, ToolExposure.Codemode, StringComparison.Ordinal)
		|| string.Equals(exposure, ToolExposure.Deferred, StringComparison.Ordinal);

	private static AgentQueueMode ParseQueueMode(string? value) =>
		string.Equals(value, "all", StringComparison.OrdinalIgnoreCase)
			? AgentQueueMode.All
			: AgentQueueMode.OneAtATime;

	private static string? CombineAddendum(string? fileText, string? optionText)
	{
		if (string.IsNullOrEmpty(fileText)) return optionText;
		if (string.IsNullOrEmpty(optionText)) return fileText;
		return $"{fileText}\n\n{optionText}";
	}

	private SystemPromptOptions SectionsFor(IReadOnlyList<BuiltinTool> tools)
	{
		IReadOnlyDictionary<string, string?>? overrides = _systemPromptSections.SectionOverrides;
		Dictionary<string, string?>? merged = null;
		Dictionary<string, string?> Merge()
		{
			merged ??= overrides is null
				? new Dictionary<string, string?>(StringComparer.Ordinal)
				: new Dictionary<string, string?>(overrides, StringComparer.Ordinal);
			return merged;
		}

		if (PlanModeEnabled) Merge()["plan_mode"] = PlanMode.Instructions;
		if (PlanExecutionSection() is { } planExecution) Merge()["plan_execution"] = planExecution;
		if (GoalSection() is { } goal) Merge()["goal_mode"] = goal;
		if (BuildMcpServersSection() is { } mcpServers) Merge()["mcp_servers"] = mcpServers;

		return _systemPromptSections with
		{
			Tools = tools,
			SectionOverrides = merged ?? overrides,
		};
	}

	/// 与 pi 的 <c>mcp_servers</c> 章节对齐：列出工具没有直接声明给模型的 MCP 服务器，
	/// 以及该用 codemode 还是 tool_search 去够到它们。
	private string? BuildMcpServersSection()
	{
		var namespaces = new Dictionary<string, McpNamespaceInfo>(StringComparer.Ordinal);
		foreach (var builtin in _fullTools)
		{
			var tool = builtin.Tool;
			if (tool.Namespace is null
				|| string.Equals(tool.Exposure, ToolExposure.Direct, StringComparison.Ordinal)
				|| string.Equals(tool.Exposure, ToolExposure.ModelOnly, StringComparison.Ordinal)
				|| string.Equals(tool.Exposure, ToolExposure.Hidden, StringComparison.Ordinal))
			{
				continue;
			}

			if (PlanModeEnabled && PlanMode.DisabledTools.Contains(tool.Name, StringComparer.Ordinal)) continue;

			var deferred = string.Equals(tool.Exposure, ToolExposure.Deferred, StringComparison.Ordinal);
			namespaces[tool.Namespace] = namespaces.TryGetValue(tool.Namespace, out var info)
				? info with
				{
					Description = info.Description ?? tool.NamespaceDescription,
					Deferred = info.Deferred || deferred,
					Codemode = info.Codemode || !deferred,
				}
				: new McpNamespaceInfo(tool.NamespaceDescription, deferred, !deferred);
		}

		if (namespaces.Count == 0) return null;

		var hasCodemode = namespaces.Values.Any(static info => info.Codemode);
		var hasDeferred = namespaces.Values.Any(static info => info.Deferred);
		var builder = new StringBuilder("MCP servers whose tools are not declared to you.");
		if (hasCodemode) builder.Append(" Call the tools of `codemode` servers from codemode scripts.");
		if (hasDeferred) builder.Append(" Load the tools of `tool_search` servers with `tool_search`.");

		foreach (var (name, info) in namespaces.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
		{
			var mode = info.Codemode && info.Deferred
				? "codemode/tool_search"
				: info.Deferred ? "tool_search" : "codemode";
			builder.Append("\n- `").Append(name).Append("` (").Append(mode).Append(')');
			if (info.Description is { Length: > 0 } description)
			{
				builder.Append(' ').Append(description.Length > 250 ? description[..249] + "…" : description);
			}
		}

		var text = builder.ToString();
		return text.Length > 4096 ? text[..4095] + "…" : text;
	}

	private sealed record McpNamespaceInfo(string? Description, bool Deferred, bool Codemode);

	private async ValueTask<BeforeToolCallResult?> GuardedBeforeToolCallAsync(
		BeforeToolCallContext context,
		CancellationToken cancellationToken)
	{
		if (PlanModeEnabled)
		{
			if (PlanMode.DisabledTools.Contains(context.ToolCall.Name, StringComparer.Ordinal))
			{
				return new BeforeToolCallResult { Block = true, Reason = "plan mode: writing tools are disabled until the plan is approved" };
			}

			if (context.ToolCall.Name == "bash" &&
				context.Arguments["command"]?.GetValue<string>() is { Length: > 0 } command &&
				!PlanMode.IsReadOnlyShellCommand(command))
			{
				return new BeforeToolCallResult { Block = true, Reason = "plan mode: only read-only shell commands are allowed" };
			}
		}

		// 扩展的 tool_call 钩子在安全门之前：扩展可以主动拦下调用（例如禁止某类写），
		// 但不能绕过安全门，被它放行的调用仍要过档位检查。
		if (Extensions is { } extensions && extensions.ListensTo("tool_call"))
		{
			var payload = new System.Text.Json.Nodes.JsonObject
			{
				["toolName"] = context.ToolCall.Name,
				["arguments"] = System.Text.Json.Nodes.JsonNode.Parse(context.Arguments.ToJsonString()),
			};
			// 用返回值：处理器在 JS 侧改的是那一份对象，宿主拿回的是重新解析的副本，
			// 读原 payload 看不到扩展的改写（第一版在这里漏掉了拦截）。
			var outcome = extensions.Dispatch("tool_call", payload);
			if (outcome["block"]?.GetValue<bool>() == true)
			{
				var blockReason = outcome["reason"]?.GetValue<string>();
				return new BeforeToolCallResult { Block = true, Reason = blockReason ?? $"扩展阻止了这次 `{context.ToolCall.Name}` 调用。" };
			}
		}

		// 安全门在用户钩子之前：宿主钩子拿到的是已经过安全判定的调用。
		// 所有工具调用的唯一汇合点：直接调用、codemode 里的嵌套调用、MCP 工具都经过它
		// （见 ExecuteNestedToolAsync），只需在这里拦一次。
		if (await SecurityGateAsync(context, cancellationToken).ConfigureAwait(false) is { } gated)
		{
			return gated;
		}

		if (OperatingSystem.IsWindows() &&
			context.ToolCall.Name == "bash" &&
			context.Arguments["command"]?.GetValue<string>() is { Length: > 0 } nulCommand)
		{
			// Windows Git Bash 不把 `nul` 当空设备；与 PiDeck 的扩展一样在调用前改写。
			var normalized = ShellCommandNormalizer.NormalizeNulRedirects(nulCommand);
			if (!string.Equals(normalized, nulCommand, StringComparison.Ordinal))
			{
				context.Arguments["command"] = normalized;
			}
		}

		if (context.ToolCall.Name is "bash" or "powershell" &&
			context.Arguments["command"]?.GetValue<string>() is { Length: > 0 } deleteCommand)
		{
			// 删除照常发生，但字面量目标先复制进回收站；失败不阻断调用。
			await TrashGuard.BackupAsync(context.ToolCall.Name, deleteCommand, Cwd, cancellationToken).ConfigureAwait(false);
		}

		return _userBeforeToolCall is null
			? null
			: await _userBeforeToolCall(context, cancellationToken).ConfigureAwait(false);
	}

	/// 发现并加载扩展，把注册的工具并进本会话的工具集。
	/// 项目扩展只在受信时加载（扩展能注册工具、改工具调用，不受信等于让仓库在用户机器上执行代码）；
	/// 单个扩展加载失败只影响它自己，会话照常起；一个坏扩展不该让整个工作台打不开。
	private static void ApplyFlagValues(ExtensionHost host, IReadOnlyDictionary<string, string?> values)
	{
		if (values.Count > 0)
		{
			host.ApplyFlagValues(values);
		}
	}

	private void LoadExtensions(CodingAgentSessionOptions options)
	{
		ExtensionHost host;
		if (options.Extensions is { } provided)
		{
			// 宿主注入的扩展宿主由宿主负责释放与重载。
			Extensions = provided;
			_ownsExtensions = false;
			ApplyFlagValues(provided, options.FlagValues);
			host = provided;
		}
		else if (!options.LoadExtensions)
		{
			// 没加载也就没有可重载/释放的东西：注入分支同理。
			_ownsExtensions = false;
			return;
		}
		else
		{
			host = new ExtensionHost(options.UiHost);
			host.Load(
				Cwd,
				options.ProjectTrusted,
				explicitPaths: options.ExtensionPaths,
				includeDiscovered: options.DiscoverExtensions);
			ApplyFlagValues(host, options.FlagValues);
			Extensions = host;
		}

		if (host.ToolRegistrations.Count == 0)
		{
			return;
		}

		// 只并表、不重建提示词：本方法要在 _fullTools 定稿之后、代理建立之前调用；
		// 早了会被覆盖，晚了 ApplyTools 还没有 _agent 可用。
		var taken = new HashSet<string>(StringComparer.Ordinal);
		foreach (var builtin in _builtinTools)
		{
			taken.Add(builtin.Tool.Name);
		}

		var merged = _builtinTools.ToList();
		foreach (var registered in host.ToolRegistrations)
		{
			var tool = registered.Tool;
			// 内置工具优先：扩展不该悄悄顶掉 read/write/bash，那会让行为无法解释。
			if (taken.Add(tool.Name))
			{
				merged.Add(new BuiltinTool(
					tool,
					registered.PromptSnippet ?? string.Empty,
					registered.PromptGuidelines));
			}
		}

		_fullTools = merged;
	}

	/// 工具调用前置钩子的公开缝：计划模式、扩展 tool_call 与安全门都在这里按顺序生效。
	/// 宿主（RPC / 桌面）也可以直接作为 <c>BeforeToolCall</c> 选项注入自己的钩子。
	public ValueTask<BeforeToolCallResult?> InvokeBeforeToolCallAsync(
		BeforeToolCallContext context,
		CancellationToken cancellationToken) =>
		GuardedBeforeToolCallAsync(context, cancellationToken);

	/// 安全门判定：档位按**会话文件路径**解析（<c>sessionLevels</c> 可以逐会话收紧），
	/// 拒绝直接挡下并给出原因；要求确认时通过宿主的确认对话框问用户。
	/// 没有宿主（RPC / print / 守护进程直连）时 ask 退化为拒绝：无审批人在场却放行，
	/// 等于这道门只在有人看着时才关上。
	private async ValueTask<BeforeToolCallResult?> SecurityGateAsync(
		BeforeToolCallContext context,
		CancellationToken cancellationToken)
	{
		var level = Security.Resolve(Session.SessionFile);
		var decision = level.Evaluate(context.ToolCall.Name, context.Arguments, Cwd);
		if (decision.Effect == SecurityEffect.Allow)
		{
			return null;
		}

		if (decision.Effect == SecurityEffect.Deny)
		{
			return new BeforeToolCallResult { Block = true, Reason = decision.Reason };
		}

		var tool = context.ToolCall.Name;
		lock (_securityApprovedTools)
		{
			if (_securityApprovedTools.Contains(tool))
			{
				return null;
			}
		}

		if (UiHost is not { HasUi: true } host)
		{
			return new BeforeToolCallResult
			{
				Block = true,
				Reason = $"{decision.Reason} 当前环境没有可用来确认的界面，已按拒绝处理；需要执行请让用户先切换档位或在界面上确认。",
			};
		}

		var summary = context.Arguments["command"]?.ToString() is { Length: > 0 } command
			? command.Length <= 300 ? command : command[..300] + "…"
			: context.Arguments.ToJsonString();
		var confirmed = await host.ConfirmAsync(
			new UiConfirmRequest(
				"安全确认",
				$"{decision.Reason}\n\n工具：{tool}\n参数：{summary}",
				null),
			cancellationToken).ConfigureAwait(false);

		if (confirmed != true)
		{
			return new BeforeToolCallResult { Block = true, Reason = $"用户拒绝了这次 `{tool}` 调用。" };
		}

		lock (_securityApprovedTools)
		{
			_securityApprovedTools.Add(tool);
		}

		return null;
	}

	private void RebindCodeMode(IReadOnlyList<AgentTool> tools)
	{
		var index = -1;
		for (var i = 0; i < _fullTools.Count; i++)
		{
			if (_fullTools[i].Tool.Name == CodeModeToolFactory.Name)
			{
				index = i;
				break;
			}
		}

		if (index < 0)
		{
			return;
		}

		var rebuilt = _fullTools.ToList();
		rebuilt[index] = CreateCodeModeTool(tools);
		_fullTools = rebuilt;
		ApplyTools();
	}

	private BuiltinTool CreateCodeModeTool(IReadOnlyList<AgentTool> tools) => CodeModeToolFactory.Create(
		new CodeModeHost
		{
			// 执行时用代理当前的工具集（MCP 刷新/计划模式之后仍然正确）。
			Tools = () => _agent?.Tools ?? tools,
			ExecuteTool = ExecuteNestedToolAsync,
			LoadStore = LoadCodeModeStore,
			SaveStore = SaveCodeModeStore,
			Models = QueryCodeModeModels,
			ModelCall = _images is not null || _classifiers is not null ? ExecuteModelCallAsync : null,
			Guidelines = ToolGuidelines,
			// 嵌套调用的钩子上下文复用同一份消息快照，避免每次工具调用拷贝整段会话记录。
			Messages = () => [.. _agent.Messages],
		},
		declaredTools: CodemodeDescribedTools(tools),
		inlineBudget: Settings.Codemode.InlineBudget);

	/// codemode 描述里列出的工具。pi 的 mode=on 只列 <c>codemode</c> exposure 的工具：已经直接
	/// 声明给模型的工具不再重复一遍（它们的描述里会加上怎么从脚本调用），deferred 留给
	/// <c>tool_search</c>。mode=only 额外列出 direct 工具。
	private IReadOnlyList<AgentTool> CodemodeDescribedTools(IReadOnlyList<AgentTool> tools)
	{
		var only = string.Equals(Settings.Codemode.Mode, "only", StringComparison.Ordinal);
		return
		[
			.. tools.Where(tool =>
				!string.Equals(tool.Name, CodeModeToolFactory.Name, StringComparison.Ordinal)
				&& !string.Equals(tool.Name, ToolSearchName, StringComparison.Ordinal)
				&& !string.Equals(tool.Exposure, ToolExposure.Hidden, StringComparison.Ordinal)
				&& (string.Equals(tool.Exposure, ToolExposure.Codemode, StringComparison.Ordinal)
					|| (only && !string.Equals(tool.Exposure, ToolExposure.Deferred, StringComparison.Ordinal)))),
		];
	}

	private HashSet<string> LoadedToolNames()
	{
		var names = new HashSet<string>(StringComparer.Ordinal);
		foreach (var entry in Session.GetBranch())
		{
			if (entry is not CustomEntry { CustomType: ToolLoadEntryType } custom || custom.Data?["tools"] is not JsonArray tools)
			{
				continue;
			}

			foreach (var node in tools)
			{
				if (node?.GetValue<string>() is { Length: > 0 } name)
				{
					names.Add(name);
				}
			}
		}

		return names;
	}

	/// 面向模型的 <c>tool_search</c>：在尚未声明的 <c>codemode</c>/<c>deferred</c> 工具里按 BM25
	/// 排序，把命中的工具写成 <c>tool-load</c> 条目并立即声明（后续回合可直接调用）。
	/// 与 pi 一样，加载会进入会话记录，因此恢复、fork 与切换分支都看得到。
	private AgentTool CreateToolSearchTool() => new()
	{
		Name = ToolSearchName,
		Label = ToolSearchName,
		Description =
			$"# Tool discovery\n\nSearches over deferred tool metadata with BM25 and exposes matching tools for the next model call.\n\n" +
			$"Some of the tools, such as tools of MCP servers, may not have been provided to you upfront, and you should use this tool (`{ToolSearchName}`) to search for the required tools. " +
			$"For MCP tool discovery, always use `{ToolSearchName}`.",
		Parameters = new JsonObject
		{
			["type"] = "object",
			["properties"] = new JsonObject
			{
				["query"] = new JsonObject
				{
					["type"] = "string",
					["description"] = "Search query for deferred tools.",
				},
				["limit"] = new JsonObject
				{
					["type"] = "number",
					["description"] = "Maximum number of tools to return. Defaults to 8.",
				},
			},
			["required"] = new JsonArray("query"),
		},
		Execute = SearchToolsAsync,
	};

	private Task<AgentToolResult> SearchToolsAsync(AgentToolInvocation invocation)
	{
		var query = invocation.Arguments["query"] is JsonValue value && value.TryGetValue<string>(out var text)
			? text.Trim()
			: string.Empty;
		if (query.Length == 0)
		{
			return Task.FromResult(AgentToolResult.Error("tool_search expects a non-empty query"));
		}

		var limit = 8;
		if (invocation.Arguments["limit"] is JsonValue limitValue
			&& (!limitValue.TryGetValue<int>(out limit) || limit <= 0))
		{
			return Task.FromResult(AgentToolResult.Error("tool_search limit must be a positive integer"));
		}

		var declared = _agent.DeclaredTools.Select(static tool => tool.Name).ToHashSet(StringComparer.Ordinal);
		var loaded = LoadedToolNames();
		var candidates = _fullTools
			.Select(static tool => tool.Tool)
			.Where(tool => IsDiscoveryExposure(tool.Exposure)
				&& !declared.Contains(tool.Name)
				&& !loaded.Contains(tool.Name))
			.ToList();
		if (candidates.Count == 0)
		{
			return Task.FromResult(AgentToolResult.Text("No matching tools found."));
		}

		var documents = candidates
			.Select(tool => CodeModeSearch.CreateToolSearchDocument(
				tool.Name,
				tool.Description,
				tool.Parameters,
				namespaceText: string.Join(
					" ",
					new[] { tool.Namespace, tool.NamespaceDescription, tool.NamespaceInstructions }
						.Where(static part => !string.IsNullOrWhiteSpace(part)))))
			.ToList();
		var matches = CodeModeSearch.Rank(query, documents, limit);
		if (matches.Count == 0)
		{
			return Task.FromResult(AgentToolResult.Text("No matching tools found."));
		}

		var names = new JsonArray();
		foreach (var name in matches)
		{
			names.Add((JsonNode?)JsonValue.Create(name));
		}

		Session.AppendCustom(ToolLoadEntryType, new JsonObject { ["tools"] = names });
		// 立即重建 loadout：后续回合的声明与脚本可调用集合都包含这些工具。
		ApplyTools();

		var loadedLines = matches.Select(name =>
		{
			var tool = candidates.First(candidate => string.Equals(candidate.Name, name, StringComparison.Ordinal));
			var firstLine = tool.Description.Trim().Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
			return $"- {name}: {firstLine}";
		});
		return Task.FromResult(AgentToolResult.Text(
			$"Loaded {matches.Count} tool{(matches.Count == 1 ? string.Empty : "s")}. They are available from your next call:\n{string.Join("\n", loadedLines)}",
			new JsonObject { ["loaded"] = new JsonArray([.. matches.Select(static name => (JsonNode?)JsonValue.Create(name))]) }));
	}

	private IReadOnlyList<string> ToolGuidelines(string name)
	{
		foreach (var builtin in _fullTools)
		{
			if (string.Equals(builtin.Tool.Name, name, StringComparison.Ordinal)) return builtin.Guidelines;
		}

		return [];
	}

	private static AgentTool WithCodemodeHint(AgentTool tool)
	{
		// 调用方可能把已经带提示的工具再传回来（MCP 刷新/测试），避免重复追加。
		if (tool.Description.Contains("\n\nCodemode: `tools.", StringComparison.Ordinal)) return tool;

		var output = tool.OutputSchema is null
			? "a string"
			: tool.OutputSchema["anyOf"] is JsonArray variants &&
				variants.Any(static variant => variant is JsonObject obj &&
					obj["type"]?.GetValue<string>() == "string")
				? "a string, or an image object"
				: "an object";
		return tool with
		{
			Description = $"{tool.Description.Trim()}\n\nCodemode: `tools.{CodeModeIdentifier.ToIdentifier(tool.Name)}(args)` resolves to {output}.",
		};
	}

	/// 执行脚本发起的一次嵌套调用。与顶层调用共用同一条管线：参数修正、schema 校验、
	/// 计划模式安全网与宿主钩子都会执行，嵌套结果不进入 LLM 上下文。
	private async Task<AgentToolCallOutcome> ExecuteNestedToolAsync(
		string name,
		JsonObject arguments,
		string toolCallId,
		CancellationToken cancellationToken,
		IReadOnlyList<Message>? messages)
	{
		var tools = _agent.Tools;
		var tool = tools.FirstOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.Ordinal));
		var toolCall = new ToolCall { Id = toolCallId, Name = name, Arguments = arguments };
		if (tool is null)
		{
			return new AgentToolCallOutcome
			{
				ToolCall = toolCall,
				Result = AgentToolResult.Error($"Tool {name} not found"),
				IsError = true,
			};
		}

		var prepared = tool.PrepareArguments is { } prepare ? prepare(arguments) : arguments;
		var validation = JsonSchemaValidator.ValidateToolArguments(tool.ToDeclaration(), prepared);
		if (validation.Error is { } validationError)
		{
			return new AgentToolCallOutcome
			{
				ToolCall = toolCall,
				Result = AgentToolResult.Error(validationError),
				IsError = true,
			};
		}

		var value = validation.Value!;
		toolCall = toolCall with { Arguments = value };
		// 每次 codemode 执行一份消息快照，全部嵌套调用复用；没有会话上下文时才按调用取。
		var snapshot = messages ?? [.. _agent.Messages];
		var context = new AgentContext { Messages = snapshot as List<Message> ?? [.. snapshot], Tools = tools };
		var assistant = snapshot.LastOrDefault(static message => message is AssistantMessage) as AssistantMessage
			?? new AssistantMessage
			{
				Content = [],
				Api = _agent.Model.Api,
				Provider = _agent.Model.Provider,
				Model = _agent.Model.Id,
			};

		var blocked = await GuardedBeforeToolCallAsync(
			new BeforeToolCallContext
			{
				AssistantMessage = assistant,
				ToolCall = toolCall,
				Arguments = value,
				Context = context,
			},
			cancellationToken).ConfigureAwait(false);
		if (blocked?.Block == true)
		{
			return new AgentToolCallOutcome
			{
				ToolCall = toolCall,
				Result = AgentToolResult.Error(blocked.Reason ?? $"Tool call blocked by policy: {name}") with
				{
					Terminate = blocked.Terminate,
				},
				IsError = true,
			};
		}

		var startedAt = Stopwatch.GetTimestamp();
		AgentToolResult result;
		bool isError;
		try
		{
			result = await tool.Execute(new AgentToolInvocation
			{
				ToolCallId = toolCallId,
				ToolName = name,
				Arguments = value,
				CancellationToken = cancellationToken,
			}).ConfigureAwait(false);
			isError = result.IsError;
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			// 取消不是工具失败：让沙箱把这次调用标成 cancelled，不产出 error 结果。
			throw;
		}
		catch (Exception ex)
		{
			result = AgentToolResult.Error(ex.Message);
			isError = true;
		}

		var durationMs = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
		if (_userAfterToolCall is { } after)
		{
			var hook = await after(
				new AfterToolCallContext
				{
					AssistantMessage = assistant,
					ToolCall = toolCall,
					Arguments = value,
					Result = result,
					IsError = isError,
					Context = context,
				},
				cancellationToken).ConfigureAwait(false);
			if (hook is not null)
			{
				result = result with
				{
					Content = hook.Content ?? result.Content,
					Details = hook.HasDetails ? hook.Details : result.Details,
					StructuredContent = hook.HasStructuredContent
						? hook.StructuredContent
						: (hook.Content is not null ? null : result.StructuredContent),
					Usage = hook.Usage ?? result.Usage,
					Terminate = hook.Terminate ?? result.Terminate,
					IsError = hook.IsError ?? result.IsError,
				};
				isError = hook.IsError ?? isError;
			}
		}

		return new AgentToolCallOutcome
		{
			ToolCall = toolCall,
			Result = result,
			IsError = isError,
			DurationMs = durationMs,
		};
	}

	/// 脚本的 <c>models.*</c>：只查询会话的模型目录，不发请求。pi 的 <c>classify</c>/<c>generateImages</c>
	/// 需要网关与凭据，PiWeft 还没把这两个入口接到会话，因此这里只暴露目录查询。
	private JsonNode? QueryCodeModeModels(CodeModeModelsRequest request)
	{
		var type = request.Type switch
		{
			"chat" => ModelType.Chat,
			"image" => ModelType.Image,
			"classifier" => ModelType.Classifier,
			_ => throw new ArgumentException(
				$"Unknown model type \"{request.Type}\". Use \"chat\", \"image\", or \"classifier\"."),
		};

		var registry = _registry ?? FallbackModelRegistry.Value;
		if (request.Operation == CodeModeModelOperation.Get)
		{
			if (request.Provider is null || request.Id is null)
			{
				throw new ArgumentException("models.getModelOfType(type, provider, id) expects three strings");
			}

			var model = registry.Get(request.Provider, request.Id);
			return model is null || model.Type != type ? null : ModelInfo(model);
		}

		IEnumerable<Model> models = registry.List(request.Provider).Where(model => model.Type == type);
		if (request.Operation == CodeModeModelOperation.Available)
		{
			models = models.Where(model => IsProviderAvailable(model.Provider));
		}

		var array = new JsonArray();
		foreach (var model in models) array.Add((JsonNode)ModelInfo(model));
		return array;
	}

	private bool IsProviderAvailable(string provider) =>
		_apiKeyForProvider is null || _apiKeyForProvider(provider) is { Length: > 0 };

	private static JsonObject ModelInfo(Model model)
	{
		var info = JsonSerializer.SerializeToNode(model, PiJsonContext.Default.Model) as JsonObject ?? new JsonObject();
		// 目录里的 headers 可能带凭据，脚本不能看到（与 pi 一样剔除）。
		info.Remove("headers");
		return info;
	}

	/// 执行一次 <c>models.classify()</c> / <c>models.generateImages()</c>。模型由脚本给出
	/// provider + id，凭据与请求头沿用会话的解析结果，脚本无法自带 baseUrl/headers。
	private async Task<CodeModeModelCallResult> ExecuteModelCallAsync(
		CodeModeModelCall call,
		CancellationToken cancellationToken)
	{
		var registry = _registry ?? FallbackModelRegistry.Value;
		var model = registry.Resolve(call.Provider, call.Id)
			?? throw new InvalidOperationException($"Unknown model \"{call.Provider}/{call.Id}\"");
		var options = new ProviderRequestOptions
		{
			ApiKey = _apiKeyForProvider?.Invoke(model.Provider),
			TimeoutMs = Settings.TimeoutMs,
			Headers = _streamOptions.Headers,
			CancellationToken = cancellationToken,
		};

		if (string.Equals(call.Operation, "classify", StringComparison.Ordinal))
		{
			if (model.Type != ModelType.Classifier)
			{
				throw new InvalidOperationException($"\"{call.Provider}/{call.Id}\" is not a classifier model");
			}

			var gateway = _classifiers ?? throw new InvalidOperationException("classifiers are not available in this session");
			var result = await gateway
				.ClassifyAsync(model, ParseClassifierContext(call.Context), options, cancellationToken)
				.ConfigureAwait(false);
			return new CodeModeModelCallResult
			{
				Status = StopStatus(result.StopReason),
				Error = result.ErrorMessage,
				Output = ClassifierJson(result),
				InputTokens = result.Usage?.Input ?? 0,
				OutputTokens = result.Usage?.Output ?? 0,
				TotalTokens = result.Usage?.TotalTokens ?? 0,
				Cost = result.Usage?.Cost.Total ?? 0,
			};
		}

		if (string.Equals(call.Operation, "generateImages", StringComparison.Ordinal))
		{
			if (model.Type != ModelType.Image)
			{
				throw new InvalidOperationException($"\"{call.Provider}/{call.Id}\" is not an image model");
			}

			var gateway = _images ?? throw new InvalidOperationException("image generation is not available in this session");
			var result = await gateway
				.GenerateAsync(model, ParseImagesContext(call.Context), options, cancellationToken)
				.ConfigureAwait(false);
			return new CodeModeModelCallResult
			{
				Status = StopStatus(result.StopReason),
				Error = result.ErrorMessage,
				Output = ImagesJson(result),
				InputTokens = result.Usage.Input,
				OutputTokens = result.Usage.Output,
				TotalTokens = result.Usage.TotalTokens,
				Cost = result.Usage.Cost.Total,
				GeneratedImages = result.Output.OfType<ImageContent>().Count(),
			};
		}

		throw new InvalidOperationException($"Unknown model operation \"{call.Operation}\"");
	}

	private static string StopStatus(StopReason reason) => reason switch
	{
		StopReason.Error => "error",
		StopReason.Aborted => "aborted",
		_ => "stop",
	};

	private static JsonObject ClassifierJson(ClassifierResult result)
	{
		var answers = new JsonObject();
		foreach (var (id, answer) in result.Answers)
		{
			answers[id] = answer switch
			{
				ClassifierChoiceAnswer choice => new JsonObject
				{
					["type"] = "choice",
					["choice"] = choice.Choice,
					["probabilities"] = MapJson(choice.Probabilities),
					["confidence"] = choice.Confidence,
				},
				ClassifierScoreAnswer score => new JsonObject
				{
					["type"] = "score",
					["score"] = score.Score,
					["confidence"] = score.Confidence,
				},
				ClassifierBoolAnswer boolean => new JsonObject
				{
					["type"] = "bool",
					["probability"] = boolean.Probability,
				},
				_ => null,
			};
		}

		return new JsonObject
		{
			["api"] = result.Api,
			["provider"] = result.Provider,
			["model"] = result.Model,
			["answers"] = answers,
			["usage"] = UsageJson(result.Usage),
			["stopReason"] = StopStatus(result.StopReason),
			["errorMessage"] = result.ErrorMessage,
			["timestamp"] = result.Timestamp,
		};
	}

	private static JsonObject ImagesJson(ImageGenerationResult result)
	{
		var output = new JsonArray();
		foreach (var block in result.Output)
		{
			output.Add(block switch
			{
				TextContent text => (JsonNode)new JsonObject { ["type"] = "text", ["text"] = text.Text },
				ImageContent image => new JsonObject
				{
					["type"] = "image",
					["data"] = image.Data,
					["mimeType"] = image.MimeType,
				},
				_ => null,
			});
		}

		return new JsonObject
		{
			["api"] = result.Api,
			["provider"] = result.Provider,
			["model"] = result.Model,
			["output"] = output,
			["usage"] = UsageJson(result.Usage),
			["stopReason"] = StopStatus(result.StopReason),
			["errorMessage"] = result.ErrorMessage,
			["timestamp"] = result.Timestamp,
		};
	}

	private static JsonObject? UsageJson(Usage? usage) => usage is null
		? null
		: new JsonObject
		{
			["input"] = usage.Input,
			["output"] = usage.Output,
			["cacheRead"] = usage.CacheRead,
			["cacheWrite"] = usage.CacheWrite,
			["totalTokens"] = usage.TotalTokens,
			["cost"] = new JsonObject { ["total"] = usage.Cost.Total },
		};

	private static JsonObject MapJson(IReadOnlyDictionary<string, double> values)
	{
		var node = new JsonObject();
		foreach (var (key, value) in values) node[key] = value;
		return node;
	}

	private static ClassifierContext ParseClassifierContext(JsonNode? node)
	{
		if (node is not JsonObject context)
		{
			throw new InvalidOperationException("models.classify() expects a context object as its second argument");
		}

		if (context["state"] is not JsonObject state)
		{
			throw new InvalidOperationException("models.classify() context.state must be an object");
		}

		var images = ParseImages(context["images"], "models.classify() context.images");
		if (context["questions"] is not JsonObject questions || questions.Count == 0)
		{
			throw new InvalidOperationException("models.classify() context.questions must map question IDs to questions");
		}

		var parsed = new Dictionary<string, ClassifierQuestion>(StringComparer.Ordinal);
		foreach (var (id, value) in questions)
		{
			if (value is not JsonObject question)
			{
				throw new InvalidOperationException($"models.classify() context.questions.{id} must be an object");
			}

			var instructions = question["instructions"]?.GetValue<string>() ?? string.Empty;
			var type = question["type"]?.GetValue<string>();
			switch (type)
			{
				case "choice":
				{
					if (question["criteria"] is not JsonObject criteria)
					{
						throw new InvalidOperationException(
							$"models.classify() context.questions.{id} is a \"choice\" question, so criteria must map each label to its meaning");
					}

					var labels = new Dictionary<string, string>(StringComparer.Ordinal);
					foreach (var (label, meaning) in criteria)
					{
						labels[label] = meaning?.GetValue<string>()
							?? throw new InvalidOperationException($"models.classify() context.questions.{id}.criteria.{label} must be a string");
					}

					parsed[id] = new ClassifierChoiceQuestion { Instructions = instructions, Criteria = labels };
					break;
				}

				case "score":
				{
					parsed[id] = new ClassifierScoreQuestion
					{
						Instructions = instructions,
						Criteria = ReadStrings(question["criteria"], $"models.classify() context.questions.{id}.criteria"),
					};
					break;
				}

				case "bool":
				{
					if (question["criteria"] is not JsonObject criteria)
					{
						throw new InvalidOperationException(
							$"models.classify() context.questions.{id} is a \"bool\" question, so criteria must be {{ true: string, false: string }}");
					}

					parsed[id] = new ClassifierBoolQuestion
					{
						Instructions = instructions,
						TrueMeaning = criteria["true"]?.GetValue<string>()
							?? throw new InvalidOperationException($"models.classify() context.questions.{id}.criteria.true must be a string"),
						FalseMeaning = criteria["false"]?.GetValue<string>()
							?? throw new InvalidOperationException($"models.classify() context.questions.{id}.criteria.false must be a string"),
					};
					break;
				}

				default:
					throw new InvalidOperationException(
						$"models.classify() context.questions.{id}.type must be \"choice\", \"score\", or \"bool\"");
			}
		}

		return new ClassifierContext
		{
			State = (JsonObject)state.DeepClone(),
			Images = images.Count > 0 ? images : null,
			Questions = parsed,
		};
	}

	private static ImageGenerationRequest ParseImagesContext(JsonNode? node)
	{
		if (node is not JsonObject context || context["input"] is not JsonArray input || input.Count == 0)
		{
			throw new InvalidOperationException(
				"models.generateImages() expects a context object with a non-empty input array of text/image blocks");
		}

		var blocks = new List<ContentBlock>(input.Count);
		foreach (var item in input)
		{
			if (item is not JsonObject block)
			{
				throw new InvalidOperationException("models.generateImages() context.input items must be text or image blocks");
			}

			switch (block["type"]?.GetValue<string>())
			{
				case "text":
					blocks.Add(new TextContent
					{
						Text = block["text"]?.GetValue<string>()
							?? throw new InvalidOperationException("models.generateImages() text blocks need a string text field"),
					});
					break;
				case "image":
					blocks.Add(new ImageContent
					{
						Data = block["data"]?.GetValue<string>()
							?? throw new InvalidOperationException("models.generateImages() image blocks need a string data field"),
						MimeType = block["mimeType"]?.GetValue<string>()
							?? throw new InvalidOperationException("models.generateImages() image blocks need a string mimeType field"),
					});
					break;
				default:
					throw new InvalidOperationException("models.generateImages() context.input items must be text or image blocks");
			}
		}

		return new ImageGenerationRequest { Input = blocks };
	}

	private static List<ImageContent> ParseImages(JsonNode? node, string label)
	{
		var images = new List<ImageContent>();
		if (node is null) return images;
		if (node is not JsonArray array)
		{
			throw new InvalidOperationException($"{label} must be an array of image blocks");
		}

		foreach (var item in array)
		{
			if (item is not JsonObject image || image["type"]?.GetValue<string>() != "image")
			{
				throw new InvalidOperationException($"{label} items must be image blocks");
			}

			images.Add(new ImageContent
			{
				Data = image["data"]?.GetValue<string>()
					?? throw new InvalidOperationException($"{label} image blocks need a string data field"),
				MimeType = image["mimeType"]?.GetValue<string>()
					?? throw new InvalidOperationException($"{label} image blocks need a string mimeType field"),
			});
		}

		return images;
	}

	private static IReadOnlyList<string> ReadStrings(JsonNode? node, string label)
	{
		if (node is not JsonArray array || array.Count == 0)
		{
			throw new InvalidOperationException($"{label} must be a non-empty array of strings");
		}

		var values = new List<string>(array.Count);
		foreach (var item in array)
		{
			values.Add(item?.GetValue<string>()
				?? throw new InvalidOperationException($"{label} must be a non-empty array of strings"));
		}

		return values;
	}

	private IReadOnlyDictionary<string, JsonNode?> LoadCodeModeStore()
	{
		var store = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
		foreach (var entry in Session.GetBranch())
		{
			if (entry is not CustomEntry { CustomType: CodeModeToolFactory.EntryType } custom || custom.Data is not JsonObject data)
			{
				continue;
			}

			if (data["delete"] is JsonArray deleted)
			{
				foreach (var key in deleted)
				{
					if (key?.GetValue<string>() is { } name) store.Remove(name);
				}
			}

			if (data["set"] is JsonObject set)
			{
				foreach (var (key, value) in set)
				{
					store[key] = value?.DeepClone();
				}
			}
		}

		return store;
	}

	private void SaveCodeModeStore(PiWeft.JavaScript.CodeModeStoreWrites writes)
	{
		var set = new JsonObject();
		foreach (var (key, value) in writes.Set)
		{
			set[key] = value?.DeepClone();
		}

		var delete = new JsonArray();
		foreach (var key in writes.Delete)
		{
			delete.Add((JsonNode?)JsonValue.Create(key));
		}

		Session.AppendCustom(CodeModeToolFactory.EntryType, new JsonObject { ["set"] = set, ["delete"] = delete });
	}

	/// 用本会话的模型、凭据与选项做一次连接测试（只发 1 个输出 token）。
	/// 桌面的「测试连接」与 RPC 的 <c>test_model</c> 共用它。
	public Task<ProviderTestResult> TestConnectionAsync(CancellationToken cancellationToken = default) =>
		ProviderProbe.TestAsync(_directStreamFn, _agent.Model, _streamOptions, cancellationToken);

	public TodoState CurrentTodo()
	{
		lock (_statisticsGate)
		{
			if (_todoRevision != Session.Revision)
			{
				_todo = TodoToolFactory.Current(Session);
				_todoRevision = Session.Revision;
			}

			return _todo;
		}
	}

	public (long Input, long Output, long CacheRead, long CacheWrite) UsageBreakdown()
	{
		var stats = Statistics();
		return (stats.Input, stats.Output, stats.CacheRead, stats.CacheWrite);
	}

	public (long Tokens, double Cost) UsageTotals()
	{
		var stats = Statistics();
		return (stats.Tokens, stats.Cost);
	}

	public async Task<IReadOnlyList<Message>> PromptAsync(string text, CancellationToken cancellationToken = default)
	{
		ThrowIfDisposed();
		// 展开技能命令与提示词模板，让 CLI、RPC、桌面与交互模式共享同一语义。
		// 图片在这之后按当前模型归一化。PrepareInput 保持同步：计划模式与模板展开要立即生效。
		var message = await NormalizeMessageAsync(PrepareInput(text), cancellationToken).ConfigureAwait(false);
		DispatchBeforeAgentStart(message);
		IReadOnlyList<Message> messages;
		using (var run = BeginRun(cancellationToken))
		{
			try
			{
				await AutoCompactAsync(run.Token).ConfigureAwait(false);
				messages = await RunWithRetryAsync(
					// 只有首次才追加用户消息；重试时它已经在投影里，再 PromptAsync 会写第二份进内存与 JSONL。
					attempt => attempt == 0
						? _agent.PromptAsync(message, run.Token)
						: _agent.ContinueAsync(run.Token),
					run.Token).ConfigureAwait(false);
				ThrowIfCallerCancelled(cancellationToken);
			}
			finally
			{
				EndRun(run);
			}
		}

		// 计划确认选在 agent_end 里发生，执行要一轮新请求：本轮完全收尾后再起。
		await RunPendingModeTurnAsync(cancellationToken).ConfigureAwait(false);
		return messages;
	}


	public bool AutoRetryEnabled => _autoRetryOverride ?? Settings.Retry.Enabled;

	public void SetAutoRetryEnabled(bool enabled) => _autoRetryOverride = enabled;

	public int RetryAttempt => _retryAttempt;

	public bool IsRetrying => _retryAbort is not null;

	public void AbortRetry() => CancelQuietly(_retryAbort);

	private async Task<CompactionResult?> RunSummarizationWithRetryAsync(
		Func<Task<CompactionResult?>> run,
		string source,
		string? reason,
		CancellationToken cancellationToken)
	{
		var policy = Settings.Retry;
		var maxAttempts = policy.Enabled ? Math.Max(1, policy.MaxRetries + 1) : 1;
		var retried = false;

		for (var attempt = 1; ; attempt++)
		{
			try
			{
				var result = await run().ConfigureAwait(false);
				if (retried)
				{
					Emit(new SummarizationRetryFinishedEvent());
				}

				return result;
			}
			catch (OperationCanceledException)
			{
				if (retried)
				{
					Emit(new SummarizationRetryFinishedEvent());
				}

				throw;
			}
			catch (Exception exception) when (attempt < maxAttempts)
			{
				retried = true;
				var delay = RetryClassifier.DelayMs(policy, attempt, exception.Message);
				Emit(new SummarizationRetryScheduledEvent
				{
					Attempt = attempt,
					MaxAttempts = maxAttempts,
					DelayMs = delay,
					ErrorMessage = exception.Message,
				});

				if (delay > 0)
				{
					await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
				}

				Emit(new SummarizationRetryAttemptStartEvent { Source = source, Reason = reason });
			}
			catch
			{
				if (retried)
				{
					Emit(new SummarizationRetryFinishedEvent());
				}

				throw;
			}
		}
	}

	/// 虚拟模型的请求入口：先按当前会话选择路由到物理模型，再把事件原样转发。
	/// 助手消息因此记录物理 provider/model，而 <c>model_change</c> 仍保留虚拟模型。
	private AssistantMessageEventStream StreamWithVirtualRouting(
		Model model,
		IReadOnlyList<Message> messages,
		StreamOptions options) =>
		StreamWithVirtualRouting(model, messages, options, direct: false);

	private AssistantMessageEventStream StreamDirectWithVirtualRouting(
		Model model,
		IReadOnlyList<Message> messages,
		StreamOptions options) =>
		StreamWithVirtualRouting(model, messages, options, direct: true);

	private AssistantMessageEventStream StreamWithVirtualRouting(
		Model model,
		IReadOnlyList<Message> messages,
		StreamOptions options,
		bool direct)
	{
		// 会话身份每次请求现取：fork / switch_session 后 Session 已换，不能沿用构造时的 ID。
		// 宿主显式传入的 SessionId 优先（跨会话缓存等场景）。
		options = options with { SessionId = options.SessionId ?? Session.SessionId };
		var definition = VirtualModels.IsVirtual(model) && _registry is not null
			? _registry.Virtual(model.Provider, model.Id)
			: null;
		if (definition is null)
		{
			return _streamFn(model, messages, options);
		}

		var stream = new AssistantMessageEventStream();
		var cancellationToken = options.CancellationToken;
		_ = Task.Run(async () =>
		{
			try
			{
				var previous = FindPreviousPhysicalResponse(messages);
				var failed = direct ? null : _routeFailed;
				var state = direct ? null : ReadVirtualModelState(model.Provider, model.Id);
				var route = await VirtualModels.RouteAsync(
					definition,
					new VirtualModelRequest
					{
						Model = model,
						ThinkingLevel = options.ThinkingLevel ?? _agent?.ThinkingLevel ?? ThinkingLevels.Off,
						Reason = direct
							? VirtualRouteReason.Direct
							: failed is not null ? VirtualRouteReason.Retry : RouteReason(messages),
						Messages = messages,
						Registry = _registry!,
						PreviousModel = previous?.Model,
						PreviousThinkingLevel = previous?.ThinkingLevel,
						Failed = failed,
						State = state,
					},
					cancellationToken).ConfigureAwait(false);

				if (!direct && route.State is not null && !JsonNode.DeepEquals(route.State, state))
				{
					StoreVirtualModelState(model.Provider, model.Id, route.State);
				}

				UiHost?.SetStatus(new UiStatusRequest(
					"piweft:virtual-model",
					$"{model.Provider}/{model.Id} → {route.Model.Provider}/{route.Model.Id}"));

				var routedOptions = options with
				{
					ThinkingLevel = route.ThinkingLevel ?? options.ThinkingLevel,
				};
				if (_apiKeyForProvider?.Invoke(route.Model.Provider) is { Length: > 0 } apiKey)
				{
					routedOptions = routedOptions with { ApiKey = apiKey };
				}

				var inner = _streamFn(route.Model, messages, routedOptions);
				await foreach (var @event in inner.WithCancellation(cancellationToken).ConfigureAwait(false))
				{
					stream.Push(@event switch
					{
						DoneEvent done => done with { Partial = done.Partial with { ThinkingLevel = route.ThinkingLevel } },
						ErrorEvent error => error with { Partial = error.Partial with { ThinkingLevel = route.ThinkingLevel } },
						_ => @event,
					});
				}

				if (!stream.IsDone)
				{
					var result = await inner.Result.ConfigureAwait(false);
					stream.End(result with { ThinkingLevel = route.ThinkingLevel });
				}
			}
			catch (OperationCanceledException)
			{
				PushRouteFailure(stream, model, "Request aborted", StopReason.Aborted);
			}
			catch (Exception exception)
			{
				PushRouteFailure(stream, model, exception.Message, StopReason.Error);
			}
		}, CancellationToken.None);

		return stream;
	}

	private JsonNode? ReadVirtualModelState(string provider, string modelId)
	{
		foreach (var entry in Session.GetBranch())
		{
			if (entry is not CustomEntry { CustomType: VirtualModels.StateEntryType, Data: JsonObject data }
				|| data["provider"]?.GetValue<string>() != provider
				|| data["modelId"]?.GetValue<string>() != modelId)
			{
				continue;
			}

			return data["state"]?.DeepClone();
		}

		return null;
	}

	private void StoreVirtualModelState(string provider, string modelId, JsonNode state)
	{
		var id = Session.AppendCustom(VirtualModels.StateEntryType, new JsonObject
		{
			["provider"] = provider,
			["modelId"] = modelId,
			["state"] = state.DeepClone(),
		});
		EmitEntryAppended(id);
	}

	private (Model Model, string? ThinkingLevel)? FindPreviousPhysicalResponse(IReadOnlyList<Message> messages)
	{
		for (var index = messages.Count - 1; index >= 0; index--)
		{
			if (messages[index] is not AssistantMessage assistant ||
				assistant.StopReason is StopReason.Error or StopReason.Aborted ||
				_registry?.Get(assistant.Provider, assistant.Model) is not { } physical ||
				VirtualModels.IsVirtual(physical))
			{
				continue;
			}

			return (physical, assistant.ThinkingLevel);
		}

		return null;
	}

	private static VirtualRouteReason RouteReason(IReadOnlyList<Message> messages)
	{
		for (var index = messages.Count - 1; index >= 0; index--)
		{
			if (messages[index] is AssistantMessage)
			{
				break;
			}

			if (messages[index] is UserMessage)
			{
				return VirtualRouteReason.User;
			}
		}

		return VirtualRouteReason.Continuation;
	}

	private static void PushRouteFailure(
		AssistantMessageEventStream stream,
		Model model,
		string message,
		StopReason reason)
	{
		var error = new AssistantMessage
		{
			Provider = model.Provider,
			Model = model.Id,
			Api = model.Api,
			Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
			StopReason = reason,
			ErrorMessage = message,
		};
		stream.Push(new ErrorEvent { Reason = reason, Partial = error });
	}

	/// 低频扩展事件。流式 update 不在每帧派发：Jint 处理器是同步的，逐 delta 回调会把流式渲染拖慢。
	private ValueTask ExtensionLifecycleAsync(AgentEvent @event)
	{
		if (Extensions is null)
		{
			return ValueTask.CompletedTask;
		}

		switch (@event)
		{
			case AgentStartEvent:
				DispatchExtension("agent_start", new JsonObject());
				break;
			case AgentEndEvent end:
				DispatchAgentEnd(end);
				break;
			case TurnStartEvent:
				DispatchExtension("turn_start", new JsonObject());
				break;
			case TurnEndEvent turn:
				DispatchExtension("turn_end", new JsonObject
				{
					["message"] = JsonNode.Parse(PiJson.SerializeMessage(turn.Message)),
					["toolResults"] = new JsonArray([.. turn.ToolResults.Select(static result =>
						JsonNode.Parse(PiJson.SerializeMessage(result)))]),
				});
				break;
			case MessageStartEvent start:
				DispatchExtension("message_start", new JsonObject
				{
					["message"] = JsonNode.Parse(PiJson.SerializeMessage(start.Message)),
				});
				break;
			case MessageEndEvent end:
				DispatchExtension("message_end", new JsonObject
				{
					["message"] = JsonNode.Parse(PiJson.SerializeMessage(end.Message)),
				});
				break;
			case ToolExecutionStartEvent start:
				DispatchExtension("tool_execution_start", new JsonObject
				{
					["toolCallId"] = start.ToolCallId,
					["toolName"] = start.ToolName,
					["input"] = start.Arguments.DeepClone(),
				});
				break;
			case ToolExecutionEndEvent end:
				DispatchExtension("tool_execution_end", new JsonObject
				{
					["toolCallId"] = end.ToolCallId,
					["toolName"] = end.ToolName,
					["content"] = PiJson.SerializeBlocks(end.Result.Content),
					["isError"] = end.IsError,
					["durationMs"] = end.DurationMs,
				});
				break;
		}

		return ValueTask.CompletedTask;
	}

	private void DispatchExtension(string @event, JsonObject payload)
	{
		if (Extensions is { } extensions && extensions.ListensTo(@event))
		{
			extensions.Dispatch(@event, payload);
		}
	}

	private void DispatchSessionLifecycle(string @event)
	{
		DispatchExtension(@event, new JsonObject
		{
			["sessionId"] = Session.SessionId,
			["sessionFile"] = Session.SessionFile,
		});
	}

	private ValueTask CacheWarmLifecycleAsync(AgentEvent @event)
	{
		switch (@event)
		{
			case AgentStartEvent:
				_cacheWarmer?.Cancel();
				break;
			case MessageEndEvent { Message: AssistantMessage }:
				StartCacheWarmer();
				break;
			case AgentEndEvent:
				_cacheWarmer?.AgentSettled();
				break;
		}

		return ValueTask.CompletedTask;
	}

	private void PushCacheWarmingStatus(Compaction.CacheWarmingStatus status)
	{
		var text = status.State switch
		{
			"scheduled" => "缓存已排程",
			"refreshing" => "缓存保温中",
			_ => null,
		};
		UiHost?.SetStatus(new UiStatusRequest("piweft:cache-warming", text));
	}

	private void StartCacheWarmer()
	{
		try
		{
			var messages = Project();
			_cacheWarmer?.Start(_agent.Model, messages, _streamOptions with { ThinkingLevel = _agent.ThinkingLevel });
		}
		catch (Exception)
		{
			// 保温是尽力而为，不影响会话。
		}
	}

	private async ValueTask<AgentTurnUpdate?> PrepareNextTurnAsync(
		AgentTurnContext turn,
		CancellationToken cancellationToken)
	{
		if (!AutoCompactionEnabled || !Settings.Compaction.Enabled || IsCompacting)
		{
			return null;
		}

		var (tokens, _) = ContextTokens();
		if (!Compaction.ContextTokens.ShouldCompact(tokens, _agent.Model.ContextWindow, Settings.Compaction.ForModel(_agent.Model)))
		{
			return null;
		}

		await CompactDuringRunAsync(CompactionReasons.Threshold, cancellationToken).ConfigureAwait(false);
		return new AgentTurnUpdate
		{
			Context = new AgentContext
			{
				Messages = [.. Session.BuildProjectedMessages()],
				Tools = turn.Context.Tools,
			},
		};
	}

	private async Task CompactDuringRunAsync(string reason, CancellationToken cancellationToken)
	{
		_cacheWarmer?.Cancel();
		IsCompacting = true;
		Emit(new CompactionStartEvent { Reason = reason });
		CompactionResult? result;
		try
		{
			result = await RunSummarizationWithRetryAsync(
				() => Compactor.CompactAsync(
					Session,
					_agent.Model,
					_directStreamFn,
					_streamOptions,
					Settings.Compaction.ForModel(_agent.Model),
					customInstructions: null,
					cancellationToken,
					thinkingLevel: _agent.ThinkingLevel),
				"compaction",
				reason,
				cancellationToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			IsCompacting = false;
			Emit(new CompactionEndEvent { Reason = reason, Aborted = true });
			throw;
		}
		catch (Exception exception)
		{
			IsCompacting = false;
			Emit(new CompactionEndEvent { Reason = reason, ErrorMessage = exception.Message });
			throw;
		}
		finally
		{
			IsCompacting = false;
		}

		if (result is not null)
		{
			AppendTodoBriefAfterCompaction();
		}

		Emit(new CompactionEndEvent
		{
			Reason = reason,
			Result = result,
			EstimatedTokensAfter = result is null ? null : ContextTokens().Tokens,
		});
	}

	/// 跑一次代理，并在「可重试的失败」上按退避策略重试。与 pi 一致：
	/// 失败的助手回复保留在会话文件里，但通过 <c>context_edit</c> 从模型投影中移除，
	/// 这样重试请求不会带上那次失败；重试本身不重新提交用户消息。
	/// <param name="run">接收「本次调用内的第几次尝试」（0 表示首次），跑一轮代理。</param>
	private async Task<IReadOnlyList<Message>> RunWithRetryAsync(
		Func<int, Task<IReadOnlyList<Message>>> run,
		CancellationToken cancellationToken)
	{
		var attempt = 0;
		AssistantMessage? routeFailed = null;
		try
		{
			for (;;)
			{
				_routeFailed = attempt == 0 ? null : routeFailed;
				var messages = await run(attempt).ConfigureAwait(false);
				var last = LastAssistantMessage(messages);
				var policy = Settings.Retry;

				if (last is null || last.StopReason != StopReason.Error)
				{
					FinishRetry(success: true, finalError: null);
					return messages;
				}

				routeFailed = last;

				if (!_overflowRecoveryAttempted && ContextOverflow.IsOverflow(last))
				{
					_overflowRecoveryAttempted = true;
					OmitFromContext(last);
					try
					{
						var compacted = await CompactAsync(
							customInstructions: null,
							cancellationToken,
							CompactionReasons.Overflow,
							willRetry: true).ConfigureAwait(false);
						if (compacted is null)
						{
							FinishRetry(success: false, "Context overflow recovery could not compact the conversation.");
							return messages;
						}
					}
					catch (Exception exception) when (exception is not OperationCanceledException)
					{
						FinishRetry(success: false, $"Context overflow recovery failed: {exception.Message}");
						return messages;
					}

					attempt++;
					continue;
				}

				var maxRetries = Math.Max(0, policy.MaxRetries);
				if (!AutoRetryEnabled || _retryAttempt >= maxRetries || !RetryClassifier.IsRetryable(last))
				{
					FinishRetry(success: false, last.ErrorMessage);
					return messages;
				}

				_retryAttempt++;
				var delayMs = RetryClassifier.DelayMs(policy, _retryAttempt, last.ErrorMessage);

				Emit(new AutoRetryStartEvent
				{
					Attempt = _retryAttempt,
					MaxAttempts = maxRetries,
					DelayMs = delayMs,
					ErrorMessage = last.ErrorMessage ?? "Unknown error",
				});

				// 把失败的那次尝试从投影里去掉（文件里保留）。
				OmitFromContext(last);

				using var abort = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
				_retryAbort = abort;
				try
				{
					await Task.Delay(delayMs, abort.Token).ConfigureAwait(false);
				}
				catch (OperationCanceledException)
				{
					_retryAbort = null;
					FinishRetry(success: false, "Aborted");
					return messages;
				}
				finally
				{
					_retryAbort = null;
				}

				// 代理自身的消息列表里还留着那次失败，重置为当前投影（已排除它）再继续。
				_agent.SetMessages(Project());

				// 下一次尝试走"继续"分支：用户消息已经在投影里了。
				attempt++;
			}
		}
		finally
		{
			_routeFailed = null;
		}
	}

	private void FinishRetry(bool success, string? finalError)
	{
		if (_retryAttempt == 0)
		{
			return;
		}

		Emit(new AutoRetryEndEvent
		{
			Success = success,
			Attempt = _retryAttempt,
			FinalError = success ? null : finalError,
		});

		_retryAttempt = 0;
	}

	private bool ShouldRetryAssistant(AssistantMessage message)
	{
		if (message.StopReason != StopReason.Error)
		{
			return false;
		}

		if (!_overflowRecoveryAttempted && ContextOverflow.IsOverflow(message))
		{
			return true;
		}

		var policy = Settings.Retry;
		return AutoRetryEnabled &&
			_retryAttempt < Math.Max(0, policy.MaxRetries) &&
			RetryClassifier.IsRetryable(message);
	}

	private static AssistantMessage? LastAssistantMessage(IReadOnlyList<Message> messages)
	{
		for (var index = messages.Count - 1; index >= 0; index--)
		{
			if (messages[index] is AssistantMessage assistant)
			{
				return assistant;
			}
		}

		return null;
	}

	private void OmitFromContext(Message message)
	{
		foreach (var entry in Session.Entries.OfType<SessionMessageEntry>())
		{
			if (ReferenceEquals(entry.Message, message))
			{
				Session.AppendContextEdit(entry.Id, replacement: null);
				return;
			}
		}
	}

	private async Task AutoCompactAsync(CancellationToken cancellationToken)
	{
		if (!AutoCompactionEnabled || !Settings.Compaction.Enabled || _agent.IsStreaming)
		{
			return;
		}

		if (ShouldCompact())
		{
			await CompactAsync(
				customInstructions: null,
				cancellationToken,
				CompactionReasons.Threshold).ConfigureAwait(false);
		}
	}

	public async Task<IReadOnlyList<Message>> PromptAsync(Message message, CancellationToken cancellationToken = default)
	{
		ThrowIfDisposed();
		// 用户消息发送前才归一化图片：桌面端的 PrepareInput 在 UI 线程上，不能在那里解码大图。
		message = await NormalizeMessageAsync(message, cancellationToken).ConfigureAwait(false);
		DispatchBeforeAgentStart(message);
		_cacheWarmer?.Cancel();
		_overflowRecoveryAttempted = false;
		IReadOnlyList<Message> messages;
		using (var run = BeginRun(cancellationToken))
		{
			try
			{
				// 与字符串重载一致：先自动压缩，再按重试策略运行；重试走「继续」，不重新提问。
				await AutoCompactAsync(run.Token).ConfigureAwait(false);
				messages = await RunWithRetryAsync(
					attempt => attempt == 0
						? _agent.PromptAsync(message, run.Token)
						: _agent.ContinueAsync(run.Token),
					run.Token).ConfigureAwait(false);
				ThrowIfCallerCancelled(cancellationToken);
			}
			finally
			{
				EndRun(run);
			}
		}

		await RunPendingModeTurnAsync(cancellationToken).ConfigureAwait(false);
		return messages;
	}

	public async Task<IReadOnlyList<Message>> ContinueAsync(CancellationToken cancellationToken = default)
	{
		ThrowIfDisposed();
		_cacheWarmer?.Cancel();
		IReadOnlyList<Message> messages;
		using (var run = BeginRun(cancellationToken))
		{
			try
			{
				await AutoCompactAsync(run.Token).ConfigureAwait(false);
				messages = await RunWithRetryAsync(
					_ => _agent.ContinueAsync(run.Token),
					run.Token).ConfigureAwait(false);
				ThrowIfCallerCancelled(cancellationToken);
			}
			finally
			{
				EndRun(run);
			}
		}

		await RunPendingModeTurnAsync(cancellationToken).ConfigureAwait(false);
		return messages;
	}

	/// 外部注入的一轮（关联会话回报或跨会话消息）：运行中排 follow-up，模式续轮排到队列尾，
	/// 空闲时立即起一轮。回报不能被并行的 PromptAsync 抢跑，所以与目标/计划共用同一份续轮队列。
	public async Task DeliverMessageAsync(UserMessage message, CancellationToken cancellationToken = default)
	{
		ThrowIfDisposed();
		if (IsRunning)
		{
			FollowUp(message);
			return;
		}

		if (_drainingModeTurn)
		{
			// 续轮循环马上要重建投影并 ContinueAsync：把消息写进分支，下一轮自然带上。
			Session.AppendMessage(message);
			_pendingModeTurn = true;
			return;
		}

		await PromptAsync(message, cancellationToken).ConfigureAwait(false);
	}

	public void Steer(Message message)
	{
		_agent.Steer(message);
		EmitQueueUpdate();
	}

	public void FollowUp(Message message)
	{
		_agent.FollowUp(message);
		EmitQueueUpdate();
	}

	public void SetModel(Model model)
	{
		_cacheWarmer?.Cancel();
		ArgumentNullException.ThrowIfNull(model);
		if (model.Id == _agent.Model.Id && model.Provider == _agent.Model.Provider)
		{
			return;
		}

		_agent.SetModel(model);
		DispatchExtension("model_select", new JsonObject
		{
			["model"] = PiJson.SerializeModelNode(model),
		});
		Session.AppendModelChange(model.Provider, model.Id);
	}

	public void SetThinkingLevel(string level)
	{
		var clamped = ThinkingLevelResolver.Clamp(_agent.Model, level);
		if (clamped == _agent.ThinkingLevel)
		{
			return;
		}

		_agent.SetThinkingLevel(clamped);
		Session.AppendThinkingLevelChange(clamped);
		Emit(new ThinkingLevelChangedEvent { Level = clamped });
		DispatchExtension("thinking_level_select", new JsonObject { ["level"] = clamped });
	}

	public sealed record BashRunResult
	{
		public required string Output { get; init; }

		public int? ExitCode { get; init; }

		public bool Cancelled { get; init; }

		public bool Truncated { get; init; }

		public string? FullOutputPath { get; init; }
	}

	/// 立即执行一条 shell 命令，并记为 <c>bashExecution</c> 消息，下一次提示能看到它；
	/// 除非设置了 <paramref name="excludeFromContext"/>。交互模式的 <c>!</c> 命令与 RPC 的 <c>bash</c>
	/// 命令都由它支撑。
	public async Task<BashRunResult> RunBashAsync(
		string command,
		bool excludeFromContext = false,
		Action<string>? onOutput = null,
		CancellationToken cancellationToken = default)
	{
		ThrowIfDisposed();

		var result = await ShellRunner
			.RunAsync(
				command,
				Cwd,
				timeoutSeconds: null,
				onOutput,
				cancellationToken,
				new ShellInvocation
				{
					ShellPath = Settings.ShellPath,
					CommandPrefix = Settings.ShellCommandPrefix,
					// pi 的 bash-executor 对用户 `!` 命令去掉 CR；工具输出仍保留原始 CR。
					StripCarriageReturns = true,
				})
			.ConfigureAwait(false);

		var truncation = Truncate.Tail(result.Output);
		if (result.OutputTruncated)
		{
			truncation = truncation with { TotalLines = result.TotalLines, TotalBytes = result.TotalBytes };
		}

		var fullOutputPath = truncation.Truncated
			? result.FullOutputPath ?? ShellRunner.WriteFullOutput(result.Output)
			: null;

		Session.AppendMessage(CodingMessages.BashExecution(
			command,
			truncation.Content,
			result.ExitCode,
			result.Cancelled,
			truncation.Truncated,
			fullOutputPath,
			excludeFromContext,
			DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));

		if (!_agent.IsStreaming)
		{
			_agent.SetMessages(Session.BuildProjectedMessages());
		}

		return new BashRunResult
		{
			Output = truncation.Content,
			ExitCode = result.ExitCode,
			Cancelled = result.Cancelled,
			Truncated = truncation.Truncated,
			FullOutputPath = fullOutputPath,
		};
	}

	public string AppendCustomMessage(string customType, string content, bool display = true)
	{
		var id = Session.AppendCustomMessage(customType, MessageContent.FromText(content), display);
		EmitEntryAppended(id);
		return id;
	}

	public void SetSessionName(string? name)
	{
		Session.AppendSessionInfo(name);
		Emit(new SessionInfoChangedEvent { Name = name });
		DispatchExtension("session_info_changed", new JsonObject { ["name"] = name });
	}

	private void EmitEntryAppended(string entryId)
	{
		if (Session.GetEntry(entryId) is not { } entry)
		{
			return;
		}

		var json = System.Text.Json.Nodes.JsonNode.Parse(SessionJson.Serialize(entry));
		if (json is System.Text.Json.Nodes.JsonObject obj)
		{
			Emit(new EntryAppendedEvent { Entry = obj });
		}
	}

	public void Dispose()
	{
		_cacheWarmer?.Dispose();
		_cacheWarmer = null;
		if (_disposed)
		{
			return;
		}

		_disposed = true;
		Linked?.Dispose();
		DispatchSessionLifecycle("session_shutdown");
		_persistence.Dispose();
		_cacheWarmLifecycle.Dispose();
		_modeLifecycle.Dispose();
		_extensionLifecycle.Dispose();
		if (_ownsExtensions)
		{
			Extensions?.Dispose();
			Extensions = null;
		}

		GC.SuppressFinalize(this);
	}

	private ValueTask<IReadOnlyList<Message>> TransformContextAsync(
		IReadOnlyList<Message> messages,
		CancellationToken cancellationToken) =>
		ValueTask.FromResult(LimitRequestImages(Project()));

	/// <c>inputLimits.images.maxPerRequest</c>：按从新到旧保留图片，超出的换成文本括注。
	/// 只作用于请求投影，不改会话文件与界面。
	private IReadOnlyList<Message> LimitRequestImages(IReadOnlyList<Message> messages)
	{
		var options = ImageResizeOptions.FromInputLimits(Model.InputLimits);
		if (options.MaxPerRequest == int.MaxValue)
		{
			return messages;
		}

		// 每次请求都要数一遍图片；投影对象在无变化时是同一个实例，按引用缓存计数。
		if (!ReferenceEquals(_imageCountSource, messages))
		{
			var counted = 0;
			foreach (var message in messages)
			{
				counted += message switch
				{
					UserMessage user => user.Content.Blocks?.Count(static block => block is ImageContent) ?? 0,
					ToolResultMessage toolResult => toolResult.Content.Count(static block => block is ImageContent),
					CustomMessage custom => custom.Content.Blocks?.Count(static block => block is ImageContent) ?? 0,
					_ => 0,
				};
			}

			_imageCountSource = messages;
			_imageCount = counted;
		}

		if (_imageCount <= options.MaxPerRequest)
		{
			return messages;
		}

		var remaining = options.MaxPerRequest;
		var limitedMessages = new List<Message>(messages.Count);
		for (var index = messages.Count - 1; index >= 0; index--)
		{
			limitedMessages.Add(LimitMessageImages(messages[index], ref remaining, options.MaxPerRequest));
		}

		limitedMessages.Reverse();
		return limitedMessages;
	}

	private static Message LimitMessageImages(Message message, ref int remaining, int limit) => message switch
	{
		UserMessage user when user.Content.Blocks is { Count: > 0 } blocks &&
			LimitBlocks(blocks, ref remaining, limit) is { } limited =>
			user with { Content = MessageContent.FromBlocks(limited) },
		ToolResultMessage result when result.Content.Count > 0 &&
			LimitBlocks(result.Content, ref remaining, limit) is { } limited =>
			result with { Content = limited },
		CustomMessage custom when custom.Content.Blocks is { Count: > 0 } blocks &&
			LimitBlocks(blocks, ref remaining, limit) is { } limited =>
			custom with { Content = MessageContent.FromBlocks(limited) },
		_ => message,
	};

	private static List<ContentBlock>? LimitBlocks(IReadOnlyList<ContentBlock> blocks, ref int remaining, int limit)
	{
		List<ContentBlock>? limited = null;
		for (var index = 0; index < blocks.Count; index++)
		{
			var block = blocks[index];
			if (block is not ImageContent)
			{
				limited?.Add(block);
				continue;
			}

			if (remaining > 0)
			{
				remaining--;
				limited?.Add(block);
				continue;
			}

			limited ??= [.. blocks.Take(index)];
			limited.Add(new TextContent { Text = $"[Image omitted: maxPerRequest={limit}.]" });
		}

		return limited;
	}

	/// 分支投影，外加当前系统提示词。会话运行期间工具集可能变化（例如 MCP 服务器宣告了新工具），
	/// 所以模型看到的提示词每次都按当前状态重建。
	/// 会话文件里不存系统提示（与 pi 一致）：文件里若因历史原因残留了 role:system 的消息，用当前
	/// 提示词替换它；否则插到最前面。压缩产生的 system 消息不在开头，保持原样。
	private IReadOnlyList<Message> Project()
	{
		// The prompt is built once and only replaced when tools/resources rebuild it. Reusing the
		// projected list also keeps the historical Message instances stable for incremental views.
		var prompt = _extensionSystemPrompt ?? (_refreshedSystemPrompt ??= SystemPrompt.Build(SectionsFor(_promptTools)));
		var revision = Session.Revision;
		if (_projectedCache is { } cached &&
			_projectedCacheRevision == revision &&
			ReferenceEquals(_projectedCachePrompt, prompt))
		{
			return cached;
		}

		var projected = Session.BuildProjectedMessages();
		IReadOnlyList<Message> result;
		if (projected.Count == 0)
		{
			result = [prompt];
		}
		else if (projected[0] is SystemMessage existing)
		{
			// 替换章节而不拍平，provider 侧缓存仍能看到相同的分界（preamble、tools、rules 等）。
			result = new List<Message>(projected)
			{
				[0] = existing with
				{
					Sections = prompt.Sections,
					ToolsAdded = prompt.ToolsAdded,
				},
			};
		}
		else
		{
			// The prompt is the only new element; keep the projected list shared instead of
			// copying thousands of message references on every revision.
			result = new PromptedMessages(prompt, projected);
		}

		_projectedCache = result;
		_projectedCacheRevision = revision;
		_projectedCachePrompt = prompt;
		return result;
	}

	/// 替换生效的工具集，例如 MCP 服务器宣告 <c>tools/list_changed</c> 之后。系统提示词会
	/// 用新的工具声明重建，未变动的工具沿用已有的措辞。
	public void SetTools(IReadOnlyList<AgentTool> tools)
	{
		// 没有宿主的会话不保留关联会话工具（工具列表刷新后重新塞回来的也要去掉）。
		if (Linked is null)
		{
			tools = [.. tools.Where(static tool => !LinkedSessionsTool.IsName(tool.Name))];
		}
		else
		{
			tools = [.. tools, .. LinkedSessionsTool.Names
				.Where(name => tools.All(tool => !string.Equals(tool.Name, name, StringComparison.Ordinal)))
				.Select(name => LinkedSessionsTool.Bind(name, () => this).Tool)];
		}

		// 有 exposure: codemode 的工具时自动补上 codemode（pi 的 autoEnableCodemode 行为）。
		if (Settings.Codemode.AutoEnable
			&& tools.Any(static tool => string.Equals(tool.Exposure, ToolExposure.Codemode, StringComparison.Ordinal))
			&& tools.All(static tool => !string.Equals(tool.Name, CodeModeToolFactory.Name, StringComparison.Ordinal)))
		{
			tools = [.. tools, CodeModeToolFactory.CreatePlaceholder().Tool];
		}

		// 同样把占位 todo 工具换成绑定到本会话的版本，保持提示词与执行一致。
		var bound = new List<AgentTool>(tools.Count);
		foreach (var tool in tools)
		{
			bound.Add(tool.Name switch
			{
				TodoToolFactory.Name => TodoToolFactory.Create(() => Session, PublishTodoWidget).Tool,
				AskQuestionToolFactory.Name => AskQuestionToolFactory.Create(() => this).Tool,
				LinkedSessionsTool.StartName or LinkedSessionsTool.SendName or LinkedSessionsTool.ListName =>
					LinkedSessionsTool.Bind(tool.Name, () => this).Tool,
				_ => tool,
			});
		}

		var known = new Dictionary<string, BuiltinTool>(StringComparer.Ordinal);
		foreach (var tool in _fullTools)
		{
			known[tool.Tool.Name] = tool;
		}

		var rebuilt = new List<BuiltinTool>(bound.Count);
		foreach (var tool in bound)
		{
			switch (tool.Name)
			{
				case TodoToolFactory.Name:
					rebuilt.Add(TodoToolFactory.Create(() => Session, PublishTodoWidget));
					continue;
				case AskQuestionToolFactory.Name:
					rebuilt.Add(AskQuestionToolFactory.Create(() => this));
					continue;
				case LinkedSessionsTool.StartName:
				case LinkedSessionsTool.SendName:
				case LinkedSessionsTool.ListName:
					rebuilt.Add(LinkedSessionsTool.Bind(tool.Name, () => this));
					continue;
				case CodeModeToolFactory.Name:
					// 工具集变化后 codemode 的描述也要重新列出可调用工具。
					rebuilt.Add(CreateCodeModeTool(bound));
					continue;
				case "bash":
					// 工具集被 MCP 刷新重建时，bash 也要重新绑定会话上下文（PI_* 注入）。
					rebuilt.Add(known.TryGetValue(tool.Name, out var bashDescribed)
						? bashDescribed with { Tool = PiWeft.CodingAgent.Tools.BashTool.Create(Cwd, BashEnvironment) }
						: new BuiltinTool(tool, tool.Description, []));
					continue;
				case "powershell":
					rebuilt.Add(known.TryGetValue(tool.Name, out var powershellDescribed)
						? powershellDescribed with { Tool = PowerShellTool.Create(Cwd, BashEnvironment) }
						: new BuiltinTool(tool, tool.Description, []));
					continue;
				case "read":
					rebuilt.Add(known.TryGetValue(tool.Name, out var readDescribed)
						? readDescribed with { Tool = FileTools.Read(Cwd, () => _agent?.Model) }
						: new BuiltinTool(tool, tool.Description, []));
					continue;
			}

			rebuilt.Add(known.TryGetValue(tool.Name, out var described)
				? described with { Tool = tool }
				: new BuiltinTool(tool, tool.Description, []));
		}

		_fullTools = rebuilt;
		ApplyTools();
	}

	private void PublishTodoWidget(TodoState state)
	{
		if (UiHost is not { HasUi: true, SupportsPiDeckWidgets: true } host)
		{
			return;
		}

		if (state.ActivePlan is not { } plan)
		{
			if (_todoWidgetPublished)
			{
				host.SetWidget(new UiWidgetRequest(PiDeckProtocol.TodoWidgetKey, null, null));
				_todoWidgetPublished = false;
			}

			return;
		}

		_todoWidgetPublished = true;
		host.SetWidget(new UiWidgetRequest(PiDeckProtocol.TodoWidgetKey, TodoToolFactory.WidgetLines(plan, Session.LeafId), null));
	}

	/// 压缩/分支摘要会冲掉历史里的计划视图；只在需要时补一条不可见 custom message，
	/// 与 PiDeck 的 pi-deck-todo-brief 行为一致。补注后再次压缩前不会重复添加。
	private void AppendTodoBriefAfterCompaction()
	{
		var state = CurrentTodo();
		if (state.ActivePlan is not { } plan || !TodoToolFactory.NeedsBriefAfterCompaction(Session))
		{
			return;
		}

		// PiDeck 同时写一条私有 marker 和一条 display:false 的 custom message；前者供
		// 下一次压缩判定可见性，后者才进入模型上下文。
		Session.AppendCustom(TodoToolFactory.BriefEntryType, new JsonObject { ["reason"] = "post-compaction" });
		Session.AppendCustomMessage(
			TodoToolFactory.BriefEntryType,
			MessageContent.FromText(TodoToolFactory.BriefText(plan)),
			display: false);
		PublishTodoWidget(state);
	}

	private Tools.BashSessionEnvironment? BashEnvironment() => new()
	{
		SessionId = Session.SessionId,
		SessionFile = Session.SessionFile,
		Provider = _agent.Model.Provider,
		ModelId = _agent.Model.Id,
		ThinkingLevel = _agent.ThinkingLevel,
		ShellPath = Settings.ShellPath,
		CommandPrefix = Settings.ShellCommandPrefix,
	};

	private ValueTask PersistAsync(AgentEvent @event)
	{
		if (@event is not MessageEndEvent messageEnd)
		{
			return ValueTask.CompletedTask;
		}

		switch (messageEnd.Message)
		{
			// 系统提示词永不入库：它是运行时构造的，写进去会让真实 pi 把它当成历史消息。
			case AssistantMessage or UserMessage or ToolResultMessage:
				if (!string.Equals(PersistedThinkingLevel(), _agent.ThinkingLevel, StringComparison.Ordinal))
				{
					Session.AppendThinkingLevelChange(_agent.ThinkingLevel);
					_persistedThinkingLevel = _agent.ThinkingLevel;
				}

				Session.AppendMessage(messageEnd.Message);
				break;
		}

		return ValueTask.CompletedTask;
	}

	private bool _persistedThinkingLevelLoaded;

	private string? _persistedThinkingLevel;

	/// 会话文件里最近一次写入的 thinking level。只在第一次需要时重建分支上下文（O(n)），
	/// 之后随写入同步更新，否则每条消息结束都要遍历一遍分支。
	private string? PersistedThinkingLevel()
	{
		if (!_persistedThinkingLevelLoaded)
		{
			_persistedThinkingLevelLoaded = true;

			// 空会话也要走一次 BuildSessionContext：它对空分支返回默认级别（"off"），
			// 缓存成 null 会让首条消息多写一条 thinking_level_change。
			_persistedThinkingLevel = Session.BuildSessionContext().ThinkingLevel;
		}

		return _persistedThinkingLevel;
	}

	private sealed class PromptedMessages(Message prompt, IReadOnlyList<Message> projected) : IReadOnlyList<Message>
	{
		public int Count => projected.Count + 1;

		public Message this[int index] => index == 0 ? prompt : projected[index - 1];

		public IEnumerator<Message> GetEnumerator()
		{
			yield return prompt;
			foreach (var message in projected)
			{
				yield return message;
			}
		}

		System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
	}

	private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
