using System.Text;
using System.Text.Json;
using PiWeft.Ai.Utils;
using System.Text.Json.Nodes;

namespace PiWeft.Ai.Providers.OpenAi;

/// <summary>
/// 把 OpenAI Responses API 的 SSE 事件折叠进一个 <see cref="AssistantMessageEventStream"/>。内容块按事件的
/// <c>output_index</c> 跟踪，这也是 Responses API 标识输出项的方式。
/// </summary>
internal sealed class OpenAiResponsesStreamState
{
	private sealed class Slot
	{
		public required string Kind { get; init; }

		public required int ContentIndex { get; init; }

		public StringBuilder Buffer { get; } = new();

		public string ToolCallId { get; set; } = string.Empty;

		public string ToolName { get; set; } = string.Empty;

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
	private string? _responseId;
	private string? _responseModel;
	private bool _started;
	private bool _finished;

	public OpenAiResponsesStreamState(
		Model model,
		AssistantMessageEventStream stream,
		long startedAt,
		string api = OpenAiResponsesProvider.ApiId)
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

		var type = GetString(root, "type");
		switch (type)
		{
			case "response.created":
				if (root.TryGetProperty("response", out var created))
				{
					_responseId ??= GetString(created, "id");
					_responseModel ??= GetString(created, "model");
				}

				break;

			case "response.output_item.added":
				if (root.TryGetProperty("item", out var addedItem))
				{
					EnsureSlot(GetInt(root, "output_index") ?? 0, addedItem);
				}

				break;

			case "response.reasoning_summary_text.delta":
			case "response.reasoning_text.delta":
				AppendThinking(GetInt(root, "output_index") ?? 0, GetString(root, "delta"));
				break;

			case "response.reasoning_summary_part.done":
				// 多个摘要分片之间用空行连接，与 pi 一致。
				AppendThinking(GetInt(root, "output_index") ?? 0, "\n\n");
				break;

			case "response.output_text.delta":
			case "response.refusal.delta":
				AppendText(GetInt(root, "output_index") ?? 0, GetString(root, "delta"));
				break;

			case "response.function_call_arguments.delta":
				AppendArguments(GetInt(root, "output_index") ?? 0, GetString(root, "delta"));
				break;

			case "response.function_call_arguments.done":
				AppendArgumentsSuffix(GetInt(root, "output_index") ?? 0, GetString(root, "arguments"));
				break;

			case "response.output_item.done":
				if (root.TryGetProperty("item", out var doneItem))
				{
					FinishSlot(GetInt(root, "output_index") ?? 0, doneItem);
				}

				break;

			case "response.completed":
			case "response.done":
			case "response.incomplete":
				if (root.TryGetProperty("response", out var response))
				{
					ReadUsage(response);
					ReadStatus(response);
				}

				Finish();
				break;

			case "response.failed":
				if (root.TryGetProperty("response", out var failed) &&
					failed.TryGetProperty("error", out var failedError))
				{
					_errorMessage = GetString(failedError, "message") ?? "Response failed";
				}

				_stopReason = StopReason.Error;
				Finish();
				break;

			case "error":
				_errorMessage = GetString(root, "message") ?? "Responses stream error";
				_stopReason = StopReason.Error;
				Finish();
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

		// 关闭 provider 没有显式收尾的内容块。
		foreach (var slot in _slots.Values.OrderBy(static pair => pair.ContentIndex))
		{
			CloseSlot(slot);
		}

		var reason = _stopReason;
		if (reason == StopReason.Pending)
		{
			reason = _content.Any(static block => block is ToolCall) ? StopReason.ToolUse : StopReason.Stop;
		}

		if (reason == StopReason.Stop && _content.Any(static block => block is ToolCall))
		{
			reason = StopReason.ToolUse;
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

	private void EnsureSlot(int outputIndex, JsonElement item)
	{
		if (_slots.ContainsKey(outputIndex))
		{
			return;
		}

		var kind = GetString(item, "type") switch
		{
			"reasoning" => "thinking",
			"function_call" or "custom_tool_call" => "toolCall",
			_ => "text",
		};

		EnsureStarted();

		var contentIndex = _content.Count;
		var slot = new Slot { Kind = kind, ContentIndex = contentIndex };

		switch (kind)
		{
			case "thinking":
				_content.Add(new ThinkingContent { Thinking = string.Empty, ThinkingSignature = string.Empty });
				break;
			case "text":
				_content.Add(new TextContent { Text = string.Empty });
				break;
			default:
				slot.ToolCallId = GetString(item, "call_id") ?? GetString(item, "id") ?? string.Empty;
				slot.ToolName = GetString(item, "name") ?? string.Empty;
				_content.Add(new ToolCall
				{
					Id = slot.ToolCallId,
					Name = slot.ToolName,
					Arguments = new JsonObject(),
				});
				break;
		}

		_slots[outputIndex] = slot;
		Push(kind switch
		{
			"thinking" => new ThinkingStartEvent { ContentIndex = contentIndex, Partial = Snapshot() },
			"text" => (AssistantMessageEvent)new TextStartEvent { ContentIndex = contentIndex, Partial = Snapshot() },
			_ => new ToolCallStartEvent { ContentIndex = contentIndex, Partial = Snapshot() },
		});
	}

	private void AppendThinking(int outputIndex, string? delta)
	{
		if (string.IsNullOrEmpty(delta) || !_slots.TryGetValue(outputIndex, out var slot) || slot.Kind != "thinking")
		{
			return;
		}

		slot.Buffer.Append(delta);
		_content[slot.ContentIndex] = new ThinkingContent
		{
			Thinking = slot.Buffer.ToString(),
			ThinkingSignature = ((ThinkingContent)_content[slot.ContentIndex]).ThinkingSignature,
		};
		Push(new ThinkingDeltaEvent { ContentIndex = slot.ContentIndex, Delta = delta, Partial = Snapshot() });
	}

	private void AppendText(int outputIndex, string? delta)
	{
		if (string.IsNullOrEmpty(delta) || !_slots.TryGetValue(outputIndex, out var slot) || slot.Kind != "text")
		{
			return;
		}

		slot.Buffer.Append(delta);
		_content[slot.ContentIndex] = new TextContent { Text = slot.Buffer.ToString() };
		Push(new TextDeltaEvent { ContentIndex = slot.ContentIndex, Delta = delta, Partial = Snapshot() });
	}

	private void AppendArguments(int outputIndex, string? delta)
	{
		if (string.IsNullOrEmpty(delta) || !_slots.TryGetValue(outputIndex, out var slot) || slot.Kind != "toolCall")
		{
			return;
		}

		slot.Buffer.Append(delta);
		Push(new ToolCallDeltaEvent { ContentIndex = slot.ContentIndex, Delta = delta, Partial = Snapshot() });
	}

	/// <summary><c>done</c> 事件带完整参数，因此只把缺失的后缀作为增量补发。</summary>
	private void AppendArgumentsSuffix(int outputIndex, string? complete)
	{
		if (complete is null || !_slots.TryGetValue(outputIndex, out var slot) || slot.Kind != "toolCall")
		{
			return;
		}

		var previous = slot.Buffer.ToString();
		if (complete.StartsWith(previous, StringComparison.Ordinal))
		{
			var suffix = complete[previous.Length..];
			slot.Buffer.Clear();
			slot.Buffer.Append(complete);
			if (suffix.Length > 0)
			{
				Push(new ToolCallDeltaEvent { ContentIndex = slot.ContentIndex, Delta = suffix, Partial = Snapshot() });
			}

			return;
		}

		slot.Buffer.Clear();
		slot.Buffer.Append(complete);
	}

	private void FinishSlot(int outputIndex, JsonElement item)
	{
		// provider 只发 done 事件时，会在这里创建对应的槽位。
		EnsureSlot(outputIndex, item);

		if (!_slots.TryGetValue(outputIndex, out var slot))
		{
			return;
		}

		if (slot.Kind == "thinking")
		{
			// 优先使用已完成项的摘要文本，并保留该项作为回放签名。
			var summary = JoinSummary(item);
			if (summary.Length > 0)
			{
				slot.Buffer.Clear();
				slot.Buffer.Append(summary);
			}

			_content[slot.ContentIndex] = new ThinkingContent
			{
				Thinking = slot.Buffer.ToString(),
				ThinkingSignature = item.GetRawText(),
			};
		}
		else if (slot.Kind == "text")
		{
			var text = JoinOutputText(item);
			if (text.Length > 0)
			{
				slot.Buffer.Clear();
				slot.Buffer.Append(text);
			}

			_content[slot.ContentIndex] = new TextContent { Text = slot.Buffer.ToString() };
		}

		CloseSlot(slot);
	}

	private void CloseSlot(Slot slot)
	{
		if (slot.Ended)
		{
			return;
		}

		slot.Ended = true;

		switch (slot.Kind)
		{
			case "thinking":
				Push(new ThinkingEndEvent
				{
					ContentIndex = slot.ContentIndex,
					Content = slot.Buffer.ToString(),
					Partial = Snapshot(),
				});
				break;

			case "text":
				Push(new TextEndEvent
				{
					ContentIndex = slot.ContentIndex,
					Content = slot.Buffer.ToString(),
					Partial = Snapshot(),
				});
				break;

			default:
			{
				var (arguments, argumentsError) = JsonRepair.ParseObject(slot.Buffer.ToString());
				var call = ((ToolCall)_content[slot.ContentIndex]) with
				{
					Arguments = arguments ?? new(),
					ArgumentsError = argumentsError,
				};
				_content[slot.ContentIndex] = call;
				Push(new ToolCallEndEvent { ContentIndex = slot.ContentIndex, ToolCall = call, Partial = Snapshot() });
				break;
			}
		}
	}

	private void ReadUsage(JsonElement response)
	{
		if (!response.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
		{
			return;
		}

		long cached = 0;
		long cacheWrite = 0;
		if (usage.TryGetProperty("input_tokens_details", out var inputDetails) &&
			inputDetails.ValueKind == JsonValueKind.Object)
		{
			cached = GetLong(inputDetails, "cached_tokens") ?? 0;
			cacheWrite = GetLong(inputDetails, "cache_write_tokens") ?? 0;
		}

		long? reasoning = null;
		if (usage.TryGetProperty("output_tokens_details", out var outputDetails) &&
			outputDetails.ValueKind == JsonValueKind.Object)
		{
			reasoning = GetLong(outputDetails, "reasoning_tokens");
		}

		var input = Math.Max(0, (GetLong(usage, "input_tokens") ?? 0) - cached - cacheWrite);
		var output = GetLong(usage, "output_tokens") ?? 0;

		_usage = CostCalculator.Calculate(_model, new Usage
		{
			Input = input,
			Output = output,
			CacheRead = cached,
			CacheWrite = cacheWrite,
			Reasoning = reasoning,
			TotalTokens = GetLong(usage, "total_tokens") ?? input + output + cached + cacheWrite,
		});
	}

	private void ReadStatus(JsonElement response)
	{
		var status = GetString(response, "status");
		string? incompleteReason = null;
		if (response.TryGetProperty("incomplete_details", out var details) &&
			details.ValueKind == JsonValueKind.Object)
		{
			incompleteReason = GetString(details, "reason");
		}

		_rawStopReason = incompleteReason is null ? status : $"{status}.{incompleteReason}";
		(_stopReason, _errorMessage) = MapStopReason(status, incompleteReason);
	}

	private static (StopReason Reason, string? Error) MapStopReason(string? status, string? incompleteReason) => status switch
	{
		"completed" => (StopReason.Stop, null),
		"incomplete" => incompleteReason switch
		{
			"max_output_tokens" => (StopReason.Length, null),
			"content_filter" => (StopReason.Error, "Response incomplete: content_filter"),
			_ => (StopReason.Error, $"Response incomplete: {incompleteReason ?? "unknown reason"}"),
		},
		"failed" => (StopReason.Error, "Response failed"),
		"cancelled" => (StopReason.Error, "Response was cancelled"),
		_ => (StopReason.Pending, null),
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

	private static string JoinSummary(JsonElement item)
	{
		if (!item.TryGetProperty("summary", out var summary) || summary.ValueKind != JsonValueKind.Array)
		{
			return string.Empty;
		}

		return string.Join(
			"\n\n",
			summary.EnumerateArray()
				.Select(entry => GetString(entry, "text"))
				.Where(static text => !string.IsNullOrEmpty(text)));
	}

	private static string JoinOutputText(JsonElement item)
	{
		if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
		{
			return string.Empty;
		}

		var builder = new StringBuilder();
		foreach (var block in content.EnumerateArray())
		{
			var text = GetString(block, "text") ?? GetString(block, "refusal");
			if (!string.IsNullOrEmpty(text))
			{
				builder.Append(text);
			}
		}

		return builder.ToString();
	}


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
