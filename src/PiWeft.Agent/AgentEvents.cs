using System.Text.Json.Nodes;
using PiWeft.Ai;

namespace PiWeft.Agent;

/// <summary>代理向外发出的事件，供界面更新使用。<see cref="AgentEndEvent"/> 永远是最后一个。</summary>
public abstract record AgentEvent;

/// <summary>一次运行开始。</summary>
public sealed record AgentStartEvent : AgentEvent;

/// <summary>一次运行结束，携带当前完整会话记录。</summary>
public sealed record AgentEndEvent : AgentEvent
{
	/// <summary>会话层是否会在本次 low-level run 结束后自动重试。</summary>
	public bool WillRetry { get; init; }

	public required IReadOnlyList<Message> Messages { get; init; }
}

/// <summary>一个轮次开始：一次助手回复，外加它可能触发的工具调用与结果。</summary>
/// <summary>准备重试一次失败的助手回复（退避开始前发出），对应 pi 的 <c>auto_retry_start</c>。</summary>
public sealed record AutoRetryStartEvent : AgentEvent
{
	public required int Attempt { get; init; }

	public required int MaxAttempts { get; init; }

	public required int DelayMs { get; init; }

	public required string ErrorMessage { get; init; }
}

/// <summary>一次重试序列的结束（成功、耗尽或中止），对应 pi 的 <c>auto_retry_end</c>。</summary>
public sealed record AutoRetryEndEvent : AgentEvent
{
	public required bool Success { get; init; }

	public required int Attempt { get; init; }

	public string? FinalError { get; init; }
}

public sealed record TurnStartEvent : AgentEvent;

/// <summary>一个轮次结束。</summary>
public sealed record TurnEndEvent : AgentEvent
{
	public required Message Message { get; init; }

	public required IReadOnlyList<ToolResultMessage> ToolResults { get; init; }
}

/// <summary>system、user、assistant、toolResult 消息都会各自触发一次。</summary>
public sealed record MessageStartEvent : AgentEvent
{
	public required Message Message { get; init; }
}

/// <summary>只有流式过程中的助手消息会触发。</summary>
public sealed record MessageUpdateEvent : AgentEvent
{
	public required Message Message { get; init; }

	public required AssistantMessageEvent AssistantMessageEvent { get; init; }
}

/// <summary>一条消息最终定型。</summary>
public sealed record MessageEndEvent : AgentEvent
{
	public required Message Message { get; init; }
}

/// <summary>一个工具开始执行。</summary>
public sealed record ToolExecutionStartEvent : AgentEvent
{
	public required string ToolCallId { get; init; }

	public required string ToolName { get; init; }

	public required JsonObject Arguments { get; init; }
}

/// <summary>工具执行过程中的增量输出（并非所有工具都会发）。</summary>
public sealed record ToolExecutionUpdateEvent : AgentEvent
{
	public required string ToolCallId { get; init; }

	public required string ToolName { get; init; }

	public required JsonObject Arguments { get; init; }

	public required AgentToolResult PartialResult { get; init; }
}

/// <summary>
/// steering / follow-up 队列发生变化，对应 pi 的 <c>queue_update</c>。
/// 两个字段都是变化后的完整队列，客户端直接替换即可。
/// </summary>
public sealed record QueueUpdateEvent : AgentEvent
{
	public required IReadOnlyList<Message> Steering { get; init; }

	public required IReadOnlyList<Message> FollowUp { get; init; }
}

/// <summary>一个工具执行结束。</summary>
public sealed record ToolExecutionEndEvent : AgentEvent
{
	public required string ToolCallId { get; init; }

	public required string ToolName { get; init; }

	public required AgentToolResult Result { get; init; }

	/// <summary>工具是否报告了错误（协议层面仍然是一次成功调用）。</summary>
	public bool IsError { get; init; }

	/// <summary>执行耗时，单位毫秒。</summary>
	public double? DurationMs { get; init; }
}
