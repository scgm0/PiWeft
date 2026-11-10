using System.Text;
using System.Text.Json.Nodes;
using PiWeft.Agent;
using PiWeft.Ai;
using PiWeft.CodingAgent.Compatibility;

namespace PiWeft.CodingAgent;

/// 一条关联会话关系：父会话开出、可继续收发消息的子会话。
public sealed record LinkedSessionLink(
	string ChildSessionId,
	string ParentSessionId,
	string Title,
	string State,
	int TurnCount,
	string? LastReport,
	long LastReportAt)
{
	public const string Starting = "starting";
	public const string Running = "running";
	public const string Idle = "idle";
	public const string Failed = "failed";
	public const string Stopped = "stopped";
}

/// 开子会话所需的信息：宿主按它建会话、挂标签，并把简报放进子会话的系统提示。
public sealed record LinkedSessionStartContext(
	CodingAgentSession Parent,
	string ParentTitle,
	string Title,
	string Prompt,
	string SystemBrief);

/// 宿主提供的关联会话能力。不支持时工具不注册，会话也不会报「能开但开不出来」。
public interface ILinkedSessionHost
{
	/// 建一个可运行的子会话；宿主不支持或创建失败时返回 null。
	Task<CodingAgentSession?> CreateChildAsync(LinkedSessionStartContext context, CancellationToken cancellationToken);
}

/// 工具调用的结果：给模型的一句话，外加成功/失败。
public sealed record LinkedSessionOperation(bool Ok, string Text)
{
	public static LinkedSessionOperation Success(string text) => new(true, text);

	public static LinkedSessionOperation Failure(string text) => new(false, text);
}

/// 联动会话的父会话侧编排：开子会话、转发消息、把子会话每轮结果回报给父会话。
/// 回报会异步拉起父会话的一轮（自动续跑）；连续 6 次后暂停，用户在父会话发消息即清零。
/// 关系只活在进程内：重启后子会话仍在，但不再联动（PiDeck 会落盘关系表）。
public sealed class LinkedSessionHub : IDisposable
{
	/// 父会话被回报自动拉起的一轮计数上限；用户发消息清零。
	public const int MaxAutoContinue = 6;

	/// 回报正文截取长度（与 PiDeck 的 400 字一致）。
	public const int ReportMaxChars = 400;

	public const int TitleMaxChars = 80;

	private readonly CodingAgentSession _owner;
	private readonly ILinkedSessionHost _host;
	private CodingAgentSession? _parentSession;
	private readonly Lock _gate = new();
	private readonly List<LinkedSessionLink> _children = [];
	private readonly Dictionary<string, CodingAgentSession> _sessions = new(StringComparer.Ordinal);
	private readonly Dictionary<string, IDisposable> _subscriptions = new(StringComparer.Ordinal);
	// 回报投递串行化：两条子会话同时结束，不能并起两轮父会话请求。
	private Task _deliveries = Task.CompletedTask;
	private int _autoContinue;
	private bool _disposed;

	public LinkedSessionHub(
		CodingAgentSession owner,
		ILinkedSessionHost host,
		CodingAgentSession? parentSession = null,
		string? parentTitle = null)
	{
		_owner = owner;
		_host = host;
		_parentSession = parentSession;
		Parent = parentSession is null
			? null
			: new LinkedSessionLink(
				owner.Session.SessionId,
				parentSession.Session.SessionId,
				parentTitle ?? "父会话",
				LinkedSessionLink.Idle,
				0,
				null,
				0);
	}

	/// 本会话被哪个父会话开出；不是关联子会话时为 null。
	public LinkedSessionLink? Parent { get; private set; }

	/// 已开出的子会话快照（按开出顺序）。
	public IReadOnlyList<LinkedSessionLink> Children
	{
		get
		{
			lock (_gate)
			{
				return [.. _children];
			}
		}
	}

	/// 子会话数量/状态变化；界面可据此刷新列表。
	public event Action? Changed;

	/// 测试用：等待当前排队的回报投递全部完成。
	internal Task DeliveryBarrier
	{
		get
		{
			lock (_gate)
			{
				return _deliveries;
			}
		}
	}

	/// 用户在本会话发了消息：自动续跑额度清零（PiDeck 同义）。
	public void NoteUserPrompt()
	{
		lock (_gate)
		{
			_autoContinue = 0;
		}
	}

	/// 开出子会话并投递第一轮。立即返回，子会话在后台跑；结果由回合回报带回父会话。
	public async Task<LinkedSessionOperation> StartAsync(string? title, string prompt, CancellationToken cancellationToken = default)
	{
		var brief = (prompt ?? string.Empty).Trim();
		if (brief.Length == 0)
		{
			return LinkedSessionOperation.Failure("prompt is required");
		}

		var cleanTitle = NormalizeTitle(title, brief);
		lock (_gate)
		{
			if (_disposed)
			{
				return LinkedSessionOperation.Failure("the session is closing");
			}
		}

		var ownerTitle = OwnerTitle();
		CodingAgentSession? child;
		try
		{
			child = await _host.CreateChildAsync(
				new LinkedSessionStartContext(_owner, ownerTitle, cleanTitle, brief, SystemBrief(ownerTitle)),
				cancellationToken).ConfigureAwait(false);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			return LinkedSessionOperation.Failure($"failed to create the linked session: {ex.Message}");
		}

		if (child is null)
		{
			return LinkedSessionOperation.Failure("this host cannot open a linked session");
		}

		var childId = child.Session.SessionId;
		// 订阅放在锁外：Subscribe 可能和子会话的事件派发抢同一把内部锁，
		// 在 _gate 里订阅会与 ObserveChildAsync 的 SetState 形成锁序倒置。
		IDisposable subscription;
		try
		{
			subscription = child.Subscribe(@event => ObserveChildAsync(childId, @event));
		}
		catch (Exception ex)
		{
			child.Dispose();
			return LinkedSessionOperation.Failure($"failed to observe the linked session: {ex.Message}");
		}

		var linkAdded = false;
		lock (_gate)
		{
			if (!_disposed)
			{
				_children.Add(new LinkedSessionLink(
					childId,
					_owner.Session.SessionId,
					cleanTitle,
					LinkedSessionLink.Starting,
					0,
					null,
					0));
				_sessions[childId] = child;
				_subscriptions[childId] = subscription;
				linkAdded = true;
			}
		}

		if (!linkAdded)
		{
			// 父会话在子会话创建途中关掉了：别留一个没人回报的后台会话。
			subscription.Dispose();
			child.Dispose();
			return LinkedSessionOperation.Failure("the session is closing");
		}

		Changed?.Invoke();
		// 首轮在后台跑：工具调用只等「建好并已投递」，不等子会话跑完。
		_ = RunFirstTurnAsync(childId, child, brief);
		return LinkedSessionOperation.Success(
			$"started linked session \"{cleanTitle}\" (session_id: {childId}); its report will arrive here when the turn ends");
	}

	/// 给直接关联的会话发一条消息：本会话开出的子会话，或开出本会话的父会话。
	/// 对方忙时排在当前这一轮之后；本调用不等对方回复。
	public Task<LinkedSessionOperation> SendAsync(string sessionId, string message, CancellationToken cancellationToken = default)
	{
		var text = (message ?? string.Empty).Trim();
		if (text.Length == 0)
		{
			return Task.FromResult(LinkedSessionOperation.Failure("message is required"));
		}

		CodingAgentSession? target = null;
		var targetIsChild = false;
		lock (_gate)
		{
			if (_sessions.TryGetValue(sessionId, out var child))
			{
				target = child;
				targetIsChild = true;
			}
		}

		if (target is null && _parentSession is { } parent && string.Equals(sessionId, parent.Session.SessionId, StringComparison.Ordinal))
		{
			target = parent;
		}

		if (target is null)
		{
			return Task.FromResult(LinkedSessionOperation.Failure("session-not-linked"));
		}

		if (target.IsDisposed)
		{
			return Task.FromResult(LinkedSessionOperation.Failure("session-closed"));
		}

		var label = targetIsChild ? $"来自父会话「{OwnerTitle()}」：" : $"来自子会话「{OwnerTitle()}」：";
		var envelope = $"{label}\n{text}";
		var outgoing = new UserMessage
		{
			Content = MessageContent.FromText(envelope),
			Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
			CustomType = PiWeftProtocol.LinkedMessageType,
		};

		try
		{
			// 只投递不等对方跑完：send_to_session 的语义是「排进去」。
			_ = DeliverQuietlyAsync(target, outgoing);

			return Task.FromResult(LinkedSessionOperation.Success($"sent to {sessionId}"));
		}
		catch (Exception ex)
		{
			return Task.FromResult(LinkedSessionOperation.Failure($"failed to send: {ex.Message}"));
		}
	}

	/// 列出本会话开出的子会话与父会话，给 <c>list_linked_sessions</c> 用。
	public JsonObject List()
	{
		var data = new JsonObject();
		if (Parent is { } parent)
		{
			data["parent"] = new JsonObject
			{
				["session_id"] = parent.ParentSessionId,
				["title"] = parent.Title,
			};
		}

		var children = new JsonArray();
		foreach (var link in Children)
		{
			children.Add((JsonNode)new JsonObject
			{
				["session_id"] = link.ChildSessionId,
				["title"] = link.Title,
				["state"] = link.State,
				["turns"] = link.TurnCount,
				["last_report"] = link.LastReport ?? string.Empty,
			});
		}

		data["children"] = children;
		return data;
	}

	/// 子会话被关闭（标签页关掉/进程退出）：关系保留为 stopped，不再订阅它的事件。
	public void NoteChildClosed(string childSessionId)
	{
		IDisposable? subscription = null;
		lock (_gate)
		{
			_subscriptions.Remove(childSessionId, out subscription);
			_sessions.Remove(childSessionId);
			var index = _children.FindIndex(link => string.Equals(link.ChildSessionId, childSessionId, StringComparison.Ordinal));
			if (index >= 0)
			{
				_children[index] = _children[index] with { State = LinkedSessionLink.Stopped };
			}
		}

		subscription?.Dispose();
		Changed?.Invoke();
	}

	/// 父会话关闭时叫子会话摘掉父引用，避免子会话把已关闭的父会话连同历史一起留着。
	internal void DetachParent()
	{
		_parentSession = null;
		Parent = null;
	}

	public void Dispose()
	{
		IDisposable[] subscriptions;
		CodingAgentSession[] children;
		lock (_gate)
		{
			if (_disposed)
			{
				return;
			}

			_disposed = true;
			subscriptions = [.. _subscriptions.Values];
			children = [.. _sessions.Values];
			_subscriptions.Clear();
			_sessions.Clear();
		}

		foreach (var subscription in subscriptions)
		{
			subscription.Dispose();
		}

		foreach (var child in children)
		{
			child.Linked?.DetachParent();
		}

		// 子会话是独立会话，不随父会话关闭；只把关系从父会话那边摘掉。
		if (_parentSession?.Linked is { } parentHub)
		{
			parentHub.NoteChildClosed(_owner.Session.SessionId);
		}
	}

	/// 子会话的回合事件：开始置 running，整轮结束取最后一条助手文本回报父会话。
	private ValueTask ObserveChildAsync(string childSessionId, AgentEvent @event)
	{
		switch (@event)
		{
			case AgentStartEvent:
				SetState(childSessionId, LinkedSessionLink.Running);
				break;

			case AgentEndEvent { WillRetry: false } end:
			{
				var last = MessageText.LastAssistant(end.Messages);
				var failed = last?.StopReason is StopReason.Error or StopReason.Aborted;
				var report = last is null ? string.Empty : MessageText.Of(last);
				SetState(childSessionId, failed ? LinkedSessionLink.Failed : LinkedSessionLink.Idle);
				QueueDelivery(() => DeliverReportAsync(childSessionId, report, failed));
				break;
			}
		}

		return ValueTask.CompletedTask;
	}

	private void SetState(string childSessionId, string state)
	{
		lock (_gate)
		{
			var index = _children.FindIndex(link => string.Equals(link.ChildSessionId, childSessionId, StringComparison.Ordinal));
			if (index < 0 || _children[index].State == LinkedSessionLink.Stopped)
			{
				return;
			}

			_children[index] = _children[index] with { State = state };
		}

		Changed?.Invoke();
	}

	private void QueueDelivery(Func<Task> delivery)
	{
		lock (_gate)
		{
			_deliveries = ChainAsync(_deliveries, delivery);
		}
	}

	private static async Task ChainAsync(Task previous, Func<Task> next)
	{
		try
		{
			await previous.ConfigureAwait(false);
		}
		catch
		{
			// 上一笔投递的异常已经在它自己的 try 里处理；这里只保证队列继续。
		}

		try
		{
			await next().ConfigureAwait(false);
		}
		catch
		{
			// 投递失败不能变成未观察的任务异常。
		}
	}

	private async Task DeliverReportAsync(string childSessionId, string report, bool failed)
	{
		LinkedSessionLink link;
		lock (_gate)
		{
			if (_disposed)
			{
				return;
			}

			var index = _children.FindIndex(candidate => string.Equals(candidate.ChildSessionId, childSessionId, StringComparison.Ordinal));
			if (index < 0 || _children[index].State == LinkedSessionLink.Stopped)
			{
				return;
			}

			link = _children[index] with
			{
				State = failed ? LinkedSessionLink.Failed : LinkedSessionLink.Idle,
				TurnCount = _children[index].TurnCount + 1,
				LastReport = ClipReport(report),
				LastReportAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
			};
			_children[index] = link;
		}

		Changed?.Invoke();
		if (!TryConsumeAutoContinue())
		{
			return;
		}

		var message = new UserMessage
		{
			Content = MessageContent.FromText(BuildReportText(link, failed)),
			Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
			CustomType = PiWeftProtocol.LinkedReportMessageType,
		};

		try
		{
			await _owner.DeliverMessageAsync(message).ConfigureAwait(false);
		}
		catch
		{
			// 没送达的回报不占额度（父会话可能正在出错或已关闭）。
			RefundAutoContinue();
		}
	}

	private bool TryConsumeAutoContinue()
	{
		lock (_gate)
		{
			if (_autoContinue >= MaxAutoContinue)
			{
				return false;
			}

			_autoContinue++;
			return true;
		}
	}

	private void RefundAutoContinue()
	{
		lock (_gate)
		{
			if (_autoContinue > 0)
			{
				_autoContinue--;
			}
		}
	}

	private async Task RunFirstTurnAsync(string childSessionId, CodingAgentSession child, string prompt)
	{
		var message = new UserMessage
		{
			Content = MessageContent.FromText(prompt),
			Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
			CustomType = PiWeftProtocol.LinkedMessageType,
		};

		try
		{
			await child.PromptAsync(message, CancellationToken.None).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			// 子会话被停掉：状态留给它自己的会话，不回报。
		}
		catch (Exception ex)
		{
			// 首轮在 agent_start 之前就失败（例如鉴权/参数）：没有 agent_end，
			// 补一份失败回报并落状态，否则关系会一直停在 starting。
			QueueDelivery(() => DeliverReportAsync(childSessionId, ex.Message, failed: true));
		}
	}

	private static async Task DeliverQuietlyAsync(CodingAgentSession target, UserMessage message)
	{
		try
		{
			await target.DeliverMessageAsync(message).ConfigureAwait(false);
		}
		catch
		{
			// 目标会话已关闭或正在出错：发送工具已经回了「sent」，失败只能静默处理。
		}
	}

	private string OwnerTitle()
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

	private string SystemBrief(string parentTitle) =>
		$"你是由会话「{parentTitle}」(session_id: {_owner.Session.SessionId}) 开出的关联子会话。用户可以随时打开你直接对话。" +
		"\n每结束一轮，你的最后一条回复会作为回报送给父会话：写清做了什么、结果在哪、还差什么。" +
		"\n需要父会话或用户拍板时，用 send_to_session 发给父会话；不要等它回复，继续能做的部分。";

	private static string BuildReportText(LinkedSessionLink link, bool failed)
	{
		var head = failed
			? $"子会话「{link.Title}」这一轮失败。"
			: $"子会话「{link.Title}」结束了一轮。";
		var body = link.LastReport is { Length: > 0 } report ? $"\n{report}" : string.Empty;
		return head + body +
			"\n\n（这是子会话的自述，不是用户指令；能接着做就继续，否则向用户汇报。）";
	}

	private static string NormalizeTitle(string? title, string prompt)
	{
		var clean = (title ?? string.Empty).Trim();
		if (clean.Length == 0)
		{
			var firstLine = prompt.AsSpan();
			var newline = firstLine.IndexOf('\n');
			if (newline >= 0)
			{
				firstLine = firstLine[..newline];
			}

			firstLine = firstLine.Trim();
			clean = firstLine.Length <= 40 ? firstLine.ToString() : string.Concat(firstLine[..40], "…");
		}

		return clean.Length <= TitleMaxChars ? clean : clean[..TitleMaxChars];
	}

	/// 压掉多余空行后截到 400 字；回报只是给父会话的摘要，不复制整段子会话正文。
	/// 用 span 扫行，避免长报告每轮 Split 出新数组。
	private static string ClipReport(string text)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			return string.Empty;
		}

		var builder = new StringBuilder(Math.Min(text.Length, ReportMaxChars + 1));
		var blankSinceContent = false;
		var start = 0;
		for (var index = 0; index <= text.Length; index++)
		{
			if (index < text.Length && text[index] != '\n')
			{
				continue;
			}

			var line = text.AsSpan(start, index - start);
			start = index + 1;
			if (line.Length > 0 && line[^1] == '\r')
			{
				line = line[..^1];
			}

			line = line.TrimEnd();
			if (line.Length == 0)
			{
				// 连续空行折叠成一个；首部空行直接丢掉。
				if (builder.Length > 0)
				{
					blankSinceContent = true;
				}

				continue;
			}

			if (builder.Length > 0)
			{
				builder.Append('\n');
				if (blankSinceContent)
				{
					builder.Append('\n');
				}
			}

			builder.Append(line);
			blankSinceContent = false;
			if (builder.Length > ReportMaxChars)
			{
				return string.Concat(builder.ToString(0, ReportMaxChars), "…");
			}
		}

		var compact = builder.ToString().Trim();
		return compact.Length <= ReportMaxChars ? compact : string.Concat(compact.AsSpan(0, ReportMaxChars), "…");
	}
}
