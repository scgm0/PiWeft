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
	public async Task OpenCodeRouteCarriesSessionHeader()
	{
		var handler = new FakeHttpHandler { Responder = _ => Sse(StreamBody) };
		var provider = new GoogleGenerativeAiProvider(new HttpClient(handler));
		var model = Model() with { Provider = "opencode", BaseUrl = "https://opencode.ai/zen/v1" };

		var stream = provider.Stream(
			model,
			new Context { Messages = [new UserMessage { Content = MessageContent.FromText("hi"), Timestamp = 1 }] },
			new StreamOptions { ApiKey = "test-key", SessionId = "session-42" });
		await foreach (var _ in stream)
		{
		}

		Assert.Equal("session-42", handler.LastRequest!.Headers.GetValues("x-opencode-session").Single());
		Assert.Equal("piweft", handler.LastRequest.Headers.GetValues("x-opencode-client").Single());
	}

	[Fact]
	public async Task SendsToolResultImagesInsideFunctionResponseForGemini3()
	{
		var handler = new FakeHttpHandler { Responder = _ => Sse(StreamBody) };
		var provider = new GoogleGenerativeAiProvider(new HttpClient(handler));

		var stream = provider.Stream(
			Model("gemini-3-pro") with { Input = ["text", "image"] },
			ToolImageContext(),
			new StreamOptions { ApiKey = "test-key" });
		await foreach (var _ in stream)
		{
		}

		using var document = JsonDocument.Parse(handler.LastBody!);
		var contents = document.RootElement.GetProperty("contents");
		Assert.Equal(3, contents.GetArrayLength());

		var functionResponse = contents[2].GetProperty("parts")[0].GetProperty("functionResponse");
		var inline = functionResponse.GetProperty("parts")[0].GetProperty("inlineData");
		Assert.Equal("image/png", inline.GetProperty("mimeType").GetString());
		Assert.Equal("AAAA", inline.GetProperty("data").GetString());
	}

	[Fact]
	public async Task SendsToolResultImagesAsSeparateUserTurnForGemini2()
	{
		var handler = new FakeHttpHandler { Responder = _ => Sse(StreamBody) };
		var provider = new GoogleGenerativeAiProvider(new HttpClient(handler));

		var stream = provider.Stream(
			Model() with { Input = ["text", "image"] },
			ToolImageContext(),
			new StreamOptions { ApiKey = "test-key" });
		await foreach (var _ in stream)
		{
		}

		using var document = JsonDocument.Parse(handler.LastBody!);
		var contents = document.RootElement.GetProperty("contents");
		// 老模型不认 functionResponse.parts，图片另起一条 user content。
		Assert.Equal(4, contents.GetArrayLength());
		Assert.False(contents[2].GetProperty("parts")[0].GetProperty("functionResponse").TryGetProperty("parts", out _));

		var imageParts = contents[3].GetProperty("parts");
		Assert.Equal("user", contents[3].GetProperty("role").GetString());
		Assert.Equal("Tool result image:", imageParts[0].GetProperty("text").GetString());
		Assert.Equal("AAAA", imageParts[1].GetProperty("inlineData").GetProperty("data").GetString());
	}

	[Fact]
	public async Task PairsToolCallIdsOnlyOnModelsThatUseThem()
	{
		var handler = new FakeHttpHandler { Responder = _ => Sse(StreamBody) };
		var provider = new GoogleGenerativeAiProvider(new HttpClient(handler));

		var stream = provider.Stream(
			Model("gemini-3-pro"),
			ToolImageContext(),
			new StreamOptions { ApiKey = "test-key" });
		await foreach (var _ in stream)
		{
		}

		using var document = JsonDocument.Parse(handler.LastBody!);
		var contents = document.RootElement.GetProperty("contents");
		Assert.Equal("call_1", contents[1].GetProperty("parts")[0].GetProperty("functionCall").GetProperty("id").GetString());
		Assert.Equal("call_1", contents[2].GetProperty("parts")[0].GetProperty("functionResponse").GetProperty("id").GetString());
	}

	[Fact]
	public async Task OmitsToolCallIdsOnGeminiTwo()
	{
		var handler = new FakeHttpHandler { Responder = _ => Sse(StreamBody) };
		var provider = new GoogleGenerativeAiProvider(new HttpClient(handler));

		var stream = provider.Stream(
			Model(),
			ToolImageContext(),
			new StreamOptions { ApiKey = "test-key" });
		await foreach (var _ in stream)
		{
		}

		using var document = JsonDocument.Parse(handler.LastBody!);
		var contents = document.RootElement.GetProperty("contents");
		Assert.False(contents[1].GetProperty("parts")[0].GetProperty("functionCall").TryGetProperty("id", out _));
		Assert.False(contents[2].GetProperty("parts")[0].GetProperty("functionResponse").TryGetProperty("id", out _));
	}

	private static Context ToolImageContext() => new()
	{
		Messages =
		[
			new UserMessage { Content = MessageContent.FromText("读图"), Timestamp = 1 },
			new AssistantMessage
			{
				Content = [new ToolCall { Id = "call_1", Name = "read", Arguments = new() { ["path"] = "a.png" } }],
				Timestamp = 2,
			},
			new ToolResultMessage
			{
				ToolCallId = "call_1",
				ToolName = "read",
				Content =
				[
					new TextContent { Text = "Read image file [image/png]" },
					new ImageContent { Data = "AAAA", MimeType = "image/png" },
				],
				Timestamp = 3,
			},
		],
	};

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
	public async Task ContinuationFinishReasonMapsToLength()
	{
		const string body = """
			data: {"candidates":[{"content":{"role":"model","parts":[{"text":"continued"}]},"finishReason":"CONTINUATION"}]}

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

		// pi 1.5.2+：SDK 的自动续接关掉后，CONTINUATION 按 length 结束一轮。
		var done = Assert.IsType<DoneEvent>(events[^1]);
		Assert.Equal(StopReason.Length, done.Reason);
		Assert.Equal("CONTINUATION", done.Message.RawStopReason);
	}

	[Theory]
	[InlineData("IMAGE_PROHIBITED_CONTENT")]
	[InlineData("FINISH_REASON_UNSPECIFIED")]
	[InlineData("NO_IMAGE")]
	[InlineData("UNKNOWN_FINISH")]
	public async Task BlockedOrUnknownFinishReasonsBecomeErrors(string finishReason)
	{
		var body = "data: {\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[]},\"finishReason\":\"" + finishReason + "\"}]}\n\n";

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

	[Theory]
	[InlineData("gemini-2.5-flash", false)]
	[InlineData("gemini-3-pro", true)]
	[InlineData("gemini-3.1-pro-preview", true)]
	[InlineData("gemini-4-argon", true)]
	// 供应商前缀不改变版本判定（pi #12321）。
	[InlineData("vendor-gemini-4-argon", true)]
	[InlineData("randomprefix-gemini-3.8-flash", true)]
	[InlineData("claude-sonnet-5", false)]
	public void GeminiMajorVersionIgnoresPrefixes(string id, bool strict)
	{
		var model = Model(id);
		Assert.Equal(strict, GoogleWire.SupportsStrictToolSampling(model));
		// Claude 也用 id 配对工具调用；这里只关心 Gemini 的版本判定不因前缀失效。
		if (id.Contains("gemini", StringComparison.Ordinal))
		{
			Assert.Equal(strict, GoogleWire.RequiresToolCallId(id));
		}
	}

	[Fact]
	public void CompatOverridesGeminiStrictMode()
	{
		var older = Model("gemini-2.5-flash") with
		{
			Compat = new System.Text.Json.Nodes.JsonObject { ["supportsStrictMode"] = true },
		};
		Assert.True(GoogleWire.SupportsStrictToolSampling(older));

		var newer = Model("gemini-4-argon") with
		{
			Compat = new System.Text.Json.Nodes.JsonObject { ["supportsStrictMode"] = false },
		};
		Assert.False(GoogleWire.SupportsStrictToolSampling(newer));
	}

	[Fact]
	public async Task CompatStrictModeSendsValidatedTools()
	{
		var handler = new FakeHttpHandler { Responder = _ => Sse(StreamBody) };
		var provider = new GoogleGenerativeAiProvider(new HttpClient(handler));
		// 老 ID 靠 compat 打开严格模式：走 wire 才看得出 VALIDATED 与 required 都生效。
		var model = Model("gemini-2.5-flash") with
		{
			Compat = new System.Text.Json.Nodes.JsonObject { ["supportsStrictMode"] = true },
		};
		var tool = new Tool
		{
			Name = "read",
			Description = "read a file",
			ConstrainedSampling = ConstrainedSampling.JsonSchema(),
			Parameters = new System.Text.Json.Nodes.JsonObject
			{
				["type"] = "object",
				["properties"] = new System.Text.Json.Nodes.JsonObject
				{
					["path"] = new System.Text.Json.Nodes.JsonObject { ["type"] = "string" },
				},
				["required"] = new System.Text.Json.Nodes.JsonArray(
					System.Text.Json.Nodes.JsonValue.Create("path")),
			},
		};

		var stream = provider.Stream(
			model,
			new Context
			{
				Messages = [new UserMessage { Content = MessageContent.FromText("hi") }],
				Tools = [tool],
			},
			new StreamOptions { ApiKey = "k" });

		await foreach (var _ in stream)
		{
		}

		using var document = JsonDocument.Parse(handler.LastBody!);
		var root = document.RootElement;
		Assert.Equal(
			"VALIDATED",
			root.GetProperty("toolConfig").GetProperty("functionCallingConfig").GetProperty("mode").GetString());
		var declaration = root.GetProperty("tools")[0].GetProperty("functionDeclarations")[0];
		Assert.Equal(1, declaration.GetProperty("parametersJsonSchema").GetProperty("required").GetArrayLength());
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
