using System.Net;
using System.Text;
using System.Text.Json;
using PiWeft.Ai;
using PiWeft.Ai.Providers.Anthropic;
using Xunit;

namespace PiWeft.Ai.Tests;

internal sealed class FakeHttpHandler : HttpMessageHandler
{
	public Func<HttpRequestMessage, HttpResponseMessage>? Responder { get; set; }

	public HttpRequestMessage? LastRequest { get; private set; }

	public string? LastBody { get; private set; }

	protected override async Task<HttpResponseMessage> SendAsync(
		HttpRequestMessage request,
		CancellationToken cancellationToken)
	{
		LastRequest = request;
		LastBody = request.Content is null
			? null
			: await request.Content.ReadAsStringAsync(cancellationToken);
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

	/// <summary>先交出前若干字节，之后只等取消的响应体流：模拟「流开到一半就停住」。</summary>
	/// <remarks>只实现 Memory 版 ReadAsync —— 与真实网络流一样没有可用的同步读。</remarks>
	internal sealed class PartialThenStalledStream : Stream
	{
		private readonly byte[] _prefix;
		private int _delivered;

		/// <summary>数据交完、即将停住时的回调：测试靠它把「中止」钉在确定的时刻，而不是靠 sleep。</summary>
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

	/// <summary>
	/// 流被中断时的两种语义必须分开：用户中止是 aborted（不重试），
	/// 供应商挂住连接是空闲超时的 error（要重试）。两者曾经共用一条取消路径，
	/// 判错方向就会「中止被当成超时」或「停滞被当成中止」。
	/// </summary>
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

		/// <summary>每个用例自己的 handler：不共享静态状态，测试并行时不会互相串。</summary>
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

			// 中止的时刻钉在「首批字节已交给解析器」那一刻，而不是 sleep：
			// 并行跑测试时按时间取消会飘到请求前或请求后，断言就不稳定了。
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
}
