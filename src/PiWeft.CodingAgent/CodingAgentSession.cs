using System.Text;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiWeft.Agent;
using PiWeft.Ai;
using PiWeft.Ai.Json;
using PiWeft.Ai.Utils;
using PiWeft.CodingAgent.Compaction;
using PiWeft.CodingAgent.Prompts;
using PiWeft.CodingAgent.Resources;
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
	public required string Cwd { get; init; }

	public required Model Model { get; init; }

	/// <summary>流式产出助手回复。接模型网关时用 <c>AgentGatewayStreamFn.For(gateway)</c>。</summary>
	public required StreamFn StreamFn { get; init; }

	/// <summary>会话使用的模型目录；虚拟模型路由需要它解析物理目标。</summary>
	public ModelRegistry? Registry { get; init; }

	/// <summary>为路由后的物理 Provider 解析凭据（来自 models.json 的 key）。</summary>
	public Func<string, string?>? ApiKeyForProvider { get; init; }

	/// <summary>生图网关；codemode 的 <c>models.generateImages()</c> 使用它。</summary>
	public ImagesGateway? Images { get; init; }

	/// <summary>分类网关；codemode 的 <c>models.classify()</c> 使用它。</summary>
	public ClassifiersGateway? Classifiers { get; init; }

	/// <summary>
	/// 思考级别。为 <c>null</c> 时沿用会话里记录的级别（恢复会话时就是上次的），没有记录则为
	/// <see cref="ThinkingLevels.Off"/> —— 与 pi 一致：显式指定优先，其次会话记录，最后默认值。
	/// </summary>
	public string? ThinkingLevel { get; init; }

	/// <summary>覆盖内置工具集。</summary>
	public IReadOnlyList<AgentTool>? Tools { get; init; }

	/// <summary>在内置工具集之上追加的工具，例如 MCP 服务器提供的工具。</summary>
	public IReadOnlyList<AgentTool> ExtraTools { get; init; } = [];

	/// <summary>要恢复的已有会话；不传则新建。</summary>
	public SessionManager? Session { get; init; }

	/// <summary>是否把会话条目写入磁盘；设为 <c>false</c> 即纯内存会话。</summary>
	public bool Persist { get; init; } = true;

	/// <summary>CLI 按 pi 的行为在启动时记录级别；桌面浏览可延迟到开始对话再记录。</summary>
	public bool RecordInitialThinkingLevel { get; init; }

	public CodingAgentSettings Settings { get; init; } = new();

	/// <summary>追加在工具准则之后的准则，默认使用 pi 的基础规则。</summary>
	public IReadOnlyList<string>? ExtraGuidelines { get; init; }

	/// <summary>自定义前缀提示词（<c>--system-prompt</c>），取代默认 preamble。</summary>
	public string? CustomSystemPrompt { get; init; }

	/// <summary>追加到提示词的 <c>addendum</c> 章节（<c>--append-system-prompt</c>）。</summary>
	public string? AppendSystemPrompt { get; init; }

	/// <summary>为 true 时不加载 AGENTS.md / CLAUDE.md 等项目上下文文件。</summary>
	public bool NoContextFiles { get; init; }

	/// <summary>宿主交互界面；为 null 时 <c>ask_question</c> 直接报告不支持。</summary>
	public IUiHost? UiHost { get; init; }

	/// <summary>
	/// 项目是否受信任。为 false 时不加载项目级技能与提示词模板（<c>&lt;cwd&gt;/.pi/skills</c>、
	/// <c>.agents/skills</c> 等），但仍加载用户级目录。上下文文件不受影响（与 pi 一致）。
	/// </summary>
	public bool ProjectTrusted { get; init; } = true;

	/// <summary>每次请求的基础 provider 选项（API key、请求头、超时、采样参数）。</summary>
	public StreamOptions Options { get; init; } = new();

	/// <summary>覆盖技能发现结果；传空列表即不加载任何技能。</summary>
	public IReadOnlyList<Skills.Skill>? Skills { get; init; }

	/// <summary>覆盖提示词模板的发现结果。</summary>
	public IReadOnlyList<PromptTemplate>? PromptTemplates { get; init; }

	/// <summary>预先解析好的资源快照（技能、模板、主题、包）；不传则由会话自行解析。</summary>
	public ResourceSnapshot? Resources { get; init; }

	public Func<BeforeToolCallContext, CancellationToken, ValueTask<BeforeToolCallResult?>>? BeforeToolCall { get; init; }

	public Func<AfterToolCallContext, CancellationToken, ValueTask<AfterToolCallResult?>>? AfterToolCall { get; init; }

	/// <summary>
	/// 扩展的 <c>cache_warming_decision</c> 处理器：返回 <c>warm</c>/<c>stop</c> 覆盖 pi 的决策，
	/// 返回 <c>null</c> 或抛异常时沿用 pi 的决策。
	/// </summary>
	public Func<CacheWarmingDecisionEvent, CancellationToken, Task<string?>>? DecideCacheWarming { get; init; }
}

/// <summary>
/// 一个编码代理会话：分章节的系统提示词、内置工具、仅追加的会话文件与代理循环接在一起。
/// 已落盘的消息才是权威记录，因此每次请求发送的都是会话当前分支的投影。
/// </summary>
public sealed class CodingAgentSession : IDisposable
{
	private readonly AgentRuntime _agent;
	private CacheWarmer? _cacheWarmer;

	/// <summary>系统提示词中最后一次描述过的工具集合。</summary>
	private IReadOnlyList<BuiltinTool> _promptTools = [];

	/// <summary>会话运行期间不会变化的提示词输入。</summary>
	private SystemPromptOptions _systemPromptSections = null!;

	/// <summary>工具集变化后重建的系统提示词。</summary>
	private SystemMessage? _refreshedSystemPrompt;
	private IReadOnlyList<Message>? _projectedCache;
	private long _projectedCacheRevision = -1;
	private SystemMessage? _projectedCachePrompt;
	private readonly IDisposable _persistence;
	private readonly IDisposable _cacheWarmLifecycle;

	/// <summary>会话订阅者：代理事件之外，会话自己也会发重试事件。</summary>
	private readonly List<Func<AgentEvent, ValueTask>> _listeners = [];

	/// <summary>连续重试次数（0 表示当前不在重试）。</summary>
	private int _retryAttempt;
	private AssistantMessage? _routeFailed;

	/// <summary>退避等待的中止开关。</summary>
	private CancellationTokenSource? _retryAbort;

	/// <summary>整次运行（自动压缩、请求、重试退避）的中止开关。</summary>
	private readonly Lock _abortGate = new();
	private CancellationTokenSource? _runAbort;

	/// <summary>RPC 的 <c>set_auto_retry</c> 覆盖值；为 null 时用设置里的默认。</summary>
	private bool? _autoRetryOverride;
	private bool _overflowRecoveryAttempted;
	private readonly StreamFn _streamFn;
	private readonly StreamFn _routedStreamFn;
	private readonly StreamFn _directStreamFn;
	private static readonly Lazy<ModelRegistry> FallbackModelRegistry = new(ModelRegistry.LoadDefault);

	private readonly ModelRegistry? _registry;
	private readonly Func<string, string?>? _apiKeyForProvider;
	private readonly ImagesGateway? _images;
	private readonly ClassifiersGateway? _classifiers;
	private readonly StreamOptions _streamOptions;
	private bool _disposed;
	private readonly Lock _statisticsGate = new();

	/// <summary>宿主显式传入的技能/模板（例如 <c>--no-skills</c>），重载资源时保持生效。</summary>
	private readonly IReadOnlyList<Skills.Skill>? _explicitSkills;
	private readonly IReadOnlyList<PromptTemplate>? _explicitTemplates;
	private readonly SessionStatisticsCache _statistics = new();
	private long _todoRevision = -1;
	private TodoState _todo = TodoState.Empty;

	/// <summary>本次计划模式是否由 PiDeck 标记开启（决定下一条无标记消息是否自动退出）。</summary>
	private bool _planModeFromMarker;
	private IReadOnlyList<Message>? _contextMessages;
	private (long Tokens, int? LastUsageIndex) _contextTokens;

	/// <summary>当前生效的完整工具集（不受计划模式过滤影响）。</summary>
	private IReadOnlyList<BuiltinTool> _fullTools = [];

	/// <summary><c>tool_search</c> 工具：懒创建，按需加入可执行与声明集合。</summary>
	private readonly Lazy<AgentTool> _toolSearch;

	/// <summary>会话里记录 <c>tool_search</c> 加载结果的条目类型。</summary>
	private const string ToolLoadEntryType = "tool-load";

	/// <summary>宿主提供的 beforeToolCall 钩子；计划模式的安全网在它之前执行。</summary>
	private readonly Func<BeforeToolCallContext, CancellationToken, ValueTask<BeforeToolCallResult?>>? _userBeforeToolCall;

	/// <summary>宿主提供的 afterToolCall 钩子；嵌套的 codemode 调用也走同一个钩子。</summary>
	private readonly Func<AfterToolCallContext, CancellationToken, ValueTask<AfterToolCallResult?>>? _userAfterToolCall;

	public CodingAgentSession(CodingAgentSessionOptions options)
	{
		ArgumentNullException.ThrowIfNull(options);

		Cwd = Path.GetFullPath(options.Cwd);
		Settings = options.Settings;
		UiHost = options.UiHost;
		ProjectTrusted = options.ProjectTrusted;
		_images = options.Images;
		_classifiers = options.Classifiers;
		Session = options.Session
			?? (options.Persist ? SessionManager.Create(Cwd) : SessionManager.InMemory(Cwd));

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
		var defaults = BuiltinTools.Create(Cwd);
		var defaultsByName = new Dictionary<string, BuiltinTool>(defaults.Count, StringComparer.Ordinal);
		foreach (var tool in defaults)
		{
			defaultsByName[tool.Tool.Name] = tool;
		}

		List<BuiltinTool> builtins;
		if (options.Tools is null)
		{
			builtins = [.. defaults, .. options.ExtraTools.Select(static tool => new BuiltinTool(tool, tool.Description, []))];
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
				builtins[index] = TodoToolFactory.Create(() => Session);
			}
			else if (builtins[index].Tool.Name == AskQuestionToolFactory.Name)
			{
				builtins[index] = AskQuestionToolFactory.Create(() => this);
			}
			else if (builtins[index].Tool.Name == "bash")
			{
				// bash 需要会话上下文来注入 PI_* 环境变量与 settings 的 shell 配置。
				builtins[index] = builtins[index] with { Tool = PiWeft.CodingAgent.Tools.BashTool.Create(Cwd, BashEnvironment) };
			}
		}

		// 系统提示词只在运行时构造，不写进会话文件 —— 与 pi 一致。
		// 写进去的话，真实 pi 读这个会话时会把 role:system 的消息当成历史，污染上下文。
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

		// 系统提示词只在运行时构造，不写进会话文件 —— 与 pi 一致。
		_fullTools = builtins;
		_refreshedSystemPrompt = SystemPrompt.Build(SectionsFor(builtins));
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
		};

		_cacheWarmer = new CacheWarmer(
			_directStreamFn,
			() => Settings.CacheWarming,
			usage => Session.AppendUsage("cache_warm", _agent!.Model.Provider, _agent.Model.Id, usage, "prompt cache warm"),
			options.DecideCacheWarming,
			PushCacheWarmingStatus);

		// 恢复会话时，会话文件里记录的思考级别优先于默认值（显式传入的级别仍然最优先）。
		var requestedThinkingLevel = options.ThinkingLevel
			?? (Session.Entries.Count > 0 ? Session.BuildSessionContext().ThinkingLevel : null)
			?? options.Settings.ModelThinkingLevels.GetValueOrDefault($"{options.Model.Provider}/{options.Model.Id}")
			?? options.Settings.DefaultThinkingLevel
			?? ThinkingLevels.Off;

		_agent = new AgentRuntime(new AgentOptions
		{
			Model = options.Model,
			ThinkingLevel = ThinkingLevelResolver.Clamp(options.Model, requestedThinkingLevel),
			StreamFn = _routedStreamFn,
			Tools = [.. builtins.Select(static builtin => builtin.Tool)],
			ConvertToLlm = CodingMessages.ConvertToLlm,
			TransformContext = TransformContextAsync,
			BeforeToolCall = GuardedBeforeToolCallAsync,
			AfterToolCall = options.AfterToolCall,
			PrepareNextTurn = PrepareNextTurnAsync,
			WillRetry = ShouldRetryAssistant,
			Options = _streamOptions,
		});

		// codemode 的描述要列出会话当前可调用的工具，因此等代理建好后再绑定并重建提示词。
		RebindCodeMode([.. builtins.Select(static builtin => builtin.Tool)]);

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
	}

	public string Cwd { get; }

	public CodingAgentSettings Settings { get; }

	/// <summary>宿主交互界面（RPC / CLI / 桌面）；为 null 时 ask_question 报告不可用。</summary>
	public IUiHost? UiHost { get; }

	/// <summary>项目是否受信任；决定项目级技能与提示词模板是否加载。</summary>
	public bool ProjectTrusted { get; }

	public SessionManager Session { get; private set; }

	public AgentRuntime Agent => _agent;

	public Model Model => _agent.Model;

	/// <summary>当前缓存保温状态；模型没有 promptCache 时也会给出 inactive 原因。</summary>
	public Compaction.CacheWarmingStatus? CacheWarmingStatus => _cacheWarmer?.Status;

	public string ThinkingLevel => _agent.ThinkingLevel;

	public IReadOnlyList<AgentTool> Tools => _agent.Tools;

	/// <summary>真正声明给模型的工具（<see cref="Tools"/> 是脚本/钩子可调用的全集）。</summary>
	public IReadOnlyList<AgentTool> DeclaredTools => _agent.DeclaredTools;

	/// <summary>当前分支上模型可见的消息，已应用上下文编辑与压缩。</summary>
	/// <summary>模型下一次请求会看到的完整消息（含运行时构造的系统提示词）。</summary>
	public IReadOnlyList<Message> Messages => Project();

	/// <summary>
	/// 下一次请求将携带的消息：分支投影；若会话中途工具集发生变化，
	/// 其中的系统提示词会被重建。
	/// </summary>
	public IReadOnlyList<Message> ProjectedMessages => Project();

	/// <summary>当前是否有压缩正在进行。</summary>
	public bool IsCompacting { get; private set; }

	/// <summary>队列中等待的 steering 与 follow-up 消息数。</summary>
	public int PendingMessageCount => _agent.PendingMessageCount;

	/// <summary>由 <c>set_session_name</c> 设置的显示名（如果设置了）。</summary>
	public string? SessionName => Session.CurrentName();

	/// <summary>估算的上下文大小，以及它所依据的最后一条助手用量下标。</summary>
	/// <summary>当前会话解析出的资源快照（技能、模板、主题、包与诊断）。</summary>
	public ResourceSnapshot Resources { get; private set; }

	/// <summary>为该会话工作目录发现的技能。</summary>
	public IReadOnlyList<Skills.Skill> DiscoveredSkills => Resources.Skills;

	/// <summary>为该会话发现的斜杠命令提示词模板。</summary>
	public IReadOnlyList<PromptTemplate> PromptTemplates => Resources.Templates;

	/// <summary>
	/// 重新解析技能、提示词模板与主题，对应 pi 的 <c>/reload</c>：不重建会话，
	/// 只刷新提示词里的技能章节，使后续请求看到新的资源集合。
	/// </summary>
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

		ApplyTools();
	}

	/// <summary>用已发现的提示词模板展开 <c>/name args</c> 形式的消息。</summary>
	public string ExpandPromptTemplate(string text) => Prompts.PromptTemplates.Expand(text, PromptTemplates);

	/// <summary>
	/// 展开一条用户输入：先展开 <c>/skill:name</c> 技能命令，再展开提示词模板，顺序与 pi 一致。
	/// </summary>
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

	/// <summary>
	/// 把一条文本输入（可选图片）组装成待发送的用户消息，展开技能命令与提示词模板。
	/// CLI、RPC 与桌面共用这条路径，保证三者的输入语义一致。
	/// </summary>
	public UserMessage PrepareInput(string text, IReadOnlyList<ImageContent>? images = null)
	{
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

	/// <summary>
	/// 把叶子移动到 <paramref name="targetId"/>，并先把被放弃的路径摘要成一条
	/// <c>branch_summary</c> 条目。
	/// </summary>
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
			Settings.Compaction,
			Settings.BranchSummary,
			targetId,
			customInstructions,
			cancellationToken).ConfigureAwait(false);

		_agent.SetMessages(Session.BuildProjectedMessages());
		return result;
	}

	/// <summary>
	/// 直接把叶子移到某个条目并重载消息，不生成分支摘要。界面上的「跳到此处」用它，
	/// 只做回退不做摘要生成，因此是即时操作。
	/// </summary>
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

	/// <summary>
	/// 替换当前会话，<c>switch_session</c>、<c>fork</c>、<c>clone</c> 都需要它。代理保留自己的模型、
	/// 工具与设置，会话记录则按新会话的分支重建。
	/// </summary>
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
	}

	/// <summary>
	/// 在当前会话的 <paramref name="entryId"/> 处派生，并切换到新的会话文件。
	/// <paramref name="position"/> 取 <c>before</c>（在用户消息之前派生，RPC 的默认值）或 <c>at</c>
	/// （保留该条目本身，也就是 <see cref="CloneAsync"/> 的行为）。
	/// </summary>
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
		return selectedText;
	}

	/// <summary>把当前分支复制成一个新会话文件，位置不变。</summary>
	public void CloneAsync()
	{
		ThrowIfDisposed();
		var leaf = Session.LeafId ?? throw new InvalidOperationException("Cannot clone session: no current entry selected");
		ForkAt(leaf, "at");
	}

	/// <summary>只摘要被放弃的路径，不做导航。</summary>
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
			Settings.Compaction,
			Settings.BranchSummary,
			targetId,
			customInstructions,
			cancellationToken);
	}

	public (long Tokens, int? LastUsageIndex) ContextTokens()
	{
		lock (_statisticsGate)
		{
			var messages = Session.BuildProjectedMessages();
			if (!ReferenceEquals(messages, _contextMessages))
			{
				_contextTokens = Compaction.ContextTokens.EstimateContextTokens(messages);
				_contextMessages = messages;
			}
			return _contextTokens;
		}
	}

	/// <summary>上下文是否超过当前模型的压缩阈值。</summary>
	public bool ShouldCompact() =>
		Compaction.ContextTokens.ShouldCompact(ContextTokens().Tokens, _agent.Model.ContextWindow, Settings.Compaction);

	/// <summary>
	/// 摘要较旧的条目并记录一条 <c>compaction</c> 条目。没有可压缩内容时返回 <c>null</c>。
	/// 完成后会从会话刷新代理的会话记录。
	/// </summary>
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
					Settings.Compaction,
					customInstructions,
					cancellationToken),
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

	/// <summary>每个抽取点投递多少条 steering 消息。</summary>
	public AgentQueueMode SteeringMode
	{
		get => _agent.SteeringMode;
		set => _agent.SteeringMode = value;
	}

	/// <summary>每个抽取点投递多少条 follow-up 消息。</summary>
	public AgentQueueMode FollowUpMode
	{
		get => _agent.FollowUpMode;
		set => _agent.FollowUpMode = value;
	}

	/// <summary>自动压缩的运行时开关，与配置里的 <c>compaction.enabled</c> 同时生效。</summary>
	public bool AutoCompactionEnabled { get; set; } = true;

	/// <summary>清空排队的 steering 与 follow-up 消息，并返回被丢弃的内容。</summary>
	public (IReadOnlyList<Message> Steering, IReadOnlyList<Message> FollowUp) ClearQueue()
	{
		var cleared = _agent.ClearQueue();
		EmitQueueUpdate();
		return cleared;
	}

	/// <summary>当前排队消息的快照，供队列类事件与界面使用。</summary>
	public (IReadOnlyList<Message> Steering, IReadOnlyList<Message> FollowUp) PendingQueues() => _agent.PendingQueues();

	/// <summary>把队列现状作为 <c>queue_update</c> 发给订阅者。</summary>
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

	/// <summary>把会话级事件（例如重试）发给订阅者。</summary>
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

	/// <summary>中止当前运行并等到会话真正空闲，供 RPC 的 abort 命令使用。</summary>
	public async Task AbortAsync(CancellationToken cancellationToken = default)
	{
		Abort();
		while (IsRunning && !cancellationToken.IsCancellationRequested)
		{
			await Task.Delay(10, cancellationToken).ConfigureAwait(false);
		}
	}

	/// <summary>是否有一次 Prompt/Continue 运行尚未收尾（含压缩与重试退避）。</summary>
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

	/// <summary>
	/// 外部调用方的取消令牌被取消时，即使本轮已经落定 aborted 消息并收尾，也把取消继续向上传递：
	/// CLI 需要据此返回 130，桌面会用同一信号清掉发送状态。会话内部的 <see cref="Abort"/>（例如 RPC
	/// 的 abort 命令）不会取消调用方令牌，因此仍按正常收尾返回。
	/// </summary>
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

	/// <summary>缓存的全文件统计快照；只有追加条目才消费新用量。</summary>
	public SessionStatistics Statistics()
	{
		lock (_statisticsGate) return _statistics.Get(Session);
	}

	/// <summary>是否处于计划模式：写工具被停用，提示词附加只读指引。</summary>
	public bool PlanModeEnabled { get; private set; }

	/// <summary>开关计划模式。停用 <c>edit</c>/<c>write</c> 并重建提示词与工具集。</summary>
	public void SetPlanMode(bool enabled)
	{
		if (PlanModeEnabled == enabled)
		{
			return;
		}

		PlanModeEnabled = enabled;
		ApplyTools();
	}

	/// <summary>把完整工具集按计划模式过滤后应用到代理，并重建提示词。</summary>
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
				|| (!string.Equals(exposure, ToolExposure.Hidden, StringComparison.Ordinal) && loaded.Contains(tool.Tool.Name)))
			{
				declared.Add(tool);
			}
		}

		if (toolSearch is not null)
		{
			declared.Add(new BuiltinTool(toolSearch, "Search for tools to load", []));
		}

		var codemodeActive = effective.Any(static tool => tool.Tool.Name == CodeModeToolFactory.Name);
		// codemode 的 mode=on：直接声明给模型的工具补上从脚本调用的说明。
		if (string.Equals(Settings.Codemode.Mode, "on", StringComparison.Ordinal) && codemodeActive)
		{
			declared = [.. declared.Select(static tool => tool.Tool.Name is CodeModeToolFactory.Name or ToolSearchName
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

		var callableTools = toolSearch is null
			? effective.Select(static tool => tool.Tool).ToList()
			: [.. effective.Select(static tool => tool.Tool), toolSearch];
		_agent.SetTools(callableTools, declared.Select(static tool => tool.Tool).ToList());
		_promptTools = declared;
		_refreshedSystemPrompt = SystemPrompt.Build(SectionsFor(declared));
	}

	/// <summary><c>tool_search</c> 的名字；它不是内置目录里的工具，只在需要发现时出现。</summary>
	private const string ToolSearchName = "tool_search";

	private static bool IsDiscoveryExposure(string exposure) =>
		string.Equals(exposure, ToolExposure.Codemode, StringComparison.Ordinal)
		|| string.Equals(exposure, ToolExposure.Deferred, StringComparison.Ordinal);

	/// <summary>带上 <c>plan_mode</c> 章节（如启用）的提示词输入。</summary>
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
		if (BuildMcpServersSection() is { } mcpServers) Merge()["mcp_servers"] = mcpServers;

		return _systemPromptSections with
		{
			Tools = tools,
			SectionOverrides = merged ?? overrides,
		};
	}

	/// <summary>
	/// 与 pi 的 <c>mcp_servers</c> 章节对齐：列出工具没有直接声明给模型的 MCP 服务器，
	/// 以及该用 codemode 还是 tool_search 去够到它们。
	/// </summary>
	private string? BuildMcpServersSection()
	{
		var namespaces = new Dictionary<string, McpNamespaceInfo>(StringComparer.Ordinal);
		foreach (var builtin in _fullTools)
		{
			var tool = builtin.Tool;
			if (tool.Namespace is null
				|| string.Equals(tool.Exposure, ToolExposure.Direct, StringComparison.Ordinal)
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

	/// <summary>计划模式下的安全网：即使模型仍调用写工具也会被拦下。</summary>
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

		return _userBeforeToolCall is null
			? null
			: await _userBeforeToolCall(context, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>把占位的 codemode 换成绑定到本会话的版本，并按新的工具集重建提示词。</summary>
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

	/// <summary>
	/// codemode 描述里列出的工具。pi 的 mode=on 只列 <c>codemode</c> exposure 的工具：已经直接
	/// 声明给模型的工具不再重复一遍（它们的描述里会加上怎么从脚本调用），deferred 留给
	/// <c>tool_search</c>。mode=only 额外列出 direct 工具。
	/// </summary>
	private IReadOnlyList<AgentTool> CodemodeDescribedTools(IReadOnlyList<AgentTool> tools)
	{
		var only = string.Equals(Settings.Codemode.Mode, "only", StringComparison.Ordinal);
		return
		[
			.. tools.Where(tool =>
				!string.Equals(tool.Name, CodeModeToolFactory.Name, StringComparison.Ordinal)
				&& !string.Equals(tool.Exposure, ToolExposure.Hidden, StringComparison.Ordinal)
				&& (string.Equals(tool.Exposure, ToolExposure.Codemode, StringComparison.Ordinal)
					|| (only && !string.Equals(tool.Exposure, ToolExposure.Deferred, StringComparison.Ordinal)))),
		];
	}

	/// <summary>按当前分支重建 <c>tool_search</c> 加载过的工具名。</summary>
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

	/// <summary>
	/// 面向模型的 <c>tool_search</c>：在尚未声明的 <c>codemode</c>/<c>deferred</c> 工具里按 BM25
	/// 排序，把命中的工具写成 <c>tool-load</c> 条目并立即声明（后续回合可直接调用）。
	/// 与 pi 一样，加载会进入会话记录，因此恢复、fork 与切换分支都看得到。
	/// </summary>
	private AgentTool CreateToolSearchTool() => new()
	{
		Name = ToolSearchName,
		Label = ToolSearchName,
		Description =
			"Search for tools that are not declared to you yet and load the matching ones so you can call them directly. " +
			"Use it when a task needs a tool you cannot see, such as an MCP tool.",
		Parameters = new JsonObject
		{
			["type"] = "object",
			["properties"] = new JsonObject
			{
				["query"] = new JsonObject
				{
					["type"] = "string",
					["description"] = "What the tool should do.",
				},
				["limit"] = new JsonObject
				{
					["type"] = "integer",
					["minimum"] = 1,
					["description"] = "Maximum number of tools to load (default 8).",
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
			return Task.FromResult(AgentToolResult.Text("No tools are waiting to be loaded."));
		}

		var documents = candidates
			.Select(tool => (tool.Name, CodeModeSearch.CreateDocument(tool.Name, tool.Description, tool.Parameters)))
			.ToList();
		var matches = CodeModeSearch.Rank(query, documents, limit);
		if (matches.Count == 0)
		{
			return Task.FromResult(AgentToolResult.Text($"No tools matched \"{query}\"."));
		}

		var names = new JsonArray();
		foreach (var name in matches)
		{
			names.Add((JsonNode?)JsonValue.Create(name));
		}

		Session.AppendCustom(ToolLoadEntryType, new JsonObject { ["tools"] = names });
		// 立即重建 loadout：后续回合的声明与脚本可调用集合都包含这些工具。
		ApplyTools();

		var sections = matches.Select(name =>
		{
			var tool = candidates.First(candidate => string.Equals(candidate.Name, name, StringComparison.Ordinal));
			return CodeModeToolFactory.RenderDeclaration(tool, ToolGuidelines(tool.Name));
		});
		return Task.FromResult(AgentToolResult.Text(
			$"Loaded {matches.Count} tool(s):\n\n{string.Join("\n\n", sections)}"));
	}

	/// <summary>内置工具的 prompt guidelines，附在声明与 ALL_TOOLS 的描述后。</summary>
	private IReadOnlyList<string> ToolGuidelines(string name)
	{
		foreach (var builtin in _fullTools)
		{
			if (string.Equals(builtin.Tool.Name, name, StringComparison.Ordinal)) return builtin.Guidelines;
		}

		return [];
	}

	/// <summary>mode=on：直接声明给模型的工具补一行从脚本调用的说明（与 pi 一致）。</summary>
	private static AgentTool WithCodemodeHint(AgentTool tool)
	{
		// 调用方可能把已经带提示的工具再传回来（MCP 刷新/测试），避免重复追加。
		if (tool.Description.Contains("\n\nCodemode: `tools.", StringComparison.Ordinal)) return tool;

		var output = tool.OutputSchema is null ? "a string" : "an object";
		return tool with
		{
			Description = $"{tool.Description.Trim()}\n\nCodemode: `tools.{CodeModeIdentifier.ToIdentifier(tool.Name)}(args)` resolves to {output}.",
		};
	}

	/// <summary>
	/// 执行脚本发起的一次嵌套调用。与顶层调用共用同一条管线：参数修正、schema 校验、
	/// 计划模式安全网与宿主钩子都会执行，嵌套结果不进入 LLM 上下文。
	/// </summary>
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
			// 取消不是工具失败：让沙箱把这次调用标成 cancelled，而不是一个 error 结果。
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

	/// <summary>
	/// 脚本的 <c>models.*</c>：只查询会话的模型目录，不发请求。pi 的 <c>classify</c>/<c>generateImages</c>
	/// 需要网关与凭据，PiWeft 还没把这两个入口接到会话，因此这里只暴露目录查询。
	/// </summary>
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

	/// <summary>没有凭据解析器时按「全部可用」处理；有解析器时以它能否给出 key 为准。</summary>
	private bool IsProviderAvailable(string provider) =>
		_apiKeyForProvider is null || _apiKeyForProvider(provider) is { Length: > 0 };

	private static JsonObject ModelInfo(Model model)
	{
		var info = JsonSerializer.SerializeToNode(model, PiJsonContext.Default.Model) as JsonObject ?? new JsonObject();
		// 目录里的 headers 可能带凭据，脚本不能看到（与 pi 一样剔除）。
		info.Remove("headers");
		return info;
	}

	/// <summary>
	/// 执行一次 <c>models.classify()</c> / <c>models.generateImages()</c>。模型由脚本给出
	/// provider + id，凭据与请求头沿用会话的解析结果，脚本无法自带 baseUrl/headers。
	/// </summary>
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

	/// <summary>按当前分支重建 <c>load()</c> 可见的 store：从根开始依次应用 codemode-store 条目。</summary>
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

	/// <summary>成功执行后把 <c>store()</c> 的改动追加为一条自定义条目（不进入上下文）。</summary>
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

	/// <summary>
	/// 用本会话的模型、凭据与选项做一次连接测试（只发 1 个输出 token）。
	/// 桌面的「测试连接」与 RPC 的 <c>test_model</c> 共用它。
	/// </summary>
	public Task<ProviderTestResult> TestConnectionAsync(CancellationToken cancellationToken = default) =>
		ProviderProbe.TestAsync(_directStreamFn, _agent.Model, _streamOptions, cancellationToken);

	/// <summary>当前分支的待办计划；按会话 revision 缓存，未变更时零扫描。</summary>
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

	/// <summary>按类型拆分的令牌合计，供客户端展示统计。</summary>
	public (long Input, long Output, long CacheRead, long CacheWrite) UsageBreakdown()
	{
		var stats = Statistics();
		return (stats.Input, stats.Output, stats.CacheRead, stats.CacheWrite);
	}

	/// <summary>整个会话的令牌与费用合计，包含其它运行写入的条目。</summary>
	public (long Tokens, double Cost) UsageTotals()
	{
		var stats = Statistics();
		return (stats.Tokens, stats.Cost);
	}

	public async Task<IReadOnlyList<Message>> PromptAsync(string text, CancellationToken cancellationToken = default)
	{
		ThrowIfDisposed();
		// 展开技能命令与提示词模板，让 CLI、RPC、桌面与交互模式共享同一语义。
		var message = PrepareInput(text);
		using var run = BeginRun(cancellationToken);
		try
		{
			await AutoCompactAsync(run.Token).ConfigureAwait(false);
			var messages = await RunWithRetryAsync(
				// 只有首次才追加用户消息；重试时它已经在投影里，再 PromptAsync 会写第二份进内存与 JSONL。
				attempt => attempt == 0
					? _agent.PromptAsync(message, run.Token)
					: _agent.ContinueAsync(run.Token),
				run.Token).ConfigureAwait(false);
			ThrowIfCallerCancelled(cancellationToken);
			return messages;
		}
		finally
		{
			EndRun(run);
		}
	}

	// ------------------------------------------------------------ 自动重试

	/// <summary>是否启用自动重试（RPC 覆盖优先，其次是设置）。</summary>
	public bool AutoRetryEnabled => _autoRetryOverride ?? Settings.Retry.Enabled;

	/// <summary>RPC 的 <c>set_auto_retry</c>。</summary>
	public void SetAutoRetryEnabled(bool enabled) => _autoRetryOverride = enabled;

	/// <summary>当前重试到第几次（0 表示没有在重试）。</summary>
	public int RetryAttempt => _retryAttempt;

	/// <summary>是否正在退避等待。</summary>
	public bool IsRetrying => _retryAbort is not null;

	/// <summary>中止正在进行的重试等待，对应 RPC 的 <c>abort_retry</c>。</summary>
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
				var delay = RetryClassifier.DelayMs(policy, attempt);
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

	/// <summary>
	/// 虚拟模型的请求入口：先按当前会话选择路由到物理模型，再把事件原样转发。
	/// 助手消息因此记录物理 provider/model，而 <c>model_change</c> 仍保留虚拟模型。
	/// </summary>
	private AssistantMessageEventStream StreamWithVirtualRouting(
		Model model,
		IReadOnlyList<Message> messages,
		StreamOptions options) =>
		StreamWithVirtualRouting(model, messages, options, direct: false);

	/// <summary>代理循环之外的调用（摘要、分支摘要）：route 结果不写路由状态。</summary>
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

	/// <summary>读取当前分支上某个虚拟模型最近一次保存的路由状态。</summary>
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

	/// <summary>把路由状态写成 <c>pi.virtual-model-state</c> 自定义条目，不进入 LLM 上下文。</summary>
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

	/// <summary>把保温状态同步到扩展 UI 的 <c>piweft:cache-warming</c> 状态项。</summary>
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

	/// <summary>工具回合结束后检查阈值；需要时在继续同一个运行前压缩。</summary>
	private async ValueTask<AgentTurnUpdate?> PrepareNextTurnAsync(
		AgentTurnContext turn,
		CancellationToken cancellationToken)
	{
		if (!AutoCompactionEnabled || !Settings.Compaction.Enabled || IsCompacting)
		{
			return null;
		}

		var projected = Session.BuildProjectedMessages();
		var (tokens, _) = Compaction.ContextTokens.EstimateContextTokens(projected);
		if (!Compaction.ContextTokens.ShouldCompact(tokens, _agent.Model.ContextWindow, Settings.Compaction))
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
					Settings.Compaction,
					customInstructions: null,
					cancellationToken),
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

		Emit(new CompactionEndEvent
		{
			Reason = reason,
			Result = result,
			EstimatedTokensAfter = result is null ? null : ContextTokens().Tokens,
		});
	}

	/// <summary>
	/// 跑一次代理，并在「可重试的失败」上按退避策略重试。与 pi 一致：
	/// 失败的助手回复保留在会话文件里，但通过 <c>context_edit</c> 从模型投影中移除，
	/// 这样重试请求不会带上那次失败；重试本身不重新提交用户消息。
	/// </summary>
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
				var delayMs = RetryClassifier.DelayMs(policy, _retryAttempt);

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

	/// <summary>把某条消息从模型投影中移除（保留在会话文件里）。</summary>
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

	/// <summary>上下文超过阈值且开启了压缩时，先压缩再继续。</summary>
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
		_cacheWarmer?.Cancel();
		_overflowRecoveryAttempted = false;
		using var run = BeginRun(cancellationToken);
		try
		{
			// 与字符串重载一致：先自动压缩，再按重试策略运行；重试同样走"继续"而不是重新提问。
			await AutoCompactAsync(run.Token).ConfigureAwait(false);
			var messages = await RunWithRetryAsync(
				attempt => attempt == 0
					? _agent.PromptAsync(message, run.Token)
					: _agent.ContinueAsync(run.Token),
				run.Token).ConfigureAwait(false);
			ThrowIfCallerCancelled(cancellationToken);
			return messages;
		}
		finally
		{
			EndRun(run);
		}
	}

	public async Task<IReadOnlyList<Message>> ContinueAsync(CancellationToken cancellationToken = default)
	{
		ThrowIfDisposed();
		_cacheWarmer?.Cancel();
		using var run = BeginRun(cancellationToken);
		try
		{
			await AutoCompactAsync(run.Token).ConfigureAwait(false);
			var messages = await RunWithRetryAsync(
				_ => _agent.ContinueAsync(run.Token),
				run.Token).ConfigureAwait(false);
			ThrowIfCallerCancelled(cancellationToken);
			return messages;
		}
		finally
		{
			EndRun(run);
		}
	}

	/// <summary>排入一条消息，在当前工具批次结束后引导正在运行的代理。</summary>
	public void Steer(Message message)
	{
		_agent.Steer(message);
		EmitQueueUpdate();
	}

	/// <summary>排入一条消息，在代理本应停止时继续处理。</summary>
	public void FollowUp(Message message)
	{
		_agent.FollowUp(message);
		EmitQueueUpdate();
	}

	/// <summary>切换模型，并在会话中记录一条 <c>model_change</c> 条目。</summary>
	public void SetModel(Model model)
	{
		_cacheWarmer?.Cancel();
		ArgumentNullException.ThrowIfNull(model);
		if (model.Id == _agent.Model.Id && model.Provider == _agent.Model.Provider)
		{
			return;
		}

		_agent.SetModel(model);
		Session.AppendModelChange(model.Provider, model.Id);
	}

	/// <summary>设置思考级别（按模型收敛），并记录一条变更条目。</summary>
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
	}

	/// <summary>在模型的工具循环之外执行 shell 命令的结果。</summary>
	public sealed record BashRunResult
	{
		public required string Output { get; init; }

		public int? ExitCode { get; init; }

		public bool Cancelled { get; init; }

		public bool Truncated { get; init; }

		public string? FullOutputPath { get; init; }
	}

	/// <summary>
	/// 立即执行一条 shell 命令，并记为 <c>bashExecution</c> 消息，因此下一次提示能看到它 ——
	/// 除非设置了 <paramref name="excludeFromContext"/>。交互模式的 <c>!</c> 命令与 RPC 的 <c>bash</c>
	/// 命令都由它支撑。
	/// </summary>
	public async Task<BashRunResult> RunBashAsync(
		string command,
		bool excludeFromContext = false,
		Action<string>? onOutput = null,
		CancellationToken cancellationToken = default)
	{
		ThrowIfDisposed();

		var result = await ShellRunner
			.RunAsync(command, Cwd, timeoutSeconds: null, onOutput, cancellationToken)
			.ConfigureAwait(false);

		var truncation = Truncate.Tail(result.Output);
		var fullOutputPath = truncation.Truncated ? ShellRunner.WriteFullOutput(result.Output) : null;

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

	/// <summary>追加一条会参与 LLM 上下文的扩展风格消息。</summary>
	public string AppendCustomMessage(string customType, string content, bool display = true)
	{
		var id = Session.AppendCustomMessage(customType, MessageContent.FromText(content), display);
		EmitEntryAppended(id);
		return id;
	}

	/// <summary>设置会话显示名；<paramref name="name"/> 为 null 表示清除。对应 RPC 的 <c>set_session_name</c>。</summary>
	public void SetSessionName(string? name)
	{
		Session.AppendSessionInfo(name);
		Emit(new SessionInfoChangedEvent { Name = name });
	}

	/// <summary>把刚追加的自定义条目作为 <c>entry_appended</c> 发出。</summary>
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
		_persistence.Dispose();
		_cacheWarmLifecycle.Dispose();
		GC.SuppressFinalize(this);
	}

	private ValueTask<IReadOnlyList<Message>> TransformContextAsync(
		IReadOnlyList<Message> messages,
		CancellationToken cancellationToken) =>
		ValueTask.FromResult(Project());

	/// <summary>
	/// 分支投影，外加当前系统提示词。会话运行期间工具集可能变化（例如 MCP 服务器宣告了新工具），
	/// 所以模型看到的提示词每次都按当前状态重建。
	/// <para>
	/// 会话文件里不存系统提示（与 pi 一致）：文件里若因历史原因残留了 role:system 的消息，用当前
	/// 提示词替换它；否则插到最前面。压缩产生的 system 消息不在开头，保持原样。
	/// </para>
	/// </summary>
	private IReadOnlyList<Message> Project()
	{
		// The prompt is built once and only replaced when tools/resources rebuild it. Reusing the
		// projected list also keeps the historical Message instances stable for incremental views.
		var prompt = _refreshedSystemPrompt ??= SystemPrompt.Build(SectionsFor(_promptTools));
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
			// 替换章节而不是把它们拍平，这样 provider 侧缓存仍能看到相同的分界
			// （preamble、tools、rules 等）。
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

	/// <summary>
	/// 替换生效的工具集，例如 MCP 服务器宣告 <c>tools/list_changed</c> 之后。系统提示词会
	/// 用新的工具声明重建，未变动的工具沿用已有的措辞。
	/// </summary>
	public void SetTools(IReadOnlyList<AgentTool> tools)
	{
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
				TodoToolFactory.Name => TodoToolFactory.Create(() => Session).Tool,
				AskQuestionToolFactory.Name => AskQuestionToolFactory.Create(() => this).Tool,
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
					rebuilt.Add(TodoToolFactory.Create(() => Session));
					continue;
				case AskQuestionToolFactory.Name:
					rebuilt.Add(AskQuestionToolFactory.Create(() => this));
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
			}

			rebuilt.Add(known.TryGetValue(tool.Name, out var described)
				? described with { Tool = tool }
				: new BuiltinTool(tool, tool.Description, []));
		}

		_fullTools = rebuilt;
		ApplyTools();
	}

	/// <summary>bash 工具的会话上下文：以 <c>PI_*</c> 环境变量暴露当前会话与模型。</summary>
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

	/// <summary>会话文件里最近一次写入的 thinking level 是否已经读过。</summary>
	private bool _persistedThinkingLevelLoaded;

	/// <summary>会话文件里最近一次写入的 thinking level；恢复会话时读一次并缓存，之后随写入更新。</summary>
	private string? _persistedThinkingLevel;

	/// <summary>
	/// 会话文件里最近一次写入的 thinking level。只在第一次需要时重建分支上下文（O(n)），
	/// 之后随写入同步更新 —— 否则每条消息结束都会把整个分支遍历一遍。
	/// </summary>
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

	/// <summary>Read-only view of a system prompt followed by the session projection.</summary>
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
