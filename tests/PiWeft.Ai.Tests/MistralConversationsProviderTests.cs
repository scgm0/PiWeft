using System.Net;
using System.Text;
using System.Text.Json;
using PiWeft.Ai;
using PiWeft.Ai.Providers.Mistral;
using Xunit;

namespace PiWeft.Ai.Tests;

/// <summary>Mistral chat completions: OpenAI-shaped requests with its own content-part and stop-reason vocabulary.</summary>
public class MistralConversationsProviderTests
{
	private static Model Model() => new()
	{
		Id = "mistral-large-latest",
		Provider = "mistral",
		Api = MistralConversationsProvider.ApiId,
		BaseUrl = "https://api.mistral.ai/v1",
		Reasoning = true,
		ContextWindow = 131_072,
		MaxTokens = 8192,
		Cost = new ModelCost { Input = 2, Output = 6 },
	};

	private static HttpResponseMessage Sse(string body)
	{
		var terminated = body.EndsWith("\n\n", StringComparison.Ordinal) ? body : body.TrimEnd('\n') + "\n\n";
		return new HttpResponseMessage(HttpStatusCode.OK)
		{
			Content = new StringContent(terminated, Encoding.UTF8, "text/event-stream"),
		};
	}

	/// <summary>Mistral sends content as a list of parts when reasoning is involved.</summary>
	private const string ArrayContentBody = """
		data: {"id":"cmpl_1","model":"mistral-large-latest","choices":[{"index":0,"delta":{"content":[{"type":"thinking","thinking":[{"type":"text","text":"considering"}]}]},"finish_reason":null}]}

		data: {"id":"cmpl_1","choices":[{"index":0,"delta":{"content":[{"type":"text","text":"Hello"}]},"finish_reason":null}]}

		data: {"id":"cmpl_1","choices":[{"index":0,"delta":{"content":[{"type":"text","text":" world"}]},"finish_reason":null}]}

		data: {"id":"cmpl_1","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call_1","type":"function","function":{"name":"read","arguments":"{\"path\":"}}]},"finish_reason":null}]}

		data: {"id":"cmpl_1","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"\"a.txt\"}"}}]},"finish_reason":null}]}

		data: {"id":"cmpl_1","choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}]}

		data: {"id":"cmpl_1","choices":[],"usage":{"prompt_tokens":9,"completion_tokens":4}}

		data: [DONE]

		""";

	[Fact]
	public async Task StreamsThinkingTextAndToolCallsFromArrayContent()
	{
		var handler = new FakeHttpHandler { Responder = _ => Sse(ArrayContentBody) };
		var provider = new MistralConversationsProvider(new HttpClient(handler));

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
		Assert.Contains("thinking_delta:considering", events);
		Assert.Contains("thinking_end:considering", events);
		Assert.Contains("text_delta:Hello", events);
		Assert.Contains("text_end:Hello world", events);
		Assert.Contains("toolcall_end:read", events);
		Assert.Equal("done:ToolUse", events[^1]);

		var message = await stream.Result;
		Assert.Equal(3, message.Content.Count);
		Assert.Equal("considering", Assert.IsType<ThinkingContent>(message.Content[0]).Thinking);
		Assert.Equal("Hello world", Assert.IsType<TextContent>(message.Content[1]).Text);
		Assert.Equal("a.txt", Assert.IsType<ToolCall>(message.Content[2]).Arguments["path"]!.GetValue<string>());

		Assert.Equal("cmpl_1", message.ResponseId);
		Assert.Equal(9, message.Usage.Input);
		Assert.Equal(4, message.Usage.Output);
		Assert.Equal(13, message.Usage.TotalTokens);
	}

	[Fact]
	public async Task HandlesPlainStringContent()
	{
		const string body = """
			data: {"id":"c2","choices":[{"index":0,"delta":{"content":"plain"},"finish_reason":null}]}

			data: {"id":"c2","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}

			data: [DONE]

			""";

		var handler = new FakeHttpHandler { Responder = _ => Sse(body) };
		var provider = new MistralConversationsProvider(new HttpClient(handler));

		var stream = provider.Stream(
			Model(),
			new Context { Messages = [new UserMessage { Content = MessageContent.FromText("hi") }] },
			new StreamOptions { ApiKey = "k" });

		await foreach (var _ in stream)
		{
		}

		var message = await stream.Result;
		Assert.Equal("plain", Assert.IsType<TextContent>(message.Content[0]).Text);
		Assert.Equal(StopReason.Stop, message.StopReason);
	}

	[Fact]
	public async Task SendsTheMistralRequestShape()
	{
		var handler = new FakeHttpHandler { Responder = _ => Sse(ArrayContentBody) };
		var provider = new MistralConversationsProvider(new HttpClient(handler));

		var context = new Context
		{
			SystemPrompt = "be terse",
			Messages = [new UserMessage { Content = MessageContent.FromText("hi"), Timestamp = 1 }],
			Tools = [new Tool { Name = "read", Description = "read a file", Parameters = new() { ["type"] = "object" } }],
		};

		var stream = provider.Stream(
			Model(),
			context,
			new StreamOptions { ApiKey = "test-key", MaxTokens = 512, Temperature = 0.2 });

		await foreach (var _ in stream)
		{
		}

		var request = handler.LastRequest!;
		Assert.Equal("https://api.mistral.ai/v1/chat/completions", request.RequestUri!.ToString());
		Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);

		using var document = JsonDocument.Parse(handler.LastBody!);
		var root = document.RootElement;

		Assert.Equal("mistral-large-latest", root.GetProperty("model").GetString());
		Assert.True(root.GetProperty("stream").GetBoolean());
		// Mistral only knows max_tokens, never max_completion_tokens, and takes no stream_options.
		Assert.Equal(512, root.GetProperty("max_tokens").GetInt32());
		Assert.False(root.TryGetProperty("max_completion_tokens", out _));
		Assert.False(root.TryGetProperty("stream_options", out _));
		Assert.Equal(0.2, root.GetProperty("temperature").GetDouble());

		// Mistral keeps the system prompt as the leading message.
		var messages = root.GetProperty("messages");
		Assert.Equal("system", messages[0].GetProperty("role").GetString());
		Assert.Equal("be terse", messages[0].GetProperty("content").GetString());
		Assert.Equal("user", messages[1].GetProperty("role").GetString());

		Assert.Equal("read", root.GetProperty("tools")[0].GetProperty("function").GetProperty("name").GetString());
	}

	[Fact]
	public async Task ErrorFinishReasonIsRetryable()
	{
		const string body = """
			data: {"id":"c3","choices":[{"index":0,"delta":{"content":"x"},"finish_reason":"error"}]}

			data: [DONE]

			""";

		var handler = new FakeHttpHandler { Responder = _ => Sse(body) };
		var provider = new MistralConversationsProvider(new HttpClient(handler));

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
		Assert.Contains("server error", error.Error.ErrorMessage);
	}

	[Fact]
	public async Task ModelLengthMapsToLength()
	{
		const string body = """
			data: {"id":"c4","choices":[{"index":0,"delta":{"content":"cut"},"finish_reason":"model_length"}]}

			data: [DONE]

			""";

		var handler = new FakeHttpHandler { Responder = _ => Sse(body) };
		var provider = new MistralConversationsProvider(new HttpClient(handler));

		var stream = provider.Stream(
			Model(),
			new Context { Messages = [new UserMessage { Content = MessageContent.FromText("hi") }] },
			new StreamOptions { ApiKey = "k" });

		await foreach (var _ in stream)
		{
		}

		var message = await stream.Result;
		Assert.Equal(StopReason.Length, message.StopReason);
		Assert.Equal("model_length", message.RawStopReason);
	}

	[Fact]
	public void ReasoningEffortFollowsTheThinkingLevelMap()
	{
		var mapped = Model() with
		{
			ThinkingLevelMap = new Dictionary<string, string?> { ["low"] = "low", ["high"] = "high" },
		};

		Assert.Equal("low", MistralConversationsProvider.ResolveReasoningEffort(mapped, new StreamOptions { ThinkingLevel = ThinkingLevels.Low }));
		Assert.Equal("high", MistralConversationsProvider.ResolveReasoningEffort(mapped, new StreamOptions { ThinkingLevel = "max" }));
		Assert.Null(MistralConversationsProvider.ResolveReasoningEffort(Model() with { Reasoning = false }, new StreamOptions()));
	}
}
