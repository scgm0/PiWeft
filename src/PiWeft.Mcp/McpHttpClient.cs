using System.Net;
using System.Net.Http.Headers;
using System.Net.ServerSentEvents;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using PiWeft.Ai.Json;
namespace PiWeft.Mcp;

/// <summary>为 MCP 请求提供 OAuth 访问令牌。</summary>
public interface IMcpAccessTokenProvider
{
	/// <summary>取当前可用的访问令牌；没有令牌时返回 <c>null</c>（照常发送匿名请求）。</summary>
	Task<string?> GetTokenAsync(CancellationToken cancellationToken);

	/// <summary>强制刷新令牌，通常在收到 401 之后调用；失败时返回 <c>null</c>。</summary>
	Task<string?> RefreshAsync(CancellationToken cancellationToken);
}

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
	private readonly IMcpAccessTokenProvider? _tokens;
	private readonly SemaphoreSlim _gate = new(1, 1);
	private int _nextId;

	/// <summary>独立通知流的取消源；服务器不支持时为 null。</summary>
	private CancellationTokenSource? _notificationCts;
	private Task? _notificationTask;

	/// <summary>服务器是否接受独立 SSE 通知流（<c>GET</c> + <c>text/event-stream</c>）。</summary>
	private volatile bool _notificationStreamActive;

	/// <summary>
	/// 服务器通过独立流推送的通知（<c>params</c> 可能为 null），
	/// 例如 <c>notifications/tools/list_changed</c>。
	/// </summary>
	public event Action<string, JsonObject?>? NotificationReceived;

	/// <summary>独立通知流当前是否在线；服务器明确拒绝（404/405 等）时为 <c>false</c>。</summary>
	public bool NotificationStreamActive => _notificationStreamActive;

	private McpHttpClient(
		HttpClient http,
		string url,
		IReadOnlyDictionary<string, string>? headers,
		TimeSpan timeout,
		bool ownsHttpClient,
		IMcpAccessTokenProvider? tokens)
	{
		_http = http;
		_url = url;
		_headers = headers ?? new Dictionary<string, string>();
		_timeout = timeout;
		_ownsHttpClient = ownsHttpClient;
		_tokens = tokens;
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
		string clientVersion = "0.1.0",
		IMcpAccessTokenProvider? tokens = null)
	{
		var client = new McpHttpClient(
			httpClient ?? CreateDefaultClient(),
			url,
			headers,
			timeout,
			ownsHttpClient: httpClient is null,
			tokens);

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

		// 打开独立 SSE 通知流：服务器在运行中宣告工具/资源变化时靠它热刷新。
		client.StartNotificationStream();
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

	public async ValueTask DisposeAsync()
	{
		if (_notificationCts is { } notifications)
		{
			try
			{
				await notifications.CancelAsync().ConfigureAwait(false);
			}
			catch (ObjectDisposedException)
			{
				// 已经释放过：忽略。
			}
		}

		if (_notificationTask is { } task)
		{
			try
			{
				await task.ConfigureAwait(false);
			}
			catch (Exception exception) when (exception is OperationCanceledException or IOException or HttpRequestException or JsonException or ObjectDisposedException)
			{
				// 流被取消或连接断开：关闭时不需要上报。
			}
		}

		_notificationCts?.Dispose();
		_notificationCts = null;
		_notificationTask = null;

		_gate.Dispose();

		if (_ownsHttpClient)
		{
			_http.Dispose();
		}
	}

	/// <summary>
	/// 启动独立通知流。它与请求-响应共用同一个端点，但必须绕开 <c>_gate</c> ——
	/// 长连接会永久占住那把锁，让所有请求饿死。
	/// </summary>
	private void StartNotificationStream()
	{
		var notifications = new CancellationTokenSource();
		_notificationCts = notifications;
		_notificationTask = Task.Run(() => RunNotificationStreamAsync(notifications.Token), CancellationToken.None);
	}

	private async Task RunNotificationStreamAsync(CancellationToken cancellationToken)
	{
		var attempt = 0;
		while (!cancellationToken.IsCancellationRequested)
		{
			try
			{
				using var request = new HttpRequestMessage(HttpMethod.Get, _url);
				request.Headers.TryAddWithoutValidation("accept", "text/event-stream");
				request.Headers.TryAddWithoutValidation("user-agent", "PiWeft/0.1.0");

				foreach (var (name, value) in _headers)
				{
					request.Headers.TryAddWithoutValidation(name, value);
				}

				if (SessionId is { Length: > 0 } session)
				{
					request.Headers.TryAddWithoutValidation("mcp-session-id", session);
				}

				await ApplyAuthorizationAsync(request, cancellationToken).ConfigureAwait(false);

				using var response = await _http
					.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
					.ConfigureAwait(false);

				if (IsUnsupported(response.StatusCode))
				{
					// 服务器不支持独立流（404/405/204 等）：静默降级到请求-响应模式。
					_notificationStreamActive = false;
					return;
				}

				if (response.StatusCode == HttpStatusCode.Unauthorized)
				{
					// 主动刷新一次令牌：应用空闲时没有 POST 请求去触发刷新，
					// 否则这条流会一直用过期令牌拿 401，永远恢复不了。
					_notificationStreamActive = false;
					if (_tokens is not null)
					{
						await _tokens.RefreshAsync(cancellationToken).ConfigureAwait(false);
					}

					attempt++;
					if (!await DelayAsync(attempt, cancellationToken).ConfigureAwait(false))
					{
						return;
					}

					continue;
				}

				if (!response.IsSuccessStatusCode)
				{
					_notificationStreamActive = false;
					attempt++;
					if (!await DelayAsync(attempt, cancellationToken).ConfigureAwait(false))
					{
						return;
					}

					continue;
				}

				_notificationStreamActive = true;
				attempt = 0;

				await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
				var parser = SseParser.Create<JsonDocument?>(stream, ParseFrame);

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

					if (JsonNode.Parse(document.RootElement.GetRawText()) is JsonObject record)
					{
						DispatchNotification(record);
					}
				}

				// 服务器把流正常关闭（例如代理回了 200 + 空正文立刻断开）时也必须退避，
				// 否则这里会零延迟地无限重连，把服务器打爆。
				attempt++;
				if (!await DelayAsync(attempt, cancellationToken).ConfigureAwait(false))
				{
					return;
				}
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				return;
			}
			catch (Exception exception) when (exception is HttpRequestException or IOException or McpException or JsonException or ObjectDisposedException)
			{
				_notificationStreamActive = false;
				attempt++;
				if (!await DelayAsync(attempt, cancellationToken).ConfigureAwait(false))
				{
					return;
				}
			}
		}
	}

	/// <summary>指数退避等待；返回 <c>false</c> 表示已取消。</summary>
	private static async Task<bool> DelayAsync(int attempt, CancellationToken cancellationToken)
	{
		var milliseconds = Math.Min(30_000, 500 * Math.Pow(2, Math.Min(attempt, 6)));
		try
		{
			await Task.Delay(TimeSpan.FromMilliseconds(milliseconds), cancellationToken).ConfigureAwait(false);
			return true;
		}
		catch (OperationCanceledException)
		{
			return false;
		}
	}

	private static bool IsUnsupported(HttpStatusCode status) => status
		is HttpStatusCode.BadRequest
		or HttpStatusCode.NotFound
		or HttpStatusCode.MethodNotAllowed
		or HttpStatusCode.NotAcceptable
		or HttpStatusCode.NoContent;

	/// <summary>分发独立流里的一条记录：处理服务端通知，并对服务端请求（带 id）反向应答。</summary>
	private void DispatchNotification(JsonObject record)
	{
		if (record["method"] is not JsonValue method || method.GetValueKind() != JsonValueKind.String)
		{
			return;
		}

		// 带 id 的是服务端请求（例如 ping）：按 JSON-RPC 语义回一条响应记录。
		if (record["id"] is { } id)
		{
			PostServerResponseAsync(id, method.GetValue<string>());
			return;
		}

		NotificationReceived?.Invoke(method.GetValue<string>(), record["params"] as JsonObject);
	}

	/// <summary>
	/// 对服务端请求反向应答：<c>ping</c> 返回空结果，其余方法返回 -32601。
	/// 不取 <c>_gate</c>（那把锁只保护客户端自己的请求-响应配对）；失败静默丢弃，
	/// 通知流的后台任务不能因为一次应答失败而中断。
	/// </summary>
	private void PostServerResponseAsync(JsonNode id, string method)
	{
		JsonObject response;
		if (string.Equals(method, "ping", StringComparison.Ordinal))
		{
			response = new JsonObject
			{
				["jsonrpc"] = "2.0",
				["id"] = id.DeepClone(),
				["result"] = new JsonObject(),
			};
		}
		else
		{
			response = new JsonObject
			{
				["jsonrpc"] = "2.0",
				["id"] = id.DeepClone(),
				["error"] = new JsonObject
				{
					["code"] = -32601,
					["message"] = $"Method not found: {method}",
				},
			};
		}

		_ = PostResponseAsync(response);
	}

	private async Task PostResponseAsync(JsonObject response)
	{
		try
		{
			using var request = await CreateRequestAsync(response, CancellationToken.None).ConfigureAwait(false);
			using var httpResponse = await _http.SendAsync(request, CancellationToken.None).ConfigureAwait(false);
		}
		catch (Exception exception) when (exception is HttpRequestException or IOException or ObjectDisposedException or InvalidOperationException)
		{
			// 反向应答失败不影响通知流；服务器会在超时后自行重试。
		}
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

			// 访问令牌可能在长时间运行后过期：401 时刷新一次并重放同一个请求。
			for (var attempt = 0; ; attempt++)
			{
				using var request = await CreateRequestAsync(record, cancellationToken).ConfigureAwait(false);
				using var response = await _http
					.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
					.ConfigureAwait(false);

				SessionId ??= ReadSessionId(response);

				if (response.StatusCode == HttpStatusCode.Unauthorized &&
					attempt == 0 &&
					_tokens is not null &&
					await _tokens.RefreshAsync(cancellationToken).ConfigureAwait(false) is not null)
				{
					continue;
				}

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

				// 读正文同样受 _timeout 约束：只给发送头阶段限时的话，
				// 服务器先回响应头再拖住正文就能让握手/列举无限挂起。
				var mediaType = response.Content.Headers.ContentType?.MediaType;
				var result = mediaType?.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase) == true
					? await ReadFromEventStreamAsync(response, id!.Value, timeout.Token).ConfigureAwait(false)
					: await ReadFromJsonAsync(response, timeout.Token).ConfigureAwait(false);

				return (result, SessionId);
			}
		}
		finally
		{
			_gate.Release();
		}
	}

	/// <summary>构造一条 POST 请求，并按需带上 OAuth 访问令牌。</summary>
	private async Task<HttpRequestMessage> CreateRequestAsync(JsonObject record, CancellationToken cancellationToken)
	{
		var request = new HttpRequestMessage(HttpMethod.Post, _url)
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

		await ApplyAuthorizationAsync(request, cancellationToken).ConfigureAwait(false);
		return request;
	}

	/// <summary>注入 <c>Authorization: Bearer …</c>；没有令牌提供者或令牌时保持匿名请求。</summary>
	private async Task ApplyAuthorizationAsync(HttpRequestMessage request, CancellationToken cancellationToken)
	{
		if (_tokens is null)
		{
			return;
		}

		if (await _tokens.GetTokenAsync(cancellationToken).ConfigureAwait(false) is { Length: > 0 } token)
		{
			// 配置里可能已经带了静态 Authorization 头；OAuth 令牌优先，避免重复发两个头。
			request.Headers.Remove("authorization");
			request.Headers.TryAddWithoutValidation("authorization", $"Bearer {token}");
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
		var parser = SseParser.Create<JsonDocument?>(stream, ParseFrame);

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

	/// <summary>
	/// 解析一帧 SSE 数据。坏帧（服务器发来非法 JSON）直接跳过而不是抛出：
	/// 通知流的后台任务一旦 fault，连 <see cref="DisposeAsync"/> 都会被带着抛出。
	/// </summary>
	private static JsonDocument? ParseFrame(string eventName, ReadOnlySpan<byte> data)
	{
		_ = eventName;
		try
		{
			return JsonDocument.Parse(data.ToArray());
		}
		catch (JsonException)
		{
			return null;
		}
	}

	private static string Truncate(string value, int max) =>
		value.Length <= max ? value : string.Concat(value.AsSpan(0, max), "…");
}
