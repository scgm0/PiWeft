using System.Text.Json.Nodes;
using PiWeft.CodingAgent;

namespace PiWeft.Cli;

/// <summary>
/// RPC 模式的 <see cref="IUiHost"/>：发出 pi 的 <c>extension_ui_request</c>（<c>method: "input"</c>），
/// 并等待客户端用 <c>extension_ui_response</c> 回答。因为 <c>ask_question</c> 使用 PiDeck 的批量信封，
/// PiDeck 的桌面端可以直接渲染问询卡片。
/// </summary>
public sealed partial class CliRunner : IUiHost
{
	private readonly Lock _uiGate = new();
	private readonly Dictionary<string, TaskCompletionSource<string?>> _pendingUiRequests = new(StringComparer.Ordinal);

	bool IUiHost.HasUi => true;

	async Task<string?> IUiHost.InputAsync(UiInputRequest request, CancellationToken cancellationToken)
	{
		var id = Guid.NewGuid().ToString("N");
		var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
		lock (_uiGate)
		{
			_pendingUiRequests[id] = completion;
		}

		try
		{
			WriteRecord(CliJson.Serialize(new JsonObject
			{
				["type"] = "extension_ui_request",
				["id"] = id,
				["method"] = "input",
				["title"] = request.Title,
				["placeholder"] = request.Placeholder,
			}));

			using var registration = cancellationToken.Register(
				static state => ((TaskCompletionSource<string?>)state!).TrySetResult(null),
				completion);

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
		if (command["id"]?.GetValue<string>() is not { Length: > 0 } id)
		{
			return false;
		}

		TaskCompletionSource<string?>? completion;
		lock (_uiGate)
		{
			if (!_pendingUiRequests.Remove(id, out completion))
			{
				return false;
			}
		}

		var cancelled = command["cancelled"]?.GetValue<bool>() == true;
		var value = command["value"]?.GetValue<string>();
		completion.TrySetResult(cancelled ? null : value);
		return true;
	}
}
