using System.Text.Json.Nodes;
using PiWeft.Agent;
using PiWeft.CodingAgent.Compaction;

namespace PiWeft.CodingAgent;

/// <summary>
/// 会话层事件（pi 的 <c>session_info_changed</c> / <c>thinking_level_changed</c> /
/// <c>entry_appended</c> / <c>compaction_*</c>）。它们与代理事件走同一条订阅通道，
/// 因此 JSON、RPC 与桌面看到的是同一份顺序。
/// </summary>
public abstract record SessionEvent : AgentEvent;

/// <summary>会话显示名变化，对应 pi 的 <c>session_info_changed</c>；<c>Name</c> 为 null 表示已清除。</summary>
public sealed record SessionInfoChangedEvent : SessionEvent
{
	public string? Name { get; init; }
}

/// <summary>当前思考级别变化，对应 pi 的 <c>thinking_level_changed</c>。</summary>
public sealed record ThinkingLevelChangedEvent : SessionEvent
{
	public required string Level { get; init; }
}

/// <summary>
/// 追加了一条扩展风格的自定义条目，对应 pi 的 <c>entry_appended</c>。
/// 条目本身按会话文件的线格式保存，客户端无需理解类型即可转发。
/// </summary>
public sealed record EntryAppendedEvent : SessionEvent
{
	public required JsonObject Entry { get; init; }
}

/// <summary>压缩开始，对应 pi 的 <c>compaction_start</c>。</summary>
public sealed record CompactionStartEvent : SessionEvent
{
	/// <summary><c>manual</c>、<c>threshold</c> 或 <c>overflow</c>。</summary>
	public required string Reason { get; init; }
}

/// <summary>压缩结束，对应 pi 的 <c>compaction_end</c>。</summary>
public sealed record CompactionEndEvent : SessionEvent
{
	public required string Reason { get; init; }

	/// <summary>成功时的结果；被中止或失败时为 null。</summary>
	public CompactionResult? Result { get; init; }

	public bool Aborted { get; init; }

	/// <summary>是否会在压缩后自动重试（本端口不做溢出恢复，恒为 false）。</summary>
	public bool WillRetry { get; init; }

	public string? ErrorMessage { get; init; }

	/// <summary>压缩后的上下文估算，成功时给出。</summary>
	public long? EstimatedTokensAfter { get; init; }
}

/// <summary>摘要请求即将重试。</summary>
public sealed record SummarizationRetryScheduledEvent : SessionEvent
{
	public required int Attempt { get; init; }
	public required int MaxAttempts { get; init; }
	public required int DelayMs { get; init; }
	public required string ErrorMessage { get; init; }
}

/// <summary>摘要重试开始；<c>Source</c> 为 <c>compaction</c> 或 <c>branchSummary</c>。</summary>
public sealed record SummarizationRetryAttemptStartEvent : SessionEvent
{
	public required string Source { get; init; }
	public string? Reason { get; init; }
}

/// <summary>摘要重试结束。</summary>
public sealed record SummarizationRetryFinishedEvent : SessionEvent;

/// <summary>压缩原因常量，与 pi 的字符串取值一致。</summary>
public static class CompactionReasons
{
	public const string Manual = "manual";

	public const string Threshold = "threshold";

	public const string Overflow = "overflow";
}
