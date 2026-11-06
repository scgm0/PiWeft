using System.Text.Json.Nodes;
using PiWeft.Ai;

namespace PiWeft.Agent;

public enum AgentQueueMode
{
	All,

	OneAtATime,
}

public enum ToolExecutionMode
{
	Sequential,

	Parallel,
}

public sealed record AgentToolResult
{
	public IReadOnlyList<ContentBlock> Content { get; init; } = [];

	public JsonNode? Details { get; init; }

	public JsonNode? StructuredContent { get; init; }

	public Usage? Usage { get; init; }

	public bool IsError { get; init; }

	public bool Terminate { get; init; }

	public static AgentToolResult Text(string text, JsonNode? details = null) =>
		new() { Content = [new TextContent { Text = text }], Details = details };

	public static AgentToolResult Error(string message, JsonNode? details = null) =>
		new() { Content = [new TextContent { Text = message }], Details = details, IsError = true };
}

public sealed record AgentToolInvocation
{
	public required string ToolCallId { get; init; }

	public required string ToolName { get; init; }

	public required JsonObject Arguments { get; init; }

	public CancellationToken CancellationToken { get; init; }

	public Action<AgentToolResult>? OnUpdate { get; init; }
}

public static class ToolExposure
{
	public const string Direct = "direct";

	public const string Codemode = "codemode";

	public const string Deferred = "deferred";

	public const string Hidden = "hidden";

	/// 声明给模型但不可被其它工具调用（pi 的 <c>model-only</c>）。
	public const string ModelOnly = "model-only";
}

public sealed record AgentTool
{
	public required string Name { get; init; }

	public required string Description { get; init; }

	public required string Label { get; init; }

	public JsonObject Parameters { get; init; } = new();

	/// 声明给模型的方式，取值见 <see cref="ToolExposure"/>。非 <c>direct</c> 的工具仍可执行
	/// （codemode 脚本或 <c>tool_search</c>），只是默认不出现在请求的工具声明里。
	public string Exposure { get; init; } = ToolExposure.Direct;

	public string? Namespace { get; init; }

	public IReadOnlyList<string>? NamespaceAliases { get; init; }

	public string? NamespaceDescription { get; init; }

	public string? NamespaceInstructions { get; init; }

	/// 结构化输出（<see cref="AgentToolResult.StructuredContent"/>）的 JSON Schema。
	/// 声明后，codemode 脚本里的嵌套调用解析为该结构，不按文本处理。
	public JsonObject? OutputSchema { get; init; }

	/// 受约束采样声明（见 <see cref="PiWeft.Ai.ConstrainedSampling"/>）。
	/// 声明后请求里的参数 schema 会被改写成 provider 的严格模式子集；
	/// 不声明就按普通 schema 发送。
	public JsonNode? ConstrainedSampling { get; init; }

	public ToolExecutionMode? ExecutionMode { get; init; }

	public Func<JsonObject, JsonObject>? PrepareArguments { get; init; }

	public required Func<AgentToolInvocation, Task<AgentToolResult>> Execute { get; init; }

	public Tool ToDeclaration() => new()
	{
		Name = Name,
		Description = Description,
		Parameters = Parameters,
		ConstrainedSampling = ConstrainedSampling,
	};
}

public sealed record AgentToolCallOutcome
{
	public required ToolCall ToolCall { get; init; }

	public required AgentToolResult Result { get; init; }

	public bool IsError { get; init; }

	public double? DurationMs { get; init; }
}

public sealed record BeforeToolCallResult
{
	public bool Block { get; init; }

	public string? Reason { get; init; }

	public bool Terminate { get; init; }
}

public sealed record AfterToolCallResult
{
	public IReadOnlyList<ContentBlock>? Content { get; init; }

	public JsonNode? Details { get; init; }

	public bool HasDetails { get; init; }

	public JsonNode? StructuredContent { get; init; }

	public bool HasStructuredContent { get; init; }

	public bool? IsError { get; init; }

	public Usage? Usage { get; init; }

	public bool? Terminate { get; init; }
}

public sealed record BeforeToolCallContext
{
	public required AssistantMessage AssistantMessage { get; init; }

	public required ToolCall ToolCall { get; init; }

	public required JsonObject Arguments { get; init; }

	public required AgentContext Context { get; init; }
}

public sealed record AfterToolCallContext
{
	public required AssistantMessage AssistantMessage { get; init; }

	public required ToolCall ToolCall { get; init; }

	public required JsonObject Arguments { get; init; }

	public required AgentToolResult Result { get; init; }

	public bool IsError { get; init; }

	public required AgentContext Context { get; init; }
}

public sealed class AgentContext
{
	public List<Message> Messages { get; init; } = [];

	public IReadOnlyList<AgentTool>? Tools { get; init; }

	public IReadOnlyList<AgentTool>? DeclaredTools { get; init; }

	/// 运行时的工具集来源。代理设置它之后，<c>tool_search</c> 等工具在回合中加载的声明
	/// 会立即对后续请求与工具执行可见；手工构造的上下文可以只设 <see cref="Tools"/>。
	public Func<IReadOnlyList<AgentTool>>? ToolProvider { get; init; }

	public Func<IReadOnlyList<AgentTool>>? DeclaredToolProvider { get; init; }

	public IReadOnlyList<AgentTool> ResolveTools() => ToolProvider?.Invoke() ?? Tools ?? [];

	public IReadOnlyList<AgentTool> ResolveDeclaredTools() =>
		DeclaredToolProvider?.Invoke() ?? DeclaredTools ?? ResolveTools();
}
