using System.Text.Json.Nodes;
using PiWeft.CodingAgent;

namespace PiWeft.Cli;

/// <summary>
/// RPC 模式的 <see cref="IUiHost"/>：发出 pi 的 <c>extension_ui_request</c> 并等待客户端用
/// <c>extension_ui_response</c> 回答。对话框方法（select/confirm/input/editor）阻塞等待，
/// notify 即发即忘。<c>ask_question</c> 走 PiDeck 批量信封（<c>method: "input"</c>），
/// 因此 PiDeck 的桌面端可以直接渲染问询卡片。
/// </summary>
public sealed partial class CliRunner : IUiHost
{
	private readonly Lock _uiGate = new();
	private readonly Dictionary<string, TaskCompletionSource<JsonObject?>> _pendingUiRequests = new(StringComparer.Ordinal);

	bool IUiHost.HasUi => true;

	async Task<string?> IUiHost.InputAsync(UiInputRequest request, CancellationToken cancellationToken)
	{
		var response = await SendUiRequestAsync(
			"input",
			new JsonObject
			{
				["title"] = request.Title,
				["placeholder"] = request.Placeholder,
			},
			cancellationToken).ConfigureAwait(false);

		return StringValueOf(response);
	}

	async Task<string?> IUiHost.SelectAsync(UiSelectRequest request, CancellationToken cancellationToken)
	{
		var parameters = new JsonObject
		{
			["title"] = request.Title,
			["options"] = new JsonArray([.. request.Options.Select(static option => (JsonNode?)JsonValue.Create(option))]),
		};
		if (request.TimeoutMs is { } timeout)
		{
			parameters["timeout"] = timeout;
		}

		var response = await SendUiRequestAsync("select", parameters, cancellationToken).ConfigureAwait(false);
		return StringValueOf(response);
	}

	async Task<bool?> IUiHost.ConfirmAsync(UiConfirmRequest request, CancellationToken cancellationToken)
	{
		var parameters = new JsonObject { ["title"] = request.Title };
		if (request.Message is { Length: > 0 } message)
		{
			parameters["message"] = message;
		}

		if (request.TimeoutMs is { } timeout)
		{
			parameters["timeout"] = timeout;
		}

		var response = await SendUiRequestAsync("confirm", parameters, cancellationToken).ConfigureAwait(false);
		if (response is null)
		{
			// 取消对 confirm 而言是明确的「否」（与 pi 一致），只有宿主不支持才是 null。
			return false;
		}

		return response["confirmed"]?.GetValueKind() == System.Text.Json.JsonValueKind.True;
	}

	async Task<string?> IUiHost.EditorAsync(UiEditorRequest request, CancellationToken cancellationToken)
	{
		var parameters = new JsonObject { ["title"] = request.Title };
		if (request.Prefill is { Length: > 0 } prefill)
		{
			parameters["prefill"] = prefill;
		}

		var response = await SendUiRequestAsync("editor", parameters, cancellationToken).ConfigureAwait(false);
		return StringValueOf(response);
	}

	void IUiHost.Notify(UiNotifyRequest request) => WriteUiFireAndForget("notify", new JsonObject
	{
		["message"] = request.Message,
		["notifyType"] = request.NotifyType,
	});

	void IUiHost.SetStatus(UiStatusRequest request)
	{
		var parameters = new JsonObject { ["statusKey"] = request.Key };
		if (request.Text is not null)
		{
			parameters["statusText"] = request.Text;
		}

		WriteUiFireAndForget("setStatus", parameters);
	}

	void IUiHost.SetWidget(UiWidgetRequest request)
	{
		var parameters = new JsonObject { ["widgetKey"] = request.Key };
		if (request.Lines is not null)
		{
			parameters["widgetLines"] = new JsonArray([.. request.Lines.Select(static line => (JsonNode?)JsonValue.Create(line))]);
		}

		if (request.Placement is { Length: > 0 } placement)
		{
			parameters["widgetPlacement"] = placement;
		}

		WriteUiFireAndForget("setWidget", parameters);
	}

	void IUiHost.SetTitle(UiTitleRequest request) =>
		WriteUiFireAndForget("setTitle", new JsonObject { ["title"] = request.Title });

	void IUiHost.SetEditorText(string text) =>
		WriteUiFireAndForget("set_editor_text", new JsonObject { ["text"] = text });

	/// <summary>发出一个不等待回应的扩展 UI 请求，字段与 pi RPC 的顶层字段一致。</summary>
	private void WriteUiFireAndForget(string method, JsonObject parameters)
	{
		var record = new JsonObject
		{
			["type"] = "extension_ui_request",
			["id"] = Guid.NewGuid().ToString("N"),
			["method"] = method,
		};
		foreach (var (name, value) in parameters)
		{
			record[name] = value?.DeepClone();
		}

		WriteRecord(CliJson.Serialize(record));
	}

	/// <summary>
	/// 发出一个对话框请求并等待 <c>extension_ui_response</c>。
	/// 请求自带 <c>timeout</c> 时在超时后自动按取消解析（pi 的 agent 侧超时语义）。
	/// </summary>
	private async Task<JsonObject?> SendUiRequestAsync(string method, JsonObject parameters, CancellationToken cancellationToken)
	{
		var id = Guid.NewGuid().ToString("N");
		var completion = new TaskCompletionSource<JsonObject?>(TaskCreationOptions.RunContinuationsAsynchronously);
		lock (_uiGate)
		{
			_pendingUiRequests[id] = completion;
		}

		var record = new JsonObject
		{
			["type"] = "extension_ui_request",
			["id"] = id,
			["method"] = method,
		};
		foreach (var (name, value) in parameters)
		{
			record[name] = value?.DeepClone();
		}

		try
		{
			WriteRecord(CliJson.Serialize(record));

			using var registration = cancellationToken.Register(
				static state => ((TaskCompletionSource<JsonObject?>)state!).TrySetResult(null),
				completion);

			if (parameters["timeout"] is JsonValue { } timeoutValue)
			{
				var timeout = timeoutValue.GetValueKind() == System.Text.Json.JsonValueKind.Number
					? timeoutValue.GetValue<int>()
					: 0;
				if (timeout > 0)
				{
					// 超时由 agent 侧自动解析，客户端无需自己计时。
					_ = Task.Delay(timeout, cancellationToken).ContinueWith(
						static (_, state) => ((TaskCompletionSource<JsonObject?>)state!).TrySetResult(null),
						completion,
						CancellationToken.None,
						TaskContinuationOptions.OnlyOnRanToCompletion,
						TaskScheduler.Default);
				}
			}

			return await completion.Task.ConfigureAwait(false);
		}
		finally
		{
			lock (_uiGate)
			{
				_pendingUiRequests.Remove(id);
			}
		}
	}

	/// <summary>取消所有等待中的扩展 UI 请求（stdin 关闭或进程退出时）。</summary>
	private void CancelPendingUiRequests()
	{
		lock (_uiGate)
		{
			foreach (var completion in _pendingUiRequests.Values)
			{
				completion.TrySetResult(null);
			}

			_pendingUiRequests.Clear();
		}
	}

	/// <summary>处理 RPC 的 <c>extension_ui_response</c>；返回是否命中等待中的请求。</summary>
	private bool ResolveUiResponse(JsonObject command)
	{
		// 协议里 id 是字符串，但客户端发数字时也必须能命中（回显容错）。
		var requestId = command["id"]?.GetValueKind() switch
		{
			System.Text.Json.JsonValueKind.String => command["id"]!.GetValue<string>(),
			System.Text.Json.JsonValueKind.Number => command["id"]!.ToJsonString(),
			_ => null,
		};

		if (requestId is not { Length: > 0 })
		{
			return false;
		}

		TaskCompletionSource<JsonObject?>? completion;
		lock (_uiGate)
		{
			if (!_pendingUiRequests.Remove(requestId, out completion))
			{
				return false;
			}
		}

		completion.TrySetResult(command);
		return true;
	}

	private static string? StringValueOf(JsonObject? response)
	{
		if (response is null || response["cancelled"]?.GetValue<bool>() == true)
		{
			return null;
		}

		return response["value"] is JsonValue value && value.GetValueKind() == System.Text.Json.JsonValueKind.String
			? value.GetValue<string>()
			: null;
	}
}
