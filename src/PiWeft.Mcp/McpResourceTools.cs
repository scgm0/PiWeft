using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiWeft.Agent;
using PiWeft.Ai;

using PiWeft.Ai.Json;
namespace PiWeft.Mcp;

/// <summary>
/// pi 暴露的三个资源工具，名称与 Codex、opencode 使用的一致：
/// <c>list_mcp_resources</c>、<c>list_mcp_resource_templates</c> 与 <c>read_mcp_resource</c>。它们接受
/// <c>server</c> 参数，覆盖所有带资源的已连接服务器，因此按这些工具训练过的模型
/// 可以直接使用。
/// </summary>
/// <remarks>
/// 列举结果是 JSON：<c>{server?, resources: [{server, ...}], nextCursor?, errors?}</c>。传 <c>server</c> 可只取
/// 某个服务器的一页（配合 <c>cursor</c> 继续），省略则遍历所有服务器的所有页。MCP 应用界面
/// 资源（<c>ui://</c> URI 与 <c>profile=mcp-app</c> HTML）会被排除：它们是给宿主渲染的用户界面。
/// 
/// </remarks>
public static class McpResourceTools
{
	public const string ListResourcesTool = "list_mcp_resources";
	public const string ListResourceTemplatesTool = "list_mcp_resource_templates";
	public const string ReadResourceTool = "read_mcp_resource";

	/// <summary>面向模型的文本上限，与 pi 的 <c>MCP_OUTPUT_MAX_BYTES</c> 一致。</summary>
	public const int MaxOutputBytes = 20 * 1024;

	/// <summary>某个工具名是否属于这一族。</summary>
	public static bool IsResourceTool(string name) =>
		name is ListResourcesTool or ListResourceTemplatesTool or ReadResourceTool;

	/// <summary>
	/// 构建这三个工具。<paramref name="servers"/> 在调用时读取，因此注册之后才连接（或断开）的
	/// 服务器也能被反映出来。
	/// </summary>
	public static IReadOnlyList<AgentTool> Create(Func<IReadOnlyList<IMcpResourceProvider>> servers) =>
	[
		ListTool(servers),
		ListTemplatesTool(servers),
		ReadTool(servers),
	];

	private static readonly JsonObject ListParameters = new()
	{
		["type"] = "object",
		["properties"] = new JsonObject
		{
			["server"] = new JsonObject
			{
				["type"] = "string",
				["description"] = "MCP server name. Omit to list every server with resources.",
			},
			["cursor"] = new JsonObject
			{
				["type"] = "string",
				["description"] = "Opaque cursor from a previous call with the same server; omit for the first page.",
			},
		},
		["additionalProperties"] = false,
	};

	private static readonly JsonObject ReadParameters = new()
	{
		["type"] = "object",
		["properties"] = new JsonObject
		{
			["server"] = new JsonObject
			{
				["type"] = "string",
				["description"] = "MCP server name exactly as configured. Must match the 'server' field returned by list_mcp_resources.",
			},
			["uri"] = new JsonObject
			{
				["type"] = "string",
				["description"] = "Resource URI to read. Must be one of the URIs returned by list_mcp_resources.",
			},
		},
		["required"] = new JsonArray("server", "uri"),
		["additionalProperties"] = false,
	};

	private static AgentTool ListTool(Func<IReadOnlyList<IMcpResourceProvider>> servers) => new()
	{
		Name = ListResourcesTool,
		Label = ListResourcesTool,
		Description =
			"Lists resources provided by MCP servers. Resources allow servers to share data that provides context to " +
			"language models, such as files, database schemas, or application-specific information. Prefer resources " +
			"over web search when possible.",
		Parameters = (JsonObject)ListParameters.DeepClone(),
		Execute = async invocation =>
		{
			var (serverName, cursor) = ReadListArguments(invocation.Arguments);
			var payload = await ListAsync(
				servers(),
				serverName,
				cursor,
				"resources",
				static (provider, token) => provider.ListResourcesAsync(token),
				static (provider, page) => page
					.Where(resource => !IsMcpApp(resource.Uri, resource.MimeType))
					.Select(resource => (JsonObject)Describe(provider.ConfiguredName, resource)),
				invocation.CancellationToken).ConfigureAwait(false);

			return ToolResult(ListResourcesTool, serverName, payload);
		},
	};

	private static AgentTool ListTemplatesTool(Func<IReadOnlyList<IMcpResourceProvider>> servers) => new()
	{
		Name = ListResourceTemplatesTool,
		Label = ListResourceTemplatesTool,
		Description =
			"Lists resource templates provided by MCP servers. Parameterized resource templates allow servers to share " +
			"data that takes parameters and provides context to language models, such as files, database schemas, or " +
			"application-specific information. Prefer resource templates over web search when possible.",
		Parameters = (JsonObject)ListParameters.DeepClone(),
		Execute = async invocation =>
		{
			var (serverName, cursor) = ReadListArguments(invocation.Arguments);
			var payload = await ListAsync(
				servers(),
				serverName,
				cursor,
				"resourceTemplates",
				static (provider, token) => provider.ListResourceTemplatesAsync(token),
				static (provider, page) => page
					.Where(template => !IsMcpApp(template.UriTemplate, template.MimeType))
					.Select(template => (JsonObject)Describe(provider.ConfiguredName, template)),
				invocation.CancellationToken).ConfigureAwait(false);

			return ToolResult(ListResourceTemplatesTool, serverName, payload);
		},
	};

	private static AgentTool ReadTool(Func<IReadOnlyList<IMcpResourceProvider>> servers) => new()
	{
		Name = ReadResourceTool,
		Label = ReadResourceTool,
		Description = "Read a specific resource from an MCP server given the server name and resource URI.",
		Parameters = (JsonObject)ReadParameters.DeepClone(),
		Execute = async invocation =>
		{
			var serverName = StringArgument(invocation.Arguments, "server")
				?? throw new McpToolArgumentException("server must be provided");
			var uri = StringArgument(invocation.Arguments, "uri")
				?? throw new McpToolArgumentException("uri must be provided");

			var provider = FindProvider(servers(), serverName);
			var contents = await provider.ReadResourceAsync(uri, invocation.CancellationToken).ConfigureAwait(false);

			// 多个内容（例如一个目录）会各自标注自己的 URI。
			var blocks = new List<ContentBlock>();
			foreach (var content in contents)
			{
				if (contents.Count > 1)
				{
					blocks.Add(new TextContent { Text = $"{content.Uri}:" });
				}

				blocks.AddRange(ToModelContent(content));
			}

			if (blocks.Count == 0)
			{
				blocks.Add(new TextContent { Text = $"Resource {uri} is empty." });
			}

			var limited = McpOutput.Limit(blocks);
			var listed = new List<JsonNode?>();
			foreach (var content in contents)
			{
				listed.Add(Describe(content));
			}

			var payload = new JsonObject
			{
				["server"] = provider.ConfiguredName,
				["uri"] = uri,
				["contents"] = new JsonArray([.. listed]),
			};

			return new AgentToolResult
			{
				Content = limited,
				StructuredContent = payload.DeepClone(),
				Details = new JsonObject
				{
					["server"] = provider.ConfiguredName,
					["tool"] = ReadResourceTool,
				},
			};
		},
	};

	private static (string? Server, string? Cursor) ReadListArguments(JsonObject arguments) =>
		(StringArgument(arguments, "server"), StringArgument(arguments, "cursor"));

	private static string? StringArgument(JsonObject arguments, string name)
	{
		var node = arguments[name];
		if (node is null || node.GetValueKind() == JsonValueKind.Null)
		{
			return null;
		}

		if (node.GetValueKind() != JsonValueKind.String)
		{
			throw new McpToolArgumentException($"{name} must be a string");
		}

		var value = node.GetValue<string>().Trim();
		return value.Length == 0 ? null : value;
	}

	private static IMcpResourceProvider FindProvider(IReadOnlyList<IMcpResourceProvider> servers, string name)
	{
		foreach (var provider in servers)
		{
			if (provider.ConfiguredName == name)
			{
				return provider;
			}
		}

		var available = string.Join(", ", servers.Select(static provider => provider.ConfiguredName));
		throw new McpToolArgumentException(
			$"MCP server \"{name}\" has no resources" +
			(available.Length > 0 ? $". Servers with resources: {available}" : string.Empty));
	}

	/// <summary>某个服务器的一页，或所有服务器的所有页，按名称排序。</summary>
	private static async Task<JsonObject> ListAsync<T>(
		IReadOnlyList<IMcpResourceProvider> servers,
		string? serverName,
		string? cursor,
		string key,
		Func<IMcpResourceProvider, CancellationToken, Task<IReadOnlyList<T>>> all,
		Func<IMcpResourceProvider, IReadOnlyList<T>, IEnumerable<JsonObject>> describe,
		CancellationToken cancellationToken)
	{
		if (serverName is not null)
		{
			var provider = FindProvider(servers, serverName);
			// 指定服务器时本应返回一页；本客户端会自行翻页，因此传入游标只是
			// 再返回一遍完整列表，而不是报错。
			var items = await all(provider, cancellationToken).ConfigureAwait(false);

			var page = new List<JsonNode?>();
			foreach (var item in describe(provider, items))
			{
				page.Add(item);
			}

			return new JsonObject
			{
				["server"] = provider.ConfiguredName,
				[key] = new JsonArray([.. page]),
			};
		}

		if (cursor is not null)
		{
			throw new McpToolArgumentException("cursor can only be used when a server is specified");
		}

		var ordered = servers.OrderBy(static provider => provider.ConfiguredName, StringComparer.Ordinal).ToList();
		var results = await Task.WhenAll(ordered.Select(async provider =>
		{
			try
			{
				return (Provider: provider, Items: await all(provider, cancellationToken).ConfigureAwait(false), Error: (string?)null);
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				return (Provider: provider, Items: Array.Empty<T>(), Error: (string?)ex.Message);
			}
		})).ConfigureAwait(false);

		var resources = new List<JsonNode?>();
		var errors = new List<JsonNode?>();

		foreach (var result in results)
		{
			if (result.Error is { Length: > 0 } reason)
			{
				errors.Add(new JsonObject
				{
					["server"] = result.Provider.ConfiguredName,
					["error"] = reason,
				});

				continue;
			}

			foreach (var item in describe(result.Provider, result.Items))
			{
				resources.Add(item);
			}
		}

		var payload = new JsonObject { [key] = new JsonArray([.. resources]) };
		if (errors.Count > 0)
		{
			payload["errors"] = new JsonArray([.. errors]);
		}

		return payload;
	}

	private static AgentToolResult ToolResult(string tool, string? server, JsonObject payload)
	{
		var text = payload.ToJsonString(PiJson.NodeOptions);

		var limited = McpOutput.Limit([new TextContent { Text = text }]);
		return new AgentToolResult
		{
			Content = limited,
			StructuredContent = payload.DeepClone(),
			Details = new JsonObject
			{
				["server"] = server ?? string.Empty,
				["tool"] = tool,
			},
		};
	}

	/// <summary>带服务器标记的资源项，不含 <c>_meta</c> 与图标。</summary>
	private static JsonNode Describe(string server, McpResource resource)
	{
		var node = WithoutMeta(resource.Raw) ?? new JsonObject();
		node["server"] = server;
		node["uri"] = resource.Uri;
		node["name"] = resource.Name;
		return node;
	}

	private static JsonNode Describe(string server, McpResourceTemplate template)
	{
		var node = WithoutMeta(template.Raw) ?? new JsonObject();
		node["server"] = server;
		node["uriTemplate"] = template.UriTemplate;
		node["name"] = template.Name;
		return node;
	}

	private static JsonNode Describe(McpResourceContent content)
	{
		var node = WithoutMeta(content.Raw) ?? new JsonObject();
		node["uri"] = content.Uri;

		if (content.MimeType is { Length: > 0 } mimeType)
		{
			node["mimeType"] = mimeType;
		}

		return node;
	}

	/// <summary>去掉 <c>_meta</c> 与图标 —— 只有能渲染它们宿主才用得上。</summary>
	private static JsonObject? WithoutMeta(JsonObject? raw)
	{
		if (raw is null)
		{
			return null;
		}

		var clone = (JsonObject)raw.DeepClone();
		clone.Remove("_meta");
		clone.Remove("icons");
		return clone;
	}

	/// <summary>该项是否为 MCP 应用界面 —— 对模型没有意义。</summary>
	internal static bool IsMcpApp(string? uri, string? mimeType)
	{
		if (uri is not null && uri.StartsWith("ui://", StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}

		if (mimeType is null)
		{
			return false;
		}

		var separator = mimeType.IndexOf(';');
		var type = (separator < 0 ? mimeType : mimeType[..separator]).Trim();
		return type.Equals("profile=mcp-app", StringComparison.OrdinalIgnoreCase) ||
			mimeType.Contains("profile=mcp-app", StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>某个资源块面向模型的内容。</summary>
	internal static IReadOnlyList<ContentBlock> ToModelContent(McpResourceContent content)
	{
		if (content.Text is not null)
		{
			return [new TextContent { Text = content.Text }];
		}

		if (content.Blob is null)
		{
			return [new TextContent { Text = $"[Resource {content.Uri} has no content]" }];
		}

		var mimeType = content.MimeType;

		// 图片保持为图片，视觉模型才能查看。
		if (mimeType is not null && mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
		{
			return [new ImageContent { Data = content.Blob, MimeType = mimeType }];
		}

		byte[] data;
		try
		{
			data = Convert.FromBase64String(content.Blob);
		}
		catch (FormatException)
		{
			return [new TextContent { Text = $"[Resource {content.Uri} has an invalid base64 blob]" }];
		}

		if (IsTextMimeType(mimeType))
		{
			return [new TextContent { Text = Encoding.UTF8.GetString(data) }];
		}

		var kind = $"{mimeType ?? "unknown type"}, {FormatSize(data.Length)}";

		try
		{
			var path = Path.Combine(Path.GetTempPath(), $"mcp-resource-{Guid.NewGuid():N}{ExtensionOf(content.Uri)}");
			File.WriteAllBytes(path, data);
			return [new TextContent { Text = $"[Binary resource {content.Uri} ({kind}) saved to {path}]" }];
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			return [new TextContent { Text = $"[Binary resource {content.Uri} ({kind}) could not be saved: {ex.Message}]" }];
		}
	}

	/// <summary>这些类型的 blob 按文本展示。</summary>
	private static bool IsTextMimeType(string? mimeType)
	{
		if (mimeType is null)
		{
			return false;
		}

		var separator = mimeType.IndexOf(';');
		var type = (separator < 0 ? mimeType : mimeType[..separator]).Trim().ToLowerInvariant();

		return type.StartsWith("text/", StringComparison.Ordinal) ||
			type == "application/json" ||
			type.EndsWith("+json", StringComparison.Ordinal) ||
			type.EndsWith("+xml", StringComparison.Ordinal);
	}

	/// <summary>保存 blob 时的文件扩展名：取 URI 末尾的扩展名，否则用 <c>.bin</c>。</summary>
	private static string ExtensionOf(string uri)
	{
		var path = Uri.TryCreate(uri, UriKind.Absolute, out var parsed) ? parsed.AbsolutePath : uri;
		var lastDot = path.LastIndexOf('.');

		if (lastDot < 0 || path.Length - lastDot > 9)
		{
			return ".bin";
		}

		var extension = path[lastDot..];
		return extension.All(static c => char.IsAsciiLetterOrDigit(c) || c == '.') ? extension : ".bin";
	}

	internal static string FormatSize(long bytes) => bytes switch
	{
		< 1024 => $"{bytes} B",
		< 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
		_ => $"{bytes / (1024.0 * 1024):0.#} MB",
	};
}

/// <summary>传给 MCP 资源工具的非法参数。</summary>
public sealed class McpToolArgumentException(string message) : Exception(message);

/// <summary>把面向模型的 MCP 输出限制在字节预算内，行为与 pi 的“截断并另存”一致。</summary>
internal static class McpOutput
{
	/// <summary>
	/// 合并后的文本超过 <see cref="McpResourceTools.MaxOutputBytes"/> 时从中间截断，
	/// 把完整文本存到临时文件并告知模型路径。图片块保留。
	/// </summary>
	public static IReadOnlyList<ContentBlock> Limit(IReadOnlyList<ContentBlock> content)
	{
		var combined = string.Concat(content.OfType<TextContent>().Select(static block => block.Text));
		var bytes = Encoding.UTF8.GetByteCount(combined);

		if (bytes <= McpResourceTools.MaxOutputBytes)
		{
			return content;
		}

		var (head, tail, totalLines) = TruncateMiddle(combined);
		var where = SaveFullOutput(combined);
		var tokens = bytes / 4;

		var text =
			$"Warning: truncated output (original token count: {tokens})\n" +
			$"Total output lines: {totalLines}\n\n{head}\n\n[…truncated…]\n\n{tail}\n\n{where}";

		var limited = new List<ContentBlock> { new TextContent { Text = text } };
		limited.AddRange(content.Where(static block => block is ImageContent));
		return limited;
	}

	private static (string Head, string Tail, int TotalLines) TruncateMiddle(string text)
	{
		var half = McpResourceTools.MaxOutputBytes / 2;
		var lines = text.Count(static c => c == '\n') + 1;

		var headChars = TakeChars(text, half, fromEnd: false);
		var tailChars = TakeChars(text, half, fromEnd: true);

		return (text[..headChars], text[^tailChars..], lines);
	}

	/// <summary>在不切断字符的前提下，<paramref name="maxBytes"/> 能容纳多少字符。</summary>
	private static int TakeChars(string text, int maxBytes, bool fromEnd)
	{
		var taken = 0;
		var bytes = 0;

		while (taken < text.Length && bytes < maxBytes)
		{
			var index = fromEnd ? text.Length - 1 - taken : taken;
			var size = Encoding.UTF8.GetByteCount(text.AsSpan(index, 1));
			if (bytes + size > maxBytes)
			{
				break;
			}

			bytes += size;
			taken++;
		}

		return taken;
	}

	private static string SaveFullOutput(string text)
	{
		try
		{
			var path = Path.Combine(Path.GetTempPath(), $"mcp-output-{Guid.NewGuid():N}.txt");
			File.WriteAllText(path, text);
			return $"[Full output: {path} (read it with offset/limit)]";
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			return $"[Could not save the full output: {ex.Message}]";
		}
	}
}
