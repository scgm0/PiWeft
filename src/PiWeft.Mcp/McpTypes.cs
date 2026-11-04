using System.Text.Json.Nodes;
using PiWeft.Ai;

namespace PiWeft.Mcp;

public sealed record McpResource
{
	public required string Uri { get; init; }

	public string Name { get; init; } = string.Empty;

	public string? Title { get; init; }

	public string? Description { get; init; }

	public string? MimeType { get; init; }

	public long? Size { get; init; }

	public JsonObject? Raw { get; init; }
}

public sealed record McpResourceTemplate
{
	public required string UriTemplate { get; init; }

	public string Name { get; init; } = string.Empty;

	public string? Title { get; init; }

	public string? Description { get; init; }

	public string? MimeType { get; init; }

	public JsonObject? Raw { get; init; }
}

public sealed record McpResourceContent
{
	public required string Uri { get; init; }

	public string? MimeType { get; init; }

	public string? Text { get; init; }

	public string? Blob { get; init; }

	public JsonObject? Raw { get; init; }
}

public sealed record McpPrompt
{
	public required string Name { get; init; }

	public string? Title { get; init; }

	public string? Description { get; init; }

	public IReadOnlyList<JsonObject> Arguments { get; init; } = [];

	public JsonObject? Raw { get; init; }
}

public sealed record McpPromptResult
{
	public string? Description { get; init; }

	public IReadOnlyList<McpPromptMessage> Messages { get; init; } = [];

	public JsonObject? Raw { get; init; }
}

public sealed record McpPromptMessage
{
	public required string Role { get; init; }

	public IReadOnlyList<ContentBlock> Content { get; init; } = [];
}

public sealed record McpServerCapabilities
{
	public bool HasTools { get; init; }

	public bool ToolsListChanged { get; init; }

	public bool HasResources { get; init; }

	public bool ResourcesSubscribe { get; init; }

	public bool ResourcesListChanged { get; init; }

	public bool HasPrompts { get; init; }

	public bool PromptsListChanged { get; init; }

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

/// 一个服务器的资源与提示，以及 MCP 客户端要做的分页发现。两种传输都实现了它，
/// 因此资源工具不必关心自己面对的是哪一种。
public interface IMcpResourceProvider
{
	/// 客户端注册时使用的名称（即 <c>mcp.json</c> 的键），用于标记结果并匹配
	/// <c>server</c> 参数。它不是服务器自报的名称。
	string ConfiguredName { get; }

	TimeSpan Timeout { get; }

	McpServerCapabilities Capabilities { get; }

	Task<IReadOnlyList<McpResource>> ListResourcesAsync(CancellationToken cancellationToken);

	Task<IReadOnlyList<McpResourceTemplate>> ListResourceTemplatesAsync(CancellationToken cancellationToken);

	Task<IReadOnlyList<McpResourceContent>> ReadResourceAsync(string uri, CancellationToken cancellationToken);

	Task<IReadOnlyList<McpPrompt>> ListPromptsAsync(CancellationToken cancellationToken);

	Task<McpPromptResult> GetPromptAsync(string name, JsonObject? arguments, CancellationToken cancellationToken);
}
