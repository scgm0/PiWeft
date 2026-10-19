using PiWeft.Ai;

namespace PiWeft.CodingAgent.Session;

/// <summary>会话文件的第一行。只含元数据，不属于条目树。</summary>
public sealed record SessionHeader
{
	public int Version { get; init; } = SessionFormat.CurrentVersion;

	public string Id { get; init; } = string.Empty;

	/// <summary>ISO 8601 时间戳。</summary>
	public string Timestamp { get; init; } = string.Empty;

	public string Cwd { get; init; } = string.Empty;

	/// <summary>由别的会话派生而来时才有该值。</summary>
	public string? ParentSession { get; init; }

	/// <summary>未知的顶层字段；原样保留，避免第三方会话工具的数据在重写时丢失。</summary>
	public System.Text.Json.Nodes.JsonObject? Extra { get; init; }
}

/// <summary>会话树中所有条目的基类。</summary>
public abstract record SessionEntry
{
	public abstract string Type { get; }

	/// <summary>通常是 8 位十六进制 id；必要时会退回完整 UUID。</summary>
	public string Id { get; init; } = string.Empty;

	/// <summary>父条目 id；根条目为 <c>null</c>。</summary>
	public string? ParentId { get; init; }

	/// <summary>ISO 8601 时间戳。</summary>
	public string Timestamp { get; init; } = string.Empty;
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

/// <summary>归属于某个模型的用量，但不是助手消息，也不进入 LLM 上下文。</summary>
public sealed record UsageEntry : SessionEntry
{
	public override string Type => "usage";

	/// <summary>任意用量类别，例如 <c>cache_warm</c>。</summary>
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

	/// <summary>本次压缩后仍保留的最早条目。若一条都不保留，则存该条目自身的 id。</summary>
	public string FirstKeptEntryId { get; init; } = string.Empty;

	public long TokensBefore { get; init; }

	/// <summary>实现私有的数据，永远不会发给模型。</summary>
	public System.Text.Json.Nodes.JsonNode? Details { get; init; }

	public Usage? Usage { get; init; }

	public bool? FromHook { get; init; }

	/// <summary>该压缩边界上的完整提示词与工具状态，会成为开头的 system 消息。</summary>
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

/// <summary>扩展状态，不参与 LLM 上下文。</summary>
public sealed record CustomEntry : SessionEntry
{
	public override string Type => "custom";

	public string CustomType { get; init; } = string.Empty;

	public System.Text.Json.Nodes.JsonNode? Data { get; init; }
}

/// <summary>扩展注入的消息，会参与 LLM 上下文。</summary>
public sealed record CustomMessageEntry : SessionEntry
{
	public override string Type => "custom_message";

	public string CustomType { get; init; } = string.Empty;

	public MessageContent Content { get; init; } = MessageContent.Empty;

	public System.Text.Json.Nodes.JsonNode? Details { get; init; }

	public bool Display { get; init; }
}

/// <summary>仅追加的编辑，用于修改某个更早条目对模型上下文的贡献。</summary>
public sealed record ContextEditEntry : SessionEntry
{
	public override string Type => "context_edit";

	public string TargetId { get; init; } = string.Empty;

	/// <summary>取值 <c>null</c> 表示把目标从上下文中移除；有值则只替换其内容。</summary>
	public MessageContent? Replacement { get; init; }
}

/// <summary>用户在条目上设置的书签；标注为 null 表示清除。</summary>
public sealed record LabelEntry : SessionEntry
{
	public override string Type => "label";

	public string TargetId { get; init; } = string.Empty;

	public string? Label { get; init; }
}

public sealed record SessionInfoEntry : SessionEntry
{
	public override string Type => "session_info";

	public string? Name { get; init; }
}

/// <summary>读写两端共用的会话常量。</summary>
public static class SessionFormat
{
	public const int CurrentVersion = 3;
	public const string Extension = ".jsonl";
}

/// <summary>从会话文件解析出的模型与思考级别状态。</summary>
public sealed record SessionContext
{
	public required IReadOnlyList<Message> Messages { get; init; }

	public string ThinkingLevel { get; init; } = ThinkingLevels.Off;

	public (string Provider, string ModelId)? Model { get; init; }
}

/// <summary>单个会话文件的摘要，供会话选择器使用。</summary>
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
