using System.Net;
using System.Text;
using System.Text.Json;
using PiWeft.Ai;
using PiWeft.Ai.Providers.OpenAi;
using Xunit;

namespace PiWeft.Ai.Tests;

public class OpenAiResponsesProviderTests
{
	private static Model Model(string provider = "openai", string baseUrl = "https://api.openai.com/v1") => new()
	{
		Id = "gpt-5",
		Provider = provider,
		Api = OpenAiResponsesProvider.ApiId,
		BaseUrl = baseUrl,
		Reasoning = true,
		ContextWindow = 400_000,
		MaxTokens = 128_000,
		Cost = new ModelCost { Input = 1.25, Output = 10, CacheRead = 0.125 },
	};

	/// <summary>Wraps an SSE body, making sure it ends with the blank line that dispatches the last event.</summary>
	private static HttpResponseMessage Sse(string body)
	{
		var terminated = body.EndsWith("\n\n", StringComparison.Ordinal)
			? body
			: body.TrimEnd('\n') + "\n\n";

		return new HttpResponseMessage(HttpStatusCode.OK)
		{
			Content = new StringContent(terminated, Encoding.UTF8, "text/event-stream"),
		};
	}

	private const string StreamBody = """
		event: response.created
		data: {"type":"response.created","response":{"id":"resp_1","model":"gpt-5","status":"in_progress"}}

		event: response.output_item.added
		data: {"type":"response.output_item.added","output_index":0,"item":{"type":"reasoning","id":"rs_1","summary":[]}}

		event: response.reasoning_summary_text.delta
		data: {"type":"response.reasoning_summary_text.delta","output_index":0,"delta":"thinking hard"}

		event: response.reasoning_summary_part.done
		data: {"type":"response.reasoning_summary_part.done","output_index":0,"summary_index":0}

		event: response.reasoning_summary_text.delta
		data: {"type":"response.reasoning_summary_text.delta","output_index":0,"delta":"more"}

		event: response.output_item.done
		data: {"type":"response.output_item.done","output_index":0,"item":{"type":"reasoning","id":"rs_1","summary":[{"type":"summary_text","text":"thinking hard"}]}}

		event: response.output_item.added
		data: {"type":"response.output_item.added","output_index":1,"item":{"type":"message","id":"msg_1","role":"assistant","content":[]}}

		event: response.output_text.delta
		data: {"type":"response.output_text.delta","output_index":1,"delta":"Hello"}

		event: response.output_text.delta
		data: {"type":"response.output_text.delta","output_index":1,"delta":" world"}

		event: response.output_item.done
		data: {"type":"response.output_item.done","output_index":1,"item":{"type":"message","id":"msg_1","role":"assistant","content":[{"type":"output_text","text":"Hello world"}]}}

		event: response.output_item.added
		data: {"type":"response.output_item.added","output_index":2,"item":{"type":"function_call","id":"fc_1","call_id":"call_1","name":"read","arguments":""}}

		event: response.function_call_arguments.delta
		data: {"type":"response.function_call_arguments.delta","output_index":2,"delta":"{\"path\":"}

		event: response.function_call_arguments.done
		data: {"type":"response.function_call_arguments.done","output_index":2,"arguments":"{\"path\":\"a.txt\"}"}

		event: response.output_item.done
		data: {"type":"response.output_item.done","output_index":2,"item":{"type":"function_call","id":"fc_1","call_id":"call_1","name":"read","arguments":"{\"path\":\"a.txt\"}"}}

		event: response.completed
		data: {"type":"response.completed","response":{"id":"resp_1","status":"completed","usage":{"input_tokens":20,"output_tokens":9,"total_tokens":29,"input_tokens_details":{"cached_tokens":4},"output_tokens_details":{"reasoning_tokens":3}}}}

		""";

	[Fact]
	public async Task StreamsReasoningTextAndFunctionCall()
	{
		var handler = new FakeHttpHandler { Responder = _ => Sse(StreamBody) };
		var provider = new OpenAiResponsesProvider(new HttpClient(handler));

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
		Assert.Contains("thinking_delta:thinking hard", events);
		Assert.Contains("thinking_delta:\n\n", events);
		Assert.Contains("thinking_end:thinking hard", events);
		Assert.Contains("text_end:Hello world", events);
		Assert.Contains("toolcall_end:read", events);
		Assert.Equal("done:ToolUse", events[^1]);

		var message = await stream.Result;
		Assert.Equal(3, message.Content.Count);

		var thinking = Assert.IsType<ThinkingContent>(message.Content[0]);
		Assert.Equal("thinking hard", thinking.Thinking);
		// The whole reasoning item is kept so it can be replayed.
		Assert.Contains("\"type\":\"reasoning\"", thinking.ThinkingSignature);

		Assert.Equal("Hello world", Assert.IsType<TextContent>(message.Content[1]).Text);
		var call = Assert.IsType<ToolCall>(message.Content[2]);
		Assert.Equal("call_1", call.Id);
		Assert.Equal("a.txt", call.Arguments["path"]!.GetValue<string>());

		Assert.Equal("resp_1", message.ResponseId);
		Assert.Equal("gpt-5", message.ResponseModel);

		// input excludes cached tokens, which OpenAI includes in input_tokens.
		Assert.Equal(16, message.Usage.Input);
		Assert.Equal(9, message.Usage.Output);
		Assert.Equal(4, message.Usage.CacheRead);
		Assert.Equal(3, message.Usage.Reasoning);
		Assert.Equal(29, message.Usage.TotalTokens);
		Assert.True(message.Usage.Cost.Total > 0);
		Assert.Equal(StopReason.ToolUse, message.StopReason);
		Assert.Equal("completed", message.RawStopReason);
	}

	[Fact]
	public async Task SendsExpectedRequest()
	{
		var handler = new FakeHttpHandler { Responder = _ => Sse(StreamBody) };
		var provider = new OpenAiResponsesProvider(new HttpClient(handler));

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

		var stream = provider.Stream(Model(), context, new StreamOptions { ApiKey = "test-key", MaxTokens = 512 });
		await foreach (var _ in stream)
		{
		}

		var request = handler.LastRequest!;
		Assert.Equal("https://api.openai.com/v1/responses", request.RequestUri!.ToString());
		Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);

		using var document = JsonDocument.Parse(handler.LastBody!);
		var root = document.RootElement;

		Assert.Equal("gpt-5", root.GetProperty("model").GetString());
		Assert.True(root.GetProperty("stream").GetBoolean());
		Assert.Equal(512, root.GetProperty("max_output_tokens").GetInt32());

		// The prompt travels in "instructions"; reasoning models also ask for encrypted reasoning replay.
		Assert.Equal("be terse", root.GetProperty("instructions").GetString());
		Assert.Equal("medium", root.GetProperty("reasoning").GetProperty("effort").GetString());
		Assert.Equal("auto", root.GetProperty("reasoning").GetProperty("summary").GetString());
		Assert.Contains("reasoning.encrypted_content", root.GetProperty("include").EnumerateArray().Select(v => v.GetString()));

		var input = root.GetProperty("input");
		Assert.Equal(3, input.GetArrayLength());
		Assert.Equal("user", input[0].GetProperty("role").GetString());
		Assert.Equal("input_text", input[0].GetProperty("content")[0].GetProperty("type").GetString());
		Assert.Equal("function_call", input[1].GetProperty("type").GetString());
		Assert.Equal("call_9", input[1].GetProperty("call_id").GetString());
		Assert.Equal("function_call_output", input[2].GetProperty("type").GetString());
		Assert.Equal("contents", input[2].GetProperty("output").GetString());

		var tools = root.GetProperty("tools");
		Assert.Equal("function", tools[0].GetProperty("type").GetString());
		Assert.Equal("read", tools[0].GetProperty("name").GetString());
	}

	[Fact]
	public async Task IncompleteResponseMapsToLength()
	{
		const string body = """
			event: response.created
			data: {"type":"response.created","response":{"id":"resp_2","status":"in_progress"}}

			event: response.output_item.added
			data: {"type":"response.output_item.added","output_index":0,"item":{"type":"message","id":"m","role":"assistant","content":[]}}

			event: response.output_text.delta
			data: {"type":"response.output_text.delta","output_index":0,"delta":"truncated"}

			event: response.incomplete
			data: {"type":"response.incomplete","response":{"id":"resp_2","status":"incomplete","incomplete_details":{"reason":"max_output_tokens"},"usage":{"input_tokens":5,"output_tokens":2,"total_tokens":7}}}

			""";

		var handler = new FakeHttpHandler { Responder = _ => Sse(body) };
		var provider = new OpenAiResponsesProvider(new HttpClient(handler));

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
		Assert.Equal("incomplete.max_output_tokens", done.Message.RawStopReason);
	}

	[Fact]
	public async Task FailedResponseBecomesAnErrorEvent()
	{
		const string body = """
			event: response.created
			data: {"type":"response.created","response":{"id":"resp_3","status":"in_progress"}}

			event: response.failed
			data: {"type":"response.failed","response":{"id":"resp_3","status":"failed","error":{"code":"server_error","message":"backend exploded"}}}

			""";

		var handler = new FakeHttpHandler { Responder = _ => Sse(body) };
		var provider = new OpenAiResponsesProvider(new HttpClient(handler));

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
		Assert.Contains("backend exploded", error.Error.ErrorMessage);
	}

	[Fact]
	public async Task NonReasoningModelOmitsReasoningFields()
	{
		var handler = new FakeHttpHandler { Responder = _ => Sse(StreamBody) };
		var provider = new OpenAiResponsesProvider(new HttpClient(handler));

		var model = Model() with { Reasoning = false, Id = "gpt-4.1" };
		var stream = provider.Stream(
			model,
			new Context
			{
				SystemPrompt = "be terse",
				Messages = [new UserMessage { Content = MessageContent.FromText("hi") }],
			},
			new StreamOptions { ApiKey = "k" });

		await foreach (var _ in stream)
		{
		}

		using var document = JsonDocument.Parse(handler.LastBody!);
		Assert.False(document.RootElement.TryGetProperty("reasoning", out _));
		Assert.False(document.RootElement.TryGetProperty("include", out _));
	}

	[Fact]
	public void ReasoningEffortFollowsThinkingLevelMap()
	{
		var mapped = Model() with
		{
			ThinkingLevelMap = new Dictionary<string, string?> { ["low"] = "minimal", ["high"] = "high" },
		};

		Assert.Equal("minimal", OpenAiResponsesProvider.ResolveReasoningEffort(mapped, new StreamOptions { ThinkingLevel = ThinkingLevels.Low }));
		Assert.Equal("high", OpenAiResponsesProvider.ResolveReasoningEffort(mapped, new StreamOptions { ThinkingLevel = "max" }));
		Assert.Equal("none", OpenAiResponsesProvider.ResolveReasoningEffort(mapped, new StreamOptions { ThinkingLevel = ThinkingLevels.Off }));
		Assert.Null(OpenAiResponsesProvider.ResolveReasoningEffort(Model() with { Reasoning = false }, new StreamOptions()));
	}
}
