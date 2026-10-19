using System.Text;
using System.Text.Json;
using PiWeft.Ai.Utils;
using System.Text.Json.Nodes;

namespace PiWeft.Ai.Providers.Bedrock;

/// <summary>
/// 把 Bedrock Converse 的流事件折叠进一个 <see cref="AssistantMessageEventStream"/>；内容块按服务给出的
/// <c>contentBlockIndex</c> 跟踪。
/// </summary>
internal sealed class BedrockStreamState
{
	private sealed class Slot
	{
		public required string Kind { get; init; }

		public required int ContentIndex { get; init; }

		public StringBuilder Buffer { get; } = new();

		public bool Ended { get; set; }
	}

	private readonly Model _model;
	private readonly AssistantMessageEventStream _stream;
	private readonly List<ContentBlock> _content = [];
	private readonly Dictionary<int, Slot> _slots = [];

	private AssistantMessage _partial;
	private Usage _usage = new();
	private StopReason _stopReason = StopReason.Pending;
	private string? _rawStopReason;
	private string? _errorMessage;
	private string? _requestId;
	private bool _started;
	private bool _finished;

	public BedrockStreamState(Model model, AssistantMessageEventStream stream, long startedAt)
	{
		_model = model;
		_stream = stream;
		_partial = new AssistantMessage
		{
			Api = BedrockConverseProvider.ApiId,
			Provider = model.Provider,
			Model = model.Id,
			Timestamp = startedAt,
			StopReason = StopReason.Pending,
		};
	}

	public void Handle(AwsEventStreamMessage message)
	{
		if (_finished)
		{
			return;
		}

		if (message.IsError)
		{
			_errorMessage = message.ErrorMessage ?? message.ErrorCode ?? "Bedrock stream error";
			_stopReason = StopReason.Error;
			Finish();
			return;
		}

		// 服务会把请求 id 附在第一个事件的消息头里。
		if (message.Headers.TryGetValue("x-amzn-requestid", out var requestId))
		{
			_requestId ??= requestId;
		}

		using var document = message.ParseJson();
		if (document is null)
		{
			return;
		}

		var root = document.RootElement;
		switch (message.EventType)
		{
			case "messageStart":
				EnsureStarted();
				break;

			case "contentBlockStart":
				OnBlockStart(root);
				break;

			case "contentBlockDelta":
				OnBlockDelta(root);
				break;

			case "contentBlockStop":
				if (TryGetSlot(root, out var slot))
				{
					CloseSlot(slot);
				}

				break;

			case "messageStop":
				_rawStopReason = GetString(root, "stopReason");
				(_stopReason, _errorMessage) = MapStopReason(_rawStopReason);
				break;

			case "metadata":
				ReadUsage(root);
				break;
		}
	}

	public void Finish()
	{
		if (_finished)
		{
			return;
		}

		EnsureStarted();
		foreach (var slot in _slots.Values.OrderBy(static pair => pair.ContentIndex))
		{
			CloseSlot(slot);
		}

		if (_stopReason == StopReason.Pending)
		{
			_stopReason = StopReason.Stop;
		}

		if (_stopReason == StopReason.Stop && _content.Any(static block => block is ToolCall))
		{
			_stopReason = StopReason.ToolUse;
		}

		if (_errorMessage is not null)
		{
			_partial = _partial with { ErrorMessage = _errorMessage };
		}

		_finished = true;
		var result = FinalMessage(_stopReason);

		if (_stopReason is StopReason.Error or StopReason.Aborted)
		{
			_stream.Push(new ErrorEvent { Reason = _stopReason, Partial = result });
			return;
		}

		_stream.Push(new DoneEvent { Reason = _stopReason, Partial = result });
	}

	private void OnBlockStart(JsonElement root)
	{
		var start = root.TryGetProperty("start", out var startElement) ? startElement : default;
		var toolUse = default(JsonElement);
		var isToolUse = start.ValueKind == JsonValueKind.Object &&
			start.TryGetProperty("toolUse", out toolUse) &&
			toolUse.ValueKind == JsonValueKind.Object;

		EnsureSlot(GetInt(root, "contentBlockIndex") ?? _content.Count, isToolUse, toolUse);
	}

	/// <summary>若 <paramref name="index"/> 对应的块尚未打开则打开它。</summary>
	private void EnsureSlot(int index, bool isToolUse, JsonElement toolUse)
	{
		EnsureStarted();

		if (_slots.ContainsKey(index))
		{
			return;
		}

		var contentIndex = _content.Count;
		var slot = new Slot { Kind = isToolUse ? "toolCall" : "text", ContentIndex = contentIndex };

		if (isToolUse)
		{
			var call = new ToolCall
			{
				Id = GetString(toolUse, "toolUseId") ?? string.Empty,
				Name = GetString(toolUse, "name") ?? string.Empty,
				Arguments = new JsonObject(),
			};

			_content.Add(call);
			_slots[index] = slot;
			Push(new ToolCallStartEvent { ContentIndex = contentIndex, Partial = Snapshot() });
			return;
		}

		_content.Add(new TextContent { Text = string.Empty });
		_slots[index] = slot;
		Push(new TextStartEvent { ContentIndex = contentIndex, Partial = Snapshot() });
	}

	private void OnBlockDelta(JsonElement root)
	{
		if (!root.TryGetProperty("delta", out var delta) || delta.ValueKind != JsonValueKind.Object)
		{
			return;
		}

		if (!TryGetSlot(root, out var slot))
		{
			// 有些服务不发 contentBlockStart，此时直接根据 delta 打开内容块。
			var index = GetInt(root, "contentBlockIndex") ?? _content.Count;
			var pending = delta.TryGetProperty("toolUse", out var pendingElement) ? pendingElement : default;
			EnsureSlot(index, pending.ValueKind == JsonValueKind.Object, pending);
			if (!TryGetSlot(root, out slot))
			{
				return;
			}
		}

		if (slot.Kind == "text")
		{
			if (GetString(delta, "text") is not { Length: > 0 } text)
			{
				return;
			}

			slot.Buffer.Append(text);
			_content[slot.ContentIndex] = new TextContent { Text = slot.Buffer.ToString() };
			Push(new TextDeltaEvent { ContentIndex = slot.ContentIndex, Delta = text, Partial = Snapshot() });
			return;
		}

		// 工具参数以 JSON 片段的形式流式下发。
		if (delta.TryGetProperty("toolUse", out var toolUse) && GetString(toolUse, "input") is { Length: > 0 } fragment)
		{
			slot.Buffer.Append(fragment);
			Push(new ToolCallDeltaEvent { ContentIndex = slot.ContentIndex, Delta = fragment, Partial = Snapshot() });
		}
	}

	private void CloseSlot(Slot slot)
	{
		if (slot.Ended)
		{
			return;
		}

		slot.Ended = true;

		if (slot.Kind == "text")
		{
			Push(new TextEndEvent
			{
				ContentIndex = slot.ContentIndex,
				Content = slot.Buffer.ToString(),
				Partial = Snapshot(),
			});
			return;
		}

		var (arguments, argumentsError) = JsonRepair.ParseObject(slot.Buffer.ToString());
		var call = ((ToolCall)_content[slot.ContentIndex]) with
		{
			Arguments = arguments ?? new(),
			ArgumentsError = argumentsError,
		};

		_content[slot.ContentIndex] = call;
		Push(new ToolCallEndEvent { ContentIndex = slot.ContentIndex, ToolCall = call, Partial = Snapshot() });
	}

	private void ReadUsage(JsonElement root)
	{
		if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
		{
			return;
		}

		var input = GetLong(usage, "inputTokens") ?? 0;
		var output = GetLong(usage, "outputTokens") ?? 0;
		var cacheRead = GetLong(usage, "cacheReadInputTokens") ?? 0;
		var cacheWrite = GetLong(usage, "cacheWriteInputTokens") ?? 0;

		_usage = CostCalculator.Calculate(_model, new Usage
		{
			Input = input,
			Output = output,
			CacheRead = cacheRead,
			CacheWrite = cacheWrite,
			TotalTokens = input + output + cacheRead + cacheWrite,
		});
	}

	private static (StopReason Reason, string? Error) MapStopReason(string? reason) => reason switch
	{
		null => (StopReason.Pending, null),
		"end_turn" or "stop_sequence" => (StopReason.Stop, null),
		"tool_use" => (StopReason.ToolUse, null),
		"max_tokens" => (StopReason.Length, null),
		"content_filtered" => (StopReason.Error, "Response blocked: content_filtered"),
		"guardrail_intervened" => (StopReason.Error, "Response blocked by a guardrail"),
		"malformed_model_output" => (StopReason.Error, "Malformed model output"),
		"malformed_tool_use" => (StopReason.Error, "Malformed tool use"),
		_ => (StopReason.Stop, null),
	};

	private bool TryGetSlot(JsonElement root, out Slot slot)
	{
		var index = GetInt(root, "contentBlockIndex");
		if (index is not null && _slots.TryGetValue(index.Value, out var found))
		{
			slot = found;
			return true;
		}

		slot = null!;
		return false;
	}

	private void EnsureStarted()
	{
		if (_started)
		{
			return;
		}

		_started = true;
		_partial = _partial with { ResponseId = _requestId };
		Push(new StartEvent { Partial = _partial });
	}

	private AssistantMessage Snapshot() => _partial = _partial with
	{
		Content = [.. _content],
		ResponseId = _requestId,
	};

	private AssistantMessage FinalMessage(StopReason reason)
	{
		_partial = _partial with
		{
			Content = [.. _content],
			Usage = _usage,
			StopReason = reason,
			RawStopReason = _rawStopReason,
			ResponseId = _requestId,
		};

		return _partial;
	}

	private void Push(AssistantMessageEvent @event) => _stream.Push(@event);


	private static string? GetString(JsonElement element, string name) =>
		element.ValueKind == JsonValueKind.Object &&
		element.TryGetProperty(name, out var value) &&
		value.ValueKind == JsonValueKind.String
			? value.GetString()
			: null;

	private static int? GetInt(JsonElement element, string name) =>
		element.ValueKind == JsonValueKind.Object &&
		element.TryGetProperty(name, out var value) &&
		value.ValueKind == JsonValueKind.Number
			? value.GetInt32()
			: null;

	private static long? GetLong(JsonElement element, string name) =>
		element.ValueKind == JsonValueKind.Object &&
		element.TryGetProperty(name, out var value) &&
		value.ValueKind == JsonValueKind.Number
			? value.GetInt64()
			: null;
}
