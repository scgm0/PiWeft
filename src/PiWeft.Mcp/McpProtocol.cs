using System.Text.Json;
using System.Text.Json.Nodes;

namespace PiWeft.Mcp;

/// <summary>连接的一端，可以执行 MCP 工具调用并重新列举它们。</summary>
public interface IMcpToolInvoker
{
	/// <summary>调用服务器声明的工具。</summary>
	Task<McpCallResult> CallToolAsync(string name, JsonObject arguments, CancellationToken cancellationToken);

	/// <summary>列出服务器的工具，并跟随分页游标。</summary>
	Task<IReadOnlyList<McpTool>> ListToolsAsync(CancellationToken cancellationToken);
}

/// <summary>stdio 与 HTTP 两种传输共用的解码逻辑。</summary>
internal static class McpProtocol
{
	public static List<McpTool> DecodeTools(JsonObject result)
	{
		var tools = new List<McpTool>();
		if (result["tools"] is not JsonArray array)
		{
			return tools;
		}

		foreach (var node in array)
		{
			if (node is not JsonObject tool || tool["name"]?.GetValue<string>() is not { Length: > 0 } name)
			{
				continue;
			}

			tools.Add(new McpTool
			{
				Name = name,
				Description = tool["description"]?.GetValue<string>() ?? string.Empty,
				InputSchema = tool["inputSchema"] as JsonObject ?? new JsonObject { ["type"] = "object" },
				OutputSchema = tool["outputSchema"] as JsonObject,
			});
		}

		return tools;
	}

	public static McpCallResult DecodeCallResult(JsonObject result)
	{
		var content = new List<Ai.ContentBlock>();

		if (result["content"] is JsonArray blocks)
		{
			foreach (var node in blocks)
			{
				if (node is not JsonObject block)
				{
					continue;
				}

				switch (block["type"]?.GetValue<string>())
				{
					case "text":
						content.Add(new Ai.TextContent { Text = block["text"]?.GetValue<string>() ?? string.Empty });
						break;
					case "image":
						content.Add(new Ai.ImageContent
						{
							Data = block["data"]?.GetValue<string>() ?? string.Empty,
							MimeType = block["mimeType"]?.GetValue<string>() ?? "image/png",
						});
						break;
				}
			}
		}

		return new McpCallResult
		{
			Content = content,
			IsError = result["isError"]?.GetValue<bool>() ?? false,
			StructuredContent = result["structuredContent"],
			Raw = result.DeepClone(),
		};
	}

	public static (List<McpResource> Items, string? NextCursor) DecodeResources(JsonObject result)
	{
		var items = new List<McpResource>();

		if (result["resources"] is JsonArray array)
		{
			foreach (var node in array)
			{
				if (node is not JsonObject resource || StringOf(resource, "uri") is not { Length: > 0 } uri)
				{
					continue;
				}

				items.Add(new McpResource
				{
					Uri = uri,
					Name = StringOf(resource, "name") ?? string.Empty,
					Title = StringOf(resource, "title"),
					Description = StringOf(resource, "description"),
					MimeType = StringOf(resource, "mimeType"),
					Size = resource["size"]?.GetValueKind() == JsonValueKind.Number ? resource["size"]!.GetValue<long>() : null,
					Raw = resource.DeepClone() as JsonObject,
				});
			}
		}

		return (items, StringOf(result, "nextCursor"));
	}

	public static (List<McpResourceTemplate> Items, string? NextCursor) DecodeResourceTemplates(JsonObject result)
	{
		var items = new List<McpResourceTemplate>();

		if (result["resourceTemplates"] is JsonArray array)
		{
			foreach (var node in array)
			{
				if (node is not JsonObject template || StringOf(template, "uriTemplate") is not { Length: > 0 } uriTemplate)
				{
					continue;
				}

				items.Add(new McpResourceTemplate
				{
					UriTemplate = uriTemplate,
					Name = StringOf(template, "name") ?? string.Empty,
					Title = StringOf(template, "title"),
					Description = StringOf(template, "description"),
					MimeType = StringOf(template, "mimeType"),
					Raw = template.DeepClone() as JsonObject,
				});
			}
		}

		return (items, StringOf(result, "nextCursor"));
	}

	public static List<McpResourceContent> DecodeResourceContents(JsonObject result)
	{
		var contents = new List<McpResourceContent>();

		if (result["contents"] is JsonArray array)
		{
			foreach (var node in array)
			{
				if (node is not JsonObject content)
				{
					continue;
				}

				contents.Add(new McpResourceContent
				{
					Uri = StringOf(content, "uri") ?? string.Empty,
					MimeType = StringOf(content, "mimeType"),
					Text = StringOf(content, "text"),
					Blob = StringOf(content, "blob"),
					Raw = content.DeepClone() as JsonObject,
				});
			}
		}

		return contents;
	}

	public static (List<McpPrompt> Items, string? NextCursor) DecodePrompts(JsonObject result)
	{
		var items = new List<McpPrompt>();

		if (result["prompts"] is JsonArray array)
		{
			foreach (var node in array)
			{
				if (node is not JsonObject prompt || StringOf(prompt, "name") is not { Length: > 0 } name)
				{
					continue;
				}

				var arguments = new List<JsonObject>();
				if (prompt["arguments"] is JsonArray declared)
				{
					foreach (var argument in declared)
					{
						if (argument is JsonObject descriptor)
						{
							arguments.Add(descriptor.DeepClone() as JsonObject ?? new JsonObject());
						}
					}
				}

				items.Add(new McpPrompt
				{
					Name = name,
					Title = StringOf(prompt, "title"),
					Description = StringOf(prompt, "description"),
					Arguments = arguments,
					Raw = prompt.DeepClone() as JsonObject,
				});
			}
		}

		return (items, StringOf(result, "nextCursor"));
	}

	public static McpPromptResult DecodePromptResult(JsonObject result)
	{
		var messages = new List<McpPromptMessage>();

		if (result["messages"] is JsonArray array)
		{
			foreach (var node in array)
			{
				if (node is not JsonObject message)
				{
					continue;
				}

				messages.Add(new McpPromptMessage
				{
					Role = StringOf(message, "role") ?? "user",
					Content = DecodePromptContent(message["content"]),
				});
			}
		}

		return new McpPromptResult
		{
			Description = StringOf(result, "description"),
			Messages = messages,
			Raw = result.DeepClone() as JsonObject,
		};
	}

	/// <summary>提示内容既可能是单个块，也可能是块列表。</summary>
	private static List<Ai.ContentBlock> DecodePromptContent(JsonNode? content)
	{
		var blocks = new List<Ai.ContentBlock>();

		if (content is JsonArray array)
		{
			foreach (var node in array)
			{
				AddPromptBlock(blocks, node as JsonObject);
			}

			return blocks;
		}

		AddPromptBlock(blocks, content as JsonObject);
		return blocks;
	}

	private static void AddPromptBlock(List<Ai.ContentBlock> blocks, JsonObject? block)
	{
		switch (block?["type"]?.GetValue<string>())
		{
			case "text":
				blocks.Add(new Ai.TextContent { Text = StringOf(block, "text") ?? string.Empty });
				break;

			case "image":
				blocks.Add(new Ai.ImageContent
				{
					Data = StringOf(block, "data") ?? string.Empty,
					MimeType = StringOf(block, "mimeType") ?? "image/png",
				});
				break;
		}
	}

	/// <summary>为 JSON-RPC 错误对象构建异常。</summary>
	public static McpException Error(JsonObject error) => new(
		error["code"]?.GetValue<int>() ?? -32000,
		error["message"]?.GetValue<string>() ?? "MCP request failed");

	/// <summary>读取 JSON-RPC 记录的 payload；遇到错误对象时抛异常。</summary>
	public static JsonObject ResultOf(JsonObject record) =>
		record["error"] is JsonObject error
			? throw Error(error)
			: record["result"] as JsonObject ?? new JsonObject();

	public static JsonObject? Parse(string json)
	{
		try
		{
			return JsonNode.Parse(json) as JsonObject;
		}
		catch (JsonException)
		{
			return null;
		}
	}

	public static string? StringOf(JsonObject record, string name) =>
		record[name]?.GetValueKind() == JsonValueKind.String ? record[name]!.GetValue<string>() : null;
}
