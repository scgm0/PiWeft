using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiWeft.Ai;

using PiWeft.Ai.Json;
namespace PiWeft.Mcp;

/// <summary>MCP 服务器声明的一个工具。</summary>
public sealed record McpTool
{
	public required string Name { get; init; }

	public string Description { get; init; } = string.Empty;

	/// <summary>工具参数的 JSON Schema。</summary>
	public required JsonObject InputSchema { get; init; }
}

/// <summary><c>tools/call</c> 解码后的结果。</summary>
public sealed record McpCallResult
{
	public IReadOnlyList<ContentBlock> Content { get; init; } = [];

	public bool IsError { get; init; }

	public JsonNode? StructuredContent { get; init; }

	/// <summary>完整的结果 payload，供程序化调用方使用。</summary>
	public JsonNode? Raw { get; init; }
}

/// <summary>
/// Model Context Protocol 客户端，在一对流上使用 JSON-RPC 2.0（stdio 或任意嵌入方式）。
/// 实现本端口需要的子集：<c>initialize</c>、<c>tools/list</c>、<c>tools/call</c>，以及资源、提示与通知。
/// </summary>
public sealed class McpClient : IMcpToolInvoker, IMcpResourceProvider, IAsyncDisposable
{
	public const string ProtocolVersion = "2025-06-18";

	private readonly Stream _output;
	private readonly StreamReader _reader;
	private readonly StreamWriter _writer;
	private readonly Lock _gate = new();
	private readonly Dictionary<int, TaskCompletionSource<JsonObject>> _pending = [];
	private readonly SemaphoreSlim _writeGate = new(1, 1);
	private readonly Task _readLoop;
	private int _nextId;
	private bool _disposed;

	private McpClient(Stream input, Stream output)
	{
		_output = output;
		_reader = new StreamReader(input, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
		_writer = new StreamWriter(output, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
		_readLoop = Task.Run(ReadLoopAsync);
	}

	/// <summary>握手时服务器自报的名称。</summary>
	public string ServerName { get; private set; } = string.Empty;

	public string ServerVersion { get; private set; } = string.Empty;

	/// <summary>配置条目里的名称；连接注册之前为空。</summary>
	public string ConfiguredServerName { get; set; } = string.Empty;

	/// <summary>工具与资源工具用于该服务器的名称。</summary>
	public string ConfiguredName => ConfiguredServerName.Length > 0 ? ConfiguredServerName : ServerName;

	/// <summary>服务器发来的自由格式说明（如果有）。</summary>
	public string? Instructions { get; private set; }

	/// <summary>服务器在握手时声明的能力。</summary>
	public McpServerCapabilities Capabilities { get; private set; } = new();

	/// <summary>资源与提示发现使用的单次请求超时。</summary>
	public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(60);

	/// <summary>
	/// 服务器发来的每个通知都会触发，包括 <c>notifications/tools/list_changed</c> 与
	/// <c>notifications/message</c>。处理函数运行在读取循环上，因此不能阻塞。
	/// </summary>
	public event Action<string, JsonObject?>? NotificationReceived;

	/// <summary>建立连接并完成 MCP 握手。</summary>
	public static async Task<McpClient> ConnectAsync(
		Stream input,
		Stream output,
		CancellationToken cancellationToken = default,
		string clientName = "PiWeft",
		string clientVersion = "0.1.0")
	{
		var client = new McpClient(input, output);

		var parameters = new JsonObject
		{
			["protocolVersion"] = ProtocolVersion,
			["capabilities"] = new JsonObject(),
			["clientInfo"] = new JsonObject
			{
				["name"] = clientName,
				["version"] = clientVersion,
			},
		};

		var result = await client.RequestAsync("initialize", parameters, cancellationToken).ConfigureAwait(false);

		if (result["serverInfo"] is JsonObject info)
		{
			client.ServerName = info["name"]?.GetValue<string>() ?? string.Empty;
			client.ServerVersion = info["version"]?.GetValue<string>() ?? string.Empty;
		}

		client.Instructions = result["instructions"]?.GetValue<string>();
		client.Capabilities = McpServerCapabilities.From(result["capabilities"] as JsonObject);
		client.Notify("notifications/initialized", parameters: null);
		return client;
	}

	/// <summary>发送请求并等待对应的响应。</summary>
	public async Task<JsonObject> RequestAsync(string method, JsonObject? parameters, CancellationToken cancellationToken)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);

		int id;
		var completion = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
		lock (_gate)
		{
			id = ++_nextId;
			_pending[id] = completion;
		}

		try
		{
			var record = new JsonObject
			{
				["jsonrpc"] = "2.0",
				["id"] = id,
				["method"] = method,
			};

			if (parameters is not null)
			{
				record["params"] = parameters;
			}

			await WriteAsync(record, cancellationToken).ConfigureAwait(false);
			return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
		}
		catch
		{
			lock (_gate)
			{
				_pending.Remove(id);
			}

			throw;
		}
	}

	/// <summary>发送通知（无 id、无响应）。</summary>
	public void Notify(string method, JsonObject? parameters)
	{
		if (_disposed)
		{
			return;
		}

		var record = new JsonObject
		{
			["jsonrpc"] = "2.0",
			["method"] = method,
		};

		if (parameters is not null)
		{
			// 做一次克隆，便于调用方复用同一个对象发送多条消息。
			record["params"] = parameters.DeepClone();
		}

		_ = WriteAsync(record, CancellationToken.None);
	}

	/// <summary>列出服务器工具的一页。</summary>
	public async Task<(IReadOnlyList<McpTool> Tools, string? NextCursor)> ListToolsPageAsync(
		string? cursor,
		CancellationToken cancellationToken)
	{
		var parameters = new JsonObject();
		if (cursor is { Length: > 0 })
		{
			parameters["cursor"] = cursor;
		}

		var result = await RequestAsync("tools/list", parameters, cancellationToken).ConfigureAwait(false);
		return (McpProtocol.DecodeTools(result), McpProtocol.StringOf(result, "nextCursor"));
	}

	/// <summary>列出服务器的工具，并跟随分页游标。</summary>
	public async Task<IReadOnlyList<McpTool>> ListToolsAsync(CancellationToken cancellationToken)
	{
		var tools = new List<McpTool>();
		string? cursor = null;

		do
		{
			var (page, next) = await ListToolsPageAsync(cursor, cancellationToken).ConfigureAwait(false);
			tools.AddRange(page);
			cursor = next;
		}
		while (cursor is { Length: > 0 });

		return tools;
	}

	/// <summary>读取 <c>resources/list</c> 的全部页。</summary>
	public async Task<IReadOnlyList<McpResource>> ListResourcesAsync(CancellationToken cancellationToken)
	{
		var resources = new List<McpResource>();
		string? cursor = null;

		do
		{
			var parameters = new JsonObject();
			if (cursor is { Length: > 0 })
			{
				parameters["cursor"] = cursor;
			}

			var result = await RequestAsync("resources/list", parameters, cancellationToken).ConfigureAwait(false);
			var (page, next) = McpProtocol.DecodeResources(result);
			resources.AddRange(page);
			cursor = next;
		}
		while (cursor is { Length: > 0 });

		return resources;
	}

	/// <summary>读取 <c>resources/templates/list</c> 的全部页。</summary>
	public async Task<IReadOnlyList<McpResourceTemplate>> ListResourceTemplatesAsync(CancellationToken cancellationToken)
	{
		var templates = new List<McpResourceTemplate>();
		string? cursor = null;

		do
		{
			var parameters = new JsonObject();
			if (cursor is { Length: > 0 })
			{
				parameters["cursor"] = cursor;
			}

			var result = await RequestAsync("resources/templates/list", parameters, cancellationToken).ConfigureAwait(false);
			var (page, next) = McpProtocol.DecodeResourceTemplates(result);
			templates.AddRange(page);
			cursor = next;
		}
		while (cursor is { Length: > 0 });

		return templates;
	}

	/// <summary>读取一个资源，可能返回多个内容块。</summary>
	public async Task<IReadOnlyList<McpResourceContent>> ReadResourceAsync(string uri, CancellationToken cancellationToken)
	{
		var result = await RequestAsync("resources/read", new JsonObject { ["uri"] = uri }, cancellationToken)
			.ConfigureAwait(false);

		return McpProtocol.DecodeResourceContents(result);
	}

	/// <summary>读取 <c>prompts/list</c> 的全部页。</summary>
	public async Task<IReadOnlyList<McpPrompt>> ListPromptsAsync(CancellationToken cancellationToken)
	{
		var prompts = new List<McpPrompt>();
		string? cursor = null;

		do
		{
			var parameters = new JsonObject();
			if (cursor is { Length: > 0 })
			{
				parameters["cursor"] = cursor;
			}

			var result = await RequestAsync("prompts/list", parameters, cancellationToken).ConfigureAwait(false);
			var (page, next) = McpProtocol.DecodePrompts(result);
			prompts.AddRange(page);
			cursor = next;
		}
		while (cursor is { Length: > 0 });

		return prompts;
	}

	/// <summary>渲染一个提示，并替换其中的参数。</summary>
	public async Task<McpPromptResult> GetPromptAsync(
		string name,
		JsonObject? arguments,
		CancellationToken cancellationToken)
	{
		var parameters = new JsonObject { ["name"] = name };
		if (arguments is { Count: > 0 })
		{
			parameters["arguments"] = arguments.DeepClone();
		}

		var result = await RequestAsync("prompts/get", parameters, cancellationToken).ConfigureAwait(false);
		return McpProtocol.DecodePromptResult(result);
	}

	/// <summary>服务器支持时发送 <c>resources/subscribe</c>；返回是否真的发了。</summary>
	public async Task<bool> SubscribeAsync(string uri, CancellationToken cancellationToken)
	{
		if (!Capabilities.ResourcesSubscribe)
		{
			return false;
		}

		await RequestAsync("resources/subscribe", new JsonObject { ["uri"] = uri }, cancellationToken)
			.ConfigureAwait(false);
		return true;
	}

	/// <summary>调用工具并解码其内容块。</summary>
	public async Task<McpCallResult> CallToolAsync(string name, JsonObject arguments, CancellationToken cancellationToken)
	{
		var parameters = new JsonObject
		{
			["name"] = name,
			["arguments"] = arguments,
		};

		var result = await RequestAsync("tools/call", parameters, cancellationToken).ConfigureAwait(false);
		return McpProtocol.DecodeCallResult(result);
	}

	public async ValueTask DisposeAsync()
	{
		if (_disposed)
		{
			return;
		}

		_disposed = true;

		lock (_gate)
		{
			foreach (var pending in _pending.Values)
			{
				pending.TrySetException(new IOException("The MCP connection was closed"));
			}

			_pending.Clear();
		}

		try
		{
			await _writer.FlushAsync().ConfigureAwait(false);
		}
		catch (IOException)
		{
			// 对端已经消失。
		}

		_reader.Dispose();
		_writer.Dispose();
		_writeGate.Dispose();

		// 给读取循环一点时间去发现流已关闭；调用方仍持有的流可能让它一直阻塞，
		// 而释放操作不能因此被拖住。
		await Task.WhenAny(_readLoop, Task.Delay(TimeSpan.FromMilliseconds(150))).ConfigureAwait(false);
	}

	private async Task WriteAsync(JsonObject record, CancellationToken cancellationToken)
	{
		var line = record.ToJsonString(PiJson.NodeOptions);

		await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			await _writer.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
			await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
		}
		finally
		{
			_writeGate.Release();
		}
	}

	private async Task ReadLoopAsync()
	{
		try
		{
			while (!_disposed)
			{
				var line = await _reader.ReadLineAsync().ConfigureAwait(false);
				if (line is null)
				{
					break;
				}

				if (line.Trim().Length == 0)
				{
					continue;
				}

				Dispatch(line);
			}
		}
		catch (IOException)
		{
			// 传输已关闭。
		}
		catch (ObjectDisposedException)
		{
			// 读取过程中被释放。
		}
		finally
		{
			lock (_gate)
			{
				foreach (var pending in _pending.Values)
				{
					pending.TrySetException(new IOException("The MCP connection closed before a response arrived"));
				}

				_pending.Clear();
			}
		}
	}

	private void Dispatch(string line)
	{
		JsonObject? record;
		try
		{
			record = JsonNode.Parse(line) as JsonObject;
		}
		catch (JsonException)
		{
			return; // 格式错误的帧直接忽略，其它 MCP 客户端也这么做
		}

		if (record is null)
		{
			return;
		}

		var idNode = record["id"];

		// 服务器发起的请求（例如 "ping"）会得到最简响应。
		if (idNode is not null && record["method"] is not null)
		{
			var method = record["method"]!.GetValue<string>();
			var reply = new JsonObject
			{
				["jsonrpc"] = "2.0",
				["id"] = idNode.DeepClone(),
			};

			if (method == "ping")
			{
				reply["result"] = new JsonObject();
			}
			else
			{
				reply["error"] = new JsonObject
				{
					["code"] = -32601,
					["message"] = $"Method not supported by this client: {method}",
				};
			}

			_ = WriteAsync(reply, CancellationToken.None);
			return;
		}

		if (idNode is null)
		{
			// 通知：交给订阅者（工具/资源列表变化、日志消息等）。
			if (McpProtocol.StringOf(record, "method") is { Length: > 0 } notification)
			{
				try
				{
					NotificationReceived?.Invoke(notification, record["params"] as JsonObject);
				}
				catch (Exception)
				{
					// 处理函数出问题也不能让读取循环挂掉。
				}
			}

			return;
		}

		if (idNode.GetValueKind() != JsonValueKind.Number)
		{
			return;
		}

		var id = idNode.GetValue<int>();
		TaskCompletionSource<JsonObject>? completion;
		lock (_gate)
		{
			if (!_pending.Remove(id, out completion))
			{
				return;
			}
		}

		try
		{
			completion.TrySetResult(McpProtocol.ResultOf(record));
		}
		catch (McpException ex)
		{
			completion.TrySetException(ex);
		}
	}
}

/// <summary>MCP 服务器返回的错误。</summary>
public sealed class McpException(int code, string message) : Exception(message)
{
	public int Code { get; } = code;
}
