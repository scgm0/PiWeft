using PiWeft.Ai;
using PiWeft.Ai.Json;

namespace PiWeft.CodingAgent.Session;

public sealed record SessionHeader
{
	public int Version { get; init; } = SessionFormat.CurrentVersion;

	public string Id { get; init; } = string.Empty;

	public string Timestamp { get; init; } = string.Empty;

	public string Cwd { get; init; } = string.Empty;

	public string? ParentSession { get; init; }

	/// <summary>派生会话的来源标记；子代理为 <c>subagent</c>，分支/普通会话为空。</summary>
	public string? Origin { get; init; }

	public System.Text.Json.Nodes.JsonObject? Extra { get; init; }
}

public abstract record SessionEntry
{
	public abstract string Type { get; }

	public string Id { get; init; } = string.Empty;

	public string? ParentId { get; init; }

	public string Timestamp { get; init; } = string.Empty;

	/// 该条目类型没建模的顶层字段，写回时原样输出。
	/// 新增字段时不丢数据靠这里，不靠手写转换补丁。
	public System.Text.Json.Nodes.JsonObject? Extra { get; init; }
}

public sealed record SessionMessageEntry : SessionEntry
{
	public override string Type => "message";

	public required Message Message { get; init; }
}

public sealed record ModelChangeEntry : SessionEntry
{
	public override string Type => "model_change";

	public string Provider { get; init; } = string.Empty;

	public string ModelId { get; init; } = string.Empty;
}

public sealed record ThinkingLevelChangeEntry : SessionEntry
{
	public override string Type => "thinking_level_change";

	public string ThinkingLevel { get; init; } = string.Empty;
}

public sealed record UsageEntry : SessionEntry
{
	public override string Type => "usage";

	public string Kind { get; init; } = string.Empty;

	public string Provider { get; init; } = string.Empty;

	public string Model { get; init; } = string.Empty;

	public Usage Usage { get; init; } = new();

	public string? Note { get; init; }
}

public sealed record CompactionEntry : SessionEntry
{
	public override string Type => "compaction";

	public string Summary { get; init; } = string.Empty;

	public string FirstKeptEntryId { get; init; } = string.Empty;

	public long TokensBefore { get; init; }

	public System.Text.Json.Nodes.JsonNode? Details { get; init; }

	public Usage? Usage { get; init; }

	public bool? FromHook { get; init; }

	public SystemMessage? SystemMessage { get; init; }
}

public sealed record BranchSummaryEntry : SessionEntry
{
	public override string Type => "branch_summary";

	public string FromId { get; init; } = string.Empty;

	public string Summary { get; init; } = string.Empty;

	public System.Text.Json.Nodes.JsonNode? Details { get; init; }

	public Usage? Usage { get; init; }

	public bool? FromHook { get; init; }
}

public sealed record CustomEntry : SessionEntry
{
	public override string Type => "custom";

	public string CustomType { get; init; } = string.Empty;

	public System.Text.Json.Nodes.JsonNode? Data { get; init; }
}

public sealed record CustomMessageEntry : SessionEntry
{
	public override string Type => "custom_message";

	public string CustomType { get; init; } = string.Empty;

	public MessageContent Content { get; init; } = MessageContent.Empty;

	public System.Text.Json.Nodes.JsonNode? Details { get; init; }

	public bool Display { get; init; }
}

public sealed record ContextEditEntry : SessionEntry
{
	public override string Type => "context_edit";

	public string TargetId { get; init; } = string.Empty;

	public MessageContent? Replacement { get; init; }
}

public sealed record LabelEntry : SessionEntry
{
	public override string Type => "label";

	public string TargetId { get; init; } = string.Empty;

	public string? Label { get; init; }
}

/// 墓碑：把被放弃的条目原地改写成它。编辑重发要真正截断分支（否则旧回答的 tool_call/toolResult
/// 配对会残留），但又不能换新会话文件（侧栏会每改一次多一条会话），所以按 pi/PiDeck 的
/// <c>deleted</c> 形状留下墓碑：原文不再进入上下文、不再出现在树里，仍可在文件里审计。
public sealed record DeletedEntry : SessionEntry
{
	public override string Type => "deleted";

	public string OriginalEntryId { get; init; } = string.Empty;

	public string? Reason { get; init; }
}

public sealed record SessionInfoEntry : SessionEntry
{
	public override string Type => "session_info";

	public string? Name { get; init; }
}

/// 未知条目类型：整行原样保留。
/// 以前未知类型会被当 <see cref="CustomEntry"/> 收下，但 <see cref="CustomEntry.Type"/> 恒为
/// <c>custom</c>，写回就成了 <c>{"type":"custom","customType":"&lt;原类型&gt;","data":{整行}}</c>，
/// 一次重写就把这行改成完全不同的语义。新增条目类型时这里保持原样，等模型补齐。
public sealed record UnknownSessionEntry : SessionEntry
{
	public required System.Text.Json.Nodes.JsonObject Raw { get; init; }

	public override string Type => PiJsonValue.Str(Raw, "type") ?? "unknown";
}

public static class SessionFormat
{
	public const int CurrentVersion = 3;
	public const string Extension = ".jsonl";
}

public sealed record SessionContext
{
	public required IReadOnlyList<Message> Messages { get; init; }

	public string ThinkingLevel { get; init; } = ThinkingLevels.Off;

	public (string Provider, string ModelId)? Model { get; init; }
}

public sealed record SessionInfo
{
	public required string Path { get; init; }

	public required string Id { get; init; }

	public string Cwd { get; init; } = string.Empty;

	public string? Name { get; init; }

	public string? ParentSessionPath { get; init; }

	public DateTimeOffset Created { get; init; }

	public DateTimeOffset Modified { get; init; }

	public int MessageCount { get; init; }

	public string FirstMessage { get; init; } = string.Empty;

	public string AllMessagesText { get; init; } = string.Empty;
}
