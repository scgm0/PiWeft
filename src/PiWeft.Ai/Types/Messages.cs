using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using PiWeft.Ai.Json;

namespace PiWeft.Ai;

/// <summary>
/// 消息内容：既可以是纯字符串，也可以是内容块列表
/// （对应 pi 的 <c>string | (TextContent | ImageContent)[]</c>）。
/// </summary>
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

	/// <summary>Unix 时间戳，单位毫秒。</summary>
	public long Timestamp { get; init; }

	/// <summary>
	/// 读取时未被建模的字段，写回时原样输出。
	/// <para>
	/// pi 与第三方工具会往消息里加字段（codemode 的 <c>nestedCalls</c> 就是一例），
	/// 丢弃它们等于改写用户的历史：<c>fork</c> 或导入后再写回，那些数据就永久没了。
	/// 已知角色的消息按角色收集，未知角色整体落在 <see cref="CustomMessage.Raw"/>。
	/// </para>
	/// </summary>
	[JsonIgnore]
	public JsonObject? Extra { get; init; }
}

public sealed record SystemMessage : Message
{
	public override string Role => "system";

	/// <summary>指令文本。位于首条消息时是基础提示词，之后则是补充指令。</summary>
	public MessageContent Content { get; init; } = MessageContent.Empty;

	/// <summary>具名且有序的提示词章节，按顺序原样拼接在 <see cref="Content"/> 之后。</summary>
	public Dictionary<string, string?>? Sections { get; init; }

	/// <summary>从这个位置开始可用的完整工具定义。</summary>
	public IReadOnlyList<Tool>? ToolsAdded { get; init; }

	/// <summary>从这个位置起不再可用的工具。</summary>
	public IReadOnlyList<ToolReference>? ToolsRemoved { get; init; }

	public string Text => Content.Text ?? string.Empty;
}

public sealed record UserMessage : Message
{
	public override string Role => "user";

	public MessageContent Content { get; init; } = MessageContent.Empty;

	/// <summary>
	/// 扩展注入的消息类型（会话文件里的 <c>customType</c>）；用户手写的消息为 <see langword="null"/>。
	/// 这类消息仍参与模型上下文，但对话里是否显示由 <see cref="Display"/> 决定。
	/// </summary>
	public string? CustomType { get; init; }

	/// <summary>
	/// 是否在对话时间线里显示；对应会话文件的 <c>display</c> 字段。
	/// 目标模式 / 计划模式这类内部上下文注入是 <see langword="false"/>，不占用户气泡。
	/// </summary>
	public bool Display { get; init; } = true;
}

public sealed record ToolResultMessage : Message
{
	public override string Role => "toolResult";

	public string ToolCallId { get; init; } = string.Empty;

	public string ToolName { get; init; } = string.Empty;

	/// <summary>旧的外部会话可能省略 toolName；保留“是否有这个字段”，重写时不凭空补空值。</summary>
	[JsonIgnore]
	public bool HasToolName { get; init; } = true;

	/// <summary>支持文本与图片。</summary>
	public IReadOnlyList<ContentBlock> Content { get; init; } = [];

	public JsonNode? Details { get; init; }

	/// <summary>工具自身产生的用量；不计入主 LLM 的上下文核算。</summary>
	public Usage? Usage { get; init; }

	public bool IsError { get; init; }

	/// <summary>工具执行耗时（毫秒）；旧格式的结果没有该字段。</summary>
	public double? DurationMs { get; init; }
}

public sealed record AssistantMessage : Message
{	public override string Role => "assistant";

	public IReadOnlyList<ContentBlock> Content { get; init; } = [];

	public string Api { get; init; } = string.Empty;

	public string Provider { get; init; } = string.Empty;

	public string Model { get; init; } = string.Empty;

	/// <summary>provider 实际使用的模型，与请求的 <see cref="Model"/> 不同时才有值。</summary>
	public string? ResponseModel { get; init; }

	public string? ResponseId { get; init; }

	public string? ProviderThinkingLevel { get; init; }

	public string? ThinkingLevel { get; init; }

	public Usage Usage { get; init; } = new();

	public StopReason StopReason { get; init; } = StopReason.Pending;

	public string? ErrorMessage { get; init; }

	public string? RawStopReason { get; init; }

	public bool? EndTurn { get; init; }

	/// <summary>从 <see cref="Message.Timestamp"/> 到响应结束的毫秒数。</summary>
	public double? DurationMs { get; set; }

	/// <summary>正文首 delta 的延迟（毫秒）；无正文时回退到首个思考 delta。</summary>
	public double? FirstTokenMs { get; set; }

	/// <summary>首个非空正文/思考 delta 到回复结束的时间（毫秒）。</summary>
	public double? StreamingDurationMs { get; set; }
}

/// <summary>
/// 角色不属于 LLM 标准角色的消息（编码代理产生的 <c>bashExecution</c>、
/// <c>custom</c>、<c>branchSummary</c>、<c>compactionSummary</c> 等），或本端口尚未建模的角色。
/// <see cref="Raw"/> 保留原始对象，保证会话文件能无损往返。
/// </summary>
public sealed record CustomMessage : Message
{
	public override string Role => CustomRole;

	/// <summary>provider 侧的角色字符串，例如 <c>bashExecution</c>。</summary>
	public string CustomRole { get; init; } = "custom";

	/// <summary>消息携带的内容（如果有）。</summary>
	public MessageContent Content { get; init; } = MessageContent.Empty;

	/// <summary>原始 JSON 对象，写回时原样输出。</summary>
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
