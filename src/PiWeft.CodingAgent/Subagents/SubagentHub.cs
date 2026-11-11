using System.Text;
using System.Text.Json.Nodes;
using PiWeft.Agent;
using PiWeft.Ai;
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

	/// <summary>启动时的一句话描述。</summary>
	public string Label { get; init; } = string.Empty;

	/// <summary>委派深度，顶层子代理为 1。</summary>
	public int Depth { get; init; }

	/// <summary>当前状态。</summary>
	public SubagentState State { get; init; } = SubagentState.Starting;

	/// <summary>已完成的回合数。</summary>
	public int Turns { get; init; }

	/// <summary>运行中正在调用的工具与摘要（<c>read src/a.cs</c>）；结束或未开始时为 null。</summary>
	public string? Activity { get; init; }

	/// <summary>最近一轮的最终回复。</summary>
	public string? Result { get; init; }

	/// <summary>启动时间，Unix 毫秒。</summary>
	public long StartedAt { get; init; }

	/// <summary>最近一轮结束时间，Unix 毫秒。</summary>
	public long FinishedAt { get; init; }

	/// <summary>从会话条目恢复出来的历史运行，没有活会话。</summary>
	public bool Restored { get; init; }

	/// <summary>是否已结束一轮且可以继续发消息。</summary>
	public bool IsTerminal => State is SubagentState.Idle or SubagentState.Failed or SubagentState.Stopped;
}

/// <summary>启动子代理的请求。</summary>
public sealed record SubagentStartRequest(string Agent, string Task, string? Name = null, string? Context = null);

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
	IReadOnlyList<Message>? Seed = null);

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
	string SessionId);

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

	/// <summary>Details 里给界面卡片留的正文上限：工具正文已经带全文，这里只够画卡。</summary>
	public const int MaxCardChars = 4_000;

	/// <summary>wait 的默认超时。</summary>
	public static readonly TimeSpan DefaultWaitTimeout = TimeSpan.FromMinutes(10);

	private sealed class RunState
	{
		public required SubagentRun Run { get; set; }

		public CodingAgentSession? Session { get; set; }

		public IDisposable? Subscription { get; set; }

		public TaskCompletionSource<bool> Completion { get; set; } =
			new(TaskCreationOptions.RunContinuationsAsynchronously);

		/// 结果已经进过父会话（作为消息投递，或被 wait 取走）。同一条正文不进两次上下文。
		public bool Delivered { get; set; }

		public bool Stopping { get; set; }
	}

	private sealed record PendingResult(string Name, bool Failed, string Text);

	private readonly CodingAgentSession _owner;
	private readonly ISubagentHost _host;
	private readonly SubagentCatalog _catalog;
	private readonly int _maxActive;
	private readonly int _maxSpawns;
	private readonly int _depth;
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

	public SubagentHub(
		CodingAgentSession owner,
		ISubagentHost host,
		SubagentCatalog catalog,
		int depth,
		int maxActive = 4,
		int maxSpawns = 32,
		TimeSpan? coalesce = null)
	{
		_owner = owner;
		_host = host;
		_catalog = catalog;
		_depth = depth;
		_maxActive = Math.Max(1, maxActive);
		_maxSpawns = Math.Max(1, maxSpawns);
		_coalesce = coalesce ?? TimeSpan.FromMilliseconds(200);
		Restore();
	}

	/// <summary>子代理列表或状态变化；界面据此刷新。</summary>
	public event Action? Changed;

	/// <summary>本会话开出的子代理快照，按启动顺序。</summary>
	public IReadOnlyList<SubagentRun> Runs
	{
		get
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
	}

	/// <summary>角色目录。</summary>
	public SubagentCatalog Catalog => _catalog;

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
							Depth = data["depth"]?.GetValue<int>() ?? 1,
							State = SubagentState.Stopped,
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
					State = SubagentState.Stopped,
				};
				continue;
			}

			if (!restored.TryGetValue(name, out var finished))
			{
				continue;
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
			};
		}

		lock (_gate)
		{
			foreach (var name in order)
			{
				var state = restored[name];
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
		}
	}

	/// <summary>子会话系统提示词里的委派边界。宿主把它追加在角色提示词后面。</summary>
	public static string BuildBoundary(SubagentStartContext context)
	{
		ArgumentNullException.ThrowIfNull(context);
		return string.Concat(
			"你是父会话「", context.ParentTitle, "」开出的子代理「", context.Name, "」，角色「", context.Agent.Name, "」。\n",
			"你看不到父会话的历史，任务全部写在第一条用户消息里。\n",
			"把结论写进最终回复：做了什么、结果在哪、还差什么。需要用户拍板时，写出阻塞点和选项后停下；",
			"你有自己的标签页，用户可能看到也可能不看。不要只停在提问上等回答。\n",
			"不要开新的子代理，也不要把工作推回父会话。");
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
				Run = new SubagentRun
				{
					Name = name,
					Agent = definition.Name,
					Label = FirstLine(task, 80),
					Depth = _depth + 1,
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
				new SubagentStartContext(_owner, ParentTitle(), definition, name, task, _depth + 1, seed),
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
	public async Task<SubagentOutcome> WaitAsync(
		IReadOnlyList<string>? targets,
		TimeSpan timeout,
		CancellationToken cancellationToken = default,
		Action<string>? progress = null)
	{
		List<RunState> waiting;
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
		}

		var pending = new List<Task>(waiting.Count);
		foreach (var state in waiting)
		{
			if (!state.Run.IsTerminal)
			{
				pending.Add(state.Completion.Task);
			}
		}

		var timedOut = false;
		if (pending.Count > 0)
		{
			progress?.Invoke(ProgressText(waiting, "waiting"));

			// .NET 9 的 Task.WhenEach：完成一个就推进一次。再加一条定时汇报，
			// 让「一个子代理跑十分钟」也不至于整段没有进度。
			using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			deadline.CancelAfter(timeout);
			using var heartbeat = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
			var reporter = ReportProgressAsync(waiting, progress, heartbeat.Token);
			try
			{
				await foreach (var completed in Task.WhenEach(pending).WithCancellation(deadline.Token).ConfigureAwait(false))
				{
					_ = completed;
					progress?.Invoke(ProgressText(waiting, "waiting"));
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


		var details = new JsonArray();
		var text = new StringBuilder(256);
		text.Append(timedOut ? "wait_subagents timed out; some subagents are still running." : "wait_subagents")
			.Append('\n');
		var budget = MaxWaitChars;
		var results = new StringBuilder(256);
		lock (_gate)
		{
			foreach (var state in waiting)
			{
				var run = state.Run;
				var row = new JsonObject
				{
					["name"] = run.Name,
					["agent"] = run.Agent,
					["state"] = StateName(run.State),
					["turns"] = run.Turns,
					["session_id"] = run.SessionId,
					["activity"] = run.Activity,
					// returned：这次工具调用把正文带回去了，所以它不会再走消息投递。
					// 桌面据此补一张只读回报卡；delivered 是「调用前就已经投递过」，两者不同。
					["delivered"] = state.Delivered,
					["returned"] = false,
					["result"] = run.Result is { Length: > 0 } waitResult ? Clip(waitResult, MaxCardChars) : null,
				};
				details.Add((JsonNode)row);

				text.Append("- ").Append(run.Name).Append(" (").Append(run.Agent).Append(") · ")
					.Append(StateName(run.State)).Append(" · ").Append(run.Turns).Append(" turns");
				if (run.Activity is { Length: > 0 } activity)
				{
					text.Append(" · ").Append(activity);
				}


				if (run.Result is not { Length: > 0 } result)
				{
					text.Append('\n');
					continue;
				}

				if (state.Delivered)
				{
					// 已经作为消息进过上下文：只报状态，避免同一段正文再进一次。
					text.Append(" · result already delivered as a message\n");
					continue;
				}

				text.Append('\n');
				var body = budget <= 0
					? "(result omitted: wait budget reached)"
					: Clip(result, budget);
				budget -= body.Length;
				results.Append("\n--- ").Append(run.Name).Append(" (").Append(run.Agent).Append(") ---\n")
					.Append(body).Append('\n');
				if (run.IsTerminal)
				{
					state.Delivered = true;
					// 标记这段正文已随本次工具结果返回，桌面据此补只读卡。
					row["returned"] = true;
				}
			}
		}

		if (timedOut)
		{
			text.Append("\nEnd your turn instead of waiting again; the result arrives as a message when it finishes.");
		}

		text.Append(results);
		return new SubagentOutcome(true, text.ToString().TrimEnd(), new JsonObject
		{
			["timed_out"] = timedOut,
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
					["state"] = StateName(run.State),
					["turns"] = run.Turns,
					["label"] = run.Label,
					["activity"] = run.Activity,
					["restored"] = run.Restored,
					["resumable"] = run.SessionFile is { Length: > 0 },
					["result"] = run.Result is { Length: > 0 } result ? Clip(result, 400) : null,
				});

				text.Append("- ").Append(run.Name).Append(" (").Append(run.Agent).Append(") · ")
					.Append(run.Restored ? "finished" : StateName(run.State))
					.Append(" · ").Append(run.Turns).Append(" turns");
				if (run.Activity is { Length: > 0 } activity)
				{
					text.Append(" · ").Append(activity);
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
		var names = new StringBuilder();
		foreach (var definition in _catalog.All)
		{
			available.Add((JsonNode)new JsonObject
			{
				["name"] = definition.Name,
				["description"] = definition.Description,
			});

			if (names.Length > 0)
			{
				names.Append(", ");
			}

			names.Append(definition.Name);
		}

		text.Append("agents: ").Append(names.Length > 0 ? names.ToString() : "(none)");
		details = new JsonObject
		{
			["subagents"] = rows,
			["available"] = available,
		};
		return text.ToString();
	}

	/// <summary>给子代理发一条消息。运行中排 follow-up，空闲时直接起一轮；不等回复。</summary>
	/// <remarks>会话没打开（重启后的历史项）时先冷恢复：按会话文件重建子会话再投递。</remarks>
	public async Task<SubagentOutcome> SendAsync(
		string target,
		string message,
		CancellationToken cancellationToken = default)
	{
		var text = (message ?? string.Empty).Trim();
		if (text.Length == 0)
		{
			return new SubagentOutcome(false, "message is required");
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

		// 停过、关过标签的子代理照样能继续：会话文件还在就冷恢复。
		// stop_subagent 停的是当前回合，不是这段关系。

		if (state.Session is not { } session || session.IsDisposed)
		{
			var reopened = await ReopenAsync(state, cancellationToken).ConfigureAwait(false);
			if (reopened is null)
			{
				return new SubagentOutcome(false,
					"the subagent session is not open and cannot be reopened; its transcript is in the session file");
			}

			session = reopened;
		}

		lock (_gate)
		{
			// 新一轮开始时换一个 waiter、清掉「已投递」与「正在停」：否则 wait 会立刻拿到上一轮的结果，
			// 新结果也会被当成已投递而不送回父会话；Stopping 留着会让这一轮结束被当成「停掉」吞掉。
			state.Completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
			state.Delivered = false;
			state.Stopping = false;
			state.Run = state.Run with { Result = null };
		}

		var outgoing = new UserMessage
		{
			Content = MessageContent.FromText($"来自父会话「{ParentTitle()}」：\n{text}"),
			Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
			CustomType = MessageType,
		};

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
					run.SessionId),
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
			current.Delivered = false;
			current.Run = current.Run with
			{
				Restored = false,
				State = SubagentState.Idle,
				Activity = null,
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
			state.Run = state.Run with
			{
				State = SubagentState.Stopped,
				FinishedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
			};
			state.Completion.TrySetResult(true);
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
			// 子会话标签关掉后不能继续发消息，状态一律落到 stopped。
			state.Run = state.Run with
			{
				State = SubagentState.Stopped,
				FinishedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
			};
			state.Completion.TrySetResult(true);
		}

		Changed?.Invoke();
	}

	public void Dispose()
	{
		IDisposable[] subscriptions;
		TaskCompletionSource<bool>[] completions;
		lock (_gate)
		{
			if (_disposed)
			{
				return;
			}

			_disposed = true;
			subscriptions = [.. _runs.Values.Select(static state => state.Subscription).OfType<IDisposable>()];
			completions = [.. _runs.Values.Select(static state => state.Completion)];
			_pending.Clear();
		}

		foreach (var completion in completions)
		{
			completion.TrySetResult(true);
		}

		foreach (var subscription in subscriptions)
		{
			subscription.Dispose();
		}

		// 子会话是独立会话，不随父会话关闭；只摘掉这一侧的订阅。
	}

	private ValueTask ObserveAsync(string name, AgentEvent @event)
	{
		switch (@event)
		{
			case AgentStartEvent:
				SetRunning(name);
				break;

			case ToolExecutionStartEvent tool:
				SetActivity(name, DescribeTool(tool.ToolName, tool.Arguments));
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
				state.Completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
				state.Delivered = false;
			}

			state.Run = state.Run with { State = SubagentState.Running };
		}

		Changed?.Invoke();
	}

	/// 子代理此刻在干什么：工具名加一个能认出来的参数，卡片与 list_subagents 都用它。
	private void SetActivity(string name, string activity)
	{
		lock (_gate)
		{
			if (!_runs.TryGetValue(name, out var state) || state.Stopping)
			{
				return;
			}

			if (string.Equals(state.Run.Activity, activity, StringComparison.Ordinal))
			{
				return;
			}

			state.Run = state.Run with { Activity = activity };
		}

		Changed?.Invoke();
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

			if (state.Stopping)
			{
				state.Run = state.Run with
				{
					State = SubagentState.Stopped,
					FinishedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
				};
				state.Completion.TrySetResult(true);
				stopped = true;
			}
			else
			{
				state.Run = state.Run with
			{
					State = failed ? SubagentState.Failed : SubagentState.Idle,
					Turns = state.Run.Turns + 1,
					Result = text,
					Activity = null,
					FinishedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
				};
				state.Completion.TrySetResult(true);
			}
		}

		AppendDescriptor(state, stopped ? "stopped" : failed ? "failed" : "idle");
		Changed?.Invoke();
		if (!stopped)
		{
			QueueDelivery(new PendingResult(name, failed, text));
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
			if (_disposed || _pending.Count == 0)
			{
				_flushScheduled = false;
				return;
			}

			batch = [.. _pending];
			_pending.Clear();
			_flushScheduled = false;
		}

		var builder = new StringBuilder();
		foreach (var result in batch)
		{
			RunState? state;
			lock (_gate)
			{
				if (!_runs.TryGetValue(result.Name, out state) || state is null || state.Delivered)
				{
					continue;
				}

				// wait 已经取走的条目不再重复进上下文。
				state.Delivered = true;
			}

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

			builder.Append('\n');
			if (result.Text.Length > 0)
			{
				builder.Append(Clip(result.Text, MaxResultChars));
				if (result.Text.Length > MaxResultChars)
				{
					builder.Append("\n（结果已截断，完整正文在子会话里。）");
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
			// 父会话已关闭或正在出错：结果还在子会话文件里，不能在这里变成未观察的异常。
		}
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
			if (state.Run.State is SubagentState.Starting or SubagentState.Running)
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
				if (!_runs[name].Run.IsTerminal)
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
			state.Completion.TrySetResult(true);
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
			_owner.Session.AppendCustom(EntryType, new JsonObject
			{
				["kind"] = kind,
				["name"] = run.Name,
				["agent"] = run.Agent,
				["session"] = run.SessionId,
				["file"] = run.SessionFile,
				["depth"] = run.Depth,
				["state"] = StateName(run.State),
				["turns"] = run.Turns,
				["label"] = run.Label,
				// 只存摘要：恢复后的列表够用，完整正文在子会话文件里。
				["result"] = run.Result is { Length: > 0 } result ? Clip(result, 400) : null,
			});
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
		for (var index = messages.Count - 1; index >= 0; index--)
		{
			if (messages[index] is not AssistantMessage assistant || HasToolCalls(assistant))
			{
				continue;
			}

			var seed = new Message[index + 1];
			for (var cursor = 0; cursor <= index; cursor++)
			{
				seed[cursor] = messages[cursor];
			}

			return seed;
		}

		return null;
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
