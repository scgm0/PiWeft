using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiWeft.Ai;
using PiWeft.Ai.Providers.Anthropic;
using Xunit;

namespace PiWeft.Ai.Tests;

internal sealed class FakeHttpHandler : HttpMessageHandler
{
	public Func<HttpRequestMessage, HttpResponseMessage>? Responder { get; set; }

	public HttpRequestMessage? LastRequest { get; private set; }

	public string? LastBody { get; private set; }

	/// 每次请求的请求体，按发送顺序；重试类用例要看「降级后的那一次」发了什么。
	public List<string> Bodies { get; } = [];

	protected override async Task<HttpResponseMessage> SendAsync(
		HttpRequestMessage request,
		CancellationToken cancellationToken)
	{
		LastRequest = request;
		LastBody = request.Content is null
			? null
			: await request.Content.ReadAsStringAsync(cancellationToken);
		if (LastBody is not null)
		{
			Bodies.Add(LastBody);
		}
		return Responder?.Invoke(request) ?? new HttpResponseMessage(HttpStatusCode.OK);
	}
}

public class AnthropicProviderTests
{
	private static Model Sonnet() => new()
	{
		Id = "claude-sonnet-4-5",
		Provider = "anthropic",
		Api = AnthropicProvider.ApiId,
		BaseUrl = "https://api.anthropic.com",
		Reasoning = true,
		ContextWindow = 200_000,
		MaxTokens = 8192,
		Cost = new ModelCost { Input = 3, Output = 15, CacheRead = 0.3, CacheWrite = 3.75 },
	};

	private static HttpResponseMessage Sse(string body) => new(HttpStatusCode.OK)
	{
		Content = new StringContent(body, Encoding.UTF8, "text/event-stream"),
	};

	private const string StreamBody = """
		event: message_start
		data: {"type":"message_start","message":{"id":"msg_1","model":"claude-sonnet-4-5","usage":{"input_tokens":10,"cache_read_input_tokens":2,"cache_creation_input_tokens":3}}}

		event: content_block_start
		data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}

		event: content_block_delta
		data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Hello"}}

		event: content_block_delta
		data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":" world"}}

		event: content_block_stop
		data: {"type":"content_block_stop","index":0}

		event: content_block_start
		data: {"type":"content_block_start","index":1,"content_block":{"type":"tool_use","id":"toolu_1","name":"read","input":{}}}

		event: content_block_delta
		data: {"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"{\"path\":"}}

		event: content_block_delta
		data: {"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"\"a.txt\"}"}}

		event: content_block_stop
		data: {"type":"content_block_stop","index":1}

		event: message_delta
		data: {"type":"message_delta","delta":{"stop_reason":"tool_use"},"usage":{"output_tokens":7}}

		event: message_stop
		data: {"type":"message_stop"}

		""";

	[Fact]
	public async Task StreamsTextAndToolCall()
	{
		var handler = new FakeHttpHandler { Responder = _ => Sse(StreamBody) };
		var provider = new AnthropicProvider(new HttpClient(handler));

		var context = new Context
		{
			SystemPrompt = "be terse",
			Messages = [new UserMessage { Content = MessageContent.FromText("hi"), Timestamp = 1 }],
			Tools =
			[
				new Tool
				{
					Name = "read",
					Description = "read a file",
					Parameters = new System.Text.Json.Nodes.JsonObject
					{
						["type"] = "object",
						["properties"] = new System.Text.Json.Nodes.JsonObject
						{
							["path"] = new System.Text.Json.Nodes.JsonObject { ["type"] = "string" },
						},
					},
				},
			],
		};

		var stream = provider.Stream(Sonnet(), context, new StreamOptions { ApiKey = "test-key" });

		var events = new List<string>();
		await foreach (var @event in stream)
		{
			events.Add(@event switch
			{
				StartEvent => "start",
				TextStartEvent => "text_start",
				TextDeltaEvent delta => $"text_delta:{delta.Delta}",
				TextEndEvent end => $"text_end:{end.Content}",
				ToolCallStartEvent => "toolcall_start",
				ToolCallDeltaEvent delta => $"toolcall_delta:{delta.Delta}",
				ToolCallEndEvent end => $"toolcall_end:{end.ToolCall.Name}",
				DoneEvent done => $"done:{done.Reason}",
				ErrorEvent error => $"error:{error.Error.ErrorMessage}",
				_ => "other",
			});
		}

		Assert.Equal("start", events[0]);
		Assert.Contains("text_start", events);
		Assert.Contains("text_delta:Hello", events);
		Assert.Contains("text_delta: world", events);
		Assert.Contains("text_end:Hello world", events);
		Assert.Contains("toolcall_start", events);
		Assert.Contains("toolcall_end:read", events);
		Assert.Equal("done:ToolUse", events[^1]);

		var message = await stream.Result;
		Assert.Equal(2, message.Content.Count);
		Assert.Equal("Hello world", Assert.IsType<TextContent>(message.Content[0]).Text);
		var call = Assert.IsType<ToolCall>(message.Content[1]);
		Assert.Equal("a.txt", call.Arguments["path"]!.GetValue<string>());
		Assert.Equal("msg_1", message.ResponseId);
		Assert.Equal(StopReason.ToolUse, message.StopReason);
		Assert.Equal(10, message.Usage.Input);
		Assert.Equal(7, message.Usage.Output);
		Assert.Equal(2, message.Usage.CacheRead);
		Assert.Equal(3, message.Usage.CacheWrite);
		Assert.Equal(22, message.Usage.TotalTokens);
		Assert.True(message.Usage.Cost.Total > 0);
		Assert.NotNull(message.DurationMs);
	}

	[Fact]
	public async Task AnthropicOAuthCarriesClaudeCodeHeadersAndIdentityPrompt()
	{
		var handler = new FakeHttpHandler { Responder = _ => Sse(StreamBody) };
		var provider = new AnthropicProvider(new HttpClient(handler));

		var stream = provider.Stream(
			Sonnet(),
			new Context { Messages = [new UserMessage { Content = MessageContent.FromText("hi"), Timestamp = 1 }] },
			new StreamOptions { ApiKey = "sk-ant-oat-test" });
		await foreach (var _ in stream)
		{
		}

		var request = handler.LastRequest!;
		Assert.Equal("Bearer sk-ant-oat-test", request.Headers.Authorization!.ToString());
		Assert.Equal(AnthropicProvider.ClaudeCliUserAgent, request.Headers.UserAgent.ToString());
		Assert.Equal("cli", request.Headers.GetValues("x-app").Single());
		Assert.Equal("true", request.Headers.GetValues("anthropic-dangerous-direct-browser-access").Single());
		Assert.Contains("claude-code-20250219", request.Headers.GetValues("anthropic-beta").Single());

		using var body = JsonDocument.Parse(handler.LastBody!);
		var system = body.RootElement.GetProperty("system");
		Assert.Equal(1, system.GetArrayLength());
		Assert.Equal("You are Claude Code, Anthropic's official CLI for Claude.", system[0].GetProperty("text").GetString());
	}

	[Fact]
	public async Task CopilotAnthropicRequestsCarryDynamicAndConfiguredAuthHeaders()
	{
		var handler = new FakeHttpHandler { Responder = _ => Sse(StreamBody) };
		var provider = new AnthropicProvider(new HttpClient(handler));
		var model = Sonnet() with
		{
			Provider = "github-copilot",
			BaseUrl = "https://api.individual.githubcopilot.com",
			AuthHeader = true,
		};

		var stream = provider.Stream(
			model,
			new Context { Messages = [new UserMessage { Content = MessageContent.FromText("hi"), Timestamp = 1 }] },
			new StreamOptions { ApiKey = "copilot-token" });
		await foreach (var _ in stream)
		{
		}

		var request = handler.LastRequest!;
		Assert.Equal("user", request.Headers.GetValues("X-Initiator").Single());
		Assert.Equal("conversation-edits", request.Headers.GetValues("Openai-Intent").Single());
		Assert.Equal("Bearer copilot-token", request.Headers.Authorization!.ToString());
		Assert.False(request.Headers.Contains("x-api-key"));
		Assert.Equal("true", request.Headers.GetValues("anthropic-dangerous-direct-browser-access").Single());
	}

	[Fact]
	public async Task OpenCodeRouteCarriesSessionHeader()
	{
		var handler = new FakeHttpHandler { Responder = _ => Sse(StreamBody) };
		var provider = new AnthropicProvider(new HttpClient(handler));
		var model = Sonnet() with { Provider = "opencode", BaseUrl = "https://opencode.ai/zen" };

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
	public async Task SendsExpectedRequest()
	{
		var handler = new FakeHttpHandler { Responder = _ => Sse(StreamBody) };
		var provider = new AnthropicProvider(new HttpClient(handler));

		var context = new Context
		{
			SystemPrompt = "be terse",
			Messages =
			[
				new UserMessage { Content = MessageContent.FromText("hi"), Timestamp = 1 },
				new AssistantMessage
				{
					Content = [new ToolCall { Id = "toolu_9", Name = "read", Arguments = new() { ["path"] = "x" } }],
					Timestamp = 2,
				},
				new ToolResultMessage
				{
					ToolCallId = "toolu_9",
					ToolName = "read",
					Content = [new TextContent { Text = "contents" }],
					Timestamp = 3,
				},
			],
		};

		var stream = provider.Stream(Sonnet(), context, new StreamOptions { ApiKey = "test-key", MaxTokens = 512 });
		await foreach (var _ in stream)
		{
		}

		var request = handler.LastRequest!;
		Assert.Equal("https://api.anthropic.com/v1/messages", request.RequestUri!.ToString());
		Assert.Equal("POST", request.Method.Method);
		Assert.Equal("test-key", request.Headers.GetValues("x-api-key").Single());
		Assert.Equal("2023-06-01", request.Headers.GetValues("anthropic-version").Single());

		using var document = JsonDocument.Parse(handler.LastBody!);
		var root = document.RootElement;
		Assert.Equal("claude-sonnet-4-5", root.GetProperty("model").GetString());
		Assert.True(root.GetProperty("stream").GetBoolean());

		// Thinking is on by default for a reasoning model, so max_tokens is lifted past the budget.
		var thinking = root.GetProperty("thinking");
		Assert.Equal("enabled", thinking.GetProperty("type").GetString());
		var budget = thinking.GetProperty("budget_tokens").GetInt32();
		Assert.True(budget > 0);
		Assert.True(root.GetProperty("max_tokens").GetInt32() > budget);

		var system = root.GetProperty("system");
		Assert.Equal("be terse", system[0].GetProperty("text").GetString());
		Assert.Equal("ephemeral", system[0].GetProperty("cache_control").GetProperty("type").GetString());

		var messages = root.GetProperty("messages");
		Assert.Equal(3, messages.GetArrayLength());
		Assert.Equal("user", messages[0].GetProperty("role").GetString());
		Assert.Equal("hi", messages[0].GetProperty("content").GetString());
		Assert.Equal("assistant", messages[1].GetProperty("role").GetString());
		Assert.Equal("tool_use", messages[1].GetProperty("content")[0].GetProperty("type").GetString());
		Assert.Equal("user", messages[2].GetProperty("role").GetString());
		Assert.Equal("tool_result", messages[2].GetProperty("content")[0].GetProperty("type").GetString());
		Assert.Equal("toolu_9", messages[2].GetProperty("content")[0].GetProperty("tool_use_id").GetString());
	}

	private static Model AdaptiveClaude() => Sonnet() with
	{
		Id = "claude-opus-5-5",
		MaxTokens = 128_000,
		Compat = new JsonObject
		{
			["forceAdaptiveThinking"] = true,
			["supportsMidConvoEffort"] = true,
			["supportsTemperature"] = false,
			["supportsStrictTools"] = true,
			["allowedFallbackModels"] = new JsonArray(new JsonObject
			{
				["provider"] = "anthropic",
				["model"] = "claude-opus-5",
			}),
		},
		ThinkingLevelMap = new Dictionary<string, string?>
		{
			["off"] = null,
			["minimal"] = null,
			["low"] = "low",
			["medium"] = "medium",
			["high"] = "high",
			["xhigh"] = "xhigh",
			["max"] = "max",
		},
	};

	[Fact]
	public async Task AdaptiveThinkingUsesEffortAndFallbacks()
	{
		var handler = new FakeHttpHandler { Responder = _ => Sse(StreamBody) };
		var provider = new AnthropicProvider(new HttpClient(handler));

		var stream = provider.Stream(
			AdaptiveClaude(),
			new Context { Messages = [new UserMessage { Content = MessageContent.FromText("hi"), Timestamp = 1 }] },
			new StreamOptions { ApiKey = "test-key", Temperature = 0.4, ThinkingLevel = ThinkingLevels.High, MaxTokens = 512 });
		await foreach (var _ in stream)
		{
		}

		using var document = JsonDocument.Parse(handler.LastBody!);
		var root = document.RootElement;
		var thinking = root.GetProperty("thinking");
		Assert.Equal("adaptive", thinking.GetProperty("type").GetString());
		Assert.Equal("summarized", thinking.GetProperty("display").GetString());
		Assert.Equal("drop_block", thinking.GetProperty("block_binding").GetProperty("prefix_mismatch_behavior").GetString());
		Assert.Equal("high", root.GetProperty("output_config").GetProperty("effort").GetString());
		Assert.False(root.TryGetProperty("temperature", out _));
		Assert.Equal(512, root.GetProperty("max_tokens").GetInt32());
		Assert.Equal("claude-opus-5", root.GetProperty("fallbacks")[0].GetProperty("model").GetString());
		var beta = handler.LastRequest!.Headers.GetValues("anthropic-beta").Single();
		Assert.Contains("mid-conversation-output-config-2026-07-01", beta);
		Assert.Contains("thinking-binding-controls-2026-08-01", beta);
		Assert.Contains("server-side-fallback-2026-07-01", beta);
	}

	[Fact]
	public async Task ErrorToolResultImagesMoveAfterAllToolResults()
	{
		var handler = new FakeHttpHandler { Responder = _ => Sse(StreamBody) };
		var provider = new AnthropicProvider(new HttpClient(handler));

		var context = new Context
		{
			Messages =
			[
				new UserMessage { Content = MessageContent.FromText("read it"), Timestamp = 1 },
				new AssistantMessage
				{
					Content = [new ToolCall { Id = "toolu_1", Name = "read", Arguments = new JsonObject { ["path"] = "x" } }],
					Timestamp = 2,
				},
				new ToolResultMessage
				{
					ToolCallId = "toolu_1",
					ToolName = "read",
					IsError = true,
					Content =
					[
						new TextContent { Text = "failed" },
						new ImageContent { Data = "aGVsbG8=", MimeType = "image/png" },
					],
					Timestamp = 3,
				},
			],
		};

		var stream = provider.Stream(Sonnet() with { Input = ["text", "image"] }, context, new StreamOptions { ApiKey = "test-key", ThinkingLevel = ThinkingLevels.Off });
		await foreach (var _ in stream)
		{
		}

		using var document = JsonDocument.Parse(handler.LastBody!);
		var messages = document.RootElement.GetProperty("messages");
		var user = messages[messages.GetArrayLength() - 1];
		var content = user.GetProperty("content");
		var result = content[0];
		Assert.Equal("tool_result", result.GetProperty("type").GetString());
		Assert.DoesNotContain(result.GetProperty("content").EnumerateArray(), block =>
			block.GetProperty("type").GetString() == "image");
		Assert.Equal("(image attached after the tool results)", result.GetProperty("content")[1].GetProperty("text").GetString());
		Assert.Equal("text", content[1].GetProperty("type").GetString());
		Assert.Equal("image", content[2].GetProperty("type").GetString());
	}

	[Fact]
	public async Task StrictToolLimitIsRememberedPerSession()
	{
		var reject = true;
		var handler = new FakeHttpHandler
		{
			Responder = _ =>
			{
				if (reject)
				{
					reject = false;
					return new HttpResponseMessage(HttpStatusCode.BadRequest)
					{
						Content = new StringContent(
							"{\"error\":{\"message\":\"The compiled grammar is too large\"}}",
							Encoding.UTF8,
							"application/json"),
					};
				}

				return Sse(StreamBody);
			},
		};
		var provider = new AnthropicProvider(new HttpClient(handler));
		var model = Sonnet() with { Compat = new JsonObject { ["supportsStrictTools"] = true } };
		var tool = new Tool
		{
			Name = "read",
			Description = "read a file",
			Parameters = new JsonObject
			{
				["type"] = "object",
				["properties"] = new JsonObject { ["path"] = new JsonObject { ["type"] = "string" } },
			},
			ConstrainedSampling = ConstrainedSampling.JsonSchema(),
		};
		var sessionId = "strict-" + Guid.NewGuid().ToString("N");
		var context = new Context
		{
			Messages =
			[
				new SystemMessage { Content = MessageContent.FromText("system"), ToolsAdded = [tool], Timestamp = 1 },
				new UserMessage { Content = MessageContent.FromText("hi"), Timestamp = 2 },
			],
		};

		var first = provider.Stream(model, context, new StreamOptions { ApiKey = "test-key", SessionId = sessionId, ThinkingLevel = ThinkingLevels.Off });
		await foreach (var _ in first)
		{
		}

		Assert.Equal(2, handler.Bodies.Count);
		Assert.Contains("\"strict\":true", handler.Bodies[0]);
		Assert.DoesNotContain("\"strict\":true", handler.Bodies[1]);

		handler.Bodies.Clear();
		var second = provider.Stream(model, context, new StreamOptions { ApiKey = "test-key", SessionId = sessionId, ThinkingLevel = ThinkingLevels.Off });
		await foreach (var _ in second)
		{
		}

		Assert.Single(handler.Bodies);
		Assert.DoesNotContain("\"strict\":true", handler.Bodies[0]);
	}

	[Fact]
	public void ThreadStateContinuesWhenOnlyNewMessagesWereAdded()
	{
		var sessionId = "probe-" + Guid.NewGuid().ToString("N");
		var first = new JsonObject
		{
			["model"] = "claude-sonnet-4-5",
			["system"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "be terse" }),
			["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "hi" }),
		};

		AnthropicThreads.Record(sessionId, first, new JsonArray(new JsonObject
		{
			["role"] = "assistant",
			["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "Hello world" }),
		}), "msg_1");

		var second = new JsonObject
		{
			["model"] = "claude-sonnet-4-5",
			["system"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "be terse" }),
			["messages"] = new JsonArray(
				new JsonObject { ["role"] = "user", ["content"] = "hi" },
				new JsonObject
				{
					["role"] = "assistant",
					["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "Hello world" }),
				},
				new JsonObject { ["role"] = "user", ["content"] = "again" }),
		};

		var kind = AnthropicThreads.Decide(sessionId, second, out var prefixCount, out var replyId);
		Assert.Equal(AnthropicThreads.RequestKind.Continue, kind);
		Assert.Equal(2, prefixCount);
		Assert.Equal("msg_1", replyId);

		var bytes = AnthropicThreads.WriteBody(second, kind, prefixCount, replyId);
		var prepared = System.Text.Json.Nodes.JsonNode.Parse(bytes)!.AsObject();
		Assert.Equal("continue", prepared["thread"]!["type"]!.GetValue<string>());
		Assert.Equal("again", prepared["messages"]![0]!["content"]!.GetValue<string>());
	}

	private const string TextOnlyStreamBody = """
		event: message_start
		data: {"type":"message_start","message":{"id":"msg_1","model":"claude-sonnet-4-5","usage":{"input_tokens":10}}}

		event: content_block_start
		data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}

		event: content_block_delta
		data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Hello world"}}

		event: content_block_stop
		data: {"type":"content_block_stop","index":0}

		event: message_delta
		data: {"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":5}}

		event: message_stop
		data: {"type":"message_stop"}

		""";

	[Fact]
	public async Task MessageThreadsSendOnlyNewMessagesOnTheSecondRequest()
	{
		var handler = new FakeHttpHandler { Responder = _ => Sse(TextOnlyStreamBody) };
		var provider = new AnthropicProvider(new HttpClient(handler));
		var sessionId = "thread-" + Guid.NewGuid().ToString("N");
		var first = new Context
		{
			SystemPrompt = "be terse",
			Messages = [new UserMessage { Content = MessageContent.FromText("hi"), Timestamp = 1 }],
		};

		var firstStream = provider.Stream(Sonnet(), first, new StreamOptions { ApiKey = "test-key", SessionId = sessionId, ThinkingLevel = ThinkingLevels.Off });
		await foreach (var _ in firstStream)
		{
		}

		using (var document = JsonDocument.Parse(handler.Bodies[0]))
		{
			Assert.Equal("create", document.RootElement.GetProperty("thread").GetProperty("type").GetString());
		}

		// 第二次请求带上了上一次的助手回复，thread 只发新增的那条用户消息。
		var second = new Context
		{
			SystemPrompt = "be terse",
			Messages =
			[
				new UserMessage { Content = MessageContent.FromText("hi"), Timestamp = 1 },
				new AssistantMessage
				{
					Api = AnthropicProvider.ApiId,
					Provider = "anthropic",
					Model = "claude-sonnet-4-5",
					Content = [new TextContent { Text = "Hello world" }],
					StopReason = StopReason.Stop,
					Timestamp = 2,
				},
				new UserMessage { Content = MessageContent.FromText("again"), Timestamp = 3 },
			],
		};

		var secondStream = provider.Stream(Sonnet(), second, new StreamOptions { ApiKey = "test-key", SessionId = sessionId, ThinkingLevel = ThinkingLevels.Off });
		await foreach (var _ in secondStream)
		{
		}

		using var secondBody = JsonDocument.Parse(handler.Bodies[1]);
		var root = secondBody.RootElement;
		Assert.Equal("continue", root.GetProperty("thread").GetProperty("type").GetString());
		Assert.Equal("msg_1", root.GetProperty("thread").GetProperty("previous_message_id").GetString());
		Assert.Single(root.GetProperty("messages").EnumerateArray());
		Assert.Equal("again", root.GetProperty("messages")[0].GetProperty("content").GetString());
		Assert.False(root.TryGetProperty("system", out _));
		Assert.False(root.TryGetProperty("tools", out _));
	}

	[Fact]
	public async Task ThreadRejectionFallsBackToAFullRequest()
	{
		var rejections = 0;
		var handler = new FakeHttpHandler
		{
			Responder = request =>
			{
				if (rejections == 0 && request.Content is not null &&
					request.Content.ReadAsStringAsync().GetAwaiter().GetResult().Contains("\"thread\"", StringComparison.Ordinal))
				{
					rejections++;
					return new HttpResponseMessage(HttpStatusCode.BadRequest)
					{
						Content = new StringContent(
							"""{"error":{"details":{"error_code":"thread_unsupported_request"}}}""",
							Encoding.UTF8,
							"application/json"),
					};
				}

				return Sse(StreamBody);
			},
		};
		var provider = new AnthropicProvider(new HttpClient(handler));
		var sessionId = "thread-off-" + Guid.NewGuid().ToString("N");
		var context = new Context { Messages = [new UserMessage { Content = MessageContent.FromText("hi"), Timestamp = 1 }] };

		var stream = provider.Stream(Sonnet(), context, new StreamOptions { ApiKey = "test-key", SessionId = sessionId, ThinkingLevel = ThinkingLevels.Off });
		await foreach (var _ in stream)
		{
		}

		Assert.Equal(2, handler.Bodies.Count);
		using (var rejected = JsonDocument.Parse(handler.Bodies[0]))
			Assert.True(rejected.RootElement.TryGetProperty("thread", out _));
		using (var resent = JsonDocument.Parse(handler.Bodies[1]))
			Assert.False(resent.RootElement.TryGetProperty("thread", out _));

		// 之后同一会话不再带 thread。
		handler.Bodies.Clear();
		var next = provider.Stream(Sonnet(), context, new StreamOptions { ApiKey = "test-key", SessionId = sessionId, ThinkingLevel = ThinkingLevels.Off });
		await foreach (var _ in next)
		{
		}

		using var plain = JsonDocument.Parse(handler.Bodies[0]);
		Assert.False(plain.RootElement.TryGetProperty("thread", out _));
	}

	[Fact]
	public async Task HttpErrorBecomesErrorEvent()
	{
		var handler = new FakeHttpHandler
		{
			Responder = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
			{
				Content = new StringContent("{\"error\":{\"message\":\"bad key\"}}", Encoding.UTF8, "application/json"),
			},
		};

		var provider = new AnthropicProvider(new HttpClient(handler));
		var stream = provider.Stream(
			Sonnet(),
			new Context { Messages = [new UserMessage { Content = MessageContent.FromText("hi") }] },
			new StreamOptions { ApiKey = "nope" });

		var events = new List<AssistantMessageEvent>();
		await foreach (var @event in stream)
		{
			events.Add(@event);
		}

		var error = Assert.IsType<ErrorEvent>(Assert.Single(events));
		Assert.Contains("401", error.Error.ErrorMessage);
		Assert.Equal(StopReason.Error, error.Reason);
	}

	[Fact]
	public async Task MissingApiKeyFailsFast()
	{
		var handler = new FakeHttpHandler();
		var provider = new AnthropicProvider(new HttpClient(handler));

		var original = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
		Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
		try
		{
			var stream = provider.Stream(
				Sonnet(),
				new Context { Messages = [new UserMessage { Content = MessageContent.FromText("hi") }] },
				new StreamOptions());

			var events = new List<AssistantMessageEvent>();
			await foreach (var @event in stream)
			{
				events.Add(@event);
			}

			Assert.Null(handler.LastRequest);
			var error = Assert.IsType<ErrorEvent>(Assert.Single(events));
			Assert.Contains("API key", error.Error.ErrorMessage);
		}
		finally
		{
			Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", original);
		}
	}

	/// 只实现 Memory 版 ReadAsync，与真实网络流一样没有可用的同步读。
	internal sealed class PartialThenStalledStream : Stream
	{
		private readonly byte[] _prefix;
		private int _delivered;

		public Action? OnStalled { get; set; }

		public PartialThenStalledStream(byte[] prefix) => _prefix = prefix;

		public override bool CanRead => true;
		public override bool CanSeek => false;
		public override bool CanWrite => false;
		public override long Length => throw new NotSupportedException();
		public override long Position { get => 0; set => throw new NotSupportedException(); }
		public override void Flush() { }
		public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
		public override long Seek(long offset, System.IO.SeekOrigin origin) => throw new NotSupportedException();
		public override void SetLength(long value) => throw new NotSupportedException();
		public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

		public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
		{
			if (_delivered < _prefix.Length)
			{
				var count = Math.Min(buffer.Length, _prefix.Length - _delivered);
				_prefix.AsSpan(_delivered, count).CopyTo(buffer.Span);
				_delivered += count;
				return count;
			}

			// 之后不再有数据，也不会关闭连接：只能靠取消结束。
			OnStalled?.Invoke();
			await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
			return 0;
		}
	}

	/// 流被中断时的两种语义必须分开：用户中止是 aborted（不重试），
	/// 供应商挂住连接是空闲超时的 error（要重试）。两者曾经共用一条取消路径，
	/// 判错方向就会「中止被当成超时」或「停滞被当成中止」。
	public class AnthropicProviderAbortionTests
	{
		private const string FirstEvent = """
			event: message_start
			data: {"type":"message_start","message":{"id":"msg_1","model":"claude-sonnet-4-5","usage":{"input_tokens":10}}}

			event: content_block_delta
			data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"half"}}

			""";

		private static Model Sonnet() => new()
		{
			Id = "claude-sonnet-4-5",
			Provider = "anthropic",
			Api = AnthropicProvider.ApiId,
			BaseUrl = "https://api.anthropic.com",
			ContextWindow = 200_000,
			MaxTokens = 8192,
			Cost = new ModelCost { Input = 3, Output = 15, CacheRead = 0.3, CacheWrite = 3.75 },
		};

		private static AnthropicProvider StalledProvider(Action? onStalled = null) =>
			new(new HttpClient(new FakeHttpHandler
			{
				Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
				{
					Content = new StreamContent(new PartialThenStalledStream(Encoding.UTF8.GetBytes(FirstEvent))
					{
						OnStalled = onStalled,
					}),
				},
			}));

		[Fact]
		public async Task UserAbortMidStreamIsReportedAsAborted()
		{
			using var source = new CancellationTokenSource();

			// 中止的时刻钉在「首批字节已交给解析器」那一刻，不用 sleep：
			// 并行跑测试时按时间取消会飘到请求前或请求后，断言不稳定。
			var provider = StalledProvider(source.Cancel);
			var stream = provider.Stream(
				Sonnet(),
				new Context { Messages = [new UserMessage { Content = MessageContent.FromText("hi"), Timestamp = 1 }] },
				new StreamOptions { ApiKey = "test-key", CancellationToken = source.Token });

			await foreach (var _ in stream)
			{
			}

			var message = await stream.Result;
			Assert.Equal(StopReason.Aborted, message.StopReason);
			Assert.Contains("abort", message.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
			Assert.False(RetryClassifier.IsRetryable(message));
		}

		[Fact]
		public async Task StalledStreamHitsTheIdleTimeoutAndStaysRetryable()
		{
			using var source = new CancellationTokenSource();
			var provider = StalledProvider();
			var stream = provider.Stream(
				Sonnet(),
				new Context { Messages = [new UserMessage { Content = MessageContent.FromText("hi"), Timestamp = 1 }] },
				new StreamOptions { ApiKey = "test-key", CancellationToken = source.Token, IdleTimeoutMs = 500 });

			await foreach (var _ in stream)
			{
			}

			var message = await stream.Result;
			Assert.Equal(StopReason.Error, message.StopReason);
			Assert.Contains("idle timeout", message.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
			Assert.True(RetryClassifier.IsRetryable(message));
		}
	}
	[Fact]
	public async Task CloudflareGatewayUsesGatewayAuthorizationHeader()
	{
		var handler = new FakeHttpHandler { Responder = _ => Sse(StreamBody) };
		var provider = new AnthropicProvider(new HttpClient(handler));
		var model = Sonnet() with
		{
			Provider = "cloudflare-ai-gateway",
			BaseUrl = "https://gateway.ai.cloudflare.com/v1/account/gateway/anthropic",
		};
		var stream = provider.Stream(model,
			new Context { Messages = [new UserMessage { Content = MessageContent.FromText("hi"), Timestamp = 1 }] },
			new StreamOptions { ApiKey = "gateway-key", MaxTokens = 128 });
		await foreach (var _ in stream)
		{
		}

		var request = handler.LastRequest!;
		Assert.Equal("Bearer gateway-key", request.Headers.GetValues("cf-aig-authorization").Single());
		Assert.False(request.Headers.Contains("x-api-key"));
		Assert.Null(request.Headers.Authorization);
	}

	/// 与目录同口径的严格工具模型：compat.supportsStrictTools 为 true 才会发 strict。
	private static Model StrictSonnet() => Sonnet() with
	{
		Compat = new System.Text.Json.Nodes.JsonObject { ["supportsStrictTools"] = true },
	};

	private static Tool StrictReadTool() => new()
	{
		Name = "read",
		Description = "read a file",
		Parameters = new System.Text.Json.Nodes.JsonObject
		{
			["type"] = "object",
			["properties"] = new System.Text.Json.Nodes.JsonObject
			{
				["path"] = new System.Text.Json.Nodes.JsonObject { ["type"] = "string" },
				["offset"] = new System.Text.Json.Nodes.JsonObject { ["type"] = "number" },
			},
			["required"] = new System.Text.Json.Nodes.JsonArray("path"),
		},
		ConstrainedSampling = new System.Text.Json.Nodes.JsonObject
		{
			["type"] = "json_schema",
			["strict"] = "prefer",
		},
	};

	[Fact]
	public async Task StrictToolsAreOnlySentToModelsThatDeclareThem()
	{
		var handler = new FakeHttpHandler { Responder = _ => Sse(StreamBody) };
		var provider = new AnthropicProvider(new HttpClient(handler));
		var context = new Context
		{
			Messages = [new UserMessage { Content = MessageContent.FromText("hi"), Timestamp = 1 }],
			Tools = [StrictReadTool()],
		};

		var strictStream = provider.Stream(StrictSonnet(), context, new StreamOptions { ApiKey = "test-key" });
		await foreach (var _ in strictStream)
		{
		}

		using (var body = JsonDocument.Parse(handler.LastBody!))
		{
			var tool = body.RootElement.GetProperty("tools")[0];
			Assert.True(tool.GetProperty("strict").GetBoolean());
			var schema = tool.GetProperty("input_schema");
			Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
			// 严格 schema 里可选属性补成 anyOf[..., null-flavoured] 并全部进 required。
			Assert.Equal(2, schema.GetProperty("required").GetArrayLength());
			Assert.True(schema.GetProperty("properties").GetProperty("offset").TryGetProperty("anyOf", out _));
		}

		// 没有声明 supportsStrictTools 的模型：不发 strict，也不把根 schema 换成严格版。
		var plainStream = provider.Stream(Sonnet(), context, new StreamOptions { ApiKey = "test-key" });
		await foreach (var _ in plainStream)
		{
		}

		using (var body = JsonDocument.Parse(handler.LastBody!))
		{
			var tool = body.RootElement.GetProperty("tools")[0];
			Assert.False(tool.TryGetProperty("strict", out _));
			var schema = tool.GetProperty("input_schema");
			Assert.False(schema.TryGetProperty("additionalProperties", out _));
			Assert.Equal(1, schema.GetProperty("required").GetArrayLength());
			Assert.False(schema.GetProperty("properties").GetProperty("offset").TryGetProperty("anyOf", out _));
		}
	}

	[Fact]
	public async Task NonStrictSchemasKeepRootAdditionalProperties()
	{
		var handler = new FakeHttpHandler { Responder = _ => Sse(StreamBody) };
		var provider = new AnthropicProvider(new HttpClient(handler));
		var tool = StrictReadTool() with
		{
			ConstrainedSampling = null,
			Parameters = new System.Text.Json.Nodes.JsonObject
			{
				["type"] = "object",
				["properties"] = new System.Text.Json.Nodes.JsonObject
				{
					["path"] = new System.Text.Json.Nodes.JsonObject { ["type"] = "string" },
				},
				["required"] = new System.Text.Json.Nodes.JsonArray("path"),
				["additionalProperties"] = false,
			},
		};

		var stream = provider.Stream(
			Sonnet(),
			new Context
			{
				Messages = [new UserMessage { Content = MessageContent.FromText("hi"), Timestamp = 1 }],
				Tools = [tool],
			},
			new StreamOptions { ApiKey = "test-key" });
		await foreach (var _ in stream)
		{
		}

		using var body = JsonDocument.Parse(handler.LastBody!);
		var schema = body.RootElement.GetProperty("tools")[0].GetProperty("input_schema");
		Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
	}

	[Fact]
	public async Task StrictToolLimitRejectionIsRetriedWithoutPreferredStrictTools()
	{
		var attempts = 0;
		var handler = new FakeHttpHandler
		{
			Responder = _ =>
			{
				attempts++;
				return attempts == 1
					? new HttpResponseMessage(HttpStatusCode.BadRequest)
					{
						Content = new StringContent(
							"{\"error\":{\"type\":\"invalid_request_error\",\"message\":\"Too many strict tools (20)\"}}",
							Encoding.UTF8,
							"application/json"),
					}
					: Sse(StreamBody);
			},
		};

		var provider = new AnthropicProvider(new HttpClient(handler));
		var stream = provider.Stream(
			StrictSonnet(),
			new Context
			{
				Messages = [new UserMessage { Content = MessageContent.FromText("hi"), Timestamp = 1 }],
				Tools = [StrictReadTool()],
			},
			new StreamOptions { ApiKey = "test-key" });

		await foreach (var _ in stream)
		{
		}

		Assert.Equal(2, attempts);
		using var first = JsonDocument.Parse(handler.Bodies[0]);
		using var second = JsonDocument.Parse(handler.Bodies[1]);
		Assert.True(first.RootElement.GetProperty("tools")[0].GetProperty("strict").GetBoolean());
		Assert.False(second.RootElement.GetProperty("tools")[0].TryGetProperty("strict", out _));
		Assert.False(second.RootElement.GetProperty("tools")[0]
			.GetProperty("input_schema").TryGetProperty("additionalProperties", out _));
	}
}

[Collection("process-environment")]
public sealed class AnthropicProviderProviderKeyTests : IDisposable
{
	private readonly string _agentDir = Path.Combine(Path.GetTempPath(), "piweft-anthropic-key-" + Guid.NewGuid().ToString("N")[..8]);
	private readonly string? _previousAgentDir = Environment.GetEnvironmentVariable("PI_CODING_AGENT_DIR");
	private readonly string? _previousOpenRouter = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
	private readonly string? _previousAnthropic = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");

	public AnthropicProviderProviderKeyTests()
	{
		Directory.CreateDirectory(_agentDir);
		Environment.SetEnvironmentVariable("PI_CODING_AGENT_DIR", _agentDir);
		Environment.SetEnvironmentVariable("OPENROUTER_API_KEY", "openrouter-key");
		Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
	}

	public void Dispose()
	{
		Environment.SetEnvironmentVariable("PI_CODING_AGENT_DIR", _previousAgentDir);
		Environment.SetEnvironmentVariable("OPENROUTER_API_KEY", _previousOpenRouter);
		Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", _previousAnthropic);
		Directory.Delete(_agentDir, recursive: true);
	}

	[Fact]
	public async Task AnthropicCompatibleProviderResolvesItsOwnStoredKey()
	{
		var handler = new FakeHttpHandler { Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
		{
			Content = new StringContent("""
				event: message_start
				data: {"type":"message_start","message":{"id":"msg_1","model":"anthropic/claude","usage":{"input_tokens":1,"output_tokens":0}}}
				event: message_stop
				data: {"type":"message_stop"}
				""", Encoding.UTF8, "text/event-stream"),
		} };
		var provider = new AnthropicProvider(new HttpClient(handler));
		var model = new Model
		{
			Id = "anthropic/claude",
			Provider = "openrouter",
			Api = AnthropicProvider.ApiId,
			BaseUrl = "https://openrouter.ai/api",
		};

		var stream = provider.Stream(
			model,
			new Context { Messages = [new UserMessage { Content = MessageContent.FromText("hi"), Timestamp = 1 }] },
			new StreamOptions());
		await foreach (var _ in stream)
		{
		}

		Assert.Equal("openrouter-key", handler.LastRequest!.Headers.GetValues("x-api-key").Single());
	}
}
