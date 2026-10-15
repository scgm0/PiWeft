using System.Net;
using System.Text;
using System.Text.Json;
using PiWeft.Ai;
using PiWeft.Ai.Providers.PiMessages;
using Xunit;

namespace PiWeft.Ai.Tests;

/// <summary>The pi-messages protocol: a POST of { model, context, options } returning serialized assistant events.</summary>
public class PiMessagesProviderTests
{
	private static Model Model() => new()
	{
		Id = "pi-sonnet",
		Provider = "pi-cloud",
		Api = PiMessagesProvider.ApiId,
		BaseUrl = "https://pi-cloud.example.test/v1",
		Reasoning = true,
		ContextWindow = 200_000,
		MaxTokens = 8192,
		Cost = new ModelCost { Input = 3, Output = 15 },
	};

	private static HttpResponseMessage Sse(string body)
	{
		var terminated = body.EndsWith("\n\n", StringComparison.Ordinal) ? body : body.TrimEnd('\n') + "\n\n";
		return new HttpResponseMessage(HttpStatusCode.OK)
		{
			Content = new StringContent(terminated, Encoding.UTF8, "text/event-stream"),
		};
	}

	private const string StreamBody = """
		data: {"type":"start"}

		data: {"type":"thinking_start","contentIndex":0}

		data: {"type":"thinking_delta","contentIndex":0,"delta":"weighing options"}

		data: {"type":"thinking_end","contentIndex":0,"content":"weighing options","contentSignature":"sig-1"}

		data: {"type":"text_start","contentIndex":1}

		data: {"type":"text_delta","contentIndex":1,"delta":"Hello"}

		data: {"type":"text_delta","contentIndex":1,"delta":" world"}

		data: {"type":"text_end","contentIndex":1,"content":"Hello world","contentSignature":"text-sig"}

		data: {"type":"toolcall_start","contentIndex":2,"id":"call_1","toolName":"read"}

		data: {"type":"toolcall_delta","contentIndex":2,"delta":"{\"path\":\"a.txt\"}"}

		data: {"type":"toolcall_end","contentIndex":2,"toolCall":{"type":"toolCall","id":"call_1","name":"read","arguments":{"path":"a.txt"}}}

		data: {"type":"done","reason":"toolUse","usage":{"input":11,"output":5,"cacheRead":3,"cacheWrite":1,"totalTokens":20},"responseId":"resp_pi_1","providerThinkingLevel":"high"}

		""";

	[Fact]
	public async Task DecodesTheEventStreamIntoAMessage()
	{
		var handler = new FakeHttpHandler { Responder = _ => Sse(StreamBody) };
		var provider = new PiMessagesProvider(new HttpClient(handler));

		var stream = provider.Stream(
			Model(),
			new Context { Messages = [new UserMessage { Content = MessageContent.FromText("hi"), Timestamp = 1 }] },
			new StreamOptions { ApiKey = "test-key" });

		var events = new List<string>();
		await foreach (var @event in stream)
		{
			events.Add(@event switch
			{
				StartEvent => "start",
				ThinkingStartEvent => "thinking_start",
				ThinkingDeltaEvent d => $"thinking_delta:{d.Delta}",
				ThinkingEndEvent e => $"thinking_end:{e.Content}",
				TextStartEvent => "text_start",
				TextDeltaEvent d => $"text_delta:{d.Delta}",
				TextEndEvent e => $"text_end:{e.Content}",
				ToolCallStartEvent => "toolcall_start",
				ToolCallDeltaEvent d => $"toolcall_delta:{d.Delta}",
				ToolCallEndEvent e => $"toolcall_end:{e.ToolCall.Name}",
				DoneEvent d => $"done:{d.Reason}",
				ErrorEvent e => $"error:{e.Error.ErrorMessage}",
				_ => "other",
			});
		}

		Assert.Equal("start", events[0]);
		Assert.Contains("thinking_delta:weighing options", events);
		Assert.Contains("text_delta:Hello", events);
		Assert.Contains("text_delta: world", events);
		Assert.Contains("text_end:Hello world", events);
		Assert.Contains("toolcall_end:read", events);
		Assert.Equal("done:ToolUse", events[^1]);

		var message = await stream.Result;
		Assert.Equal(3, message.Content.Count);

		var thinking = Assert.IsType<ThinkingContent>(message.Content[0]);
		Assert.Equal("weighing options", thinking.Thinking);
		Assert.Equal("sig-1", thinking.ThinkingSignature);

		var text = Assert.IsType<TextContent>(message.Content[1]);
		Assert.Equal("Hello world", text.Text);
		Assert.Equal("text-sig", text.TextSignature);

		var call = Assert.IsType<ToolCall>(message.Content[2]);
		Assert.Equal("call_1", call.Id);
		Assert.Equal("a.txt", call.Arguments["path"]!.GetValue<string>());

		Assert.Equal("resp_pi_1", message.ResponseId);
		Assert.Equal("high", message.ProviderThinkingLevel);
		Assert.Equal(StopReason.ToolUse, message.StopReason);
		Assert.Equal(11, message.Usage.Input);
		Assert.Equal(5, message.Usage.Output);
		Assert.Equal(3, message.Usage.CacheRead);
		Assert.Equal(1, message.Usage.CacheWrite);
		Assert.Equal(20, message.Usage.TotalTokens);
	}

	[Fact]
	public async Task SendsModelContextAndOptions()
	{
		var handler = new FakeHttpHandler { Responder = _ => Sse(StreamBody) };
		var provider = new PiMessagesProvider(new HttpClient(handler));

		var context = new Context
		{
			SystemPrompt = "be terse",
			Messages = [new UserMessage { Content = MessageContent.FromText("hi"), Timestamp = 1 }],
			Tools = [new Tool { Name = "read", Description = "read a file", Parameters = new() { ["type"] = "object" } }],
		};

		var stream = provider.Stream(
			Model(),
			context,
			new StreamOptions
			{
				ApiKey = "test-key",
				Temperature = 0.5,
				MaxTokens = 256,
				ThinkingLevel = ThinkingLevels.Low,
				SessionId = "session-1",
			});

		await foreach (var _ in stream)
		{
		}

		var request = handler.LastRequest!;
		Assert.Equal("https://pi-cloud.example.test/v1/messages", request.RequestUri!.ToString());
		Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
		Assert.Equal("test-key", request.Headers.Authorization.Parameter);

		using var document = JsonDocument.Parse(handler.LastBody!);
		var root = document.RootElement;

		Assert.Equal("pi-sonnet", root.GetProperty("model").GetString());

		var sentContext = root.GetProperty("context");
		Assert.Equal("be terse", sentContext.GetProperty("systemPrompt").GetString());
		Assert.Equal("hi", sentContext.GetProperty("messages")[0].GetProperty("content").GetString());
		Assert.Equal("read", sentContext.GetProperty("tools")[0].GetProperty("name").GetString());

		var options = root.GetProperty("options");
		Assert.Equal(0.5, options.GetProperty("temperature").GetDouble());
		Assert.Equal(256, options.GetProperty("maxTokens").GetInt32());
		Assert.Equal("low", options.GetProperty("reasoning").GetString());
		Assert.Equal("session-1", options.GetProperty("sessionId").GetString());
	}

	[Fact]
	public async Task ErrorEventCarriesTheBackendMessage()
	{
		const string body = """
			data: {"type":"start"}

			data: {"type":"error","reason":"error","usage":{"input":3,"output":0,"cacheRead":0,"cacheWrite":0,"totalTokens":3},"errorMessage":"backend exploded"}

			""";

		var handler = new FakeHttpHandler { Responder = _ => Sse(body) };
		var provider = new PiMessagesProvider(new HttpClient(handler));

		var stream = provider.Stream(
			Model(),
			new Context { Messages = [new UserMessage { Content = MessageContent.FromText("hi") }] },
			new StreamOptions { ApiKey = "k" });

		var events = new List<AssistantMessageEvent>();
		await foreach (var @event in stream)
		{
			events.Add(@event);
		}

		var error = Assert.IsType<ErrorEvent>(events[^1]);
		Assert.Equal(StopReason.Error, error.Reason);
		Assert.Equal("backend exploded", error.Error.ErrorMessage);
		Assert.Equal(3, error.Error.Usage.TotalTokens);
	}

	[Fact]
	public async Task AStreamWithoutATerminalEventIsAnError()
	{
		const string body = """
			data: {"type":"start"}

			data: {"type":"text_start","contentIndex":0}

			data: {"type":"text_delta","contentIndex":0,"delta":"cut off"}

			""";

		var handler = new FakeHttpHandler { Responder = _ => Sse(body) };
		var provider = new PiMessagesProvider(new HttpClient(handler));

		var stream = provider.Stream(
			Model(),
			new Context { Messages = [new UserMessage { Content = MessageContent.FromText("hi") }] },
			new StreamOptions { ApiKey = "k" });

		var events = new List<AssistantMessageEvent>();
		await foreach (var @event in stream)
		{
			events.Add(@event);
		}

		var error = Assert.IsType<ErrorEvent>(events[^1]);
		Assert.Contains("terminal event", error.Error.ErrorMessage);
		// The partial answer is still reported.
		Assert.Equal("cut off", Assert.IsType<TextContent>(error.Error.Content[0]).Text);
	}

	[Fact]
	public async Task HttpErrorsIncludeTheBackendCode()
	{
		var handler = new FakeHttpHandler
		{
			Responder = _ => new HttpResponseMessage(HttpStatusCode.PaymentRequired)
			{
				Content = new StringContent(
					"""{"error":{"message":"quota exhausted","code":"insufficient_quota"}}""",
					Encoding.UTF8,
					"application/json"),
			},
		};

		var provider = new PiMessagesProvider(new HttpClient(handler));
		var stream = provider.Stream(
			Model(),
			new Context { Messages = [new UserMessage { Content = MessageContent.FromText("hi") }] },
			new StreamOptions { ApiKey = "k" });

		var events = new List<AssistantMessageEvent>();
		await foreach (var @event in stream)
		{
			events.Add(@event);
		}

		var error = Assert.IsType<ErrorEvent>(Assert.Single(events));
		Assert.Contains("quota exhausted", error.Error.ErrorMessage);
		Assert.Contains("insufficient_quota", error.Error.ErrorMessage);
	}

	[Fact]
	public async Task MissingApiKeyFailsBeforeAnyRequest()
	{
		var handler = new FakeHttpHandler();
		var provider = new PiMessagesProvider(new HttpClient(handler));

		var previous = Environment.GetEnvironmentVariable("PI_CLOUD_API_KEY");
		Environment.SetEnvironmentVariable("PI_CLOUD_API_KEY", null);
		try
		{
			var stream = provider.Stream(
				Model(),
				new Context { Messages = [new UserMessage { Content = MessageContent.FromText("hi") }] },
				new StreamOptions());

			var events = new List<AssistantMessageEvent>();
			await foreach (var @event in stream)
			{
				events.Add(@event);
			}

			Assert.Contains("No API key", Assert.IsType<ErrorEvent>(events[^1]).Error.ErrorMessage);
			Assert.Null(handler.LastRequest);
		}
		finally
		{
			Environment.SetEnvironmentVariable("PI_CLOUD_API_KEY", previous);
		}
	}
}
