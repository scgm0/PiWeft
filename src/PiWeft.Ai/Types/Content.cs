using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using PiWeft.Ai.Json;

namespace PiWeft.Ai;

/// 助手或用户消息中的一个内容块，对应 <c>TextContent | ThinkingContent | ImageContent | ToolCall</c>
/// （来自 <c>@earendil-works/pi-ai</c>）。序列化时带显式的 <c>type</c> 判别字段。
[JsonConverter(typeof(ContentBlockJsonConverter))]
public abstract record ContentBlock
{
	public abstract string Type { get; }
}

public sealed record TextContent : ContentBlock
{
	public override string Type => "text";

	public string Text { get; init; } = string.Empty;

	public string? TextSignature { get; init; }

	public static TextContent Of(string text) => new() { Text = text };
}

public sealed record ThinkingContent : ContentBlock
{
	public override string Type => "thinking";

	public string Thinking { get; init; } = string.Empty;

	public string? ThinkingSignature { get; init; }

	public bool? Redacted { get; init; }
}

public sealed record ImageContent : ContentBlock
{
	public override string Type => "image";

	public string Data { get; init; } = string.Empty;

	public string MimeType { get; init; } = string.Empty;
}

public sealed record ToolCall : ContentBlock
{
	public override string Type => "toolCall";

	public string Id { get; init; } = string.Empty;

	public string Name { get; init; } = string.Empty;

	public JsonObject Arguments { get; init; } = new();

	/// 模型给出的完整参数不是合法 JSON 对象时为错误文本，<see cref="Arguments"/> 则为空对象。
	/// 工具参数校验会用它失败并把原文反馈给模型。
	public string? ArgumentsError { get; init; }

	public string? ThoughtSignature { get; init; }

	[JsonPropertyName("namespace")]
	public string? Namespace { get; init; }
}

public sealed record TextSignatureV1
{
	public int V { get; init; } = 1;

	public string Id { get; init; } = string.Empty;

	public string? Phase { get; init; }
}
