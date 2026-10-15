using System.Text.Json;
using System.Text.Json.Nodes;

namespace PiWeft.Ai.Providers.PiMessages;

/// <summary>
/// 根据 pi-messages 事件流重建助手消息。该协议发送的是事件而非部分快照，
/// 因此这里的状态机自行维护内容列表，并在每个事件后重新生成快照。
/// </summary>
internal sealed class PiMessagesStreamState
{
	private readonly Model _model;
	private readonly AssistantMessageEventStream _stream;
	private readonly List<ContentBlock> _content = [];

	private AssistantMessage _partial;
	private Usage _usage = new();
	private string? _responseId;
	private string? _providerThinkingLevel;
	private string? _rawStopReason;
	private string? _errorMessage;
	private bool _started;
	private bool _finished;

	public PiMessagesStreamState(Model model, AssistantMessageEventStream stream, long startedAt)
	{
		_model = model;
		_stream = stream;
		_partial = new AssistantMessage
		{
			Api = PiMessagesProvider.ApiId,
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

		switch (GetString(root, "type"))
		{
			case "start":
				EnsureStarted();
				break;

			case "text_start":
			{
				var index = EnsureBlock(root, static () => new TextContent { Text = string.Empty });
				Push(new TextStartEvent { ContentIndex = index, Partial = Snapshot() });
				break;
			}

			case "text_delta":
			{
				var index = EnsureBlock(root, static () => new TextContent { Text = string.Empty });
				if (_content[index] is TextContent text)
				{
					_content[index] = text with { Text = text.Text + (GetString(root, "delta") ?? string.Empty) };
					Push(new TextDeltaEvent
					{
						ContentIndex = index,
						Delta = GetString(root, "delta") ?? string.Empty,
						Partial = Snapshot(),
					});
				}

				break;
			}

			case "text_end":
			{
				var index = EnsureBlock(root, static () => new TextContent { Text = string.Empty });
				var content = GetString(root, "content") ?? string.Empty;
				_content[index] = new TextContent
				{
					Text = content,
					TextSignature = GetString(root, "contentSignature") ?? (_content[index] as TextContent)?.TextSignature,
				};
				Push(new TextEndEvent { ContentIndex = index, Content = content, Partial = Snapshot() });
				break;
			}

			case "thinking_start":
			{
				var index = EnsureBlock(root, static () => new ThinkingContent { Thinking = string.Empty });
				Push(new ThinkingStartEvent { ContentIndex = index, Partial = Snapshot() });
				break;
			}

			case "thinking_delta":
			{
				var index = EnsureBlock(root, static () => new ThinkingContent { Thinking = string.Empty });
				if (_content[index] is ThinkingContent thinking)
				{
					_content[index] = thinking with
					{
						Thinking = thinking.Thinking + (GetString(root, "delta") ?? string.Empty),
					};
					Push(new ThinkingDeltaEvent
					{
						ContentIndex = index,
						Delta = GetString(root, "delta") ?? string.Empty,
						Partial = Snapshot(),
					});
				}

				break;
			}

			case "thinking_end":
			{
				var index = EnsureBlock(root, static () => new ThinkingContent { Thinking = string.Empty });
				var previous = _content[index] as ThinkingContent;
				var content = GetString(root, "content") ?? previous?.Thinking ?? string.Empty;
				_content[index] = new ThinkingContent
				{
					Thinking = content,
					ThinkingSignature = GetString(root, "contentSignature") ?? previous?.ThinkingSignature,
					Redacted = GetBool(root, "redacted") ?? previous?.Redacted,
				};
				Push(new ThinkingEndEvent { ContentIndex = index, Content = content, Partial = Snapshot() });
				break;
			}

			case "toolcall_start":
			{
				var index = EnsureBlock(root, static () => new ToolCall { Id = string.Empty, Name = string.Empty });
				if (_content[index] is ToolCall call)
				{
					_content[index] = call with
					{
						Id = GetString(root, "id") ?? string.Empty,
						Name = GetString(root, "toolName") ?? string.Empty,
					};
				}

				Push(new ToolCallStartEvent { ContentIndex = index, Partial = Snapshot() });
				break;
			}

			case "toolcall_delta":
			{
				var index = EnsureBlock(root, static () => new ToolCall { Id = string.Empty, Name = string.Empty });
				Push(new ToolCallDeltaEvent
				{
					ContentIndex = index,
					Delta = GetString(root, "delta") ?? string.Empty,
					Partial = Snapshot(),
				});
				break;
			}

			case "toolcall_end":
			{
				var index = EnsureBlock(root, static () => new ToolCall { Id = string.Empty, Name = string.Empty });
				var call = ReadToolCall(
						root.TryGetProperty("toolCall", out var toolCallNode) && toolCallNode.ValueKind == JsonValueKind.Object
							? JsonNode.Parse(toolCallNode.GetRawText()) as JsonObject
							: null)
					?? (_content[index] as ToolCall)
					?? new ToolCall { Id = string.Empty, Name = string.Empty };
				_content[index] = call;
				Push(new ToolCallEndEvent { ContentIndex = index, ToolCall = call, Partial = Snapshot() });
				break;
			}

			case "done":
				ReadUsage(root);
				_rawStopReason = GetString(root, "reason");
				_responseId ??= GetString(root, "responseId");
				_providerThinkingLevel ??= GetString(root, "providerThinkingLevel");
				Finish();
				break;

			case "error":
				ReadUsage(root);
				_responseId ??= GetString(root, "responseId");
				_providerThinkingLevel ??= GetString(root, "providerThinkingLevel");
				_rawStopReason = GetString(root, "reason") ?? "error";
				_errorMessage = GetString(root, "errorMessage") ?? "pi-messages backend error";
				Finish();
				break;
		}
	}

	/// <summary>
	/// 收束最终消息。没有终止事件就结束的流会按错误处理，因为无法确认响应完整。
	/// </summary>
	public void Finish()
	{
		if (_finished)
		{
			return;
		}

		EnsureStarted();
		_finished = true;

		var reason = _rawStopReason switch
		{
			null => StopReason.Error,
			_ => ParseStopReason(_rawStopReason),
		};

		if (_rawStopReason is null)
		{
			_errorMessage ??= "pi-messages backend ended without a terminal event";
		}

		_partial = _partial with
		{
			Content = [.. _content],
			Usage = _usage,
			StopReason = reason,
			RawStopReason = _rawStopReason,
			ResponseId = _responseId,
			ProviderThinkingLevel = _providerThinkingLevel,
			ErrorMessage = _errorMessage,
		};

		if (reason is StopReason.Error or StopReason.Aborted)
		{
			_stream.Push(new ErrorEvent { Reason = reason, Partial = _partial });
			return;
		}

		_stream.Push(new DoneEvent { Reason = reason, Partial = _partial });
	}

	private static StopReason ParseStopReason(string reason) => reason switch
	{
		"stop" => StopReason.Stop,
		"length" => StopReason.Length,
		"toolUse" => StopReason.ToolUse,
		"aborted" => StopReason.Aborted,
		"deferred" => StopReason.Deferred,
		_ => StopReason.Error,
	};

	private int EnsureBlock(JsonElement root, Func<ContentBlock> factory)
	{
		EnsureStarted();

		var index = GetInt(root, "contentIndex") ?? _content.Count;

		// 后端会给出它想要的索引，因此把它可能留下的空档补齐。
		while (_content.Count <= index)
		{
			_content.Add(factory());
		}

		return index;
	}

	private void ReadUsage(JsonElement root)
	{
		if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("usage", out var usage) ||
			usage.ValueKind != JsonValueKind.Object)
		{
			return;
		}

		_usage = CostCalculator.Calculate(_model, new Usage
		{
			Input = GetLong(usage, "input") ?? 0,
			Output = GetLong(usage, "output") ?? 0,
			CacheRead = GetLong(usage, "cacheRead") ?? 0,
			CacheWrite = GetLong(usage, "cacheWrite") ?? 0,
			CacheWrite1h = GetLong(usage, "cacheWrite1h"),
			Reasoning = GetLong(usage, "reasoning"),
			TotalTokens = GetLong(usage, "totalTokens") ?? 0,
		});
	}

	private static ToolCall? ReadToolCall(JsonObject? node)
	{
		if (node is null)
		{
			return null;
		}

		return new ToolCall
		{
			Id = node["id"]?.GetValue<string>() ?? string.Empty,
			Name = node["name"]?.GetValue<string>() ?? string.Empty,
			Arguments = node["arguments"] as JsonObject ?? new JsonObject(),
			ThoughtSignature = node["thoughtSignature"]?.GetValue<string>(),
		};
	}

	private void EnsureStarted()
	{
		if (_started)
		{
			return;
		}

		_started = true;
		Push(new StartEvent { Partial = _partial });
	}

	private AssistantMessage Snapshot() => _partial = _partial with
	{
		Content = [.. _content],
		ResponseId = _responseId,
	};

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

	private static bool? GetBool(JsonElement element, string name) =>
		element.ValueKind == JsonValueKind.Object &&
		element.TryGetProperty(name, out var value) &&
		value.ValueKind is JsonValueKind.True or JsonValueKind.False
			? value.GetBoolean()
			: null;
}
