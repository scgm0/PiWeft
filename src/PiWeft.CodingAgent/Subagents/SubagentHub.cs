using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using PiWeft.Agent;
using PiWeft.Ai;
using PiWeft.Ai.Json;
using PiWeft.CodingAgent.Session;

namespace PiWeft.CodingAgent.Subagents;

/// <summary>一个子代理实例的状态。</summary>
public enum SubagentState
{
	/// <summary>已登记，子会话还没建好。</summary>
	Starting,

	/// <summary>子会话正在跑一轮。</summary>
	Running,

	/// <summary>空闲，可以继续发消息。</summary>
	Idle,

	/// <summary>上一轮失败。</summary>
	Failed,

	/// <summary>已被父会话停掉。</summary>
	Stopped,
}

/// <summary>子代理的只读快照。名字在父会话的子代理里唯一。</summary>
public sealed record SubagentRun
{
	/// <summary>父会话这一侧的名字。</summary>
	public required string Name { get; init; }

	/// <summary>子会话 id；子会话还没建好时为空串。</summary>
	public string SessionId { get; init; } = string.Empty;

	/// <summary>子会话文件；冷恢复靠它把会话读回来。</summary>
	public string? SessionFile { get; init; }

	/// <summary>角色名。</summary>
	public required string Agent { get; init; }

	/// <summary>启动时的一句话描述；显式 label 优先，否则取任务首行。</summary>
	public string Label { get; init; } = string.Empty;

	/// <summary>任务全文，供卡片展开与恢复后查看；超过 <see cref="SubagentHub.MaxBriefChars"/> 的部分截断。</summary>
	public string Brief { get; init; } = string.Empty;

	/// <summary>子会话模型，<c>provider/id</c>；启动完成前为空串。</summary>
	public string Model { get; init; } = string.Empty;

	/// <summary>子会话思考级别；启动完成前为空串。</summary>
	public string Thinking { get; init; } = string.Empty;

	/// <summary>委派深度，顶层子代理为 1。</summary>
	public int Depth { get; init; }

	/// <summary>当前状态。</summary>
	public SubagentState State { get; init; } = SubagentState.Starting;

	/// <summary>已完成的回合数。</summary>
	public int Turns { get; init; }

	/// <summary>运行中正在调用的工具与摘要（<c>read src/a.cs</c>）；结束或未开始时为 null。</summary>
	public string? Activity { get; init; }

	/// <summary>最近一条助手正文的首行摘要，给等待与卡片显示进度；新一轮开始时清空。</summary>
	public string? Progress { get; init; }

	/// <summary>子代理在自己的标签里等用户回答 <c>ask_question</c>；只影响展示与等待提示。</summary>
	public bool AwaitingUser { get; init; }

	/// <summary>最近一轮的最终回复。</summary>
	public string? Result { get; init; }

	/// <summary>子会话累计输入 + 输出 token；不含缓存命中。</summary>
	public long Tokens { get; init; }

	/// <summary>子会话累计费用。</summary>
	public double Cost { get; init; }

	/// <summary>最近一次通过 output_schema 校验的结构化结果。</summary>
	public JsonNode? StructuredOutput { get; init; }

	/// <summary>创建时父会话是否在计划模式；冷恢复按它重建只读边界。</summary>
	public bool PlanMode { get; init; }

	/// <summary>启动时间，Unix 毫秒。</summary>
	public long StartedAt { get; init; }

	/// <summary>最近一轮结束时间，Unix 毫秒。</summary>
	public long FinishedAt { get; init; }

	/// <summary>从会话条目恢复出来的历史运行，没有活会话。</summary>
	public bool Restored { get; init; }

	/// <summary>是否真的在跑：启动中或运行中。</summary>
	public bool IsActive => State is SubagentState.Starting or SubagentState.Running;

	/// <summary>是否已结束一轮且可以继续发消息。</summary>
	public bool IsTerminal => State is SubagentState.Idle or SubagentState.Failed or SubagentState.Stopped;
}

/// <summary>启动子代理的请求。</summary>
public sealed record SubagentStartRequest(string Agent, string Task, string? Name = null, string? Context = null)
{
	/// <summary>卡片上的一句话标签；省略时取任务首行。</summary>
	public string? Label { get; init; }

	/// <summary>要求最终回复返回的 JSON Schema；null 表示不要求。</summary>
	public JsonObject? OutputSchema { get; init; }
}

/// <summary>启动结果：成功时带名字与子会话 id。</summary>
public sealed record SubagentStartOutcome(bool Ok, string Text, string? Name = null, string? SessionId = null);

/// <summary>send/stop/wait 的统一结果。</summary>
/// <param name="Ok">是否成功。</param>
/// <param name="Text">给模型看的文本，同时是界面显示的结果。</param>
/// <param name="Details">结构化版本，供界面与程序调用方使用。</param>
public sealed record SubagentOutcome(bool Ok, string Text, JsonObject? Details = null);

/// <summary>宿主创建子会话需要的信息。</summary>
/// <param name="Parent">发起委派的会话。</param>
/// <param name="ParentTitle">父会话标题，用于边界提示。</param>
/// <param name="Agent">角色定义。</param>
/// <param name="Name">父会话这一侧给子代理起的名字。</param>
/// <param name="Task">第一条用户消息。</param>
/// <param name="Depth">子会话的深度；顶层子代理为 1。</param>
/// <param name="Seed">fork 上下文要写进子会话的历史；<c>fresh</c> 为 null。</param>
public sealed record SubagentStartContext(
	CodingAgentSession Parent,
	string ParentTitle,
	SubagentDefinition Agent,
	string Name,
	string Task,
	int Depth,
	IReadOnlyList<Message>? Seed = null)
{
	/// <summary>子会话建好后直接进入计划模式。</summary>
	public bool PlanMode { get; init; }

	/// <summary>要求子会话最终回复返回的 JSON Schema；null 表示不要求。</summary>
	public JsonObject? OutputSchema { get; init; }
}

/// <summary>冷恢复一个已结束的子代理：按会话文件重建子会话。</summary>
/// <param name="Parent">发起恢复的父会话。</param>
/// <param name="ParentTitle">父会话标题。</param>
/// <param name="Agent">角色定义（会话文件里只存了角色名，按名字从目录取）。</param>
/// <param name="Name">父会话这一侧的名字。</param>
/// <param name="SessionFile">子会话文件。</param>
/// <param name="Depth">子会话深度。</param>
/// <param name="SessionId">子会话 id，宿主打不开自己核对时用。</param>
public sealed record SubagentReopenContext(
	CodingAgentSession Parent,
	string ParentTitle,
	SubagentDefinition Agent,
	string Name,
	string SessionFile,
	int Depth,
	string SessionId)
{
	/// <summary>恢复后继续按只读边界运行。</summary>
	public bool PlanMode { get; init; }

	/// <summary>恢复后要求的结构化输出 schema。</summary>
	public JsonObject? OutputSchema { get; init; }
}

/// <summary>
/// 宿主提供的子会话能力。桌面把子会话开成后台标签；没有宿主的纯 CLI 不注册委派工具。
/// </summary>
public interface ISubagentHost
{
	/// <summary>建一个子会话；宿主不支持或创建失败时返回 null。</summary>
	Task<CodingAgentSession?> CreateChildAsync(SubagentStartContext context, CancellationToken cancellationToken);

	/// <summary>
	/// 冷恢复：按会话文件把已结束的子代理重新打开，让它能继续收消息。
	/// 宿主不支持时返回 null，调用方按「会话未打开」回绝。
	/// </summary>
	Task<CodingAgentSession?> ReopenChildAsync(SubagentReopenContext context, CancellationToken cancellationToken) =>
		Task.FromResult<CodingAgentSession?>(null);
}

/// <summary>
/// 父会话侧的异步委派：登记子代理、转发消息、限制并发与深度，把每轮最终回复送回父会话。
/// 工具调用立即返回；结果走消息，父会话空闲时直接起一轮，忙时排 follow-up。
/// </summary>
public sealed class SubagentHub : IDisposable
{
	/// <summary>父会话看到的结果条目类型。</summary>
	public const string EntryType = "piweft-subagent";

	/// <summary>子代理结果消息的类型。</summary>
	public const string ResultMessageType = "piweft-subagent-result";

	/// <summary>父会话发给子代理的消息类型。</summary>
	public const string MessageType = "piweft-subagent-message";

	/// <summary>单个结果进上下文的上限；超出部分留在子会话文件里。</summary>
	public const int MaxResultChars = 12_000;

	/// <summary>wait 一次最多带回的总字符数。</summary>
	public const int MaxWaitChars = 24_000;

	/// <summary>fork 复制给子会话的历史上限；超出时只保留最近一段。</summary>
	public const int MaxForkSeedChars = 100_000;

	/// <summary><c>read_subagent</c> 一次最多返回的字符数。</summary>
	public const int MaxReadChars = 48_000;

	/// <summary>任务全文进卡片与 session 条目的上限；完整 brief 本来就在模型输入里。</summary>
	public const int MaxBriefChars = 4_000;

	/// <summary>进度摘要上限，按首行截断。</summary>
	public const int MaxProgressChars = 240;

	private const int MaxListedAgents = 32;

	private const int MaxAgentDescriptionChars = 200;

	/// <summary>Details 里给界面卡片留的正文上限：工具正文已经带全文，这里只够画卡。</summary>
	public const int MaxCardChars = 4_000;

	/// <summary>wait 的默认超时。</summary>
	public static readonly TimeSpan DefaultWaitTimeout = TimeSpan.FromMinutes(10);

	/// <summary>回报连续拉起父会话的轮数上限；用户发消息清零。</summary>
	public const int MaxAutoContinue = 6;

	/// <summary>子代理单次运行的默认时限。</summary>
	public static readonly TimeSpan DefaultRunTimeout = TimeSpan.FromMinutes(30);

	private sealed class RunState
	{
		public required SubagentRun Run { get; set; }

		public CodingAgentSession? Session { get; set; }

		public IDisposable? Subscription { get; set; }

		/// 冷恢复只允许一个调用方建标签；并行 send 在这里排队，避免同一历史项开出两个会话。
		public SemaphoreSlim? ReopenGate { get; set; }

		public TaskCompletionSource<TurnResult> Completion { get; set; } =
			new(TaskCreationOptions.RunContinuationsAsynchronously);

		/// 结果按运行记账：同一子代理第二次运行时，两段正文各自投递一次。
		public long TurnSeq { get; set; }

		public long ResultTurn { get; set; }

		public long DeliveredTurn { get; set; }

		public bool Stopping { get; set; }

		public bool TimedOut { get; set; }

		public CancellationTokenSource? Deadline { get; set; }

		public JsonObject? OutputSchema { get; set; }

		public JsonNode? StructuredOutput { get; set; }

		public int TokensVersion { get; set; } = 1;

		/// 并行工具调用时按开始顺序记着，最后一个结束不会把仍在跑的那一个清掉。
		public List<(string Id, string Description)> ActiveTools { get; } = [];
	}

	private sealed record PendingResult(string Name, bool Failed, string Text, long Turn, JsonNode? Structured = null);

	/// 一次运行收尾的完整结果：wait 用它拿到「等的那一次」，不会读到下一次运行的正文。
	private sealed record TurnResult(long Turn, string Text, JsonNode? Structured = null);

	private readonly CodingAgentSession _owner;
	private readonly ISubagentHost _host;
	private readonly SubagentCatalog _catalog;
	private readonly int _maxActive;
	private readonly int _maxSpawns;
	private readonly int _depth;
	private readonly TimeSpan _runTimeout;
	private readonly TimeSpan _coalesce;
	private readonly Lock _gate = new();
	private readonly Dictionary<string, RunState> _runs = new(StringComparer.Ordinal);
	private readonly List<string> _order = [];
	private readonly List<PendingResult> _pending = [];
	private Task _flush = Task.CompletedTask;
	private bool _flushScheduled;
	private bool _disposed;
	private IDisposable? _parentSubscription;
	private int _spawns;
	private int _autoContinue;

	public SubagentHub(
		CodingAgentSession owner,
		ISubagentHost host,
		SubagentCatalog catalog,
		int depth,
		int maxActive = 4,
		int maxSpawns = 32,
		TimeSpan? runTimeout = null,
		TimeSpan? coalesce = null)
	{
		_owner = owner;
		_host = host;
		_catalog = catalog;
		_depth = depth;
		_maxActive = Math.Max(1, maxActive);
		_maxSpawns = Math.Max(1, maxSpawns);
		_runTimeout = runTimeout is { } configured && configured > TimeSpan.Zero ? configured : DefaultRunTimeout;
		_coalesce = coalesce ?? TimeSpan.FromMilliseconds(200);
		Restore();
	}

	/// <summary>子代理列表或状态变化；界面据此刷新。</summary>
	public event Action? Changed;

	/// <summary>本会话开出的子代理快照，按启动顺序。</summary>
	public IReadOnlyList<SubagentRun> Runs => RunsSnapshot();

	/// <summary>
	/// 返回调用方独占的数组，按启动顺序。界面可以原地排序，不必再复制一份；
	/// 数组与 hub 内部状态分离，改数组不会影响运行。
	/// </summary>
	public SubagentRun[] RunsSnapshot()
	{
		lock (_gate)
		{
			var runs = new SubagentRun[_order.Count];
			for (var index = 0; index < _order.Count; index++)
			{
				runs[index] = _runs[_order[index]].Run;
			}

			return runs;
		}
	}

	/// <summary>角色目录。</summary>
	public SubagentCatalog Catalog => _catalog;

	/// <summary>是否有子代理正在等用户回答；页签圆点用它，避免每次活动都复制整份运行列表。</summary>
	public bool HasAwaitingUser
	{
		get
		{
			lock (_gate)
			{
				foreach (var state in _runs.Values)
				{
					if (state.Run.AwaitingUser)
					{
						return true;
					}
				}

				return false;
			}
		}
	}

	/// <summary>
	/// 从父会话的 <c>piweft-subagent</c> 条目恢复历史运行。恢复出来的项没有活会话：
	/// 状态按最后一条记录落定，<c>session_id</c> 仍在，桌面可以按 id 打开子会话文件。
	/// </summary>
	private void Restore()
	{
		var restored = new Dictionary<string, RunState>(StringComparer.Ordinal);
		var order = new List<string>();
		foreach (var entry in _owner.Session.Entries)
		{
			if (entry is not CustomEntry { CustomType: EntryType, Data: JsonObject data })
			{
				continue;
			}

			var kind = data["kind"]?.GetValue<string>();
			var name = data["name"]?.GetValue<string>();
			if (string.IsNullOrEmpty(kind) || string.IsNullOrEmpty(name))
			{
				continue;
			}

			if (kind is "start" or "started")
			{
				if (!restored.TryGetValue(name, out var state))
				{
					state = new RunState
					{
						Run = new SubagentRun
						{
							Name = name,
							Agent = data["agent"]?.GetValue<string>() ?? string.Empty,
							Label = data["label"]?.GetValue<string>() ?? string.Empty,
							Brief = data["brief"]?.GetValue<string>() ?? string.Empty,
							Model = data["model"]?.GetValue<string>() ?? string.Empty,
							Thinking = data["thinking"]?.GetValue<string>() ?? string.Empty,
							Depth = data["depth"]?.GetValue<int>() ?? 1,
							StartedAt = ReadLong(data["startedAt"]) ?? 0,
							State = SubagentState.Stopped,
							PlanMode = ReadPlanMode(data["planMode"]),
							Restored = true,
						},
					};
					restored[name] = state;
					order.Add(name);
				}

				// started 条目才带子会话 id；start 条目先占名字。
				state.Run = state.Run with
				{
					SessionId = data["session"]?.GetValue<string>() is { Length: > 0 } sessionId
						? sessionId
						: state.Run.SessionId,
					SessionFile = data["file"]?.GetValue<string>() is { Length: > 0 } file
						? file
						: state.Run.SessionFile,
					Agent = data["agent"]?.GetValue<string>() ?? state.Run.Agent,
					Label = data["label"]?.GetValue<string>() ?? state.Run.Label,
					Brief = data["brief"]?.GetValue<string>() ?? state.Run.Brief,
					Model = data["model"]?.GetValue<string>() ?? state.Run.Model,
					Thinking = data["thinking"]?.GetValue<string>() ?? state.Run.Thinking,
					PlanMode = state.Run.PlanMode || ReadPlanMode(data["planMode"]),
					State = SubagentState.Stopped,
				};
				if (data["outputSchema"] is JsonObject schema)
				{
					state.OutputSchema = schema.DeepClone().AsObject();
				}

				if (ReadLong(data["tokensVersion"]) is { } version)
				{
					state.TokensVersion = (int)version;
				}

				continue;
			}

			if (!restored.TryGetValue(name, out var finished))
			{
				continue;
			}

			if (ReadLong(data["tokensVersion"]) is { } tokensVersion)
			{
				finished.TokensVersion = (int)tokensVersion;
			}

			var persistedStructured = data["structuredOutput"]?.DeepClone();
			if (kind is "failed" or "stopped")
			{
				persistedStructured = null;
				finished.StructuredOutput = null;
			}
			else if (persistedStructured is not null)
			{
				finished.StructuredOutput = persistedStructured;
			}

			finished.Run = finished.Run with
			{
				State = kind switch
				{
					"idle" => SubagentState.Idle,
					"failed" => SubagentState.Failed,
					_ => SubagentState.Stopped,
				},
				Turns = data["turns"]?.GetValue<int>() ?? finished.Run.Turns,
				Result = data["result"]?.GetValue<string>() is { Length: > 0 } result
					? result
					: finished.Run.Result,
				Tokens = ReadLong(data["tokens"]) ?? finished.Run.Tokens,
				Cost = ReadDouble(data["cost"]) ?? finished.Run.Cost,
				FinishedAt = ReadLong(data["finishedAt"]) ?? finished.Run.FinishedAt,
				StructuredOutput = kind is "failed" or "stopped"
					? null
					: persistedStructured ?? finished.Run.StructuredOutput,
				PlanMode = finished.Run.PlanMode || ReadPlanMode(data["planMode"]),
			};
		}

		lock (_gate)
		{
			foreach (var name in order)
			{
				var state = restored[name];
				// 恢复出来的正文算作一轮，未投递：wait 还能把它取回去一次。
				// 旧版本把 TotalTokens（含 cacheRead）写进 tokens；按会话文件重算成
				// 与标签页一致的输入 + 输出，避免历史卡片一直显示 68k。
				// 时间戳另有一条兼容路径：更早的条目没写 startedAt/finishedAt，恢复后卡片会没有总耗时。
				var needsFile = state.Run.SessionFile is { Length: > 0 } &&
					(state.TokensVersion < 2 || state.Run.StartedAt == 0 ||
					 (state.Run.IsTerminal && state.Run.FinishedAt == 0));
				if (needsFile)
				{
					var usageFile = state.Run.SessionFile!;
					try
					{
						var childManager = SessionManager.Open(usageFile);
						var recomputeUsage = state.TokensVersion < 2;
						var statistics = recomputeUsage ? new SessionStatisticsCache().Get(childManager) : null;
						var finishedAt = state.Run.FinishedAt > 0
							? state.Run.FinishedAt
							: ParseUnixMs(childManager.Entries.Count > 0
								? childManager.Entries[^1].Timestamp
								: childManager.Header?.Timestamp);
						state.Run = state.Run with
						{
							Tokens = statistics is null ? state.Run.Tokens : statistics.Input + statistics.Output,
							Cost = statistics?.Cost ?? state.Run.Cost,
							StartedAt = state.Run.StartedAt > 0
								? state.Run.StartedAt
								: ParseUnixMs(childManager.Header?.Timestamp),
							FinishedAt = state.Run.IsTerminal ? finishedAt : state.Run.FinishedAt,
						};
						state.TokensVersion = 2;
					}
					catch (Exception)
					{
						// 子会话文件缺失或损坏：保留条目里的旧值。
					}
				}

				if (state.Run.Result is { Length: > 0 } result)
				{
					state.TurnSeq = 1;
					state.ResultTurn = 1;
					if (state.Run.State == SubagentState.Idle &&
						state.StructuredOutput is null &&
						state.OutputSchema is { } schema)
					{
						var (value, error) = SubagentSchema.ParseResult(result, schema);
						if (error is null)
						{
							state.StructuredOutput = value;
							state.Run = state.Run with { StructuredOutput = value };
						}
					}
				}
				// 没建出会话的 start 条目（创建失败）不恢复。
				if (state.Run.SessionId.Length == 0 || _runs.ContainsKey(name))
				{
					continue;
				}

				_runs[name] = state;
				_order.Add(name);
			}
		}
	}

	/// <summary>用户发了一条消息：本回合的启动预算清零。回报触发的父会话轮次不算。</summary>
	public void NoteUserPrompt()
	{
		lock (_gate)
		{
			_spawns = 0;
			_autoContinue = 0;
		}
	}

	/// <summary>子会话系统提示词里的委派边界。宿主把它追加在角色提示词后面。</summary>
	public static string BuildBoundary(SubagentStartContext context)
	{
		ArgumentNullException.ThrowIfNull(context);
		var history = context.Task.Length == 0
			? "这是既有子会话的继续；之前的对话在上下文里，新消息写在后续用户消息里。\n"
			: context.Seed is { Count: > 0 }
				? "你继承了父会话到上一个完整回合为止的历史；之后的内容你看不到，本次任务写在最后一条用户消息里。\n"
				: "你看不到父会话的历史，任务全部写在第一条用户消息里。\n";
		var boundary = string.Concat(
			"你是父会话「", context.ParentTitle, "」开出的子代理「", context.Name, "」，角色「", context.Agent.Name, "」。\n",
			history,
			"把结论写进最终回复：做了什么、结果在哪、还差什么。需要用户拍板时，写出阻塞点和选项后停下；",
			"你有自己的标签页，用户可能看到也可能不看。不要只停在提问上等回答。\n",
			"不要开新的子代理，也不要把工作推回父会话。");
		if (context.OutputSchema is not { } schema)
		{
			return boundary;
		}

		return string.Concat(
			boundary,
			"\n\n最终回复必须是一个 JSON 值，符合以下 schema。不要输出其它文字，可用 ```json 围栏包裹：\n```json\n",
			schema.ToJsonString(PiJson.NodeOptions),
			"\n```");
	}

	/// <summary>本会话的委派深度；顶层为 0。</summary>
	public int Depth => _depth;

	/// <summary>测试用：等待当前排队的投递全部完成。</summary>
	internal Task DeliveryBarrier
	{
		get
		{
			lock (_gate)
			{
				return _flush;
			}
		}
	}

	/// <summary>启动一个子代理。结果不在这里返回，完成后作为消息回到父会话。</summary>
	public async Task<SubagentStartOutcome> StartAsync(
		SubagentStartRequest request,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);
		EnsureParentSubscription();
		var task = (request.Task ?? string.Empty).Trim();
		if (task.Length == 0)
		{
			return new SubagentStartOutcome(false, "task is required");
		}

		var definition = _catalog.Get((request.Agent ?? string.Empty).Trim());
		if (definition is null)
		{
			return new SubagentStartOutcome(false, UnknownAgent(request.Agent));
		}

		var context = NormalizeContext(request.Context);
		IReadOnlyList<Message>? seed = null;
		var planMode = _owner.PlanModeEnabled;
		JsonObject? outputSchema = null;
		if (request.OutputSchema is not null)
		{
			outputSchema = request.OutputSchema.DeepClone().AsObject();
			if (outputSchema.ToJsonString(PiJson.NodeOptions).Length > 32_000)
			{
				return new SubagentStartOutcome(false, "output_schema is too large; keep it under 32000 characters");
			}

			var schemaError = SubagentSchema.ValidateDefinition(outputSchema);
			if (schemaError is not null)
			{
				return new SubagentStartOutcome(false, "output_schema: " + schemaError);
			}
		}
		if (context == "fork")
		{
			seed = BuildForkSeed();
			if (seed is null)
			{
				return new SubagentStartOutcome(false, "context: fork needs a completed turn in this session; use fresh");
			}
		}

		var name = (request.Name ?? string.Empty).Trim();
		if (name.Length > 0 && !SubagentCatalog.IsValidName(name))
		{
			return new SubagentStartOutcome(false, "name must be 1-32 lower-case letters, digits or hyphens, start with a letter, and not be \"lead\"");
		}

		// 显式 label 只做展示：仍限制成一行，卡片标题不会被多行 brief 撑开。
		var label = FirstLine(request.Label ?? string.Empty, 120);
		if (label.Length == 0)
		{
			label = FirstLine(task, 80);
		}

		RunState state;
		lock (_gate)
		{
			if (_disposed)
			{
				return new SubagentStartOutcome(false, "the session is closing");
			}

			if (ActiveCountLocked() >= _maxActive)
			{
				return new SubagentStartOutcome(false, CapacityText());
			}

			if (_spawns >= _maxSpawns)
			{
				return new SubagentStartOutcome(false,
					$"cannot start another subagent: this user turn already started {_maxSpawns}. Ask the user before starting more.");
			}

			if (name.Length == 0)
			{
				name = NextNameLocked(definition.Name);
			}
			else if (_runs.ContainsKey(name))
			{
				return new SubagentStartOutcome(false, $"a subagent named \"{name}\" already exists");
			}

			state = new RunState
			{
				OutputSchema = outputSchema,
				Run = new SubagentRun
				{
					Name = name,
					Agent = definition.Name,
					Label = label,
					Brief = Clip(task, MaxBriefChars),
					Depth = _depth + 1,
					PlanMode = planMode,
					StartedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
				},
			};
			_runs[name] = state;
			_order.Add(name);
			_spawns++;
		}

		AppendDescriptor(state, "start");
		Changed?.Invoke();

		CodingAgentSession? child;
		try
		{
			child = await _host.CreateChildAsync(
				new SubagentStartContext(_owner, ParentTitle(), definition, name, task, _depth + 1, seed)
				{
					PlanMode = planMode,
					OutputSchema = outputSchema,
				},
				cancellationToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			RemoveRun(name);
			throw;
		}
		catch (Exception exception)
		{
			RemoveRun(name);
			return new SubagentStartOutcome(false, $"failed to create the subagent session: {exception.Message}");
		}

		if (child is null)
		{
			RemoveRun(name);
			return new SubagentStartOutcome(false, "this host cannot open a subagent session");
		}

		IDisposable subscription;
		try
		{
			// 订阅放在锁外：Subscribe 可能与子会话的事件派发抢同一把内部锁。
			subscription = child.Subscribe(@event => ObserveAsync(name, @event));
		}
		catch (Exception exception)
		{
			child.Dispose();
			RemoveRun(name);
			return new SubagentStartOutcome(false, $"failed to observe the subagent session: {exception.Message}");
		}

		var sessionId = child.Session.SessionId;
		lock (_gate)
		{
			if (_disposed || !_runs.TryGetValue(name, out var current))
			{
				subscription.Dispose();
				child.Dispose();
				return new SubagentStartOutcome(false, "the session is closing");
			}

			// 建会话要几百毫秒，这期间可能已经被 stop_subagent 停掉：别再起首轮。
			if (current.Stopping)
			{
				current.Run = current.Run with
				{
					SessionId = sessionId,
					SessionFile = child.Session.SessionFile,
					Model = ModelLabel(child.Model),
					Thinking = child.ThinkingLevel,
				};
				subscription.Dispose();
				child.Dispose();
				return new SubagentStartOutcome(false, $"subagent {name} was stopped before its first turn");
			}

			current.Session = child;
			current.Subscription = subscription;
			current.Run = current.Run with
			{
				SessionId = sessionId,
				SessionFile = child.Session.SessionFile,
				Model = ModelLabel(child.Model),
				Thinking = child.ThinkingLevel,
			};
		}

		Changed?.Invoke();
		AppendDescriptor(state, "started");

		// 首轮在后台跑：工具调用只等「建好并已投递」，不等子代理跑完。
		_ = RunFirstTurnAsync(name, child, task);
		return new SubagentStartOutcome(true, $"started subagent {name}", name, sessionId);
	}

	/// <summary>
	/// 等到目标子代理结束一轮或超时。省略 <paramref name="targets"/> 时等全部在跑的子代理。
	/// 已通过 wait 取走的结果不再单独唤醒父会话，避免同一段正文进两次上下文。
	/// </summary>
	/// <param name="targets">目标子代理名或会话 id；省略时等全部在跑的。</param>
	/// <param name="timeout">最长等待。</param>
	/// <param name="cancellationToken">取消。</param>
	/// <param name="progress">等待期间的进度快照；界面用它显示「等的是谁、在干什么」。</param>
	public Task<SubagentOutcome> WaitAsync(
		IReadOnlyList<string>? targets,
		TimeSpan timeout,
		CancellationToken cancellationToken = default,
		Action<string>? progress = null) =>
		WaitAsync(targets, timeout, any: false, cancellationToken, progress);

	/// <summary>
	/// 等到目标子代理结束一轮或超时；<paramref name="any"/> 为 true 时第一个结束就返回。
	/// </summary>
	/// <param name="targets">目标子代理名或会话 id；省略时等全部在跑的。</param>
	/// <param name="timeout">最长等待。</param>
	/// <param name="any">第一个目标结束就返回；其余仍在跑的目标保持未投递。</param>
	/// <param name="cancellationToken">取消。</param>
	/// <param name="progress">等待期间的进度快照；界面用它显示「等的是谁、在干什么」。</param>
	public async Task<SubagentOutcome> WaitAsync(
		IReadOnlyList<string>? targets,
		TimeSpan timeout,
		bool any,
		CancellationToken cancellationToken = default,
		Action<string>? progress = null)
	{
		// 心跳与完成通知可能从不同线程推进度，回调本身串行化；上层 OnUpdate 也不是线程安全的。
		var progressGate = new Lock();
		void Report(string text)
		{
			if (progress is null)
			{
				return;
			}

			lock (progressGate)
			{
				progress(text);
			}
		}

		List<RunState> waiting;
		var completedTurns = new Dictionary<string, TurnResult>(StringComparer.Ordinal);
		var pending = new List<(RunState State, Task<TurnResult> Task)>();
		lock (_gate)
		{
			if (_disposed)
			{
				return new SubagentOutcome(false, "the session is closing");
			}

			var resolved = ResolveLocked(targets);
			if (resolved.Count == 0)
			{
				return new SubagentOutcome(false, targets is { Count: > 0 }
					? "no subagent matched: " + string.Join(", ", targets)
					: "no subagents");
			}

			waiting = resolved;
			foreach (var state in waiting)
			{
				// 状态还是 idle、但子会话已经在新一轮里：send 刚投递、AgentStart 还没到，
				// 不能拿上一次运行的正文立即返回。failed/stopped 即使还在 abort 也直接出快照。
				if (state.Run.State is not (SubagentState.Starting or SubagentState.Running) &&
					(state.Run.State != SubagentState.Idle || state.Session is not { IsRunning: true }))
				{
					// 已经结束的目标在调用时就确定：wait 期间用户可能在子标签里再起一次运行。
					completedTurns[state.Run.Name] = new TurnResult(
						state.ResultTurn,
						state.Run.Result ?? string.Empty,
						state.StructuredOutput);
				}
				else
				{
					if (state.Run.IsTerminal && state.Completion.Task.IsCompleted)
					{
						state.Completion = new TaskCompletionSource<TurnResult>(TaskCreationOptions.RunContinuationsAsynchronously);
					}

					pending.Add((state, state.Completion.Task));
				}
			}
		}

		var taskNames = new Dictionary<Task<TurnResult>, string>(pending.Count);
		foreach (var (state, task) in pending)
		{
			taskNames[task] = state.Run.Name;
		}

		var timedOut = false;
		// any 只要有一个目标已经结束就立即返回；继续等 pending 会把「等一个」变成「等全部」。
		if (pending.Count > 0 && !(any && completedTurns.Count > 0))
		{
			Report(ProgressText(waiting, "waiting"));

			// .NET 9 的 Task.WhenEach：完成一个就推进一次。再加一条定时汇报，
			// 让「一个子代理跑十分钟」也不至于整段没有进度。
			using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			deadline.CancelAfter(timeout);
			using var heartbeat = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
			var reporter = ReportProgressAsync(waiting, progress is null ? null : Report, heartbeat.Token);
			var tasks = pending.Select(static item => item.Task).ToArray();
			try
			{
				if (any)
				{
					// 第一个结束就拿到结果；其余任务不 await，免得把「等一个」拖成「等全部」。
					var first = await Task.WhenAny(tasks).WaitAsync(deadline.Token).ConfigureAwait(false);
					if (first.IsCompletedSuccessfully && taskNames.TryGetValue(first, out var name))
					{
						completedTurns[name] = first.Result;
					}

					Report(ProgressText(waiting, "waiting"));
				}
				else
				{
					await foreach (var completed in Task.WhenEach(tasks).WithCancellation(deadline.Token).ConfigureAwait(false))
					{
						if (completed.IsCompletedSuccessfully && taskNames.TryGetValue(completed, out var name))
						{
							completedTurns[name] = completed.Result;
						}

						Report(ProgressText(waiting, "waiting"));
					}
				}
			}
			catch (OperationCanceledException)
			{
				cancellationToken.ThrowIfCancellationRequested();
				timedOut = true;
			}
			finally
			{
				await heartbeat.CancelAsync().ConfigureAwait(false);
				await reporter.ConfigureAwait(false);
			}
		}

		// 超时、被移除或任务异常：用当前状态补一份快照，别让结果段落空着。
		var stillRunning = 0;
		lock (_gate)
		{
			foreach (var state in waiting)
			{
				if (!completedTurns.ContainsKey(state.Run.Name))
				{
					completedTurns[state.Run.Name] = new TurnResult(
						state.ResultTurn,
						state.Run.Result ?? string.Empty,
						state.StructuredOutput);
				}

				if (state.Run.IsActive)
				{
					stillRunning++;
				}
			}
		}


		var details = new JsonArray();
		var text = new StringBuilder(256);
		if (timedOut)
		{
			text.Append("wait_subagents timed out; some subagents are still running.");
		}
		else if (any && stillRunning > 0)
		{
			text.Append("wait_subagents returned early; ").Append(stillRunning).Append(" subagents are still running.");
		}
		else
		{
			text.Append("wait_subagents");
		}

		text.Append('\n');
		var budget = MaxWaitChars;
		var results = new StringBuilder(256);
		lock (_gate)
		{
			foreach (var state in waiting)
			{
				var run = state.Run;
				if (!completedTurns.TryGetValue(run.Name, out var turnResult))
				{
					turnResult = new TurnResult(0, string.Empty);
				}

				// 已投递按轮算：同一子代理第二轮完成后，上一轮的标记不会把它吞掉。
				var alreadyDelivered = turnResult.Turn > 0 && state.DeliveredTurn >= turnResult.Turn;
				var row = new JsonObject
				{
					["name"] = run.Name,
					["agent"] = run.Agent,
					["label"] = run.Label,
					["model"] = run.Model,
					["thinking"] = run.Thinking,
					["state"] = StateName(run.State),
					["restored"] = run.Restored,
					["turns"] = run.Turns,
					["tokens"] = run.Tokens,
					["cost"] = run.Cost,
					["elapsed_ms"] = ElapsedMs(run),
					["session_id"] = run.SessionId,
					["session_file"] = run.SessionFile,
					["structured_output"] = turnResult.Structured,
					["activity"] = run.Activity,
					["progress"] = run.Progress,
					["awaiting_user"] = run.AwaitingUser,
					// returned：这次工具调用把正文带回去了，所以它不会再走消息投递。
					// 桌面据此补一张只读回报卡；delivered 是「调用前就已经投递过」，两者不同。
					["delivered"] = alreadyDelivered,
					["returned"] = false,
					["result"] = turnResult.Text is { Length: > 0 } waitResult ? Clip(waitResult, MaxCardChars) : null,
				};
				details.Add((JsonNode)row);

				text.Append("- ").Append(run.Name).Append(" (").Append(run.Agent).Append(") · ")
					.Append(StateName(run.State)).Append(" · ").Append(run.Turns).Append(" turns");
				if (run.AwaitingUser)
				{
					text.Append(" · waiting for user input");
				}

				if (run.Activity is { Length: > 0 } activity)
				{
					text.Append(" · ").Append(activity);
				}


				if (turnResult.Text is not { Length: > 0 } result)
				{
					text.Append('\n');
					continue;
				}

				if (alreadyDelivered)
				{
					// 已经进过上下文：只报状态，避免同一段正文再进一次。
					text.Append(" · result already delivered\n");
					continue;
				}

				if (budget <= 0)
				{
					// 不标已投递：下一次 wait 还能把这段正文带走。
					text.Append(" · result omitted: wait budget reached; call wait_subagents again\n");
					continue;
				}

				text.Append('\n');
				var limit = Math.Min(budget, MaxResultChars);
				var body = Clip(result, limit);
				var truncated = result.Length > limit;
				budget -= body.Length;
				results.Append("\n--- ").Append(run.Name).Append(" (").Append(run.Agent).Append(") ---\n")
					.Append(body).Append('\n');
				if (turnResult.Structured is { } structured)
				{
					results.Append("structured_output:\n")
						.Append(structured.ToJsonString(PiJson.NodeOptions))
						.Append('\n');
				}

				if (truncated)
				{
					results.Append("（已截断，完整正文：")
						.Append(run.SessionFile ?? "子会话文件")
						.Append("）\n");
				}

				state.DeliveredTurn = turnResult.Turn;
				// 标记这段正文已随本次工具结果返回，桌面据此补只读卡。
				row["returned"] = true;
			}
		}

		if (timedOut)
		{
			text.Append("\nEnd your turn instead of waiting again; the result arrives as a message when it finishes.");
		}
		else if (any && stillRunning > 0)
		{
			text.Append("\nCall wait_subagents again to collect the rest; results not returned here arrive as messages when this turn ends.");
		}

		text.Append(results);
		return new SubagentOutcome(true, text.ToString().TrimEnd(), new JsonObject
		{
			["timed_out"] = timedOut,
			["any"] = any,
			["still_running"] = stillRunning,
			["subagents"] = details,
		});
	}

	/// <summary>列本会话开出的子代理；给 <c>list_subagents</c> 用。</summary>
	/// <param name="details">同一份列表的结构化形式，给界面与程序调用方用。</param>
	public string Describe(out JsonObject details)
	{
		var rows = new JsonArray();
		var text = new StringBuilder(256);
		lock (_gate)
		{
			text.Append(_order.Count == 0 ? "no subagents in this session" : $"subagents: {_order.Count}");
			text.Append('\n');
			foreach (var name in _order)
			{
				var run = _runs[name].Run;
				rows.Add((JsonNode)new JsonObject
				{
					["name"] = run.Name,
					["agent"] = run.Agent,
					["session_id"] = run.SessionId,
					["session_file"] = run.SessionFile,
					["state"] = StateName(run.State),
					["turns"] = run.Turns,
					["tokens"] = run.Tokens,
					["cost"] = run.Cost,
					["elapsed_ms"] = ElapsedMs(run),
					["structured_output"] = run.StructuredOutput,
					["label"] = run.Label,
					["model"] = run.Model,
					["thinking"] = run.Thinking,
					["activity"] = run.Activity,
					["progress"] = run.Progress,
					["awaiting_user"] = run.AwaitingUser,
					["restored"] = run.Restored,
					["resumable"] = run.SessionFile is { Length: > 0 },
					["result"] = run.Result is { Length: > 0 } result ? Clip(result, 400) : null,
				});

				text.Append("- ").Append(run.Name).Append(" (").Append(run.Agent).Append(") · ")
					.Append(StateName(run.State))
					.Append(" · ").Append(run.Turns).Append(" turns");
				if (run.AwaitingUser)
				{
					text.Append(" · waiting for user input");
				}

				if (run.Activity is { Length: > 0 } activity)
				{
					text.Append(" · ").Append(activity);
				}

				if (run.Progress is { Length: > 0 } progress)
				{
					text.Append(" · ").Append(Clip(progress, 80));
				}

				if (run.Label.Length > 0)
				{
					text.Append(" · ").Append(run.Label);
				}

				if (run.SessionId.Length > 0)
				{
					text.Append(" · ").Append(run.SessionId);
				}

				// 历史项能不能继续发消息，就看会话文件还在不在。
				if (run.Restored && run.SessionFile is { Length: > 0 })
				{
					text.Append(" · resumable");
				}

				text.Append('\n');
			}
		}

		var available = new JsonArray();
		var listed = 0;
		text.Append("agents:\n");
		foreach (var definition in _catalog.All)
		{
			available.Add((JsonNode)new JsonObject
			{
				["name"] = definition.Name,
				["description"] = definition.Description,
				["advertise"] = definition.Advertise == true,
			});

			if (listed >= MaxListedAgents)
			{
				continue;
			}

			// 未广告的角色只能在这里被发现；只给名字不够，模型不知道它是做什么的。
			text.Append("- ").Append(definition.Name).Append(": ")
				.Append(Clip(definition.Description, MaxAgentDescriptionChars));
			if (definition.Advertise != true)
			{
				text.Append(" (not advertised)");
			}

			text.Append('\n');
			listed++;
		}

		if (_catalog.All.Count == 0)
		{
			text.Append("- (none)\n");
		}
		else if (_catalog.All.Count > listed)
		{
			text.Append("- +").Append(_catalog.All.Count - listed).Append(" more\n");
		}

		details = new JsonObject
		{
			["subagents"] = rows,
			["available"] = available,
		};
		return text.ToString();
	}

	/// <summary>读子会话最近的助手正文；结果被截断或重启后需要全文时用。</summary>
	/// <param name="target">子代理名或会话 id。</param>
	/// <param name="maxChars">正文上限；小于等于零用默认 12k，最大 48k。</param>
	public SubagentOutcome Read(string target, int maxChars = MaxResultChars)
	{
		RunState? state;
		CodingAgentSession? session;
		string? file;
		lock (_gate)
		{
			state = ResolveOneLocked(target);
			if (state is null)
			{
				return new SubagentOutcome(false, $"no subagent matched \"{target}\"");
			}

			session = state.Session;
			file = state.Run.SessionFile;
		}

		var limit = maxChars > 0 ? Math.Min(maxChars, MaxReadChars) : MaxResultChars;
		IReadOnlyList<Message> messages = [];
		try
		{
			if (session is { IsDisposed: false })
			{
				messages = session.ProjectedMessages;
			}
			else if (file is { Length: > 0 })
			{
				messages = SessionManager.Open(file).BuildProjectedMessages();
			}
		}
		catch (Exception)
		{
			// 文件被删或格式坏了：下面退回运行里保存的最后一份正文。
			messages = [];
		}

		var texts = new List<string>();
		var used = 0;
		var truncated = false;
		for (var index = messages.Count - 1; index >= 0 && texts.Count < 40; index--)
		{
			if (messages[index] is not AssistantMessage assistant)
			{
				continue;
			}

			var text = MessageText.Of(assistant);
			if (text.Length == 0)
			{
				continue;
			}

			if (used + text.Length > limit)
			{
				if (texts.Count == 0)
				{
					texts.Add(Clip(text, limit));
					used = limit;
				}

				truncated = true;
				break;
			}

			texts.Add(text);
			used += text.Length;
		}

		texts.Reverse();
		var run = state.Run;
		var body = texts.Count > 0 ? string.Join("\n\n---\n\n", texts) : string.Empty;
		if (body.Length == 0 && run.Result is { Length: > 0 } result)
		{
			body = Clip(result, limit);
			truncated = result.Length > limit;
		}

		var builder = new StringBuilder(256);
		builder.Append("subagent ").Append(run.Name).Append(" (").Append(run.Agent).Append(") · ")
			.Append(StateName(run.State)).Append(" · ").Append(run.Turns).Append(" turns");
		if (run.SessionId.Length > 0)
		{
			builder.Append(" · session_id: ").Append(run.SessionId);
		}

		if (file is { Length: > 0 })
		{
			builder.Append(" · file: ").Append(file);
		}

		builder.Append('\n');
		if (body.Length == 0)
		{
			builder.Append("no assistant text yet");
		}
		else
		{
			builder.Append(body);
			if (truncated)
			{
				builder.Append("\n\n（已截断，完整正文在会话文件里。）");
			}
		}

		return new SubagentOutcome(true, builder.ToString(), new JsonObject
		{
			["name"] = run.Name,
			["agent"] = run.Agent,
			["state"] = StateName(run.State),
			["turns"] = run.Turns,
			["session_id"] = run.SessionId,
			["session_file"] = file,
			// 正文已经在工具结果里进过一次会话文件，这里只留画卡够用的预览。
			["text"] = Clip(body, MaxCardChars),
			["chars"] = body.Length,
			["truncated"] = truncated,
		});
	}

	/// <summary>给子代理发一条消息。默认排 follow-up，也可以 steer 当前运行；不等回复。</summary>
	/// <remarks>会话没打开（重启后的历史项）时先冷恢复：按会话文件重建子会话再投递。</remarks>
	public async Task<SubagentOutcome> SendAsync(
		string target,
		string message,
		string? mode = null,
		CancellationToken cancellationToken = default)
	{
		var text = (message ?? string.Empty).Trim();
		if (text.Length == 0)
		{
			return new SubagentOutcome(false, "message is required");
		}

		var resolvedMode = (mode ?? string.Empty).Trim().ToLowerInvariant();
		if (resolvedMode.Length == 0)
		{
			resolvedMode = "follow_up";
		}

		if (resolvedMode is not ("follow_up" or "steer" or "auto"))
		{
			return new SubagentOutcome(false, "mode must be steer, follow_up or auto");
		}

		RunState? state;
		bool running;
		bool canSteer;
		lock (_gate)
		{
			state = ResolveOneLocked(target);
			running = state is not null &&
				(state.Run.IsActive || state.Session is { IsRunning: true });
			// steer 只认「正在跑」：状态已经回 idle、但 PromptAsync 还没完全退栈时不收，
			// 否则消息会被塞进一个已经结束的回合。
			canSteer = state?.Run.State == SubagentState.Running && state.Session is { IsRunning: true };
		}

		if (state is null)
		{
			return new SubagentOutcome(false, $"no subagent matched \"{target}\"");
		}

		// 停掉/超时的回合可能还在 abort：这时把消息排进 follow-up 会落在一个
		// 不再轮询队列的运行里，既送不到也不报错。让调用方等一下子再发。
		if (state.Run.State is SubagentState.Stopped or SubagentState.Failed &&
			state.Session is { IsRunning: true })
		{
			return new SubagentOutcome(false, $"{state.Run.Name} is still stopping; wait for it to settle before sending");
		}

		if (resolvedMode == "steer" && !canSteer)
		{
			return new SubagentOutcome(false, $"cannot steer {state.Run.Name}: it is not running; use mode \"follow_up\"");
		}

		if (resolvedMode == "auto")
		{
			resolvedMode = canSteer ? "steer" : "follow_up";
		}

		// 给空闲子代理发消息等于开一轮：与 delegate 共用同一个并发上限。
		// 运行中的子代理只是排 follow-up 或 steer，不占新的并发名额。
		if (!running)
		{
			lock (_gate)
			{
				if (ActiveCountLocked() >= _maxActive)
				{
					return new SubagentOutcome(false,
						$"cannot send to {state.Run.Name}: {_maxActive} subagents are already active. Wait for one to finish or stop it.");
				}
			}
		}

		// 停过、关过标签的子代理照样能继续：会话文件还在就冷恢复。
		// stop_subagent 停的是当前回合，不是这段关系。

		CodingAgentSession? session;
		lock (_gate)
		{
			session = state.Session;
		}

		if (session is not { IsDisposed: false })
		{
			SemaphoreSlim reopenGate;
			lock (_gate)
			{
				reopenGate = state.ReopenGate ??= new SemaphoreSlim(1, 1);
			}

			await reopenGate.WaitAsync(cancellationToken).ConfigureAwait(false);
			try
			{
				// 排队期间别的调用方可能已经把它打开；先看一次，别重复建标签。
				lock (_gate)
				{
					session = state.Session;
				}

				if (session is not { IsDisposed: false })
				{
					session = await ReopenAsync(state, cancellationToken).ConfigureAwait(false);
				}
			}
			finally
			{
				reopenGate.Release();
			}
		}

		if (session is null)
		{
			return new SubagentOutcome(false,
				"the subagent session is not open and cannot be reopened; its transcript is in the session file");
		}

		lock (_gate)
		{
			// 不动 Completion、Result 与已投递轮次：正在等待上一轮的 wait 必须继续等这一轮收尾，
			// 新一轮开始时 SetRunning 会根据「上一轮的 TCS 已完成」换一个新的。
			// Stopping 只属于被停的那一轮，新消息要能正常收尾。
			state.Stopping = false;
			state.TimedOut = false;
		}

		var outgoing = new UserMessage
		{
			Content = MessageContent.FromText($"来自父会话「{ParentTitle()}」：\n{text}"),
			Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
			CustomType = MessageType,
		};

		// steer 只在运行中有效：打进当前回合的 steering 队列，下一个工具边界被模型看到。
		if (resolvedMode == "steer")
		{
			session.Steer(outgoing);
			return new SubagentOutcome(true, $"steered {state.Run.Name}");
		}

		// 首轮还没真正开始（建会话到 AgentStart 之间）：直接排队。
		// 这时 DeliverMessageAsync 看到 IsRunning=false 会再起一个 PromptAsync，和首轮抢子会话。
		if (state.Run.State == SubagentState.Starting)
		{
			session.FollowUp(outgoing);
			return new SubagentOutcome(true, $"queued for {state.Run.Name}");
		}

		// 只投递不等结果：send 的语义是「排进去」，结果照常走完成消息。
		_ = DeliverQuietlyAsync(session, outgoing);
		return new SubagentOutcome(true, $"sent to {state.Run.Name}");
	}

	/// 冷恢复：宿主按会话文件重建子会话，重建后重新订阅事件。
	private async Task<CodingAgentSession?> ReopenAsync(RunState state, CancellationToken cancellationToken)
	{
		var run = state.Run;
		if (run.SessionFile is not { Length: > 0 } file)
		{
			return null;
		}

		var definition = _catalog.Get(run.Agent) ?? new SubagentDefinition
		{
			Name = run.Agent,
			Description = run.Label,
			Source = "restored",
		};

		CodingAgentSession? child;
		try
		{
			child = await _host.ReopenChildAsync(
				new SubagentReopenContext(
					_owner,
					ParentTitle(),
					definition,
					run.Name,
					file,
					run.Depth,
					run.SessionId)
				{
					// 恢复时父会话还在计划模式，或者这条关系本来就是在计划模式下建的：继续只读。
					PlanMode = run.PlanMode || _owner.PlanModeEnabled,
					OutputSchema = state.OutputSchema?.DeepClone().AsObject(),
				},
				cancellationToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception)
		{
			// 宿主打不开（文件被删、格式坏了）：按「会话未打开」回绝，别把异常抛给工具调用。
			return null;
		}

		if (child is null)
		{
			return null;
		}

		IDisposable subscription;
		try
		{
			// 订阅放在锁外：Subscribe 可能与子会话的事件派发抢同一把内部锁。
			subscription = child.Subscribe(@event => ObserveAsync(run.Name, @event));
		}
		catch (Exception)
		{
			child.Dispose();
			return null;
		}

		lock (_gate)
		{
			if (_disposed || !_runs.TryGetValue(run.Name, out var current) || current.Run.SessionId != run.SessionId)
			{
				subscription.Dispose();
				child.Dispose();
				return null;
			}

			current.Session = child;
			current.Subscription = subscription;
			// Stopping 只描述「当前这一轮被叫停」，冷恢复之后新的一轮要能正常收尾。
			current.Stopping = false;
			current.ActiveTools.Clear();
			current.Run = current.Run with
			{
				Restored = false,
				State = SubagentState.Idle,
				PlanMode = run.PlanMode || _owner.PlanModeEnabled,
				Activity = null,
				Progress = null,
				AwaitingUser = false,
				Model = ModelLabel(child.Model),
				Thinking = child.ThinkingLevel,
				SessionFile = child.Session.SessionFile ?? run.SessionFile,
			};
		}

		Changed?.Invoke();
		return child;
	}

	/// <summary>停掉一个子代理。已经结束的再停一次是空操作。</summary>
	public SubagentOutcome Stop(string target)
	{
		RunState? state;
		CodingAgentSession? session;
		lock (_gate)
		{
			state = ResolveOneLocked(target);
			if (state is null)
			{
				return new SubagentOutcome(false, $"no subagent matched \"{target}\"");
			}

			if (state.Run.State == SubagentState.Stopped)
			{
				return new SubagentOutcome(true, $"{state.Run.Name} is already stopped");
			}

			state.Stopping = true;
			CancelDeadlineLocked(state);
			state.ActiveTools.Clear();
			state.Run = state.Run with
			{
				State = SubagentState.Stopped,
				Activity = null,
				Progress = null,
				AwaitingUser = false,
				FinishedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
			};
			state.Completion.TrySetResult(new TurnResult(0, string.Empty));
			session = state.Session;
		}

		AppendDescriptor(state, "stopped");
		Changed?.Invoke();
		try
		{
			session?.Abort();
		}
		catch (Exception)
		{
			// 子会话可能已经在关闭；状态已经落成 stopped。
		}

		return new SubagentOutcome(true, $"stopped {state.Run.Name}");
	}

	/// <summary>子会话被宿主关掉：关系保留为 stopped，不再订阅。</summary>
	public void NoteChildClosed(string sessionId)
	{
		RunState? state = null;
		lock (_gate)
		{
			if (_disposed)
			{
				return;
			}

			foreach (var candidate in _runs.Values)
			{
				if (string.Equals(candidate.Run.SessionId, sessionId, StringComparison.Ordinal))
				{
					state = candidate;
					break;
				}
			}

			if (state is null)
			{
				return;
			}

			state.Subscription?.Dispose();
			state.Subscription = null;
			state.Session = null;
			state.ActiveTools.Clear();
			CancelDeadlineLocked(state);
			// 已排队的 AgentEnd 可能在 Dispose 之后才到：标成 Stopping，迟到的收尾只能落 Stopped，
			// 不能把已关闭的子会话又改回 Idle 并投递结果。
			state.Stopping = true;
			state.TimedOut = false;
			// 子会话标签关掉后不能继续发消息，状态一律落到 stopped。
			state.Run = state.Run with
			{
				State = SubagentState.Stopped,
				Activity = null,
				Progress = null,
				AwaitingUser = false,
				FinishedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
			};
			state.Completion.TrySetResult(new TurnResult(0, string.Empty));
		}

		Changed?.Invoke();
	}

	/// <summary>
	/// 子会话开始或结束等待用户回答：只更新展示状态。找不到对应运行（历史项、关联会话）时返回 null。
	/// </summary>
	/// <param name="sessionId">子会话 id。</param>
	/// <param name="awaiting">是否正在等回答。</param>
	public SubagentRun? NoteChildAwaiting(string sessionId, bool awaiting)
	{
		SubagentRun? snapshot;
		lock (_gate)
		{
			if (_disposed)
			{
				return null;
			}

			RunState? state = null;
			foreach (var candidate in _runs.Values)
			{
				if (string.Equals(candidate.Run.SessionId, sessionId, StringComparison.Ordinal))
				{
					state = candidate;
					break;
				}
			}

			if (state is null || state.Run.IsTerminal)
			{
				return null;
			}

			if (state.Run.AwaitingUser == awaiting)
			{
				return null;
			}

			state.Run = state.Run with { AwaitingUser = awaiting };
			snapshot = state.Run;
		}

		Changed?.Invoke();
		return snapshot;
	}

	public void Dispose()
	{
		IDisposable[] subscriptions;
		TaskCompletionSource<TurnResult>[] completions;
		CodingAgentSession[] active;
		IDisposable? parentSubscription;
		lock (_gate)
		{
			if (_disposed)
			{
				return;
			}

			_disposed = true;
			parentSubscription = _parentSubscription;
			_parentSubscription = null;
			subscriptions = [.. _runs.Values.Select(static state => state.Subscription).OfType<IDisposable>()];
			completions = [.. _runs.Values.Select(static state => state.Completion)];
			active = [.. _runs.Values
				.Where(static state => state.Run.IsActive)
				.Select(static state => state.Session)
				.OfType<CodingAgentSession>()];
			foreach (var state in _runs.Values)
			{
				CancelDeadlineLocked(state);
				if (state.Run.IsActive)
				{
					// 父会话关了，结果已经无处投递：停掉正在跑的子代理，别继续烧 token。
					// 子会话标签与文件保留，用户仍可在子标签里读或继续。
					state.Stopping = true;
					state.Run = state.Run with
					{
						State = SubagentState.Stopped,
						FinishedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
					};
					state.Completion.TrySetResult(new TurnResult(0, string.Empty));
				}
			}

			_pending.Clear();
		}

		foreach (var completion in completions)
		{
			completion.TrySetResult(new TurnResult(0, string.Empty));
		}

		parentSubscription?.Dispose();
		foreach (var subscription in subscriptions)
		{
			subscription.Dispose();
		}

		foreach (var session in active)
		{
			try
			{
				session.Abort();
			}
			catch (Exception)
			{
				// 子会话可能已经在关闭。
			}
		}
	}

	private ValueTask ObserveAsync(string name, AgentEvent @event)
	{
		switch (@event)
		{
			case AgentStartEvent:
				SetRunning(name);
				break;

			case ToolExecutionStartEvent tool:
				StartTool(name, tool.ToolCallId, DescribeTool(tool.ToolName, tool.Arguments));
				break;

			case ToolExecutionEndEvent tool:
				// 并行工具里一个结束不代表都结束：仍活跃的按最后开始的显示。
				EndTool(name, tool.ToolCallId);
				if (string.Equals(tool.ToolName, "ask_question", StringComparison.Ordinal))
				{
					ClearAwaiting(name);
				}

				break;

			case MessageEndEvent { Message: AssistantMessage assistant }:
				// 每完成一条助手消息就更新进度：长回合里 parent 的 wait 与卡片能看到它写到了哪。
				SetProgress(name, MessageText.Of(assistant));
				break;

			case AgentEndEvent { WillRetry: false } end:
			{
				var last = MessageText.LastAssistant(end.Messages);
				var text = last is null ? string.Empty : MessageText.Of(last);
				var failed = last?.StopReason is StopReason.Error or StopReason.Aborted;
				if (failed && text.Length == 0)
				{
					text = last?.ErrorMessage ?? string.Empty;
				}

				CompleteRun(name, text, failed);
				break;
			}
		}

		return ValueTask.CompletedTask;
	}

	private void SetRunning(string name)
	{
		RunState? running;
		lock (_gate)
		{
			if (!_runs.TryGetValue(name, out var state) || state.Stopping)
			{
				return;
			}

			if (state.Run.State == SubagentState.Running)
			{
				return;
			}

			if (state.Completion.Task.IsCompleted)
			{
				// 上一轮的 waiter 已经拿到结果；新一轮换一个 TCS，轮次标记不动。
				state.Completion = new TaskCompletionSource<TurnResult>(TaskCreationOptions.RunContinuationsAsynchronously);
			}

			state.ActiveTools.Clear();
			state.Run = state.Run with
			{
				State = SubagentState.Running,
				Activity = null,
				Progress = null,
				AwaitingUser = false,
			};
			running = state;
		}

		StartDeadline(name, running);
		Changed?.Invoke();
	}

	/// 每轮运行一个截止时间：卡死的子代理不会无限烧 token。
	private void StartDeadline(string name, RunState state)
	{
		var deadline = new CancellationTokenSource();
		lock (_gate)
		{
			if (_disposed || !_runs.TryGetValue(name, out var current) || !ReferenceEquals(current, state) ||
				state.Run.State != SubagentState.Running)
			{
				deadline.Dispose();
				return;
			}

			CancelDeadlineLocked(state);
			state.Deadline = deadline;
		}

		_ = EnforceDeadlineAsync(name, state, deadline);
	}

	private async Task EnforceDeadlineAsync(string name, RunState state, CancellationTokenSource deadline)
	{
		try
		{
			await Task.Delay(_runTimeout, deadline.Token).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			return;
		}

		CodingAgentSession? session;
		long turn;
		string text;
		lock (_gate)
		{
			if (_disposed || !_runs.TryGetValue(name, out var current) || !ReferenceEquals(current, state) ||
				!ReferenceEquals(state.Deadline, deadline) ||
				state.Run.State is not (SubagentState.Starting or SubagentState.Running))
			{
				return;
			}

			state.TimedOut = true;
			state.Stopping = true;
			state.StructuredOutput = null;
			state.ActiveTools.Clear();
			state.ResultTurn = ++state.TurnSeq;
			turn = state.ResultTurn;
			text = TimeoutText();
			state.Run = state.Run with
			{
				State = SubagentState.Failed,
				Turns = state.Run.Turns + 1,
				Result = text,
				StructuredOutput = null,
				Activity = null,
				Progress = null,
				AwaitingUser = false,
				FinishedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
			};
			state.Completion.TrySetResult(new TurnResult(turn, text));
			state.Deadline = null;
			session = state.Session;
		}

		deadline.Dispose();
		if (session is not null)
		{
			var usage = UsageOf(session);
			lock (_gate)
			{
				state.Run = state.Run with { Tokens = usage.Tokens, Cost = usage.Cost };
			}
		}

		try
		{
			AppendDescriptor(state, "failed");
			Changed?.Invoke();
			QueueDelivery(new PendingResult(name, true, text, turn));
		}
		catch (Exception)
		{
			// 这是一个 fire-and-forget 的超时任务；异常不能变成未观察的任务异常。
		}

		try
		{
			session?.Abort();
		}
		catch (Exception)
		{
			// 子会话可能已经在关闭；结果已经写进状态与队列。
		}
	}

	private static void CancelDeadlineLocked(RunState state)
	{
		var deadline = state.Deadline;
		state.Deadline = null;
		if (deadline is null)
		{
			return;
		}

		try
		{
			deadline.Cancel();
		}
		catch (ObjectDisposedException)
		{
			// 超时路径已经释放过这个 CTS。
		}

		deadline.Dispose();
	}

	private string TimeoutText() => _runTimeout >= TimeSpan.FromMinutes(1)
		? $"subagent timed out after {_runTimeout.TotalMinutes:0.#} minutes"
		: $"subagent timed out after {_runTimeout.TotalSeconds:0.#} seconds";

	/// 子代理此刻在干什么：工具名加一个能认出来的参数，卡片与 list_subagents 都用它。
	/// 并行工具调用各自记账：结束一个时若还有别的在跑，Activity 换成剩下最后开始的那个。
	private void StartTool(string name, string callId, string description)
	{
		lock (_gate)
		{
			if (!_runs.TryGetValue(name, out var state) || state.Stopping || state.Run.IsTerminal)
			{
				return;
			}

			RemoveToolLocked(state, callId);
			state.ActiveTools.Add((callId, description));
			state.Run = state.Run with { Activity = description };
		}

		Changed?.Invoke();
	}

	private void EndTool(string name, string callId)
	{
		lock (_gate)
		{
			if (!_runs.TryGetValue(name, out var state) || state.Stopping || state.Run.IsTerminal)
			{
				return;
			}

			if (!RemoveToolLocked(state, callId))
			{
				return;
			}

			var activity = state.ActiveTools.Count > 0 ? state.ActiveTools[^1].Description : null;
			if (string.Equals(state.Run.Activity, activity, StringComparison.Ordinal))
			{
				return;
			}

			state.Run = state.Run with { Activity = activity };
		}

		Changed?.Invoke();
	}

	/// 助手正文的进度摘要：只取首行，避免进度把卡片与 wait 的心跳撑成多行。
	private void SetProgress(string name, string text)
	{
		var progress = FirstLine(text, MaxProgressChars);
		if (progress.Length == 0)
		{
			return;
		}

		lock (_gate)
		{
			if (!_runs.TryGetValue(name, out var state) || state.Stopping || state.Run.IsTerminal ||
				string.Equals(state.Run.Progress, progress, StringComparison.Ordinal))
			{
				return;
			}

			state.Run = state.Run with { Progress = progress };
		}

		Changed?.Invoke();
	}

	private void ClearAwaiting(string name)
	{
		var changed = false;
		lock (_gate)
		{
			if (_runs.TryGetValue(name, out var state) && state.Run.AwaitingUser)
			{
				state.Run = state.Run with { AwaitingUser = false };
				changed = true;
			}
		}

		if (changed)
		{
			Changed?.Invoke();
		}
	}

	private static bool RemoveToolLocked(RunState state, string callId)
	{
		for (var index = 0; index < state.ActiveTools.Count; index++)
		{
			if (string.Equals(state.ActiveTools[index].Id, callId, StringComparison.Ordinal))
			{
				state.ActiveTools.RemoveAt(index);
				return true;
			}
		}

		return false;
	}

	private static string DescribeTool(string tool, JsonObject arguments)
	{
		foreach (var key in ActivityKeys)
		{
			if (arguments.TryGetPropertyValue(key, out var node) &&
				node is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0)
			{
				return string.Concat(tool, " ", Clip(text.Replace('\n', ' ').Trim(), ActivityMaxChars));
			}
		}

		return tool;
	}

	private static readonly string[] ActivityKeys = ["path", "file_path", "command", "pattern", "query", "url", "id"];

	private const int ActivityMaxChars = 60;

	private void CompleteRun(string name, string text, bool failed)
	{
		RunState? state;
		var stopped = false;
		lock (_gate)
		{
			if (!_runs.TryGetValue(name, out state))
			{
				return;
			}

			// 超时已经写过失败结果：迟到的 AgentEnd 不能再把状态改回 idle。
			if (state.TimedOut)
			{
				return;
			}

			CancelDeadlineLocked(state);
			state.ActiveTools.Clear();

			if (state.Stopping)
			{
				state.StructuredOutput = null;
				state.Run = state.Run with
				{
					State = SubagentState.Stopped,
					StructuredOutput = null,
					Activity = null,
					Progress = null,
					AwaitingUser = false,
					FinishedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
				};
				state.Completion.TrySetResult(new TurnResult(0, string.Empty));
				stopped = true;
			}
			else
			{
				JsonNode? structured = null;
				if (!failed && state.OutputSchema is { } outputSchema)
				{
					var (value, schemaError) = SubagentSchema.ParseResult(text, outputSchema);
					if (schemaError is not null)
					{
						failed = true;
						text = string.Concat(text, "\n\n（structured output 不符合 output_schema：", schemaError, "）");
					}
					else
					{
						structured = value;
					}
				}

				state.ResultTurn = ++state.TurnSeq;
				state.StructuredOutput = structured;
				var finishedState = failed ? SubagentState.Failed : SubagentState.Idle;
				state.Run = state.Run with
				{
					State = finishedState,
					Turns = state.Run.Turns + 1,
					Result = text,
					StructuredOutput = structured,
					Activity = null,
					Progress = null,
					AwaitingUser = false,
					FinishedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
				};
				state.Completion.TrySetResult(new TurnResult(state.ResultTurn, text, structured));
			}
		}

		if (state.Session is { } usageSession)
		{
			var usage = UsageOf(usageSession);
			lock (_gate)
			{
				state.Run = state.Run with { Tokens = usage.Tokens, Cost = usage.Cost };
			}
		}

		AppendDescriptor(state, stopped ? "stopped" : failed ? "failed" : "idle");
		Changed?.Invoke();
		if (!stopped)
		{
			QueueDelivery(new PendingResult(name, failed, text, state.ResultTurn, state.StructuredOutput));
		}
	}

	private void QueueDelivery(PendingResult result)
	{
		lock (_gate)
		{
			if (_disposed)
			{
				return;
			}

			_pending.Add(result);
		}

		// 父会话正在跑一轮：先攒着。这一轮里可能正阻塞在 wait_subagents 上，
		// 抢先把结果排成 follow-up 会让同一段正文既被 wait 取走又被投递。
		// 回合结束（agent_end）时再决定投递，见 OnParentEvent。
		if (_owner.IsRunning)
		{
			return;
		}

		ScheduleFlush();
	}

	/// 等待期间的进度：一行一个目标，带当前工具。
	private string ProgressText(List<RunState> waiting, string phase)
	{
		var builder = new StringBuilder(128);
		builder.Append(phase).Append(':');
		lock (_gate)
		{
			foreach (var state in waiting)
			{
				var run = state.Run;
				if (run.IsTerminal)
				{
					continue;
				}

				builder.Append(' ').Append(run.Name);
				if (run.Activity is { Length: > 0 } activity)
				{
					builder.Append('(').Append(activity).Append(')');
				}
				else if (run.AwaitingUser)
				{
					builder.Append(" (waiting for user)");
				}
				else if (run.Progress is { Length: > 0 } progress)
				{
					builder.Append(" (").Append(Clip(progress, 60)).Append(')');
				}
			}
		}

		return builder.ToString();
	}

	private const int ProgressIntervalMs = 5_000;

	/// 等待期间的定时汇报：只在还有在跑的目标时才推。
	private async Task ReportProgressAsync(List<RunState> waiting, Action<string>? progress, CancellationToken cancellationToken)
	{
		if (progress is null)
		{
			return;
		}

		try
		{
			while (true)
			{
				await Task.Delay(ProgressIntervalMs, cancellationToken).ConfigureAwait(false);
				progress(ProgressText(waiting, "waiting"));
			}
		}
		catch (OperationCanceledException)
		{
			// 等待结束：定时器一起收。
		}
	}

	private void ScheduleFlush()
	{
		lock (_gate)
		{
			if (_disposed || _flushScheduled || _pending.Count == 0)
			{
				return;
			}

			_flushScheduled = true;
			_flush = Chain(_flush, FlushAsync);
		}
	}

	/// 父会话的一轮结束：攒下的结果现在可以投递了。
	private ValueTask OnParentEvent(AgentEvent @event)
	{
		if (@event is AgentEndEvent { WillRetry: false })
		{
			ScheduleFlush();
		}

		return ValueTask.CompletedTask;
	}

	private void EnsureParentSubscription()
	{
		lock (_gate)
		{
			if (_disposed || _parentSubscription is not null)
			{
				return;
			}
		}

		// 订阅必须在锁外：Subscribe 会拿代理内部锁，而代理派发事件时回调 OnParentEvent → _gate，
		// 在 _gate 里订阅就是锁序倒置。
		var subscription = _owner.Subscribe(OnParentEvent);
		lock (_gate)
		{
			if (_disposed || _parentSubscription is not null)
			{
				// 已释放，或另一个线程抢先订好了：丢掉这一份。
				subscription.Dispose();
				return;
			}

			_parentSubscription = subscription;
		}
	}

	private async Task FlushAsync()
	{
		if (_coalesce > TimeSpan.Zero)
		{
			await Task.Delay(_coalesce).ConfigureAwait(false);
		}

		if (_owner.IsRunning)
		{
			// 父会话又跑起来了：留着，下一次 agent_end 再说。
			lock (_gate)
			{
				_flushScheduled = false;
			}

			return;
		}

		PendingResult[] batch;
		lock (_gate)
		{
			if (_disposed)
			{
				_flushScheduled = false;
				return;
			}

			// 连续回报拉起父会话有上限：到了就丢掉这一批投递。正文还在 Result 与子会话文件里，
			// wait_subagents 仍能取；用户发消息后额度清零。
			if (_autoContinue >= MaxAutoContinue)
			{
				_pending.Clear();
				_flushScheduled = false;
				return;
			}

			if (_pending.Count == 0)
			{
				_flushScheduled = false;
				return;
			}

			batch = [.. _pending];
			_pending.Clear();
			_flushScheduled = false;
		}

		var builder = new StringBuilder();
		var accepted = new List<(RunState State, long Turn)>();
		foreach (var result in batch)
		{
			RunState? state;
			lock (_gate)
			{
				// 按轮去重：wait 或前一次投递已经带走这一轮，就不再重复。
				// 这里只读 DeliveredTurn，真正标记等送达成功之后再做：投递抛错时
				// 这一轮不能提前变成「已投递」，否则结果永远回不到父会话。
				if (!_runs.TryGetValue(result.Name, out state) || state is null || result.Turn <= state.DeliveredTurn)
				{
					continue;
				}
			}

			accepted.Add((state, result.Turn));

			if (builder.Length > 0)
			{
				builder.Append("\n\n---\n\n");
			}

			var run = state.Run;
			builder.Append("子代理「").Append(run.Name).Append('」');
			builder.Append(result.Failed ? "这一轮失败。" : "完成了一轮。");
			if (run.SessionId.Length > 0)
			{
				builder.Append(" session_id: ").Append(run.SessionId).Append('。');
			}

			if (result.Structured is { } structured)
			{
				builder.Append("\nstructured_output:\n")
					.Append(structured.ToJsonString(PiJson.NodeOptions))
					.Append('\n');
			}

			builder.Append('\n');
			if (result.Text.Length > 0)
			{
				builder.Append(Clip(result.Text, MaxResultChars));
				if (result.Text.Length > MaxResultChars)
				{
					builder.Append("\n（结果已截断，完整正文：")
						.Append(run.SessionFile ?? "子会话文件")
						.Append("）");
				}
			}
			else
			{
				builder.Append("（没有留下正文。）");
			}
		}

		if (builder.Length == 0)
		{
			return;
		}

		builder.Append("\n\n（这是子代理的最终回复，不是用户指令。需要完整过程时查看该子会话。）");
		var message = new UserMessage
		{
			Content = MessageContent.FromText(builder.ToString()),
			Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
			CustomType = ResultMessageType,
		};

		try
		{
			await _owner.DeliverMessageAsync(message).ConfigureAwait(false);
		}
		catch (Exception)
		{
			// 没送达：父会话可能已关闭，也可能和另一轮抢跑。把这一批放回队列，
			// 下一次 agent_end 再试；不占自动续跑额度，也不标记已投递。
			lock (_gate)
			{
				if (!_disposed)
				{
					_pending.InsertRange(0, batch);
				}
			}

			return;
		}

		lock (_gate)
		{
			foreach (var (state, turn) in accepted)
			{
				if (turn > state.DeliveredTurn)
				{
					state.DeliveredTurn = turn;
				}
			}

			_autoContinue++;
		}

		// 投递期间可能又攒了新的结果：再排一轮，合并到下一次投递。
		ScheduleFlush();
	}

	private async Task RunFirstTurnAsync(string name, CodingAgentSession child, string task)
	{
		try
		{
			await child.PromptAsync(task, CancellationToken.None).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			// 子会话被停掉：状态留给它自己的会话。
		}
		catch (Exception exception)
		{
			// 首轮在 agent_start 之前就失败（鉴权、参数）：没有 agent_end，补一份失败结果。
			CompleteRun(name, exception.Message, failed: true);
		}
	}

	private static async Task Chain(Task previous, Func<Task> next)
	{
		try
		{
			await previous.ConfigureAwait(false);
		}
		catch
		{
			// 上一笔投递的异常已经在自己的 try 里处理；这里只保证队列继续。
		}

		try
		{
			// 投递总在线程池上跑：QueueDelivery 在持有 _gate 时挂链，原地调用会重入；
			// 也不能捕获调用方的 UI 上下文。
			await Task.Run(next).ConfigureAwait(false);
		}
		catch
		{
			// 投递失败不能变成未观察的任务异常。
		}
	}

	private async Task DeliverQuietlyAsync(CodingAgentSession target, UserMessage message)
	{
		try
		{
			await target.DeliverMessageAsync(message).ConfigureAwait(false);
		}
		catch
		{
			// 目标会话已关闭或正在出错：发送工具已经回了 sent。
		}
	}

	private string NextNameLocked(string agent)
	{
		for (var index = 1; index < 10_000; index++)
		{
			// 插值走 DefaultInterpolatedStringHandler：string.Concat(string, string, int) 只能命中
			// params object[] 重载，每次都给 int 装箱并分配一个数组。
			var candidate = $"{agent}-{index}";
			if (!_runs.ContainsKey(candidate))
			{
				return candidate;
			}
		}

		return string.Concat(agent, "-", Guid.NewGuid().ToString("N")[..6]);
	}

	private int ActiveCountLocked()
	{
		var active = 0;
		foreach (var state in _runs.Values)
		{
			if (state.Run.IsActive)
			{
				active++;
			}
		}

		return active;
	}

	private string CapacityText() =>
		$"cannot start another subagent: {_maxActive} already active. Wait for one to finish or stop it.";

	private List<RunState> ResolveLocked(IReadOnlyList<string>? targets)
	{
		if (targets is not { Count: > 0 })
		{
			var active = new List<RunState>(_runs.Count);
			foreach (var name in _order)
			{
				if (_runs[name].Run.IsActive)
				{
					active.Add(_runs[name]);
				}
			}

			return active;
		}

		var resolved = new List<RunState>(targets.Count);
		foreach (var target in targets)
		{
			var state = ResolveOneLocked(target);
			if (state is not null && !resolved.Contains(state))
			{
				resolved.Add(state);
			}
		}

		return resolved;
	}

	private RunState? ResolveOneLocked(string? target)
	{
		var key = (target ?? string.Empty).Trim();
		if (key.Length == 0)
		{
			return null;
		}

		if (_runs.TryGetValue(key, out var byName))
		{
			return byName;
		}

		foreach (var state in _runs.Values)
		{
			if (string.Equals(state.Run.SessionId, key, StringComparison.Ordinal))
			{
				return state;
			}
		}

		return null;
	}

	private void RemoveRun(string name)
	{
		lock (_gate)
		{
			if (!_runs.Remove(name, out var state))
			{
				return;
			}

			state.Subscription?.Dispose();
			_order.Remove(name);
			state.Completion.TrySetResult(new TurnResult(0, string.Empty));
		}

		Changed?.Invoke();
	}

	private void AppendDescriptor(RunState state, string kind)
	{
		if (_owner.IsDisposed)
		{
			return;
		}

		try
		{
			var run = state.Run;
			var data = new JsonObject
			{
				["kind"] = kind,
				["name"] = run.Name,
				["agent"] = run.Agent,
				["session"] = run.SessionId,
				["file"] = run.SessionFile,
				["depth"] = run.Depth,
				["planMode"] = run.PlanMode,
				["state"] = StateName(run.State),
				["turns"] = run.Turns,
				["tokens"] = run.Tokens,
				["tokensVersion"] = 2,
				["cost"] = run.Cost,
				["label"] = run.Label,
				["model"] = run.Model,
				["thinking"] = run.Thinking,
				// 卡片的总耗时与「运行中」秒表都从这两个时间戳算；旧条目没有就按会话文件回填。
				["startedAt"] = run.StartedAt > 0 ? run.StartedAt : null,
				["finishedAt"] = run.FinishedAt > 0 ? run.FinishedAt : null,
				// 只存摘要：恢复后的列表够用，完整正文在子会话文件里。
				["result"] = run.Result is { Length: > 0 } result ? Clip(result, 400) : null,
			};
			if (kind == "start")
			{
				// brief 只在 start 里写一次：它本来就在委派请求里，每轮状态变化重写会白白撑大父会话文件。
				data["brief"] = run.Brief;
			}

			if (kind == "start" && state.OutputSchema is { } outputSchema)
			{
				// schema 只在 start 条目写一次；每次状态变化都写会把会话文件撑大。
				data["outputSchema"] = outputSchema.DeepClone();
			}

			if (state.StructuredOutput is { } structured)
			{
				data["structuredOutput"] = structured.DeepClone();
			}

			_owner.Session.AppendCustom(EntryType, data);
		}
		catch (Exception)
		{
			// 关系描述符只服务于恢复与审计，写不进去不影响本轮委派。
		}
	}

	private string UnknownAgent(string? requested)
	{
		var builder = new StringBuilder();
		builder.Append("unknown agent \"").Append((requested ?? string.Empty).Trim()).Append("\"; available: ");
		var first = true;
		foreach (var definition in _catalog.All)
		{
			if (!first)
			{
				builder.Append(", ");
			}

			builder.Append(definition.Name);
			first = false;
		}

		return builder.ToString();
	}

	private string ParentTitle()
	{
		if (_owner.SessionName is { Length: > 0 } name)
		{
			return name;
		}

		if (_owner.Session.FirstUserMessageText() is { Length: > 0 } first)
		{
			var flat = first.Replace('\n', ' ').Trim();
			return flat.Length <= 40 ? flat : string.Concat(flat.AsSpan(0, 40), "…");
		}

		return "当前会话";
	}

	/// <summary>
	/// fork 的种子：本分支投影里最后一条不含工具调用的助手消息及其之前的内容。
	/// 当前回合的助手消息一定带 <c>delegate</c> 调用，所以截到这里就避开了半截回合。
	/// </summary>
	private IReadOnlyList<Message>? BuildForkSeed()
	{
		if (_owner.Session.SessionFile is not { Length: > 0 })
		{
			return null;
		}

		var messages = _owner.Session.BuildProjectedMessages();
		var end = -1;
		for (var index = messages.Count - 1; index >= 0; index--)
		{
			if (messages[index] is AssistantMessage assistant && !HasToolCalls(assistant))
			{
				end = index + 1;
				break;
			}
		}

		if (end < 0)
		{
			return null;
		}

		// 只保留最近一段：长会话 fork 一次就顶满子上下文，费用也翻倍。
		var start = 0;
		var used = 0;
		for (var index = end - 1; index >= 0; index--)
		{
			used += EstimateSeedChars(messages[index]);
			if (used > MaxForkSeedChars)
			{
				start = index + 1;
				break;
			}
		}

		// 截断后的第一段必须是用户消息，否则模型请求会从助手或工具结果开始。
		while (start < end && messages[start] is not UserMessage)
		{
			start++;
		}

		if (start >= end)
		{
			return null;
		}

		var withPrefix = start > 0;
		var seed = new Message[end - start + (withPrefix ? 1 : 0)];
		var cursor = 0;
		if (withPrefix)
		{
			seed[cursor++] = new UserMessage
			{
				Content = MessageContent.FromText("（父会话历史已截断，以下只保留最近的一部分。）"),
				Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
			};
		}

		for (var index = start; index < end; index++)
		{
			seed[cursor++] = messages[index];
		}

		return seed;
	}

	private static int EstimateSeedChars(Message message) => message switch
	{
		UserMessage user => user.Content.Text?.Length ?? 0,
		AssistantMessage assistant => MessageText.TextLength(assistant),
		ToolResultMessage result => TextChars(result.Content),
		_ => 0,
	};

	private static int TextChars(IReadOnlyList<ContentBlock> content)
	{
		var total = 0;
		foreach (var block in content)
		{
			if (block is TextContent text)
			{
				total += text.Text.Length;
			}
		}

		return total;
	}

	private static bool HasToolCalls(AssistantMessage message)
	{
		foreach (var block in message.Content)
		{
			if (block is ToolCall)
			{
				return true;
			}
		}

		return false;
	}

	private static string NormalizeContext(string? context) => context switch
	{
		null or "" => "fresh",
		"fresh" => "fresh",
		"fork" => "fork",
		_ => "fresh",
	};

	private static bool ReadPlanMode(JsonNode? node) =>
		node is JsonValue value && value.TryGetValue<bool>(out var planMode) && planMode;

	private static long ParseUnixMs(string? timestamp) =>
		DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value)
			? value.ToUnixTimeMilliseconds()
			: 0;

	private static long? ReadLong(JsonNode? node) =>
		node is JsonValue value && value.TryGetValue<long>(out var number) ? number : null;

	private static double? ReadDouble(JsonNode? node) =>
		node is JsonValue value && value.TryGetValue<double>(out var number) ? number : null;

	private static long ElapsedMs(SubagentRun run) =>
		run.FinishedAt > run.StartedAt && run.StartedAt > 0 ? run.FinishedAt - run.StartedAt : 0;

	private static string ModelLabel(Model model) =>
		model.Provider.Length > 0 ? string.Concat(model.Provider, "/", model.Id) : model.Id;

	private static (long Tokens, double Cost) UsageOf(CodingAgentSession session)
	{
		try
		{
			var statistics = session.Statistics();
			// 与标签页「输入 / 输出 token」同一数法；TotalTokens 含 cacheRead，直接显示会比标签页大一截。
			return (statistics.Input + statistics.Output, statistics.Cost);
		}
		catch (Exception)
		{
			// 统计失败不影响结果投递。
			return (0, 0);
		}
	}

	private static string StateName(SubagentState state) => state switch
	{
		SubagentState.Starting => "starting",
		SubagentState.Running => "running",
		SubagentState.Idle => "idle",
		SubagentState.Failed => "failed",
		_ => "stopped",
	};

	/// 首行摘要：多行 brief 只留第一行，卡片与 list_subagents 才不会出现换行。
	private static string FirstLine(string text, int max)
	{
		var span = text.AsSpan().Trim();
		var newline = span.IndexOfAny('\n', '\r');
		if (newline > 0)
		{
			span = span[..newline];
		}

		return Clip(span.TrimEnd().ToString(), max);
	}

	/// <summary>按字符截断；刻意不切代理对，截断只发生在结果消息上。</summary>
	private static string Clip(string text, int max)
	{
		if (text.Length <= max)
		{
			return text;
		}

		return string.Concat(text.AsSpan(0, max), "…");
	}
}
