using System.Text.Json.Nodes;
using PiWeft.Agent;
using PiWeft.CodingAgent.Compaction;

namespace PiWeft.CodingAgent;

/// 会话层事件（pi 的 <c>session_info_changed</c> / <c>thinking_level_changed</c> /
/// <c>entry_appended</c> / <c>compaction_*</c>）。它们与代理事件走同一条订阅通道，
/// 因此 JSON、RPC 与桌面看到的是同一份顺序。
public abstract record SessionEvent : AgentEvent;

public sealed record SessionInfoChangedEvent : SessionEvent
{
	public string? Name { get; init; }
}

public sealed record ThinkingLevelChangedEvent : SessionEvent
{
	public required string Level { get; init; }
}

/// 追加了一条扩展风格的自定义条目，对应 pi 的 <c>entry_appended</c>。
/// 条目本身按会话文件的线格式保存，客户端无需理解类型即可转发。
public sealed record EntryAppendedEvent : SessionEvent
{
	public required JsonObject Entry { get; init; }
}

public sealed record CompactionStartEvent : SessionEvent
{
	public required string Reason { get; init; }
}

public sealed record CompactionEndEvent : SessionEvent
{
	public required string Reason { get; init; }

	public CompactionResult? Result { get; init; }

	public bool Aborted { get; init; }

	public bool WillRetry { get; init; }

	public string? ErrorMessage { get; init; }

	public long? EstimatedTokensAfter { get; init; }
}

public sealed record SummarizationRetryScheduledEvent : SessionEvent
{
	public required int Attempt { get; init; }
	public required int MaxAttempts { get; init; }
	public required int DelayMs { get; init; }
	public required string ErrorMessage { get; init; }
}

public sealed record SummarizationRetryAttemptStartEvent : SessionEvent
{
	public required string Source { get; init; }
	public string? Reason { get; init; }
}

public sealed record SummarizationRetryFinishedEvent : SessionEvent;

public static class CompactionReasons
{
	public const string Manual = "manual";

	public const string Threshold = "threshold";

	public const string Overflow = "overflow";
}
