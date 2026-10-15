using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using PiWeft.Agent;

// `PiWeft.Agent` is a namespace, so alias the runtime type.
using AgentRuntime = PiWeft.Agent.Agent;
using PiWeft.Ai;
using PiWeft.CodingAgent;
using PiWeft.CodingAgent.Compaction;
using PiWeft.CodingAgent.Session;
using PiWeft.CodingAgent.Settings;
using PiWeft.CodingAgent.Tools;
using Xunit;

namespace PiWeft.CodingAgent.Tests;

/// <summary>
/// A scripted provider that reports token usage derived from the transcript it was handed, so context growth behaves
/// like a real provider and the compaction threshold is reached the way it would be in production.
/// </summary>
internal sealed class UsageAwareStreamFn(int inputPerTurn)
{
	private readonly Lock _gate = new();
	private int _calls;

	public int Calls
	{
		get
		{
			lock (_gate)
			{
				return _calls;
			}
		}
	}

	public List<int> ReportedInputs { get; } = [];

	public AssistantMessageEventStream Invoke(Model model, IReadOnlyList<Message> messages, StreamOptions options)
	{
		// A provider bills the full transcript it was handed. That is the sum over every message, not the
		// usage-based estimate the agent uses internally (which trusts the previous response's usage).
		var estimated = (int)messages.Sum(ContextTokens.EstimateTokens);

		int call;
		lock (_gate)
		{
			call = ++_calls;
			ReportedInputs.Add(estimated);
		}

		// The provider bills exactly the transcript it was sent, and the answer is padded so the text the compaction
		// estimator sees is proportionate to that usage.
		var text = $"turn {call} answer\n" + new string('x', inputPerTurn * 4);
		return Streams.Text(model, text, estimated);
	}
}

/// <summary>Scripted responses used by the resilience tests.</summary>
internal static class Streams
{
	public static AssistantMessageEventStream Text(Model model, string text, int inputTokens)
	{
		var stream = new AssistantMessageEventStream();
		var blank = Blank(model);
		var message = blank with
		{
			Content = [new TextContent { Text = text }],
			StopReason = StopReason.Stop,
			Usage = new Usage { Input = inputTokens, Output = 5, TotalTokens = inputTokens + 5 },
		};

		stream.Push(new StartEvent { Partial = blank });
		stream.Push(new TextStartEvent { ContentIndex = 0, Partial = message });
		stream.Push(new TextDeltaEvent { ContentIndex = 0, Delta = text, Partial = message });
		stream.Push(new TextEndEvent { ContentIndex = 0, Content = text, Partial = message });
		stream.Push(new DoneEvent { Reason = StopReason.Stop, Partial = message });
		return stream;
	}

	public static AssistantMessageEventStream Empty(Model model)
	{
		var stream = new AssistantMessageEventStream();
		var blank = Blank(model);
		var message = blank with { StopReason = StopReason.Stop };
		stream.Push(new StartEvent { Partial = blank });
		stream.Push(new DoneEvent { Reason = StopReason.Stop, Partial = message });
		return stream;
	}

	public static AssistantMessageEventStream ToolCall(Model model, string id, string name, JsonObject arguments)
	{
		var stream = new AssistantMessageEventStream();
		var blank = Blank(model);
		var call = new ToolCall { Id = id, Name = name, Arguments = arguments };
		var message = blank with { Content = [call], StopReason = StopReason.ToolUse };

		stream.Push(new StartEvent { Partial = blank });
		stream.Push(new ToolCallStartEvent { ContentIndex = 0, Partial = message });
		stream.Push(new ToolCallEndEvent { ContentIndex = 0, ToolCall = call, Partial = message });
		stream.Push(new DoneEvent { Reason = StopReason.ToolUse, Partial = message });
		return stream;
	}

	public static AssistantMessage Blank(Model model) => new()
	{
		Api = model.Api,
		Provider = model.Provider,
		Model = model.Id,
		Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
	};
}

/// <summary>A queue of scripted responses, for tests that need several turns.</summary>
internal sealed class QueuedStreamFn
{
	private readonly Queue<Func<Model, AssistantMessageEventStream>> _responses = new();

	public void Enqueue(Func<Model, AssistantMessageEventStream> response) => _responses.Enqueue(response);

	public AssistantMessageEventStream Invoke(Model model, IReadOnlyList<Message> messages, StreamOptions options) =>
		_responses.Count > 0
			? _responses.Dequeue()(model)
			: throw new InvalidOperationException("QueuedStreamFn ran out of responses");
}

/// <summary>A minimal HTTP double for provider resilience checks.</summary>
internal sealed class StubHttpHandler : HttpMessageHandler
{
	public Func<HttpRequestMessage, HttpResponseMessage>? Responder { get; set; }

	protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
		Task.FromResult(Responder?.Invoke(request) ?? new HttpResponseMessage(HttpStatusCode.OK));
}

public class SessionSoakTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-soak-" + Guid.NewGuid().ToString("N")[..8]);

	public SessionSoakTests() => Directory.CreateDirectory(_root);

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	private string SessionsDir => Path.Combine(_root, "sessions");

	[Fact]
	public async Task ManyTurnsCompactRepeatedlyAndKeepTheContextBounded()
	{
		var script = new UsageAwareStreamFn(inputPerTurn: 700);
		var session = SessionManager.Create(_root, SessionsDir);

		using var coding = new CodingAgentSession(new CodingAgentSessionOptions
		{
			Cwd = _root,
			// The reserve must exceed one turn, otherwise a single turn can overshoot the window before the next
			// request gets a chance to compact.
			Model = ScriptedSessions.Model() with { ContextWindow = 8000, MaxTokens = 4096 },
			StreamFn = script.Invoke,
			Session = session,
			Skills = [],
			PromptTemplates = [],
			Settings = new CodingAgentSettings
			{
				Compaction = new CompactionSettings { Enabled = true, ReserveTokens = 2000, KeepRecentTokens = 1500 },
			},
		});

		var maxProjected = 0;
		for (var turn = 0; turn < 40; turn++)
		{
			await coding.PromptAsync($"question {turn}");
			maxProjected = Math.Max(maxProjected, coding.Messages.Count);
		}

		// Auto-compaction runs before each request, so every request that actually went out fits the window.
		Assert.All(
			script.ReportedInputs,
			static input => Assert.True(input < 8000, $"a request carried {input} tokens, over the 8000 window"));

		var compactions = session.Entries.Count(static entry => entry is CompactionEntry);
		Assert.True(compactions >= 2, $"expected repeated compaction, saw {compactions}");

		// The projected context stays bounded instead of growing with the turn count.
		Assert.True(maxProjected < 80, $"projected messages peaked at {maxProjected}");

		// Every line the run wrote still parses and round-trips byte-identically.
		var lines = File.ReadAllLines(session.SessionFile!);
		Assert.True(lines.Length > 40, $"expected a long session file, saw {lines.Length} lines");
		Assert.All(lines, static line =>
		{
			var parsed = SessionJson.ParseLine(line);
			Assert.NotNull(parsed);
			var reserialized = parsed switch
			{
				SessionHeader header => SessionJson.Serialize(header),
				SessionEntry entry => SessionJson.Serialize(entry),
				_ => throw new InvalidOperationException("unexpected parse result"),
			};

			Assert.True(
				JsonNode.DeepEquals(JsonNode.Parse(line), JsonNode.Parse(reserialized)),
				$"line did not round-trip: {line}");
		});

		// The completed run is resumable and its totals are coherent.
		var reopened = SessionManager.Open(session.SessionFile!);
		Assert.NotNull(reopened.Header);
		Assert.True(reopened.BuildProjectedMessages().Count > 0);

		var (tokens, cost) = coding.UsageTotals();
		Assert.True(tokens > 0);
		Assert.True(cost >= 0);
	}

	[Fact]
	public async Task CompactionKeepsTheMostRecentTurnsVerbatim()
	{
		var script = new UsageAwareStreamFn(inputPerTurn: 900);
		var session = SessionManager.Create(_root, SessionsDir);

		using var coding = new CodingAgentSession(new CodingAgentSessionOptions
		{
			Cwd = _root,
			Model = ScriptedSessions.Model() with { ContextWindow = 8000, MaxTokens = 4096 },
			StreamFn = script.Invoke,
			Session = session,
			Skills = [],
			PromptTemplates = [],
			Settings = new CodingAgentSettings
			{
				Compaction = new CompactionSettings { Enabled = true, ReserveTokens = 2000, KeepRecentTokens = 900 },
			},
		});

		await coding.PromptAsync("first topic");
		for (var turn = 0; turn < 8; turn++)
		{
			await coding.PromptAsync($"later question {turn}");
		}

		var messages = coding.Messages;

		// The newest user turn survives verbatim, and the summary is present ahead of it.
		Assert.Contains(messages, static message =>
			message is UserMessage user && user.Content.Text == "later question 7");

		Assert.Contains(messages, static message =>
			message is UserMessage user &&
			user.Content.Text is not null &&
			user.Content.Text.Contains("compacted into the following summary", StringComparison.Ordinal));
	}

	[Fact]
	public async Task LongSessionsStayResumableAcrossProcessBoundaries()
	{
		var script = new UsageAwareStreamFn(inputPerTurn: 500);
		var session = SessionManager.Create(_root, SessionsDir);

		using (var coding = new CodingAgentSession(new CodingAgentSessionOptions
		{
			Cwd = _root,
			Model = ScriptedSessions.Model() with { ContextWindow = 8000 },
			StreamFn = script.Invoke,
			Session = session,
			Skills = [],
			PromptTemplates = [],
		}))
		{
			for (var turn = 0; turn < 12; turn++)
			{
				await coding.PromptAsync($"q{turn}");
			}
		}

		// Reopen the way a fresh process would and continue the conversation.
		var reopened = SessionManager.Open(session.SessionFile!);
		var resumeScript = new UsageAwareStreamFn(inputPerTurn: 500);

		using var resumed = new CodingAgentSession(new CodingAgentSessionOptions
		{
			Cwd = _root,
			Model = ScriptedSessions.Model() with { ContextWindow = 8000 },
			StreamFn = resumeScript.Invoke,
			Session = reopened,
			Skills = [],
			PromptTemplates = [],
		});

		await resumed.PromptAsync("after resume");

		Assert.Contains(resumed.Messages, static message =>
			message is UserMessage user && user.Content.Text == "after resume");

		// The resumed run sent the whole previous transcript.
		Assert.Contains(resumeScript.ReportedInputs, static input => input > 0);
		Assert.True(resumed.Session.Entries.Count > 12);
	}
}

public class ProviderResilienceTests
{
	private static Model Model() => new()
	{
		Id = "claude-sonnet-4-5",
		Provider = "anthropic",
		Api = "anthropic-messages",
		BaseUrl = "https://api.anthropic.com",
		Reasoning = false,
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

	private static PiWeft.Ai.Providers.Anthropic.AnthropicProvider Provider(StubHttpHandler handler) =>
		new(new HttpClient(handler));

	private static AssistantMessageEventStream Stream(string body)
	{
		var handler = new StubHttpHandler { Responder = _ => Sse(body) };
		return Provider(handler).Stream(
			Model(),
			new Context { Messages = [new UserMessage { Content = MessageContent.FromText("hi") }] },
			new StreamOptions { ApiKey = "k" });
	}

	[Fact]
	public async Task AStreamThatEndsMidBlockStillProducesAMessage()
	{
		// The connection drops after the text block opened but before it was closed.
		const string body = """
			event: message_start
			data: {"type":"message_start","message":{"id":"m1","model":"claude-sonnet-4-5","usage":{"input_tokens":7}}}

			event: content_block_start
			data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}

			event: content_block_delta
			data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"partial"}}

			""";

		var stream = Stream(body);
		await foreach (var _ in stream)
		{
		}

		var message = await stream.Result;

		// The partial answer is preserved rather than discarded.
		Assert.Equal("partial", Assert.IsType<TextContent>(message.Content[0]).Text);
		Assert.True(message.StopReason is StopReason.Stop or StopReason.Error);
	}

	[Fact]
	public async Task MalformedSsePayloadsFailTheTurnWithAClearMessage()
	{
		const string body = """
			event: message_start
			data: {"type":"message_start","message":{"id":"m1","model":"claude-sonnet-4-5","usage":{"input_tokens":3}}}

			event: content_block_start
			data: not json at all

			event: content_block_start
			data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}

			event: content_block_delta
			data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"survived"}}

			event: content_block_stop
			data: {"type":"content_block_stop","index":0}

			event: message_delta
			data: {"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":2}}

			event: message_stop
			data: {"type":"message_stop"}

			""";

		var stream = Stream(body);
		var events = new List<AssistantMessageEvent>();
		await foreach (var @event in stream)
		{
			events.Add(@event);
		}

		// A payload the client cannot parse means the stream is untrustworthy, so the turn fails loudly.
		var error = Assert.IsType<ErrorEvent>(events[^1]);
		Assert.Equal(StopReason.Error, error.Reason);
		Assert.Contains("Could not parse provider SSE event", error.Error.ErrorMessage);
	}

	[Fact]
	public async Task UnknownSseEventsAreIgnored()
	{
		const string body = """
			event: message_start
			data: {"type":"message_start","message":{"id":"m1","model":"claude-sonnet-4-5","usage":{"input_tokens":3}}}

			event: something_new
			data: {"type":"something_new","payload":{"anything":true}}

			event: content_block_start
			data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}

			event: content_block_delta
			data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"ok"}}

			event: content_block_stop
			data: {"type":"content_block_stop","index":0}

			event: message_delta
			data: {"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":1}}

			event: message_stop
			data: {"type":"message_stop"}

			""";

		var stream = Stream(body);
		await foreach (var _ in stream)
		{
		}

		var message = await stream.Result;
		Assert.Equal("ok", Assert.IsType<TextContent>(message.Content[0]).Text);
		Assert.Equal(StopReason.Stop, message.StopReason);
	}

	[Fact]
	public async Task AThrowingToolBecomesAnErrorResultAndTheRunContinues()
	{
		var queued = new QueuedStreamFn();
		queued.Enqueue(model => Streams.ToolCall(model, "c1", "boom", new JsonObject()));
		queued.Enqueue(model => Streams.Text(model, "recovered", inputTokens: 20));

		AgentRuntime? agent = null;
		var tool = new AgentTool
		{
			Name = "boom",
			Label = "boom",
			Description = "always throws",
			Parameters = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
			Execute = _ => throw new InvalidOperationException("tool exploded"),
		};

		agent = new AgentRuntime(new AgentOptions
		{
			Model = ScriptedSessions.Model(),
			StreamFn = queued.Invoke,
			Tools = [tool],
			ToolExecution = ToolExecutionMode.Sequential,
		});

		var messages = await agent.PromptAsync("go");

		var toolResult = messages.OfType<ToolResultMessage>().Single();
		Assert.True(toolResult.IsError);
		Assert.Contains("tool exploded", Assert.IsType<TextContent>(toolResult.Content[0]).Text);

		// The failure reached the model and the next turn produced an answer.
		Assert.Contains(messages, static message =>
			message is AssistantMessage assistant &&
			assistant.Content.OfType<TextContent>().Any(static text => text.Text == "recovered"));
		Assert.Null(agent.ErrorMessage);
	}
}

public class SessionResilienceTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-resil-" + Guid.NewGuid().ToString("N")[..8]);

	public SessionResilienceTests() => Directory.CreateDirectory(_root);

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	[Fact]
	public void CorruptLinesAreSkippedAndTheRestOfTheSessionLoads()
	{
		var session = SessionManager.Create(_root, Path.Combine(_root, "sessions"));
		session.AppendMessage(new UserMessage { Content = MessageContent.FromText("one"), Timestamp = 1 });
		session.AppendMessage(new UserMessage { Content = MessageContent.FromText("two"), Timestamp = 2 });

		// Simulate a torn write in the middle of the file.
		var lines = File.ReadAllLines(session.SessionFile!);
		File.WriteAllLines(session.SessionFile!, [lines[0], lines[1], "{ this is not json", lines[2], string.Empty, "[]"]);

		var reopened = SessionManager.Open(session.SessionFile!);

		Assert.Equal(2, reopened.Entries.Count);
		Assert.Equal(2, reopened.BuildProjectedMessages().Count);
		Assert.Equal("two", Assert.IsType<UserMessage>(reopened.BuildProjectedMessages()[1]).Content.Text);
	}

	[Fact]
	public void AFileWithoutAHeaderDoesNotBreakListing()
	{
		var sessionsDir = Path.Combine(_root, "sessions");
		Directory.CreateDirectory(sessionsDir);
		var session = SessionManager.Create(_root, sessionsDir);
		session.AppendMessage(new UserMessage { Content = MessageContent.FromText("hello"), Timestamp = 1 });

		// A file with only half a header line must not stop the listing.
		File.WriteAllText(Path.Combine(sessionsDir, "broken.jsonl"), "{\"type\":\"sess");

		var listed = SessionManager.ListSessions(sessionsDir, _root);
		Assert.Contains(listed, entry => entry.Id == session.SessionId);
	}

	[Fact]
	public void HugeToolOutputIsTruncatedAndTheFullTextIsKeptOnDisk()
	{
		var output = string.Join("\n", Enumerable.Range(1, 5000).Select(static i => $"line {i}"));
		var truncation = Truncate.Tail(output);

		Assert.True(truncation.Truncated);
		Assert.True(truncation.OutputLines <= Truncate.DefaultMaxLines);

		var path = ShellRunner.WriteFullOutput(output);
		try
		{
			Assert.True(File.Exists(path));
			Assert.Contains("line 5000", File.ReadAllText(path));
		}
		finally
		{
			File.Delete(path);
		}
	}

	[Fact]
	public async Task ATurnWithAnEmptyAssistantResponseEndsCleanly()
	{
		var queued = new QueuedStreamFn();
		queued.Enqueue(Streams.Empty);

		var session = SessionManager.Create(_root, Path.Combine(_root, "sessions"));

		using var coding = new CodingAgentSession(new CodingAgentSessionOptions
		{
			Cwd = _root,
			Model = ScriptedSessions.Model(),
			StreamFn = queued.Invoke,
			Session = session,
			Skills = [],
			PromptTemplates = [],
		});

		var messages = await coding.PromptAsync("hello");

		var assistant = Assert.IsType<AssistantMessage>(messages[^1]);
		Assert.Empty(assistant.Content);
		Assert.Equal(StopReason.Stop, assistant.StopReason);

		// The session stays loadable: user turn, empty assistant turn (系统提示词不入库).
		Assert.Equal(2, session.Entries.Count(static entry => entry.Type == "message"));
	}
}
