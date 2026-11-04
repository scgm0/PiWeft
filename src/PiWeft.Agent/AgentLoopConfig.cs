using PiWeft.Ai;

namespace PiWeft.Agent;

public delegate AssistantMessageEventStream StreamFn(Model model, IReadOnlyList<Message> messages, StreamOptions options);

public delegate ValueTask AgentEventSink(AgentEvent @event);

public sealed record AgentTurnContext
{
	public required AssistantMessage Message { get; init; }

	public required IReadOnlyList<ToolResultMessage> ToolResults { get; init; }

	public required AgentContext Context { get; init; }

	public required IReadOnlyList<Message> NewMessages { get; init; }
}

public enum AgentTurnAction
{
	Normal,

	End,

	Continue,
}

public sealed record AgentTurnUpdate
{
	public AgentContext? Context { get; init; }

	public IReadOnlyList<Message>? Messages { get; init; }

	public Model? Model { get; init; }

	public string? ThinkingLevel { get; init; }
}

public sealed record PrepareRequestContext
{
	public required AgentContext Context { get; init; }

	public required Model Model { get; init; }

	public required string ThinkingLevel { get; init; }
}

public sealed record PrepareRequestUpdate
{
	public AgentContext? Context { get; init; }

	public Model? Model { get; init; }

	public string? ThinkingLevel { get; init; }
}

public sealed record AgentLoopConfig
{
	public required Model Model { get; init; }

	public string ThinkingLevel { get; init; } = ThinkingLevels.Off;

	public StreamOptions Options { get; init; } = new();

	public Func<IReadOnlyList<Message>, CancellationToken, IReadOnlyList<Message>> ConvertToLlm { get; init; } =
		static (messages, _) => messages;

	public Func<IReadOnlyList<Message>, CancellationToken, ValueTask<IReadOnlyList<Message>>>? TransformContext { get; init; }

	public Func<string, string?>? GetApiKey { get; init; }

	public Func<CancellationToken, ValueTask<IReadOnlyList<Message>>>? GetSteeringMessages { get; init; }

	public Func<CancellationToken, ValueTask<IReadOnlyList<Message>>>? GetFollowUpMessages { get; init; }

	public ToolExecutionMode ToolExecution { get; init; } = ToolExecutionMode.Parallel;

	public Func<BeforeToolCallContext, CancellationToken, ValueTask<BeforeToolCallResult?>>? BeforeToolCall { get; init; }

	public Func<AfterToolCallContext, CancellationToken, ValueTask<AfterToolCallResult?>>? AfterToolCall { get; init; }

	public Func<AgentTurnContext, CancellationToken, ValueTask<AgentTurnAction>>? FinishTurn { get; init; }

	public Func<PrepareRequestContext, CancellationToken, ValueTask<PrepareRequestUpdate?>>? PrepareRequest { get; init; }

	public Func<AgentTurnContext, CancellationToken, ValueTask<AgentTurnUpdate?>>? PrepareNextTurn { get; init; }

	public Func<AssistantMessage, bool>? WillRetry { get; init; }
}
