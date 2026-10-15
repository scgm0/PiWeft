using System.Net.Http.Headers;
using System.Net.ServerSentEvents;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using PiWeft.Ai.Json;
namespace PiWeft.Mcp;

/// <summary>
/// 基于 streamable HTTP 传输的 MCP 客户端：每条 JSON-RPC 消息都以 POST 发往同一个端点，
/// 服务器要么回单个 JSON 对象，要么用 SSE 流回传响应。本端不使用会话状态，
/// 服务器下发的会话 id 若存在则原样回传。
/// </summary>
public sealed class McpHttpClient : IMcpToolInvoker, IMcpResourceProvider, IAsyncDisposable
{
	private readonly HttpClient _http;
	private readonly string _url;
	private readonly IReadOnlyDictionary<string, string> _headers;
	private readonly TimeSpan _timeout;
	private readonly bool _ownsHttpClient;
	private readonly SemaphoreSlim _gate = new(1, 1);
	private int _nextId;

	private McpHttpClient(HttpClient http, string url, IReadOnlyDictionary<string, string>? headers, TimeSpan timeout, bool ownsHttpClient)
	{
		_http = http;
		_url = url;
		_headers = headers ?? new Dictionary<string, string>();
		_timeout = timeout;
		_ownsHttpClient = ownsHttpClient;
	}

	public string ServerName { get; private set; } = string.Empty;

	public string ServerVersion { get; private set; } = string.Empty;

	/// <summary>配置条目里的名称；连接注册之前为空。</summary>
	public string ConfiguredServerName { get; set; } = string.Empty;

	/// <summary>工具与资源工具用于该服务器的名称。</summary>
	public string ConfiguredName => ConfiguredServerName.Length > 0 ? ConfiguredServerName : ServerName;

	/// <summary>服务器发来的自由格式说明（如果有）。</summary>
	public string? Instructions { get; private set; }

	/// <summary>服务器下发的会话 id，后续请求会原样回传。</summary>
	public string? SessionId { get; private set; }

	/// <summary>服务器在握手时声明的能力。</summary>
	public McpServerCapabilities Capabilities { get; private set; } = new();

	/// <summary>资源与提示发现使用的单次请求超时。</summary>
	public TimeSpan Timeout => _timeout;

	/// <summary>建立连接并完成 MCP 握手。</summary>
	public static async Task<McpHttpClient> ConnectAsync(
		string url,
		IReadOnlyDictionary<string, string>? headers,
		TimeSpan timeout,
		HttpClient? httpClient = null,
		CancellationToken cancellationToken = default,
		string clientName = "PiWeft",
		string clientVersion = "0.1.0")
	{
		var client = new McpHttpClient(
			httpClient ?? CreateDefaultClient(),
			url,
			headers,
			timeout,
			ownsHttpClient: httpClient is null);

		var parameters = new JsonObject
		{
			["protocolVersion"] = McpClient.ProtocolVersion,
			["capabilities"] = new JsonObject(),
			["clientInfo"] = new JsonObject
			{
				["name"] = clientName,
				["version"] = clientVersion,
			},
		};

		var response = await client.SendAsync("initialize", parameters, expectResult: true, cancellationToken)
			.ConfigureAwait(false);

		if (response.Result?["serverInfo"] is JsonObject info)
		{
			client.ServerName = McpProtocol.StringOf(info, "name") ?? string.Empty;
			client.ServerVersion = McpProtocol.StringOf(info, "version") ?? string.Empty;
		}

		client.Instructions = response.Result is null ? null : McpProtocol.StringOf(response.Result, "instructions");
		client.Capabilities = McpServerCapabilities.From(response.Result?["capabilities"] as JsonObject);

		// 客户端确认后握手才算完成；该通知不带参数。
		await client.SendAsync("notifications/initialized", parameters: null, expectResult: false, cancellationToken)
			.ConfigureAwait(false);

		return client;
	}

	/// <summary>列出服务器的工具，并跟随分页游标。</summary>
	public Task<IReadOnlyList<McpTool>> ListToolsAsync(CancellationToken cancellationToken) =>
		PageAsync(
			"tools/list",
			"tools",
			static result => (McpProtocol.DecodeTools(result), McpProtocol.StringOf(result, "nextCursor")),
			cancellationToken);

	/// <summary>读取 <c>resources/list</c> 的全部页。</summary>
	public Task<IReadOnlyList<McpResource>> ListResourcesAsync(CancellationToken cancellationToken) =>
		PageAsync("resources/list", "resources", McpProtocol.DecodeResources, cancellationToken);

	/// <summary>读取 <c>resources/templates/list</c> 的全部页。</summary>
	public Task<IReadOnlyList<McpResourceTemplate>> ListResourceTemplatesAsync(CancellationToken cancellationToken) =>
		PageAsync("resources/templates/list", "templates", McpProtocol.DecodeResourceTemplates, cancellationToken);

	/// <summary>读取一个资源。</summary>
	public async Task<IReadOnlyList<McpResourceContent>> ReadResourceAsync(string uri, CancellationToken cancellationToken)
	{
		var response = await SendAsync(
			"resources/read",
			new JsonObject { ["uri"] = uri },
			expectResult: true,
			cancellationToken).ConfigureAwait(false);

		return response.Result is null ? [] : McpProtocol.DecodeResourceContents(response.Result);
	}

	/// <summary>读取 <c>prompts/list</c> 的全部页。</summary>
	public Task<IReadOnlyList<McpPrompt>> ListPromptsAsync(CancellationToken cancellationToken) =>
		PageAsync("prompts/list", "prompts", McpProtocol.DecodePrompts, cancellationToken);

	/// <summary>渲染一个提示。</summary>
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

		var response = await SendAsync("prompts/get", parameters, expectResult: true, cancellationToken)
			.ConfigureAwait(false);

		return response.Result is null
			? new McpPromptResult()
			: McpProtocol.DecodePromptResult(response.Result);
	}

	/// <summary>收集某个列举方法的全部页。<paramref name="pageKey"/> 仅用于可读性。</summary>
	private async Task<IReadOnlyList<T>> PageAsync<T>(
		string method,
		string pageKey,
		Func<JsonObject, (List<T> Items, string? NextCursor)> decode,
		CancellationToken cancellationToken)
	{
		_ = pageKey;
		var items = new List<T>();
		string? cursor = null;

		do
		{
			var parameters = new JsonObject();
			if (cursor is { Length: > 0 })
			{
				parameters["cursor"] = cursor;
			}

			var response = await SendAsync(method, parameters, expectResult: true, cancellationToken)
				.ConfigureAwait(false);

			if (response.Result is null)
			{
				break;
			}

			var (page, next) = decode(response.Result);
			items.AddRange(page);
			cursor = next;
		}
		while (cursor is { Length: > 0 });

		return items;
	}

	/// <summary>调用工具。即使工具报告了错误，协议层的结果仍会返回。</summary>
	public async Task<McpCallResult> CallToolAsync(string name, JsonObject arguments, CancellationToken cancellationToken)
	{
		var parameters = new JsonObject
		{
			["name"] = name,
			["arguments"] = arguments,
		};

		var response = await SendAsync("tools/call", parameters, expectResult: true, cancellationToken)
			.ConfigureAwait(false);

		return McpProtocol.DecodeCallResult(response.Result ?? new JsonObject());
	}

	public ValueTask DisposeAsync()
	{
		_gate.Dispose();

		if (_ownsHttpClient)
		{
			_http.Dispose();
		}

		return ValueTask.CompletedTask;
	}

	private static HttpClient CreateDefaultClient() => new(new SocketsHttpHandler
	{
		PooledConnectionLifetime = TimeSpan.FromMinutes(5),
		ConnectTimeout = TimeSpan.FromSeconds(30),
	})
	{
		Timeout = System.Threading.Timeout.InfiniteTimeSpan,
	};

	private async Task<(JsonObject? Result, string? SessionId)> SendAsync(
		string method,
		JsonObject? parameters,
		bool expectResult,
		CancellationToken cancellationToken)
	{
		await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			timeout.CancelAfter(_timeout);

			var record = new JsonObject
			{
				["jsonrpc"] = "2.0",
				["method"] = method,
			};

			int? id = null;
			if (expectResult)
			{
				id = ++_nextId;
				record["id"] = id;
			}

			if (parameters is not null)
			{
				// 做一次克隆，便于调用方复用同一个对象发起多次请求。
				record["params"] = parameters.DeepClone();
			}

			using var request = new HttpRequestMessage(HttpMethod.Post, _url)
			{
				Content = new StringContent(record.ToJsonString(PiJson.NodeOptions), new UTF8Encoding(false), "application/json"),
			};

			request.Headers.TryAddWithoutValidation("accept", "application/json, text/event-stream");
			request.Headers.TryAddWithoutValidation("user-agent", "PiWeft/0.1.0");

			foreach (var (name, value) in _headers)
			{
				request.Headers.TryAddWithoutValidation(name, value);
			}

			if (SessionId is { Length: > 0 } session)
			{
				request.Headers.TryAddWithoutValidation("mcp-session-id", session);
			}

			using var response = await _http
				.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
				.ConfigureAwait(false);

			SessionId ??= ReadSessionId(response);

			if (!response.IsSuccessStatusCode)
			{
				var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
				throw new McpException(
					(int)response.StatusCode,
					$"MCP server returned {(int)response.StatusCode}: {Truncate(body, 400)}");
			}

			if (!expectResult)
			{
				// 通知以 202 确认，且不带响应体。
				return (null, SessionId);
			}

			var mediaType = response.Content.Headers.ContentType?.MediaType;
			var result = mediaType?.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase) == true
				? await ReadFromEventStreamAsync(response, id!.Value, cancellationToken).ConfigureAwait(false)
				: await ReadFromJsonAsync(response, cancellationToken).ConfigureAwait(false);

			return (result, SessionId);
		}
		finally
		{
			_gate.Release();
		}
	}

	private static async Task<JsonObject?> ReadFromJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
	{
		var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
		var record = McpProtocol.Parse(body)
			?? throw new McpException(-32700, "MCP server returned a body that is not a JSON-RPC record");

		return McpProtocol.ResultOf(record);
	}

	private static async Task<JsonObject?> ReadFromEventStreamAsync(
		HttpResponseMessage response,
		int requestId,
		CancellationToken cancellationToken)
	{
		await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
		var parser = SseParser.Create<JsonDocument?>(stream, static (_, span) => JsonDocument.Parse(span.ToArray()));

		await foreach (var item in parser.EnumerateAsync(cancellationToken).ConfigureAwait(false))
		{
			if (item.Data is null)
			{
				continue;
			}

			using var document = item.Data;
			if (document.RootElement.ValueKind != JsonValueKind.Object)
			{
				continue;
			}

			var record = JsonNode.Parse(document.RootElement.GetRawText()) as JsonObject;
			if (record is null)
			{
				continue;
			}

			// 服务器发起的通知没有 id，本客户端会忽略。
			if (record["id"]?.GetValueKind() != JsonValueKind.Number ||
				record["id"]!.GetValue<int>() != requestId)
			{
				continue;
			}

			return McpProtocol.ResultOf(record);
		}

		throw new McpException(-32000, "MCP event stream ended without a response");
	}

	private static string? ReadSessionId(HttpResponseMessage response)
	{
		foreach (var header in response.Headers)
		{
			if (string.Equals(header.Key, "Mcp-Session-Id", StringComparison.OrdinalIgnoreCase))
			{
				return header.Value.FirstOrDefault();
			}
		}

		return null;
	}

	private static string Truncate(string value, int max) =>
		value.Length <= max ? value : string.Concat(value.AsSpan(0, max), "…");
}
