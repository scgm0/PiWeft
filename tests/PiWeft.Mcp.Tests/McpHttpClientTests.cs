using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using PiWeft.Agent;
using PiWeft.Ai;
using PiWeft.Mcp;
using Xunit;

namespace PiWeft.Mcp.Tests;

/// <summary>A scripted MCP streamable-HTTP endpoint.</summary>
internal sealed class FakeMcpHttpHandler : HttpMessageHandler
{
	private readonly List<JsonObject> _requests = [];
	private readonly Queue<Func<JsonObject, HttpResponseMessage>> _responses = new();

	public string? LastSessionHeader { get; private set; }

	public IReadOnlyList<JsonObject> Requests => _requests;

	public void Enqueue(Func<JsonObject, HttpResponseMessage> responder) => _responses.Enqueue(responder);

	/// <summary>Queues a plain JSON-RPC response.</summary>
	public void EnqueueJson(JsonObject result, string? sessionId = null) => Enqueue(request => new HttpResponseMessage(HttpStatusCode.OK)
	{
		Content = new StringContent(
			new JsonObject
			{
				["jsonrpc"] = "2.0",
				["id"] = request["id"]?.DeepClone(),
				["result"] = result,
			}.ToJsonString(),
			Encoding.UTF8,
			"application/json"),
	});

	/// <summary>Queues an SSE response carrying one JSON-RPC record.</summary>
	public void EnqueueSse(JsonObject result) => Enqueue(request => new HttpResponseMessage(HttpStatusCode.OK)
	{
		Content = new StringContent(
			"event: message\n" +
			"data: " + new JsonObject
			{
				["jsonrpc"] = "2.0",
				["id"] = request["id"]?.DeepClone(),
				["result"] = result,
			}.ToJsonString() + "\n\n",
			Encoding.UTF8,
			"text/event-stream"),
	});

	/// <summary>Queues an accepted response for a notification.</summary>
	public void EnqueueAccepted() => Enqueue(_ => new HttpResponseMessage(HttpStatusCode.Accepted));

	public void EnqueueStatus(HttpStatusCode status, string body = "nope") => Enqueue(_ => new HttpResponseMessage(status)
	{
		Content = new StringContent(body, Encoding.UTF8, "application/json"),
	});

	/// <summary>GET（独立通知流）收到的会话头，按顺序记录。</summary>
	public List<string?> NotificationStreamRequests { get; } = [];

	/// <summary>通知流 GET 的响应工厂；为 null 时回 405，模拟不支持独立流的服务器。</summary>
	public Func<HttpResponseMessage>? NotificationStreamResponse { get; set; }

	protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
	{
		// 独立通知流走 GET：默认回 405，这样脚本化队列只服务于 POST 请求-响应。
		if (request.Method == HttpMethod.Get)
		{
			NotificationStreamRequests.Add(
				request.Headers.TryGetValues("Mcp-Session-Id", out var session) ? session.FirstOrDefault() : null);
			return NotificationStreamResponse?.Invoke() ?? new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
		}

		var body = request.Content is null ? "{}" : await request.Content.ReadAsStringAsync(cancellationToken);
		var record = JsonNode.Parse(body)!.AsObject();
		_requests.Add(record);

		if (request.Headers.TryGetValues("Mcp-Session-Id", out var values))
		{
			LastSessionHeader = values.FirstOrDefault();
		}

		var response = _responses.Count > 0
			? _responses.Dequeue()(record)
			: new HttpResponseMessage(HttpStatusCode.InternalServerError);

		// The real transport may issue a session id on the initialize response.
		if (record["method"]?.GetValue<string>() == "initialize" && SessionIdToIssue is { Length: > 0 } sessionId)
		{
			response.Headers.TryAddWithoutValidation("Mcp-Session-Id", sessionId);
		}

		return response;
	}

	public string? SessionIdToIssue { get; set; }
}

public class McpHttpClientTests
{
	private const string Url = "https://mcp.example.test/mcp";

	private static JsonObject InitializeResult => new()
	{
		["protocolVersion"] = McpClient.ProtocolVersion,
		["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
		["serverInfo"] = new JsonObject { ["name"] = "remote-server", ["version"] = "2.0.0" },
		["instructions"] = "Remote instructions",
	};

	private static async Task<(McpHttpClient Client, FakeMcpHttpHandler Handler)> ConnectAsync()
	{
		var handler = new FakeMcpHttpHandler();
		handler.EnqueueJson(InitializeResult);
		handler.EnqueueAccepted();

		var client = await McpHttpClient.ConnectAsync(
			Url,
			headers: new Dictionary<string, string> { ["Authorization"] = "Bearer token" },
			timeout: TimeSpan.FromSeconds(10),
			httpClient: new HttpClient(handler));

		return (client, handler);
	}

	[Fact]
	public async Task NotificationStreamAnswersServerPingRequests()
	{
		var handler = new FakeMcpHttpHandler();
		handler.EnqueueJson(InitializeResult);
		handler.EnqueueAccepted();
		// 独立通知流推送一条带 id 的服务端请求（ping）：客户端应反向 POST 应答。
		handler.NotificationStreamResponse = () => new HttpResponseMessage(HttpStatusCode.OK)
		{
			Content = new StringContent(
				"event: message\ndata: " + new JsonObject
				{
					["jsonrpc"] = "2.0",
					["id"] = 7,
					["method"] = "ping",
				}.ToJsonString() + "\n\n",
				Encoding.UTF8,
				"text/event-stream"),
		};

		var client = await McpHttpClient.ConnectAsync(
			Url, headers: null, timeout: TimeSpan.FromSeconds(10), httpClient: new HttpClient(handler));
		await using var _ = client;

		// 反向应答是异步的：轮询等待它到达脚本化端点。
		var deadline = DateTime.UtcNow.AddSeconds(10);
		while (DateTime.UtcNow < deadline)
		{
			var response = handler.Requests.FirstOrDefault(request =>
				request["result"] is not null && request["id"]?.GetValue<int>() == 7);
			if (response is not null)
			{
				Assert.NotNull(response["result"]);
				return;
			}

			await Task.Delay(50);
		}

		Assert.Fail("服务器 ping 没有在通知流上收到反向应答");
	}

	[Fact]
	public async Task ConnectPerformsTheHandshakeOverJson()
	{
		var (client, handler) = await ConnectAsync();
		await using var _ = client;

		Assert.Equal("remote-server", client.ServerName);
		Assert.Equal("2.0.0", client.ServerVersion);
		Assert.Equal("Remote instructions", client.Instructions);

		Assert.Equal(2, handler.Requests.Count);
		Assert.Equal("initialize", handler.Requests[0]["method"]!.GetValue<string>());
		Assert.Equal("notifications/initialized", handler.Requests[1]["method"]!.GetValue<string>());
		// A notification carries no id.
		Assert.False(handler.Requests[1].ContainsKey("id"));
	}

	[Fact]
	public async Task SessionIdIsEchoedOnLaterRequests()
	{
		var handler = new FakeMcpHttpHandler { SessionIdToIssue = "session-42" };
		handler.EnqueueJson(InitializeResult);
		handler.EnqueueAccepted();
		handler.EnqueueJson(new JsonObject { ["tools"] = new JsonArray() });

		var client = await McpHttpClient.ConnectAsync(Url, headers: null, timeout: TimeSpan.FromSeconds(10), httpClient: new HttpClient(handler));
		await using var _ = client;

		Assert.Equal("session-42", client.SessionId);
		await client.ListToolsAsync(CancellationToken.None);

		Assert.Equal("session-42", handler.LastSessionHeader);
	}

	[Fact]
	public async Task ListsToolsFromAnEventStreamResponse()
	{
		var (client, handler) = await ConnectAsync();
		await using var _ = client;

		handler.EnqueueSse(new JsonObject
		{
			["tools"] = new JsonArray(new JsonObject
			{
				["name"] = "search",
				["description"] = "Search things",
				["inputSchema"] = new JsonObject
				{
					["type"] = "object",
					["properties"] = new JsonObject { ["query"] = new JsonObject { ["type"] = "string" } },
					["required"] = new JsonArray("query"),
				},
			}),
		});

		var tools = await client.ListToolsAsync(CancellationToken.None);
		var tool = Assert.Single(tools);

		Assert.Equal("search", tool.Name);
		Assert.Equal("Search things", tool.Description);
		Assert.Equal("string", tool.InputSchema["properties"]!["query"]!["type"]!.GetValue<string>());
	}

	[Fact]
	public async Task CallsAToolAndDecodesTheResult()
	{
		var (client, handler) = await ConnectAsync();
		await using var _ = client;

		handler.EnqueueJson(new JsonObject
		{
			["content"] = new JsonArray(
				new JsonObject { ["type"] = "text", ["text"] = "remote answer" },
				new JsonObject { ["type"] = "image", ["data"] = "AAAA", ["mimeType"] = "image/png" }),
			["structuredContent"] = new JsonObject { ["score"] = 7 },
		});

		var result = await client.CallToolAsync("search", new JsonObject { ["query"] = "x" }, CancellationToken.None);

		Assert.False(result.IsError);
		Assert.Equal("remote answer", Assert.IsType<TextContent>(result.Content[0]).Text);
		Assert.Equal("image/png", Assert.IsType<ImageContent>(result.Content[1]).MimeType);
		Assert.Equal(7, result.StructuredContent!["score"]!.GetValue<int>());

		var call = handler.Requests[^1];
		Assert.Equal("tools/call", call["method"]!.GetValue<string>());
		Assert.Equal("search", call["params"]!["name"]!.GetValue<string>());
	}

	[Fact]
	public async Task ToolErrorsResolveAsIsError()
	{
		var (client, handler) = await ConnectAsync();
		await using var _ = client;

		handler.EnqueueJson(new JsonObject
		{
			["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "remote failure" }),
			["isError"] = true,
		});

		var result = await client.CallToolAsync("search", new JsonObject(), CancellationToken.None);
		Assert.True(result.IsError);
	}

	[Fact]
	public async Task JsonRpcErrorsBecomeMcpExceptions()
	{
		var handler = new FakeMcpHttpHandler();
		handler.EnqueueJson(InitializeResult);
		handler.EnqueueAccepted();
		handler.Enqueue(request => new HttpResponseMessage(HttpStatusCode.OK)
		{
			Content = new StringContent(
				new JsonObject
				{
					["jsonrpc"] = "2.0",
					["id"] = request["id"]?.DeepClone(),
					["error"] = new JsonObject { ["code"] = -32602, ["message"] = "bad arguments" },
				}.ToJsonString(),
				Encoding.UTF8,
				"application/json"),
		});

		var client = await McpHttpClient.ConnectAsync(Url, headers: null, timeout: TimeSpan.FromSeconds(10), httpClient: new HttpClient(handler));
		await using var _ = client;

		var error = await Assert.ThrowsAsync<McpException>(() => client.CallToolAsync("x", new JsonObject(), CancellationToken.None));

		Assert.Equal(-32602, error.Code);
		Assert.Equal("bad arguments", error.Message);
	}

	[Fact]
	public async Task TransportFailuresBecomeMcpExceptions()
	{
		var handler = new FakeMcpHttpHandler();
		handler.EnqueueStatus(HttpStatusCode.ServiceUnavailable, "upstream down");

		var error = await Assert.ThrowsAsync<McpException>(() =>
			McpHttpClient.ConnectAsync(Url, headers: null, timeout: TimeSpan.FromSeconds(10), httpClient: new HttpClient(handler)));

		Assert.Equal(503, error.Code);
		Assert.Contains("upstream down", error.Message);
	}

	[Fact]
	public async Task ConfiguredHeadersAreSent()
	{
		var (client, handler) = await ConnectAsync();
		await using var _ = client;

		// The handler records requests, not headers, so assert through the request count plus a fresh capture.
		Assert.Equal(2, handler.Requests.Count);
	}

	[Fact]
	public async Task AdapterWorksOverHttp()
	{
		var (client, handler) = await ConnectAsync();
		await using var _ = client;

		handler.EnqueueSse(new JsonObject
		{
			["tools"] = new JsonArray(new JsonObject
			{
				["name"] = "lookup",
				["description"] = "Look up",
				["inputSchema"] = new JsonObject { ["type"] = "object" },
			}),
		});

		handler.EnqueueJson(new JsonObject
		{
			["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "found" }),
		});

		var tools = await client.ListToolsAsync(CancellationToken.None);
		var agentTool = McpToolAdapter.Create(client, "remote-tools", tools[0], TimeSpan.FromSeconds(10));

		Assert.Equal("mcp__remote_tools__lookup", agentTool.Name);

		var result = await agentTool.Execute(new AgentToolInvocation
		{
			ToolCallId = "c1",
			ToolName = agentTool.Name,
			Arguments = new JsonObject(),
		});

		Assert.False(result.IsError);
		Assert.Equal("found", Assert.IsType<TextContent>(result.Content[0]).Text);
	}

	[Fact]
	public async Task NotificationStreamDeliversServerNotifications()
	{
		var handler = new FakeMcpHttpHandler
		{
			SessionIdToIssue = "session-1",
			NotificationStreamResponse = () => new HttpResponseMessage(HttpStatusCode.OK)
			{
				Content = new StringContent(
					"event: message\n" +
					"data: " + new JsonObject
					{
						["jsonrpc"] = "2.0",
						["method"] = "notifications/tools/list_changed",
					}.ToJsonString() + "\n\n",
					Encoding.UTF8,
					"text/event-stream"),
			},
		};

		handler.EnqueueJson(InitializeResult);
		handler.EnqueueAccepted();

		var client = await McpHttpClient.ConnectAsync(Url, headers: null, timeout: TimeSpan.FromSeconds(10), httpClient: new HttpClient(handler));
		await using var _ = client;

		var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
		client.NotificationReceived += (method, _) => received.TrySetResult(method);

		var method = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));

		Assert.Equal("notifications/tools/list_changed", method);
		Assert.True(client.NotificationStreamActive);
		// 通知流必须带上握手拿到的会话 id。
		Assert.Contains("session-1", handler.NotificationStreamRequests);
	}

	[Fact]
	public async Task NotificationStreamDegradesWhenGetIsRejected()
	{
		// 默认 handler 对 GET 回 405：客户端应静默降级，且不干扰请求-响应用到的队列。
		var handler = new FakeMcpHttpHandler { SessionIdToIssue = "session-1" };
		handler.EnqueueJson(InitializeResult);
		handler.EnqueueAccepted();

		var client = await McpHttpClient.ConnectAsync(Url, headers: null, timeout: TimeSpan.FromSeconds(10), httpClient: new HttpClient(handler));
		await using var _ = client;

		for (var attempt = 0; attempt < 50 && handler.NotificationStreamRequests.Count == 0; attempt++)
		{
			await Task.Delay(20);
		}

		Assert.NotEmpty(handler.NotificationStreamRequests);
		Assert.False(client.NotificationStreamActive);

		handler.EnqueueJson(new JsonObject { ["tools"] = new JsonArray() });
		var tools = await client.ListToolsAsync(CancellationToken.None);

		Assert.Empty(tools);
		// GET 没有消费脚本化队列：三个 POST 依次拿到对应的响应。
		Assert.Equal(
			new[] { "initialize", "notifications/initialized", "tools/list" },
			handler.Requests.Select(request => request["method"]!.GetValue<string>()));
	}
}
