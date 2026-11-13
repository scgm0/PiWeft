using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using PiWeft.Agent;
using PiWeft.Ai;
using PiWeft.CodingAgent;
using PiWeft.CodingAgent.Compatibility;
using PiWeft.CodingAgent.Session;
using PiWeft.CodingAgent.Settings;
using Xunit;

namespace PiWeft.CodingAgent.Tests;

/// PiDeck 的模型请求快照：请求帧带完整 payload、响应帧按到达顺序配对。
[Collection("process-environment")]
public sealed class PiDeckModelTraceTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-model-trace-" + Guid.NewGuid().ToString("N")[..8]);
	private readonly HttpListener _listener = new();
	private readonly List<JsonObject> _frames = [];
	private readonly Lock _gate = new();
	private readonly string? _previousUrl = Environment.GetEnvironmentVariable(PiDeckModelTrace.UrlVariable);
	private readonly string? _previousToken = Environment.GetEnvironmentVariable(PiDeckModelTrace.TokenVariable);
	private readonly CancellationTokenSource _stop = new();

	public PiDeckModelTraceTests()
	{
		Directory.CreateDirectory(_root);
		// 端口 0 不被 HttpListener 支持：让系统分配，再读实际端口。
		var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
		probe.Start();
		var port = ((IPEndPoint)probe.LocalEndpoint).Port;
		probe.Stop();

		_listener.Prefixes.Add($"http://127.0.0.1:{port}/trace/");
		_listener.Start();
		Environment.SetEnvironmentVariable(PiDeckModelTrace.UrlVariable, $"http://127.0.0.1:{port}/trace/token");
		Environment.SetEnvironmentVariable(PiDeckModelTrace.TokenVariable, "token");
		_ = Task.Run(() => ServeAsync(_listener, _stop.Token));
	}

	public void Dispose()
	{
		_stop.Cancel();
		_listener.Close();
		Environment.SetEnvironmentVariable(PiDeckModelTrace.UrlVariable, _previousUrl);
		Environment.SetEnvironmentVariable(PiDeckModelTrace.TokenVariable, _previousToken);
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}
	}

	private async Task ServeAsync(HttpListener listener, CancellationToken cancellationToken)
	{
		while (!cancellationToken.IsCancellationRequested)
		{
			HttpListenerContext context;
			try
			{
				context = await listener.GetContextAsync().ConfigureAwait(false);
			}
			catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException or InvalidOperationException)
			{
				return;
			}

			using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
			var text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
			if (JsonNode.Parse(text) is JsonObject frame)
			{
				lock (_gate)
				{
					_frames.Add(frame);
				}
			}

			context.Response.StatusCode = 200;
			context.Response.Close();
		}
	}

	private async Task<JsonObject> WaitForFrameAsync(string kind)
	{
		for (var attempt = 0; attempt < 100; attempt++)
		{
			lock (_gate)
			{
				if (_frames.FirstOrDefault(frame => frame["kind"]?.GetValue<string>() == kind) is { } frame)
				{
					return frame;
				}
			}

			await Task.Delay(20).ConfigureAwait(false);
		}

		throw new InvalidOperationException($"no {kind} frame arrived");
	}

	[Fact]
	public void TruncationKeepsAValidUtf8Head()
	{
		var json = string.Concat(Enumerable.Repeat("中文", 100));

		var truncated = PiDeckModelTrace.TruncateJsonHead(json, 10);

		Assert.True(Encoding.UTF8.GetByteCount(truncated) <= 10);
		Assert.StartsWith("中", truncated, StringComparison.Ordinal);
		// 恰好落在 ASCII 边界时不能把整串原样返回。
		Assert.Equal(new string('a', 10), PiDeckModelTrace.TruncateJsonHead(new string('a', 20), 10));

		// 字符数小于上限但字节数超了：按字符判断会放过三倍于上限的 CJK 内容。
		var cjk = PiDeckModelTrace.TruncateJsonHead(new string('中', 5), 10);
		Assert.True(Encoding.UTF8.GetByteCount(cjk) <= 10);
		Assert.Equal(3, cjk.Length);
	}

	[Fact]
	public void AttachKeepsExistingCallbacks()
	{
		using var trace = Assert.IsType<PiDeckModelTrace>(PiDeckModelTrace.FromEnvironment("ses-attach"));
		var payloadCalls = 0;
		var responseCalls = 0;
		var options = new StreamOptions
		{
			OnProviderRequestPayload = (_, _) => payloadCalls++,
			OnResponse = (_, _) => responseCalls++,
		};

		var attached = trace.Attach(options);
		var model = new Model { Id = "test-model", Provider = "test" };
		attached.NotifyRequestPayload(new JsonObject { ["model"] = "test-model" }, model);
		attached.OnResponse?.Invoke(new ProviderResponse { Status = 200 }, model);

		// 两个回调都要跑：只保留其中一个会让请求帧永远配不上响应。
		Assert.Equal(1, payloadCalls);
		Assert.Equal(1, responseCalls);
	}

	[Fact]
	public async Task RequestAndResponseFramesArePairedByTraceId()
	{
		using var trace = Assert.IsType<PiDeckModelTrace>(PiDeckModelTrace.FromEnvironment("ses-1"));
		var model = new Model { Id = "test-model", Provider = "test" };
		var payload = new JsonObject
		{
			["model"] = "test-model",
			["messages"] = new JsonArray(new JsonObject { ["role"] = "user" }),
			["tools"] = new JsonArray(new JsonObject { ["name"] = "read" }),
		};

		trace.OnRequestPayload(payload, model);
		trace.OnResponse(new ProviderResponse { Status = 200 });

		var request = await WaitForFrameAsync("request");
		Assert.Equal("ses-1", request["sessionId"]!.GetValue<string>());
		Assert.Equal("test-model", request["model"]!.GetValue<string>());
		Assert.Equal("test", request["provider"]!.GetValue<string>());
		Assert.Equal(1, request["messageCount"]!.GetValue<int>());
		Assert.Equal(1, request["toolCount"]!.GetValue<int>());
		Assert.False(request["truncated"]!.GetValue<bool>());
		Assert.Contains("\"messages\"", request["payloadJson"]!.GetValue<string>());

		var response = await WaitForFrameAsync("response");
		Assert.Equal(request["traceId"]!.GetValue<string>(), response["traceId"]!.GetValue<string>());
		Assert.Equal(200, response["status"]!.GetValue<int>());
		Assert.True(response["durationMs"]!.GetValue<long>() >= 0);
	}

	[Fact]
	public async Task SessionAttachesTheRecorderToItsProviderRequests()
	{
		var script = new ScriptedStreamFn();
		script.EnqueueText("done");
		using var session = new CodingAgentSession(new CodingAgentSessionOptions
		{
			Cwd = _root,
			Model = ScriptedSessions.Model(),
			// 模拟 provider：把请求体与响应状态交给流选项上的观察者。
			StreamFn = (model, messages, options) =>
			{
				options.NotifyRequestPayload(new JsonObject { ["model"] = model.Id, ["messages"] = new JsonArray() }, model);
				options.OnResponse?.Invoke(new ProviderResponse { Status = 200 }, model);
				return script.Invoke(model, messages, options);
			},
			Session = SessionManager.InMemory(_root),
			Skills = [],
			Settings = new CodingAgentSettings(),
		});

		await session.PromptAsync("hi");

		var request = await WaitForFrameAsync("request");
		Assert.Equal(session.Session.SessionId, request["sessionId"]!.GetValue<string>());
		await WaitForFrameAsync("response");
	}

	[Fact]
	public void NoEnvironmentMeansNoRecorder()
	{
		Environment.SetEnvironmentVariable(PiDeckModelTrace.UrlVariable, null);

		Assert.Null(PiDeckModelTrace.FromEnvironment("ses-1"));
	}
}
