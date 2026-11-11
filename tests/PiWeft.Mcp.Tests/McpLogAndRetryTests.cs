using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using PiWeft.Mcp;
using Xunit;

namespace PiWeft.Mcp.Tests;

/// <c>notifications/message</c> 落盘，以及瞬时 HTTP 故障的重试。
public sealed class McpLogAndRetryTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-mcplog-" + Guid.NewGuid().ToString("N")[..8]);

	public McpLogAndRetryTests() => Directory.CreateDirectory(_root);

	public void Dispose()
	{
		try
		{
			Directory.Delete(_root, recursive: true);
		}
		catch (IOException)
		{
		}
	}

	[Fact]
	public void MessageNotificationIsAppendedWithServerAndLevel()
	{
		var log = new McpServerLog(_root);
		log.Write("demo", new JsonObject
		{
			["level"] = "warning",
			["logger"] = "db",
			["data"] = "connection\npool exhausted",
		});

		var line = Assert.Single(File.ReadAllLines(log.FilePath));
		Assert.Contains("[demo]", line, StringComparison.Ordinal);
		Assert.Contains("warning", line, StringComparison.Ordinal);
		Assert.Contains("db: connection pool exhausted", line, StringComparison.Ordinal);
	}

	[Fact]
	public void OversizedLogIsRotated()
	{
		var log = new McpServerLog(_root);
		File.WriteAllText(log.FilePath, new string('x', 5 * 1024 * 1024));
		log.Append("after\n");

		Assert.True(File.Exists(log.FilePath + ".1"));
		Assert.Equal("after\n", File.ReadAllText(log.FilePath));
	}

	[Fact]
	public async Task TransientStatusIsRetried()
	{
		var failures = 0;
		var handler = new StubHandler(request =>
		{
			// 通知流（GET）也会重试；这里只数请求-响应往返。
			if (request.Method != HttpMethod.Post)
			{
				return new HttpResponseMessage(HttpStatusCode.NotFound);
			}

			if (failures < 2)
			{
				failures++;
				var busy = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
				{
					Content = new StringContent("busy", Encoding.UTF8, "text/plain"),
				};
				busy.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
				return busy;
			}

			return new HttpResponseMessage(HttpStatusCode.OK)
			{
				Content = new StringContent(
					"""{"jsonrpc":"2.0","id":1,"result":{"tools":[]}}""",
					Encoding.UTF8,
					"application/json"),
			};
		});

		await using var client = await McpHttpClient.ConnectAsync(
			"https://mcp.example/mcp",
			headers: null,
			TimeSpan.FromSeconds(10),
			httpClient: new HttpClient(handler));

		var tools = await client.ListToolsAsync(CancellationToken.None);

		Assert.Empty(tools);
		// 两次瞬时故障各重试一次后成功。
		Assert.Equal(2, failures);
	}

	private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
			Task.FromResult(respond(request));
	}
}
