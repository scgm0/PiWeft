using System.Text.Json.Nodes;
using PiWeft.Ai;

namespace PiWeft.Agent;

public abstract record AgentEvent;

public sealed record AgentStartEvent : AgentEvent;

public sealed record AgentEndEvent : AgentEvent
{
	public bool WillRetry { get; init; }

	public required IReadOnlyList<Message> Messages { get; init; }
}

public sealed record AutoRetryStartEvent : AgentEvent
{
	public required int Attempt { get; init; }

	public required int MaxAttempts { get; init; }

	public required int DelayMs { get; init; }

	public required string ErrorMessage { get; init; }
}

public sealed record AutoRetryEndEvent : AgentEvent
{
	public required bool Success { get; init; }

	public required int Attempt { get; init; }

	public string? FinalError { get; init; }
}

public sealed record TurnStartEvent : AgentEvent;

public sealed record TurnEndEvent : AgentEvent
{
	public required Message Message { get; init; }

	public required IReadOnlyList<ToolResultMessage> ToolResults { get; init; }
}

public sealed record MessageStartEvent : AgentEvent
{
	public required Message Message { get; init; }
}

public sealed record MessageUpdateEvent : AgentEvent
{
	public required Message Message { get; init; }

	public required AssistantMessageEvent AssistantMessageEvent { get; init; }
}

public sealed record MessageEndEvent : AgentEvent
{
	public required Message Message { get; init; }
}

public sealed record ToolExecutionStartEvent : AgentEvent
{
	public required string ToolCallId { get; init; }

	public required string ToolName { get; init; }

	public required JsonObject Arguments { get; init; }
}

public sealed record ToolExecutionUpdateEvent : AgentEvent
{
	public required string ToolCallId { get; init; }

	public required string ToolName { get; init; }

	public required JsonObject Arguments { get; init; }

	public required AgentToolResult PartialResult { get; init; }
}

/// steering / follow-up 队列发生变化，对应 pi 的 <c>queue_update</c>。
/// 两个字段都是变化后的完整队列，客户端直接替换即可。
public sealed record QueueUpdateEvent : AgentEvent
{
	public required IReadOnlyList<Message> Steering { get; init; }

	public required IReadOnlyList<Message> FollowUp { get; init; }
}

public sealed record ToolExecutionEndEvent : AgentEvent
{
	public required string ToolCallId { get; init; }

	public required string ToolName { get; init; }

	public required AgentToolResult Result { get; init; }

	public bool IsError { get; init; }

	public double? DurationMs { get; init; }
}
