using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using PiWeft.Ai.Json;

namespace PiWeft.Ai;

/// 消息内容：既可以是纯字符串，也可以是内容块列表
/// （对应 pi 的 <c>string | (TextContent | ImageContent)[]</c>）。
[JsonConverter(typeof(MessageContentJsonConverter))]
public readonly record struct MessageContent
{
	private MessageContent(string? text, IReadOnlyList<ContentBlock>? blocks)
	{
		Text = text;
		Blocks = blocks;
	}

	public string? Text { get; }

	public IReadOnlyList<ContentBlock>? Blocks { get; }

	public bool IsText => Text is not null;

	public static MessageContent FromText(string text) => new(text, null);

	public static MessageContent FromBlocks(IReadOnlyList<ContentBlock> blocks) => new(null, blocks);

	public static implicit operator MessageContent(string text) => FromText(text);

	public static MessageContent Empty => new(string.Empty, null);
}

[JsonConverter(typeof(MessageJsonConverter))]
public abstract record Message
{
	public abstract string Role { get; }

	public long Timestamp { get; init; }

	/// 读取时未被建模的字段，写回时原样输出。
	/// pi 与第三方工具会往消息里加字段（codemode 的 <c>nestedCalls</c> 就是一例），
	/// 丢弃它们等于改写用户的历史：<c>fork</c> 或导入后再写回，那些数据就永久没了。
	/// 已知角色的消息按角色收集，未知角色整体落在 <see cref="CustomMessage.Raw"/>。
	[JsonIgnore]
	public JsonObject? Extra { get; init; }
}

public sealed record SystemMessage : Message
{
	public override string Role => "system";

	public MessageContent Content { get; init; } = MessageContent.Empty;

	public Dictionary<string, string?>? Sections { get; init; }

	public IReadOnlyList<Tool>? ToolsAdded { get; init; }

	public IReadOnlyList<ToolReference>? ToolsRemoved { get; init; }

	public string Text => Content.Text ?? string.Empty;
}

public sealed record UserMessage : Message
{
	public override string Role => "user";

	public MessageContent Content { get; init; } = MessageContent.Empty;

	/// 扩展注入的消息类型（会话文件里的 <c>customType</c>）；用户手写的消息为 <see langword="null"/>。
	/// 这类消息仍参与模型上下文，但对话里是否显示由 <see cref="Display"/> 决定。
	public string? CustomType { get; init; }

	/// 用户亲手写的消息。注入的上下文（子代理回报、关联会话消息、继续生成收据）
	/// 一样是 user 角色，但都带 <see cref="CustomType"/>；标题、分叉点、重试与导出
	/// 都按这个区分，否则机器文本会冒充用户提问。
	[JsonIgnore]
	public bool IsUserAuthored => CustomType is null;

	/// 是否在对话时间线里显示；对应会话文件的 <c>display</c> 字段。
	/// 目标模式 / 计划模式这类内部上下文注入是 <see langword="false"/>，不占用户气泡。
	public bool Display { get; init; } = true;
}

public sealed record ToolResultMessage : Message
{
	public override string Role => "toolResult";

	public string ToolCallId { get; init; } = string.Empty;

	public string ToolName { get; init; } = string.Empty;

	[JsonIgnore]
	public bool HasToolName { get; init; } = true;

	public IReadOnlyList<ContentBlock> Content { get; init; } = [];

	public JsonNode? Details { get; init; }

	public Usage? Usage { get; init; }

	public bool IsError { get; init; }

	public double? DurationMs { get; init; }
}

public sealed record AssistantMessage : Message
{	public override string Role => "assistant";

	public IReadOnlyList<ContentBlock> Content { get; init; } = [];

	public string Api { get; init; } = string.Empty;

	public string Provider { get; init; } = string.Empty;

	public string Model { get; init; } = string.Empty;

	public string? ResponseModel { get; init; }

	public string? ResponseId { get; init; }

	public string? ProviderThinkingLevel { get; init; }

	public string? ThinkingLevel { get; init; }

	public Usage Usage { get; init; } = new();

	public StopReason StopReason { get; init; } = StopReason.Pending;

	public string? ErrorMessage { get; init; }

	public string? RawStopReason { get; init; }

	public bool? EndTurn { get; init; }

	public double? DurationMs { get; set; }

	public double? FirstTokenMs { get; set; }

	public double? StreamingDurationMs { get; set; }
}

/// 角色不属于 LLM 标准角色的消息（编码代理产生的 <c>bashExecution</c>、
/// <c>custom</c>、<c>branchSummary</c>、<c>compactionSummary</c> 等），或本端口尚未建模的角色。
/// <see cref="Raw"/> 保留原始对象，保证会话文件能无损往返。
public sealed record CustomMessage : Message
{
	public override string Role => CustomRole;

	public string CustomRole { get; init; } = "custom";

	public MessageContent Content { get; init; } = MessageContent.Empty;

	public JsonObject? Raw { get; init; }
}

public enum StopReason
{
	Pending,
	Stop,
	Length,
	ToolUse,
	Error,
	Aborted,
	Deferred,
}

public static class StopReasonExtensions
{
	public static string ToWire(this StopReason reason) => reason switch
	{
		StopReason.Pending => "pending",
		StopReason.Stop => "stop",
		StopReason.Length => "length",
		StopReason.ToolUse => "toolUse",
		StopReason.Error => "error",
		StopReason.Aborted => "aborted",
		StopReason.Deferred => "deferred",
		_ => "pending",
	};

	public static StopReason ParseStopReason(string? value) => value switch
	{
		"stop" => StopReason.Stop,
		"length" => StopReason.Length,
		"toolUse" => StopReason.ToolUse,
		"error" => StopReason.Error,
		"aborted" => StopReason.Aborted,
		"deferred" => StopReason.Deferred,
		_ => StopReason.Pending,
	};
}
