using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using PiWeft.Agent;
using PiWeft.Ai;
using PiWeft.Ai.Json;
using PiWeft.CodingAgent.Session;
using PiWeft.CodingAgent.Tools;

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

	/// <summary>子会话实际工作目录；普通运行时是父工作区内的路径，worktree 时是副本路径。</summary>
	public string Cwd { get; init; } = string.Empty;

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

	/// <summary>累计发起的工具调用数；卡片与 list_subagents 用它判断工作量。</summary>
	public int ToolCalls { get; init; }

	/// <summary>本轮 edit/write 落到的文件，绝对路径，按首次编辑顺序。</summary>
	public IReadOnlyList<string> ChangedFiles { get; init; } = [];

	/// <summary>隔离副本的工作目录；没有开 worktree 时为 null。</summary>
	public string? WorktreePath { get; init; }

	/// <summary>隔离副本的分支名。</summary>
	public string? WorktreeBranch { get; init; }

	/// <summary>隔离副本的基线 ref；diff 以它为基线。没有显式指定时是 HEAD。</summary>
	public string? WorktreeBase { get; init; }

	/// <summary>跑过可能写入的 shell 命令；无法静态判断具体文件。</summary>
	public bool MayWrite { get; init; }

	/// <summary>运行中正在调用的工具与摘要（<c>read src/a.cs</c>）；结束或未开始时为 null。</summary>
	public string? Activity { get; init; }

	/// <summary>最近一条助手正文的首行摘要，给等待与卡片显示进度；新一轮开始时清空。</summary>
	public string? Progress { get; init; }

	/// <summary>子代理在自己的标签里等用户回答 <c>ask_question</c>；只影响展示与等待提示。</summary>
	public bool AwaitingUser { get; init; }

	/// <summary>最近一轮的最终回复。</summary>
	public string? Result { get; init; }

	/// <summary>这一轮结果因为连续自动回报上限没有自动送进父会话；<c>wait_subagents</c> 仍能取。</summary>
	public bool DeliverySuppressed { get; init; }

	/// <summary>给子会话的 steer 消息还在队列里，模型尚未在下一个工具边界读到它。</summary>
	public bool SteerPending { get; init; }

	/// <summary>子会话累计输入 + 输出 token；不含缓存命中。</summary>
	public long Tokens { get; init; }

	/// <summary>子会话累计费用。</summary>
	public double Cost { get; init; }

	/// <summary>正在等父会话或用户回答的问题；没有挂起的问题时为 null。</summary>
	public string? Question { get; init; }

	/// <summary>最近一次通过 output_schema 校验的结构化结果。</summary>
	public JsonNode? StructuredOutput { get; init; }

	/// <summary>本次运行的 token 上限；0 表示不限制。超了停 run 并投递失败结果。</summary>
	public long MaxTokens { get; init; }

	/// <summary>本次运行的费用上限（provider 回报的累计费用）；0 表示不限制。</summary>
	public double MaxCost { get; init; }

	/// <summary>本次运行的工具调用上限；0 表示不限制。</summary>
	public long MaxToolCalls { get; init; }

	/// <summary>上一轮结束时模型给出的停止原因（<c>stop</c> / <c>length</c> / <c>error</c> / <c>aborted</c>）；未结束时为空串。</summary>
	public string StopReason { get; init; } = string.Empty;

	/// <summary>宿主在回合结束后跑过的验证命令；没有配置时为空。</summary>
	public IReadOnlyList<SubagentVerifyResult> Verify { get; init; } = [];

	/// <summary>创建时父会话是否在计划模式；冷恢复按它重建只读边界。</summary>
	public bool PlanMode { get; init; }

	/// <summary>启动时间，Unix 毫秒。</summary>
	public long StartedAt { get; init; }

	/// <summary>最近一轮结束时间，Unix 毫秒。</summary>
	public long FinishedAt { get; init; }

	/// <summary>
	/// 最后一次工具起止或助手消息的时间，Unix 毫秒。
	/// 卡片用它区分「在跑」与「卡住」：两分钟没有新动作比一个不变的当前工具名有用。
	/// </summary>
	public long LastActivityAt { get; init; }

	/// <summary>从会话条目恢复出来的历史运行，没有活会话。</summary>
	public bool Restored { get; init; }

	/// <summary>是否真的在跑：启动中或运行中。</summary>
	public bool IsActive => State is SubagentState.Starting or SubagentState.Running;

	/// <summary>是否已结束一轮且可以继续发消息。</summary>
	public bool IsTerminal => State is SubagentState.Idle or SubagentState.Failed or SubagentState.Stopped;
}

/// <summary>子代理挂起的问题：父会话用 <c>send_to_subagent</c> 回答，用户也能在卡片上回答。</summary>
public sealed record SubagentQuestion
{
	/// <summary>问题正文。</summary>
	public required string Text { get; init; }

	/// <summary>可选项；只是提示，回答仍是自由文本。</summary>
	public IReadOnlyList<string> Options { get; init; } = [];
}

/// <summary>一条验证命令的结果。宿主在子代理回合结束后自己执行，父会话据此判断「测试通过」是否属实。</summary>
public sealed record SubagentVerifyResult
{
	/// <summary>原样执行的命令。</summary>
	public required string Command { get; init; }

	/// <summary>退出码；超时或无法启动时为 null。</summary>
	public int? ExitCode { get; init; }

	/// <summary>命令被超时终止。</summary>
	public bool TimedOut { get; init; }

	/// <summary>输出尾巴，供结果正文与卡片展开区显示。</summary>
	public string Output { get; init; } = string.Empty;

	/// <summary>退出码为 0 且没有超时。</summary>
	public bool Passed => !TimedOut && ExitCode == 0;
}

/// <summary>启动子代理的请求。</summary>
public sealed record SubagentStartRequest(string Agent, string Task, string? Name = null, string? Context = null)
{
	/// <summary>卡片上的一句话标签；省略时取任务首行。</summary>
	public string? Label { get; init; }

	/// <summary>本次运行的模型覆盖：<c>provider/id</c> 或唯一 id；null 时用角色文件或父会话模型。</summary>
	public string? Model { get; init; }

	/// <summary>本次运行的思考级别覆盖；null 时用角色文件或父会话当前级别。</summary>
	public string? Thinking { get; init; }

	/// <summary>本次运行的工作目录；null 时沿用父会话 cwd，只允许父工作区内的子目录。</summary>
	public string? Cwd { get; init; }

	/// <summary>要求最终回复返回的 JSON Schema；null 表示不要求。</summary>
	public JsonObject? OutputSchema { get; init; }

	/// <summary>本次运行的时限；null 时取角色文件或设置里的默认值。</summary>
	public TimeSpan? RunTimeout { get; init; }

	/// <summary>本次运行的 token 上限；null 时取角色文件或设置里的默认值。0 表示不限制。</summary>
	public long? MaxTokens { get; init; }

	/// <summary>本次运行的费用上限；null 时取角色文件。0 表示不限制。</summary>
	public double? MaxCost { get; init; }

	/// <summary>本次运行的工具调用上限；null 时取角色文件。0 表示不限制。</summary>
	public long? MaxToolCalls { get; init; }

	/// <summary>回合结束后由宿主执行的验证命令；null 时取角色文件里的设置。</summary>
	public IReadOnlyList<string>? Verify { get; init; }

	/// <summary>在独立 Git 工作区里运行；null 时取角色文件。只对写角色有意义。</summary>
	public bool? Worktree { get; init; }

	/// <summary>隔离副本的基线 ref；null 时用角色文件或 HEAD。</summary>
	public string? WorktreeBase { get; init; }
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

	/// <summary>本次运行的模型覆盖；null 时用角色文件或父会话模型。</summary>
	public string? Model { get; init; }

	/// <summary>本次运行的思考级别覆盖；null 时用角色文件或父会话当前级别。</summary>
	public string? Thinking { get; init; }

	/// <summary>本次运行的工作目录；null 时沿用父会话 cwd。</summary>
	public string? Cwd { get; init; }

	/// <summary>要求子会话最终回复返回的 JSON Schema；null 表示不要求。</summary>
	public JsonObject? OutputSchema { get; init; }

	/// <summary>回合结束后宿主会执行的验证命令；子会话据此知道结论会被复核。</summary>
	public IReadOnlyList<string> Verify { get; init; } = [];

	/// <summary>宿主为这个子代理建隔离副本；分支由 hub 生成。</summary>
	public bool Worktree { get; init; }

	/// <summary>隔离副本的分支名；<see cref="Worktree"/> 为 false 时忽略。</summary>
	public string? WorktreeBranch { get; init; }

	/// <summary>隔离副本的基线 ref；null 时 HEAD。</summary>
	public string? WorktreeBase { get; init; }

	/// <summary>宿主解析出基线 commit 后回写；diff 钉在它上面，父仓库 HEAD 移动也不影响。</summary>
	public Action<string>? WorktreeBaseResolved { get; init; }
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

	/// <summary>恢复时沿用的模型覆盖；null/空串表示按角色或父会话模型。</summary>
	public string? Model { get; init; }

	/// <summary>恢复时沿用的思考级别覆盖。</summary>
	public string? Thinking { get; init; }

	/// <summary>恢复时沿用的工作目录；null 时按会话文件或父会话 cwd。</summary>
	public string? Cwd { get; init; }

	/// <summary>恢复后要求的结构化输出 schema。</summary>
	public JsonObject? OutputSchema { get; init; }

	/// <summary>回合结束后宿主会执行的验证命令。</summary>
	public IReadOnlyList<string> Verify { get; init; } = [];

	/// <summary>隔离副本路径；冷恢复时目录还在就用它当 cwd。</summary>
	public string? WorktreePath { get; init; }

	/// <summary>隔离副本分支。</summary>
	public string? WorktreeBranch { get; init; }

	/// <summary>隔离副本基线 commit。</summary>
	public string? WorktreeBase { get; init; }
}

/// <summary>单个会话的子代理配额与默认值，来自 <c>settings.subagents</c>。</summary>
public sealed record SubagentLimits
{
	/// <summary>默认的单次运行时限。</summary>
	public static readonly TimeSpan DefaultRunTimeout = TimeSpan.FromMinutes(30);

	/// <summary>同一个父会话同时活跃的子代理数量上限。</summary>
	public int MaxActive { get; init; } = 4;

	/// <summary>每个用户回合最多启动多少个子代理。</summary>
	public int MaxSpawns { get; init; } = 32;

	/// <summary>同一工作目录同时活跃的写子代理上限；0 表示不限。</summary>
	public int MaxWriters { get; init; }

	/// <summary>是否启用共享任务板。</summary>
	public bool Team { get; init; }

	/// <summary>单次运行的时限。</summary>
	public TimeSpan RunTimeout { get; init; } = SubagentLimits.DefaultRunTimeout;

	/// <summary>单次运行的 token 上限；0 表示不限制。</summary>
	public long MaxTokens { get; init; }

	/// <summary>每条验证命令的时限。</summary>
	public TimeSpan VerifyTimeout { get; init; } = TimeSpan.FromMinutes(10);
}

/// <summary>
/// 宿主提供的子会话能力。桌面让子会话在 hub 里跑、按需挂标签；没有宿主的纯 CLI 不注册委派工具。
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

	/// <summary>单次运行里父会话最多回答几次子代理的问题；到上限后只剩用户能回答。</summary>
	public const int MaxParentQuestions = 3;

	/// <summary>子代理问父会话的默认时限；到点按「没人回答」返回，子代理自己转为问用户。</summary>
	public static readonly TimeSpan QuestionTimeout = TimeSpan.FromMinutes(5);

	/// <summary>一次委托最多配几条验证命令。</summary>
	public const int MaxVerifyCommands = 8;

	/// <summary>单条验证命令的字符上限。</summary>
	public const int MaxVerifyCommandChars = 500;

	/// <summary>子代理单次运行的默认时限。</summary>
	public static readonly TimeSpan DefaultRunTimeout = SubagentLimits.DefaultRunTimeout;

	private sealed class RunState
	{
		public required SubagentRun Run { get; set; }

		public CodingAgentSession? Session { get; set; }

		public IDisposable? Subscription { get; set; }

		/// 冷恢复只允许一个调用方建标签；并行 send 在这里排队，避免同一历史项开出两个会话。
		public SemaphoreSlim? ReopenGate { get; set; }

		public TaskCompletionSource<TurnResult> Completion { get; set; } =
			new(TaskCreationOptions.RunContinuationsAsynchronously);

		/// 子会话建好（或建失败）时完成：启动窗口内的 send 在这里排队，
		/// 否则它会按「没有活会话」去冷恢复，然后因为没有会话文件直接失败。
		public TaskCompletionSource StartGate { get; } =
			new(TaskCreationOptions.RunContinuationsAsynchronously);

		/// 结果按运行记账：同一子代理第二次运行时，两段正文各自投递一次。
		public long TurnSeq { get; set; }

		public long ResultTurn { get; set; }

		public long DeliveredTurn { get; set; }

		/// 最近一次 read_subagent 给父会话看的正文。结果与它相同时不再重复投递。
		public string? LastReadText { get; set; }

		/// 本轮 edit/write 碰过的文件；Run.ChangedFiles 只在新增时重建数组。
		public HashSet<string> ChangedFiles { get; } = new(StringComparer.Ordinal);

		/// 跑过 shell 类工具：无法确定写了哪些文件。
		public bool MayWrite { get; set; }

		/// 这个角色可能有写工具：严格模式按它占写者名额。
		public bool Writer { get; set; }

		/// 写者名额租约；终态、停止、移除时还回去。
		public IDisposable? WriterLease { get; set; }

		public bool Stopping { get; set; }

		/// 超时或超预算已经写过终结结果：迟到的 agent_end 不能再把状态改回 idle。
		public bool Guarded { get; set; }

		/// 本次运行的时限；每次运行开始时从 Run 或默认值取。
		public TimeSpan RunTimeout { get; set; }

		/// 到期前是否已经推过「收尾」提示。
		public bool CheckpointSent { get; set; }

		public CancellationTokenSource? Deadline { get; set; }

		/// 计时开始时的 Stopwatch 时间戳；0 表示没有在计时（暂停中或未开始）。
		public long DeadlineStartedAt { get; set; }

		/// 暂停时剩下的时间；恢复时按它重新起表。
		public TimeSpan DeadlineRemaining { get; set; }

		/// 本轮累计输入 + 输出 token，用于预算判断；与结束时的统计口径一致。
		public long BudgetTokens { get; set; }

		/// 本轮累计费用，用于费用上限判断。
		public double BudgetCost { get; set; }

		/// 本轮开始时的会话累计费用；卡片上的费用 = CostBase + BudgetCost。
		public double CostBase { get; set; }

		/// 本轮开始时的累计工具数；工具上限按本轮增量判断，而不是一生累计。
		public long ToolCallBase { get; set; }


		/// 验证命令执行中：Stop 与 Dispose 用它杀掉还在跑的 shell。
		public CancellationTokenSource? VerifyCancellation { get; set; }

		/// 这一轮结束后要跑的验证命令；空列表表示不验证。
		public IReadOnlyList<string> VerifyCommands { get; set; } = [];

		/// 挂起的问题：子代理停在 ask_parent 上等人回答。
		public PendingAsk? Question { get; set; }

		/// 本轮已经由父会话回答过几次问题；到上限后只剩用户能回答（用户回答不占额度）。
		public int ParentAnswers { get; set; }

		/// 正在做空闲回收：会话 Dispose 会回调 NoteChildClosed，这条标记区分「收会话」与「关标签」。
		public bool Unloading { get; set; }

		/// 消息已交给子会话、AgentStart 还没到：这一段状态仍是 Idle，但会话马上要跑，不能回收。
		public bool Sending { get; set; }

		/// 已排进 steering 队列的那条消息；队列更新里消失就表示模型已经读到。
		public UserMessage? PendingSteer { get; set; }

		/// 轮次代数：新的一轮（或停掉）就加一。验证任务是上一代的，收尾前用它判断自己是否还该写结果。
		public int Generation { get; set; }

		/// 本轮开始时的会话累计 token；卡片显示累计值，预算比对本轮增量。
		public long TokenBase { get; set; }

		public JsonObject? OutputSchema { get; set; }

		public JsonNode? StructuredOutput { get; set; }

		public int TokensVersion { get; set; } = 1;

		/// 并行工具调用时按开始顺序记着，最后一个结束不会把仍在跑的那一个清掉。
		public List<(string Id, string Description)> ActiveTools { get; } = [];
	}

	/// 一个挂起的问题。超时用 CancelAfter 表达：到点让等待方按「没人回答」收尾，
	/// 不需要额外定时器；被停/被关也走同一个出口。
	private sealed class PendingAsk(
		string text,
		IReadOnlyList<string> options,
		TaskCompletionSource<string?> completion,
		CancellationTokenSource timeout)
	{
		public string Text { get; } = text;

		public IReadOnlyList<string> Options { get; } = options;

		public TaskCompletionSource<string?> Completion { get; } = completion;

		public CancellationTokenSource Timeout { get; } = timeout;
	}

	private sealed record PendingResult(string Name, bool Failed, string Text, long Turn, JsonNode? Structured = null)
	{
		/// 恢复出来的一轮只留了 400 字摘要：投递前在投递线程上从子会话文件补全文。
		/// 放在这里而不是构造 hub 时读，是为了不把文件解析放到打开标签页的 UI 线程上。
		public string? FullTextFile { get; init; }
	}

	/// 一次运行收尾的完整结果：wait 用它拿到「等的那一次」，不会读到下一次运行的正文。
	private sealed record TurnResult(long Turn, string Text, JsonNode? Structured = null);

	private readonly CodingAgentSession _owner;
	private readonly ISubagentHost _host;
	private readonly SubagentCatalog _catalog;
	private readonly SubagentLimits _limits;
	private readonly int _maxActive;
	private readonly int _maxSpawns;
	private readonly int _depth;
	private readonly TeamBoard? _team;
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
		SubagentLimits? limits = null,
		TimeSpan? coalesce = null)
	{
		_owner = owner;
		_host = host;
		_catalog = catalog;
		_depth = depth;
		_limits = limits ?? new SubagentLimits();
		_maxActive = Math.Max(1, _limits.MaxActive);
		_maxSpawns = Math.Max(1, _limits.MaxSpawns);
		_coalesce = coalesce ?? TimeSpan.FromMilliseconds(200);
		Restore();
		_team = _limits.Team ? new TeamBoard(owner, IsTeamMember) : null;
	}

	/// <summary>共享任务板；Team 模式关闭时为 null。</summary>
	public TeamBoard? Team => _team;

	private bool IsTeamMember(string name)
	{
		if (string.Equals(name, "lead", StringComparison.Ordinal))
		{
			return true;
		}

		lock (_gate)
		{
			return _runs.ContainsKey(name);
		}
	}

	/// <summary>子代理列表或状态变化；界面据此刷新。</summary>
	public event Action? Changed;

	/// <summary>某个子代理开始等用户回答。桌面据此提示；参数是当时的运行快照。</summary>
	public event Action<SubagentRun>? AwaitingChanged;

	/// <summary>某个子代理提出了一个要人回答的问题；父会话在 <c>wait_subagents</c> 里能看到，桌面可据此提示用户。</summary>
	public event Action<SubagentRun>? QuestionRaised;

	/// <summary>那个问题已经有结果了（被回答、超时或被停）：事件订阅方据此收起提示。</summary>
	public event Action<SubagentRun>? QuestionAnswered;

	/// <summary>本会话开出的子代理快照，按启动顺序。</summary>
	public IReadOnlyList<SubagentRun> Runs => RunsSnapshot();

	/// <summary>正在跑的子代理数量；关标签确认用它，不必复制整份快照。</summary>
	public int ActiveRunCount()
	{
		lock (_gate)
		{
			var count = 0;
			foreach (var state in _runs.Values)
			{
				if (state.Run.IsActive)
				{
					count++;
				}
			}

			return count;
		}
	}

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

	/// 按子会话 id 找一个运行；调用方必须已持锁。事件入口共用同一套匹配规则。
	private RunState? FindStateLocked(string? sessionId)
	{
		if (string.IsNullOrEmpty(sessionId))
		{
			return null;
		}

		foreach (var state in _runs.Values)
		{
			if (string.Equals(state.Run.SessionId, sessionId, StringComparison.Ordinal))
			{
				return state;
			}
		}

		return null;
	}

	/// <summary>取子代理正在等回答的问题；没有问题或找不到运行时返回 <see langword="null"/>。</summary>
	public SubagentQuestion? PendingQuestion(string? sessionId)
	{
		lock (_gate)
		{
			var question = FindStateLocked(sessionId)?.Question;
			return question is null
				? null
				: new SubagentQuestion { Text = question.Text, Options = question.Options };
		}
	}

	/// <summary>子代理是否停在终态且没有挂起的问题；回收方按这个判断能不能释放会话。</summary>
	public bool IsIdle(string? sessionId)
	{
		lock (_gate)
		{
			var state = FindStateLocked(sessionId);
			return state is not null && CanUnloadLocked(state);
		}
	}

	/// 能不能释放会话：终态、没问题、没有投递在飞、会话自己也不在跑。
	/// 后两条挡住两个真实窗口：send 已 PromptAsync 但 AgentStart 还没到；Stop 之后 abort 还没退栈。
	private static bool CanUnloadLocked(RunState state) =>
		!state.Run.IsActive &&
		state.Question is null &&
		state.PendingSteer is null &&
		!state.Sending &&
		!state.Unloading &&
		state.Session is not { IsRunning: true };

	/// <summary>
	/// 释放一个空闲子代理的会话对象，保留运行条目与会话文件；之后 <c>send_to_subagent</c>
	/// 或打开转录会按文件冷恢复。还在跑、在提问或没有活会话时不动它。
	/// </summary>
	public bool UnloadSession(string? sessionId)
	{
		RunState? state;
		CodingAgentSession session;
		SemaphoreSlim? gate = null;
		lock (_gate)
		{
			state = FindStateLocked(sessionId);
			if (state?.Session is not { IsDisposed: false } current || !CanUnloadLocked(state))
			{
				return false;
			}

			state.Unloading = true;
			session = current;
			// 闸门也在这里建：否则「先回收、后冷恢复」的时序里两边看到的是不同的对象，互斥不成立。
			gate = state.ReopenGate ??= new SemaphoreSlim(1, 1);
		}

		// 与冷恢复互斥：Dispose 是异步收尾（字段先置 IsDisposed，回调 NoteChildClosed 在后），
		// 中间被 ReopenAsync 插进来就会把新会话当成旧的摘掉。抢不到说明正好在恢复，下一拍再试。
		if (!gate.Wait(0))
		{
			lock (_gate)
			{
				state.Unloading = false;
			}

			return false;
		}

		try
		{
			// Dispose 会回调 NoteChildClosed：Unloading 让那条路径只摘订阅，不改状态。
			session.Dispose();
		}
		catch (Exception)
		{
			// 会话可能已经在关闭；状态与文件不受影响。
		}
		finally
		{
			lock (_gate)
			{
				// 订阅可能在回收期间被冷恢复换过：只清“确实没见过”的那一种情况由 NoteChildClosed 处理。
				state.Unloading = false;
			}

			gate.Release();
		}

		return true;
	}

	/// <summary>
	/// 冷恢复一个已结束或被回收的子代理，返回活会话供界面打开（不投递消息）。
	/// 已经有活会话时直接返回它；与 <c>send_to_subagent</c> 共用同一把恢复锁，不会开出两份。
	/// </summary>
	public async Task<CodingAgentSession?> ReopenForViewAsync(string? sessionId, CancellationToken cancellationToken = default)
	{
		RunState? state;
		lock (_gate)
		{
			state = FindStateLocked(sessionId);
			if (state is null)
			{
				return null;
			}

			if (state.Session is { IsDisposed: false } live)
			{
				return live;
			}
		}

		SemaphoreSlim gate;
		lock (_gate)
		{
			gate = state.ReopenGate ??= new SemaphoreSlim(1, 1);
		}

		await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			lock (_gate)
			{
				if (state.Session is { IsDisposed: false } live)
				{
					return live;
				}
			}

			return await ReopenAsync(state, cancellationToken).ConfigureAwait(false);
		}
		finally
		{
			gate.Release();
		}
	}

	/// <summary>
	/// 子代理向父会话提问并等回答。返回 <see langword="null"/> 表示没人回答（超时、被停、
	/// 本轮已经问满 <see cref="MaxParentQuestions"/> 次，或本来就不在委派里）。
	/// </summary>
	/// <remarks>等待期间不计入运行时限；父会话用 <c>send_to_subagent</c> 回答，用户也能在卡片上回答。</remarks>
	public async Task<string?> AskParentAsync(
		string? childSessionId,
		string question,
		IReadOnlyList<string>? options,
		CancellationToken cancellationToken,
		TimeSpan? timeout = null)
	{
		var text = Clip((question ?? string.Empty).Trim(), MaxQuestionChars);
		if (text.Length == 0)
		{
			return null;
		}

		RunState? state;
		PendingAsk pending;
		lock (_gate)
		{
			state = FindStateLocked(childSessionId);
			if (state is null || state.Run.IsTerminal || state.Guarded || state.Stopping ||
				state.Question is not null || state.ParentAnswers >= MaxParentQuestions)
			{
				return null;
			}

			var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			deadline.CancelAfter(timeout is { } configured && configured > TimeSpan.Zero ? configured : QuestionTimeout);
			pending = new PendingAsk(
				text,
				ClipOptions(options),
				new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously),
				deadline);
			state.Question = pending;
			state.Run = state.Run with { Question = text };
			RefreshWaitPauseLocked(state);
		}

		Changed?.Invoke();
		QuestionRaised?.Invoke(state.Run);

		var answered = false;
		try
		{
			var answer = await pending.Completion.Task.WaitAsync(pending.Timeout.Token).ConfigureAwait(false);
			answered = true;
			return answer;
		}
		catch (OperationCanceledException)
		{
			return null;
		}
		finally
		{
			lock (_gate)
			{
				// 被回答时 SendAsync 已经清掉并计过数；这里只收尾超时、被停与被关。
				if (ReferenceEquals(state.Question, pending))
				{
					state.Question = null;
					state.Run = state.Run with { Question = null };
					RefreshWaitPauseLocked(state);
				}
			}

			pending.Timeout.Dispose();
			if (!answered)
			{
				// 回答路径已由 PublishAnswer 通知过；超时与被停要从这里让界面收起问题卡。
				Changed?.Invoke();
				QuestionAnswered?.Invoke(state.Run);
			}
		}
	}

	/// <summary>用户回答子代理的问题（桌面卡片）。返回是否真的有一个挂起的问题被回答。</summary>
	public bool AnswerQuestion(string? sessionId, string answer)
	{
		var text = (answer ?? string.Empty).Trim();
		if (text.Length == 0)
		{
			return false;
		}

		RunState? state;
		PendingAsk? question;
		lock (_gate)
		{
			state = FindStateLocked(sessionId);
			question = state is null ? null : TakeQuestionLocked(state, byParent: false);
		}

		if (state is null || question is null)
		{
			return false;
		}

		PublishAnswer(state, question, Clip(text, MaxAnswerChars));
		return true;
	}

	/// 把挂起的问题取走；调用方随后调用 PublishAnswer。
	/// <paramref name="byParent"/> 为 true 时记一次父会话回答，消耗本轮额度；用户回答不占。
	private static PendingAsk? TakeQuestionLocked(RunState state, bool byParent)
	{
		if (state.Question is not { } question)
		{
			return null;
		}

		state.Question = null;
		if (byParent)
		{
			state.ParentAnswers++;
		}

		state.Run = state.Run with { Question = null };
		return question;
	}

	/// 问题答完：唤醒子代理、恢复计时、通知界面收起提示。
	private void PublishAnswer(RunState state, PendingAsk question, string text)
	{
		lock (_gate)
		{
			RefreshWaitPauseLocked(state);
		}

		// 只完成等待，不动 Timeout：取消定时器会和 WaitAsync 抢同一个出口，
		// 先到的是取消时，明明回答了却按「没人回答」返回。CTS 由 AskParentAsync 收尾。
		question.Completion.TrySetResult(text);
		Changed?.Invoke();
		QuestionAnswered?.Invoke(state.Run);
	}

	private static IReadOnlyList<string> ClipOptions(IReadOnlyList<string>? options)
	{
		if (options is not { Count: > 0 })
		{
			return [];
		}

		var clipped = new List<string>(Math.Min(options.Count, MaxQuestionOptions));
		foreach (var option in options)
		{
			var value = Clip((option ?? string.Empty).Trim(), MaxQuestionOptionChars);
			if (value.Length > 0)
			{
				clipped.Add(value);
			}

			if (clipped.Count == MaxQuestionOptions)
			{
				break;
			}
		}

		return clipped;
	}

	private const int MaxQuestionChars = 2_000;
	private const int MaxQuestionOptions = 8;
	private const int MaxQuestionOptionChars = 200;
	private const int MaxAnswerChars = 4_000;

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
		var undelivered = new List<string>();
		var forgotten = new HashSet<string>(StringComparer.Ordinal);
		foreach (var entry in _owner.Session.Entries)
		{
			if (entry is not CustomEntry { CustomType: EntryType, Data: JsonObject data })
			{
				continue;
			}

			var kind = ReadString(data["kind"]);
			var name = ReadString(data["name"]);
			if (string.IsNullOrEmpty(kind) || string.IsNullOrEmpty(name))
			{
				continue;
			}

			if (kind == "forgotten")
			{
				// 会话文件已经被删除：这条运行记录不再恢复。
				forgotten.Add(name);
				continue;
			}

			if (kind is "start" or "started")
			{
				if (!restored.TryGetValue(name, out var state))
				{
					var agentName = ReadString(data["agent"]) ?? string.Empty;
					state = new RunState
					{
						// 恢复项继续时也要按角色重新占写者名额，不能让重启绕过严格模式。
						Writer = _catalog.Get(agentName) is { } definition && IsWriter(definition),
						Run = new SubagentRun
						{
							Name = name,
							Agent = agentName,
							Label = ReadString(data["label"]) ?? string.Empty,
							Brief = ReadString(data["brief"]) ?? string.Empty,
							Model = ReadString(data["model"]) ?? string.Empty,
							Thinking = ReadString(data["thinking"]) ?? string.Empty,
							Cwd = ReadString(data["cwd"]) ?? string.Empty,
							Depth = ReadInt(data["depth"]) ?? 1,
							StartedAt = ReadLong(data["startedAt"]) ?? 0,
							State = SubagentState.Stopped,
							PlanMode = ReadPlanMode(data["planMode"]),
							ChangedFiles = ReadStrings(data["changedFiles"]),
							MayWrite = ReadBool(data["mayWrite"]),
							WorktreePath = ReadString(data["worktreePath"]),
							WorktreeBranch = ReadString(data["worktreeBranch"]),
							WorktreeBase = ReadString(data["worktreeBase"]),
							Restored = true,
						},
					};
					restored[name] = state;
					order.Add(name);
				}

				// started 条目才带子会话 id；start 条目先占名字。
				state.Run = state.Run with
				{
					SessionId = ReadString(data["session"]) is { Length: > 0 } sessionId
						? sessionId
						: state.Run.SessionId,
					SessionFile = ReadString(data["file"]) is { Length: > 0 } file
						? file
						: state.Run.SessionFile,
					Agent = ReadString(data["agent"]) ?? state.Run.Agent,
					Label = ReadString(data["label"]) ?? state.Run.Label,
					Brief = ReadString(data["brief"]) ?? state.Run.Brief,
					Model = ReadString(data["model"]) ?? state.Run.Model,
					Thinking = ReadString(data["thinking"]) ?? state.Run.Thinking,
					Cwd = ReadString(data["cwd"]) is { Length: > 0 } restoredCwd ? restoredCwd : state.Run.Cwd,
					MaxTokens = ReadLong(data["maxTokens"]) ?? state.Run.MaxTokens,
					MaxCost = ReadDouble(data["maxCost"]) ?? state.Run.MaxCost,
					MaxToolCalls = ReadLong(data["maxToolCalls"]) ?? state.Run.MaxToolCalls,
					PlanMode = state.Run.PlanMode || ReadPlanMode(data["planMode"]),
					WorktreePath = ReadString(data["worktreePath"]) ?? state.Run.WorktreePath,
					WorktreeBranch = ReadString(data["worktreeBranch"]) ?? state.Run.WorktreeBranch,
					WorktreeBase = ReadString(data["worktreeBase"]) ?? state.Run.WorktreeBase,
					State = SubagentState.Stopped,
				};
				if (data["outputSchema"] is JsonObject schema)
				{
					state.OutputSchema = schema.DeepClone().AsObject();
				}

				if (ReadLong(data["runTimeoutMs"]) is { } timeoutMs && timeoutMs > 0)
				{
					state.RunTimeout = TimeSpan.FromMilliseconds(timeoutMs);
				}

				if (data["verify"] is JsonArray verify)
				{
					var commands = new List<string>(verify.Count);
					foreach (var item in verify)
					{
						if (item is JsonValue value && value.TryGetValue<string>(out var command) && command.Length > 0)
						{
							commands.Add(command);
						}
					}

					state.VerifyCommands = commands;
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

			if (kind == "worktree-removed")
			{
				finished.Run = finished.Run with
				{
					WorktreePath = null,
					WorktreeBranch = null,
					WorktreeBase = null,
				};
				continue;
			}

			if (kind == "delivered")
			{
				if (ReadLong(data["turn"]) is { } deliveredTurn && deliveredTurn > finished.DeliveredTurn)
				{
					finished.DeliveredTurn = deliveredTurn;
				}

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
				Turns = ReadInt(data["turns"]) ?? finished.Run.Turns,
				Result = ReadString(data["result"]) is { Length: > 0 } result
					? result
					: finished.Run.Result,
				Tokens = ReadLong(data["tokens"]) ?? finished.Run.Tokens,
				Cost = ReadDouble(data["cost"]) ?? finished.Run.Cost,
				FinishedAt = ReadLong(data["finishedAt"]) ?? finished.Run.FinishedAt,
				StopReason = ReadString(data["stopReason"]) ?? finished.Run.StopReason,
				Verify = ReadVerifyResults(data["verifyResults"]),
				StructuredOutput = kind is "failed" or "stopped"
					? null
					: persistedStructured ?? finished.Run.StructuredOutput,
				PlanMode = finished.Run.PlanMode || ReadPlanMode(data["planMode"]),
				ChangedFiles = ReadStrings(data["changedFiles"]) is { Count: > 0 } changedFiles
					? changedFiles
					: finished.Run.ChangedFiles,
				MayWrite = ReadBool(data["mayWrite"]) || finished.Run.MayWrite,
			};

			if (ReadLong(data["turn"]) is { } resultTurn && resultTurn > 0)
			{
				finished.ResultTurn = resultTurn;
				finished.TurnSeq = Math.Max(finished.TurnSeq, resultTurn);
			}

			if (ReadBool(data["deliverable"]) && kind is "idle" or "failed")
			{
				if (!undelivered.Contains(name))
				{
					undelivered.Add(name);
				}
			}
		}

		if (forgotten.Count > 0)
		{
			order.RemoveAll(forgotten.Contains);
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
					// 旧条目没有 turn：算作第 1 轮，未投递时 wait 还能取一次。
					if (state.ResultTurn <= 0)
					{
						state.ResultTurn = 1;
					}

					state.TurnSeq = Math.Max(state.TurnSeq, state.ResultTurn);
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

		QueueRestoredResults(undelivered, restored);
	}

	/// 上一轮结果没送到父会话（关标签、退出、崩溃）就恢复了：排进队列补投一次。
	/// 不补的话，模型永远不知道那段结论，卡片里的正文只有用户看得到。
	private void QueueRestoredResults(List<string> names, Dictionary<string, RunState> restored)
	{
		if (names.Count == 0)
		{
			return;
		}

		var pending = new List<PendingResult>(names.Count);
		foreach (var name in names)
		{
			if (!restored.TryGetValue(name, out var state) || !_runs.ContainsKey(name))
			{
				continue;
			}

			if (state.ResultTurn > 0 && state.DeliveredTurn >= state.ResultTurn)
			{
				continue;
			}

			var text = state.Run.Result ?? string.Empty;
			if (text.Length == 0 && state.Run.SessionFile is not { Length: > 0 })
			{
				continue;
			}

			// 恢复出来的正文算一轮；投递成功后正常路径会补一条 delivered 记录，第二次重启不会重推。
			state.ResultTurn = state.ResultTurn > 0 ? state.ResultTurn : 1;
			pending.Add(new PendingResult(name, state.Run.State == SubagentState.Failed, text, state.ResultTurn, state.StructuredOutput)
			{
				FullTextFile = state.Run.SessionFile,
			});
		}

		if (pending.Count == 0)
		{
			return;
		}

		// 下一次父会话起轮或收尾时投递；不在这里直接发，构造函数里会话还没装好。
		lock (_gate)
		{
			if (_disposed)
			{
				return;
			}

			_pending.AddRange(pending);
		}
	}

	private static string? ReadLastAssistantTextFromFile(string file)
	{
		try
		{
			var manager = SessionManager.Open(file);
			for (var index = manager.Entries.Count - 1; index >= 0; index--)
			{
				if (manager.Entries[index] is SessionMessageEntry { Message: AssistantMessage assistant })
				{
					var text = MessageText.Of(assistant);
					if (text.Length > 0)
					{
						return text;
					}
				}
			}
		}
		catch (Exception)
		{
			// 文件缺失或损坏：退回条目里的摘要。
		}

		return null;
	}

	/// <summary>用户发了一条消息：本回合的启动预算清零。回报触发的父会话轮次不算。</summary>
	public void NoteUserPrompt()
	{
		var flush = false;
		lock (_gate)
		{
			_spawns = 0;
			_autoContinue = 0;
			// 重启后补投的正文攒在队列里；用户下一次提问时先到，模型才不需要猜。
			flush = _pending.Count > 0;
		}

		if (flush)
		{
			ScheduleFlush();
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
			"把结论写进最终回复：做了什么、结果在哪、还差什么。\n",
			"父会话已经做过的决定、给过的范围与偏好，用 ask_parent 问它；只有用户能决定的事（权限、偏好、任务外的选择）用 ask_question。",
			"用户可以打开你的转录；没有打开时提问也会回到父标签。停在提问上等回答之前，先确认这条问题真的需要人。\n",
			"不要开新的子代理，也不要把工作推回父会话。");
		if (context.Verify.Count > 0)
		{
			// 子代理知道结论会被复核，就不容易交一份「测试通过」的口头声明。
			boundary = string.Concat(
				boundary,
				"\n\n这一轮结束后，宿主会自己执行下面这些命令并记录退出码，结论按它们的真实结果判定：\n",
				string.Join("\n", context.Verify.Select(static command => string.Concat("- `", command, "`"))),
				"\n不要声称这些命令通过了；把改动做完，让命令自己说话。");
		}

		if (context.Parent.Subagents?.Team is not null)
		{
			boundary = string.Concat(boundary, "\n\n", TeamBoard.Policy);
		}

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

	/// <summary>测试用：还没投递出去的结果条数。</summary>
	internal int PendingCount
	{
		get
		{
			lock (_gate)
			{
				return _pending.Count;
			}
		}
	}

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

		// 显式给的时限才做范围校验；设置与会话默认值（包括测试里的短预算）不受下限限制。
		var runTimeout = request.RunTimeout
			?? (definition.TimeoutMinutes is { } minutes && minutes > 0
				? TimeSpan.FromMinutes(minutes)
				: TimeSpan.Zero);
		if (runTimeout <= TimeSpan.Zero)
		{
			runTimeout = _limits.RunTimeout;
		}
		else if (runTimeout < TimeSpan.FromSeconds(10) || runTimeout > TimeSpan.FromHours(24))
		{
			return new SubagentStartOutcome(false, "timeout_ms must be between 10000 and 86400000");
		}

		var maxTokens = request.MaxTokens ?? definition.MaxTokens ?? _limits.MaxTokens;
		if (maxTokens < 0)
		{
			return new SubagentStartOutcome(false, "max_tokens must not be negative");
		}

		var maxCost = request.MaxCost ?? definition.MaxCost ?? 0;
		if (!double.IsFinite(maxCost) || maxCost < 0)
		{
			return new SubagentStartOutcome(false, "max_cost must be a non-negative number");
		}

		var maxToolCalls = request.MaxToolCalls ?? definition.MaxToolCalls ?? 0;
		if (maxToolCalls < 0)
		{
			return new SubagentStartOutcome(false, "max_tool_calls must not be negative");
		}

		// 模型与思考级别只做长度校验；宿主解析失败时按角色/父会话回落，
		// 与角色文件写错模型时的行为一致。
		var model = (request.Model ?? string.Empty).Trim();
		if (model.Length > 200)
		{
			return new SubagentStartOutcome(false, "model must be at most 200 characters");
		}

		var thinking = (request.Thinking ?? string.Empty).Trim();
		if (thinking.Length > 32)
		{
			return new SubagentStartOutcome(false, "thinking must be at most 32 characters");
		}

		var verify = request.Verify ?? definition.Verify ?? [];
		if (verify.Count > MaxVerifyCommands)
		{
			return new SubagentStartOutcome(false, $"too many verify commands; at most {MaxVerifyCommands}");
		}

		var planMode = _owner.PlanModeEnabled;
		foreach (var command in verify)
		{
			if (command.Trim().Length == 0 || command.Length > MaxVerifyCommandChars)
			{
				return new SubagentStartOutcome(false,
					$"each verify command must be 1-{MaxVerifyCommandChars} characters");
			}

			// 验证由宿主跑，不经过子会话的工具闸；计划模式下不能成为写工作区的后门。
			if (planMode && !PlanMode.IsReadOnlyShellCommand(command))
			{
				return new SubagentStartOutcome(false,
					$"verify command `{command}` is not read-only; drop verify or leave plan mode");
			}
		}

		var worktree = request.Worktree ?? definition.Worktree ?? false;
		var worktreeBase = request.WorktreeBase ?? definition.WorktreeBase;
		if (worktree && planMode)
		{
			return new SubagentStartOutcome(false, "worktree is a write operation; leave plan mode first");
		}
		if (worktree && !IsWriter(definition))
		{
			return new SubagentStartOutcome(false, "worktree needs a writing role; this role cannot change files");
		}
		if (worktreeBase is { Length: > 0 } requestedBase &&
			(requestedBase.Length > 200 || requestedBase.StartsWith('-')))
		{
			return new SubagentStartOutcome(false, "worktree_base must be a plain ref name such as HEAD or a branch");
		}

		var requestedCwd = (request.Cwd ?? string.Empty).Trim();
		string? childCwd = null;
		if (requestedCwd.Length > 0)
		{
			if (worktree)
			{
				return new SubagentStartOutcome(false, "cwd cannot be combined with worktree; the isolated copy sets its own directory");
			}

			if (requestedCwd.Length > 1_000)
			{
				return new SubagentStartOutcome(false, "cwd must be at most 1000 characters");
			}

			try
			{
				childCwd = Path.GetFullPath(requestedCwd, _owner.Cwd);
			}
			catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
			{
				return new SubagentStartOutcome(false, $"cwd is not a valid path: {requestedCwd}");
			}

			if (!Directory.Exists(childCwd))
			{
				return new SubagentStartOutcome(false, $"cwd does not exist: {requestedCwd}");
			}

			// 只允许父工作区内的子目录：跨仓库仍由父会话处理，避免子代理加载另一项目的
			// 设置、MCP 与项目资源，也避免侧栏把子会话挂到另一个项目下。
			if (!IsWithinWorkspace(_owner.Cwd, childCwd))
			{
				return new SubagentStartOutcome(false, "cwd must stay inside the parent workspace; cross-repo delegation is not supported");
			}
		}

		var context = NormalizeContext(request.Context);
		IReadOnlyList<Message>? seed = null;
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
		string? worktreeBranch = null;
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

			// 严格模式：同一工作目录的写子代理串行。默认 0 表示不限；隔离副本不占父工作区名额。
			var writer = IsWriter(definition);
			worktreeBranch = worktree
				? $"piweft/{name}-{_owner.Session.SessionId[..8]}"
				: null;
			IDisposable? writerLease = null;
			if (writer && !worktree && !WorkspaceWriterGate.TryAcquire(_owner.Cwd, _limits.MaxWriters, out writerLease))
			{
				return new SubagentStartOutcome(false,
					$"writer limit reached: this workspace already has {_limits.MaxWriters} writing subagent(s). " +
					"Wait for one to finish, or raise settings.subagents.maxWriters.");
			}

			var startedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
			state = new RunState
			{
				OutputSchema = outputSchema,
				RunTimeout = runTimeout,
				VerifyCommands = verify,
				Writer = writer,
				WriterLease = writerLease,
				Run = new SubagentRun
				{
					Name = name,
					Agent = definition.Name,
					Label = label,
					Brief = Clip(task, MaxBriefChars),
					Depth = _depth + 1,
					Cwd = childCwd ?? Path.GetFullPath(_owner.Cwd),
					PlanMode = planMode,
					MaxTokens = maxTokens,
					MaxCost = maxCost,
					MaxToolCalls = maxToolCalls,
					StartedAt = startedAt,
					LastActivityAt = startedAt,
					WorktreeBranch = worktreeBranch,
					WorktreeBase = worktree ? worktreeBase ?? "HEAD" : null,
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
					Model = model.Length > 0 ? model : null,
					Thinking = thinking.Length > 0 ? thinking : null,
					Cwd = childCwd,
					OutputSchema = outputSchema,
					Verify = verify,
					Worktree = worktree,
					WorktreeBranch = worktreeBranch,
					WorktreeBase = worktree ? worktreeBase ?? "HEAD" : null,
					WorktreeBaseResolved = worktree
						? commit =>
						{
							lock (_gate)
							{
								if (_runs.TryGetValue(name, out var current))
								{
									current.Run = current.Run with { WorktreeBase = commit };
								}
							}
						}
						: null,
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
				current.StartGate.TrySetResult();
				subscription.Dispose();
				child.Dispose();
				return new SubagentStartOutcome(false, $"subagent {name} was stopped before its first turn");
			}

			current.Session = child;
			current.Subscription = subscription;
			current.StartGate.TrySetResult();
			current.Run = current.Run with
			{
				SessionId = sessionId,
				SessionFile = child.Session.SessionFile,
				Model = ModelLabel(child.Model),
				Thinking = child.ThinkingLevel,
				Cwd = child.Cwd,
				WorktreePath = worktree ? child.Cwd : current.Run.WorktreePath,
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
		var questionAlreadyPending = false;
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

			questionAlreadyPending = HasPendingQuestionLocked(waiting);
		}

		var taskNames = new Dictionary<Task<TurnResult>, string>(pending.Count);
		foreach (var (state, task) in pending)
		{
			taskNames[task] = state.Run.Name;
		}

		var timedOut = false;
		var questionSeen = false;
		// any 只要有一个目标已经结束就立即返回；继续等 pending 会把「等一个」变成「等全部」。
		if (pending.Count > 0 && !(any && completedTurns.Count > 0) && !questionAlreadyPending)
		{
			Report(ProgressText(waiting, "waiting"));

			// .NET 9 的 Task.WhenEach：完成一个就推进一次。再加一条定时汇报，
			// 让「一个子代理跑十分钟」也不至于整段没有进度。
			using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			deadline.CancelAfter(timeout);
			// 子代理提问算「需要你」：它与完成一样值得提前结束等待。
			using var stop = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
			// 用完成的 TCS 而不是跨线程读一个 bool：取消与读取之间不需要额外的内存序保证。
			var questionSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			var targetNames = new HashSet<string>(waiting.Select(static state => state.Run.Name), StringComparer.Ordinal);
			void OnQuestion(SubagentRun run)
			{
				if (!targetNames.Contains(run.Name))
				{
					return;
				}

				questionSignal.TrySetResult();
				try
				{
					stop.Cancel();
				}
				catch (ObjectDisposedException)
				{
					// 等待已经收尾。
				}
			}

			QuestionRaised += OnQuestion;
			// 订阅与「已经在等答案」之间没有原子性：订阅后补看一次，避免刚好错过那一次事件。
			lock (_gate)
			{
				if (HasPendingQuestionLocked(waiting))
				{
					questionSignal.TrySetResult();
					stop.Cancel();
				}
			}

			using var heartbeat = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
			var reporter = ReportProgressAsync(waiting, progress is null ? null : Report, heartbeat.Token);
			var tasks = pending.Select(static item => item.Task).ToArray();
			try
			{
				if (any)
				{
					// 第一个结束就拿到结果；其余任务不 await，免得把「等一个」拖成「等全部」。
					var first = await Task.WhenAny(tasks).WaitAsync(stop.Token).ConfigureAwait(false);
					if (first.IsCompletedSuccessfully && taskNames.TryGetValue(first, out var name))
					{
						completedTurns[name] = first.Result;
					}

					Report(ProgressText(waiting, "waiting"));
				}
				else
				{
					await foreach (var completed in Task.WhenEach(tasks).WithCancellation(stop.Token).ConfigureAwait(false))
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
				questionSeen = questionSignal.Task.IsCompleted;
				timedOut = !questionSeen;
			}
			finally
			{
				QuestionRaised -= OnQuestion;
				await heartbeat.CancelAsync().ConfigureAwait(false);
				await reporter.ConfigureAwait(false);
			}
		}
		else if (pending.Count > 0 && questionAlreadyPending)
		{
			questionSeen = true;
		}

		// 超时、被移除或任务异常：用当前状态补一份快照，别让结果段落空着。
		var stillRunning = 0;
		JsonArray questionDetails;
		string questionNames;
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

			// 同一把锁下取问题快照：后面的正文与 Details 都读这一份。
			questionDetails = PendingQuestionDetailsLocked(waiting);
			questionNames = PendingQuestionNamesLocked(waiting);
		}


		var details = new JsonArray();
		var text = new StringBuilder(256);
		if (questionSeen)
		{
			text.Append("wait_subagents: a subagent is waiting for your answer.");
		}
		else if (timedOut)
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
		// wait 取走的正文同样要落 delivered：否则重启后同一条结果会被补投第二次。
		var taken = new List<RunState>(waiting.Count);
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
					["changed_files"] = run.ChangedFiles is { Count: > 0 } changed
						? new JsonArray([.. changed.Select(static file => (JsonNode)file)])
						: null,
					["may_write"] = run.MayWrite,
					["worktree_path"] = run.WorktreePath,
					["worktree_branch"] = run.WorktreeBranch,
					["worktree_base"] = run.WorktreeBase,
					["team_tasks"] = TeamTasksJson(run.Name),
					["tokens"] = run.Tokens,
					["max_tokens"] = run.MaxTokens,
					["max_cost"] = run.MaxCost > 0 ? run.MaxCost : null,
					["max_tool_calls"] = run.MaxToolCalls > 0 ? run.MaxToolCalls : null,
					["cost"] = run.Cost,
					["elapsed_ms"] = ElapsedMs(run),
					["session_id"] = run.SessionId,
					["session_file"] = run.SessionFile,
					["cwd"] = run.Cwd is { Length: > 0 } runCwd ? runCwd : null,
					["structured_output"] = turnResult.Structured,
					["activity"] = run.Activity,
					["progress"] = run.Progress,
					["awaiting_user"] = run.AwaitingUser,
					["question"] = run.Question,
					["stop_reason"] = run.StopReason is { Length: > 0 } stopReason ? stopReason : null,
					["verify"] = VerifyDetails(run.Verify),
					["delivery_suppressed"] = run.DeliverySuppressed,
					["steer_pending"] = run.SteerPending,
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

				if (run.Question is { Length: > 0 } question)
				{
					text.Append(" · asks: ").Append(Clip(question, 300));
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
				state.Run = state.Run with { DeliverySuppressed = false };
				taken.Add(state);
				// 标记这段正文已随本次工具结果返回，桌面据此补只读卡。
				row["returned"] = true;
			}
		}

		foreach (var state in taken)
		{
			AppendDescriptor(state, "delivered");
		}

		if (questionSeen)
		{
			text.Append("\nAnswer with send_to_subagent({ id: \"<name>\", message: \"...\" }) — it is delivered as the answer, not as a new turn. ")
				.Append("The subagent stays blocked until you answer, the user answers on the card, or the question times out. ");
			// 把可回答的名字列出来：模型不用再猜哪个子代理在等。
			if (questionNames.Length > 0)
			{
				text.Append("Waiting now: ").Append(questionNames).Append(". ");
			}

			text.Append("Answer only what this conversation already authorizes; anything else belongs to the user.");
		}
		else if (timedOut)
		{
			text.Append("\nEnd your turn instead of waiting again; the result arrives as a message when it finishes.");
		}
		else if (any && stillRunning > 0)
		{
			text.Append("\nCall wait_subagents again to collect the rest; results not returned here are delivered after the current parent turn ends.");
		}

		text.Append(results);
		return new SubagentOutcome(true, text.ToString().TrimEnd(), new JsonObject
		{
			["timed_out"] = timedOut,
			["reason"] = questionSeen ? "question" : timedOut ? "timeout" : any && stillRunning > 0 ? "any" : "completed",
			["any"] = any,
			["still_running"] = stillRunning,
			["questions"] = questionDetails,
			["subagents"] = details,
		});
	}

	/// 这个子代理名下仍在进行中的团队任务；给 wait / list / read 的 Details 用。
	/// 任务板没开时返回 null，不占字段。
	private JsonArray? TeamTasksJson(string owner)
	{
		if (_team is null)
		{
			return null;
		}

		var tasks = _team.Tasks
			.Where(task => task.Status == TeamTaskStatus.InProgress &&
				string.Equals(task.Owner, owner, StringComparison.Ordinal))
			.ToArray();
		if (tasks.Length == 0)
		{
			return null;
		}

		return new JsonArray([.. tasks.Select(static task => (JsonNode)new JsonObject
		{
			["id"] = task.Id,
			["revision"] = task.Revision,
			["subject"] = task.Subject,
			["status"] = task.Status,
		})]);
	}

	/// <summary>列本会话开出的子代理；给 <c>list_subagents</c> 用。</summary>
	/// <param name="details">同一份列表的结构化形式，给界面与程序调用方用。</param>
	/// <param name="status">按状态过滤：active / starting / running / idle / completed / failed / stopped。</param>
	/// <param name="agent">按角色名过滤，忽略大小写。</param>
	public string Describe(out JsonObject details, string? status = null, string? agent = null)
	{
		var rows = new JsonArray();
		// 每个运行一行、每个角色一行；先给足容量，避免多子代理时反复扩容。
		var text = new StringBuilder(128 + (_order.Count + _catalog.All.Count) * 96);
		var active = 0;
		var statusFilter = (status ?? string.Empty).Trim().ToLowerInvariant();
		var agentFilter = (agent ?? string.Empty).Trim();
		var listedRuns = 0;
		lock (_gate)
		{
			foreach (var candidate in _runs.Values)
			{
				if (candidate.Run.IsActive)
				{
					active++;
				}
			}

			// 带上容量：模型知道还能不能再派，不必试一次才收到拒绝。
			text.Append(_order.Count == 0 ? "no subagents in this session" : $"subagents: {_order.Count}");
			text.Append(" · running ").Append(active).Append('/').Append(_maxActive);
			if (statusFilter.Length > 0 || agentFilter.Length > 0)
			{
				text.Append(" · filtered");
			}

			text.Append('\n');
			foreach (var name in _order)
			{
				var run = _runs[name].Run;
				if (!MatchesListFilter(run, statusFilter, agentFilter))
				{
					continue;
				}

				listedRuns++;
				rows.Add((JsonNode)new JsonObject
				{
					["name"] = run.Name,
					["agent"] = run.Agent,
					["session_id"] = run.SessionId,
					["session_file"] = run.SessionFile,
					["cwd"] = run.Cwd is { Length: > 0 } runCwd ? runCwd : null,
					["state"] = StateName(run.State),
					["turns"] = run.Turns,
					["tool_calls"] = run.ToolCalls,
					["changed_files"] = run.ChangedFiles is { Count: > 0 } changed
						? new JsonArray([.. changed.Select(static file => (JsonNode)file)])
						: null,
					["may_write"] = run.MayWrite,
					["worktree_path"] = run.WorktreePath,
					["worktree_branch"] = run.WorktreeBranch,
					["worktree_base"] = run.WorktreeBase,
					["team_tasks"] = TeamTasksJson(run.Name),
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
					["question"] = run.Question,
					["restored"] = run.Restored,
					["max_tokens"] = run.MaxTokens,
					["max_cost"] = run.MaxCost > 0 ? run.MaxCost : null,
					["max_tool_calls"] = run.MaxToolCalls > 0 ? run.MaxToolCalls : null,
					["stop_reason"] = run.StopReason is { Length: > 0 } stopReason ? stopReason : null,
					["verify"] = VerifyDetails(run.Verify),
					["delivery_suppressed"] = run.DeliverySuppressed,
					["steer_pending"] = run.SteerPending,
					["resumable"] = run.SessionFile is { Length: > 0 },
					["result"] = run.Result is { Length: > 0 } result ? Clip(result, 400) : null,
				});

				text.Append("- ").Append(run.Name).Append(" (").Append(run.Agent).Append(") · ")
					.Append(StateName(run.State))
					.Append(" · ").Append(run.Turns).Append(" turns");
				if (run.ToolCalls > 0)
				{
					text.Append(" · ").Append(run.ToolCalls).Append(" tool calls");
				}

				if (run.ChangedFiles.Count > 0)
				{
					text.Append(" · ").Append(run.ChangedFiles.Count).Append(" files changed");
				}
				else if (run.MayWrite)
				{
					text.Append(" · may write files");
				}

				if (run.WorktreePath is { Length: > 0 })
				{
					text.Append(" · worktree ").Append(run.WorktreeBranch ?? run.WorktreePath);
				}
				if (run.AwaitingUser)
				{
					text.Append(" · waiting for user input");
				}

				if (run.Question is { Length: > 0 } question)
				{
					text.Append(" · asks: ").Append(Clip(question, 300));
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

				if (run.DeliverySuppressed)
				{
					text.Append(" · result not auto-delivered; wait_subagents can collect it");
				}

				if (run.SteerPending)
				{
					text.Append(" · steer queued for the next model request");
				}

				// 历史项能不能继续发消息，就看会话文件还在不在。
				if (run.Restored && run.SessionFile is { Length: > 0 })
				{
					text.Append(" · resumable");
				}

				text.Append('\n');
			}

			if (listedRuns == 0 && (statusFilter.Length > 0 || agentFilter.Length > 0))
			{
				text.Append("- (no subagent matched the filter)\n");
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
				// 模型选角色时能看到的执行面；路径与正文不暴露。
				["model"] = definition.Model,
				["thinking"] = definition.Thinking,
				["tools"] = definition.Tools is { Count: > 0 } tools
					? new JsonArray([.. tools.Select(static tool => (JsonNode)tool)])
					: null,
				["exclude_tools"] = definition.ExcludeTools is { Count: > 0 } excluded
					? new JsonArray([.. excluded.Select(static tool => (JsonNode)tool)])
					: null,
				["timeout_minutes"] = definition.TimeoutMinutes,
				["max_tokens"] = definition.MaxTokens,
				["max_cost"] = definition.MaxCost,
				["max_tool_calls"] = definition.MaxToolCalls,
			});

			if (listed >= MaxListedAgents)
			{
				continue;
			}

			// 未广告的角色只能在这里被发现；只给名字不够，模型不知道它是做什么的。
			text.Append("- ").Append(definition.Name).Append(": ")
				.Append(Clip(definition.Description, MaxAgentDescriptionChars));
			if (definition.Model is { Length: > 0 } model)
			{
				text.Append(" · ").Append(model);
			}

			if (definition.Thinking is { Length: > 0 } thinking)
			{
				text.Append(" · ").Append(thinking);
			}

			if (definition.Tools is { Count: > 0 } roleTools)
			{
				text.Append(" · tools: ").Append(Clip(string.Join(",", roleTools), 120));
			}

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
			["running"] = active,
			["limit"] = _maxActive,
			["list"] = _order.Count,
			["listed"] = listedRuns,
			["filter"] = statusFilter.Length > 0 || agentFilter.Length > 0
				? new JsonObject { ["status"] = statusFilter, ["agent"] = agentFilter }
				: null,
			["subagents"] = rows,
			["available"] = available,
		};
		return text.ToString();
	}

	/// 子目录是否在父工作区内：先解析路径本身的符号链接，再交给 GetRelativePath
	/// 处理大小写、分隔符与不同盘符。父级目录上的链接仍不解析，但这已经挡住最常见的
	/// `cwd: "vendor"` 指向外部目录的情况。
	private static bool IsWithinWorkspace(string root, string path)
	{
		var relative = Path.GetRelativePath(ResolveDirectory(root), ResolveDirectory(path));
		if (relative == ".")
		{
			return true;
		}

		if (Path.IsPathRooted(relative) || relative == "..")
		{
			return false;
		}

		return !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
			!relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
	}

	private static string ResolveDirectory(string path)
	{
		try
		{
			return new DirectoryInfo(path).ResolveLinkTarget(returnFinalTarget: true)?.FullName
				?? Path.GetFullPath(path);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or PlatformNotSupportedException)
		{
			return Path.GetFullPath(path);
		}
	}

	/// 列表过滤：状态接受 active（启动中或运行中）与四个终态名；角色名忽略大小写。
	/// 未知状态不在这里报错，工具层先校验；直接调用 Describe 时按无匹配处理。
	private static bool MatchesListFilter(SubagentRun run, string status, string agent)
	{
		if (agent.Length > 0 && !string.Equals(run.Agent, agent, StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}

		return status switch
		{
			"" => true,
			"active" => run.IsActive,
			"starting" => run.State == SubagentState.Starting,
			"running" => run.State == SubagentState.Running,
			"idle" or "completed" => run.State == SubagentState.Idle,
			"failed" => run.State == SubagentState.Failed,
			"stopped" => run.State == SubagentState.Stopped,
			_ => false,
		};
	}

	/// 读带工具轨迹的转录：从尾部向前装，超预算时保留最近的一部分。
	/// 工具输出单独截到 1.2k，避免一条 bash 结果把整份转录挤满。
	private static (string Body, bool Truncated) ReadTranscript(IReadOnlyList<Message> messages, int limit)
	{
		var parts = new List<string>(Math.Min(messages.Count, 64));
		var used = 0;
		var truncated = false;
		for (var index = messages.Count - 1; index >= 0; index--)
		{
			var text = TranscriptText(messages[index]);
			if (text.Length == 0)
			{
				continue;
			}

			if (used + text.Length > limit)
			{
				if (parts.Count == 0)
				{
					parts.Add(Clip(text, limit));
					used = limit;
				}

				truncated = true;
				break;
			}

			parts.Add(text);
			used += text.Length;
		}

		parts.Reverse();
		return (string.Join("\n\n", parts), truncated);
	}

	private static string TranscriptText(Message message)
	{
		switch (message)
		{
			case UserMessage user:
			{
				var text = user.Content.Text ?? ContentText(user.Content.Blocks);
				return text is { Length: > 0 } ? string.Concat("[user] ", text) : string.Empty;
			}
			case AssistantMessage assistant:
			{
				var builder = new StringBuilder();
				var body = MessageText.Of(assistant);
				if (body.Length > 0)
				{
					builder.Append("[assistant] ").Append(body);
				}

				foreach (var block in assistant.Content)
				{
					if (block is not ToolCall call)
					{
						continue;
					}

					if (builder.Length > 0)
					{
						builder.Append('\n');
					}

					builder.Append("→ ").Append(call.Name);
					if (call.Arguments.Count > 0)
					{
						builder.Append(' ').Append(Clip(call.Arguments.ToJsonString(PiJson.NodeOptions), 200));
					}
				}

				return builder.ToString();
			}
			case ToolResultMessage tool:
			{
				var text = Clip(ContentText(tool.Content), 1_200);
				var marker = tool.IsError ? "[tool error " : "[tool ";
				return string.Concat(marker, tool.ToolName, "] ", text);
			}
			default:
				return string.Empty;
		}
	}

	private static string ContentText(IReadOnlyList<ContentBlock>? content)
	{
		if (content is not { Count: > 0 })
		{
			return string.Empty;
		}

		var builder = new StringBuilder();
		foreach (var block in content)
		{
			if (block is TextContent text)
			{
				builder.Append(text.Text);
			}
		}

		return builder.ToString();
	}

	/// <summary>读子会话最近的助手正文；结果被截断或重启后需要全文时用。</summary>
	/// <param name="target">子代理名或会话 id。</param>
	/// <param name="maxChars">正文上限；小于等于零用默认 12k，最大 48k。</param>
	/// <param name="includeTools">把用户消息、工具调用与工具结果一起读回来；默认只读助手正文。</param>
	public SubagentOutcome Read(string target, int maxChars = MaxResultChars, bool includeTools = false)
	{
		RunState? state;
		CodingAgentSession? session;
		string? file;
		long readTurn;
		lock (_gate)
		{
			state = ResolveOneLocked(target);
			if (state is null)
			{
				return new SubagentOutcome(false, $"no subagent matched \"{target}\"");
			}

			session = state.Session;
			file = state.Run.SessionFile;
			// 读正文在锁外：先记下当时是第几轮，回来时这一轮已经变了就不能标已投递。
			readTurn = state.ResultTurn;
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

		var run = state.Run;
		string body;
		bool truncated;
		if (includeTools)
		{
			(body, truncated) = ReadTranscript(messages, limit);
		}
		else
		{
			var texts = new List<string>();
			var used = 0;
			truncated = false;
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
			body = texts.Count > 0 ? string.Join("\n\n---\n\n", texts) : string.Empty;
		}

		if (body.Length == 0 && run.Result is { Length: > 0 } result)
		{
			body = Clip(result, limit);
			truncated = result.Length > limit;
		}

		// 父会话已经看过这段正文：结束后的补投不再推一遍。
		// 结构化结果与验证证据不在正文里，有它们时仍要投递。
		lock (_gate)
		{
			state.LastReadText = body.Length > 0 ? body : null;
			// 这一轮已经结束、读到的是同一轮的完整正文：用户/父会话已经看到内容，
			// 停投标记就该收起。失败或带验证证据的结果不再自动投递，但同样算已读。
			var readCompletedTurn = body.Length > 0 &&
				!truncated &&
				state.ResultTurn == readTurn &&
				state.Run.IsTerminal &&
				state.ResultTurn > 0;
			if (readCompletedTurn)
			{
				state.Run = state.Run with { DeliverySuppressed = false };
			}

			// 只有「正常完成、完整读到最终正文、没有额外证据」才记成已投递，
			// 避免 read 把失败/验证证据当成结论销账。
			if (readCompletedTurn &&
				state.Run.State == SubagentState.Idle &&
				!string.Equals(state.Run.StopReason, "length", StringComparison.Ordinal) &&
				state.Run.StructuredOutput is null &&
				state.Run.Verify.Count == 0 &&
				state.Run.Result is { Length: > 0 } final &&
				body.EndsWith(final, StringComparison.Ordinal))
			{
				state.DeliveredTurn = state.ResultTurn;
				_pending.RemoveAll(result =>
					string.Equals(result.Name, run.Name, StringComparison.Ordinal) &&
					result.Turn <= state.ResultTurn);
			}
		}

		var builder = new StringBuilder(body.Length + 160);
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
			["cwd"] = state.Run.Cwd is { Length: > 0 } cwd ? cwd : null,
			["worktree_path"] = run.WorktreePath,
			["worktree_branch"] = run.WorktreeBranch,
			["worktree_base"] = run.WorktreeBase,
			["team_tasks"] = TeamTasksJson(run.Name),
			["delivery_suppressed"] = state.Run.DeliverySuppressed,
			["steer_pending"] = state.Run.SteerPending,
			["include_tools"] = includeTools,
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
		lock (_gate)
		{
			state = ResolveOneLocked(target);
		}

		if (state is null)
		{
			return new SubagentOutcome(false, $"no subagent matched \"{target}\"");
		}

		// 启动中还没有子会话：等宿主把会话建好，别按「历史项」走冷恢复。
		// 建会话可能要几秒（worktree、MCP 工具），这段时间里的消息要能排队。
		if (state.Run.State == SubagentState.Starting && state.Session is null)
		{
			try
			{
				await state.StartGate.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
			}
			catch (TimeoutException)
			{
				return new SubagentOutcome(false,
					$"{state.Run.Name} is still starting; send again once its session is ready");
			}

			lock (_gate)
			{
				if (_disposed)
				{
					return new SubagentOutcome(false, "the session is closing");
				}

				if (!_runs.TryGetValue(state.Run.Name, out var current) || !ReferenceEquals(current, state))
				{
					return new SubagentOutcome(false, $"{state.Run.Name} failed to start");
				}
			}

			if (state.Run.SessionId.Length == 0 && state.Run.State is SubagentState.Stopped or SubagentState.Failed)
			{
				return new SubagentOutcome(false, $"{state.Run.Name} was stopped before its first turn");
			}
		}

		bool running;
		bool canSteer;
		lock (_gate)
		{
			running = state.Run.IsActive || state.Session is { IsRunning: true };
			// steer 只认「正在跑」：状态已经回 idle、但 PromptAsync 还没完全退栈时不收，
			// 否则消息会被塞进一个已经结束的回合。
			canSteer = state.Run.State == SubagentState.Running && state.Session is { IsRunning: true };
		}

		// 子代理正停在 ask_parent 上：这条消息就是回答，不另起一轮。
		PendingAsk? answer = null;
		lock (_gate)
		{
			if (state.Question is not null)
			{
				answer = TakeQuestionLocked(state, byParent: true);
			}
		}

		if (answer is not null)
		{
			PublishAnswer(state, answer, Clip(text, MaxAnswerChars));
			return new SubagentOutcome(true, $"answered {state.Run.Name}'s question");
		}

		// 停掉/超时的回合可能还在 abort：这时把消息排进 follow-up 会落在一个
		// 不再轮询队列的运行里，既送不到也不报错。让调用方等一下子再发。
		if (state.Run.State is SubagentState.Stopped or SubagentState.Failed &&
			state.Session is { IsRunning: true })
		{
			return new SubagentOutcome(false, $"{state.Run.Name} is still stopping; wait for it to settle before sending");
		}

		// 验证期间状态还是 running，但子会话已经空了：这时起新一轮会让验证任务把结果写到新一轮头上。
		if (state.VerifyCancellation is not null)
		{
			return new SubagentOutcome(false,
				$"{state.Run.Name} is running the host verification; wait for it to finish before sending");
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

				// 空闲的写子代理继续跑时重新占名额：严格模式下不能让 send 绕过写者上限。
				// 隔离副本不占父工作区名额，它和主工作区不在一个目录。
				if (state.Writer && state.Run.WorktreePath is null && state.WriterLease is null)
				{
					if (!WorkspaceWriterGate.TryAcquire(_owner.Cwd, _limits.MaxWriters, out var writerLease))
					{
						return new SubagentOutcome(false,
							$"writer limit reached: this workspace already has {_limits.MaxWriters} writing subagent(s). " +
							"Wait for one to finish, or raise settings.subagents.maxWriters.");
					}

					state.WriterLease = writerLease;
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
			// Stopping 只属于被停的那一轮，新消息要能正常收尾；Guarded 留到新一轮的 AgentStart
			// 再清，否则被停/超时那一轮迟到的 agent_end 会把这一轮写成它的收尾。
			state.Stopping = false;
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
			lock (_gate)
			{
				state.PendingSteer = outgoing;
				state.Run = state.Run with { SteerPending = true };
			}

			// Steer 与排空可能交错：入队后立刻看一次队列。若已经不在队列里，
			// 说明模型刚好在边界把它取走，标记不该继续挂着。
			var stillQueued = true;
			try
			{
				stillQueued = false;
				foreach (var queued in session.PendingQueues().Steering)
				{
					if (ReferenceEquals(queued, outgoing))
					{
						stillQueued = true;
						break;
					}
				}
			}
			catch (ObjectDisposedException)
			{
				// 子会话在投递中途关闭：保持 pending，让关闭路径统一清除。
			}

			if (!stillQueued)
			{
				lock (_gate)
				{
					if (ReferenceEquals(state.PendingSteer, outgoing))
					{
						state.PendingSteer = null;
						state.Run = state.Run with { SteerPending = false };
					}
				}
			}

			Changed?.Invoke();
			return new SubagentOutcome(
				true,
				$"steered {state.Run.Name}; it is delivered before the next model request",
				new JsonObject { ["delivery"] = "steer", ["pending"] = stillQueued });
		}

		// 首轮还没真正开始（建会话到 AgentStart 之间）：直接排队。
		// 这时 DeliverMessageAsync 看到 IsRunning=false 会再起一个 PromptAsync，和首轮抢子会话。
		if (state.Run.State == SubagentState.Starting)
		{
			session.FollowUp(outgoing);
			return new SubagentOutcome(true, $"queued for {state.Run.Name}");
		}

		if (!running)
		{
			// 从「空闲」到 AgentStart 之间还有一段：标上，免得回收把刚送出的一轮连会话一起 dispose。
			lock (_gate)
			{
				state.Sending = true;
			}
		}

		// 只投递不等结果：send 的语义是「排进去」，结果照常走完成消息。
		_ = DeliverQuietlyAsync(state, session, outgoing);
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
					Model = run.Model is { Length: > 0 } model ? model : null,
					Thinking = run.Thinking is { Length: > 0 } thinking ? thinking : null,
					Cwd = run.Cwd is { Length: > 0 } cwd ? cwd : null,
					OutputSchema = state.OutputSchema?.DeepClone().AsObject(),
					Verify = state.VerifyCommands,
					WorktreePath = run.WorktreePath,
					WorktreeBranch = run.WorktreeBranch,
					WorktreeBase = run.WorktreeBase,
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
			// 回收路径通常已经摘过订阅；父会话被回收、NoteChildClosed 没跑到的边角里这里补一次。
			current.Subscription?.Dispose();
			current.Subscription = subscription;
			// Stopping 只描述「当前这一轮被叫停」，冷恢复之后新的一轮要能正常收尾。
			current.Stopping = false;
			current.PendingSteer = null;
			current.ActiveTools.Clear();
			current.Run = current.Run with
			{
				Restored = false,
				SteerPending = false,
				// 只归一起不来的启动态：冷恢复本身不代表上一轮是成功的，
				// 把 failed/stopped 改成 idle 会让卡片和 list 与最后结果自相矛盾。
				State = run.State == SubagentState.Starting ? SubagentState.Idle : run.State,
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
			// 终结状态已经写好了：这个回合迟到的 agent_end 不能再落一次，否则会和随后的
			// send 抢同一个 RunState（把新一轮写成「已停止」）。
			state.Guarded = true;
			state.Generation++;
			// 挂在问题上的那一轮先解开：否则子代理的工具要等满问题时限才醒。
			state.Question?.Timeout.Cancel();
			state.Question = null;
			state.PendingSteer = null;
			state.Run = state.Run with { Question = null, SteerPending = false };
			CancelDeadlineLocked(state);
			CancelVerifyLocked(state);
			state.ActiveTools.Clear();
			state.Run = state.Run with
			{
				State = SubagentState.Stopped,
				Activity = null,
				Progress = null,
				AwaitingUser = false,
				FinishedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
			};
			state.WriterLease?.Dispose();
			state.WriterLease = null;
			state.StartGate.TrySetResult();
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

	/// <summary>隔离副本已被删除：清掉运行里的路径与分支，重启后不再显示一个不存在的目标。</summary>
	public bool ClearWorktree(string target)
	{
		RunState? state;
		lock (_gate)
		{
			state = ResolveOneLocked(target);
			if (state is null)
			{
				return false;
			}

			state.Run = state.Run with
			{
				WorktreePath = null,
				WorktreeBranch = null,
				WorktreeBase = null,
			};
		}

		AppendDescriptor(state, "worktree-removed");
		Changed?.Invoke();
		return true;
	}

	/// <summary>会话文件被删除：把这条运行记录从列表里摘掉，命令面板不再挂一个打不开的目标。</summary>
	/// <remarks>只接受终态；正在跑或等回答的不动，删除前先停。</remarks>
	public bool Forget(string target)
	{
		RunState? state;
		CodingAgentSession? session;
		lock (_gate)
		{
			state = ResolveOneLocked(target);
			// 与空闲回收同一判据：正在跑、等回答、send 已发未开跑、abort 未退栈都不算终态。
			if (state is null || !CanUnloadLocked(state))
			{
				return false;
			}

			session = state.Session;
			state.Subscription?.Dispose();
			state.Subscription = null;
			state.Session = null;
			state.WriterLease?.Dispose();
			state.WriterLease = null;
			state.StartGate.TrySetResult();
			_runs.Remove(state.Run.Name);
			_order.Remove(state.Run.Name);
			_pending.RemoveAll(result => string.Equals(result.Name, state.Run.Name, StringComparison.Ordinal));
		}

		// 父会话文件里留一条墓碑：重启后的 Restore 才不会把已删的会话文件又挂回来。
		AppendDescriptor(state, "forgotten");
		session?.Dispose();
		Changed?.Invoke();
		return true;
	}

	/// <summary>子会话被宿主关掉：关系保留为 stopped，不再订阅。</summary>
	public void NoteChildClosed(string sessionId, CodingAgentSession? session = null)
	{
		RunState? state;
		lock (_gate)
		{
			if (_disposed)
			{
				return;
			}

			state = FindStateLocked(sessionId);
			if (state is null || (session is not null && !ReferenceEquals(state.Session, session)))
			{
				// 回收与冷恢复交错：这条通知属于已经被换掉的旧会话，别动现在这个。
				return;
			}

			state.Subscription?.Dispose();
			state.Subscription = null;
			state.Session = null;
			state.StartGate.TrySetResult();
			state.ActiveTools.Clear();
			state.Generation++;
			CancelDeadlineLocked(state);
			CancelVerifyLocked(state);

			if (!state.Unloading)
			{
				// 已排队的 AgentEnd 可能在关闭之后才到：标成 Stopping 与 Guarded，迟到的收尾直接忽略，
				// 不能把已关闭的子会话又改回 Idle 并投递结果。
				state.Stopping = true;
				state.Guarded = true;
				state.Question?.Timeout.Cancel();
				state.Question = null;
				state.PendingSteer = null;
				// 子会话标签关掉后不能继续发消息，状态一律落到 stopped。
				state.Run = state.Run with
				{
					State = SubagentState.Stopped,
					Question = null,
					Activity = null,
					Progress = null,
					AwaitingUser = false,
					SteerPending = false,
					FinishedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
				};
				state.WriterLease?.Dispose();
				state.WriterLease = null;
				state.Completion.TrySetResult(new TurnResult(0, string.Empty));
			}
			// 空闲回收（Unloading）只把活会话摘掉：条目、状态与文件留着，
			// send_to_subagent 与打开转录都会冷恢复。
		}

		Changed?.Invoke();
	}

	/// <summary>
	/// 子会话开始或结束等待用户回答：只更新展示状态，同时把运行截止时间暂停与恢复。
	/// 找不到对应运行（历史项、关联会话）时返回 <see langword="null"/>。
	/// </summary>
	/// <param name="sessionId">子会话 id。</param>
	/// <param name="awaiting">是否正在等回答。</param>
	public SubagentRun? NoteChildAwaiting(string sessionId, bool awaiting)
	{
		SubagentRun? snapshot;
		var changed = false;
		var notify = false;
		lock (_gate)
		{
			if (_disposed)
			{
				return null;
			}

			var state = FindStateLocked(sessionId);
			if (state is null || state.Run.IsTerminal)
			{
				return null;
			}

			// 幂等地维护「等待就不计时」：状态没变也要走一遍，把漏掉的暂停补上。
			changed = state.Run.AwaitingUser != awaiting;
			SetAwaitingLocked(state, awaiting);
			snapshot = state.Run;
			notify = awaiting && changed;
		}

		if (notify)
		{
			AwaitingChanged?.Invoke(snapshot);
		}

		Changed?.Invoke();
		return changed ? snapshot : null;
	}

	/// <summary>
	/// 等用户回答的开合：改展示字段，同时暂停/恢复运行截止时间。
	/// 两个来源都会调到这里：子会话自己的 <c>ask_question</c> 工具起止（hub 内部），
	/// 与桌面把问询面板的开合同步过来（父标签开着时更早一步）。重复调用是空操作。
	/// </summary>
	private void SetAwaitingLocked(RunState state, bool awaiting)
	{
		if (state.Run.IsTerminal)
		{
			return;
		}

		if (state.Run.AwaitingUser != awaiting)
		{
			state.Run = state.Run with { AwaitingUser = awaiting };
		}

		RefreshWaitPauseLocked(state);
	}

	/// 子代理在等人（用户或父会话）时不该烧运行预算：两种等待共用这一处暂停/恢复，
	/// 谁最后解除谁负责重新起表。
	private void RefreshWaitPauseLocked(RunState state)
	{
		if (state.Guarded || state.Stopping || state.Run.State != SubagentState.Running)
		{
			return;
		}

		if (state.Run.AwaitingUser || state.Question is not null)
		{
			// 只看计时器是否真的在走：重复到来的「开始等」也要把表停掉。
			if (state.Deadline is not null)
			{
				PauseDeadlineLocked(state);
			}

			return;
		}

		// 验证期间不计时：那一轮已经结束，计时留给验证命令自己的超时。
		if (state.VerifyCancellation is not null || state.Deadline is not null)
		{
			return;
		}

		// 回答完至少再给原预算的一半（最多 30 秒）：剩下的几秒不够模型把一句话说完，
		// 结果就是刚恢复又超时，回答白输入了。
		var floor = state.RunTimeout > TimeSpan.Zero ? state.RunTimeout * 0.5 : MaxResumeWindow;
		if (floor > MaxResumeWindow)
		{
			floor = MaxResumeWindow;
		}

		var resume = state.DeadlineRemaining > floor ? state.DeadlineRemaining : floor;
		StartDeadlineLocked(state, resume);
	}

	/// 起表并登记；调用方已持锁。
	private void StartDeadlineLocked(RunState state, TimeSpan remaining)
	{
		if (_disposed)
		{
			// 关会话后仍可能有排队的 AgentStart 事件：不装表，免得留下一个跑满预算的定时器。
			return;
		}

		if (remaining <= TimeSpan.Zero)
		{
			remaining = TimeSpan.FromMilliseconds(1);
		}

		var deadline = new CancellationTokenSource();
		CancelDeadlineLocked(state);
		state.Deadline = deadline;
		state.DeadlineRemaining = remaining;
		state.DeadlineStartedAt = Stopwatch.GetTimestamp();
		_ = RunDeadlineAsync(state.Run.Name, state, deadline, remaining);
	}

	/// 回答完最多补这么多时间；再长就超出它自己那一轮的意义了。
	private static readonly TimeSpan MaxResumeWindow = TimeSpan.FromSeconds(30);

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
				state.StartGate.TrySetResult();
				state.Question?.Timeout.Cancel();
				state.Question = null;
				CancelDeadlineLocked(state);
				CancelVerifyLocked(state);
				if (state.Run.IsActive)
				{
					// 父会话关了，结果已经无处投递：停掉正在跑的子代理，别继续烧 token。
					// 子会话标签与文件保留，用户仍可在子标签里读或继续。
					state.Stopping = true;
					state.PendingSteer = null;
					state.Run = state.Run with
					{
						State = SubagentState.Stopped,
						SteerPending = false,
						FinishedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
					};
					state.WriterLease?.Dispose();
					state.WriterLease = null;
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
				StartTool(name, tool.ToolCallId, DescribeTool(tool.ToolName, tool.Arguments), tool.ToolName, tool.Arguments);
				if (string.Equals(tool.ToolName, AskQuestionToolName, StringComparison.Ordinal))
				{
					// 暂停计时由 hub 自己驱动：桌面没开着父标签时，那条展示同步根本不跑。
					SetAwaiting(name, awaiting: true);
				}

				break;

			case ToolExecutionEndEvent tool:
				// 并行工具里一个结束不代表都结束：仍活跃的按最后开始的显示。
				EndTool(name, tool.ToolCallId);
				if (string.Equals(tool.ToolName, AskQuestionToolName, StringComparison.Ordinal))
				{
					SetAwaiting(name, awaiting: false);
				}

				break;

			case MessageEndEvent { Message: AssistantMessage assistant }:
				// 进度与用量一起记：同一拍里两件事、一次界面刷新。
				NoteAssistantMessage(name, assistant);
				break;

			case QueueUpdateEvent update:
				NoteQueueUpdate(name, update);
				break;

			case AgentEndEvent { WillRetry: false } end:
			{
				var last = MessageText.LastAssistant(end.Messages);
				var text = last is null ? string.Empty : MessageText.Of(last);
				var failed = last?.StopReason is StopReason.Error or StopReason.Aborted;
				var reason = StopReasonName(last?.StopReason);
				if (failed && text.Length == 0)
				{
					text = last?.ErrorMessage ?? string.Empty;
				}

				FinishRun(name, text, failed, reason);
				break;
			}
		}

		return ValueTask.CompletedTask;
	}

	private static string StopReasonName(StopReason? reason) => reason switch
	{
		StopReason.Length => "length",
		StopReason.Error => "error",
		StopReason.Aborted => "aborted",
		StopReason.Stop => "stop",
		StopReason.ToolUse => "tool_use",
		StopReason.Deferred => "deferred",
		_ => string.Empty,
	};

	private void SetRunning(string name)
	{
		TimeSpan budget;
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
			// 上一轮读过的正文不该替下一轮销账：两轮回复相同的时候尤其明显。
			state.LastReadText = null;
			// 预算按轮算：新一轮有新额度，上一轮的消耗不会把这一轮提前卡住。
			state.BudgetTokens = 0;
			state.BudgetCost = 0;
			state.TokenBase = state.Run.Tokens;
			state.CostBase = state.Run.Cost;
			state.ToolCallBase = state.Run.ToolCalls;
			state.CheckpointSent = false;
			state.ParentAnswers = 0;
			state.Sending = false;
			state.Generation++;
			budget = state.RunTimeout > TimeSpan.Zero ? state.RunTimeout : _limits.RunTimeout;
			state.DeadlineRemaining = budget;
			state.DeadlineStartedAt = 0;
			state.Run = state.Run with
			{
				State = SubagentState.Running,
				Activity = null,
				Progress = null,
				AwaitingUser = false,
				// 上一轮的验证、停止原因与停投标记属于上一轮：运行中展开区不该还挂着旧结论。
				Verify = [],
				StopReason = string.Empty,
				DeliverySuppressed = false,
			};
			state.Guarded = false;
			// 上一轮的问题不可能还挂着；万一有（停掉后马上又起一轮），让它按「没人回答」收尾。
			state.Question?.Timeout.Cancel();
			state.Question = null;
			state.Run = state.Run with { Question = null };
			// 在同一把锁里起表：否则 UI 线程刚好在此刻标上「等待回答」时看到 Deadline 还没装，
			// 暂停落空，用户读题的时间又会算进预算。
			StartDeadlineLocked(state, budget);
		}

		Changed?.Invoke();
	}

	/// 截止时间分两段：先推一次「收尾」，再把 run 停掉。
	/// 只砍不提醒会把子代理已经写出的半份结果白扔，而那正是父会话需要的东西。
	private async Task RunDeadlineAsync(
		string name,
		RunState state,
		CancellationTokenSource deadline,
		TimeSpan remaining)
	{
		var lead = CheckpointLead(remaining);
		// lead 为 0（短运行）时别再 steer：那句提醒会卡在到期那一刻，紧接着就是 abort。
		if (lead > TimeSpan.Zero && lead < remaining)
		{
			try
			{
				await Task.Delay(remaining - lead, deadline.Token).ConfigureAwait(false);
				RequestCheckpoint(name, state, lead);
				await Task.Delay(lead, deadline.Token).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				return;
			}
		}
		else
		{
			try
			{
				await Task.Delay(remaining, deadline.Token).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				return;
			}
		}

		await EnforceDeadlineAsync(name, state, deadline).ConfigureAwait(false);
	}

	/// 收尾提前量：短运行不值得打断，长运行给一分钟把话说完。
	internal static TimeSpan CheckpointLeadForTests(TimeSpan remaining) => CheckpointLead(remaining);

	/// 收尾提前量：短运行不值得打断，长运行给一分钟把话说完。
	private static TimeSpan CheckpointLead(TimeSpan remaining)
	{
		if (remaining < TimeSpan.FromMinutes(3))
		{
			return TimeSpan.Zero;
		}

		var lead = remaining * 0.1;
		return lead > TimeSpan.FromMinutes(2) ? TimeSpan.FromMinutes(2) : lead;
	}

	/// 到期前让子代理自己收尾：停止开新工作，把已完成与剩下的写进最终回复。
	private void RequestCheckpoint(string name, RunState state, TimeSpan lead)
	{
		CodingAgentSession? session;
		lock (_gate)
		{
			if (_disposed || state.Stopping || state.Guarded || state.CheckpointSent ||
				state.Run.State != SubagentState.Running || !_runs.TryGetValue(name, out var current) ||
				!ReferenceEquals(current, state))
			{
				return;
			}

			state.CheckpointSent = true;
			session = state.Session;
		}

		if (session is not { IsRunning: true })
		{
			return;
		}

		try
		{
			session.Steer(new UserMessage
			{
				// 用这次真正计时的提前量，不是整份预算重算一遍：暂停恢复后两者会差很多。
				Content = MessageContent.FromText(CheckpointText(lead)),
				Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
				CustomType = MessageType,
			});
		}
		catch (Exception)
		{
			// 子会话可能正在收尾：提醒送不到就只靠后面的硬超时。
		}
	}

	private static string CheckpointText(TimeSpan lead) => string.Concat(
		"来自宿主：这次运行还剩 ",
		FormatSpan(lead),
		"。停止开新工作，把已经完成的部分、改过的文件和剩下的工作写进最终回复，然后结束这一轮。");

	private static string FormatSpan(TimeSpan span) => span >= TimeSpan.FromMinutes(1)
		? string.Create(CultureInfo.InvariantCulture, $"{(int)Math.Round(span.TotalMinutes)} 分钟")
		: string.Create(CultureInfo.InvariantCulture, $"{(int)Math.Round(span.TotalSeconds)} 秒");

	private async Task EnforceDeadlineAsync(string name, RunState state, CancellationTokenSource deadline)
	{
		CodingAgentSession? session;
		string text;
		lock (_gate)
		{
			if (_disposed || !_runs.TryGetValue(name, out var current) || !ReferenceEquals(current, state) ||
				!ReferenceEquals(state.Deadline, deadline) ||
				state.Run.State is not (SubagentState.Starting or SubagentState.Running))
			{
				return;
			}

			state.Deadline = null;
			state.DeadlineStartedAt = 0;
			session = state.Session;
		}

		deadline.Dispose();
		var last = LastAssistantText(session);
		text = TimeoutText(state.RunTimeout > TimeSpan.Zero ? state.RunTimeout : _limits.RunTimeout);
		if (last is { Length: > 0 })
		{
			// 已经写出来的正文比「超时了」有用：父会话至少能拿这段继续做。
			text = string.Concat(text, "\n\n已写出的正文：\n", Clip(last, MaxResultChars));
		}

		FailRun(name, state, text, failed: true);
		try
		{
			session?.Abort();
		}
		catch (Exception)
		{
			// 子会话可能已经在关闭；结果已经写进状态与队列。
		}
	}

	/// 守卫（超时 / 超预算）的终结路径：写结果、标 guarded、投递，迟到的 agent_end 不再改状态。
	private void FailRun(string name, RunState state, string text, bool failed, IReadOnlyList<SubagentVerifyResult>? verify = null)
	{
		long turn;
		lock (_gate)
		{
			if (_disposed || state.Guarded || state.Stopping || !_runs.TryGetValue(name, out var current) ||
				!ReferenceEquals(current, state))
			{
				return;
			}

			state.Guarded = true;
			state.StructuredOutput = null;
			state.ActiveTools.Clear();
			state.Question?.Timeout.Cancel();
			state.Question = null;
			CancelDeadlineLocked(state);
			CancelVerifyLocked(state);
			state.ResultTurn = ++state.TurnSeq;
			turn = state.ResultTurn;
			// 用量与终端状态一起落：TCS 完成后可能马上起下一轮，TokenBase 不能再读到旧值。
			var tokens = state.Run.Tokens;
			var cost = state.Run.Cost;
			if (state.Session is { } usageSession)
			{
				var usage = UsageOf(usageSession);
				tokens = usage.Tokens > 0 ? usage.Tokens : tokens;
				cost = usage.Cost > 0 ? usage.Cost : cost;
			}

			state.PendingSteer = null;
			state.Run = state.Run with
			{
				State = failed ? SubagentState.Failed : SubagentState.Idle,
				Turns = state.Run.Turns + 1,
				Result = text,
				StructuredOutput = null,
				Activity = null,
				Progress = null,
				AwaitingUser = false,
				SteerPending = false,
				StopReason = failed ? "error" : state.Run.StopReason,
				Verify = verify ?? [],
				Tokens = tokens,
				Cost = cost,
				FinishedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
			};
			state.WriterLease?.Dispose();
			state.WriterLease = null;
			state.Completion.TrySetResult(new TurnResult(turn, text));
		}

		try
		{
			AppendDescriptor(state, "failed");
			Changed?.Invoke();
			QueueDelivery(new PendingResult(name, true, text, turn));
		}
		catch (Exception)
		{
			// fire-and-forget 路径：异常不能变成未观察的任务异常。
		}
	}

	private static void CancelDeadlineLocked(RunState state)
	{
		var deadline = state.Deadline;
		state.Deadline = null;
		state.DeadlineStartedAt = 0;
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

	/// 暂停截止时间：子代理在等用户回答，这段时间不该算进它的运行预算。
	/// 不暂停的话，用户离开半小时回来会看到一个已经被判超时、问题也丢了的子代理。
	private void PauseDeadlineLocked(RunState state)
	{
		if (state.DeadlineStartedAt > 0)
		{
			var elapsed = Stopwatch.GetElapsedTime(state.DeadlineStartedAt);
			state.DeadlineRemaining = state.DeadlineRemaining > elapsed
				? state.DeadlineRemaining - elapsed
				: TimeSpan.Zero;
		}

		CancelDeadlineLocked(state);
	}

	private static void CancelVerifyLocked(RunState state)
	{
		var verify = state.VerifyCancellation;
		state.VerifyCancellation = null;
		if (verify is null)
		{
			return;
		}

		try
		{
			verify.Cancel();
		}
		catch (ObjectDisposedException)
		{
			// 验证任务已经收尾。
		}
	}

	private static string TimeoutText(TimeSpan budget) => budget >= TimeSpan.FromMinutes(1)
		? $"subagent timed out after {budget.TotalMinutes:0.#} minutes"
		: $"subagent timed out after {budget.TotalSeconds:0.#} seconds";

	/// 子代理此刻在干什么：工具名加一个能认出来的参数，卡片与 list_subagents 都用它。
	/// 并行工具调用各自记账：结束一个时若还有别的在跑，Activity 换成剩下最后开始的那个。
	private void StartTool(string name, string callId, string description, string toolName, JsonObject arguments)
	{
		RunState? state;
		string? budgetText = null;
		lock (_gate)
		{
			if (!_runs.TryGetValue(name, out state) || state.Stopping || state.Run.IsTerminal)
			{
				return;
			}

			RemoveToolLocked(state, callId);
			state.ActiveTools.Add((callId, description));

			// 写工具的 path 是确定的；bash/powershell 只能标「可能写入」。
			if (toolName is "edit" or "write" &&
				arguments.TryGetPropertyValue("path", out var pathNode) &&
				pathNode is JsonValue pathValue &&
				pathValue.TryGetValue<string>(out var path) &&
				path.Length > 0)
			{
				var cwd = state.Session?.Cwd ?? _owner.Cwd;
				var full = path;
				try
				{
					full = Path.GetFullPath(path, cwd);
				}
				catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
				{
					// 路径本身有问题：保留原样，至少让用户看到指向。
				}

				state.ChangedFiles.Add(full);
			}
			else if (toolName is "bash" or "powershell")
			{
				state.MayWrite = true;
			}

			state.Run = state.Run with
			{
				Activity = description,
				ToolCalls = state.Run.ToolCalls + 1,
				ChangedFiles = state.ChangedFiles.Count > state.Run.ChangedFiles.Count
					? [.. state.ChangedFiles]
					: state.Run.ChangedFiles,
				MayWrite = state.MayWrite,
				LastActivityAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
			};
			var runToolCalls = state.Run.ToolCalls - state.ToolCallBase;
			if (state.Run.MaxToolCalls > 0 && runToolCalls > state.Run.MaxToolCalls)
			{
				budgetText = string.Concat(
					"subagent stopped: this run called ",
					runToolCalls.ToString(CultureInfo.InvariantCulture),
					" tools, over the ",
					state.Run.MaxToolCalls.ToString(CultureInfo.InvariantCulture),
					" tool-call budget set for it");
			}
		}

		if (budgetText is not null)
		{
			var session = state!.Session;
			FailRun(name, state, budgetText, failed: true);
			try
			{
				session?.Abort();
			}
			catch (Exception)
			{
				// 子会话可能已经在关闭；状态已经落成 failed。
			}
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
			// 活动名可能不变（并行工具结束了一个），时间仍要推：卡片按它判断是否卡住。
			state.Run = state.Run with
			{
				Activity = activity,
				LastActivityAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
			};
		}

		Changed?.Invoke();
	}

	/// 一条助手消息落定：进度摘要与 token 用量一起更新，界面只刷一次。
	/// 预算也在这里判：超时那一道刹车之外再给一道成本闸，30 分钟足够一个小模型烧掉很多钱。
	private void NoteAssistantMessage(string name, AssistantMessage assistant)
	{
		var progress = FirstLineOf(assistant, MaxProgressChars);
		var usage = assistant.Usage;
		var delta = usage is null ? 0 : usage.Input + usage.Output;
		var costDelta = usage?.Cost.Total ?? 0;

		RunState? state;
		string? budgetText = null;
		var changed = false;
		lock (_gate)
		{
			if (!_runs.TryGetValue(name, out state) || state.Stopping || state.Run.IsTerminal)
			{
				return;
			}

			// 进度与用量合成一次 with：每条助手消息只复制一次 Run。
			var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
			var progressChanged = progress.Length > 0 &&
				!string.Equals(state.Run.Progress, progress, StringComparison.Ordinal);
			var usageChanged = delta > 0 || costDelta > 0;
			if (progressChanged || usageChanged)
			{
				if (delta > 0)
				{
					state.BudgetTokens += delta;
				}

				if (costDelta > 0)
				{
					state.BudgetCost += costDelta;
				}

				// 会话累计口径与结束时的统计一致；预算比的是本轮增量。
				state.Run = state.Run with
				{
					Progress = progressChanged ? progress : state.Run.Progress,
					Tokens = usageChanged ? state.TokenBase + state.BudgetTokens : state.Run.Tokens,
					Cost = usageChanged ? state.CostBase + state.BudgetCost : state.Run.Cost,
					LastActivityAt = now,
				};
				changed = true;
				if (state.Run.MaxTokens > 0 && state.BudgetTokens > state.Run.MaxTokens)
				{
					budgetText = string.Concat(
						"subagent stopped: this run used ",
						state.BudgetTokens.ToString(CultureInfo.InvariantCulture),
						" tokens, over the ",
						state.Run.MaxTokens.ToString(CultureInfo.InvariantCulture),
						" token budget set for it");
				}

				if (state.Run.MaxCost > 0 && state.BudgetCost > state.Run.MaxCost)
				{
					var costText = string.Concat(
						"subagent stopped: this run cost $",
						state.BudgetCost.ToString("0.####", CultureInfo.InvariantCulture),
						", over the $",
						state.Run.MaxCost.ToString("0.####", CultureInfo.InvariantCulture),
						" budget set for it");
					budgetText = budgetText is null ? costText : string.Concat(budgetText, "; ", costText);
				}
			}
		}

		if (budgetText is not null)
		{
			CodingAgentSession? session;
			lock (_gate)
			{
				session = state.Session;
			}

			FailRun(name, state, budgetText, failed: true);
			try
			{
				session?.Abort();
			}
			catch (Exception)
			{
				// 子会话可能已经在关闭；结果已经写进状态与队列。
			}

			return;
		}

		if (changed)
		{
			Changed?.Invoke();
		}
	}

	/// steering 队列变化：排进去的消息从队列里消失，就是模型已经在下一个工具边界读到了。
	/// 只在 pending 消息不再出现在队列时翻转一次；其余队列更新直接返回。
	private void NoteQueueUpdate(string name, QueueUpdateEvent update)
	{
		var changed = false;
		lock (_gate)
		{
			if (!_runs.TryGetValue(name, out var state) || state.PendingSteer is not { } pending)
			{
				return;
			}

			foreach (var message in update.Steering)
			{
				if (ReferenceEquals(message, pending))
				{
					return;
				}
			}

			state.PendingSteer = null;
			state.Run = state.Run with { SteerPending = false };
			changed = true;
		}

		if (changed)
		{
			Changed?.Invoke();
		}
	}

	/// 子会话自己的问询工具起止：与桌面的展示同步共用同一套暂停/恢复。
	private void SetAwaiting(string name, bool awaiting)
	{
		var changed = false;
		SubagentRun? snapshot = null;
		lock (_gate)
		{
			if (!_runs.TryGetValue(name, out var state))
			{
				return;
			}

			changed = state.Run.AwaitingUser != awaiting;
			SetAwaitingLocked(state, awaiting);
			if (awaiting && changed)
			{
				snapshot = state.Run;
			}
		}

		if (snapshot is not null)
		{
			AwaitingChanged?.Invoke(snapshot);
		}

		if (changed)
		{
			Changed?.Invoke();
		}
	}

	/// 问询工具名；子会话的工具面里只有这一个会等用户。
	private const string AskQuestionToolName = AskQuestionTool.Name;

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

	/// 角色可能有写工具：`tools` 省略时沿用父工具，按写者算；显式白名单只看有没有写工具。
	/// 计划模式不改变这个判断，严格模式在计划下也会拦住只读 bash。
	private static bool IsWriter(SubagentDefinition definition) =>
		!definition.ExcludeTools.Any(IsWriteToolName) &&
		(definition.Tools is null || definition.Tools.Any(IsWriteToolName));

	private static bool IsWriteToolName(string name) =>
		name is "edit" or "write" or "bash" or "powershell";

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

	/// 验证输出的尾巴上限：失败通常落在最后几行，正文里不必放全文。
	private const int MaxVerifyOutputChars = 1_200;

	/// 子代理一轮结束：先看要不要跑验证命令，再落终结状态。
	/// 配了验证的命令时这一轮停在 Running（卡片显示 verify），直到命令跑完才交给 wait 与投递。
	private void FinishRun(string name, string text, bool failed, string stopReason)
	{
		RunState? state;
		CancellationTokenSource? cancellation = null;
		lock (_gate)
		{
			if (!_runs.TryGetValue(name, out state) || failed || state.Guarded || state.Stopping ||
				state.VerifyCommands.Count == 0 || state.Session is not { IsDisposed: false })
			{
				state = null;
			}
			else
			{
				// 验证是宿主自己的事，再让它撞上运行时限只会把已有结论扔掉。
				CancelDeadlineLocked(state);
				state.Run = state.Run with
				{
					Activity = VerifyActivity(state.VerifyCommands[0]),
					Progress = null,
					// 验证可能跑十分钟，时间戳不推会显示「没有新动作」。
					LastActivityAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
				};
				// 在同一把锁里登记「验证中」：否则这段窗口里 send 会放行，
				// 起一轮把 generation 推高，验证结果连同刚结束那一轮的结论一起作废。
				cancellation = new CancellationTokenSource();
				state.VerifyCancellation = cancellation;
			}
		}

		if (state is null)
		{
			CompleteRun(name, text, failed, stopReason);
			return;
		}

		Changed?.Invoke();
		_ = VerifyThenCompleteAsync(name, state, text, failed, stopReason, cancellation!);
	}

	private static string VerifyActivity(string command) => string.Concat("verify: ", Clip(command.Replace('\n', ' ').Trim(), ActivityMaxChars));

	private async Task VerifyThenCompleteAsync(
		string name,
		RunState state,
		string text,
		bool failed,
		string stopReason,
		CancellationTokenSource cancellation)
	{
		using var _ = cancellation;
		var commands = state.VerifyCommands;
		var session = state.Session;
		var cwd = session?.Cwd ?? _owner.Cwd;
		var results = new List<SubagentVerifyResult>(commands.Count);
		int generation;
		lock (_gate)
		{
			if (_disposed || state.Stopping || state.Guarded ||
				!ReferenceEquals(state.VerifyCancellation, cancellation))
			{
				return;
			}

			generation = state.Generation;
		}

		try
		{
			foreach (var command in commands)
			{
				if (cancellation.IsCancellationRequested)
				{
					return;
				}

				var result = await ExecuteVerifyAsync(command, cwd, cancellation.Token).ConfigureAwait(false);
				results.Add(result);
				// 第一条失败就是定论；后面的不必再花时间。
				if (!result.Passed)
				{
					break;
				}
			}
		}
		finally
		{
			lock (_gate)
			{
				if (ReferenceEquals(state.VerifyCancellation, cancellation))
				{
					state.VerifyCancellation = null;
				}
			}
		}

		lock (_gate)
		{
			// 这一代已经不是验证时的 RunState 了（期间被停、被重新发消息）：结果交给那一轮的路径。
			if (_disposed || state.Stopping || state.Guarded || state.Generation != generation)
			{
				return;
			}
		}

		var passed = results.All(static result => result.Passed);
		CompleteRun(name, text, failed || !passed, stopReason, results);
	}

	private async Task<SubagentVerifyResult> ExecuteVerifyAsync(string command, string cwd, CancellationToken cancellationToken)
	{
		try
		{
			var result = await ShellRunner
				.RunAsync(
					command,
					cwd,
					_limits.VerifyTimeout.TotalSeconds,
					onOutput: null,
					cancellationToken,
					new ShellInvocation
					{
						ShellPath = _owner.Settings.ShellPath,
						CommandPrefix = _owner.Settings.ShellCommandPrefix,
						TempFilePrefix = "pi-subagent-verify",
					})
				.ConfigureAwait(false);

			var output = Tail(result.Output, MaxVerifyOutputChars);
			if (result.FullOutputPath is { Length: > 0 } fullOutput)
			{
				// 只留尾巴：整份输出只在临时文件里，不删会在 /tmp 里留孤文件。
				try
				{
					File.Delete(fullOutput);
				}
				catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
				{
					// 删不掉就留着：验证结论不受影响。
				}
			}

			return new SubagentVerifyResult
			{
				Command = command,
				ExitCode = result.Cancelled ? null : result.ExitCode,
				TimedOut = result.TimedOut || result.Cancelled,
				Output = output,
			};
		}
		catch (OperationCanceledException)
		{
			return new SubagentVerifyResult { Command = command, TimedOut = true };
		}
		catch (Exception exception)
		{
			return new SubagentVerifyResult { Command = command, Output = exception.Message };
		}
	}

	/// 验证结果进结果正文：父会话看到的是一份自己跑过的证据，不是子代理的声明。
	private static string VerifyEvidence(IReadOnlyList<SubagentVerifyResult> results)
	{
		var builder = new StringBuilder(256);
		builder.Append("\n\nverify (run by the host after your turn):");
		foreach (var result in results)
		{
			builder.Append("\n- `").Append(result.Command).Append("` → ");
			if (result.TimedOut)
			{
				builder.Append("timed out");
			}
			else if (result.ExitCode is { } exitCode)
			{
				builder.Append("exit ").Append(exitCode.ToString(CultureInfo.InvariantCulture));
			}
			else
			{
				builder.Append("did not start");
			}

			if (result.Output is { Length: > 0 } output)
			{
				builder.Append('\n').Append(output);
			}
		}

		if (!results.All(static result => result.Passed))
		{
			builder.Append("\n\nThe host verification failed; treat the work as unverified and say what is still broken.");
		}

		return builder.ToString();
	}

	private void CompleteRun(
		string name,
		string text,
		bool failed,
		string stopReason,
		IReadOnlyList<SubagentVerifyResult>? verify = null)
	{
		RunState? state;
		var stopped = false;
		lock (_gate)
		{
			if (!_runs.TryGetValue(name, out state))
			{
				return;
			}

			// 超时或超预算已经写过终结结果：迟到的 AgentEnd 不能再把状态改回 idle。
			if (state.Guarded)
			{
				return;
			}

			// 正常收尾时不会还有问题挂着；万一有（停掉与完成抢），按「没人回答」解开。
			state.Question?.Timeout.Cancel();
			state.Question = null;
			CancelDeadlineLocked(state);
			CancelVerifyLocked(state);
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
				state.Question?.Timeout.Cancel();
				state.Question = null;
				state.Run = state.Run with { Question = null };
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

				// 验证证据在 schema 校验之后才拼：它不属于模型输出，不能被当成 JSON 的一部分。
				if (verify is { Count: > 0 })
				{
					text = string.Concat(text, VerifyEvidence(verify));
				}

				state.ResultTurn = ++state.TurnSeq;
				state.StructuredOutput = structured;
				var finishedState = failed ? SubagentState.Failed : SubagentState.Idle;
				// 用量在同一把锁里取：TCS 一完成，send 可能立刻起下一轮，
				// 那时 TokenBase 读到的必须是含本轮在内的累计值。
				var tokens = state.Run.Tokens;
				var cost = state.Run.Cost;
				if (state.Session is { } usageSession)
				{
					var usage = UsageOf(usageSession);
					tokens = usage.Tokens > 0 ? usage.Tokens : tokens;
					cost = usage.Cost > 0 ? usage.Cost : cost;
				}

				state.Run = state.Run with
				{
					State = finishedState,
					Turns = state.Run.Turns + 1,
					Result = text,
					StructuredOutput = structured,
					Activity = null,
					Progress = null,
					AwaitingUser = false,
					Question = null,
					StopReason = stopReason,
					Verify = verify ?? [],
					Tokens = tokens,
					Cost = cost,
					FinishedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
				};
				state.WriterLease?.Dispose();
				state.WriterLease = null;
				state.Completion.TrySetResult(new TurnResult(state.ResultTurn, text, structured));
			}
		}

		AppendDescriptor(state, stopped ? "stopped" : failed ? "failed" : "idle");
		Changed?.Invoke();

		// 运行中读过这段正文（read_subagent）就不再补投；失败、截断、结构化结果与验证证据仍要送到。
		var alreadyRead = false;
		lock (_gate)
		{
			alreadyRead = state.LastReadText is { Length: > 0 } read &&
				text.Length > 0 &&
				!failed &&
				!string.Equals(state.Run.StopReason, "length", StringComparison.Ordinal) &&
				state.StructuredOutput is null &&
				state.Run.Verify.Count == 0 &&
				read.EndsWith(text, StringComparison.Ordinal);
		}

		if (!stopped && !alreadyRead)
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
		// 等 agent_end 再投；wait 会在这之前把结果取走。
		if (_owner.IsRunning)
		{
			return;
		}

		ScheduleFlush();
	}

	/// 目标里有没有人正等着提问的回答；调用方持锁。
	private static bool HasPendingQuestionLocked(List<RunState> waiting)
	{
		foreach (var state in waiting)
		{
			if (state.Question is not null)
			{
				return true;
			}
		}

		return false;
	}

	/// 正在等回答的子代理名字，逗号分隔；调用方持锁。
	private static string PendingQuestionNamesLocked(List<RunState> waiting)
	{
		var names = new List<string>(waiting.Count);
		foreach (var state in waiting)
		{
			if (state.Question is not null)
			{
				names.Add(state.Run.Name);
			}
		}

		return string.Join(", ", names);
	}

	/// 挂起问题的结构化版本：名字、正文与可选项。
	private static JsonArray PendingQuestionDetailsLocked(List<RunState> waiting)
	{
		var rows = new JsonArray();
		foreach (var state in waiting)
		{
			if (state.Question is not { } question)
			{
				continue;
			}

			rows.Add((JsonNode)new JsonObject
			{
				["name"] = state.Run.Name,
				["question"] = question.Text,
				["options"] = question.Options.Count > 0
					? new JsonArray([.. question.Options.Select(static option => (JsonNode)option)])
					: null,
			});
		}

		return rows;
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

		List<RunState>? suppressed = null;
		PendingResult[] batch;
		lock (_gate)
		{
			if (_disposed)
			{
				_flushScheduled = false;
				return;
			}

			// 连续回报拉起父会话有上限：到了就不再自动投递。正文还在 Result 与子会话文件里，
			// wait_subagents 仍能取；卡片与 list 要标出来，别让结果看起来已经送到。
			if (_autoContinue >= MaxAutoContinue)
			{
				foreach (var result in _pending)
				{
					if (_runs.TryGetValue(result.Name, out var state) && result.Turn > state.DeliveredTurn)
					{
						state.Run = state.Run with { DeliverySuppressed = true };
						(suppressed ??= []).Add(state);
					}
				}

				_pending.Clear();
				batch = [];
				_flushScheduled = false;
			}
			else if (_pending.Count == 0)
			{
				_flushScheduled = false;
				return;
			}
			else
			{
				batch = [.. _pending];
				_pending.Clear();
				_flushScheduled = false;
			}
		}

		if (suppressed is not null)
		{
			Changed?.Invoke();
			return;
		}

		var builder = new StringBuilder(batch.Length * 256);
		// Previous 是占位前的值：投递抛错时按它回退，同一段正文不会被标成已投递。
		var accepted = new List<(RunState State, long Turn, long Previous)>();
		foreach (var result in batch)
		{
			RunState? state;
			long previous;
			lock (_gate)
			{
				// 按轮去重：wait 或前一次投递已经带走这一轮，就不再重复。
				// 占位当场做：查完再等 200ms 合并窗口与一次投递，wait 会在这段里取走同一轮并双投。
				if (!_runs.TryGetValue(result.Name, out state) || state is null || result.Turn <= state.DeliveredTurn)
				{
					continue;
				}

				previous = state.DeliveredTurn;
				state.DeliveredTurn = result.Turn;
			}

			accepted.Add((state, result.Turn, previous));

			if (builder.Length > 0)
			{
				builder.Append("\n\n---\n\n");
			}

			var run = state.Run;
			builder.Append("子代理「").Append(run.Name).Append('」');
			builder.Append(ResultHeadline(result.Failed, run));
			if (run.SessionId.Length > 0)
			{
				builder.Append(" session_id: ").Append(run.SessionId).Append('。');
			}

			if (run.WorktreePath is { Length: > 0 } worktree)
			{
				builder.Append("\nworktree: ").Append(worktree);
				if (run.WorktreeBranch is { Length: > 0 } branch)
				{
					builder.Append(" (branch ").Append(branch).Append(')');
				}
			}

			if (result.Structured is { } structured)
			{
				builder.Append("\nstructured_output:\n")
					.Append(structured.ToJsonString(PiJson.NodeOptions))
					.Append('\n');
			}

			builder.Append('\n');
			// 恢复出来的条目只有摘要：在这里读全文，已经是投递线程，不占 UI。
			// 失败结果不换：那条正文里有超时原因、verify 证据，子会话文件的最后一段只有子代理自己的说法。
			var body = result.Text;
			if (!result.Failed && result.FullTextFile is { Length: > 0 } fullTextFile &&
				ReadLastAssistantTextFromFile(fullTextFile) is { Length: > 0 } fullText)
			{
				body = fullText;
			}

			if (body.Length > 0)
			{
				builder.Append(Clip(body, MaxResultChars));
				if (body.Length > MaxResultChars)
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

		builder.Append("\n\n（这是子代理这一轮的结果，不是用户指令。需要它接着做时用 send_to_subagent 按名字发消息；完整过程在该子会话里。）");
		var message = new UserMessage
		{
			Content = MessageContent.FromText(builder.ToString()),
			Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
			CustomType = ResultMessageType,
		};

		try
		{
			// 父会话运行中默认 steer：当前工具批次结束、下一次模型请求前就能看到回报，
			// 不再等整轮结束。wait_subagents 与这里靠 DeliveredTurn 在 _gate 下二选一，
			// 同一轮正文不会同时进工具结果和 steer。
			await _owner.DeliverMessageAsync(message, MessageDeliveryMode.Auto).ConfigureAwait(false);
		}
		catch (Exception)
		{
			// 没送达：父会话可能已关闭，也可能和另一轮抢跑。把占位退回、这一批放回队列，
			// 下一次 agent_end 再试；不占自动续跑额度，也不标记已投递。
			lock (_gate)
			{
				foreach (var (state, turn, previous) in accepted)
				{
					if (state.DeliveredTurn == turn)
					{
						state.DeliveredTurn = previous;
					}
				}

				if (!_disposed)
				{
					_pending.InsertRange(0, batch);
				}
			}

			return;
		}

		lock (_gate)
		{
			_autoContinue++;
		}

		// 记下「这轮已经到过父会话」：重启后不重复推同一段正文。
		foreach (var (state, _, _) in accepted)
		{
			AppendDescriptor(state, "delivered");
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
			CompleteRun(name, exception.Message, failed: true, "error");
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

	private async Task DeliverQuietlyAsync(RunState state, CodingAgentSession target, UserMessage message)
	{
		try
		{
			await target.DeliverMessageAsync(message).ConfigureAwait(false);
		}
		catch
		{
			// 目标会话已关闭或正在出错：发送工具已经回了 sent。放开回收标记，别让这个 run 永远不可回收。
			lock (_gate)
			{
				state.Sending = false;
			}
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
			state.WriterLease?.Dispose();
			state.WriterLease = null;
			state.StartGate.TrySetResult();
			_order.Remove(name);
			// 没建出会话就不占本回合的启动配额；否则宿主出一次错就把额度烧掉一格。
			if (_spawns > 0)
			{
				_spawns--;
			}

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

		// 先在同一把锁下拍快照：调用点大多在锁外，边读边写会落下「新的 run + 旧的 turn」这种混搭条目。
		SubagentRun run;
		long deliveredTurn;
		long resultTurn;
		TimeSpan runTimeout;
		IReadOnlyList<string> verifyCommands;
		JsonObject? outputSchema;
		JsonNode? structuredOutput;
		lock (_gate)
		{
			run = state.Run;
			deliveredTurn = state.DeliveredTurn;
			resultTurn = state.ResultTurn;
			runTimeout = state.RunTimeout;
			verifyCommands = state.VerifyCommands;
			outputSchema = state.OutputSchema;
			structuredOutput = state.StructuredOutput;
		}

		try
		{
			if (kind == "delivered")
			{
				// 投递标记只需要名字与轮次：它是文件里最常写的一类条目，带全量字段会白白撑大父会话文件。
				_owner.Session.AppendCustom(EntryType, new JsonObject
				{
					["kind"] = kind,
					["name"] = run.Name,
					["turn"] = deliveredTurn,
				});
				return;
			}

			var data = new JsonObject
			{
				["kind"] = kind,
				["name"] = run.Name,
				["agent"] = run.Agent,
				["session"] = run.SessionId,
				["file"] = run.SessionFile,
				["cwd"] = run.Cwd is { Length: > 0 } cwd ? cwd : null,
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
				["worktreePath"] = run.WorktreePath,
				["worktreeBranch"] = run.WorktreeBranch,
				["worktreeBase"] = run.WorktreeBase,
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
				data["runTimeoutMs"] = (long)runTimeout.TotalMilliseconds;
				data["maxTokens"] = run.MaxTokens;
				data["maxCost"] = run.MaxCost > 0 ? run.MaxCost : null;
				data["maxToolCalls"] = run.MaxToolCalls > 0 ? run.MaxToolCalls : null;
				if (verifyCommands.Count > 0)
				{
					// 验证命令要跨重启保留：冷恢复后的下一轮还得继续跑同一套证据。
					data["verify"] = new JsonArray([.. verifyCommands.Select(static command => (JsonNode)command)]);
				}
			}

			if (kind is "start" or "idle" or "failed" or "stopped")
			{
				if (run.ChangedFiles.Count > 0)
				{
					data["changedFiles"] = new JsonArray([.. run.ChangedFiles.Select(static file => (JsonNode)file)]);
				}

				if (run.MayWrite)
				{
					data["mayWrite"] = true;
				}
			}

			if (kind is "idle" or "failed")
			{
				// 这一轮的结果可能还躺在内存队列里就退出了：恢复时靠这个标记把它补投一次。
				// 旧条目没有这个键，不会在升级后被当成未投递的正文重新推给模型。
				data["deliverable"] = true;
				data["turn"] = resultTurn;
				if (run.StopReason.Length > 0)
				{
					data["stopReason"] = run.StopReason;
				}

				if (run.Verify.Count > 0)
				{
					data["verifyResults"] = VerifyResultsJson(run.Verify);
				}
			}

			if (kind == "start" && outputSchema is not null)
			{
				// schema 只在 start 条目写一次；每次状态变化都写会把会话文件撑大。
				data["outputSchema"] = outputSchema.DeepClone();
			}

			if (structuredOutput is not null)
			{
				data["structuredOutput"] = structuredOutput.DeepClone();
			}

			_owner.Session.AppendCustom(EntryType, data);
		}
		catch (Exception)
		{
			// 关系描述符只服务于恢复与审计，写不进去不影响本轮委派。
		}
	}

	private static JsonArray VerifyResultsJson(IReadOnlyList<SubagentVerifyResult> results)
	{
		var array = new JsonArray();
		foreach (var result in results)
		{
			array.Add((JsonNode)new JsonObject
			{
				["command"] = result.Command,
				["exitCode"] = result.ExitCode,
				["timedOut"] = result.TimedOut,
				["output"] = result.Output is { Length: > 0 } output ? output : null,
			});
		}

		return array;
	}

	private static List<SubagentVerifyResult> ReadVerifyResults(JsonNode? node)
	{
		if (node is not JsonArray array || array.Count == 0)
		{
			return [];
		}

		var results = new List<SubagentVerifyResult>(array.Count);
		foreach (var item in array)
		{
			if (item is not JsonObject row || ReadString(row["command"]) is not { Length: > 0 } command)
			{
				continue;
			}

			results.Add(new SubagentVerifyResult
			{
				Command = command,
				ExitCode = ReadLong(row["exitCode"]) is { } exitCode ? (int)exitCode : null,
				TimedOut = ReadBool(row["timedOut"]),
				Output = ReadString(row["output"]) ?? string.Empty,
			});
		}

		return results;
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
			// 父历史只作为上下文带过去，不是子代理花掉的用量。原样复制会把父会话的
			// usage / 时长重复计进子会话统计，也会让全局用量把 fork 之前的花费再算一遍。
			seed[cursor++] = messages[index] is AssistantMessage assistant
				? assistant with
				{
					Usage = new Usage(),
					DurationMs = null,
					FirstTokenMs = null,
					StreamingDurationMs = null,
				}
				: messages[index];
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

	/// 会话文件是用户可编辑的：类型不对的条目按缺失处理，`GetValue` 抛异常会让整个会话打不开。
	private static string? ReadString(JsonNode? node) =>
		node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

	private static int? ReadInt(JsonNode? node) =>
		node is JsonValue value && value.TryGetValue<int>(out var number) ? number : null;

	private static double? ReadDouble(JsonNode? node) =>
		node is JsonValue value && value.TryGetValue<double>(out var number) ? number : null;

	private static bool ReadBool(JsonNode? node) =>
		node is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;

	private static IReadOnlyList<string> ReadStrings(JsonNode? node)
	{
		if (node is not JsonArray array || array.Count == 0)
		{
			return [];
		}

		var values = new List<string>(array.Count);
		foreach (var item in array)
		{
			if (item is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0)
			{
				values.Add(text);
			}
		}

		return values;
	}

	/// 子会话最近一条带正文的助手消息：超时、超预算这类硬停时用它保住已经写出来的东西。
	private static string? LastAssistantText(CodingAgentSession? session)
	{
		if (session is not { IsDisposed: false })
		{
			return null;
		}

		try
		{
			var messages = session.ProjectedMessages;
			for (var index = messages.Count - 1; index >= 0; index--)
			{
				if (messages[index] is AssistantMessage assistant)
				{
					var text = MessageText.Of(assistant);
					if (text.Length > 0)
					{
						return text;
					}
				}
			}
		}
		catch (Exception)
		{
			// 会话正在切换或文件不可读：没有正文就不附。
		}

		return null;
	}

	/// 输出或正文的尾部：命令失败的信号几乎总在最后几行。
	private static string Tail(string text, int max)
	{
		if (text.Length <= max)
		{
			return text;
		}

		return string.Concat("…", text.AsSpan(text.Length - max));
	}

	private static long ElapsedMs(SubagentRun run) =>
		run.FinishedAt > run.StartedAt && run.StartedAt > 0 ? run.FinishedAt - run.StartedAt : 0;

	private static string ModelLabel(Model model) =>
		model.Provider.Length > 0 ? string.Concat(model.Provider, "/", model.Id) : model.Id;

	private static JsonArray VerifyDetails(IReadOnlyList<SubagentVerifyResult> results)
	{
		var array = new JsonArray();
		foreach (var result in results)
		{
			array.Add((JsonNode)new JsonObject
			{
				["command"] = result.Command,
				["exit_code"] = result.ExitCode,
				["timed_out"] = result.TimedOut,
				["passed"] = result.Passed,
				["output"] = result.Output is { Length: > 0 } output ? Clip(output, MaxCardChars) : null,
			});
		}

		return array;
	}

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

	private static string ResultHeadline(bool failed, SubagentRun run)
	{
		if (failed)
		{
			foreach (var result in run.Verify)
			{
				if (!result.Passed)
				{
					return "这一轮失败：宿主验证没通过。";
				}
			}

			return "这一轮失败。";
		}

		return string.Equals(run.StopReason, "length", StringComparison.Ordinal)
			? "这一轮完成了，但回复被 max tokens 截断。"
			: "完成了一轮。";
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

		span = span.TrimEnd();
		// 先截断再落成字符串：一条 5 KB 的单行回复不该为 240 字进度分配两遍。
		return span.Length <= max ? span.ToString() : string.Concat(span[..max], "…");
	}

	/// 助手消息的首行进度：跳过思考块与空块，只取第一段正文的首行。
	/// 与 <see cref="MessageText.Of"/> 加 <see cref="FirstLine"/> 结果一致，但不会先把整条回复拼出来。
	private static string FirstLineOf(AssistantMessage message, int max)
	{
		foreach (var block in message.Content)
		{
			if (block is TextContent { Text.Length: > 0 } text)
			{
				return FirstLine(text.Text, max);
			}
		}

		return string.Empty;
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
