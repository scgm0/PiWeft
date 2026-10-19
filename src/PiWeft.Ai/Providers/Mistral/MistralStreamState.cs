using System.Text;
using System.Text.Json;
using PiWeft.Ai.Utils;
using System.Text.Json.Nodes;

namespace PiWeft.Ai.Providers.Mistral;

/// <summary>
/// 把 Mistral 的对话补充分片折叠进一个 <see cref="AssistantMessageEventStream"/>。Mistral 可能把
/// <c>delta.content</c> 发成字符串或分片列表（文本与思考），两种形状都要处理。
/// </summary>
internal sealed class MistralStreamState
{
	private readonly Model _model;
	private readonly AssistantMessageEventStream _stream;
	private readonly List<ContentBlock> _content = [];
	private readonly Dictionary<int, (int ContentIndex, StringBuilder Arguments)> _toolCalls = [];

	private AssistantMessage _partial;
	private Usage _usage = new();
	private StopReason _stopReason = StopReason.Pending;
	private string? _rawStopReason;
	private string? _errorMessage;
	private string? _responseId;
	private string? _responseModel;
	private int _textIndex = -1;
	private int _thinkingIndex = -1;
	private bool _started;
	private bool _finished;

	public MistralStreamState(Model model, AssistantMessageEventStream stream, long startedAt)
	{
		_model = model;
		_stream = stream;
		_partial = new AssistantMessage
		{
			Api = MistralConversationsProvider.ApiId,
			Provider = model.Provider,
			Model = model.Id,
			Timestamp = startedAt,
			StopReason = StopReason.Pending,
		};
	}

	public void Handle(JsonElement root)
	{
		if (_finished || root.ValueKind != JsonValueKind.Object)
		{
			return;
		}

		if (GetString(root, "id") is { Length: > 0 } id)
		{
			_responseId ??= id;
		}

		if (GetString(root, "model") is { Length: > 0 } modelName &&
			!string.Equals(modelName, _model.Id, StringComparison.Ordinal))
		{
			_responseModel ??= modelName;
		}

		if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
		{
			ReadUsage(usage);
		}

		if (!root.TryGetProperty("choices", out var choices) ||
			choices.ValueKind != JsonValueKind.Array ||
			choices.GetArrayLength() == 0)
		{
			return;
		}

		var choice = choices[0];

		if (GetString(choice, "finish_reason") is { Length: > 0 } finishReason)
		{
			_rawStopReason = finishReason;
			(_stopReason, _errorMessage) = MapStopReason(finishReason);
		}

		if (!choice.TryGetProperty("delta", out var delta) || delta.ValueKind != JsonValueKind.Object)
		{
			return;
		}

		HandleContent(delta);
		HandleToolCalls(delta);
	}

	public void Finish()
	{
		if (_finished)
		{
			return;
		}

		EnsureStarted();

		if (_textIndex >= 0 && _textIndex < _content.Count)
		{
			Push(new TextEndEvent
			{
				ContentIndex = _textIndex,
				Content = ((TextContent)_content[_textIndex]).Text,
				Partial = Snapshot(),
			});
		}

		if (_thinkingIndex >= 0 && _thinkingIndex < _content.Count)
		{
			Push(new ThinkingEndEvent
			{
				ContentIndex = _thinkingIndex,
				Content = ((ThinkingContent)_content[_thinkingIndex]).Thinking,
				Partial = Snapshot(),
			});
		}

		foreach (var (streamIndex, entry) in _toolCalls.OrderBy(static pair => pair.Key))
		{
			if (_content[entry.ContentIndex] is not ToolCall call)
			{
				continue;
			}

			var (arguments, argumentsError) = JsonRepair.ParseObject(entry.Arguments.ToString());
			call = call with { Arguments = arguments ?? new(), ArgumentsError = argumentsError };
			_content[entry.ContentIndex] = call;
			Push(new ToolCallEndEvent { ContentIndex = entry.ContentIndex, ToolCall = call, Partial = Snapshot() });
		}

		var reason = _stopReason;
		if (reason == StopReason.Pending)
		{
			reason = _content.Any(static block => block is ToolCall) ? StopReason.ToolUse : StopReason.Stop;
		}

		if (_errorMessage is not null)
		{
			_partial = _partial with { ErrorMessage = _errorMessage };
		}

		_finished = true;
		_partial = _partial with
		{
			Content = [.. _content],
			Usage = _usage,
			StopReason = reason,
			RawStopReason = _rawStopReason,
			ResponseId = _responseId,
			ResponseModel = _responseModel,
		};

		if (reason is StopReason.Error or StopReason.Aborted)
		{
			_stream.Push(new ErrorEvent { Reason = reason, Partial = _partial });
			return;
		}

		_stream.Push(new DoneEvent { Reason = reason, Partial = _partial });
	}

	private void HandleContent(JsonElement delta)
	{
		if (!delta.TryGetProperty("content", out var content) || content.ValueKind == JsonValueKind.Null)
		{
			return;
		}

		if (content.ValueKind == JsonValueKind.String)
		{
			AppendText(content.GetString());
			return;
		}

		if (content.ValueKind != JsonValueKind.Array)
		{
			return;
		}

		foreach (var item in content.EnumerateArray())
		{
			switch (item.ValueKind)
			{
				case JsonValueKind.String:
					AppendText(item.GetString());
					break;

				case JsonValueKind.Object when GetString(item, "type") == "text":
					AppendText(GetString(item, "text"));
					break;

				case JsonValueKind.Object when GetString(item, "type") == "thinking":
					AppendThinking(item);
					break;
			}
		}
	}

	private void AppendText(string? delta)
	{
		if (string.IsNullOrEmpty(delta))
		{
			return;
		}

		EnsureStarted();

		if (_textIndex < 0)
		{
			_textIndex = _content.Count;
			_content.Add(new TextContent { Text = string.Empty });
			Push(new TextStartEvent { ContentIndex = _textIndex, Partial = Snapshot() });
		}

		var current = (TextContent)_content[_textIndex];
		_content[_textIndex] = current with { Text = current.Text + delta };
		Push(new TextDeltaEvent { ContentIndex = _textIndex, Delta = delta, Partial = Snapshot() });
	}

	private void AppendThinking(JsonElement item)
	{
		var text = string.Empty;
		if (item.TryGetProperty("thinking", out var thinking))
		{
			text = thinking.ValueKind switch
			{
				JsonValueKind.Array => string.Concat(thinking.EnumerateArray()
					.Select(part => part.ValueKind == JsonValueKind.Object ? GetString(part, "text") : part.GetString())),
				JsonValueKind.String => thinking.GetString(),
				_ => string.Empty,
			};
		}

		if (string.IsNullOrEmpty(text))
		{
			return;
		}

		EnsureStarted();

		if (_thinkingIndex < 0)
		{
			_thinkingIndex = _content.Count;
			_content.Add(new ThinkingContent { Thinking = string.Empty });
			Push(new ThinkingStartEvent { ContentIndex = _thinkingIndex, Partial = Snapshot() });
		}

		var current = (ThinkingContent)_content[_thinkingIndex];
		_content[_thinkingIndex] = current with { Thinking = current.Thinking + text };
		Push(new ThinkingDeltaEvent { ContentIndex = _thinkingIndex, Delta = text, Partial = Snapshot() });
	}

	private void HandleToolCalls(JsonElement delta)
	{
		if (!delta.TryGetProperty("tool_calls", out var calls) || calls.ValueKind != JsonValueKind.Array)
		{
			return;
		}

		foreach (var toolCall in calls.EnumerateArray())
		{
			var streamIndex = toolCall.TryGetProperty("index", out var index) && index.ValueKind == JsonValueKind.Number
				? index.GetInt32()
				: 0;

			if (!_toolCalls.TryGetValue(streamIndex, out var entry))
			{
				EnsureStarted();

				var contentIndex = _content.Count;
				_content.Add(new ToolCall
				{
					Id = GetString(toolCall, "id") ?? string.Empty,
					Name = GetString(toolCall, "function", "name") ?? string.Empty,
					Arguments = new JsonObject(),
				});

				entry = (contentIndex, new StringBuilder());
				_toolCalls[streamIndex] = entry;
				Push(new ToolCallStartEvent { ContentIndex = contentIndex, Partial = Snapshot() });
			}

			if (_content[entry.ContentIndex] is ToolCall current)
			{
				var id = GetString(toolCall, "id");
				var name = GetString(toolCall, "function", "name");
				if (!string.IsNullOrEmpty(id) || !string.IsNullOrEmpty(name))
				{
					_content[entry.ContentIndex] = current with
					{
						Id = string.IsNullOrEmpty(id) ? current.Id : id,
						Name = string.IsNullOrEmpty(name) ? current.Name : name,
					};
				}
			}

			if (GetString(toolCall, "function", "arguments") is { Length: > 0 } arguments)
			{
				entry.Arguments.Append(arguments);
				Push(new ToolCallDeltaEvent
				{
					ContentIndex = entry.ContentIndex,
					Delta = arguments,
					Partial = Snapshot(),
				});
			}
		}
	}

	private void ReadUsage(JsonElement usage)
	{
		var prompt = GetLong(usage, "prompt_tokens") ?? 0;
		var completion = GetLong(usage, "completion_tokens") ?? 0;

		_usage = CostCalculator.Calculate(_model, new Usage
		{
			Input = prompt,
			Output = completion,
			CacheRead = 0,
			CacheWrite = 0,
			TotalTokens = prompt + completion,
		});
	}

	private static (StopReason Reason, string? Error) MapStopReason(string reason) => reason switch
	{
		"stop" => (StopReason.Stop, null),
		"length" or "model_length" => (StopReason.Length, null),
		"tool_calls" => (StopReason.ToolUse, null),
		// Mistral 用这种方式上报服务端临时故障。
		"error" => (StopReason.Error, "Provider stopped with: error (server error)"),
		_ => (StopReason.Error, $"Provider stopped with: {reason}"),
	};

	private void EnsureStarted()
	{
		if (_started)
		{
			return;
		}

		_started = true;
		_partial = _partial with { ResponseId = _responseId, ResponseModel = _responseModel };
		Push(new StartEvent { Partial = _partial });
	}

	private AssistantMessage Snapshot() => _partial = _partial with
	{
		Content = [.. _content],
		ResponseId = _responseId,
		ResponseModel = _responseModel,
	};

	private void Push(AssistantMessageEvent @event) => _stream.Push(@event);


	private static string? GetString(JsonElement element, string name) =>
		element.ValueKind == JsonValueKind.Object &&
		element.TryGetProperty(name, out var value) &&
		value.ValueKind == JsonValueKind.String
			? value.GetString()
			: null;

	private static string? GetString(JsonElement element, string name, string nested)
	{
		if (element.ValueKind != JsonValueKind.Object ||
			!element.TryGetProperty(name, out var middle) ||
			middle.ValueKind != JsonValueKind.Object)
		{
			return null;
		}

		return GetString(middle, nested);
	}

	private static long? GetLong(JsonElement element, string name) =>
		element.ValueKind == JsonValueKind.Object &&
		element.TryGetProperty(name, out var value) &&
		value.ValueKind == JsonValueKind.Number
			? value.GetInt64()
			: null;
}
