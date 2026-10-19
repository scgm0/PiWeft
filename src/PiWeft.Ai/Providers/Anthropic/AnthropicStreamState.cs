using System.Text;
using System.Text.Json;
using PiWeft.Ai.Utils;

namespace PiWeft.Ai.Providers.Anthropic;

/// <summary>
/// 把 Anthropic 的 SSE 事件折叠进一个 <see cref="AssistantMessageEventStream"/>。内容块按 content index 跟踪，
/// 每次变化后都会重新生成实时的 <see cref="AssistantMessageEvent.Partial"/> 快照。
/// </summary>
internal sealed class AnthropicStreamState
{
	private readonly Model _model;
	private readonly AssistantMessageEventStream _stream;
	private readonly List<ContentBlock> _content = [];

	private readonly StringBuilder _text = new();
	private readonly StringBuilder _thinking = new();
	private readonly StringBuilder _toolJson = new();

	private AssistantMessage _partial;
	private string _signature = string.Empty;
	private int _index = -1;
	private string _blockKind = string.Empty;
	private long _input;
	private long _output;
	private long _cacheRead;
	private long _cacheWrite;
	private StopReason _stopReason = StopReason.Pending;
	private string? _rawStopReason;
	private string? _responseId;
	private string? _responseModel;
	private bool _started;
	private bool _finished;

	public AnthropicStreamState(Model model, AssistantMessageEventStream stream, long startedAt)
	{
		_model = model;
		_stream = stream;
		_partial = new AssistantMessage
		{
			Api = AnthropicProvider.ApiId,
			Provider = model.Provider,
			Model = model.Id,
			Timestamp = startedAt,
			StopReason = StopReason.Pending,
		};
	}

	public void Handle(string? eventType, JsonElement root)
	{
		if (_finished)
		{
			return;
		}

		var type = GetString(root, "type") ?? eventType;
		switch (type)
		{
			case "message_start":
				OnMessageStart(root);
				break;
			case "content_block_start":
				OnContentBlockStart(root);
				break;
			case "content_block_delta":
				OnContentBlockDelta(root);
				break;
			case "content_block_stop":
				OnContentBlockStop();
				break;
			case "message_delta":
				OnMessageDelta(root);
				break;
			case "message_stop":
				Finish();
				break;
			case "error":
				OnError(root);
				break;
			case "ping":
			case null:
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

		// 服务端若在 content_block_stop 之前就结束（代理截断 / TCP 干净关闭），
		// 在途的工具参数还没解析成 ToolCallEndEvent，会丢参数；这里补一次收尾。
		if (_blockKind.Length > 0)
		{
			OnContentBlockStop();
		}

		if (_stopReason is StopReason.Error or StopReason.Aborted)
		{
			_finished = true;
			var failed = FinalMessage();
			_stream.Push(new ErrorEvent { Reason = _stopReason, Partial = failed });
			return;
		}

		var reason = _stopReason == StopReason.Pending ? StopReason.Stop : _stopReason;
		_finished = true;
		var message = FinalMessage(reason);
		_stream.Push(new DoneEvent { Reason = reason, Partial = message });
	}

	private void OnMessageStart(JsonElement root)
	{
		if (root.TryGetProperty("message", out var message))
		{
			_responseId = GetString(message, "id");
			_responseModel = GetString(message, "model");
			ReadUsage(message, isDelta: false);
		}

		EnsureStarted();
	}

	private void OnContentBlockStart(JsonElement root)
	{
		EnsureStarted();
		_index = GetInt(root, "index") ?? _content.Count;
		ResetBuffers();

		if (!root.TryGetProperty("content_block", out var block))
		{
			return;
		}

		switch (GetString(block, "type"))
		{
			case "text":
				_blockKind = "text";
				_content.Add(new TextContent { Text = GetString(block, "text") ?? string.Empty });
				Push(new TextStartEvent { ContentIndex = _index, Partial = Snapshot() });
				break;

			case "thinking":
				_blockKind = "thinking";
				_signature = GetString(block, "signature") ?? string.Empty;
				_thinking.Append(GetString(block, "thinking") ?? string.Empty);
				_content.Add(new ThinkingContent
				{
					Thinking = _thinking.ToString(),
					ThinkingSignature = _signature,
				});
				Push(new ThinkingStartEvent { ContentIndex = _index, Partial = Snapshot() });
				break;

			case "redacted_thinking":
				_blockKind = "redacted";
				_content.Add(new ThinkingContent
				{
					Thinking = string.Empty,
					ThinkingSignature = GetString(block, "data") ?? string.Empty,
					Redacted = true,
				});
				Push(new ThinkingStartEvent { ContentIndex = _index, Partial = Snapshot() });
				break;

			case "tool_use":
				_blockKind = "tool";
				_content.Add(new ToolCall
				{
					Id = GetString(block, "id") ?? string.Empty,
					Name = GetString(block, "name") ?? string.Empty,
					Arguments = ReadObject(block, "input"),
				});
				Push(new ToolCallStartEvent { ContentIndex = _index, Partial = Snapshot() });
				break;

			default:
				_blockKind = string.Empty;
				break;
		}
	}

	private void OnContentBlockDelta(JsonElement root)
	{
		var index = GetInt(root, "index") ?? _index;
		if (!root.TryGetProperty("delta", out var delta))
		{
			return;
		}

		switch (GetString(delta, "type"))
		{
			case "text_delta":
			{
				var text = GetString(delta, "text");
				if (string.IsNullOrEmpty(text) || index < 0 || index >= _content.Count)
				{
					return;
				}

				_text.Append(text);
				_content[index] = new TextContent { Text = _text.ToString() };
				Push(new TextDeltaEvent { ContentIndex = index, Delta = text, Partial = Snapshot() });
				break;
			}

			case "thinking_delta":
			{
				var text = GetString(delta, "thinking");
				if (string.IsNullOrEmpty(text) || index < 0 || index >= _content.Count)
				{
					return;
				}

				_thinking.Append(text);
				_content[index] = new ThinkingContent
				{
					Thinking = _thinking.ToString(),
					ThinkingSignature = _signature,
				};
				Push(new ThinkingDeltaEvent { ContentIndex = index, Delta = text, Partial = Snapshot() });
				break;
			}

			case "signature_delta":
			{
				var signature = GetString(delta, "signature");
				if (string.IsNullOrEmpty(signature))
				{
					return;
				}

				_signature += signature;
				if (index >= 0 && index < _content.Count && _content[index] is ThinkingContent thinking)
				{
					_content[index] = thinking with { ThinkingSignature = _signature };
				}

				break;
			}

			case "input_json_delta":
			{
				var partial = GetString(delta, "partial_json");
				if (string.IsNullOrEmpty(partial))
				{
					return;
				}

				_toolJson.Append(partial);
				Push(new ToolCallDeltaEvent { ContentIndex = index, Delta = partial, Partial = Snapshot() });
				break;
			}
		}
	}

	private void OnContentBlockStop()
	{
		switch (_blockKind)
		{
			case "text":
				Push(new TextEndEvent
				{
					ContentIndex = _index,
					Content = _text.ToString(),
					Partial = Snapshot(),
				});
				break;

			case "thinking":
				Push(new ThinkingEndEvent
				{
					ContentIndex = _index,
					Content = _thinking.ToString(),
					Partial = Snapshot(),
				});
				break;

			case "redacted":
				Push(new ThinkingEndEvent { ContentIndex = _index, Content = string.Empty, Partial = Snapshot() });
				break;

			case "tool":
			{
				if (_index >= 0 && _index < _content.Count && _content[_index] is ToolCall call)
				{
					var (arguments, argumentsError) = JsonRepair.ParseObject(_toolJson.ToString());
					call = call with { Arguments = arguments ?? new(), ArgumentsError = argumentsError };
					_content[_index] = call;
					Push(new ToolCallEndEvent { ContentIndex = _index, ToolCall = call, Partial = Snapshot() });
				}

				break;
			}
		}

		_blockKind = string.Empty;
	}

	private void OnMessageDelta(JsonElement root)
	{
		if (root.TryGetProperty("usage", out _))
		{
			ReadUsage(root, isDelta: true);
		}

		if (root.TryGetProperty("delta", out var delta))
		{
			if (GetString(delta, "stop_reason") is { } stopReason)
			{
				_rawStopReason = stopReason;
				_stopReason = MapStopReason(stopReason);
			}
		}
	}

	private void OnError(JsonElement root)
	{
		var message = root.TryGetProperty("error", out var error)
			? GetString(error, "message") ?? "Anthropic stream error"
			: "Anthropic stream error";

		_stopReason = StopReason.Error;
		_partial = _partial with { ErrorMessage = message };
		Finish();
	}

	private void ReadUsage(JsonElement element, bool isDelta)
	{
		if (!element.TryGetProperty("usage", out var usage))
		{
			return;
		}

		if (GetLong(usage, "input_tokens") is { } input && input > 0)
		{
			_input = input;
		}

		if (GetLong(usage, "cache_read_input_tokens") is { } cacheRead)
		{
			_cacheRead = cacheRead;
		}

		if (GetLong(usage, "cache_creation_input_tokens") is { } cacheWrite)
		{
			_cacheWrite = cacheWrite;
		}

		if (GetLong(usage, "output_tokens") is { } output)
		{
			_output = isDelta ? _output + output : output;
		}
	}

	private void EnsureStarted()
	{
		if (_started)
		{
			return;
		}

		_started = true;
		_partial = _partial with
		{
			ResponseId = _responseId,
			ResponseModel = _responseModel,
		};
		Push(new StartEvent { Partial = _partial });
	}

	private AssistantMessage Snapshot() => _partial = _partial with { Content = [.. _content] };

	private AssistantMessage FinalMessage(StopReason? reason = null)
	{
		var usage = new Usage
		{
			Input = _input,
			Output = _output,
			CacheRead = _cacheRead,
			CacheWrite = _cacheWrite,
			TotalTokens = _input + _output + _cacheRead + _cacheWrite,
		};

		usage = CostCalculator.Calculate(_model, usage);

		_partial = _partial with
		{
			Content = [.. _content],
			Usage = usage,
			StopReason = reason ?? _stopReason,
			RawStopReason = _rawStopReason,
			ResponseId = _responseId,
			ResponseModel = _responseModel,
		};

		return _partial;
	}

	private void Push(AssistantMessageEvent @event)
	{
		_stream.Push(@event);
	}

	private void ResetBuffers()
	{
		_text.Clear();
		_thinking.Clear();
		_toolJson.Clear();
		_signature = string.Empty;
	}

	private static StopReason MapStopReason(string? value) => value switch
	{
		"end_turn" or "stop_sequence" or "refusal" or "pause_turn" or "sensitive" => StopReason.Stop,
		"max_tokens" => StopReason.Length,
		"tool_use" => StopReason.ToolUse,
		_ => StopReason.Stop,
	};

	private static string? GetString(JsonElement element, string name) =>
		element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
			? value.GetString()
			: null;

	private static int? GetInt(JsonElement element, string name) =>
		element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
			? value.GetInt32()
			: null;

	private static long? GetLong(JsonElement element, string name) =>
		element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
			? value.GetInt64()
			: null;

	private static System.Text.Json.Nodes.JsonObject ReadObject(JsonElement element, string name) =>
		element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object
			? System.Text.Json.Nodes.JsonNode.Parse(value.GetRawText())!.AsObject()
			: new System.Text.Json.Nodes.JsonObject();

}
