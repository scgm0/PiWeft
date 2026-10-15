using System.IO.Pipelines;
using System.Text;
using System.Text.Json.Nodes;

namespace PiWeft.Mcp.Tests;

/// <summary>
/// A scripted MCP server speaking JSON-RPC over pipes, so the client can be tested without spawning a process.
/// </summary>
internal sealed class FakeMcpServer : IAsyncDisposable
{
	private readonly Stream _input;
	private readonly StreamWriter _writer;
	private readonly StreamReader _reader;
	private readonly List<string> _methods = [];
	private readonly Dictionary<string, Func<JsonObject, JsonNode?>> _tools = new(StringComparer.Ordinal);
	private readonly Task _loop;

	private FakeMcpServer(Stream input, Stream output)
	{
		_input = input;
		_writer = new StreamWriter(output, new UTF8Encoding(false)) { AutoFlush = true };
		_reader = new StreamReader(input, Encoding.UTF8);
		_loop = Task.Run(RunAsync);
	}

	public string Instructions { get; set; } = "Fake server instructions";

	public IReadOnlyList<string> Methods => _methods;

	public static (McpClient Client, FakeMcpServer Server) Create()
	{
		var clientToServer = new Pipe();
		var serverToClient = new Pipe();

		var server = new FakeMcpServer(
			clientToServer.Reader.AsStream(),
			serverToClient.Writer.AsStream());

		var client = McpClient.ConnectAsync(
			serverToClient.Reader.AsStream(),
			clientToServer.Writer.AsStream()).GetAwaiter().GetResult();

		return (client, server);
	}

	public void AddTool(string name, string description, JsonObject schema, Func<JsonObject, JsonNode?> handler)
	{
		_tools[name] = handler;
		_added.Add((name, description, schema));
	}

	private readonly List<(string Name, string Description, JsonObject Schema)> _added = [];

	private async Task RunAsync()
	{
		while (true)
		{
			string? line;
			try
			{
				line = await _reader.ReadLineAsync().ConfigureAwait(false);
			}
			catch (Exception)
			{
				return;
			}

			if (line is null)
			{
				return;
			}

			if (line.Trim().Length == 0)
			{
				continue;
			}

			var request = JsonNode.Parse(line)!.AsObject();
			var method = request["method"]?.GetValue<string>() ?? string.Empty;
			var id = request["id"]?.DeepClone();

			if (method.Length > 0)
			{
				_methods.Add(method);
			}

			if (id is null)
			{
				continue; // a notification
			}

			var response = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id };

			switch (method)
			{
				case "initialize":
					response["result"] = new JsonObject
					{
						["protocolVersion"] = McpClient.ProtocolVersion,
						["capabilities"] = new JsonObject
						{
							["tools"] = new JsonObject(),
							["resources"] = new JsonObject(),
							["prompts"] = new JsonObject(),
						},
						["serverInfo"] = new JsonObject { ["name"] = "fake-server", ["version"] = "9.9.9" },
						["instructions"] = Instructions,
					};
					break;

				case "resources/list":
					response["result"] = new JsonObject
					{
						["resources"] = new JsonArray(new JsonObject
						{
							["uri"] = "fake://one",
							["name"] = "one",
							["mimeType"] = "text/plain",
						}),
					};
					break;

				case "resources/read":
					response["result"] = new JsonObject
					{
						["contents"] = new JsonArray(new JsonObject
						{
							["uri"] = request["params"]?["uri"]?.DeepClone() ?? "fake://one",
							["mimeType"] = "text/plain",
							["text"] = "fake content",
						}),
					};
					break;

				case "prompts/list":
					response["result"] = new JsonObject
					{
						["prompts"] = new JsonArray(new JsonObject { ["name"] = "fake-prompt", ["description"] = "A prompt" }),
					};
					break;

				case "prompts/get":
					response["result"] = new JsonObject
					{
						["messages"] = new JsonArray(new JsonObject
						{
							["role"] = "user",
							["content"] = new JsonObject { ["type"] = "text", ["text"] = "fake prompt body" },
						}),
					};
					break;

				case "tools/list":
				{
					// Built from a list: JsonArray.Add<T> would need runtime code generation.
					var tools = new List<JsonNode?>();
					foreach (var (name, description, schema) in _added)
					{
						tools.Add(new JsonObject
						{
							["name"] = name,
							["description"] = description,
							["inputSchema"] = schema.DeepClone(),
						});
					}

					response["result"] = new JsonObject { ["tools"] = new JsonArray([.. tools]) };
					break;
				}

				case "tools/call":
				{
					var name = request["params"]!["name"]!.GetValue<string>();
					var arguments = request["params"]!["arguments"] as JsonObject ?? new JsonObject();

					if (!_tools.TryGetValue(name, out var handler))
					{
						response["error"] = new JsonObject
						{
							["code"] = -32602,
							["message"] = $"Unknown tool: {name}",
						};
						break;
					}

					response["result"] = handler(arguments);
					break;
				}

				default:
					response["error"] = new JsonObject
					{
						["code"] = -32601,
						["message"] = $"Method not found: {method}",
					};
					break;
			}

			await _writer.WriteLineAsync(response.ToJsonString()).ConfigureAwait(false);
		}
	}

	public async ValueTask DisposeAsync()
	{
		_writer.Dispose();
		_reader.Dispose();
		_input.Dispose();

		// Do not block on a loop that may still be waiting for a line that never comes.
		await Task.WhenAny(_loop, Task.Delay(TimeSpan.FromMilliseconds(100))).ConfigureAwait(false);
	}
}
