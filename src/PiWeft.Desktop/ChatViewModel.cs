using System.Text;
using System.Text.Json.Nodes;
using PiWeft.Agent;
using PiWeft.Ai;
using PiWeft.CodingAgent;
using PiWeft.CodingAgent.Session;

using PiWeft.Ai.Json;
using PiWeft.Cli;
using ZLogger;
namespace PiWeft.Desktop;

/// <summary>聊天记录中一行的类型。</summary>
public enum ChatRole
{
	/// <summary>用户输入。</summary>
	User,

	/// <summary>助手回复。</summary>
	Assistant,

	/// <summary>工具调用或工具结果。</summary>
	Tool,

	/// <summary>错误。</summary>
	Error,

	/// <summary>可折叠的执行过程汇总。</summary>
	Process,
}

/// <summary>聊天记录里的图片：Base64 数据与媒体类型。</summary>
public sealed record ChatImage(string Data, string MimeType);

/// <summary>聊天记录中的一行。</summary>
public sealed record ChatEntry
{
	public required ChatRole Role { get; init; }

	public required string Text { get; init; }

	/// <summary>可展示的思考文本，与回复正文和不透明签名分离。</summary>
	public string Thinking { get; init; } = string.Empty;

	/// <summary>当前消息的思考块仍在生成。</summary>
	public bool IsThinking { get; init; }

	/// <summary>工具名，仅工具行有值。</summary>
	public string? ToolName { get; init; }

	/// <summary>关联工具调用与结果，执行过程只计数一次。</summary>
	public string? ToolCallId { get; init; }

	/// <summary>合并调用与结果后仍保留原始输入参数。</summary>
	public string? ToolArguments { get; init; }

	/// <summary>执行过程行内按原顺序保留的思考、工具与中间回复。</summary>
	public IReadOnlyList<ChatEntry> ProcessEntries { get; init; } = [];

	/// <summary>工具是否失败，仅工具行有意义。</summary>
	public bool IsError { get; init; }

	/// <summary>本轮已停止且工具没有结果；不能继续显示为运行中或成功。</summary>
	public bool IsStopped { get; init; }

	/// <summary>工具执行或整次助手回复的耗时（毫秒），不代表思考耗时。</summary>
	public double? DurationMs { get; init; }

	/// <summary>该消息对应的会话条目 id；用户消息用于「在此分支」。</summary>
	public string? EntryId { get; init; }

	/// <summary>消息里的图片（Base64 与 MIME 类型），界面直接渲染。</summary>
	public IReadOnlyList<ChatImage> Images { get; init; } = [];

	/// <summary>该行的消息时间（Unix 毫秒），用于在角色行显示时间。</summary>
	public long Timestamp { get; init; }

	/// <summary>执行过程内的行：不再重复角色名、时间与复制按钮。</summary>
	public bool Compact { get; init; }

	/// <summary>工具返回的统一 diff 补丁（<c>edit</c> 工具的细节），界面渲染成 diff 视图。</summary>
	public string? Patch { get; init; }

	/// <summary>
	/// 本轮（自上一条用户消息以来）成功修改过的文件，用于回答下方的修改汇总。
	/// 只在助手正文行有意义。
	/// </summary>
	public IReadOnlyList<string> ChangedFiles { get; init; } = [];

	/// <summary>助手仍在写这一行时为 true。</summary>
	public bool IsStreaming { get; init; }
}

/// <summary>
/// 把 <see cref="CodingAgentSession"/> 转成聊天视图要渲染的行，外加底部状态栏的一行文字。
/// 这里不依赖任何 UI 框架，可以无窗口运行，因此映射逻辑都能被单元测试覆盖；窗口只订阅
/// <see cref="Changed"/> 并在 UI 线程重绘。
/// </summary>
/// <remarks>
/// 聊天记录由会话的投影消息推导，而不是逐事件累加：会话已经处理了压缩、上下文编辑和分支切换，
/// 视图因此不会与模型真正看到的内容产生偏差。唯一增量跟踪的是尚未落盘的流式文本。
/// </remarks>
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
	private readonly StringBuilder _streamingText = new();
	private readonly StringBuilder _streamingThinking = new();
	private long _streamingTimestamp;
	private bool _isThinking;
	private bool _isStreaming;
	private bool _isCompacting;
	private string? _error;
	private long _runStarted;

	/// <summary>在产生变更的线程上触发；窗口负责把它转到 UI 线程。</summary>
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

	/// <summary>绑定一个会话，替换掉之前的会话。</summary>
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
			// 打开会话时的首次投影就从这里走：长会话整段映射要 200+ 毫秒，必须计时。
			RebuildTimed(session);
			_subscription = session.Subscribe(OnAgentEvent);
		}

		Changed?.Invoke();
	}

	/// <summary>解除绑定，但不释放会话。</summary>
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
		}

		Changed?.Invoke();
	}

	/// <summary>要渲染的行，不含系统提示。</summary>
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
				rows.Add(new ChatEntry
				{
					Role = ChatRole.Assistant,
					Text = _streamingText.ToString(),
					Thinking = _streamingThinking.ToString(),
					IsThinking = _isThinking,
					Timestamp = _streamingTimestamp,
					EntryId = $"live-assistant/{_streamingTimestamp}",
					IsStreaming = true,
				});
			}

			return _rowsSnapshot = rows.Count == 0 ? _committedRows : new JoinedRows(_committedRows, rows.ToArray());
		}
	}

	/// <summary>最后一条带文本的助手回复，供复制或导出。</summary>
	public string? LastAssistantText()
	{
		lock (_gate)
		{
			return _session?.Session.LastAssistantText();
		}
	}

	/// <summary>最后一条用户消息，供「重试」使用。</summary>
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

	/// <summary>状态栏文字：模型、思考级别、上下文占用与费用。</summary>
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

	/// <summary>通过会话发送一条指令，运行结束后返回。</summary>
	public async Task SendAsync(string text, CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			return;
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

	public void Abort() => Session?.Abort();

	/// <summary>运行中把一条消息插入当前回合，在下一次模型调用前生效。</summary>
	public bool Steer(string text, IReadOnlyList<ImageContent>? images = null) => Enqueue(text, images, steer: true);

	/// <summary>运行中把一条消息排到下一轮，当前回复结束后发送。</summary>
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
					break;

				case MessageUpdateEvent { AssistantMessageEvent: ThinkingStartEvent }:
					if (_streamingThinking.Length > 0)
					{
						_streamingThinking.Append("\n\n");
					}

					_isThinking = true;
					break;

				case MessageUpdateEvent { AssistantMessageEvent: ThinkingDeltaEvent thinking }:
					_streamingThinking.Append(thinking.Delta);
					_isThinking = true;
					break;

				case MessageUpdateEvent { AssistantMessageEvent: ThinkingEndEvent }:
					_isThinking = false;
					break;

				case MessageUpdateEvent { AssistantMessageEvent: ErrorEvent error }:
					_error = error.Error.ErrorMessage;
					_isStreaming = false;
					Rebuild();
					break;

				case MessageEndEvent ended:
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
					_tools.Add(new ToolEntry(start.ToolCallId, start.ToolName, start.Arguments.ToJsonString(PiJson.NodeOptions), null, false, null));
					break;

				case ToolExecutionEndEvent end:
					var arguments = _tools.FirstOrDefault(tool => tool.ToolCallId == end.ToolCallId)?.Arguments ?? string.Empty;
					_tools.RemoveAll(tool => tool.ToolCallId == end.ToolCallId);
					_tools.Add(new ToolEntry(
						end.ToolCallId,
						end.ToolName,
						arguments,
						ToolResultText(end.Result),
						end.IsError,
						end.DurationMs));
					break;

				case AutoRetryStartEvent retry:
					AppLog.For<ChatViewModel>().ZLogWarning($"Retry scheduled: session={_session?.Session.SessionId}, attempt={retry.Attempt}, delayMs={retry.DelayMs}");
					break;

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
		_streamingText.Clear();
		_streamingThinking.Clear();
		_streamingTimestamp = 0;
		_isThinking = false;
	}

	/// <summary>从会话投影重建已落盘的行。调用时必须持有锁。</summary>
	/// <summary>重建投影并计时：关闭诊断时只是多一个分支，不分配秒表。</summary>
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
		var turnChanges = new List<string>();
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
		var turnChanges = new List<string>();
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
		List<string> turnChanges)
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

					if (results.TryGetValue(call.Id, out var toolResult) && !toolResult.IsError && !turnChanges.Contains(changedPath, StringComparer.Ordinal))
					{
						turnChanges.Add(changedPath);
					}
				}

				if (text.Length > 0 || thinking.Length > 0 || images.Count > 0)
				{
					_entries.Add(new ChatEntry
					{
						Role = ChatRole.Assistant,
						Text = text,
						Thinking = thinking,
						DurationMs = assistant.DurationMs,
						EntryId = entryIds.GetValueOrDefault(message),
						Images = images,
						Timestamp = assistant.Timestamp,
						ChangedFiles = turnChanges.Count > 0 ? [.. turnChanges] : [],
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

	/// <summary>工具细节里的 diff 补丁。</summary>
	private static string? PatchOf(JsonNode? details) =>
		details is JsonObject detailsObject && detailsObject["patch"] is JsonValue patch &&
		patch.TryGetValue<string>(out var text) && text.Length > 0
			? text
			: null;

	/// <summary>消息内容里的文本（字符串或内容块两种形态）。</summary>
	private static string TextOf(MessageContent content) =>
		content.Blocks is { Count: > 0 } blocks
			? string.Concat(blocks.OfType<TextContent>().Select(static block => block.Text))
			: content.Text ?? string.Empty;

	/// <summary>消息内容里的图片。</summary>
	private static IReadOnlyList<ChatImage> ImagesOf(MessageContent content) =>
		content.Blocks is { Count: > 0 } blocks ? ImagesOfBlocks(blocks) : [];

	private static IReadOnlyList<ChatImage> ImagesOfBlocks(IReadOnlyList<ContentBlock> blocks) =>
		[.. blocks.OfType<ImageContent>().Select(static image => new ChatImage(image.Data, image.MimeType))];

	/// <summary>尚未并入聊天记录的工具调用。</summary>
	private sealed record ToolEntry(
		string ToolCallId,
		string ToolName,
		string Arguments,
		string? Result,
		bool IsError,
		double? DurationMs)
	{
		public ChatEntry ToEntry() => new()
		{
			Role = ChatRole.Tool,
			ToolName = ToolName,
			ToolCallId = ToolCallId,
			ToolArguments = Arguments.Length > 0 ? Arguments : null,
			IsStreaming = Result is null,
			EntryId = $"live-tool/{ToolCallId}",
			IsError = IsError,
			DurationMs = DurationMs,
			Text = Result is null ? $"→ {ToolName} {Arguments}" : $"→ {ToolName}\n{Result}",
		};
	}

	private static string ToolResultText(AgentToolResult result) =>
		string.Concat(result.Content.OfType<TextContent>().Select(static block => block.Text));
}
