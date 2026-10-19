using System.Text;
using System.Text.Json;
using PiWeft.Ai.Utils;
using System.Text.Json.Nodes;

using PiWeft.Ai.Json;
namespace PiWeft.Ai.Providers.Google;

/// <summary>
/// 把 Gemini <c>streamGenerateContent</c> 的分片折叠进一个 <see cref="AssistantMessageEventStream"/>。Gemini 流式下发的是
/// 完整分片（不是增量），且文本与思考共用同一个 part 列表，因此内容块会随 part 类型变化而开合。
/// </summary>
internal sealed class GoogleStreamState
{
	private readonly Model _model;
	private readonly AssistantMessageEventStream _stream;
	private readonly List<ContentBlock> _content = [];

	private AssistantMessage _partial;
	private StringBuilder? _textBuffer;
	private StringBuilder? _thinkingBuffer;
	private int _textIndex = -1;
	private int _thinkingIndex = -1;
	private string? _textSignature;
	private string? _thinkingSignature;
	private Usage _usage = new();
	private StopReason _stopReason = StopReason.Pending;
	private string? _rawStopReason;
	private string? _errorMessage;
	private string? _responseId;
	private string? _responseModel;
	private int _toolCallCounter;
	private bool _started;
	private bool _finished;

	public GoogleStreamState(
		Model model,
		AssistantMessageEventStream stream,
		long startedAt,
		string api = GoogleGenerativeAiProvider.ApiId)
	{
		_model = model;
		_stream = stream;
		_partial = new AssistantMessage
		{
			Api = api,
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

		if (GetString(root, "responseId") is { Length: > 0 } responseId)
		{
			_responseId ??= responseId;
		}

		if (GetString(root, "modelVersion") is { Length: > 0 } modelVersion)
		{
			_responseModel ??= modelVersion;
		}

		if (root.TryGetProperty("usageMetadata", out var usageMetadata) &&
			usageMetadata.ValueKind == JsonValueKind.Object)
		{
			ReadUsage(usageMetadata);
		}

		if (root.TryGetProperty("candidates", out var candidates) &&
			candidates.ValueKind == JsonValueKind.Array &&
			candidates.GetArrayLength() > 0)
		{
			var candidate = candidates[0];

			if (candidate.TryGetProperty("content", out var content) &&
				content.ValueKind == JsonValueKind.Object &&
				content.TryGetProperty("parts", out var parts) &&
				parts.ValueKind == JsonValueKind.Array)
			{
				foreach (var part in parts.EnumerateArray())
				{
					HandlePart(part);
				}
			}

			if (GetString(candidate, "finishReason") is { Length: > 0 } finishReason)
			{
				_rawStopReason = finishReason;
				(_stopReason, _errorMessage) = MapStopReason(finishReason);
			}
		}
	}

	public void Finish()
	{
		if (_finished)
		{
			return;
		}

		EnsureStarted();
		CloseText();
		CloseThinking();

		if (_stopReason == StopReason.Pending)
		{
			_stopReason = StopReason.Stop;
		}

		// 停止原因伴随工具调用时，说明这是一个工具轮次。
		if (_stopReason == StopReason.Stop && _content.Any(static block => block is ToolCall))
		{
			_stopReason = StopReason.ToolUse;
		}

		if (_errorMessage is not null)
		{
			_partial = _partial with { ErrorMessage = _errorMessage };
		}

		_finished = true;
		var message = FinalMessage(_stopReason);

		if (_stopReason is StopReason.Error or StopReason.Aborted)
		{
			_stream.Push(new ErrorEvent { Reason = _stopReason, Partial = message });
			return;
		}

		_stream.Push(new DoneEvent { Reason = _stopReason, Partial = message });
	}

	private void HandlePart(JsonElement part)
	{
		if (part.TryGetProperty("text", out var textNode) && textNode.ValueKind == JsonValueKind.String)
		{
			var text = textNode.GetString();
			var isThought = part.TryGetProperty("thought", out var thought) && thought.ValueKind == JsonValueKind.True;
			var signature = GetString(part, "thoughtSignature");

			if (!string.IsNullOrEmpty(text) || !string.IsNullOrEmpty(signature))
			{
				if (isThought)
				{
					AppendThinking(text ?? string.Empty, signature);
				}
				else
				{
					AppendText(text ?? string.Empty, signature);
				}
			}
		}

		if (part.TryGetProperty("functionCall", out var functionCall) && functionCall.ValueKind == JsonValueKind.Object)
		{
			CloseText();
			CloseThinking();
			AddToolCall(functionCall, GetString(part, "thoughtSignature"));
		}
	}

	private void AppendText(string delta, string? signature)
	{
		if (delta.Length == 0 && string.IsNullOrEmpty(signature))
		{
			return;
		}

		CloseThinking();
		EnsureStarted();

		if (_textBuffer is null)
		{
			_textIndex = _content.Count;
			_textBuffer = new StringBuilder();
			_content.Add(new TextContent { Text = string.Empty });
			Push(new TextStartEvent { ContentIndex = _textIndex, Partial = Snapshot() });
		}

		_textBuffer.Append(delta);
		_textSignature = string.IsNullOrEmpty(signature) ? _textSignature : signature;
		_content[_textIndex] = new TextContent
		{
			Text = _textBuffer.ToString(),
			TextSignature = _textSignature,
		};

		if (delta.Length > 0)
		{
			Push(new TextDeltaEvent { ContentIndex = _textIndex, Delta = delta, Partial = Snapshot() });
		}
	}

	private void AppendThinking(string delta, string? signature)
	{
		if (delta.Length == 0 && string.IsNullOrEmpty(signature))
		{
			return;
		}

		CloseText();
		EnsureStarted();

		if (_thinkingBuffer is null)
		{
			_thinkingIndex = _content.Count;
			_thinkingBuffer = new StringBuilder();
			_content.Add(new ThinkingContent { Thinking = string.Empty });
			Push(new ThinkingStartEvent { ContentIndex = _thinkingIndex, Partial = Snapshot() });
		}

		_thinkingBuffer.Append(delta);
		_thinkingSignature = string.IsNullOrEmpty(signature) ? _thinkingSignature : signature;
		_content[_thinkingIndex] = new ThinkingContent
		{
			Thinking = _thinkingBuffer.ToString(),
			ThinkingSignature = _thinkingSignature,
		};

		if (delta.Length > 0)
		{
			Push(new ThinkingDeltaEvent { ContentIndex = _thinkingIndex, Delta = delta, Partial = Snapshot() });
		}
	}

	private void AddToolCall(JsonElement functionCall, string? thoughtSignature)
	{
		EnsureStarted();

		var name = GetString(functionCall, "name") ?? string.Empty;
		var providedId = GetString(functionCall, "id");

		// Gemini 常常不给 id，也可能重复给同一个；需要时自行生成唯一 id。
		var needsId = string.IsNullOrEmpty(providedId) ||
			_content.Any(block => block is ToolCall existing && existing.Id == providedId);

		var id = needsId ? $"{name}_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{++_toolCallCounter}" : providedId!;
		var (arguments, argumentsError) = functionCall.TryGetProperty("args", out var args)
			? JsonRepair.ParseObject(args.GetRawText())
			: (new JsonObject(), null);

		var call = new ToolCall
		{
			Id = id,
			Name = name,
			Arguments = arguments ?? new(),
			ArgumentsError = argumentsError,
			ThoughtSignature = thoughtSignature,
		};

		var index = _content.Count;
		_content.Add(call);

		Push(new ToolCallStartEvent { ContentIndex = index, Partial = Snapshot() });
		Push(new ToolCallDeltaEvent { ContentIndex = index, Delta = (arguments ?? new JsonObject()).ToJsonString(PiJson.NodeOptions), Partial = Snapshot() });
		Push(new ToolCallEndEvent { ContentIndex = index, ToolCall = call, Partial = Snapshot() });
	}

	private void CloseText()
	{
		if (_textBuffer is null)
		{
			return;
		}

		Push(new TextEndEvent
		{
			ContentIndex = _textIndex,
			Content = _textBuffer.ToString(),
			Partial = Snapshot(),
		});

		_textBuffer = null;
		_textIndex = -1;
		_textSignature = null;
	}

	private void CloseThinking()
	{
		if (_thinkingBuffer is null)
		{
			return;
		}

		Push(new ThinkingEndEvent
		{
			ContentIndex = _thinkingIndex,
			Content = _thinkingBuffer.ToString(),
			Partial = Snapshot(),
		});

		_thinkingBuffer = null;
		_thinkingIndex = -1;
		_thinkingSignature = null;
	}

	private void ReadUsage(JsonElement usage)
	{
		var prompt = GetLong(usage, "promptTokenCount") ?? 0;
		var cached = GetLong(usage, "cachedContentTokenCount") ?? 0;
		var candidates = GetLong(usage, "candidatesTokenCount") ?? 0;
		var thoughts = GetLong(usage, "thoughtsTokenCount") ?? 0;

		_usage = CostCalculator.Calculate(_model, new Usage
		{
			Input = Math.Max(0, prompt - cached),
			// Gemini 把思考令牌与可见回答分开上报。
			Output = candidates + thoughts,
			CacheRead = cached,
			CacheWrite = 0,
			Reasoning = thoughts,
			TotalTokens = Math.Max(0, prompt - cached) + candidates + thoughts + cached,
		});
	}

	private static (StopReason Reason, string? Error) MapStopReason(string reason) => reason switch
	{
		"STOP" => (StopReason.Stop, null),
		"MAX_TOKENS" => (StopReason.Length, null),
		"SAFETY" => (StopReason.Error, "Response blocked by safety filters"),
		"RECITATION" => (StopReason.Error, "Response blocked for recitation"),
		"BLOCKLIST" => (StopReason.Error, "Response blocked by the blocklist"),
		"PROHIBITED_CONTENT" => (StopReason.Error, "Response blocked: prohibited content"),
		"SPII" => (StopReason.Error, "Response blocked: sensitive personal information"),
		"MALFORMED_FUNCTION_CALL" => (StopReason.Error, "Malformed function call"),
		"IMAGE_SAFETY" => (StopReason.Error, "Image blocked by safety filters"),
		_ => (StopReason.Stop, null),
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

	private static string? GetString(JsonElement element, string name) =>
		element.ValueKind == JsonValueKind.Object &&
		element.TryGetProperty(name, out var value) &&
		value.ValueKind == JsonValueKind.String
			? value.GetString()
			: null;

	private static long? GetLong(JsonElement element, string name) =>
		element.ValueKind == JsonValueKind.Object &&
		element.TryGetProperty(name, out var value) &&
		value.ValueKind == JsonValueKind.Number
			? value.GetInt64()
			: null;
}
