using System.Net;
using System.Text;
using System.Text.Json.Nodes;

// A minimal MCP server used by the MCP integration tests: JSON-RPC 2.0 over stdio (default) or the streamable HTTP
// transport (`--http <port>`). It implements the protocol by hand so a bug in the client cannot be masked by sharing
// code with it: initialize, tools/list (paginated), tools/call, resources/list (paginated), resources/templates/list,
// resources/read, prompts/list and prompts/get.
//
// Calling the `add_tool` tool registers another tool and announces `notifications/tools/list_changed` on stdio, which
// is how the client's live tool refresh is tested.

if (args.Length >= 2 && args[0] == "--http" && int.TryParse(args[1], out var port))
{
	return await RunHttpAsync(port);
}

if (args.Length >= 2 && args[0] == "--llm" && int.TryParse(args[1], out var llmPort))
{
	return await RunLlmAsync(llmPort, args.Length >= 3 ? args[2] : null);
}

return await RunStdioAsync();

// ---------------------------------------------------------------- stdio mode

async Task<int> RunStdioAsync()
{
	using var stdin = Console.OpenStandardInput();
	using var stdout = Console.OpenStandardOutput();
	using var reader = new StreamReader(stdin, Encoding.UTF8);
	var writer = new StreamWriter(stdout, new UTF8Encoding(false)) { AutoFlush = true };

	// A marker lets a test prove that the configured environment reached the process.
	var greeting = Environment.GetEnvironmentVariable("FIXTURE_GREETING") ?? "hello";
	var extraTools = new List<JsonObject>();
	var writeGate = new SemaphoreSlim(1, 1);

	// A tool list change is announced only after the call's own response went out.
	async Task AnnounceToolChangeAsync()
	{
		await writeGate.WaitAsync();
		try
		{
			await writer.WriteLineAsync(new JsonObject
			{
				["jsonrpc"] = "2.0",
				["method"] = "notifications/tools/list_changed",
			}.ToJsonString());
		}
		finally
		{
			writeGate.Release();
		}
	}

	while (await reader.ReadLineAsync() is { } line)
	{
		if (line.Trim().Length == 0)
		{
			continue;
		}

		var request = JsonNode.Parse(line)!.AsObject();
		var id = request["id"]?.DeepClone();
		if (id is null)
		{
			continue; // notifications need no reply
		}

		var response = Respond(request, greeting, extraTools);
		if (response is not null)
		{
			await writeGate.WaitAsync();
			try
			{
				await writer.WriteLineAsync(response.ToJsonString());
			}
			finally
			{
				writeGate.Release();
			}

			// Announce the new tool only after the call's own response went out.
			var called = request["method"]?.GetValue<string>() == "tools/call"
				? request["params"]?["name"]?.GetValue<string>() ?? string.Empty
				: string.Empty;

			if (called == "add_tool")
			{
				await AnnounceToolChangeAsync();
			}
		}
	}

	return 0;
}

// ---------------------------------------------------------------- HTTP mode

async Task<int> RunHttpAsync(int httpPort)
{
	using var listener = new HttpListener();
	listener.Prefixes.Add($"http://127.0.0.1:{httpPort}/mcp/");
	listener.Start();

	// The test waits for this line before connecting.
	Console.WriteLine($"listening {httpPort}");
	Console.Out.Flush();

	while (listener.IsListening)
	{
		HttpListenerContext context;
		try
		{
			context = await listener.GetContextAsync();
		}
		catch (Exception)
		{
			break;
		}

		try
		{
			await HandleHttpAsync(context);
		}
		catch (Exception)
		{
			// 单个坏请求不应让 fixture 退出：回 500 后继续服务。
			try
			{
				context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
				context.Response.Close();
			}
			catch (Exception)
			{
				// 连接可能已经断开。
			}
		}
	}

	listener.Stop();
	return 0;
}

async Task HandleHttpAsync(HttpListenerContext context)
{
	// 独立通知流走 GET：这个 fixture 模拟**不支持**该流的服务器，客户端应静默降级。
	// 不处理 GET 的话会去 parse 空 body 并抛异常，把整个 fixture 带下线。
	if (!string.Equals(context.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
	{
		context.Response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
		context.Response.Close();
		return;
	}

	using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
	var request = JsonNode.Parse(await reader.ReadToEndAsync())?.AsObject();

	// Notifications are acknowledged without a body.
	if (request?["id"] is null)
	{
		context.Response.Headers["Mcp-Session-Id"] = "fixture-session";
		context.Response.StatusCode = (int)HttpStatusCode.Accepted;
		context.Response.Close();
		return;
	}

	var response = Respond(request!, greeting: "http")!;
	var payload = Encoding.UTF8.GetBytes(response.ToJsonString());

	context.Response.Headers["Mcp-Session-Id"] = "fixture-session";
	context.Response.ContentType = "application/json";
	context.Response.ContentLength64 = payload.Length;
	await context.Response.OutputStream.WriteAsync(payload);
	context.Response.Close();
}

// ---------------------------------------------------------------- mock LLM mode

// Answers every request with a scripted Anthropic stream: the first request asks for a tool call, later ones answer.
// This lets a test drive the real CLI binary end to end without a provider account.
async Task<int> RunLlmAsync(int llmPort, string? logPath)
{
	using var listener = new HttpListener();
	listener.Prefixes.Add($"http://127.0.0.1:{llmPort}/");
	listener.Start();

	Console.WriteLine($"listening {llmPort}");
	Console.Out.Flush();

	var turn = 0;

	while (listener.IsListening)
	{
		HttpListenerContext context;
		try
		{
			context = await listener.GetContextAsync();
		}
		catch (Exception)
		{
			break;
		}

		using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
		{
			var body = await reader.ReadToEndAsync();
			if (logPath is { Length: > 0 })
			{
				await File.AppendAllTextAsync(logPath, body + "\n");
			}
		}

		turn++;
		var payload = Encoding.UTF8.GetBytes(turn == 1 ? ToolCallStream() : TextStream("mock final answer"));

		context.Response.ContentType = "text/event-stream";
		context.Response.ContentLength64 = payload.Length;
		await context.Response.OutputStream.WriteAsync(payload);
		context.Response.Close();
	}

	listener.Stop();
	return 0;
}

string ToolCallStream() => string.Join("\n",
	"event: message_start",
	"data: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_mock_1\",\"model\":\"mock-model\",\"usage\":{\"input_tokens\":11}}}",
	"",
	"event: content_block_start",
	"data: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"tool_use\",\"id\":\"toolu_mock_1\",\"name\":\"read\",\"input\":{}}}",
	"",
	"event: content_block_delta",
	"data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"{\\\"path\\\":\\\"note.txt\\\"}\"}}",
	"",
	"event: content_block_stop",
	"data: {\"type\":\"content_block_stop\",\"index\":0}",
	"",
	"event: message_delta",
	"data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"tool_use\"},\"usage\":{\"output_tokens\":5}}",
	"",
	"event: message_stop",
	"data: {\"type\":\"message_stop\"}",
	"",
	"");

string TextStream(string text) => string.Join("\n",
	"event: message_start",
	"data: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_mock_2\",\"model\":\"mock-model\",\"usage\":{\"input_tokens\":20}}}",
	"",
	"event: content_block_start",
	"data: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}",
	"",
	"event: content_block_delta",
	$"data: {{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{{\"type\":\"text_delta\",\"text\":\"{text}\"}}}}",
	"",
	"event: content_block_stop",
	"data: {\"type\":\"content_block_stop\",\"index\":0}",
	"",
	"event: message_delta",
	"data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":3}}",
	"",
	"event: message_stop",
	"data: {\"type\":\"message_stop\"}",
	"",
	"");

// ---------------------------------------------------------------- protocol

JsonObject? Respond(JsonObject request, string greeting, List<JsonObject>? extraTools = null)
{
	var id = request["id"]?.DeepClone();
	if (id is null)
	{
		return null;
	}

	var method = request["method"]?.GetValue<string>() ?? string.Empty;
	var response = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id };

	switch (method)
	{
		case "initialize":
			response["result"] = new JsonObject
			{
				["protocolVersion"] = "2025-06-18",
				["capabilities"] = new JsonObject
				{
					["tools"] = new JsonObject { ["listChanged"] = true },
					["resources"] = new JsonObject { ["subscribe"] = true, ["listChanged"] = true },
					["prompts"] = new JsonObject { ["listChanged"] = true },
				},
				["serverInfo"] = new JsonObject { ["name"] = "fixture-server", ["version"] = "1.1.0" },
				["instructions"] = "Fixture server for integration tests.",
			};
			break;

		case "tools/list":
		{
			// Paginated: the first page holds one tool and points at the second.
			var all = new List<JsonNode?>
			{
				new JsonObject
				{
					["name"] = "echo",
					["description"] = "Echo a message back",
					["inputSchema"] = new JsonObject
					{
						["type"] = "object",
						["properties"] = new JsonObject { ["message"] = new JsonObject { ["type"] = "string" } },
						["required"] = new JsonArray("message"),
					},
				},
			};

			foreach (var tool in extraTools ?? [])
			{
				all.Add(tool.DeepClone());
			}

			all.Add(new JsonObject
			{
				["name"] = "fail",
				["description"] = "Always reports an error result",
				["inputSchema"] = new JsonObject { ["type"] = "object" },
			});

			all.Add(new JsonObject
			{
				["name"] = "add_tool",
				["description"] = "Registers another tool and announces the change",
				["inputSchema"] = new JsonObject
				{
					["type"] = "object",
					["properties"] = new JsonObject { ["name"] = new JsonObject { ["type"] = "string" } },
					["required"] = new JsonArray("name"),
				},
			});

			var cursor = request["params"]?["cursor"]?.GetValue<string>();
			var page = cursor is null ? all.Take(1).ToList() : all.Skip(1).ToList();

			var result = new JsonObject { ["tools"] = new JsonArray([.. page]) };
			if (cursor is null)
			{
				result["nextCursor"] = "page-2";
			}

			response["result"] = result;
			break;
		}

		case "resources/list":
		{
			// Also paginated, and it carries an item the client must hide: an MCP App UI.
			var all = new List<JsonNode?>
			{
				new JsonObject
				{
					["uri"] = "fixture://notes/one",
					["name"] = "note-one",
					["title"] = "First note",
					["description"] = "A note from the fixture",
					["mimeType"] = "text/plain",
					["size"] = 12,
				},
				new JsonObject
				{
					["uri"] = "fixture://data/blob",
					["name"] = "blob",
					["mimeType"] = "application/octet-stream",
				},
				new JsonObject
				{
					["uri"] = "ui://dashboard",
					["name"] = "dashboard",
					["mimeType"] = "text/html; profile=mcp-app",
				},
			};

			var cursor = request["params"]?["cursor"]?.GetValue<string>();
			var page = cursor is null ? all.Take(2).ToList() : all.Skip(2).ToList();

			var result = new JsonObject { ["resources"] = new JsonArray([.. page]) };
			if (cursor is null)
			{
				result["nextCursor"] = "page-2";
			}

			response["result"] = result;
			break;
		}

		case "resources/templates/list":
			response["result"] = new JsonObject
			{
				["resourceTemplates"] = new JsonArray(new JsonObject
				{
					["uriTemplate"] = "fixture://notes/{id}",
					["name"] = "note",
					["description"] = "One note by id",
					["mimeType"] = "text/plain",
				}),
			};
			break;

		case "resources/read":
		{
			var uri = request["params"]?["uri"]?.GetValue<string>() ?? string.Empty;
			response["result"] = uri switch
			{
				"fixture://notes/one" => new JsonObject
				{
					["contents"] = new JsonArray(new JsonObject
					{
						["uri"] = uri,
						["mimeType"] = "text/plain",
						["text"] = "hello resources",
					}),
				},

				// A directory-style read: several labeled contents.
				"fixture://notes" => new JsonObject
				{
					["contents"] = new JsonArray(
						new JsonObject { ["uri"] = "fixture://notes/one", ["mimeType"] = "text/plain", ["text"] = "one" },
						new JsonObject { ["uri"] = "fixture://notes/two", ["mimeType"] = "text/plain", ["text"] = "two" }),
				},

				"fixture://data/blob" => new JsonObject
				{
					["contents"] = new JsonArray(new JsonObject
					{
						["uri"] = uri,
						["mimeType"] = "application/octet-stream",
						["blob"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("binary body")),
					}),
				},

				"fixture://data/json" => new JsonObject
				{
					["contents"] = new JsonArray(new JsonObject
					{
						["uri"] = uri,
						["mimeType"] = "application/json",
						["blob"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("""{"ok":true}""")),
					}),
				},

				"fixture://empty" => new JsonObject { ["contents"] = new JsonArray() },

				_ => new JsonObject { ["contents"] = new JsonArray() },
			};
			break;
		}

		case "prompts/list":
			response["result"] = new JsonObject
			{
				["prompts"] = new JsonArray(new JsonObject
				{
					["name"] = "review",
					["description"] = "Review a diff",
					["arguments"] = new JsonArray(new JsonObject
					{
						["name"] = "target",
						["description"] = "What to review",
						["required"] = true,
					}),
				}),
			};
			break;

		case "prompts/get":
		{
			var name = request["params"]?["name"]?.GetValue<string>() ?? string.Empty;
			var target = request["params"]?["arguments"]?["target"]?.GetValue<string>() ?? "nothing";

			response["result"] = name == "review"
				? new JsonObject
				{
					["description"] = "Review a diff",
					["messages"] = new JsonArray(new JsonObject
					{
						["role"] = "user",
						["content"] = new JsonObject
						{
							["type"] = "text",
							["text"] = $"Review {target} carefully.",
						},
					}),
				}
				: new JsonObject { ["messages"] = new JsonArray() };
			break;
		}

		case "tools/call":
		{
			var name = request["params"]?["name"]?.GetValue<string>() ?? string.Empty;
			var arguments = request["params"]?["arguments"] as JsonObject ?? new JsonObject();

			if (name == "add_tool")
			{
				var added = (arguments["name"]?.GetValue<string>() ?? "added").Trim();
				(extraTools ??= []).Add(new JsonObject
				{
					["name"] = added,
					["description"] = $"Tool added at runtime by {greeting}",
					["inputSchema"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
				});

				response["result"] = new JsonObject
				{
					["content"] = new JsonArray(new JsonObject
					{
						["type"] = "text",
						["text"] = $"registered {added}",
					}),
				};
				break;
			}

			response["result"] = name switch
			{
				"echo" => new JsonObject
				{
					["content"] = new JsonArray(new JsonObject
					{
						["type"] = "text",
						["text"] = $"{greeting}: {arguments["message"]?.GetValue<string>() ?? string.Empty}",
					}),
					["structuredContent"] = new JsonObject { ["message"] = arguments["message"]?.DeepClone() },
				},
				"fail" => new JsonObject
				{
					["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "fixture failure" }),
					["isError"] = true,
				},
				_ => new JsonObject
				{
					["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = $"unknown tool {name}" }),
					["isError"] = true,
				},
			};
			break;
		}

		default:
			response["error"] = new JsonObject { ["code"] = -32601, ["message"] = $"Method not found: {method}" };
			break;
	}

	return response;
}
