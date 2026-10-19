using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using PiWeft.Ai.Json;

namespace PiWeft.Ai;

/// <summary>
/// 助手或用户消息中的一个内容块，对应 <c>TextContent | ThinkingContent | ImageContent | ToolCall</c>
/// （来自 <c>@earendil-works/pi-ai</c>）。序列化时带显式的 <c>type</c> 判别字段。
/// </summary>
[JsonConverter(typeof(ContentBlockJsonConverter))]
public abstract record ContentBlock
{
	/// <summary>写入 JSON 的判别值：<c>text</c>、<c>thinking</c>、<c>image</c> 或 <c>toolCall</c>。</summary>
	public abstract string Type { get; }
}

public sealed record TextContent : ContentBlock
{
	public override string Type => "text";

	public string Text { get; init; } = string.Empty;

	/// <summary>provider 元数据（OpenAI responses 的消息 id，或序列化后的 <see cref="TextSignatureV1"/>）。</summary>
	public string? TextSignature { get; init; }

	public static TextContent Of(string text) => new() { Text = text };
}

public sealed record ThinkingContent : ContentBlock
{
	public override string Type => "thinking";

	public string Thinking { get; init; } = string.Empty;

	/// <summary>provider 私有的不透明推理回放数据。</summary>
	public string? ThinkingSignature { get; init; }

	/// <summary>安全过滤器屏蔽了思考内容时为 true，实际内容存放在 <see cref="ThinkingSignature"/>。</summary>
	public bool? Redacted { get; init; }
}

public sealed record ImageContent : ContentBlock
{
	public override string Type => "image";

	/// <summary>Base64 编码的图片数据（不含 data: 前缀）。</summary>
	public string Data { get; init; } = string.Empty;

	/// <summary>例如 <c>image/jpeg</c> 或 <c>image/png</c>。</summary>
	public string MimeType { get; init; } = string.Empty;
}

public sealed record ToolCall : ContentBlock
{
	public override string Type => "toolCall";

	public string Id { get; init; } = string.Empty;

	public string Name { get; init; } = string.Empty;

	public JsonObject Arguments { get; init; } = new();

	/// <summary>
	/// 模型给出的完整参数不是合法 JSON 对象时为错误文本，<see cref="Arguments"/> 则为空对象。
	/// 工具参数校验会用它失败并把原文反馈给模型。
	/// </summary>
	public string? ArgumentsError { get; init; }

	/// <summary>Google 私有的不透明签名，用于复用思考上下文。</summary>
	public string? ThoughtSignature { get; init; }

	/// <summary>OpenAI Responses 的命名空间，用于调用动态加载或带命名空间的工具。</summary>
	[JsonPropertyName("namespace")]
	public string? Namespace { get; init; }
}

/// <summary>旧版 OpenAI responses 的文本元数据。</summary>
public sealed record TextSignatureV1
{
	public int V { get; init; } = 1;

	public string Id { get; init; } = string.Empty;

	public string? Phase { get; init; }
}
