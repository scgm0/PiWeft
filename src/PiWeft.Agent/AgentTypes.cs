using System.Text.Json.Nodes;
using PiWeft.Ai;

namespace PiWeft.Agent;

/// <summary>一次抽取时投递多少条排队消息。</summary>
public enum AgentQueueMode
{
	/// <summary>清空队列，把所有排队消息一次性注入。</summary>
	All,

	/// <summary>只取最旧的一条，其它留给后续的抽取点。</summary>
	OneAtATime,
}

/// <summary>同一条助手消息里的多个工具调用如何执行。</summary>
public enum ToolExecutionMode
{
	/// <summary>逐个完成“准备 → 执行 → 收尾”后再开始下一个。</summary>
	Sequential,

	/// <summary>按顺序准备，然后允许并行执行多个工具。</summary>
	Parallel,
}

/// <summary>工具产出的最终或阶段性结果。</summary>
public sealed record AgentToolResult
{
	/// <summary>返回给模型的文本或图片内容。</summary>
	public IReadOnlyList<ContentBlock> Content { get; init; } = [];

	/// <summary>供日志或界面渲染用的任意结构化细节。</summary>
	public JsonNode? Details { get; init; }

	/// <summary>供程序化调用方使用的机器可读结果，永远不会发给模型。</summary>
	public JsonNode? StructuredContent { get; init; }

	/// <summary>工具自身产生的用量，不计入主 LLM 的上下文核算。</summary>
	public Usage? Usage { get; init; }

	/// <summary>不抛异常地报告失败；模型会把它当作错误结果。</summary>
	public bool IsError { get; init; }

	/// <summary>提示代理在当前工具批次结束后停下（所有结果必须一致）。</summary>
	public bool Terminate { get; init; }

	/// <summary>成功结果。</summary>
	public static AgentToolResult Text(string text, JsonNode? details = null) =>
		new() { Content = [new TextContent { Text = text }], Details = details };

	/// <summary>错误结果。</summary>
	public static AgentToolResult Error(string message, JsonNode? details = null) =>
		new() { Content = [new TextContent { Text = message }], Details = details, IsError = true };
}

/// <summary>一次工具调用所需的全部输入。</summary>
public sealed record AgentToolInvocation
{
	public required string ToolCallId { get; init; }

	public required string ToolName { get; init; }

	public required JsonObject Arguments { get; init; }

	public CancellationToken CancellationToken { get; init; }

	/// <summary>工具运行期间上报阶段性结果；工具结束后再调用会被忽略。</summary>
	public Action<AgentToolResult>? OnUpdate { get; init; }
}

/// <summary>代理运行时可以执行的工具。</summary>
public sealed record AgentTool
{
	public required string Name { get; init; }

	public required string Description { get; init; }

	/// <summary>界面展示用的可读名称。</summary>
	public required string Label { get; init; }

	/// <summary>工具参数的 JSON Schema。</summary>
	public JsonObject Parameters { get; init; } = new();

	/// <summary>覆盖该工具的执行模式。</summary>
	public ToolExecutionMode? ExecutionMode { get; init; }

	/// <summary>执行前对原始工具参数做的兼容性修正。</summary>
	public Func<JsonObject, JsonObject>? PrepareArguments { get; init; }

	/// <summary>执行工具。失败时要么抛异常，要么返回带 <see cref="AgentToolResult.IsError"/> 的结果。</summary>
	public required Func<AgentToolInvocation, Task<AgentToolResult>> Execute { get; init; }

	/// <summary>面向模型的工具声明。</summary>
	public Tool ToDeclaration() => new() { Name = Name, Description = Description, Parameters = Parameters };
}

/// <summary>一次工具调用经过钩子处理后的最终结果。</summary>
public sealed record AgentToolCallOutcome
{
	public required ToolCall ToolCall { get; init; }

	public required AgentToolResult Result { get; init; }

	public bool IsError { get; init; }

	/// <summary>工具执行耗时（毫秒）；未执行时为空。</summary>
	public double? DurationMs { get; init; }
}

/// <summary><c>beforeToolCall</c> 钩子的返回值。</summary>
public sealed record BeforeToolCallResult
{
	/// <summary>阻止本次调用。</summary>
	public bool Block { get; init; }

	/// <summary>阻止原因，会作为错误结果告诉模型。</summary>
	public string? Reason { get; init; }

	/// <summary>阻止并结束当前运行。</summary>
	public bool Terminate { get; init; }
}

/// <summary><c>afterToolCall</c> 钩子返回的部分覆盖。未给出的字段保持原值。</summary>
public sealed record AfterToolCallResult
{
	public IReadOnlyList<ContentBlock>? Content { get; init; }

	public JsonNode? Details { get; init; }

	/// <summary>是否覆盖 <see cref="Details"/>（区分“设成 null”与“不改”）。</summary>
	public bool HasDetails { get; init; }

	public JsonNode? StructuredContent { get; init; }

	/// <summary>是否覆盖 <see cref="StructuredContent"/>。</summary>
	public bool HasStructuredContent { get; init; }

	public bool? IsError { get; init; }

	public Usage? Usage { get; init; }

	public bool? Terminate { get; init; }
}

/// <summary><c>beforeToolCall</c> 钩子的上下文。</summary>
public sealed record BeforeToolCallContext
{
	public required AssistantMessage AssistantMessage { get; init; }

	public required ToolCall ToolCall { get; init; }

	public required JsonObject Arguments { get; init; }

	public required AgentContext Context { get; init; }
}

/// <summary><c>afterToolCall</c> 钩子的上下文。</summary>
public sealed record AfterToolCallContext
{
	public required AssistantMessage AssistantMessage { get; init; }

	public required ToolCall ToolCall { get; init; }

	public required JsonObject Arguments { get; init; }

	public required AgentToolResult Result { get; init; }

	public bool IsError { get; init; }

	public required AgentContext Context { get; init; }
}

/// <summary>模型可见的会话记录，以及运行时可以执行的工具。</summary>
public sealed class AgentContext
{
	public List<Message> Messages { get; init; } = [];

	public IReadOnlyList<AgentTool>? Tools { get; init; }
}
