using System.Text.Json.Nodes;
using PiWeft.Ai;

namespace PiWeft.Agent.Tests;

/// <summary>Builds scripted assistant streams so the loop can be tested without a network.</summary>
internal static class FakeStream
{
	public static Model Model(string id = "test-model") => new()
	{
		Id = id,
		Provider = "test",
		Api = "test",
		BaseUrl = "https://example.invalid",
		MaxTokens = 4096,
		ContextWindow = 128_000,
		Cost = new ModelCost { Input = 1, Output = 2 },
	};

	private static AssistantMessage Blank(Model model) => new()
	{
		Api = model.Api,
		Provider = model.Provider,
		Model = model.Id,
		Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
	};

	public static AssistantMessageEventStream Text(Model model, string text, StopReason reason = StopReason.Stop)
	{
		var stream = new AssistantMessageEventStream();
		var message = Blank(model);
		stream.Push(new StartEvent { Partial = message });

		message = message with { Content = [new TextContent { Text = text }] };
		stream.Push(new TextStartEvent { ContentIndex = 0, Partial = message });
		stream.Push(new TextDeltaEvent { ContentIndex = 0, Delta = text, Partial = message });
		stream.Push(new TextEndEvent { ContentIndex = 0, Content = text, Partial = message });

		var final = message with { StopReason = reason };
		stream.Push(reason is StopReason.Error or StopReason.Aborted
			? new ErrorEvent { Reason = reason, Partial = final }
			: new DoneEvent { Reason = reason, Partial = final });
		return stream;
	}

	public static AssistantMessageEventStream ToolCalls(
		Model model,
		StopReason reason,
		params (string Id, string Name, JsonObject Arguments)[] calls)
	{
		var stream = new AssistantMessageEventStream();
		var message = Blank(model);
		stream.Push(new StartEvent { Partial = message });

		var content = new List<ContentBlock>();
		foreach (var call in calls)
		{
			var index = content.Count;
			var toolCall = new ToolCall { Id = call.Id, Name = call.Name, Arguments = call.Arguments };
			content.Add(toolCall);
			message = message with { Content = [.. content] };
			stream.Push(new ToolCallStartEvent { ContentIndex = index, Partial = message });
			stream.Push(new ToolCallEndEvent { ContentIndex = index, ToolCall = toolCall, Partial = message });
		}

		var final = message with { StopReason = reason };
		stream.Push(reason is StopReason.Error or StopReason.Aborted
			? new ErrorEvent { Reason = reason, Partial = final }
			: new DoneEvent { Reason = reason, Partial = final });
		return stream;
	}

	public static AssistantMessageEventStream Error(Model model, string message)
	{
		var stream = new AssistantMessageEventStream();
		var failed = Blank(model) with { StopReason = StopReason.Error, ErrorMessage = message };
		stream.Push(new StartEvent { Partial = Blank(model) });
		stream.Push(new ErrorEvent { Reason = StopReason.Error, Partial = failed });
		return stream;
	}
}

/// <summary>A <see cref="StreamFn"/> that returns queued responses and records the requests it saw.</summary>
internal sealed class ScriptedStreamFn
{
	private readonly Queue<Func<Model, AssistantMessageEventStream>> _responses = new();

	public List<(Model Model, IReadOnlyList<Message> Messages, StreamOptions Options)> Requests { get; } = [];

	public int Remaining => _responses.Count;

	public void Enqueue(Func<Model, AssistantMessageEventStream> response) => _responses.Enqueue(response);

	public void EnqueueText(string text) => _responses.Enqueue(model => FakeStream.Text(model, text));

	public void EnqueueToolCalls(StopReason reason, params (string Id, string Name, JsonObject Arguments)[] calls) =>
		_responses.Enqueue(model => FakeStream.ToolCalls(model, reason, calls));

	public AssistantMessageEventStream Invoke(Model model, IReadOnlyList<Message> messages, StreamOptions options)
	{
		// The loop appends to the live transcript, so snapshot what this request actually saw.
		Requests.Add((model, [.. messages], options));
		if (_responses.Count == 0)
		{
			throw new InvalidOperationException("ScriptedStreamFn ran out of responses");
		}

		return _responses.Dequeue()(model);
	}
}

internal sealed class EventRecorder
{
	private readonly Lock _gate = new();
	private readonly List<AgentEvent> _events = [];

	public IReadOnlyList<AgentEvent> Events
	{
		get
		{
			lock (_gate)
			{
				return [.. _events];
			}
		}
	}

	public ValueTask Record(AgentEvent @event)
	{
		lock (_gate)
		{
			_events.Add(@event);
		}

		return ValueTask.CompletedTask;
	}

	public IEnumerable<string> Names
	{
		get
		{
			foreach (var @event in Events)
			{
				yield return @event switch
				{
					AgentStartEvent => "agent_start",
					AgentEndEvent => "agent_end",
					TurnStartEvent => "turn_start",
					TurnEndEvent => "turn_end",
					MessageStartEvent start => $"message_start:{Role(start.Message)}",
					MessageEndEvent end => $"message_end:{Role(end.Message)}",
					MessageUpdateEvent => "message_update",
					ToolExecutionStartEvent start => $"tool_start:{start.ToolName}",
					ToolExecutionEndEvent end => $"tool_end:{end.ToolName}",
					ToolExecutionUpdateEvent update => $"tool_update:{update.ToolName}",
					_ => "other",
				};
			}
		}
	}

	private static string Role(Message message) => message switch
	{
		SystemMessage => "system",
		UserMessage => "user",
		AssistantMessage => "assistant",
		ToolResultMessage => "toolResult",
		_ => "unknown",
	};
}
