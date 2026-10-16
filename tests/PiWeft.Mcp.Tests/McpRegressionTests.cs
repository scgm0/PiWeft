using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using PiWeft.Mcp;
using Xunit;

namespace PiWeft.Mcp.Tests;

/// <summary>
/// MCP 的回归用例：写入 <c>mcp.json</c> 不得丢数据，坏 SSE 帧不得让通知流崩溃。
/// </summary>
public class McpRegressionTests : IDisposable
{
	private const string Url = "https://mcp.example.test/mcp";

	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-mcpreg-" + Guid.NewGuid().ToString("N")[..8]);

	public McpRegressionTests() => Directory.CreateDirectory(_root);

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	private static JsonObject InitializeResult => new()
	{
		["protocolVersion"] = McpClient.ProtocolVersion,
		["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
		["serverInfo"] = new JsonObject { ["name"] = "remote-server", ["version"] = "2.0.0" },
	};

	[Fact]
	public void SaveEntryKeepsOtherServersInAFileWithCommentsAndTrailingCommas()
	{
		var path = Path.Combine(_root, "mcp.json");
		File.WriteAllText(path, """
			{
			  // 手写注释：加载路径允许，写回时也必须能解析，否则会把其它服务器整片抹掉
			  "mcpServers": {
			    "keep": { "command": "/bin/keep" },
			  }
			}
			""");

		McpConfig.SaveEntry(path, "added", new McpServerEntry { Url = Url });

		var servers = JsonNode.Parse(File.ReadAllText(path))!.AsObject()["mcpServers"]!.AsObject();
		Assert.Equal(2, servers.Count);
		Assert.True(servers.ContainsKey("keep"));
		Assert.True(servers.ContainsKey("added"));
	}

	[Fact]
	public void SaveEntryRefusesToOverwriteAnUnparsableFile()
	{
		var path = Path.Combine(_root, "mcp.json");
		const string broken = "{ this is not json";
		File.WriteAllText(path, broken);

		Assert.Throws<InvalidDataException>(() =>
			McpConfig.SaveEntry(path, "added", new McpServerEntry { Url = Url }));

		// 原文件必须原封不动：宁可报错，也不能把用户的配置清空。
		Assert.Equal(broken, File.ReadAllText(path));
	}

	[Fact]
	public void RemoveEntryKeepsOtherServers()
	{
		var path = Path.Combine(_root, "mcp.json");
		File.WriteAllText(path, """{ "mcpServers": { "a": { "command": "/bin/a" }, "b": { "command": "/bin/b" } } }""");

		Assert.True(McpConfig.RemoveEntry(path, "a"));

		var servers = JsonNode.Parse(File.ReadAllText(path))!.AsObject()["mcpServers"]!.AsObject();
		Assert.Single(servers);
		Assert.True(servers.ContainsKey("b"));
	}

	[Fact]
	public async Task NotificationStreamSkipsMalformedFramesAndDeliversGoodOnes()
	{
		var handler = new FakeMcpHttpHandler
		{
			SessionIdToIssue = "session-1",
			NotificationStreamResponse = () => new HttpResponseMessage(HttpStatusCode.OK)
			{
				Content = new StringContent(
					"event: message\ndata: { this is not json }\n\n" +
					"event: message\ndata: " + new JsonObject
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

		var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
		client.NotificationReceived += (method, _) => received.TrySetResult(method);

		// 坏帧被跳过，同一批里的好帧仍然要送达。
		Assert.Equal(
			"notifications/tools/list_changed",
			await received.Task.WaitAsync(TimeSpan.FromSeconds(5)));
		Assert.True(client.NotificationStreamActive);
		// 通知流必须带上握手拿到的会话 id。
		Assert.Contains("session-1", handler.NotificationStreamRequests);

		// 后台任务没有 fault，因此关连接时 DisposeAsync 必须干净返回。
		await client.DisposeAsync();
	}
}
