using System.Text.Json.Nodes;
using PiWeft.Ai;

namespace PiWeft.Mcp;

/// <summary>MCP 服务器声明的资源。</summary>
public sealed record McpResource
{
	public required string Uri { get; init; }

	public string Name { get; init; } = string.Empty;

	public string? Title { get; init; }

	public string? Description { get; init; }

	public string? MimeType { get; init; }

	public long? Size { get; init; }

	/// <summary>原始 JSON，使本端口未建模的字段也能出现在工具输出里。</summary>
	public JsonObject? Raw { get; init; }
}

/// <summary>MCP 服务器声明的参数化资源模板。</summary>
public sealed record McpResourceTemplate
{
	public required string UriTemplate { get; init; }

	public string Name { get; init; } = string.Empty;

	public string? Title { get; init; }

	public string? Description { get; init; }

	public string? MimeType { get; init; }

	public JsonObject? Raw { get; init; }
}

/// <summary><c>resources/read</c> 返回的一个内容块。</summary>
public sealed record McpResourceContent
{
	public required string Uri { get; init; }

	public string? MimeType { get; init; }

	/// <summary>文本型资源的文本内容。</summary>
	public string? Text { get; init; }

	/// <summary>二进制资源的 Base64 内容。</summary>
	public string? Blob { get; init; }

	public JsonObject? Raw { get; init; }
}

/// <summary>MCP 服务器声明的提示。</summary>
public sealed record McpPrompt
{
	public required string Name { get; init; }

	public string? Title { get; init; }

	public string? Description { get; init; }

	/// <summary>参数描述，保留为 JSON —— 这里只关心它们的名称与提示。</summary>
	public IReadOnlyList<JsonObject> Arguments { get; init; } = [];

	public JsonObject? Raw { get; init; }
}

/// <summary>提示渲染后的消息。</summary>
public sealed record McpPromptResult
{
	public string? Description { get; init; }

	/// <summary>渲染出的消息，均已归一化为本端口的内容块。</summary>
	public IReadOnlyList<McpPromptMessage> Messages { get; init; } = [];

	public JsonObject? Raw { get; init; }
}

/// <summary>渲染出的一条提示消息。</summary>
public sealed record McpPromptMessage
{
	public required string Role { get; init; }

	public IReadOnlyList<ContentBlock> Content { get; init; } = [];
}

/// <summary>与本客户端相关的服务器能力。</summary>
public sealed record McpServerCapabilities
{
	public bool HasTools { get; init; }

	public bool ToolsListChanged { get; init; }

	public bool HasResources { get; init; }

	public bool ResourcesSubscribe { get; init; }

	public bool ResourcesListChanged { get; init; }

	public bool HasPrompts { get; init; }

	public bool PromptsListChanged { get; init; }

	/// <summary>服务器原样发来的能力 JSON。</summary>
	public JsonObject? Raw { get; init; }

	public static McpServerCapabilities From(JsonObject? capabilities)
	{
		if (capabilities is null)
		{
			return new McpServerCapabilities();
		}

		var tools = capabilities["tools"] as JsonObject;
		var resources = capabilities["resources"] as JsonObject;
		var prompts = capabilities["prompts"] as JsonObject;

		static bool Flag(JsonObject? section, string name) =>
			section?[name] is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;

		return new McpServerCapabilities
		{
			HasTools = tools is not null,
			ToolsListChanged = Flag(tools, "listChanged"),
			HasResources = resources is not null,
			ResourcesSubscribe = Flag(resources, "subscribe"),
			ResourcesListChanged = Flag(resources, "listChanged"),
			HasPrompts = prompts is not null,
			PromptsListChanged = Flag(prompts, "listChanged"),
			Raw = capabilities.DeepClone() as JsonObject,
		};
	}
}

/// <summary>
/// 一个服务器的资源与提示，以及 MCP 客户端要做的分页发现。两种传输都实现了它，
/// 因此资源工具不必关心自己面对的是哪一种。
/// </summary>
public interface IMcpResourceProvider
{
	/// <summary>
	/// 客户端注册时使用的名称（即 <c>mcp.json</c> 的键），用于标记结果并匹配
	/// <c>server</c> 参数。它不是服务器自报的名称。
	/// </summary>
	string ConfiguredName { get; }

	/// <summary>发现与读取操作的单次请求超时。</summary>
	TimeSpan Timeout { get; }

	McpServerCapabilities Capabilities { get; }

	/// <summary>读取 <c>resources/list</c> 的全部页。</summary>
	Task<IReadOnlyList<McpResource>> ListResourcesAsync(CancellationToken cancellationToken);

	/// <summary>读取 <c>resources/templates/list</c> 的全部页。</summary>
	Task<IReadOnlyList<McpResourceTemplate>> ListResourceTemplatesAsync(CancellationToken cancellationToken);

	/// <summary>按 URI 读取一个资源。</summary>
	Task<IReadOnlyList<McpResourceContent>> ReadResourceAsync(string uri, CancellationToken cancellationToken);

	/// <summary>读取 <c>prompts/list</c> 的全部页。</summary>
	Task<IReadOnlyList<McpPrompt>> ListPromptsAsync(CancellationToken cancellationToken);

	/// <summary>渲染一个提示。</summary>
	Task<McpPromptResult> GetPromptAsync(string name, JsonObject? arguments, CancellationToken cancellationToken);
}
