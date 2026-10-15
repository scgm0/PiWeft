using System.Net;
using System.Text;
using System.Text.Json;
using PiWeft.Ai;
using PiWeft.Ai.Providers.Google;
using Xunit;

namespace PiWeft.Ai.Tests;

public class GoogleGenerativeAiProviderTests
{
	private static Model Model(string id = "gemini-2.5-flash") => new()
	{
		Id = id,
		Provider = "google",
		Api = GoogleGenerativeAiProvider.ApiId,
		BaseUrl = "https://generativelanguage.googleapis.com/v1beta",
		Reasoning = true,
		ContextWindow = 1_048_576,
		MaxTokens = 65_536,
		Cost = new ModelCost { Input = 0.3, Output = 2.5, CacheRead = 0.03 },
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
		data: {"responseId":"resp_g1","modelVersion":"gemini-2.5-flash","candidates":[{"content":{"role":"model","parts":[{"text":"thinking about it","thought":true,"thoughtSignature":"sig-1"}]}}]}

		data: {"candidates":[{"content":{"role":"model","parts":[{"text":"Hello"}]}}]}

		data: {"candidates":[{"content":{"role":"model","parts":[{"text":" world"}]}}]}

		data: {"candidates":[{"content":{"role":"model","parts":[{"functionCall":{"name":"read","args":{"path":"a.txt"},"id":"call_1"},"thoughtSignature":"sig-2"}]}}]}

		data: {"candidates":[{"content":{"role":"model","parts":[]},"finishReason":"STOP"}],"usageMetadata":{"promptTokenCount":30,"cachedContentTokenCount":10,"candidatesTokenCount":7,"thoughtsTokenCount":4}}

		""";

	[Fact]
	public async Task StreamsThinkingTextAndFunctionCall()
	{
		var handler = new FakeHttpHandler { Responder = _ => Sse(StreamBody) };
		var provider = new GoogleGenerativeAiProvider(new HttpClient(handler));

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
				ToolCallEndEvent e => $"toolcall_end:{e.ToolCall.Name}",
				DoneEvent d => $"done:{d.Reason}",
				ErrorEvent e => $"error:{e.Error.ErrorMessage}",
				_ => "other",
			});
		}

		Assert.Equal("start", events[0]);
		Assert.Contains("thinking_delta:thinking about it", events);
		Assert.Contains("thinking_end:thinking about it", events);
		Assert.Contains("text_end:Hello world", events);
		Assert.Contains("toolcall_end:read", events);
		Assert.Equal("done:ToolUse", events[^1]);

		var message = await stream.Result;
		Assert.Equal(3, message.Content.Count);

		var thinking = Assert.IsType<ThinkingContent>(message.Content[0]);
		Assert.Equal("thinking about it", thinking.Thinking);
		Assert.Equal("sig-1", thinking.ThinkingSignature);

		var text = Assert.IsType<TextContent>(message.Content[1]);
		Assert.Equal("Hello world", text.Text);

		var call = Assert.IsType<ToolCall>(message.Content[2]);
		Assert.Equal("call_1", call.Id);
		Assert.Equal("a.txt", call.Arguments["path"]!.GetValue<string>());
		Assert.Equal("sig-2", call.ThoughtSignature);

		Assert.Equal("resp_g1", message.ResponseId);
		Assert.Equal("gemini-2.5-flash", message.ResponseModel);
		Assert.Equal("STOP", message.RawStopReason);

		// Cached tokens are excluded from input and thinking tokens are part of the output.
		Assert.Equal(20, message.Usage.Input);
		Assert.Equal(11, message.Usage.Output);
		Assert.Equal(10, message.Usage.CacheRead);
		Assert.Equal(4, message.Usage.Reasoning);
		Assert.True(message.Usage.Cost.Total > 0);
	}

	[Fact]
	public async Task SendsExpectedRequest()
	{
		var handler = new FakeHttpHandler { Responder = _ => Sse(StreamBody) };
		var provider = new GoogleGenerativeAiProvider(new HttpClient(handler));

		var context = new Context
		{
			SystemPrompt = "be terse",
			Messages =
			[
				new UserMessage { Content = MessageContent.FromText("hi"), Timestamp = 1 },
				new AssistantMessage
				{
					Provider = "google",
					Model = "gemini-2.5-flash",
					Content = [new ToolCall { Id = "call_9", Name = "read", Arguments = new() { ["path"] = "x" } }],
					Timestamp = 2,
				},
				new ToolResultMessage
				{
					ToolCallId = "call_9",
					ToolName = "read",
					Content = [new TextContent { Text = "contents" }],
					Timestamp = 3,
				},
			],
			Tools = [new Tool { Name = "read", Description = "read a file", Parameters = new() { ["type"] = "object" } }],
		};

		var stream = provider.Stream(
			Model(),
			context,
			new StreamOptions { ApiKey = "test-key", MaxTokens = 4096, ThinkingLevel = ThinkingLevels.High });

		await foreach (var _ in stream)
		{
		}

		var request = handler.LastRequest!;
		Assert.Equal(
			"https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:streamGenerateContent?alt=sse",
			request.RequestUri!.ToString());
		Assert.Equal("test-key", request.Headers.GetValues("x-goog-api-key").Single());

		using var document = JsonDocument.Parse(handler.LastBody!);
		var root = document.RootElement;

		Assert.Equal("be terse", root.GetProperty("systemInstruction").GetProperty("parts")[0].GetProperty("text").GetString());

		var contents = root.GetProperty("contents");
		Assert.Equal(3, contents.GetArrayLength());
		Assert.Equal("user", contents[0].GetProperty("role").GetString());
		Assert.Equal("model", contents[1].GetProperty("role").GetString());
		Assert.Equal("read", contents[1].GetProperty("parts")[0].GetProperty("functionCall").GetProperty("name").GetString());

		// Tool results arrive as one merged user turn carrying functionResponse parts.
		Assert.Equal("user", contents[2].GetProperty("role").GetString());
		var functionResponse = contents[2].GetProperty("parts")[0].GetProperty("functionResponse");
		Assert.Equal("read", functionResponse.GetProperty("name").GetString());
		Assert.Equal("contents", functionResponse.GetProperty("response").GetProperty("output").GetString());

		var tool = root.GetProperty("tools")[0].GetProperty("functionDeclarations")[0];
		Assert.Equal("read", tool.GetProperty("name").GetString());
		Assert.True(tool.TryGetProperty("parametersJsonSchema", out _));

		var generationConfig = root.GetProperty("generationConfig");
		Assert.Equal(4096, generationConfig.GetProperty("maxOutputTokens").GetInt32());
		Assert.Equal(16384, generationConfig.GetProperty("thinkingConfig").GetProperty("thinkingBudget").GetInt32());
		Assert.True(generationConfig.GetProperty("thinkingConfig").GetProperty("includeThoughts").GetBoolean());
	}

	[Fact]
	public async Task SafetyBlockBecomesAnErrorEvent()
	{
		const string body = """
			data: {"candidates":[{"content":{"role":"model","parts":[]},"finishReason":"SAFETY"}]}

			""";

		var handler = new FakeHttpHandler { Responder = _ => Sse(body) };
		var provider = new GoogleGenerativeAiProvider(new HttpClient(handler));

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
		Assert.Contains("safety filters", error.Error.ErrorMessage);
	}

	[Fact]
	public async Task MaxTokensFinishReasonMapsToLength()
	{
		const string body = """
			data: {"candidates":[{"content":{"role":"model","parts":[{"text":"cut off"}]},"finishReason":"MAX_TOKENS"}]}

			""";

		var handler = new FakeHttpHandler { Responder = _ => Sse(body) };
		var provider = new GoogleGenerativeAiProvider(new HttpClient(handler));

		var stream = provider.Stream(
			Model(),
			new Context { Messages = [new UserMessage { Content = MessageContent.FromText("hi") }] },
			new StreamOptions { ApiKey = "k" });

		var events = new List<AssistantMessageEvent>();
		await foreach (var @event in stream)
		{
			events.Add(@event);
		}

		var done = Assert.IsType<DoneEvent>(events[^1]);
		Assert.Equal(StopReason.Length, done.Reason);
		Assert.Equal("MAX_TOKENS", done.Message.RawStopReason);
	}

	[Fact]
	public void ThinkingBudgetFollowsLevelAndModelSupport()
	{
		var model = Model();

		Assert.Equal(16384, GoogleGenerativeAiProvider.ResolveThinkingBudget(model, new StreamOptions { ThinkingLevel = ThinkingLevels.High }));
		Assert.Equal(0, GoogleGenerativeAiProvider.ResolveThinkingBudget(model, new StreamOptions { ThinkingLevel = ThinkingLevels.Off }));
		Assert.Null(GoogleGenerativeAiProvider.ResolveThinkingBudget(model with { Reasoning = false }, new StreamOptions()));

		var mapped = model with
		{
			ThinkingLevelMap = new Dictionary<string, string?> { ["high"] = "24576" },
		};
		Assert.Equal(24576, GoogleGenerativeAiProvider.ResolveThinkingBudget(mapped, new StreamOptions { ThinkingLevel = ThinkingLevels.High }));
	}

	[Fact]
	public async Task GeneratedToolCallIdsAreUnique()
	{
		// Gemini may omit ids entirely, which must not collide across calls.
		const string body = """
			data: {"candidates":[{"content":{"role":"model","parts":[{"functionCall":{"name":"read","args":{"path":"a"}}},{"functionCall":{"name":"read","args":{"path":"b"}}}]}}]}

			""";

		var handler = new FakeHttpHandler { Responder = _ => Sse(body) };
		var provider = new GoogleGenerativeAiProvider(new HttpClient(handler));

		var stream = provider.Stream(
			Model(),
			new Context { Messages = [new UserMessage { Content = MessageContent.FromText("hi") }] },
			new StreamOptions { ApiKey = "k" });

		await foreach (var _ in stream)
		{
		}

		var message = await stream.Result;
		var calls = message.Content.OfType<ToolCall>().ToList();
		Assert.Equal(2, calls.Count);
		Assert.NotEqual(calls[0].Id, calls[1].Id);
		Assert.All(calls, static call => Assert.StartsWith("read_", call.Id));
	}
}
