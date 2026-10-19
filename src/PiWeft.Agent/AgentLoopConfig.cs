using PiWeft.Ai;

namespace PiWeft.Agent;

/// <summary>流式产出一次助手回复。实现不得抛异常；失败要以终止性的流事件形式给出。</summary>
public delegate AssistantMessageEventStream StreamFn(Model model, IReadOnlyList<Message> messages, StreamOptions options);

/// <summary>代理事件的接收端。等待它完成可以保证事件顺序。</summary>
public delegate ValueTask AgentEventSink(AgentEvent @event);

/// <summary>一个轮次结束时的上下文，会传给轮次回调。</summary>
/// <summary>一个轮次结束时的上下文。</summary>
public sealed record AgentTurnContext
{
	public required AssistantMessage Message { get; init; }

	public required IReadOnlyList<ToolResultMessage> ToolResults { get; init; }

	public required AgentContext Context { get; init; }

	public required IReadOnlyList<Message> NewMessages { get; init; }
}

/// <summary><see cref="AgentLoopConfig.FinishTurn"/> 返回的决定。</summary>
public enum AgentTurnAction
{
	/// <summary>维持正常调度。</summary>
	Normal,

	/// <summary>结束本次运行：不再查看队列、也不再准备下一次请求。</summary>
	End,

	/// <summary>即使没有其它理由，也再发一次 provider 请求。</summary>
	Continue,
}

/// <summary>下一次 provider 请求前替换的运行时状态。</summary>
/// <summary><see cref="AgentLoopConfig.PrepareNextTurn"/> 返回的覆盖。</summary>
public sealed record AgentTurnUpdate
{
	public AgentContext? Context { get; init; }

	public IReadOnlyList<Message>? Messages { get; init; }

	public Model? Model { get; init; }

	public string? ThinkingLevel { get; init; }
}

/// <summary>准备请求时的上下文。</summary>
public sealed record PrepareRequestContext
{
	public required AgentContext Context { get; init; }

	public required Model Model { get; init; }

	public required string ThinkingLevel { get; init; }
}

/// <summary><see cref="AgentLoopConfig.PrepareRequest"/> 返回的覆盖。</summary>
public sealed record PrepareRequestUpdate
{
	public AgentContext? Context { get; init; }

	public Model? Model { get; init; }

	public string? ThinkingLevel { get; init; }
}

/// <summary>一次代理运行的运行时配置。</summary>
public sealed record AgentLoopConfig
{
	public required Model Model { get; init; }

	/// <summary>本次运行请求的 pi 思考级别。</summary>
	public string ThinkingLevel { get; init; } = ThinkingLevels.Off;

	/// <summary>转发给 provider 的请求选项（温度、最大令牌数、请求头等）。</summary>
	public StreamOptions Options { get; init; } = new();

	/// <summary>把代理的会话记录转换成 LLM 能理解的消息。实现不得抛异常。</summary>
	public Func<IReadOnlyList<Message>, CancellationToken, IReadOnlyList<Message>> ConvertToLlm { get; init; } =
		static (messages, _) => messages;

	/// <summary>在 <see cref="ConvertToLlm"/> 之前应用的可选变换（裁剪、注入上下文等）。</summary>
	public Func<IReadOnlyList<Message>, CancellationToken, ValueTask<IReadOnlyList<Message>>>? TransformContext { get; init; }

	/// <summary>每次调用时解析 API key，可支持短期令牌。</summary>
	public Func<string, string?>? GetApiKey { get; init; }

	/// <summary>返回 steering 消息，在当前轮次的工具调用结束后注入。</summary>
	public Func<CancellationToken, ValueTask<IReadOnlyList<Message>>>? GetSteeringMessages { get; init; }

	/// <summary>返回 follow-up 消息，在代理本应停止时继续处理。</summary>
	public Func<CancellationToken, ValueTask<IReadOnlyList<Message>>>? GetFollowUpMessages { get; init; }

	public ToolExecutionMode ToolExecution { get; init; } = ToolExecutionMode.Parallel;

	public Func<BeforeToolCallContext, CancellationToken, ValueTask<BeforeToolCallResult?>>? BeforeToolCall { get; init; }

	public Func<AfterToolCallContext, CancellationToken, ValueTask<AfterToolCallResult?>>? AfterToolCall { get; init; }

	public Func<AgentTurnContext, CancellationToken, ValueTask<AgentTurnAction>>? FinishTurn { get; init; }

	public Func<PrepareRequestContext, CancellationToken, ValueTask<PrepareRequestUpdate?>>? PrepareRequest { get; init; }

	public Func<AgentTurnContext, CancellationToken, ValueTask<AgentTurnUpdate?>>? PrepareNextTurn { get; init; }

	/// <summary>给定失败的助手消息，判断本次 run 结束后是否会自动重试。</summary>
	public Func<AssistantMessage, bool>? WillRetry { get; init; }
}
