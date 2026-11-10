using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using PiWeft.Agent;
using PiWeft.Ai;
using PiWeft.CodingAgent;
using PiWeft.CodingAgent.Session;
using PiWeft.CodingAgent.Tools;

using PiWeft.Ai.Json;
using PiWeft.Cli;
using ZLogger;
namespace PiWeft.Desktop;

public enum ChatRole
{
	User,

	Assistant,

	Tool,

	Error,

	Process,
}

public sealed record ChatImage(string Data, string MimeType);

/// 一次思考段的独立起止（Unix 毫秒）。只在当前进程的流式事件里可得：
/// pi 的会话文件只保存 assistant 的请求起点与整条回复耗时，不落盘思考段起止。
internal readonly record struct ChatThinkingTiming(long StartedAt, long EndedAt);

public sealed record ChatEntry
{
	private string? _text;
	private string? _thinking;

	public required ChatRole Role { get; init; }

	/// 正文。流式行由 <see cref="StreamedText"/> 提供，这里只在复制/导出这类冷路径上整篇物化；
	/// 逐帧渲染走 <see cref="BodyText"/>，不会产生整篇字符串拷贝。
	public required string Text
	{
		get => _text ?? StreamedText?.Materialize() ?? string.Empty;
		init
		{
			_text = string.IsNullOrEmpty(value) ? null : value;
			// 显式给了正文就不再使用流式来源：投影用 with { Text = … } 抹掉正文时必须真的抹掉。
			StreamedText = null;
		}
	}

	public StreamedText? StreamedText { get; init; }

	public int TextLength => _text is not null ? _text.Length : StreamedText?.Length ?? 0;

	internal StreamText BodyText => _text is not null
		? new StreamText(_text)
		: StreamedText is { } live ? new StreamText(live) : StreamText.Empty;

	public string Thinking
	{
		get => _thinking ?? StreamedThinking?.Materialize() ?? string.Empty;
		init
		{
			_thinking = string.IsNullOrEmpty(value) ? null : value;
			StreamedThinking = null;
		}
	}

	public StreamedText? StreamedThinking { get; init; }

	public int ThinkingLength => _thinking is not null ? _thinking.Length : StreamedThinking?.Length ?? 0;

	internal StreamText ThinkingSource => _thinking is not null
		? new StreamText(_thinking)
		: StreamedThinking is { } live ? new StreamText(live) : StreamText.Empty;

	public bool IsThinking { get; init; }

	public long? ThinkingStartedAt { get; init; }

	public long? ThinkingEndedAt { get; init; }

	public long? ThinkingDurationMs => ThinkingStartedAt is { } startedAt && ThinkingEndedAt is { } endedAt && endedAt >= startedAt
		? endedAt - startedAt
		: null;

	public string? ToolName { get; init; }

	public string? ToolCallId { get; init; }

	public string? ToolArguments { get; init; }

	public IReadOnlyList<ChatEntry> ProcessEntries { get; init; } = [];

	public bool IsError { get; init; }

	public bool IsStopped { get; init; }

	public double? DurationMs { get; init; }

	public long? ToolStartedAt { get; init; }

	public string? EntryId { get; init; }

	public IReadOnlyList<ChatImage> Images { get; init; } = [];

	public long Timestamp { get; init; }

	public bool Compact { get; init; }

	public string? Patch { get; init; }

	/// 本轮（自上一条用户消息以来）成功修改过的文件，用于回答下方的修改汇总。
	/// 只在助手正文行有意义。
	public IReadOnlyList<ChatFileChange> ChangedFiles { get; init; } = [];

	public bool IsStreaming { get; init; }

	internal bool SameText(ChatEntry other)
	{
		if (ReferenceEquals(this, other)) return true;
		if (_text is not null || other._text is not null) return string.Equals(_text, other._text, StringComparison.Ordinal);
		return StreamedText is { } mine ? mine.SameSnapshot(other.StreamedText) : other.StreamedText is null;
	}

	internal bool SameThinking(ChatEntry other)
	{
		if (ReferenceEquals(this, other)) return true;
		if (_thinking is not null || other._thinking is not null) return string.Equals(_thinking, other._thinking, StringComparison.Ordinal);
		return StreamedThinking is { } mine ? mine.SameSnapshot(other.StreamedThinking) : other.StreamedThinking is null;
	}
}

/// 一次自动重试的界面状态。倒计时结束后 <see cref="RetryAtUnixMs"/> 成为过去时刻，
/// 界面据此从「N 秒后重试」切回「正在自动重试 n/m」。
public sealed record ChatRetryStatus(
	int Attempt,
	int MaxAttempts,
	int DelayMs,
	long StartedAtUnixMs,
	string ErrorMessage,
	bool Running)
{
	public long RetryAtUnixMs => DelayMs > 0 ? StartedAtUnixMs + DelayMs : 0;
}

/// 一条还没被模型消费的排队消息（PiDeck 的排队行）。用户运行中再发一条时，
/// 它先待在 steering 或 follow-up 队列里；界面靠这张快照把它列出来、撤回或丢弃。
/// <param name="Message">原始消息，撤回时按引用匹配。</param>
/// <param name="Text">预览文本（只取正文，不含图片数据）。</param>
/// <param name="ImageCount">随行的图片张数。</param>
/// <param name="Steer">true 表示下一次模型调用前插入当前回合，false 表示排队到下一轮。</param>
public sealed record ChatQueuedMessage(Message Message, string Text, int ImageCount, bool Steer);

public sealed record ChatRetryNotice(bool Success, int Attempt, int MaxAttempts, string? FinalError);

/// 把 <see cref="CodingAgentSession"/> 转成聊天视图要渲染的行，外加底部状态栏的一行文字。
/// 这里不依赖任何 UI 框架，可以无窗口运行，因此映射逻辑都能被单元测试覆盖；窗口只订阅
/// <see cref="Changed"/> 并在 UI 线程重绘。
/// 聊天记录由会话的投影消息推导，不逐事件累加：压缩、上下文编辑和分支切换都已由会话处理，
/// 视图不会偏离模型看到的内容。只有尚未落盘的流式文本走增量。
public sealed class ChatViewModel : IDisposable
{
	private readonly Lock _gate = new();
	private readonly List<ChatEntry> _entries = [];
	private IReadOnlyList<ChatEntry> _committedRows = [];
	private IReadOnlyList<ChatEntry>? _rowsSnapshot;
	private SessionManager? _builtSession;
	private long _builtRevision = -1;
	private IReadOnlyList<Message>? _rebuiltMessages;
	private int _rebuiltLastUserIndex = -1;
	private int _stableEntryCount;
	private Dictionary<Message, string>? _entryIds;
	private IReadOnlyList<SessionEntry>? _entryIdsBranch;
	private readonly List<ToolEntry> _tools = [];
	private IDisposable? _subscription;
	private CodingAgentSession? _session;
	// 追加式缓冲：片段不可变、快照共享，UI 线程读旧快照时不会碰到被下一轮清空的缓冲。
	private readonly StreamingBuffer _streamingText = new();
	private readonly StreamingBuffer _streamingThinking = new();
	// 回合代号在 ResetStreaming 时推进。只有同回合、同来源的快照才谈得上纯追加，
	// 否则下一回合会拿新长度去套上一回合的前缀。
	private int _streamingGeneration;
	private long _streamingTimestamp;
	private long _streamingThinkingStartedAt;
	private long _streamingThinkingEndedAt;
	private readonly ConcurrentDictionary<long, ChatThinkingTiming> _thinkingTimings = new();
	private long _thinkingTimingsRevision;
	private bool _isThinking;
	private bool _isStreaming;
	private bool _isCompacting;
	private string? _error;
	private long _runStarted;
	// 自动重试的实时状态与收口提示：重试退避没有消息事件，界面靠这两个字段显示「正在重试」。
	private ChatRetryStatus? _retry;
	private ChatRetryNotice? _retryNotice;
	private bool _aborting;
	// 未投递给模型的排队消息（steer + follow-up），顺序与队列一致。
	private ChatQueuedMessage[] _queued = [];

	public event Action? Changed;

	public CodingAgentSession? Session
	{
		get
		{
			lock (_gate)
			{
				return _session;
			}
		}
	}

	public bool IsStreaming
	{
		get
		{
			lock (_gate)
			{
				return _isStreaming;
			}
		}
	}

	/// 当前进程已经观测到的思考段起止，按 assistant 请求时间戳索引。轨迹页用它给已落盘的
	/// 思考行补独立耗时；新进程重新打开历史会话时表为空，回退到发生时刻（不伪造耗时）。
	internal IReadOnlyDictionary<long, ChatThinkingTiming> ThinkingTimings => _thinkingTimings;

	internal long ThinkingTimingsRevision => Volatile.Read(ref _thinkingTimingsRevision);

	/// 本回合开始的单调时间戳（<see cref="System.Diagnostics.Stopwatch.GetTimestamp"/> 口径），
	/// 在 <c>AgentStart</c> 上刷新；0 表示这一轮还没起过。等待期要回答「已经让读者等多久了」，
	/// 只能用一个不受系统时钟调改影响的起点。
	internal long TurnStarted
	{
		get
		{
			lock (_gate)
			{
				return _runStarted;
			}
		}
	}

	public string? Error
	{
		get
		{
			lock (_gate)
			{
				return _error;
			}
		}
	}

	public ChatRetryStatus? Retry
	{
		get
		{
			lock (_gate)
			{
				return _retry;
			}
		}
	}

	public ChatRetryNotice? TakeRetryNotice()
	{
		lock (_gate)
		{
			var notice = _retryNotice;
			_retryNotice = null;
			return notice;
		}
	}

	/// 当前还没被模型消费的排队消息，steering 在前（它会先进上下文）、follow-up 在后。
	/// PiDeck 的排队区同口径：一轮里可以排多条，逐条撤回或丢弃。
	public IReadOnlyList<ChatQueuedMessage> QueuedMessages()
	{
		lock (_gate)
		{
			return _queued;
		}
	}

	public bool RetractQueued(ChatQueuedMessage queued)
	{
		ArgumentNullException.ThrowIfNull(queued);
		var removed = Session?.RemoveQueued(queued.Message) ?? false;
		if (!removed)
		{
			// 已经投递给模型了：队列快照会由 queue_update 纠正，这里只拒绝这次撤回。
			return false;
		}

		Changed?.Invoke();
		return true;
	}

	public void Attach(CodingAgentSession session)
	{
		ArgumentNullException.ThrowIfNull(session);

		lock (_gate)
		{
			_subscription?.Dispose();
			_session = session;
			_builtSession = null;
			_rebuiltMessages = null;
			_rebuiltLastUserIndex = -1;
			_stableEntryCount = 0;
			_entryIds = null;
			_entryIdsBranch = null;
			_entries.Clear();
			_tools.Clear();
			ResetStreaming();
			_error = null;
			_isStreaming = false;
			_isCompacting = false;
			_retry = null;
			_retryNotice = null;
			_aborting = false;
			_queued = [];
			// 打开会话时的首次投影就从这里走：长会话整段映射要 200+ 毫秒，必须计时。
			RebuildTimed(session);
			_subscription = session.Subscribe(OnAgentEvent);
		}

		Changed?.Invoke();
	}

	public void Detach()
	{
		lock (_gate)
		{
			_subscription?.Dispose();
			_subscription = null;
			_session = null;
			_entries.Clear();
			_committedRows = [];
			_rowsSnapshot = null;
			_builtSession = null;
			_rebuiltMessages = null;
			_rebuiltLastUserIndex = -1;
			_stableEntryCount = 0;
			_entryIds = null;
			_entryIdsBranch = null;
			_tools.Clear();
			ResetStreaming();
			_isStreaming = false;
			_error = null;
			_retry = null;
			_retryNotice = null;
			_aborting = false;
			_queued = [];
		}

		Changed?.Invoke();
	}

	private static ChatQueuedMessage[] ProjectQueue(QueueUpdateEvent queue)
	{
		var total = queue.Steering.Count + queue.FollowUp.Count;
		if (total == 0)
		{
			return [];
		}

		var rows = new ChatQueuedMessage[total];
		var index = 0;
		foreach (var message in queue.Steering)
		{
			rows[index++] = Queued(message, steer: true);
		}

		foreach (var message in queue.FollowUp)
		{
			rows[index++] = Queued(message, steer: false);
		}

		return rows;
	}

	private static ChatQueuedMessage Queued(Message message, bool steer) => message switch
	{
		UserMessage user => new(message, TextOf(user.Content), ImagesOf(user.Content).Count, steer),
		_ => new(message, string.Empty, 0, steer),
	};

	public IReadOnlyList<ChatEntry> Entries()
	{
		lock (_gate)
		{
			// 投影是长会话里最贵的 UI 前置工作之一：整段重建时记一次，供诊断页对照。
			if (_session is { } session &&
				(!ReferenceEquals(_builtSession, session.Session) || _builtRevision != session.Session.Revision))
			{
				RebuildTimed(session);
			}
			if (_rowsSnapshot is { } cached) return cached;
			var rows = new List<ChatEntry>(_tools.Count + 1);

			// 尚未写入会话文件的工具调用来自事件流，因此单独补齐。
			foreach (var tool in _tools)
			{
				rows.Add(tool.ToEntry());
			}

			if (_isStreaming && (_streamingText.Length > 0 || _streamingThinking.Length > 0 || _isThinking))
			{
				// 流式正文/思考只给快照（来源 + 长度），不复制正文：每帧整篇 ToString() 是
				// 诊断面板「每帧分配」的来源（200K 字回复 = 每帧 ~400 KB）。渲染侧的打字机与
				// 增量 Markdown 按前缀切片，整篇物化只发生在复制/导出这类冷路径。
				rows.Add(new ChatEntry
				{
					Role = ChatRole.Assistant,
					Text = string.Empty,
					Thinking = string.Empty,
					IsThinking = _isThinking,
					ThinkingStartedAt = _streamingThinkingStartedAt > 0 ? _streamingThinkingStartedAt : null,
					ThinkingEndedAt = _streamingThinkingEndedAt > 0 ? _streamingThinkingEndedAt : null,
					Timestamp = _streamingTimestamp,
					EntryId = $"live-assistant/{_streamingTimestamp}",
					IsStreaming = true,
				} with
				{
					// 两个来源必须在 Text/Thinking 之后赋值：那两个 init 会把来源清空。
					StreamedText = new StreamedText(_streamingText, _streamingGeneration),
					StreamedThinking = new StreamedText(_streamingThinking, _streamingGeneration),
				});
			}

			return _rowsSnapshot = rows.Count == 0 ? _committedRows : new JoinedRows(_committedRows, rows.ToArray());
		}
	}

	public string? LastAssistantText()
	{
		lock (_gate)
		{
			return _session?.Session.LastAssistantText();
		}
	}

	public string? LastUserText()
	{
		lock (_gate)
		{
			if (_session is null)
			{
				return null;
			}

			var forkable = _session.Session.GetUserMessagesForForking();
			return forkable.Count > 0 ? forkable[^1].Text : null;
		}
	}

	public string StatusLine()
	{
		lock (_gate)
		{
			if (_session is not { } session)
			{
				return Strings.NoSession;
			}

			var stats = session.Statistics();
			var tokens = stats.Tokens;
			var cost = stats.Cost;
			var (contextTokens, _) = session.ContextTokens();
			var window = session.Model.ContextWindow;
			var percent = window > 0 ? 100.0 * contextTokens / window : 0;
			var cacheHit = stats.LatestCacheHitPercent ?? 0;

			// 状态行只保留决策相关的信息；缓存命中与 token 明细在设置面板里，避免状态行变成日志。
			var parts = new List<string>
			{
				session.Model.Id,
				Strings.ThinkingShort(session.ThinkingLevel),
				$"{Strings.Context} {percent:0.#}%",
			};

			if (tokens > 0)
			{
				parts.Add($"{Strings.Tokens} {tokens}");
			}

			if (cost > 0)
			{
				parts.Add($"${cost:0.0000}");
			}

			if (cacheHit > 0)
			{
				parts.Add($"{Strings.CacheHit} {cacheHit:0}%");
			}

			if (_isCompacting)
			{
				parts.Add(Strings.Compacting);
			}

			if (_isStreaming)
			{
				parts.Add(Strings.Streaming);
			}

			if (session.SessionName is { Length: > 0 } name)
			{
				parts.Insert(0, name);
			}

			return string.Join("  ·  ", parts);
		}
	}

	public async Task SendAsync(string text, CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			return;
		}

		lock (_gate)
		{
			// 上一轮被中断后留下的中止标记不能污染这一次重试收口判定。
			_aborting = false;
		}

		var session = Session ?? throw new InvalidOperationException("尚未绑定会话");

		lock (_gate)
		{
			_error = null;
		}

		try
		{
			await session.PromptAsync(text, cancellationToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			// 用户主动中断属于正常操作，不当作错误展示；但必须清掉流式状态，
			// 否则桌面会一直停在「中断」按钮上，后续消息也发不出去。
			lock (_gate)
			{
				_isStreaming = false;
				_retry = null;
				ResetStreaming();
				Rebuild();
			}
		}
		catch (Exception ex)
		{
			AppLog.Failure(AppLog.For<ChatViewModel>(), "Session prompt", ex);
			lock (_gate)
			{
				_error = ex.Message;
			}
		}

		Changed?.Invoke();
	}

	public void Abort()
	{
		lock (_gate)
		{
			_aborting = true;
		}
		Session?.Abort();
	}

	public bool Steer(string text, IReadOnlyList<ImageContent>? images = null) => Enqueue(text, images, steer: true);

	public bool FollowUp(string text, IReadOnlyList<ImageContent>? images = null) => Enqueue(text, images, steer: false);

	private bool Enqueue(string text, IReadOnlyList<ImageContent>? images, bool steer)
	{
		var session = Session;
		if (session is null) return false;
		var message = session.PrepareInput(text, images);
		if (steer) session.Steer(message);
		else session.FollowUp(message);
		return true;
	}

	public void Dispose()
	{
		Detach();
		GC.SuppressFinalize(this);
	}

	private ValueTask OnAgentEvent(AgentEvent @event)
	{
		// 工具的部分输出还没进 UI：这类事件既不改投影也不改状态，早退可避免每次
		// tool_execution_update 都清空快照并让宿主整帧重绘（接实时输出时再在这里处理）。
		if (@event is ToolExecutionUpdateEvent)
		{
			return ValueTask.CompletedTask;
		}

		lock (_gate)
		{
			_rowsSnapshot = null;
			switch (@event)
			{
				case AgentStartEvent:
					_runStarted = System.Diagnostics.Stopwatch.GetTimestamp();
					AppLog.For<ChatViewModel>().ZLogInformation($"Session run started: session={_session?.Session.SessionId}");
					_isStreaming = true;
					_error = null;
					ResetStreaming();
					break;

				case MessageStartEvent { Message: AssistantMessage assistant }:
					ResetStreaming();
					_streamingTimestamp = assistant.Timestamp;
					break;

				case MessageUpdateEvent { AssistantMessageEvent: TextDeltaEvent delta }:
					_streamingText.Append(delta.Delta);
					// 有的 provider 不补 thinking_end：首个正文 delta 就是思考段的事实终点。
					if (_streamingThinkingStartedAt > 0 && _streamingThinkingEndedAt == 0)
					{
						_streamingThinkingEndedAt = NowMs();
					}

					break;

				case MessageUpdateEvent { AssistantMessageEvent: ThinkingStartEvent }:
					if (_streamingThinking.Length > 0)
					{
						_streamingThinking.Append("\n\n");
					}

					if (_streamingThinkingStartedAt <= 0)
					{
						_streamingThinkingStartedAt = NowMs();
					}

					_streamingThinkingEndedAt = 0;
					_isThinking = true;
					break;

				case MessageUpdateEvent { AssistantMessageEvent: ThinkingDeltaEvent thinking }:
					if (_streamingThinkingStartedAt <= 0)
					{
						_streamingThinkingStartedAt = NowMs();
					}

					_streamingThinkingEndedAt = 0;
					_streamingThinking.Append(thinking.Delta);
					_isThinking = true;
					break;

				case MessageUpdateEvent { AssistantMessageEvent: ThinkingEndEvent }:
					_streamingThinkingEndedAt = NowMs();
					_isThinking = false;
					break;

				case MessageUpdateEvent { AssistantMessageEvent: ErrorEvent error }:
					_error = error.Error.ErrorMessage;
					_isStreaming = false;
					Rebuild();
					break;

				case MessageEndEvent ended:
					if (ended.Message is AssistantMessage endedAssistant)
					{
						RecordThinkingTiming(endedAssistant);
					}

					if (ended.Message is AssistantMessage { StopReason: StopReason.Error or StopReason.Aborted } failed)
					{
						AppLog.For<ChatViewModel>().ZLogWarning($"Assistant stopped: session={_session?.Session.SessionId}, reason={failed.StopReason.ToString()}");
					}

					// 落盘的消息已经包含刚才流式输出的内容。
					ResetStreaming();
					Rebuild();
					break;

				case ToolExecutionStartEvent start:
					_tools.RemoveAll(tool => tool.ToolCallId == start.ToolCallId);
					// 开始时刻只在这里取一次：tool_execution_update 会带新的部分结果，但不会重置秒表。
					_tools.Add(new ToolEntry(start.ToolCallId, start.ToolName, start.Arguments.ToJsonString(PiJson.NodeOptions), null, false, null, NowMs()));
					break;

				case ToolExecutionEndEvent end:
					var running = _tools.FirstOrDefault(tool => tool.ToolCallId == end.ToolCallId);
					var arguments = running?.Arguments ?? string.Empty;
					var startedAt = running?.StartedAt ?? (end.DurationMs is { } elapsedMs
						? NowMs() - (long)elapsedMs
						: NowMs());
					_tools.RemoveAll(tool => tool.ToolCallId == end.ToolCallId);
					_tools.Add(new ToolEntry(
						end.ToolCallId,
						end.ToolName,
						arguments,
						ToolResultText(end.Result),
						end.IsError,
						end.DurationMs,
						startedAt));
					break;

				case QueueUpdateEvent queue:
					// pi 的 queue_update 带的是变化后的全量队列，直接替换即可。
					_queued = ProjectQueue(queue);
					break;

				case AutoRetryStartEvent retry:
					AppLog.For<ChatViewModel>().ZLogWarning($"Retry scheduled: session={_session?.Session.SessionId}, attempt={retry.Attempt}, delayMs={retry.DelayMs}");
					// 退避期间没有其它消息事件：把「正在重试 n/m」连同倒计时终点交给界面。
					_retry = new ChatRetryStatus(
						retry.Attempt,
						retry.MaxAttempts,
						retry.DelayMs,
						DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
						retry.ErrorMessage,
						Running: true);
					// 失败的那次尝试不是最终结果：状态栏不要提前挂上错误。
					_error = null;
					break;

				case AutoRetryEndEvent retryEnd:
				{
					var status = _retry;
					var maxAttempts = status?.MaxAttempts ?? 0;
					_retry = null;
					if (retryEnd.Success)
					{
						AppLog.For<ChatViewModel>().ZLogInformation($"Retry succeeded: session={_session?.Session.SessionId}, attempt={retryEnd.Attempt}");
						_retryNotice = new ChatRetryNotice(true, retryEnd.Attempt, maxAttempts, null);
					}
					else if (!_aborting && !string.Equals(retryEnd.FinalError, "Aborted", StringComparison.Ordinal))
					{
						AppLog.For<ChatViewModel>().ZLogWarning($"Retry exhausted: session={_session?.Session.SessionId}, attempt={retryEnd.Attempt}, error={retryEnd.FinalError}");
						_error = retryEnd.FinalError;
						_retryNotice = new ChatRetryNotice(false, retryEnd.Attempt, maxAttempts, retryEnd.FinalError);
					}
					break;
				}

				case AgentEndEvent:
					var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(_runStarted).TotalMilliseconds;
					AppLog.For<ChatViewModel>().ZLogInformation($"Session run ended: session={_session?.Session.SessionId}, elapsedMs={elapsed:0}");
					_isStreaming = false;
					ResetStreaming();
					Rebuild();
					break;
			}
		}

		Changed?.Invoke();
		return ValueTask.CompletedTask;
	}

	private void ResetStreaming()
	{
		_streamingText.Reset();
		_streamingThinking.Reset();
		_streamingGeneration++;
		_streamingTimestamp = 0;
		_streamingThinkingStartedAt = 0;
		_streamingThinkingEndedAt = 0;
		_isThinking = false;
	}

	private void RecordThinkingTiming(AssistantMessage message)
	{
		if (_streamingThinkingStartedAt <= 0)
		{
			return;
		}

		var endedAt = _streamingThinkingEndedAt > 0 ? _streamingThinkingEndedAt : NowMs();
		if (endedAt < _streamingThinkingStartedAt)
		{
			endedAt = _streamingThinkingStartedAt;
		}

		_thinkingTimings[message.Timestamp] = new ChatThinkingTiming(_streamingThinkingStartedAt, endedAt);
		Interlocked.Increment(ref _thinkingTimingsRevision);
	}

	private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

	/// 仅供 <c>--ui-smoke stream</c>：把一段流式增量塞进当前行。没有模型也能复现
	/// 「逐字打字机 + 增量 Markdown + 每帧重绘」这条路径，用来观察流式期的内存曲线。
	internal void SmokeStream(string bodyDelta, string thinkingDelta, bool finish)
	{
		lock (_gate)
		{
			_rowsSnapshot = null;
			if (!_isStreaming && !finish)
			{
				_isStreaming = true;
				ResetStreaming();
				_streamingTimestamp = DateTimeOffset.Now.ToUnixTimeMilliseconds();
			}
			if (thinkingDelta.Length > 0)
			{
				_streamingThinking.Append(thinkingDelta);
				_isThinking = true;
			}
			if (bodyDelta.Length > 0)
			{
				_streamingText.Append(bodyDelta);
				_isThinking = false;
			}
			if (finish)
			{
				_isStreaming = false;
				_isThinking = false;
			}
		}
		Changed?.Invoke();
	}

	/// 仅供 <c>--ui-smoke waiting</c>：把视图模型摆成「刚发出一条消息、首 token 还没到」。
	/// 这一步在真实运行里要等网络，视觉验收不能等，所以直接种一行用户消息并把回合标为运行中；
	/// 之后的行为与真的首轮等待一致（状态条判预热、时间线尾部拼占位行）。
	internal void SmokeWaitingTurn(string text)
	{
		lock (_gate)
		{
			_entries.Clear();
			_entries.Add(new ChatEntry
			{
				Role = ChatRole.User,
				Text = text,
				Timestamp = DateTimeOffset.Now.ToUnixTimeMilliseconds(),
				EntryId = "smoke-user",
			});
			_committedRows = _entries.ToArray();
			// 把重建基线定在「当前版本」：下一次 Entries() 不会用真会话把这一行冲掉。
			_builtSession = _session?.Session;
			_builtRevision = _session?.Session.Revision ?? -1;
			_rowsSnapshot = null;
			_isStreaming = true;
			ResetStreaming();
		}

		Changed?.Invoke();
	}

	private void RebuildTimed(CodingAgentSession session)
	{
		if (!Diagnostics.Enabled)
		{
			Rebuild();
			return;
		}

		var watch = System.Diagnostics.Stopwatch.StartNew();
		Rebuild();
		Diagnostics.Record("chat.project", watch, $"messages={session.Messages.Count}, rows={_entries.Count}");
	}

	private void Rebuild()
	{
		_rowsSnapshot = null;
		_tools.Clear();
		if (_session is not null && ReferenceEquals(_builtSession, _session.Session) &&
			_builtRevision == _session.Session.Revision) return;

		if (_session is null)
		{
			_entries.Clear();
			_committedRows = [];
			return;
		}
		_builtSession = _session.Session;
		_builtRevision = _session.Session.Revision;

		var messages = _session.Messages;
		var lastUser = LastUserIndex(messages);

		// Streaming and tool events append to the current turn. Reuse every row before the last
		// user message and rebuild only that turn; a full mapping of a long session is O(history)
		// allocations on the UI thread for each new message.
		if (TryRebuildTail(messages, lastUser))
		{
			_committedRows = _entries.ToArray();
			return;
		}

		_entries.Clear();
		_committedRows = [];
		var entryIds = BuildEntryIds();
		var results = BuildResults(messages, 0);
		// 本轮（自上一条用户消息以来）成功修改过的文件，附在助手正文行下方。
		var turnChanges = new TurnFileChanges();
		var stableEntryCount = 0;
		for (var index = 0; index < messages.Count; index++)
		{
			if (index == lastUser)
			{
				stableEntryCount = _entries.Count;
			}

			AppendMessage(messages[index], entryIds, results, turnChanges);
		}

		_stableEntryCount = lastUser >= 0 ? stableEntryCount : _entries.Count;
		_rebuiltMessages = messages;
		_rebuiltLastUserIndex = lastUser;
		_committedRows = _entries.ToArray();
	}

	private bool TryRebuildTail(IReadOnlyList<Message> messages, int lastUser)
	{
		if (_rebuiltMessages is not { } previous || lastUser < 0)
		{
			return false;
		}

		if (lastUser == _rebuiltLastUserIndex)
		{
			if (!PrefixEquals(messages, previous, lastUser))
			{
				return false;
			}

			var retained = Math.Min(_stableEntryCount, _entries.Count);
			_entries.RemoveRange(retained, _entries.Count - retained);
			AppendTail(messages, lastUser);
			_rebuiltMessages = messages;
			return true;
		}

		// A new user message after the previously rebuilt history: every old row is stable.
		if (lastUser == previous.Count && PrefixEquals(messages, previous, previous.Count))
		{
			_stableEntryCount = _entries.Count;
			AppendTail(messages, lastUser);
			_rebuiltMessages = messages;
			_rebuiltLastUserIndex = lastUser;
			return true;
		}

		return false;
	}

	private void AppendTail(IReadOnlyList<Message> messages, int start)
	{
		var entryIds = BuildEntryIds();
		var results = BuildResults(messages, start);
		var turnChanges = new TurnFileChanges();
		for (var index = start; index < messages.Count; index++)
		{
			AppendMessage(messages[index], entryIds, results, turnChanges);
		}
	}

	private Dictionary<Message, string> BuildEntryIds()
	{
		var branch = _session!.Session.GetBranch();
		if (_entryIds is null || !BranchPrefixEquals(branch, _entryIdsBranch))
		{
			// 会话条目 id 与投影消息的对应关系：投影基本复用原实例，因此按引用匹配即可。
			_entryIds = new Dictionary<Message, string>(ReferenceEqualityComparer.Instance);
			foreach (var entry in branch)
			{
				AddEntryId(_entryIds, entry);
			}
		}
		else
		{
			for (var index = _entryIdsBranch!.Count; index < branch.Count; index++)
			{
				AddEntryId(_entryIds, branch[index]);
			}
		}

		_entryIdsBranch = branch;
		return _entryIds;
	}

	private static void AddEntryId(Dictionary<Message, string> entryIds, SessionEntry entry)
	{
		if (entry is SessionMessageEntry sessionEntry)
		{
			entryIds[sessionEntry.Message] = sessionEntry.Id;
		}
	}

	private static bool BranchPrefixEquals(IReadOnlyList<SessionEntry> branch, IReadOnlyList<SessionEntry>? previous)
	{
		if (previous is null || branch.Count < previous.Count)
		{
			return false;
		}

		// A branch is a tree path: if the last previously cached entry still sits at the same
		// depth, its parent chain proves every entry before it is unchanged.
		return previous.Count == 0 || ReferenceEquals(branch[previous.Count - 1], previous[previous.Count - 1]);
	}

	private static Dictionary<string, ToolResultMessage> BuildResults(IReadOnlyList<Message> messages, int start)
	{
		// 工具结果按调用 id 索引：判断写工具是否真的成功了。
		var results = new Dictionary<string, ToolResultMessage>(StringComparer.Ordinal);
		for (var index = start; index < messages.Count; index++)
		{
			if (messages[index] is ToolResultMessage result)
			{
				results[result.ToolCallId] = result;
			}
		}

		return results;
	}

	private void AppendMessage(
		Message message,
		Dictionary<Message, string> entryIds,
		Dictionary<string, ToolResultMessage> results,
		TurnFileChanges turnChanges)
	{
		switch (message)
		{
			case SystemMessage:
				return;

			case UserMessage user:
				// 扩展注入的内部上下文（目标模式 / 计划模式等 display:false）仍参与模型上下文，
				// 也仍是一轮的边界，但不占用户气泡：和 PiDeck 一样不把这类注入铺在对话里。
				turnChanges.Clear();
				if (!user.Display) return;
				_entries.Add(new ChatEntry
				{
					Role = ChatRole.User,
					Text = TextOf(user.Content),
					Images = ImagesOf(user.Content),
					Timestamp = user.Timestamp,
					EntryId = entryIds.GetValueOrDefault(message),
				});
				return;

			case AssistantMessage assistant:
			{
				var text = TextOfBlocks(assistant.Content);
				var thinking = ThinkingOf(assistant.Content);
				var calls = CallsOf(assistant.Content);
				var images = ImagesOfBlocks(assistant.Content);

				// 先累计本轮写工具的成功改动，再给正文行挂上汇总。
				foreach (var call in calls)
				{
					if (call.Name is not ("edit" or "write"))
					{
						continue;
					}

					if (call.Arguments["path"]?.GetValue<string>() is not { Length: > 0 } changedPath)
					{
						continue;
					}

					if (results.TryGetValue(call.Id, out var toolResult) && !toolResult.IsError)
					{
						var (added, removed, patch) = FileChangeOf(call, toolResult, changedPath);
						turnChanges.Add(changedPath, added, removed, patch);
					}
				}

				if (text.Length > 0 || thinking.Length > 0 || images.Count > 0)
				{
					var hasThinkingTiming = _thinkingTimings.TryGetValue(assistant.Timestamp, out var thinkingTiming);
					_entries.Add(new ChatEntry
					{
						Role = ChatRole.Assistant,
						Text = text,
						Thinking = thinking,
						ThinkingStartedAt = hasThinkingTiming ? thinkingTiming.StartedAt : null,
						ThinkingEndedAt = hasThinkingTiming ? thinkingTiming.EndedAt : null,
						DurationMs = assistant.DurationMs,
						EntryId = entryIds.GetValueOrDefault(message),
						Images = images,
						Timestamp = assistant.Timestamp,
						ChangedFiles = turnChanges.Count > 0 ? [.. turnChanges.Items] : [],
					});
				}

				foreach (var call in calls)
				{
					// 同一份参数序列化一次复用：连续两次 ToJsonString 在工具多的会话里是白做的热路径开销。
					var arguments = call.Arguments.ToJsonString(PiJson.NodeOptions);
					_entries.Add(new ChatEntry
					{
						Role = ChatRole.Tool,
						EntryId = $"{entryIds.GetValueOrDefault(message) ?? call.Id}/tool/{call.Id}",
						ToolName = call.Name,
						ToolCallId = call.Id,
						ToolArguments = arguments,
						IsStreaming = true,
						Text = arguments,
					});
				}

				if (assistant.StopReason == StopReason.Error && assistant.ErrorMessage is { Length: > 0 } error)
				{
					_entries.Add(new ChatEntry { Role = ChatRole.Error, Text = error });
				}

				return;
			}

			case ToolResultMessage result:
			{
				_entries.Add(new ChatEntry
				{
					Role = ChatRole.Tool,
					EntryId = entryIds.GetValueOrDefault(message),
					ToolName = result.ToolName,
					ToolCallId = result.ToolCallId,
					Text = TextOfBlocks(result.Content),
					Images = ImagesOfBlocks(result.Content),
					Timestamp = result.Timestamp,
					Patch = PatchOf(result.Details),
					IsError = result.IsError,
					DurationMs = result.DurationMs,
				});
				return;
			}

			case CustomMessage custom:
			{
				// 压缩检查点和分支摘要各自成行。
				var text = custom.Content.Text ?? string.Empty;
				if (text.Length > 0)
				{
					_entries.Add(new ChatEntry { Role = ChatRole.Assistant, Text = $"【{custom.CustomRole}】\n{text}" });
				}

				return;
			}
		}
	}

	private static int LastUserIndex(IReadOnlyList<Message> messages)
	{
		for (var index = messages.Count - 1; index >= 0; index--)
		{
			if (messages[index] is UserMessage)
			{
				return index;
			}
		}

		return -1;
	}

	private static bool PrefixEquals(IReadOnlyList<Message> messages, IReadOnlyList<Message> previous, int count)
	{
		if (messages.Count < count || previous.Count < count)
		{
			return false;
		}

		for (var index = 0; index < count; index++)
		{
			if (!ReferenceEquals(messages[index], previous[index]))
			{
				return false;
			}
		}

		return true;
	}

	private static string TextOfBlocks(IReadOnlyList<ContentBlock> blocks)
	{
		if (blocks.Count == 0)
		{
			return string.Empty;
		}

		if (blocks.Count == 1 && blocks[0] is TextContent only)
		{
			return only.Text;
		}

		var builder = new StringBuilder();
		foreach (var block in blocks)
		{
			if (block is TextContent text && text.Text.Length > 0)
			{
				builder.Append(text.Text);
			}
		}

		return builder.ToString();
	}

	private static string ThinkingOf(IReadOnlyList<ContentBlock> blocks)
	{
		StringBuilder? builder = null;
		foreach (var block in blocks)
		{
			if (block is ThinkingContent { Redacted: not true, Thinking.Length: > 0 } thinking)
			{
				if (builder is null)
				{
					builder = new StringBuilder(thinking.Thinking);
				}
				else
				{
					builder.Append("\n\n").Append(thinking.Thinking);
				}
			}
		}

		return builder?.ToString() ?? string.Empty;
	}

	private static List<ToolCall> CallsOf(IReadOnlyList<ContentBlock> blocks)
	{
		List<ToolCall>? calls = null;
		foreach (var block in blocks)
		{
			if (block is ToolCall call)
			{
				(calls ??= []).Add(call);
			}
		}

		return calls ?? [];
	}

	// Share the immutable historical prefix; a streaming frame allocates only its small tail.
	private sealed class JoinedRows(IReadOnlyList<ChatEntry> history, IReadOnlyList<ChatEntry> tail) : IReadOnlyList<ChatEntry>
	{
		public int Count => history.Count + tail.Count;
		public ChatEntry this[int index] => index < history.Count ? history[index] : tail[index - history.Count];
		public IEnumerator<ChatEntry> GetEnumerator()
		{
			foreach (var entry in history) yield return entry;
			foreach (var entry in tail) yield return entry;
		}
		System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
	}

	/// 把一次成功的写工具调用折算成增删行数与补丁：优先用结果里的统一补丁，
	/// 历史会话没有补丁时 write 按整篇新增、edit 按参数里的 oldText/newText 估算。
	private static (int Added, int Removed, string Patch) FileChangeOf(ToolCall call, ToolResultMessage result, string path)
	{
		if (PatchOf(result.Details) is { } patch)
		{
			var (added, removed) = DiffStats.Count(patch);
			return (added, removed, patch);
		}

		if (call.Name == "write" && StringOf(call.Arguments["content"]) is { } content)
		{
			var written = EditDiff.UnifiedPatch(string.Empty, content, path);
			var (addedLines, removedLines) = DiffStats.Count(written);
			return (addedLines, removedLines, written);
		}

		return (EditArgumentLines(call.Arguments, "newText"), EditArgumentLines(call.Arguments, "oldText"), string.Empty);
	}

	private static int EditArgumentLines(JsonObject arguments, string field)
	{
		var lines = 0;
		if (arguments["edits"] is JsonArray edits)
		{
			foreach (var edit in edits)
			{
				if (edit is JsonObject item) lines += LineCount(StringOf(item[field]));
			}

			return lines;
		}

		return LineCount(StringOf(arguments[field]));
	}

	private static int LineCount(string? text) => text is { Length: > 0 } ? text.AsSpan().Count('\n') + 1 : 0;

	private static string? StringOf(JsonNode? node) =>
		node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

	private static string? PatchOf(JsonNode? details) =>
		details is JsonObject detailsObject && detailsObject["patch"] is JsonValue patch &&
		patch.TryGetValue<string>(out var text) && text.Length > 0
			? text
			: null;

	private static string TextOf(MessageContent content) =>
		content.Blocks is { Count: > 0 } blocks
			? string.Concat(blocks.OfType<TextContent>().Select(static block => block.Text))
			: content.Text ?? string.Empty;

	private static IReadOnlyList<ChatImage> ImagesOf(MessageContent content) =>
		content.Blocks is { Count: > 0 } blocks ? ImagesOfBlocks(blocks) : [];

	private static IReadOnlyList<ChatImage> ImagesOfBlocks(IReadOnlyList<ContentBlock> blocks) =>
		[.. blocks.OfType<ImageContent>().Select(static image => new ChatImage(image.Data, image.MimeType))];

	private sealed record ToolEntry(
		string ToolCallId,
		string ToolName,
		string Arguments,
		string? Result,
		bool IsError,
		double? DurationMs,
		long StartedAt)
	{
		// 流式期每帧都会重投影：已完成的工具结果可能几十 KB，逐帧重拼一遍纯属垃圾。
		// 条目本身不可变（事件到达时换新实例），缓存一次投影即安全。
		private ChatEntry? _projected;

		public ChatEntry ToEntry() => _projected ??= new ChatEntry
		{
			Role = ChatRole.Tool,
			ToolName = ToolName,
			ToolCallId = ToolCallId,
			ToolArguments = Arguments.Length > 0 ? Arguments : null,
			IsStreaming = Result is null,
			EntryId = $"live-tool/{ToolCallId}",
			IsError = IsError,
			DurationMs = DurationMs,
			ToolStartedAt = Result is null && StartedAt > 0 ? StartedAt : null,
			Text = Result is null ? $"→ {ToolName} {Arguments}" : $"→ {ToolName}\n{Result}",
		};
	}

	private static string ToolResultText(AgentToolResult result) =>
		string.Concat(result.Content.OfType<TextContent>().Select(static block => block.Text));
}
