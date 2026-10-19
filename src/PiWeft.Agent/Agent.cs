using PiWeft.Ai;

namespace PiWeft.Agent;

public sealed record AgentOptions
{
	public string SystemPrompt { get; init; } = string.Empty;

	public required Model Model { get; init; }

	public string ThinkingLevel { get; init; } = ThinkingLevels.Off;

	public IReadOnlyList<AgentTool> Tools { get; init; } = [];

	/// <summary>流式产出助手回复。接模型网关时用 <see cref="AgentGatewayStreamFn.For"/>。</summary>
	public required StreamFn StreamFn { get; init; }

	public Func<IReadOnlyList<Message>, CancellationToken, IReadOnlyList<Message>>? ConvertToLlm { get; init; }

	public Func<IReadOnlyList<Message>, CancellationToken, ValueTask<IReadOnlyList<Message>>>? TransformContext { get; init; }

	public Func<string, string?>? GetApiKey { get; init; }

	public ToolExecutionMode ToolExecution { get; init; } = ToolExecutionMode.Parallel;

	public Func<BeforeToolCallContext, CancellationToken, ValueTask<BeforeToolCallResult?>>? BeforeToolCall { get; init; }

	public Func<AfterToolCallContext, CancellationToken, ValueTask<AfterToolCallResult?>>? AfterToolCall { get; init; }

	public Func<AgentTurnContext, CancellationToken, ValueTask<AgentTurnAction>>? FinishTurn { get; init; }

	public Func<PrepareRequestContext, CancellationToken, ValueTask<PrepareRequestUpdate?>>? PrepareRequest { get; init; }

	public Func<AgentTurnContext, CancellationToken, ValueTask<AgentTurnUpdate?>>? PrepareNextTurn { get; init; }

	public Func<AssistantMessage, bool>? WillRetry { get; init; }

	public StreamOptions Options { get; init; } = new();
}

/// <summary>
/// 有状态代理：负责工具执行与事件流。它持有会话记录、steering 队列、follow-up 队列与监听器集合；
/// 具体调度交给 <see cref="AgentLoop"/>。
/// </summary>
public sealed class Agent
{
	private static readonly Func<IReadOnlyList<Message>, CancellationToken, IReadOnlyList<Message>> DefaultConvertToLlm =
		static (messages, _) => messages;

	private readonly Lock _gate = new();
	private readonly List<Message> _messages = [];
	private readonly List<Func<AgentEvent, ValueTask>> _listeners = [];
	private readonly Queue<Message> _steering = new();
	private readonly Queue<Message> _followUp = new();

	private AgentOptions _options;
	private AgentQueueMode _steeringMode = AgentQueueMode.OneAtATime;
	private AgentQueueMode _followUpMode = AgentQueueMode.OneAtATime;
	private IReadOnlyList<AgentTool> _tools;
	private CancellationTokenSource? _cancellation;
	private bool _isStreaming;
	private string? _errorMessage;

	public Agent(AgentOptions options)
	{
		_options = options;
		_tools = options.Tools;
		if (!string.IsNullOrEmpty(options.SystemPrompt))
		{
			_messages.Add(new SystemMessage
			{
				Content = MessageContent.FromText(options.SystemPrompt),
				Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
			});
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

	public string? ErrorMessage
	{
		get
		{
			lock (_gate)
			{
				return _errorMessage;
			}
		}
	}

	public string SystemPrompt => _options.SystemPrompt;

	public Model Model
	{
		get
		{
			lock (_gate)
			{
				return _options.Model;
			}
		}
	}

	public string ThinkingLevel
	{
		get
		{
			lock (_gate)
			{
				return _options.ThinkingLevel;
			}
		}
	}

	public IReadOnlyList<AgentTool> Tools
	{
		get
		{
			lock (_gate)
			{
				return _tools;
			}
		}
	}

	/// <summary>会话记录的快照，返回的是副本。</summary>
	public IReadOnlyList<Message> Messages
	{
		get
		{
			lock (_gate)
			{
				return [.. _messages];
			}
		}
	}

	public void SetModel(Model model)
	{
		lock (_gate)
		{
			_options = _options with { Model = model };
		}
	}

	public void SetThinkingLevel(string level)
	{
		lock (_gate)
		{
			_options = _options with { ThinkingLevel = level };
		}
	}

	public void SetTools(IReadOnlyList<AgentTool> tools)
	{
		lock (_gate)
		{
			_tools = tools;
		}
	}

	/// <summary>替换整个会话记录，加载会话时使用。</summary>
	public void SetMessages(IReadOnlyList<Message> messages)
	{
		lock (_gate)
		{
			_messages.Clear();
			_messages.AddRange(messages);
		}
	}

	/// <summary>注册监听器；释放返回的订阅即可注销。</summary>
	public IDisposable Subscribe(Func<AgentEvent, ValueTask> listener)
	{
		lock (_gate)
		{
			_listeners.Add(listener);
		}

		return new Subscription(this, listener);
	}

	/// <summary>每个抽取点投递多少条 steering 消息。默认一次一条。</summary>
	public AgentQueueMode SteeringMode
	{
		get
		{
			lock (_gate)
			{
				return _steeringMode;
			}
		}

		set
		{
			lock (_gate)
			{
				_steeringMode = value;
			}
		}
	}

	/// <summary>每个抽取点投递多少条 follow-up 消息。默认一次一条。</summary>
	public AgentQueueMode FollowUpMode
	{
		get
		{
			lock (_gate)
			{
				return _followUpMode;
			}
		}

		set
		{
			lock (_gate)
			{
				_followUpMode = value;
			}
		}
	}

	/// <summary>steering 与 follow-up 队列中等待的消息总数。</summary>
	public int PendingMessageCount
	{
		get
		{
			lock (_gate)
			{
				return _steering.Count + _followUp.Count;
			}
		}
	}

	/// <summary>当前排队消息的快照（steering、follow-up 各一份）。</summary>
	public (IReadOnlyList<Message> Steering, IReadOnlyList<Message> FollowUp) PendingQueues()
	{
		lock (_gate)
		{
			return (_steering.ToList(), _followUp.ToList());
		}
	}

	/// <summary>清空所有排队消息，并返回被丢弃的内容。</summary>
	public (IReadOnlyList<Message> Steering, IReadOnlyList<Message> FollowUp) ClearQueue()
	{
		lock (_gate)
		{
			var steering = _steering.ToList();
			var followUp = _followUp.ToList();
			_steering.Clear();
			_followUp.Clear();
			return (steering, followUp);
		}
	}

	/// <summary>排入一条消息，在当前轮次的工具调用结束后注入。</summary>
	public void Steer(Message message)
	{
		lock (_gate)
		{
			_steering.Enqueue(message);
		}
	}

	/// <summary>排入一条消息，在代理本应停止时继续处理。</summary>
	public void FollowUp(Message message)
	{
		lock (_gate)
		{
			_followUp.Enqueue(message);
		}
	}

	/// <summary>取消当前运行；助手消息会以停止原因 <c>aborted</c> 结束。</summary>
	public void Abort()
	{
		lock (_gate)
		{
			_cancellation?.Cancel();
		}
	}

	public Task<IReadOnlyList<Message>> PromptAsync(string text, CancellationToken cancellationToken = default) =>
		PromptAsync(
			new UserMessage
			{
				Content = MessageContent.FromText(text),
				Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
			},
			cancellationToken);

	public async Task<IReadOnlyList<Message>> PromptAsync(Message message, CancellationToken cancellationToken = default)
	{
		using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		var context = BeginRun(linked);
		try
		{
			return await AgentLoop.RunAsync(
				[message],
				context,
				BuildConfig(),
				_options.StreamFn,
				EmitAsync,
				linked.Token).ConfigureAwait(false);
		}
		finally
		{
			EndRun();
		}
	}

	public async Task<IReadOnlyList<Message>> ContinueAsync(CancellationToken cancellationToken = default)
	{
		using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		var context = BeginRun(linked);
		try
		{
			return await AgentLoop.ContinueAsync(
				context,
				BuildConfig(),
				_options.StreamFn,
				EmitAsync,
				linked.Token).ConfigureAwait(false);
		}
		finally
		{
			EndRun();
		}
	}

	private AgentContext BeginRun(CancellationTokenSource cancellation)
	{
		lock (_gate)
		{
			if (_isStreaming)
			{
				throw new InvalidOperationException("Agent is already streaming");
			}

			_isStreaming = true;
			_errorMessage = null;
			_cancellation = cancellation;
		}

		return new AgentContext { Tools = Tools, Messages = _messages };
	}

	private void EndRun()
	{
		lock (_gate)
		{
			_isStreaming = false;
			_cancellation = null;
		}
	}

	private AgentLoopConfig BuildConfig() => new()
	{
		Model = _options.Model,
		ThinkingLevel = _options.ThinkingLevel,
		Options = _options.Options,
		ConvertToLlm = _options.ConvertToLlm ?? DefaultConvertToLlm,
		TransformContext = _options.TransformContext,
		GetApiKey = _options.GetApiKey,
		ToolExecution = _options.ToolExecution,
		BeforeToolCall = _options.BeforeToolCall,
		AfterToolCall = _options.AfterToolCall,
		FinishTurn = _options.FinishTurn,
		PrepareRequest = _options.PrepareRequest,
		PrepareNextTurn = _options.PrepareNextTurn,
		WillRetry = _options.WillRetry,
		GetSteeringMessages = DrainSteering,
		GetFollowUpMessages = DrainFollowUp,
	};

	private async ValueTask<IReadOnlyList<Message>> DrainSteering(CancellationToken cancellationToken)
	{
		IReadOnlyList<Message> messages;
		QueueUpdateEvent update;
		lock (_gate)
		{
			messages = Drain(_steering, _steeringMode);
			update = QueueUpdate();
		}

		// 交给模型的那部分已经离开队列：把剩余队列的最新全量发给订阅者（pi 的 queue_update）。
		if (messages.Count > 0)
		{
			await EmitAsync(update).ConfigureAwait(false);
		}

		return messages;
	}

	private async ValueTask<IReadOnlyList<Message>> DrainFollowUp(CancellationToken cancellationToken)
	{
		IReadOnlyList<Message> messages;
		QueueUpdateEvent update;
		lock (_gate)
		{
			messages = Drain(_followUp, _followUpMode);
			update = QueueUpdate();
		}

		if (messages.Count > 0)
		{
			await EmitAsync(update).ConfigureAwait(false);
		}

		return messages;
	}

	/// <summary>调用方必须持有 <see cref="_gate"/>。</summary>
	private QueueUpdateEvent QueueUpdate() => new()
	{
		Steering = _steering.ToList(),
		FollowUp = _followUp.ToList(),
	};

	private static List<Message> Drain(Queue<Message> queue, AgentQueueMode mode)
	{
		if (queue.Count == 0)
		{
			return [];
		}

		if (mode == AgentQueueMode.OneAtATime)
		{
			return [queue.Dequeue()];
		}

		var messages = new List<Message>(queue.Count);
		while (queue.Count > 0)
		{
			messages.Add(queue.Dequeue());
		}

		return messages;
	}

	private async ValueTask EmitAsync(AgentEvent @event)
	{
		if (@event is AgentEndEvent end && end.Messages.OfType<AssistantMessage>().LastOrDefault() is { } last &&
			last.StopReason is StopReason.Error or StopReason.Aborted)
		{
			lock (_gate)
			{
				_errorMessage = last.ErrorMessage;
			}
		}

		Func<AgentEvent, ValueTask>[] listeners;
		lock (_gate)
		{
			listeners = [.. _listeners];
		}

		foreach (var listener in listeners)
		{
			await listener(@event).ConfigureAwait(false);
		}
	}

	private sealed class Subscription(Agent agent, Func<AgentEvent, ValueTask> listener) : IDisposable
	{
		private int _disposed;

		public void Dispose()
		{
			if (Interlocked.Exchange(ref _disposed, 1) != 0)
			{
				return;
			}

			lock (agent._gate)
			{
				agent._listeners.Remove(listener);
			}
		}
	}
}
