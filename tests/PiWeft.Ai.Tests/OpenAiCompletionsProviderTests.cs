using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiWeft.Ai;
using PiWeft.Ai.Providers.OpenAi;
using Xunit;

namespace PiWeft.Ai.Tests;

public class OpenAiCompletionsProviderTests
{
	private static Model Model(string provider = "deepseek", string baseUrl = "https://api.deepseek.com/v1") => new()
	{
		Id = "deepseek-chat",
		Provider = provider,
		Api = OpenAiCompletionsProvider.ApiId,
		BaseUrl = baseUrl,
		Reasoning = true,
		ContextWindow = 64_000,
		MaxTokens = 8192,
		Cost = new ModelCost { Input = 0.27, Output = 1.1, CacheRead = 0.07 },
	};

	private static HttpResponseMessage Sse(string body) => new(HttpStatusCode.OK)
	{
		Content = new StringContent(body, Encoding.UTF8, "text/event-stream"),
	};

	private const string StreamBody = """
		data: {"id":"chatcmpl-1","model":"deepseek-chat","choices":[{"index":0,"delta":{"role":"assistant","content":""},"finish_reason":null}]}

		data: {"id":"chatcmpl-1","model":"deepseek-chat","choices":[{"index":0,"delta":{"reasoning_content":"let me think"},"finish_reason":null}]}

		data: {"id":"chatcmpl-1","choices":[{"index":0,"delta":{"content":"Hello"}}]}

		data: {"id":"chatcmpl-1","choices":[{"index":0,"delta":{"content":" world"}}]}

		data: {"id":"chatcmpl-1","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call_1","type":"function","function":{"name":"read","arguments":"{\"path\":"}}]}}]}

		data: {"id":"chatcmpl-1","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"\"a.txt\"}"}}]}}]}

		data: {"id":"chatcmpl-1","choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}]}

		data: {"id":"chatcmpl-1","choices":[],"usage":{"prompt_tokens":10,"completion_tokens":7,"prompt_tokens_details":{"cached_tokens":2,"cache_write_tokens":1},"completion_tokens_details":{"reasoning_tokens":3}}}

		data: [DONE]

		""";

	[Fact]
	public async Task StreamsReasoningTextAndToolCall()
	{
		var handler = new FakeHttpHandler { Responder = _ => Sse(StreamBody) };
		var provider = new OpenAiCompletionsProvider(new HttpClient(handler));

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
		Assert.Contains("thinking_delta:let me think", events);
		Assert.Contains("thinking_end:let me think", events);
		Assert.Contains("text_end:Hello world", events);
		Assert.Contains("toolcall_end:read", events);
		Assert.Equal("done:ToolUse", events[^1]);

		var message = await stream.Result;
		Assert.Equal(3, message.Content.Count);
		Assert.Equal("let me think", Assert.IsType<ThinkingContent>(message.Content[0]).Thinking);
		Assert.Equal("Hello world", Assert.IsType<TextContent>(message.Content[1]).Text);
		var call = Assert.IsType<ToolCall>(message.Content[2]);
		Assert.Equal("call_1", call.Id);
		Assert.Equal("a.txt", call.Arguments["path"]!.GetValue<string>());

		// input excludes cached and written tokens; total counts every category.
		Assert.Equal(7, message.Usage.Input);
		Assert.Equal(7, message.Usage.Output);
		Assert.Equal(2, message.Usage.CacheRead);
		Assert.Equal(1, message.Usage.CacheWrite);
		Assert.Equal(3, message.Usage.Reasoning);
		Assert.Equal(17, message.Usage.TotalTokens);
		Assert.True(message.Usage.Cost.Total > 0);
		Assert.Equal("chatcmpl-1", message.ResponseId);
		Assert.Equal(StopReason.ToolUse, message.StopReason);
	}

	[Fact]
	public async Task SendsExpectedRequest()
	{
		var handler = new FakeHttpHandler { Responder = _ => Sse(StreamBody) };
		var provider = new OpenAiCompletionsProvider(new HttpClient(handler));

		var context = new Context
		{
			SystemPrompt = "be terse",
			Messages =
			[
				new UserMessage { Content = MessageContent.FromText("hi"), Timestamp = 1 },
				new AssistantMessage
				{
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

		var stream = provider.Stream(Model(), context, new StreamOptions { ApiKey = "test-key", MaxTokens = 256 });
		await foreach (var _ in stream)
		{
		}

		var request = handler.LastRequest!;
		Assert.Equal("https://api.deepseek.com/v1/chat/completions", request.RequestUri!.ToString());
		Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
		Assert.Equal("test-key", request.Headers.Authorization.Parameter);

		using var document = JsonDocument.Parse(handler.LastBody!);
		var root = document.RootElement;
		Assert.Equal("deepseek-chat", root.GetProperty("model").GetString());
		Assert.True(root.GetProperty("stream").GetBoolean());
		Assert.True(root.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());

		// Non-OpenAI hosts only accept max_tokens.
		Assert.Equal(256, root.GetProperty("max_tokens").GetInt32());
		Assert.False(root.TryGetProperty("max_completion_tokens", out _));

		var messages = root.GetProperty("messages");
		Assert.Equal(4, messages.GetArrayLength());
		Assert.Equal("system", messages[0].GetProperty("role").GetString());
		Assert.Equal("be terse", messages[0].GetProperty("content").GetString());
		Assert.Equal("user", messages[1].GetProperty("role").GetString());

		var assistant = messages[2];
		Assert.Equal("assistant", assistant.GetProperty("role").GetString());
		Assert.Equal("read", assistant.GetProperty("tool_calls")[0].GetProperty("function").GetProperty("name").GetString());

		var tool = messages[3];
		Assert.Equal("tool", tool.GetProperty("role").GetString());
		Assert.Equal("call_9", tool.GetProperty("tool_call_id").GetString());
		Assert.Equal("contents", tool.GetProperty("content").GetString());

		var tools = root.GetProperty("tools");
		Assert.Equal("function", tools[0].GetProperty("type").GetString());
		Assert.Equal("read", tools[0].GetProperty("function").GetProperty("name").GetString());
	}

	[Fact]
	public async Task UsesMaxCompletionTokensOnOpenAi()
	{
		var handler = new FakeHttpHandler { Responder = _ => Sse(StreamBody) };
		var provider = new OpenAiCompletionsProvider(new HttpClient(handler));

		var model = Model("openai", "https://api.openai.com/v1") with { Id = "gpt-5", Cost = new ModelCost() };
		var stream = provider.Stream(
			model,
			new Context { Messages = [new UserMessage { Content = MessageContent.FromText("hi") }] },
			new StreamOptions { ApiKey = "k", MaxTokens = 128 });

		await foreach (var _ in stream)
		{
		}

		using var document = JsonDocument.Parse(handler.LastBody!);
		Assert.Equal(128, document.RootElement.GetProperty("max_completion_tokens").GetInt32());
	}

	[Fact]
	public async Task ContentFilterBecomesErrorEvent()
	{
		const string body = """
			data: {"id":"c1","choices":[{"index":0,"delta":{"content":"x"},"finish_reason":"content_filter"}]}

			data: [DONE]

			""";

		var handler = new FakeHttpHandler { Responder = _ => Sse(body) };
		var provider = new OpenAiCompletionsProvider(new HttpClient(handler));

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
		Assert.Contains("content_filter", error.Error.ErrorMessage);
	}

	[Fact]
	public async Task HttpErrorBecomesErrorEvent()
	{
		var handler = new FakeHttpHandler
		{
			Responder = _ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
			{
				Content = new StringContent("{\"error\":{\"message\":\"slow down\"}}", Encoding.UTF8, "application/json"),
			},
		};

		var provider = new OpenAiCompletionsProvider(new HttpClient(handler));
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
		Assert.Contains("429", error.Error.ErrorMessage);
	}

	[Fact]
	public void ReasoningEffortFollowsThinkingLevelMap()
	{
		var mapped = Model() with
		{
			ThinkingLevelMap = new Dictionary<string, string?> { ["low"] = "minimal", ["high"] = "high" },
		};

		Assert.Equal("minimal", OpenAiCompletionsProvider.ResolveReasoningEffort(mapped, new StreamOptions { ThinkingLevel = ThinkingLevels.Low }));
		Assert.Equal("high", OpenAiCompletionsProvider.ResolveReasoningEffort(mapped, new StreamOptions { ThinkingLevel = "max" }));
		Assert.Null(OpenAiCompletionsProvider.ResolveReasoningEffort(mapped, new StreamOptions { ThinkingLevel = ThinkingLevels.Off }));
		Assert.Null(OpenAiCompletionsProvider.ResolveReasoningEffort(Model() with { Reasoning = false }, new StreamOptions()));
	}

	private static Model DeepSeekThinkingModel() => Model() with
	{
		Compat = new JsonObject
		{
			["thinkingFormat"] = "deepseek",
			["requiresReasoningContentOnAssistantMessages"] = true,
		},
		ThinkingLevelMap = new Dictionary<string, string?> { ["low"] = "low", ["high"] = "high", ["max"] = "max" },
	};

	[Fact]
	public void ThinkingSwitchFollowsTheDeclaredFormat()
	{
		var deepSeek = DeepSeekThinkingModel();

		// DeepSeek 默认开启思考，关闭必须显式下发 thinking.type=disabled。
		Assert.Equal("disabled", OpenAiCompletionsProvider.ResolveThinkingMode(deepSeek, new StreamOptions { ThinkingLevel = ThinkingLevels.Off }));
		Assert.Equal("enabled", OpenAiCompletionsProvider.ResolveThinkingMode(deepSeek, new StreamOptions { ThinkingLevel = ThinkingLevels.Low }));
		Assert.Equal("enabled", OpenAiCompletionsProvider.ResolveThinkingMode(deepSeek, new StreamOptions { ThinkingLevel = "max" }));

		// 没有声明 thinking 开关的端点只看 reasoning_effort。
		Assert.Null(OpenAiCompletionsProvider.ResolveThinkingMode(Model(), new StreamOptions { ThinkingLevel = "max" }));
		Assert.Null(OpenAiCompletionsProvider.ResolveThinkingMode(deepSeek with { Reasoning = false }, new StreamOptions()));

		// 目录把 off 映射成 null 时该档位本身不可用（与 pi 的 getSupportedThinkingLevels 一致）：
		// 请求 off 会被收敛到下一个可用档位，因此仍然下发 enabled。
		var omittedOff = deepSeek with
		{
			ThinkingLevelMap = new Dictionary<string, string?> { [ThinkingLevels.Off] = null, ["high"] = "high" },
		};
		Assert.Equal("enabled", OpenAiCompletionsProvider.ResolveThinkingMode(omittedOff, new StreamOptions { ThinkingLevel = ThinkingLevels.Off }));
	}

	[Fact]
	public async Task DeepSeekRequestTurnsThinkingOffAndDropsTheEffort()
	{
		var handler = new FakeHttpHandler { Responder = _ => Sse(StreamBody) };
		var provider = new OpenAiCompletionsProvider(new HttpClient(handler));

		var stream = provider.Stream(
			DeepSeekThinkingModel(),
			new Context { Messages = [new UserMessage { Content = MessageContent.FromText("hi") }] },
			new StreamOptions { ApiKey = "k", ThinkingLevel = ThinkingLevels.Off });

		await foreach (var _ in stream)
		{
		}

		using var document = JsonDocument.Parse(handler.LastBody!);
		Assert.Equal("disabled", document.RootElement.GetProperty("thinking").GetProperty("type").GetString());
		Assert.False(document.RootElement.TryGetProperty("reasoning_effort", out _));
	}

	[Fact]
	public async Task DeepSeekRequestReplaysReasoningContentOnAssistantMessages()
	{
		var handler = new FakeHttpHandler { Responder = _ => Sse(StreamBody) };
		var provider = new OpenAiCompletionsProvider(new HttpClient(handler));

		var context = new Context
		{
			Messages =
			[
				new UserMessage { Content = MessageContent.FromText("hi"), Timestamp = 1 },
				new AssistantMessage
				{
					Content =
					[
						new ThinkingContent { Thinking = "plan", ThinkingSignature = "reasoning_content" },
						new TextContent { Text = "done" },
					],
					Timestamp = 2,
				},
			],
		};

		var stream = provider.Stream(DeepSeekThinkingModel(), context, new StreamOptions { ApiKey = "k", ThinkingLevel = "high" });
		await foreach (var _ in stream)
		{
		}

		using var document = JsonDocument.Parse(handler.LastBody!);
		var assistant = document.RootElement.GetProperty("messages")[1];
		Assert.Equal("plan", assistant.GetProperty("reasoning_content").GetString());
	}

	[Fact]
	public async Task DeepSeekRequestSendsEmptyReasoningContentWhenTheTurnHadNone()
	{
		var handler = new FakeHttpHandler { Responder = _ => Sse(StreamBody) };
		var provider = new OpenAiCompletionsProvider(new HttpClient(handler));

		var context = new Context
		{
			Messages =
			[
				new UserMessage { Content = MessageContent.FromText("hi"), Timestamp = 1 },
				new AssistantMessage { Content = [new TextContent { Text = "plain" }], Timestamp = 2 },
			],
		};

		var stream = provider.Stream(DeepSeekThinkingModel(), context, new StreamOptions { ApiKey = "k", ThinkingLevel = ThinkingLevels.Off });
		await foreach (var _ in stream)
		{
		}

		// 带工具的 DeepSeek 请求要求每条助手消息都有 reasoning_content，漏传会直接 400。
		using var document = JsonDocument.Parse(handler.LastBody!);
		var assistant = document.RootElement.GetProperty("messages")[1];
		Assert.Equal(string.Empty, assistant.GetProperty("reasoning_content").GetString());
	}

	[Fact]
	public async Task DeepSeekRequestTurnsThinkingOnWithTheMappedEffort()
	{
		var handler = new FakeHttpHandler { Responder = _ => Sse(StreamBody) };
		var provider = new OpenAiCompletionsProvider(new HttpClient(handler));

		var stream = provider.Stream(
			DeepSeekThinkingModel(),
			new Context { Messages = [new UserMessage { Content = MessageContent.FromText("hi") }] },
			new StreamOptions { ApiKey = "k", ThinkingLevel = "max" });

		await foreach (var _ in stream)
		{
		}

		using var document = JsonDocument.Parse(handler.LastBody!);
		Assert.Equal("enabled", document.RootElement.GetProperty("thinking").GetProperty("type").GetString());
		Assert.Equal("max", document.RootElement.GetProperty("reasoning_effort").GetString());
	}
}

/// <summary>
/// 超时只覆盖「等响应头」。pi 1.2.0 修的就是这个：固定时限把长时间思考的流中途切断。
/// 这里让响应头立刻返回、正文慢慢产出，并要求结果完整。
/// </summary>
public class HeaderOnlyTimeoutTests
{
	private sealed class SlowBodyHandler(string body, TimeSpan bodyDelay) : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(
			HttpRequestMessage request,
			CancellationToken cancellationToken)
		{
			// 头部必须真的「立刻」返回。这里不能 `await Task.Yield()`——线程池繁忙时它能把
			// 头部推迟到超过配置的 50 ms 请求超时，超时就真的在「等头部」阶段触发，
			// 测试于是在验证一个它没打算验证的场景，随机变红。
			cancellationToken.ThrowIfCancellationRequested();

			var content = new StreamContent(new SlowStream(body, bodyDelay));
			content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");

			return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
		}
	}

	private sealed class SlowStream(string body, TimeSpan delay) : Stream
	{
		private readonly MemoryStream _inner = new(System.Text.Encoding.UTF8.GetBytes(body));

		public override bool CanRead => true;

		public override bool CanSeek => false;

		public override bool CanWrite => false;

		public override long Length => _inner.Length;

		public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }

		public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

		public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
		{
			// 每读一次都慢一拍：总耗时远超配置的请求超时。
			await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
			return await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
		}

		public override void Flush()
		{
		}

		public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

		public override void SetLength(long value) => throw new NotSupportedException();

		public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
	}

	[Fact]
	public async Task SlowBodyIsNotCutOffByTheRequestTimeout()
	{
		var body = """
			data: {"id":"1","choices":[{"index":0,"delta":{"role":"assistant","content":""},"finish_reason":null}]}

			data: {"id":"1","choices":[{"index":0,"delta":{"content":"慢慢来"}}]}

			data: {"id":"1","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}

			data: [DONE]

			""";

		var provider = new OpenAiCompletionsProvider(
			new HttpClient(new SlowBodyHandler(body, TimeSpan.FromMilliseconds(300))));

		var model = new Model
		{
			Id = "deepseek-chat",
			Provider = "deepseek",
			Api = OpenAiCompletionsProvider.ApiId,
			BaseUrl = "https://api.deepseek.com/v1",
			MaxTokens = 1024,
		};

		var stream = provider.Stream(
			model,
			new Context { Messages = [new UserMessage { Content = MessageContent.FromText("hi"), Timestamp = 1 }] },
			// 超时预算必须比「正文总耗时」小得多，同时又要远大于「等响应头」的真实耗时。
			// 原来的 50 ms 太贴边：并行跑整套测试时线程池一忙，连同步返回的响应头都能超过 50 ms，
			// 超时真的在等头部阶段触发，测试就变成在验证它不想验证的场景（实测 8 并发时 4 次失败）。
			// 正文 4 次读 × 300 ms ≈ 1.2 s，是 400 ms 预算的 3 倍，语义仍然成立。
			new StreamOptions { ApiKey = "k", TimeoutMs = 400 });

		var text = new StringBuilder();
		await foreach (var @event in stream)
		{
			if (@event is TextDeltaEvent delta)
			{
				text.Append(delta.Delta);
			}
		}

		Assert.Equal("慢慢来", text.ToString());
	}
}
