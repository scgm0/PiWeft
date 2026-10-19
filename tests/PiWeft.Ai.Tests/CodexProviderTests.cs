using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiWeft.Ai;
using PiWeft.Ai.Auth;
using PiWeft.Ai.Providers.OpenAi;
using Xunit;

namespace PiWeft.Ai.Tests;

[Collection("process-environment")]
public class CodexAuthTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "pi-codex-auth-" + Guid.NewGuid().ToString("N")[..8]);
	private readonly HttpClient _http;
	private readonly List<JsonObject> _requests = [];

	public CodexAuthTests()
	{
		Directory.CreateDirectory(_root);

		_http = new HttpClient(new StubHandler(async request =>
		{
			var body = request.Content is null ? "{}" : await request.Content.ReadAsStringAsync();
			_requests.Add(JsonNode.Parse(body)!.AsObject());
			return Route(request.RequestUri!.AbsolutePath, body);
		}));
	}

	public void Dispose()
	{
		_http.Dispose();
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	private HttpResponseMessage Route(string path, string body) => path switch
	{
		"/api/accounts/deviceauth/usercode" => Json("""{"device_auth_id":"dev-1","user_code":"ABCD-1234","interval":1}"""),
		"/api/accounts/deviceauth/token" when _requests.Count(static r => r["device_auth_id"] is not null) < 2 =>
			new HttpResponseMessage(HttpStatusCode.BadRequest)
			{
				Content = new StringContent("""{"error":"authorization_pending"}""", Encoding.UTF8, "application/json"),
			},
		"/api/accounts/deviceauth/token" => Json("""{"authorization_code":"code-1","code_verifier":"verifier-1"}"""),
		"/oauth/token" when body.Contains("authorization_code", StringComparison.Ordinal) =>
			Json("""{"access_token":"access-exchanged","refresh_token":"refresh-exchanged","expires_in":3600}"""),
		"/oauth/token" => Json("""{"access_token":"access-refreshed","refresh_token":"refresh-refreshed","expires_in":3600}"""),
		_ => new HttpResponseMessage(HttpStatusCode.NotFound),
	};

	private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
	{
		Content = new StringContent(body, Encoding.UTF8, "application/json"),
	};

	[Fact]
	public async Task DeviceFlowPollsUntilAuthorizedThenExchangesTheCode()
	{
		var device = await CodexAuth.StartDeviceAsync(_http, "https://auth.example.test");

		Assert.Equal("dev-1", device.DeviceAuthId);
		Assert.Equal("ABCD-1234", device.UserCode);
		Assert.Equal(TimeSpan.FromSeconds(1), device.Interval);
		Assert.Equal("https://auth.example.test/codex/device", device.VerificationUri);
		Assert.Equal(CodexAuth.ClientId, _requests[0]["client_id"]!.GetValue<string>());

		var tokens = await CodexAuth.PollDeviceAsync(
			_http,
			device,
			"https://auth.example.test",
			TimeSpan.FromSeconds(30));

		Assert.Equal("access-exchanged", tokens.Access);
		Assert.Equal("refresh-exchanged", tokens.Refresh);
		Assert.True(tokens.ExpiresAt > DateTimeOffset.UtcNow);

		// The flow polled twice (pending, then authorized) and then exchanged the code.
		var polls = _requests.Count(static r => r["device_auth_id"] is not null);
		Assert.Equal(2, polls);
		var exchange = _requests.Last();
		Assert.Equal("authorization_code", exchange["grant_type"]!.GetValue<string>());
		Assert.Equal("verifier-1", exchange["code_verifier"]!.GetValue<string>());
	}

	[Fact]
	public async Task RefreshExchangesTheRefreshToken()
	{
		var tokens = await CodexAuth.RefreshAsync(_http, "refresh-1", "https://auth.example.test");

		Assert.Equal("access-refreshed", tokens.Access);
		Assert.Equal("refresh-refreshed", tokens.Refresh);

		var request = Assert.Single(_requests);
		Assert.Equal("refresh_token", request["grant_type"]!.GetValue<string>());
		Assert.Equal("refresh-1", request["refresh_token"]!.GetValue<string>());
		Assert.Equal(CodexAuth.ClientId, request["client_id"]!.GetValue<string>());
	}

	[Fact]
	public void AccountIdIsReadFromTheJwtClaim()
	{
		var token = MakeJwt("""
			{"https://api.openai.com/auth":{"chatgpt_account_id":"acct_123"}}
			""");

		Assert.Equal("acct_123", CodexAuth.GetAccountId(token));
		Assert.Null(CodexAuth.GetAccountId(MakeJwt("""{"sub":"x"}""")));
		Assert.Null(CodexAuth.GetAccountId("not-a-jwt"));
		Assert.Null(CodexAuth.GetAccountId("a.!!!not-base64!!!.c"));
	}

	[Fact]
	public void CredentialsRoundTripThroughAuthJsonWithoutClobberingOtherProviders()
	{
		var authPath = Path.Combine(_root, "auth.json");
		File.WriteAllText(authPath, """{"deepseek":{"type":"key","key":"sk-existing"}}""");

		CodexAuth.Save(
			new CodexCredentials
			{
				Access = "access-1",
				Refresh = "refresh-1",
				Expires = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(),
			},
			_root);

		var root = JsonNode.Parse(File.ReadAllText(authPath))!.AsObject();
		Assert.Equal("sk-existing", root["deepseek"]!["key"]!.GetValue<string>());
		Assert.Equal("oauth", root[CodexAuth.ProviderKey]!["type"]!.GetValue<string>());

		var loaded = CodexAuth.Load(_root);
		Assert.NotNull(loaded);
		Assert.Equal("access-1", loaded!.Access);
		Assert.Equal("refresh-1", loaded.Refresh);
	}

	[Fact]
	public async Task ResolveRefreshesAnExpiredCredentialAndPersistsIt()
	{
		CodexAuth.Save(
			new CodexCredentials
			{
				Access = "stale",
				Refresh = "refresh-1",
				// Already expired, so a refresh is required.
				Expires = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds(),
			},
			_root);

		var resolved = await CodexAuth.ResolveAsync(_http, _root, "https://auth.example.test");

		Assert.Equal("access-refreshed", resolved.Access);
		Assert.Equal("access-refreshed", CodexAuth.Load(_root)!.Access);
	}

	[Fact]
	public async Task ResolveKeepsAValidCredential()
	{
		CodexAuth.Save(
			new CodexCredentials
			{
				Access = "still-good",
				Refresh = "refresh-1",
				Expires = DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeSeconds(),
			},
			_root);

		var resolved = await CodexAuth.ResolveAsync(_http, _root, "https://auth.example.test");

		Assert.Equal("still-good", resolved.Access);
		Assert.Empty(_requests);
	}

	[Fact]
	public async Task ResolveWithoutCredentialsExplainsWhatToDo()
	{
		var error = await Assert.ThrowsAsync<CodexAuthException>(() =>
			CodexAuth.ResolveAsync(_http, Path.Combine(_root, "missing"), "https://auth.example.test"));

		Assert.Contains("No ChatGPT credentials", error.Message);
	}

	[Fact]
	public async Task DeviceLoginDisabledIsReportedClearly()
	{
		using var http = new HttpClient(new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))));

		var error = await Assert.ThrowsAsync<CodexAuthException>(() =>
			CodexAuth.StartDeviceAsync(http, "https://auth.example.test"));

		Assert.Contains("not enabled", error.Message);
	}

	private static string MakeJwt(string payload)
	{
		static string Encode(string value) =>
			Convert.ToBase64String(Encoding.UTF8.GetBytes(value)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

		return $"{Encode("""{"alg":"none"}""")}.{Encode(payload)}.sig";
	}

	private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder) : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
			responder(request);
	}
}

[Collection("process-environment")]
public class OpenAiCodexResponsesProviderTests
{
	private const string StreamBody = """
		event: response.created
		data: {"type":"response.created","response":{"id":"resp_codex","model":"gpt-5-codex","status":"in_progress"}}

		event: response.output_item.added
		data: {"type":"response.output_item.added","output_index":0,"item":{"type":"message","id":"m","role":"assistant","content":[]}}

		event: response.output_text.delta
		data: {"type":"response.output_text.delta","output_index":0,"delta":"codex answer"}

		event: response.completed
		data: {"type":"response.completed","response":{"id":"resp_codex","status":"completed","usage":{"input_tokens":6,"output_tokens":3,"total_tokens":9}}}

		""";

	private static Model Model() => new()
	{
		Id = "gpt-5-codex",
		Provider = "openai-codex",
		Api = OpenAiCodexResponsesProvider.ApiId,
		BaseUrl = "https://chatgpt.com/backend-api",
		Reasoning = true,
		ContextWindow = 400_000,
		MaxTokens = 128_000,
		Cost = new ModelCost { Input = 1.25, Output = 10 },
	};

	private static HttpResponseMessage Sse(string body)
	{
		var terminated = body.EndsWith("\n\n", StringComparison.Ordinal) ? body : body.TrimEnd('\n') + "\n\n";
		return new HttpResponseMessage(HttpStatusCode.OK)
		{
			Content = new StringContent(terminated, Encoding.UTF8, "text/event-stream"),
		};
	}

	private static string AccessToken(string accountId = "acct_123")
	{
		static string Encode(string value) =>
			Convert.ToBase64String(Encoding.UTF8.GetBytes(value)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

		return $"{Encode("""{"alg":"none"}""")}.{Encode($"{{\"https://api.openai.com/auth\":{{\"chatgpt_account_id\":\"{accountId}\"}}}}")}.sig";
	}

	[Fact]
	public void UrlNormalisationHandlesBasesWithThePath()
	{
		Assert.Equal("https://chatgpt.com/backend-api/codex/responses", OpenAiCodexResponsesProvider.BuildUrl(Model()));
		Assert.Equal("https://chatgpt.com/backend-api/codex/responses", OpenAiCodexResponsesProvider.BuildUrl(Model() with { BaseUrl = "https://chatgpt.com/backend-api/codex" }));
		Assert.Equal("https://chatgpt.com/backend-api/codex/responses", OpenAiCodexResponsesProvider.BuildUrl(Model() with { BaseUrl = "https://chatgpt.com/backend-api/codex/responses" }));
		Assert.Equal("https://chatgpt.com/backend-api/codex/responses", OpenAiCodexResponsesProvider.BuildUrl(Model() with { BaseUrl = string.Empty }));
		Assert.Equal("wss://chatgpt.com/backend-api/codex/responses", OpenAiCodexResponsesProvider.BuildWebSocketUrl(Model()));
	}

	[Fact]
	public async Task AutoTransportFallsBackToSse()
	{
		var handler = new FakeHttpHandler { Responder = _ => Sse(StreamBody) };
		var provider = new OpenAiCodexResponsesProvider(new HttpClient(handler));

		var stream = provider.Stream(
			Model() with { BaseUrl = "http://127.0.0.1:1" },
			new Context { Messages = [new UserMessage { Content = MessageContent.FromText("hi") }] },
			new StreamOptions { ApiKey = AccessToken(), Transport = "auto", TimeoutMs = 1000 });

		await foreach (var _ in stream)
		{
		}

		var message = await stream.Result;
		Assert.Equal("codex answer", Assert.IsType<TextContent>(message.Content[0]).Text);
		Assert.NotNull(handler.LastRequest);
	}

	[Fact]
	public async Task WebSocketTransportStreamsCodexEvents()
	{
		var probe = new TcpListener(IPAddress.Loopback, 0);
		probe.Start();
		var port = ((IPEndPoint)probe.LocalEndpoint).Port;
		probe.Stop();

		using var listener = new HttpListener();
		listener.Prefixes.Add($"http://127.0.0.1:{port}/");
		listener.Start();
		var server = Task.Run(async () =>
		{
			var context = await listener.GetContextAsync();
			var accepted = await context.AcceptWebSocketAsync(null);
			var socket = accepted.WebSocket;
			var buffer = new byte[4096];
			await socket.ReceiveAsync(buffer, CancellationToken.None);

			foreach (var payload in new[]
			{
				"""{"type":"response.created","response":{"id":"resp_ws","model":"gpt-5-codex","status":"in_progress"}}""",
				"""{"type":"response.output_item.added","output_index":0,"item":{"type":"message","id":"m","role":"assistant","content":[]}}""",
				"""{"type":"response.output_text.delta","output_index":0,"delta":"websocket answer"}""",
				"""{"type":"response.completed","response":{"id":"resp_ws","status":"completed","usage":{"input_tokens":6,"output_tokens":3,"total_tokens":9}}}""",
			})
			{
				await socket.SendAsync(Encoding.UTF8.GetBytes(payload), WebSocketMessageType.Text, true, CancellationToken.None);
			}

			await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
		});

		var provider = new OpenAiCodexResponsesProvider(new HttpClient());
		var stream = provider.Stream(
			Model() with { BaseUrl = $"http://127.0.0.1:{port}" },
			new Context { Messages = [new UserMessage { Content = MessageContent.FromText("hi") }] },
			new StreamOptions { ApiKey = AccessToken(), Transport = "websocket" });

		await foreach (var _ in stream)
		{
		}

		var message = await stream.Result;
		Assert.Equal("websocket answer", Assert.IsType<TextContent>(message.Content[0]).Text);
		await server.WaitAsync(TimeSpan.FromSeconds(5));
	}

	[Fact]
	public async Task StreamsWithChatGptHeadersAndCodexBody()
	{
		var handler = new FakeHttpHandler { Responder = _ => Sse(StreamBody) };
		var provider = new OpenAiCodexResponsesProvider(new HttpClient(handler));

		var stream = provider.Stream(
			Model(),
			new Context
			{
				SystemPrompt = "be terse",
				Messages = [new UserMessage { Content = MessageContent.FromText("hi"), Timestamp = 1 }],
			},
			new StreamOptions { ApiKey = AccessToken(), SessionId = "session-1", Transport = "sse" });

		await foreach (var _ in stream)
		{
		}

		var message = await stream.Result;
		Assert.Equal("codex answer", Assert.IsType<TextContent>(message.Content[0]).Text);
		Assert.Equal("openai-codex-responses", message.Api);

		var request = handler.LastRequest!;
		Assert.Equal("https://chatgpt.com/backend-api/codex/responses", request.RequestUri!.ToString());
		Assert.Equal($"Bearer {AccessToken()}", request.Headers.GetValues("authorization").Single());
		Assert.Equal("acct_123", request.Headers.GetValues("chatgpt-account-id").Single());
		Assert.Equal("pi", request.Headers.GetValues("originator").Single());

		using var document = JsonDocument.Parse(handler.LastBody!);
		var root = document.RootElement;

		Assert.Equal("gpt-5-codex", root.GetProperty("model").GetString());
		// The Codex backend rejects stored responses.
		Assert.False(root.GetProperty("store").GetBoolean());
		Assert.Equal("session-1", root.GetProperty("prompt_cache_key").GetString());
		Assert.Equal("be terse", root.GetProperty("instructions").GetString());
	}

	[Fact]
	public async Task AMissingAccountClaimIsReportedClearly()
	{
		var handler = new FakeHttpHandler();
		var provider = new OpenAiCodexResponsesProvider(new HttpClient(handler));

		var stream = provider.Stream(
			Model(),
			new Context { Messages = [new UserMessage { Content = MessageContent.FromText("hi") }] },
			new StreamOptions { ApiKey = "not-a-jwt" });

		var events = new List<AssistantMessageEvent>();
		await foreach (var @event in stream)
		{
			events.Add(@event);
		}

		var error = Assert.IsType<ErrorEvent>(events[^1]);
		Assert.Contains("account id", error.Error.ErrorMessage);
		Assert.Null(handler.LastRequest);
	}
}
