using System.Globalization;
using System.Text;
using PiWeft.Ai;
using PiWeft.CodingAgent.Session;

namespace PiWeft.Desktop;

/// <summary>
/// 轨迹账本的泳道：一条会话在时间轴上同时有「用户输入 / 模型回复 / 工具执行 / 过程事件」四条线。
/// 与 PiDeck 的会话轨迹同一口径（输入 / 模型 / 工具 / 过程）。
/// </summary>
internal enum TrajectoryLane
{
	Input,
	Model,
	Tools,
	Process,
}

/// <summary>一条轨迹记录的种类。种类决定行首图标、文案与检查器的字段。</summary>
internal enum TrajectoryKind
{
	User,
	Assistant,
	Thinking,
	Tool,
	Error,
	SystemPrompt,
	ModelChange,
	ThinkingChange,
	Compaction,
	BranchSummary,
	Usage,
	Custom,
	SessionInfo,
	ContextEdit,
	InternalPrompt,
	Label,
	Unknown,
}

/// <summary>
/// 账本里的一行。只保存渲染与检查器需要的最小字段：正文/工具结果这类大对象保留源引用，
/// 摘要按前缀就地生成，不把整份历史复制进轨迹模型（长会话里工具输出动辄几十 KB）。
/// </summary>
internal sealed class TrajectoryRecord
{
	public required string Id { get; init; }

	public required TrajectoryKind Kind { get; init; }

	public required TrajectoryLane Lane { get; init; }

	public int TurnIndex { get; internal set; }

	public required string Summary { get; init; }

	/// <summary>记录开始时刻（Unix 毫秒）；点记录与区间记录共用这一列。</summary>
	public long StartedAt { get; internal set; }

	/// <summary>区间记录的结束时刻；<see langword="null"/> 表示仍在进行（或不伪造耗时）。</summary>
	public long? EndedAt { get; internal set; }

	/// <summary>实测耗时；没有实测时留空，时间列退回发生时刻，不写 0ms 冒充。</summary>
	public double? DurationMs { get; internal set; }

	/// <summary>工具状态（running / done / error）或助手停止原因；检查器按字符串原样显示。</summary>
	public string? Status { get; internal set; }

	public bool IsError { get; internal set; }

	public string? ToolName { get; init; }

	public string? ToolCallId { get; init; }

	/// <summary>自定义过程的类型（扩展写入的 customType）；工具记录留空。</summary>
	public string? CustomType { get; init; }

	public string? Provider { get; init; }

	public string? ModelId { get; init; }

	public string? ThinkingLevel { get; init; }

	public Usage? Usage { get; init; }

	/// <summary>压缩前 token 数（压缩过程记录）。</summary>
	public long? TokensBefore { get; init; }

	/// <summary>首条用户消息 = 会话的初始提示词。</summary>
	public bool IsInitialPrompt { get; init; }

	/// <summary>过程记录的补充说明（工具/压缩目标的短文本），不做全文。</summary>
	public string? Detail { get; init; }

	internal Message? Source { get; init; }

	internal ToolCall? Call { get; init; }

	internal ToolResultMessage? Result { get; set; }

	internal ChatEntry? Live { get; init; }

	public bool InFlight => EndedAt is null;
}

/// <summary>一轮：一条用户消息开启，后续的思考、回复、工具与过程事件都挂在它下面。</summary>
internal sealed record TrajectoryTurn(
	int Index,
	long StartedAt,
	long? EndedAt,
	bool InFlight,
	double? DurationMs,
	IReadOnlyList<TrajectoryRecord> Records)
{
	/// <summary>本轮所有助手消息的 token 合计（含只有思考/工具、没有正文的请求）。</summary>
	public long InputTokens { get; init; }

	public long OutputTokens { get; init; }

	public long ReasoningTokens { get; init; }

	/// <summary>本轮工具记录数（构建时一次算好，账本每次绑定轮头不再扫整轮）。</summary>
	public int ToolCalls { get; init; } = -1;

	/// <summary>本轮在会话分支里的起始条目下标：增量重建从这一轮开始，之前的轮可直接复用。</summary>
	public int SourceStartIndex { get; init; } = -1;

	/// <summary>本轮是否已有用户消息（复用轮时决定下一条用户消息是否算「初始提示」）。</summary>
	public bool HasUser { get; init; }

	/// <summary>本轮是否已有系统提示词（复用轮时决定新的提示词算初始还是更新）。</summary>
	public bool HasSystemPrompt { get; init; }
}

/// <summary>轨迹模型：轮 + 扁平记录 + 概览时间域（右端在有进行中记录时伸到 now）。</summary>
internal sealed record TrajectoryModel(
	IReadOnlyList<TrajectoryTurn> Turns,
	IReadOnlyList<TrajectoryRecord> Records,
	long DomainStart,
	long DomainEnd)
{
	public static TrajectoryModel Empty { get; } = new([], [], 0, 0);
}

/// <summary>
/// 轨迹页的数据源：当前分支的会话条目（事实）+ 聊天投影的流式尾行（还没落盘的部分）。
/// 会话文件是唯一权威；流式行只补「正在写的那一句」，落盘后自然消失。
/// </summary>
internal readonly record struct TrajectorySource(
	IReadOnlyList<SessionEntry> Entries,
	IReadOnlyList<ChatEntry> LiveRows,
	bool Busy,
	IReadOnlyDictionary<long, ChatThinkingTiming>? ThinkingTimings = null,
	long ThinkingTimingsRevision = 0);

/// <summary>
/// 从 pi 会话分支构建轨迹。规则与 PiDeck 的 buildTrajectory 对齐：
/// 用户消息开轮；思考/回复/工具归入当前轮；工具调用与结果按 callId 合并成一条记录；
/// 过程条目（模型切换、思考档位、压缩、扩展）按文件顺序落入当前轮；
/// 进行中的记录不伪造结束时间，时间列显示「进行中」。
/// </summary>
internal static class TrajectoryBuilder
{
	private const int SummaryLimit = 96;

	public static TrajectoryModel Build(
		IReadOnlyList<SessionEntry>? entries,
		IReadOnlyList<ChatEntry>? liveRows,
		long now,
		bool busy = false,
		IReadOnlyDictionary<long, ChatThinkingTiming>? thinkingTimings = null)
	{
		var draft = new Draft(busy, thinkingTimings: thinkingTimings);
		if (entries is not null)
		{
			for (var index = 0; index < entries.Count; index++)
			{
				draft.Add(entries[index], index);
			}
		}

		AddLiveRows(draft, liveRows);
		return draft.Complete(now);
	}

	/// <summary>
	/// 增量重建：复用 <paramref name="reuse"/> 里已收口的轮，只从 <paramref name="startIndex"/>
	/// 开始重建尾部（通常是最后一轮）。数据源是 append-only 的分支时，流式每秒的刷新从
	/// O(整份历史) 降到 O(最后一轮)。
	/// </summary>
	internal static TrajectoryModel BuildTail(
		IReadOnlyList<SessionEntry> entries,
		int startIndex,
		IReadOnlyList<ChatEntry>? liveRows,
		long now,
		bool busy,
		IReadOnlyList<TrajectoryTurn> reuse,
		IReadOnlyDictionary<long, ChatThinkingTiming>? thinkingTimings = null)
	{
		var draft = new Draft(busy, reuse, thinkingTimings);
		for (var index = Math.Max(0, startIndex); index < entries.Count; index++)
		{
			draft.Add(entries[index], index);
		}

		AddLiveRows(draft, liveRows);
		return draft.Complete(now);
	}

	private static void AddLiveRows(Draft draft, IReadOnlyList<ChatEntry>? liveRows)
	{
		if (liveRows is null)
		{
			return;
		}

		// 流式行永远在投影尾部：从后往前遇到第一条已落盘的行就停，空闲时是 O(1)。
		for (var index = liveRows.Count - 1; index >= 0; index--)
		{
			var row = liveRows[index];
			if (!row.IsStreaming)
			{
				break;
			}

			draft.AddLive(row);
		}
	}

	/// <summary>一次构建的可变状态；只在 UI 线程使用，不共享。</summary>
	private sealed class Draft
	{
		private readonly bool _busy;
		private readonly List<TrajectoryTurn> _turns = [];
		private readonly List<TrajectoryRecord> _current = [];
		private readonly List<TrajectoryRecord> _leading = [];
		private readonly Dictionary<string, TrajectoryRecord> _calls = new(StringComparer.Ordinal);
		private readonly IReadOnlyDictionary<long, ChatThinkingTiming>? _thinkingTimings;
		private long _turnStartedAt;
		private long _lastTime;
		private long _turnInputTokens;
		private long _turnOutputTokens;
		private long _turnReasoningTokens;
		private int _currentStartIndex = -1;
		private int _leadingStartIndex = -1;
		private int _entryIndex = -1;
		private readonly int _reusedTurnCount;
		private bool _sawUser;
		private bool _sawSystem;

		public Draft(
			bool busy,
			IReadOnlyList<TrajectoryTurn>? reuse = null,
			IReadOnlyDictionary<long, ChatThinkingTiming>? thinkingTimings = null)
		{
			_busy = busy;
			_thinkingTimings = thinkingTimings;
			if (reuse is null)
			{
				return;
			}

			_turns.AddRange(reuse);
			_reusedTurnCount = reuse.Count;
			foreach (var turn in reuse)
			{
				_sawUser |= turn.HasUser;
				_sawSystem |= turn.HasSystemPrompt;
			}
		}

		public void Add(SessionEntry entry, int entryIndex)
		{
			_entryIndex = entryIndex;
			var at = EntryTime(entry);
			if (at > 0)
			{
				_lastTime = at;
			}

			switch (entry)
			{
				case SessionMessageEntry message:
					AddMessage(message.Message, entry.Id, at);
					break;

				case ModelChangeEntry change:
					AddProcess(entry.Id, TrajectoryKind.ModelChange, at,
						$"{change.Provider}/{change.ModelId}".Trim('/'));
					break;

				case ThinkingLevelChangeEntry change:
					AddProcess(entry.Id, TrajectoryKind.ThinkingChange, at, change.ThinkingLevel);
					break;

				case CompactionEntry compaction:
					AddProcess(entry.Id, TrajectoryKind.Compaction, at,
						TrajectoryText.Summarize(compaction.Summary, SummaryLimit), tokensBefore: compaction.TokensBefore,
						detail: compaction.Summary);
					break;

				case BranchSummaryEntry branch:
					AddProcess(entry.Id, TrajectoryKind.BranchSummary, at,
						TrajectoryText.Summarize(branch.Summary, SummaryLimit), detail: branch.Summary);
					break;

				case UsageEntry usage:
					AddProcess(entry.Id, TrajectoryKind.Usage, at,
						usage.Kind.Length > 0
							? usage.Kind
							: $"{TrajectoryFormat.CompactTokens(usage.Usage.Input)}→{TrajectoryFormat.CompactTokens(usage.Usage.Output)}",
						usage: usage.Usage);
					break;

				case CustomEntry custom:
					AddProcess(entry.Id, TrajectoryKind.Custom, at,
						TrajectoryText.CustomSummary(custom.CustomType, custom.Data), customType: custom.CustomType,
						detail: custom.Data?.ToJsonString());
					break;

				case CustomMessageEntry custom:
					AddProcess(entry.Id, TrajectoryKind.Custom, at,
						TrajectoryText.ContentSummary(custom.Content, SummaryLimit), customType: custom.CustomType,
						detail: TrajectoryText.ContentText(custom.Content));
					break;

				case SessionInfoEntry info:
					AddProcess(entry.Id, TrajectoryKind.SessionInfo, at, info.Name ?? string.Empty);
					break;

				case ContextEditEntry edit:
					AddProcess(entry.Id, TrajectoryKind.ContextEdit, at, edit.TargetId);
					break;

				case LabelEntry label:
					AddProcess(entry.Id, TrajectoryKind.Label, at, label.Label ?? string.Empty);
					break;

				case UnknownSessionEntry unknown:
					AddProcess(entry.Id, TrajectoryKind.Unknown, at, unknown.Type);
					break;
			}
		}

		private void AddMessage(Message message, string entryId, long at)
		{
			var stamp = at > 0 ? at : message.Timestamp;
			switch (message)
			{
				case SystemMessage system:
					AddSystemPrompt(system, entryId, stamp);
					return;

				case UserMessage user:
					AddUser(user, entryId, stamp);
					return;

				case AssistantMessage assistant:
					AddAssistant(assistant, entryId, stamp);
					return;

				case ToolResultMessage result:
					AddToolResult(result, entryId, stamp);
					return;
			}
		}

		private void AddUser(UserMessage user, string entryId, long at)
		{
			var text = TrajectoryText.ContentText(user.Content);
			var images = TrajectoryText.ImageCount(user.Content);
			if (!user.Display)
			{
				// 扩展注入的内部上下文（目标模式 / 计划模式）参与模型上下文，但不是用户发起的一轮：
				// 作为过程记录保留，不拆轮，也不冒充「初始提示」。
				EnsureTurn(at, _entryIndex);
				_current.Add(new TrajectoryRecord
				{
					Id = $"internal:{entryId}",
					Kind = TrajectoryKind.InternalPrompt,
					Lane = TrajectoryLane.Process,
					Summary = TrajectoryText.Summarize(TrajectoryText.WithImages(text, images), SummaryLimit),
					StartedAt = at,
					EndedAt = at,
					CustomType = user.CustomType,
					Detail = user.CustomType,
					Source = user,
				});
				return;
			}

			StartTurn(at, _entryIndex);
			var initial = !_sawUser;
			_sawUser = true;
			_current.Add(new TrajectoryRecord
			{
				Id = $"user:{entryId}",
				Kind = TrajectoryKind.User,
				Lane = TrajectoryLane.Input,
				Summary = TrajectoryText.Summarize(TrajectoryText.WithImages(text, images), SummaryLimit),
				StartedAt = at,
				EndedAt = at,
				IsInitialPrompt = initial,
				Source = user,
			});
		}

		private void AddAssistant(AssistantMessage assistant, string entryId, long at)
		{
			EnsureTurn(at, _entryIndex);
			var pending = assistant.StopReason == StopReason.Pending;
			if (HasUsage(assistant.Usage))
			{
				// 用量按消息记账，不按可见行：只有思考/工具、没有正文的请求同样消耗 token，
				// 轮头的「输入→输出」不能只统计有正文的那几句。
				_turnInputTokens += assistant.Usage.Input;
				_turnOutputTokens += assistant.Usage.Output;
				_turnReasoningTokens += assistant.Usage.Reasoning ?? 0;
			}

			var thinking = TrajectoryText.ThinkingSummary(assistant.Content, SummaryLimit);
			if (thinking.Length > 0)
			{
				ChatThinkingTiming? timing = null;
				if (_thinkingTimings is not null &&
					_thinkingTimings.TryGetValue(assistant.Timestamp, out var recordedTiming))
				{
					timing = recordedTiming;
				}

				// pi 的会话文件不保存思考段起止；当前进程从 thinking_start/end 事件补，
				// 历史重开没有就退回发生时刻，不把整条回复的耗时复制到思考行上。
				var thinkingStart = timing?.StartedAt > 0 ? timing.Value.StartedAt : at;
				long? thinkingEnd = timing?.EndedAt;
				if (thinkingEnd is null && !pending)
				{
					thinkingEnd = at;
				}

				_current.Add(new TrajectoryRecord
				{
					Id = $"thinking:{entryId}",
					Kind = TrajectoryKind.Thinking,
					Lane = TrajectoryLane.Model,
					Summary = thinking,
					StartedAt = thinkingStart,
					EndedAt = thinkingEnd,
					DurationMs = timing is { } span && span.EndedAt >= span.StartedAt
						? span.EndedAt - span.StartedAt
						: null,
					Source = assistant,
				});
			}

			var text = TrajectoryText.BlocksSummary(assistant.Content, SummaryLimit);
			var images = TrajectoryText.ImageCount(assistant.Content);
			if (text.Length > 0 || images > 0)
			{
				var duration = assistant.DurationMs;
				long? ended = null;
				double? measured = null;
				if (!pending)
				{
					if (duration is > 0)
					{
						ended = at + (long)duration.Value;
						measured = duration;
					}
					else
					{
						ended = at;
					}
				}

				_current.Add(new TrajectoryRecord
				{
					Id = $"assistant:{entryId}",
					Kind = TrajectoryKind.Assistant,
					Lane = TrajectoryLane.Model,
					Summary = TrajectoryText.Summarize(TrajectoryText.WithImages(text, images), SummaryLimit),
					StartedAt = at,
					EndedAt = ended,
					DurationMs = measured,
					Status = assistant.StopReason.ToWire(),
					Provider = assistant.Provider,
					ModelId = assistant.Model,
					ThinkingLevel = assistant.ThinkingLevel,
					Usage = HasUsage(assistant.Usage) ? assistant.Usage : null,
					Source = assistant,
				});
			}

			var index = 0;
			foreach (var block in assistant.Content)
			{
				if (block is not ToolCall call)
				{
					continue;
				}

				var startedAt = at + (long)(assistant.DurationMs ?? 0);
				if (startedAt <= 0)
				{
					startedAt = at;
				}

				var record = new TrajectoryRecord
				{
					Id = $"tool:{entryId}:{call.Id}:{index}",
					Kind = TrajectoryKind.Tool,
					Lane = TrajectoryLane.Tools,
					Summary = TrajectoryText.ToolSummary(call),
					StartedAt = startedAt,
					EndedAt = null,
					Status = "running",
					ToolName = call.Name,
					ToolCallId = call.Id,
					Call = call,
					Source = assistant,
				};
				index++;
				_current.Add(record);
				if (call.Id.Length > 0)
				{
					_calls[call.Id] = record;
				}
			}

			if (assistant.StopReason == StopReason.Error && assistant.ErrorMessage is { Length: > 0 } error)
			{
				_current.Add(new TrajectoryRecord
				{
					Id = $"error:{entryId}",
					Kind = TrajectoryKind.Error,
					Lane = TrajectoryLane.Model,
					Summary = TrajectoryText.Summarize(error, SummaryLimit),
					StartedAt = at,
					EndedAt = at,
					IsError = true,
					Detail = error,
					Source = assistant,
				});
			}
		}

		private void AddToolResult(ToolResultMessage result, string entryId, long at)
		{
			if (result.ToolCallId.Length > 0 && _calls.TryGetValue(result.ToolCallId, out var tool))
			{
				tool.Result = result;
				tool.EndedAt = at;
				tool.DurationMs = result.DurationMs;
				tool.Status = result.IsError ? "error" : "done";
				tool.IsError = result.IsError;
				// 工具的真实起止是「结果时刻 − 实测耗时」：用助手消息时间推算会把整段生成时间
				// 都算进工具（模型写完正文才执行工具），长回复的工具条会被拉成几十秒。
				if (result.DurationMs is > 0 && at > 0)
				{
					tool.StartedAt = at - (long)result.DurationMs.Value;
				}

				return;
			}

			// 没有对应调用（旧的导入会话只留结果）：仍然给一条证据，不把结果吞掉。
			EnsureTurn(at, _entryIndex);
			var started = result.DurationMs is > 0 && at > 0 ? at - (long)result.DurationMs.Value : at;
			_current.Add(new TrajectoryRecord
			{
				Id = $"tool-result:{entryId}",
				Kind = TrajectoryKind.Tool,
				Lane = TrajectoryLane.Tools,
				Summary = TrajectoryText.Summarize(TrajectoryText.BlocksText(result.Content), SummaryLimit),
				StartedAt = started,
				EndedAt = at,
				DurationMs = result.DurationMs,
				Status = result.IsError ? "error" : "done",
				IsError = result.IsError,
				ToolName = result.ToolName.Length > 0 ? result.ToolName : null,
				ToolCallId = result.ToolCallId,
				Result = result,
			});
		}

		private void AddSystemPrompt(SystemMessage system, string entryId, long at)
		{
			var initial = !_sawSystem;
			_sawSystem = true;
			AddProcess(entryId, TrajectoryKind.SystemPrompt, at,
				TrajectoryText.SystemPromptSummary(system, SummaryLimit),
				detail: initial ? "initial" : "update", source: system);
		}

		private void AddProcess(string entryId, TrajectoryKind kind, long at, string summary,
			string? customType = null, string? detail = null, Usage? usage = null, long? tokensBefore = null,
			Message? source = null)
		{
			var stamp = at > 0 ? at : _lastTime;
			var record = new TrajectoryRecord
			{
				Id = $"process:{entryId}",
				Kind = kind,
				Lane = TrajectoryLane.Process,
				Summary = summary,
				StartedAt = stamp,
				// 过程条目是时间点（模型切换、压缩、扩展调用），没有可测区间。
				EndedAt = stamp,
				CustomType = customType,
				Detail = detail,
				Usage = usage,
				TokensBefore = tokensBefore,
				Source = source,
			};
			if (_current.Count > 0)
			{
				if (_currentStartIndex < 0 && _entryIndex >= 0)
				{
					_currentStartIndex = _entryIndex;
				}

				_current.Add(record);
			}
			else
			{
				if (_leadingStartIndex < 0 && _entryIndex >= 0)
				{
					_leadingStartIndex = _entryIndex;
				}

				// 会话开头的模型切换/系统提示先压着：首条用户消息一到就并进第 1 轮，
				// 避免账本漂出「第 0 轮」这种会话文件里不存在的分组。
				_leading.Add(record);
			}
		}

		public void AddLive(ChatEntry row)
		{
			// 只补流式那一行：已落盘的历史由会话条目负责，ChatViewModel 投影里重复的行不再算一遍。
			if (!row.IsStreaming || row.Role != ChatRole.Assistant)
			{
				return;
			}

			var at = row.Timestamp;
			EnsureTurn(at, -1);
			// 落盘与流式行可能同时存在一帧：同时间戳的助手/思考记录已经在了就不再补。
			if (_current.Any(record => (record.Kind is TrajectoryKind.Assistant or TrajectoryKind.Thinking) &&
				record.StartedAt == at))
			{
				return;
			}

			if (row.ThinkingLength > 0)
			{
				var thinkingStart = row.ThinkingStartedAt ?? at;
				var thinkingEnd = row.ThinkingEndedAt ?? (row.IsThinking ? (long?)null : at);
				_current.Add(new TrajectoryRecord
				{
					Id = $"live:{at}:thinking",
					Kind = TrajectoryKind.Thinking,
					Lane = TrajectoryLane.Model,
					Summary = TrajectoryText.Summarize(row.ThinkingSource, SummaryLimit),
					StartedAt = thinkingStart,
					EndedAt = thinkingEnd,
					DurationMs = row.ThinkingStartedAt is { } startedAt && row.ThinkingEndedAt is { } endedAt &&
						endedAt >= startedAt
						? endedAt - startedAt
						: null,
					Live = row,
				});
			}

			if (row.TextLength > 0 || row.Images.Count > 0)
			{
				_current.Add(new TrajectoryRecord
				{
					Id = $"live:{at}:assistant",
					Kind = TrajectoryKind.Assistant,
					Lane = TrajectoryLane.Model,
					Summary = TrajectoryText.WithImages(
						TrajectoryText.Summarize(row.BodyText, SummaryLimit),
						row.Images.Count),
					StartedAt = at,
					EndedAt = null,
					Status = "pending",
					Live = row,
				});
			}
		}

		private void StartTurn(long at, int entryIndex)
		{
			// 会话开头的系统提示 / 内部注入先并进这一轮：它们不是独立的一轮，
			// 只有已经出现助手/思考/工具/错误记录时才说明那是一段真实的先行回合。
			if (_current.Count > 0 && !HasWorkRecords(_current))
			{
				if (_currentStartIndex >= 0 && (_leadingStartIndex < 0 || _currentStartIndex < _leadingStartIndex))
				{
					_leadingStartIndex = _currentStartIndex;
				}

				_leading.InsertRange(0, _current);
				_current.Clear();
				_currentStartIndex = -1;
			}

			Flush();
			if (_leading.Count > 0)
			{
				_current.AddRange(_leading);
				_leading.Clear();
				_currentStartIndex = _leadingStartIndex;
				_leadingStartIndex = -1;
			}
			else
			{
				_currentStartIndex = entryIndex;
			}

			_turnStartedAt = at > 0 ? at : _lastTime;
			_turnInputTokens = 0;
			_turnOutputTokens = 0;
			_turnReasoningTokens = 0;
		}

		private void EnsureTurn(long at, int entryIndex)
		{
			if (_current.Count > 0)
			{
				if (_currentStartIndex < 0 && entryIndex >= 0)
				{
					_currentStartIndex = entryIndex;
				}

				return;
			}

			if (_leading.Count > 0)
			{
				_current.AddRange(_leading);
				_leading.Clear();
				_currentStartIndex = _leadingStartIndex;
				_leadingStartIndex = -1;
			}
			else
			{
				_currentStartIndex = entryIndex;
			}

			_turnStartedAt = at > 0 ? at : _lastTime;
			_turnInputTokens = 0;
			_turnOutputTokens = 0;
			_turnReasoningTokens = 0;
		}

		private static bool HasWorkRecords(List<TrajectoryRecord> records)
		{
			foreach (var record in records)
			{
				// 纯思考回复、错误也是真实的一轮输出；只有系统提示 / 过程注入才应并入下一轮。
				if (record.Kind is TrajectoryKind.User or TrajectoryKind.Assistant or TrajectoryKind.Thinking
					or TrajectoryKind.Tool or TrajectoryKind.Error)
				{
					return true;
				}
			}

			return false;
		}

		/// <summary>
		/// 收口后再算一轮的结束与耗时：工具结果晚于下一个用户消息到达时，把它记回上一轮；
		/// 会话空闲时仍挂着无结果工具（进程退出/中断）统一收成「已中断」，不永久显示进行中。
		/// </summary>
		private void FinalizeTurns()
		{
			// 会话仍在跑时，复用轮已经收口，不再逐条扫描；只有本轮新建的轮需要收口。
			// 转为空闲时再补一次全量收口（把中断留下的无结果工具标成「已中断」）。
			var start = _busy ? _reusedTurnCount : 0;
			for (var index = start; index < _turns.Count; index++)
			{
				_turns[index] = FinalizeTurn(_turns[index]);
			}
		}

		private TrajectoryTurn FinalizeTurn(TrajectoryTurn turn)
		{
			if (!_busy)
			{
				foreach (var record in turn.Records)
				{
					if (record.EndedAt is not null)
					{
						continue;
					}

					record.EndedAt = record.StartedAt > 0 ? record.StartedAt : turn.StartedAt;
					record.DurationMs = null;
					if (record.Status is "running" or "pending")
					{
						record.Status = "stopped";
					}
				}
			}

			var inFlight = false;
			var end = 0L;
			foreach (var record in turn.Records)
			{
				if (record.EndedAt is { } endedAt)
				{
					end = Math.Max(end, endedAt);
				}
				else
				{
					inFlight = true;
				}
			}

			var duration = !inFlight && turn.StartedAt > 0 && end >= turn.StartedAt
				? end - turn.StartedAt
				: (double?)null;
			return turn with { EndedAt = inFlight ? null : end, InFlight = inFlight, DurationMs = duration };
		}

		private void Flush()
		{
			if (_current.Count == 0)
			{
				return;
			}

			var index = _turns.Count;
			var toolCalls = 0;
			var inFlight = false;
			var hasUser = false;
			var hasSystem = false;
			var userStart = 0L;
			var minStart = long.MaxValue;
			var end = 0L;
			foreach (var record in _current)
			{
				record.TurnIndex = index;
				if (record.Kind == TrajectoryKind.Tool)
				{
					toolCalls++;
				}

				if (record.Kind == TrajectoryKind.User)
				{
					hasUser = true;
				}
				else if (record.Kind == TrajectoryKind.SystemPrompt)
				{
					hasSystem = true;
				}

				if (record.Kind == TrajectoryKind.User && record.StartedAt > 0)
				{
					userStart = record.StartedAt;
				}

				if (record.StartedAt > 0)
				{
					minStart = Math.Min(minStart, record.StartedAt);
				}

				if (record.EndedAt is { } endedAt)
				{
					end = Math.Max(end, endedAt);
				}
				else
				{
					inFlight = true;
				}
			}

			// 轮从「用户的提问」开始：会话开头的模型切换/系统提示比首条用户消息早，
			// 但它们属于同一轮的准备阶段，不能把轮起点拉到几小时前。
			var start = userStart > 0 ? userStart
				: _turnStartedAt > 0 ? _turnStartedAt
				: minStart == long.MaxValue ? 0 : minStart;
			var duration = !inFlight && start > 0 && end >= start ? end - start : (double?)null;
			_turns.Add(new TrajectoryTurn(index, start, inFlight ? null : end, inFlight, duration, [.. _current])
			{
				InputTokens = _turnInputTokens,
				OutputTokens = _turnOutputTokens,
				ReasoningTokens = _turnReasoningTokens,
				ToolCalls = toolCalls,
				SourceStartIndex = _currentStartIndex,
				HasUser = hasUser,
				HasSystemPrompt = hasSystem,
			});
			_current.Clear();
			_currentStartIndex = -1;
			_turnStartedAt = 0;
			_turnInputTokens = 0;
			_turnOutputTokens = 0;
			_turnReasoningTokens = 0;
		}

		public TrajectoryModel Complete(long now)
		{
			Flush();
			if (_current.Count > 0)
			{
				Flush();
			}

			if (_leading.Count > 0)
			{
				// 正常情况下 leading 只在首条用户消息前出现；这里同时兜住尾部的纯过程条目，
				// 不让它们因为「没有 current」而消失。
				_current.AddRange(_leading);
				_leading.Clear();
				_currentStartIndex = _leadingStartIndex;
				_leadingStartIndex = -1;
				Flush();
			}

			FinalizeTurns();
			var total = 0;
			foreach (var turn in _turns)
			{
				total += turn.Records.Count;
			}

			var records = new List<TrajectoryRecord>(total);
			var anyInFlight = false;
			long min = long.MaxValue;
			long max = long.MinValue;
			foreach (var turn in _turns)
			{
				foreach (var record in turn.Records)
				{
					records.Add(record);
					if (record.StartedAt > 0)
					{
						min = Math.Min(min, record.StartedAt);
					}

					if (record.EndedAt is { } ended)
					{
						max = Math.Max(max, ended);
					}
					else
					{
						anyInFlight = true;
					}
				}
			}

			if (min == long.MaxValue)
			{
				min = now;
			}

			if (max == long.MinValue)
			{
				max = now;
			}

			// 进行中的记录把概览右端伸到 now：时间线的开区间看得见，账本自身仍不写时长。
			var domainEnd = anyInFlight ? Math.Max(max, now) : max;
			return new TrajectoryModel(_turns, records, min, domainEnd);
		}
	}

	private static long EntryTime(SessionEntry entry)
	{
		// 消息条目自己的 Timestamp 才是「请求开始」（assistant）或「结果时刻」（toolResult）；
		// 条目 Timestamp 是落盘时刻，拿它算耗时会把整段回复错算到工具上。
		if (entry is SessionMessageEntry { Message.Timestamp: > 0 } message)
		{
			return message.Message.Timestamp;
		}

		if (DateTimeOffset.TryParse(entry.Timestamp, CultureInfo.InvariantCulture,
			DateTimeStyles.RoundtripKind, out var parsed))
		{
			return parsed.ToUnixTimeMilliseconds();
		}

		return 0;
	}

	private static bool HasUsage(Usage usage) =>
		usage.Input > 0 || usage.Output > 0 || usage.CacheRead > 0 || usage.CacheWrite > 0 || usage.Reasoning > 0;
}

/// <summary>
/// 会话轨迹的增量投影：分支是 append-only 的，每秒流式刷新只需要重建最后一轮，
/// 之前的轮（及其摘要、工具结果、用量）直接复用。分支切换 / 重写时回退到全量构建。
/// </summary>
internal sealed class TrajectoryProjector
{
	private int _entryCount;
	private SessionEntry? _lastEntry;
	private TrajectoryModel _model = TrajectoryModel.Empty;
	private bool _primed;

	public TrajectoryModel Project(
		IReadOnlyList<SessionEntry> entries,
		IReadOnlyList<ChatEntry>? liveRows,
		long now,
		bool busy,
		IReadOnlyDictionary<long, ChatThinkingTiming>? thinkingTimings = null)
	{
		if (!_primed)
		{
			return Reset(entries, liveRows, now, busy, thinkingTimings);
		}

		// 只认「在老快照后面追加」：条目对象是 ImmutableList 里的同一批引用；
		// 分支切换 / 会话重载会换掉前缀，直接全量最稳。
		var appendOnly = entries.Count >= _entryCount &&
			(_entryCount == 0 || (_lastEntry is not null && ReferenceEquals(entries[_entryCount - 1], _lastEntry)));
		if (!appendOnly)
		{
			return Reset(entries, liveRows, now, busy, thinkingTimings);
		}

		var lastTurn = _model.Turns.Count > 0 ? _model.Turns[^1] : null;
		var startIndex = lastTurn?.SourceStartIndex ?? -1;
		if (startIndex < 0 || startIndex >= entries.Count)
		{
			return Reset(entries, liveRows, now, busy, thinkingTimings);
		}

		// 结果落在更早的轮（工具调用不在最后一轮）时，尾重建会把结果错记成孤立工具：
		// 这种少见情况回退全量，保证工具调用与结果仍然合并。
		for (var index = _entryCount; index < entries.Count; index++)
		{
			if (entries[index] is SessionMessageEntry { Message: ToolResultMessage { ToolCallId.Length: > 0 } result } &&
				!ContainsToolCall(lastTurn, result.ToolCallId))
			{
				return Reset(entries, liveRows, now, busy, thinkingTimings);
			}
		}

		var reuseCount = _model.Turns.Count - 1;
		var reuse = new TrajectoryTurn[reuseCount];
		for (var index = 0; index < reuseCount; index++)
		{
			reuse[index] = _model.Turns[index];
		}

		_model = TrajectoryBuilder.BuildTail(entries, startIndex, liveRows, now, busy, reuse, thinkingTimings);
		_entryCount = entries.Count;
		_lastEntry = entries.Count > 0 ? entries[^1] : null;
		return _model;
	}

	private TrajectoryModel Reset(
		IReadOnlyList<SessionEntry> entries,
		IReadOnlyList<ChatEntry>? liveRows,
		long now,
		bool busy,
		IReadOnlyDictionary<long, ChatThinkingTiming>? thinkingTimings)
	{
		_model = TrajectoryBuilder.Build(entries, liveRows, now, busy, thinkingTimings);
		_entryCount = entries.Count;
		_lastEntry = entries.Count > 0 ? entries[^1] : null;
		_primed = true;
		return _model;
	}

	/// <summary>丢弃增量前缀；下一轮 Project 会做一次全量构建（条目没变、只补到思考耗时时用）。</summary>
	public void Invalidate()
	{
		_primed = false;
	}

	private static bool ContainsToolCall(TrajectoryTurn? turn, string callId)
	{
		if (turn is null)
		{
			return false;
		}

		foreach (var record in turn.Records)
		{
			if (string.Equals(record.ToolCallId, callId, StringComparison.Ordinal))
			{
				return true;
			}
		}

		return false;
	}
}

/// <summary>轨迹账本的一行：单条记录，或被折叠的一段连续工具调用。</summary>
internal abstract record TrajectoryLedgerItem(string Key)
{
	public sealed record Record(TrajectoryRecord Value) : TrajectoryLedgerItem(Value.Id);

	public sealed record Group(IReadOnlyList<TrajectoryRecord> Records) : TrajectoryLedgerItem($"group:{Records[0].Id}");
}

/// <summary>轮头摘要：步数、工具数与本轮 token 用量。</summary>
internal readonly record struct TrajectoryTurnSummary(
	int Steps,
	int ToolCalls,
	long InputTokens,
	long OutputTokens,
	long ReasoningTokens,
	double? DurationMs);

/// <summary>轨迹的纯函数：轮摘要、连续工具折叠、渲染窗口与格式。视图与单测共用这一份。</summary>
internal static class TrajectoryLedger
{
	/// <summary>连续工具超过这个条数就压成一行；与 PiDeck 的阈值一致。</summary>
	public const int ToolGroupLimit = 8;

	/// <summary>初始渲染的尾部轮数；每次「加载更早记录」再加同样多。</summary>
	public const int TurnWindowStep = 3;

	public static TrajectoryTurnSummary Summarize(TrajectoryTurn turn)
	{
		var tools = turn.ToolCalls >= 0 ? turn.ToolCalls : CountTools(turn.Records);
		long input = turn.InputTokens;
		long output = turn.OutputTokens;
		long reasoning = turn.ReasoningTokens;
		if (input == 0 && output == 0 && reasoning == 0)
		{
			// 直接构造 TrajectoryTurn 的调用方（测试/外部投影）没有轮级合计时退回记录求和。
			foreach (var record in turn.Records)
			{
				if (record.Usage is { } usage)
				{
					input += usage.Input;
					output += usage.Output;
					reasoning += usage.Reasoning ?? 0;
				}
			}
		}

		return new TrajectoryTurnSummary(turn.Records.Count, tools, input, output, reasoning, turn.DurationMs);
	}

	private static int CountTools(IReadOnlyList<TrajectoryRecord> records)
	{
		var count = 0;
		foreach (var record in records)
		{
			if (record.Kind == TrajectoryKind.Tool)
			{
				count++;
			}
		}

		return count;
	}

	/// <summary>把连续工具调用折叠成组；未超过阈值时原样输出（不为折叠而折叠）。</summary>
	public static IReadOnlyList<TrajectoryLedgerItem> Fold(IReadOnlyList<TrajectoryRecord> records, int limit = ToolGroupLimit)
	{
		var items = new List<TrajectoryLedgerItem>();
		var run = new List<TrajectoryRecord>();

		void Flush()
		{
			if (run.Count == 0)
			{
				return;
			}

			if (run.Count > limit)
			{
				items.Add(new TrajectoryLedgerItem.Group([.. run]));
			}
			else
			{
				foreach (var record in run)
				{
					items.Add(new TrajectoryLedgerItem.Record(record));
				}
			}

			run.Clear();
		}

		foreach (var record in records)
		{
			if (record.Kind == TrajectoryKind.Tool)
			{
				run.Add(record);
				continue;
			}

			Flush();
			items.Add(new TrajectoryLedgerItem.Record(record));
		}

		Flush();
		return items;
	}

	/// <summary>折叠后仍露出的记录：本轮第一条用户消息，折叠行不能只剩计数。</summary>
	public static TrajectoryRecord? Peek(IReadOnlyList<TrajectoryRecord> records) =>
		records.FirstOrDefault(static record => record.Kind == TrajectoryKind.User);

	/// <summary>默认折叠除最后一轮外的所有轮：打开轨迹时最关心「当前这轮在干什么」。</summary>
	public static HashSet<int> DefaultFoldedTurns(IReadOnlyList<TrajectoryTurn> turns) =>
		[.. turns.Take(Math.Max(0, turns.Count - 1)).Select(static turn => turn.Index)];

	/// <summary>取尾部 <paramref name="window"/> 轮（按轮计数，不按记录数）。</summary>
	public static IReadOnlyList<TrajectoryTurn> Window(IReadOnlyList<TrajectoryTurn> turns, int window)
	{
		var count = Math.Clamp(window, 1, Math.Max(1, turns.Count));
		if (turns.Count == 0)
		{
			return [];
		}

		var start = Math.Max(0, turns.Count - count);
		var result = new List<TrajectoryTurn>(turns.Count - start);
		for (var index = start; index < turns.Count; index++)
		{
			result.Add(turns[index]);
		}

		return result;
	}
}

/// <summary>轨迹页的数字/时间格式：与 PiDeck 的账本列同一口径。</summary>
internal static class TrajectoryFormat
{
	/// <summary>紧凑 token：1000 → 1.0k，10000 → 12k（窄账本列放不下完整数字）。</summary>
	public static string CompactTokens(long value)
	{
		if (value >= 10_000)
		{
			return $"{Math.Round(value / 1000.0, MidpointRounding.AwayFromZero):0}k";
		}

		if (value >= 1_000)
		{
			return $"{value / 1000.0:0.0}k";
		}

		return value.ToString(CultureInfo.InvariantCulture);
	}

	/// <summary>耗时：&lt;1s 用 ms，&lt;60s 用一位小数的秒，再往上保留 h/m/s 档位。</summary>
	public static string Duration(double milliseconds)
	{
		if (!double.IsFinite(milliseconds) || milliseconds < 0)
		{
			milliseconds = 0;
		}

		if (milliseconds < 1000)
		{
			return $"{milliseconds:0}ms";
		}

		var totalSeconds = (long)Math.Floor(milliseconds / 1000);
		if (totalSeconds < 60)
		{
			return $"{totalSeconds}.{(long)(milliseconds % 1000 / 100)}s";
		}

		var hours = totalSeconds / 3600;
		var minutes = totalSeconds % 3600 / 60;
		var seconds = totalSeconds % 60;
		var text = new StringBuilder(8);
		if (hours > 0)
		{
			text.Append(hours).Append('h');
		}

		if (minutes > 0)
		{
			text.Append(minutes).Append('m');
		}

		if (seconds > 0)
		{
			text.Append(seconds).Append('s');
		}

		return text.Length > 0 ? text.ToString() : "0ms";
	}

	/// <summary>点记录的时间列：本地时钟。</summary>
	public static string Clock(long unixMs) => unixMs > 0
		? DateTimeOffset.FromUnixTimeMilliseconds(unixMs).ToLocalTime()
			.ToString("HH:mm:ss", CultureInfo.InvariantCulture)
		: "—";

	/// <summary>检查器里的完整时间：带上日期。</summary>
	public static string Time(long unixMs) => unixMs > 0
		? DateTimeOffset.FromUnixTimeMilliseconds(unixMs).ToLocalTime()
			.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
		: "—";
}

/// <summary>从 pi 消息/内容块里提取轨迹需要的文本，摘要按前缀生成，不复制整份历史。</summary>
internal static class TrajectoryText
{
	public const int SummaryLimit = 96;

	/// <summary>把多行文本压成一行摘要并截断；不物化整篇正文。</summary>
	public static string Summarize(string? text, int limit = SummaryLimit)
	{
		if (string.IsNullOrEmpty(text))
		{
			return string.Empty;
		}

		var builder = new StringBuilder(Math.Min(text.Length, limit + 1));
		var whitespace = false;
		foreach (var character in text)
		{
			if (char.IsWhiteSpace(character))
			{
				whitespace = builder.Length > 0;
				continue;
			}

			if (whitespace && builder.Length > 0)
			{
				builder.Append(' ');
			}

			whitespace = false;
			builder.Append(character);
			if (builder.Length >= limit)
			{
				return FinishSummary(builder, limit);
			}
		}

		return builder.ToString();
	}

	public static string BlocksText(IReadOnlyList<ContentBlock>? blocks)
	{
		if (blocks is null || blocks.Count == 0)
		{
			return string.Empty;
		}

		var builder = new StringBuilder();
		foreach (var block in blocks)
		{
			if (block is TextContent text)
			{
				builder.Append(text.Text);
			}
		}

		return builder.ToString();
	}

	public static string BlocksSummary(IReadOnlyList<ContentBlock>? blocks, int limit = SummaryLimit)
	{
		if (blocks is null || blocks.Count == 0)
		{
			return string.Empty;
		}

		StringBuilder? builder = null;
		foreach (var block in blocks)
		{
			if (block is not TextContent text || text.Text.Length == 0)
			{
				continue;
			}

			// Summarize 只在凑满 limit 个可见字符时停下来，不会复制整篇正文；
			// 这样「大量换行」的正文也不会因为前缀太短而摘要偏短。
			var space = builder is { Length: > 0 } ? 1 : 0;
			var remaining = limit - (builder?.Length ?? 0) - space;
			if (remaining <= 0)
			{
				break;
			}

			var summary = Summarize(text.Text, remaining);
			if (builder is null)
			{
				if (summary.Length >= limit)
				{
					return summary;
				}

				builder = new StringBuilder(limit + 1);
				builder.Append(summary);
			}
			else
			{
				if (summary.Length > 0 && space > 0)
				{
					builder.Append(' ');
				}

				builder.Append(summary);
			}

			if (builder.Length >= limit)
			{
				if (builder.Length > limit)
				{
					builder.Length = limit;
				}

				break;
			}
		}

		return builder?.ToString() ?? string.Empty;
	}

	public static string ThinkingSummary(IReadOnlyList<ContentBlock>? blocks, int limit = SummaryLimit)
	{
		if (blocks is null || blocks.Count == 0)
		{
			return string.Empty;
		}

		StringBuilder? builder = null;
		foreach (var block in blocks)
		{
			if (block is not ThinkingContent thinking || thinking.Thinking.Length == 0)
			{
				continue;
			}

			var space = builder is { Length: > 0 } ? 1 : 0;
			var remaining = limit - (builder?.Length ?? 0) - space;
			if (remaining <= 0)
			{
				break;
			}

			var summary = Summarize(thinking.Thinking, remaining);
			if (builder is null)
			{
				if (summary.Length >= limit)
				{
					return summary;
				}

				builder = new StringBuilder(limit + 1);
				builder.Append(summary);
			}
			else
			{
				if (summary.Length > 0 && space > 0)
				{
					builder.Append(' ');
				}

				builder.Append(summary);
			}

			if (builder.Length >= limit)
			{
				if (builder.Length > limit)
				{
					builder.Length = limit;
				}

				break;
			}
		}

		return builder?.ToString() ?? string.Empty;
	}

	/// <summary>流式文本来源的摘要：直接按快照读前缀，不在每帧切出临时字符串。</summary>
	public static string Summarize(StreamText text, int limit = SummaryLimit)
	{
		if (text.Length == 0)
		{
			return string.Empty;
		}

		var builder = new StringBuilder(limit + 1);
		var whitespace = false;
		for (var index = 0; index < text.Length; index++)
		{
			var character = text.At(index);
			if (char.IsWhiteSpace(character))
			{
				whitespace = builder.Length > 0;
				continue;
			}

			if (whitespace && builder.Length > 0)
			{
				builder.Append(' ');
			}

			whitespace = false;
			builder.Append(character);
			if (builder.Length >= limit)
			{
				return FinishSummary(builder, limit);
			}
		}

		return builder.ToString();
	}

	/// <summary>截断到 limit 并补省略号；不把代理对切成半截（Emoji 不会变成替换字符）。</summary>
	private static string FinishSummary(StringBuilder builder, int limit)
	{
		builder.Length = Math.Max(0, limit - 1);
		if (builder.Length >= 2 &&
			char.IsHighSurrogate(builder[builder.Length - 2]) && char.IsLowSurrogate(builder[builder.Length - 1]))
		{
			builder.Length -= 2;
		}
		else if (builder.Length > 0 && char.IsHighSurrogate(builder[builder.Length - 1]))
		{
			builder.Length--;
		}

		builder.Append('…');
		return builder.ToString();
	}

	private static void AppendPrefix(StringBuilder builder, string text, int maxLength)
	{
		if (builder.Length >= maxLength || text.Length == 0)
		{
			return;
		}

		builder.Append(text.AsSpan(0, Math.Min(text.Length, maxLength - builder.Length)));
	}

	public static string ThinkingText(IReadOnlyList<ContentBlock>? blocks)
	{
		if (blocks is null || blocks.Count == 0)
		{
			return string.Empty;
		}

		var builder = new StringBuilder();
		foreach (var block in blocks)
		{
			if (block is ThinkingContent thinking)
			{
				builder.Append(thinking.Thinking);
			}
		}

		return builder.ToString();
	}

	public static string ContentText(MessageContent content) => content.IsText
		? content.Text ?? string.Empty
		: BlocksText(content.Blocks);

	public static string ContentSummary(MessageContent content, int limit = SummaryLimit) => content.IsText
		? Summarize(content.Text, limit)
		: BlocksSummary(content.Blocks, limit);

	public static int ImageCount(MessageContent content) => CountImages(content.Blocks);

	public static int ImageCount(IReadOnlyList<ContentBlock>? blocks) => CountImages(blocks);

	private static int CountImages(IReadOnlyList<ContentBlock>? blocks) => blocks?.Count(static block => block is ImageContent) ?? 0;

	/// <summary>纯图片消息也要在账本里读得懂：没有正文时用「[n 张图片]」占位。</summary>
	public static string WithImages(string text, int imageCount)
	{
		if (imageCount <= 0)
		{
			return text;
		}

		return text.Length > 0
			? $"{text} · {Strings.TrajectoryImages(imageCount)}"
			: Strings.TrajectoryImages(imageCount);
	}

	/// <summary>工具行摘要：命令取 command、文件工具取路径，其它退回参数 JSON 前缀。</summary>
	public static string ToolSummary(ToolCall call)
	{
		var arguments = call.Arguments;
		var detail = call.Name switch
		{
			"bash" or "terminal" => JsonString(arguments, "command"),
			"read" or "write" or "edit" => JsonString(arguments, "path", "file_path", "filePath"),
			"grep" or "find" or "ls" => JsonString(arguments, "pattern", "path"),
			_ => null,
		};

		if (detail is { Length: > 0 })
		{
			return Summarize(detail);
		}

		if (call.ArgumentsError is { Length: > 0 } error)
		{
			return Summarize(error);
		}

		return Summarize(arguments.ToJsonString());
	}

	private static string? JsonString(System.Text.Json.Nodes.JsonObject arguments, params ReadOnlySpan<string> names)
	{
		foreach (var name in names)
		{
			if (arguments[name] is System.Text.Json.Nodes.JsonValue value &&
				value.TryGetValue<string>(out var text) && text.Length > 0)
			{
				return text;
			}
		}

		return null;
	}

	/// <summary>扩展条目：类型 + 数据摘要（只取前缀，不把整块扩展状态塞进账本）。</summary>
	public static string CustomSummary(string customType, System.Text.Json.Nodes.JsonNode? data)
	{
		var payload = data is null ? string.Empty : Summarize(data.ToJsonString(), SummaryLimit);
		if (payload.Length == 0)
		{
			return customType;
		}

		return customType.Length > 0 ? $"{customType} · {payload}" : payload;
	}

	/// <summary>系统提示词摘要：只读前缀，不在每次刷新时复制整份提示词。</summary>
	public static string SystemPromptSummary(SystemMessage system, int limit = SummaryLimit)
	{
		var builder = new StringBuilder(limit * 3);
		AppendPrefix(builder, system.Text, limit * 3);
		if (builder.Length < limit * 3 && system.Sections is { Count: > 0 } sections)
		{
			foreach (var (_, value) in sections)
			{
				if (string.IsNullOrEmpty(value))
				{
					continue;
				}

				if (builder.Length > 0)
				{
					builder.Append("\n\n");
				}

				AppendPrefix(builder, value, limit * 3);
				if (builder.Length >= limit * 3)
				{
					break;
				}
			}
		}

		return Summarize(builder.ToString(), limit);
	}

	/// <summary>系统提示词全文：指令文本 + 具名章节，顺序与模型看到的一致。</summary>
	public static string SystemPromptText(SystemMessage system)
	{
		var builder = new StringBuilder(system.Text);
		if (system.Sections is { Count: > 0 } sections)
		{
			foreach (var (name, value) in sections)
			{
				if (string.IsNullOrEmpty(value))
				{
					continue;
				}

				if (builder.Length > 0)
				{
					builder.AppendLine().AppendLine();
				}

				builder.Append("## ").Append(name).AppendLine().Append(value);
			}
		}

		return builder.ToString();
	}

	/// <summary>检查器里的入参：用户消息正文或工具参数 JSON。</summary>
	public static string InputOf(TrajectoryRecord record) => record switch
	{
		{ Kind: TrajectoryKind.Tool, Call: { } call } => call.ArgumentsError is { Length: > 0 } error
			? error
			: call.Arguments.ToJsonString(),
		{ Source: UserMessage user } => ContentText(user.Content),
		_ => string.Empty,
	};

	/// <summary>检查器里的输出：助手正文、思考全文、工具结果或错误原因。</summary>
	public static string OutputOf(TrajectoryRecord record)
	{
		if (record.Kind == TrajectoryKind.Thinking)
		{
			if (record.Live is { } live)
			{
				return live.ThinkingSource.Slice(0, live.ThinkingLength);
			}

			return ThinkingText((record.Source as AssistantMessage)?.Content);
		}

		if (record.Kind == TrajectoryKind.Tool)
		{
			return record.Result is { } result ? BlocksText(result.Content) : string.Empty;
		}

		return record.Source switch
		{
			AssistantMessage assistant => BlocksText(assistant.Content),
			ToolResultMessage result => BlocksText(result.Content),
			SystemMessage system => SystemPromptText(system),
			UserMessage user => ContentText(user.Content),
			_ => string.Empty,
		};
	}
}
