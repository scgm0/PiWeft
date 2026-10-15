using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PiWeft.Ai.Providers.OpenAi;

/// <summary>
/// 把 OpenAI 兼容的 <c>chat.completion.chunk</c> 事件折叠进一个 <see cref="AssistantMessageEventStream"/>。
/// 一条流最多包含一个文本块与一个思考块；工具调用按流式索引区分。
/// </summary>
internal sealed class OpenAiCompletionsStreamState
{
	private readonly Model _model;
	private readonly AssistantMessageEventStream _stream;
	private readonly List<ContentBlock> _content = [];
	private readonly Dictionary<int, int> _toolContentIndex = [];
	private readonly Dictionary<int, StringBuilder> _toolArguments = [];
	private readonly StringBuilder _text = new();
	private readonly StringBuilder _thinking = new();

	private AssistantMessage _partial;
	private int _textIndex = -1;
	private int _thinkingIndex = -1;
	private string _thinkingSignature = string.Empty;
	private Usage _usage = new();
	private StopReason _stopReason = StopReason.Pending;
	private string? _rawStopReason;
	private string? _errorMessage;
	private string? _responseId;
	private string? _responseModel;
	private bool _started;
	private bool _finished;

	public OpenAiCompletionsStreamState(Model model, AssistantMessageEventStream stream, long startedAt)
	{
		_model = model;
		_stream = stream;
		_partial = new AssistantMessage
		{
			Api = OpenAiCompletionsProvider.ApiId,
			Provider = model.Provider,
			Model = model.Id,
			Timestamp = startedAt,
			StopReason = StopReason.Pending,
		};
	}

	public void Handle(JsonElement root)
	{
		if (_finished)
		{
			return;
		}

		if (root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
		{
			_responseId ??= id.GetString();
		}

		if (root.TryGetProperty("model", out var chunkModel) &&
			chunkModel.ValueKind == JsonValueKind.String &&
			chunkModel.GetString() is { Length: > 0 } modelName &&
			!string.Equals(modelName, _model.Id, StringComparison.Ordinal))
		{
			_responseModel ??= modelName;
		}

		var hasChunkUsage = false;
		if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
		{
			_usage = ParseUsage(usage);
			hasChunkUsage = true;
		}

		if (!root.TryGetProperty("choices", out var choices) ||
			choices.ValueKind != JsonValueKind.Array ||
			choices.GetArrayLength() == 0)
		{
			return;
		}

		var choice = choices[0];

		// 有些 provider（如 Moonshot）把用量放在 choice 上而不是 chunk 上。
		if (!hasChunkUsage &&
			choice.TryGetProperty("usage", out var choiceUsage) &&
			choiceUsage.ValueKind == JsonValueKind.Object)
		{
			_usage = ParseUsage(choiceUsage);
		}

		if (choice.TryGetProperty("finish_reason", out var finish) && finish.ValueKind == JsonValueKind.String)
		{
			_rawStopReason = finish.GetString();
			(_stopReason, _errorMessage) = MapStopReason(_rawStopReason);
		}

		if (!choice.TryGetProperty("delta", out var delta) || delta.ValueKind != JsonValueKind.Object)
		{
			return;
		}

		HandleTextDelta(delta);
		HandleThinkingDelta(delta);
		HandleToolCallDeltas(delta);
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
			Push(new TextEndEvent { ContentIndex = _textIndex, Content = _text.ToString(), Partial = Snapshot() });
		}

		if (_thinkingIndex >= 0 && _thinkingIndex < _content.Count)
		{
			Push(new ThinkingEndEvent
			{
				ContentIndex = _thinkingIndex,
				Content = _thinking.ToString(),
				Partial = Snapshot(),
			});
		}

		foreach (var (streamIndex, contentIndex) in _toolContentIndex.OrderBy(static pair => pair.Key))
		{
			if (_content[contentIndex] is not ToolCall call)
			{
				continue;
			}

			var arguments = ParseArguments(_toolArguments.TryGetValue(streamIndex, out var buffer) ? buffer.ToString() : null);
			call = call with { Arguments = arguments };
			_content[contentIndex] = call;
			Push(new ToolCallEndEvent { ContentIndex = contentIndex, ToolCall = call, Partial = Snapshot() });
		}

		var reason = _stopReason;
		if (reason == StopReason.Pending)
		{
			reason = _content.Any(static b => b is ToolCall) ? StopReason.ToolUse : StopReason.Stop;
		}

		if (_errorMessage is not null)
		{
			_partial = _partial with { ErrorMessage = _errorMessage };
		}

		_finished = true;
		var message = FinalMessage(reason);

		if (reason is StopReason.Error or StopReason.Aborted)
		{
			_stream.Push(new ErrorEvent { Reason = reason, Partial = message });
			return;
		}

		_stream.Push(new DoneEvent { Reason = reason, Partial = message });
	}

	public void Fail(string message, StopReason reason = StopReason.Error)
	{
		if (_finished)
		{
			return;
		}

		_partial = _partial with { ErrorMessage = message };
		_stopReason = reason;
		Finish();
	}

	private void HandleTextDelta(JsonElement delta)
	{
		if (!delta.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.String)
		{
			return;
		}

		var text = content.GetString();
		if (string.IsNullOrEmpty(text))
		{
			return;
		}

		if (_textIndex < 0)
		{
			EnsureStarted();
			_textIndex = _content.Count;
			_content.Add(new TextContent { Text = string.Empty });
			Push(new TextStartEvent { ContentIndex = _textIndex, Partial = Snapshot() });
		}

		_text.Append(text);
		_content[_textIndex] = new TextContent { Text = _text.ToString() };
		Push(new TextDeltaEvent { ContentIndex = _textIndex, Delta = text, Partial = Snapshot() });
	}

	private void HandleThinkingDelta(JsonElement delta)
	{
		// llama.cpp 用 reasoning_content，其它兼容端点用 reasoning 或 reasoning_text。
		// 取第一个非空字段，避免同时下发两个相同字段的 provider 产生重复输出。
		foreach (var field in ReasoningFields)
		{
			if (!delta.TryGetProperty(field, out var value) ||
				value.ValueKind != JsonValueKind.String ||
				value.GetString() is not { Length: > 0 } text)
			{
				continue;
			}

			if (_thinkingIndex < 0)
			{
				EnsureStarted();
				_thinkingSignature = field;
				_thinkingIndex = _content.Count;
				_content.Add(new ThinkingContent { Thinking = string.Empty, ThinkingSignature = _thinkingSignature });
				Push(new ThinkingStartEvent { ContentIndex = _thinkingIndex, Partial = Snapshot() });
			}

			_thinking.Append(text);
			_content[_thinkingIndex] = new ThinkingContent
			{
				Thinking = _thinking.ToString(),
				ThinkingSignature = _thinkingSignature,
			};
			Push(new ThinkingDeltaEvent { ContentIndex = _thinkingIndex, Delta = text, Partial = Snapshot() });
			return;
		}
	}

	private void HandleToolCallDeltas(JsonElement delta)
	{
		if (!delta.TryGetProperty("tool_calls", out var toolCalls) || toolCalls.ValueKind != JsonValueKind.Array)
		{
			return;
		}

		foreach (var toolCall in toolCalls.EnumerateArray())
		{
			var streamIndex = toolCall.TryGetProperty("index", out var index) && index.ValueKind == JsonValueKind.Number
				? index.GetInt32()
				: 0;

			if (!_toolContentIndex.TryGetValue(streamIndex, out var contentIndex))
			{
				EnsureStarted();
				contentIndex = _content.Count;
				_toolContentIndex[streamIndex] = contentIndex;
				_toolArguments[streamIndex] = new StringBuilder();
				_content.Add(new ToolCall
				{
					Id = GetString(toolCall, "id") ?? string.Empty,
					Name = GetString(toolCall, "function", "name") ?? string.Empty,
				});
				Push(new ToolCallStartEvent { ContentIndex = contentIndex, Partial = Snapshot() });
			}

			if (_content[contentIndex] is ToolCall current)
			{
				var id = GetString(toolCall, "id");
				var name = GetString(toolCall, "function", "name");
				if (!string.IsNullOrEmpty(id) || !string.IsNullOrEmpty(name))
				{
					current = current with
					{
						Id = string.IsNullOrEmpty(id) ? current.Id : id,
						Name = string.IsNullOrEmpty(name) ? current.Name : name,
					};
					_content[contentIndex] = current;
				}
			}

			var arguments = GetString(toolCall, "function", "arguments");
			if (string.IsNullOrEmpty(arguments))
			{
				continue;
			}

			var buffer = _toolArguments[streamIndex];
			buffer.Append(arguments);
			Push(new ToolCallDeltaEvent { ContentIndex = contentIndex, Delta = arguments, Partial = Snapshot() });
		}
	}

	private Usage ParseUsage(JsonElement usage)
	{
		var prompt = GetLong(usage, "prompt_tokens") ?? 0;
		var completion = GetLong(usage, "completion_tokens") ?? 0;

		long cacheRead = 0;
		long cacheWrite = 0;
		if (usage.TryGetProperty("prompt_tokens_details", out var promptDetails) &&
			promptDetails.ValueKind == JsonValueKind.Object)
		{
			cacheRead = GetLong(promptDetails, "cached_tokens") ?? 0;
			cacheWrite = GetLong(promptDetails, "cache_write_tokens") ?? 0;
		}

		cacheRead = cacheRead != 0
			? cacheRead
			: GetLong(usage, "prompt_cache_hit_tokens") ?? GetLong(usage, "cached_tokens") ?? 0;

		long? reasoning = null;
		if (usage.TryGetProperty("completion_tokens_details", out var completionDetails) &&
			completionDetails.ValueKind == JsonValueKind.Object)
		{
			reasoning = GetLong(completionDetails, "reasoning_tokens");
		}

		var input = Math.Max(0, prompt - cacheRead - cacheWrite);
		var parsed = new Usage
		{
			Input = input,
			Output = completion,
			CacheRead = cacheRead,
			CacheWrite = cacheWrite,
			Reasoning = reasoning,
			TotalTokens = input + completion + cacheRead + cacheWrite,
		};

		return CostCalculator.Calculate(_model, parsed);
	}

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

	private AssistantMessage FinalMessage(StopReason reason)
	{
		_partial = _partial with
		{
			Content = [.. _content],
			Usage = _usage,
			StopReason = reason,
			RawStopReason = _rawStopReason,
			ResponseId = _responseId,
			ResponseModel = _responseModel,
		};

		return _partial;
	}

	private void Push(AssistantMessageEvent @event) => _stream.Push(@event);

	private static readonly string[] ReasoningFields = ["reasoning_content", "reasoning", "reasoning_text"];

	private static (StopReason Reason, string? Error) MapStopReason(string? value) => value switch
	{
		null => (StopReason.Pending, null),
		"stop" or "end" => (StopReason.Stop, null),
		"length" => (StopReason.Length, null),
		"function_call" or "tool_calls" => (StopReason.ToolUse, null),
		"content_filter" => (StopReason.Error, "Provider finish_reason: content_filter"),
		"network_error" => (StopReason.Error, "Provider finish_reason: network_error"),
		_ => (StopReason.Error, $"Provider finish_reason: {value}"),
	};

	private static string? GetString(JsonElement element, string name) =>
		element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
			? value.GetString()
			: null;

	private static string? GetString(JsonElement element, string name, string nested)
	{
		if (!element.TryGetProperty(name, out var middle) || middle.ValueKind != JsonValueKind.Object)
		{
			return null;
		}

		return GetString(middle, nested);
	}

	private static long? GetLong(JsonElement element, string name) =>
		element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
			? value.GetInt64()
			: null;

	private static JsonObject ParseArguments(string? json)
	{
		if (string.IsNullOrWhiteSpace(json))
		{
			return [];
		}

		try
		{
			return JsonNode.Parse(json)?.AsObject() ?? [];
		}
		catch (JsonException)
		{
			return [];
		}
	}
}
